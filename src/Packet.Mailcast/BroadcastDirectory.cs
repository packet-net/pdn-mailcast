using System.Globalization;
using System.Text;
using Packet.Mailcast.Feedback;

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
/// Another station a head end tells listeners about in its directory (issue #178): when and where
/// to listen for it. Only a hint about where to listen: it never makes a receiver trust a key or
/// deliver anything; that comes from the station's own signed directory.
/// </summary>
/// <param name="Callsign">The station's callsign, upper case.</param>
/// <param name="Timetable">Its slots.</param>
/// <param name="FrequencyHz">Where its signal's centre is, in Hz.</param>
/// <param name="Sideband">The sideband it is sent on.</param>
/// <param name="Locator">Where it transmits from, a 4 or 6 character Maidenhead locator; null when not given (an advert from before v0.20.0 never does).</param>
public sealed record StationAdvert(string Callsign, SlotTimetable Timetable, long FrequencyHz, Sideband Sideband, string? Locator = null)
{
    /// <summary>The directory field's name.</summary>
    public const string FieldName = "advert=";

    /// <summary>The station as a directory field: <c>advert=G8BPQ;00:30/60;-;7047000;usb</c>, with the locator after the sideband when it has one, <c>advert=G8BPQ;00:30/60;-;7047000;usb;IO83</c>.</summary>
    public string ToField()
    {
        var fields = Timetable.ToFields();
        string slots = fields[0]["slots=".Length..];
        string daylight = fields.Count > 1 ? fields[1]["daylight=".Length..] : "-";
        return string.Create(CultureInfo.InvariantCulture, $"{FieldName}{Callsign};{slots};{daylight};{FrequencyHz};{(Sideband == Sideband.Usb ? "usb" : "lsb")}{(Locator is null ? "" : ";" + Locator)}");
    }

    /// <summary>Reads one field, or null if it is not an advert this code reads.</summary>
    public static StationAdvert? FromField(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!field.StartsWith(FieldName, StringComparison.Ordinal))
        {
            return null;
        }
        var p = field[FieldName.Length..].Split(';');
        if (p.Length < 5 || !IsCallsign(p[0]) || !long.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out long hz) || hz < 1)
        {
            return null;
        }
        Sideband? sideband = p[4] switch { "usb" => Sideband.Usb, "lsb" => Sideband.Lsb, _ => null };
        var timetable = SlotTimetable.FromFields(p[2] == "-" ? ["slots=" + p[1]] : ["slots=" + p[1], "daylight=" + p[2]]);
        // A sixth part is the locator (v0.20.0); earlier readers ignore anything after the sideband.
        string? locator = p.Length > 5 && Maidenhead.TryParse(p[5], out _, out _) ? p[5] : null;
        return sideband is null || timetable is null ? null : new StationAdvert(p[0], timetable, hz, sideband.Value, locator);
    }

    /// <summary>What is wrong with it for a directory, or null if nothing.</summary>
    public string? Problem() =>
        !IsCallsign(Callsign) ? $"'{Callsign}' is not a callsign (A to Z and 0 to 9, an optional -SSID)"
        : FrequencyHz < 1 ? $"{Callsign}: {FrequencyHz} Hz is not a frequency"
        : Locator is not null && !Maidenhead.TryParse(Locator, out _, out _) ? $"{Callsign}: '{Locator}' is not a 4 or 6 character Maidenhead locator"
        : null;

    /// <summary>A callsign as a directory names a station: 3 to 6 of A to Z and 0 to 9 with a digit, and an optional -SSID of 0 to 15.</summary>
    public static bool IsCallsign(string? text)
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
        return dash < 0 || (text.Length - dash - 1 is 1 or 2 && int.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int ssid) && ssid <= 15);
    }
}

