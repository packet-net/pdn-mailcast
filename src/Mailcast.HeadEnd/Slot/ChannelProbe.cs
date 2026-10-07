using Packet.SoundModem.Audio;

namespace Mailcast.HeadEnd.Slot;

/// <summary>
/// The channel-sounding probe that follows the calibration tone in every slot, in the same keyup:
/// pdn-soundmodem's <c>zc255</c> (0.87.2 or later), 1.5 s after the tone, on the tone's own audio
/// frequency. Receivers measure the path's modes, delays and Doppler from it.
/// </summary>
/// <remarks>
/// Always asked for, with no setting: a station too old to know it sends the tone alone, and one
/// that will not send both (a tone so long the two pass its <c>txTest.maxSeconds</c>) gets asked
/// for the tone alone. The probe never costs a slot its tone or its bulletins.
/// </remarks>
public static class ChannelProbe
{
    /// <summary>The probe, as pdn-soundmodem describes it.</summary>
    public static ProbeDescriptor Descriptor => ProbeSignal.Zc255;

    /// <summary>The kind a request names it by.</summary>
    public static string Kind => ProbeSignal.Zc255Kind;

    /// <summary>The silence between the end of the tone and the probe.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(1.5);

    /// <summary>The probe itself, ramps included: 6.5 s.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(ProbeSignal.Zc255.DurationSeconds);

    /// <summary>What it adds to the keyup: the gap and the probe, about 8 s.</summary>
    public static TimeSpan Airtime => Gap + Length;
}
