using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Web;

/// <summary>
/// The receiver's local web page: status, settings, the input level and a live spectrogram.
/// </summary>
/// <remarks>
/// <para>The spectrogram and the level meter come from pdn-soundmodem's
/// <see cref="WaterfallWebServer"/>, one per audio pipeline, served under <c>/waterfall/</c> on
/// this page's port. This page reads its WebSocket for the spectrum lines and the level, and draws
/// them with the broadcast's markers on top; pdn-soundmodem's own full page is there too, at
/// <c>/waterfall/</c>.</para>
/// <para>It listens on this machine only unless the config's <c>web.lan</c> is set, because it has
/// no login and its settings include the BBS password.</para>
/// </remarks>
public sealed class StatusPage : IAsyncDisposable
{
    private const string WaterfallBase = "/waterfall/";

    /// <summary>The largest settings form accepted.</summary>
    public const int MaxFormBytes = 16 * 1024;

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
        host.PipelineCreated += Attach;
    }

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
    private void Attach(AudioPipeline pipeline)
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
                if (refused.Status == 401)
                {
                    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"pdn-mailcast receiver\", charset=\"UTF-8\"";
                }
                await RespondAsync(context, refused.Status, "text/plain", refused.Why + "\n").ConfigureAwait(false);
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
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
                else if (waterfall is null || !await waterfall.TryServeAsync(context, WaterfallBase).ConfigureAwait(false))
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
    /// <item>With a password set (always, on the network), the browser's Basic login must give it.</item>
    /// <item>A WebSocket or a POST that says it comes from another site is refused: a page the
    /// operator has open elsewhere must not be able to change the settings (CSRF).</item>
    /// </list>
    /// </summary>
    internal (int Status, string Why)? Refusal(HttpListenerRequest request)
    {
        var web = _host.Config.Web;
        string host = request.Headers["Host"] ?? "";
        if (!web.Lan && !IsLoopbackHost(host))
        {
            return (421, "This page only answers to localhost.");
        }
        if (web.Password.Length > 0 && !PasswordGiven(request.Headers["Authorization"], web.Password))
        {
            return (401, "This page needs its password (any user name).");
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

    internal static bool PasswordGiven(string? authorization, string password)
    {
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }
        int colon = decoded.IndexOf(':', StringComparison.Ordinal);
        byte[] given = Encoding.UTF8.GetBytes(colon < 0 ? "" : decoded[(colon + 1)..]);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(password));
    }

    /// <summary>What the page shows, in one object.</summary>
    internal object Status()
    {
        var config = _host.Config;
        var slot = _host.Slots.Last;
        var (directory, progress) = _host.Intake.Progress();
        double? liveTone = _host.Pipeline?.Tone.LiveFrequencyHz;
        return new
        {
            version = ReceiverHost.Version,
            audio = new
            {
                source = config.Audio,
                state = _host.AudioState,
                dialKHz = config.DialKHz,
                centreKHz = config.CentreHz / 1000,
            },
            bbs = new
            {
                target = ReceiverHost.DescribeBbs(config.Bbs),
                lastFailure = _host.Delivery.LastFailure,
                nextAttempt = _host.Delivery.NextAttempt,
                waiting = _host.Intake.Pending().Count,
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
            level = new { lowDbFs = InputLevelMeter.TargetPeakLowDbFs, highDbFs = InputLevelMeter.TargetPeakHighDbFs },
            slot = slot is null ? null : new
            {
                started = slot.Started,
                scheduled = slot.Scheduled,
                framesHeard = slot.FramesHeard,
                lastFrame = slot.LastFrame,
                tone = slot.Tone is not { } tone ? null : new
                {
                    frequencyHz = Math.Round(tone.FrequencyHz, 1),
                    offsetHz = Math.Round(tone.OffsetHz, 1),
                    snrDb = Math.Round(tone.SnrDb, 1),
                    seconds = Math.Round(tone.Duration.TotalSeconds),
                },
            },
            framesHeard = _host.Intake.FramesHeard,
            directory = directory is null ? null : new { date = directory.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), entries = directory.Entries.Count },
            bulletins = progress.Select(p =>
            {
                var delivered = _host.Ledger.Latest(p.Entry.Bid);
                return new
                {
                    bid = p.Entry.Bid,
                    title = p.Entry.Title,
                    complete = p.Complete,
                    received = p.Received,
                    needed = p.Needed,
                    delivery = delivered is null ? null : DeliveryService.Describe(delivered.Verdict),
                };
            }),
            deliveries = _host.Ledger.Recent.Take(40).Select(r => new
            {
                time = r.Time,
                bid = r.Bid,
                title = r.Title,
                verdict = r.Verdict,
                said = DeliveryService.Describe(r.Verdict),
                detail = r.Detail,
            }),
        };
    }

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
        var (opens, closes, listenSlot) = ListeningWindow.Next(now, schedule, config.WebSdrSlotsPerDay);
        var listened = ListeningWindow.WebSdrSlotsOn(schedule, config.WebSdrSlotsPerDay, today);
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
        bbs = new
        {
            type = config.Bbs.Type,
            host = config.Bbs.Host,
            port = config.Bbs.Port,
            login = config.Bbs.Login,
            passwordSet = config.Bbs.Password.Length > 0,
            command = config.Bbs.Command,
        },
        soundCards = SoundCards(),
    };

    /// <summary>The form's answer. An empty password keeps the one already set.</summary>
    internal sealed record SettingsForm(string Audio, string Type, string Host, int Port, string Login, string? Password, string Command);

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
        };
        next.Validate();
        return next;
    }

    private async Task SaveSettingsAsync(HttpListenerContext context)
    {
        if (!(context.Request.ContentType ?? "").Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            // A form a page elsewhere can post without asking (text/plain, a form encoding) is not one.
            await RespondAsync(context, 415, "application/json", JsonSerializer.Serialize(new { error = "Settings are sent as application/json." })).ConfigureAwait(false);
            return;
        }
        if (context.Request.ContentLength64 > MaxFormBytes)
        {
            await RespondAsync(context, 413, "application/json", JsonSerializer.Serialize(new { error = "That form is too large." })).ConfigureAwait(false);
            return;
        }

        SettingsForm? form = null;
        var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await context.Request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > MaxFormBytes)
            {
                await RespondAsync(context, 413, "application/json", JsonSerializer.Serialize(new { error = "That form is too large." })).ConfigureAwait(false);
                return;
            }
        }
        try
        {
            form = JsonSerializer.Deserialize<SettingsForm>(body.ToArray(), ReceiverConfig.Json);
        }
        catch (JsonException)
        {
        }
        if (form is null)
        {
            await RespondAsync(context, 400, "application/json", JsonSerializer.Serialize(new { error = "That was not a settings form." })).ConfigureAwait(false);
            return;
        }

        ReceiverConfig next;
        try
        {
            next = Apply(_host.Config, form);
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
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { saved = true })).ConfigureAwait(false);
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

    private static void Redirect(HttpListenerContext context, string to)
    {
        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = to;
        context.Response.Close();
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static readonly Lazy<string> Page = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mailcast.Receiver.Web.index.html")
            ?? throw new InvalidOperationException("The page is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

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