/// <summary>One object in rotation, as the directory lists it.</summary>
/// <param name="ObjectId">The object ID frames carry, which is also the object's own hash.</param>
/// <param name="DictionaryId">The zstd dictionary the object was compressed with.</param>
/// <param name="Size">The serialised bulletin's length in octets, before compression.</param>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line.</param>
/// <param name="Type">The object's content type (<see cref="ContentType"/>): 1, a packet mail bulletin, unless the directory says otherwise.</param>
/// <param name="Hash">
/// The object's long hash (<see cref="Mailcast.ObjectId.LongHash"/>): octets 8 to 15 of its
/// SHA-256, the ID being octets 0 to 7, as 16 lower-case hex digits,
/// which a signed directory gives for every entry; null when the directory does not.
/// </param>
/// <param name="Envelope">
/// The link to this bulletin's envelope object (issue #142), when the head end sends one;
/// null otherwise. Written as <c>env=</c> and is inside what a signature covers.
/// </param>
public sealed record DirectoryEntry(ulong ObjectId, ushort DictionaryId, int Size, string Bid, string Title, byte Type = (byte)ObjectKind.Bulletin, string? Hash = null, EnvelopeLink? Envelope = null);

/// <summary>
/// Where a bulletin's envelope object is and what it must hash to: the envelope's object ID and
/// its long hash (<see cref="Mailcast.ObjectId.LongHash"/>), as the <c>env=</c> field of the
/// bulletin's directory entry.
/// </summary>
/// <param name="ObjectId">The envelope object's ID.</param>
/// <param name="Hash">The envelope object's long hash, 16 lower-case hex digits.</param>
public sealed record EnvelopeLink(ulong ObjectId, string Hash)
{
    /// <summary>The field name, with its equals sign.</summary>
    public const string FieldName = "env=";

    /// <summary>The field as written in a directory line: <c>env=</c>, the 16-digit ID, a semicolon, the 16-digit hash.</summary>
    public string ToField() => FieldName + Mailcast.ObjectId.Format(ObjectId) + ";" + Hash;

    /// <summary>Reads a field written by <see cref="ToField"/>; null if it is anything else.</summary>
    public static EnvelopeLink? FromField(string field)
    {
        if (!field.StartsWith(FieldName, StringComparison.Ordinal))
        {
            return null;
        }
        var parts = field[FieldName.Length..].Split(';');
        if (parts.Length != 2 || !IsLowerHex(parts[0]) || !IsLowerHex(parts[1]) || !Mailcast.ObjectId.TryParse(parts[0], out ulong id))
        {
            return null;
        }
        return new EnvelopeLink(id, parts[1]);
    }

