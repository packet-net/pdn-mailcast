using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>How a receiver hears a station on its own frequency (issue #179).</summary>
public enum HowHeard
{
    /// <summary>The station's signal falls in the audio the receiver already has: nothing is retuned.</summary>
    Passband,

    /// <summary>The dial is moved to the station's frequency for its slots (a rig, pdn-soundmodem's receive window, or a web SDR).</summary>
    Retune,
}

/// <summary>Whether a receiver can hear a station, and on which dial.</summary>
/// <param name="Heard">Whether it can.</param>
/// <param name="DialHz">The USB dial to hear it on; meaningful when <paramref name="Heard"/>.</param>
/// <param name="How">How, when <paramref name="Heard"/>.</param>
/// <param name="Why">When not heard, in words, why not.</param>
/// <param name="IfHz">When heard through a downconverting LNB, the intermediate frequency the signal is at (the station's frequency less the LNB's oscillator); null otherwise, when the signal is at the station's own frequency.</param>
public sealed record StationReach(bool Heard, double DialHz, HowHeard? How, string? Why, long? IfHz = null);

/// <summary>
/// Whether a receiver can hear a station on the frequency it says it is on (issue #179). A
/// receiver that cannot move its dial (a sound card, pdn-soundmodem without <c>retune</c>,
/// SDRconnect) hears a station if its signal falls in the passband it already has. One that can
/// (a rig, pdn-soundmodem's receive window, a web SDR) moves the dial to the station's frequency
/// for its slots, as far as HF goes. Above 30 MHz (QO-100's downlink) a station is heard only
/// through a downconverting LNB the listener has set (<see cref="ReceiverConfig.LnbHz"/>, issue
/// #180): the receiver then works at the intermediate frequency, the station's frequency less
/// the LNB's oscillator, and everything it shows is the real frequency.
/// </summary>
public static class StationReaching
{
    /// <summary>The highest frequency a receiver retunes to, Hz: HF. Above it a path is not skywave either.</summary>
    public const long HfMaxHz = StationOnAir.SkywaveMaxHz;

    /// <summary>The lowest intermediate frequency an LNB's output is taken to give, Hz.</summary>
    public const long LnbIfMinHz = 100_000_000;

    /// <summary>The highest intermediate frequency an LNB's output is taken to give, Hz.</summary>
    public const long LnbIfMaxHz = 2_400_000_000;

    /// <summary>The intermediate frequency <paramref name="frequencyHz"/> comes out at behind an LNB with oscillator <paramref name="lnbHz"/>, or null when it is outside the LNB's input range (its oscillator plus 100 MHz to 2400 MHz).</summary>
    public static long? LnbIf(long frequencyHz, long lnbHz) =>
        frequencyHz - lnbHz is >= LnbIfMinHz and <= LnbIfMaxHz and var ifHz ? ifHz : null;

    /// <summary>Judges one station.</summary>
    /// <param name="station">Where it says it is.</param>
    /// <param name="audioCentreHz">Where in the audio the receiver wants a signal: its config's, 1800 Hz at the usual dial.</param>
    /// <param name="currentDialHz">The dial the receiver is on, which fixes what a receiver that cannot retune hears.</param>
    /// <param name="canRetune">Whether the receiver moves its dial for a station's slots.</param>
    public static StationReach Judge(StationOnAir station, double audioCentreHz, double currentDialHz, bool canRetune, long? lnbHz = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        string where = $"{OnAir.Mhz(station.FrequencyHz)} MHz {(station.Sideband == Sideband.Usb ? "USB" : "LSB")}";
        if (station.Sideband != Sideband.Usb)
        {
            return new(false, 0, null, $"it is on {where}, and this receiver only listens for upper sideband");
        }
        long? ifHz = null;
        double signalHz = station.FrequencyHz;
        if (station.FrequencyHz > HfMaxHz)
        {
            if (lnbHz is { } lo)
            {
                if (LnbIf(station.FrequencyHz, lo) is not { } found)
                {
                    return new(false, 0, null, $"it is on {where}, outside the range of the LNB set in lnbHz");
                }
                ifHz = found;
                signalHz = found;
            }
            else if (canRetune)
            {
                return new(false, 0, null, $"it is on {where}, above HF; set lnbHz for a dish and LNB to hear it");
            }
        }
        if (canRetune)
        {
            return new(true, signalHz - audioCentreHz, HowHeard.Retune, null, ifHz);
        }
        double centre = OnAir.AudioCentreHz(currentDialHz, signalHz);
        if (centre >= ReceiverConfig.LowestAudioCentreHz && centre <= ReceiverConfig.HighestAudioCentreHz)
        {
            return new(true, currentDialHz, HowHeard.Passband, null, ifHz);
        }
        return new(false, 0, null, string.Create(CultureInfo.InvariantCulture,
            $"it is on {where}, not where this receiver listens (USB dial {OnAir.Mhz(currentDialHz)} MHz hears {OnAir.Mhz(currentDialHz + ReceiverConfig.LowestAudioCentreHz)} to {OnAir.Mhz(currentDialHz + ReceiverConfig.HighestAudioCentreHz)} MHz), and this receiver does not retune{(station.FrequencyHz > HfMaxHz && lnbHz is null ? "; set lnbHz for a dish and LNB" : "")}"));
    }
}

/// <summary>Where one station is on the air and where it is on the ground, as far as this receiver knows (issue #179).</summary>
/// <param name="Station">Its callsign.</param>
/// <param name="FrequencyHz">Its signal's centre, Hz.</param>
/// <param name="Sideband">Its sideband.</param>
/// <param name="Locator">Its Maidenhead locator; null when no directory or advert gave one.</param>
/// <param name="Source">Where this was read: <c>its own directory</c>, <c>an advert</c>, or <c>assumed</c>.</param>
public sealed record StationOnAir(string Station, long FrequencyHz, Sideband Sideband, string? Locator, string Source)
{
    /// <summary>The highest frequency a path is taken to be skywave (an ionosphere to measure): HF. A satellite downlink is above it.</summary>
    public const long SkywaveMaxHz = 30_000_000;

    /// <summary>Whether its path is skywave HF, so that virtual height, multipath and the path picture mean something.</summary>
    public bool Skywave => FrequencyHz <= SkywaveMaxHz;

    /// <summary>Its place on the ground, when its locator is known.</summary>
    public GroundPlace? Place => GroundPlace.FromLocator(Locator);
}
