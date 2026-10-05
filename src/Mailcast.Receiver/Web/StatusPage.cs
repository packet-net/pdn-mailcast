using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
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
/// <para>It listens on this machine only unless the config's <c>web.lan</c> is set, because it has
/// no login and its settings include the BBS password.</para>
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

    private readonly Dictionary<ulong, DateTimeOffset> _resentAt = [];

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
                case ("/api/mail", "GET"):
                    await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(MailList(context.Request.QueryString), ReceiverConfig.JsonLine)).ConfigureAwait(false);
                    break;
                case ("/api/mail/resend", "POST"):
                    await ResendAsync(context).ConfigureAwait(false);
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
                waiting = _host.Intake.Mail().Waiting, // from memory: this page is asked every few seconds
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
        if (!(context.Request.ContentType ?? "").Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(context, 415, "application/json", JsonSerializer.Serialize(new { error = "Send this as application/json." })).ConfigureAwait(false);
            return (true, null);
        }
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
        try
        {
            return (false, JsonSerializer.Deserialize<T>(body.ToArray(), ReceiverConfig.Json));
        }
        catch (JsonException)
        {
            return (false, null);
        }
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

    /// <summary>One bulletin for the list: what it is, where it is, and what the BBS has said.</summary>
    private object MailView(Packet.Mailcast.MailEntry m)
    {
        string status;
        DeliveryRecord? last = null;
        if (m.Waiting)
        {
            // Only a non-final answer can be about this stay in the outbox; a final one is from
            // before the bulletin was sent again.
            last = _host.Ledger.Latest(m.Bid) is { Verdict: DeliveryVerdict.Deferred or DeliveryVerdict.Unconfirmed } r ? r : null;
            status = last is not null ? "waiting: " + DeliveryService.Describe(last.Verdict)
                : _host.Delivery.LastFailure is { } failure ? "waiting: " + failure
                : "waiting for the BBS";
        }
        else
        {
            status = m.Verdict switch
            {
                Packet.Mailcast.BbsVerdict.Accepted => "accepted by the BBS",
                Packet.Mailcast.BbsVerdict.AlreadyHad => "the BBS already had it",
                _ => "refused by the BBS",
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
            verdict = m.Verdict,
            detail = m.Detail ?? last?.Detail,
            lastAttempt = last?.Time,
            nextAttempt = m.Waiting ? _host.Delivery.NextAttempt : null,
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
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
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
