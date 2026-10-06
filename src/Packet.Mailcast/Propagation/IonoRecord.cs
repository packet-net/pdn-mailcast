using System.Buffers.Binary;
using System.Text;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// Content type 4, the ionosonde reading, on the air: a fixed 22-octet record after the type
/// octet, not compressed, sent with dictionary 0. 23 octets in all, so the object is one RaptorQ
/// symbol and any one of its frames rebuilds it.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field (after the content type octet, 4)
///      0     1  record version, 1
///      1     5  station, the URSI code in ASCII, such as RL052
///      6     4  sounding time, minutes since 1970-01-01 00:00 UTC, big-endian
///     10     2  foF2, in 10 kHz, big-endian; 0xFFFF for none
///     12     2  MUF at 100 km, the same
///     14     2  MUF at 500 km, the same
///     16     2  MUF at 1000 km, the same
///     18     1  skip zone radius in 10 km; 0xFF when not open within 1000 km, or unknown
///     19     1  bits 0-2 state (0 UNKNOWN, 1 POOR, 2 MARGINAL, 3 GOOD), bits 3-4 method
///               (0 none, 1 measured, 2 estimated, 3 mixed), bits 5-6 source (1 GIRO, 2 PROPquest)
///     20     1  verdicts, two bits each, 100 km in bits 0-1, 500 km in 2-3, 1000 km in 4-5
///               (0 no data, 1 closed, 2 open, 3 reliable)
///     21     1  the sounding's age when sent, minutes, 255 for 255 or more
/// </code>
/// <para>A reader takes a newer version's record as far as it knows it: later versions only add
/// octets at the end, and a reader ignores bits it does not know.</para>
/// </remarks>
public static class IonoRecord
{
    /// <summary>The record version this code writes.</summary>
    public const byte Version = 1;

    /// <summary>The record's length after the type octet.</summary>
    public const int Length = 22;

    /// <summary>The object's length: the type octet and the record.</summary>
    public const int ObjectLength = Length + 1;

    private const ushort NoValue = 0xFFFF;
    private const byte NoSkipZone = 0xFF;

    /// <summary>The object's octets: the content type (4) and the record. The reading must have a sounding.</summary>
    public static byte[] Encode(IonoReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.SoundingTimeUtc is not { } at)
        {
            throw new ArgumentException("A reading without a sounding is not sent.", nameof(reading));
        }
        var bytes = new byte[ObjectLength];
        bytes[0] = (byte)ObjectKind.Ionosphere;
        var record = bytes.AsSpan(1);
        record[0] = Version;
        string station = (reading.Station ?? "").ToUpperInvariant();
        for (int i = 0; i < 5; i++)
        {
            char c = i < station.Length ? station[i] : ' ';
            record[1 + i] = c is > ' ' and <= '~' ? (byte)c : (byte)' ';
        }
        long minutes = at.ToUnixTimeSeconds() / 60;
        BinaryPrimitives.WriteUInt32BigEndian(record[6..], (uint)Math.Clamp(minutes, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(record[10..], TenKhz(reading.FoF2));
        BinaryPrimitives.WriteUInt16BigEndian(record[12..], TenKhz(reading.Mufd100));
        BinaryPrimitives.WriteUInt16BigEndian(record[14..], TenKhz(reading.Mufd500));
        BinaryPrimitives.WriteUInt16BigEndian(record[16..], TenKhz(reading.Mufd1000));
        record[18] = reading.SkipZoneKm is { } skip ? (byte)Math.Clamp((int)Math.Round(skip / 10.0, MidpointRounding.AwayFromZero), 0, NoSkipZone - 1) : NoSkipZone;
        record[19] = (byte)(((byte)reading.State & 0x07) | (((byte)reading.Method & 0x03) << 3) | (((byte)reading.Source & 0x03) << 5));
        record[20] = (byte)(((byte)reading.At100 & 0x03) | (((byte)reading.At500 & 0x03) << 2) | (((byte)reading.At1000 & 0x03) << 4));
        record[21] = (byte)Math.Clamp(reading.AgeMinutes ?? 0, 0, 255);
        return bytes;
    }

    /// <summary>The reading as a ready-to-send object: dictionary 0, one symbol of the object's own length rounded up to the alignment.</summary>
    public static TransferObject ToTransferObject(IonoReading reading, int alignment = MailcastFrame.StandardAlignment) =>
        TransferObject.ForRecord((byte)ObjectKind.Ionosphere, Encode(reading).AsSpan(1), alignment);

    /// <summary>
    /// Reads an object of content type 4 back. False for anything shorter than a version 1 record,
    /// or of a record version 0; true for a later version, read as far as version 1 goes.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> objectBytes, out IonoReading reading)
    {
        reading = IonoReading.None;
        if (objectBytes.Length < ObjectLength || (objectBytes[0] & ~ContentType.MetadataFollows) != (byte)ObjectKind.Ionosphere
            || (objectBytes[0] & ContentType.MetadataFollows) != 0)
        {
            return false;
        }
        var record = objectBytes[1..];
        if (record[0] == 0)
        {
            return false;
        }
        string station = Encoding.ASCII.GetString(record.Slice(1, 5)).Trim();
        var at = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(record[6..]) * 60L);
        byte flags = record[19];
        byte verdicts = record[20];
        reading = new IonoReading
        {
            Station = station.Length == 0 ? null : station,
            SoundingTimeUtc = at,
            FoF2 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(record[10..])),
            Mufd100 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(record[12..])),
            Mufd500 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(record[14..])),
            Mufd1000 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(record[16..])),
            SkipZoneKm = record[18] == NoSkipZone ? null : record[18] * 10,
            State = (flags & 0x07) is var s && s <= (byte)IonoState.Good ? (IonoState)s : IonoState.Unknown,
            Method = (MufMethod)((flags >> 3) & 0x03),
            Source = ((flags >> 5) & 0x03) is var source && source <= (byte)IonoSource.PropQuest ? (IonoSource)source : IonoSource.None,
            At100 = (PathVerdict)(verdicts & 0x03),
            At500 = (PathVerdict)((verdicts >> 2) & 0x03),
            At1000 = (PathVerdict)((verdicts >> 4) & 0x03),
            AgeMinutes = record[21],
        };
        return true;
    }

    private static ushort TenKhz(double? mhz) =>
        mhz is { } v && v > 0 ? (ushort)Math.Clamp((int)Math.Round(v * 100, MidpointRounding.AwayFromZero), 0, NoValue - 1) : NoValue;

    private static double? Mhz(ushort tenKhz) => tenKhz == NoValue ? null : tenKhz / 100.0;
}
