using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>
/// What the status page shows that the receiver otherwise keeps only in memory, saved in
/// <see cref="FileName"/> in the state directory so a restart does not empty the page: "What was
/// heard", the last slot tracked, the last window ended early, where stations were heard, and what
/// the web SDR said about itself.
/// </summary>
/// <remarks>
/// <para>Written seldom, for SD cards: never per frame, only when a slot's window ends, at most
/// every <see cref="SaveEvery"/> while something changes, and at shutdown; and not at all when
/// nothing changed since the last write. The file is written to a temporary name and moved over
/// the old one, so it is either the old or the new.</para>
/// <para>A missing, unreadable or wrong-version file is ignored with one line in the log, and the
/// page starts empty, as it did before this was kept.</para>
/// </remarks>
internal sealed class StatusMemory
{
    /// <summary>The file's name in the state directory.</summary>
    public const string FileName = "status-history.json";

    /// <summary>The file's layout; a file with another is ignored.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The most often the file is written while something changes, outside a slot's end and shutdown.</summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A tone heard over no noise at all has an SNR of +Infinity.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private string? _written;
    private DateTimeOffset _lastLooked = DateTimeOffset.MinValue;

    public StatusMemory(string stateDirectory, TimeProvider time, Action<string> log)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _time = time;
        _log = log;
    }

    /// <summary>What was saved, or null if there is no file or it cannot be used (said in the log).</summary>
    public SavedStatus? Load()
    {
        string text;
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            text = File.ReadAllText(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"status: cannot read {_path}, starting empty: {Ascii.Clean(e.Message)}");
            return null;
        }
        SavedStatus? saved;
        try
        {
            saved = JsonSerializer.Deserialize<SavedStatus>(text, Json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
        {
            _log($"status: {FileName} is not readable, starting empty: {Ascii.Clean(e.Message)}");
            return null;
        }
        if (saved is null || saved.Version != CurrentVersion)
        {
            _log($"status: {FileName} is from another version, starting empty");
            return null;
        }
        lock (_gate)
        {
            _written = text;
        }
        return saved;
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/>'s state if it differs from what was last written: at once
    /// when <paramref name="now"/> is true (a slot's end, shutdown), otherwise only if
    /// <see cref="SaveEvery"/> has passed since this was last looked at. True if it wrote.
    /// </summary>
    public bool Save(Func<SavedStatus> snapshot, bool now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var at = _time.GetUtcNow();
        lock (_gate)
        {
            if (!now && at - _lastLooked < SaveEvery)
            {
                return false;
            }
            _lastLooked = at;
            string text;
            try
            {
                text = JsonSerializer.Serialize(snapshot(), Json);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                _log($"status: cannot save the page's history: {Ascii.Clean(e.Message)}");
                return false;
            }
            if (text == _written)
            {
                return false;
            }
            try
            {
                string temp = _path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, _path, overwrite: true);
                _written = text;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"status: cannot write {_path}: {Ascii.Clean(e.Message)}");
                return false;
            }
        }
    }
}

/// <summary>What <see cref="StatusMemory"/> keeps.</summary>
internal sealed record SavedStatus
{
    public int Version { get; init; }

    /// <summary>"What was heard": <see cref="SlotFrameHistory.Slots"/>, oldest first.</summary>
    public List<HeardSlotPieces>? Heard { get; init; }

    /// <summary>The slot tracked last (<see cref="SlotTracker.Last"/>).</summary>
    public SavedSlot? Slot { get; init; }

    /// <summary>The newest slot that has had its summary line.</summary>
    public DateTimeOffset? LastEnded { get; init; }

    /// <summary>The newest window ended early (<see cref="ReceiverHost.LastEarlyEnd"/>).</summary>
    public SavedEarlyEnd? LastEarlyEnd { get; init; }

    /// <summary>Where each station whose directory gives no frequency was heard, in Hz.</summary>
    public Dictionary<string, long>? HeardAtHz { get; init; }

    /// <summary>What the web SDR said about itself, and for which audio setting.</summary>
    public SavedWords? WebSdrAbout { get; init; }

    /// <summary>Where the web SDR said it is (null if it would not say), and for which audio setting.</summary>
    public SavedPlace? WebSdrPlace { get; init; }
}

internal sealed record SavedEarlyEnd(DateTimeOffset Slot, string Reason, DateTimeOffset? At);

internal sealed record SavedWords(string Source, string Words);

internal sealed record SavedPlace(string Source, GroundPlace? Place);

/// <summary>A <see cref="SlotSummary"/> as kept on disk; its directory in its own serialised form.</summary>
internal sealed record SavedSlot
{
    public DateTimeOffset Started { get; init; }
    public ToneReport? Tone { get; init; }
    public int FramesHeard { get; init; }
    public DateTimeOffset? LastFrame { get; init; }
    public DateTimeOffset? Scheduled { get; init; }
    public Dictionary<string, int>? FrameCounts { get; init; }
    public string? Station { get; init; }
    public string? ListedWaveform { get; init; }
    public byte[]? HeardDirectory { get; init; }
    public bool ProbeCaptured { get; init; }
    public bool IonosphereHeard { get; init; }
    public bool PskReporterHeard { get; init; }
    public long BytesDecoded { get; init; }
    public int BulletinsCompleted { get; init; }
    public int Delivered { get; init; }
    public int AlreadyHad { get; init; }
    public int GivenUp { get; init; }
    public int Touched { get; init; }
    public bool Ended { get; init; }

    public static SavedSlot From(SlotSummary s) => new()
    {
        Started = s.Started,
        Tone = s.Tone,
        FramesHeard = s.FramesHeard,
        LastFrame = s.LastFrame,
        Scheduled = s.Scheduled,
        FrameCounts = s.FrameCounts.Count == 0 ? null : s.FrameCounts.ToDictionary(StringComparer.Ordinal),
        Station = s.Station,
        ListedWaveform = s.ListedWaveform,
        HeardDirectory = s.HeardDirectory?.Serialize(),
        ProbeCaptured = s.ProbeCaptured,
        IonosphereHeard = s.IonosphereHeard,
        PskReporterHeard = s.PskReporterHeard,
        BytesDecoded = s.BytesDecoded,
        BulletinsCompleted = s.BulletinsCompleted,
        Delivered = s.Delivered,
        AlreadyHad = s.AlreadyHad,
        GivenUp = s.GivenUp,
        Touched = s.Touched,
        Ended = s.Ended,
    };

    /// <summary>The summary again; throws <see cref="FormatException"/> for a directory that does not read.</summary>
    public SlotSummary ToSummary() => new(Started, Tone, FramesHeard, LastFrame, Scheduled)
    {
        FrameCounts = (FrameCounts ?? []).ToImmutableSortedDictionary(StringComparer.Ordinal),
        Station = Station,
        ListedWaveform = ListedWaveform,
        HeardDirectory = HeardDirectory is { } bytes ? BroadcastDirectory.Parse(bytes) : null,
        ProbeCaptured = ProbeCaptured,
        IonosphereHeard = IonosphereHeard,
        PskReporterHeard = PskReporterHeard,
        BytesDecoded = BytesDecoded,
        BulletinsCompleted = BulletinsCompleted,
        Delivered = Delivered,
        AlreadyHad = AlreadyHad,
        GivenUp = GivenUp,
        Touched = Touched,
        Ended = Ended,
    };
}
