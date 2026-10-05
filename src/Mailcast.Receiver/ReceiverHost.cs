using Packet.Mailcast;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Retune;

namespace Mailcast.Receiver;

/// <summary>
/// The whole receiver: the audio pipeline, the store it feeds, the slot and tone record, and the
/// delivery into the BBS.
/// </summary>
public sealed class ReceiverHost : IAsyncDisposable
{
    /// <summary>How long to wait before reopening an audio source that failed or was lost.</summary>
    public static readonly TimeSpan AudioRetry = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private ReceiverConfig _config;
    private AudioPipeline? _pipeline;
    private CancellationTokenSource? _audioRestart;

    /// <summary>Creates the receiver; nothing runs until <see cref="RunAsync"/> or <see cref="DecodeOnceAsync"/>.</summary>
    public ReceiverHost(ReceiverConfig config, TimeProvider time, Action<string> log)
    {
        _config = config;
        _time = time;
        _log = log;
        Intake = new Intake(config.StateDirectory, log, new ReceiverStoreOptions
        {
            Time = time,
            ArchiveRetention = TimeSpan.FromDays(config.Archive.Days),
            ArchiveMaxBytes = config.Archive.MaxMegabytes * 1024L * 1024,
        });
        Ledger = new DeliveryLedger(config.StateDirectory);
        Slots = new SlotTracker(time, log, AudioSource.Parse(config.Audio).Kind == AudioSourceKind.Wav ? null : Schedule);
        Bbs = new BbsClient(config.Bbs, time, log) { Version = Version };
        Delivery = new DeliveryService(Intake, new SwitchableSession(this), Ledger, time, log);
        Intake.FrameHeard += Slots.OnFrame;
        Intake.ScheduleHeard += OnScheduleHeard;
        if (config.Rig is not null)
        {
            Retuner = new Retuner(config, () => Schedule, () => AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.Alsa, time, log);
        }
    }

    /// <summary>
    /// What moves a shared radio to the bulletin frequency for each slot and holds LinBPQ off the
    /// air meanwhile, when the config has <c>rig</c>; null otherwise.
    /// </summary>
    public Retuner? Retuner { get; }

    /// <summary>
    /// GB7RDG's slots: as its newest directory gives them once one has been heard, and until then
    /// as the config file does.
    /// </summary>
    public SlotSchedule Schedule => Intake.HeardSchedule is { } heard ? SlotSchedule.From(heard) : Config.Schedule;

    /// <summary>Whether <see cref="Schedule"/> comes from GB7RDG's directory rather than the config file.</summary>
    public bool ScheduleFromDirectory => Intake.HeardSchedule is not null;

    private void OnScheduleHeard(Packet.Mailcast.SlotTimetable heard)
    {
        var schedule = SlotSchedule.From(heard);
        _log($"slots: GB7RDG's directory gives its slots as {schedule.Describe()}; using that instead of the config file's");
        if (Slots.Schedule is not null)
        {
            Slots.Schedule = schedule;
        }
    }

