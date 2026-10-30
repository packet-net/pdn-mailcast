namespace Packet.Mailcast;

/// <summary>Where a trusted key came from (issue #146).</summary>
public enum KeySource
{
    /// <summary>Compiled into the receiver (<see cref="TrustedKey.BuiltIn"/>).</summary>
    BuiltIn,

    /// <summary>The config file's <c>trustedKeys</c>; changed only there.</summary>
    Config,

    /// <summary>Learnt on the air, from a station's first signed directory.</summary>
    Learnt,

    /// <summary>Added by hand on the status page.</summary>
    Added,
}

/// <summary>One row of the status page's keys table.</summary>
/// <param name="Station">The callsign the key is pinned to, or its label when it is not pinned yet.</param>
/// <param name="Key">The key.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="LastHeard">When a directory signed by it was last heard, or null if none is remembered.</param>
public sealed record KeyRow(string Station, TrustedKey Key, KeySource Source, DateTimeOffset? LastHeard)
{
    /// <summary>Only a learnt or page-added key can be forgotten; the others are changed in the program or the config file.</summary>
    public bool CanForget => Source is KeySource.Learnt or KeySource.Added;
}

/// <summary>What adding or forgetting a key did.</summary>
/// <param name="Error">Why it was refused, or null when it was done.</param>
/// <param name="Released">Bulletins newly complete because a held directory is now good.</param>
/// <param name="Delivered">Bulletins newly in the outbox for the same reason.</param>
public sealed record KeyChange(string? Error, IReadOnlyList<Bulletin> Released, IReadOnlyList<Bulletin> Delivered)
{
    internal static KeyChange Refused(string why) => new(why, [], []);
}

/// <summary>The keys table: every trusted key, adding one by hand and forgetting one (issue #146).</summary>
public sealed partial class ReceiverStore
{
    /// <summary>Every trusted key with its station, source and when its directory was last heard. GB7RDG first, then by callsign.</summary>
    public IReadOnlyList<KeyRow> KeyRows()
    {
        var heard = new Dictionary<ulong, DateTimeOffset>();
        void Note(BroadcastDirectory d, DirectoryCheck c, DateTimeOffset at)
        {
            if (c.State == SignatureState.Trusted && d.SigningKeyId is { } id && at > DateTimeOffset.MinValue && (!heard.TryGetValue(id, out var had) || at > had))
            {
                heard[id] = at;
            }
        }
        foreach (var (d, at, c) in _knownDirectories.Values)
        {
            Note(d, c, at);
        }
        var rows = new List<KeyRow>();
        foreach (var pin in _pins)
        {
            KeySource source = _options.TrustedKeys.Any(k => k.KeyId == pin.Key.KeyId)
                ? (TrustedKey.BuiltIn.Any(k => k.KeyId == pin.Key.KeyId) ? KeySource.BuiltIn : KeySource.Config)
                : pin.FromTrustedKeys ? KeySource.Config : pin.Added ? KeySource.Added : KeySource.Learnt;
            rows.Add(new KeyRow(pin.Station, pin.Key, source, heard.TryGetValue(pin.Key.KeyId, out var at) ? at : null));
        }
        foreach (var key in _options.TrustedKeys.Where(k => !_pins.Any(p => p.Key.KeyId == k.KeyId)))
        {
            rows.Add(new KeyRow(key.Label, key, TrustedKey.BuiltIn.Any(k => k.KeyId == key.KeyId) ? KeySource.BuiltIn : KeySource.Config, heard.TryGetValue(key.KeyId, out var at) ? at : null));
        }
        return [.. rows.OrderBy(r => r.Station != BuiltInStation).ThenBy(r => r.Station, StringComparer.Ordinal).ThenBy(r => r.Source)];
    }

    /// <summary>
    /// Adds a key by hand: <paramref name="callsign"/> and the key in base64 (32 raw octets, or a
    /// whole SubjectPublicKeyInfo). Refused, with the reason, for a bad callsign or key or one
    /// already trusted. Directories remembered as refused that it now makes good are used.
    /// </summary>
    public KeyChange AddKey(string? callsign, string? base64)
    {
        string station = (callsign ?? "").Trim().ToUpperInvariant();
        if (!StationAdvert.IsCallsign(station))
        {
            return KeyChange.Refused("Give the station's callsign, like GB7RDG.");
        }
        TrustedKey key;
        try
        {
            key = TrustedKey.Parse(station, (base64 ?? "").Trim());
        }
        catch (FormatException e)
        {
            return KeyChange.Refused(e.Message + ".");
        }
        if (DirectoryKeys.Any(k => k.KeyId == key.KeyId))
        {
            return KeyChange.Refused($"Key {KeyId.Format(key.KeyId)} is already trusted.");
        }
        var now = _options.Time.GetUtcNow();
        _pins.Add(new PinnedStation(station, key, now, Added: true));
        UpdateKeys();
        Log($"key {KeyId.Format(key.KeyId)} added for {station} from the page");
        _newPin = true;
        WritePins(station + "'s key");
        var released = AfterNewPin() ?? [];
        return new KeyChange(null, released, TakeDelivered());
    }

    /// <summary>
    /// Forgets a learnt or page-added key. Directories it signed are dropped, so a restart does not
    /// learn it again; the key is learnt again the next time its station's directory is heard.
    /// </summary>
    public KeyChange ForgetKey(ulong keyId)
    {
        var pin = _pins.FirstOrDefault(p => p.Key.KeyId == keyId);
        var row = KeyRows().FirstOrDefault(r => r.Key.KeyId == keyId);
        if (pin is null || row is null)
        {
            return KeyChange.Refused("That key is not held.");
        }
        if (!row.CanForget)
        {
            return KeyChange.Refused(row.Source == KeySource.BuiltIn ? "A built-in key cannot be forgotten." : "A key from the config file is changed in the config file.");
        }
        _pins.Remove(pin);
        UpdateKeys();
        foreach (string station in _conflicts.Where(c => c.Value.PinnedKeyId == keyId).Select(c => c.Key).ToList())
        {
            _conflicts.Remove(station);
        }
        foreach (var (id, (directory, _, _)) in _knownDirectories.ToList())
        {
            if (directory.SigningKeyId == keyId)
            {
                _knownDirectories.Remove(id);
                TryDelete(Path.Combine(_root, KnownDirectoriesFolder, ObjectId.Format(id) + ".txt"));
            }
        }
        if (Directory?.SigningKeyId == keyId)
        {
            Directory = null;
            NewestCheck = null;
            TryDelete(Path.Combine(_root, "directory.txt"));
        }
        WritePins("a forgotten key");
        Log($"key {KeyId.Format(keyId)} of {pin.Station} forgotten from the page");
        return new KeyChange(null, [], []);
    }
}
