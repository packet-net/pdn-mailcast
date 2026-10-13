using Packet.SoundModem.Windows;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A Windows sound card (issue #119): the setting as Windows reads it, and choosing the
/// recording device it names. Both are kept apart from the devices, so they run anywhere.
/// </summary>
public class WindowsSoundCardTests
{
    private static RadioInterface Card(string name, string fullName, string id) =>
        new(name, RadioInterfaceKind.SoundCard, new AudioEndpoint(id, fullName, name, AudioFlow.Capture, Guid.NewGuid()), null, null, null, Guid.NewGuid());

    private static readonly RadioInterface Aioc = Card("AIOC Audio", "Microphone (AIOC Audio)", "{0.0.1.00000000}.{aioc}");
    private static readonly RadioInterface Laptop = Card("Realtek Audio", "Microphone Array (Realtek Audio)", "{0.0.1.00000000}.{laptop}");

    [Theory]
    [InlineData("soundcard:Microphone (AIOC Audio)", "Microphone (AIOC Audio)")]
    [InlineData("SoundCard: AIOC Audio ", "AIOC Audio")]
    public void Parse_OnWindows_ASoundCardIsItsName(string setting, string name)
    {
        var source = AudioSource.Parse(setting, windows: true);

        Assert.Equal(AudioSourceKind.WindowsSoundCard, source.Kind);
        Assert.Equal(name, source.Target);
        Assert.True(source.IsSoundCard);
        Assert.Equal("soundcard:" + name, source.ToString());
    }

    [Fact]
    public void Parse_OnWindows_TheOtherSourcesAreAsOnLinux()
    {
        Assert.Equal(AudioSourceKind.UberSdr, AudioSource.Parse("ubersdr:wessex.zapto.org", windows: true).Kind);
        Assert.Equal(AudioSourceKind.SoundModem, AudioSource.Parse("soundmodem", windows: true).Kind);
        Assert.Equal(AudioSourceKind.Wav, AudioSource.Parse(@"wav:C:\slot.wav", windows: true).Kind);
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0")]
    [InlineData("default")]
    public void Parse_OnWindows_AnAlsaDevice_IsRefusedNamingTheWindowsForms(string setting)
    {
        var e = Assert.Throws<ConfigException>(() => AudioSource.Parse(setting, windows: true));

        Assert.Contains("on Windows", e.Message, StringComparison.Ordinal);
        Assert.Contains("soundcard:", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OnWindows_EmptyOrNameless_SaysTheWindowsForm()
    {
        Assert.Contains("soundcard:", Assert.Throws<ConfigException>(() => AudioSource.Parse("", windows: true)).Message, StringComparison.Ordinal);
        Assert.Contains("needs the recording device's name", Assert.Throws<ConfigException>(() => AudioSource.Parse("soundcard:", windows: true)).Message, StringComparison.Ordinal);
        Assert.Contains("ALSA", Assert.Throws<ConfigException>(() => AudioSource.Parse("", windows: false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OnLinux_NothingChanges()
    {
        Assert.Equal(AudioSourceKind.Alsa, AudioSource.Parse("plughw:CARD=Device,DEV=0", windows: false).Kind);
        Assert.Equal(AudioSourceKind.Alsa, AudioSource.Parse("soundcard:AIOC Audio", windows: false).Kind);
    }

    [Theory]
    [InlineData("Microphone (AIOC Audio)")]
    [InlineData("aioc audio")]
    [InlineData("{0.0.1.00000000}.{aioc}")]
    public void Choose_ByFullNameDeviceNameOrId(string wanted)
    {
        Assert.Equal("{0.0.1.00000000}.{aioc}", WindowsSoundCards.Choose([Laptop, Aioc], wanted).Id);
    }

    [Fact]
    public void Choose_NothingByThatName_SaysWhatThereIs()
    {
        var e = Assert.Throws<AudioSourceException>(() => WindowsSoundCards.Choose([Laptop, Aioc], "Digirig"));

        Assert.Contains("soundcard:Realtek Audio, soundcard:AIOC Audio", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAlike_AreToldApartByTheirFullNames()
    {
        var first = Card("USB Audio Device", "Microphone (USB Audio Device)", "{one}");
        var second = Card("USB Audio Device", "Microphone (2- USB Audio Device)", "{two}");

        var e = Assert.Throws<AudioSourceException>(() => WindowsSoundCards.Choose([first, second], "USB Audio Device"));
        Assert.Contains("soundcard:Microphone (USB Audio Device) or soundcard:Microphone (2- USB Audio Device)", e.Message, StringComparison.Ordinal);
        Assert.Equal("{two}", WindowsSoundCards.Choose([first, second], "Microphone (2- USB Audio Device)").Id);
        Assert.Equal(["Microphone (USB Audio Device)", "Microphone (2- USB Audio Device)", "AIOC Audio"], WindowsSoundCards.Names([first, second, Aioc]));
    }

    [Fact]
    public void AnInterfaceWithoutARecordingDevice_IsNotOffered()
    {
        var speaker = new RadioInterface("Speakers", RadioInterfaceKind.SoundCard, null, new AudioEndpoint("{r}", "Speakers (Realtek)", "Realtek", AudioFlow.Render, null), null, null, null);

        Assert.Equal(["AIOC Audio"], WindowsSoundCards.Names([speaker, Aioc]));
        Assert.Throws<AudioSourceException>(() => WindowsSoundCards.Choose([speaker], "Speakers"));
    }

    [Fact]
    public void TheDefaults_AreLinuxsOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(@"pdn-mailcast\receiver.json", ReceiverConfig.DefaultPath, StringComparison.Ordinal);
            Assert.EndsWith(@"pdn-mailcast\state", new ReceiverConfig().StateDirectory, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("/etc/pdn-mailcast/receiver.json", ReceiverConfig.DefaultPath);
            Assert.Equal("/var/lib/pdn-mailcast", new ReceiverConfig().StateDirectory);
        }
    }
}
