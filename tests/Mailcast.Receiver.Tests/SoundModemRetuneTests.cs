using Packet.Mailcast;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mailcast.Receiver.Retune;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Retuning through pdn-soundmodem's receive window (issue #78), against the fake window in
/// <see cref="FakeSoundModem"/>: taken a minute before the slot, renewed every minute, given back
/// at the end, refused, lost mid-slot, taken again after pdn-soundmodem restarts mid-slot, and
/// given back when the receiver stops. The retuner's waits are on a fake clock moved by exactly
/// what it asked for; the window is real HTTP on loopback.
/// </summary>
public sealed class SoundModemRetuneTests
{
    private const long PacketDialHz = 7_048_000;
    private const long BulletinDialHz = 7_052_000;

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A retuner on the receive window of a fake pdn-soundmodem, on a fake clock.</summary>
    private sealed class Station : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TempDirectory _dir = new();
        private Task? _run;
        private TimeSpan? _pending;

        public Station(FakeSoundModem fake, DateTimeOffset start, Func<DateTimeOffset, string?>? earlyEnd = null, TimeSpan? requestTimeout = null)
        {
            Fake = fake;
            Port = fake.Port;
            Clock = new FakeTimeProvider(start);
            Config = new ReceiverConfig { Audio = $"soundmodem:{fake.Port}", StateDirectory = _dir.Path, Daylight = null, Retune = true };
            Config.Validate();
            Retuner = new Retuner(Config, () => Config.Schedule, () => true, Clock, Log.Enqueue,
                new ReceiveWindow(() => Port, requestTimeout ?? ReceiveWindow.RequestTimeout), () => Config.DialHz, dial => Dials.Enqueue(dial), null, earlyEnd);
            Retuner.Waiting += d => Waits.Writer.TryWrite(d);
        }

        public FakeSoundModem Fake { get; set; }

        public int Port { get; }

        public FakeTimeProvider Clock { get; }

        public ReceiverConfig Config { get; }

        public Retuner Retuner { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        /// <summary>Every dial the retuner said the radio was on, for the audio's placement to follow.</summary>
        public ConcurrentQueue<double?> Dials { get; } = new();

        public Channel<TimeSpan> Waits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public void Start() => _run = Retuner.RunAsync(_stop.Token);

        public async Task<TimeSpan> NextWait()
        {
            if (_pending is { } pending)
            {
                return pending;
            }
            try
            {
                _pending = await Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
                return _pending.Value;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"the retuner set no timer; at {Clock.GetUtcNow():HH:mm:ss}, it said: {string.Join(" | ", Log)}");
            }
        }

        /// <summary>After something that ends the retuner's wait early (the stream connecting again): its next wait.</summary>
        public async Task<TimeSpan> Resync()
        {
            _pending = null;
            return await NextWait();
        }

        public async Task Step()
        {
            var wait = await NextWait();
            _pending = null;
            Clock.Advance(wait);
        }

        public async Task RunTo(DateTimeOffset at)
        {
            while (Clock.GetUtcNow() < at)
            {
                await Step();
            }
            await NextWait();
        }

