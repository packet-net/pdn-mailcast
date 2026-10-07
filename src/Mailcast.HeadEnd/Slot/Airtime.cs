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

/// <summary>
/// A whole slot's time on the air, as the planner budgets it: the start (the Flex, the KISS port
/// and the lease), the calibration tone, the channel probe after it and the pause, the bursts as the runner packs
/// them, each with the station's own delay, the gaps between them, and the closing ident.
/// </summary>
/// <remarks>
/// The fixed parts were measured on GB7RDG. The slot of 2026-10-06 09:00 UTC (33 frames of 271
/// octets on WN4, 6 bursts, a 10 s tone) took 119.7 s from its start to the release, and the one
/// of 2026-10-05 16:00 (41 frames, 7 bursts, a 30 s tone) 161.5 s. Of that, about 3 s went
/// before the tone, the tone took about 1 s more than its length, and each burst about 2.5 s
/// more than the modem's own airtime for it (gathering, keying and the acknowledgement). The
/// station's closing ident takes about 8 s after the release. The channel probe adds its gap and
/// its length to the tone's keyup (<see cref="ChannelProbe.Airtime"/>, about 8 s), counted whether
/// or not the station turns out to send it.
/// </remarks>
public sealed class SlotAirtime(SlotSettings settings, IAirtime airtime)
{
    /// <summary>From the slot's start to the lease: reading the Flex, opening the KISS port, taking the lease.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);

    /// <summary>How much longer the tone takes than its length, from asking to its answer.</summary>
    public static readonly TimeSpan ToneDelay = TimeSpan.FromSeconds(1);

    /// <summary>How much longer each burst takes than the modem's airtime for it.</summary>
    public static readonly TimeSpan BurstDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>The longest the modem may gather a burst's first frame before it contends for the channel.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromSeconds(1);

    /// <summary>The station's closing CW ident after the release.</summary>
    public static readonly TimeSpan ClosingIdent = TimeSpan.FromSeconds(8);

    /// <summary>The airtime model for one burst.</summary>
    public IAirtime Airtime { get; } = airtime ?? throw new ArgumentNullException(nameof(airtime));

    private readonly SlotSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// How AX.25 frames of these lengths split into bursts: as many as fit in
    /// <see cref="SlotSettings.MaxBurst"/> less two seconds, or <see cref="SlotSettings.FramesPerBurst"/>
    /// when set. At least one each.
    /// </summary>
    public IReadOnlyList<int> BurstSizes(IReadOnlyList<int> lengths)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        var sizes = new List<int>();
        TimeSpan target = _settings.MaxBurst - TimeSpan.FromSeconds(2);
        int next = 0;
        var burst = new List<int>();
        while (next < lengths.Count)
        {
            int count = 0;
            burst.Clear();
            while (next + count < lengths.Count)
            {
                if (_settings.FramesPerBurst is int fixedCount)
                {
                    if (count == fixedCount)
                    {
                        break;
                    }
                }
                else
                {
                    burst.Add(lengths[next + count]);
                    if (count > 0 && Airtime.Burst(burst) > target)
                    {
                        break;
                    }
                }
                count++;
            }
            sizes.Add(count);
            next += count;
        }
        return sizes;
    }

    /// <summary>The bursts alone, as the modem sends them: no tone, idents, gaps or delays.</summary>
    public TimeSpan Bursts(IReadOnlyList<int> lengths, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(sizes);
        TimeSpan total = TimeSpan.Zero;
        int next = 0;
        foreach (int n in sizes)
        {
            total += Airtime.Burst([.. lengths.Skip(next).Take(n)]);
            next += n;
        }
        return total;
    }

    /// <summary>
    /// The whole slot for AX.25 frames of these lengths, in sending order, from its start to the end
    /// of the closing ident; zero for no frames, since such a slot keys nothing.
    /// </summary>
    public TimeSpan Slot(IReadOnlyList<int> lengths) => Slot(lengths, BurstSizes(lengths));

    /// <summary>The whole slot for frames in bursts of <paramref name="sizes"/>.</summary>
    public TimeSpan Slot(IReadOnlyList<int> lengths, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count == 0)
        {
            return TimeSpan.Zero;
        }
        TimeSpan tone = _settings.ToneLength > TimeSpan.Zero ? _settings.ToneLength + ChannelProbe.Airtime + ToneDelay + _settings.PauseAfterTone : TimeSpan.Zero;
        return StartDelay + tone + Bursts(lengths, sizes) + (BurstDelay * sizes.Count) + (_settings.BurstGap * (sizes.Count - 1)) + ClosingIdent;
    }

    /// <summary>The whole slot for mailcast frames of these payload lengths, each sent as an AX.25 UI frame.</summary>
    public TimeSpan ForPayloads(IReadOnlyList<int> payloadLengths)
    {
        ArgumentNullException.ThrowIfNull(payloadLengths);
        return Slot([.. payloadLengths.Select(l => l + Packet.Mailcast.MailcastFrame.Ax25UiOverhead)]);
    }
}
