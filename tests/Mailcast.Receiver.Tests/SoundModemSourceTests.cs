using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

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

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

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
        Assert.Equal(AudioSourceKind.Alsa, AudioSource.Parse("plughw:CARD=Device,DEV=0").Kind);
        Assert.Equal(AudioSourceKind.UberSdr, AudioSource.Parse("ubersdr:wessex.zapto.org").Kind);
        Assert.Equal(AudioSourceKind.Wav, AudioSource.Parse("wav:/tmp/x.wav").Kind);
        Assert.Equal("soundModem", Web.StatusPage.AudioKind("soundmodem"));
        Assert.Equal("clippingOnly", Web.StatusPage.LevelAdvice("soundmodem"));
        Assert.Equal("target", Web.StatusPage.LevelAdvice("plughw:CARD=Device,DEV=0"));
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
    }

    [Fact]
    public void AChannelTooSlowForTheSignal_IsOutOfReach()
    {
        Assert.Null(AudioPipeline.SoundModemReach(12000, null, 7_052_000));
        Assert.Null(AudioPipeline.SoundModemReach(8000, null, 7_052_000));
        Assert.Contains("carries audio only up to 3000 Hz", AudioPipeline.SoundModemReach(6000, null, 7_052_000), StringComparison.Ordinal);
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
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TheModem_ListensAfresh_AfterEachKeyedSpan_NotDuringIt()
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
    public async Task TheModem_ListensAfresh_OnAGapMarker_InTheSameWay()
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

    [Fact]
    public async Task ADialThatPutsTheSignalOutOfReach_IsRefused_SayingWhy()
    {
        await using var fake = new FakeSoundModem(dialHz: 7_040_000);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.SoundModem, fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReceiverConfig.DefaultDialKHz * 1000, _ => { }, TimeProvider.System);
        await using var _ = pipeline;

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => pipeline.OpenAsync(CancellationToken.None));

        Assert.Contains("outside what pdn-soundmodem hears", e.Message, StringComparison.Ordinal);
        Assert.Contains("It says its radio is on 7.040 MHz USB, which would put the signal's centre at 13800 Hz", e.Message, StringComparison.Ordinal);
        Assert.Contains("from 7.0518 to 7.0528 MHz", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APdnSoundmodemFromBeforeTheStream_IsNamedAsTooOld()
    {
        // Before 0.88.0 any WebSocket path is the page's own, which starts with its config.
        await using var fake = new FakeSoundModem { Hello = "{\"type\":\"config\",\"sampleRate\":12000}" };

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => SoundModemAudioInput.OpenAsync(fake.Port, CancellationToken.None));

        Assert.Contains("needs pdn-soundmodem 0.88.0 or later", e.Message, StringComparison.Ordinal);
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
        await second.Gone.WaitAsync(TimeSpan.FromSeconds(10));
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
    public async Task Hooks_RunAroundEverySlot_AsForASoundCard()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir, 8107), TimeProvider.System, _ => { });

        var window = host.HookWindowAt(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero));

        Assert.NotNull(window);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 1, 0, 0, TimeSpan.Zero), window.Value.Slot);
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
        var (filter, problem) = await measuring.WaitAsync(TimeSpan.FromSeconds(10));

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
