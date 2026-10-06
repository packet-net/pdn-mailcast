using System.Buffers.Binary;
using System.Text;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// Content type 4, a propagation reading, on the air: one object per source, each a short record
/// after the type octet, not compressed, sent with dictionary 0. A common part any source fills
/// (the source, the time, the state and the verdict at 100, 500 and 1000 km), then the source's
/// own numbers. The ionosonde's is 23 octets, so with the type octet the object is 24, one RaptorQ
/// symbol, and any one of its frames rebuilds it.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field (after the content type octet, 4)
///      0     1  record version, 1
///      1     1  source: 1 ionosonde by GIRO, 2 ionosonde by PROPquest; others reserved (3 for
///               PSK Reporter spots)
///      2     4  observation time, minutes since 1970-01-01 00:00 UTC, big-endian
///      6     1  its age when sent, minutes, 255 for 255 or more
///      7     1  bits 0-1 verdict at 100 km, 2-3 at 500 km, 4-5 at 1000 km (0 unknown, 1 closed,
///               2 open, 3 reliable); bits 6-7 state (0 UNKNOWN, 1 POOR, 2 MARGINAL, 3 GOOD)
///      8     1  skip zone radius in 10 km; 0xFF when not open within 1000 km, or unknown
///   then, for an ionosonde (sources 1 and 2):
///      9     5  station, the URSI code in ASCII, such as RL052
///     14     1  method: 0 none, 1 measured, 2 estimated, 3 mixed
///     15     2  foF2, in 10 kHz, big-endian; 0xFFFF for none
///     17     2  MUF at 100 km, the same
///     19     2  MUF at 500 km, the same
///     21     2  MUF at 1000 km, the same
/// </code>
/// <para>A reader that does not know a source still has the common part. Later record versions
/// only add octets at the end, and a reader ignores bits and octets it does not know.</para>
/// </remarks>
public static class IonoRecord
{
    /// <summary>The record version this code writes.</summary>
    public const byte Version = 1;

    /// <summary>The common part's length, after the type octet.</summary>
    public const int CommonLength = 9;

    /// <summary>An ionosonde record's length, after the type octet.</summary>
    public const int Length = CommonLength + 14;

    /// <summary>An ionosonde object's length: the type octet and the record.</summary>
    public const int ObjectLength = Length + 1;

    private const ushort NoValue = 0xFFFF;
    private const byte NoSkipZone = 0xFF;

    /// <summary>The object's octets: the content type (4) and the record. The reading must have a sounding and an ionosonde source.</summary>
    public static byte[] Encode(IonoReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.SoundingTimeUtc is not { } at)
        {
            throw new ArgumentException("A reading without a sounding is not sent.", nameof(reading));
        }
        if (reading.Source is not (IonoSource.Giro or IonoSource.PropQuest))
        {
            throw new ArgumentException("Only an ionosonde reading is written here.", nameof(reading));
        }
        var bytes = new byte[ObjectLength];
        bytes[0] = (byte)ObjectKind.Propagation;
        var record = bytes.AsSpan(1);
        record[0] = Version;
        record[1] = (byte)reading.Source;
        BinaryPrimitives.WriteUInt32BigEndian(record[2..], (uint)Math.Clamp(at.ToUnixTimeSeconds() / 60, 0, uint.MaxValue));
        record[6] = (byte)Math.Clamp(reading.AgeMinutes ?? 0, 0, 255);
        record[7] = (byte)(((byte)reading.At100 & 0x03) | (((byte)reading.At500 & 0x03) << 2) | (((byte)reading.At1000 & 0x03) << 4) | (((byte)reading.State & 0x03) << 6));
        record[8] = reading.SkipZoneKm is { } skip ? (byte)Math.Clamp((int)Math.Round(skip / 10.0, MidpointRounding.AwayFromZero), 0, NoSkipZone - 1) : NoSkipZone;

        var ionosonde = record[CommonLength..];
        string station = (reading.Station ?? "").ToUpperInvariant();
        for (int i = 0; i < 5; i++)
        {
            char c = i < station.Length ? station[i] : ' ';
            ionosonde[i] = c is > ' ' and <= '~' ? (byte)c : (byte)' ';
        }
        ionosonde[5] = (byte)((byte)reading.Method & 0x03);
        BinaryPrimitives.WriteUInt16BigEndian(ionosonde[6..], TenKhz(reading.FoF2));
        BinaryPrimitives.WriteUInt16BigEndian(ionosonde[8..], TenKhz(reading.Mufd100));
        BinaryPrimitives.WriteUInt16BigEndian(ionosonde[10..], TenKhz(reading.Mufd500));
        BinaryPrimitives.WriteUInt16BigEndian(ionosonde[12..], TenKhz(reading.Mufd1000));
        return bytes;
    }

    /// <summary>The reading as a ready-to-send object: dictionary 0, one symbol of the object's own length rounded up to the alignment.</summary>
    public static TransferObject ToTransferObject(IonoReading reading, int alignment = MailcastFrame.StandardAlignment) =>
        TransferObject.ForRecord((byte)ObjectKind.Propagation, Encode(reading).AsSpan(1), alignment);

    /// <summary>
    /// Reads an ionosonde reading back from an object of content type 4. False for a source that
    /// is not an ionosonde (its common part is there for a reader that wants it), for anything
    /// cut short, and for record version 0; a later version is read as far as version 1 goes.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> objectBytes, out IonoReading reading)
    {
        reading = IonoReading.None;
        if (objectBytes.Length < 1 + CommonLength || objectBytes[0] != (byte)ObjectKind.Propagation)
        {
            return false;
        }
        var record = objectBytes[1..];
        if (record[0] == 0 || record[1] is not ((byte)IonoSource.Giro or (byte)IonoSource.PropQuest) || record.Length < Length)
        {
            return false;
        }
        byte verdicts = record[7];
        var ionosonde = record[CommonLength..];
        string station = Encoding.ASCII.GetString(ionosonde[..5]).Trim();
        reading = new IonoReading
        {
            Source = (IonoSource)record[1],
            SoundingTimeUtc = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(record[2..]) * 60L),
            AgeMinutes = record[6],
            At100 = (PathVerdict)(verdicts & 0x03),
            At500 = (PathVerdict)((verdicts >> 2) & 0x03),
            At1000 = (PathVerdict)((verdicts >> 4) & 0x03),
            State = (IonoState)((verdicts >> 6) & 0x03),
            SkipZoneKm = record[8] == NoSkipZone ? null : record[8] * 10,
            Station = station.Length == 0 ? null : station,
            Method = (MufMethod)(ionosonde[5] & 0x03),
            FoF2 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(ionosonde[6..])),
            Mufd100 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(ionosonde[8..])),
            Mufd500 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(ionosonde[10..])),
            Mufd1000 = Mhz(BinaryPrimitives.ReadUInt16BigEndian(ionosonde[12..])),
        };
        return true;
    }

    private static ushort TenKhz(double? mhz) =>
        mhz is { } v && v > 0 ? (ushort)Math.Clamp((int)Math.Round(v * 100, MidpointRounding.AwayFromZero), 0, NoValue - 1) : NoValue;

    private static double? Mhz(ushort tenKhz) => tenKhz == NoValue ? null : tenKhz / 100.0;
}
