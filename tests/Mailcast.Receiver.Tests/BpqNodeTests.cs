using Mailcast.Receiver.Retune;

namespace Mailcast.Receiver.Tests;

/// <summary>The XMITOFF client against the fake node: the login, the replies, and each refusal.</summary>
public sealed class BpqNodeTests
{
    [Fact]
    public async Task SetAndRead_LogsInOnceAndConfirms()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));
        Assert.True(await node.SetTransmitOffAsync(2, true, CancellationToken.None));
        Assert.True(await node.SetTransmitOffAsync(2, false, CancellationToken.None));
        Assert.False(await node.TransmitOffAsync(2, CancellationToken.None));

        Assert.Equal(1, fake.Connections);
        Assert.Equal(["PASSWORD", "XMITOFF 2 1", "XMITOFF 2", "XMITOFF 2 1", "XMITOFF 2 0", "XMITOFF 2"], fake.Commands);
        Assert.Equal(0, fake.XmitOff(2));
    }

    [Fact]
    public async Task SessionDropped_SaysSoAtOnceAndTheNextCommandLogsInAgain()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());
        var lost = new TaskCompletionSource<string>();
        node.SessionLost += why => lost.TrySetResult(why);
        await node.SetTransmitOffAsync(2, true, CancellationToken.None);

        fake.Kill();
        Assert.Contains("closed the connection", await lost.Task.WaitAsync(TimeSpan.FromSeconds(30)), StringComparison.Ordinal);
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));
        Assert.Equal(2, fake.Connections);
    }

    [Fact]
    public async Task IdledOut_SaysSoAtOnceAndTheNextCommandLogsInAgain()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());
        var lost = new TaskCompletionSource<string>();
        node.SessionLost += why => lost.TrySetResult(why);
        await node.SetTransmitOffAsync(2, true, CancellationToken.None);

        fake.IdleOut();
        Assert.Contains("ended the node session", await lost.Task.WaitAsync(TimeSpan.FromSeconds(30)), StringComparison.Ordinal);
        Assert.False(node.Connected);
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));
        Assert.Equal(2, fake.Connections);
    }

    [Fact]
    public async Task SysopStatusGoneWithoutAWord_IsTriedOnceMoreOnAFreshLogin()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());
        await node.TransmitOffAsync(2, CancellationToken.None);

        fake.ForgetSysop();
        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Equal(1, fake.XmitOff(2));
        Assert.Equal(2, fake.Connections);
        Assert.Equal(2, fake.Commands.Count(c => c == "XMITOFF 2 1"));
    }

    [Fact]
    public async Task NonsenseOrWrongWayRound_CountsAsMaybeTaken()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());
        fake.GarbleAfter = "XMITOFF 2 1";

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.True(e.MaybeApplied);
        Assert.Contains("neither yes nor no", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortId_IsReadFromPorts()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());

        Assert.Equal("HF through QtSoundModem 2", await node.PortIdAsync(2, CancellationToken.None));
        Assert.Equal("Telnet", await node.PortIdAsync(1, CancellationToken.None));
        Assert.Null(await node.PortIdAsync(3, CancellationToken.None));
    }

    [Theory]
    [InlineData("wrong password", "refused the password for sysop")]
    [InlineData("unknown user", "does not know the user nobody")]
    [InlineData("not sysop", "did not give sysop sysop status")]
    [InlineData("no port", "LinBPQ has no port 2")]
    [InlineData("unreachable", "closed the connection")]
    public async Task Refusals_SayWhyAndNeverQuoteThePassword(string why, string said)
    {
        await using var fake = new FakeLinBpqNode(why == "no port" ? [1] : [1, 2]);
        fake.Sysop = why != "not sysop";
        fake.Accepting = why != "unreachable";
        var settings = why switch
        {
            "wrong password" => fake.Settings(password: "not-it"),
            "unknown user" => fake.Settings(user: "nobody"),
            _ => fake.Settings(),
        };
        await using var node = new BpqNode(settings);

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Contains(said, e.Message, StringComparison.Ordinal);
        Assert.False(e.MaybeApplied, "LinBPQ answered, or was never asked");
        Assert.DoesNotContain(settings.Password, e.Message, StringComparison.Ordinal);
        if (why != "no port")
        {
            Assert.DoesNotContain(fake.Commands, c => c.StartsWith("XMITOFF", StringComparison.Ordinal));
        }
    }
}
