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

    /// <summary>
    /// LinBPQ ends a configured CTEXT with a bare CR, so the answer to PASSWORD shares the
    /// CTEXT's line; it is still read as the answer (issue #156).
    /// </summary>
    [Fact]
    public async Task Ctext_EndingInABareCr_StillLetsTheAnswerToPasswordBeRead()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { Ctext = "Welcome to GB7TST\r\nPlease behave" };
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Equal(["PASSWORD", "XMITOFF 2 1"], fake.Commands);
    }

    /// <summary>
    /// LinBPQ's FBBPORT sends no prompts and nothing for a login it takes (issue #158): the
    /// user and password go without waiting for prompts, and the commands work as on the TCPPORT.
    /// </summary>
    [Fact]
    public async Task FbbPort_LogsInWithoutPromptsAndSetsReadsAndNames()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { Fbb = true };
        await using var node = new BpqNode(fake.Settings());
        Assert.Null(node.PortKind);

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));
        Assert.Equal("HF through QtSoundModem 2", await node.PortIdAsync(2, CancellationToken.None));
        Assert.True(await node.SetTransmitOffAsync(2, false, CancellationToken.None));

        Assert.Equal(BpqPortKind.Fbb, node.PortKind);
        Assert.Equal(1, fake.Connections);
        Assert.Equal(["PASSWORD", "XMITOFF 2 1", "XMITOFF 2", "PORTS", "XMITOFF 2", "XMITOFF 2 0"], fake.Commands);
        Assert.Equal(0, fake.XmitOff(2));
    }

    [Fact]
    public async Task TelnetPort_IsToldApartByItsUserPrompt()
    {
        await using var fake = new FakeLinBpqNode(1, 2);
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.TransmitOffAsync(2, CancellationToken.None));

        Assert.Equal(BpqPortKind.Telnet, node.PortKind);
    }

    [Theory]
    [InlineData("nobody", FakeLinBpqNode.Password, true, "LinBPQ does not know the user nobody on its Telnet port (user names are case-sensitive)")]
    [InlineData(FakeLinBpqNode.User, "not-it-at-all", true, "LinBPQ refused the password for sysop")]
    [InlineData(FakeLinBpqNode.User, FakeLinBpqNode.Password, false, "LinBPQ did not give sysop sysop status")]
    public async Task FbbPort_RefusedLogins_SayWhyAndNeverThePassword(string user, string password, bool sysop, string said)
    {
        await using var fake = new FakeLinBpqNode(1, 2) { Fbb = true, Sysop = sysop };
        await using var node = new BpqNode(fake.Settings(user: user, password: password));

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.StartsWith(said, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, e.Message, StringComparison.Ordinal);
        Assert.False(e.MaybeApplied);
        Assert.DoesNotContain("XMITOFF 2 1", fake.Commands);
        Assert.Equal(0, fake.XmitOff(2));
    }

    [Fact]
    public async Task FbbPort_IdleTimeout_IsNoticedAndTheNextCommandLogsInAgain()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { Fbb = true };
        await using var node = new BpqNode(fake.Settings());
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SessionLost += why => lost.TrySetResult(why);
        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));

        fake.IdleOut();

        Assert.Contains("ended the node session", await lost.Task.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));
        Assert.Equal(["PASSWORD", "XMITOFF 2 1", "PASSWORD", "XMITOFF 2"], fake.Commands);
    }

    [Fact]
    public async Task SlowWelcome_IsWaitedForBeforePassword()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { WelcomeDelay = TimeSpan.FromSeconds(1) };
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Equal(["PASSWORD", "XMITOFF 2 1"], fake.Commands);
    }

    [Fact]
    public async Task PasswordLost_IsSentOnceMore()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { PasswordsLost = 1 };
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Equal(["PASSWORD", "PASSWORD", "XMITOFF 2 1"], fake.Commands);
        Assert.Equal(1, fake.XmitOff(2));
    }

    /// <summary>Both answered, late: the second Ok must not be taken for the answer to XMITOFF.</summary>
    [Fact]
    public async Task PasswordAnsweredLate_BothAnswersAreLetPastBeforeTheFirstCommand()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { FirstPasswordDelay = TimeSpan.FromSeconds(6) };
        await using var node = new BpqNode(fake.Settings());

        Assert.False(await node.SetTransmitOffAsync(2, true, CancellationToken.None));
        Assert.True(await node.TransmitOffAsync(2, CancellationToken.None));

        Assert.Equal(["PASSWORD", "PASSWORD", "XMITOFF 2 1", "XMITOFF 2"], fake.Commands);
    }

    [Fact]
    public async Task PasswordNeverAnswered_QuotesWhatLinBpqSent()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { PasswordsLost = 2, Ctext = "Welcome to GB7TST" };
        await using var node = new BpqNode(fake.Settings());

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Contains("after the login it sent \"Welcome to GB7TST\"", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reply holding the password is not quoted at all, so where it fell gives nothing away;
    /// nor is any reply while the password is under 8 characters. Others are quoted.
    /// </summary>
    [Theory]
    [InlineData("longer-pw-123", "Eh? G4TST longer-pw-123", "(reply not shown)")]
    [InlineData("s3cret-sysop", "Eh?", "\"Eh?\"")]
    [InlineData("pw7", "Eh?", "(reply not shown)")]
    [InlineData("pw7", "Hello pw7 there", "(reply not shown)")]
    public async Task Replies_HoldingThePasswordOrWithAShortOne_AreNotQuoted(string password, string garble, string said)
    {
        await using var fake = new FakeLinBpqNode(1, 2) { LoginPassword = password, GarbleAfter = "XMITOFF 2 1", Garble = garble };
        await using var node = new BpqNode(fake.Settings(password: password));

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Equal($"LinBPQ answered \"XMITOFF 2 1\" with {said}, which says neither yes nor no", e.Message);
        Assert.DoesNotContain(password, e.Message, StringComparison.Ordinal);
        Assert.True(e.MaybeApplied);
    }

    [Fact]
    public async Task PasswordNeverAnswered_SaysWhatLinBpqSentAndNeverThePassword()
    {
        await using var fake = new FakeLinBpqNode(1, 2) { PasswordsLost = 2, Ctext = $"Hello, the password is {FakeLinBpqNode.Password}" };
        await using var node = new BpqNode(fake.Settings());

        var e = await Assert.ThrowsAsync<BpqNodeException>(() => node.SetTransmitOffAsync(2, true, CancellationToken.None));

        Assert.Contains("did not answer PASSWORD, sent twice", e.Message, StringComparison.Ordinal);
        Assert.Contains("after the login it sent (reply not shown)", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeLinBpqNode.Password, e.Message, StringComparison.Ordinal);
        Assert.False(e.MaybeApplied);
        Assert.Equal(["PASSWORD", "PASSWORD"], fake.Commands);
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

    [Theory]
    [InlineData("s3cret-sysop", "HF through QtSoundModem", "\"HF through QtSoundModem\"")]
    [InlineData("s3cret-sysop", "HF s3cret-sysop", "(reply not shown)")]
    [InlineData("pw7", "HF through QtSoundModem", "(reply not shown)")]
    public async Task Quoted_ShowsAPortNameOnlyWhenItGivesNothingAway(string password, string text, string shown)
    {
        await using var node = new BpqNode(new BpqNodeSettings { Host = "127.0.0.1", Port = 1, User = "sysop", Password = password, HfPort = 2 });

        Assert.Equal(shown, node.Quoted(text));
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
