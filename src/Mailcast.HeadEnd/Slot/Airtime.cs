using Packet.SoundModem.Modems;

namespace Mailcast.HeadEnd.Slot;

/// <summary>How long frames take on the air in one packed burst.</summary>
public interface IAirtime
{
    /// <summary>The airtime of one burst carrying AX.25 frames of these lengths, preamble to EOM.</summary>
    TimeSpan Burst(IReadOnlyList<int> frameLengths);
}

/// <summary>
/// A linear airtime model, measured from the modem itself: a fixed cost per burst (preamble,
/// interleaver flush, EOM) and a cost per octet and per frame.
/// </summary>
public sealed class LinearAirtime(TimeSpan perBurst, TimeSpan perFrame, double secondsPerOctet) : IAirtime
{
    /// <summary>The fixed cost of a burst.</summary>
    public TimeSpan PerBurst { get; } = perBurst;

    /// <summary>The fixed cost of each frame in a burst, beyond its octets.</summary>
    public TimeSpan PerFrame { get; } = perFrame;

    /// <summary>Seconds per AX.25 octet, coded.</summary>
    public double SecondsPerOctet { get; } = secondsPerOctet;

    /// <inheritdoc />
    public TimeSpan Burst(IReadOnlyList<int> frameLengths)
    {
        ArgumentNullException.ThrowIfNull(frameLengths);
        return frameLengths.Count == 0
            ? TimeSpan.Zero
            : PerBurst + (PerFrame * frameLengths.Count) + TimeSpan.FromSeconds(SecondsPerOctet * frameLengths.Sum());
    }

    /// <summary>
    /// Measures a modem from the pdn-soundmodem package by modulating single frames of two sizes.
    /// The published package sends one frame per burst, so the per-frame cost inside a packed
    /// burst is taken as the IL2P header and CRC (17 octets) at the measured rate; the rest of a
    /// single burst's fixed cost is the burst's.
    /// </summary>
    public static LinearAirtime Measure(string mode)
    {
        const int sampleRate = 48000;
        IModem modem = ModemCatalog.Create(mode, sampleRate, _ => { });
        double Seconds(int length) => modem.Modulate(new byte[length], 0).Length / (double)sampleRate;
        const int small = 100;
        const int large = 1000;
        double perOctet = (Seconds(large) - Seconds(small)) / (large - small);
        double perFrame = 17 * perOctet;
        double perBurst = Math.Max(0, Seconds(small) - (small * perOctet) - perFrame);
        return new LinearAirtime(TimeSpan.FromSeconds(perBurst), TimeSpan.FromSeconds(perFrame), perOctet);
    }
}
