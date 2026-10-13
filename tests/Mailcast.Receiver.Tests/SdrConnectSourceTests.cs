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
    public void Problem_ConnectTimedOut_PointsAtTheWindowsFirewall()
    {
        var endpoint = new SdrConnectEndpoint("192.168.1.20", 5454);
        var timedOut = new SdrConnectUnreachableException(SdrConnectFailure.NoAnswer, "no answer from ws://192.168.1.20:5454/ within 15 s");

        string said = AudioPipeline.SdrConnectProblem(endpoint, timedOut);

        Assert.StartsWith("no answer from SDRconnect at 192.168.1.20:5454", said, StringComparison.Ordinal);
        Assert.Contains("Private", said, StringComparison.Ordinal);
        Assert.Contains("TCP port 5454", said, StringComparison.Ordinal);
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
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.TransmitHz };
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
        await using var sdr = new FakeSdrConnect { ToneHz = OnAir.TransmitHz };
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
}
