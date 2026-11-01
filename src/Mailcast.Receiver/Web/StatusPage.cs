using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Updates;
using Packet.Mailcast.Propagation;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Web;

/// <summary>
/// The receiver's local web page: status, the mail held, settings, the input level and a live spectrogram.
/// </summary>
/// <remarks>
/// <para>The spectrogram and the level meter come from pdn-soundmodem's
/// <see cref="WaterfallWebServer"/>, one per audio pipeline, served under <c>/waterfall/</c> on
/// this page's port. This page reads its WebSocket for the spectrum lines and the level, and draws
/// them with the broadcast's markers on top; pdn-soundmodem's own full page is there too, at
/// <c>/waterfall/</c>.</para>
/// <para>It listens on the local network too unless the config's <c>web.lan</c> is off. Without
/// <c>web.password</c> anyone who can reach it can change the settings. With a password set, a
/// browser signs in on a page of its own and is given a session cookie (see
/// <see cref="SessionStore"/>); a script can give the password with HTTP Basic instead.</para>
/// </remarks>
public sealed partial class StatusPage : IAsyncDisposable
{
    private const string WaterfallBase = "/waterfall/";

    /// <summary>
    /// Blocked ahead of the embedded <see cref="WaterfallWebServer"/>, which otherwise always
    /// answers it: a loopback-only, keyless stream of the audio this receiver feeds it. On this
    /// page's own port that is surprising, not useful - a pdn-soundmodem audio source pointed at
    /// the receiver's own port would read the receiver back its own audio. pdn-soundmodem has no
    /// option to turn this path off; filed as packet-net/pdn-soundmodem#683.
    /// </summary>
    private const string ChannelAudioPath = WaterfallBase + "channel-audio";

    /// <summary>The largest settings form, or any other request body, accepted.</summary>
    public const int MaxFormBytes = 16 * 1024;

    /// <summary>The most mail entries one page of <c>/api/mail</c> gives.</summary>
    public const int MaxMailPage = 200;

    private const string MailBase = "/api/mail/";

    /// <summary>The start of a slot drill-down request, issue #48: <c>/api/slots/{slot}/frames</c>.</summary>
    private const string SlotsBase = "/api/slots/";

    /// <summary>The end of a slot drill-down request, issue #48.</summary>
    private const string SlotsFramesSuffix = "/frames";

    /// <summary>How soon one bulletin can be sent to the BBS again after the last time.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(2);

    /// <summary>How soon the BBS login can be tested again after the last time.</summary>
    public static readonly TimeSpan BbsTestCooldown = TimeSpan.FromSeconds(10);

    private readonly Dictionary<ulong, DateTimeOffset> _resentAt = [];
    private DateTimeOffset? _lastBbsTestAt;

    /// <summary>What the last "Test BBS login" found, issue #99: counts towards <see cref="BbsReachable"/> until a real delivery session has.</summary>
    private BbsLoginTestOutcome? _lastBbsTestOutcome;

    private readonly SessionStore _sessions;
    private readonly Dictionary<HttpListenerContext, string?> _sockets = [];
    private readonly SignInThrottle _throttle;
    private readonly SignInThrottle _keyThrottle;

    private readonly ReceiverHost _host;
    private readonly ConfigFile? _configFile;

    /// <summary>Held across a read-modify-save of the config from the page, so two changes cannot lose one another.</summary>
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly Action<string> _log;
    private HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private WaterfallWebServer? _waterfall;
    private Task? _accept;

    /// <summary>
    /// Creates the page for <paramref name="host"/>; settings are saved to <paramref name="configFile"/>,
    /// unless it has been changed by hand since it was read. Null saves nothing (tests).
    /// </summary>
    public StatusPage(ReceiverHost host, ConfigFile? configFile, Action<string> log)
    {
        _host = host;
        _configFile = configFile;
        _log = log;
        var web = host.Config.Web;
        if (web.Lan)
        {
            _listener.Prefixes.Add($"http://+:{web.Port}/");
        }
        else
        {
            OnThisMachine(_listener, web.Port);
        }
        Url = $"http://127.0.0.1:{web.Port}/";
        CookieName = $"pdn-mailcast-{web.Port}";
        _sessions = new SessionStore(Path.Combine(host.Config.StateDirectory, SessionStore.FileName), () => _host.Config.Web.Password, host.Time, log);
        _throttle = new SignInThrottle(host.Time, log);
        _keyThrottle = new SignInThrottle(host.Time, log, "API keys");
        host.PipelineCreated += Attach;
    }

    /// <summary>
    /// The session cookie's name. Cookies are kept per host name, not per port, so it has the
    /// port in it: two receivers on one machine must not sign each other's browsers out.
    /// </summary>
    internal string CookieName { get; }

    /// <summary>How long a wrong password waits before it is answered; tests that are not about the wait set it to zero.</summary>
    internal TimeSpan FailureDelay { get; init; } = SignInThrottle.FailureDelay;

    /// <summary>How the wait for a wrong password is done, for a test to hold it; null waits on the host's clock.</summary>
    internal Func<TimeSpan, Task>? Pause { get; init; }

    /// <summary>How "Test BBS login" actually tries the login, for a test to make it throw; null uses a real <see cref="BbsClient"/>.</summary>
    internal Func<BbsSettings, Task<BbsLoginTestResult>>? TestBbsLogin { get; init; }

    /// <summary>The check for a newer version, whose finding the page shows; null shows none.</summary>
    public UpdateCheck? Updates { get; init; }

    /// <summary>The signed-in browsers, for tests.</summary>
    internal SessionStore Sessions => _sessions;

    /// <summary>Where the page is, for the log.</summary>
    public string Url { get; }

