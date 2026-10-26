using System.Collections.Immutable;
using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>One slot as this receiver heard it.</summary>
/// <param name="Started">When the slot began: the start of its tone, or its first frame if no tone was heard.</param>
/// <param name="Tone">The tone's measurement, if it was heard.</param>
/// <param name="FramesHeard">Frames decoded in the slot.</param>
/// <param name="LastFrame">When the newest of them arrived.</param>
/// <param name="Scheduled">The scheduled start of the slot, when the receiver knows the schedule (not for a recording).</param>
public sealed record SlotSummary(DateTimeOffset Started, ToneReport? Tone, int FramesHeard, DateTimeOffset? LastFrame, DateTimeOffset? Scheduled = null)
{
    /// <summary>Frames heard on each waveform, by the modem's autobaud (<c>ms110d-wn4</c>). Frames whose waveform it could not say are not in it.</summary>
    public ImmutableSortedDictionary<string, int> FrameCounts { get; init; } = ImmutableSortedDictionary<string, int>.Empty;

    /// <summary>
    /// Issue #144: the station whose slot this is, when the receiver follows more than one; null
    /// when it follows one (GB7RDG alone, as before) or for a recording.
    /// </summary>
    public string? Station { get; init; }

    /// <summary>The waveform GB7RDG's directory said this slot went out on, if a directory completed in it.</summary>
    public string? ListedWaveform { get; init; }

    /// <summary>
    /// Issue #49: the directory recognised while this slot was tracked (newly completed, or a
    /// repeat of one already known from a recent slot: the two waveforms taking turns give the
    /// same rotation different object IDs), so the receiver knows what is in that day's
    /// rotation. Null until one is recognised.
    /// </summary>
    public BroadcastDirectory? HeardDirectory { get; init; }

    /// <summary>
    /// Issue #49: whether the probe audio after this slot's tone (used for the channel
    /// measurement when no burst decodes) has been captured.
    /// </summary>
    public bool ProbeCaptured { get; init; }

    /// <summary>Issue #49: whether this slot's ionosonde reading has been received.</summary>
    public bool IonosphereHeard { get; init; }

    /// <summary>Issue #49: whether this slot's PSK Reporter reading has been received.</summary>
    public bool PskReporterHeard { get; init; }

    /// <summary>
    /// Issue #133: bytes decoded from the slot's frames, counting each broadcast frame's
    /// information field (the AX.25 header is not counted), new pieces and repeats alike. Not the
    /// same as the size of the bulletins rebuilt from them.
    /// </summary>
    public long BytesDecoded { get; init; }

    /// <summary>Issue #133: bulletins rebuilt while this slot was the one tracked.</summary>
    public int BulletinsCompleted { get; init; }

    /// <summary>Issue #133: bulletins the BBS accepted while this slot was the one tracked.</summary>
    public int Delivered { get; init; }

    /// <summary>Issue #133: bulletins the BBS already had, while this slot was the one tracked.</summary>
    public int AlreadyHad { get; init; }

    /// <summary>Issue #133: part-received bulletins given up while this slot was the one tracked.</summary>
    public int GivenUp { get; init; }

    /// <summary>Issue #133: whether the journal has had this slot's summary line.</summary>
    public bool Ended { get; init; }

    /// <summary>The waveform most of the slot's frames came on, <c>mixed</c> if two or more tie for most, or null if none is known.</summary>
    public string? Waveform
    {
        get
        {
            if (FrameCounts.IsEmpty)
            {
                return null;
            }
            int most = FrameCounts.Values.Max();
            var top = FrameCounts.Where(c => c.Value == most).Select(c => c.Key).ToList();
            return top.Count == 1 ? top[0] : Mixed;
        }
    }

    /// <summary>What <see cref="Waveform"/> says when no one waveform has the most frames.</summary>
    public const string Mixed = "mixed";
}

