using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A burst too weak to read, as GB7RDG heard through a closed band, must not leave the modem
/// demodulating noise for ever: that cost a whole core on a Pi and left the receiver deaf to every
/// later slot until it was restarted.
/// </summary>
public class ModemLockTests
{
    private const int Rate = OnAir.SampleRate;
    private const int Block = Rate / 10;
    private const double NoiseSigma = 0.05;

    /// <summary>Never read: these tests feed the pipeline themselves, on their own thread.</summary>
    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;

        public int Read(Span<float> destination) => 0;
    }

    /// <summary>Seeded noise, with bursts added where they are put.</summary>
    private sealed class Air(int seed)
    {
        private readonly Random _rng = new(seed);

        public long Position { get; private set; }

        public float[] Noise(int samples)
        {
            var block = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                double u1 = 1.0 - _rng.NextDouble();
                double u2 = _rng.NextDouble();
                block[i] = (float)(NoiseSigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }
            Position += samples;
            return block;
        }

        /// <summary>One MS110D burst at <paramref name="snrDb"/> over the noise in 3 kHz, in noise.</summary>
        public float[] Burst(byte[] frame, double snrDb)
        {
            float[] burst = new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = 4 }).Modulate(frame, 0);
            double power = burst.Average(s => (double)s * s);
            double noiseIn3k = NoiseSigma * NoiseSigma * 3000 / (Rate / 2.0);
            double gain = Math.Sqrt(noiseIn3k * Math.Pow(10, snrDb / 10) / power);
            float[] air = Noise(burst.Length);
            for (int i = 0; i < air.Length; i++)
            {
                air[i] += (float)(burst[i] * gain);
            }
            return air;
        }
    }

    private static byte[] Frame(int seed)
    {
        var payload = new byte[200];
        new Random(seed).NextBytes(payload);
        return Ax25UiFrame.Build(OnAir.Source, OnAir.Destination, payload);
    }

    private static void Feed(AudioPipeline pipeline, float[] audio)
    {
        for (int offset = 0; offset < audio.Length; offset += Block)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(Block, audio.Length - offset)));
        }
    }

    [Fact]
    public async Task WeakBurst_LockIsLetGoAfterTheLongestBurst_AndTheNextBurstIsHeard()
    {
        var limit = TimeSpan.FromSeconds(10);
        var log = new List<string>();
        var pipeline = AudioPipeline.ForInput(new NoInput(), log.Add, new FakeTimeProvider(), watch: false, longestBurst: limit);
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => frames++;
        var air = new Air(seed: 2026);

        Feed(pipeline, air.Noise(Rate));
        Assert.False(pipeline.Channel.CarrierDetect);

        // A burst whose preamble is heard but whose data cannot be read.
        long lockedAt = -1;
        float[] weak = air.Burst(Frame(1), snrDb: -6);
        long burstStart = air.Position - weak.Length;
        for (int offset = 0; offset < weak.Length; offset += Block)
        {
            pipeline.Feed(weak.AsSpan(offset, Math.Min(Block, weak.Length - offset)));
            if (lockedAt < 0 && pipeline.Channel.CarrierDetect)
            {
                lockedAt = burstStart + offset;
            }
        }
        Assert.True(lockedAt >= 0, "the weak burst's preamble should have been heard");
        Assert.Equal(0, frames);

        // Noise only from here. The modem stays locked on it until the limit, and is then let go.
        long releasedAt = -1;
        for (int block = 0; block < 30 * 10 && releasedAt < 0; block++)
        {
            Assert.True(pipeline.Channel.CarrierDetect, "the modem let go by itself, before the limit");
            pipeline.Feed(air.Noise(Block));
            if (pipeline.LocksReleased > 0)
            {
                releasedAt = air.Position;
            }
        }
        Assert.True(releasedAt >= 0, "the modem was never let go");
        double lockedSeconds = (releasedAt - lockedAt) / (double)Rate;
        // Counted from the preamble's block; the preamble may be heard, dropped and heard again
        // within the burst, which starts the count afresh, so allow a second on top.
        Assert.InRange(lockedSeconds, limit.TotalSeconds, limit.TotalSeconds + 1);
        Assert.False(pipeline.Channel.CarrierDetect);
        Assert.Contains(log, l => l.Contains("listening afresh", StringComparison.Ordinal));

        // Listening afresh: it does not lock on the noise again, and it hears the next real burst.
        Feed(pipeline, air.Noise(2 * Rate));
        Assert.False(pipeline.Channel.CarrierDetect);
        Feed(pipeline, air.Burst(Frame(2), snrDb: 10));
        Feed(pipeline, air.Noise(2 * Rate));
        Assert.Equal(1, frames);
        Assert.Equal(1, pipeline.LocksReleased);
        Assert.False(pipeline.Channel.CarrierDetect);

        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task ReadableBursts_AreNeverCut()
    {
        var log = new List<string>();
        var pipeline = AudioPipeline.ForInput(new NoInput(), log.Add, new FakeTimeProvider(), watch: false, longestBurst: TimeSpan.FromSeconds(10));
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => frames++;
        var air = new Air(seed: 7);

        // A slot's worth of readable bursts back to back, longer in all than the limit: each ends
        // with its EOM, so the lock is counted per burst and never reaches it.
        Feed(pipeline, air.Noise(Rate));
        for (int i = 0; i < 6; i++)
        {
            Feed(pipeline, air.Burst(Frame(10 + i), snrDb: 10));
            Feed(pipeline, air.Noise(Rate / 2));
        }
        Feed(pipeline, air.Noise(Rate));

        Assert.Equal(6, frames);
        Assert.Equal(0, pipeline.LocksReleased);
        await pipeline.DisposeAsync();
    }
}
