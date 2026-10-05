namespace Mailcast.Receiver.Tests;

public class ToneDetectorTests
{
    private const int Rate = OnAir.SampleRate;

    /// <summary>
    /// White Gaussian noise whose power in 3 kHz is 1, with a tone of the given SNR (in 3 kHz) at
    /// <paramref name="toneHz"/> between <paramref name="toneFrom"/> and <paramref name="toneTo"/>
    /// seconds.
    /// </summary>
    internal static float[] Signal(double seconds, double toneHz, double snrDb, double toneFrom, double toneTo, int seed, double scale = 0.02)
    {
        var rng = new Random(seed);
        var samples = new float[(int)(seconds * Rate)];
        // Noise of variance s2 over 0 to 24 kHz puts s2 * 3000 / 24000 into 3 kHz.
        double sigma = Math.Sqrt(Rate / 2.0 / 3000.0) * scale;
        double amplitude = Math.Sqrt(2 * Math.Pow(10, snrDb / 10)) * scale;
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double noise = sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            double t = (double)i / Rate;
            double tone = t >= toneFrom && t < toneTo ? amplitude * Math.Sin(2 * Math.PI * toneHz * t) : 0;
            samples[i] = (float)(noise + tone);
        }
        return samples;
    }

    private static List<ToneReport> Run(float[] samples)
    {
        var detector = new ToneDetector();
        var reports = new List<ToneReport>();
        detector.ToneMeasured += reports.Add;
        for (int offset = 0; offset < samples.Length; offset += 4800)
        {
            detector.Process(samples.AsSpan(offset, Math.Min(4800, samples.Length - offset)));
        }
        return reports;
    }

    [Theory]
    [InlineData(1803.2, 10.0, 7)]
    [InlineData(1741.7, 0.0, 7)]
    [InlineData(1800.0, -10.0, 7)]
    [InlineData(1800.0, -10.0, 21)]
    [InlineData(1858.0, 3.0, 22)]
    public void TenSecondTone_MeasuresOffsetAndSnr(double toneHz, double snrDb, int seed)
    {
        var reports = Run(Signal(20, toneHz, snrDb, toneFrom: 3.3, toneTo: 13.3, seed: seed));

        var report = Assert.Single(reports);
        Assert.InRange(report.OffsetHz, toneHz - 1800 - 0.3, toneHz - 1800 + 0.3);
        Assert.InRange(report.SnrDb, snrDb - 2, snrDb + 2);
        Assert.InRange(report.Duration.TotalSeconds, 8, 11.5);
    }

    /// <summary>
    /// A slot's opening as GB7RDG sends it: the 10 s tone, a gap, then the CW ident on the same
    /// 1800 Hz from pdn-soundmodem's own <see cref="Packet.SoundModem.Ident.StationIdentifier"/>,
    /// in noise of the given SNR (relative to the tone, in 3 kHz).
    /// </summary>
    private static float[] ToneThenIdent(double gapSeconds, double snrDb, double identAmplitude, int seed)
    {
        const double toneAmplitude = 0.5;
        var identifier = new Packet.SoundModem.Ident.StationIdentifier(
            "GB7RDG", modeSuffix: null, toneHz: OnAir.CentreAudioHz, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(10), sampleRate: Rate, amplitude: identAmplitude,
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        float[] ident = identifier.Render();
        var audio = new List<float>();
        audio.AddRange(new float[2 * Rate]);
        for (int i = 0; i < OnAir.ToneSeconds * Rate; i++)
        {
            audio.Add((float)(toneAmplitude * Math.Sin(2 * Math.PI * OnAir.CentreAudioHz * i / Rate)));
        }
        audio.AddRange(new float[(int)(gapSeconds * Rate)]);
        audio.AddRange(ident);
        audio.AddRange(new float[4 * Rate]);

        // Tone power is a^2 / 2; noise over 0 to 24 kHz puts 3000 / 24000 of its variance into 3 kHz.
        double sigma = Math.Sqrt(toneAmplitude * toneAmplitude / 2 / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 3000.0));
        var rng = new Random(seed);
        var samples = new float[audio.Count];
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            samples[i] = (float)(audio[i] + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
        }
        return samples;
    }

    [Theory]
    [InlineData(0.1, 15.0, 0.8)]
    [InlineData(0.5, 15.0, 0.8)]
    [InlineData(1.0, 15.0, 0.8)]
    [InlineData(2.0, 15.0, 0.8)]
    [InlineData(5.0, 15.0, 0.8)]
    [InlineData(0.1, 0.0, 0.8)]
    [InlineData(0.5, 0.0, 0.5)]
    [InlineData(1.0, -5.0, 0.8)]
    [InlineData(2.0, 5.0, 0.5)]
    [InlineData(5.0, -5.0, 0.5)]
    public void TenSecondTone_FollowedByTheCwIdent_IsMeasuredWithoutIt(double gapSeconds, double snrDb, double identAmplitude)
    {
        var reports = Run(ToneThenIdent(gapSeconds, snrDb, identAmplitude, seed: 31));

        var report = Assert.Single(reports);
        Assert.InRange(report.Duration.TotalSeconds, 8, 11.5);
        Assert.InRange(report.OffsetHz, -0.3, 0.3);
        Assert.InRange(report.SnrDb, snrDb - 2, snrDb + 2);
    }

    [Fact]
    public void TheCwIdentAlone_IsNotATone()
    {
        var identifier = new Packet.SoundModem.Ident.StationIdentifier(
            "GB7RDG", modeSuffix: null, toneHz: OnAir.CentreAudioHz, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(10), sampleRate: Rate, amplitude: 0.8,
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        float[] ident = identifier.Render();
        var samples = new float[ident.Length * 3 + (4 * Rate)];
        for (int i = 0; i < 3; i++)
        {
            ident.CopyTo(samples, (2 * Rate) + (i * ident.Length));
        }

        Assert.Empty(Run(samples));
    }

    [Fact]
    public void TenSecondTone_ThatFadesSlowly_IsStillOneTone()
    {
        // 40 m fades: 10 dB deep over a few seconds is ordinary, and must not split the tone.
        var samples = Signal(18, 1800, 10, toneFrom: 3.3, toneTo: 13.3, seed: 41);
        var quiet = Signal(18, 1800, 0, toneFrom: 0, toneTo: 0, seed: 41);
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / Rate;
            double gain = Math.Pow(10, -10.0 / 20 * (0.5 - (0.5 * Math.Cos(2 * Math.PI * t / 6))));
            samples[i] = (float)(quiet[i] + ((samples[i] - quiet[i]) * gain));
        }

        var report = Assert.Single(Run(samples));
        Assert.InRange(report.Duration.TotalSeconds, 8, 11.5);
    }

    [Fact]
    public void TenSecondTone_IsInsideTheLengthsAccepted()
    {
        Assert.InRange(TimeSpan.FromSeconds(OnAir.ToneSeconds), ToneDetector.MinDuration + TimeSpan.FromSeconds(2.1), ToneDetector.MaxDuration - TimeSpan.FromSeconds(2.1));
    }

    [Theory]
    [InlineData(1850, 75)]
    [InlineData(1790, 75)]
    [InlineData(1810, 30)]
    [InlineData(1800, 25)]
    public void CarrierNearTheCentre_ThatGoesOnTooLong_IsNotReported(double hz, double seconds)
    {
        Assert.Empty(Run(Signal(seconds + 10, hz, 10, toneFrom: 2, toneTo: 2 + seconds, seed: 8)));
    }

    [Fact]
    public void ToneTooFarOff_IsNotReported()
    {
        Assert.Empty(Run(Signal(20, 1650, 10, toneFrom: 3, toneTo: 13, seed: 11)));
    }

    [Fact]
    public void NoiseOnly_IsNotReported()
    {
        Assert.Empty(Run(Signal(40, 1800, 0, toneFrom: 0, toneTo: 0, seed: 9)));
    }

    [Fact]
    public void ShortBlip_IsNotReported()
    {
        Assert.Empty(Run(Signal(15, 1800, 10, toneFrom: 5, toneTo: 9, seed: 10)));
    }
}