/// <summary>
/// Groups what the receiver hears into GB7RDG's slots and keeps the latest.
/// </summary>
/// <remarks>
/// <para>With a <see cref="Schedule"/>, each frame belongs to the slot whose start most recently
/// passed (allowing <see cref="ClockAllowance"/> for a receiver clock that is a little slow), so
/// a long slot is not split and a late one is not merged with the one before. A tone is only
/// taken as a slot's if it began within <see cref="ToneWindow"/> of a slot's start: anything
/// else near 1800 Hz is someone tuning up, not GB7RDG.</para>
/// <para>Without one (a recording, whose time of day is unknown) a slot starts with a tone, or
/// with the first frame after <see cref="Gap"/> of quiet.</para>
/// </remarks>
public sealed class SlotTracker
{
    /// <summary>Without a schedule: frames this long after the slot's last one start a new slot.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    /// <summary>How far from a slot's start a tone may begin and still be that slot's.</summary>
    public static readonly TimeSpan ToneWindow = TimeSpan.FromMinutes(5);

    /// <summary>How early, by this receiver's clock, a frame may arrive and still count for the coming slot.</summary>
    public static readonly TimeSpan ClockAllowance = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private SlotSummary? _current;
    private FollowedSlots? _schedule;
    private DateTimeOffset? _listening;
    private DateTimeOffset? _lastEnded;

    /// <summary>Creates a tracker.</summary>
    public SlotTracker(TimeProvider time, Action<string> log, FollowedSlots? schedule = null)
    {
        _time = time;
        _log = log;
        _schedule = schedule;
    }

    /// <summary>The slots of every station followed (issue #144), or null when listening to a recording.</summary>
    public FollowedSlots? Schedule
    {
        get
        {
            lock (_gate)
            {
                return _schedule;
            }
        }
        set
        {
            lock (_gate)
            {
                _schedule = value;
            }
        }
    }

    /// <summary>The slot in progress or most recently heard, if any.</summary>
    public SlotSummary? Last
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>A tone has been measured: if it is at a slot's start, that slot begins with it.</summary>
    public void OnTone(ToneReport tone)
    {
        var began = _time.GetUtcNow() - tone.Duration;
        string measured = string.Create(CultureInfo.InvariantCulture,
            $"{tone.FrequencyHz:F1} Hz, {tone.OffsetHz:+0.0;-0.0;0.0} Hz from {tone.FrequencyHz - tone.OffsetHz:F0} Hz, SNR {tone.SnrDb:F1} dB in 3 kHz, {tone.Duration.TotalSeconds:F0} s");
        string? ended = null;
        lock (_gate)
        {
            DateTimeOffset? slot = null;
            if (_schedule is { } schedule)
            {
                var nearest = schedule.NearestStart(began);
                var off = (began - nearest).Duration();
                if (off > ToneWindow)
                {
                    _log(string.Create(CultureInfo.InvariantCulture,
                        $"tone: {measured}, but it began at {began.UtcDateTime:HH:mm:ss} UTC, {off.TotalMinutes:F0} min from any slot, so it is not {(schedule.OneStation ? schedule.Stations[0].Station + "'s" : "any followed station's")}: ignored"));
                    return;
                }
                slot = nearest;
            }
            if (_current is { Scheduled: not null } current && current.Scheduled == slot)
            {
                _current = current with { Started = began, Tone = tone };
            }
            else
            {
                ended = Replace(new SlotSummary(began, tone, 0, null, slot) { Station = StationOf(slot) });
            }
        }
        if (ended is not null)
        {
            _log(ended);
        }
        _log($"tone: {measured}");
    }

