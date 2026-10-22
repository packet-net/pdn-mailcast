namespace Mailcast.Receiver.Tests;

/// <summary>
/// The hints for a sound card that will not open. The errors are the ones a Pi gave for a shared
/// dsnoop device named snoop03: defined in ~/.asoundrc, opened first by another user without
/// ipc_perm, and run at 12 kHz.
/// </summary>
public class AlsaOpenFailureTests
{
    [Fact]
    public void ADeviceTheReceiverCannotSee_SaysToMoveItToEtc()
    {
        string said = AudioPipeline.AlsaOpenFailure("snoop03", "snd_pcm_open(snoop03): No such file or directory");

        Assert.StartsWith("cannot open the sound card snoop03: snd_pcm_open(snoop03): No such file or directory. ", said);
        Assert.Contains("~/.asoundrc", said);
        Assert.Contains("/etc/asound.conf", said);
    }

    [Fact]
    public void ADeviceAnotherUserHolds_SaysWhichLinesLetItShare()
    {
        string said = AudioPipeline.AlsaOpenFailure("snoop03", "snd_pcm_open(snoop03): Permission denied");

        Assert.Contains("ipc_perm 0666", said);
        Assert.Contains("ipc_key_add_uid false", said);
    }

    [Fact]
    public void ADeviceAtAnotherRate_SaysToRunItAt48k()
    {
        string said = AudioPipeline.AlsaOpenFailure("snoop03", "snd_pcm_set_params(snoop03, 48000 Hz, 1 ch): Invalid argument");

        Assert.Contains("rate 48000", said);
    }

    [Fact]
    public void PlugInFront_StillGetsTheHint_ForTheNameUnderneath()
    {
        string said = AudioPipeline.AlsaOpenFailure("plug:snoop03", "snd_pcm_open(plug:snoop03): No such file or directory");

        Assert.Contains("If you made snoop03 yourself", said);
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0", "snd_pcm_open(plughw:CARD=Device,DEV=0): No such file or directory")]
    [InlineData("hw:1,0", "snd_pcm_open(hw:1,0): Permission denied")]
    [InlineData("default", "snd_pcm_open(default): No such file or directory")]
    [InlineData("snoop03", "snd_pcm_open(snoop03): Device or resource busy")]
    public void AnythingElse_IsTheErrorAlone(string device, string error)
    {
        Assert.Equal($"cannot open the sound card {device}: {error}", AudioPipeline.AlsaOpenFailure(device, error));
    }
}
