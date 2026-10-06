using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Feedback;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;
using Xunit.Abstractions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The daily report into a real LinBPQ in docker, as the receiver's login sends it: taken as a
/// personal message to M0LTE at GB7RDG, readable back as a report, and not taken twice.
/// </summary>
[Trait("Category", "Docker")]
public class FeedbackLinBpqTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DailyReport_LinBpqTakesItAsPersonalMail_Once()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellation = deadline.Token;
        await using var bpq = await LinBpqContainer.StartAsync(cancellation);
        var client = new BbsClient(new BbsSettings { Host = "127.0.0.1", Port = bpq.FbbPort, Login = LinBpqContainer.Login, Password = LinBpqContainer.Password },
            TimeProvider.System, output.WriteLine);

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var report = new DailyReport("G4ABC", day, new ReportHeader("0.6.0", "IO91lk", "wessex.zapto.org", 3, 3, new Dictionary<string, int> { ["BBS"] = 1 }),
        [
            new ReportSlot(new TimeOnly(10, 0), "W4", 212, 18, 1.2, ["IG"], new ReportChannel(2, 1.9, -17, 0.35, 0.21, 290, 'b')),
            new ReportSlot(new TimeOnly(11, 0), null, 0, null, null, []),
        ]);
        string bid = FeedbackService.Bid("G4ABC", day);
        var mail = new Bulletin('P', "G4ABC", FeedbackSettings.To, FeedbackSettings.At, bid, report.Title,
            DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), [], report.Body);

        var first = await client.DeliverAsync([mail], cancellation);
        Assert.True(first.Graceful, first.Failure);
        Assert.Equal(DeliveryVerdict.Accepted, Assert.Single(first.Outcomes).Verdict);

        var messages = await bpq.ReadAllMessagesAsync(cancellation);
        foreach (var (listing, text) in messages)
        {
            output.WriteLine(listing);
            output.WriteLine(text);
        }
        var (line, message) = Assert.Single(messages);
        Assert.Contains("M0LTE", line, StringComparison.Ordinal);
        Assert.Contains("@GB7RDG", line, StringComparison.Ordinal);
        Assert.Contains("Type/Status: P", message, StringComparison.Ordinal);
        Assert.Contains("From: G4ABC\n", message, StringComparison.Ordinal);
        Assert.Contains("To: M0LTE\n", message, StringComparison.Ordinal);
        Assert.Contains("Bid: " + bid + "\n", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Title: " + report.Title + "\n", message, StringComparison.Ordinal);

        // As a sysop or a tool reads it back from the BBS, routing lines and all: the same report.
        var read = DailyReport.Parse(report.Title, message);
        Assert.Equal(report.Body, read.Body);

        // Offered again (a retry after a session that did not close cleanly): the BBS has the MID.
        var second = await client.DeliverAsync([mail], cancellation);
        Assert.Equal(DeliveryVerdict.AlreadyHad, Assert.Single(second.Outcomes).Verdict);
        Assert.Single(await bpq.ReadAllMessagesAsync(cancellation));
    }
}
