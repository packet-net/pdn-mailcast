using Mailcast.Receiver.Delivery;
using Xunit.Abstractions;

namespace Mailcast.Receiver.Tests;

/// <summary>"Test BBS login" against a real XRouter in docker, set up from XRouter/.</summary>
[Trait("Category", "Docker")]
public class BbsLoginTestXRouterTests(ITestOutputHelper output)
{
    private BbsClient Client(int port, string password, string command = "BBS") =>
        new(new BbsSettings { Type = BbsKind.XRouter, Host = "127.0.0.1", Port = port, Login = XRouterContainer.Login, Password = password, Command = command },
            TimeProvider.System, output.WriteLine);

    [Fact]
    public async Task TestLogin_GoodPassword_ReachesTheMailboxForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var xrouter = await XRouterContainer.StartAsync(deadline.Token);

        var result = await Client(xrouter.TelnetPort, XRouterContainer.Password).TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task TestLogin_WrongPassword_SaysSoForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var xrouter = await XRouterContainer.StartAsync(deadline.Token);

        var result = await Client(xrouter.TelnetPort, "not-the-password").TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.WrongPassword, result.Outcome);
    }

    [Fact]
    public async Task TestLogin_UnknownCommand_SaysSoForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var xrouter = await XRouterContainer.StartAsync(deadline.Token);

        var result = await Client(xrouter.TelnetPort, XRouterContainer.Password, "MAIL").TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.UnexpectedReply, result.Outcome);
        Assert.Contains("did not know the command", result.Said, StringComparison.Ordinal);
    }
}
