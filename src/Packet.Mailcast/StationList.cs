using System.Globalization;
using System.Text;

namespace Packet.Mailcast;

/// <summary>Which sideband a station transmits on.</summary>
public enum Sideband
{
    /// <summary>Upper sideband, as on HF data.</summary>
    Usb,

    /// <summary>Lower sideband.</summary>
    Lsb,
}

/// <summary>
/// One transmitter, as the signed station list gives it.
/// </summary>
/// <param name="Callsign">The station's callsign, upper case, as the page and the log name it.</param>
/// <param name="Locator">Its Maidenhead locator.</param>
/// <param name="FrequencyHz">Where the signal's centre is on the air, in Hz (GB7RDG's is 7053800).</param>
/// <param name="Sideband">The sideband it is sent on.</param>
/// <param name="Timetable">Its slots: anchor, interval and daylight rule.</param>
/// <param name="Modes">The waveforms it sends on, as pdn-soundmodem names them (<c>ms110d-wn4</c>).</param>
/// <param name="Key">The key its directories are signed with, labelled with its callsign.</param>
public sealed record StationEntry(string Callsign, string Locator, long FrequencyHz, Sideband Sideband, SlotTimetable Timetable, IReadOnlyList<string> Modes, TrustedKey Key)
{
    /// <summary>
    /// GB7RDG as built in: what every receiver follows when it holds no station list, so that a
    /// receiver that has heard none behaves exactly as before the list existed. Its timetable is
    /// the one the receiver already uses (its directory's, or its own settings).
    /// </summary>
    public static StationEntry Gb7rdg(SlotTimetable timetable) =>
        new("GB7RDG", "IO91lk", 7_053_800, Sideband.Usb, timetable, ["ms110d-wn3", "ms110d-wn4"], TrustedKey.Gb7rdg);

    /// <summary>The station in a few words, for the page and the log: "G8BPQ at IO92ab, 7.0538 MHz USB, every hour at 30 minutes past".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Callsign} at {Locator}, {FrequencyHz / 1e6:0.0000} MHz {(Sideband == Sideband.Usb ? "USB" : "LSB")}, slots {string.Join(" ", Timetable.ToFields())}");
}

/// <summary>
/// The built-in public halves of the station list key: the separate Ed25519 key, used for
/// nothing else and held offline, that signs the list of transmitters receivers follow.
/// </summary>
/// <remarks>
/// <para>
/// PLACEHOLDER: no list key has been made yet, so <see cref="BuiltIn"/> is empty and every list
/// heard is refused ("no list key is built in yet"); receivers then follow GB7RDG alone, exactly
/// as before. When the real key exists, add its public half here (the base64 of
/// <c>openssl pkey -in list-key.pem -pubout</c>'s body, or the 32 raw octets) and release.
/// Never put a key here that has been on a live box.
/// </para>
/// </remarks>
public static class StationListKey
{
    /// <summary>The keys a station list must be signed by. Empty: see the class remarks (PLACEHOLDER).</summary>
    public static IReadOnlyList<TrustedKey> BuiltIn { get; } = [];

    /// <summary>Whether <see cref="BuiltIn"/> is still the empty placeholder.</summary>
    public static bool IsPlaceholder => BuiltIn.Count == 0;
}

