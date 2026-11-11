using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The status page's history kept across a restart (<see cref="StatusMemory"/>): each part comes
/// back in a new <see cref="ReceiverHost"/> on the same state directory, a bad file is ignored, and
/// the file is written seldom.
/// </summary>
public class StatusMemoryTests
{
    private static readonly DateTimeOffset SlotStart = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static ReceiverHost Host(TempDirectory dir, TimeProvider time, Action<string>? log = null) =>
        new(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = StationFixture.EverySlot }, time, log ?? (_ => { }));

    private static string FilePath(TempDirectory dir) => Path.Combine(dir.Path, StatusMemory.FileName);

    private static HeardPiece Piece(DateTimeOffset heard, uint esi) => new(heard, esi, PieceStatus.New, true, "bulletin 1_GB7RDG, \"test\"");

    [Fact]
    public async Task WhatWasHeard_SurvivesARestart()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart.AddMinutes(1));
        await using (var host = Host(dir, time))
        {
            host.RestoreStatus();
            host.FrameHistory.Record(SlotStart, SlotStart.AddSeconds(30), "ms110d-wn4", Piece(SlotStart.AddSeconds(31), 0));
            host.FrameHistory.Record(SlotStart, SlotStart.AddSeconds(30), "ms110d-wn4", Piece(SlotStart.AddSeconds(32), 1));
            host.FrameHistory.Record(SlotStart.AddHours(1), null, null, new HeardPiece(SlotStart.AddHours(1), null, PieceStatus.NotRecognised, false, "not a mailcast frame"));
        }

        await using var restarted = Host(dir, time);
        restarted.RestoreStatus();

        Assert.Equal(2, restarted.FrameHistory.Slots.Count);
        var first = restarted.FrameHistory.Slot(SlotStart)!;
        var burst = Assert.Single(first.Bursts);
        Assert.Equal(SlotStart.AddSeconds(30), burst.Started);
        Assert.Equal("ms110d-wn4", burst.Waveform);
        Assert.Equal([0u, 1u], burst.Pieces.Select(p => p.Esi!.Value));
        Assert.Equal(PieceStatus.New, burst.Pieces[0].Status);
        var second = Assert.Single(Assert.Single(restarted.FrameHistory.Slot(SlotStart.AddHours(1))!.Bursts).Pieces);
        Assert.Equal(PieceStatus.NotRecognised, second.Status);
        Assert.Null(second.Esi);
        Assert.False(second.CrcGood);

        // And it carries on recording into the same slot.
        restarted.FrameHistory.Record(SlotStart.AddHours(1), null, null, Piece(SlotStart.AddHours(1).AddSeconds(5), 7));
        Assert.Equal(2, restarted.FrameHistory.Slots.Count);
    }

    [Fact]
    public async Task TheLastSlot_SurvivesARestart_WithItsToneFramesAndDirectory()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart.AddSeconds(20));
        SlotSummary before;
        await using (var host = Host(dir, time))
        {
            host.RestoreStatus();
            host.Slots.OnTone(new ToneReport(1801.3, 1.3, 14.2, TimeSpan.FromSeconds(10)));
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
            {
                host.Intake.Offer(frame, "ms110d-wn4");
            }
            await host.Intake.DrainAsync(CancellationToken.None);
            before = host.Slots.Last!;
            Assert.NotNull(before.HeardDirectory);
        }

        await using var restarted = Host(dir, time);
        restarted.RestoreStatus();

        var after = restarted.Slots.Last!;
        Assert.Equal(before.Scheduled, after.Scheduled);
        Assert.Equal(before.Started, after.Started);
        Assert.Equal(before.Tone, after.Tone);
        Assert.Equal(before.FramesHeard, after.FramesHeard);
        Assert.Equal(before.FrameCounts, after.FrameCounts);
        Assert.Equal(before.BytesDecoded, after.BytesDecoded);
        Assert.Equal(before.BulletinsCompleted, after.BulletinsCompleted);
        Assert.Equal(before.LastFrame, after.LastFrame);
        Assert.Equal(before.HeardDirectory!.Serialize(), after.HeardDirectory!.Serialize());
        Assert.Equal(before.HeardDirectory.IsSigned, after.HeardDirectory.IsSigned);
        // Its window is still open: it has not had its line yet, and still gets one.
        Assert.False(after.Ended);
    }

    [Fact]
    public async Task ASlotWhoseWindowClosedWhileStopped_CountsAsEnded_AndGetsNoLateLine()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart.AddSeconds(20));
        await using (var host = Host(dir, time))
        {
            host.RestoreStatus();
            host.Slots.OnFrame("ms110d-wn4", 100);
        }

        time.Advance(TimeSpan.FromHours(2));
        var lines = new List<string>();
        await using var restarted = Host(dir, time, lines.Add);
        restarted.RestoreStatus();
        restarted.Slots.EndIfOver(null);

        Assert.True(restarted.Slots.Last!.Ended);
        Assert.Equal(1, restarted.Slots.Last.FramesHeard);
        Assert.DoesNotContain(lines, l => l.Contains("slot ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLastEarlyEnd_SurvivesARestart_OnceItsWindowIsOver()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart.AddSeconds(5));
        string reason;
        await using (var host = Host(dir, time))
        {
            host.RestoreStatus();
            host.ListeningSince = SlotStart;
            time.Advance(ReceiverHost.NothingHeardBy);
            reason = host.EarlyEndReason(SlotStart)!;
            Assert.NotNull(reason);
        }

        // Restarted within the window (a copy of the state directory): judged afresh, as before.
        using (var copy = new TempDirectory())
        {
            File.Copy(FilePath(dir), FilePath(copy));
            await using var within = Host(copy, time);
            within.RestoreStatus();
            Assert.Null(within.LastEarlyEnd);
        }

        time.Advance(ReceiverConfig.WebSdrAfter);
        await using var restarted = Host(dir, time);
        restarted.RestoreStatus();

        Assert.Equal((SlotStart, reason), restarted.LastEarlyEnd);
        Assert.NotNull(restarted.LastEarlyEndAt);
    }

    [Fact]
    public async Task WhatTheWebSdrSaid_AndWhereStationsWereHeard_SurviveARestart()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FilePath(dir), """{"version":1,"heardAtHz":{"G4XYZ":7047000},"webSdrAbout":{"source":"ubersdr:wessex.zapto.org","words":"G4ABC, Wessex, IO90"},"webSdrPlace":{"source":"ubersdr:wessex.zapto.org","place":{"latitude":51.1,"longitude":-1.5,"locator":"IO91ac"}}}""");
        var time = new FakeTimeProvider(SlotStart);
        await using (var host = Host(dir, time))
        {
            host.RestoreStatus();
            Assert.Equal("G4ABC, Wessex, IO90", host.WebSdrAbout);
            Assert.Equal("IO91ac", host.Place?.Locator);
        }

        // Saved again at shutdown as it was read, so a second restart keeps it too.
        await using var again = Host(dir, time);
        again.RestoreStatus();
        var saved = again.StatusSnapshot();
        Assert.Equal(7047000, saved.HeardAtHz!["G4XYZ"]);
        Assert.Equal("G4ABC, Wessex, IO90", again.WebSdrAbout);
    }

    [Fact]
    public async Task ACorruptFile_IsIgnoredWithOneLine()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FilePath(dir), "{ this is not json");
        var lines = new List<string>();
        var time = new FakeTimeProvider(SlotStart);

        await using var host = Host(dir, time, lines.Add);
        host.RestoreStatus();

        Assert.Empty(host.FrameHistory.Slots);
        Assert.Null(host.Slots.Last);
        Assert.Null(host.LastEarlyEnd);
        Assert.Single(lines, l => l.StartsWith("status: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADirectoryThatDoesNotRead_IsIgnoredWithOneLine()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FilePath(dir), """{"version":1,"heard":[],"slot":{"started":"2026-10-05T12:00:00+00:00","heardDirectory":"bm90IGEgZGlyZWN0b3J5"}}""");
        var lines = new List<string>();

        await using var host = Host(dir, new FakeTimeProvider(SlotStart), lines.Add);
        host.RestoreStatus();

        Assert.Null(host.Slots.Last);
        Assert.Single(lines, l => l.StartsWith("status: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileFromAnotherVersion_IsIgnored()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FilePath(dir), """{"version":99,"heard":[{"slot":"2026-10-05T12:00:00+00:00","bursts":[]}]}""");
        var lines = new List<string>();

        await using var host = Host(dir, new FakeTimeProvider(SlotStart), lines.Add);
        host.RestoreStatus();

        Assert.Empty(host.FrameHistory.Slots);
        Assert.Contains(lines, l => l.Contains("another version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_IsSeldom_AndNeverWhenNothingChanged()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart);
        await using var host = Host(dir, time);
        host.RestoreStatus();

        // Nothing at all yet: written once, then not again while nothing changes.
        Assert.True(host.SaveStatus(now: true));
        Assert.False(host.SaveStatus(now: true));

        host.FrameHistory.Record(SlotStart, null, null, Piece(SlotStart, 0));
        Assert.False(host.SaveStatus(now: false)); // changed, but not SaveEvery since the last look
        time.Advance(TimeSpan.FromMinutes(1));
        host.FrameHistory.Record(SlotStart, null, null, Piece(SlotStart.AddMinutes(1), 1));
        Assert.False(host.SaveStatus(now: false));
        time.Advance(StatusMemory.SaveEvery);
        Assert.True(host.SaveStatus(now: false));
        Assert.False(host.SaveStatus(now: true)); // nothing new since
        Assert.True(File.Exists(FilePath(dir)));
    }

    [Fact]
    public async Task NothingIsWritten_WithoutRestoreStatus()
    {
        using var dir = new TempDirectory();
        await using (var host = Host(dir, new FakeTimeProvider(SlotStart)))
        {
            // As --decode: it must leave a running service's state directory alone.
            host.FrameHistory.Record(SlotStart, null, null, Piece(SlotStart, 0));
            Assert.False(host.SaveStatus(now: true));
        }

        Assert.False(File.Exists(FilePath(dir)));
    }
}
