using Packet.SoundModem.SdrConnect;
using Packet.SoundModem.UberSdr;

namespace Mailcast.Receiver;

/// <summary>What kind of thing the audio comes from.</summary>
public enum AudioSourceKind
{
    /// <summary>An ALSA capture device.</summary>
    Alsa,

    /// <summary>An UberSDR web receiver.</summary>
    UberSdr,

    /// <summary>A WAV recording, decoded once from start to end.</summary>
    Wav,

    /// <summary>The channel audio stream of a pdn-soundmodem on this machine.</summary>
    SoundModem,

    /// <summary>A Windows recording device, read through WASAPI: the Windows counterpart of <see cref="Alsa"/>.</summary>
    WindowsSoundCard,

    /// <summary>
    /// An SDRplay radio through SDRconnect's WebSocket API, usually on another computer: its IQ,
    /// demodulated here, with the receiver tuning it. A radio of your own, heard all the time,
    /// but not a sound card.
    /// </summary>
    SdrConnect,
}

/// <summary>The config's "audio" setting, parsed.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Target">The ALSA device, the full ubersdr: or sdrconnect: string, the WAV file's path, pdn-soundmodem's page port, or the Windows recording device's name.</param>
public sealed record AudioSource(AudioSourceKind Kind, string Target)
{
    /// <summary>
    /// pdn-soundmodem's own default page port (<c>"waterfall"."port"</c>), which plain
    /// <c>soundmodem</c> means. The receiver never reads pdn-soundmodem's config to find it; a
    /// station on another port is named as <c>soundmodem:PORT</c>.
    /// </summary>
    public const int DefaultSoundModemPort = 8107;

    /// <summary>The setting's own word for pdn-soundmodem on this machine.</summary>
    public const string SoundModemWord = "soundmodem";

    /// <summary>The setting's own word for a Windows recording device, as <c>soundcard:NAME</c>.</summary>
    public const string WindowsSoundCardWord = "soundcard";

    /// <summary>The setting's own word for SDRconnect, as <c>sdrconnect:HOST[:PORT]</c>.</summary>
    public const string SdrConnectWord = "sdrconnect";

    /// <summary>Whether this is a sound card on this machine (ALSA, or a Windows recording device), whose level is the operator's to set.</summary>
    public bool IsSoundCard => Kind is AudioSourceKind.Alsa or AudioSourceKind.WindowsSoundCard;

