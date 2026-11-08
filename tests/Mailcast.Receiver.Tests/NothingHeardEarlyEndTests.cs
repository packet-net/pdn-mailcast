using Mailcast.Receiver.Hooks;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A slot's window given back at <see cref="ReceiverHost.NothingHeardBy"/> when no tone, probe
/// or frame has been heard (<see cref="ReceiverHost.EarlyEndReason"/>), on a fake clock without
/// a real audio pipeline: the audio's "listening since" is set directly.
/// </summary>
public class NothingHeardEarlyEndTests
{
    private static readonly DateTimeOffset SlotStart = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static ToneReport Tone() => new(1801, 1, 12, TimeSpan.FromSeconds(OnAir.ToneSeconds));

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDirectory Dir { get; } = new();

        public FakeTimeProvider Time { get; }

        public ReceiverHost Host { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

        /// <summary>A receiver at <paramref name="start"/>, its audio listening since <paramref name="listening"/> (the window's opening by default).</summary>
        public Fixture(DateTimeOffset start, string audio = "ubersdr:wessex.zapto.org", DateTimeOffset? listening = null)
        {
            Time = new FakeTimeProvider(start);
            var config = new ReceiverConfig { Audio = audio, StateDirectory = Dir.Path, Stations = StationFixture.EverySlot };
            Host = new ReceiverHost(config, Time, Log.Enqueue) { ListeningSince = listening ?? SlotStart - ReceiverConfig.WebSdrBefore };
        }

        public async Task HearFrameAsync()
        {
            Host.Intake.Offer(Samples.Frames([Samples.Bulletin(1)])[1]);
            await Host.Intake.DrainAsync(CancellationToken.None);
        }

        public void At(TimeSpan afterStart) => Time.SetUtcNow(SlotStart + afterStart);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task Threshold_IsThreeAndAHalfMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(210), ReceiverHost.NothingHeardBy);
        await using var f = new Fixture(SlotStart);
        f.At(ReceiverHost.NothingHeardBy - TimeSpan.FromSeconds(1));
        Assert.Null(f.Host.EarlyEndReason(SlotStart));

        f.At(ReceiverHost.NothingHeardBy);
        string? why = f.Host.EarlyEndReason(SlotStart);

