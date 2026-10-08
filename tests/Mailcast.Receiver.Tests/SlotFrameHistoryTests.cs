using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

public class SlotFrameHistoryTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minute, int second = 0) => Base.AddMinutes(minute).AddSeconds(second);

    private static HeardPiece Piece(DateTimeOffset heard, uint esi = 0, PieceStatus status = PieceStatus.New) =>
        new(heard, esi, status, true, "bulletin 1_GB7RDG, \"test\"");

    [Fact]
    public void Record_SamePiecesWithinAWindow_AreOneBurst()
    {
        var history = new SlotFrameHistory();
        var slot = At(0);
        var started = At(0, 1);

        history.Record(slot, started, "ms110d-wn4", Piece(At(0, 1), 0));
        history.Record(slot, started, "ms110d-wn4", Piece(At(0, 2), 1));

        var kept = Assert.Single(history.Slots);
        Assert.Equal(slot, kept.Slot);
        var burst = Assert.Single(kept.Bursts);
        Assert.Equal(started, burst.Started);
        Assert.Equal("ms110d-wn4", burst.Waveform);
        Assert.Equal(2, burst.Pieces.Count);
        Assert.Equal((uint?)0, burst.Pieces[0].Esi);
        Assert.Equal((uint?)1, burst.Pieces[1].Esi);
    }

    [Fact]
    public void Record_ADifferentBurstStart_IsAnotherBurstInTheSameSlot()
    {
        var history = new SlotFrameHistory();
        var slot = At(0);

        history.Record(slot, At(0, 1), "ms110d-wn4", Piece(At(0, 1)));
        history.Record(slot, At(0, 20), "ms110d-wn4", Piece(At(0, 20)));

        var kept = Assert.Single(history.Slots);
        Assert.Equal(2, kept.Bursts.Count);
    }

    [Fact]
    public void Record_WithoutABurstStart_FallsBackToWhenThePieceWasHeard()
    {
        var history = new SlotFrameHistory();
        var slot = At(0);

        history.Record(slot, null, null, Piece(At(0, 1)));
        history.Record(slot, null, null, Piece(At(0, 2)));

        var kept = Assert.Single(history.Slots);
        Assert.Equal(2, kept.Bursts.Count); // each piece's own heard time is a burst of its own
    }

    [Fact]
    public void Record_MoreThanTheMostSlots_DropsTheOldestFirst()
    {
        var history = new SlotFrameHistory();
        for (int i = 0; i < SlotFrameHistory.MostSlots + 2; i++)
        {
            history.Record(At(i), At(i), null, Piece(At(i)));
        }

        Assert.Equal(SlotFrameHistory.MostSlots, history.Slots.Count);
        Assert.Equal(At(2), history.Slots[0].Slot); // slots 0 and 1 scrolled out
        Assert.Equal(At(SlotFrameHistory.MostSlots + 1), history.Slots[^1].Slot);
    }

    [Fact]
    public void Record_MoreThanTheMostFrames_DropsTheOldestSlot()
    {
        var history = new SlotFrameHistory();
        var slot1 = At(0);
        var slot2 = At(1);
        for (int i = 0; i < SlotFrameHistory.MostFrames; i++)
        {
            history.Record(slot1, At(0, i), null, Piece(At(0, i), (uint)i));
        }
        Assert.Single(history.Slots);

        // One more piece, in a new slot: the frame cap is passed, so the whole of slot1 goes.
        history.Record(slot2, At(1, 0), null, Piece(At(1, 0)));

        var kept = Assert.Single(history.Slots);
        Assert.Equal(slot2, kept.Slot);
    }

    [Fact]
    public void Record_OneSlotAloneOverTheFrameCap_IsNeverDroppedToNothing()
    {
        var history = new SlotFrameHistory();
        var slot = At(0);
        for (int i = 0; i < SlotFrameHistory.MostFrames + 50; i++)
        {
            history.Record(slot, At(0, i % 60), null, Piece(At(0, i % 60), (uint)i));
        }

        var kept = Assert.Single(history.Slots);
        Assert.Equal(SlotFrameHistory.MostFrames + 50, kept.Bursts.Sum(b => b.Pieces.Count));
    }

    [Fact]
    public void OnChannelMeasured_FillsInSnr_ForTheMatchingSlotsBurstsOnly()
    {
        var history = new SlotFrameHistory();
        var slotA = At(0);
        var slotB = At(10);
        history.Record(slotA, At(0, 1), null, Piece(At(0, 1)));
        history.Record(slotB, At(10, 1), null, Piece(At(10, 1)));

        history.OnChannelMeasured(new ChannelReport { Slot = slotA, SnrDb = 12.5 });

        var a = history.Slots.Single(s => s.Slot == slotA);
        var b = history.Slots.Single(s => s.Slot == slotB);
        Assert.Equal(12.5, a.Bursts[0].SnrDb);
        Assert.Null(b.Bursts[0].SnrDb);
    }

    [Fact]
    public void OnChannelMeasured_NeverOverwritesASnrAlreadySet()
    {
        var history = new SlotFrameHistory();
        var slot = At(0);
        history.Record(slot, At(0, 1), null, Piece(At(0, 1)));
        history.OnChannelMeasured(new ChannelReport { Slot = slot, SnrDb = 5 });
        history.OnChannelMeasured(new ChannelReport { Slot = slot, SnrDb = 9, Basis = "probe" });

        Assert.Equal(5, history.Slots.Single().Bursts[0].SnrDb);
    }

    [Fact]
    public void Slot_ForOneNotKept_IsNull()
    {
        var history = new SlotFrameHistory();
        history.Record(At(0), At(0, 1), null, Piece(At(0, 1)));

        Assert.Null(history.Slot(At(99)));
        Assert.NotNull(history.Slot(At(0)));
    }

    private static readonly Bulletin SampleBulletin = Samples.Bulletin(1);

    private static readonly BroadcastDirectory SampleDirectory = new(
        Samples.Day,
        [new DirectoryEntry(42, 0, 100, "1_GB7RDG", "Club news number 1")]);

    [Fact]
    public void HeardPiece_FromNotAFrame_IsNotRecognisedAndCrcBad()
    {
        var piece = HeardPiece.From(new AcceptResult(FrameOutcome.NotAFrame), null, At(0), null);

        Assert.Equal(PieceStatus.NotRecognised, piece.Status);
        Assert.False(piece.CrcGood);
        Assert.Null(piece.Esi);
    }

    [Fact]
    public void HeardPiece_FromStored_IsNewAndNamedFromTheDirectory()
    {
        var piece = HeardPiece.From(new AcceptResult(FrameOutcome.Stored, 42), 3, At(0), SampleDirectory);

        Assert.Equal(PieceStatus.New, piece.Status);
        Assert.True(piece.CrcGood);
        Assert.Equal((uint?)3, piece.Esi);
        Assert.Contains("1_GB7RDG", piece.What, StringComparison.Ordinal);
        Assert.Contains("Club news number 1", piece.What, StringComparison.Ordinal);
    }

    [Fact]
    public void HeardPiece_FromStored_WithNoDirectoryEntry_NamesTheObjectById()
    {
        var piece = HeardPiece.From(new AcceptResult(FrameOutcome.Stored, 99), 0, At(0), SampleDirectory);

        Assert.Equal(PieceStatus.New, piece.Status);
        Assert.Contains(ObjectId.Format(99), piece.What, StringComparison.Ordinal);
    }

    [Fact]
    public void HeardPiece_FromDuplicateOrAlreadyComplete_IsAlreadyHeld()
    {
        var duplicate = HeardPiece.From(new AcceptResult(FrameOutcome.Duplicate, 42), 1, At(0), SampleDirectory);
        var already = HeardPiece.From(new AcceptResult(FrameOutcome.AlreadyComplete, 42), 1, At(0), SampleDirectory);

        Assert.Equal(PieceStatus.AlreadyHeld, duplicate.Status);
        Assert.Equal(PieceStatus.AlreadyHeld, already.Status);
    }

    [Fact]
    public void HeardPiece_FromCompletedBulletin_IsNamedFromTheBulletinItself()
    {
        var piece = HeardPiece.From(
            new AcceptResult(FrameOutcome.CompletedBulletin, 1, Bulletin: SampleBulletin), 7, At(0), null);

        Assert.Equal(PieceStatus.CompletedObject, piece.Status);
        Assert.Contains(SampleBulletin.Bid, piece.What, StringComparison.Ordinal);
        Assert.Contains(SampleBulletin.Title, piece.What, StringComparison.Ordinal);
    }

    [Fact]
    public void HeardPiece_FromCompletedDirectory_NamesItByDate()
    {
        var piece = HeardPiece.From(
            new AcceptResult(FrameOutcome.CompletedDirectory, 2, Directory: SampleDirectory), 0, At(0), null);

        Assert.Equal(PieceStatus.CompletedObject, piece.Status);
        Assert.Contains(SampleDirectory.Date.ToString("yyyy-MM-dd"), piece.What, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FrameOutcome.CompletedIonosphere, "ionosonde")]
    [InlineData(FrameOutcome.CompletedPskReporter, "PSK Reporter")]
    public void HeardPiece_FromPropagationReadings_NamesTheSource(FrameOutcome outcome, string source)
    {
        var piece = HeardPiece.From(new AcceptResult(outcome, 3), 0, At(0), null);

        Assert.Equal(PieceStatus.CompletedObject, piece.Status);
        Assert.Contains(source, piece.What, StringComparison.Ordinal);
    }

    [Fact]
    public void HeardPiece_FromRejected_IsRejectedButCrcWasGood()
    {
        var piece = HeardPiece.From(new AcceptResult(FrameOutcome.Rejected, 1, Detail: "bad symbol"), 2, At(0), null);

        Assert.Equal(PieceStatus.Rejected, piece.Status);
        Assert.True(piece.CrcGood);
    }

    [Fact]
    public void Record_UsesTheFakeTimeProviderClock_ForWhenEachPieceWasHeard()
    {
        var time = new FakeTimeProvider(At(0));
        var history = new SlotFrameHistory();

        var first = HeardPiece.From(new AcceptResult(FrameOutcome.Stored, 1), 0, time.GetUtcNow(), null);
        time.Advance(TimeSpan.FromSeconds(5));
        var second = HeardPiece.From(new AcceptResult(FrameOutcome.Stored, 1), 1, time.GetUtcNow(), null);

        history.Record(At(0), At(0), null, first);
        history.Record(At(0), At(0), null, second);

        var pieces = history.Slots.Single().Bursts.Single().Pieces;
        Assert.Equal(At(0), pieces[0].Heard);
        Assert.Equal(At(0).AddSeconds(5), pieces[1].Heard);
    }
}
