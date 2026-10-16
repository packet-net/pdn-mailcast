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
        Assert.Equal("password:", result.Detail);
    }

    /// <summary>
    /// Issue #134: LinBPQ compares the login with its USER= lines case-sensitively (strcmp in
    /// TelnetV6.c), and answers one it has no line for with "user:" again, not "password:".
    /// </summary>
    [Fact]
    public async Task TestLogin_LoginInTheWrongCase_SaysTheLoginWasNotRecognisedForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Host = "127.0.0.1", Port = bpq.FbbPort, Login = LinBpqContainer.Login.ToLowerInvariant(), Password = LinBpqContainer.Password },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.LoginNotRecognised, result.Outcome);
        Assert.Equal("user:", result.Detail);
    }

    /// <summary>
    /// Issue #134: the right login and password on LinBPQ's ordinary telnet port (TCPPORT) come
    /// back "user:password:", because that port takes one line at a time. It must say it is the
    /// wrong port, not that the password is wrong.
    /// </summary>
    [Fact]
    public async Task TestLogin_OrdinaryTelnetPort_SaysItIsNotTheFbbPortForReal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartAsync(deadline.Token);
        var client = new BbsClient(new BbsSettings { Host = "127.0.0.1", Port = bpq.TelnetPort, Login = LinBpqContainer.Login, Password = LinBpqContainer.Password },
            TimeProvider.System, output.WriteLine);

        var result = await client.TestLoginAsync(deadline.Token);

        Assert.Equal(BbsLoginTestOutcome.NotFbbPort, result.Outcome);
        Assert.Contains("user:", result.Detail, StringComparison.Ordinal);
    }
}
