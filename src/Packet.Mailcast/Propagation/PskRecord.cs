using System.Buffers.Binary;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// The PSK Reporter reading on the air: content type 4, source 3. The common part every
/// propagation reading has (see <see cref="IonoRecord"/>), then the window and, for 40 m at
/// each of 100, 500 and 1000 km, the spots, stations and median SNR behind its verdict. 80 m is
/// not sent. The record is 26 octets, so with the type octet the object is 27, one RaptorQ
/// symbol of 28, and any one of its frames rebuilds it.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field (after the content type octet, 4)
///    0-8     9  the common part: version 1, source 3, observation time (the end of the window),
///               age, verdicts and state, skip zone; a verdict here is 0 unknown, 1 closed, 2 open
///      9     1  window, minutes (30)
///     10     1  bits 0-2: closed at 100, 500, 1000 km because 80 m is busy there;
///               bits 3-5: closed there because 40 m is busy at the other distances;
///               bit 7: the feed was down for too much of the window, so nothing is judged
///     11     5  100 km: spots (2), stations (2), median SNR in dB (1, signed; 0x80 for none)
///     16     5  500 km, the same
///     21     5  1000 km, the same
/// </code>
/// <para>All numbers are big-endian; counts stop at 65535. A reader that does not know source 3
/// still has the common part, and a later version only adds octets at the end.</para>
/// </remarks>
public static class PskRecord
{
    /// <summary>The record's length after the type octet.</summary>
    public const int Length = IonoRecord.CommonLength + 17;

    /// <summary>The object's length: the type octet and the record.</summary>
    public const int ObjectLength = Length + 1;

    private const byte NoSkipZone = 0xFF;
    private const sbyte NoSnr = sbyte.MinValue;
    private const byte FeedDownBit = 0x80;

    /// <summary>The object's octets: the content type (4) and the record. The reading must have an observation.</summary>
    public static byte[] Encode(PskReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.ObservedUtc is not { } at)
        {
            throw new ArgumentException("A reading without an observation is not sent.", nameof(reading));
        }
        var bytes = new byte[ObjectLength];
        bytes[0] = (byte)ObjectKind.Propagation;
        var record = bytes.AsSpan(1);
        record[0] = IonoRecord.Version;
        record[1] = (byte)IonoSource.PskReporter;
        BinaryPrimitives.WriteUInt32BigEndian(record[2..], (uint)Math.Clamp(at.ToUnixTimeSeconds() / 60, 0, uint.MaxValue));
        record[6] = (byte)Math.Clamp(reading.AgeMinutes ?? 0, 0, 255);
        record[7] = (byte)(((byte)reading.At100 & 0x03) | (((byte)reading.At500 & 0x03) << 2) | (((byte)reading.At1000 & 0x03) << 4) | (((byte)reading.State & 0x03) << 6));
        record[8] = reading.SkipZoneKm is { } skip ? (byte)Math.Clamp((int)Math.Round(skip / 10.0, MidpointRounding.AwayFromZero), 0, NoSkipZone - 1) : NoSkipZone;

        var own = record[IonoRecord.CommonLength..];
        own[0] = (byte)Math.Clamp(reading.WindowMinutes, 0, 255);
        byte flags = reading.FeedDown ? FeedDownBit : (byte)0;
        for (int i = 0; i < PskEvaluator.Bins.Count; i++)
        {
            var bin = reading.Forty?.At(PskEvaluator.Bins[i].Km);
            var at5 = own[(2 + (5 * i))..];
            if (bin is null)
            {
                at5[4] = unchecked((byte)NoSnr);
                continue;
            }
            if (bin.Verdict == PathVerdict.Closed)
            {
                flags |= bin.ClosedBy switch
                {
                    PskEvidence.EightyMetres => (byte)(1 << i),
                    PskEvidence.OtherDistances => (byte)(1 << (3 + i)),
                    _ => 0,
                };
            }
            BinaryPrimitives.WriteUInt16BigEndian(at5, (ushort)Math.Clamp(bin.Spots, 0, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16BigEndian(at5[2..], (ushort)Math.Clamp(bin.Stations, 0, ushort.MaxValue));
            at5[4] = unchecked((byte)(bin.SnrMedianDb is { } snr ? (sbyte)Math.Clamp(snr, -127, 127) : NoSnr));
        }
        own[1] = flags;
        return bytes;
    }

    /// <summary>The reading as a ready-to-send object: dictionary 0, one symbol of the object's own length rounded up to the alignment.</summary>
    public static TransferObject ToTransferObject(PskReading reading, int alignment = MailcastFrame.StandardAlignment) =>
        TransferObject.ForRecord((byte)ObjectKind.Propagation, Encode(reading).AsSpan(1), alignment);

    /// <summary>
    /// Reads a PSK Reporter reading back from an object of content type 4. False for another
    /// source, anything cut short, and record version 0; a later version is read as far as this one goes.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> objectBytes, out PskReading reading)
    {
        reading = PskReading.None;
        if (objectBytes.Length < 1 + Length || objectBytes[0] != (byte)ObjectKind.Propagation)
        {
            return false;
        }
        var record = objectBytes[1..];
        if (record[0] == 0 || record[1] != (byte)IonoSource.PskReporter)
        {
            return false;
        }
        byte verdicts = record[7];
        var own = record[IonoRecord.CommonLength..];
        byte flags = own[1];
        var bins = new List<PskBin>(PskEvaluator.Bins.Count);
        for (int i = 0; i < PskEvaluator.Bins.Count; i++)
        {
            var (km, from, to) = PskEvaluator.Bins[i];
            var at5 = own[(2 + (5 * i))..];
            var verdict = (PathVerdict)((verdicts >> (2 * i)) & 0x03);
            sbyte snr = unchecked((sbyte)at5[4]);
            var why = verdict != PathVerdict.Closed ? PskEvidence.None
                : (flags & (1 << i)) != 0 ? PskEvidence.EightyMetres
                : (flags & (1 << (3 + i))) != 0 ? PskEvidence.OtherDistances
                : PskEvidence.None;
            bins.Add(new PskBin(km, from, to, verdict, BinaryPrimitives.ReadUInt16BigEndian(at5), BinaryPrimitives.ReadUInt16BigEndian(at5[2..]), snr == NoSnr ? null : snr, why));
        }
        reading = new PskReading
        {
            ObservedUtc = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(record[2..]) * 60L),
            AgeMinutes = record[6],
            State = (IonoState)((verdicts >> 6) & 0x03),
            WindowMinutes = own[0],
            FeedDown = (flags & FeedDownBit) != 0,
            Forty = new PskBandReading(PskBand.Forty, bins, record[8] == NoSkipZone ? null : record[8] * 10),
        };
        return true;
    }
}