/// <summary>
/// The signed list of transmitters, sent over the air as an object of its own (content type 5,
/// <see cref="ObjectKind.StationList"/>) by every head end that has a copy.
/// </summary>
/// <remarks>
/// <para>Serialised as Latin-1 text, lines ended by LF:</para>
/// <code>
/// MAILCAST STATIONS 1
/// serial=2026102501
/// GB7RDG TAB locator=IO91lk TAB hz=7053800 TAB sideband=usb TAB slots=00:00/60 TAB daylight=IO91lk/120/30 TAB modes=ms110d-wn3,ms110d-wn4 TAB key=BASE64
/// listkey=0123456789abcdef TAB sig=BASE64
/// </code>
/// <para>
/// One line per station: its callsign, then <c>name=value</c> fields in any order. <c>hz</c> is
/// where the signal's centre is, <c>slots</c> and <c>daylight</c> are the directory's own
/// timetable fields (<see cref="SlotTimetable.ToFields"/>), <c>modes</c> is a comma-separated list
/// of waveforms, and <c>key</c> is the base64 of the station's 32-octet Ed25519 directory key.
/// A reader ignores fields it does not know, so later lists can add some. A station line it
/// cannot use (a field missing or unreadable) is skipped and named in <see cref="Skipped"/>.
/// </para>
/// <para>
/// The last line is the signature: <c>listkey=</c> and the list key's ID (<see cref="KeyId"/>),
/// then <c>sig=</c> and the Ed25519 signature in base64, over every octet before the tab that
/// starts the <c>sig=</c> field, as in the signed directory.
/// </para>
/// <para>
/// The serial says which list is newer: a receiver keeps the list with the highest serial whose
/// signature checks, and never goes back to a lower one.
/// </para>
/// </remarks>
public sealed class StationList
{
    /// <summary>The first line of a serialised list.</summary>
    public const string VersionLine = "MAILCAST STATIONS 1";

    private const string SerialField = "serial=";
    private const string ListKeyField = "listkey=";
    private const string SignatureField = "sig=";

    private readonly byte[] _serialized;
    private readonly string? _keyText;
    private readonly string _signatureText;
    private readonly int _signedLength;

    private StationList(long serial, IReadOnlyList<StationEntry> stations, IReadOnlyList<string> skipped, byte[] serialized, string? keyText, string signatureText, int signedLength)
    {
        Serial = serial;
        Stations = stations;
        Skipped = skipped;
        _serialized = serialized;
        _keyText = keyText;
        _signatureText = signatureText;
        _signedLength = signedLength;
    }

    /// <summary>Which list this is: a higher serial is a newer list.</summary>
    public long Serial { get; }

    /// <summary>The stations, in the order the list gives them.</summary>
    public IReadOnlyList<StationEntry> Stations { get; }

    /// <summary>Station lines this reader could not use, each with the reason, in plain ASCII.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>The ID of the key the list says signed it, if it says.</summary>
    public ulong? ListKeyId => _keyText is not null && KeyId.TryParse(_keyText, out ulong id) ? id : null;

    /// <summary>The serialised list, exactly as signed or read.</summary>
    public byte[] Serialize() => (byte[])_serialized.Clone();

    /// <summary>
    /// Makes and signs a list. Throws <see cref="ArgumentException"/> if a station cannot be
    /// written (its callsign, locator or modes), or two stations share a callsign or a key.
    /// </summary>
    public static StationList Sign(long serial, IEnumerable<StationEntry> stations, DirectorySigner listKey)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(listKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(serial, 1);
        var all = stations.ToList();
        var text = new StringBuilder();
        text.Append(VersionLine).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"{SerialField}{serial}\n");
        foreach (var s in all)
        {
            string? problem = Problem(s);
            if (problem is not null)
            {
                throw new ArgumentException(problem, nameof(stations));
            }
            text.Append(s.Callsign);
            text.Append("\tlocator=").Append(s.Locator);
            text.Append(CultureInfo.InvariantCulture, $"\thz={s.FrequencyHz}");
            text.Append("\tsideband=").Append(s.Sideband == Sideband.Usb ? "usb" : "lsb");
            foreach (var field in s.Timetable.ToFields())
            {
                text.Append('\t').Append(field);
            }
            text.Append("\tmodes=").Append(string.Join(',', s.Modes));
            text.Append("\tkey=").Append(Convert.ToBase64String(s.Key.PublicKey));
            text.Append('\n');
        }
        foreach (var duplicate in all.GroupBy(s => s.Callsign, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            throw new ArgumentException($"{duplicate.Key} is in the list twice", nameof(stations));
        }
        foreach (var duplicate in all.GroupBy(s => s.Key.KeyId).Where(g => g.Count() > 1))
        {
            throw new ArgumentException($"{string.Join(" and ", duplicate.Select(s => s.Callsign))} have the same key", nameof(stations));
        }
        text.Append(ListKeyField).Append(KeyId.Format(listKey.KeyId));
        byte[] signature = listKey.Sign(Bulletin.TextEncoding.GetBytes(text.ToString()));
        text.Append('\t').Append(SignatureField).Append(Convert.ToBase64String(signature)).Append('\n');
        return Parse(Bulletin.TextEncoding.GetBytes(text.ToString()));
    }

