using System.Net;
using System.Net.Sockets;
using Packet.Fbb;
using Mailcast.Receiver.Delivery;

namespace Mailcast.Receiver.Tests;

public class BbsClientTests
{
    private static BbsClient Client(int port, string password = "secret", Action<string>? log = null) =>
        new(new BbsSettings { Host = "127.0.0.1", Port = port, Login = "Q0CAST", Password = password }, TimeProvider.System, log ?? (_ => { }));

    private static BbsClient FbbClient(int port, string password = "mailcast12") =>
        new(new BbsSettings { Type = BbsKind.Fbb, Host = "127.0.0.1", Port = port, Login = "Q0CAST", Password = password }, TimeProvider.System, _ => { });

    [Fact]
    public async Task Deliver_NewBulletins_BbsTakesThemAsAPartnerWouldSendThem()
    {
        await using var bbs = new FakeBbs();
        var first = Samples.Bulletin(101);
        var second = Samples.Bulletin(102, from: "M0XYZ-7", to: "NEWS", at: "WW", title: "Second, with a long title that goes on for quite a while past forty");

        var report = await Client(bbs.Port).DeliverAsync([first, second], CancellationToken.None);

        Assert.True(report.Graceful, report.Failure);
        Assert.All(report.Outcomes, o => Assert.Equal(DeliveryVerdict.Accepted, o.Verdict));
        Assert.Equal(["Q0CAST", "secret", "BBS"], Assert.Single(bbs.Logins));
        var taken = bbs.Taken.ToArray();
        Assert.Equal(2, taken.Length);

        var fa = Assert.IsType<FaProposal>(taken[0].Proposal);
        Assert.Equal(('B', "G4ABC", "GBR", "ALL", "101_GB7RDG"), (fa.MessageType, fa.From, fa.AtBbs, fa.To, fa.Bid));
        Assert.Equal(first.Title, taken[0].Title);
        Assert.Equal(first.MessageText, taken[0].Body);

        var fa2 = Assert.IsType<FaProposal>(taken[1].Proposal);
        Assert.Equal(("M0XYZ", "WW", "NEWS"), (fa2.From, fa2.AtBbs, fa2.To));
        Assert.Equal(second.Title, taken[1].Title);
        Assert.Equal(second.MessageText, taken[1].Body);
    }

    [Fact]
    public async Task Deliver_BidTheBbsHas_IsAlreadyHad()
    {
        await using var bbs = new FakeBbs();
        bbs.Known["201_GB7RDG"] = true;

        var report = await Client(bbs.Port).DeliverAsync([Samples.Bulletin(201), Samples.Bulletin(202)], CancellationToken.None);

        Assert.True(report.Graceful, report.Failure);
        Assert.Equal([DeliveryVerdict.AlreadyHad, DeliveryVerdict.Accepted], report.Outcomes.Select(o => o.Verdict));
        Assert.Equal("202_GB7RDG", Assert.Single(bbs.Taken).Proposal is FaProposal fa ? fa.Bid : null);
    }

    [Fact]
    public async Task Deliver_BbsOffersMailBack_ReceiverDefersItAndNeverSaysItHasIt()
    {
        await using var bbs = new FakeBbs();
        bbs.Queued.Add(new FbbOutboundMessage
        {
            MessageType = 'P', From = "G4TST", AtBbs = "Q0CAST", To = "Q0CAST", Bid = "1_GB7TST", Title = "hello",
            Body = "R:261004/1200Z 1@GB7TST\r\n\r\nhello\r\n"u8.ToArray(),
        });

        var report = await Client(bbs.Port).DeliverAsync([Samples.Bulletin(301)], CancellationToken.None);

        Assert.True(report.Graceful, report.Failure);
        Assert.Equal(1, report.ReverseOffered);
        Assert.Equal(DeliveryVerdict.Accepted, Assert.Single(report.Outcomes).Verdict);
        Assert.Equal(nameof(FsAnswerKind.Defer), Assert.Single(bbs.ReverseAnswers));
    }

