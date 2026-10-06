using System.Numerics;

namespace Mailcast.Receiver;

/// <summary>
/// A run of channel estimates: the radio path's impulse response at a row of delays, measured
/// again and again. Whatever measures the path (the bursts decoded, or later a sounding probe)
/// makes one of these, and <see cref="ChannelAnalysis"/> takes it from there.
/// </summary>
/// <remarks>
/// Each estimate is the path as seen through the signal's own pulse: one propagation path shows
/// as a raised cosine (<see cref="ChannelMaths.RaisedCosine"/> at <see cref="SymbolRate"/> and
/// <see cref="RollOff"/>) centred on its delay, which is how paths closer than a symbol apart
/// are still told apart.
/// </remarks>
internal sealed class ChannelSnapshots
{
    /// <summary>What measured it: <c>bursts</c> (decoded data, re-modulated), or later <c>probe</c>.</summary>
    public required string Basis { get; init; }

    /// <summary>The delay of each estimate's first value, in seconds; it is relative to the measurement's own timing.</summary>
    public required double FirstLagSeconds { get; init; }

    /// <summary>The step between the values, in seconds.</summary>
    public required double LagStepSeconds { get; init; }

    /// <summary>The estimates, one row per snapshot, each with one value per delay.</summary>
    public required Complex[][] Estimates { get; init; }

    /// <summary>Whether each snapshot is fit to use: false where the reference did not match what was heard (a block the decoder got wrong).</summary>
    public required bool[] Good { get; init; }

    /// <summary>Seconds between snapshots.</summary>
    public required double SnapshotSeconds { get; init; }

    /// <summary>The pulse's symbol rate, for the path model.</summary>
    public double SymbolRate { get; init; } = ChannelMaths.Baud;

    /// <summary>The pulse's roll-off, for the path model.</summary>
    public double RollOff { get; init; } = 0.35;

    /// <summary>Signal to noise in 3 kHz, in dB, as measured alongside the estimates.</summary>
    public double SnrDb { get; init; } = double.NaN;

    /// <summary>The frequency offset already taken out before estimating, in Hz (the modem's lock): the paths' own offsets add to it.</summary>
    public double OffsetHz { get; init; }

    /// <summary>The number of delays in each estimate.</summary>
    public int Lags => Estimates.Length == 0 ? 0 : Estimates[0].Length;

    /// <summary>The delay of value <paramref name="i"/>, in seconds.</summary>
    public double Lag(int i) => FirstLagSeconds + (i * LagStepSeconds);
}
