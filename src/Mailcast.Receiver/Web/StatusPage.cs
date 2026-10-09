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
/// <para>It listens on this machine only unless the config's <c>web.lan</c> is set, which needs
/// <c>web.password</c>, because its settings include the BBS password. With a password set, a
/// browser signs in on a page of its own and is given a session cookie (see
/// <see cref="SessionStore"/>); a script can give the password with HTTP Basic instead.</para>
/// </remarks>
public sealed class StatusPage : IAsyncDisposable
{
    private const string WaterfallBase = "/waterfall/";

    /// <summary>The largest settings form, or any other request body, accepted.</summary>
    public const int MaxFormBytes = 16 * 1024;

    /// <summary>The most mail entries one page of <c>/api/mail</c> gives.</summary>
    public const int MaxMailPage = 200;

    private const string MailBase = "/api/mail/";

    /// <summary>How soon one bulletin can be sent to the BBS again after the last time.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(2);

    /// <summary>How soon the BBS login can be tested again after the last time.</summary>
    public static readonly TimeSpan BbsTestCooldown = TimeSpan.FromSeconds(10);

    private readonly Dictionary<ulong, DateTimeOffset> _resentAt = [];
    private DateTimeOffset? _lastBbsTestAt;

    private readonly SessionStore _sessions;
    private readonly Dictionary<HttpListenerContext, string?> _sockets = [];
    private readonly SignInThrottle _throttle;

    private readonly ReceiverHost _host;
    private readonly string? _configPath;
    private readonly Action<string> _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private WaterfallWebServer? _waterfall;
    private Task? _accept;

    /// <summary>Creates the page for <paramref name="host"/>; settings are saved to <paramref name="configPath"/>.</summary>
    public StatusPage(ReceiverHost host, string? configPath, Action<string> log)
    {
        _host = host;
        _configPath = configPath;
        _log = log;
        var web = host.Config.Web;
        if (web.Lan)
        {
            _listener.Prefixes.Add($"http://+:{web.Port}/");
        }
        else
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{web.Port}/");
            _listener.Prefixes.Add($"http://localhost:{web.Port}/");
        }
        Url = $"http://127.0.0.1:{web.Port}/";
        CookieName = $"pdn-mailcast-{web.Port}";
        _sessions = new SessionStore(Path.Combine(host.Config.StateDirectory, SessionStore.FileName), () => _host.Config.Web.Password, host.Time, log);
        _throttle = new SignInThrottle(host.Time, log);
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

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> if the port is taken.</summary>
    public void Start()
    {
        _listener.Start();
        _accept = AcceptAsync();
        _log($"web: status page at {Url}{(_host.Config.Web.Lan ? " and on the local network" : "")}");
    }

    /// <summary>Gives a new pipeline its waterfall, before its audio starts.</summary>
    internal void Attach(AudioPipeline pipeline)
    {
        var waterfall = WaterfallWebServer.Routed(pipeline.Channel, new WaterfallOptions
        {
            DialFrequencyHz = pipeline.DialHz,
            Sideband = "usb",
            LinesPerSecond = 10,
            InputLevelMeter = true,
            Title = "pdn-mailcast receiver",
            DeclaredBands = [new DeclaredBand(0, "mailcast", OnAir.CentreAudioHz, 2 * OnAir.HalfWidthHz)],
            Log = line => _log("web: " + Ascii.Clean(line)),
        });
        waterfall.Start();
        pipeline.SourceBlock += waterfall.MeterInputClipping;
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
            }
            string? session = null;
            if (!await AdmitAsync(context, path, id => session = id).ConfigureAwait(false))
            {
                return; // answered: the sign-in page, or why not
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
                case ("/api/status", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(Status(), ReceiverConfig.JsonLine)).ConfigureAwait(false);
                    break;
                case ("/api/settings", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(SettingsView(_host.Config), ReceiverConfig.JsonLine)).ConfigureAwait(false);
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
                case ("/api/bbs/test", "POST"):
                    await TestBbsAsync(context).ConfigureAwait(false);
                    break;
                case ("/api/listen-now", "POST"):
                    await ListenNowAsync(context).ConfigureAwait(false);
                    break;
                case (_, "GET") when path.StartsWith(MailBase, StringComparison.Ordinal):
                    await ServeBulletinAsync(context, path[MailBase.Length..]).ConfigureAwait(false);
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
        bool changes = request.HttpMethod != "GET" || request.IsWebSocketRequest;
        string? origin = request.Headers["Origin"];
        if (changes && origin is not null && !SameOrigin(origin, host))
        {
            return (403, "Refused: this request came from another site.");
        }
        return null;
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
            if (request.HttpMethod == "GET" && !request.IsWebSocketRequest && !path.StartsWith("/api/", StringComparison.Ordinal))
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

    private static string TooMany(SignInThrottle.Verdict verdict, TimeSpan wait) =>
        verdict == SignInThrottle.Verdict.Busy ? Busy(wait) : LockedOut(wait);

    private static string Busy(TimeSpan wait)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return $"There have been too many wrong passwords lately, so sign-in is slowed down. Try again in {seconds} second{(seconds == 1 ? "" : "s")}.";
    }

