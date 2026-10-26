using System.Net;
using System.Net.Sockets;
using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Web;
using Packet.SoundModem.SdrConnect;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// SDRconnect as the audio source, against <see cref="FakeSdrConnect"/>: the setting, what is
/// said when it cannot be reached or will not be tuned, the radio pointed below the dial, a tone
/// on the air coming out as audio where it should, and a dropped connection taken up again.
/// </summary>
public class SdrConnectSourceTests
{
    private static readonly double Dial = ReceiverConfig.DefaultDialKHz * 1000;

    // ---------- the setting ----------

    [Theory]
    [InlineData("sdrconnect:192.168.1.20", "192.168.1.20", 5454)]
    [InlineData(" SDRconnect:192.168.1.20 ", "192.168.1.20", 5454)]
    [InlineData("sdrconnect:shack-pc.lan:5455", "shack-pc.lan", 5455)]
    [InlineData("sdrconnect:[fd00::5]:5454", "fd00::5", 5454)]
    public void Parse_SdrConnect_GivesItsAddress(string setting, string host, int port)
    {
        foreach (bool windows in new[] { false, true })
        {
            var source = AudioSource.Parse(setting, windows);

            Assert.Equal(AudioSourceKind.SdrConnect, source.Kind);
            Assert.Equal(new SdrConnectEndpoint(host, port), source.SdrConnectEndpoint);
            Assert.StartsWith("sdrconnect:", source.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sdrconnect:", "needs the computer")]
    [InlineData("sdrconnect:  ", "needs the computer")]
    [InlineData("sdrconnect:shack pc", "not a computer and a port")]
    [InlineData("sdrconnect:192.168.1.20:0", "not a computer and a port")]
    [InlineData("sdrconnect:192.168.1.20:http", "not a computer and a port")]
    [InlineData("sdrconnect:fd00::5", "not a computer and a port")]
    public void Parse_SdrConnectThatNamesNoComputer_SaysWhat(string setting, string words)
    {
        var e = Assert.Throws<ConfigException>(() => AudioSource.Parse(setting, windows: false));

        Assert.Contains(words, e.Message, StringComparison.Ordinal);
        Assert.Contains("\"audio\"", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"device\"", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SdrConnect_IsARadioButNotASoundCard()
    {
        const string audio = "sdrconnect:192.168.1.20";

        Assert.False(AudioSource.Parse(audio).IsSoundCard);
        Assert.Equal("sdrConnect", StatusPage.AudioKind(audio));
        // Demodulated here at the level it arrives at: not a level the operator sets.
        Assert.Equal("clippingOnly", StatusPage.LevelAdvice(audio));
        // Never the address in the daily report.
        Assert.Equal(ReportAudio.SdrConnect, ReportAudio.For(AudioSource.Parse(audio)));
        Assert.Null(StatusPage.WebSdrView(audio, null));
    }

    [Fact]
    public void SettingsPage_SavesSdrConnectAndTheLocator()
    {
        var saved = StatusPage.Apply(new ReceiverConfig(), new StatusPage.SettingsForm("sdrconnect:192.168.1.20", "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS", Locator: "IO91lk"));

        Assert.Equal("sdrconnect:192.168.1.20", saved.Audio);
        Assert.Equal("IO91lk", saved.Locator);
    }

    [Fact]
    public void EmptyAudio_MentionsSdrConnect()
    {
        Assert.Contains("sdrconnect:", Assert.Throws<ConfigException>(() => AudioSource.Parse("", windows: false)).Message, StringComparison.Ordinal);
        Assert.Contains("sdrconnect:", Assert.Throws<ConfigException>(() => AudioSource.Parse("", windows: true)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_HoldsTheSignalWithRoomEitherSide()
    {
        var (low, high) = AudioPipeline.SdrConnectFilter(Dial);
        double centre = OnAir.AudioCentreHz(Dial);

        Assert.True(low < centre - OnAir.HalfWidthHz && low >= 100);
        Assert.True(high > centre + OnAir.HalfWidthHz);
    }

    [Fact]
    public async Task SettingsPage_ACorrectedAddress_IsTriedAtOnce_NotAfterTheWaitForTheWrongOne()
    {
        // The usual first-time mistake: the wrong address saved, then the right one. The right
        // one is tried as soon as it is saved, as the README says, not after the 30 s wait the
        // wrong one started.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.DefaultSignalHz };
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = $"sdrconnect:127.0.0.1:{closed}",
            StateDirectory = dir.Path,
            Daylight = null,
            Web = new WebSettings { Port = 18137 },
        };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var failed = await PhaseAsync(conditions, AudioPhase.Failed, TimeSpan.FromSeconds(30));
        Assert.Contains("refused the connection", failed.Problem, StringComparison.Ordinal);
        host.Reconfigure(config with { Audio = sdr.Audio });

        // Well inside the 30 s wait.
        await PhaseAsync(conditions, AudioPhase.Listening, TimeSpan.FromSeconds(10));
        Assert.True(sdr.Connections >= 1);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ACorrectedAddress_SavedTheMomentTheFailureIsAnnounced_IsNotLost()
    {
        // The worst case of the one above, and what made it flaky on a busy machine: the address
        // corrected from inside the "failed" announcement itself, before the receiver has gone
        // on to wait. The receiver once announced the failure first and armed the wait's early
        // end after, so a correction in between found nothing to end and waited the full 30 s.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var page = new TcpListener(IPAddress.Loopback, 0);
        page.Start();
        int pagePort = ((IPEndPoint)page.LocalEndpoint).Port;
        page.Stop();
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.DefaultSignalHz };
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = $"sdrconnect:127.0.0.1:{closed}",
            StateDirectory = dir.Path,
            Daylight = null,
            Web = new WebSettings { Port = pagePort },
        };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        int corrected = 0;
        host.AudioChanged += c =>
        {
            if (c.Phase == AudioPhase.Failed && Interlocked.Exchange(ref corrected, 1) == 0)
            {
                host.Reconfigure(config with { Audio = sdr.Audio });
            }
            conditions.Writer.TryWrite(c);
        };
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        await PhaseAsync(conditions, AudioPhase.Failed, TimeSpan.FromSeconds(30));
        await PhaseAsync(conditions, AudioPhase.Listening, TimeSpan.FromSeconds(10));
        Assert.True(sdr.Connections >= 1);

        await stop.CancelAsync();
        await run;
    }

    private static async Task<AudioCondition> PhaseAsync(System.Threading.Channels.Channel<AudioCondition> conditions, AudioPhase phase, TimeSpan within)
    {
        using var giveUp = new CancellationTokenSource(within);
        while (true)
        {
            var condition = await conditions.Reader.ReadAsync(giveUp.Token);
            if (condition.Phase == phase)
            {
                return condition;
            }
        }
    }

    // ---------- what is said when it will not open ----------

    [Fact]
    public async Task Start_NothingListening_SaysSdrConnectIsNotRunningOrItsServerIsOff()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse($"sdrconnect:127.0.0.1:{port}"), Dial, _ => { });

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => pipeline.StartAsync(CancellationToken.None));

        Assert.Contains("refused the connection", e.Message, StringComparison.Ordinal);
        Assert.Contains("Other Actions -> Preferences -> WebSocket Server", e.Message, StringComparison.Ordinal);
        Assert.True(e.Message.All(c => c is >= ' ' and <= '~'), e.Message);
    }

    [Fact]
    public void Problem_ConnectTimedOut_SaysSdrConnectMayNotBeRunning_ThenTheWindowsFirewall()
    {
        var endpoint = new SdrConnectEndpoint("192.168.1.20", 5454);
        var timedOut = new SdrConnectUnreachableException(SdrConnectFailure.NoAnswer, "no answer from ws://192.168.1.20:5454/ within 15 s");

        string said = AudioPipeline.SdrConnectProblem(endpoint, timedOut);

        Assert.StartsWith("no answer from SDRconnect at 192.168.1.20:5454. Check that SDRconnect is running", said, StringComparison.Ordinal);
        Assert.Contains("Private", said, StringComparison.Ordinal);
        // SDRconnect's own rule blocks it on a Public network, and in Windows a block beats any
        // allow: a rule letting the port in does nothing, so it is not suggested.
        Assert.DoesNotContain("TCP port", said, StringComparison.Ordinal);
        Assert.DoesNotContain("port 5454 in", said, StringComparison.Ordinal);
        Assert.True(said.All(c => c is >= ' ' and <= '~'), said);
    }

    [Fact]
    public void ShortfallAdvice_NamesNoSettingAndIsPlainAscii()
    {
        string advice = AudioPipeline.SdrConnectShortfallAdvice;

        Assert.DoesNotContain("\"", advice, StringComparison.Ordinal);
        Assert.EndsWith(".", advice, StringComparison.Ordinal);
        Assert.True(advice.All(c => c is >= ' ' and <= '~'), advice);
    }

    [Fact]
    public void Problem_ConnectTimedOut_FromAnAddressThatAnsweredEarlier_SaysSdrConnectHasGone()
    {
        var endpoint = new SdrConnectEndpoint("192.168.40.61", 5454);
        var timedOut = new SdrConnectUnreachableException(SdrConnectFailure.NoAnswer, "no answer from ws://192.168.40.61:5454/ within 15 s");

        string said = AudioPipeline.SdrConnectProblem(endpoint, timedOut, answeredBefore: true);

        Assert.StartsWith("no answer from SDRconnect at 192.168.40.61:5454, which answered earlier: SDRconnect has most likely been closed", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameFailureOverAndOver_IsSaidAgainOnlyEveryTenMinutes_WithTheAttemptsBetweenCounted()
    {
        // SDRconnect closed on its PC for a quarter of an hour was once the same line every 45 s.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = $"sdrconnect:127.0.0.1:{closed}",
            StateDirectory = dir.Path,
            Daylight = null,
            Web = new WebSettings { Port = 18139 },
        };
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero));
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ReceiverHost(config, clock, log.Enqueue);
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        // 25 attempts, 30 s apart: twelve and a half minutes.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            await PhaseAsync(conditions, AudioPhase.Failed, TimeSpan.FromSeconds(30));
            var next = PhaseAsync(conditions, AudioPhase.Opening, TimeSpan.FromSeconds(30));
            while (!next.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.WhenAny(next, Task.Delay(5));
            }
            await next;
        }

        string[] said = [.. log.Where(l => l.Contains("refused the connection", StringComparison.Ordinal))];
        Assert.Equal(2, said.Length);
        Assert.DoesNotContain("more failed", said[0], StringComparison.Ordinal);
        Assert.Matches(@"\((19|20|21) more failed attempts since the last line\)$", said[1]);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public void Problem_UnknownName_SaysToGiveTheAddress()
    {
        var error = new SdrConnectUnreachableException(SdrConnectFailure.NameNotFound, "cannot reach SDRconnect (name not found)");

        string said = AudioPipeline.SdrConnectProblem(new SdrConnectEndpoint("shack-pc", 5454), error);

        Assert.Contains("cannot find the computer shack-pc", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Problem_AnythingElse_SaysWhatSdrConnectSaid()
    {
        var error = new SdrConnectRefusedException(SdrConnectFailure.NoSetUp, "SDRconnect at 192.168.1.20:5454 accepted the connection but did not finish setting up within 20 s.");

        string said = AudioPipeline.SdrConnectProblem(new SdrConnectEndpoint("192.168.1.20", 5454), error);

        Assert.Equal("cannot use SDRconnect at 192.168.1.20:5454: SDRconnect at 192.168.1.20:5454 accepted the connection but did not finish setting up within 20 s", said);
    }

    [Fact]
    public async Task Start_SdrConnectThatWillNotBeTuned_SaysCanControlIsFalse()
    {
        await using var sdr = new FakeSdrConnect(canControl: false);
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse(sdr.Audio), Dial, _ => { });

        var e = await Assert.ThrowsAsync<AudioSourceException>(() => pipeline.StartAsync(CancellationToken.None));

        Assert.Contains("will not let the receiver tune its radio", e.Message, StringComparison.Ordinal);
        Assert.Contains("can_control is false", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sdr.Sets, s => s.Property == "device_center_frequency");
    }

    // ---------- listening ----------

    /// <summary>The power at <paramref name="hz"/> in <paramref name="samples"/> (Goertzel).</summary>
    private static double Power(ReadOnlySpan<float> samples, double hz)
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

    /// <summary>Collects <paramref name="seconds"/> of the pipeline's audio, after the first <paramref name="skipSeconds"/> (the filters settling).</summary>
    private static async Task<float[]> ListenAsync(AudioPipeline pipeline, double seconds, double skipSeconds = 0.5)
    {
        var heard = new List<float>();
        int skip = (int)(skipSeconds * OnAir.SampleRate), want = (int)(seconds * OnAir.SampleRate);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBlock(ReadOnlySpan<float> block)
        {
            lock (heard)
            {
                foreach (float x in block)
                {
                    if (skip > 0)
                    {
                        skip--;
                    }
                    else if (heard.Count < want)
                    {
                        heard.Add(x);
                    }
                }
                if (heard.Count >= want)
                {
                    done.TrySetResult();
                }
            }
        }
        pipeline.SourceBlock += OnBlock;
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            pipeline.SourceBlock -= OnBlock;
        }
        lock (heard)
        {
            return [.. heard];
        }
    }

    [Theory]
    [InlineData(7052.0)]
    [InlineData(7052.5)]
    public async Task Listening_PointsTheRadioBelowTheDial_AndAToneComesOutAsUsbAudio(double dialKHz)
    {
        double dial = dialKHz * 1000;
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.DefaultSignalHz };
        var log = new List<string>();
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse(sdr.Audio), dial, line => { lock (log) { log.Add(line); } });

        await pipeline.StartAsync(CancellationToken.None);
        float[] audio = await ListenAsync(pipeline, 0.5);

        // The LO a little below the dial, clear of its DC spike: 25 kHz at the fake's 250 kS/s.
        Assert.True(sdr.Sets.Any(s => s.Property == "device_center_frequency" && double.Parse(s.Value, System.Globalization.CultureInfo.InvariantCulture) == dial - 25_000),
            "LO sets: " + string.Join(", ", sdr.Sets.Where(s => s.Property == "device_center_frequency").Select(s => s.Value)));
        // The signal's centre where this dial puts it in USB audio, and not elsewhere.
        double centre = OnAir.AudioCentreHz(dial);
        // 100 times: plainly the tone and nowhere else, with room for a hole a loaded machine makes.
        double at = Power(audio, centre);
        Assert.True(at > 100 * Power(audio, centre - 500), $"{at} vs {Power(audio, centre - 500)}");
        Assert.True(at > 100 * Power(audio, centre + 500), $"{at} vs {Power(audio, centre + 500)}");
        string opened;
        lock (log)
        {
            var openings = log.Where(l => l.StartsWith("audio: SDRconnect", StringComparison.Ordinal)).ToList();
            Assert.True(openings.Count == 1, $"log: {string.Join(" | ", log)}");
            opened = openings[0];
            Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
        }
        Assert.Contains("(RSP1A)", opened, StringComparison.Ordinal);
        Assert.Contains("250000 S/s", opened, StringComparison.Ordinal);
        Assert.Contains("leave its tuning alone", opened, StringComparison.Ordinal);
        Assert.False(pipeline.Finished.IsCompleted);
    }