    /// <summary>This program's version, for the SID and the log.</summary>
    public static string Version { get; } =
        typeof(ReceiverHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>The clock the receiver runs on.</summary>
    internal TimeProvider Time => _time;

    /// <summary>The configuration in force.</summary>
    public ReceiverConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>The store of pieces and rebuilt bulletins.</summary>
    public Intake Intake { get; }

    /// <summary>What the BBS has said about each bulletin.</summary>
    public DeliveryLedger Ledger { get; }

    /// <summary>The slots heard, with their tones.</summary>
    public SlotTracker Slots { get; }

    /// <summary>The delivery loop.</summary>
    public DeliveryService Delivery { get; }

    /// <summary>The client for the BBS in the current configuration.</summary>
    public BbsClient Bbs { get; private set; }

    /// <summary>The pipeline running now, if any.</summary>
    public AudioPipeline? Pipeline
    {
        get
        {
            lock (_gate)
            {
                return _pipeline;
            }
        }
    }

    /// <summary>A sentence on what the audio is doing, for the page and the log.</summary>
    public string AudioState { get; private set; } = "starting";

    /// <summary>
    /// Raised on the audio supervisor for each new pipeline before its audio starts, so the page
    /// can attach its waterfall: anything listening to the channel has to be added before then.
    /// </summary>
    public event Action<AudioPipeline>? PipelineCreated;

    /// <summary>Raised on the audio supervisor whenever a pipeline has started.</summary>
    public event Action<AudioPipeline>? PipelineStarted;

    /// <summary>
    /// Runs the service until <paramref name="cancellation"/> is cancelled. Each loop catches and
    /// logs what goes wrong inside it; if one dies anyway, this throws, so the process exits
    /// non-zero and systemd restarts it rather than leaving half a receiver running.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        _log($"pdn-mailcast receiver {Version}: listening on {Config.Audio} (USB dial {OnAir.Mhz(Config.DialHz)} MHz), delivering to {DescribeBbs(Config.Bbs)}");
        _log(ScheduleFromDirectory
            ? $"slots: GB7RDG's slots are {Schedule.Describe()}, as its directory gives them (used instead of the config file's)"
            : $"slots: GB7RDG's slots are {Schedule.Describe()}");
        if (Config.SlotUtcWithoutEveryMinutes)
        {
            _log($"config: \"slotUtc\" without \"everyMinutes\" is from before hourly slots; read as every 60 minutes from {Config.SlotUtc} UTC, the same hourly slots, so nothing needs changing");
        }
        int pending = Intake.Pending().Count;
        if (pending > 0)
        {
            _log($"store: {pending} rebuilt bulletin{(pending == 1 ? "" : "s")} waiting for the BBS from before");
        }

        if (Retuner is null)
        {
            Retuner.SayIfLeftOver(Config.StateDirectory, _log);
        }

        using var stopOthers = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task delivery = Delivery.RunAsync(stopOthers.Token);
        Task audio = SuperviseAudioAsync(stopOthers.Token);
        // The retuner catches and logs its own problems and ends only when the others do,
        // putting the rig back and LinBPQ on the air on its way out; if it ends any other way,
        // that is a failure like the others', and systemd's restart picks up where it left off.
        Task retune = Retuner?.RunAsync(stopOthers.Token) ?? DelayAsync(Timeout.InfiniteTimeSpan, stopOthers.Token);
        Task first = await Task.WhenAny(delivery, audio, retune).ConfigureAwait(false);
        await stopOthers.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(delivery, audio, retune).ConfigureAwait(false);
        }
        catch (Exception) when (first.IsFaulted || first == retune)
        {
        }
        if (first == retune && !cancellation.IsCancellationRequested && !first.IsFaulted)
        {
            throw new InvalidOperationException("the retune loop stopped on its own");
        }
        if (first.IsFaulted)
        {
            string which = first == delivery ? "delivery" : first == audio ? "audio" : "retune";
            throw new InvalidOperationException($"the {which} loop failed: {Ascii.Clean(first.Exception!.GetBaseException().Message)}", first.Exception);
        }
    }

    /// <summary>
    /// Decodes one recording and delivers what it completes, then returns: the command line's
    /// --decode. Returns null when everything rebuilt was answered for, or the reason it was not.
    /// </summary>
    public async Task<string?> DecodeOnceAsync(string wavPath, CancellationToken cancellation)
    {
        var pipeline = CreatePipeline(new AudioSource(AudioSourceKind.Wav, wavPath), Config);
        Slots.Schedule = null;
        Attach(pipeline);
        await pipeline.StartAsync(cancellation).ConfigureAwait(false);
        await pipeline.Finished.WaitAsync(cancellation).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        await Intake.DrainAsync(cancellation).ConfigureAwait(false);
        _log($"decode: {Intake.FramesHeard} frames heard, {Intake.Pending().Count} bulletins to deliver");
        return await Delivery.DeliverPendingAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a new configuration in force: the BBS settings at once, and the audio pipeline is
    /// restarted if its source changed.
    /// </summary>
    public void Reconfigure(ReceiverConfig config)
    {
        CancellationTokenSource? restart = null;
        lock (_gate)
        {
            bool audioChanged = !string.Equals(_config.Audio, config.Audio, StringComparison.Ordinal)
                || !string.Equals(_config.SlotUtc, config.SlotUtc, StringComparison.Ordinal)
                || _config.EveryMinutes != config.EveryMinutes
                || _config.WebSdrSlotsPerDay != config.WebSdrSlotsPerDay
                || _config.Daylight != config.Daylight
                || _config.DialKHz != config.DialKHz;
            _config = config;
            Bbs = new BbsClient(config.Bbs, _time, _log) { Version = Version };
            if (audioChanged)
            {
                restart = _audioRestart;
            }
        }
        _log($"config: now listening on {config.Audio}, delivering to {DescribeBbs(config.Bbs)}");
        restart?.Cancel();
        Delivery.Nudge();
    }

    /// <summary>The waits after a web receiver refuses us for now; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> RefusedBackoff =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30)];

    private async Task SuperviseAudioAsync(CancellationToken cancellation)
    {
        int refusals = 0;
        string? described = null;
        string? closedSaid = null;
        while (!cancellation.IsCancellationRequested)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            ReceiverConfig config;
            lock (_gate)
            {
                _audioRestart = restart;
                config = _config;
            }

            var source = AudioSource.Parse(config.Audio);
            CancellationTokenSource? closeAt = null;
            if (source.Kind == AudioSourceKind.UberSdr)
            {
                // A public web SDR allows each address about three hours a day, so it is only
                // listened to around some of the slots.
                // Said again whenever it changes: once a day with a daylight rule, as the days
                // lengthen and shorten.
                var schedule = Schedule;
                var (opens, closes, slot) = ListeningWindow.Next(_time.GetUtcNow(), schedule, config.WebSdrSlotsPerDay);
                string words = ListeningWindow.Describe(schedule, config.WebSdrSlotsPerDay, DateOnly.FromDateTime(slot.UtcDateTime));
                if (words != described)
                {
                    described = words;
                    _log($"audio: {words}");
                }
                if (opens > _time.GetUtcNow())
                {
                    AudioState = $"the web SDR is closed until {opens:HH:mm} UTC, ready for the {slot:HH:mm} UTC slot";
                    if (AudioState != closedSaid)
                    {
                        // Said once per wait, not at every look at the clock.
                        closedSaid = AudioState;
                        _log($"audio: {AudioState}");
                    }
                    // At most a minute at a time, then the clock again: a Pi that booted on
                    // fake-hwclock's stale time is put right by NTP partway through the wait,
                    // and one long timer would sleep straight through the real slot.
                    await ClockDelayAsync(Shorter(opens - _time.GetUtcNow(), ClockCheck), restart.Token).ConfigureAwait(false);
                    continue;
                }
                closedSaid = null;
                closeAt = new CancellationTokenSource();
                _ = CloseAtAsync(opens, closes, closeAt, restart.Token);
            }

            using var window = closeAt;
            using var running = window is null ? null : CancellationTokenSource.CreateLinkedTokenSource(restart.Token, window.Token);
            CancellationToken token = running?.Token ?? restart.Token;
            var pipeline = PipelineFactory?.Invoke(source) ?? CreatePipeline(source, config);
            Slots.Schedule = source.Kind == AudioSourceKind.Wav ? null : Schedule;
            lock (_gate)
            {
                _pipeline = pipeline;
            }

            string? why = null;
            bool refused = false;
            try
            {
                Attach(pipeline);
                PipelineCreated?.Invoke(pipeline);
                AudioState = $"opening {pipeline.Source}";
                await pipeline.StartAsync(token).ConfigureAwait(false);
                AudioState = $"listening to {pipeline.Source}";
                refusals = 0;
                PipelineStarted?.Invoke(pipeline);
                await pipeline.Finished.WaitAsync(token).ConfigureAwait(false);
                why = pipeline.EndReason ?? "the audio stopped";
            }
            catch (AudioSourceException e)
            {
                why = e.Message;
                refused = e.Refused;
            }
            catch (OperationCanceledException)
            {
                if (window is { IsCancellationRequested: true } && !restart.IsCancellationRequested)
                {
                    _log("audio: the clock is outside the slot's listening window now; closing the web SDR until its next slot");
                }
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                why = "unexpected failure: " + Ascii.Clean(e.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _audioRestart = null;
                    _pipeline = null;
                }
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            if (pipeline.LeftStuck)
            {
                // The device is still held by the stuck read, so every reopen would fail.
                throw new InvalidOperationException($"the read from {pipeline.Source} is stuck in the driver; restarting the service to let the device go");
            }
            if (why is null || cancellation.IsCancellationRequested)
            {
                continue;
            }
            why = why.TrimEnd('.', ' ');
            if (pipeline.Source.Kind == AudioSourceKind.Wav)
            {
                _log($"audio: {why}; the receiver keeps running to deliver what it rebuilt");
                AudioState = why;
                await WaitForRestartAsync(cancellation).ConfigureAwait(false);
                continue;
            }
            TimeSpan wait = refused ? RefusedBackoff[Math.Min(refusals++, RefusedBackoff.Count - 1)] : AudioRetry;
            _log($"audio: {why}. Trying again in {(wait.TotalMinutes >= 1 ? $"{wait.TotalMinutes:F0} min" : $"{wait.TotalSeconds:F0} s")}.");
            AudioState = $"{why}; trying again shortly";
            await DelayAsync(wait, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>The longest any wait on the wall clock goes before looking at it again.</summary>
    public static readonly TimeSpan ClockCheck = TimeSpan.FromSeconds(60);

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    /// <summary>
    /// Cancels <paramref name="close"/> once the clock reads <paramref name="closes"/>, or is put
    /// back before <paramref name="opens"/>, looking at it every minute.
    /// </summary>
    private async Task CloseAtAsync(DateTimeOffset opens, DateTimeOffset closes, CancellationTokenSource close, CancellationToken cancellation)
    {
        try
        {
            while (_time.GetUtcNow() >= opens && _time.GetUtcNow() < closes)
            {
                var tick = Task.Delay(Shorter(closes - _time.GetUtcNow(), ClockCheck), _time, cancellation);
                ClockWaiting?.Invoke();
                await tick.ConfigureAwait(false);
            }
            await close.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The pipeline for <paramref name="source"/>, heard on <paramref name="config"/>'s dial.</summary>
    internal AudioPipeline CreatePipeline(AudioSource source, ReceiverConfig config) =>
        AudioPipeline.Create(source, config.DialHz, _log, _time);

    /// <summary>For tests: builds the pipeline for a source in place of the real one.</summary>
    internal Func<AudioSource, AudioPipeline>? PipelineFactory { get; set; }

    /// <summary>For tests: raised once a wait on the wall clock (the window's opening or closing) has set its timer.</summary>
    internal event Action? ClockWaiting;

    private async Task ClockDelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            var tick = Task.Delay(delay, _time, cancellation);
            ClockWaiting?.Invoke();
            await tick.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, _time, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>After a recording has played: wait until the source is changed or the service stops.</summary>
    private async Task WaitForRestartAsync(CancellationToken cancellation)
    {
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (_gate)
        {
            _audioRestart = restart;
        }
        await DelayAsync(Timeout.InfiniteTimeSpan, restart.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _audioRestart = null;
        }
    }

    private void Attach(AudioPipeline pipeline)
    {
        pipeline.Channel.FrameReceived += (_, frame) => Intake.Offer(frame);
        pipeline.Tone.ToneMeasured += Slots.OnTone;
    }

    internal static string DescribeBbs(BbsSettings bbs) =>
        $"{(bbs.Type == BbsKind.LinBpq ? "LinBPQ" : "FBB")} at {bbs.Host}:{bbs.Port} as {bbs.Login}";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Retuner is not null)
        {
            await Retuner.DisposeAsync().ConfigureAwait(false);
        }
        await Intake.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Always delivers through the client for the configuration in force.</summary>
    private sealed class SwitchableSession(ReceiverHost host) : IBbsSession
    {
        public Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation) =>
            host.Bbs.DeliverAsync(bulletins, cancellation);
    }
}