    /// <summary>Whether the text is 16 lower-case hex digits.</summary>
    internal static bool IsLowerHex(string text) => text.Length == 16 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>
/// The list of objects in rotation on one day's broadcast, so that a receiver can tell how many
/// bulletins it has heard of and how many it has rebuilt.
/// </summary>
/// <remarks>
/// <para>Serialised as Latin-1 text, lines ended by LF: a version line, the date, then one line per entry.</para>
/// <code>
/// MAILCAST DIRECTORY 1
/// 2026-10-04
/// 0123456789abcdef TAB 1 TAB 2345 TAB 12345_GB7RDG TAB Title text
/// </code>
/// <para>
/// The entry fields, separated by tabs, are object ID (16 hex digits), dictionary ID, size, BID
/// and title. A reader ignores any fields after the title, so later versions can add some. A tab
/// in a title becomes a space.
/// </para>
/// <para>
/// From v0.3.0 each entry also gives its object's content type, <c>type=1</c> for a packet mail
/// bulletin (an entry without one is a bulletin), and the first entry's line also carries the head end's <see cref="SlotTimetable"/> in
/// fields after its title, <c>slots=00:00/60</c> and, with a daylight rule,
/// <c>daylight=IO91lk/120/30</c> (see <see cref="SlotTimetable.ToFields"/>). Readers from before
/// ignore them, as they ignore any field after the title; a directory with no entries has nowhere
/// to carry it.
/// </para>
/// <para>
/// The first entry's line may also name the waveform the slot went out on,
/// <c>mode=ms110d-wn3</c>, so that a recording or a receiver's log says which slot used which
/// waveform. Receivers decode any MS110D waveform by itself, so it is for the record only, and
/// earlier readers ignore it like any field after the title.
/// </para>
/// <para>
/// From v0.14.0 a head end with a signing key signs the directory. Each entry's line then also
/// gives its object's long hash, <c>hash=</c> and 16 hex digits: octets 8 to 15 of the same
/// SHA-256 whose first 8 are the object ID (<see cref="Mailcast.ObjectId.LongHash"/>), so that
/// with the ID an object is checked against 16 octets. The last entry's line
/// ends with two more fields: <c>key=</c> and the signing key's ID (<see cref="KeyId"/>, 16 hex
/// digits), then <c>sig=</c> and the Ed25519 signature in base64 (64 octets). The signature is
/// over every octet of the serialised directory before the tab that starts the <c>sig=</c> field,
/// so it covers the whole directory, key ID included, but not itself or the final LF. Earlier
/// readers ignore all three like any field after the title, so they read a signed directory
/// exactly as an unsigned one. A directory with no entries has nowhere to carry a signature, so
/// it goes out unsigned.
/// </para>
/// <para>
/// From v0.16.0 the first entry's line may also list the listeners whose daily reports have
/// reached the head end lately, <c>reports=2026-10-15:G4ABC,G8BPQ;2026-10-14:M9TWM</c> (see
/// <see cref="ReportReceipts"/>), after the mode. Being on an entry line, it is inside what a
/// signature covers. Earlier readers ignore it like any field after the title.
/// </para>
/// <para>
/// From v0.19.0 (issue #178) the first entry's line may also name the station,
/// <c>station=G8BPQ</c> (the head end's callsign); on a signed directory give the signing key,
/// <c>pubkey=</c> and the base64 of its 32 octets, so a receiver that has never heard the station
/// can check the signature and pin the key; and tell listeners about other stations, one
/// <c>advert=CALL;HH:MM/EVERY;DAYLIGHT;HZ;usb</c> field each (<see cref="StationAdvert"/>,
/// <c>-</c> for no daylight rule). All are inside what a signature covers. Earlier readers ignore
/// them like any field after the title: none of their names starts with a name those readers look for.
/// </para>
/// <para>
/// From v0.20.0 (issue #179) the first entry's line may also say where the station itself is on
/// the air and where it is: <c>hz=7053800</c> (its signal's centre), <c>sideband=usb</c> and
/// <c>locator=IO91lk</c>, each optional, and an advert may end with its station's locator. A
/// receiver then takes a station's frequency, sideband and place from its own directory, and from
/// the adverts for a station it has not heard. Earlier readers ignore all of them (none of the
/// names starts with one they look for) and the signature still checks.
/// </para>
/// <para>
/// From the release after v0.23.0 (issue #142) an entry for a canonical bulletin may also link its envelope object
/// (content type 5), <c>env=</c>, the envelope's 16 hex digit object ID, a semicolon and its 16
/// hex digit long hash (<see cref="EnvelopeLink"/>), after <c>hash=</c>. It is inside what a
/// signature covers. Earlier readers ignore it like any field after the title (no name they look
/// for starts with <c>env=</c>); a reader that finds it malformed ignores it too.
/// </para>
/// <para>
/// Like every object, the directory's ID is the hash of its own octets, so two directories with
/// different contents never share an ID, even on the same day. Ed25519 signatures are
/// deterministic, so the same directory signed twice is the same object.
/// </para>
/// </remarks>
public sealed class BroadcastDirectory
{
    /// <summary>The first line of a serialised directory.</summary>
    public const string VersionLine = "MAILCAST DIRECTORY 1";

    private const string HashField = "hash=";
    private const string StationField = "station=";
    private const string PublicKeyField = "pubkey=";
    private const string KeyField = "key=";
    private const string SignatureField = "sig=";

    private readonly DirectorySigner? _signer;
    private byte[]? _serialized;

    // As read, when the directory came with a signature: the key ID and signature fields as
    // written, and how many of the octets read the signature covers.
    private readonly string? _keyText;
    private readonly string? _signatureText;
    private readonly int _signedLength;

