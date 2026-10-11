using Mailcast.Receiver.Delivery;
using Xunit.Abstractions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// "Test BBS login" against a real Linux FBB in docker, set up from Fbb/start.sh exactly as a
/// sysop sets up Q0CAST, per docs/receiver.md. Checks that <see cref="BbsClient.TestLoginAsync"/>
/// tells a good password from a wrong one for real, and that a correctly set-up forwarding
/// partner (the BBS flag) is reported Ok rather than NotForwardingPartner (issue #108: FBB never
/// sends LinBPQ's "de CALL>" prompt, so Classify's regex for it never matched FBB at all).
/// </summary>
[Trait("Category", "Docker")]
public class BbsLoginTestFbbTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TestLogin_GoodPasswordOnAForwardingPartner_ReachesTheForwardingPromptForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fbb = await FbbContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Type = BbsKind.Fbb, Host = "127.0.0.1", Port = fbb.TelnetPort, Login = FbbContainer.Login, Password = FbbContainer.Password },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task TestLogin_WrongPassword_SaysSoForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fbb = await FbbContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Type = BbsKind.Fbb, Host = "127.0.0.1", Port = fbb.TelnetPort, Login = FbbContainer.Login, Password = "not-the-password" },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.WrongPassword, result.Outcome);
    }

    /// <summary>
    /// Coordinator follow-up to #108: a login FBB has registered once before (not a forwarding
    /// partner, the BBS flag never set) reaches a bare prompt too, once reconnected, so
    /// TestLoginAsync cannot tell it apart from a real partner and still says Ok, but hedged.
    /// </summary>
    [Fact]
    public async Task TestLogin_AlreadyRegisteredNonPartner_SaysOkButHedgesForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fbb = await FbbContainer.StartAsync(deadline.Token);
        await fbb.RegisterPlainUserAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Type = BbsKind.Fbb, Host = "127.0.0.1", Port = fbb.TelnetPort, Login = FbbContainer.PlainLogin, Password = FbbContainer.PlainPassword },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Contains("can't confirm the BBS flag", result.Said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coordinator follow-up to #108: whether the harmless SID-and-FF handshake a real delivery
    /// session opens with when it has nothing to send (<see cref="BbsClient"/>'s Run.FeedAsync)
    /// can tell a correctly set-up forwarding partner apart from an already-registered login that
    /// is not one. Against a real FBB 7.0.11, both answer FQ identically and close cleanly, so the
    /// handshake does not tell them apart either; this is why TestLoginAsync hedges rather than
    /// trying it. Also checks the probe leaves nothing behind on the BBS: no message taken, no
    /// stuck session (FbbContainer's own disposal would otherwise hang the test run).
    /// </summary>
    [Fact]
    public async Task Handshake_PartnerAndAlreadyRegisteredNonPartner_BothAnswerFqIdentically()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fbb = await FbbContainer.StartAsync(deadline.Token);
        await fbb.RegisterPlainUserAsync(deadline.Token);

        string? partnerReply = await fbb.ProbeHandshakeAsync(FbbContainer.Login, FbbContainer.Password, deadline.Token);
        string? nonPartnerReply = await fbb.ProbeHandshakeAsync(FbbContainer.PlainLogin, FbbContainer.PlainPassword, deadline.Token);

        output.WriteLine($"partner: {partnerReply}");
        output.WriteLine($"non-partner: {nonPartnerReply}");
        Assert.NotNull(partnerReply);
        Assert.NotNull(nonPartnerReply);
        Assert.Contains("FQ", partnerReply, StringComparison.Ordinal);
        Assert.Contains("FQ", nonPartnerReply, StringComparison.Ordinal);
        Assert.Empty(await fbb.ReadAllBulletinsAsync(deadline.Token));
    }
}
