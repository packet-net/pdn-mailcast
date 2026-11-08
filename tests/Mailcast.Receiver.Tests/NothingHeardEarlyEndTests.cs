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
    public async Task NothingHeard_EndsAtTwoMinutes_AndSaysWhy()
    {
        await using var f = new Fixture(SlotStart);
        f.At(ReceiverHost.NothingHeardBy - TimeSpan.FromSeconds(1));
        Assert.Null(f.Host.EarlyEndReason(SlotStart));

        f.At(ReceiverHost.NothingHeardBy);
        string? why = f.Host.EarlyEndReason(SlotStart);

        Assert.Equal("nothing heard from GB7RDG by 12:02 (no tone, probe or frame)", why);
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
    public async Task FrameHeardWithoutATone_KeepsTheWindow()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(80));
        await f.HearFrameAsync();

        f.At(ReceiverHost.NothingHeardBy + TimeSpan.FromSeconds(30));

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

    [Theory]
    [InlineData(3)] // a receiver started mid-window, after the 2 minute mark
    [InlineData(1)] // or after the slot began: the tone may have been missed
    public async Task ListeningOnlySinceAfterTheSlotBegan_KeepsTheWindow(int minutesIn)
    {
        await using var f = new Fixture(SlotStart.AddMinutes(minutesIn), listening: SlotStart.AddMinutes(minutesIn));

        f.At(TimeSpan.FromMinutes(minutesIn) + ReceiverHost.NothingHeardBy);

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
    public async Task SoundCard_NothingHeard_StopsDecodingUntilTheNextSlot_AndClosesItsHookWindow()
    {
        await using var f = new Fixture(SlotStart, Samples.SoundCard, listening: SlotStart.AddHours(-3));
        f.At(ReceiverHost.NothingHeardBy);

        f.Host.CheckSlotWindow();

        var now = f.Time.GetUtcNow();
        Assert.Equal(new HookWindow(SlotStart - ReceiverConfig.WebSdrBefore, now, SlotStart), f.Host.HookWindowAt(now));
        Assert.Contains("audio: the 12:00 UTC slot is over early: nothing heard from GB7RDG by 12:02 (no tone, probe or frame)", f.Log);
        Assert.Equal((SlotStart.AddMinutes(58), SlotStart.AddHours(1)), f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SlotSummary_SaysNothingWasHeardAndTheWindowEndedEarly()
    {
        await using var f = new Fixture(SlotStart);
        f.At(TimeSpan.FromSeconds(15));
        f.Host.Slots.OnListening(SlotStart);
        f.At(ReceiverHost.NothingHeardBy);

        f.Host.Slots.EndIfOver(f.Host.EarlyEndReason(SlotStart) is null ? null : f.Host.LastEarlyEnd?.Slot);

        Assert.Contains("slot: the 12:00 UTC slot ended: nothing heard; window ended early, as nothing was heard in its first 2 minutes", f.Log);
    }
}
