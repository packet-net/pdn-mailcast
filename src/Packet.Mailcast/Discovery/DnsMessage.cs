using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Packet.Mailcast.Discovery;

/// <summary>One resource record of an mDNS message: the few kinds service discovery uses.</summary>
public sealed class DnsRecord
{
    /// <summary>A record: an IPv4 address.</summary>
    public const ushort TypeA = 1;

    /// <summary>PTR record: a pointer to a name.</summary>
    public const ushort TypePtr = 12;

    /// <summary>TXT record: strings.</summary>
    public const ushort TypeTxt = 16;

    /// <summary>SRV record: host and port.</summary>
    public const ushort TypeSrv = 33;

    /// <summary>The query type that asks for every kind.</summary>
    public const ushort TypeAny = 255;

    /// <summary>The record's owner name, labels joined by dots.</summary>
    public required string Name { get; init; }

    /// <summary>The record type.</summary>
    public required ushort Type { get; init; }

    /// <summary>Seconds it may be kept; 0 says it is gone.</summary>
    public uint Ttl { get; init; }

    /// <summary>For PTR: the name it points to. For SRV: the host.</summary>
    public string? Target { get; init; }

    /// <summary>For SRV: the port.</summary>
    public ushort Port { get; init; }

    /// <summary>For A: the address.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>For TXT: the strings, each normally <c>key=value</c>.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];
}

/// <summary>A question: a name and the type asked for.</summary>
public readonly record struct DnsQuestion(string Name, ushort Type);

/// <summary>
/// The part of a DNS message (RFC 1035) that mDNS service discovery (RFC 6762, 6763) needs: the question
/// and the records of a message, read and written. Names are written without compression and read
/// with it; anything malformed is refused rather than guessed at.
/// </summary>
public sealed class DnsMessage
{
    private const ushort ClassIn = 1;
    private const ushort CacheFlush = 0x8000;
    private const int MaxRecords = 200;

    /// <summary>The questions.</summary>
    public List<DnsQuestion> Questions { get; } = [];

    /// <summary>The answers, authority and additional records, together: service discovery treats them alike.</summary>
    public List<DnsRecord> Records { get; } = [];

    /// <summary>True for a response, false for a query.</summary>
    public bool IsResponse { get; init; }

    /// <summary>Writes the message.</summary>
    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], IsResponse ? (ushort)0x8400 : (ushort)0);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)(IsResponse ? Records.Count : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)(IsResponse ? 0 : Records.Count));
        buffer.Write(header);
        foreach (var q in Questions)
        {
            WriteName(buffer, q.Name);
            WriteUInt16(buffer, q.Type);
            WriteUInt16(buffer, ClassIn);
        }
        foreach (var r in Records)
        {
            WriteName(buffer, r.Name);
            WriteUInt16(buffer, r.Type);
            WriteUInt16(buffer, r.Type == DnsRecord.TypePtr ? ClassIn : (ushort)(ClassIn | CacheFlush));
            var ttl = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ttl, r.Ttl);
            buffer.Write(ttl);
            using var data = new MemoryStream();
            switch (r.Type)
            {
                case DnsRecord.TypePtr:
                    WriteName(data, r.Target ?? throw new InvalidOperationException("a PTR record needs a target"));
                    break;
                case DnsRecord.TypeSrv:
                    WriteUInt16(data, 0);
                    WriteUInt16(data, 0);
                    WriteUInt16(data, r.Port);
                    WriteName(data, r.Target ?? throw new InvalidOperationException("an SRV record needs a host"));
                    break;
                case DnsRecord.TypeA:
                    data.Write((r.Address ?? throw new InvalidOperationException("an A record needs an address")).GetAddressBytes());
                    break;
                case DnsRecord.TypeTxt:
                    foreach (string s in r.Text.DefaultIfEmpty(""))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(s);
                        if (bytes.Length > 255)
                        {
                            throw new InvalidOperationException("a TXT string is at most 255 octets");
                        }
                        data.WriteByte((byte)bytes.Length);
                        data.Write(bytes);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"cannot write record type {r.Type}");
            }
            WriteUInt16(buffer, (ushort)data.Length);
            data.WriteTo(buffer);
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a message; null if it is malformed. Records of kinds not used here are skipped.</summary>
    public static DnsMessage? Parse(ReadOnlySpan<byte> packet)
    {
        try
        {
            return ParseInner(packet);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static DnsMessage? ParseInner(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12)
        {
            return null;
        }
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) + BinaryPrimitives.ReadUInt16BigEndian(p[8..]) + BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        if (questions > MaxRecords || records > MaxRecords)
        {
            return null;
        }
        var message = new DnsMessage { IsResponse = (flags & 0x8000) != 0 };
        int at = 12;
        for (int i = 0; i < questions; i++)
        {
            string name = ReadName(p, ref at);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
            at += 4;
            message.Questions.Add(new DnsQuestion(name, type));
        }
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(p, ref at);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(p[(at + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 8)..]);
            at += 10;
            if (at + length > p.Length)
            {
                return null;
            }
            int end = at + length;
            int cursor = at;
            switch (type)
            {
                case DnsRecord.TypePtr:
                    message.Records.Add(new DnsRecord { Name = name, Type = type, Ttl = ttl, Target = ReadName(p, ref cursor) });
                    break;
                case DnsRecord.TypeSrv when length >= 7:
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 4)..]);
                    cursor = at + 6;
                    message.Records.Add(new DnsRecord { Name = name, Type = type, Ttl = ttl, Port = port, Target = ReadName(p, ref cursor) });
                    break;
                case DnsRecord.TypeA when length == 4:
                    message.Records.Add(new DnsRecord { Name = name, Type = type, Ttl = ttl, Address = new IPAddress(p.Slice(at, 4)) });
                    break;
                case DnsRecord.TypeTxt:
                    var strings = new List<string>();
                    while (cursor < end)
                    {
                        int n = p[cursor++];
                        if (cursor + n > end)
                        {
                            return null;
                        }
                        strings.Add(Encoding.UTF8.GetString(p.Slice(cursor, n)));
                        cursor += n;
                    }
                    message.Records.Add(new DnsRecord { Name = name, Type = type, Ttl = ttl, Text = strings });
                    break;
            }
            at = end;
        }
        return message;
    }

    private static string ReadName(ReadOnlySpan<byte> p, ref int at)
    {
        var labels = new List<string>();
        int cursor = at;
        int resumeAt = -1;
        for (int hops = 0; hops < 64; hops++)
        {
            int length = p[cursor];
            if (length == 0)
            {
                at = resumeAt >= 0 ? resumeAt : cursor + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                int target = ((length & 0x3F) << 8) | p[cursor + 1];
                if (resumeAt < 0)
                {
                    resumeAt = cursor + 2;
                }
                if (target >= p.Length)
                {
                    throw new FormatException("bad name pointer");
                }
                cursor = target;
                continue;
            }
            if ((length & 0xC0) != 0 || cursor + 1 + length > p.Length)
            {
                throw new FormatException("bad label");
            }
            labels.Add(Encoding.UTF8.GetString(p.Slice(cursor + 1, length)));
            cursor += 1 + length;
        }
        throw new FormatException("name pointers loop");
    }

    private static void WriteName(Stream to, string name)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidOperationException("a name label is 1 to 63 octets");
            }
            to.WriteByte((byte)bytes.Length);
            to.Write(bytes);
        }
        to.WriteByte(0);
    }

    private static void WriteUInt16(Stream to, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        to.Write(bytes);
    }
}