    [Fact]
    public async Task Deliver_BbsOffersMoreThanOneRound_ReceiverHangsUpAndKeepsNothing()
    {
        await using var bbs = new FakeBbs();
        for (int i = 0; i < 6; i++)
        {
            bbs.Queued.Add(new FbbOutboundMessage
            {
                MessageType = 'P', From = "G4TST", AtBbs = "Q0CAST", To = "Q0CAST", Bid = $"{i}_GB7TST", Title = "hello",
                Body = "R:261004/1200Z 1@GB7TST\r\n\r\nhello\r\n"u8.ToArray(),
            });
        }

        var report = await Client(bbs.Port).DeliverAsync([Samples.Bulletin(302)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.Contains("kept offering", report.Failure, StringComparison.Ordinal);
        // Its transfer went before the BBS's turn, so it is offered again and FS - confirms it.
        Assert.Equal(DeliveryVerdict.Unconfirmed, Assert.Single(report.Outcomes).Verdict);
        Assert.All(bbs.ReverseAnswers, a => Assert.Equal(nameof(FsAnswerKind.Defer), a));
    }

    [Fact]
    public async Task Deliver_WrongPassword_FailsWithAHint()
    {
        await using var bbs = new FakeBbs();

        var report = await Client(bbs.Port, password: "wrong").DeliverAsync([Samples.Bulletin(401)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.Contains("USER=", report.Failure, StringComparison.Ordinal);
        Assert.Equal(DeliveryVerdict.NotOffered, Assert.Single(report.Outcomes).Verdict);
    }

    /// <summary>Issue #84: a BBS user with no Name gets LinBPQ's new-user prompt in place of the
    /// forwarding prompt. A real delivery session meeting it must fail plainly, rather than
    /// starting an FBB session over a connection that is actually waiting for a Name (which hangs
    /// until the idle timeout, or fails with a generic protocol error).</summary>
    [Fact]
    public async Task Deliver_LinBpqAsksForAName_FailsWithThePlainExplanation()
    {
        await using var bbs = new FakeBbs { AsksForName = true };

        var report = await Client(bbs.Port).DeliverAsync([Samples.Bulletin(402)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.Contains("Q0CAST", report.Failure, StringComparison.Ordinal);
        Assert.Contains("asking for a name", report.Failure, StringComparison.Ordinal);
        Assert.Contains("BBS Users", report.Failure, StringComparison.Ordinal);
        Assert.Equal(DeliveryVerdict.NotOffered, Assert.Single(report.Outcomes).Verdict);
        Assert.Empty(bbs.Taken);
    }

    [Fact]
    public async Task Deliver_NothingListening_FailsAndOffersNothing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var report = await Client(port).DeliverAsync([Samples.Bulletin(501)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.NotNull(report.Failure);
        Assert.Equal(DeliveryVerdict.NotOffered, Assert.Single(report.Outcomes).Verdict);
    }

    [Fact]
    public async Task Deliver_ConnectionDropsAfterTheTransfer_IsUnconfirmed()
    {
        await using var bbs = new FakeBbs { HangUpAfterTransfer = true };

        var report = await Client(bbs.Port).DeliverAsync([Samples.Bulletin(601)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.Equal(DeliveryVerdict.Unconfirmed, Assert.Single(report.Outcomes).Verdict);
        Assert.Single(bbs.Taken);
    }

    [Fact]
    public async Task Deliver_BidTooLongForFbb_IsRefusedWithoutAConnection()
    {
        var bulletin = Samples.Bulletin(1) is var b
            ? new Packet.Mailcast.Bulletin(b.Type, b.From, b.To, b.At, "1234567890123", b.Title, b.Date, b.RoutingLines, b.Body)
            : null!;

        var report = await Client(1).DeliverAsync([bulletin], CancellationToken.None);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(DeliveryVerdict.Refused, outcome.Verdict);
        Assert.Null(report.Failure);
    }

    [Fact]
    public async Task Deliver_DailyReport_IsProposedAsABulletinToMcastAtGb7rdg()
    {
        await using var bbs = new FakeBbs();
        var report = new Packet.Mailcast.Bulletin(Feedback.FeedbackSettings.Type, "G4ABC", Feedback.FeedbackSettings.To, Feedback.FeedbackSettings.At, "G4ABC_61006",
            "MCR G4ABC 2026-10-06", new DateTimeOffset(2026, 10, 6, 18, 30, 0, TimeSpan.Zero), [], "MCR1 0.6.0 - sc 0/0 0\r\n10 W4 150 16 +1.2 IG\r\n");

        var session = await Client(bbs.Port).DeliverAsync([report], CancellationToken.None);

        Assert.True(session.Graceful, session.Failure);
        Assert.Equal(DeliveryVerdict.Accepted, Assert.Single(session.Outcomes).Verdict);
        var taken = Assert.Single(bbs.Taken);
        var fa = Assert.IsType<FaProposal>(taken.Proposal);
        Assert.Equal(('B', "G4ABC", "GB7RDG.#42.GBR.EURO", "MCAST", "G4ABC_61006"), (fa.MessageType, fa.From, fa.AtBbs, fa.To, fa.Bid));
        Assert.Equal("MCR G4ABC 2026-10-06", taken.Title);
        Assert.Equal(report.Body, taken.Body);
    }

    [Fact]
    public async Task Deliver_DailyReportTheBbsWantsLater_IsDeferred()
    {
        await using var bbs = new FakeBbs();
        bbs.Later["G4ABC_61006"] = true;
        var report = new Packet.Mailcast.Bulletin('B', "G4ABC", "MCAST", "GB7RDG.#42.GBR.EURO", "G4ABC_61006",
            "MCR G4ABC 2026-10-06", new DateTimeOffset(2026, 10, 6, 18, 30, 0, TimeSpan.Zero), [], "MCR1 0.6.0 - sc 0/0 0\r\n");

        var session = await Client(bbs.Port).DeliverAsync([report], CancellationToken.None);

        Assert.True(session.Graceful, session.Failure);
        Assert.Equal(DeliveryVerdict.Deferred, Assert.Single(session.Outcomes).Verdict);
        Assert.Empty(bbs.Taken);
    }

    [Fact]
    public async Task TestLogin_GoodPassword_ReachesTheForwardingPromptAsAPartnerWould()
    {
        await using var bbs = new FakeBbs();

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Null(result.Detail);
        Assert.Equal(["Q0CAST", "secret", "BBS"], Assert.Single(bbs.Logins));
        // No mail was exchanged: it never got the chance to offer or take anything.
        Assert.Empty(bbs.Taken);
    }

    [Fact]
    public async Task TestLogin_WrongPassword_SaysSoWithTheBbsWordsAndWhatWasSent()
    {
        await using var bbs = new FakeBbs();
        var log = new List<string>();

        var result = await Client(bbs.Port, password: "wrong", log: log.Add).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.WrongPassword, result.Outcome);
        Assert.StartsWith("LinBPQ asked for the password again", result.Said, StringComparison.Ordinal);
        Assert.Equal("password:", result.Detail);
        Assert.Equal("the login \"Q0CAST\", a password and the command \"BBS\", each line ending in CR, all at once", result.Sent);
        Assert.Contains(log, l => l.StartsWith("bbs: login test: sent LinBPQ at 127.0.0.1:", StringComparison.Ordinal) && l.Contains("a password and the command", StringComparison.Ordinal));
        // Whether the close is seen too depends on timing: the reply is settled at "password:".
        Assert.Contains(log, l => l.StartsWith("bbs: login test: the BBS answered 9 bytes, \"password:\"", StringComparison.Ordinal));
        Assert.All(log, l => Assert.DoesNotContain("wrong", l, StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
    }

    /// <summary>
    /// Issue #134: LinBPQ compares a login with its USER= lines case-sensitively and answers one
    /// it has no line for with "user:" again. That used to read as "That password is not right.",
    /// and the settings page used to upper-case the login, so a working "m7taw" went as "M7TAW".
    /// </summary>
    [Fact]
    public async Task TestLogin_LoginInTheWrongCase_SaysTheLoginWasNotRecognised()
    {
        await using var bbs = new FakeBbs { Login = "m7taw" };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.LoginNotRecognised, result.Outcome);
        Assert.Contains("\"Q0CAST\"", result.Said, StringComparison.Ordinal);
        Assert.Contains("case-sensitive", result.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("password", result.Said, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user:", result.Detail);
    }

    /// <summary>
    /// Issue #134: LinBPQ's ordinary telnet port opens with telnet negotiation and "user:", then
    /// takes one line at a time, so all three lines at once always come back "user:password:",
    /// right password or not. That is the wrong port, not a refused login.
    /// </summary>
    [Fact]
    public async Task TestLogin_LinBpqOrdinaryTelnetPort_SaysItIsNotTheFbbPort()
    {
        await using var bbs = new FakeBbs { TelnetPort = true };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.NotFbbPort, result.Outcome);
        Assert.Contains("TCPPORT", result.Said, StringComparison.Ordinal);
        Assert.Contains("FBBPORT", result.Said, StringComparison.Ordinal);
        Assert.StartsWith("<FF><FB><03><FF><FB><01>user:", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliver_LoginNotRecognised_LogsTheExchangeWithThePasswordMasked()
    {
        await using var bbs = new FakeBbs { Login = "m7taw" };
        var log = new List<string>();

        var report = await Client(bbs.Port, log: log.Add).DeliverAsync([Samples.Bulletin(404)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.StartsWith("LinBPQ asked for the login again", report.Failure, StringComparison.Ordinal);
        Assert.Contains("it said \"user:\"", report.Failure, StringComparison.Ordinal);
        Assert.Contains(log, l => l.StartsWith("bbs: delivery login: sent LinBPQ at 127.0.0.1:", StringComparison.Ordinal)
            && l.Contains("the login \"Q0CAST\", a password and the command", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("bbs: delivery login: the BBS answered 5 bytes, \"user:\"", StringComparison.Ordinal));
        Assert.All(log, l => Assert.DoesNotContain("secret", l, StringComparison.Ordinal));
    }

    /// <summary>
    /// Issue #134: the login exchange is logged, but nothing about the password ever is, not
    /// even its length, including when the BBS echoes it back, for a login test and a delivery
    /// alike, and for every way a login can go wrong.
    /// </summary>
    [Theory]
    [InlineData("echo")]
    [InlineData("wrong-password")]
    [InlineData("wrong-login")]
    [InlineData("telnet-port")]
    public async Task LoginExchange_LogLinesNeverContainThePassword(string how)
    {
        const string Password = "s3cr3t-Pa55";
        await using var bbs = how switch
        {
            "echo" => new FakeBbs { Password = Password, Garbled = $"you said {Password}, which is wrong\r\n{Password}\r\n" },
            "wrong-login" => new FakeBbs { Password = Password, Login = "m7taw" },
            "telnet-port" => new FakeBbs { Password = Password, TelnetPort = true },
            _ => new FakeBbs(),
        };
        var log = new List<string>();
        var client = Client(bbs.Port, password: Password, log: log.Add);

        var result = await client.TestLoginAsync(CancellationToken.None);
        var report = await client.DeliverAsync([Samples.Bulletin(406)], CancellationToken.None);

        Assert.Contains(log, l => l.StartsWith("bbs: login test: sent ", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("bbs: delivery login: sent ", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("bbs: delivery login: the BBS answered ", StringComparison.Ordinal));
        foreach (string shown in log.Append(result.Said).Append(result.Detail ?? "").Append(result.Sent ?? "").Append(report.Failure ?? ""))
        {
            Assert.DoesNotContain(Password, shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("character", shown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Deliver_GoodLogin_LogsNoLoginExchange()
    {
        await using var bbs = new FakeBbs();
        var log = new List<string>();

        var report = await Client(bbs.Port, log: log.Add).DeliverAsync([Samples.Bulletin(405)], CancellationToken.None);

        Assert.True(report.Graceful, report.Failure);
        Assert.DoesNotContain(log, l => l.Contains("login", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestLogin_NotSetUpAsAPartner_SaysSo()
    {
        await using var bbs = new FakeBbs { NotAPartner = true };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.NotForwardingPartner, result.Outcome);
        Assert.Contains("Q0CAST", result.Said, StringComparison.Ordinal);
    }

    /// <summary>Issue #84: a user with no Name gets LinBPQ's new-user prompt, "Please enter your
    /// Name" followed by a bare '>' (LinBPQ's MailDataDefs.c, NewUserPrompt, hardcoded and never
    /// read from its config). That used to read as the bare-prompt NotForwardingPartner case;
    /// it should instead say plainly that LinBPQ wants a Name for the login.</summary>
    [Fact]
    public async Task TestLogin_LinBpqAsksForAName_SaysSoPlainly()
    {
        await using var bbs = new FakeBbs { AsksForName = true };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.NeedsName, result.Outcome);
        Assert.Contains("Q0CAST", result.Said, StringComparison.Ordinal);
        Assert.Contains("asking for a name", result.Said, StringComparison.Ordinal);
        Assert.Contains("BBS Users", result.Said, StringComparison.Ordinal);
        Assert.Null(result.Detail);
    }

    /// <summary>Issue #108: an FBB login FBB has never seen connect before, which lacks the BBS
    /// flag, gets FBB's own new-user prompt in place of the forwarding prompt. A real delivery
    /// session meeting it must fail plainly, rather than waiting out the idle timeout for an
    /// answer that will never come, as the LinBPQ case above does.</summary>
    [Fact]
    public async Task Deliver_FbbAsksForFirstName_FailsWithThePlainExplanation()
    {
        await using var fbb = new FakeFbb(FbbNotAPartnerReply);

        var report = await FbbClient(fbb.Port).DeliverAsync([Samples.Bulletin(403)], CancellationToken.None);

        Assert.False(report.Graceful);
        Assert.Contains("Q0CAST", report.Failure, StringComparison.Ordinal);
        Assert.Contains("new user", report.Failure, StringComparison.Ordinal);
        Assert.Contains("BBS flag", report.Failure, StringComparison.Ordinal);
        Assert.Equal(DeliveryVerdict.NotOffered, Assert.Single(report.Outcomes).Verdict);
    }

    /// <summary>
    /// Issue #108: a real Linux FBB 7.0.11 never sends LinBPQ's "de CALL>" forwarding prompt, so
    /// Classify's PromptCall regex never matched it, and a correctly set-up FBB forwarding
    /// partner (the BBS flag set) fell through to NotForwardingPartner. These replies are the
    /// exact bytes captured from a real FBB in docker (see tests/Mailcast.Receiver.Tests/Fbb,
    /// from PR #107), for: the BBS flag set, a plain login FBB has never seen before and which
    /// lacks the BBS flag, a login that has registered once before and lacks the BBS flag, and a
    /// wrong password.
    /// </summary>
    private const string FbbForwardingPartnerReply =
        "\r\nLogon Ok. Type NP to change password.\r\n\r\n[FBB-7.0.11-AB1FHMRX$]\rGB7FBB Mailbox, QTH Reading.\r(1) GB7FBB BBS>\r";

    private const string FbbNotAPartnerReply =
        "\r\nLogon Ok. Type NP to change password.\r\n\r\n[FBB-7.0.11-AB1FHMRX$]\rGB7FBB BBS, QTH IO91LK.\rHello ???, you are now on channel 1.\r"
        + "Here are 0 active messages, 100 is last message and \r80 is the last you have listed.\r\rAssigned channels:\r"
        + "Ch. 1   (TELNET) : Q1CAST-0  - Sun 11/10/26 14:03\r\rPlease enter your first name :";

    /// <summary>
    /// A login that registered once (first name, city, home BBS, zip code) and reconnects, still
    /// without the BBS flag. Captured from a real FBB 7.0.11: once registered, it reaches the same
    /// bare prompt shape a real partner does, "(n) CALL BBS (H for help) >", BBS flag or not.
    /// </summary>
    private const string FbbRegisteredNonPartnerReply =
        "\r\nLogon Ok. Type NP to change password.\r\n\r\n[FBB-7.0.11-AB1FHMRX$]\rGB7FBB BBS, QTH IO91LK.\rHello TestUser, you are now on channel 1.\r"
        + "Here are 0 active messages, 100 is last message and \r80 is the last you have listed.\r\rAssigned channels:\r"
        + "Ch. 1   (TELNET) : Q1CAST-0  - Sun 11/10/26 14:45\r\r(1) GB7FBB BBS (H for help) >\r";

    private const string FbbWrongPasswordReply = "Password error !\r\nPassword : ";

    [Fact]
    public async Task TestLogin_Fbb_GoodPasswordOnAForwardingPartner_ReachesTheForwardingPromptForReal()
    {
        await using var fbb = new FakeFbb(FbbForwardingPartnerReply);

        var result = await FbbClient(fbb.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Contains("can't confirm the BBS flag", result.Said, StringComparison.Ordinal);
        Assert.Null(result.Detail);
    }

    /// <summary>
    /// A login FBB has never seen connect before, and which lacks the BBS flag, gets FBB's own
    /// new-user prompt, "Please enter your first name :", instead of the bare forwarding prompt a
    /// BBS-flagged login reaches directly. Unlike LinBPQ's name prompt (#84), giving this login a
    /// name on its own would not make it a forwarding partner (only the BBS flag does), so this is
    /// NotForwardingPartner, not NeedsName.
    /// </summary>
    [Fact]
    public async Task TestLogin_Fbb_NotSetUpAsAPartner_SaysSoForReal()
    {
        await using var fbb = new FakeFbb(FbbNotAPartnerReply);

        var result = await FbbClient(fbb.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.NotForwardingPartner, result.Outcome);
        Assert.Contains("Q0CAST", result.Said, StringComparison.Ordinal);
        Assert.Contains("BBS flag", result.Said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coordinator follow-up to #108: an already-registered login without the BBS flag reaches a
    /// bare prompt too, so Classify cannot confidently call this Ok the way it does for LinBPQ's
    /// "de CALL>". Tried the harmless SID-and-FF handshake a real delivery session opens with when
    /// it has nothing to send (see BbsLoginTestFbbTests.cs for the real-FBB confirmation that both
    /// a partner and this case answer FQ identically), so the login test says Ok but hedges rather
    /// than claiming it confirmed the BBS flag.
    /// </summary>
    [Fact]
    public async Task TestLogin_Fbb_AlreadyRegisteredNonPartner_SaysOkButHedges()
    {
        await using var fbb = new FakeFbb(FbbRegisteredNonPartnerReply);

        var result = await FbbClient(fbb.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.Ok, result.Outcome);
        Assert.Contains("can't confirm the BBS flag", result.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestLogin_Fbb_WrongPassword_SaysSoForReal()
    {
        await using var fbb = new FakeFbb(FbbWrongPasswordReply);

        var result = await FbbClient(fbb.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.WrongPassword, result.Outcome);
        Assert.StartsWith("FBB did not take the password", result.Said, StringComparison.Ordinal);
        Assert.Contains("Password error !", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestLogin_GarbledReply_IsUnexpectedWithItsFirstLine()
    {
        await using var bbs = new FakeBbs { Garbled = "Something has gone wrong here\r\nmore detail that is not the first line" };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.UnexpectedReply, result.Outcome);
        Assert.Equal("Something has gone wrong here", result.Detail);
    }

    /// <summary>
    /// Whether a real BBS ever echoes the password back isn't verified for every kind, so the
    /// password tested is taken out of anything that reaches the reply, not assumed never to appear.
    /// </summary>
    [Fact]
    public async Task TestLogin_BbsEchoesThePasswordBack_IsRedactedFromTheReply()
    {
        await using var bbs = new FakeBbs { Garbled = "you said secret, which is wrong\r\nmore after it" };

        var result = await Client(bbs.Port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.UnexpectedReply, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.DoesNotContain("secret", result.Detail, StringComparison.Ordinal);
        Assert.Contains("***", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestLogin_NothingListening_SaysItCannotReachIt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Client(port).TestLoginAsync(CancellationToken.None);

        Assert.Equal(BbsLoginTestOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Detail);
    }
}
