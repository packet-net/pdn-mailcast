using System.Globalization;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;
using Packet.Mailcast.Propagation;

namespace Mailcast.Receiver.Feedback;

/// <summary>Where the daily report's contents come from; the receiver's are wired up in <see cref="ReceiverHost"/>.</summary>
public sealed record FeedbackSources
{
    /// <summary>The config's <c>feedback</c>, as in force now.</summary>
    public required Func<FeedbackSettings?> Settings { get; init; }

    /// <summary>GB7RDG's slots, or null when there are none to report on (a recording).</summary>
    public required Func<SlotSchedule?> Schedule { get; init; }

    /// <summary>The slots the receiver listens to on a UTC day.</summary>
    public required Func<DateOnly, IReadOnlyList<DateTimeOffset>> Listened { get; init; }

    /// <summary>The slot in progress or most recently heard.</summary>
    public required Func<SlotSummary?> LastSlot { get; init; }

    /// <summary>The newest ionosonde reading heard, if any.</summary>
    public Func<IonoReading?> Ionosphere { get; init; } = () => null;

    /// <summary>The newest PSK Reporter reading heard, if any.</summary>
    public Func<PskReading?> PskReporter { get; init; } = () => null;

    /// <summary>The channel measurement for a slot (by its scheduled start), if there is one.</summary>
    public Func<DateTimeOffset, ReportChannel?> Channel { get; init; } = _ => null;

    /// <summary>The receiver's locator, from the position a web SDR reports (6 characters, or 4 if that is all it gives); null for a sound card.</summary>
    public Func<string?> Locator { get; init; } = () => null;

    /// <summary>Where the audio comes from, for the report: <c>sc</c> or the web SDR's host.</summary>
    public required Func<string> Audio { get; init; }

    /// <summary>The receiver's version.</summary>
    public string Version { get; init; } = ReceiverHost.Version;
}

/// <summary>What the BBS said about a daily report.</summary>
public enum FeedbackAnswer
{
    /// <summary>Not sent yet.</summary>
    Pending,

    /// <summary>The BBS took it (or already had it). Final.</summary>
    Accepted,

    /// <summary>The BBS refused it. Final: that day's report is not sent again.</summary>
    Refused,

    /// <summary>The BBS asked for it later; it is offered again.</summary>
    Deferred,

    /// <summary>The session failed, or did not confirm it; it is offered again.</summary>
    Failed,
}

/// <summary>The daily report most recently made, and what became of it. Kept in feedback.json.</summary>
public sealed record SentReport
{
    /// <summary>The UTC day it is about.</summary>
    public DateOnly Day { get; init; }

    /// <summary>Its title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Its body.</summary>
    public string Body { get; init; } = "";

    /// <summary>Its BID (MID).</summary>
    public string Bid { get; init; } = "";

    /// <summary>When it was last offered to the BBS.</summary>
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>What the BBS said.</summary>
    public FeedbackAnswer Answer { get; init; }

    /// <summary>More about the answer, where there is more to say.</summary>
    public string? Detail { get; init; }

    /// <summary>When it is offered again, if it will be.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Whether the BBS has answered for good.</summary>
    public bool Final => Answer is FeedbackAnswer.Accepted or FeedbackAnswer.Refused;
}

/// <summary>
/// Sends the daily report: notes what each slot listened to brought, and about
/// <see cref="AfterLastSlot"/> after the day's last daylight slot sends a <see cref="DailyReport"/>
/// as a personal mail through the BBS. Once per UTC day at most, kept in <see cref="FileName"/>.
/// </summary>
/// <remarks>
/// <para>After a restart, the most recent day that was missed is sent, once; any older are let go.
/// A report the BBS deferred, or a session that failed, is offered again every
/// <see cref="Retry"/> until the BBS answers or the next day's report is due. A refused one is not
/// offered again.</para>
/// </remarks>
public sealed class FeedbackService
{
    /// <summary>How long after the day's last daylight slot the report goes.</summary>
    public static readonly TimeSpan AfterLastSlot = TimeSpan.FromMinutes(30);

    /// <summary>How long after a deferral or a failure the report is offered again.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);

    /// <summary>How often the clock is looked at.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    /// <summary>The state file in the state directory.</summary>
    public const string FileName = "feedback.json";

    /// <summary>How long into a slot its line is still being filled in: its listening window and a tick.</summary>
    private static readonly TimeSpan Window = ReceiverConfig.WebSdrAfter + Tick;

    private readonly FeedbackSources _sources;
    private readonly IBbsSession _bbs;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly State _state;
    private bool _said;

