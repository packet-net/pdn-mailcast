using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

public class IntakeTests
{
    [Fact]
    public async Task Frames_RebuildBulletins_WhichWaitInTheOutboxAcrossARestart()
    {
        using var dir = new TempDirectory();
        var bulletins = new[] { Samples.Bulletin(1), Samples.Bulletin(2, bodyLines: 80), Samples.Bulletin(3) };
        var completed = new List<string>();

        await using (var intake = new Intake(dir.Path, _ => { }))
        {
            intake.BulletinCompleted += b => completed.Add(b.Bid);
            foreach (var frame in Samples.Frames(bulletins))
            {
                Assert.True(intake.Offer(frame));
            }
            await intake.DrainAsync(CancellationToken.None);
        }

        Assert.Equal(bulletins.Select(b => b.Bid).Order(), completed.Order());
        await using (var reopened = new Intake(dir.Path, _ => { }))
        {
            Assert.Equal(bulletins.OrderBy(b => b.Bid), reopened.Pending().OrderBy(b => b.Bid));
            reopened.Acknowledge("2_GB7RDG", BbsVerdict.Accepted);
            Assert.Equal(2, reopened.Pending().Count);
        }
    }

    [Fact]
    public async Task OtherTraffic_IsIgnored()
    {
        using var dir = new TempDirectory();
        await using var intake = new Intake(dir.Path, _ => { });
        var payload = Samples.Frames([Samples.Bulletin(1)])[0].AsSpan(16).ToArray();

        Assert.False(intake.Offer(Ax25UiFrame.Build("G4ABC", OnAir.Destination, payload)));
        Assert.False(intake.Offer(Ax25UiFrame.Build(Samples.Source, "ID", payload)));
        Assert.False(intake.Offer([1, 2, 3]));
        Assert.True(intake.Offer(Ax25UiFrame.Build(Samples.Source + "-3", OnAir.Destination, payload)));
        Assert.Equal(1, intake.FramesHeard);
    }

    [Fact]
    public async Task FullQueue_DropsAndCounts_AndDrainStillReturns()
    {
        using var dir = new TempDirectory();
        await using var intake = new Intake(dir.Path, _ => { }, queueLength: 2);
        var frames = Samples.Frames([Samples.Bulletin(1, bodyLines: 200)]);
        Assert.True(frames.Count >= 8);
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        intake.FrameHeard += _ =>
        {
            entered.Release();
            release.Wait();
        };

        // The worker takes the first frame and is held; two more fill the queue; the rest drop.
        intake.Offer(frames[0]);
        await entered.WaitAsync();
        for (int i = 1; i < 8; i++)
        {
            intake.Offer(frames[i]);
        }
        Assert.Equal(5, intake.FramesDropped);

        release.Set();
        await intake.DrainAsync(CancellationToken.None);
        Assert.Equal(8, intake.FramesHeard);
    }

    /// <summary>
    /// Issue #48: <see cref="Intake.Piece"/> fires on the worker, once per frame, with the
    /// piece's ESI from the frame itself, the waveform it came on, and the clock's time when the
    /// worker got to it (<see cref="FakeTimeProvider"/>, never advancing backwards).
    /// </summary>
    [Fact]
    public async Task Piece_FiresOnceEachFrame_WithTheFramesEsiAndTheClocksTime()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        await using var intake = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time });
        var frames = Samples.Frames([Samples.Bulletin(1)]);
        var pieces = new List<PieceHandled>();
        intake.Piece += pieces.Add;

        foreach (var frame in frames)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            intake.Offer(frame, "ms110d-wn4");
        }
        await intake.DrainAsync(CancellationToken.None);

        Assert.Equal(frames.Count, pieces.Count);
        Assert.All(pieces, p => Assert.Equal("ms110d-wn4", p.Waveform));
        Assert.All(pieces, p => Assert.NotNull(p.Esi));
        Assert.Contains(pieces, p => p.Result.Outcome == FrameOutcome.CompletedBulletin);
        for (int i = 1; i < pieces.Count; i++)
        {
            Assert.True(pieces[i].Heard >= pieces[i - 1].Heard);
        }
    }

    /// <summary>Issue #48: a frame too short or malformed to be a mailcast piece still raises <see cref="Intake.Piece"/>, with no ESI.</summary>
    [Fact]
    public async Task Piece_ForAFrameThatDoesNotParse_HasNoEsi()
    {
        using var dir = new TempDirectory();
        await using var intake = new Intake(dir.Path, _ => { });
        PieceHandled? seen = null;
        intake.Piece += p => seen = p;

        intake.Offer(Ax25UiFrame.Build(Samples.Source, OnAir.Destination, [1, 2, 3]));
        await intake.DrainAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(FrameOutcome.NotAFrame, seen!.Value.Result.Outcome);
        Assert.Null(seen.Value.Esi);
    }
}
