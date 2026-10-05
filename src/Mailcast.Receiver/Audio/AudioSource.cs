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
}

/// <summary>The config's "audio" setting, parsed.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Target">The ALSA device, the full ubersdr: string, or the WAV file's path.</param>
public sealed record AudioSource(AudioSourceKind Kind, string Target)
{
    /// <summary>Parses the setting. Throws <see cref="ConfigException"/> for one that cannot work.</summary>
    public static AudioSource Parse(string? setting)
    {
        string value = setting?.Trim() ?? "";
        if (value.Length == 0)
        {
            throw new ConfigException(
                "\"audio\" is empty: give an ALSA device such as plughw:CARD=Device,DEV=0, "
                + "a web receiver such as ubersdr:wessex.zapto.org, or a recording as wav:/path/file.wav");
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
        _ => Target,
    };
}
