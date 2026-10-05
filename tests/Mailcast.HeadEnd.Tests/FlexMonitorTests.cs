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
            Assert.Null(monitor.Reference.GpsLocked);

            await radio.InjectStatusAsync("S12345678|radio oscillator state=gpsdo setting=gpsdo locked=1");
            await Until(() => monitor.Reference.GpsLocked is not null);
            Assert.True(monitor.Reference.GpsLocked);
            Assert.StartsWith("GPS locked", monitor.Reference.Summary, StringComparison.Ordinal);

            radio.PushMeters((9, (short)(45 * 64)));
            await Until(() => monitor.PaTemperatureC is not null);
            Assert.Equal(45.0, monitor.PaTemperatureC);

            await radio.InjectStatusAsync("S12345678|radio oscillator state=tcxo setting=gpsdo locked=1");
            await Until(() => monitor.Reference.GpsLocked == false);
            Assert.StartsWith("NOT GPS locked", monitor.Reference.Summary, StringComparison.Ordinal);
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
