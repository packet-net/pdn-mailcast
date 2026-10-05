using System.Net;
using System.Net.Sockets;
using Packet.Fbb;
using Mailcast.Receiver.Delivery;

namespace Mailcast.Receiver.Tests;

public class BbsClientTests
{
    private static BbsClient Client(int port, string password = "secret") =>
        new(new BbsSettings { Host = "127.0.0.1", Port = port, Login = "Q0CAST", Password = password }, TimeProvider.System, _ => { });

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
}
