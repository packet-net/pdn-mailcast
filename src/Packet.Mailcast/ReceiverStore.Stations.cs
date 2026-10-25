using System.Globalization;

namespace Packet.Mailcast;

/// <summary>A station's key as this receiver holds it (issue #178).</summary>
/// <param name="Station">The station's callsign.</param>
/// <param name="Key">Its directory key.</param>
/// <param name="PinnedAt">When it was learnt from the station's first signed directory; null for one built in or in <c>trustedKeys</c>.</param>
public sealed record PinnedStation(string Station, TrustedKey Key, DateTimeOffset? PinnedAt);

/// <summary>
/// A signed directory that named a station whose key is already pinned, under another key: it
/// is refused (issue #178), and the status page shows it.
/// </summary>
/// <param name="Station">The callsign the directory named.</param>
/// <param name="PinnedKeyId">The key pinned for it.</param>
/// <param name="HeardKeyId">The key the directory was signed with.</param>
/// <param name="HeardAt">When the last such directory was heard.</param>
public sealed record KeyConflict(string Station, ulong PinnedKeyId, ulong HeardKeyId, DateTimeOffset HeardAt);

/// <summary>The newest directory heard from one station (issue #144).</summary>
/// <param name="Station">The station's callsign, as its pinned key names it.</param>
/// <param name="Directory">Its newest directory still remembered: the latest date, and of those the one heard last.</param>
/// <param name="Check">What the directory's signature check found.</param>
/// <param name="HeardAt">When it was last heard; <see cref="DateTimeOffset.MinValue"/> for one kept only as the newest across a restart.</param>
public sealed record StationDirectory(string Station, BroadcastDirectory Directory, DirectoryCheck Check, DateTimeOffset HeardAt);

/// <summary>Stations, their pinned keys, and their directories (issues #178 and #144).</summary>
/// <remarks>
/// <para>
/// Each station signs its own directories. GB7RDG's key is built in, and keys in the receiver's
/// <c>trustedKeys</c> are pinned to their labels. Any other station's key is pinned the first
/// time its signed directory is heard: the directory names the station (<c>station=</c>) and
/// gives its key (<c>pubkey=</c>), and if the signature checks against that key and the callsign
/// is not pinned yet, the callsign is pinned to the key in <see cref="PinsFile"/> and the
/// directory is used at once, so the new station's bulletins are delivered straight away. A
/// directory naming a pinned callsign under another key is refused like any bad signature, and
/// kept in <see cref="KeyConflicts"/> for the page. A key already pinned keeps its name: a
/// directory signed by it belongs to that station whatever callsign it names.
/// </para>
/// <para>
/// A trusted signed directory belongs to the station its key is pinned to. An unsigned one
/// belongs to GB7RDG when it names no station or names GB7RDG, exactly as before stations were
/// named; one naming another station belongs to none, since anyone can send an unsigned
/// directory. A directory of no station still names and delivers bulletins as any usable
/// directory does, but gives no station's bulletins up and sets no station's timetable.
/// </para>
/// </remarks>
public sealed partial class ReceiverStore
{
    /// <summary>The station whose key is built in, and that unsigned directories belong to: GB7RDG.</summary>
    public const string BuiltInStation = "GB7RDG";

    /// <summary>Where keys learnt on the air are kept, under the store's folder: one <c>CALL TAB base64 TAB pinned-at</c> line each.</summary>
    public const string PinsFile = "pins.txt";

    private readonly Dictionary<string, PinnedStation> _pins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyConflict> _conflicts = new(StringComparer.Ordinal);
    private bool _newPin;

    /// <summary>Every station's pinned key: built in, from <c>trustedKeys</c>, and learnt on the air.</summary>
    public IReadOnlyList<PinnedStation> Pins => [.. _pins.Values];

    /// <summary>Signed directories refused because they named a station pinned to another key, newest per station.</summary>
    public IReadOnlyList<KeyConflict> KeyConflicts => [.. _conflicts.Values.OrderBy(c => c.Station, StringComparer.Ordinal)];

    /// <summary>The keys directories are checked against: every pinned key.</summary>
    public IReadOnlyList<TrustedKey> DirectoryKeys { get; private set; } = [];

    private void LoadPins()
    {
        foreach (var key in _options.TrustedKeys)
        {
            _pins.TryAdd(key.Label, new PinnedStation(key.Label, key, null));
        }
        string file = Path.Combine(_root, PinsFile);
        if (File.Exists(file))
        {
            foreach (string line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                try
                {
                    if (p.Length >= 3 && StationAdvert.IsCallsign(p[0])
                        && DateTimeOffset.TryParse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                        && !_pins.Values.Any(x => x.Key.PublicKey.AsSpan().SequenceEqual(Convert.FromBase64String(p[1]))))
                    {
                        _pins.TryAdd(p[0], new PinnedStation(p[0], TrustedKey.Parse(p[0], p[1]), at));
                    }
                }
                catch (FormatException)
                {
                    Log($"{PinsFile}: skipped an unreadable line");
                }
            }
        }
        DirectoryKeys = [.. _pins.Values.Select(x => x.Key).DistinctBy(k => k.KeyId)];
    }

