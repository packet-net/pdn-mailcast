namespace Mailcast.Receiver;

/// <summary>
/// The transmission's fixed details, from docs/design.md. The callsigns it may come from are a
/// setting: <see cref="ReceiverConfig.Sources"/>.
/// </summary>
public static class OnAir
{
    /// <summary>The AX.25 destination every broadcast frame carries.</summary>
    public const string Destination = "MCAST";

    /// <summary>
    /// GB7RDG's transmitter, fixed: it never moves off 7.0538 MHz, whatever dial a receiver
    /// uses. See <see cref="ReceiverConfig.DialKHz"/> and <see cref="AudioCentreHz"/>.
    /// </summary>
    public const double TransmitHz = 7_053_800;

    /// <summary>
    /// Where the centre of the signal and the tone fall in a receiver's own audio for its USB
    /// dial, the MS110D standard's own centre at the usual 7.052 MHz dial: 1800 Hz. A sound card
    /// behind a rig filter 2.4 kHz or narrower can be set to 7.0523 MHz instead (docs/receiver.md),
    /// which puts it at 1500 Hz. The receiver's own front end (<see cref="ChannelMaths.ToBaseband"/>)
    /// always mixes to this fixed 1800 Hz baseband reference regardless of the dial; what moves
    /// with the dial is <see cref="AudioCentreHz(double)"/>.
    /// </summary>
    public const double CentreAudioHz = 1800;

    /// <summary>
    /// Where <see cref="TransmitHz"/> falls in a receiver's own audio for USB dial
    /// <paramref name="dialHz"/>, Hz: <see cref="CentreAudioHz"/> (1800) at the usual 7.052 MHz
    /// dial, or 1500 Hz at 7.0523 MHz.
    /// </summary>
    public static double AudioCentreHz(double dialHz) => TransmitHz - dialHz;

    /// <summary>
    /// Half the signal's occupied width: MS110D with the standard's pulse shaping fills about
    /// 2.9 kHz, so its edges sit near 350 and 3250 Hz of audio.
    /// </summary>
    public const double HalfWidthHz = 1450;

    /// <summary>How long the steady tone that opens each slot lasts, in seconds.</summary>
    public const int ToneSeconds = 10;

    /// <summary>The audio rate the modem and everything else run at.</summary>
    public const int SampleRate = 48_000;

    /// <summary>A frequency in Hz as MHz for a person: 7.052, 7.0538, 7.05225.</summary>
    public static string Mhz(double hz) => (hz / 1e6).ToString("0.000###", System.Globalization.CultureInfo.InvariantCulture);
}