    /// <summary>Creates the service, reading what it kept before. Call <see cref="RunAsync"/> to start it.</summary>
    public FeedbackService(FeedbackSources sources, IBbsSession bbs, string stateDirectory, TimeProvider time, Action<string> log)
    {
        _sources = sources;
        _bbs = bbs;
        _time = time;
        _log = log;
        _path = Path.Combine(stateDirectory, FileName);
        _state = Load(_path, log);
    }

    /// <summary>The report most recently made, if any.</summary>
    public SentReport? Last
    {
        get
        {
            lock (_gate)
            {
                return _state.Last;
            }
        }
    }

    private bool Enabled => _sources.Settings() is { Enabled: true } s && FeedbackSettings.IsPlausibleCallsign(s.Callsign);

    /// <summary>A bulletin was rebuilt.</summary>
    public void NoteRebuilt() => Count(d => d.Rebuilt++);

    /// <summary>The BBS accepted <paramref name="count"/> bulletins.</summary>
    public void NoteDelivered(int count)
    {
        if (count > 0)
        {
            Count(d => d.Delivered += count);
        }
    }

    /// <summary>Something went wrong, of the kind <paramref name="code"/> (see <see cref="ReportErrors"/>).</summary>
    public void NoteError(string code) => Count(d => d.Errors[code] = d.Errors.GetValueOrDefault(code) + 1);