    /// <summary>What is wrong with a station for the list, or null if nothing.</summary>
    public static string? Problem(StationEntry station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (!IsCallsign(station.Callsign))
        {
            return $"'{station.Callsign}' is not a callsign (A to Z, 0 to 9, an optional -SSID, at most 9 characters)";
        }
        if (!Maidenhead.TryParse(station.Locator, out _, out _) || station.Locator.Any(c => c is <= ' ' or > '~'))
        {
            return $"{station.Callsign}: '{station.Locator}' is not a Maidenhead locator";
        }
        if (station.FrequencyHz is < 1 or > 300_000_000_000)
        {
            return $"{station.Callsign}: {station.FrequencyHz} Hz is not a frequency";
        }
        if (station.Modes.Count == 0 || station.Modes.Any(m => m.Length == 0 || m.Any(c => c is <= ' ' or > '~' or ',')))
        {
            return $"{station.Callsign}: give at least one mode, each a plain name such as ms110d-wn4";
        }
        return null;
    }

    private static bool IsCallsign(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 9)
        {
            return false;
        }
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        string call = dash < 0 ? text : text[..dash];
        if (call.Length is < 3 or > 6 || !call.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) || !call.Any(char.IsAsciiDigit))
        {
            return false;
        }
        return dash < 0 || (int.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int ssid) && ssid is >= 0 and <= 15 && text.Length - dash - 1 <= 2);
    }

    /// <summary>
    /// Reads the serialised form. Throws <see cref="FormatException"/> if it is not a list this
    /// code reads, or has no signature line. Does not check the signature: see <see cref="Check"/>.
    /// </summary>
    public static StationList Parse(ReadOnlySpan<byte> serialized)
    {
        var lines = Bulletin.TextEncoding.GetString(serialized).Split('\n');
        if (lines.Length < 4 || lines[^1].Length != 0)
        {
            throw new FormatException("A station list is lines ended by LF.");
        }
        if (lines[0] != VersionLine)
        {
            throw new FormatException($"Not a station list this code reads: {Ascii(lines[0])}");
        }
        if (!lines[1].StartsWith(SerialField, StringComparison.Ordinal)
            || !long.TryParse(lines[1].AsSpan(SerialField.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long serial) || serial < 1)
        {
            throw new FormatException("The station list's serial is not a positive whole number.");
        }
        string last = lines[^2];
        var signatureParts = last.Split('\t');
        if (signatureParts.Length < 2 || !signatureParts[^1].StartsWith(SignatureField, StringComparison.Ordinal))
        {
            throw new FormatException("The station list has no signature line.");
        }
        string? keyText = signatureParts[..^1].LastOrDefault(f => f.StartsWith(ListKeyField, StringComparison.Ordinal))?[ListKeyField.Length..];
        int signedLength = serialized.Length - 1 - signatureParts[^1].Length - 1;

        var stations = new List<StationEntry>();
        var skipped = new List<string>();
        for (int n = 2; n < lines.Length - 2; n++)
        {
            if (TryReadStation(lines[n], out var station, out string? why))
            {
                if (stations.Any(s => s.Callsign == station!.Callsign || s.Key.KeyId == station.Key.KeyId))
                {
                    skipped.Add($"{station!.Callsign}: listed twice, or with another station's key; the first is used");
                    continue;
                }
                stations.Add(station!);
            }
            else
            {
                skipped.Add(why!);
            }
        }
        return new StationList(serial, stations, skipped, serialized.ToArray(), keyText, signatureParts[^1][SignatureField.Length..], signedLength);
    }

    private static bool TryReadStation(string line, out StationEntry? station, out string? why)
    {
        station = null;
        var parts = line.Split('\t');
        string call = parts[0];
        string name = Ascii(call.Length == 0 ? "(a line with no callsign)" : call);
        if (!IsCallsign(call))
        {
            why = $"{name}: not a callsign";
            return false;
        }
        string? Field(string key) => parts.Skip(1).FirstOrDefault(f => f.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        string? locator = Field("locator");
        if (locator is null || !Maidenhead.TryParse(locator, out _, out _))
        {
            why = $"{name}: no readable locator";
            return false;
        }
        if (!long.TryParse(Field("hz"), NumberStyles.None, CultureInfo.InvariantCulture, out long hz) || hz < 1)
        {
            why = $"{name}: no readable frequency (hz=)";
            return false;
        }
        Sideband? sideband = Field("sideband") switch { "usb" => Sideband.Usb, "lsb" => Sideband.Lsb, _ => null };
        if (sideband is null)
        {
            why = $"{name}: no readable sideband (usb or lsb)";
            return false;
        }
        var timetable = SlotTimetable.FromFields(parts.Skip(1));
        if (timetable is null)
        {
            why = $"{name}: no readable slots";
            return false;
        }
        var modes = (Field("modes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (modes.Length == 0)
        {
            why = $"{name}: no modes";
            return false;
        }
        TrustedKey key;
        try
        {
            key = TrustedKey.Parse(call, Field("key") ?? "");
        }
        catch (FormatException e)
        {
            why = $"{name}: {Ascii(e.Message)}";
            return false;
        }
        station = new StationEntry(call, locator, hz, sideband.Value, timetable, modes, key);
        why = null;
        return true;
    }

    /// <summary>
    /// Checks the signature against <paramref name="listKeys"/> (normally
    /// <see cref="StationListKey.BuiltIn"/>): <see cref="SignatureState.Trusted"/> when one of
    /// them made it, <see cref="SignatureState.Rejected"/> with the reason otherwise. A list is
    /// never <see cref="SignatureState.None"/>: one with no signature does not parse.
    /// </summary>
    public DirectoryCheck Check(IReadOnlyList<TrustedKey> listKeys)
    {
        ArgumentNullException.ThrowIfNull(listKeys);
        if (_keyText is null || !KeyId.TryParse(_keyText, out ulong keyId))
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: "its signature does not say which key made it");
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(_signatureText);
        }
        catch (FormatException)
        {
            signature = [];
        }
        if (signature.Length != 64)
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: "its signature is not readable");
        }
        if (listKeys.Count == 0)
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: "no station list key is built in yet");
        }
        var key = listKeys.FirstOrDefault(k => k.KeyId == keyId);
        if (key is null)
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"it is signed by key {KeyId.Format(keyId)}, which is not the station list key");
        }
        if (!key.Verifies(_serialized.AsSpan(0, _signedLength), signature))
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"its signature does not match its content (key {KeyId.Format(keyId)})");
        }
        return new DirectoryCheck(SignatureState.Trusted, SignedBy: key.Label);
    }

    /// <summary>The list in a few words: "serial 2026102501, 2 stations: GB7RDG, G8BPQ".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"serial {Serial}, {Stations.Count} station{(Stations.Count == 1 ? "" : "s")}{(Stations.Count > 0 ? ": " + string.Join(", ", Stations.Select(s => s.Callsign)) : "")}");

    private static string Ascii(string text) => new([.. text.Take(60).Select(c => c is < ' ' or > '~' ? '?' : c)]);
}