    /// <summary>
    /// A frame was heard, on <paramref name="waveform"/> by the modem's autobaud (null if it
    /// cannot say), with <paramref name="bytes"/> in its information field.
    /// </summary>
    public void OnFrame(string? waveform = null, int bytes = 0)
    {
        var now = _time.GetUtcNow();
        int frames;
        bool newWaveform = false;
        string? ended = null;
        lock (_gate)
        {
            if (_schedule is { } schedule)
            {
                var slot = schedule.LatestStart(now + ClockAllowance);
                if (_current?.Scheduled != slot)
                {
                    ended = Replace(new SlotSummary(now, null, 0, null, slot) { Station = StationOf(slot) });
                }
            }
            else if (_current is null || now - (_current.LastFrame ?? _current.Started) > Gap)
            {
                ended = Replace(new SlotSummary(now, null, 0, null));
            }
            _current = _current! with { FramesHeard = _current.FramesHeard + 1, LastFrame = now, BytesDecoded = _current.BytesDecoded + bytes };
            if (waveform is not null)
            {
                newWaveform = !_current.FrameCounts.TryGetValue(waveform, out int had);
                _current = _current with { FrameCounts = _current.FrameCounts.SetItem(waveform, had + 1) };
            }
            frames = _current.FramesHeard;
        }
        if (ended is not null)
        {
            _log(ended);
        }
        string on = waveform is null ? "" : $" on {Mailcast.Receiver.Waveform.Words(waveform)}";
        if (frames == 1)
        {
            _log($"slot: 1 frame heard{on}");
        }
        else if (newWaveform)
        {
            _log($"slot: frame {frames} heard{on}, a different waveform from the frames before it");
        }
        else if (frames % 100 == 0)
        {
            _log($"slot: {frames} frames heard");
        }
    }

    /// <summary>
    /// A directory was recognised for the slot now on, naming the waveform it went out on in
    /// <see cref="SlotSummary.ListedWaveform"/> (null from a head end that does not say, which
    /// leaves whatever was already there): kept beside the slot's frames as a cross-check, and
    /// (issue #49) as the exact rotation list the early-end check judges completeness against.
    /// </summary>
    /// <remarks>
    /// Issue #72: only replaces what is already tracked for this slot when nothing is tracked
    /// yet, or when <paramref name="directory"/> belongs to it (<see cref="BelongsToSameSlot"/>).
    /// A stray directory heard mid-slot (a decode of an old directory, a second head end, or a
    /// test head end on the air at the same time) must not take its place: the early-end check
    /// would then judge completeness against the wrong rotation, and the window could never end
    /// early for this slot.
    /// </remarks>
    public void OnDirectory(BroadcastDirectory directory) => TakeDirectory(directory, known: false, null);

    /// <summary>
    /// The same, for a directory known to be <paramref name="station"/>'s (issue #144; null for
    /// none): when several stations are followed, one from another station than the slot's own,
    /// or from none, heard during it, is not that slot's.
    /// </summary>
    public void OnDirectory(BroadcastDirectory directory, string? station) => TakeDirectory(directory, known: true, station);

