using Mailcast.Receiver.Retune;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The XMITOFF exchange against a real LinBPQ 6.0.25.41 in docker, set up from
/// LinBpq/bpq32-node.cfg: a sysop and a plain user on the Telnet port, and port 2 on the shared
/// radio. This is what the fake node in the other tests is checked against.
/// </summary>
[Trait("Category", "Docker")]
public sealed class LinBpqNodeTests
{
    private static BpqNodeSettings Settings(LinBpqContainer bpq, string user = "sysop", string password = "s3cret-sysop", int hfPort = 2) =>
        new() { Host = "127.0.0.1", Port = bpq.TelnetPort, User = user, Password = password, HfPort = hfPort };

    [Fact]
    public async Task RealLinBpq_TakesXmitOffAndSaysNoWhereItShould()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartNodeAsync("bpq32-node.cfg", limit.Token);

        await using (var node = new BpqNode(Settings(bpq)))
        {
            Assert.False(await node.TransmitOffAsync(2, limit.Token));
            Assert.False(await node.SetTransmitOffAsync(2, true, limit.Token));
            Assert.True(await node.TransmitOffAsync(2, limit.Token));
            Assert.True(await node.SetTransmitOffAsync(2, false, limit.Token));
            Assert.False(await node.TransmitOffAsync(2, limit.Token));
        }

        // A second session, after the first has gone, sees what the first left.
        await using (var node = new BpqNode(Settings(bpq)))
        {
            await node.SetTransmitOffAsync(2, true, limit.Token);
        }
        await using (var node = new BpqNode(Settings(bpq)))
        {
            Assert.True(await node.TransmitOffAsync(2, limit.Token));
            await node.SetTransmitOffAsync(2, false, limit.Token);
        }

        foreach (var (settings, said) in new[]
        {
            (Settings(bpq, hfPort: 3), "LinBPQ has no port 3"),
            (Settings(bpq, user: "plain", password: "plain-pw"), "did not give plain sysop status"),
            (Settings(bpq, password: "not-it"), "refused the password for sysop"),
            (Settings(bpq, user: "nobody"), "does not know the user nobody"),
        })
        {
            await using var node = new BpqNode(settings);
            var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(settings.HfPort, true, limit.Token));
            Assert.Contains(said, e.Message, StringComparison.Ordinal);
            Assert.False(e.MaybeApplied);
        }

        await using (var node = new BpqNode(Settings(bpq)))
        {
            Assert.False(await node.TransmitOffAsync(2, limit.Token));
        }
    }

    [Fact]
    public async Task RealLinBpq_NamesItsPorts()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartNodeAsync("bpq32-node.cfg", limit.Token);
        await using var node = new BpqNode(Settings(bpq));

        Assert.Equal("HF through QtSoundModem", await node.PortIdAsync(2, limit.Token));
        Assert.Equal("Telnet", await node.PortIdAsync(1, limit.Token));
        Assert.Null(await node.PortIdAsync(3, limit.Token));
    }

    /// <summary>
    /// LinBPQ's IDLETIME ends an idle node session but keeps the telnet socket, and sysop status
    /// goes with the node session. This waits on the real clock for LinBPQ to do it, which is
    /// the only way to see it happen: up to a few minutes with IDLETIME=120.
    /// </summary>
    [Fact]
    public async Task RealLinBpq_IdleTimeout_IsNoticedAtOnceAndTheNextCommandLogsInAgain()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using var bpq = await LinBpqContainer.StartNodeAsync("bpq32-node-idle.cfg", limit.Token);
        await using var node = new BpqNode(Settings(bpq));
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SessionLost += why => lost.TrySetResult(why);
        Assert.False(await node.SetTransmitOffAsync(2, true, limit.Token));

        string why = await lost.Task.WaitAsync(limit.Token);

        Assert.Contains("ended the node session", why, StringComparison.Ordinal);
        Assert.False(node.Connected);
        Assert.True(await node.TransmitOffAsync(2, limit.Token));
        Assert.True(await node.SetTransmitOffAsync(2, false, limit.Token));
        Assert.False(await node.TransmitOffAsync(2, limit.Token));
    }
}
