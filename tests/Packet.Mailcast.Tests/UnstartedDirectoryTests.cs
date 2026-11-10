namespace Packet.Mailcast.Tests;

/// <summary>
/// The directory lists a bulletin from the slot in which its first piece is sent until it retires, and never one that has not started.
/// </summary>
public class UnstartedDirectoryTests
{
    private static readonly DateTimeOffset Slot = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private static readonly ScheduleOptions Options = ScheduleOptions.HourlyBudget with { Budget = new BudgetRule { Bulk = new HashSet<string>(["LU9DCE"]) } };

    private static SlotBudget Frames(int frames) => new(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static CarriedBulletin Held(int seed, int size, uint nextEsi = 0, DateTimeOffset? firstSlot = null, string? from = null)
    {
        var b = TestBulletins.Make(seed, size);
        var transfer = TransferObject.ForBulletin(b, Options.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);
        return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 11, 10), transfer, nextEsi, firstSlot, From: from, TakenIn: Slot.AddHours(-3));
    }

    private static bool Listed(SlotBroadcast plan, CarriedBulletin c) => plan.Directory.Entries.Any(e => e.Bid == c.Bid);

    [Fact]
    public void Unstarted_NotGettingPieces_IsNotListed()
    {
        var started = Held(1, 6000, 2, Slot.AddHours(-1));
        var bulk = Enumerable.Range(10, 30).Select(i => Held(i, 6000, from: "LU9DCE")).ToList();
        // Little room: the digests wait at the back, so most get nothing, and the directory lists only those that do.
        var plan = BroadcastScheduler.Plan([started, .. bulk], Slot, 1, Compression.Default, Options, null, Frames(16));
        var sent = plan.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Bid!);
        Assert.True(Listed(plan, started));
        Assert.Contains(bulk, c => sent[c.Bid].Count == 0);
        Assert.All(bulk, c => Assert.Equal(sent[c.Bid].Count > 0, Listed(plan, c)));
        Assert.Equal(1 + bulk.Count, plan.OwnInRotation);
        Assert.Equal(1 + bulk.Count(c => sent[c.Bid].Count > 0), plan.Directory.Entries.Count);
    }

    [Fact]
    public void StartedButUnsentThisSlot_StaysListed()
    {
        // A started bulletin well covered gets nothing when the room goes to a fresher one, but leaving the directory would
        // make receivers drop it.
        var fresh = Held(1, 6000);
        var old = Held(2, 6000, 3, Slot.AddHours(-5));
        var plan = BroadcastScheduler.Plan([fresh, old], Slot, 1, Compression.Default, Options, null, Frames(10));
        var scheduled = plan.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Bid!);
        Assert.Equal(0, scheduled[old.Bid].Count);
        Assert.True(Listed(plan, old));
        Assert.True(Listed(plan, fresh));
    }

    [Fact]
    public void FirstPieceThisSlot_IsListed()
    {
        var fresh = Held(1, 6000);
        var plan = BroadcastScheduler.Plan([fresh], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.True(plan.Objects.Single(o => o.Bid == fresh.Bid).Count > 0);
        Assert.True(Listed(plan, fresh));
    }

    [Fact]
    public void HeldWithSentEsisButNoFirstSlot_CountsAsStarted()
    {
        var old = Held(1, 6000, 3);
        Assert.True(BroadcastScheduler.Started(old));
        Assert.False(BroadcastScheduler.Started(Held(2, 6000)));
        var normal = Enumerable.Range(20, 5).Select(i => Held(i, 6000, 1, Slot.AddHours(-1))).ToList();
        var plan = BroadcastScheduler.Plan([old, .. normal], Slot, 1, Compression.Default, Options, null, Frames(13));
        Assert.True(Listed(plan, old));
    }

    [Fact]
    public void Retired_IsNotListed()
    {
        var k = Held(1, 6000).Transfer.SourceSymbols;
        var retired = Held(1, 6000, (uint)(10 * k), Slot.AddHours(-5));
        var live = Held(2, 6000, 1, Slot.AddHours(-1));
        var plan = BroadcastScheduler.Plan([retired, live], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.False(Listed(plan, retired));
        Assert.True(Listed(plan, live));
    }

    [Fact]
    public void ListingTheStarting_AlsoTakesTheirEnvelopeAndKeepsTheDirectorySmaller()
    {
        var started = Held(1, 6000, 2, Slot.AddHours(-1));
        var bulk = Enumerable.Range(10, 30).Select(i => Held(i, 6000, from: "LU9DCE")).ToList();
        var tight = BroadcastScheduler.Plan([started, .. bulk], Slot, 1, Compression.Default, Options, null, Frames(16));
        var plenty = BroadcastScheduler.Plan([started, .. bulk], Slot, 1, Compression.Default, Options, null, Frames(5000));
        Assert.True(tight.Objects[0].Transfer.SourceSymbols < plenty.Objects[0].Transfer.SourceSymbols);
        Assert.All(bulk, c => Assert.True(Listed(plenty, c)));
    }
}