    private void Count(Action<DayRecord> change)
    {
        if (!Enabled)
        {
            return;
        }
        lock (_gate)
        {
            change(Day(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)));
            Save();
        }
    }

    /// <summary>What the page shows: off, or the latest report as sent, its answer, and when the next goes.</summary>
    public object View()
    {
        if (!Enabled)
        {
            return new { enabled = false };
        }
        var last = Last;
        return new
        {
            enabled = true,
            to = $"{FeedbackSettings.To}@{FeedbackSettings.At}",
            lastSent = last?.SentAt,
            lastAnswer = last is null ? null : Describe(last),
            answer = last?.Answer,
            next = Next(_time.GetUtcNow()),
            text = last is null ? null : last.Title + "\n" + last.Body.Replace("\r\n", "\n", StringComparison.Ordinal),
        };
    }

    /// <summary>When the next report goes, or the last is offered again; null when off or with no slot ahead.</summary>
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (!Enabled || _sources.Schedule() is not { } schedule)
        {
            return null;
        }
        var last = Last;
        if (last is { Final: false, RetryAt: { } retry })
        {
            return retry;
        }
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        for (int d = 0; d <= SlotTimetable.SearchDays; d++)
        {
            var day = today.AddDays(d);
            if (last is not null && day <= last.Day)
            {
                continue;
            }
            if (ReportTime(schedule, day) is { } at && at > now)
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>When a day's report goes: <see cref="AfterLastSlot"/> after its last slot; null with no slot.</summary>
    public static DateTimeOffset? ReportTime(SlotSchedule schedule, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return schedule.ActiveOn(day) is { Count: > 0 } slots ? slots[^1] + AfterLastSlot : null;
    }

    /// <summary>Looks at the clock every <see cref="Tick"/> until <paramref name="cancellation"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                _log($"feedback: unexpected failure: {Ascii.Clean(e.Message)}");
            }
            try
            {
                await Task.Delay(Tick, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>One look at the clock: note the slot in progress, and send a report that is due.</summary>
    internal async Task TickAsync(CancellationToken cancellation)
    {
        if (!Enabled || _sources.Schedule() is not { } schedule || _sources.Settings() is not { } settings)
        {
            return;
        }
        var now = _time.GetUtcNow();
        if (!_said)
        {
            _said = true;
            _log($"feedback: on. A daily report from {settings.From} goes to {FeedbackSettings.To}@{FeedbackSettings.At} through the BBS, "
                + $"{AfterLastSlot.TotalMinutes:F0} minutes after the last daylight slot"
                + (Next(now) is { } next ? $"; the next at {next:yyyy-MM-dd HH:mm} UTC" : ""));
        }
        NoteSlots(now);

        SentReport? send;
        lock (_gate)
        {
            send = Due(now, schedule, settings);
        }
        if (send is not null)
        {
            await SendAsync(send, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Notes each slot listened to whose window is open now: what the slot tracker has for it, and the verdict heard.</summary>
    private void NoteSlots(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var heard = _sources.LastSlot();
        var verdicts = new List<string>();
        if (_sources.Ionosphere() is { HasSounding: true } reading)
        {
            verdicts.Add("I" + StateLetter(reading.AsOf(now, IonoSettings.DefaultStaleAfter).State));
        }
        if (_sources.PskReporter() is { HasObservation: true } spots)
        {
            verdicts.Add("P" + StateLetter(spots.AsOf(now, PskEvaluator.StaleAfter).State));
        }
        string? verdict = verdicts.Count == 0 ? null : string.Join('+', verdicts);
        lock (_gate)
        {
            bool changed = false;
            foreach (var day in new[] { today.AddDays(-1), today })
            {
                foreach (var slot in _sources.Listened(day))
                {
                    if (now < slot || now >= slot + Window)
                    {
                        continue;
                    }
                    var record = Day(DateOnly.FromDateTime(slot.UtcDateTime));
                    var before = record.Slots.Find(s => s.Slot == slot);
                    var tone = heard?.Scheduled == slot ? heard.Tone : null;
                    var line = new SlotRecord
                    {
                        Slot = slot,
                        Waveform = heard?.Scheduled == slot ? WaveformCode(heard) : null,
                        Frames = heard?.Scheduled == slot ? heard.FramesHeard : 0,
                        SnrDb = tone is null ? null : Math.Round(tone.SnrDb, 1),
                        OffsetHz = tone is null ? null : Math.Round(tone.OffsetHz, 1),
                        Verdict = verdict ?? before?.Verdict,
                        Channel = _sources.Channel(slot) ?? before?.Channel,
                    };
                    if (before is null)
                    {
                        record.Slots.Add(line);
                        record.Slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                        changed = true;
                    }
                    else if (before != line)
                    {
                        record.Slots[record.Slots.IndexOf(before)] = line;
                        changed = true;
                    }
                }
            }
            // A slot's channel measurement can finish after its window closes: picked up once it has.
            foreach (var record in _state.Days)
            {
                for (int i = 0; i < record.Slots.Count; i++)
                {
                    if (record.Slots[i].Channel is null && _sources.Channel(record.Slots[i].Slot) is { } measured)
                    {
                        record.Slots[i] = record.Slots[i] with { Channel = measured };
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>The report to send now, if one is due: one to offer again, or the newest day not yet sent.</summary>
    private SentReport? Due(DateTimeOffset now, SlotSchedule schedule, FeedbackSettings settings)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var last = _state.Last;
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            if (last is not null && day <= last.Day)
            {
                break;
            }
            if (ReportTime(schedule, day) is not { } at || now < at
                || _state.Days.Find(d => d.Day == day) is not { Slots.Count: > 0 } record)
            {
                continue;
            }
            if (last is { Final: false })
            {
                _log($"feedback: giving up on the report for {last.Day:yyyy-MM-dd}, which the BBS never took: the one for {day:yyyy-MM-dd} is due");
            }
            var report = Build(record, settings);
            _state.Last = new SentReport
            {
                Day = day,
                Title = report.Title,
                Body = report.Body,
                Bid = Bid(settings.From, day),
                Answer = FeedbackAnswer.Pending,
            };
            Save();
            return _state.Last;
        }
        return last is { Final: false } pending && (pending.RetryAt is null || now >= pending.RetryAt) ? pending : null;
    }

    private DailyReport Build(DayRecord record, FeedbackSettings settings)
    {
        var slots = record.Slots.Select(s => new ReportSlot(
            TimeOnly.FromDateTime(s.Slot.UtcDateTime),
            s.Waveform,
            s.Frames,
            s.SnrDb,
            s.OffsetHz,
            s.Verdict is null ? [] : s.Verdict.Split('+'),
            // The measurement may have finished after the slot's window closed.
            _sources.Channel(s.Slot) ?? s.Channel)).ToList();
        string? locator = _sources.Locator() is { } l && Maidenhead.TryParse(l, out _, out _)
            ? l[..4].ToUpperInvariant() + l[4..].ToLowerInvariant()
            : null;
        var header = new ReportHeader(_sources.Version, locator, _sources.Audio(), record.Rebuilt, record.Delivered,
            new Dictionary<string, int>(record.Errors, StringComparer.Ordinal));
        return new DailyReport(settings.From, record.Day, header, slots);
    }

    /// <summary>The report's BID: the callsign and the date, G4ABC_61006, unique to the listener and the day and at most 12 characters.</summary>
    internal static string Bid(string callsign, DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture, $"{callsign}_{day.Year % 10}{day:MMdd}");

    private async Task SendAsync(SentReport report, CancellationToken cancellation)
    {
        var now = _time.GetUtcNow();
        var settings = _sources.Settings();
        string from = settings?.From ?? "";
        var mail = new Bulletin('P', from, FeedbackSettings.To, FeedbackSettings.At, report.Bid, report.Title,
            DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), [], report.Body);
        DeliveryOutcome outcome;
        string? failure;
        try
        {
            var session = await _bbs.DeliverAsync([mail], cancellation).ConfigureAwait(false);
            outcome = session.Outcomes.Count > 0 ? session.Outcomes[0] : new DeliveryOutcome(report.Bid, DeliveryVerdict.NotOffered);
            failure = session.Failure;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            outcome = new DeliveryOutcome(report.Bid, DeliveryVerdict.NotOffered);
            failure = "the session failed: " + Ascii.Clean(e.Message);
        }

        var answered = outcome.Verdict switch
        {
            DeliveryVerdict.Accepted => report with { Answer = FeedbackAnswer.Accepted, Detail = null, RetryAt = null },
            DeliveryVerdict.AlreadyHad => report with { Answer = FeedbackAnswer.Accepted, Detail = "the BBS already had it", RetryAt = null },
            DeliveryVerdict.Refused => report with { Answer = FeedbackAnswer.Refused, Detail = outcome.Detail, RetryAt = null },
            DeliveryVerdict.Deferred => report with { Answer = FeedbackAnswer.Deferred, Detail = null, RetryAt = now + Retry },
            _ => report with { Answer = FeedbackAnswer.Failed, Detail = failure ?? outcome.Detail ?? DeliveryService.Describe(outcome.Verdict), RetryAt = now + Retry },
        };
        answered = answered with { SentAt = now };
        lock (_gate)
        {
            // Only if it is still the one in hand: never put back an older day's.
            if (_state.Last is { } current && current.Day == report.Day)
            {
                _state.Last = answered;
                Save();
            }
        }
        int bytes = Bulletin.TextEncoding.GetByteCount(report.Body);
        _log($"feedback: the report for {report.Day:yyyy-MM-dd} ({Ascii.Clean(report.Title)}, {bytes} bytes) was {Describe(answered)}");
    }

    /// <summary>The answer in words, for the log and the page.</summary>
    internal static string Describe(SentReport report)
    {
        string detail = report.Detail is { } d ? ": " + Ascii.Clean(d) : "";
        string retry = report.RetryAt is { } at ? string.Create(CultureInfo.InvariantCulture, $"; trying again at {at:HH:mm} UTC") : "";
        return report.Answer switch
        {
            FeedbackAnswer.Accepted => "accepted by the BBS" + detail,
            FeedbackAnswer.Refused => "refused by the BBS" + detail + "; it is not sent again",
            FeedbackAnswer.Deferred => "deferred by the BBS" + detail + retry,
            FeedbackAnswer.Failed => "not sent" + detail + retry,
            _ => "waiting to be sent",
        };
    }

    private static string? WaveformCode(SlotSummary slot) => slot.Waveform switch
    {
        null => null,
        SlotSummary.Mixed => "WX",
        { } name when Waveform.Number(name) is { } wn => string.Create(CultureInfo.InvariantCulture, $"W{wn}"),
        _ => null,
    };

    private static char StateLetter(IonoState state) => state switch
    {
        IonoState.Good => 'G',
        IonoState.Marginal => 'M',
        IonoState.Poor => 'P',
        _ => 'U',
    };

    /// <summary>The record for <paramref name="day"/>, made if need be; records more than two days old are let go.</summary>
    private DayRecord Day(DateOnly day)
    {
        if (_state.Days.Find(d => d.Day == day) is { } found)
        {
            return found;
        }
        var record = new DayRecord { Day = day };
        _state.Days.Add(record);
        _state.Days.Sort((a, b) => a.Day.CompareTo(b.Day));
        _state.Days.RemoveAll(d => d.Day < day.AddDays(-2));
        return record;
    }

    private void Save()
    {
        try
        {
            string tmp = _path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _state, ReceiverConfig.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"feedback: cannot write {_path}: {Ascii.Clean(e.Message)}");
        }
    }

    private static State Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            return new State();
        }
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), ReceiverConfig.Json) ?? new State();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"feedback: cannot read {path} ({Ascii.Clean(e.Message)}); starting afresh");
            return new State();
        }
    }

    /// <summary>What is kept in <see cref="FileName"/>.</summary>
    internal sealed class State
    {
        public List<DayRecord> Days { get; set; } = [];

        public SentReport? Last { get; set; }
    }

    /// <summary>One UTC day's slots and counts.</summary>
    internal sealed class DayRecord
    {
        public DateOnly Day { get; set; }

        public List<SlotRecord> Slots { get; set; } = [];

        public int Rebuilt { get; set; }

        public int Delivered { get; set; }

        public SortedDictionary<string, int> Errors { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>One slot listened to.</summary>
    internal sealed record SlotRecord
    {
        public DateTimeOffset Slot { get; init; }

        public string? Waveform { get; init; }

        public int Frames { get; init; }

        public double? SnrDb { get; init; }

        public double? OffsetHz { get; init; }

        public string? Verdict { get; init; }

        public ReportChannel? Channel { get; init; }
    }
}