    [Fact]
    public async Task DroppedConnection_IsTakenUpAgain_WithoutEndingThePipeline()
    {
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.DefaultSignalHz };
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse(sdr.Audio), Dial, _ => { });
        await pipeline.StartAsync(CancellationToken.None);
        await ListenAsync(pipeline, 0.2);

        sdr.DropAll();
        // Connected and tuned again, on a new connection.
        await FakeSdrConnect.WaitForAsync(() => sdr.Connections >= 2 && sdr.Sets.Count(s => s.Property == "device_center_frequency") >= 2, "the receiver connect and tune again");
        // The time it was away comes through as silence first, as long as it was away on a
        // loaded machine; then the tone again.
        double centre = OnAir.AudioCentreHz(Dial);
        bool back = false;
        for (int tries = 0; tries < 40 && !back; tries++)
        {
            float[] audio = await ListenAsync(pipeline, 0.25, skipSeconds: 0);
            back = Power(audio, centre) > 0 && Power(audio, centre) > 100 * Power(audio, centre - 500);
        }

        Assert.True(back, "the tone never came back");
        Assert.False(pipeline.Finished.IsCompleted);
    }

    // ---------- while its own client reconnects ----------

    [Fact]
    public async Task DroppedConnection_StatusSaysReconnectingWithWhy_ThenListeningAgainOnceItIsBack()
    {
        // Eoin closed SDRconnect on his PC and the page still said "listening", the spectrogram
        // simply stopped: pdn-soundmodem's client reconnects by itself for five minutes before
        // the receiver hears anything of it. Now the page says it was lost, and why.
        var page = new TcpListener(IPAddress.Loopback, 0);
        page.Start();
        int pagePort = ((IPEndPoint)page.LocalEndpoint).Port;
        page.Stop();
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.DefaultSignalHz };
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = sdr.Audio,
            StateDirectory = dir.Path,
            Daylight = null,
            Web = new WebSettings { Port = pagePort },
        };
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ReceiverHost(config, TimeProvider.System, log.Enqueue);
        var conditions = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();
        host.AudioChanged += c => conditions.Writer.TryWrite(c);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        await PhaseAsync(conditions, AudioPhase.Listening, TimeSpan.FromSeconds(30));

        sdr.Closed = true;
        sdr.DropAll();
        var lost = await PhaseAsync(conditions, AudioPhase.Reconnecting, TimeSpan.FromSeconds(30));

        string where = $"SDRconnect at 127.0.0.1:{sdr.Port}";
        Assert.StartsWith($"lost {where} (", lost.Words, StringComparison.Ordinal);
        Assert.EndsWith("); reconnecting", lost.Words, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(lost.Problem));
        Assert.Contains($"({lost.Problem})", lost.Words, StringComparison.Ordinal);
        Assert.True(lost.Words.All(c => c is >= ' ' and <= '~'), lost.Words);
        var audio = StatusAudio(host);
        Assert.Equal("reconnecting", audio.GetProperty("phase").GetString());
        Assert.False(audio.GetProperty("live").GetBoolean());
        Assert.StartsWith($"lost {where} (", audio.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(audio.GetProperty("problem").GetString()));

        sdr.Closed = false;
        await PhaseAsync(conditions, AudioPhase.Listening, TimeSpan.FromSeconds(30));

        audio = StatusAudio(host);
        Assert.Equal("listening", audio.GetProperty("phase").GetString());
        Assert.True(audio.GetProperty("live").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, audio.GetProperty("problem").ValueKind);
        Assert.Contains(log, l => l == $"audio: {sdr.Audio} is back; listening again");
        Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));

        await stop.CancelAsync();
        await run;
    }

    private static System.Text.Json.JsonElement StatusAudio(ReceiverHost host) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new StatusPage(host, null, _ => { }).Status(), ReceiverConfig.JsonLine).GetProperty("audio");

    [Theory]
    // As pdn-soundmodem 0.92.0's SdrConnectClient says them.
    [InlineData("sdrconnect: 192.168.40.61:5454: SDRconnect closed the connection; reconnecting", "SDRconnect closed the connection")]
    [InlineData("sdrconnect: 192.168.40.61:5454: the connection failed (The remote party closed the WebSocket connection without completing the close handshake.); reconnecting",
        "the connection failed (The remote party closed the WebSocket connection without completing the close handshake.)")]
    [InlineData("sdrconnect: cannot reach 192.168.40.61:5454 (no answer from ws://192.168.40.61:5454/ within 15 s); still trying", "no answer from ws://192.168.40.61:5454/ within 15 s")]
    [InlineData("sdrconnect: cannot reach 192.168.40.61:5454 (Connection refused (192.168.40.61:5454)); still trying (3 more failed attempt(s) since the last line)", "Connection refused (192.168.40.61:5454)")]
    public async Task ClientLine_ThatSaysItDropped_GivesTheWordsAndWhy(string line, string why)
    {
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse("sdrconnect:192.168.40.61"), Dial, _ => { });

        var drop = pipeline.DropSaid(line);

        Assert.NotNull(drop);
        Assert.Equal(why, drop.Reason);
        Assert.Equal($"lost SDRconnect at 192.168.40.61:5454 ({why}); reconnecting", drop.Words);
    }

    [Theory]
    [InlineData("sdrconnect: 192.168.40.61:5454 is delivering at 250000 samples a second again")]
    [InlineData("sdrconnect: set up: LO 7050000 Hz")]
    [InlineData("sdrconnect: 10.0.0.9:5454: SDRconnect closed the connection; reconnecting")]
    public async Task ClientLine_ThatSaysNoDrop_OrAnotherSdrConnects_IsNotOne(string line)
    {
        await using var pipeline = AudioPipeline.Create(AudioSource.Parse("sdrconnect:192.168.40.61"), Dial, _ => { });

        Assert.Null(pipeline.DropSaid(line));
    }

    [Fact]
    public void Page_SaysReconnectingWithWhy_AndTheLastTryWhileOpeningAgain()
    {
        using var stream = typeof(ReceiverHost).Assembly.GetManifestResourceStream("Mailcast.Receiver.Web.index.html")!;
        string html = new StreamReader(stream).ReadToEnd();

        Assert.Contains("case \"reconnecting\":", html, StringComparison.Ordinal);
        Assert.Contains("${a.state.charAt(0).toUpperCase()}${a.state.slice(1)}... The spectrogram comes back once its audio does.", html, StringComparison.Ordinal);
        Assert.Contains("(a.problem ? ` Last try: ${a.problem}.` : \"\")", html, StringComparison.Ordinal);
        Assert.True(html.All(c => c != '\u2013' && c != '\u2014'));
    }
}
