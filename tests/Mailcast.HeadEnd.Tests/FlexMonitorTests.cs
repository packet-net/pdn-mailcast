using M0LTE.Flex;
using Mailcast.HeadEnd.Flex;

namespace Mailcast.HeadEnd.Tests;

public class FlexMonitorTests
{
    private static async Task Until(Func<bool> condition)
    {
        // Waits for the mock's packets to arrive over loopback; bounded, never a timing check.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, limit.Token);
        }
    }

    [Fact]
    public async Task Monitor_ReadsReferenceAndPaTemperature_AndSendsOnlyReads()
    {
        await using var radio = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        radio.Start();
        string[] log;
        await using (var monitor = new FlexMonitor("127.0.0.1", radio.TcpPort, referenceWait: TimeSpan.Zero))
        {
            Assert.True(await monitor.ConnectAsync(CancellationToken.None), monitor.Problem);
            Assert.Null(monitor.Reference.Disciplined);

            await radio.InjectStatusAsync("S12345678|radio oscillator state=gpsdo setting=gpsdo locked=1");
            await Until(() => monitor.Reference.Disciplined is not null);
            Assert.True(monitor.Reference.Disciplined);
            Assert.Equal("internal GPSDO, locked", monitor.Reference.Summary);

            radio.PushMeters((9, (short)(45 * 64)));
            await Until(() => monitor.PaTemperatureC is not null);
            Assert.Equal(45.0, monitor.PaTemperatureC);

            await radio.InjectStatusAsync("S12345678|radio oscillator state=tcxo setting=gpsdo locked=1");
            await Until(() => monitor.Reference.Disciplined == false);
            Assert.Equal("its own TCXO, though set to internal GPSDO", monitor.Reference.Summary);
        }
        log = [.. radio.CommandLog];

        // Everything sent was a read or this client's own subscription; nothing that could take
        // pdn-soundmodem's slice, its DAX streams or the transmitter.
        Assert.NotEmpty(log);
        Assert.All(log, c => Assert.True(
            c.StartsWith("client udpport ", StringComparison.Ordinal)
            || c is "sub radio all" or "meter list" or "sub meter all" or "unsub meter all" or "keepalive enable" or "ping"
            || (c.StartsWith("sub meter ", StringComparison.Ordinal) && int.TryParse(c["sub meter ".Length..], out _)),
            $"the monitor sent '{c}'"));
        Assert.Contains("sub radio all", log);
        Assert.Contains("meter list", log);
        Assert.Contains("keepalive enable", log);
    }

    /// <summary>
    /// GB7RDG's FLEX-6500 has no internal GPSDO (gps="Not Present") and runs on an external 10 MHz from a GPSDO: that is a
    /// true reference, and the head end said "NOT GPS locked" about it every slot until November 2026.
    /// </summary>
    [Theory]
    [InlineData("external", "external", true, "external 10 MHz, locked", true)]
    [InlineData("external", "external", false, "external 10 MHz, NOT locked", false)]
    [InlineData("gpsdo", "gpsdo", true, "internal GPSDO, locked", true)]
    [InlineData("gpsdo", "gpsdo", false, "internal GPSDO, NOT locked", false)]
    [InlineData("tcxo", "tcxo", true, "its own TCXO", false)]
    [InlineData("ocxo", "ocxo", true, "its own OCXO", false)]
    [InlineData("tcxo", "external", true, "its own TCXO, though set to external 10 MHz", false)]
    [InlineData("", "external", true, "set to external 10 MHz, but the source in use not reported", null)]
    [InlineData("", "", false, "unknown", null)]
    public void Read_SaysWhatTheRadioRunsOn_AndOnlyTcxoOcxoOrUnlockedIsNotDisciplined(string state, string setting, bool locked, string summary, bool? disciplined)
    {
        var status = new FlexReferenceStatus(FlexReferenceStatus.ParseSource(setting), FlexReferenceStatus.ParseSource(state), locked, null, setting, state);
        var reading = FlexMonitor.Read(status);
        Assert.Equal(summary, reading.Summary);
        Assert.Equal(disciplined, reading.Disciplined);
        Assert.DoesNotContain("GPS locked", reading.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_TreatsAnOldPaReadingAsNone()
    {
        await using var radio = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        radio.Start();
        var clock = new VirtualTime(DateTimeOffset.UtcNow);
        await using var monitor = new FlexMonitor("127.0.0.1", radio.TcpPort, time: clock, referenceWait: TimeSpan.Zero, staleAfter: TimeSpan.FromSeconds(15));
        Assert.True(await monitor.ConnectAsync(CancellationToken.None), monitor.Problem);
        radio.PushMeters((9, (short)(45 * 64)));
        await Until(() => monitor.PaTemperatureC is not null);
        // A wall-clock step does not age it; time passing does.
        clock.StepWallClock(TimeSpan.FromHours(-2));
        Assert.Equal(45.0, monitor.PaTemperatureC);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(monitor.PaTemperatureC);
    }

    [Fact]
    public async Task Monitor_SaysWhyWhenTheRadioIsNotThere()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        await using var monitor = new FlexMonitor("127.0.0.1", port, connectTimeout: TimeSpan.FromSeconds(30));
        Assert.False(await monitor.ConnectAsync(CancellationToken.None));
        Assert.NotNull(monitor.Problem);
        Assert.False(monitor.Connected);
        Assert.Null(monitor.PaTemperatureC);
    }
}