    private const string WrongPassword = "That password is not right.";

    private static string LockedOut(TimeSpan wait)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Too many wrong passwords from this address. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
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

    /// <summary>What the page shows, in one object.</summary>
    internal object Status()
    {
        var config = _host.Config;
        var slot = _host.Slots.Last;
        var (directory, progress) = _host.Intake.Progress();
        var mail = _host.Intake.Mail();
        double? liveTone = _host.Pipeline?.Tone.LiveFrequencyHz;
        return new
        {
            version = ReceiverHost.Version,
            // Whether the apt repository has a newer receiver; null when nothing checks (tests).
            update = Updates?.View(),
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
                dialKHz = config.DialKHz,
                centreKHz = config.CentreHz / 1000,
                webSdr = WebSdrView(config.Audio, _host.WebSdrAbout),
            },
            bbs = new
            {
                target = ReceiverHost.DescribeBbs(config.Bbs),
                lastFailure = _host.Delivery.LastFailure,
                nextAttempt = _host.Delivery.NextAttempt,
                waiting = _host.Intake.Mail().Waiting, // from memory: this page is asked every few seconds
                // Whether there is a BBS to send through, for the daily report's opt-in.
                reachable = BbsReachable(_host.Ledger.Recent, _host.Feedback.Last, _host.Delivery.LastFailure),
            },
            retune = _host.Retuner is not { } retuner ? null : new
            {
                stage = retuner.Stage,
                state = retuner.State,
                rigctld = retuner.Endpoint.ToString(),
                linBpq = config.Bpq?.ToString(),
                nextSlot = retuner.NextSlot,
                problem = retuner.LastProblem?.Text,
                problemAt = retuner.LastProblem?.At,
            },
            markers = new
            {
                centreHz = OnAir.CentreAudioHz,
                lowHz = OnAir.CentreAudioHz - OnAir.HalfWidthHz,
                highHz = OnAir.CentreAudioHz + OnAir.HalfWidthHz,
                toneHz = liveTone ?? (slot?.Tone is { } t ? t.FrequencyHz : null),
                toneLive = liveTone is not null,
            },
            schedule = Schedule(config, _host.Schedule, _host.ScheduleFromDirectory, _host.Time.GetUtcNow()),
            // Issue #53: the "Listen now" button, for a web SDR only.
            listenNow = ListenNowView(_host, config, _host.Time.GetUtcNow()),
            level = new { lowDbFs = InputLevelMeter.TargetPeakLowDbFs, highDbFs = InputLevelMeter.TargetPeakHighDbFs, advice = LevelAdvice(config.Audio) },
            burst = BurstView(_host.Pipeline?.Burst.Shown),
            // The radio path from GB7RDG, measured after each slot from the bursts decoded.
            channel = ChannelTile.View(_host.ChannelWatch, slot, _host.Time.GetUtcNow()),
            slot = slot is null ? null : new
            {
                started = slot.Started,
                scheduled = slot.Scheduled,
                framesHeard = slot.FramesHeard,
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
            iono = IonoView(_host.Intake.Ionosphere, _host.Time.GetUtcNow()),
            pskReporter = PskView(_host.Intake.PskReporter, _host.Time.GetUtcNow()),
            // The daily report, when the config's "feedback" turns it on.
            feedback = _host.Feedback.View(),
            directory = directory is null ? null : new { date = directory.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), entries = directory.Entries.Count },
            bulletins = progress.Select(p =>
            {
                var delivered = _host.Ledger.Latest(p.Entry.Bid);
                // Issue #47: the rebuilt time is only known once it is held, waiting or archived.
                var held = p.Complete ? mail.ById(p.Entry.ObjectId) : null;
                var final = FinalAnswer(held, delivered);
                return new
                {
                    bid = p.Entry.Bid,
                    title = p.Entry.Title,
                    complete = p.Complete,
                    received = p.Received,
                    needed = p.Needed,
                    delivery = delivered is null ? null : DeliveryService.Describe(delivered.Verdict),
                    deliveryShort = delivered is null ? null : DeliveryService.DescribeShort(delivered.Verdict),
                    completedAt = held?.Completed,
                    deliveredAt = final?.At,
                    deliveredVerdict = final?.Verdict,
                    deliveredDetail = final?.Detail,
                };
            }),
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
    /// Whether the receiver has a BBS to send through, as far as it knows: the BBS has answered
    /// for a bulletin or a daily report, or no session with it has failed yet. False for a
    /// receiver whose BBS has never been reached, such as one set up to listen only, with
    /// nothing at the BBS's address; the page offers the daily report only when this is true.
    /// </summary>
    internal static bool BbsReachable(IReadOnlyList<DeliveryRecord> recent, SentReport? report, string? lastFailure) =>
        lastFailure is null
        || recent.Any(r => r.Verdict is DeliveryVerdict.Accepted or DeliveryVerdict.AlreadyHad or DeliveryVerdict.Deferred or DeliveryVerdict.Refused or DeliveryVerdict.Unconfirmed)
        || report?.Answer is FeedbackAnswer.Accepted or FeedbackAnswer.Refused or FeedbackAnswer.Deferred;

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
    internal static object? BurstView(HeardBurst? burst) => burst is null ? null : new
    {
        waveform = burst.Waveform,
        bps = Waveform.Bps(burst.Waveform),
        words = Waveform.Words(burst.Waveform),
        live = burst.Live,
        frames = burst.Frames,
        started = burst.Started,
        ended = burst.Ended,
    };