    /// <summary>
    /// Creates a directory, signed by <paramref name="signer"/> when given and there is an entry
    /// to carry the signature. Each entry should then give its <see cref="DirectoryEntry.Hash"/>.
    /// </summary>
    public BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule = null, string? mode = null, DirectorySigner? signer = null, ReportReceipts? reports = null, string? station = null, IReadOnlyList<StationAdvert>? adverts = null, StationDetails? details = null)
    {
        if (details?.Problem() is { } detailsProblem)
        {
            throw new ArgumentException(detailsProblem, nameof(details));
        }
        Details = details;
        if (station is not null && !StationAdvert.IsCallsign(station))
        {
            throw new ArgumentException($"The station '{station}' cannot be serialised.", nameof(station));
        }
        foreach (var advert in adverts ?? [])
        {
            if (advert.Problem() is { } problem)
            {
                throw new ArgumentException(problem, nameof(adverts));
            }
        }
        Station = station;
        Adverts = adverts ?? [];
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not null && (mode.Length == 0 || mode.Any(c => c is <= ' ' or > '~')))
        {
            throw new ArgumentException($"The mode '{mode}' cannot be serialised.", nameof(mode));
        }
        Date = date;
        Schedule = schedule;
        Mode = mode;
        Reports = reports;
        Entries = entries.Select(e => e with { Title = e.Title.Replace('\t', ' ') }).ToArray();
        foreach (var e in Entries)
        {
            if (e.Bid.Length == 0 || e.Bid.Any(c => c is ' ' or '\t' or '\n' or '\r') || e.Title.Contains('\n') || e.Title.Contains('\r'))
            {
                throw new ArgumentException($"Entry {e.Bid} cannot be serialised.", nameof(entries));
            }
            if (e.Hash is { } hash && !IsLongHash(hash))
            {
                throw new ArgumentException($"Entry {e.Bid} has a hash that is not {ObjectId.ListedHashOctets * 2} lower-case hex digits.", nameof(entries));
            }
            if (e.Envelope is { } link && !IsLongHash(link.Hash))
            {
                throw new ArgumentException($"Entry {e.Bid} has an envelope hash that is not {ObjectId.ListedHashOctets * 2} lower-case hex digits.", nameof(entries));
            }
        }
        _signer = Entries.Count > 0 ? signer : null;
        PublicKey = _signer?.PublicKey;
    }

    private BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule, string? mode, ReportReceipts? reports, string? station, IReadOnlyList<StationAdvert> adverts, StationDetails? details, byte[]? publicKey, byte[] serialized, string? keyText, string? signatureText, int signedLength)
        : this(date, entries, schedule, mode, reports: reports, station: station, adverts: adverts, details: details)
    {
        PublicKey = publicKey;
        _serialized = serialized;
        _keyText = keyText;
        _signatureText = signatureText;
        _signedLength = signedLength;
    }

    /// <summary>
    /// Whether the directory carries a signature (good or bad: see <see cref="Check"/>), or will
    /// when serialised.
    /// </summary>
    public bool IsSigned => _signer is not null || _signatureText is not null;

    /// <summary>
    /// Where the station says its signal is and where it is (<c>hz=</c>, <c>sideband=</c>,
    /// <c>locator=</c>, issue #179); null when the directory gives none, as from an earlier head end.
    /// </summary>
    public StationDetails? Details { get; }

    /// <summary>
    /// The station the directory says it is from (<c>station=</c>, the head end's callsign), or
    /// null from a head end before this was sent, or with no entry to carry it.
    /// </summary>
    public string? Station { get; }

    /// <summary>
    /// The 32 raw octets of the key a signed directory says it was signed with (<c>pubkey=</c>), so
    /// that a receiver that has never heard the station can check it and pin the key; null when
    /// the directory does not give it.
    /// </summary>
    public byte[]? PublicKey { get; }

    /// <summary>How many <c>advert=</c> fields a reader takes from one directory: any after these are not used for listening (<see cref="IgnoredAdverts"/>).</summary>
    public const int MostAdverts = 8;

    /// <summary>The <c>advert=</c> fields read past the first <see cref="MostAdverts"/>: not used, only shown.</summary>
    public IReadOnlyList<StationAdvert> IgnoredAdverts { get; private init; } = [];

    /// <summary>The other stations the head end tells listeners about (<c>advert=</c>), the first <see cref="MostAdverts"/> read; empty when none.</summary>
    public IReadOnlyList<StationAdvert> Adverts { get; }

    /// <summary>
    /// The ID of the key the directory says signed it (<see cref="Packet.Mailcast.KeyId"/>), or
    /// null when it is not signed or does not say. Whether the signature is good is
    /// <see cref="Check"/>'s business.
    /// </summary>
    public ulong? SigningKeyId => _signer is { } signer ? signer.KeyId
        : _keyText is not null && Packet.Mailcast.KeyId.TryParse(_keyText, out ulong id) ? id : null;

    /// <summary>The broadcast day.</summary>
    public DateOnly Date { get; }

    /// <summary>
    /// The head end's timetable, when it sent one and there is an entry to carry it: what a
    /// receiver should take as the slots, in place of its own settings.
    /// </summary>
    public SlotTimetable? Schedule { get; }

    /// <summary>
    /// The waveform the slot went out on, as pdn-soundmodem names it (<c>ms110d-wn4</c>), when the
    /// head end said and there is an entry to carry it; null otherwise.
    /// </summary>
    public string? Mode { get; }

    /// <summary>
    /// The listeners whose daily reports have reached the head end lately, when it said and there
    /// is an entry to carry them; null when the directory does not say, as from a head end before
    /// v0.16.0.
    /// </summary>
    public ReportReceipts? Reports { get; }

    /// <summary>The objects in rotation.</summary>
    public IReadOnlyList<DirectoryEntry> Entries { get; }

    /// <summary>The entry for an object, if listed.</summary>
    public DirectoryEntry? Find(ulong objectId) => Entries.FirstOrDefault(e => e.ObjectId == objectId);

    /// <summary>
    /// The serialised form described in the class remarks. A directory read by <see cref="Parse"/>
    /// gives back exactly the octets it was read from, so its signature still checks.
    /// </summary>
    public byte[] Serialize() => (byte[])(_serialized ??= Build()).Clone();

    private byte[] Build()
    {
        var text = new StringBuilder();
        text.Append(VersionLine).Append('\n');
        text.Append(Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            text.Append(CultureInfo.InvariantCulture, $"{ObjectId.Format(e.ObjectId)}\t{e.DictionaryId}\t{e.Size}\t{e.Bid}\t{e.Title}\ttype={e.Type}");
            if (i == 0 && Schedule is { } schedule)
            {
                foreach (var field in schedule.ToFields())
                {
                    text.Append('\t').Append(field);
                }
            }
            if (i == 0 && Mode is { } mode)
            {
                text.Append("\tmode=").Append(mode);
            }
            if (i == 0 && Reports is { } reports)
            {
                text.Append('\t').Append(reports.ToField());
            }
            if (i == 0 && Station is { } station)
            {
                text.Append('\t').Append(StationField).Append(station);
            }
            if (i == 0 && Station is not null && _signer is { } keyed)
            {
                text.Append('\t').Append(PublicKeyField).Append(keyed.PublicKeyBase64);
            }
            if (i == 0 && Details is { } details)
            {
                foreach (var field in details.ToFields())
                {
                    text.Append('\t').Append(field);
                }
            }
            if (i == 0)
            {
                foreach (var advert in Adverts)
                {
                    text.Append('\t').Append(advert.ToField());
                }
            }
            if (e.Hash is { } hash)
            {
                text.Append('\t').Append(HashField).Append(hash);
            }
            if (e.Envelope is { } envelope)
            {
                text.Append('\t').Append(envelope.ToField());
            }
            if (i == Entries.Count - 1 && _signer is { } signer)
            {
                text.Append('\t').Append(KeyField).Append(KeyId.Format(signer.KeyId));
                byte[] signature = signer.Sign(Bulletin.TextEncoding.GetBytes(text.ToString()));
                text.Append('\t').Append(SignatureField).Append(Convert.ToBase64String(signature));
            }
            text.Append('\n');
        }
        return Bulletin.TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>
    /// Checks the signature against <paramref name="trusted"/>: <see cref="SignatureState.None"/>
    /// when there is none, <see cref="SignatureState.Trusted"/> when a trusted key's signature
    /// checks, and <see cref="SignatureState.Rejected"/>, with the reason, otherwise.
    /// </summary>
    public DirectoryCheck Check(IReadOnlyList<TrustedKey> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        if (!IsSigned)
        {
            return DirectoryCheck.NotSigned;
        }
        byte[] serialized = Serialize();
        string? keyText = _keyText;
        string? signatureText = _signatureText;
        int signedLength = _signedLength;
        if (signatureText is null)
        {
            // Made here, so read back what Build wrote.
            var parsed = Parse(serialized);
            (keyText, signatureText, signedLength) = (parsed._keyText, parsed._signatureText, parsed._signedLength);
        }
        if (keyText is null || !KeyId.TryParse(keyText, out ulong keyId))
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: "its signature does not say which key made it");
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText!);
        }
        catch (FormatException)
        {
            signature = [];
        }
        if (signature.Length != 64)
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: "its signature is not readable");
        }
        var key = trusted.FirstOrDefault(k => k.KeyId == keyId);
        if (key is null)
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"it is signed by key {KeyId.Format(keyId)}, which is not a trusted key");
        }
        if (!key.Verifies(serialized.AsSpan(0, signedLength), signature))
        {
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"its signature does not match its content (key {KeyId.Format(keyId)}, {key.Label})");
        }
        return new DirectoryCheck(SignatureState.Trusted, SignedBy: key.Label);
    }

    private static bool IsLongHash(string text) => text.Length == ObjectId.ListedHashOctets * 2 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Reads the serialised form. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static BroadcastDirectory Parse(ReadOnlySpan<byte> serialized)
    {
        var lines = Bulletin.TextEncoding.GetString(serialized).Split('\n');
        if (lines.Length < 3 || lines[^1].Length != 0)
        {
            throw new FormatException("A directory is lines ended by LF.");
        }
        if (lines[0] != VersionLine)
        {
            throw new FormatException($"Not a directory this code reads: {lines[0]}");
        }
        if (!DateOnly.TryParseExact(lines[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException("The directory's date is not yyyy-MM-dd.");
        }
        var entries = new List<DirectoryEntry>();
        SlotTimetable? schedule = null;
        string? mode = null;
        ReportReceipts? reports = null;
        string? station = null;
        byte[]? publicKey = null;
        StationDetails? details = null;
        var adverts = new List<StationAdvert>();
        var ignoredAdverts = new List<StationAdvert>();
        string? keyText = null;
        string? signatureText = null;
        int signedLength = 0;
        int lineStart = lines[0].Length + 1 + lines[1].Length + 1;
        for (int n = 2; n < lines.Length - 1; n++)
        {
            string line = lines[n];
            bool last = n == lines.Length - 2;
            var parts = line.Split('\t');
            if (parts.Length < 5
                || !ObjectId.TryParse(parts[0], out ulong objectId)
                || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort dictionaryId)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size))
            {
                throw new FormatException($"Bad directory line: {line}");
            }
            byte type = (byte)ObjectKind.Bulletin;
            foreach (var field in parts.AsSpan(5))
            {
                if (field.StartsWith("type=", StringComparison.Ordinal)
                    && byte.TryParse(field.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out byte t) && t is > 0 and < ContentType.MetadataFollows)
                {
                    type = t;
                    break;
                }
            }
            string? hash = parts.Skip(5).FirstOrDefault(f => f.StartsWith(HashField, StringComparison.Ordinal))?[HashField.Length..];
            if (last && parts.Length > 5 && parts[^1].StartsWith(SignatureField, StringComparison.Ordinal))
            {
                // The signature covers every octet before the tab that starts its own field.
                signatureText = parts[^1][SignatureField.Length..];
                keyText = parts[5..^1].LastOrDefault(f => f.StartsWith(KeyField, StringComparison.Ordinal))?[KeyField.Length..];
                signedLength = lineStart + line.Length - parts[^1].Length - 1;
            }
            var envelope = parts.Skip(5).Select(EnvelopeLink.FromField).FirstOrDefault(l => l is not null);
            lineStart += line.Length + 1;
            entries.Add(new DirectoryEntry(objectId, dictionaryId, size, parts[3], parts[4], type, hash is not null && IsLongHash(hash) ? hash : null, envelope));
            if (entries.Count == 1 && parts.Length > 5)
            {
                schedule = SlotTimetable.FromFields(parts[5..]);
                mode = parts[5..].FirstOrDefault(f => f.StartsWith("mode=", StringComparison.Ordinal) && f.Length > 5)?[5..];
                reports = ReportReceipts.FromFields(parts[5..]);
                station = parts[5..].FirstOrDefault(f => f.StartsWith(StationField, StringComparison.Ordinal))?[StationField.Length..];
                station = StationAdvert.IsCallsign(station) ? station : null;
                if (parts[5..].FirstOrDefault(f => f.StartsWith(PublicKeyField, StringComparison.Ordinal)) is { } keyField)
                {
                    try
                    {
                        publicKey = Convert.FromBase64String(keyField[PublicKeyField.Length..]);
                        publicKey = publicKey.Length == 32 ? publicKey : null;
                    }
                    catch (FormatException)
                    {
                        publicKey = null;
                    }
                }
                details = StationDetails.FromFields(parts[5..]);
                var fields = parts[5..].Where(f => f.StartsWith(StationAdvert.FieldName, StringComparison.Ordinal)).ToList();
                adverts.AddRange(fields.Take(MostAdverts).Select(StationAdvert.FromField).OfType<StationAdvert>());
                ignoredAdverts.AddRange(fields.Skip(MostAdverts).Select(StationAdvert.FromField).OfType<StationAdvert>());
            }
        }
        try
        {
            return new BroadcastDirectory(date, entries, schedule, mode, reports, station, adverts, details, publicKey, serialized.ToArray(), keyText, signatureText, signedLength) { IgnoredAdverts = ignoredAdverts };
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }
    }
}

