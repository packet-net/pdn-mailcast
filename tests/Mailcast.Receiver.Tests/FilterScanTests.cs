namespace Mailcast.Receiver.Tests;

/// <summary>
/// <see cref="FilterScan"/>: the maths that turns a radio's receive filter (measured from band
/// noise, or typed by hand) into the USB dial that centres GB7RDG's signal in it.
/// </summary>
public class FilterScanTests
{
    private const int Rate = OnAir.SampleRate;

    /// <summary>A simple FIR convolution (not circular), for building test signals only.</summary>
    private static float[] Convolve(float[] x, float[] h)
    {
        var y = new float[x.Length];
        int half = h.Length / 2;
        for (int i = 0; i < x.Length; i++)
        {
            double s = 0;
            for (int k = 0; k < h.Length; k++)
            {
                int idx = i + half - k;
                if (idx >= 0 && idx < x.Length)
                {
                    s += h[k] * x[idx];
                }
            }
            y[i] = (float)s;
        }
        return y;
    }

    /// <summary>White noise filtered to a band from <paramref name="lowHz"/> to <paramref name="highHz"/>, as a radio's own filter would shape it.</summary>
    private static float[] BandpassNoise(double lowHz, double highHz, int seconds, int seed)
    {
        int n = seconds * Rate;
        var rnd = new Random(seed);
        var white = new float[n];
        for (int i = 0; i < n; i++)
        {
            white[i] = (float)((rnd.NextDouble() * 2) - 1);
        }
        var low = Convolve(white, ChannelMaths.LowPass(highHz, Rate, 401));
        var high = Convolve(white, ChannelMaths.LowPass(lowHz, Rate, 401));
        var band = new float[n];
        for (int i = 0; i < n; i++)
        {
            band[i] = low[i] - high[i];
        }
        return band;
    }

    private static float[] WhiteNoise(int seconds, int seed)
    {
        int n = seconds * Rate;
        var rnd = new Random(seed);
        var noise = new float[n];
        for (int i = 0; i < n; i++)
        {
            noise[i] = (float)((rnd.NextDouble() * 2) - 1);
        }
        return noise;
    }

    [Fact]
    public void Analyse_FindsAWideFilter_AndSaysItIsNotNarrow()
    {
        var audio = BandpassNoise(300, 2700, seconds: 3, seed: 1);

        var (filter, problem) = FilterScan.Analyse(audio);

        Assert.Null(problem);
        Assert.NotNull(filter);
        Assert.InRange(filter!.LowHz, 200, 450);
        Assert.InRange(filter.HighHz, 2550, 2850);
        Assert.False(filter.Narrow);
    }

    [Fact]
    public void Analyse_FindsTheFT450DExampleFilter_AndSaysItIsNarrow()
    {
        // Tom's example: a filter from about 367 to 2190 Hz.
        var audio = BandpassNoise(367, 2190, seconds: 3, seed: 2);

        var (filter, problem) = FilterScan.Analyse(audio);

        Assert.Null(problem);
        Assert.NotNull(filter);
        Assert.InRange(filter!.LowHz, 250, 480);
        Assert.InRange(filter.HighHz, 2070, 2320);
        Assert.True(filter.Narrow);
        // 7.0538 MHz minus the filter's own middle (about 1278 Hz), to the nearest 10 Hz.
        Assert.InRange(filter.DialKHz, 7052.4, 7052.65);
    }

    [Fact]
    public void Analyse_FlatNoise_LooksLikeNoFilter()
    {
        var audio = WhiteNoise(seconds: 3, seed: 3);

        var (filter, problem) = FilterScan.Analyse(audio);

        Assert.Null(filter);
        Assert.Contains("flat", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyse_AToneInThePassband_IsTooUnevenToTrust()
    {
        var audio = BandpassNoise(300, 2700, seconds: 3, seed: 4);
        // A strong steady tone in the middle of the passband: not the clean hiss the method needs.
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] += (float)(20 * Math.Sin(2 * Math.PI * 1500 * i / Rate));
        }

        var (filter, problem) = FilterScan.Analyse(audio);

        Assert.Null(filter);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Analyse_NotEnoughAudio_SaysSo()
    {
        var (filter, problem) = FilterScan.Analyse(new float[1000]);

        Assert.Null(filter);
        Assert.Contains("Not enough audio", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void DialFor_TheFT450DExample_MatchesTomsWorkedAnswer()
    {
        var (filter, problem) = FilterScan.DialFor(367, 2190);

        Assert.Null(problem);
        Assert.NotNull(filter);
        Assert.Equal(7052.52, filter!.DialKHz, 3);
        Assert.Equal(1823, filter.WidthHz, 3);
        Assert.True(filter.Narrow);
    }

    [Fact]
    public void DialFor_TheUsualWideFilter_GivesTheUsualDial()
    {
        // A filter centred on 1800 Hz (the usual dial's own audio centre) and 2.9 kHz wide.
        var (filter, problem) = FilterScan.DialFor(350, 3250);

        Assert.Null(problem);
        Assert.Equal(7052.0, filter!.DialKHz, 3);
        Assert.False(filter.Narrow);
    }

    [Theory]
    [InlineData(100, 2100)] // centre 1100, accepted
    [InlineData(1000, 3000)] // centre 2000, accepted (the top edge)
    public void DialFor_AcceptsTheWholeAudioCentreRange(double lowHz, double highHz)
    {
        var (filter, problem) = FilterScan.DialFor(lowHz, highHz);

        Assert.Null(problem);
        Assert.NotNull(filter);
        double centreHz = OnAir.TransmitHz - (filter!.DialKHz * 1000);
        Assert.InRange(centreHz, ReceiverConfig.LowestAudioCentreHz - 5, ReceiverConfig.HighestAudioCentreHz + 5);
    }

    [Theory]
    [InlineData(50, 1900)] // centre 975, just under the floor
    [InlineData(1050, 3000)] // centre 2025, just over the ceiling
    public void DialFor_RefusesAFilterThatNeedsADialOutsideTheAcceptedRange(double lowHz, double highHz)
    {
        var (filter, problem) = FilterScan.DialFor(lowHz, highHz);

        Assert.Null(filter);
        Assert.NotNull(problem);
    }

    [Fact]
    public void DialFor_ARangeWithNoWidth_IsRefused()
    {
        var (filter, problem) = FilterScan.DialFor(2000, 1000);

        Assert.Null(filter);
        Assert.NotNull(problem);
    }

    [Fact]
    public void DialFor_RoundsToTheNearest10Hz()
    {
        var (filter, _) = FilterScan.DialFor(400, 2203); // middle 1301.5, not a multiple of 10 Hz away from 7053800

        Assert.NotNull(filter);
        double dialHz = filter!.DialKHz * 1000;
        Assert.Equal(0, dialHz % 10, 6);
    }
}
