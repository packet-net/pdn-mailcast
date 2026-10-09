namespace Mailcast.Receiver.Tests;

/// <summary>The settings page's "Measure my filter": who it refuses, outside <see cref="FilterScanTests"/>'s maths.</summary>
public class MeasureFilterTests
{
    [Fact]
    public async Task RefusesForAWebSdr()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });

        var (filter, problem) = await host.MeasureFilterAsync(CancellationToken.None);

        Assert.Null(filter);
        Assert.Contains("sound card", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesForARecording()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "wav:/path/to/file.wav", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });

        var (filter, problem) = await host.MeasureFilterAsync(CancellationToken.None);

        Assert.Null(filter);
        Assert.Contains("sound card", problem, StringComparison.OrdinalIgnoreCase);
    }
}