/// <summary>
/// What a station says about its own transmitter in its directory (issue #179): where its signal
/// is centred, on which sideband, and where it is. Each part is optional: a head end that is not
/// told leaves it out, and a receiver then falls back to an advert for the station, and for GB7RDG
/// to what it has always assumed.
/// </summary>
/// <param name="FrequencyHz">The signal's centre in Hz (GB7RDG's is 7053800); null for not given.</param>
/// <param name="Sideband">The sideband; null for not given (usb is then assumed).</param>
/// <param name="Locator">A 4 or 6 character Maidenhead locator; null for not given.</param>
public sealed record StationDetails(long? FrequencyHz = null, Sideband? Sideband = null, string? Locator = null)
{
    /// <summary>The first entry's fields: <c>hz=</c>, <c>sideband=</c>, <c>locator=</c>, those given.</summary>
    public IEnumerable<string> ToFields()
    {
        if (FrequencyHz is { } hz)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"hz={hz}");
        }
        if (Sideband is { } sideband)
        {
            yield return sideband == Packet.Mailcast.Sideband.Usb ? "sideband=usb" : "sideband=lsb";
        }
        if (Locator is { } locator)
        {
            yield return "locator=" + locator;
        }
    }

    /// <summary>Reads the fields of a first entry; null when it gives none of the three.</summary>
    public static StationDetails? FromFields(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        long? hz = null;
        Sideband? sideband = null;
        string? locator = null;
        foreach (string field in fields)
        {
            if (field.StartsWith("hz=", StringComparison.Ordinal) && hz is null
                && long.TryParse(field.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed >= 1)
            {
                hz = parsed;
            }
            else if (field.StartsWith("sideband=", StringComparison.Ordinal) && sideband is null)
            {
                sideband = field[9..] switch { "usb" => Packet.Mailcast.Sideband.Usb, "lsb" => Packet.Mailcast.Sideband.Lsb, _ => null };
            }
            else if (field.StartsWith("locator=", StringComparison.Ordinal) && locator is null && Maidenhead.TryParse(field[8..], out _, out _))
            {
                locator = field[8..];
            }
        }
        return hz is null && sideband is null && locator is null ? null : new StationDetails(hz, sideband, locator);
    }

    /// <summary>What is wrong with it for a directory, or null if nothing.</summary>
    public string? Problem() =>
        FrequencyHz is < 1 ? $"{FrequencyHz} Hz is not a frequency"
        : Locator is not null && !Maidenhead.TryParse(Locator, out _, out _) ? $"'{Locator}' is not a 4 or 6 character Maidenhead locator"
        : null;
}
