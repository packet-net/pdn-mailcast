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
}