    /// <summary>
    /// How the page judges the input level: <c>target</c> for a sound card, whose level you set,
    /// so it says when peaks are outside -18 to -9 dBFS; <c>clippingOnly</c> for a web SDR or a
    /// recording, whose level is not yours to set and which the modem copes with unless it clips.
    /// </summary>
    internal static string LevelAdvice(string audio) => AudioSource.Parse(audio).Kind == AudioSourceKind.Alsa ? "target" : "clippingOnly";

    /// <summary>The audio source's kind, as the page names it: <c>webSdr</c>, <c>soundCard</c> or <c>recording</c>.</summary>
    internal static string AudioKind(string audio) => AudioSource.Parse(audio).Kind switch
    {
        AudioSourceKind.UberSdr => "webSdr",
        AudioSourceKind.Wav => "recording",
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
        var nextOpens = ListeningWindow.Next(now, host.Schedule).Opens;
        var until = host.ListenNow.ActiveUntil(now, nextOpens);
        bool alreadyListening = host.Audio.Phase == AudioPhase.Listening && until is null;
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
        AudioPhase.Failed => "failed",
        AudioPhase.Ended => "ended",
        _ => "starting",
    };

    /// <summary>When the slots are, for the page, as the config file gives them.</summary>
    internal static object Schedule(ReceiverConfig config, DateTimeOffset now) => Schedule(config, config.Schedule, false, now);

