using System.Globalization;
using System.Text;

namespace Packet.Mailcast.Tests;

// How receivers 0.14.0 and 0.15.0 read and check a directory: v0.14.0's BroadcastDirectory,
// unchanged to v0.15.0, copied whole and renamed, so the tests can show those receivers read a
// directory listing reports exactly as one without, and its signature still checks.
#pragma warning disable
internal sealed class V0140Directory
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
    /// to carry the signature. Each entry should then give its X.
    /// </summary>
    public V0140Directory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule = null, string? mode = null, DirectorySigner? signer = null)
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
                throw new ArgumentException($"Entry {e.Bid} has a hash that is not {ObjectId.ListedHashOctets * 2} lower-case hex digits.", nameof(entries));
            }
        }
        _signer = Entries.Count > 0 ? signer : null;
    }

    private V0140Directory(DateOnly date, IEnumerable<DirectoryEntry> entries, SlotTimetable? schedule, string? mode, byte[] serialized, string? keyText, string? signatureText, int signedLength)
        : this(date, entries, schedule, mode)
    {
        _serialized = serialized;
        _keyText = keyText;
        _signatureText = signatureText;
        _signedLength = signedLength;
    }

    /// <summary>
    /// Whether the directory carries a signature (good or bad: see X), or will
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
    /// The serialised form described in the class remarks. A directory read by X
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
    /// Checks the signature against <paramref name="trusted"/>: X
    /// when there is none, X when a trusted key's signature
    /// checks, and X, with the reason, otherwise.
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

    /// <summary>Reads the serialised form. Throws X if it is not one.</summary>
    public static V0140Directory Parse(ReadOnlySpan<byte> serialized)
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
            return new V0140Directory(date, entries, schedule, mode, serialized.ToArray(), keyText, signatureText, signedLength);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }
    }
}
#pragma warning restore