        Assert.Equal("nothing heard from GB7RDG by 12:03:30 (no tone, probe or frame)", why);
        Assert.Equal((SlotStart, why!), f.Host.LastEarlyEnd);
    }

    [Fact]
    public async Task ToneHeard_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(26));
        f.Host.Slots.OnTone(Tone());

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(30));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task LongOrBrokenToneNotMeasured_ButSeen_KeepsTheWindow()
    {
        // A daily station's 30 s tone, which the tone detector never measures, ending 2:20 in.
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(140));
        f.Host.Slots.OnSteadyTone();

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(30));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task FrameHeardWithoutATone_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(130));
        await f.HearFrameAsync();

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(30));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task FrameDecodedButNotYetThroughTheStore_KeepsTheWindow()
    {
        // Decoded just before the threshold; the store's worker has not handled it yet (it is
        // never drained here), so the slot tracker has not seen it.
        await using var f = new Fixture(SlotStart);
        f.At(ReceiverHost.NothingHeardBy - TimeSpan.FromSeconds(1));
        f.Host.Intake.Offer(Samples.Frames([Samples.Bulletin(1)])[1]);
        Assert.Equal(SlotStart + ReceiverHost.NothingHeardBy - TimeSpan.FromSeconds(1), f.Host.Intake.LastOffered);

        f.At(ReceiverHost.NothingHeardBy);

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task ProbeOnly_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(50));
        f.Host.Slots.OnProbeCaptured();

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(30));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task FrameFromTheSlotBefore_DoesNotCount()
    {
        // A frame of the 11:00 slot's tail, well before this one: nothing heard of 12:00's.
        await using var f = new Fixture(SlotStart.AddMinutes(-5));
        await f.HearFrameAsync();

        f.At(ReceiverHost.NothingHeardBy);

        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task ListeningOnlySinceAfterTheSlotBegan_IsJudgedFromThen()
    {
        // A receiver started, or a web SDR reopened, 3 minutes into the slot.
        await using var f = new Fixture(SlotStart.AddMinutes(3), listening: SlotStart.AddMinutes(3));

        f.At(TimeSpan.FromMinutes(3) + ReceiverHost.NothingHeardBy - TimeSpan.FromSeconds(1));
        Assert.Null(f.Host.EarlyEndReason(SlotStart));

        f.At(TimeSpan.FromMinutes(3) + ReceiverHost.NothingHeardBy);
        Assert.Equal("nothing heard from GB7RDG by 12:06:30 (no tone, probe or frame)", f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task ListeningOnlySinceLateInTheWindow_KeepsTheWholeWindow()
    {
        await using var f = new Fixture(SlotStart.AddMinutes(10), listening: SlotStart.AddMinutes(10));

        f.At(ReceiverConfig.WebSdrAfter - TimeSpan.FromSeconds(1));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task NotListening_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart);
        f.Host.ListeningSince = null;

        f.At(ReceiverHost.NothingHeardBy);

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task RadioTunedAfterTheSlotBegan_IsJudgedFromThen()
    {
        // The audio has been up for hours, but the rig (or pdn-soundmodem's receive window) only
        // reached the slot's frequency a minute in: a slow "before", a refusal then a retake.
        await using var f = new Fixture(SlotStart, Samples.SoundCard, listening: SlotStart.AddHours(-3));
        f.Host.RadioTunedSince = () => SlotStart.AddMinutes(1);

        f.At(ReceiverHost.NothingHeardBy);
        Assert.Null(f.Host.EarlyEndReason(SlotStart));

        f.At(TimeSpan.FromMinutes(1) + ReceiverHost.NothingHeardBy);
        Assert.Equal("nothing heard from GB7RDG by 12:04:30 (no tone, probe or frame)", f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task RadioNeverTuned_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart, Samples.SoundCard, listening: SlotStart.AddHours(-3));
        f.Host.RadioTunedSince = () => null;

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromMinutes(2));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task DecidedOnce_SoTheRetunerAndTheSlotWindowCheckAgree()
    {
        // The retuner (polling every 5 s) gives the window up first; a frame then arrives before
        // the slot window check (every 15 s) looks: it still ends the sound card's window too.
        await using var f = new Fixture(SlotStart, Samples.SoundCard, listening: SlotStart.AddHours(-3));
        f.At(ReceiverHost.NothingHeardBy);
        string? first = f.Host.EarlyEndReason(SlotStart);
        Assert.NotNull(first);

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(5));
        await f.HearFrameAsync();
        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(10));
        f.Host.CheckSlotWindow();

        Assert.Equal(first, f.Host.EarlyEndReason(SlotStart));
        Assert.Contains($"audio: the 12:00 UTC slot is over early: {first}", f.Log);
        Assert.Equal((SlotStart.AddMinutes(58), SlotStart.AddHours(1)), f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SoundCard_NothingHeard_StopsDecodingUntilTheNextSlot_AndClosesItsHookWindow()
    {
        await using var f = new Fixture(SlotStart, Samples.SoundCard, listening: SlotStart.AddHours(-3));
        f.At(ReceiverHost.NothingHeardBy);

        f.Host.CheckSlotWindow();

        var now = f.Time.GetUtcNow();
        Assert.Equal(new HookWindow(SlotStart - ReceiverConfig.WebSdrBefore, now, SlotStart), f.Host.HookWindowAt(now));
        Assert.Contains("audio: the 12:00 UTC slot is over early: nothing heard from GB7RDG by 12:03:30 (no tone, probe or frame)", f.Log);
        Assert.Equal((SlotStart.AddMinutes(58), SlotStart.AddHours(1)), f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SlotSummary_SaysNothingWasHeardAndWhenTheWindowEnded_AndALateFrameAddsNoSecondLine()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(15));
        f.Host.Slots.OnListening(SlotStart);
        f.At(ReceiverHost.NothingHeardBy);
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));

        f.Host.Slots.EndIfOver(f.Host.LastEarlyEnd?.Slot, f.Host.LastEarlyEndAt);
        Assert.Contains("slot: the 12:00 UTC slot ended: nothing heard; window ended early at 12:03:30 UTC, as nothing was heard by then", f.Log);

        // A frame decoded afterwards (a sound card still decoding while a page watches): no
        // later line contradicting "nothing heard".
        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromMinutes(1));
        await f.HearFrameAsync();
        f.At(ReceiverConfig.WebSdrAfter + TimeSpan.FromMinutes(1));
        f.Host.Slots.EndIfOver(f.Host.LastEarlyEnd?.Slot, f.Host.LastEarlyEndAt);
        Assert.Single(f.Log, l => l.StartsWith("slot: the 12:00 UTC slot ended", StringComparison.Ordinal));
    }
}