    private void Pin(string station, TrustedKey key)
    {
        var now = _options.Time.GetUtcNow();
        _pins[station] = new PinnedStation(station, key, now);
        DirectoryKeys = [.. _pins.Values.Select(x => x.Key).DistinctBy(k => k.KeyId)];
        var lines = _pins.Values.Where(x => x.PinnedAt is not null)
            .Select(x => $"{x.Station}\t{Convert.ToBase64String(x.Key.PublicKey)}\t{x.PinnedAt!.Value.ToString("o", CultureInfo.InvariantCulture)}\n");
        DurableFile.WriteAtomically(Path.Combine(_root, PinsFile), Bulletin.TextEncoding.GetBytes(string.Concat(lines)), flush: _options.FlushToDisk);
        Log($"pinned {station}'s key {KeyId.Format(key.KeyId)}, learnt from its first signed directory; its bulletins are delivered from now on");
        _newPin = true;
    }

    /// <summary>
    /// Checks a directory's signature against the pinned keys, pinning a new station's key when
    /// the directory names a callsign not yet pinned and checks against the key it gives.
    /// </summary>
    internal DirectoryCheck CheckDirectory(BroadcastDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var check = directory.Check(DirectoryKeys);
        if (check.State != SignatureState.Rejected || directory.SigningKeyId is not { } id || DirectoryKeys.Any(k => k.KeyId == id) || directory.Station is not { } station)
        {
            return check; // fine, unsigned, a pinned key's bad signature, or names no station
        }
        if (_pins.TryGetValue(station, out var pinned))
        {
            _conflicts[station] = new KeyConflict(station, pinned.Key.KeyId, id, _options.Time.GetUtcNow());
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"it names {station}, whose key is pinned as {KeyId.Format(pinned.Key.KeyId)}, but is signed by key {KeyId.Format(id)}");
        }
        if (directory.PublicKey is not { } publicKey || KeyId.Of(publicKey) != id)
        {
            return check;
        }
        var key = TrustedKey.Parse(station, Convert.ToBase64String(publicKey));
        var own = directory.Check([key]);
        if (own.State == SignatureState.Trusted)
        {
            Pin(station, key);
        }
        return own;
    }

    /// <summary>
    /// After a key was pinned: directories remembered as refused that it now makes good are used
    /// from now on, and bulletins held because of them are released. Null when nothing was pinned.
    /// </summary>
    private List<Bulletin>? AfterNewPin()
    {
        if (!_newPin)
        {
            return null;
        }
        _newPin = false;
        var released = new List<Bulletin>();
        foreach (var (id, (directory, heardAt, _)) in _rejectedDirectories.ToList())
        {
            var check = directory.Check(DirectoryKeys);
            if (check.Usable)
            {
                _rejectedDirectories.Remove(id);
                _knownDirectories[id] = (directory, heardAt, check);
                released.AddRange(ReleaseWithheld(directory) ?? []);
            }
        }
        return released;
    }

    /// <summary>Which station a directory belongs to, or null for none: see the remarks.</summary>
    public string? StationOf(BroadcastDirectory directory, DirectoryCheck check)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(check);
        return check.State switch
        {
            SignatureState.Trusted => directory.SigningKeyId is { } id ? _pins.Values.FirstOrDefault(p => p.Key.KeyId == id)?.Station : null,
            SignatureState.None => directory.Station is null or BuiltInStation ? BuiltInStation : null,
            _ => null,
        };
    }

    /// <summary>
    /// The newest directory remembered from each station (issue #144): the latest date, and of
    /// those the one heard last. GB7RDG first, then by callsign.
    /// </summary>
    public IReadOnlyList<StationDirectory> StationDirectories()
    {
        var newest = new Dictionary<string, StationDirectory>(StringComparer.Ordinal);
        var all = _knownDirectories.Values.Select(k => (k.Directory, k.HeardAt, k.Check));
        if (Directory is { } held && NewestCheck is { } heldCheck)
        {
            all = all.Append((held, DateTimeOffset.MinValue, heldCheck));
        }
        foreach (var (directory, heardAt, check) in all)
        {
            if (StationOf(directory, check) is not { } station)
            {
                continue;
            }
            if (!newest.TryGetValue(station, out var had) || directory.Date > had.Directory.Date || (directory.Date == had.Directory.Date && heardAt > had.HeardAt))
            {
                newest[station] = new StationDirectory(station, directory, check, heardAt);
            }
        }
        return [.. newest.Values.OrderBy(d => d.Station != BuiltInStation).ThenBy(d => d.Station, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The timetable <paramref name="station"/>'s own directories give: for GB7RDG,
    /// <see cref="HeardSchedule"/>, kept across restarts; for another, that of its newest
    /// remembered directory that gives one, always one signed by its own pinned key. Null when
    /// none does.
    /// </summary>
    public SlotTimetable? HeardTimetable(string station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (station == BuiltInStation)
        {
            return HeardSchedule;
        }
        return _knownDirectories.Values
            .Where(k => k.Directory.Schedule is not null && k.Check.State == SignatureState.Trusted && StationOf(k.Directory, k.Check) == station)
            .OrderBy(k => k.Directory.Date).ThenBy(k => k.HeardAt)
            .LastOrDefault().Directory?.Schedule;
    }

    /// <summary>
    /// The stations the head ends heard tell listeners about (issue #178), newest per callsign,
    /// from trusted signed directories only. Hints about where to listen, never trust.
    /// </summary>
    public IReadOnlyList<StationAdvert> Adverts() =>
        [.. _knownDirectories.Values
            .Where(k => k.Check.State == SignatureState.Trusted)
            .OrderBy(k => k.Directory.Date).ThenBy(k => k.HeardAt)
            .SelectMany(k => k.Directory.Adverts)
            .GroupBy(a => a.Callsign, StringComparer.Ordinal)
            .Select(g => g.Last())
            .OrderBy(a => a.Callsign, StringComparer.Ordinal)];
}
