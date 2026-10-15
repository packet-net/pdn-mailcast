using System.Globalization;
using System.Text;

namespace Packet.Mailcast;

/// <summary>One object in rotation, as the directory lists it.</summary>
/// <param name="ObjectId">The object ID frames carry, which is also the object's own hash.</param>
/// <param name="DictionaryId">The zstd dictionary the object was compressed with.</param>
/// <param name="Size">The serialised bulletin's length in octets, before compression.</param>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line.</param>
/// <param name="Type">The object's content type (<see cref="ContentType"/>): 1, a packet mail bulletin, unless the directory says otherwise.</param>
/// <param name="Hash">
/// The object's long hash (<see cref="Mailcast.ObjectId.LongHash"/>), 32 lower-case hex digits,
/// which a signed directory gives for every entry; null when the directory does not.
/// </param>
public sealed record DirectoryEntry(ulong ObjectId, ushort DictionaryId, int Size, string Bid, string Title, byte Type = (byte)ObjectKind.Bulletin, string? Hash = null);

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
/// gives its object's long hash, <c>hash=</c> and 32 hex digits: the first 16 octets of the same
/// SHA-256 whose first 8 are the object ID (<see cref="Mailcast.ObjectId.LongHash"/>). The last entry's line
/// ends with two more fields: <c>key=</c> and the signing key's ID (<see cref="KeyId"/>, 16 hex
/// digits), then <c>sig=</c> and the Ed25519 signature in base64 (64 octets). The signature is
/// over every octet of the serialised directory before the tab that starts the <c>sig=</c> field,
/// so it covers the whole directory, key ID included, but not itself or the final LF. Earlier
/// readers ignore all three like any field after the title, so they read a signed directory
/// exactly as an unsigned one. A directory with no entries has nowhere to carry a signature, so
/// it goes out unsigned.
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
    public BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule = null, string? mode = null, DirectorySigner? signer = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not null && (mode.Length == 0 || mode.Any(c => c is <= ' ' or > '~')))
        {
            throw new ArgumentException($"The mode '{mode}' cannot be serialised.", nameof(mode));
        }
        Date = date;
        Schedule = schedule;
        Mode = mode;
        Entries = entries.Select(e => e with { Title = e.Title.Replace('\t', ' ') }).ToArray();
        foreach (var e in Entries)
        {
            if (e.Bid.Length == 0 || e.Bid.Any(c => c is ' ' or '\t' or '\n' or '\r') || e.Title.Contains('\n') || e.Title.Contains('\r'))
            {
                throw new ArgumentException($"Entry {e.Bid} cannot be serialised.", nameof(entries));
            }
            if (e.Hash is { } hash && !IsLongHash(hash))
            {
                throw new ArgumentException($"Entry {e.Bid} has a hash that is not {ObjectId.LongHashOctets * 2} lower-case hex digits.", nameof(entries));
            }
        }
        _signer = Entries.Count > 0 ? signer : null;
    }

    private BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule, string? mode, byte[] serialized, string? keyText, string? signatureText, int signedLength)
        : this(date, entries, schedule, mode)
    {
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
            if (e.Hash is { } hash)
            {
                text.Append('\t').Append(HashField).Append(hash);
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

    private static bool IsLongHash(string text) => text.Length == ObjectId.LongHashOctets * 2 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
            lineStart += line.Length + 1;
            entries.Add(new DirectoryEntry(objectId, dictionaryId, size, parts[3], parts[4], type, hash is not null && IsLongHash(hash) ? hash : null));
            if (entries.Count == 1 && parts.Length > 5)
            {
                schedule = SlotTimetable.FromFields(parts[5..]);
                mode = parts[5..].FirstOrDefault(f => f.StartsWith("mode=", StringComparison.Ordinal) && f.Length > 5)?[5..];
            }
        }
        try
        {
            return new BroadcastDirectory(date, entries, schedule, mode, serialized.ToArray(), keyText, signatureText, signedLength);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }
    }
}