    /// <summary>Whether the page is being served to the local network as well as to this machine; false until started.</summary>
    public bool OnLan { get; private set; }

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> if the port is taken.</summary>
    public void Start()
    {
        bool lan = _host.Config.Web.Lan;
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException e) when (lan && OperatingSystem.IsWindows() && e.ErrorCode == AccessDenied)
        {
            // Windows lets only an administrator (the service is one), or a user it has reserved
            // the address for, listen on every address: the page is still worth having here.
            _listener.Close();
            _listener = new HttpListener();
            OnThisMachine(_listener, _host.Config.Web.Port);
            _listener.Start();
            lan = false;
            _log($"web: Windows will not let {Environment.UserName} serve the page on the local network, so it is on this machine only. "
                + "Run the receiver as the service (install-service.ps1), or once, from an administrator command prompt: "
                + $"netsh http add urlacl url=http://+:{_host.Config.Web.Port}/ user=\"{Environment.UserDomainName}\\{Environment.UserName}\"");
        }
        OnLan = lan;
        _accept = AcceptAsync();
        _log($"web: status page at {Url}{(lan ? " and on the local network" : "")}");
    }

    /// <summary>Windows' ERROR_ACCESS_DENIED, which http.sys gives for an address this user may not listen on.</summary>
    private const int AccessDenied = 5;

    private static void OnThisMachine(HttpListener listener, int port)
    {
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>Gives a new pipeline its waterfall, before its audio starts.</summary>
    internal void Attach(AudioPipeline pipeline)
    {
        var waterfall = WaterfallWebServer.Routed(pipeline.Channel, new WaterfallOptions
        {
            DialFrequencyHz = pipeline.SpectrumDialHz + pipeline.LnbOffsetHz,
            Sideband = "usb",
            LinesPerSecond = 10,
            InputLevelMeter = true,
            Title = "pdn-mailcast receiver",
            DeclaredBands = [new DeclaredBand(0, "mailcast", pipeline.AudioCentreHz, 2 * OnAir.HalfWidthHz)],
            Log = line => _log("web: " + Ascii.Clean(line)),
            // Issue #100: report this receiver's own version, the same one its page and the daily
            // report show, instead of the bundled Packet.SoundModem library's build (pinned in
            // Directory.Packages.props), which drifts behind the real pdn-soundmodem elsewhere.
            VersionLabel = ReceiverHost.Version,
        });
        waterfall.Start();
        pipeline.SourceBlock += waterfall.MeterInputClipping;
        pipeline.Watched = () => waterfall.Viewers > 0;
        WaterfallWebServer? old;
        lock (_gate)
        {
            old = _waterfall;
            _waterfall = waterfall;
        }
        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            if (Refusal(context.Request) is { } refused)
            {
                await RespondAsync(context, refused.Status, "text/plain", refused.Why + "\n").ConfigureAwait(false);
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
            switch (path, context.Request.HttpMethod)
            {
                case ("/login", "POST"):
                    await SignInAsync(context).ConfigureAwait(false);
                    return;
                case ("/login", "GET"):
                    if (_host.Config.Web.Password is { Length: > 0 } password && _sessions.Find(SessionToken(context.Request)) is null)
                    {
                        await SignInPageAsync(context, 200, path, null).ConfigureAwait(false);
                    }
                    else
                    {
                        Redirect(context, "./");
                    }
                    return;
                case ("/logout", "POST"):
                    await SignOutAsync(context).ConfigureAwait(false);
                    return;
                case ("/api/pieces", "POST"):
                    // The API key, not the page's password, lets a program in (issue #214).
                    await PostPiecesAsync(context).ConfigureAwait(false);
                    return;
            }
            if (context.Request.HttpMethod == "GET" && await ServePublicAsync(context, path).ConfigureAwait(false))
            {
                return; // the object and piece reads and the API reference: open on the network whatever the page password
            }
            string? session = null;
            if (!await AdmitAsync(context, path, id => session = id).ConfigureAwait(false))
            {
                return; // answered: the sign-in page, or why not
            }

            if (path == ChannelAudioPath)
            {
                await RespondAsync(context, 404, "text/plain", "Not found.\n").ConfigureAwait(false);
                return;
            }

            if (path.StartsWith(WaterfallBase, StringComparison.Ordinal) || path == "/waterfall")
            {
                WaterfallWebServer? waterfall;
                lock (_gate)
                {
                    waterfall = _waterfall;
                }
                if (path == "/waterfall")
                {
                    Redirect(context, WaterfallBase);
                }
                else if (waterfall is null)
                {
                    await RespondAsync(context, 503, "text/plain", "No audio yet.\n").ConfigureAwait(false);
                }
                else if (context.Request.IsWebSocketRequest)
                {
                    await ServeSocketAsync(waterfall, context, session).ConfigureAwait(false);
                }
                else if (!await waterfall.TryServeAsync(context, WaterfallBase).ConfigureAwait(false))
                {
                    await RespondAsync(context, 503, "text/plain", "No audio yet.\n").ConfigureAwait(false);
                }
                return;
            }

            switch (path, context.Request.HttpMethod)
            {
                case ("/" or "/index.html", "GET"):
                    await RespondAsync(context, 200, "text/html; charset=utf-8", Page.Value).ConfigureAwait(false);
                    break;
                case ("/globe.json", "GET"):
                    await RespondAsync(context, 200, "application/json", Globe.Value).ConfigureAwait(false);
                    break;
                case ("/api/status", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(Status(), ReceiverConfig.JsonLine)).ConfigureAwait(false);
                    break;
                case ("/api/settings", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(SettingsView(_host.Config, _host.SourcesFromFollowed), ReceiverConfig.JsonLine)).ConfigureAwait(false);
                    break;
                case ("/api/settings", "POST"):
                    await SaveSettingsAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/mail", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(MailList(context.Request.QueryString), ReceiverConfig.JsonLine)).ConfigureAwait(false);
                    break;
                case ("/api/mail/resend", "POST"):
                    await ResendAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/mail/deliver-now", "POST"):
                    await DeliverNowAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/bbs/test", "POST"):
                    await TestBbsAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/listen-now", "POST"):
                    await ListenNowAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/filter/measure", "POST"):
                    await MeasureFilterAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/key", "GET"):
                    await ServeKeyAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/key/new", "POST"):
                    await ChangeKeyAsync(context, clear: false).ConfigureAwait(false);
                    break;
                case ("/api/key/clear", "POST"):
                    await ChangeKeyAsync(context, clear: true).ConfigureAwait(false);
                    break;
                case ("/api/keys/add", "POST"):
                    await AddKeyAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/keys/forget", "POST"):
                    await ForgetKeyAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/stations/listen", "POST"):
                    await _configLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await ListenToStationAsync(context).ConfigureAwait(false);
                    }
                    finally
                    {
                        _configLock.Release();
                    }
                    break;
                case ("/api/stations/listen-for", "POST"):
                    await ListenForAsync(context, add: true).ConfigureAwait(false);
                    break;
                case ("/api/stations/listen-for/remove", "POST"):
                    await ListenForAsync(context, add: false).ConfigureAwait(false);
                    break;
                case ("/api/filter/dial", "POST"):
                    await FilterDialAsync(context).ConfigureAwait(false);
                    break;
                case (_, "GET") when path.StartsWith(MailBase, StringComparison.Ordinal):
                    await ServeBulletinAsync(context, path[MailBase.Length..]).ConfigureAwait(false);
                    break;
                case (_, "GET") when path.StartsWith(SlotsBase, StringComparison.Ordinal) && path.EndsWith(SlotsFramesSuffix, StringComparison.Ordinal):
                    await ServeSlotFramesAsync(context, path[SlotsBase.Length..^SlotsFramesSuffix.Length]).ConfigureAwait(false);
                    break;
                default:
                    await RespondAsync(context, 404, "text/plain", "Not found.\n").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser went away.
        }
    }

    /// <summary>
    /// The public reads: the objects and pieces, the OpenAPI document and the reference page. They are open to anyone who can
    /// reach the page, with or without a password (head ends and other programs read them with none); the password protects
    /// the mail, the settings and the keys. They show only what was heard on air or handed in, never the mail or the settings.
    /// </summary>
    private async Task<bool> ServePublicAsync(HttpListenerContext context, string path)
    {
        switch (path)
        {
            case ObjectsPath:
                await ServeObjectsAsync(context).ConfigureAwait(false);
                return true;
            case "/api/openapi.json":
                await RespondAsync(context, 200, "application/json", OpenApi.Value).ConfigureAwait(false);
                return true;
            case "/api/docs":
                await RespondAsync(context, 200, "text/html; charset=utf-8", DocsPage).ConfigureAwait(false);
                return true;
            case "/api/docs/scalar.js":
                await ServeScalarAsync(context).ConfigureAwait(false);
                return true;
            default:
                if (path.StartsWith(ObjectsBase, StringComparison.Ordinal))
                {
                    await ServeObjectAsync(context, path[ObjectsBase.Length..]).ConfigureAwait(false);
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// Why a request is refused before it is looked at, or null to serve it:
    /// <list type="bullet">
    /// <item>On this machine only, a Host that is not this machine is refused, so a page
    /// elsewhere cannot reach this one by pointing a name of its own at 127.0.0.1 (DNS rebinding).</item>
    /// <item>A WebSocket or a POST that says it comes from another site is refused: a page the
    /// operator has open elsewhere must not be able to change the settings, sign in or sign out (CSRF).</item>
    /// </list>
    /// The password is asked for after this, by <see cref="AdmitAsync"/>.
    /// </summary>
    internal (int Status, string Why)? Refusal(HttpListenerRequest request)
    {
        var web = _host.Config.Web;
        string host = request.Headers["Host"] ?? "";
        if (!web.Lan && !IsLoopbackHost(host))
        {
            return (421, "This page only answers to localhost.");
        }
        string path = request.Url?.AbsolutePath ?? "/";
        bool sensitive = (request.HttpMethod != "GET" && path is not ("/api/pieces" or "/login" or "/logout")) || path == "/api/key";
        if (web.Lan && sensitive && !IsOwnHost(host))
        {
            // DNS rebinding: a page elsewhere must not change this one, or read its key, through a name of its own that it points here.
            // Viewing needs nothing: any name, a DNS alias or a reverse proxy, may read the page. Posting pieces has its own key.
            return (421, "Use this receiver's IP address in the URL to change settings or see the API key; other names are refused to stop other websites doing it.");
        }
        bool changes = request.HttpMethod != "GET" || request.IsWebSocketRequest;
        string? origin = request.Headers["Origin"];
        if (changes && origin is not null && !SameOrigin(origin, host))
        {
            return (403, "Refused: this request came from another site.");
        }
        return null;
    }

    /// <summary>
    /// Whether a Host header names this receiver by something an outsider cannot point at it: an IP
    /// address, localhost, this machine's exact name, or that name with .local (any case, nothing else).
    /// No Host at all passes: a browser always sends one.
    /// </summary>
    internal static bool IsOwnHost(string host)
    {
        if (host.Length == 0)
        {
            return true;
        }
        string name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        if (name.Length == 0)
        {
            return false;
        }
        if (IPAddress.TryParse(name.Trim('[', ']'), out _) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string me = Environment.MachineName;
        return name.Equals(me, StringComparison.OrdinalIgnoreCase) || name.Equals(me + ".local", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLoopbackHost(string host)
    {
        string name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "[::1]";
    }

    internal static bool SameOrigin(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serves a waterfall WebSocket, which stays open as long as the page does, noting which
    /// session it is for so that signing that session out, or changing the password, closes it.
    /// </summary>
    private async Task ServeSocketAsync(WaterfallWebServer waterfall, HttpListenerContext context, string? session)
    {
        lock (_gate)
        {
            _sockets[context] = session;
        }
        try
        {
            if (session is not null && !_sessions.Holds(session))
            {
                // Signed out between being let in and getting here.
                context.Response.Abort();
                return;
            }
            await waterfall.TryServeAsync(context, WaterfallBase).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _sockets.Remove(context);
            }
        }
    }

    /// <summary>Closes the open waterfall WebSockets whose session <paramref name="which"/> picks (null for one let in by HTTP Basic).</summary>
    private void CloseSockets(Func<string?, bool> which)
    {
        List<HttpListenerContext> closing;
        lock (_gate)
        {
            closing = [.. _sockets.Where(s => which(s.Value)).Select(s => s.Key)];
            foreach (var context in closing)
            {
                _sockets.Remove(context);
            }
        }
        foreach (var context in closing)
        {
            try
            {
                context.Response.Abort(); // drops the connection under the WebSocket
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or HttpListenerException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>The open waterfall WebSockets, for tests.</summary>
    internal int OpenSockets
    {
        get
        {
            lock (_gate)
            {
                return _sockets.Count;
            }
        }
    }

    /// <summary>The password in an HTTP Basic <c>Authorization</c> header (any user name), or null if there is none.</summary>
    internal static string? BasicPassword(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
        }
        catch (FormatException)
        {
            return "";
        }
        int colon = decoded.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? "" : decoded[(colon + 1)..];
    }

    /// <summary>
    /// Lets a request in if the page has no password, it has a session cookie that is still good,
    /// or it is a script giving the password with HTTP Basic. Otherwise answers it and gives false:
    /// a browser asking for a page gets the sign-in page, anything else 401 (or 429 while sign-in
    /// is refused for a while) as JSON. <paramref name="session"/> is the session let in on, if any.
    /// </summary>
    /// <remarks>
    /// What counts as a browser is <see cref="IsBrowser(string?, string?, string?)"/>. A
    /// browser's HTTP Basic is ignored, never counted as a wrong password: one that remembers a
    /// Basic login from before the sign-in page would otherwise never see that page, could not sign
    /// out, and after a change of password would lock its own address out with its old password
    /// within seconds, the page asking for its status every few.
    /// </remarks>
    private async Task<bool> AdmitAsync(HttpListenerContext context, string path, Action<string?> session)
    {
        var request = context.Request;
        string password = _host.Config.Web.Password;
        if (password.Length == 0)
        {
            return true;
        }
        if (_sessions.Find(SessionToken(request)) is { } found)
        {
            session(found.Id);
            return true;
        }
        if (IsBrowser(request))
        {
            if (request.HttpMethod == "GET" && !request.IsWebSocketRequest && (!path.StartsWith("/api/", StringComparison.Ordinal) || path == "/api/docs"))
            {
                await SignInPageAsync(context, 200, path, null).ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(context, 401, "application/json", JsonSerializer.Serialize(new { error = SignInFirst })).ConfigureAwait(false);
            }
            return false;
        }

        string? message = null;
        int status = 401;
        if (BasicPassword(request.Headers["Authorization"]) is { } given)
        {
            var verdict = _throttle.Check(RemoteAddress(request), given, password, out var wait);
            switch (verdict)
            {
                case SignInThrottle.Verdict.Right:
                    return true;
                case SignInThrottle.Verdict.Wrong:
                    await PauseAsync().ConfigureAwait(false);
                    message = WrongPassword;
                    break;
                default:
                    status = 429;
                    message = TooMany(verdict, wait);
                    context.Response.Headers["Retry-After"] = RetryAfter(wait);
                    break;
            }
        }
        if (status == 401)
        {
            // A script: one that only gives its password when asked still can.
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"pdn-mailcast receiver\", charset=\"UTF-8\"";
        }
        await RespondAsync(context, status, "application/json", JsonSerializer.Serialize(new { error = message ?? SignInFirst })).ConfigureAwait(false);
        return false;
    }

    private const string SignInFirst = "Sign in first: this page needs its password.";

    private static bool IsBrowser(HttpListenerRequest request) =>
        IsBrowser(request.Headers["Sec-Fetch-Mode"], request.Headers["User-Agent"], request.Headers["Accept"]);

    /// <summary>
    /// Whether a request comes from a browser rather than a script, from its
    /// <c>Sec-Fetch-Mode</c>, <c>User-Agent</c> and <c>Accept</c> headers. A browser sends
    /// <c>Sec-Fetch-Mode</c> only to HTTPS and to localhost, never to this page on a plain-HTTP
    /// network address, which is where the sign-in page matters most; but every browser's user
    /// agent starts <c>Mozilla/</c>, and its pages ask for <c>text/html</c>. curl, wget and
    /// python-requests send none of these, so a script is answered as before.
    /// </summary>
    internal static bool IsBrowser(string? secFetchMode, string? userAgent, string? accept) =>
        secFetchMode is not null
        || (userAgent?.StartsWith("Mozilla/", StringComparison.Ordinal) ?? false)
        || (accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Waits <see cref="FailureDelay"/> before a wrong password is answered.</summary>
    private Task PauseAsync() =>
        FailureDelay <= TimeSpan.Zero ? Task.CompletedTask
        : Pause is { } pause ? pause(FailureDelay)
        : Task.Delay(FailureDelay, _host.Time);

    private static string TooMany(SignInThrottle.Verdict verdict, TimeSpan wait, string what = "passwords") =>
        verdict == SignInThrottle.Verdict.Busy ? Busy(wait, what) : LockedOut(wait, what);

    private static string Busy(TimeSpan wait, string what)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return $"There have been too many wrong {what} lately, so it is slowed down. Try again in {seconds} second{(seconds == 1 ? "" : "s")}.";
    }

    private const string WrongPassword = "That password is not right.";

    private static string LockedOut(TimeSpan wait, string what)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Too many wrong {what} from this address. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    private static string RetryAfter(TimeSpan wait) =>
        Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IPAddress RemoteAddress(HttpListenerRequest request) => request.RemoteEndPoint?.Address ?? IPAddress.None;

    /// <summary>The session token in the request's cookie, if it has one.</summary>
    private string? SessionToken(HttpListenerRequest request)
    {
        string prefix = CookieName + "=";
        foreach (string part in (request.Headers["Cookie"] ?? "").Split(';'))
        {
            string cookie = part.Trim();
            if (cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                return cookie[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// The session cookie. HttpOnly, so no script on the page can read it; SameSite=Strict, so no
    /// other site's page can make the browser send it; not Secure, because the page is plain HTTP.
    /// Without <paramref name="maxAge"/> the browser forgets it when it closes.
    /// </summary>
    private void SetSessionCookie(HttpListenerContext context, string value, TimeSpan? maxAge) =>
        context.Response.AppendHeader("Set-Cookie", $"{CookieName}={value}; Path=/; HttpOnly; SameSite=Strict"
            + (maxAge is { } age ? $"; Max-Age={Math.Max(0, (long)age.TotalSeconds)}" : ""));

    /// <summary>Starts a session for this browser and gives it the cookie.</summary>
    private void StartSession(HttpListenerContext context, bool remember)
    {
        var (token, session) = _sessions.Create(remember);
        SetSessionCookie(context, token, remember ? session.Expires - _host.Time.GetUtcNow() : null);
    }

    /// <summary>The sign-in form: a password, and whether to keep this device signed in.</summary>
    internal sealed record SignInForm(string? Password, bool Remember);

    /// <summary>
    /// POST /login, as JSON (<c>{"password": "...", "remember": true}</c>, from the sign-in page's
    /// script) or as a plain form post (the same page without its script). A wrong password is
    /// answered after <see cref="FailureDelay"/>, and an address that keeps getting it wrong is
    /// locked out for a while (<see cref="SignInThrottle"/>).
    /// </summary>
    private async Task SignInAsync(HttpListenerContext context)
    {
        var request = context.Request;
        bool form = MediaType(request) == "application/x-www-form-urlencoded";
        if (!form && MediaType(request) != "application/json")
        {
            await RespondAsync(context, 415, "application/json", JsonSerializer.Serialize(new { error = "Send this as application/json or as a form." })).ConfigureAwait(false);
            return;
        }
        var (answered, body) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        SignInForm? given = null;
        if (form)
        {
            var fields = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body!));
            given = new SignInForm(fields["password"], fields["remember"] is "on" or "true" or "1");
        }
        else
        {
            try
            {
                given = JsonSerializer.Deserialize<SignInForm>(body, ReceiverConfig.Json);
            }
            catch (JsonException)
            {
            }
        }
        string password = _host.Config.Web.Password;
        if (password.Length == 0)
        {
            // Nothing to sign in to.
            await SignedInAsync(context, form).ConfigureAwait(false);
            return;
        }
        if (given?.Password is not { } attempt)
        {
            await SignInRefusedAsync(context, form, 400, "Enter the password.").ConfigureAwait(false);
            return;
        }
        var from = RemoteAddress(request);
        var verdict = _throttle.Check(from, attempt, password, out var wait);
        switch (verdict)
        {
            case SignInThrottle.Verdict.Right:
                StartSession(context, given.Remember);
                _log($"web: signed in from {from}{(given.Remember ? $", kept signed in for {SessionStore.RememberedLifetime.TotalDays:0} days" : "")}");
                await SignedInAsync(context, form).ConfigureAwait(false);
                break;
            case SignInThrottle.Verdict.Wrong:
                await PauseAsync().ConfigureAwait(false);
                await SignInRefusedAsync(context, form, 401, WrongPassword).ConfigureAwait(false);
                break;
            default:
                context.Response.Headers["Retry-After"] = RetryAfter(wait);
                await SignInRefusedAsync(context, form, 429, TooMany(verdict, wait)).ConfigureAwait(false);
                break;
        }
    }

    private static async Task SignedInAsync(HttpListenerContext context, bool form)
    {
        if (form)
        {
            Redirect(context, "./", 303);
            return;
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { signedIn = true })).ConfigureAwait(false);
    }

    private static async Task SignInRefusedAsync(HttpListenerContext context, bool form, int status, string why)
    {
        if (form)
        {
            await SignInPageAsync(context, status, "/login", why).ConfigureAwait(false);
            return;
        }
        await RespondAsync(context, status, "application/json", JsonSerializer.Serialize(new { error = why })).ConfigureAwait(false);
    }

    /// <summary>POST /logout: ends this browser's session, if it has one, and clears its cookie.</summary>
    private async Task SignOutAsync(HttpListenerContext context)
    {
        var (answered, _) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (_sessions.Remove(SessionToken(context.Request)) is { } ended)
        {
            CloseSockets(id => id == ended);
            _log($"web: signed out from {RemoteAddress(context.Request)}");
        }
        SetSessionCookie(context, "", TimeSpan.Zero);
        if (MediaType(context.Request) == "application/x-www-form-urlencoded")
        {
            Redirect(context, "./", 303);
            return;
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { signedOut = true })).ConfigureAwait(false);
    }

    /// <summary>The sign-in page, for a browser that asked for <paramref name="path"/>; its form posts to /login by a relative path.</summary>
    private static async Task SignInPageAsync(HttpListenerContext context, int status, string path, string? message)
    {
        int depth = Math.Max(0, path.Count(c => c == '/') - 1);
        string html = SignInTemplate.Value
            .Replace("{{action}}", string.Concat(Enumerable.Repeat("../", depth)) + "login", StringComparison.Ordinal)
            .Replace("{{message}}", WebUtility.HtmlEncode(message ?? ""), StringComparison.Ordinal);
        context.Response.Headers["X-Frame-Options"] = "DENY";
        await RespondAsync(context, status, "text/html; charset=utf-8", html).ConfigureAwait(false);
    }

    private static string MediaType(HttpListenerRequest request) =>
        (request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();

    private readonly object _mailVersionGate = new();
    private Packet.Mailcast.MailSnapshot? _lastMailSnapshot;
    private int _mailSnapshots;

    /// <summary>
    /// A number that differs whenever anything <see cref="MailList"/> shows does: a new snapshot of
    /// the bulletins held (one is made at each change), a delivery answer, the BBS's last failure or
    /// next try, a part-received bulletin's progress, the directory's signature, the newest
    /// directory going stale at UTC midnight (the list's summary says so), or the archive settings. Only for comparing with the last one, in the same run of the receiver.
    /// </summary>
    internal int MailVersion(Packet.Mailcast.MailSnapshot mail, IReadOnlyList<Packet.Mailcast.HeldBulletin> held,
        Packet.Mailcast.DirectoryCheck? newestCheck, Packet.Mailcast.HeardDirectoryCheck? lastHeard, DateOnly? staleDirectory)
    {
        int snapshots;
        lock (_mailVersionGate)
        {
            if (!ReferenceEquals(mail, _lastMailSnapshot))
            {
                _lastMailSnapshot = mail;
                _mailSnapshots++;
            }
            snapshots = _mailSnapshots;
        }
        var hash = new HashCode();
        hash.Add(snapshots);
        hash.Add(_host.Ledger.Changes);
        hash.Add(_host.Delivery.LastFailure);
        hash.Add(_host.Delivery.NextAttempt);
        foreach (var h in held)
        {
            hash.Add(h);
        }
        hash.Add(newestCheck?.Describe());
        hash.Add(staleDirectory);
        hash.Add(lastHeard is { Check.Usable: false } ? lastHeard.Check.Reason : null);
        hash.Add(_host.Config.Archive.Days);
        hash.Add(_host.Config.Archive.MaxMegabytes);
        return hash.ToHashCode();
    }

    /// <summary>What the page shows, in one object.</summary>
    internal object Status()
    {
        var config = _host.Config;
        var slot = _host.Slots.Last;
        var (directory, _) = _host.Intake.Progress();
        var (newestCheck, lastHeard) = _host.Intake.DirectoryChecks();
        var held = _host.Intake.HeldBulletins();
        var mail = _host.Intake.Mail();
        double? liveTone = _host.Pipeline?.Tone.LiveFrequencyHz;
        double dialHz = _host.DialHz;
        double centreHz = _host.AudioCentreHz;
        return new
        {
            version = ReceiverHost.Version,
            // Whether the apt repository has a newer receiver; null when nothing checks (tests).
            update = Updates?.View(),
            // Issue #154: the config file has been changed by hand since the receiver read it,
            // so the page will not save over it until the receiver is restarted.
            configChanged = _configFile?.ChangedByHand ?? false,
            audio = new
            {
                source = config.Audio,
                state = _host.AudioState,
                // What the spectrogram shows instead while there is no audio: see AudioView.
                kind = AudioKind(config.Audio),
                live = _host.Audio.Phase == AudioPhase.Listening,
                phase = AudioPhaseName(_host.Audio.Phase),
                reopens = _host.Audio.Reopens,
                forSlot = _host.Audio.ForSlot,
                problem = _host.Audio.Problem,
                retryAt = _host.Audio.RetryAt,
                // pdn-soundmodem gives something this receiver cannot use, rather than not answering.
                lasting = _host.Audio.Lasting,
                // The dial the audio is heard on: pdn-soundmodem's own once it has said one.
                dialKHz = dialHz / 1000,
                dialFromSoundModem = _host.DialFromSoundModem,
                centreKHz = config.CentreHz / 1000,
                webSdr = WebSdrView(config.Audio, _host.WebSdrAbout),
            },
            bbs = new
            {
                target = ReceiverHost.DescribeBbs(config.Bbs),
                lastFailure = _host.Delivery.LastFailure,
                nextAttempt = _host.Delivery.NextAttempt,
                waiting = _host.Intake.Mail().Waiting, // from memory: this page is asked every few seconds
                // Whether there is a BBS to send through, for the daily report's opt-in; null
                // until something has actually been tried (issue #99).
                reachable = BbsReachable(_host.Delivery.HasAttempted, _host.Ledger.Recent, _host.Feedback.Last, _host.Delivery.LastFailure, _lastBbsTestOutcome),
            },
            retune = _host.Retuner is not { } retuner ? null : new
            {
                stage = retuner.Stage,
                state = retuner.State,
                rigctld = retuner.Endpoint?.ToString(),
                // pdn-soundmodem's receive window and where, instead of rigctld and LinBPQ.
                window = retuner.Window is { } window ? window.Where : null,
                linBpq = retuner.Window is null ? config.Bpq?.ToString() : null,
                nextSlot = retuner.NextSlot,
                problem = retuner.LastProblem?.Text,
                problemAt = retuner.LastProblem?.At,
            },
            markers = new
            {
                centreHz,
                lowHz = centreHz - OnAir.HalfWidthHz,
                highHz = centreHz + OnAir.HalfWidthHz,
                toneHz = liveTone ?? (slot?.Tone is { } t ? t.FrequencyHz : null),
                toneLive = liveTone is not null,
            },
            schedule = Schedule(config, _host.Followed, _host.Time.GetUtcNow()),
            // Issues #178 and #144: each station's pinned key, when its newest directory was dated,
            // and any directory refused for naming a pinned station under another key.
            sharing = SharingView(),
            stations = StationsView(_host.Intake.Pins(), _host.Intake.StationDirectories(), _host.Intake.KeyConflicts(), _host.Intake.KeyRows()),
            // Every station whose slots are known, whether this receiver listens at them, and if not why.
            listening = _host.Listening.Select(l => new
            {
                station = l.Station,
                slots = SlotSchedule.From(l.Timetable).Describe(),
                frequencyHz = l.FrequencyHz,
                sideband = l.Sideband == Packet.Mailcast.Sideband.Usb ? "usb" : "lsb",
                from = l.Source,
                followed = l.Followed,
                why = l.NotFollowedBecause,
                skipped = config.IsSkipped(l.Station),
                byHand = l.Source == "added by hand",
            }),
            // Issue #224: "Listen for a station" entries, and which are no longer needed.
            listenFor = (config.ListenFor ?? []).Select(e =>
            {
                var kept = _host.ListenForState.Get(e);
                return new
                {
                    callsign = e.Callsign,
                    slots = SlotSchedule.From(e.ToAdvert().Timetable).Describe() + (e.Daylight is null ? " (every slot, no daylight rule)" : ""),
                    frequencyHz = e.FrequencyHz,
                    sideband = e.Sideband.ToLowerInvariant(),
                    locator = e.Locator,
                    addedUtc = kept?.AddedUtc,
                    removedAfter = kept is null ? (DateTimeOffset?)null : kept.AddedUtc + ListenForEntry.Lifetime,
                    retired = kept?.Retired,
                };
            }),
            // Issue #148: what each station's slots came to today (UTC), whether or not the daily report is on.
            heardToday = _host.Feedback.StationFigures(DateOnly.FromDateTime(_host.Time.GetUtcNow().UtcDateTime)).Select(f => new
            {
                station = f.Station,
                listened = f.Listened,
                heard = f.Heard,
                frames = f.Frames,
                completed = f.Completed,
                snrDb = f.SnrDb,
                virtualHeightKm = f.VirtualHeightKm,
            }),
            // Issue #53: the "Listen now" button, for a web SDR only.
            listenNow = ListenNowView(_host, config, _host.Time.GetUtcNow()),
            level = new { lowDbFs = InputLevelMeter.TargetPeakLowDbFs, highDbFs = InputLevelMeter.TargetPeakHighDbFs, advice = LevelAdvice(config.Audio) },
            burst = BurstView(_host.Pipeline?.Burst.Shown),
            // The radio path from M0LTE, measured after each slot from the bursts decoded.
            channel = ChannelTile.View(_host.ChannelWatch, slot, _host.Time.GetUtcNow()),
            slot = slot is null ? null : new
            {
                started = slot.Started,
                scheduled = slot.Scheduled,
                framesHeard = slot.FramesHeard,
                // Issue #133: bytes decoded from those frames, not the bulletins rebuilt from them.
                bytesDecoded = slot.BytesDecoded,
                lastFrame = slot.LastFrame,
                waveform = slot.Waveform,
                frameCounts = slot.FrameCounts,
                // The same, most first and in words, for the page.
                heard = slot.FrameCounts.OrderByDescending(c => c.Value).Select(c => new { waveform = c.Key, words = Waveform.Words(c.Key), frames = c.Value }),
                listedWaveform = slot.ListedWaveform,
                listedWords = slot.ListedWaveform is { } listed ? Waveform.Words(listed) : null,
                tone = slot.Tone is not { } tone ? null : new
                {
                    frequencyHz = Math.Round(tone.FrequencyHz, 1),
                    offsetHz = Math.Round(tone.OffsetHz, 1),
                    snrDb = Math.Round(tone.SnrDb, 1),
                    seconds = Math.Round(tone.Duration.TotalSeconds),
                },
                // Issue #49: why its window ended early, for this slot only; null otherwise.
                endedEarly = _host.LastEarlyEnd is { } early && early.Slot == slot.Scheduled ? early.Reason : null,
            },
            framesHeard = _host.Intake.FramesHeard,
            // Changes whenever anything the Bulletins list shows does, so the page fetches the
            // list only then, not every few seconds whatever happened.
            mailVersion = MailVersion(mail, held, newestCheck, lastHeard,
                directory is not null && directory.Date != DateOnly.FromDateTime(_host.Time.GetUtcNow().UtcDateTime) ? directory.Date : null),
            // Issue #48: a slim list of the slots "what was heard" can show; the full drill-down
            // for one of them is GET /api/slots/{slotTicks}/frames, fetched when the page opens
            // it. slotTicks is the slot's UTC instant in .NET ticks, sent as a string of digits:
            // nothing to URL-escape, and no precision lost. As a JSON number it would be about
            // 6.4e17, past JavaScript's safe integer range (2^53), and the browser would round it
            // to a slot that does not exist. Milliseconds would fit but lose the sub-millisecond
            // part of the slot's identity.
            heardSlots = _host.FrameHistory.Slots.Reverse().Select(s => new
            {
                slot = s.Slot,
                slotTicks = s.Slot.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bursts = s.Bursts.Count,
                frames = s.Bursts.Sum(b => b.Pieces.Count),
            }),
            iono = IonoView(_host.Intake.Ionosphere, _host.Time.GetUtcNow()),
            pskReporter = PskView(_host.Intake.PskReporter, _host.Time.GetUtcNow()),
            // The daily report, when the config's "feedback" turns it on.
            feedback = _host.Feedback.View(),
            // Issue #68: "stale" says the newest directory heard is not today's (UTC), so the
            // page can say plainly that its list is old, rather than letting the bulletins
            // below (which are not limited to that one directory) go unexplained.
            directory = directory is null ? null : new
            {
                date = directory.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                entries = directory.Entries.Count,
                stale = directory.Date != DateOnly.FromDateTime(_host.Time.GetUtcNow().UtcDateTime),
                // "signed by M0LTE" or "not signed".
                signature = newestCheck?.Describe(),
            },
            // The directory heard most recently, used or not: "signed" (by a trusted key, named
            // in "signedBy"), "unsigned", or "rejected" (not used, "reason" says why).
            lastDirectory = lastHeard is null ? null : new
            {
                date = lastHeard.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                state = SignatureWord(lastHeard.Check.State),
                signedBy = lastHeard.Check.SignedBy,
                reason = lastHeard.Check.Reason,
                words = lastHeard.Check.Describe(),
            },
            // Issue #68: every bulletin still relevant, complete or not, whether or not the
            // newest directory heard happens to name it; see Intake.HeldBulletins. Issue #73
            // review: kept on this endpoint, unchanged in shape, for scripts that already read it
            // (such as the CT 150 survey script); the combined Bulletins section on the page
            // itself now reads the same facts from GET /api/mail instead (its "partial" and
            // "items"), which is where new work should look.
            bulletins = held.Select(p =>
            {
                var delivered = p.Bid is null ? null : _host.Ledger.Latest(p.Bid);
                // Issue #47: the rebuilt time is only known once it is held, waiting or archived.
                var mailEntry = p.Complete ? mail.ById(p.ObjectId) : null;
                var final = FinalAnswer(mailEntry, delivered);
                return new
                {
                    bid = p.Bid,
                    title = p.Title,
                    complete = p.Complete,
                    received = p.Received,
                    needed = p.Needed,
                    delivery = delivered is null ? null : DeliveryService.Describe(delivered.Verdict),
                    deliveryShort = delivered is null ? null : DeliveryService.DescribeShort(delivered.Verdict),
                    completedAt = mailEntry?.Completed,
                    deliveredAt = final?.At,
                    deliveredVerdict = final?.Verdict,
                    deliveredDetail = final?.Detail,
                    signature = p.Signature,
                    withheld = p.Withheld,
                };
            }),
            // Issue #73 review: kept on this endpoint, unchanged in shape, for the same reason as
            // "bulletins" above. It also remains the only place a delivery record for a bulletin
            // since pruned from the archive (by archive.days or archive.maxMegabytes) can still be
            // read: the combined Bulletins section's reader, on GET /api/mail, can only show a
            // bulletin's attempts while the bulletin itself is still held. In practice the 200
            // most recent records here age out of relevance before a 30 day archive does, so this
            // is a narrow gap, not a working fallback list; see docs/receiver.md.
            deliveries = _host.Ledger.Recent.Take(40).Select(r => new
            {
                time = r.Time,
                bid = r.Bid,
                title = r.Title,
                verdict = r.Verdict,
                said = DeliveryService.Describe(r.Verdict),
                saidShort = DeliveryService.DescribeShort(r.Verdict),
                detail = r.Detail,
            }),
        };
    }

    /// <summary>
    /// Whether the receiver has a BBS to send through, as far as it knows: null until a delivery
    /// session or a "Test BBS login" has actually been tried (issue #99: before that, there is
    /// nothing to base an answer on, and silence is not the same as "it works"); true once one has
    /// reached the BBS and gotten an answer worth calling reachable, even a refusal of one bulletin
    /// (the BBS answered, so it is there to send through); false once one has been tried and never
    /// got that far. The page offers the daily report only when this is true.
    /// </summary>
    internal static bool? BbsReachable(bool deliveryAttempted, IReadOnlyList<DeliveryRecord> recent, SentReport? report, string? lastFailure, BbsLoginTestOutcome? lastTest)
    {
        bool answered = recent.Any(r => r.Verdict is DeliveryVerdict.Accepted or DeliveryVerdict.AlreadyHad or DeliveryVerdict.Deferred or DeliveryVerdict.Refused or DeliveryVerdict.Unconfirmed)
            || report?.Answer is FeedbackAnswer.Accepted or FeedbackAnswer.Refused or FeedbackAnswer.Deferred
            || lastTest == BbsLoginTestOutcome.Ok;
        if (answered)
        {
            return true;
        }
        bool tried = deliveryAttempted || lastFailure is not null || recent.Count > 0
            || (report is not null && report.Answer != FeedbackAnswer.Pending) || lastTest is not null;
        return tried ? false : null;
    }

    /// <summary>
    /// The ionosonde tile: the head end's newest reading, aged by this receiver's clock and
    /// UNKNOWN once its sounding is older than <see cref="IonoSettings.DefaultStaleAfter"/>, the
    /// head end's own default, with the words the page shows.
    /// Null until one has been heard.
    /// </summary>
    internal static object? IonoView(IonoReading? heard, DateTimeOffset now)
    {
        if (heard is null)
        {
            return null;
        }
        var r = heard.AsOf(now, IonoSettings.DefaultStaleAfter);
        return new
        {
            state = r.State,
            foF2 = r.FoF2,
            mufd100 = r.Mufd100,
            mufd500 = r.Mufd500,
            mufd1000 = r.Mufd1000,
            skipZoneKm = r.SkipZoneKm,
            station = r.Station,
            stationName = r.StationName,
            soundingTimeUtc = r.SoundingTimeUtc,
            ageMinutes = r.AgeMinutes,
            ageWhenSent = heard.AgeMinutes,
            source = r.Source,
            method = r.Method,
            distances = new[]
            {
                new { km = 100, mufMhz = r.Mufd100, verdict = r.At100, words = IonoReading.Words(r.At100) },
                new { km = 500, mufMhz = r.Mufd500, verdict = r.At500, words = IonoReading.Words(r.At500) },
                new { km = 1000, mufMhz = r.Mufd1000, verdict = r.At1000, words = IonoReading.Words(r.At1000) },
            },
            headline = r.Headline(),
            words = r.Summary(now),
            distanceWords = r.State == IonoState.Unknown ? null : r.Distances(),
        };
    }

    /// <summary>
    /// The PSK Reporter part of the propagation tile: the head end's newest reading of live 40 m
    /// spots between UK and Irish stations, aged by this receiver's clock and UNKNOWN once older
    /// than <see cref="PskEvaluator.StaleAfter"/>, with the words the page shows. Null until one
    /// has been heard.
    /// </summary>
    internal static object? PskView(PskReading? heard, DateTimeOffset now)
    {
        if (heard is null)
        {
            return null;
        }
        var r = heard.AsOf(now, PskEvaluator.StaleAfter);
        return new
        {
            state = r.State,
            observedUtc = r.ObservedUtc,
            ageMinutes = r.AgeMinutes,
            windowMinutes = r.WindowMinutes,
            feedDown = r.FeedDown,
            skipZoneKm = r.SkipZoneKm,
            distances = (r.Forty?.Bins ?? []).Select(b => new
            {
                km = b.Km,
                fromKm = b.FromKm,
                toKm = b.ToKm,
                verdict = b.Verdict,
                spots = b.Spots,
                stations = b.Stations,
                snrMedianDb = b.SnrMedianDb,
                closedBy = b.ClosedBy,
                words = PskReading.Words(b.Verdict),
            }),
            headline = r.Headline(),
            words = r.Summary(now),
            distanceWords = r.FeedDown || TimeSpan.FromMinutes(r.AgeMinutes ?? 0) > PskEvaluator.StaleAfter ? null : PskReading.Distances(r.Forty),
        };
    }

    /// <summary>
    /// The burst for the speed tile: the waveform the modem's autobaud locked to, its rate, and
    /// whether the modem is on it now (else it is the last burst any frames came from).
    /// </summary>
    /// <remarks>
    /// Issue #135: the waveform, rate and words stay null until a frame has decoded on the burst.
    /// The autobaud can lock briefly on the tone or the channel probe, to a waveform M0LTE never
    /// sends (75 bps, WN0), before a later layer throws it away as noise; a lock alone is not a speed.
    /// </remarks>
    internal static object? BurstView(HeardBurst? burst) => burst is null ? null : new
    {
        waveform = burst.Frames > 0 ? burst.Waveform : null,
        bps = burst.Frames > 0 ? Waveform.Bps(burst.Waveform) : null,
        words = burst.Frames > 0 ? Waveform.Words(burst.Waveform) : null,
        live = burst.Live,
        frames = burst.Frames,
        started = burst.Started,
        ended = burst.Ended,
    };

    /// <summary>
    /// How the page judges the input level: <c>target</c> for a sound card, whose level you set,
    /// so it says when peaks are outside -18 to -9 dBFS; <c>clippingOnly</c> for a web SDR or a
    /// recording, whose level is not yours to set and which the modem copes with unless it clips
    /// (SDRconnect's IQ too, demodulated here at the level it arrives at);
    /// and for pdn-soundmodem, whose level is set there, not here.
    /// </summary>
    internal static string LevelAdvice(string audio) => AudioSource.Parse(audio).IsSoundCard ? "target" : "clippingOnly";

    /// <summary>The audio source's kind, as the page names it: <c>webSdr</c>, <c>soundCard</c>, <c>soundModem</c>, <c>sdrConnect</c> or <c>recording</c>.</summary>
    internal static string AudioKind(string audio) => AudioSource.Parse(audio).Kind switch
    {
        AudioSourceKind.UberSdr => "webSdr",
        AudioSourceKind.SdrConnect => "sdrConnect",
        AudioSourceKind.Wav => "recording",
        AudioSourceKind.SoundModem => "soundModem",
        _ => "soundCard",
    };

    /// <summary>
    /// Which web SDR the audio comes from, for the page: its address from the config, a link to
    /// its own page, and once it has been opened, what it says about itself (callsign, name and
    /// location). Null for a sound card or a recording.
    /// </summary>
    internal static object? WebSdrView(string audio, string? about)
    {
        var source = AudioSource.Parse(audio);
        if (source.Kind != AudioSourceKind.UberSdr)
        {
            return null;
        }
        var endpoint = Packet.SoundModem.UberSdr.UberSdrDevice.Parse(source.Target);
        return new { host = endpoint.ToString(), url = endpoint.PublicUrl, about };
    }

    /// <summary>
    /// Issue #53's "Listen now" tile: null unless the audio is a web SDR. <c>until</c> is set
    /// while a session it opened is still running, for the page's countdown.
    /// </summary>
    internal static object? ListenNowView(ReceiverHost host, ReceiverConfig config, DateTimeOffset now)
    {
        if (AudioSource.Parse(config.Audio).Kind != AudioSourceKind.UberSdr)
        {
            return null;
        }
        // Issue #80: accounts for a window closed early (not just the clock schedule), or the
        // page would offer "Listen now" as refused for a reason that is not true.
        var nextOpens = host.NextWebSdrWindowOpens(now);
        var until = host.ListenNow.ActiveUntil(now, nextOpens);
        bool alreadyListening = (host.Audio.Phase is AudioPhase.Listening or AudioPhase.Reconnecting) && until is null;
        return new
        {
            usesLeft = host.ListenNow.UsesLeft(now),
            usesMax = ListenNowService.MaxPerDay,
            minutes = (int)ListenNowService.Duration.TotalMinutes,
            until,
            canUse = until is null && host.ListenNow.Problem(now, nextOpens, alreadyListening) is null,
            problem = until is null ? host.ListenNow.Problem(now, nextOpens, alreadyListening) : null,
        };
    }

    internal static string AudioPhaseName(AudioPhase phase) => phase switch
    {
        AudioPhase.Closed => "closed",
        AudioPhase.Opening => "opening",
        AudioPhase.Listening => "listening",
        AudioPhase.Reconnecting => "reconnecting",
        AudioPhase.Failed => "failed",
        AudioPhase.Ended => "ended",
        _ => "starting",
    };

    /// <summary>The stations this receiver knows, for the page.</summary>
    internal static object StationsView(IReadOnlyList<Packet.Mailcast.PinnedStation> pins, IReadOnlyList<Packet.Mailcast.StationDirectory> heard, IReadOnlyList<Packet.Mailcast.KeyConflict> conflicts, IReadOnlyList<Packet.Mailcast.KeyRow>? keys = null) => new
    {
        // Issue #146: every trusted key, where it came from, and when its directory was last heard.
        keys = (keys ?? []).Select(k => new
        {
            station = k.Station,
            keyId = Packet.Mailcast.KeyId.Format(k.Key.KeyId),
            publicKey = Convert.ToBase64String(k.Key.PublicKey),
            source = k.Source switch
            {
                Packet.Mailcast.KeySource.StationsFile => "stations file",
                Packet.Mailcast.KeySource.Config => "config file",
                Packet.Mailcast.KeySource.Learnt => "learnt on first hearing",
                _ => "added on this page",
            },
            lastHeard = k.LastHeard,
            canForget = k.CanForget,
        }),
        known = pins.Select(p => new
        {
            station = p.Station,
            keyId = Packet.Mailcast.KeyId.Format(p.Key.KeyId),
            // "built in or configured", or when the key was learnt from the station's first signed directory.
            pinnedAt = p.PinnedAt,
            heardDirectory = heard.FirstOrDefault(h => h.Station == p.Station)?.Directory.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }),
        conflicts = conflicts.Select(c => new
        {
            station = c.Station,
            pinnedKeyId = Packet.Mailcast.KeyId.Format(c.PinnedKeyId),
            heardKeyId = Packet.Mailcast.KeyId.Format(c.HeardKeyId),
            heardAt = c.HeardAt,
        }),
    };

    /// <summary>
    /// When the slots are, for the page: in words, today's slots, the next slot and the one before
    /// it if that may still be on, and for a web SDR which slots it listens to and the next of
    /// those.
    /// </summary>
    internal static object Schedule(ReceiverConfig config, FollowedSlots followed, DateTimeOffset now)
    {
        if (followed.Stations.Count == 0)
        {
            return new
            {
                words = followed.Describe(),
                followed = Array.Empty<object>(),
                slotsPerDay = 0,
                today = Array.Empty<string>(),
                toneSeconds = OnAir.ToneSeconds,
            };
        }
        // Issue #144: the next slot, today's and the web SDR's are those of every station
        // followed; the timetable's own fields (every, anchor, daylight) are the first one's.
        var schedule = followed.First;
        var next = followed.NextActiveStart(now);
        var before = followed.LatestActiveStart(now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var todays = followed.ActiveOn(today);
        bool webSdr = AudioSource.Parse(config.Audio).Kind == AudioSourceKind.UberSdr;
        var (opens, closes, listenSlot) = ListeningWindow.Next(now, followed);
        var listened = ListeningWindow.WebSdrSlotsOn(followed, today);
        static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new
        {
            words = followed.Describe(),
            // Each station followed, with its slots in words.
            followed = followed.Stations.Select(f => new { station = f.Station, words = f.Schedule.Describe() }),
            everyMinutes = schedule.EveryMinutes,
            slotUtc = schedule.Anchor.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            slotsPerDay = todays.Count,
            today = todays.Select(Hhmm),
            daylight = schedule.Daylight is not { } d ? null : new
            {
                locator = d.Locator,
                afterSunriseMinutes = d.AfterSunriseMinutes,
                beforeSunsetMinutes = d.BeforeSunsetMinutes,
                words = d.Describe(),
            },
            next,
            // The slot before, while it may still be on: within the time a web SDR would stay open for it.
            recent = before is { } b && b < now && now - b < ReceiverConfig.WebSdrAfter ? b : (DateTimeOffset?)null,
            toneSeconds = OnAir.ToneSeconds,
            webSdr = !webSdr ? null : new
            {
                slotsPerDay = listened.Count,
                times = listened.Select(Hhmm),
                slot = listenSlot,
                opens,
                closes,
                openNow = opens <= now,
            },
        };
    }

    /// <summary>The settings as the form shows them: the password is never sent back, only whether one is set.</summary>
    internal static object SettingsView(ReceiverConfig config, IReadOnlyList<string>? followed = null) => new
    {
        audio = config.Audio,
        locator = config.Locator,
        dialKHz = config.ResolvedDialKHz,
        dialKHzSet = config.DialKHz is not null,
        sources = config.AcceptedSources,
        sourcesSet = config.Sources is not null,
        sourcesFollowed = followed ?? [],
        bbs = new
        {
            type = config.Bbs.Type,
            host = config.Bbs.Host,
            port = config.Bbs.Port,
            login = config.Bbs.Login,
            passwordSet = config.Bbs.Password.Length > 0,
            command = config.Bbs.Command,
        },
        web = new
        {
            lan = config.Web.Lan,
            passwordSet = config.Web.Password.Length > 0,
        },
        // The daily report, a public bulletin to MCAST: whether it is on, and the callsign it is sent from.
        feedback = new
        {
            enabled = config.Feedback?.Enabled ?? false,
            callsign = config.Feedback?.Callsign,
        },
        soundCards = SoundCards(),
        windows = OperatingSystem.IsWindows(),
    };

    /// <summary>
    /// The form's answer. An empty password keeps the one already set; so does an empty
    /// <paramref name="PagePassword"/>, the page's own, which needs <paramref name="CurrentPagePassword"/>
    /// to change once there is one. A null <paramref name="Sources"/> keeps the callsigns accepted as they are,
    /// and a null <paramref name="Feedback"/> the daily report as it is. A null <paramref name="DialKHz"/>
    /// keeps the dial as it is too: the settings page only sends one for a sound card, from the
    /// receive filter choice (<c>7052</c> or <c>7052.3</c>); a web SDR source keeps the usual 7.052.
    /// A null <paramref name="Locator"/> keeps the receiver's locator as it is, and an empty one clears it.
    /// </summary>
    internal sealed record SettingsForm(string Audio, string Type, string Host, int Port, string Login, string? Password, string Command,
        string? PagePassword = null, string? CurrentPagePassword = null, IReadOnlyList<string?>? Sources = null, FeedbackForm? Feedback = null,
        double? DialKHz = null, string? Locator = null);

    /// <summary>
    /// The daily report's part of the settings form: on or off, and the callsign it is sent from.
    /// Turned off with no callsign, the one already set is kept, for when it is turned on again.
    /// </summary>
    internal sealed record FeedbackForm(bool Enabled, string? Callsign);

    /// <summary>The daily report's settings after <paramref name="form"/>; throws <see cref="ConfigException"/> for a callsign it cannot be sent from.</summary>
    internal static FeedbackSettings? ApplyFeedback(FeedbackSettings? current, FeedbackForm? form)
    {
        if (form is null)
        {
            return current;
        }
        string callsign = (form.Callsign ?? "").Trim().ToUpperInvariant();
        if (form.Enabled && callsign.Length == 0)
        {
            throw new ConfigException("Enter your callsign: the daily report is sent from it.");
        }
        if (form.Enabled && !FeedbackSettings.IsPlausibleCallsign(callsign))
        {
            throw new ConfigException($"\"{Ascii.Clean(callsign)}\" does not look like a callsign. Give your own, such as G4ABC, with no SSID or \"/\".");
        }
        if (!form.Enabled && callsign.Length == 0)
        {
            callsign = current?.Callsign ?? "";
        }
        return new FeedbackSettings { Enabled = form.Enabled, Callsign = callsign.Length == 0 ? null : callsign };
    }

    /// <summary>
    /// The BBS login as typed, trimmed. For LinBPQ it is kept in the case typed: LinBPQ compares
    /// a login with its USER= lines case-sensitively, so upper-casing it, as this once did, turned
    /// a working "m7taw" into a refused "M7TAW" (issue #134). FBB ignores the case, and its
    /// callsigns are upper case, so for FBB it is upper-cased as before.
    /// </summary>
    internal static string BbsLogin(BbsKind kind, string login) =>
        kind == BbsKind.Fbb ? login.Trim().ToUpperInvariant() : login.Trim();

    /// <summary>Applies a settings form to <paramref name="current"/>; throws <see cref="ConfigException"/> for one that cannot work.</summary>
    internal static ReceiverConfig Apply(ReceiverConfig current, SettingsForm form)
    {
        if (form.Audio is null || form.Host is null || form.Login is null || form.Command is null || form.Type is null)
        {
            throw new ConfigException("The form is missing a setting.");
        }
        if (!Enum.TryParse<BbsKind>(form.Type, ignoreCase: true, out var kind))
        {
            throw new ConfigException($"\"{form.Type}\" is not a kind of BBS this receiver knows; use linBpq or fbb");
        }
        bool elsewhere = kind != current.Bbs.Type
            || form.Port != current.Bbs.Port
            || !string.Equals(form.Host.Trim(), current.Bbs.Host, StringComparison.OrdinalIgnoreCase);
        if (elsewhere && string.IsNullOrEmpty(form.Password))
        {
            // Otherwise anything that could post this form could send the saved password to a
            // BBS of its own choosing.
            throw new ConfigException("The BBS's address or type has changed: enter its password again.");
        }
        var next = current with
        {
            Audio = form.Audio.Trim(),
            DialKHz = form.DialKHz is { } dial && dial != current.ResolvedDialKHz ? dial : current.DialKHz,
            Locator = form.Locator is null ? current.Locator : form.Locator.Trim() is { Length: > 0 } locator ? locator : null,
            Bbs = current.Bbs with
            {
                Type = kind,
                Host = form.Host.Trim(),
                Port = form.Port,
                Login = BbsLogin(kind, form.Login),
                Password = string.IsNullOrEmpty(form.Password) ? current.Bbs.Password : form.Password,
                Command = form.Command.Trim(),
            },
            // The caller has checked CurrentPagePassword: it needs the sign-in throttle.
            Web = string.IsNullOrEmpty(form.PagePassword) ? current.Web : current.Web with { Password = form.PagePassword },
            Sources = form.Sources is null ? current.Sources : CallsignList.Parse(form.Sources),
            Feedback = ApplyFeedback(current.Feedback, form.Feedback),
        };
        next.Validate();
        return next;
    }

    /// <summary>
    /// Reads a state-changing request's JSON body, or answers the request itself and returns null:
    /// 415 for a body that is not application/json (a form a page elsewhere can post without
    /// asking, text/plain or a form encoding, is not one) and 413 for one over
    /// <see cref="MaxFormBytes"/>. A body that is not a <typeparamref name="T"/> gives null with
    /// nothing answered yet, for the caller to say why.
    /// </summary>
    private static async Task<(bool Answered, T? Value)> ReadJsonAsync<T>(HttpListenerContext context)
        where T : class
    {
        if (MediaType(context.Request) != "application/json")
        {
            await RespondAsync(context, 415, "application/json", JsonSerializer.Serialize(new { error = "Send this as application/json." })).ConfigureAwait(false);
            return (true, null);
        }
        var (answered, body) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return (true, null);
        }
        try
        {
            return (false, JsonSerializer.Deserialize<T>(body, ReceiverConfig.Json));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>Reads a request's body, or answers 413 for one over <see cref="MaxFormBytes"/> and gives null.</summary>
    private static async Task<(bool Answered, byte[]? Body)> ReadBodyAsync(HttpListenerContext context, int maxBytes = MaxFormBytes)
    {
        if (context.Request.ContentLength64 > maxBytes)
        {
            await RespondAsync(context, 413, "application/json", JsonSerializer.Serialize(new { error = "That form is too large." })).ConfigureAwait(false);
            return (true, null);
        }
        var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await context.Request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > maxBytes)
            {
                await RespondAsync(context, 413, "application/json", JsonSerializer.Serialize(new { error = "That form is too large." })).ConfigureAwait(false);
                return (true, null);
            }
        }
        return (false, body.ToArray());
    }

    private async Task SaveSettingsAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<SettingsForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return; // not JSON, or too large
        }
        if (form is null)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "That was not a settings form." })).ConfigureAwait(false);
            return;
        }

        var current = _host.Config;
        string pagePassword = current.Web.Password;
        // Before anything changes: whether this browser is signed in, to keep it so under a new password.
        var mine = pagePassword.Length > 0 ? _sessions.Find(SessionToken(context.Request)) : null;
        bool newPagePassword = !string.IsNullOrEmpty(form.PagePassword);
        if (newPagePassword && pagePassword.Length > 0)
        {
            var verdict = _throttle.Check(RemoteAddress(context.Request), form.CurrentPagePassword ?? "", pagePassword, out var wait);
            switch (verdict)
            {
                case SignInThrottle.Verdict.Right:
                    break;
                case SignInThrottle.Verdict.Wrong:
                    await PauseAsync().ConfigureAwait(false);
                    await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "To change the page password, enter the current one too." })).ConfigureAwait(false);
                    return;
                default:
                    context.Response.Headers["Retry-After"] = RetryAfter(wait);
                    await RespondAsync(context, 429, "application/json", JsonSerializer.Serialize(new { error = TooMany(verdict, wait) })).ConfigureAwait(false);
                    return;
            }
        }

        ReceiverConfig next;
        try
        {
            next = Apply(current, form);
        }
        catch (ConfigException e)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = e.Message })).ConfigureAwait(false);
            return;
        }

        if (_configFile is not null)
        {
            try
            {
                // Issue #154: never over a hand edit, which would be lost without a word.
                switch (_configFile.TrySave(next, _log))
                {
                    case ConfigFile.SaveOutcome.EditedByHand:
                        await RespondAsync(context, 409, "application/json", JsonSerializer.Serialize(new { error = ConfigFile.EditedByHand + "." })).ConfigureAwait(false);
                        return;
                    case ConfigFile.SaveOutcome.Unreadable:
                        await RespondAsync(context, 503, "application/json", JsonSerializer.Serialize(new { error = ConfigFile.Unreadable + "." })).ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await RespondAsync(context, 500, "application/json", JsonSerializer.Serialize(new { error = $"Cannot write {_configFile.Path}: {Ascii.Clean(e.Message)}" })).ConfigureAwait(false);
                return;
            }
        }
        _log(ConfigFile.Changes(current, next) is { } changes ? $"config: saved from the page: {changes}" : "config: saved from the page, with nothing changed");
        // The line above says the daily report going on or off, and from which callsign.
        _host.Reconfigure(next);
        if (!string.Equals(next.Web.Password, pagePassword, StringComparison.Ordinal))
        {
            _sessions.NoticePasswordChange(); // signs every browser out
            CloseSockets(_ => true);
            _log("web: the page password was changed from the page");
            if (mine is not null || pagePassword.Length == 0)
            {
                // Except this one, which knew the old password and gave the new.
                StartSession(context, mine?.Remembered ?? false);
            }
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { saved = true })).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #73: one page of the bulletins held, newest first: <c>offset</c> and <c>limit</c> (at
    /// most <see cref="MaxMailPage"/>) from the query string, plus everything the combined
    /// Bulletins section needs alongside that page so the merge can happen here rather than in
    /// the page's script:
    /// <list type="bullet">
    /// <item>"partial": every object still part-received (issue #68's progress bars), named by a
    /// remembered directory or not; never paged, since there are normally few of them and they
    /// belong on top of every page the section shows (but the page itself only puts them there on
    /// its first page). Each says whether the head end has stopped sending it ("noLongerSent"),
    /// and if so when it will be cleared ("clearsAt").</item>
    /// <item>"delivered", "alreadyHad" and "refused": how many archived bulletins answered each
    /// way, for the summary line ("N coming in, N waiting for your BBS, N delivered, N already
    /// had"), alongside the existing "waiting" count. Issue #86: "delivered" means the BBS
    /// accepted the bulletin outright; "alreadyHad" is its own count now, rather than folded into
    /// "delivered" as it was before.</item>
    /// </list>
    /// </summary>
    internal object MailList(System.Collections.Specialized.NameValueCollection query)
    {
        int offset = Math.Max(0, int.TryParse(query["offset"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int o) ? o : 0);
        int limit = int.TryParse(query["limit"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int l) ? Math.Clamp(l, 1, MaxMailPage) : 50;
        var mail = _host.Intake.Mail(); // in memory, newest first, already counted: no files, no lock, no sorting
        var held = _host.Intake.HeldBulletins();
        // What the directories still remembered say of each: "signed by M0LTE" only once its own hash matched (see HeldBulletin.Signature).
        var signatures = held.Where(h => h.Signature is not null).ToDictionary(h => h.ObjectId, h => h.Signature!);
        var partial = held.Where(h => !h.Complete).Select(h => new
        {
            bid = h.Bid,
            title = h.Title,
            received = h.Received,
            needed = h.Needed,
            // The head end has stopped sending it, so it can never be finished, and it is
            // cleared at clearsAt; false, with clearsAt null, while it is still being sent.
            noLongerSent = h.NoLongerSentSince is not null,
            clearsAt = h.ClearedAt,
            signature = signatures.GetValueOrDefault(h.ObjectId),
            // Rebuilt, but only a directory that failed its signature check lists it.
            withheld = h.Withheld,
        }).ToList();
        var (newestCheck, lastHeard) = _host.Intake.DirectoryChecks();
        // Issue #147: other copies of a delivered bulletin, shown as one short line under it.
        var copies = _host.Intake.OtherCopies().ToLookup(c => c.Bid, StringComparer.OrdinalIgnoreCase);
        var shownFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // once per BID, not under every row with it
        return new
        {
            total = mail.Count,
            waiting = mail.Waiting,
            archived = mail.Archived,
            // Counted once, when the snapshot was built: see MailSnapshot (issue #73 review).
            delivered = mail.Delivered,
            // Issue #86: its own count, no longer folded into "delivered".
            alreadyHad = mail.AlreadyHad,
            refused = mail.Refused,
            partial,
            // Issue #145: complete canonical bulletins waiting for their station's details.
            awaiting = _host.Intake.Awaiting().Select(a => new
            {
                id = Packet.Mailcast.ObjectId.Format(a.ObjectId),
                bid = a.Bulletin.Bid,
                title = a.Bulletin.Title,
                since = a.Since,
            }).ToList(),
            offset,
            limit,
            archive = new { days = _host.Config.Archive.Days, maxMegabytes = _host.Config.Archive.MaxMegabytes },
            items = mail.Page(offset, limit).Select(m => MailView(m, signatures.GetValueOrDefault(m.ObjectId), shownFor.Add(m.Bid) ? copies[m.Bid] : [])).ToList(),
            // The newest directory in use: "signed by M0LTE" or "not signed"; and, when the
            // directory heard most recently was not used, why.
            signature = newestCheck?.Describe(),
            rejected = lastHeard is { Check.Usable: false } ? lastHeard.Check.Reason : null,
        };
    }

    private static string SignatureWord(Packet.Mailcast.SignatureState state) => state switch
    {
        Packet.Mailcast.SignatureState.Trusted => "signed",
        Packet.Mailcast.SignatureState.None => "unsigned",
        _ => "rejected",
    };

    /// <summary>
    /// The BBS's final answer for a bulletin on the progress list, if it has one: from the archive
    /// copy, or from the delivery record when no copy is kept. One back in the outbox, sent again,
    /// has none yet, whatever the BBS said the time before; nor does one only deferred.
    /// </summary>
    private static (DateTimeOffset At, Packet.Mailcast.BbsVerdict Verdict, string? Detail)? FinalAnswer(Packet.Mailcast.MailEntry? held, DeliveryRecord? delivered)
    {
        if (held is not null)
        {
            return held.Waiting || held.Verdict is not { } verdict ? null : (held.Time, verdict, held.Detail);
        }
        return delivered is { Verdict: DeliveryVerdict.Accepted or DeliveryVerdict.AlreadyHad or DeliveryVerdict.Refused }
            ? (delivered.Time, DeliveryService.Final(delivered.Verdict), delivered.Detail)
            : null;
    }

    /// <summary>One bulletin for the list: what it is, where it is, what the BBS has said, and
    /// (issue #73) its delivery history, for the reader to show when it is opened.</summary>
    private object MailView(Packet.Mailcast.MailEntry m, string? signature, IEnumerable<Packet.Mailcast.OtherCopy> otherCopies)
    {
        string status, statusShort;
        DeliveryRecord? last = null;
        if (m.Waiting)
        {
            // Only a non-final answer can be about this stay in the outbox; a final one is from
            // before the bulletin was sent again.
            last = _host.Ledger.Latest(m.Bid) is { Verdict: DeliveryVerdict.Deferred or DeliveryVerdict.Unconfirmed } r ? r : null;
            status = last is not null ? "waiting: " + DeliveryService.Describe(last.Verdict)
                : _host.Delivery.LastFailure is { } failure ? "waiting: " + failure
                : "waiting for the BBS";
            statusShort = "waiting";
        }
        else
        {
            status = m.Verdict switch
            {
                Packet.Mailcast.BbsVerdict.Accepted => "accepted by the BBS",
                Packet.Mailcast.BbsVerdict.AlreadyHad => "the BBS already had it",
                _ => "refused by the BBS",
            };
            statusShort = m.Verdict switch
            {
                Packet.Mailcast.BbsVerdict.Accepted => "accepted",
                Packet.Mailcast.BbsVerdict.AlreadyHad => "already had",
                _ => "refused",
            };
        }
        return new
        {
            id = Packet.Mailcast.ObjectId.Format(m.ObjectId),
            waiting = m.Waiting,
            bid = m.Bid,
            from = m.From,
            to = m.To,
            at = m.At,
            title = m.Title,
            date = m.Date,
            size = m.Size,
            time = m.Time,
            status,
            statusShort,
            verdict = m.Verdict,
            detail = m.Detail ?? last?.Detail,
            lastAttempt = last?.Time,
            nextAttempt = m.Waiting ? _host.Delivery.NextAttempt : null,
            // Issue #47: when it was rebuilt, and (once archived) when the BBS answered, shown together.
            completed = m.Completed,
            delivered = m.Waiting ? (DateTimeOffset?)null : m.Time,
            // As HeldBulletin.Signature: "signed by M0LTE" only once its own hash matched; null once no directory remembered names it.
            signature,
            // Issue #147: "other copy from G8BPQ, not needed", one short line each, under the bulletin.
            otherCopies = otherCopies.Select(c => new { from = c.Station, time = c.Time, text = c.Station is null ? "other copy, not needed" : "other copy from " + c.Station + ", not needed" }).ToList(),
            // Issue #73: every answer this session remembers for this BID, oldest first, for the
            // reader to show as "what the BBS said" each time it was offered.
            attempts = _host.Ledger.History(m.Bid).Select(r => new
            {
                time = r.Time,
                said = DeliveryService.Describe(r.Verdict),
                saidShort = DeliveryService.DescribeShort(r.Verdict),
                detail = r.Detail,
            }).ToList(),
        };
    }

    /// <summary>
    /// One bulletin as stored (its header block, R: lines and body) as plain text. Its content is
    /// whatever was on the air, so the browser is told plainly that it is text, not to guess
    /// otherwise, and to run nothing.
    /// </summary>
    private async Task ServeBulletinAsync(HttpListenerContext context, string id)
    {
        (Packet.Mailcast.MailEntry Entry, byte[] Serialized)? found = null;
        if (Packet.Mailcast.ObjectId.TryParse(id, out ulong objectId))
        {
            try
            {
                found = _host.Intake.ReadMail(objectId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await RespondAsync(context, 500, "text/plain; charset=utf-8", $"Cannot read that bulletin: {Ascii.Clean(e.Message)}\n").ConfigureAwait(false);
                return;
            }
        }
        if (found is not { } held)
        {
            await RespondAsync(context, 404, "text/plain; charset=utf-8", "No such bulletin.\n").ConfigureAwait(false);
            return;
        }
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        await RespondAsync(context, 200, "text/plain; charset=utf-8", BulletinText(held.Serialized)).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #48: one slot's drill-down, its bursts and their pieces, from
    /// <see cref="ReceiverHost.FrameHistory"/>. <paramref name="rawSlot"/> is the slot's identity
    /// as the page's own JSON gives it in <c>heardSlots</c> (<c>slotTicks</c>, a string): the
    /// slot's UTC instant in .NET ticks, plain digits, so nothing here needs URL-escaping. 400
    /// for anything that is not a tick count a date can have; 404 for a slot that was never
    /// kept, or has since been trimmed.
    /// </summary>
    private async Task ServeSlotFramesAsync(HttpListenerContext context, string rawSlot)
    {
        if (!long.TryParse(rawSlot, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ticks))
        {
            await RespondAsync(context, 400, "text/plain; charset=utf-8", "Bad slot.\n").ConfigureAwait(false);
            return;
        }
        DateTimeOffset slot;
        try
        {
            slot = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            await RespondAsync(context, 400, "text/plain; charset=utf-8", "Bad slot.\n").ConfigureAwait(false);
            return;
        }
        var heard = _host.FrameHistory.Slot(slot);
        if (heard is null)
        {
            await RespondAsync(context, 404, "text/plain; charset=utf-8", "That slot is not held (it may have scrolled out of the last few).\n").ConfigureAwait(false);
            return;
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(SlotFramesView(heard), ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    /// <summary>Issue #48: one slot's drill-down, for the status page and the API.</summary>
    internal static object SlotFramesView(HeardSlotPieces slot) => new
    {
        slot = slot.Slot,
        bursts = slot.Bursts.Select(b => new
        {
            started = b.Started,
            waveform = b.Waveform,
            words = b.Waveform is null ? null : Waveform.Words(b.Waveform),
            snrDb = b.SnrDb,
            frames = b.Pieces.Count,
            pieces = b.Pieces.Select(p => new
            {
                heard = p.Heard,
                esi = p.Esi,
                status = p.Status,
                crcGood = p.CrcGood,
                what = p.What,
            }),
        }),
    };

    /// <summary>
    /// BBS mail is octets in no declared character set: read as UTF-8 if it is valid UTF-8, else
    /// as Latin-1, which every octet is. Either way it is sent on as UTF-8.
    /// </summary>
    internal static string BulletinText(byte[] stored)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(stored);
        }
        catch (DecoderFallbackException)
        {
            return Packet.Mailcast.Bulletin.TextEncoding.GetString(stored);
        }
    }

    /// <summary>The resend form: which bulletin, by its object ID.</summary>
    internal sealed record ResendForm(string? Id);

    /// <summary>POST /api/mail/deliver-now: delivers a canonical bulletin that is waiting for its station's details at once (issue #145).</summary>
    private async Task DeliverNowAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<ResendForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (form?.Id is not { } id || !Packet.Mailcast.ObjectId.TryParse(id, out ulong objectId))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Say which bulletin, by its id." })).ConfigureAwait(false);
            return;
        }
        var delivered = _host.Intake.DeliverNow(objectId);
        if (delivered is null)
        {
            await RespondAsync(context, 404, "application/json", JsonSerializer.Serialize(new { error = "That bulletin is not waiting for its station's details." })).ConfigureAwait(false);
            return;
        }
        _log($"mail: {Ascii.Clean(delivered.Bid)} delivered at once from the web page ({context.Request.RemoteEndPoint?.Address})");
        _host.Delivery.Nudge();
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { delivered = true, bid = delivered.Bid }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    private async Task ResendAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<ResendForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (form?.Id is not { } id || !Packet.Mailcast.ObjectId.TryParse(id, out ulong objectId))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Say which bulletin, by its id." })).ConfigureAwait(false);
            return;
        }
        var now = _host.Time.GetUtcNow();
        TimeSpan? wait = null;
        lock (_gate)
        {
            if (!_host.Intake.Mail().IsWaiting(objectId) && _resentAt.TryGetValue(objectId, out var last) && now - last < ResendCooldown)
            {
                wait = ResendCooldown - (now - last);
            }
        }
        if (wait is TimeSpan left)
        {
            // Each one sent again is a session with the BBS; a script or a stuck key should not make dozens.
            await RespondAsync(context, 429, "application/json", JsonSerializer.Serialize(new { error = $"That one was sent again a moment ago. Try again in {Math.Ceiling(left.TotalSeconds)} s." })).ConfigureAwait(false);
            return;
        }
        (Packet.Mailcast.ResendOutcome Outcome, Packet.Mailcast.Bulletin? Bulletin) result;
        try
        {
            result = _host.Intake.Resend(objectId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            await RespondAsync(context, 500, "application/json", JsonSerializer.Serialize(new { error = "Cannot put it back in the outbox: " + Ascii.Clean(e.Message) })).ConfigureAwait(false);
            return;
        }
        switch (result.Outcome)
        {
            case Packet.Mailcast.ResendOutcome.Resent:
                lock (_gate)
                {
                    foreach (var old in _resentAt.Where(r => now - r.Value >= ResendCooldown).Select(r => r.Key).ToList())
                    {
                        _resentAt.Remove(old);
                    }
                    _resentAt[objectId] = now;
                }
                var b = result.Bulletin!;
                _log($"mail: {Ascii.Clean(b.Bid)} \"{Ascii.Clean(b.Title)}\" put back in the outbox from the web page ({context.Request.RemoteEndPoint?.Address}), to be offered to the BBS again");
                _host.Delivery.Nudge();
                await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { resent = true, bid = b.Bid }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
                break;
            case Packet.Mailcast.ResendOutcome.TooMany:
                await RespondAsync(context, 429, "application/json", JsonSerializer.Serialize(new { error = $"{Packet.Mailcast.ReceiverStore.MaxResentWaiting} bulletins sent again are already waiting for the BBS. Try again once it has answered for them." })).ConfigureAwait(false);
                break;
            case Packet.Mailcast.ResendOutcome.AlreadyWaiting:
                await RespondAsync(context, 409, "application/json", JsonSerializer.Serialize(new { error = "It is already waiting for the BBS." })).ConfigureAwait(false);
                break;
            default:
                await RespondAsync(context, 404, "application/json", JsonSerializer.Serialize(new { error = "No such bulletin in the archive." })).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// The "Test BBS login" form: the host, port, login, password and command to try. Any field
    /// left out, or an empty password, uses the one already saved, so the button can test a
    /// login that has not been saved yet, or the one already on the page.
    /// </summary>
    internal sealed record TestBbsForm(string? Type, string? Host, int? Port, string? Login, string? Password, string? Command);

    /// <summary>
    /// POST /api/bbs/test: tries the host, port, login, password and command given (or, for any
    /// left out, the ones already saved), the same way <see cref="BbsClient.TestLoginAsync"/>
    /// does, and says in plain words how far it got. Exchanges no mail, and never logs or returns
    /// the password. Rate-limited to one try every <see cref="BbsTestCooldown"/>, the same CSRF
    /// and sign-in checks as every other setting, because this opens a real connection out from
    /// the receiver on whatever is asked.
    /// </summary>
    private async Task TestBbsAsync(HttpListenerContext context)
    {
        var now = _host.Time.GetUtcNow();
        TimeSpan? wait = null;
        lock (_gate)
        {
            if (_lastBbsTestAt is { } last && now - last < BbsTestCooldown)
            {
                wait = BbsTestCooldown - (now - last);
            }
            else
            {
                _lastBbsTestAt = now;
            }
        }
        if (wait is { } left)
        {
            context.Response.Headers["Retry-After"] = RetryAfter(left);
            await RespondAsync(context, 429, "application/json",
                JsonSerializer.Serialize(new { error = $"Only one login test at a time. Try again in {Math.Ceiling(left.TotalSeconds)} s." })).ConfigureAwait(false);
            return;
        }

        var (answered, form) = await ReadJsonAsync<TestBbsForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (form is null)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "That was not a login to test." })).ConfigureAwait(false);
            return;
        }

        var current = _host.Config.Bbs;
        var kind = current.Type;
        if (form.Type is { Length: > 0 } type && !Enum.TryParse(type, ignoreCase: true, out kind))
        {
            await RespondAsync(context, 400, "application/json",
                JsonSerializer.Serialize(new { error = $"\"{Ascii.Clean(type)}\" is not a kind of BBS this receiver knows; use linBpq or fbb" })).ConfigureAwait(false);
            return;
        }
        string host = string.IsNullOrWhiteSpace(form.Host) ? current.Host : form.Host.Trim();
        int port = form.Port ?? current.Port;
        string login = string.IsNullOrWhiteSpace(form.Login) ? current.Login : form.Login.Trim();
        string password = string.IsNullOrEmpty(form.Password) ? current.Password : form.Password;
        string command = string.IsNullOrWhiteSpace(form.Command) ? current.Command : form.Command.Trim();
        // The same bounds as a saved config (ReceiverConfig.Validate): a host that is not empty
        // and not absurd, and a port that is an actual TCP port. Without this, a port such as
        // 70000 reaches TcpClient.ConnectAsync, which throws rather than refusing cleanly, and
        // the request never answers.
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255 || host.Any(char.IsControl))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Give a host to test, such as 127.0.0.1." })).ConfigureAwait(false);
            return;
        }
        if (port is < 1 or > 65535)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = $"\"{port}\" is not a TCP port; it must be from 1 to 65535." })).ConfigureAwait(false);
            return;
        }
        if (password.Length == 0)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Enter the password to test." })).ConfigureAwait(false);
            return;
        }

        var settings = new BbsSettings { Type = kind, Host = host, Port = port, Login = BbsLogin(kind, login), Password = password, Command = command };
        BbsLoginTestResult result;
        try
        {
            result = TestBbsLogin is { } hook
                ? await hook(settings).ConfigureAwait(false)
                : await new BbsClient(settings, _host.Time, _log).TestLoginAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Whatever this is, the page still gets an answer rather than a hung request; never
            // the password, and the type name only, not the exception's own message, which could
            // quote back something given to it.
            _log($"web: testing the BBS login for {Ascii.Clean(login)} from {RemoteAddress(context.Request)} failed unexpectedly ({e.GetType().Name})");
            _lastBbsTestOutcome = BbsLoginTestOutcome.UnexpectedReply;
            await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new
            {
                outcome = BbsLoginTestOutcome.UnexpectedReply,
                said = "Something went wrong testing the login.",
                detail = (string?)null,
                sent = (string?)null,
            }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
            return;
        }
        _lastBbsTestOutcome = result.Outcome;
        _log($"web: tested the BBS login for {Ascii.Clean(login)} from {RemoteAddress(context.Request)}: {Ascii.Clean(result.Said)}");
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new
        {
            outcome = result.Outcome,
            said = result.Said,
            detail = result.Detail,
            sent = result.Sent,
        }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /api/listen-now: issue #53's button. Opens a web SDR for a few minutes between its
    /// scheduled windows, or says why not (already used up for today, a slot's window is too
    /// close, or the audio is not a web SDR).
    /// </summary>
    private async Task ListenNowAsync(HttpListenerContext context)
    {
        var (answered, _) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        var result = _host.RequestListenNow();
        if (result.Ok)
        {
            _log($"web: \"Listen now\" opened from the page ({context.Request.RemoteEndPoint?.Address}), until {result.Until:HH:mm:ss} UTC");
            await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { ok = true, until = result.Until }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
        }
        else
        {
            await RespondAsync(context, 409, "application/json", JsonSerializer.Serialize(new { ok = false, error = result.Reason })).ConfigureAwait(false);
        }
    }

    /// <summary>The answer to a filter measurement or a typed one: the maths, or why not.</summary>
    private static object FilterView((RadioFilter? Filter, string? Problem) result) => result.Filter is { } f
        ? new { lowHz = f.LowHz, highHz = f.HighHz, widthHz = f.WidthHz, dialKHz = f.DialKHz, narrow = f.Narrow, problem = (string?)null }
        : new { lowHz = (double?)null, highHz = (double?)null, widthHz = (double?)null, dialKHz = (double?)null, narrow = (bool?)null, problem = result.Problem };

    /// <summary>
    /// POST /api/filter/measure: listens to <see cref="ReceiverHost.FilterMeasureDuration"/> of
    /// this radio's own audio (a sound card's, or pdn-soundmodem's) and measures its receive filter, for the settings page's
    /// "Measure my filter". A problem, not a filter, if it could not be used or measured.
    /// </summary>
    private async Task MeasureFilterAsync(HttpListenerContext context)
    {
        var (answered, _) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        var result = await _host.MeasureFilterAsync(CancellationToken.None).ConfigureAwait(false);
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(FilterView(result), ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    /// <summary>The typed-edges form: low and high, Hz.</summary>
    private sealed record FilterEdgesForm(double LowHz, double HighHz);

    private sealed record AddKeyForm(string? Station, string? PublicKey);

    private sealed record ForgetKeyForm(string? KeyId);

    /// <summary>POST /api/keys/add: trusts a callsign's public key (issue #146).</summary>
    private async Task AddKeyAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<AddKeyForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        var result = _host.Intake.AddKey(form?.Station, form?.PublicKey);
        if (result.Error is { } why)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = why })).ConfigureAwait(false);
            return;
        }
        _log($"keys: a key was added from the web page ({context.Request.RemoteEndPoint?.Address})");
        _host.Delivery.Nudge();
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { added = true, note = result.Note })).ConfigureAwait(false);
    }

    /// <summary>POST /api/keys/forget: forgets a learnt or added key (issue #146).</summary>
    private async Task ForgetKeyAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<ForgetKeyForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (form?.KeyId is not { } text || !Packet.Mailcast.KeyId.TryParse(text, out ulong keyId))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Say which key, by its id." })).ConfigureAwait(false);
            return;
        }
        var result = _host.Intake.ForgetKey(keyId);
        if (result.Error is { } why)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = why })).ConfigureAwait(false);
            return;
        }
        _log($"keys: key {Packet.Mailcast.KeyId.Format(keyId)} was forgotten from the web page ({context.Request.RemoteEndPoint?.Address})");
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { forgotten = true })).ConfigureAwait(false);
    }

    private sealed record ListenForm(string? Station, bool Listen);

    /// <summary>
    /// Issue #178: listens, or stops listening, at one station's slots, saved in the config
    /// file's <c>skipStations</c>, as the page's buttons ask.
    /// </summary>
    private async Task ListenToStationAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<ListenForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        string? station = form?.Station?.Trim().ToUpperInvariant();
        if (form is null || !Packet.Mailcast.StationAdvert.IsCallsign(station))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "Give the callsign of a station." })).ConfigureAwait(false);
            return;
        }
        var current = _host.Config;
        var skip = (current.SkipStations ?? []).Where(c => !string.Equals(c, station, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!form.Listen)
        {
            skip.Add(station!);
        }
        var next = current with { SkipStations = skip.Count == 0 ? null : skip };
        if (!await SaveFromPageAsync(context, next).ConfigureAwait(false))
        {
            return;
        }
        _log($"config: saved from the page: {(form.Listen ? "listening at" : "not listening at")} {station}'s slots");
        _host.Reconfigure(next);
        _host.RefreshFollowed();
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { station, listen = form.Listen })).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves <paramref name="next"/> to the config file, if there is one, as the page's other
    /// settings are saved. Answers the request itself and returns false when it cannot.
    /// </summary>
    private async Task<bool> SaveFromPageAsync(HttpListenerContext context, ReceiverConfig next)
    {
        if (_configFile is null)
        {
            return true;
        }
        try
        {
            switch (_configFile.TrySave(next, _log))
            {
                case ConfigFile.SaveOutcome.EditedByHand:
                    await RespondAsync(context, 409, "application/json", JsonSerializer.Serialize(new { error = ConfigFile.EditedByHand + "." })).ConfigureAwait(false);
                    return false;
                case ConfigFile.SaveOutcome.Unreadable:
                    await RespondAsync(context, 503, "application/json", JsonSerializer.Serialize(new { error = ConfigFile.Unreadable + "." })).ConfigureAwait(false);
                    return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await RespondAsync(context, 500, "application/json", JsonSerializer.Serialize(new { error = $"Cannot write {_configFile.Path}: {Ascii.Clean(e.Message)}" })).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private sealed record ListenForForm(string? Callsign, int? Minute, int? EveryMinutes, long? FrequencyHz, string? Sideband, string? Locator, bool? DaylightOnly);

    /// <summary>
    /// Issue #224: adds, or removes, a "Listen for a station" entry, saved in the config file's
    /// <c>listenFor</c>. Same sign-in and Host rules as every other change (see <c>AdmitAsync</c>).
    /// </summary>
    private async Task ListenForAsync(HttpListenerContext context, bool add)
    {
        var (answered, form) = await ReadJsonAsync<ListenForForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        string? callsign = form?.Callsign?.Trim().ToUpperInvariant();
        if (form is null || !Packet.Mailcast.StationAdvert.IsCallsign(callsign))
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = form is null ? "Could not read the request." : "Give the station's callsign." })).ConfigureAwait(false);
            return;
        }
        await _configLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ChangeListenForAsync(context, form, callsign!, add).ConfigureAwait(false);
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <summary>One read-modify-save of <c>listenFor</c>, under <see cref="_configLock"/>.</summary>
    private async Task ChangeListenForAsync(HttpListenerContext context, ListenForForm form, string callsign, bool add)
    {
        var current = _host.Config;
        var list = (current.ListenFor ?? []).ToList();
        string said;
        if (add)
        {
            var entry = new ListenForEntry
            {
                Callsign = callsign!,
                Minute = form.Minute ?? 0,
                EveryMinutes = form.EveryMinutes ?? 60,
                FrequencyHz = form.FrequencyHz ?? 0,
                Sideband = (form.Sideband ?? "usb").Trim().ToLowerInvariant(),
                Locator = string.IsNullOrWhiteSpace(form.Locator) ? null : form.Locator.Trim(),
            };
            // A locator means daylight at that place unless told otherwise, 120 minutes after sunrise to 30 before sunset, as head ends do.
            bool daylight = form.DaylightOnly ?? entry.Locator is not null;
            string? problem = daylight && entry.Locator is null ? "A daylight rule needs a locator: add one, or untick daylight only." : null;
            if (daylight && entry.Locator is not null)
            {
                entry = entry with { Daylight = new Packet.Mailcast.DaylightSettings { Locator = entry.Locator } };
            }
            problem ??= entry.Problem(_host.Config.Stations);
            if (problem is null && _host.Intake.HeardTimetable(entry.Callsign) is not null)
            {
                problem = $"{entry.Callsign}'s own signed directory has been heard here already, so its own timetable is used";
            }
            if (problem is not null)
            {
                await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = problem })).ConfigureAwait(false);
                return;
            }
            list.RemoveAll(entry.SamePlaceAs);
            list.Add(entry);
            _host.ListenForState.Restart(entry, _host.Time.GetUtcNow());
            said = $"listening for {entry.Callsign} at {SlotSchedule.From(entry.ToAdvert().Timetable).Describe()} on {entry.FrequencyHz} Hz {entry.Sideband.ToUpperInvariant()}";
        }
        else
        {
            if (list.RemoveAll(e => e.Callsign == callsign && (form.FrequencyHz is not { } hz || e.FrequencyHz == hz)) == 0)
            {
                await RespondAsync(context, 404, "application/json", JsonSerializer.Serialize(new { error = $"{callsign} is not in the list." })).ConfigureAwait(false);
                return;
            }
            said = $"no longer listening for {callsign}";
        }
        var next = current with { ListenFor = list.Count == 0 ? null : list };
        if (!await SaveFromPageAsync(context, next).ConfigureAwait(false))
        {
            return;
        }
        _log($"config: saved from the page: {said}");
        _host.Reconfigure(next);
        _host.RefreshFollowed();
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { callsign, listenFor = add })).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /api/filter/dial: the dial for a typed low and high edge, the same maths "Measure my
    /// filter" uses.
    /// </summary>
    private async Task FilterDialAsync(HttpListenerContext context)
    {
        var (answered, form) = await ReadJsonAsync<FilterEdgesForm>(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        if (form is null)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "That was not a low and high edge." })).ConfigureAwait(false);
            return;
        }
        var result = FilterScan.DialFor(form.LowHz, form.HighHz, _host.Config.CentreHz);
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(FilterView(result), ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    /// <summary>The sound cards this machine has, as the audio setting takes them: ALSA's devices, or on Windows its recording devices.</summary>
    private static string[] SoundCards()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSoundCards.Settings();
        }
        try
        {
            return [.. File.ReadAllLines("/proc/asound/cards")
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && char.IsAsciiDigit(l[0]) && l.Contains('['))
                .Select(l => l[(l.IndexOf('[') + 1)..l.IndexOf(']')].Trim())
                .Select(id => $"plughw:CARD={id},DEV=0")];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    private static void Redirect(HttpListenerContext context, string to, int status = 302)
    {
        context.Response.StatusCode = status;
        context.Response.RedirectLocation = to;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Close();
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static readonly Lazy<string> Page = new(() => Embedded("index.html"));

    /// <summary>The coastlines for the globe of where the signal bounced, fetched only when that section is opened.</summary>
    private static readonly Lazy<string> Globe = new(() => Embedded("globe.json"));

    private static readonly Lazy<string> SignInTemplate = new(() => Embedded("signin.html"));

    private static string Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mailcast.Receiver.Web." + name)
            ?? throw new InvalidOperationException($"The page {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _host.PipelineCreated -= Attach;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        if (_accept is not null)
        {
            await _accept.ConfigureAwait(false);
        }
        WaterfallWebServer? waterfall;
        lock (_gate)
        {
            waterfall = _waterfall;
            _waterfall = null;
        }
        if (waterfall is not null)
        {
            await waterfall.DisposeAsync().ConfigureAwait(false);
        }
        _stop.Dispose();
    }
}
