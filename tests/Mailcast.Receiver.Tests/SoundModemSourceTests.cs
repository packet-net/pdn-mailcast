using System.Text.Json;
using Mailcast.Receiver.Hooks;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// pdn-soundmodem as the audio source (issue #77), against <see cref="FakeSoundModem"/>: the
/// setting, the stream's shape and rates, its gaps and keyed spans, the modem made to listen
/// afresh after each, reconnecting, the dial pdn-soundmodem says, and "Measure my filter".
/// </summary>
public class SoundModemSourceTests
{
    private const int Block48 = OnAir.SampleRate / 10;

    private static float[] Sine(int rate, double hz, int count, int start = 0)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * (start + i) / rate));
        }
        return samples;
    }

    /// <summary>Reads one block from the input on a thread of its own, as the audio thread does.</summary>
    private static Task<(float[] Samples, bool Transmitted, bool Gap)> ReadAsync(SoundModemAudioInput input) => Task.Run(() =>
    {
        var block = new float[Block48];
        for (int tries = 0; tries < 20; tries++)
        {
            int read = input.ReadBlock(block, out bool transmitted, out bool gap);
            if (read > 0)
            {
                return (block[..read], transmitted, gap);
            }
        }
        throw new TimeoutException("no block came");
    });

    // Generous, for a loaded CI runner: a pass takes milliseconds, so only a failure waits this long.
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(30));

    // ---------- the setting ----------

    [Theory]
    [InlineData("soundmodem", 8107, "soundmodem")]
    [InlineData("SoundModem", 8107, "soundmodem")]
    [InlineData(" soundmodem:8107 ", 8107, "soundmodem")]
    [InlineData("soundmodem:8108", 8108, "soundmodem:8108")]
    public void Parse_SoundModem_GivesItsPort(string setting, int port, string written)
    {
        var source = AudioSource.Parse(setting);

        Assert.Equal(AudioSourceKind.SoundModem, source.Kind);
        Assert.Equal(port, source.SoundModemPort);
        Assert.Equal(written, source.ToString());
    }

    [Theory]
    [InlineData("soundmodem:")]
    [InlineData("soundmodem:0")]
    [InlineData("soundmodem:65536")]
    [InlineData("soundmodem:http")]
    [InlineData("soundmodem:-1")]
    public void Parse_SoundModemWithABadPort_SaysWhat(string setting)
    {
        var e = Assert.Throws<ConfigException>(() => AudioSource.Parse(setting));
        Assert.Contains("page port", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherSources_AreUnchanged()
    {
        Assert.Equal(AudioSourceKind.Alsa, AudioSource.Parse("plughw:CARD=Device,DEV=0", windows: false).Kind);
        Assert.Equal(AudioSourceKind.UberSdr, AudioSource.Parse("ubersdr:wessex.zapto.org").Kind);
        Assert.Equal(AudioSourceKind.Wav, AudioSource.Parse("wav:/tmp/x.wav").Kind);
        Assert.Equal("soundModem", Web.StatusPage.AudioKind("soundmodem"));
        Assert.Equal("clippingOnly", Web.StatusPage.LevelAdvice("soundmodem"));
        Assert.Equal("target", Web.StatusPage.LevelAdvice(Samples.SoundCard));
    }

    // ---------- shape ----------

    [Fact]
    public async Task Open_ConnectsAsALocalProgram_AndSaysWhoItIsAndTheBandItNeeds()
    {
        await using var fake = new FakeSoundModem();
        var log = new List<string>();
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReceiverConfig.DefaultDialKHz * 1000, line => { lock (log) { log.Add(line); } }, TimeProvider.System, pagePort: 18135);
        await using var _ = pipeline;
        await pipeline.OpenAsync(CancellationToken.None);
        var connection = await fake.NextAsync();

        Assert.StartsWith("GET /channel-audio HTTP/1.1", connection.Request, StringComparison.Ordinal);
        Assert.DoesNotContain("Origin:", connection.Request, StringComparison.OrdinalIgnoreCase);
        using var identify = JsonDocument.Parse(await Within(connection.Said.Reader.ReadAsync().AsTask()));
        Assert.Equal("pdn-mailcast-receiver", identify.RootElement.GetProperty("name").GetString());
        Assert.Equal(18135, identify.RootElement.GetProperty("pagePort").GetInt32());
        // The signal's centre at 1800 Hz on the usual dial, 1450 Hz either side.
        Assert.Equal(350, identify.RootElement.GetProperty("band").GetProperty("lowHz").GetInt32());
        Assert.Equal(3250, identify.RootElement.GetProperty("band").GetProperty("highHz").GetInt32());
        Assert.False(pipeline.DialFromSoundModem);
        lock (log)
        {
            Assert.Contains(log, l => l.Contains($"pdn-soundmodem on 127.0.0.1:{fake.Port}, its channel at 48000 Hz", StringComparison.Ordinal)
                && l.Contains("does not say where its radio is", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Blocks_At48kHz_ComeThroughSampleForSample()
    {
        await using var fake = new FakeSoundModem();
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        var sent = Sine(OnAir.SampleRate, 1000, Block48);

        await connection.SendAsync(0, sent);
        var (samples, transmitted, gap) = await Within(ReadAsync(input));

        Assert.Equal(OnAir.SampleRate, input.ChannelRate);
        Assert.Equal(sent, samples);
        Assert.False(transmitted);
        Assert.False(gap);
    }

    // ---------- rates ----------

    [Fact]
    public async Task A12kHzChannel_IsBroughtUpTo48kHz_AsARecordingAtThatRateIs()
    {
        await using var fake = new FakeSoundModem(rateHz: 12000);
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        const int blocks = 5, each = 1200;
        var whole = Sine(12000, 1000, blocks * each);

        var heard = new List<float>();
        for (int b = 0; b < blocks; b++)
        {
            await connection.SendAsync((ulong)(b * each), whole[(b * each)..((b + 1) * each)]);
            var (samples, _, gap) = await Within(ReadAsync(input));
            Assert.Equal(4 * each, samples.Length);
            Assert.False(gap);
            heard.AddRange(samples);
        }

        // Block by block, the same as the whole thing at once: the upsampler carries on across blocks.
        var expected = AudioPipeline.ToPipelineRate(whole, 12000);
        Assert.Equal(expected.Length, heard.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], heard[i], 1e-5f);
        }
    }

    [Theory]
    [InlineData(11025)]
    [InlineData(96000)]
    [InlineData(44100)]
    public async Task ARateNotAWholeFractionOf48kHz_IsRefusedClearly(int rate)
    {
        await using var fake = new FakeSoundModem(rateHz: rate);

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None));

        Assert.Contains($"runs at {rate} Hz, which is not a whole fraction of 48000 Hz", e.Message, StringComparison.Ordinal);
        Assert.True(e.Refused);
    }

    [Fact]
    public void AChannelTooSlowForTheSignal_IsOutOfReach()
    {
        Assert.Null(AudioPipeline.SoundModemReach(12000, null, 7_052_000));
        Assert.Null(AudioPipeline.SoundModemReach(8000, null, 7_052_000));
        Assert.Contains("carries audio only up to 3000 Hz", AudioPipeline.SoundModemReach(6000, null, 7_052_000), StringComparison.Ordinal);
    }

    [Theory]
    // A headless Flex on 7.050 MHz: the signal at 2350 to 5250 Hz, as pdn-soundmodem's built-in receiver took it.
    [InlineData(48000, 7_050_000)]
    [InlineData(12000, 7_050_000)]
    // The highest centre each channel carries whole, and the lowest this receiver takes.
    [InlineData(48000, 7_031_250)]
    [InlineData(12000, 7_049_250)]
    [InlineData(48000, 7_052_800)]
    public void ADialWhoseChannelCarriesTheWholeSignal_IsInReach(int rate, double dialHz)
    {
        Assert.Null(AudioPipeline.SoundModemReach(rate, dialHz, dialHz));
    }

    // ---------- gaps and keyed spans ----------

    [Fact]
    public async Task GapsAndKeyedBlocks_AreMarked_IncludingAnIndexThatJumps()
    {
        await using var fake = new FakeSoundModem();
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        var audio = Sine(OnAir.SampleRate, 1000, Block48);

        await connection.SendAsync(0, audio);
        await connection.SendAsync(4800, audio, gap: true);          // the input lost audio: index runs on
        await connection.SendAsync(4 * 4800, audio);                 // two blocks missing, unmarked
        await connection.SendAsync(5 * 4800, audio, transmitted: true);
        await connection.SendAsync(6 * 4800, audio);

        Assert.False((await Within(ReadAsync(input))).Gap);
        Assert.True((await Within(ReadAsync(input))).Gap);
        Assert.True((await Within(ReadAsync(input))).Gap);
        var keyed = await Within(ReadAsync(input));
        Assert.True(keyed.Transmitted);
        Assert.False(keyed.Gap);
        Assert.All(keyed.Samples, s => Assert.Equal(0f, s));
        var after = await Within(ReadAsync(input));
        Assert.False(after.Transmitted);
        Assert.False(after.Gap);
    }

    [Fact]
    public async Task AReaderTooSlow_LosesTheOldestBlocks_AndTheNextIsAGap()
    {
        await using var fake = new FakeSoundModem();
        using var input = await SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None);
        var connection = await fake.NextAsync();
        var audio = Sine(OnAir.SampleRate, 1000, Block48);
        int sent = SoundModemAudioInput.QueueBlocks + 3;
        for (int b = 0; b < sent; b++)
        {
            await connection.SendAsync((ulong)(b * Block48), audio);
        }
        // Once the last one is in, the queue has dropped three to hold it.
        await UntilAsync(() => input.BlocksReceived == sent);

        var first = await Within(ReadAsync(input));
        Assert.True(first.Gap);
        for (int b = 1; b < SoundModemAudioInput.QueueBlocks; b++)
        {
            Assert.False((await Within(ReadAsync(input))).Gap);
        }
    }

    /// <summary>A pipeline on the fake, started, with a count of the blocks it has taken in.</summary>
    private static async Task<(AudioPipeline Pipeline, FakeSoundModem.Connection Connection, Func<int> Fed)> StartAsync(FakeSoundModem fake, List<string>? log = null)
    {
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReceiverConfig.DefaultDialKHz * 1000, line => { if (log is not null) { lock (log) { log.Add(line); } } }, TimeProvider.System);
        await pipeline.OpenAsync(CancellationToken.None);
        var connection = await fake.NextAsync();
        int fed = 0;
        pipeline.Tone.ToneMeasured += _ => { };
        pipeline.Channel.AddReceiveTap(_ => Interlocked.Increment(ref fed));
        await pipeline.StartAsync(CancellationToken.None);
        return (pipeline, connection, () => Volatile.Read(ref fed));
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task KeyedSpansOnTheStream_ResetTheModemOnceEach_AfterTheSpanNotDuringIt()
    {
        await using var fake = new FakeSoundModem();
        var (pipeline, connection, fed) = await StartAsync(fake);
        await using var _ = pipeline;
        var audio = Sine(OnAir.SampleRate, 1000, Block48);
        ulong index = 0;
        async Task Send(bool transmitted = false, bool gap = false)
        {
            await connection.SendAsync(index, audio, transmitted, gap);
            index += Block48;
        }

        await Send();
        await Send();
        await Send(transmitted: true);
        await Send(transmitted: true);
        await UntilAsync(() => fed() == 4);
        Assert.Equal(0, pipeline.ModemResets);

        await Send();
        await UntilAsync(() => fed() == 5);
        Assert.Equal(1, pipeline.ModemResets);

        await Send();
        await Send(transmitted: true);
        await Send();
        await UntilAsync(() => fed() == 8);
        Assert.Equal(2, pipeline.ModemResets);
    }

    [Fact]
    public async Task GapsOnTheStream_ResetTheModem_MarkedOrByAJumpInTheIndex()
    {
        await using var fake = new FakeSoundModem();
        var log = new List<string>();
        var (pipeline, connection, fed) = await StartAsync(fake, log);
        await using var _ = pipeline;
        var audio = Sine(OnAir.SampleRate, 1000, Block48);

        await connection.SendAsync(0, audio);
        await connection.SendAsync(4800, audio);
        await UntilAsync(() => fed() == 2);
        Assert.Equal(0, pipeline.ModemResets);

        await connection.SendAsync(9600, audio, gap: true);
        await connection.SendAsync(14400, audio);
        await UntilAsync(() => fed() == 4);
        Assert.Equal(1, pipeline.ModemResets);

        // An index that jumps is a gap too, unmarked or not.
        await connection.SendAsync(10 * 4800, audio);
        await UntilAsync(() => fed() == 5);
        Assert.Equal(2, pipeline.ModemResets);
        lock (log)
        {
            // Said once, then held back for a minute.
            Assert.Single(log, l => l.Contains("lost some audio", StringComparison.Ordinal));
        }
    }

    // ---------- the modem listening afresh, on real MS110D audio ----------

    /// <summary>Never read: these tests feed the pipeline themselves.</summary>
    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => OnAir.SampleRate;

        public int Read(Span<float> destination) => 0;
    }

    /// <summary>A frame of GB7RDG's, of random bytes.</summary>
    private static byte[] Frame(int seed)
    {
        var payload = new byte[120];
        new Random(seed).NextBytes(payload);
        return Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload);
    }

    /// <summary>One MS110D burst at the modem's own 1800 Hz, moved to <paramref name="centreHz"/>.</summary>
    private static float[] Burst(byte[] frame, double centreHz = OnAir.CentreAudioHz)
    {
        float[] native = new Ms110dModem(OnAir.SampleRate, _ => { }, new Ms110dTxSettings { WaveformNumber = 4 }).Modulate(frame, 0);
        if (Math.Abs(centreHz - OnAir.CentreAudioHz) < 0.5)
        {
            return native;
        }
        var moved = new float[native.Length];
        new FrequencyShifter(OnAir.SampleRate, centreHz - OnAir.CentreAudioHz).Process(native, moved);
        return moved;
    }

    /// <summary>Seeded noise at 48 kHz, <paramref name="snrDb"/> below <paramref name="signal"/>'s power in 3 kHz, added to it.</summary>
    private static float[] InNoise(float[] signal, double snrDb, int seed)
    {
        var rng = new Random(seed);
        double power = signal.Average(s => (double)s * s);
        double sigma = Math.Sqrt(power / Math.Pow(10, snrDb / 10) * (OnAir.SampleRate / 2.0) / 3000);
        var air = new float[signal.Length];
        for (int i = 0; i < air.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            air[i] = signal[i] + (float)(sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return air;
    }

    private static void FeedStream(AudioPipeline pipeline, float[] audio, bool transmitted = false)
    {
        for (int offset = 0; offset < audio.Length; offset += Block48)
        {
            pipeline.FeedStream(audio.AsSpan(offset, Math.Min(Block48, audio.Length - offset)), transmitted, gap: false);
        }
    }

    /// <summary>
    /// A pipeline that has heard the first half of one burst, preamble and all, so its modem is
    /// locked on it part way through; and the frames it decodes from then on.
    /// </summary>
    private static (AudioPipeline Pipeline, List<byte[]> Frames) HalfwayThroughABurst()
    {
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, TimeProvider.System, watch: false);
        var frames = new List<byte[]>();
        pipeline.Channel.FrameReceived += (_, frame) => frames.Add(frame);
        FeedStream(pipeline, new float[OnAir.SampleRate]);
        var first = Burst(Frame(1));
        FeedStream(pipeline, first[..(first.Length / 2)]);
        Assert.True(pipeline.Channel.CarrierDetect, "the half burst's preamble should have been heard");
        Assert.Empty(frames);
        return (pipeline, frames);
    }

    [Fact]
    public async Task HalfABurst_ThenSilenceThatIsNotMarked_LeavesTheModemLockedOnIt()
    {
        // What the reset is for: without it the modem is still part way through the old burst.
        var (pipeline, frames) = HalfwayThroughABurst();
        await using var _ = pipeline;

        FeedStream(pipeline, new float[OnAir.SampleRate / 2]);

        Assert.True(pipeline.Channel.CarrierDetect);
        Assert.Equal(0, pipeline.ModemResets);
        Assert.Empty(frames);
    }

    [Fact]
    public async Task TheModem_ListensAfresh_AfterAKeyedSpan_NotDuringIt_AndDecodesTheNextWholeBurst()
    {
        var (pipeline, frames) = HalfwayThroughABurst();
        await using var _ = pipeline;

        // Keyed: still where it was, part way through the old burst.
        FeedStream(pipeline, new float[OnAir.SampleRate / 2], transmitted: true);
        Assert.True(pipeline.Channel.CarrierDetect);
        Assert.Equal(0, pipeline.ModemResets);

        // The first block heard again: the old burst is let go, and the modem is searching.
        FeedStream(pipeline, new float[Block48]);
        Assert.Equal(1, pipeline.ModemResets);
        Assert.False(pipeline.Channel.CarrierDetect);

        var next = Frame(2);
        FeedStream(pipeline, Burst(next));
        FeedStream(pipeline, new float[OnAir.SampleRate]);
        Assert.Equal([next], frames.Select(f => f[..next.Length]));
    }

    [Fact]
    public async Task TheModem_ListensAfresh_OnAGap_AndDecodesTheNextWholeBurst()
    {
        var (pipeline, frames) = HalfwayThroughABurst();
        await using var _ = pipeline;

        pipeline.FeedStream(new float[Block48], transmitted: false, gap: true);
        Assert.Equal(1, pipeline.ModemResets);
        Assert.False(pipeline.Channel.CarrierDetect);

        var next = Frame(2);
        FeedStream(pipeline, Burst(next));
        FeedStream(pipeline, new float[OnAir.SampleRate]);
        Assert.Equal([next], frames.Select(f => f[..next.Length]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7_050_000.0)] // moved from 3800 Hz, so the move's filters are cleared on the gap too
    public async Task ThroughTheStream_HalfABurst_AKeyedSpan_AndAGap_ThenWholeBursts_DecodeOnlyTheWholeOnes(double? dialHz)
    {
        await using var fake = new FakeSoundModem(dialHz: dialHz);
        var (pipeline, connection, fed) = await StartAsync(fake);
        await using var _ = pipeline;
        double centre = OnAir.AudioCentreHz(dialHz ?? ReceiverConfig.DefaultDialKHz * 1000);
        Assert.Equal(OnAir.CentreAudioHz, pipeline.AudioCentreHz);
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        ulong index = 0;
        int sent = 0;
        async Task Send(float[] audio, bool transmitted = false, bool gap = false)
        {
            for (int offset = 0; offset < audio.Length; offset += Block48)
            {
                var block = audio[offset..Math.Min(audio.Length, offset + Block48)];
                await connection.SendAsync(index, block, transmitted, gap && offset == 0);
                index += (ulong)block.Length;
                // No faster than the pipeline takes it in, so its queue never has to drop any.
                if (++sent % 16 == 0)
                {
                    int now = sent;
                    await UntilAsync(() => fed() == now);
                }
            }
        }
        var half = Burst(Frame(1), centre);
        half = half[..(half.Length / 2)];

        await Send(new float[OnAir.SampleRate]);
        await Send(half);
        await Send(new float[OnAir.SampleRate / 2], transmitted: true);
        await Send(Burst(Frame(2), centre));
        await Send(new float[OnAir.SampleRate / 2]);
        await Send(half);
        await Send(new float[OnAir.SampleRate / 2], gap: true);
        await Send(Burst(Frame(3), centre));
        await Send(new float[OnAir.SampleRate]);
        await UntilAsync(() => fed() == sent);

        Assert.Equal(2, Volatile.Read(ref frames));
        Assert.Equal(2, pipeline.ModemResets);
    }

    // ---------- a dial off the usual 1000 to 2000 Hz, as the built-in receiver took it ----------

    [Theory]
    [InlineData(48000, 7_050_000)] // a headless Flex on 7.050 MHz: the signal's centre at 3800 Hz
    [InlineData(12000, 7_050_000)]
    [InlineData(48000, 7_045_000)] // 8800 Hz, only a 48 kHz channel carries it
    public async Task AnOffCentreDial_IsMovedToTheModemsCentre_AndDecodes(int rate, double dialHz)
    {
        await using var fake = new FakeSoundModem(rateHz: rate, dialHz: dialHz);
        var log = new List<string>();
        var (pipeline, connection, fed) = await StartAsync(fake, log);
        await using var _ = pipeline;
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        double centre = OnAir.AudioCentreHz(dialHz);

        // Asked for where the signal really is, so a headless Flex opens its filter for it.
        using (var identify = JsonDocument.Parse(await Within(connection.Said.Reader.ReadAsync().AsTask())))
        {
            Assert.Equal((int)(centre - OnAir.HalfWidthHz), identify.RootElement.GetProperty("band").GetProperty("lowHz").GetInt32());
            Assert.Equal((int)(centre + OnAir.HalfWidthHz), identify.RootElement.GetProperty("band").GetProperty("highHz").GetInt32());
        }
        Assert.Equal(dialHz, pipeline.DialHz);
        // Everything here reads it moved to the usual dial's 1800 Hz.
        Assert.Equal(OnAir.CentreAudioHz, pipeline.AudioCentreHz);
        Assert.Equal(OnAir.CentreAudioHz, pipeline.Tone.ExpectedHz);
        Assert.Equal(7_052_000, pipeline.SpectrumDialHz);
        lock (log)
        {
            Assert.Contains(log, l => l.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the signal's centre at {centre:F0} Hz in its audio"), StringComparison.Ordinal)
                && l.Contains("a radio on a sound card needs its filter open", StringComparison.Ordinal)
                && l.Contains("Its audio is moved to 1800 Hz here", StringComparison.Ordinal));
        }

        // Two bursts in noise, where the dial puts them, at the channel's own rate.
        var air = InNoise([.. new float[OnAir.SampleRate], .. Burst(Frame(4), centre), .. new float[OnAir.SampleRate / 2], .. Burst(Frame(5), centre), .. new float[OnAir.SampleRate]], snrDb: 10, seed: 77);
        int each = rate / 10;
        if (rate != OnAir.SampleRate)
        {
            var decimator = new Decimator(OnAir.SampleRate, OnAir.SampleRate / rate);
            var slow = new float[decimator.MaxOutput(air.Length)];
            air = slow[..decimator.Process(air, slow)];
        }
        int blocks = air.Length / each;
        for (int b = 0; b < blocks; b++)
        {
            await connection.SendAsync((ulong)(b * each), air[(b * each)..((b + 1) * each)]);
            if (b % 16 == 15)
            {
                int now = b + 1;
                await UntilAsync(() => fed() == now);
            }
        }
        await UntilAsync(() => fed() == blocks);

        Assert.Equal(2, Volatile.Read(ref frames));
    }

    [Fact]
    public async Task AnOffCentreDial_TheSlotsToneIsMeasuredTooAsIfOnTheUsualDial()
    {
        // The tone detector reads only up to 4 kHz; the tone at 3800 Hz is moved to 1800 Hz first.
        await using var fake = new FakeSoundModem(dialHz: 7_050_000);
        var (pipeline, connection, fed) = await StartAsync(fake);
        await using var _ = pipeline;
        var tones = new System.Collections.Concurrent.ConcurrentQueue<ToneReport>();
        pipeline.Tone.ToneMeasured += tones.Enqueue;
        var air = InNoise([.. new float[2 * OnAir.SampleRate], .. Sine(OnAir.SampleRate, 3803, OnAir.ToneSeconds * OnAir.SampleRate), .. new float[3 * OnAir.SampleRate]], snrDb: 10, seed: 3);
        int blocks = air.Length / Block48;
        for (int b = 0; b < blocks; b++)
        {
            await connection.SendAsync((ulong)(b * Block48), air[(b * Block48)..((b + 1) * Block48)]);
            if (b % 16 == 15)
            {
                int now = b + 1;
                await UntilAsync(() => fed() == now);
            }
        }
        await UntilAsync(() => fed() == blocks);

        var tone = Assert.Single(tones);
        // 3 Hz high on 7.050 MHz is 3 Hz high on the usual dial.
        Assert.Equal(1803, tone.FrequencyHz, 0.5);
    }

    [Fact]
    public async Task KeyedSilence_IsNotGivenToTheLevelMeterOrTheFilterMeasurement()
    {
        await using var fake = new FakeSoundModem();
        var (pipeline, connection, fed) = await StartAsync(fake);
        await using var _ = pipeline;
        int heard = 0;
        pipeline.SourceBlock += _ => Interlocked.Increment(ref heard);
        var audio = Sine(OnAir.SampleRate, 1000, Block48);

        await connection.SendAsync(0, audio);
        await connection.SendAsync(4800, audio, transmitted: true);
        await connection.SendAsync(9600, audio);
        await UntilAsync(() => fed() == 3);

        Assert.Equal(2, Volatile.Read(ref heard));
    }

    // ---------- the dial pdn-soundmodem says ----------

    [Fact]
    public async Task ADialPdnSoundmodemSays_IsListenedOn_AndAskedForInItsAudio()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        var log = new List<string>();
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReceiverConfig.DefaultDialKHz * 1000, line => { lock (log) { log.Add(line); } }, TimeProvider.System);
        await using var _ = pipeline;

        await pipeline.OpenAsync(CancellationToken.None);
        var connection = await fake.NextAsync();

        Assert.Equal(7_052_300, pipeline.DialHz);
        Assert.True(pipeline.DialFromSoundModem);
        Assert.Equal(1500, pipeline.Tone.ExpectedHz, 3);
        using var identify = JsonDocument.Parse(await Within(connection.Said.Reader.ReadAsync().AsTask()));
        Assert.Equal(50, identify.RootElement.GetProperty("band").GetProperty("lowHz").GetInt32());
        Assert.Equal(2950, identify.RootElement.GetProperty("band").GetProperty("highHz").GetInt32());
        lock (log)
        {
            Assert.Contains(log, l => l.Contains("7.0523 MHz, as pdn-soundmodem says (\"dialKHz\", 7.052 MHz, is not used while it does)", StringComparison.Ordinal));
        }
    }

    [Theory]
    // Below 1000 Hz the bottom of the signal is under DC.
    [InlineData(48000, 7_053_000, "which would put the signal's centre at 800 Hz in its audio, below the 1000 Hz this receiver needs at least", "from 7.03125 to 7.0528 MHz")]
    // Past what a 12 kHz channel carries.
    [InlineData(12000, 7_048_000, "which would put the signal's centre at 5800 Hz in its audio, and its channel runs at 12000 Hz, which carries audio only up to 6000 Hz, while the signal reaches 7250 Hz", "from 7.04925 to 7.0528 MHz")]
    public async Task ADialThatPutsTheSignalOutOfReach_IsRefused_SayingWhereToSetIt(int rate, double dialHz, string why, string range)
    {
        await using var fake = new FakeSoundModem(rateHz: rate, dialHz: dialHz);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReceiverConfig.DefaultDialKHz * 1000, _ => { }, TimeProvider.System);
        await using var _ = pipeline;

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => pipeline.OpenAsync(CancellationToken.None));

        Assert.Contains("outside what pdn-soundmodem hears", e.Message, StringComparison.Ordinal);
        Assert.Contains($"It says its radio is on {OnAir.Mhz(dialHz)} MHz USB, {why}", e.Message, StringComparison.Ordinal);
        // Where pdn-soundmodem's dial comes from, a Flex's included.
        Assert.Contains("set the dial pdn-soundmodem gives (its \"waterfall\".\"dialFrequencyHz\" if that is set, else its band plan's dial, which also tunes a Flex, else its \"dialFrequency\")", e.Message, StringComparison.Ordinal);
        Assert.Contains(range, e.Message, StringComparison.Ordinal);
        Assert.True(e.Refused);
    }

    [Fact]
    public async Task APdnSoundmodemFromBeforeTheStream_IsNamedAsTooOld()
    {
        // Before 0.88.0 any WebSocket path is the page's own, which starts with its config.
        await using var fake = new FakeSoundModem { Hello = "{\"type\":\"config\",\"sampleRate\":12000}" };

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None));

        Assert.Contains("needs pdn-soundmodem 0.88.0 or later", e.Message, StringComparison.Ordinal);
        Assert.True(e.Refused);
    }

    [Fact]
    public async Task APdnSoundmodemThatGoesBeforeItSaysHello_MayBeRestarting()
    {
        await using var fake = new FakeSoundModem { DropBeforeHello = true };

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None));

        Assert.Contains("broke before it said hello (it may be restarting)", e.Message, StringComparison.Ordinal);
        Assert.False(e.Refused);
    }

    // ---------- reconnecting ----------

    private static ReceiverConfig Config(TempDirectory dir, int port) => new()
    {
        Audio = $"soundmodem:{port}",
        StateDirectory = dir.Path,
        Daylight = null,
        Web = new WebSettings { Port = 18135 },
    };

    private static async Task<AudioCondition> PhaseAsync(System.Threading.Channels.Channel<AudioCondition> conditions, AudioPhase phase)
    {
        while (true)
        {
            var condition = await Within(conditions.Reader.ReadAsync().AsTask());
            if (condition.Phase == phase)
            {
                return condition;
            }
        }
    }

    [Fact]
    public async Task WhenPdnSoundmodemRestarts_TheReceiverConnectsAgain_SayingSo()
    {
        await using var fake = new FakeSoundModem();
        using var dir = new TempDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ReceiverHost(Config(dir, fake.Port), TimeProvider.System, log.Enqueue);
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var first = await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        using (var identify = JsonDocument.Parse(await Within(first.Said.Reader.ReadAsync().AsTask())))
        {
            Assert.Equal(18135, identify.RootElement.GetProperty("pagePort").GetInt32());
        }
        await first.CloseAsync();

        var failed = await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Contains("closed the channel audio stream (it may be restarting)", failed.Problem, StringComparison.Ordinal);
        var second = await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        Assert.Contains(log, l => l.Contains("closed the channel audio stream (it may be restarting). Trying again in 1 s.", StringComparison.Ordinal));
        Assert.NotNull(host.Pipeline);

        await stop.CancelAsync();
        await run;
        await second.Gone.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WhenPdnSoundmodemIsNotUpYet_TheReceiverSaysSo_AndKeepsTrying_SoonerFirst()
    {
        int port = FakeSoundModem.FreePort();
        using var dir = new TempDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ReceiverHost(Config(dir, port), TimeProvider.System, log.Enqueue);
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var failed = await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Contains($"pdn-soundmodem is not answering on 127.0.0.1:{port} (connection refused)", failed.Problem, StringComparison.Ordinal);
        await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Contains(log, l => l.Contains("Trying again in 1 s.", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Trying again in 2 s.", StringComparison.Ordinal));

        await using var fake = new FakeSoundModem(port: port);
        await fake.NextAsync(TimeSpan.FromSeconds(20));
        await PhaseAsync(conditions, AudioPhase.Listening);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Hooks_RunAroundEverySlot_AsForASoundCard_WithTheDialPdnSoundmodemSays()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        using var dir = new TempDirectory();
        string record = Path.Combine(dir.Path, "runs");
        // Half a second before "before" is due for the 12:00 slot (a minute ahead of its window
        // opening at 11:58), so the clock only moves that far and the stream is never starved.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 11, 56, 59, 500, TimeSpan.Zero));
        var config = Config(dir, fake.Port) with
        {
            Hooks = new HooksSettings
            {
                Before = HookScript.Write(dir.Path, "before.sh", record),
                After = HookScript.Write(dir.Path, "after.sh", record),
            },
        };
        config.Validate();
        await using var host = new ReceiverHost(config, time, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        var waits = System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        host.Hooks.Waiting += d => waits.Writer.TryWrite(d);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);

        var wait = await Within(waits.Reader.ReadAsync().AsTask());
        Assert.True(wait <= TimeSpan.FromSeconds(1), $"the hooks loop waits {wait} for \"before\"");
        time.Advance(wait);
        await UntilAsync(() => HookScript.Runs(record).Length > 0);

        // pdn-soundmodem's dial, not "dialKHz"'s 7052.0: the one the audio is heard on.
        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.3 7053.8 none"], HookScript.Runs(record));

        await stop.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData(48000, 7_053_000.0, null)]
    [InlineData(11025, null, null)]
    [InlineData(48000, null, "{\"type\":\"config\",\"sampleRate\":12000}")]
    public async Task WhatPdnSoundmodemGives_ThatCannotBeUsed_IsAskedAboutAgainOnlyAfterMinutes(int rate, double? dialHz, string? hello)
    {
        await using var fake = new FakeSoundModem(rateHz: rate, dialHz: dialHz) { Hello = hello };
        using var dir = new TempDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ReceiverHost(Config(dir, fake.Port), TimeProvider.System, log.Enqueue);
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var failed = await PhaseAsync(conditions, AudioPhase.Failed);

        Assert.True(failed.Lasting);
        Assert.InRange(failed.RetryAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Contains(log, l => l.Contains("Trying again in 5 min.", StringComparison.Ordinal));

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task WhileReconnecting_TheDialIsTheOnePdnSoundmodemLastSaid_ForThePageAndTheHooks()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_052_300);
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir, fake.Port), TimeProvider.System, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        var first = await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        Assert.Equal(7_052_300, host.DialHz);

        // pdn-soundmodem restarts and is slow to say hello again: the receiver is connecting,
        // with a new pipeline that does not know the dial yet.
        var hello = new TaskCompletionSource();
        fake.HelloAfter = hello.Task;
        await first.CloseAsync();
        await PhaseAsync(conditions, AudioPhase.Failed);
        await PhaseAsync(conditions, AudioPhase.Opening);
        Assert.NotNull(host.Pipeline);
        Assert.False(host.Pipeline.DialSettled);

        Assert.Equal(7_052_300, host.DialHz);
        Assert.True(host.DialFromSoundModem);
        Assert.Equal(1500, host.AudioCentreHz, 3);
        // What a hook run now is told in MAILCAST_DIAL_KHZ.
        Assert.Equal(7052.3, host.Hooks.DialKHz!(), 6);

        hello.SetResult();
        await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        Assert.Equal(7_052_300, host.DialHz);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ANewReasonToRefuse_StartsTheLongWaitsAgainFromTheShortest()
    {
        // Too old first; then, upgraded, its dial is out of reach.
        await using var fake = new FakeSoundModem(dialHz: 7_053_000) { Hello = "{\"type\":\"config\",\"sampleRate\":12000}" };
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero));
        await using var host = new ReceiverHost(Config(dir, fake.Port), time, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var tooOld = await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Contains("0.88.0 or later", tooOld.Problem, StringComparison.Ordinal);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(5), tooOld.RetryAt);

        fake.Hello = null;
        time.Advance(TimeSpan.FromMinutes(5));
        var dial = await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Contains("outside what pdn-soundmodem hears", dial.Problem, StringComparison.Ordinal);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(5), dial.RetryAt);

        // The same reason again waits longer.
        time.Advance(TimeSpan.FromMinutes(5));
        var again = await PhaseAsync(conditions, AudioPhase.Failed);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(10), again.RetryAt);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ANewAudioSetting_EndsALongWait_AtOnce()
    {
        await using var refusing = new FakeSoundModem(dialHz: 7_053_000);
        await using var fake = new FakeSoundModem();
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir, refusing.Port), TimeProvider.System, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        Assert.True((await PhaseAsync(conditions, AudioPhase.Failed)).Lasting);

        host.Reconfigure(Config(dir, fake.Port));

        await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        await stop.CancelAsync();
        await run;
    }

    // ---------- measure ----------

    [Fact]
    public async Task MeasureMyFilter_WorksOnPdnSoundmodemsAudio_LeavingOutKeyedSilence()
    {
        await using var fake = new FakeSoundModem(rateHz: 12000);
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero));
        await using var host = new ReceiverHost(Config(dir, fake.Port), time, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        var connection = await fake.NextAsync();
        await PhaseAsync(conditions, AudioPhase.Listening);
        var pipeline = host.Pipeline!;
        int heard = 0;
        pipeline.SourceBlock += _ => Interlocked.Increment(ref heard);

        // A radio passing 300 to 2700 Hz, at the channel's 12 kHz: 10 s of its noise, with a
        // second of keyed silence in the middle that must not count as a quiet band.
        var noise = BandpassNoise(12000, 300, 2700, 10, seed: 7);
        var measuring = host.MeasureFilterAsync(CancellationToken.None);
        int blocks = noise.Length / 1200;
        int keyed = 0;
        for (int b = 0; b < blocks; b++)
        {
            bool transmitted = b is >= 40 and < 50;
            keyed += transmitted ? 1 : 0;
            await connection.SendAsync((ulong)(b * 1200), noise[(b * 1200)..((b + 1) * 1200)], transmitted);
            if (b % 16 == 15)
            {
                // No faster than the pipeline takes it in, so its queue never has to drop any.
                int sent = b + 1 - keyed;
                await UntilAsync(() => Volatile.Read(ref heard) == sent);
            }
        }
        // Every block but the ten keyed ones reaches the measurement.
        await UntilAsync(() => Volatile.Read(ref heard) == blocks - 10);
        time.Advance(ReceiverHost.FilterMeasureDuration);
        var (filter, problem) = await measuring.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(problem);
        Assert.NotNull(filter);
        // The same tolerances FilterScanTests allows the maths on its own.
        Assert.InRange(filter.LowHz, 200, 450);
        Assert.InRange(filter.HighHz, 2550, 2850);
        Assert.InRange(filter.DialKHz, 7052.2, 7052.45);

        await stop.CancelAsync();
        await run;
    }

    /// <summary>White noise band-passed from <paramref name="lowHz"/> to <paramref name="highHz"/> at <paramref name="rate"/>.</summary>
    private static float[] BandpassNoise(int rate, double lowHz, double highHz, int seconds, int seed)
    {
        int n = seconds * rate;
        var rnd = new Random(seed);
        var white = new float[n];
        for (int i = 0; i < n; i++)
        {
            white[i] = (float)((rnd.NextDouble() * 2) - 1) * 0.3f;
        }
        var lowPass = ChannelMaths.LowPass(highHz, rate, 201);
        var highCut = ChannelMaths.LowPass(lowHz, rate, 201);
        var band = new float[n];
        int half = lowPass.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double a = 0, b = 0;
            for (int k = 0; k < lowPass.Length; k++)
            {
                int idx = i + half - k;
                if (idx >= 0 && idx < n)
                {
                    a += lowPass[k] * white[idx];
                    b += highCut[k] * white[idx];
                }
            }
            band[i] = (float)(a - b);
        }
        return band;
    }
}
