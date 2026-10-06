using System.Net;

namespace Mailcast.Receiver.Feedback;

/// <summary>What the daily report's header says about where the audio came from.</summary>
internal static class ReportAudio
{
    /// <summary>What the header says for a sound card.</summary>
    public const string SoundCard = "sc";

    /// <summary>What it says for a web SDR on a private address, which is nobody else's business.</summary>
    public const string PrivateSdr = "sdr";

    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".home", ".internal", ".localdomain", ".home.arpa", ".localhost"];

    /// <summary>
    /// A web SDR's host name, lower case and without its port, when it is a public DNS name such
    /// as wessex.zapto.org; <see cref="PrivateSdr"/> for an IP address, localhost, a name with no
    /// dot, or one in a private domain such as .local or .lan.
    /// </summary>
    public static string WebSdr(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.StartsWith('[') || IPAddress.TryParse(name, out _))
        {
            return PrivateSdr;
        }
        if (name.Length == 0 || !name.Contains('.', StringComparison.Ordinal) || name == "localhost"
            || PrivateSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal))
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
        {
            return PrivateSdr;
        }
        return name;
    }

    /// <summary>The header's audio for a config's <c>audio</c>.</summary>
    public static string For(AudioSource source) =>
        source.Kind == AudioSourceKind.UberSdr
            ? WebSdr(Packet.SoundModem.UberSdr.UberSdrDevice.Parse(source.Target).Host)
            : SoundCard;
}
