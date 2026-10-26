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
            TrustedKeys = config.AllTrustedKeys,
        });
        Intake.Sources = config.AcceptedSources;
        Ledger = new DeliveryLedger(config.StateDirectory);
        Slots = new SlotTracker(time, log, AudioSource.Parse(config.Audio).Kind == AudioSourceKind.Wav ? null : Followed);
        Bbs = new BbsClient(config.Bbs, time, log) { Version = Version };
        Delivery = new DeliveryService(Intake, new SwitchableSession(this), Ledger, time, log);
        Intake.FrameHeard += Slots.OnFrame;
        Intake.DirectoryHeard += OnDirectoryHeard;
        Intake.IonosphereHeard += _ => Slots.OnIonosphereHeard();
        Intake.PskReporterHeard += _ => Slots.OnPskReporterHeard();
        // Issue #133: the bulletins of the slot now on, for its summary line.
        Intake.BulletinCompleted += _ => Slots.OnBulletinCompleted();
        Intake.GivenUp += ids => Slots.OnGivenUp(ids.Count);
        Delivery.SessionFinished += report => Slots.OnDelivered(
            report.Outcomes.Count(o => o.Verdict == DeliveryVerdict.Accepted),
            report.Outcomes.Count(o => o.Verdict == DeliveryVerdict.AlreadyHad));
        Intake.ScheduleHeard += OnScheduleHeard;
        // Issue #48: after Slots.OnFrame above, so the slot this piece belongs to is already
        // the one Slots.Last knows about.
        Intake.Piece += OnPieceHandled;
        // The dial the audio is really heard on: pdn-soundmodem's own, when it says one.
        Hooks = new SlotHooks(() => Config, time, log) { DialKHz = () => DialHz / 1000, SignalKHz = slot => StationAtSlot(slot)?.FrequencyHz / 1000.0 };
        ChannelWatch = new ChannelWatch(time, log, Path.Combine(config.StateDirectory, ChannelWatch.FileName), () => Place) { AudioCentreHz = config.AudioCentreHz };
        ChannelWatch.Transmitter = slot => StationAtSlot(slot)?.OnTheAir;
        ChannelWatch.Measured += FrameHistory.OnChannelMeasured;
        if (config.Retune && AudioSource.Parse(config.Audio).Kind == AudioSourceKind.SoundModem)
        {
            // pdn-soundmodem's receive window: it tunes its own radio and holds its own
            // transmitting, so neither rigctld nor LinBPQ is touched.
            Retuner = new Retuner(config, () => Followed, () => AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.SoundModem, time, log,
                new ReceiveWindow(() => AudioSource.Parse(Config.Audio) is { Kind: AudioSourceKind.SoundModem } s ? s.SoundModemPort : AudioSource.DefaultSoundModemPort),
                () => Config.DialHz, FollowDial, Hooks, EarlyEndReason, slot => PointAtSlot(slot));
        }
        else if (config.Rig is not null)
        {
            Retuner = new Retuner(config, () => Followed, () => AudioSource.Parse(Config.Audio).IsSoundCard, time, log, Hooks, EarlyEndReason, slot => PointAtSlot(slot));
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
        var nextOpens = NextWebSdrWindowOpens(now);
        var result = ListenNow.Request(now, nextOpens, alreadyListening: Audio.Phase is AudioPhase.Listening or AudioPhase.Reconnecting);
        if (result.Ok)
        {
            WakeListenNow();
        }
        return result;
    }

    /// <summary>How long "Measure my filter" listens to band noise.</summary>
    public static readonly TimeSpan FilterMeasureDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Listens to <see cref="FilterMeasureDuration"/> of this radio's own audio and measures its
    /// receive filter (<see cref="FilterScan.Analyse"/>): only for a sound card or pdn-soundmodem
    /// (whose keyed silence is left out), and never while a slot's own window is open (nothing
    /// should be tuned away from the signal then).
    /// </summary>
    public async Task<(RadioFilter? Filter, string? Problem)> MeasureFilterAsync(CancellationToken cancellation)
    {
        var pipeline = Pipeline;
        if (pipeline is null || AudioSource.Parse(Config.Audio) is not ({ IsSoundCard: true } or { Kind: AudioSourceKind.SoundModem }))
        {
            return (null, "\"Measure my filter\" is for a sound card or pdn-soundmodem only.");
        }
        var now = _time.GetUtcNow();
        var window = ListeningWindow.SoundCard(now, Followed);
        if (window is { } w && now >= w.Opens && now < w.Closes && EarlyEndFinished?.Slot != w.Slot)
        {
            return (null, "A slot's window is open now: wait until it closes and try again.");
        }
        var samples = new List<float>();
        var gate = new object();
        void OnBlock(ReadOnlySpan<float> block)
        {
            lock (gate)
            {
                samples.AddRange(block);
            }
        }
        pipeline.SourceBlock += OnBlock;
        try
        {
            await Task.Delay(FilterMeasureDuration, _time, cancellation).ConfigureAwait(false);
        }
        finally
        {
            pipeline.SourceBlock -= OnBlock;
        }
        float[] captured;
        lock (gate)
        {
            captured = samples.ToArray();
        }
        return FilterScan.Analyse(captured);
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

    /// <summary>The slots the receiver listens to on a UTC day, of every station it follows: a web SDR's own, or every one that runs.</summary>
    private IReadOnlyList<DateTimeOffset> ListenedOn(DateOnly day)
    {
        var followed = Followed;
        return AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.UberSdr ? ListeningWindow.WebSdrSlotsOn(followed, day) : followed.ActiveOn(day);
    }

    private FeedbackService CreateFeedback()
    {
        AudioSourceKind Kind() => AudioSource.Parse(Config.Audio).Kind;
        var feedback = new FeedbackService(new FeedbackSources
        {
            Settings = () => Config.Feedback,
            Schedule = () => Kind() == AudioSourceKind.Wav ? null : Schedule,
            Listened = ListenedOn,
            Station = slot => Followed.OneStation ? Followed.Stations[0].Station : Followed.StationAt(slot) ?? ReceiverStore.BuiltInStation,
            OwnStation = () => Followed.OneStation ? Followed.Stations[0].Station : ReceiverStore.BuiltInStation,
            LastSlot = () => Slots.Last,
            Ionosphere = () => Intake.Ionosphere,
            PskReporter = () => Intake.PskReporter,
            Channel = slot => ChannelSummary.From(ChannelWatch.History.LastOrDefault(r => r.Slot == slot)),
            Locator = () => ChannelSummary.Locator(Place),
            Audio = () => ReportAudio.For(AudioSource.Parse(Config.Audio)),
        }, new SwitchableSession(this), Config.StateDirectory, _time, _log);
        Intake.BulletinCompleted += _ => feedback.NoteRebuilt();
        Intake.GivenUp += feedback.NoteGivenUp;
        // The report goes to GB7RDG, and only its directories say whether it got there, whatever
        // the report says about other stations (issue #148).
        Intake.DirectoryHeard += directory =>
        {
            if (Intake.StationOf(directory) == ReceiverStore.BuiltInStation)
            {
                feedback.NoteDirectory(directory);
            }
        };
        Delivery.SessionFinished += report =>
        {
            feedback.NoteDelivered(report.Outcomes.Count(o => o.Verdict == DeliveryVerdict.Accepted));
            feedback.NoteAlreadyHad(report.Outcomes.Count(o => o.Verdict == DeliveryVerdict.AlreadyHad));
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
    /// Issue #48: what the last few slots carried, burst by burst and piece by piece, for the
    /// status page's "what was heard" section and <c>GET /api/slots/{slot}/frames</c>. In
    /// memory only; empty again after a restart.
    /// </summary>
    public SlotFrameHistory FrameHistory { get; } = new();

    /// <summary>
    /// Issue #48: records one piece into <see cref="FrameHistory"/>, on the intake worker
    /// (<see cref="Intake.Piece"/>), never the audio thread. The slot is <see cref="Slots"/>'s
    /// own identity; the burst is the audio thread's own published snapshot
    /// (<see cref="BurstWatch.Shown"/>), read here, not measured here.
    /// </summary>
    private void OnPieceHandled(PieceHandled piece)
    {
        var slot = Slots.Last is { } last ? last.Scheduled ?? last.Started : piece.Heard;
        var burstStarted = Pipeline?.Burst.Shown?.Started;
        var directory = Intake.Progress().Directory;
        FrameHistory.Record(slot, burstStarted, piece.Waveform, HeardPiece.From(piece.Result, piece.Esi, piece.Heard, directory));
    }

    /// <summary>
    /// Where the receiver is, for naming the hops: where the web SDR in use says it is, once it
    /// has said; for a sound card, pdn-soundmodem or a recording, the configured
    /// <see cref="ReceiverConfig.Locator"/>, or null without one.
    /// </summary>
    public GroundPlace? Place
    {
        get
        {
            var config = Config;
            if (AudioSource.Parse(config.Audio).Kind != AudioSourceKind.UberSdr)
            {
                return GroundPlace.FromLocator(config.Locator);
            }
            lock (_gate)
            {
                return _webSdrPlace is { } p && p.Source == config.Audio ? p.Place : null;
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
    internal bool RetunerRunsHooks => Retuner is not null
        && (Retuner.Window is null ? AudioSource.Parse(Config.Audio).IsSoundCard : AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.SoundModem);

    /// <summary>
    /// The retuner's word on where pdn-soundmodem's radio is as its receive window is taken (the
    /// window's dial) and given back or lost (the rig's, or null when it did not say): the
    /// running pipeline's band placement follows it.
    /// </summary>
    private void FollowDial(double? dialHz)
    {
        if (Pipeline is { } pipeline && pipeline.FollowDial(dialHz))
        {
            ChannelWatch.AudioCentreHz = AudioPipeline.AudioCentreFor(pipeline.DialHz);
        }
    }

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
        if (slot.HeardDirectory is not { } heardDirectory)
        {
            // The receiver does not yet know what is in today's rotation.
            return null;
        }
        // Judged against the specific directory heard this slot, not whichever is newest
        // overall: the two waveforms taking turns give the same rotation different object IDs
        // (the mode they went out on is part of the object), so a slot's own resend may not be
        // the "latest" the store has seen.
        if (heardDirectory.Entries.Any(e => !Intake.IsComplete(e.ObjectId)))
        {
            // A bulletin in it is not complete yet.
            return null;
        }
        // The ionosonde and PSK Reporter readings are new objects each slot, never listed in the
        // directory, so the check above says nothing about them. Wait for each to be heard this
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
                    var (opens, closes, slot) = ListeningWindow.Next(at, Followed);
                    // Issue #49: a web SDR's window that already ended early closed then, not
                    // at its usual time; "after" (run by the hooks loop, not this audio one)
                    // should not wait for a close that already happened.
                    if (EarlyEndFinished is { } early && early.Slot == slot && early.At < closes)
                    {
                        closes = early.At;
                    }
                    return new HookWindow(opens, closes, slot);
                case AudioSourceKind.Alsa or AudioSourceKind.WindowsSoundCard or AudioSourceKind.SoundModem or AudioSourceKind.SdrConnect:
                    // pdn-soundmodem's radio and SDRconnect's, like a sound card's, are heard all the
                    // time. A slot heard in full closes its window then (WatchSlotWindowsAsync), the
                    // same as a web SDR's, so "after" runs then too.
                    if (ListeningWindow.SoundCard(at, Followed) is not { } window)
                    {
                        return null;
                    }
                    var heardBy = window.Closes;
                    if (EarlyEndFinished is { } done && done.Slot == window.Slot && done.At < heardBy)
                    {
                        heardBy = done.At;
                    }
                    return new HookWindow(window.Opens, heardBy, window.Slot);
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

    /// <summary>The most stations, besides GB7RDG, whose slots this receiver listens at.</summary>
    public const int MostFollowedStations = 6;

    /// <summary>The fewest minutes between a followed station's slots: at most one slot an hour.</summary>
    public const int MinutesBetweenFollowedSlots = 60;

    /// <summary>Whether this receiver moves its dial for a station's slots: a rig or pdn-soundmodem's receive window (<see cref="Retuner"/>), or a web SDR, which is tuned for each window.</summary>
    internal bool CanRetune => Retuner is not null || AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.UberSdr;

    private readonly Dictionary<string, long> _heardAtHz = new(StringComparer.Ordinal);

    /// <summary>Where a station whose directory gives no frequency was heard: the signal this receiver was listening for when it decoded, else the one the dial setting is written for.</summary>
    private long WhereHeard(string station)
    {
        lock (_gate)
        {
            return _heardAtHz.TryGetValue(station, out long hz) ? hz : (long)Config.CentreHz;
        }
    }

    /// <summary>Whether, and on which dial, this receiver can hear a station where it says it is (issue #179): see <see cref="StationReaching"/>.</summary>
    public StationReach Reach(StationOnAir station) =>
        StationReaching.Judge(station, Config.AudioCentreHz, Pipeline is { } pipeline ? pipeline.DialHz : Config.DialHz, CanRetune,
            AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.UberSdr ? null : Config.LnbHz,
            AudioSource.Parse(Config.Audio).Kind == AudioSourceKind.UberSdr);

    /// <summary>
    /// Every station this receiver knows the slots of (issues #178 and #144), GB7RDG first:
    /// GB7RDG by its directory or the config; any other by the timetable its own signed directory
    /// gives, or else the newest advert for it. Each station's frequency, sideband and place come
    /// from its own directory, else an advert (issue #179); GB7RDG alone falls back to 7.0538 MHz
    /// USB at IO91lk, and any other to the frequency the dial setting is written for. Each says
    /// whether it is followed, and if not, why.
    /// </summary>
    public IReadOnlyList<ListenedStation> Listening
    {
        get
        {
            var skip = new HashSet<string>(Config.SkipStations ?? [], StringComparer.OrdinalIgnoreCase);
            var rdg = Intake.HeardDetails(ReceiverStore.BuiltInStation);
            var rdgBuiltIn = ReceiverStore.BuiltInDetails;
            var rdgOnAir = new StationOnAir(ReceiverStore.BuiltInStation, rdg?.FrequencyHz ?? rdgBuiltIn.FrequencyHz!.Value, rdg?.Sideband ?? rdgBuiltIn.Sideband!.Value,
                rdg?.Locator ?? rdgBuiltIn.Locator, rdg?.FrequencyHz is null ? "assumed" : "its own directory");
            // GB7RDG is always listened at, as before; if this receiver cannot hear where it says it is, the log says so (see RefreshFollowed).
            var rdgReach = Reach(rdgOnAir);
            var list = new List<ListenedStation>
            {
                new(ReceiverStore.BuiltInStation, Schedule.Timetable, rdgOnAir.FrequencyHz, rdgOnAir.Sideband,
                    ScheduleFromDirectory ? "its own directory" : "the config file", true, null, rdgOnAir.Locator, rdgReach.Heard ? rdgReach.DialHz : Config.DialHz, rdgReach.How, rdgReach.IfHz),
            };
            var adverts = Intake.Adverts();
            var heard = Intake.StationDirectories().Select(d => d.Station);
            var pins = Intake.Pins();
            var candidates = new List<(ListenedStation Station, bool Direct, DateTimeOffset Seen)>();
            foreach (string station in adverts.Select(a => a.Callsign).Concat(heard).Distinct(StringComparer.Ordinal).Where(c => c != ReceiverStore.BuiltInStation).Order(StringComparer.Ordinal))
            {
                var own = Intake.HeardTimetable(station);
                var details = Intake.HeardDetails(station);
                var advertised = adverts.Where(a => a.Callsign == station).ToList();
                // What its own directory says about where it is on the air comes first. Otherwise
                // the advert this receiver can hear, if one is, else the first, to show why not.
                // A directory that says nothing of it (a head end from before v0.20.0) and no advert:
                // the frequency the dial setting is written for, as before.
                StationAdvert? advert = null;
                bool heardHere = heard.Contains(station);
                // A station whose own signed directory is heard here is where it was heard (the
                // frequency this receiver was listening on when that directory decoded) whatever
                // an advert says; adverts only place stations known from adverts alone.
                if (details?.FrequencyHz is null && !heardHere)
                {
                    // Its own directory heard here, saying nothing of where it is, and adverts that put it
                    // out of reach: it is plainly within reach (an advert is only a hint), so it is
                    // taken to be where the dial setting is written for, as before issue #179.
                    advert = advertised.FirstOrDefault(a => Reach(new StationOnAir(station, a.FrequencyHz, a.Sideband, a.Locator, "an advert")).Heard) ?? advertised.FirstOrDefault();
                }
                if ((own ?? advert?.Timetable ?? advertised.FirstOrDefault()?.Timetable) is not { } timetable)
                {
                    continue;
                }
                long hz = details?.FrequencyHz ?? advert?.FrequencyHz ?? WhereHeard(station);
                var sideband = details?.Sideband ?? (details?.FrequencyHz is not null ? Sideband.Usb : advert?.Sideband ?? Sideband.Usb);
                string? locator = details?.Locator ?? (heardHere ? null : advert?.Locator ?? advertised.Select(a => a.Locator).FirstOrDefault(l => l is not null));
                string frequencyFrom = details?.FrequencyHz is not null ? "its own directory" : advert is not null ? "an advert" : heardHere ? "where its directory was heard" : "assumed";
                var reach = Reach(new StationOnAir(station, hz, sideband, locator, frequencyFrom));
                string? why = skip.Contains(station) ? "you chose not to listen at its slots"
                    : !reach.Heard ? reach.Why
                    : timetable.EveryMinutes < MinutesBetweenFollowedSlots ? $"its slots are every {timetable.EveryMinutes} minutes, and a station is only listened at for at most one slot an hour"
                    : null;
                // Heard directly first, by when its key was pinned (keys given by hand first);
                // then advertised only, by the date it was first advertised; then by callsign.
                DateTimeOffset seen = own is not null
                    ? pins.Where(p => p.Station == station).Select(p => p.PinnedAt ?? DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.MaxValue).Min()
                    : Intake.AdvertFirstSeen(station) is { } first ? new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MaxValue;
                candidates.Add((new ListenedStation(station, timetable, hz, sideband, own is not null ? "its own directory" : "an advert", why is null, why, locator, reach.Heard ? reach.DialHz : null, reach.Heard ? reach.How : null, reach.Heard ? reach.IfHz : null), own is not null, seen));
            }
            int taken = 0;
            foreach (var (station, _, _) in candidates.OrderBy(c => !c.Direct).ThenBy(c => c.Seen).ThenBy(c => c.Station.Station, StringComparer.Ordinal))
            {
                if (station.Followed && ++taken > MostFollowedStations)
                {
                    list.Add(station with { Followed = false, NotFollowedBecause = $"this receiver listens at the slots of at most {MostFollowedStations} stations besides GB7RDG, and others were heard of first" });
                }
                else
                {
                    list.Add(station);
                }
            }
            foreach (var (callsign, by) in Intake.AdvertsBeyondCap())
            {
                if (list.All(l => l.Station != callsign))
                {
                    list.Add(new ListenedStation(callsign, new SlotTimetable(TimeOnly.MinValue, 60), 0, Sideband.Usb, "an advert", false,
                        $"it was advertised{(by is null ? "" : $" by {by}")} past the first {BroadcastDirectory.MostAdverts} adverts, the most a directory is read for"));
                }
            }
            // A dial that cannot hear GB7RDG (pdn-soundmodem's radio on the LNB's IF) while it does
            // hear another station: GB7RDG is not followed, rather than followed at a nonsense audio position.
            if (!rdgReach.Heard && (Pipeline?.HomeLnb is not null || list.Any(l => l.Station != ReceiverStore.BuiltInStation && l.Followed)))
            {
                list[0] = list[0] with { Followed = false, NotFollowedBecause = rdgReach.Why };
            }
            return list;
        }
    }

    /// <summary>
    /// Where the station whose slot starts at <paramref name="slot"/> is on the air and the dial to
    /// hear it on, or null when no followed station has a slot then (issue #179).
    /// </summary>
    internal ListenedStation? StationAtSlot(DateTimeOffset slot) =>
        Followed.StationAt(slot) is { } station ? Listening.FirstOrDefault(l => l.Station == station && l.Followed) : null;

    /// <summary>
    /// Points the audio at the station whose slot starts at <paramref name="slot"/>: the pipeline
    /// listens for that station's signal from now on. Returns the dial to hear it on, in Hz, when
    /// it is heard by retuning (null: the dial is left where it is, or no station has the slot).
    /// </summary>
    internal double? PointAtSlot(DateTimeOffset slot, AudioPipeline? pipelineForTest = null)
    {
        if (StationAtSlot(slot) is not { } station)
        {
            return null;
        }
        if ((pipelineForTest ?? Pipeline) is { } pipeline)
        {
            pipeline.SetSignal(station.HeardAtHz, station.IfHz is null ? 0 : station.FrequencyHz - station.IfHz.Value);
            // A rig retuned for the slot moves the radio under a sound card's audio: the pipeline
            // must look for the signal where that dial puts it. (pdn-soundmodem's window tells it
            // through FollowDial; a web SDR is opened on the dial.)
            if (station.How == HowHeard.Retune && Retuner is { Window: null } && pipeline.Source.Kind != AudioSourceKind.SoundModem)
            {
                pipeline.SetDial(station.DialHz!.Value);
            }
            // The channel measurement and the page's marks look for the signal where this dial puts it.
            ChannelWatch.AudioCentreHz = AudioPipeline.AudioCentreFor(station.How == HowHeard.Retune ? station.DialHz!.Value : pipeline.DialHz, station.HeardAtHz);
        }
        return station.How == HowHeard.Retune ? station.DialHz : null;
    }

    /// <summary>
    /// Returns the audio to the config's dial and signal once a station's window has closed
    /// (issue #187), so a tone heard between windows is looked for where the config says. A
    /// window of pdn-soundmodem gives its own dial back through <see cref="FollowDial"/>.
    /// </summary>
    internal void ReturnToConfig(AudioPipeline? pipelineForTest = null)
    {
        if ((pipelineForTest ?? Pipeline) is not { } pipeline)
        {
            return;
        }
        if (pipeline.HomeLnb is { } home)
        {
            // Opened on an LNB's IF: between slots it stays on the downlink, not on GB7RDG's HF signal.
            pipeline.SetSignal(home.IfHz, home.LnbHz);
            ChannelWatch.AudioCentreHz = AudioPipeline.AudioCentreFor(pipeline.DialHz, home.IfHz);
            return;
        }
        pipeline.SetSignal(OnAir.DefaultSignalHz);
        if (Retuner is { Window: null } && pipeline.Source.Kind != AudioSourceKind.SoundModem)
        {
            pipeline.SetDial(Config.DialHz);
        }
        ChannelWatch.AudioCentreHz = AudioPipeline.AudioCentreFor(pipeline.DialHz);
    }

    /// <summary>
    /// The slots of every station this receiver follows (issue #144): GB7RDG's
    /// (<see cref="Schedule"/>), then every other station in <see cref="Listening"/> that is
    /// followed. With none, GB7RDG's alone, exactly as before.
    /// </summary>
    public FollowedSlots Followed
    {
        get
        {
            var followed = Listening.Where(l => l.Followed).ToList();
            if (followed.Count == 0)
            {
                // Only the LNB's downlink is heard and no station on it is known yet: GB7RDG's
                // slots stand in as the placeholder timetable, and nothing is pointed at them.
                return FollowedSlots.One(Schedule);
            }
            return followed.Count == 1 && followed[0].Station == ReceiverStore.BuiltInStation
                ? FollowedSlots.One(Schedule)
                : new FollowedSlots([.. followed.Select(l => (l.Station, l.Station == ReceiverStore.BuiltInStation ? Schedule : SlotSchedule.From(l.Timetable)))]);
        }
    }

    /// <summary>
    /// A directory was heard: the slot now on keeps it if it is that slot's station's, and the
    /// slot tracker takes up any change it made to the stations followed (a new station, an
    /// advert, or a station moving its slots), so frames and channel bursts land in the right slot.
    /// </summary>
    private void OnDirectoryHeard(BroadcastDirectory directory)
    {
        // A station whose directory says nothing of its frequency is where this receiver was
        // listening when the directory decoded (issue #179); kept for this run.
        if (Intake.StationOf(directory) is { } heardStation && Intake.HeardDetails(heardStation)?.FrequencyHz is null)
        {
            lock (_gate)
            {
                _heardAtHz[heardStation] = (long)(Pipeline is { } p ? p.SignalHz + p.LnbOffsetHz : Config.CentreHz);
            }
        }
        Slots.OnDirectory(directory, Intake.StationOf(directory));
        RefreshFollowed();
    }

    /// <summary>Gives the slot tracker the stations followed now, and says so, if they changed.</summary>
    internal void RefreshFollowed()
    {
        if (Slots.Schedule is not { } tracked)
        {
            return;
        }
        var followed = Followed;
        if (followed.Describe() != tracked.Describe())
        {
            _log($"slots: now following {followed.Describe()}");
            foreach (var skipped in Listening.Where(l => !l.Followed))
            {
                _log($"slots: not following {skipped.Station}: {skipped.NotFollowedBecause}");
            }
            Slots.Schedule = followed;
            WakeListenNow();
        }
    }

    /// <summary>Whether <see cref="Schedule"/> comes from GB7RDG's directory rather than the config file.</summary>
    public bool ScheduleFromDirectory => Intake.HeardSchedule is not null;

    private void OnScheduleHeard(Packet.Mailcast.SlotTimetable heard)
    {
        var schedule = SlotSchedule.From(heard);
        _log($"slots: GB7RDG's directory gives its slots as {schedule.Describe()}; using that instead of the config file's");
        if (Slots.Schedule is not null)
        {
            Slots.Schedule = Followed;
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

    /// <summary>The config file this receiver was started from, for the start-up lines; null when there is none (tests).</summary>
    public ConfigFile? ConfigFile { get; init; }

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

    /// <summary>
    /// The USB dial the audio is heard on, in Hz: the running pipeline's once its dial is settled
    /// (pdn-soundmodem's own, when it says one), else the last pdn-soundmodem said for this same
    /// <c>audio</c> while it is reconnecting, else the config's.
    /// </summary>
    public double DialHz => Pipeline is { DialSettled: true } pipeline ? pipeline.DialHz : SoundModemDialSaid ?? Config.DialHz;

    /// <summary>Whether <see cref="DialHz"/> is pdn-soundmodem's, said now or the last time while it reconnects.</summary>
    public bool DialFromSoundModem => Pipeline is { DialSettled: true } pipeline ? pipeline.DialFromSoundModem : SoundModemDialSaid is not null;

    /// <summary>Where the signal's centre is in the audio the page's spectrogram shows, Hz; see <see cref="AudioPipeline.AudioCentreHz"/>.</summary>
    public double AudioCentreHz => Pipeline is { DialSettled: true } pipeline ? pipeline.AudioCentreHz : AudioPipeline.AudioCentreFor(DialHz);

    private (string Audio, double DialHz)? _soundModemDial;

    /// <summary>The dial pdn-soundmodem last said, while <c>audio</c> is still the source it said it for.</summary>
    private double? SoundModemDialSaid
    {
        get
        {
            lock (_gate)
            {
                return _soundModemDial is { } said && string.Equals(said.Audio, _config.Audio, StringComparison.Ordinal) ? said.DialHz : null;
            }
        }
    }

    /// <summary>A sentence on what the audio is doing, for the page and the log.</summary>
    public string AudioState => Audio.Phase == AudioPhase.Listening && BetweenSlots is { } next
        ? $"{Audio.Words}; between slots until {next.Opens:HH:mm} UTC, so it only decodes while a status page shows the spectrogram"
        : Audio.Words;

    /// <summary>
    /// For a sound card between slots: when decoding starts again, and for which slot; null
    /// while a slot's window is open, and for any other source. See <see cref="AudioPipeline.BetweenSlots"/>.
    /// </summary>
    public (DateTimeOffset Opens, DateTimeOffset Slot)? BetweenSlots
    {
        get { lock (_gate) { return _betweenSlots; } }
        private set { lock (_gate) { _betweenSlots = value; } }
    }

    private (DateTimeOffset Opens, DateTimeOffset Slot)? _betweenSlots;

    /// <summary>
    /// For a source heard all the time (a sound card, pdn-soundmodem, SDRconnect): ends the
    /// window of a slot heard in full (<see cref="EarlyEndReason"/>), so "after" runs then, and
    /// for a sound card works out whether it is between slots, and tells the pipeline. Called
    /// every <see cref="EarlyEndPoll"/>; a web SDR and a recording are left alone.
    /// </summary>
    internal void CheckSlotWindow()
    {
        var source = AudioSource.Parse(Config.Audio);
        var now = _time.GetUtcNow();
        (DateTimeOffset Opens, DateTimeOffset Slot)? between = null;
        bool inWindow = false;
        bool listensBySlot = source.Kind is AudioSourceKind.Alsa or AudioSourceKind.WindowsSoundCard or AudioSourceKind.SoundModem or AudioSourceKind.SdrConnect;
        if (source.Kind is AudioSourceKind.Alsa or AudioSourceKind.WindowsSoundCard or AudioSourceKind.SoundModem or AudioSourceKind.SdrConnect
            && ListeningWindow.SoundCard(now, Followed) is { } window)
        {
            var (opens, closes, slot) = window;
            if (now >= opens && slot != EarlyEndFinished?.Slot && EarlyEndReason(slot) is { } why)
            {
                if (!RetunerRunsHooks)
                {
                    // With a rig, the retuner says it, as it gives the radio back.
                    _log($"audio: the {slot.UtcDateTime:HH:mm} UTC slot is over early: {why}");
                }
                EarlyEndFinished = (slot, now);
            }
            if (slot == EarlyEndFinished?.Slot && ListeningWindow.SoundCard(closes, Followed) is { } next)
            {
                // Heard in full: the rest of its window is between slots, the same as after it.
                (opens, _, slot) = next;
            }
            if (source.IsSoundCard && now < opens)
            {
                between = (opens, slot);
            }
            if (now >= opens)
            {
                // The window of this slot's station is open: listen for that station's signal.
                PointAtSlot(slot);
                inWindow = true;
            }
        }
        if (listensBySlot && !inWindow)
        {
            // No station's window is open: back to the config's dial and signal.
            ReturnToConfig();
        }

        var was = BetweenSlots;
        BetweenSlots = between;
        if (Pipeline is { } pipeline)
        {
            pipeline.BetweenSlots = between is not null;
        }
        if (between is { } b && b != was)
        {
            _log($"audio: between slots until {b.Opens.UtcDateTime:HH:mm} UTC, ready for the {b.Slot.UtcDateTime:HH:mm} UTC slot; "
                + "the sound card stays open, but it only decodes while a status page shows the spectrogram");
        }
        else if (between is null && was is { } w)
        {
            _log($"audio: decoding for the {w.Slot.UtcDateTime:HH:mm} UTC slot");
        }
    }

    /// <summary>Raised after each look <see cref="WatchSlotWindowsAsync"/> takes, for tests.</summary>
    internal event Action? SlotWindowChecked;

    /// <summary>Runs <see cref="CheckSlotWindow"/> every <see cref="EarlyEndPoll"/> until <paramref name="cancellation"/> is cancelled. Catches and logs its own problems.</summary>
    private async Task WatchSlotWindowsAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                CheckSlotWindow();
            }
            catch (Exception e)
            {
                _log($"audio: unexpected failure checking the slot's window: {Ascii.Clean(e.Message)}");
            }
            SlotWindowChecked?.Invoke();
            try
            {
                await Task.Delay(EarlyEndPoll, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

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
        if (Followed is { OneStation: false } followed)
        {
            _log($"slots: following {followed.Describe()}");
        }
        _log(SourcesLine(Config));
        if (ConfigFile is { } file)
        {
            // Issue #154: which file, and what in it was not read, so nothing is ignored unseen.
            _log($"config: read {file.Path}");
            foreach (string key in file.UnknownKeys)
            {
                _log($"config: \"{key}\" is not a setting and is ignored");
            }
        }
        if (Config.SlotUtcWithoutEveryMinutes)
        {
            _log($"config: no \"everyMinutes\" (an older file); read as every 60 minutes, at :{Config.SlotStart.Minute:00} past each hour, the same hourly slots, so nothing needs changing");
        }
        if (Config.WebSdrSlotsPerDayIgnored)
        {
            _log("config: webSdrSlotsPerDay is no longer used and is ignored; a web SDR listens to every daylight slot that fits in its allowance");
        }
        _log(ConfigFile.PartsLine(Config));
        int pending = Intake.Pending().Count;
        if (pending > 0)
        {
            _log($"store: {pending} rebuilt bulletin{(pending == 1 ? "" : "s")} waiting for the BBS from before");
        }

        if (Retuner is null)
        {
            Retuner.SayIfLeftOver(Config.StateDirectory, _log);
        }
        if (Config.Retune && AudioSource.Parse(Config.Audio).Kind != AudioSourceKind.SoundModem)
        {
            _log("config: \"retune\" is for \"audio\": \"soundmodem\" only, where pdn-soundmodem's receive window retunes its radio for each slot; it does nothing with this source"
                + (Config.Rig is not null ? " (\"rig\" retunes the radio as before)" : ""));
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
        // Issue #133: the summary line as each slot ends. Never fails, so not one the others stop for.
        Task slotEnds = WatchSlotEndsAsync(stopOthers.Token);
        // A slot heard in full ends early, and a sound card rests between slots. Never fails either.
        Task slotWindows = WatchSlotWindowsAsync(stopOthers.Token);
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
            await slotEnds.ConfigureAwait(false);
            await slotWindows.ConfigureAwait(false);
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
        Slots.Finish();
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

    /// <summary>
    /// Issue #133: every <see cref="EarlyEndPoll"/>, writes the summary line of the slot tracked
    /// once its window is over (<see cref="SlotTracker.EndIfOver"/>), until
    /// <paramref name="cancellation"/> is cancelled. Catches and logs its own problems.
    /// </summary>
    private async Task WatchSlotEndsAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                // A slot whose window is open now gets a line even if nothing is heard in it.
                var now = _time.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                if (Slots.Schedule is not null && AudioSource.Parse(Config.Audio).Kind != AudioSourceKind.Wav
                    && ListenedOn(today.AddDays(-1)).Concat(ListenedOn(today)).LastOrDefault(s => s <= now && now < s + ReceiverConfig.WebSdrAfter) is { } open
                    && open != default && open != LastEarlyEnd?.Slot)
                {
                    Slots.OnListening(open);
                }
                Slots.EndIfOver(LastEarlyEnd?.Slot);
            }
            catch (Exception e)
            {
                _log($"slot: unexpected failure writing a slot's summary: {Ascii.Clean(e.Message)}");
            }
            try
            {
                await Task.Delay(EarlyEndPoll, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The longest --decode waits for the channel measurement once the mail is delivered.</summary>
    internal static readonly TimeSpan DecodeMeasureWait = TimeSpan.FromMinutes(2);

    /// <summary>Whether going from <paramref name="a"/> to <paramref name="b"/> restarts the audio: its source, or when it listens.</summary>
    private static bool AudioSettingsDiffer(ReceiverConfig a, ReceiverConfig b) =>
        !string.Equals(a.Audio, b.Audio, StringComparison.Ordinal)
        || !string.Equals(a.SlotAnchor, b.SlotAnchor, StringComparison.Ordinal)
        || a.EveryMinutes != b.EveryMinutes
        || a.Daylight != b.Daylight
        || a.DialKHz != b.DialKHz;

    /// <summary>
    /// Puts a new configuration in force: the BBS settings at once, and the audio pipeline is
    /// restarted if its source changed.
    /// </summary>
    public void Reconfigure(ReceiverConfig config)
    {
        CancellationTokenSource? restart = null;
        lock (_gate)
        {
            bool audioChanged = AudioSettingsDiffer(_config, config);
            _config = config;
            Intake.Sources = config.AcceptedSources;
            // A running pipeline that carries on keeps its dial (pdn-soundmodem's, if it said one).
            ChannelWatch.AudioCentreHz = !audioChanged && _pipeline is { } running ? running.AudioCentreHz : config.AudioCentreHz;
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

    /// <summary>
    /// The waits before connecting to pdn-soundmodem again after its stream could not be opened
    /// or ended, one after another; the last repeats. Short at first, because pdn-soundmodem
    /// restarting takes a few seconds and a slot is going on meanwhile.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> SoundModemRetry =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), AudioRetry];

    /// <summary>A pdn-soundmodem stream that ran this long before it ended starts the waits in <see cref="SoundModemRetry"/> again from the shortest.</summary>
    internal static readonly TimeSpan SoundModemSteady = TimeSpan.FromMinutes(1);

    /// <summary>The waits after a web receiver refuses us for now; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> RefusedBackoff =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30)];

    /// <summary>
    /// How often the same failure, attempt after attempt, is said again in the journal: SDRconnect
    /// closed on its PC for a quarter of an hour was one identical line every 45 s. Between, the
    /// attempts are counted, and the status page says the failure as it stands throughout.
    /// </summary>
    public static readonly TimeSpan RepeatedFailureEvery = TimeSpan.FromMinutes(10);

    private async Task SuperviseAudioAsync(CancellationToken cancellation)
    {
        int refusals = 0;
        string? sdrConnectAnswered = null;
        string? failureSaid = null;
        DateTimeOffset failureSaidAt = default;
        int failuresUnsaid = 0;
        string? refusedFor = null;
        int soundModemFailures = 0;
        string? described = null;
        string? closedSaid = null;
        // Why the last attempt failed, for the page while the next is opening: the same audio
        // setting only, since a changed one is a fresh start.
        (string Audio, string Why)? lastFailure = null;
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
                var schedule = Followed;
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
                    // A failure in a window gone by is not the last try before the next opening.
                    lastFailure = null;
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
                // mid-session is still never overlapped. Issue #80: NextWebSdrWindowOpens, not
                // the raw schedule, so a session granted during what would have been the rest of
                // a window that ended early is not mistaken for overlapping that same window
                // (already over) and cut off at once.
                Func<DateTimeOffset, DateTimeOffset>? recomputeCloses = !listenNow ? null
                    : now => ListenNow.ActiveUntil(now, NextWebSdrWindowOpens(now)) ?? now;
                _ = CloseAtAsync(opens, closes, slot, recomputeCloses, closeAt, restart.Token);
            }

            using var window = closeAt;
            using var running = window is null ? null : CancellationTokenSource.CreateLinkedTokenSource(restart.Token, window.Token);
            CancellationToken token = running?.Token ?? restart.Token;
            var pipeline = PipelineFactory?.Invoke(source) ?? CreatePipeline(source, config, closingSlot);
            pipeline.BetweenSlots = source.IsSoundCard && BetweenSlots is not null;
            pipeline.SdrConnectAnsweredBefore = source.Kind == AudioSourceKind.SdrConnect
                && string.Equals(sdrConnectAnswered, config.Audio, StringComparison.OrdinalIgnoreCase);
            Slots.Schedule = source.Kind == AudioSourceKind.Wav ? null : Followed;
            lock (_gate)
            {
                _pipeline = pipeline;
            }

            string? why = null;
            bool refused = false;
            DateTimeOffset? listening = null;
            string? before = lastFailure is { } failure && string.Equals(failure.Audio, config.Audio, StringComparison.OrdinalIgnoreCase) ? failure.Why : null;
            void ShowDrop() => ShowReconnecting(pipeline);
            try
            {
                // pdn-soundmodem says its radio's dial when its stream opens, and everything
                // attached (the page's spectrogram, its marks) has to see the dial it really is.
                bool openFirst = source.Kind == AudioSourceKind.SoundModem;
                if (openFirst)
                {
                    Audio = new($"connecting to pdn-soundmodem on {SoundModemAudioInput.Describe(source.SoundModemPort)}", AudioPhase.Opening, Problem: before);
                    await pipeline.OpenAsync(token).ConfigureAwait(false);
                }
                Attach(pipeline);
                PipelineCreated?.Invoke(pipeline);
                if (!openFirst)
                {
                    Audio = new($"opening {pipeline.Source}", AudioPhase.Opening, Problem: before);
                }
                await pipeline.StartAsync(token).ConfigureAwait(false);
                ChannelWatch.AudioCentreHz = pipeline.AudioCentreHz;
                lock (_gate)
                {
                    _soundModemDial = pipeline.DialFromSoundModem ? (config.Audio, pipeline.DialHz) : null;
                }
                listening = _time.GetUtcNow();
                failureSaid = null;
                failuresUnsaid = 0;
                if (source.Kind == AudioSourceKind.SdrConnect)
                {
                    sdrConnectAnswered = config.Audio;
                }
                if (pipeline.WebSdrDescription is { } about)
                {
                    lock (_gate)
                    {
                        _webSdrAbout = (config.Audio, about);
                    }
                }
                lock (_gate)
                {
                    Audio = new($"listening to {pipeline.Source}", AudioPhase.Listening);
                }
                lastFailure = null;
                // SDRconnect or a web SDR may drop and be reconnected to by its own client
                // without the pipeline ending: the page says so meanwhile.
                pipeline.ReconnectingChanged += ShowDrop;
                ShowReconnecting(pipeline);
                refusals = 0;
                PipelineStarted?.Invoke(pipeline);
                if (source.Kind == AudioSourceKind.SoundModem)
                {
                    // pdn-soundmodem may have restarted mid-slot, which puts its rig back: the
                    // retuner asks for its receive window again now, for what is left of the slot.
                    // If it still holds the window, this pipeline is placed by the window's dial.
                    Retuner?.SourceReconnected();
                }
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
                pipeline.ReconnectingChanged -= ShowDrop;
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
            if (listening is { } since && _time.GetUtcNow() - since >= SoundModemSteady)
            {
                soundModemFailures = 0;
            }
            // A dial, rate or version pdn-soundmodem gives that this receiver cannot use stays so
            // until someone changes it: asked about as seldom as a web SDR that refused us.
            bool soundModem = pipeline.Source.Kind == AudioSourceKind.SoundModem;
            if (refused && soundModem && !string.Equals(why, refusedFor, StringComparison.Ordinal))
            {
                // Something else is wrong now (pdn-soundmodem upgraded, then its dial refused, say):
                // the waits start again from the shortest.
                refusals = 0;
            }
            refusedFor = refused && soundModem ? why : null;
            TimeSpan wait = refused ? RefusedBackoff[Math.Min(refusals++, RefusedBackoff.Count - 1)]
                : soundModem ? SoundModemRetry[Math.Min(soundModemFailures++, SoundModemRetry.Count - 1)]
                : AudioRetry;
            string line = $"audio: {why}. Trying again in {(wait.TotalMinutes >= 1 ? $"{wait.TotalMinutes:F0} min" : $"{wait.TotalSeconds:F0} s")}.";
            DateTimeOffset now = _time.GetUtcNow();
            if (line == failureSaid && now - failureSaidAt < RepeatedFailureEvery)
            {
                failuresUnsaid++;
            }
            else
            {
                _log(failuresUnsaid == 0 ? line
                    : $"{line} ({failuresUnsaid} more failed attempt{(failuresUnsaid == 1 ? "" : "s")} since the last line)");
                failureSaid = line;
                failureSaidAt = now;
                failuresUnsaid = 0;
            }
            // For pdn-soundmodem a new "audio" setting ends the wait at once, long or short; so
            // it does for SDRconnect, whose address is typed in and so often corrected. Armed
            // before the failure is announced, so a correction made the moment it is seen (the
            // page shows it, then the address is fixed) finds a wait to end, and checked against
            // the settings this attempt started with, so one saved while it was failing (when
            // there was no wait yet to end) is not lost either: either way, no 30 s for nothing.
            bool endedBySetting = soundModem || pipeline.Source.Kind == AudioSourceKind.SdrConnect;
            bool alreadyChanged = false;
            if (endedBySetting)
            {
                lock (_gate)
                {
                    _audioRestart = restart;
                    alreadyChanged = AudioSettingsDiffer(config, _config);
                }
            }
            if (alreadyChanged)
            {
                restart.Cancel();
            }
            lastFailure = (config.Audio, why);
            Audio = new($"{why}; trying again shortly", AudioPhase.Failed, Problem: why, RetryAt: _time.GetUtcNow() + wait, Lasting: refused && soundModem);
            await DelayAsync(wait, endedBySetting ? restart.Token : cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                _audioRestart = null;
            }
        }
    }

    /// <summary>
    /// Says on the page whether <paramref name="pipeline"/>'s source has dropped and is being
    /// reconnected to by its own client (SDRconnect, a web SDR), or is back: only while it is the
    /// pipeline listening, so nothing said late overwrites what the supervisor has said since.
    /// </summary>
    private void ShowReconnecting(AudioPipeline pipeline)
    {
        lock (_gate)
        {
            if (_pipeline != pipeline || Audio.Phase is not (AudioPhase.Listening or AudioPhase.Reconnecting))
            {
                return;
            }
            if (pipeline.Reconnecting is not { } drop)
            {
                if (Audio.Phase == AudioPhase.Reconnecting)
                {
                    Audio = new($"listening to {pipeline.Source}", AudioPhase.Listening);
                    _log($"audio: {pipeline.Source} is back; listening again");
                }
                return;
            }
            if (Audio.Phase != AudioPhase.Reconnecting || Audio.Words != drop.Words)
            {
                // Its client has already said it in the journal.
                Audio = new(drop.Words, AudioPhase.Reconnecting, Problem: drop.Reason);
            }
        }
    }

    /// <summary>The longest any wait on the wall clock goes before looking at it again.</summary>
    public static readonly TimeSpan ClockCheck = TimeSpan.FromSeconds(60);

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    /// <summary>How often a web SDR's open window is checked for ending early (issue #49).</summary>
    internal static readonly TimeSpan EarlyEndPoll = TimeSpan.FromSeconds(15);

    private (DateTimeOffset Slot, DateTimeOffset At)? _earlyEndFinished;

    /// <summary>
    /// Issue #49: the most recent slot whose window was closed early, before its usual end, and
    /// when (a web SDR's by <see cref="CloseAtAsync"/>, any source heard all the time's by
    /// <see cref="CheckSlotWindow"/>); it is not reopened for the rest of that window, the same as one that ran to
    /// its usual close, and <see cref="HookWindowAt"/> says its window closed then, not at its
    /// usual time, so "after" does not wait for that either.
    /// </summary>
    private (DateTimeOffset Slot, DateTimeOffset At)? EarlyEndFinished
    {
        get { lock (_gate) { return _earlyEndFinished; } }
        set { lock (_gate) { _earlyEndFinished = value; } }
    }

    /// <summary>
    /// Issue #80: when the web SDR's next window really opens, from <paramref name="now"/>: the
    /// schedule's own next window, unless that is the one just closed early
    /// (<see cref="EarlyEndFinished"/>), which is not reopened for the rest of it, the same as
    /// <see cref="SuperviseAudioAsync"/> treats it for the audio loop itself. Without this, a
    /// "Listen now" request made during what would have been the rest of that window sees it as
    /// still open by the clock (<see cref="ListeningWindow.Next(DateTimeOffset,SlotSchedule)"/>
    /// alone does not know it ended early), and is refused as if a slot's window were about to
    /// open, which is not true.
    /// </summary>
    internal DateTimeOffset NextWebSdrWindowOpens(DateTimeOffset now)
    {
        var followed = Followed;
        var (opens, closes, slot) = ListeningWindow.Next(now, followed);
        if (slot == EarlyEndFinished?.Slot)
        {
            (opens, _, _) = ListeningWindow.Next(closes, followed);
        }
        return opens;
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
    internal AudioPipeline CreatePipeline(AudioSource source, ReceiverConfig config, DateTimeOffset? slot = null)
    {
        // A web SDR is opened for one slot's window: tuned to that slot's station (issue #179).
        var station = slot is { } s ? StationAtSlot(s) : null;
        var pipeline = AudioPipeline.Create(source, station?.How == HowHeard.Retune ? station.DialHz!.Value : config.DialHz, _log, _time, config.Web.Port,
            retunedByWindow: Retuner?.Window is not null, signalHz: station?.HeardAtHz ?? OnAir.DefaultSignalHz);
        pipeline.AlternateSignals = LnbSignals(source, config);
        pipeline.LnbHz = source.Kind == AudioSourceKind.UberSdr ? null : config.LnbHz;
        return pipeline;
    }

    /// <summary>
    /// Behind an LNB (<see cref="ReceiverConfig.LnbHz"/>), the intermediate frequency and
    /// oscillator of every station above HF this receiver knows of: where pdn-soundmodem's radio
    /// may already be dialled when it opens, before any slot has pointed the pipeline at a station.
    /// Empty without the setting, and for a web SDR.
    /// </summary>
    private List<(double IfHz, double LnbHz)> LnbSignals(AudioSource source, ReceiverConfig config)
    {
        var found = new List<(double, double)>();
        if (config.LnbHz is not { } lo || source.Kind == AudioSourceKind.UberSdr)
        {
            return found;
        }
        var stations = Intake.Adverts().Select(a => a.Callsign).Concat(Intake.StationDirectories().Select(d => d.Station)).Distinct(StringComparer.Ordinal);
        foreach (string station in stations)
        {
            long? hz = Intake.HeardDetails(station)?.FrequencyHz ?? Intake.Adverts().FirstOrDefault(a => a.Callsign == station)?.FrequencyHz;
            if (hz is { } f && f > StationReaching.HfMaxHz && StationReaching.LnbIf(f, lo) is { } ifHz && !found.Contains((ifHz, lo)))
            {
                found.Add((ifHz, lo));
            }
        }
        return found;
    }

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

    /// <summary>
    /// The source (SDRconnect, a web SDR) has dropped and its own client is reconnecting to it;
    /// <see cref="AudioCondition.Problem"/> says why, if it said. Back to <see cref="Listening"/>
    /// once its audio comes again; after five minutes of nothing the source is opened afresh.
    /// </summary>
    Reconnecting,

    /// <summary>The source could not be opened or stopped; it is tried again at <see cref="AudioCondition.RetryAt"/>.</summary>
    Failed,

    /// <summary>A recording has played to its end.</summary>
    Ended,
}

/// <summary>
/// What the audio is doing: <paramref name="Words"/> for the log and the page's summary, and for a
/// web SDR closed between slots when it opens again (<paramref name="Reopens"/>) and for which slot,
/// or for a source that failed, why (<paramref name="Problem"/>) and when it is tried again, and
/// whether it is pdn-soundmodem giving something this receiver cannot use, which waiting will not
/// mend (<paramref name="Lasting"/>), rather than pdn-soundmodem not answering.
/// </summary>
public sealed record AudioCondition(string Words, AudioPhase Phase, DateTimeOffset? Reopens = null, DateTimeOffset? ForSlot = null, string? Problem = null, DateTimeOffset? RetryAt = null, bool Lasting = false);
