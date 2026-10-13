using System.Runtime.Versioning;
using Packet.SoundModem.Windows;

namespace Mailcast.Receiver;

/// <summary>
/// Windows recording devices, through pdn-soundmodem-windows: finding the one the config names
/// and opening it with WASAPI. Nothing here runs anywhere but Windows; the choosing is kept apart
/// from the devices so it can be tested on any machine.
/// </summary>
public static class WindowsSoundCards
{
    /// <summary>
    /// The recording device <paramref name="wanted"/> names, among <paramref name="interfaces"/>:
    /// by its full Windows name ("Microphone (AIOC Audio)"), else by the device's own name as
    /// <see cref="RadioInterfaces.Discover"/> gives it ("AIOC Audio") when only one device has it,
    /// else by its endpoint ID. Case does not matter.
    /// </summary>
    /// <exception cref="AudioSourceException">None matches, or the device's own name fits more than one; the message lists what there is.</exception>
    public static AudioEndpoint Choose(IReadOnlyList<RadioInterface> interfaces, string wanted)
    {
        var captures = interfaces.Where(i => i.Capture is not null).ToList();
        var byFullName = captures.Where(i => Same(i.Capture!.Name, wanted)).ToList();
        if (byFullName.Count >= 1)
        {
            return byFullName[0].Capture!;
        }
        var byDevice = captures.Where(i => Same(i.Name, wanted) || Same(i.Capture!.DeviceName, wanted)).ToList();
        if (byDevice.Count == 1)
        {
            return byDevice[0].Capture!;
        }
        if (byDevice.Count > 1)
        {
            throw new AudioSourceException(Ascii.Clean(
                $"more than one Windows recording device is called {wanted}: name one by its full name, as soundcard:"
                + string.Join(" or soundcard:", byDevice.Select(i => i.Capture!.Name))));
        }
        var byId = captures.FirstOrDefault(i => Same(i.Capture!.Id, wanted));
        if (byId is not null)
        {
            return byId.Capture!;
        }
        throw new AudioSourceException(Ascii.Clean(captures.Count == 0
            ? $"there is no Windows recording device called {wanted}: Windows lists no recording devices at all. Is the sound card plugged in and enabled?"
            : $"there is no Windows recording device called {wanted}. Windows has: " + string.Join(", ", Names(interfaces).Select(n => "soundcard:" + n))));
    }

    /// <summary>
    /// The name to give each recording device in <paramref name="interfaces"/> as the config
    /// takes it: the device's own name where that is unique, else its full Windows name, which
    /// Windows numbers apart ("Microphone (2- USB Audio Device)").
    /// </summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<RadioInterface> interfaces)
    {
        var captures = interfaces.Where(i => i.Capture is not null).ToList();
        return [.. captures.Select(i =>
            captures.Count(o => Same(o.Name, i.Name)) == 1 ? i.Name
            : captures.Count(o => Same(o.Capture!.Name, i.Capture!.Name)) == 1 ? i.Capture!.Name
            : i.Capture!.Id)];
    }

    /// <summary>The recording devices this machine has, as <c>soundcard:</c> settings; none if Windows cannot list them.</summary>
    [SupportedOSPlatform("windows")]
    public static string[] Settings()
    {
        try
        {
            return [.. Names(RadioInterfaces.Discover()).Select(n => $"{AudioSource.WindowsSoundCardWord}:{n}")];
        }
#pragma warning disable CA1031 // a list for the page: no failure in it may reach the page
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    /// <summary>Opens the recording device <paramref name="wanted"/> names, mono at <paramref name="sampleRate"/>.</summary>
    /// <returns>The input, and the device's full Windows name for the log.</returns>
    /// <exception cref="AudioSourceException">It is not there, or cannot be opened.</exception>
    [SupportedOSPlatform("windows")]
    public static (WasapiAudioInput Input, string Name) Open(string wanted, int sampleRate)
    {
        AudioEndpoint endpoint;
        try
        {
            endpoint = Choose(RadioInterfaces.Discover(), wanted);
        }
        catch (Exception e) when (e is not AudioSourceException)
        {
            throw new AudioSourceException($"cannot list the Windows recording devices: {Ascii.Clean(e.Message)}");
        }
        try
        {
            return (new WasapiAudioInput(endpoint.Id, sampleRate), endpoint.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            throw new AudioSourceException($"cannot open the sound card {Ascii.Clean(endpoint.Name)}: {Ascii.Clean(e.Message)}");
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