    private void TakeDirectory(BroadcastDirectory directory, bool known, string? station)
    {
        ArgumentNullException.ThrowIfNull(directory);
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }
            if (known && _current.Station is { } own && own != station)
            {
                // Another station's, or no station's (issue #144): not this slot's directory.
                return;
            }
            if (_current.HeardDirectory is { } tracked && !BelongsToSameSlot(tracked, directory))
            {
                return;
            }
            _current = _current with { HeardDirectory = directory, ListedWaveform = directory.Mode ?? _current.ListedWaveform };
        }
    }

    /// <summary>
    /// Issue #72: whether <paramref name="candidate"/> may take the place of <paramref name="tracked"/>
    /// as the slot's own directory: the same day's rotation (even as a different object, such as
    /// the other waveform's resend of it), or a newer one (the day rolling over mid-slot). The
    /// same day but a different rotation, or an older day, is someone else's or something stale,
    /// not this slot's.
    /// </summary>
    private static bool BelongsToSameSlot(BroadcastDirectory tracked, BroadcastDirectory candidate) =>
        candidate.Date > tracked.Date || (candidate.Date == tracked.Date && candidate.Entries.SequenceEqual(tracked.Entries));

    /// <summary>Issue #49: the probe audio after the slot's tone has been captured.</summary>
    public void OnProbeCaptured()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { ProbeCaptured = true };
            }
        }
    }

    /// <summary>Issue #49: this slot's ionosonde reading was received.</summary>
    public void OnIonosphereHeard()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { IonosphereHeard = true };
            }
        }
    }

    /// <summary>Issue #49: this slot's PSK Reporter reading was received.</summary>
    public void OnPskReporterHeard()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { PskReporterHeard = true };
            }
        }
    }

    /// <summary>Issue #133: a bulletin was rebuilt.</summary>
    public void OnBulletinCompleted() => Change(s => s with { BulletinsCompleted = s.BulletinsCompleted + 1 });

    /// <summary>Issue #133: a BBS session accepted <paramref name="accepted"/> bulletins and already had <paramref name="alreadyHad"/>.</summary>
    public void OnDelivered(int accepted, int alreadyHad)
    {
        if (accepted > 0 || alreadyHad > 0)
        {
            Change(s => s with { Delivered = s.Delivered + accepted, AlreadyHad = s.AlreadyHad + alreadyHad });
        }
    }

    /// <summary>Issue #133: <paramref name="count"/> part-received bulletins were given up.</summary>
    public void OnGivenUp(int count)
    {
        if (count > 0)
        {
            Change(s => s with { GivenUp = s.GivenUp + count });
        }
    }

    private void Change(Func<SlotSummary, SlotSummary> change)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = change(_current);
            }
        }
    }

    /// <summary>
    /// Issue #133: writes the slot's summary line once its listening window is over: when the
    /// window closes (<see cref="ReceiverConfig.WebSdrAfter"/> after its start, which is after
    /// the closing CW ident), or as soon as <paramref name="endedEarly"/> names it, the slot
    /// whose window was ended early. Once a slot; nothing without a schedule (see <see cref="Finish"/>).
    /// </summary>
    public void EndIfOver(DateTimeOffset? endedEarly)
    {
        var now = _time.GetUtcNow();
        string? line = null;
        lock (_gate)
        {
            if (_current is { Scheduled: { } slot, Ended: false } current && (endedEarly == slot || now >= slot + ReceiverConfig.WebSdrAfter))
            {
                _current = current with { Ended = true };
                line = SummaryLine(_current, endedEarly == slot);
                _lastEnded = slot;
                if (_listening == slot)
                {
                    _listening = null;
                }
            }
            else if (_listening is { } listened && (endedEarly == listened || now >= listened + ReceiverConfig.WebSdrAfter))
            {
                // A slot listened to that nothing at all was heard in.
                _listening = null;
                _lastEnded = listened;
                if (_current?.Scheduled != listened)
                {
                    line = $"slot: the {listened.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC slot ended: nothing heard"
                        + (endedEarly == listened ? "; window ended early, nothing left to wait for" : "");
                }
            }
        }
        if (line is not null)
        {
            _log(line);
        }
    }

    /// <summary>
    /// Issue #133: the receiver is listening to <paramref name="slot"/> now (its window is
    /// open), so it gets a line when it ends even if nothing at all is heard in it. A slot
    /// already ended is ignored, as is one never noted (not listened to: no line).
    /// </summary>
    public void OnListening(DateTimeOffset slot)
    {
        lock (_gate)
        {
            if (_lastEnded is not { } ended || slot > ended)
            {
                _listening = slot;
            }
        }
    }

    /// <summary>Issue #133: writes the summary line for the slot tracked, if it has not had one: for the end of a recording.</summary>
    public void Finish()
    {
        string? line = null;
        lock (_gate)
        {
            if (_current is { Ended: false } current)
            {
                _current = current with { Ended = true };
                line = SummaryLine(_current, endedEarly: false);
            }
        }
        if (line is not null)
        {
            _log(line);
        }
    }

    /// <summary>The station of a slot, when more than one is followed; under <see cref="_gate"/>.</summary>
    private string? StationOf(DateTimeOffset? slot) =>
        slot is { } start && _schedule is { OneStation: false } followed ? followed.StationAt(start) : null;

    /// <summary>
    /// Makes <paramref name="next"/> the slot tracked. Returns the summary line of the one it
    /// replaces if that never had one (a check that came too late, or a recording), so no slot
    /// goes without; under <see cref="_gate"/>, logged by the caller outside it.
    /// </summary>
    private string? Replace(SlotSummary next)
    {
        string? line = _current is { Ended: false } old ? SummaryLine(old with { Ended = true }, endedEarly: false) : null;
        if (line is not null && _current!.Scheduled is { } slot)
        {
            // It has had its line now: not "nothing heard" for it later as well.
            _lastEnded = slot;
            if (_listening == slot)
            {
                _listening = null;
            }
        }
        _current = next;
        return line;
    }

    /// <summary>
    /// Issue #133: one plain ASCII line summing a slot up, for the journal: frames by waveform,
    /// bytes decoded from them, the tone, the bulletins, the directory, and whether the window
    /// ended early.
    /// </summary>
    internal static string SummaryLine(SlotSummary slot, bool endedEarly)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var c = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        string which = slot.Scheduled is { } scheduled
            ? $"{(slot.Station is { } station ? station + "'s" : "the")} {scheduled.UtcDateTime.ToString("HH:mm", c)} UTC slot"
            : $"the slot that began at {slot.Started.UtcDateTime.ToString("HH:mm:ss", c)} UTC";
        string frames = Count(slot.FramesHeard, "frame");
        int known = slot.FrameCounts.Values.Sum();
        var on = slot.FrameCounts.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => string.Create(c, $"{f.Value} on {Mailcast.Receiver.Waveform.Words(f.Key)}")).ToList();
        if (slot.FramesHeard > known)
        {
            on.Add(string.Create(c, $"{slot.FramesHeard - known} on a waveform not known"));
        }
        if (on.Count == 1 && known == slot.FramesHeard)
        {
            frames += " on " + Mailcast.Receiver.Waveform.Words(slot.FrameCounts.Keys.First());
        }
        else if (on.Count > 1)
        {
            frames += ", " + string.Join(" and ", on);
        }
        parts.Add(frames);
        parts.Add(Bytes(slot.BytesDecoded) + " decoded from them");
        parts.Add(slot.Tone is { } tone
            ? string.Create(c, $"tone SNR {tone.SnrDb:F0} dB, {tone.OffsetHz:+0.0;-0.0;0.0} Hz off")
            : "no tone");
        string bulletins = string.Create(c, $"bulletins {slot.BulletinsCompleted} completed, {slot.Delivered} delivered");
        if (slot.AlreadyHad > 0)
        {
            bulletins += string.Create(c, $", {slot.AlreadyHad} already had");
        }
        if (slot.GivenUp > 0)
        {
            bulletins += string.Create(c, $", {slot.GivenUp} given up");
        }
        parts.Add(bulletins);
        parts.Add(slot.HeardDirectory is { } directory
            ? directory.IsSigned ? "directory heard, signed" : "directory heard, not signed"
            : "no directory");
        if (endedEarly)
        {
            parts.Add("window ended early, nothing left to wait for");
        }
        return $"slot: {which} ended: {string.Join("; ", parts)}";
    }

    private static string Count(int n, string what) => string.Create(CultureInfo.InvariantCulture, $"{n} {what}{(n == 1 ? "" : "s")}");

    /// <summary>A byte count for a person: <c>850 bytes</c>, or <c>29.4 KB</c> (1 KB is 1000 bytes) from 1000 up.</summary>
    internal static string Bytes(long bytes) => bytes < 1000
        ? Count((int)bytes, "byte")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1000.0:F1} KB");
}
