namespace Mailcast.HeadEnd.Tests;

public class ConfigTests
{
    [Fact]
    public void Parse_TheSmallestConfigurationIsTheApiKey()
    {
        var config = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}}""");
        Assert.Equal("GB7RDG", config.Callsign);
        Assert.Equal(new TimeOnly(12, 0), config.SlotTime);
        var slot = config.ToSlotSettings();
        Assert.Equal(TimeSpan.FromMinutes(2), slot.ChannelWait);
        Assert.Equal(TimeSpan.FromSeconds(30), slot.RenewEvery);
        Assert.Equal(32 * 1024, config.ToScheduleOptions().MaxBulletinSize);
    }

    [Fact]
    public void Parse_TheExampleFileIsValid()
    {
        string example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));

        // As shipped it has no API key, so the service refuses to start until one is set.
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(example));
        Assert.Contains("apiKey", e.Message, StringComparison.Ordinal);

        var config = HeadEndConfig.Parse(example.Replace("\"apiKey\": \"\"", "\"apiKey\": \"k\"", StringComparison.Ordinal));
        Assert.NotNull(config.Intake.Fbb);
        Assert.Equal(FbbIntakeConfig.LinBpqFbbPort, config.Intake.Fbb.Login);
    }

    [Theory]
    [InlineData("""{"station": {}}""", "apiKey")]
    [InlineData("""{"station": {"apiKey": "k", "leaseSeconds": 60}}""", "leaseSeconds")]
    [InlineData("""{"station": {"apiKey": "k"}, "slot": {"timeUtc": "noon"}}""", "timeUtc")]
    [InlineData("""{"station": {"apiKey": "k"}, "colour": "blue"}""", "colour")]
    [InlineData("""{"station": {"apiKey": "k"}, "callsign": "NOT A CALL"}""", "callsign")]
    [InlineData("""{"station": {"apiKey": "k"}, "flex": {"enabled": true}}""", "flex")]
    public void Parse_RefusesWhatCannotWork(string json, string mentions)
    {
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(json));
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }
}

public class ClockSyncTests
{
    [Fact]
    public void KernelClockSync_AnswersWithoutThrowing()
    {
        // Whether this machine is synchronised is not the test's business; that the kernel can be asked is.
        var state = new Mailcast.HeadEnd.Slot.KernelClockSync().Check();
        Assert.False(string.IsNullOrWhiteSpace(state.Detail));
        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(state.Synchronised);
        }
    }
}
