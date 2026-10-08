using Mailcast.Receiver.Delivery;
using Xunit.Abstractions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// "Test BBS login" against a real LinBPQ in docker, set up from LinBpq/bpq32.cfg and
/// linmail.cfg exactly as a sysop sets up Q0CAST, per README.md. Checks that
/// <see cref="BbsClient.TestLoginAsync"/> tells a good password from a wrong one for real.
/// </summary>
[Trait("Category", "Docker")]
public class BbsLoginTestLinBpqTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TestLogin_GoodPassword_ReachesTheForwardingPromptForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Host = "127.0.0.1", Port = bpq.FbbPort, Login = LinBpqContainer.Login, Password = LinBpqContainer.Password },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task TestLogin_WrongPassword_SaysSoForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Host = "127.0.0.1", Port = bpq.FbbPort, Login = LinBpqContainer.Login, Password = "not-the-password" },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.WrongPassword, result.Outcome);
    }
}