        public IEnumerable<JsonElement> Takes() => Fake.WindowLog
            .Where(r => r.Method == "POST" && !r.Body.Contains("release", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body).RootElement);

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null)
            {
                await _run;
                _run = null;
            }
            await Retuner.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _dir.Dispose();
        }
    }

    private static string Said(Station s) => string.Join(" | ", s.Log);

    [Fact]
    public async Task TheWindowIsTaken_AMinuteBeforeTheSlot_OnTheMailcastDial_WithTheBandTheSignalNeeds()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();

        await s.NextWait();
        Assert.Empty(fake.WindowLog);
        Assert.Contains("idle until 11:59 UTC, ready for the 12:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
        Assert.Contains(s.Log, l => l.Contains($"through pdn-soundmodem's receive window on 127.0.0.1:{fake.Port} to dial 7.052 MHz for each slot", StringComparison.Ordinal));

        await s.RunTo(Noon.AddMinutes(-1));

        var take = Assert.Single(s.Takes());
        Assert.Equal(BulletinDialHz, take.GetProperty("dialHz").GetInt64());
        // Up to the signal's top edge at the usual dial: 1800 + 1450 Hz.
        Assert.Equal(3250, take.GetProperty("widthHz").GetInt32());
        // 13 minutes to the slot's close is more than one window gives: asked for the most.
        Assert.Equal(300, take.GetProperty("seconds").GetInt32());
        var request = fake.WindowLog.Single();
        Assert.Equal("/rig-window", request.Path);
        Assert.DoesNotContain("Origin:", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Forwarded", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-API-Key", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.True(s.Retuner.WindowHeld);
        Assert.Contains("tuned to 7.052 MHz through pdn-soundmodem's receive window for the 12:00 UTC slot until 12:12 UTC", s.Retuner.State, StringComparison.Ordinal);
        Assert.Equal(BulletinDialHz, s.Dials.Last());
        Assert.Null(s.Retuner.LastProblem);
    }

    [Fact]
    public async Task TheWindowIsRenewedEveryMinute_AndGivenBackAtTheSlotsEnd()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();

        await s.RunTo(Noon.AddMinutes(-1));
        Assert.Single(s.Takes());
        await s.RunTo(Noon.AddSeconds(5));
        Assert.Equal(2, s.Takes().Count());
        await s.RunTo(Noon.AddMinutes(5).AddSeconds(5));
        // Taken at 11:59, renewed at 12:00, 12:01, ... 12:05.
        Assert.Equal(7, s.Takes().Count());
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);

        await s.RunTo(Noon.AddMinutes(12));

        Assert.Equal(13, s.Takes().Count());
        var last = fake.WindowLog.Last();
        Assert.Contains("\"release\":true", last.Body, StringComparison.Ordinal);
        Assert.Null(fake.WindowDialHz);
        Assert.Equal(PacketDialHz, fake.RigDialHz);
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
        Assert.Contains(s.Log, l => l == "retune: gave pdn-soundmodem's receive window back; it puts its radio back, now on 7.048 MHz");
        // The audio's placement is told where the radio went back to.
        Assert.Equal(PacketDialHz, s.Dials.Last());
        Assert.Null(s.Retuner.LastProblem);
        Assert.False(s.Retuner.WindowHeld);

        // The next slot's window opens at 12:59.
        await s.NextWait();
        Assert.Contains("ready for the 13:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_IsSaidPlainly_TheSlotIsHeardOnWhateverTheRigIsOn_AndTheWindowIsAskedForAgain()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz)
        {
            RefuseWith = "sub-channel 4 holds the transmit lease, so a receive window is refused until it ends",
        };
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();

        await s.RunTo(Noon.AddMinutes(-1));

        Assert.Contains(s.Log, l => l == "retune: WARNING - pdn-soundmodem refused its receive window for the 12:00 UTC slot (sub-channel 4 holds the transmit lease, so a receive window is refused until it ends); listening on whatever its radio is on, and asking again every 60 s until 12:12 UTC");
        Assert.Contains("not tuned for the 12:00 UTC slot: pdn-soundmodem refused its receive window (sub-channel 4 holds the transmit lease", s.Retuner.State, StringComparison.Ordinal);
        Assert.Contains("sub-channel 4 holds the transmit lease", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
        Assert.Equal(PacketDialHz, fake.RigDialHz);
        Assert.False(s.Retuner.WindowHeld);
        Assert.Empty(s.Dials);

        // Asked again a minute later, and said only once while it says the same.
        await s.RunTo(Noon.AddSeconds(5));
        Assert.Equal(2, s.Takes().Count());
        Assert.Single(s.Log, l => l.Contains("refused its receive window", StringComparison.Ordinal));

        fake.RefuseWith = null;
        await s.RunTo(Noon.AddMinutes(1).AddSeconds(5));

        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
        Assert.True(s.Retuner.WindowHeld);
        Assert.Contains(s.Log, l => l.StartsWith("retune: took pdn-soundmodem's receive window for what is left of the 12:00 UTC slot: tuned to 7.052 MHz", StringComparison.Ordinal));

        await s.RunTo(Noon.AddMinutes(12));
        Assert.Null(fake.WindowDialHz);
    }

    [Fact]
    public async Task APdnSoundmodemWithNoRig_SaysSoPlainly_AndIsNotAskedEveryMinute()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_000) { HasRig = false };
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();

        await s.RunTo(Noon.AddMinutes(-1));

        Assert.Contains(s.Log, l => l.StartsWith($"retune: WARNING - pdn-soundmodem could not give its receive window for the 12:00 UTC slot (pdn-soundmodem on 127.0.0.1:{fake.Port} has no receive window to give: it has no \"rig\" section", StringComparison.Ordinal)
            && l.EndsWith("; listening on whatever its radio is on", StringComparison.Ordinal));
        await s.RunTo(Noon.AddMinutes(4));
        Assert.Single(s.Takes());

        // A stream that connects again may be a pdn-soundmodem restarted with a rig: asked once more.
        s.Retuner.SourceReconnected();
        await s.Resync();
        Assert.Equal(2, s.Takes().Count());
        // No window was ever granted, so none is given back.
        await s.RunTo(Noon.AddMinutes(12));
        Assert.DoesNotContain(fake.WindowLog, r => r.Body.Contains("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWindowLostMidSlot_IsSaid_AndTakenAgainForWhatIsLeft()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();
        await s.RunTo(Noon.AddMinutes(3));
        int before = s.Takes().Count();

        fake.DropWindow();
        await s.Step();
        await s.NextWait();

        Assert.Contains(s.Log, l => l == "retune: WARNING - pdn-soundmodem's receive window ended before the 12:00 UTC slot did (pdn-soundmodem says no window is open now: it was restarted, or the window ran out); listening on whatever its radio is on until it can be taken again");
        Assert.Contains("ended before the 12:00 UTC slot did", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
        // Asked for again at once, and the audio told where the radio was meanwhile and is now.
        Assert.Equal(before + 1, s.Takes().Count());
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
        Assert.True(s.Retuner.WindowHeld);
        Assert.Equal([PacketDialHz, BulletinDialHz], s.Dials.TakeLast(2).ToArray());
        Assert.Contains(s.Log, l => l.StartsWith("retune: took pdn-soundmodem's receive window for what is left of the 12:00 UTC slot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PdnSoundmodemRestartingMidSlot_TheWindowIsTakenAgainAsSoonAsItsStreamIsBack()
    {
        var fake = new FakeSoundModem(dialHz: PacketDialHz);
        int port = fake.Port;
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();
        await s.RunTo(Noon.AddMinutes(10));
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);

        // pdn-soundmodem stops; its restore file puts the rig back when it starts again.
        await fake.DisposeAsync();
        await s.Step();
        await s.NextWait();
        Assert.Contains(s.Log, l => l.StartsWith("retune: WARNING - pdn-soundmodem's receive window ended before the 12:00 UTC slot did (pdn-soundmodem is not answering on", StringComparison.Ordinal));
        Assert.False(s.Retuner.WindowHeld);
        await s.RunTo(Noon.AddMinutes(10).AddSeconds(30));

        await using var restarted = new FakeSoundModem(dialHz: PacketDialHz, port: port);
        s.Fake = restarted;
        // The receiver's audio stream connects again.
        s.Retuner.SourceReconnected();
        await s.Resync();

        var take = Assert.Single(s.Takes());
        Assert.Equal(BulletinDialHz, take.GetProperty("dialHz").GetInt64());
        // What is left of the slot, to 12:12, and past the next renewal.
        Assert.Equal(90 + 90, take.GetProperty("seconds").GetInt32());
        Assert.Equal(BulletinDialHz, restarted.WindowDialHz);
        Assert.True(s.Retuner.WindowHeld);
        Assert.Contains(s.Log, l => l.StartsWith("retune: took pdn-soundmodem's receive window for what is left of the 12:00 UTC slot", StringComparison.Ordinal));

        await s.RunTo(Noon.AddMinutes(12));
        Assert.Null(restarted.WindowDialHz);
        Assert.Equal(PacketDialHz, restarted.RigDialHz);
    }

    [Fact]
    public async Task StoppingTheReceiverMidSlot_GivesTheWindowBack()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();
        await s.RunTo(Noon.AddMinutes(3));
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);

        await s.StopAsync();

        Assert.Contains("\"release\":true", fake.WindowLog.Last().Body, StringComparison.Ordinal);
        Assert.Null(fake.WindowDialHz);
        Assert.Equal(PacketDialHz, fake.RigDialHz);
        Assert.Contains(s.Log, l => l == "retune: gave pdn-soundmodem's receive window back; it puts its radio back, now on 7.048 MHz");
        // Given back once only.
        Assert.Single(fake.WindowLog, r => r.Body.Contains("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEarlyEnd_GivesTheWindowBackSooner()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        Station? station = null;
        await using var s = station = new Station(fake, Noon.AddMinutes(-2), slot => station!.Clock.GetUtcNow() >= Noon.AddMinutes(6) ? "everything heard" : null);
        s.Start();

        await s.RunTo(Noon.AddMinutes(6).AddSeconds(5));

        Assert.Null(fake.WindowDialHz);
        Assert.Contains(s.Log, l => l == "retune: ending the 12:00 UTC slot early: everything heard");
        Assert.Contains(s.Log, l => l.StartsWith("retune: gave pdn-soundmodem's receive window back", StringComparison.Ordinal));
        Assert.True(s.Clock.GetUtcNow() < Noon.AddMinutes(12));
    }

    [Fact]
    public async Task ARenewalThatFails_ButLeavesTheWindowOpen_KeepsListening()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();
        await s.RunTo(Noon.AddSeconds(-5));

        // Refused while still held, as pdn-soundmodem's rig control can while it holds
        // transmitting: the window stays ours.
        fake.RefuseWith = "the rig is being retuned already; ask again in a moment";
        await s.RunTo(Noon.AddSeconds(5));

        Assert.Contains(s.Log, l => l == "retune: WARNING - could not renew pdn-soundmodem's receive window (the rig is being retuned already; ask again in a moment)");
        Assert.True(s.Retuner.WindowHeld);
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
        Assert.DoesNotContain(s.Log, l => l.Contains("ended before", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusalAtTheSlotsOpening_IsAskedAgainWithinSeconds_NotAMinute()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz)
        {
            RefuseWith = "the transmitter is keyed, and the rig is never retuned in the middle of a transmission; ask again once it has unkeyed",
        };
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();

        await s.RunTo(Noon.AddMinutes(-1));
        Assert.Contains(s.Log, l => l.StartsWith("retune: WARNING - pdn-soundmodem refused its receive window for the 12:00 UTC slot (the transmitter is keyed", StringComparison.Ordinal)
            && l.EndsWith("and asking again every 10 s until 12:12 UTC", StringComparison.Ordinal));
        fake.RefuseWith = null;

        await s.RunTo(Noon.AddMinutes(-1).AddSeconds(10));

        Assert.Equal(2, s.Takes().Count());
        Assert.True(s.Retuner.WindowHeld);
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
    }

    [Fact]
    public async Task APdnSoundmodemThatDoesNotAnswerInTime_IsNotTakenToHaveLostTheWindow()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2), requestTimeout: TimeSpan.FromMilliseconds(300));
        s.Start();
        await s.RunTo(Noon.AddMinutes(2).AddSeconds(30));
        int dials = s.Dials.Count;

        fake.Hang = true;
        await s.RunTo(Noon.AddMinutes(2).AddSeconds(45));
        fake.Hang = false;
        await s.RunTo(Noon.AddMinutes(3).AddSeconds(10));

        Assert.True(s.Retuner.WindowHeld);
        Assert.DoesNotContain(s.Log, l => l.Contains("ended before", StringComparison.Ordinal));
        Assert.Equal(dials, s.Dials.Count);
        Assert.Equal(BulletinDialHz, fake.WindowDialHz);
    }

    [Fact]
    public async Task AStreamThatReconnectsWhileTheWindowIsHeld_IsPlacedByTheWindowsDial_WithoutAskingAgain()
    {
        await using var fake = new FakeSoundModem(dialHz: PacketDialHz);
        await using var s = new Station(fake, Noon.AddMinutes(-2));
        s.Start();
        await s.RunTo(Noon.AddMinutes(3));
        int takes = s.Takes().Count();
        while (s.Dials.TryDequeue(out _))
        {
        }

        s.Retuner.SourceReconnected();

        // At once, before the retuner's own loop has run.
        Assert.Equal([(double?)BulletinDialHz], s.Dials.ToArray());
        await s.Resync();
        Assert.Equal(takes, s.Takes().Count());
        Assert.True(s.Retuner.WindowHeld);
    }

    [Fact]
    public async Task TheInputsMove_ChangesMidStream_WhenTheRadioIsRetuned()
    {
        await using var fake = new FakeSoundModem(rateHz: 12_000, dialHz: 7_052_000);
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        Assert.Null(input.MovedFromHz);
        var tone = new float[1200];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 3800 * i / 12_000));
        }
        var block = new float[OnAir.SampleRate / 10];
        var moved = new float[OnAir.SampleRate / 10];

        async Task<double?> NextAsync(ulong index)
        {
            await connection.SendAsync(index, tone);
            return await Task.Run(() =>
            {
                for (int tries = 0; tries < 20; tries++)
                {
                    if (input.ReadBlock(block, moved, out _, out _, out double? from) > 0)
                    {
                        return from;
                    }
                }
                throw new TimeoutException("no block came");
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Null(await NextAsync(0));
        // The window has moved the radio to 7.050 MHz: the signal at 3800 Hz, moved to 1800.
        input.MoveFrom(3800);
        Assert.Equal(3800, await NextAsync(1200));
        Assert.Equal(3800, input.MovedFromHz);
        // The tone is at 1800 Hz in the moved block now, not at 3800 (the second half, past the filters' start).
        var settled = moved.AsSpan(moved.Length / 2).ToArray();
        Assert.True(Power(settled, 1800) > 100 * Power(settled, 3800), $"1800 Hz {Power(settled, 1800)}, 3800 Hz {Power(settled, 3800)}");
        input.MoveFrom(null);
        Assert.Null(await NextAsync(2400));
        Assert.Null(input.MovedFromHz);
    }

    /// <summary>The power of <paramref name="hz"/> in 48 kHz audio (Goertzel).</summary>
    private static double Power(float[] samples, double hz)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * hz / OnAir.SampleRate), s1 = 0, s2 = 0;
        foreach (float x in samples)
        {
            double s0 = x + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }
        return (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
    }

    [Fact]
    public async Task ABlockAlreadyQueued_KeepsTheMoveItWasMadeWith()
    {
        await using var fake = new FakeSoundModem(rateHz: 12_000, dialHz: 7_052_000);
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        var samples = new float[1200];

        await connection.SendAsync(0, samples);
        await WaitForAsync(() => input.BlocksReceived == 1);
        input.MoveFrom(3800);
        await connection.SendAsync(1200, samples);
        await WaitForAsync(() => input.BlocksReceived == 2);

        var block = new float[OnAir.SampleRate / 10];
        var moved = new float[OnAir.SampleRate / 10];
        Assert.True(input.ReadBlock(block, moved, out _, out _, out double? first) > 0);
        Assert.True(input.ReadBlock(block, moved, out _, out _, out double? second) > 0);
        Assert.Null(first);
        Assert.Equal(3800, second);
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(done());
    }

    [Fact]
    public async Task ARetuneWhileTheAudioThreadWaits_DropsTheBlockMadeWithTheOldMove_AndFeedsTheNext()
    {
        await using var fake = new FakeSoundModem(rateHz: 12_000, dialHz: BulletinDialHz);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, _ => { }, TimeProvider.System, retunedByWindow: true);
        await using var _ = pipeline;
        int heard = 0;
        pipeline.SourceBlock += _ => Interlocked.Increment(ref heard);
        await pipeline.OpenAsync(CancellationToken.None);
        var connection = await fake.NextAsync();
        await pipeline.StartAsync(CancellationToken.None);
        var samples = new float[1200];
        samples[0] = 0.1f;

        await connection.SendAsync(0, samples);
        await WaitForAsync(() => Volatile.Read(ref heard) == 1);

        // The window gives the radio back to 7.050 MHz while the audio thread waits for a block:
        // the next block was made unmoved and is dropped; the one after comes moved and is fed.
        Assert.True(pipeline.FollowDial(7_050_000));
        await connection.SendAsync(1200, samples);
        await WaitForAsync(() => pipeline.DroppedAtRetune == 1);
        await connection.SendAsync(2400, samples);
        await WaitForAsync(() => Volatile.Read(ref heard) == 2);

        Assert.Equal(1, pipeline.DroppedAtRetune);
        Assert.Equal(1800, pipeline.AudioCentreHz, 3);
        Assert.Equal(1800, pipeline.Tone.ExpectedHz, 3);
    }

    [Fact]
    public async Task APipelineStillOpening_IgnoresADialToFollow_AndIsPlacedOnceOpen()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, _ => { }, TimeProvider.System, retunedByWindow: true);
        await using var _ = pipeline;

        Assert.False(pipeline.FollowDial(7_051_800));
        await pipeline.OpenAsync(CancellationToken.None);
        Assert.Equal(7_052_300, pipeline.DialHz);

        // What Retuner.SourceReconnected then does for a window it holds.
        Assert.True(pipeline.FollowDial(BulletinDialHz));
        Assert.Equal(BulletinDialHz, pipeline.DialHz);
    }

    // ---------- the receiver as a whole ----------

    private static WebSettings FreeWeb() => new() { Port = FakeSoundModem.FreePort() };

    [Fact]
    public async Task Retune_WithTheSoundmodemSource_RetunesThroughTheReceiveWindow_AndRunsTheHooks()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "soundmodem:8199", StateDirectory = dir.Path, Retune = true, Web = FreeWeb() };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });

        Assert.NotNull(host.Retuner);
        Assert.NotNull(host.Retuner.Window);
        Assert.Null(host.Retuner.Rig);
        Assert.Equal("pdn-soundmodem's receive window on 127.0.0.1:8199", host.Retuner.Via);
        Assert.True(host.RetunerRunsHooks);
    }

    [Fact]
    public async Task Retune_WithAnySourceButSoundmodem_ChangesNothing()
    {
        using var dir = new TempDirectory();
        var withRig = new ReceiverConfig
        {
            Audio = Samples.SoundCard,
            StateDirectory = dir.Path,
            Retune = true,
            Rig = new RigSettings { Rigctld = "127.0.0.1:4532", DedicatedRadio = true },
            Web = FreeWeb(),
        };
        await using (var host = new ReceiverHost(withRig, TimeProvider.System, _ => { }))
        {
            Assert.NotNull(host.Retuner);
            Assert.Null(host.Retuner.Window);
            Assert.Equal("rigctld at 127.0.0.1:4532", host.Retuner.Via);
        }

        foreach (string audio in new[] { Samples.SoundCard, "ubersdr:wessex.zapto.org", "wav:/nonexistent.wav" })
        {
            await using var host = new ReceiverHost(new ReceiverConfig { Audio = audio, StateDirectory = dir.Path, Retune = true, Web = FreeWeb() }, TimeProvider.System, _ => { });
            Assert.Null(host.Retuner);
        }
    }

    [Fact]
    public async Task Retune_WithAnotherSource_IsSaidToDoNothing()
    {
        using var dir = new TempDirectory();
        var log = new ConcurrentQueue<string>();
        await using var host = new ReceiverHost(new ReceiverConfig { Audio = "wav:/nonexistent.wav", StateDirectory = dir.Path, Retune = true, Web = FreeWeb() }, TimeProvider.System, log.Enqueue);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        await stop.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Contains(log, l => l == "config: \"retune\" is for \"audio\": \"soundmodem\" only, where pdn-soundmodem's receive window retunes its radio for each slot; it does nothing with this source");
    }

    [Fact]
    public void Retune_IsReadFromTheFile_AndLeftOutOfASavedFileWhileOff()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "soundmodem", "retune": true }""");

        var on = ReceiverConfig.Load(path);
        Assert.True(on.Retune);
        on.Save(path);
        Assert.Contains("\"retune\": true", File.ReadAllText(path), StringComparison.Ordinal);

        (on with { Retune = false }).Save(path);
        Assert.DoesNotContain("retune", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(ReceiverConfig.Load(path).Retune);
    }

    // ---------- the audio's placement ----------

    [Fact]
    public async Task WithRetune_ARadioOutOfReach_IsNotRefused_AndTheSignalIsPlacedByTheMailcastDial()
    {
        // 7.060 MHz puts the signal below the dial: nothing pdn-soundmodem's channel carries.
        await using var fake = new FakeSoundModem(dialHz: 7_060_000);
        var log = new ConcurrentQueue<string>();
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, log.Enqueue, TimeProvider.System, retunedByWindow: true);
        await using var _ = pipeline;

        await pipeline.OpenAsync(CancellationToken.None);
        var connection = await fake.NextAsync();

        Assert.Equal(BulletinDialHz, pipeline.DialHz);
        Assert.False(pipeline.DialFromSoundModem);
        Assert.Equal(1800, pipeline.Tone.ExpectedHz, 3);
        Assert.Contains(log, l => l.Contains("the radio on USB dial 7.060 MHz as pdn-soundmodem says, which puts the signal out of reach until its receive window tunes it to 7.052 MHz (\"dialKHz\") for each slot, the signal's centre then at 1800 Hz", StringComparison.Ordinal));
        // The band asked for is the one the signal has once the window has tuned the radio.
        using var identify = JsonDocument.Parse(await connection.Said.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(350, identify.RootElement.GetProperty("band").GetProperty("lowHz").GetInt32());
        Assert.Equal(3250, identify.RootElement.GetProperty("band").GetProperty("highHz").GetInt32());
    }

    [Fact]
    public async Task WithRetune_ThePlacementFollowsTheDialTheWindowMovesTheRadioTo()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, _ => { }, TimeProvider.System, retunedByWindow: true);
        await using var _ = pipeline;
        await pipeline.OpenAsync(CancellationToken.None);
        Assert.Equal(7_052_300, pipeline.DialHz);

        // The window is taken: the radio is on the mailcast dial.
        Assert.True(pipeline.FollowDial(BulletinDialHz));
        Assert.Equal(BulletinDialHz, pipeline.DialHz);
        // Given back to a dial in reach: placed by that.
        Assert.True(pipeline.FollowDial(7_052_300));
        Assert.Equal(7_052_300, pipeline.DialHz);
        // Given back to a dial out of reach: nothing is heard there, so placed for the next window.
        Assert.True(pipeline.FollowDial(7_060_000));
        Assert.Equal(BulletinDialHz, pipeline.DialHz);
        // Given back to a dial the channel carries off-centre: placed there, and the audio moved
        // to 1800 Hz once the audio thread has the next block.
        Assert.True(pipeline.FollowDial(7_040_000));
        Assert.Equal(7_040_000, pipeline.DialHz);
        Assert.Equal(1800, AudioPipeline.AudioCentreFor(pipeline.DialHz), 3);
        var block = new float[OnAir.SampleRate / 10];
        // A block the input made before it was asked to move (no tag) is dropped, not fed under
        // the new placement.
        Assert.False(pipeline.FeedTagged(block, default, movedFrom: null, transmitted: false, gap: false));
        Assert.Equal(1, pipeline.DroppedAtRetune);
        Assert.Equal(1800, pipeline.AudioCentreHz, 3);
        Assert.Equal(1800, pipeline.Tone.ExpectedHz, 3);
        Assert.Equal(1, pipeline.ModemResets);
        // One made with the new move goes through.
        Assert.True(pipeline.FeedTagged(block, block, movedFrom: 13_800, transmitted: false, gap: false));
        Assert.Equal(1, pipeline.DroppedAtRetune);
        // Not said: the hello's dial, in reach.
        Assert.True(pipeline.FollowDial(null));
        Assert.Equal(7_052_300, pipeline.DialHz);
        Assert.False(pipeline.FollowDial(null));
    }

    [Fact]
    public async Task WithoutRetune_ThePlacementNeverMoves_AndARadioOutOfReachIsStillRefused()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, _ => { }, TimeProvider.System);
        await using var _ = pipeline;
        await pipeline.OpenAsync(CancellationToken.None);

        Assert.False(pipeline.FollowDial(BulletinDialHz));
        Assert.Equal(7_052_300, pipeline.DialHz);

        await using var away = new FakeSoundModem(dialHz: 7_060_000);
        var refused = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, away.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BulletinDialHz, _ => { }, TimeProvider.System);
        await using var __ = refused;
        var e = await Assert.ThrowsAsync<AudioSourceException>(() => refused.OpenAsync(CancellationToken.None));
        Assert.Contains("set \"retune\": true here for its receive window to tune the radio for each slot", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePage_SaysTheReceiveWindowRetunes_NotRigctldOrLinBpq()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "soundmodem:8199", StateDirectory = dir.Path, Retune = true, Web = FreeWeb() };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        var page = new Web.StatusPage(host, null, _ => { });
        try
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Status(), ReceiverConfig.JsonLine));
            var retune = json.RootElement.GetProperty("retune");
            Assert.Equal("127.0.0.1:8199", retune.GetProperty("window").GetString());
            Assert.Equal(JsonValueKind.Null, retune.GetProperty("rigctld").ValueKind);
            Assert.Equal(JsonValueKind.Null, retune.GetProperty("linBpq").ValueKind);
        }
        finally
        {
            try
            {
                await page.DisposeAsync();
            }
            catch (System.Net.HttpListenerException)
            {
            }
        }
    }

    [Fact]
    public async Task APdnSoundmodemDialledOnAnLnbsIf_IsOpened_WhenTheLnbStationIsKnown()
    {
        // An RTL dongle on a Bullseye: pdn-soundmodem's dial on the IF, 739.735855 MHz USB.
        const double IfDial = 739_735_855;
        await using var fake = new FakeSoundModem(dialHz: IfDial);
        var source = new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var pipeline = AudioPipeline.Create(source, 7_052_000, _ => { }, TimeProvider.System);
        await using var _ = pipeline;
        pipeline.AlternateSignals = [(739_737_655, 9_750_012_345)];
        await pipeline.OpenAsync(CancellationToken.None);

        Assert.Equal(IfDial, pipeline.DialHz, 0.5);
        Assert.Equal(739_737_655, pipeline.SignalHz);
        Assert.Equal(9_750_012_345, pipeline.LnbOffsetHz);
        Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
        // The page's spectrogram is labelled with the downlink dial.
        Assert.Equal(IfDial + 9_750_012_345, pipeline.SpectrumDialHz + pipeline.LnbOffsetHz, 0.5);
    }

    [Fact]
    public async Task APdnSoundmodemDialledOnAnLnbsIf_WithNoLnbStation_IsStillRefused()
    {
        await using var fake = new FakeSoundModem(dialHz: 739_735_855);
        var source = new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var pipeline = AudioPipeline.Create(source, 7_052_000, _ => { }, TimeProvider.System);
        await using var _ = pipeline;
        await Assert.ThrowsAsync<AudioSourceException>(() => pipeline.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFreshReceiverOnAnLnbsIf_OpensAnyway_ListensOnTheDownlink_AndLearnsAStationOutsideAnyWindow()
    {
        // Only the QO-100 dongle: no advert, no station known, pdn-soundmodem's dial on the IF.
        const double IfDial = 739_735_855;
        await using var fake = new FakeSoundModem(dialHz: IfDial);
        var source = new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = $"soundmodem:{fake.Port}", StateDirectory = dir.Path, Daylight = null, LnbHz = 9_750_012_345 };
        var log = new List<string>();
        var noon = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        // 12:47 UTC: not in any slot window of GB7RDG's.
        var now = noon.AddMinutes(47);
        await using var host = new ReceiverHost(config, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now), l => { lock (log) { log.Add(l); } });
        var pipeline = host.CreatePipeline(source, config);
        await using var _ = pipeline;
        await pipeline.OpenAsync(CancellationToken.None);

        Assert.Equal(IfDial, pipeline.DialHz, 0.5);
        Assert.Equal(IfDial + 1800, pipeline.SignalHz, 0.5);
        Assert.Equal(9_750_012_345, pipeline.LnbOffsetHz);
        Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
        Assert.Contains(log, l => l.Contains("listening continuously for 10489.7", StringComparison.Ordinal) && l.Contains("until it hears a station", StringComparison.Ordinal));
        host.ReturnToConfig(pipeline);
        Assert.Equal(IfDial + 1800, pipeline.SignalHz, 0.5);

        // A new station's signed directory arrives and is learnt.
        var station = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var slot = new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.Zero);
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = station, Station = "G8BPQ", Details = new StationDetails(10_489_750_000, Sideband.Usb, "IO83") }, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
        Assert.Contains(host.Intake.StationDirectories(), d => d.Station == "G8BPQ");
    }
}
