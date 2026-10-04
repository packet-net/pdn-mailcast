namespace Mailcast.Receiver;

/// <summary>The broadcast's fixed details, from docs/design.md.</summary>
public static class OnAir
{
    /// <summary>The station that sends the broadcast.</summary>
    public const string Source = "GB7RDG";

    /// <summary>The AX.25 destination every broadcast frame carries.</summary>
    public const string Destination = "MCAST";

    /// <summary>The centre of the signal, 7.0515 MHz.</summary>
    public const double CentreHz = 7_051_500;

    /// <summary>The USB dial that puts the centre at <see cref="CentreAudioHz"/>.</summary>
    public const double DialHz = 7_049_700;

    /// <summary>Where the centre and the tone fall in the receiver's audio, the MS110D standard's own centre.</summary>
    public const double CentreAudioHz = CentreHz - DialHz;

    /// <summary>
    /// Half the signal's occupied width: MS110D with the standard's pulse shaping fills about
    /// 2.9 kHz, so its edges sit near 350 and 3250 Hz of audio.
    /// </summary>
    public const double HalfWidthHz = 1450;

    /// <summary>The audio rate the modem and everything else run at.</summary>
    public const int SampleRate = 48_000;
}
