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
}

/// <summary>The config's "audio" setting, parsed.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Target">The ALSA device, the full ubersdr: string, the WAV file's path, or pdn-soundmodem's page port.</param>
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

    /// <summary>pdn-soundmodem's page port, for a <see cref="AudioSourceKind.SoundModem"/> source; 0 for any other.</summary>
    public int SoundModemPort => Kind == AudioSourceKind.SoundModem ? int.Parse(Target, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>Parses the setting. Throws <see cref="ConfigException"/> for one that cannot work.</summary>
    public static AudioSource Parse(string? setting)
    {
        string value = setting?.Trim() ?? "";
        if (value.Length == 0)
        {
            throw new ConfigException(
                "\"audio\" is empty: give an ALSA device such as plughw:CARD=Device,DEV=0, "
                + "a web receiver such as ubersdr:wessex.zapto.org, pdn-soundmodem on this machine as soundmodem, "
                + "or a recording as wav:/path/file.wav");
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

        return new AudioSource(AudioSourceKind.Alsa, value);
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        AudioSourceKind.Wav => "wav:" + Target,
        AudioSourceKind.SoundModem => SoundModemPort == DefaultSoundModemPort ? SoundModemWord : $"{SoundModemWord}:{Target}",
        _ => Target,
    };
}
