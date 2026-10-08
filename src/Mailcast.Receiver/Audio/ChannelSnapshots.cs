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
    /// <summary>What measured it: <c>bursts</c> (decoded data, re-modulated) or <c>probe</c> (the sounding probe after the tone).</summary>
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

    /// <summary>
    /// How far a path's fitted delay is moved by each hertz of its Doppler shift, in seconds; the
    /// analysis adds back this times each mode's own shift. Zero for the bursts; the probe's
    /// Zadoff-Chu sequence couples delay and Doppler (<see cref="ProbeChannel.DelayPerHz"/>).
    /// </summary>
    public double DelayPerHzSeconds { get; init; }

    /// <summary>
    /// What one path looks like in the estimates, against its delay in seconds, when it is not
    /// quite the raised cosine (<see cref="SymbolRate"/>, <see cref="RollOff"/>): the probe's,
    /// exactly as its truncated pulse makes it. Null for the raised cosine.
    /// </summary>
    public Func<double, double>? Pulse { get; init; }

    /// <summary>How far before the profile's peak paths are looked for, in seconds: an earlier, weaker mode is missed beyond it.</summary>
    public double SearchBeforeSeconds { get; init; } = ChannelAnalysis.SearchBeforeSeconds;

    /// <summary>A path is kept if its power is this far above the profile's floor, in dB.</summary>
    public double FloorMarginDb { get; init; } = ChannelAnalysis.FloorMarginDb;

    /// <summary>
    /// Whether the Doppler spectra are taken over the longest unbroken run of good snapshots
    /// rather than all of them with the bad ones zeroed: for estimates whose gaps are long
    /// stretches (the probe's, after lost audio), which would otherwise read as a spread.
    /// </summary>
    public bool ContiguousDoppler { get; init; }

    /// <summary>
    /// With <see cref="ContiguousDoppler"/>, the snapshots where the signal was not there at all
    /// (a stretch of audio lost), as against ones merely unfit (a crash): the run is never
    /// carried across these. Null for none.
    /// </summary>
    public bool[]? Missing { get; init; }

    /// <summary>With <see cref="ContiguousDoppler"/>, the shortest run a Doppler spread is given for; a shorter one gives the shift only.</summary>
    public int FewestDopplerSnapshots { get; init; }

    /// <summary>Paths closer than this are one mode, in seconds.</summary>
    public double ModeGapSeconds { get; init; } = ChannelAnalysis.ModeGapSeconds;

    /// <summary>Whether the fitted delays are refined below the path search's 5 us lattice: worth it where the estimates are clean enough (the probe's).</summary>
    public bool FinerDelays { get; init; }

    /// <summary>One path's response at <paramref name="seconds"/> from its delay.</summary>
    public double PulseAt(double seconds) => Pulse is { } pulse ? pulse(seconds) : ChannelMaths.RaisedCosine(seconds, SymbolRate, RollOff);

    /// <summary>The number of delays in each estimate.</summary>
    public int Lags => Estimates.Length == 0 ? 0 : Estimates[0].Length;

    /// <summary>The delay of value <paramref name="i"/>, in seconds.</summary>
    public double Lag(int i) => FirstLagSeconds + (i * LagStepSeconds);
}
