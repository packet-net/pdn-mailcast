using Packet.Mailcast;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Hooks;
using Mailcast.Receiver.Retune;
using Packet.Mailcast.Feedback;

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
        Intake.Sources = config.AcceptedSources;
        Ledger = new DeliveryLedger(config.StateDirectory);
        Slots = new SlotTracker(time, log, AudioSource.Parse(config.Audio).Kind == AudioSourceKind.Wav ? null : Schedule);
        Bbs = new BbsClient(config.Bbs, time, log) { Version = Version };
        Delivery = new DeliveryService(Intake, new SwitchableSession(this), Ledger, time, log);
        Intake.FrameHeard += Slots.OnFrame;
        Intake.DirectoryHeard += directory => Slots.OnDirectory(directory.Mode);
        Intake.IonosphereHeard += _ => Slots.OnIonosphereHeard();
        Intake.PskReporterHeard += _ => Slots.OnPskReporterHeard();
        Intake.ScheduleHeard += OnScheduleHeard;
        Hooks = new SlotHooks(() => Config, time, log);
        ChannelWatch = new ChannelWatch(time, log, Path.Combine(config.StateDirectory, ChannelWatch.FileName), () => Place);
        if (config.Rig is not null)
        {
            Retuner = new Retuner(config, () => Schedule, () => AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.Alsa, time, log, Hooks, EarlyEndReason);
        }
        ListenNow = new ListenNowService(time, config.StateDirectory);
        Feedback = CreateFeedback();
    }

    /// <summary>
    /// Issue #53: a web SDR receiver's "Listen now" button, opened for a few minutes between
    /// its scheduled windows.
    /// </summary>
    public ListenNowService ListenNow { get; }

    /// <summary>
    /// Opens a "Listen now" session, if one is not refused (see <see cref="ListenNowService.Problem"/>):
    /// only for a web SDR, and never overlapping a slot's own window.
    /// </summary>
    public ListenNowService.Result RequestListenNow()
    {
        var config = Config;
        if (AudioSource.Parse(config.Audio).Kind != AudioSourceKind.UberSdr)
        {
            return new ListenNowService.Result(false, "Listen now is for a web SDR receiver only.", null);
        }
        var now = _time.GetUtcNow();
        var nextOpens = ListeningWindow.Next(now, Schedule).Opens;
        var result = ListenNow.Request(now, nextOpens, alreadyListening: Audio.Phase == AudioPhase.Listening);
        if (result.Ok)
        {
            WakeListenNow();
        }
        return result;
    }

    private readonly object _listenNowGate = new();
    private TaskCompletionSource _listenNowWake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Wakes a wait on "Listen now" state at once, rather than it waiting out the clock check: a
    /// granted request while closed opens the web SDR right away, and (issue #57) a schedule
    /// change while a session is open re-checks its close against it right away too.
    /// </summary>
    private void WakeListenNow()
    {
        TaskCompletionSource wake;
        lock (_listenNowGate)
        {
            wake = _listenNowWake;
        }
        wake.TrySetResult();
    }

    /// <summary>The daily report to the broadcast's author, when the config's <c>feedback</c> turns it on.</summary>
    public FeedbackService Feedback { get; }

    private FeedbackService CreateFeedback()
    {
        AudioSourceKind Kind() => AudioSource.Parse(Config.Audio).Kind;
        var feedback = new FeedbackService(new FeedbackSources
        {
            Settings = () => Config.Feedback,
            Schedule = () => Kind() == AudioSourceKind.Wav ? null : Schedule,
            Listened = day => Kind() == AudioSourceKind.UberSdr ? ListeningWindow.WebSdrSlotsOn(Schedule, day) : Schedule.ActiveOn(day),
            LastSlot = () => Slots.Last,
            Ionosphere = () => Intake.Ionosphere,
            PskReporter = () => Intake.PskReporter,
            Channel = slot => ChannelSummary.From(ChannelWatch.History.LastOrDefault(r => r.Slot == slot)),
            Locator = () => ChannelSummary.Locator(Place),
            Audio = () => ReportAudio.For(AudioSource.Parse(Config.Audio)),
        }, new SwitchableSession(this), Config.StateDirectory, _time, _log);
        Intake.BulletinCompleted += _ => feedback.NoteRebuilt();
        Delivery.SessionFinished += report =>
        {
            feedback.NoteDelivered(report.Outcomes.Count(o => o.Verdict == DeliveryVerdict.Accepted));
            if (report.Failure is not null)
            {
                feedback.NoteError(ReportErrors.Bbs);
            }
        };
        AudioChanged += audio =>
        {
            if (audio.Phase == AudioPhase.Failed)
            {
                feedback.NoteError(ReportErrors.Audio);
            }
        };
        Hooks.Warned += () => feedback.NoteError(ReportErrors.Hook);
        if (Retuner is not null)
        {
            Retuner.ProblemNoted += () => feedback.NoteError(ReportErrors.Rig);
        }
        return feedback;
    }

    /// <summary>The config's <c>hooks</c>, run around each slot listened to.</summary>
    public SlotHooks Hooks { get; }

    /// <summary>The radio path from GB7RDG, measured after each slot from the bursts decoded.</summary>
    public ChannelWatch ChannelWatch { get; }

    /// <summary>
    /// Where the receiver is, for naming the hops: where the web SDR in use says it is, once it
    /// has said; null for a sound card or a recording.
    /// </summary>
    public GroundPlace? Place
    {
        get
        {
            var audio = Config.Audio;
            lock (_gate)
            {
                return _webSdrPlace is { } p && p.Source == audio ? p.Place : null;
            }
        }
    }

    private (string Source, GroundPlace? Place)? _webSdrPlace;

    /// <summary>How long after a slot's start a burst still counts for it.</summary>
    public static readonly TimeSpan SlotLasts = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The slot a burst heard at <paramref name="heard"/> belongs to, for the channel
    /// measurement: with GB7RDG's slots known, the slot that began within <see cref="SlotLasts"/>
    /// before it, or null if none did (a sound card hearing another MS110D station between
    /// slots). Without them (a recording), one slot for each run of bursts with no gap of
    /// <see cref="SlotTracker.Gap"/>, named by its first burst.
    /// </summary>
    internal DateTimeOffset? ChannelSlot(DateTimeOffset heard, ref (DateTimeOffset Slot, DateTimeOffset Last)? recording)
    {
        if (Slots.Schedule is { } schedule)
        {
            return schedule.LatestActiveStart(heard + SlotTracker.ClockAllowance) is { } start && heard - start <= SlotLasts ? start : null;
        }
        recording = recording is { } r && heard - r.Last <= SlotTracker.Gap ? (r.Slot, heard) : (heard, heard);
        return recording.Value.Slot;
    }

    /// <summary>Whether the retuner runs the hooks, in step with its own work: it is retuning the radio the audio comes from.</summary>
    internal bool RetunerRunsHooks => Retuner is not null && AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.Alsa;

    /// <summary>
    /// Issue #49: the probe after a slot's tone is captured within about this long of the slot's
    /// start (the tone, <see cref="OnAir.ToneSeconds"/>, then up to
    /// <see cref="CapturedProbe.AfterSeconds"/> of audio after it, with a little margin for
    /// processing). Past it, ending the window early no longer waits for a probe that is not coming.
    /// </summary>
    internal static readonly TimeSpan ProbeCaptureWindow = TimeSpan.FromSeconds(OnAir.ToneSeconds + CapturedProbe.AfterSeconds + 5);

    /// <summary>Issue #49: how long since the last frame before a window may end early, in case a burst is still to come.</summary>
    internal static readonly TimeSpan QuietBeforeEarlyEnd = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Issue #49: why the listening window for the slot starting at <paramref name="slotStart"/>
    /// can end now, before its usual close; null while it should keep going. Conservative:
    /// anything unknown or incomplete, or no slot tracked yet matching <paramref name="slotStart"/>,
    /// keeps it open.
    /// </summary>
    internal string? EarlyEndReason(DateTimeOffset slotStart)
    {
        var slot = Slots.Last;
        if (slot is null || slot.Scheduled != slotStart)
        {
            return null;
        }
        if (!slot.DirectoryHeard)
        {
            // The receiver does not yet know what is in today's rotation.
            return null;
        }
        var (directory, progress) = Intake.Progress();
        if (directory is null || progress.Any(p => !p.Complete))
        {
            // A bulletin, or the directory itself, is not complete yet.
            return null;
        }
        // The ionosonde and PSK Reporter readings are new objects each slot, never listed in the
        // directory, so Progress() above says nothing about them. Wait for each to be heard this
        // slot, unless this receiver has never heard that kind at all: then the head end, or this
        // broadcast, may simply not send it, and there is no other way to tell "the plan has none".
        if (!slot.IonosphereHeard && Intake.Ionosphere is not null)
        {
            return null;
        }
        if (!slot.PskReporterHeard && Intake.PskReporter is not null)
        {
            return null;
        }
        var now = _time.GetUtcNow();
        if (!slot.ProbeCaptured && now - slot.Started < ProbeCaptureWindow)
        {
            // The probe used for the channel measurement when no burst decodes may still be coming.
            return null;
        }
        var quiet = now - (slot.LastFrame ?? slot.Started);
        if (quiet < QuietBeforeEarlyEnd)
        {
            // A burst may still be coming; be conservative while the head end may send something new.
            return null;
        }
        string reason = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"the directory and everything in rotation are heard, the probe/channel measurement is captured, and it has been quiet for {quiet.TotalSeconds:F0} s");
        NoteEarlyEnd(slotStart, reason);
        return reason;
    }

    private (DateTimeOffset Slot, string Reason)? _lastEarlyEnd;

    /// <summary>
    /// Issue #49: the most recent slot a window was ended early for, and why, for the status
    /// page; null once nothing has ended early yet.
    /// </summary>
    public (DateTimeOffset Slot, string Reason)? LastEarlyEnd
    {
        get { lock (_gate) { return _lastEarlyEnd; } }
    }

    private void NoteEarlyEnd(DateTimeOffset slot, string reason)
    {
        lock (_gate)
        {
            _lastEarlyEnd = (slot, reason);
        }
    }

    /// <summary>
    /// The listening window the hooks run around, in progress at <paramref name="at"/> or else
    /// the next: a web SDR's own, or for a sound card the same around every slot that runs. None
    /// for a recording, or with no slot in the year ahead.
    /// </summary>
    internal HookWindow? HookWindowAt(DateTimeOffset at)
    {
        var config = Config;
        try
        {
            switch (AudioSource.Parse(config.Audio).Kind)
            {
                case AudioSourceKind.UberSdr:
                    var (opens, closes, slot) = ListeningWindow.Next(at, Schedule);
                    // Issue #49: a web SDR's window that already ended early closed then, not
                    // at its usual time; "after" (run by the hooks loop, not this audio one)
                    // should not wait for a close that already happened.
                    if (EarlyEndFinished is { } early && early.Slot == slot && early.At < closes)
                    {
                        closes = early.At;
                    }
                    return new HookWindow(opens, closes, slot);
                case AudioSourceKind.Alsa:
                    return ListeningWindow.SoundCard(at, Schedule) is { } window ? new HookWindow(window.Opens, window.Closes, window.Slot) : null;
                default:
                    return null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
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
        // Issue #57: a "Listen now" session re-checks its close against the new schedule at
        // once, rather than waiting for its next clock check.
        WakeListenNow();
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
    public string AudioState => Audio.Words;

    /// <summary>What the audio is doing, for the page: in words, and the parts the page needs to explain it.</summary>
    public AudioCondition Audio
    {
        get => _audio;
        private set
        {
            _audio = value;
            AudioChanged?.Invoke(value);
        }
    }

    private AudioCondition _audio = new("starting", AudioPhase.Starting);

    /// <summary>
    /// What the web SDR said about itself when it was last opened (its callsign, name and
    /// location), kept between slots while the audio setting is the same; null before it has
    /// been opened, or if it would not say.
    /// </summary>
    public string? WebSdrAbout
    {
        get
        {
            lock (_gate)
            {
                return _webSdrAbout is { } about && about.Source == _config.Audio ? about.Words : null;
            }
        }
    }

    private (string Source, string Words)? _webSdrAbout;

    /// <summary>For tests: raised each time <see cref="Audio"/> changes.</summary>
    internal event Action<AudioCondition>? AudioChanged;

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
        _log(SourcesLine(Config));
        if (Config.SlotUtcWithoutEveryMinutes)
        {
            _log($"config: \"slotUtc\" without \"everyMinutes\" is from before hourly slots; read as every 60 minutes from {Config.SlotUtc} UTC, the same hourly slots, so nothing needs changing");
        }
        if (Config.WebSdrSlotsPerDayIgnored)
        {
            _log("config: webSdrSlotsPerDay is no longer used and is ignored; a web SDR listens to every daylight slot that fits in its allowance");
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
        if (Config.Hooks is { } hooks && Hooks.Configured)
        {
            _log("hooks: "
                + (hooks.Before is { } b ? $"\"before\" each slot listened to runs {b.Describe()}, started {b.TimeoutSeconds} s ahead so it is done in time" : "no \"before\"")
                + (hooks.After is { } a ? $"; \"after\" runs {a.Describe()}" : "; no \"after\""));
        }
        Hooks.Recover();

        using var stopOthers = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task delivery = Delivery.RunAsync(stopOthers.Token);
        Task audio = SuperviseAudioAsync(stopOthers.Token);
        // The retuner catches and logs its own problems and ends only when the others do,
        // putting the rig back and LinBPQ on the air on its way out; if it ends any other way,
        // that is a failure like the others', and systemd's restart picks up where it left off.
        Task retune = Retuner?.RunAsync(stopOthers.Token) ?? DelayAsync(Timeout.InfiniteTimeSpan, stopOthers.Token);
        // The hooks around a web SDR's or a sound card's windows; the retuner runs its own.
        Task hookLoop = Hooks.RunAsync(() => RetunerRunsHooks, HookWindowAt, stopOthers.Token);
        // The daily report catches and logs its own problems, and ends only when the others do.
        Task feedback = Feedback.RunAsync(stopOthers.Token);
        Task first;
        try
        {
            first = await Task.WhenAny(delivery, audio, retune, hookLoop, feedback).ConfigureAwait(false);
            await stopOthers.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(delivery, audio, retune, hookLoop, feedback).ConfigureAwait(false);
            }
            catch (Exception) when (first.IsFaulted || first == retune || first == hookLoop || first == feedback)
            {
            }
        }
        finally
        {
            // "after" always runs once "before" has, whatever stopped the receiver.
            await Hooks.FinishAsync().ConfigureAwait(false);
        }
        if ((first == retune || first == hookLoop || first == feedback) && !cancellation.IsCancellationRequested && !first.IsFaulted)
        {
            throw new InvalidOperationException($"the {(first == retune ? "retune" : first == hookLoop ? "hooks" : "feedback")} loop stopped on its own");
        }
        if (first.IsFaulted)
        {
            string which = first == delivery ? "delivery" : first == audio ? "audio" : first == retune ? "retune" : first == hookLoop ? "hooks" : "feedback";
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
        // A recording's measurement is for the log only: never in the state directory of the
        // service that may be running beside it, and without the rests, which are there to
        // spare a running receiver's CPU.
        ChannelWatch.Persist = false;
        ChannelWatch.Rest = false;
        await pipeline.StartAsync(cancellation).ConfigureAwait(false);
        await pipeline.Finished.WaitAsync(cancellation).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        await Intake.DrainAsync(cancellation).ConfigureAwait(false);
        _log($"decode: {Intake.FramesHeard} frames heard, {Intake.Pending().Count} bulletins to deliver");
        // Delivered first: the measurement is a nicety, and must not hold the mail up.
        string? delivered = await Delivery.DeliverPendingAsync(cancellation).ConfigureAwait(false);
        ChannelWatch.SlotOver();
        try
        {
            await ChannelWatch.IdleAsync().WaitAsync(DecodeMeasureWait, _time, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log($"channel: the measurement took longer than {DecodeMeasureWait.TotalMinutes:F0} minutes; not waiting for it");
        }
        return delivered;
    }

    /// <summary>The start-up line saying which callsigns frames are accepted from, and whether that is the default for a file without "sources".</summary>
    internal static string SourcesLine(ReceiverConfig config) => config.Sources is null
        ? $"config: there is no \"sources\" in the config file, so frames are accepted from {ReceiverConfig.DefaultSources}, the default; add \"sources\" to choose"
        : $"config: frames are accepted from {config.Sources}, as \"sources\" says";

    /// <summary>The longest --decode waits for the channel measurement once the mail is delivered.</summary>
    internal static readonly TimeSpan DecodeMeasureWait = TimeSpan.FromMinutes(2);

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
                || _config.Daylight != config.Daylight
                || _config.DialKHz != config.DialKHz;
            _config = config;
            Intake.Sources = config.AcceptedSources;
            Bbs = new BbsClient(config.Bbs, _time, _log) { Version = Version };
            if (audioChanged)
            {
                restart = _audioRestart;
            }
        }
        _log($"config: now listening on {config.Audio}, delivering to {DescribeBbs(config.Bbs)}, accepting frames from {config.AcceptedSources}");
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
            DateTimeOffset? closingSlot = null;
            if (source.Kind == AudioSourceKind.UberSdr)
            {
                // A public web SDR allows each address about three hours a day, so it is only
                // listened to around some of the slots.
                // Said again whenever it changes: once a day with a daylight rule, as the days
                // lengthen and shorten.
                var schedule = Schedule;
                var (opens, closes, slot) = ListeningWindow.Next(_time.GetUtcNow(), schedule);
                if (slot == EarlyEndFinished?.Slot)
                {
                    // Issue #49: this window was closed early, before its usual end; it is not
                    // reopened for the rest of it, same as if it had run to its usual close.
                    (opens, closes, slot) = ListeningWindow.Next(closes, schedule);
                }
                string words = ListeningWindow.Describe(schedule, DateOnly.FromDateTime(slot.UtcDateTime));
                if (words != described)
                {
                    described = words;
                    _log($"audio: {words}");
                }
                // Issue #53: a "Listen now" session granted while the web SDR would otherwise be
                // closed opens it at once, until the session's own end (never the real window's:
                // ListenNowService caps it at the window's opening).
                bool listenNow = false;
                if (opens > _time.GetUtcNow() && ListenNow.ActiveUntil(_time.GetUtcNow(), opens) is { } until)
                {
                    opens = _time.GetUtcNow();
                    closes = until;
                    listenNow = true;
                }
                if (opens > _time.GetUtcNow())
                {
                    Audio = new($"the web SDR {WebSdrHost(source)} is closed until {opens:HH:mm} UTC, ready for the {slot:HH:mm} UTC slot", AudioPhase.Closed, Reopens: opens, ForSlot: slot);
                    if (AudioState != closedSaid)
                    {
                        // Said once per wait, not at every look at the clock.
                        closedSaid = AudioState;
                        _log($"audio: {AudioState}");
                    }
                    // At most a minute at a time, then the clock again: a Pi that booted on
                    // fake-hwclock's stale time is put right by NTP partway through the wait,
                    // and one long timer would sleep straight through the real slot. A granted
                    // "Listen now" request also wakes this at once.
                    await ClockDelayAsync(Shorter(opens - _time.GetUtcNow(), ClockCheck), restart.Token).ConfigureAwait(false);
                    continue;
                }
                closedSaid = null;
                if (listenNow)
                {
                    _log($"audio: \"Listen now\": opening the web SDR {WebSdrHost(source)} until {closes.UtcDateTime:HH:mm:ss} UTC");
                }
                closeAt = new CancellationTokenSource();
                closingSlot = slot;
                // Issue #57: a "Listen now" session's close is re-checked against the schedule
                // every time, not just at grant, so a window GB7RDG's directory moves earlier
                // mid-session is still never overlapped.
                Func<DateTimeOffset, DateTimeOffset>? recomputeCloses = !listenNow ? null
                    : now => ListenNow.ActiveUntil(now, ListeningWindow.Next(now, Schedule).Opens) ?? now;
                _ = CloseAtAsync(opens, closes, slot, recomputeCloses, closeAt, restart.Token);
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
                Audio = new($"opening {pipeline.Source}", AudioPhase.Opening);
                await pipeline.StartAsync(token).ConfigureAwait(false);
                if (pipeline.WebSdrDescription is { } about)
                {
                    lock (_gate)
                    {
                        _webSdrAbout = (config.Audio, about);
                    }
                }
                Audio = new($"listening to {pipeline.Source}", AudioPhase.Listening);
                refusals = 0;
                PipelineStarted?.Invoke(pipeline);
                _ = NotePlaceAsync(pipeline.Source, config.Audio, token);
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
                // Issue #49: CloseAtAsync already said why and set EarlyEndFinished when it
                // ended this slot's window early; saying the clock is outside it too would be
                // misleading, since by the clock it is not.
                if (window is { IsCancellationRequested: true } && !restart.IsCancellationRequested && EarlyEndFinished?.Slot != closingSlot)
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
                ChannelWatch.SlotOver();
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
                Audio = new(why, AudioPhase.Ended, Problem: why);
                await WaitForRestartAsync(cancellation).ConfigureAwait(false);
                continue;
            }
            TimeSpan wait = refused ? RefusedBackoff[Math.Min(refusals++, RefusedBackoff.Count - 1)] : AudioRetry;
            _log($"audio: {why}. Trying again in {(wait.TotalMinutes >= 1 ? $"{wait.TotalMinutes:F0} min" : $"{wait.TotalSeconds:F0} s")}.");
            Audio = new($"{why}; trying again shortly", AudioPhase.Failed, Problem: why, RetryAt: _time.GetUtcNow() + wait);
            await DelayAsync(wait, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>The longest any wait on the wall clock goes before looking at it again.</summary>
    public static readonly TimeSpan ClockCheck = TimeSpan.FromSeconds(60);

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    /// <summary>How often a web SDR's open window is checked for ending early (issue #49).</summary>
    internal static readonly TimeSpan EarlyEndPoll = TimeSpan.FromSeconds(15);

    private (DateTimeOffset Slot, DateTimeOffset At)? _earlyEndFinished;

    /// <summary>
    /// Issue #49: the most recent web SDR slot whose window was closed early, before its usual
    /// end, and when; it is not reopened for the rest of that window, the same as one that ran to
    /// its usual close, and <see cref="HookWindowAt"/> says its window closed then, not at its
    /// usual time, so "after" does not wait for that either.
    /// </summary>
    private (DateTimeOffset Slot, DateTimeOffset At)? EarlyEndFinished
    {
        get { lock (_gate) { return _earlyEndFinished; } }
        set { lock (_gate) { _earlyEndFinished = value; } }
    }

    /// <summary>
    /// Cancels <paramref name="close"/> once the clock reads <paramref name="closes"/>, it is put
    /// back before <paramref name="opens"/>, or (issue #49) <paramref name="slot"/>'s window can
    /// end early; looking at it every minute, or <see cref="EarlyEndPoll"/> if sooner.
    /// <paramref name="recomputeCloses"/>, for a "Listen now" session (issue #57), is asked for a
    /// fresh <paramref name="closes"/> each time, and wakes this at once if the schedule changes
    /// mid-session, so the session never overlaps a window that has since moved earlier.
    /// </summary>
    private async Task CloseAtAsync(DateTimeOffset opens, DateTimeOffset closes, DateTimeOffset slot, Func<DateTimeOffset, DateTimeOffset>? recomputeCloses, CancellationTokenSource close, CancellationToken cancellation)
    {
        try
        {
            while (_time.GetUtcNow() >= opens && _time.GetUtcNow() < closes)
            {
                if (EarlyEndReason(slot) is { } why)
                {
                    var now = _time.GetUtcNow();
                    _log($"audio: ending the {slot.UtcDateTime:HH:mm} UTC slot's window early: {why}");
                    EarlyEndFinished = (slot, now);
                    break;
                }
                if (recomputeCloses is null)
                {
                    var tick = Task.Delay(Shorter(Shorter(closes - _time.GetUtcNow(), ClockCheck), EarlyEndPoll), _time, cancellation);
                    ClockWaiting?.Invoke();
                    await tick.ConfigureAwait(false);
                    continue;
                }
                closes = recomputeCloses(_time.GetUtcNow());
                Task wake;
                lock (_listenNowGate)
                {
                    if (_listenNowWake.Task.IsCompleted)
                    {
                        _listenNowWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    wake = _listenNowWake.Task;
                }
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var listenTick = Task.Delay(Shorter(closes - _time.GetUtcNow(), ClockCheck), _time, stop.Token);
                ClockWaiting?.Invoke();
                await Task.WhenAny(listenTick, wake).ConfigureAwait(false);
                await stop.CancelAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
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
        Task wake;
        lock (_listenNowGate)
        {
            if (_listenNowWake.Task.IsCompleted)
            {
                _listenNowWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            wake = _listenNowWake.Task;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            var tick = Task.Delay(delay, _time, stop.Token);
            ClockWaiting?.Invoke();
            await Task.WhenAny(tick, wake).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Asks the web SDR where it is, once for each audio setting, and keeps the answer.
    /// pdn-soundmodem (0.86.0) reads the web SDR's <c>/api/description</c> when it opens it, but
    /// keeps only a line of words from it, without the position, so it is read again here: once
    /// a run of the receiver, not every slot.
    /// </summary>
    private async Task NotePlaceAsync(AudioSource source, string audio, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (source.Kind != AudioSourceKind.UberSdr || _webSdrPlace?.Source == audio)
            {
                return;
            }
        }
        var place = await AudioPipeline.FetchWebSdrPlaceAsync(Packet.SoundModem.UberSdr.UberSdrDevice.Parse(source.Target), cancellation).ConfigureAwait(false);
        if (place is not null || !cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                _webSdrPlace = (audio, place);
            }
        }
    }

    private void Attach(AudioPipeline pipeline)
    {
        (DateTimeOffset Slot, DateTimeOffset Last)? recording = null;
        pipeline.BurstCaptured += burst =>
        {
            if (ChannelSlot(burst.Heard, ref recording) is { } slot)
            {
                ChannelWatch.Offer(burst, slot);
            }
        };
        pipeline.ProbeCaptured += probe =>
        {
            Slots.OnProbeCaptured();
            if (ChannelSlot(probe.Heard, ref recording) is { } slot)
            {
                ChannelWatch.OfferProbe(probe, slot);
            }
        };
        pipeline.Capture.TooLong += seconds =>
        {
            ChannelWatch.NoteTooLong();
            _log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"channel: a {seconds:F0} s burst is longer than the {BurstCapture.RingSeconds} s of audio kept for measuring, so it is left out"));
        };
        // On the audio thread, while the modem is still locked to the burst the frame came on.
        pipeline.Channel.FrameReceived += (_, frame) => Intake.Offer(frame, pipeline.FrameWaveform);
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
        await ChannelWatch.DisposeAsync().ConfigureAwait(false);
        Hooks.Dispose();
        _bbsTurn.Dispose();
    }

    /// <summary>A web SDR's address as an operator writes it, such as wessex.zapto.org; for a sound card or recording, its setting.</summary>
    internal static string WebSdrHost(AudioSource source) =>
        source.Kind == AudioSourceKind.UberSdr ? Packet.SoundModem.UberSdr.UberSdrDevice.Parse(source.Target).ToString() : source.ToString();

    /// <summary>One session with the BBS at a time: the bulletins' and the daily report's.</summary>
    private readonly SemaphoreSlim _bbsTurn = new(1, 1);

    /// <summary>Always delivers through the client for the configuration in force, one session at a time.</summary>
    private sealed class SwitchableSession(ReceiverHost host) : IBbsSession
    {
        public async Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
        {
            await host._bbsTurn.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await host.Bbs.DeliverAsync(bulletins, cancellation).ConfigureAwait(false);
            }
            finally
            {
                host._bbsTurn.Release();
            }
        }
    }
}

/// <summary>Where the audio is: what the status page shows in place of the spectrogram while there is none.</summary>
public enum AudioPhase
{
    /// <summary>The receiver has only just started.</summary>
    Starting,

    /// <summary>A web SDR, closed between the slots it listens to.</summary>
    Closed,

    /// <summary>Opening the source.</summary>
    Opening,

    /// <summary>Audio is coming in.</summary>
    Listening,

    /// <summary>The source could not be opened or stopped; it is tried again at <see cref="AudioCondition.RetryAt"/>.</summary>
    Failed,

    /// <summary>A recording has played to its end.</summary>
    Ended,
}

/// <summary>
/// What the audio is doing: <paramref name="Words"/> for the log and the page's summary, and for a
/// web SDR closed between slots when it opens again (<paramref name="Reopens"/>) and for which slot,
/// or for a source that failed, why (<paramref name="Problem"/>) and when it is tried again.
/// </summary>
public sealed record AudioCondition(string Words, AudioPhase Phase, DateTimeOffset? Reopens = null, DateTimeOffset? ForSlot = null, string? Problem = null, DateTimeOffset? RetryAt = null);