    /// <summary>
    /// When the slots are, for the page: in words, today's slots, the next slot and the one before
    /// it if that may still be on, and for a web SDR which slots it listens to and the next of
    /// those. <paramref name="fromDirectory"/> says the schedule is GB7RDG's own, from its directory.
    /// </summary>
    internal static object Schedule(ReceiverConfig config, SlotSchedule schedule, bool fromDirectory, DateTimeOffset now)
    {
        var next = schedule.NextActiveStart(now);
        var before = schedule.LatestActiveStart(now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var todays = schedule.ActiveOn(today);
        bool webSdr = AudioSource.Parse(config.Audio).Kind == AudioSourceKind.UberSdr;
        var (opens, closes, listenSlot) = ListeningWindow.Next(now, schedule);
        var listened = ListeningWindow.WebSdrSlotsOn(schedule, today);
        static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new
        {
            words = schedule.Describe(),
            from = fromDirectory ? "directory" : "config",
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
    internal static object SettingsView(ReceiverConfig config) => new
    {
        audio = config.Audio,
        dialKHz = config.DialKHz,
        sources = config.AcceptedSources,
        sourcesSet = config.Sources is not null,
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
    };

    /// <summary>
    /// The form's answer. An empty password keeps the one already set; so does an empty
    /// <paramref name="PagePassword"/>, the page's own, which needs <paramref name="CurrentPagePassword"/>
    /// to change once there is one. A null <paramref name="Sources"/> keeps the callsigns accepted as they are,
    /// and a null <paramref name="Feedback"/> the daily report as it is.
    /// </summary>
    internal sealed record SettingsForm(string Audio, string Type, string Host, int Port, string Login, string? Password, string Command,
        string? PagePassword = null, string? CurrentPagePassword = null, IReadOnlyList<string?>? Sources = null, FeedbackForm? Feedback = null);

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
            Bbs = current.Bbs with
            {
                Type = kind,
                Host = form.Host.Trim(),
                Port = form.Port,
                Login = form.Login.Trim().ToUpperInvariant(),
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
    private static async Task<(bool Answered, byte[]? Body)> ReadBodyAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > MaxFormBytes)
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
            if (body.Length > MaxFormBytes)
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

        if (_configPath is not null)
        {
            try
            {
                next.Save(_configPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await RespondAsync(context, 500, "application/json", JsonSerializer.Serialize(new { error = $"Cannot write {_configPath}: {Ascii.Clean(e.Message)}" })).ConfigureAwait(false);
                return;
            }
        }
        _host.Reconfigure(next);
        bool wasOn = current.Feedback?.Enabled ?? false, isOn = next.Feedback?.Enabled ?? false;
        if (wasOn != isOn || (isOn && next.Feedback!.From != current.Feedback!.From))
        {
            _log(isOn
                ? $"web: the daily report (a public bulletin to {FeedbackSettings.To}) was turned on from the page, sent from {next.Feedback!.From}"
                : $"web: the daily report (a public bulletin to {FeedbackSettings.To}) was turned off from the page");
        }
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
    /// One page of the bulletins held, newest first: <c>offset</c> and <c>limit</c> (at most
    /// <see cref="MaxMailPage"/>) from the query string.
    /// </summary>
    internal object MailList(System.Collections.Specialized.NameValueCollection query)
    {
        int offset = Math.Max(0, int.TryParse(query["offset"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int o) ? o : 0);
        int limit = int.TryParse(query["limit"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int l) ? Math.Clamp(l, 1, MaxMailPage) : 50;
        var mail = _host.Intake.Mail(); // in memory, newest first, already counted: no files, no lock, no sorting
        return new
        {
            total = mail.Count,
            waiting = mail.Waiting,
            archived = mail.Archived,
            offset,
            limit,
            archive = new { days = _host.Config.Archive.Days, maxMegabytes = _host.Config.Archive.MaxMegabytes },
            items = mail.Page(offset, limit).Select(MailView).ToList(),
        };
    }

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

    /// <summary>One bulletin for the list: what it is, where it is, and what the BBS has said.</summary>
    private object MailView(Packet.Mailcast.MailEntry m)
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

        var settings = new BbsSettings { Type = kind, Host = host, Port = port, Login = login.ToUpperInvariant(), Password = password, Command = command };
        BbsLoginTestResult result;
        try
        {
            result = TestBbsLogin is { } hook
                ? await hook(settings).ConfigureAwait(false)
                : await new BbsClient(settings, _host.Time, _ => { }).TestLoginAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Whatever this is, the page still gets an answer rather than a hung request; never
            // the password, and the type name only, not the exception's own message, which could
            // quote back something given to it.
            _log($"web: testing the BBS login for {Ascii.Clean(login)} from {RemoteAddress(context.Request)} failed unexpectedly ({e.GetType().Name})");
            await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new
            {
                outcome = BbsLoginTestOutcome.UnexpectedReply,
                said = "Something went wrong testing the login.",
                detail = (string?)null,
            }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
            return;
        }
        _log($"web: tested the BBS login for {Ascii.Clean(login)} from {RemoteAddress(context.Request)}: {Ascii.Clean(result.Said)}");
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new
        {
            outcome = result.Outcome,
            said = result.Said,
            detail = result.Detail,
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

    /// <summary>The sound cards ALSA knows, as device names the audio setting takes.</summary>
    private static string[] SoundCards()
    {
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