    /// <summary>pdn-soundmodem's page port, for a <see cref="AudioSourceKind.SoundModem"/> source; 0 for any other.</summary>
    public int SoundModemPort => Kind == AudioSourceKind.SoundModem ? int.Parse(Target, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>SDRconnect's address, for a <see cref="AudioSourceKind.SdrConnect"/> source.</summary>
    /// <exception cref="InvalidOperationException">This is not an SDRconnect source.</exception>
    public SdrConnectEndpoint SdrConnectEndpoint => Kind == AudioSourceKind.SdrConnect
        && SdrConnectEndpoint.TryParse(Target[(SdrConnectWord.Length + 1)..], out var endpoint, out _)
        ? endpoint
        : throw new InvalidOperationException($"{this} is not SDRconnect");

    /// <summary>Parses the setting, as this machine reads it. Throws <see cref="ConfigException"/> for one that cannot work.</summary>
    public static AudioSource Parse(string? setting) => Parse(setting, OperatingSystem.IsWindows());

    /// <summary>
    /// Parses the setting as Windows (<paramref name="windows"/>) or Linux reads it: a sound card
    /// is <c>soundcard:NAME</c> on Windows and an ALSA device anywhere else.
    /// </summary>
    internal static AudioSource Parse(string? setting, bool windows)
    {
        string value = setting?.Trim() ?? "";
        if (value.Length == 0)
        {
            throw new ConfigException(windows
                ? "\"audio\" is empty: give a sound card by its Windows name such as soundcard:Microphone (USB Audio Device), "
                    + "a web receiver such as ubersdr:wessex.zapto.org, pdn-soundmodem on this machine as soundmodem, "
                    + "SDRconnect on another computer as sdrconnect:192.168.1.20, or a recording as wav:C:\\slot.wav"
                : "\"audio\" is empty: give an ALSA device such as plughw:CARD=Device,DEV=0, "
                    + "a web receiver such as ubersdr:wessex.zapto.org, pdn-soundmodem on this machine as soundmodem, "
                    + "SDRconnect on another computer as sdrconnect:192.168.1.20, or a recording as wav:/path/file.wav");
        }

        if (value.Equals(SoundModemWord, StringComparison.OrdinalIgnoreCase))
        {
            return new AudioSource(AudioSourceKind.SoundModem, DefaultSoundModemPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (value.StartsWith(SoundModemWord + ":", StringComparison.OrdinalIgnoreCase))
        {
            string port = value[(SoundModemWord.Length + 1)..].Trim();
            return int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 65535
                ? new AudioSource(AudioSourceKind.SoundModem, number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : throw new ConfigException(Ascii.Clean(
                    $"\"audio\": soundmodem:{port} needs pdn-soundmodem's page port, a number from 1 to 65535, such as soundmodem:8108; plain soundmodem means its usual {DefaultSoundModemPort}"));
        }

        if (value.StartsWith("wav:", StringComparison.OrdinalIgnoreCase))
        {
            string path = value[4..].Trim();
            return path.Length == 0
                ? throw new ConfigException("\"audio\": wav: needs the path of the recording, such as wav:/tmp/slot.wav")
                : new AudioSource(AudioSourceKind.Wav, path);
        }

        if (value.StartsWith(SdrConnectWord + ":", StringComparison.OrdinalIgnoreCase))
        {
            string where = value[(SdrConnectWord.Length + 1)..].Trim();
            if (!SdrConnectEndpoint.TryParse(where, out _, out _))
            {
                throw new ConfigException(Ascii.Clean(where.Length == 0
                    ? "\"audio\": sdrconnect: needs the computer SDRconnect runs on, such as sdrconnect:192.168.1.20"
                    : $"\"audio\": sdrconnect:{where} is not a computer and a port. Give the computer SDRconnect runs on, such as "
                        + $"sdrconnect:192.168.1.20, or sdrconnect:192.168.1.20:5455 if its WebSocket port is not the usual {SdrConnectProtocol.DefaultPort} "
                        + "(an IPv6 address goes in brackets, such as sdrconnect:[fd00::5])"));
            }
            return new AudioSource(AudioSourceKind.SdrConnect, SdrConnectWord + ":" + where);
        }

        if (UberSdrDevice.IsUberSdr(value))
        {
            try
            {
                _ = UberSdrDevice.Parse(value);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException)
            {
                throw new ConfigException(Ascii.Clean(e.Message));
            }
            return new AudioSource(AudioSourceKind.UberSdr, value);
        }

        if (!windows)
        {
            return new AudioSource(AudioSourceKind.Alsa, value);
        }

        if (value.StartsWith(WindowsSoundCardWord + ":", StringComparison.OrdinalIgnoreCase))
        {
            string name = value[(WindowsSoundCardWord.Length + 1)..].Trim();
            return name.Length == 0
                ? throw new ConfigException("\"audio\": soundcard: needs the recording device's name as Windows shows it, such as soundcard:Microphone (USB Audio Device)")
                : new AudioSource(AudioSourceKind.WindowsSoundCard, name);
        }

        // There is no ALSA on Windows: anything else would have been an ALSA device.
        throw new ConfigException(Ascii.Clean(
            $"\"audio\": {value} is not something the receiver can open on Windows. Give a sound card by its Windows name, such as "
            + "soundcard:Microphone (USB Audio Device) (the status page's settings list the ones it can see), "
            + "a web receiver such as ubersdr:wessex.zapto.org, pdn-soundmodem on this machine as soundmodem, "
            + "SDRconnect on another computer as sdrconnect:192.168.1.20, or a recording as wav:C:\\slot.wav"));
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        AudioSourceKind.Wav => "wav:" + Target,
        AudioSourceKind.SoundModem => SoundModemPort == DefaultSoundModemPort ? SoundModemWord : $"{SoundModemWord}:{Target}",
        AudioSourceKind.WindowsSoundCard => $"{WindowsSoundCardWord}:{Target}",
        _ => Target,
    };
}
