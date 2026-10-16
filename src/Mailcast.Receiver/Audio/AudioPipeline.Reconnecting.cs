using System.Text.RegularExpressions;

namespace Mailcast.Receiver;

/// <summary>
/// A networked source's own client has lost its connection and is getting it back: what the page
/// says meanwhile (<paramref name="Words"/>) and why it was lost, if the client said
/// (<paramref name="Reason"/>).
/// </summary>
public sealed record SourceDrop(string Words, string? Reason);

/// <summary>
/// The part of the pipeline that knows when SDRconnect or a web SDR has dropped and is being
/// reconnected to by pdn-soundmodem's client, so the page can say so rather than "listening".
/// </summary>
/// <remarks>
/// <para>pdn-soundmodem's clients (0.92.0) reconnect by themselves and raise Lost only after five
/// minutes of nothing; until then the only structured signal is <c>SessionLive</c>, which says
/// whether audio should be coming but not why it is not. Why is only in the lines they journal,
/// so those are read for it here. A line in a form this does not know is no worse than before:
/// after <see cref="QuietBeforeSaid"/> with the session not live and no audio, the page is told
/// the source was lost all the same, only without the reason.</para>
/// <para>Back is when audio comes again with the session live; or, since SDRconnect calls a
/// session over a slow link not live while its audio still flows, once more audio has come than
/// the client could have had buffered from before the drop.</para>
/// </remarks>
public sealed partial class AudioPipeline
{
    /// <summary>How long a source whose session is not live may give no audio before the page says it was lost, when its client has not said why.</summary>
    internal static readonly TimeSpan QuietBeforeSaid = TimeSpan.FromSeconds(5);

    /// <summary>Audio after a drop that means the source is back, whatever its session says: more than its client holds.</summary>
    private const long BackAfterSamples = 5L * OnAir.SampleRate;

    private readonly object _dropGate = new();
    private SourceDrop? _drop;
    private volatile bool _dropped;
    private long _samplesSinceDrop;
    private long _lastRead;

    /// <summary>Why the web SDR's last stream ended, as its client said, until it reconnects.</summary>
    private string? _webSdrEnded;

    /// <summary>
    /// Set while SDRconnect or a web SDR has dropped and pdn-soundmodem's client is reconnecting to
    /// it; null while its audio comes, and for every other source.
    /// </summary>
    public SourceDrop? Reconnecting
    {
        get
        {
            lock (_dropGate)
            {
                return _drop;
            }
        }
    }

    /// <summary>Raised when <see cref="Reconnecting"/> changes, on whichever thread noticed.</summary>
    public event Action? ReconnectingChanged;

    /// <summary>Whether the networked source's client says audio should be coming; true for any other source.</summary>
    private bool SessionLive => _webSdr?.SessionLive ?? _sdrConnect?.SessionLive ?? true;

    /// <summary>Where the page says the source is, in words.</summary>
    private string SourceWords => _source.Kind == AudioSourceKind.SdrConnect
        ? $"SDRconnect at {_source.SdrConnectEndpoint}"
        : $"the web SDR {ReceiverHost.WebSdrHost(_source)}";

    /// <summary>Reads one line pdn-soundmodem's SDRconnect or web SDR client journalled, for a drop and why.</summary>
    private void NoteSourceLine(string line)
    {
        if (DropSaid(line) is { } drop)
        {
            SetDrop(drop);
        }
    }

    /// <summary>The drop a line from the source's client says, or null if it says none.</summary>
    internal SourceDrop? DropSaid(string line)
    {
        string? why = null;
        bool refusing = false;
        if (_source.Kind == AudioSourceKind.SdrConnect)
        {
            string prefix = $"sdrconnect: {_source.SdrConnectEndpoint}: ";
            const string reconnecting = "; reconnecting";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(reconnecting, StringComparison.Ordinal))
            {
                why = line[prefix.Length..^reconnecting.Length];
            }
            else if (SdrConnectCannotReach().Match(line) is { Success: true } cannot)
            {
                why = cannot.Groups["why"].Value;
            }
        }
        else if (_source.Kind == AudioSourceKind.UberSdr)
        {
            if (WebSdrStreamEnded().Match(line) is { Success: true } ended)
            {
                why = _webSdrEnded = ended.Groups["why"].Value;
            }
            else if (line.StartsWith("ubersdr: reconnecting to ", StringComparison.Ordinal))
            {
                why = _webSdrEnded ?? "the session ended";
            }
            else if (line.StartsWith("ubersdr: the session ended after ", StringComparison.Ordinal))
            {
                why = _webSdrEnded ?? "it ended the session almost at once";
            }
            else if (WebSdrUnreachable().Match(line) is { Success: true } unreachable)
            {
                why = unreachable.Groups["why"].Value;
            }
            else if (WebSdrRefusing().Match(line) is { Success: true } refused)
            {
                why = refused.Groups["why"].Value;
                refusing = true;
            }
            else if (line.StartsWith("ubersdr: reconnected to ", StringComparison.Ordinal))
            {
                _webSdrEnded = null;
            }
        }
        if (why is null)
        {
            return null;
        }
        why = Ascii.Clean(why).TrimEnd('.', ' ');
        return refusing
            ? new($"{SourceWords} is refusing us for now ({why}); asking again later", why)
            : new($"lost {SourceWords} ({why}); reconnecting", why);
    }

    private void SetDrop(SourceDrop drop)
    {
        lock (_dropGate)
        {
            _samplesSinceDrop = 0;
            if (_drop == drop)
            {
                return;
            }
            _drop = drop;
            _dropped = true;
        }
        ReconnectingChanged?.Invoke();
    }

    /// <summary>On the audio thread, with each block read: the source is back once audio comes as it should.</summary>
    private void NoteAudioRead(int samples)
    {
        Interlocked.Exchange(ref _lastRead, _time.GetTimestamp());
        if (!_dropped)
        {
            return;
        }
        lock (_dropGate)
        {
            _samplesSinceDrop += samples;
            if (_drop is null || (!SessionLive && _samplesSinceDrop < BackAfterSamples))
            {
                return;
            }
            _drop = null;
            _dropped = false;
        }
        ReconnectingChanged?.Invoke();
    }

    /// <summary>
    /// From the starvation watch: a source whose session is not live and that has given no audio
    /// for <see cref="QuietBeforeSaid"/> was lost, even if its client said nothing this knows.
    /// </summary>
    private void NoteQuietSession()
    {
        if (_dropped || _time.GetElapsedTime(Interlocked.Read(ref _lastRead)) < QuietBeforeSaid)
        {
            return;
        }
        SetDrop(new($"lost {SourceWords}; reconnecting", null));
    }

    [GeneratedRegex(@"^sdrconnect: cannot reach \S+ \((?<why>.*)\); still trying(?: \(\d+ more failed attempt\(s\) since the last line\))?$")]
    private static partial Regex SdrConnectCannotReach();

    [GeneratedRegex(@"^ubersdr: stream from \S+ ended \((?<why>.*)\)$")]
    private static partial Regex WebSdrStreamEnded();

    [GeneratedRegex(@"^ubersdr: \S+ unreachable \((?<why>.*)\); retrying in \d+s$")]
    private static partial Regex WebSdrUnreachable();

    [GeneratedRegex(@"^ubersdr: \S+ is refusing us for now \((?<why>.*)\)(?:; asking again in \d+ min|\. The stream will be retried.*)$")]
    private static partial Regex WebSdrRefusing();
}
