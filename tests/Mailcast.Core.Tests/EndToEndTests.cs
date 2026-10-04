using Xunit.Abstractions;

namespace Mailcast.Core.Tests;

/// <summary>
/// The head end's frames through a lossy channel into a receiver's store, with restarts
/// between days. Loss is seeded, so every run sees the same pattern.
/// </summary>
public class EndToEndTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    [Fact]
    public void ThirtyPercentLoss_MostOnDayOne_AllByDayThree()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(20, 25);
        var offered = bulletins.Select(b => new BroadcastBulletin(b, Day1)).ToList();
        var loss = new Random(2026);
        var delivered = new List<Bulletin>();

        for (int day = 0; day < 3; day++)
        {
            var plan = BroadcastScheduler.Plan(offered, Day1.AddDays(day), 100 + day, Compression.Default);
            var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast); // a restart every day
            int heard = 0;
            foreach (var frame in plan.Frames)
            {
                if (loss.NextDouble() < 0.3)
                {
                    continue;
                }
                heard++;
                var result = store.Accept(frame.ToBytes());
                Assert.NotEqual(FrameOutcome.Rejected, result.Outcome);
                if (result.Bulletin is not null)
                {
                    delivered.Add(result.Bulletin);
                }
            }
            output.WriteLine($"day {day + 1}: {plan.Frames.Count} frames sent, {heard} heard, {delivered.Count} of {bulletins.Count} bulletins complete");
            if (day == 0)
            {
                Assert.True(delivered.Count >= bulletins.Count * 3 / 4, $"only {delivered.Count} on day one");
            }
        }

        // Every bulletin arrived, exactly once, exactly as sent.
        Assert.Equal(bulletins.OrderBy(b => b.Bid), delivered.OrderBy(b => b.Bid));
        Assert.Equal(bulletins.Count, delivered.Select(b => b.Bid).Distinct().Count());
    }

    [Fact]
    public void TwoDays_NeitherEnoughAlone_TogetherEnough()
    {
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(30, 30_000);
        var others = TestBulletins.Day(31, 5);
        var offered = others.Append(bulletin).Select(b => new BroadcastBulletin(b, Day1)).ToList();
        ulong id = Ids.Of(bulletin);

        var day1 = BroadcastScheduler.Plan(offered, Day1, 1, Compression.Default);
        var day2 = BroadcastScheduler.Plan(offered, Day1.AddDays(1), 2, Compression.Default);
        int k = day1.Objects.Single(o => o.Transfer.ObjectId == id).Transfer.SourceSymbols;
        var day1Frames = day1.Frames.Where(f => f.ObjectId == id).ToList();
        var day2Frames = day2.Frames.Where(f => f.ObjectId == id).ToList();
        output.WriteLine($"K = {k}; day one sends {day1Frames.Count}, day two {day2Frames.Count}");
        Assert.True(k >= 4, $"K = {k}");
        Assert.True(day2Frames.Count < k, "day two alone must not be enough");

        // Day one: a long fade, so only K - 1 of this bulletin's frames are heard.
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        foreach (var frame in day1Frames.Take(k - 1))
        {
            Assert.Equal(FrameOutcome.Stored, store.Accept(frame.ToBytes()).Outcome);
        }
        Assert.False(store.IsComplete(id));

        // Day two on its own, at a receiver that missed day one, is not enough either.
        using (var other = new TempDirectory())
        {
            var fresh = new ReceiverStore(other.Path, Compression.Default, TestStores.Fast);
            foreach (var frame in day2Frames)
            {
                Assert.Equal(FrameOutcome.Stored, fresh.Accept(frame.ToBytes()).Outcome);
            }
            Assert.False(fresh.IsComplete(id));
        }

        // Day two after a restart, adding to day one's pieces: enough.
        store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Bulletin? completed = null;
        foreach (var frame in day2Frames)
        {
            completed ??= store.Accept(frame.ToBytes()).Bulletin;
        }
        Assert.Equal(bulletin, completed);
        Assert.Equal([bulletin], store.Pending());
    }
}
