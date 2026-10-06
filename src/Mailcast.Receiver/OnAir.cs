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
    /// Where the centre of the signal and the tone fall in the receiver's audio, the MS110D
    /// standard's own centre. The receiver's USB dial is a setting
    /// (<see cref="ReceiverConfig.DialKHz"/>); the signal is always this far above it.
    /// </summary>
    public const double CentreAudioHz = 1800;

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
