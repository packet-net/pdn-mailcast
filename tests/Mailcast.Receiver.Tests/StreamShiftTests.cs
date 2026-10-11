namespace Mailcast.Receiver.Tests;

/// <summary>
/// <see cref="StreamShift"/>, which moves pdn-soundmodem's audio to the usual dial's 1800 Hz when
/// its dial puts the signal elsewhere: the move itself, at the channel's own rates, and its reset.
/// </summary>
public class StreamShiftTests
{
    private static float[] Sine(int rate, double hz, int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        }
        return samples;
    }

    /// <summary>The power at <paramref name="hz"/> (Goertzel), over the second half of <paramref name="x"/>.</summary>
    private static double Power(float[] x, int rate, double hz)
    {
        int from = x.Length / 2;
        double coefficient = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double s1 = 0, s2 = 0;
        for (int i = from; i < x.Length; i++)
        {
            double s0 = x[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }
        int n = x.Length - from;
        return ((s1 * s1) + (s2 * s2) - (coefficient * s1 * s2)) / ((double)n * n);
    }

    [Theory]
    [InlineData(48000, 639)]
    [InlineData(24000, 319)]
    [InlineData(12000, 159)]
    public void TheFilters_AreAsLongInTimeAsTheBuiltInsAt48kHz(int rate, int taps)
    {
        Assert.Equal(taps, StreamShift.TapsFor(rate));
    }

    [Theory]
    [InlineData(48000, 3800)]
    [InlineData(12000, 3800)]
    [InlineData(48000, 8800)]
    [InlineData(12000, 1000)]
    public void AToneAtTheSignalsCentre_ComesOutAt1800Hz_WithNoImage(int rate, double centreHz)
    {
        var shift = new StreamShift(rate, centreHz, OnAir.CentreAudioHz, OnAir.HalfWidthHz);
        var input = Sine(rate, centreHz + 300, rate);
        var output = new float[input.Length];
        // In 100 ms blocks, as the stream comes.
        for (int at = 0; at < input.Length; at += rate / 10)
        {
            shift.Process(input.AsSpan(at, rate / 10), output.AsSpan(at, rate / 10));
        }

        double wanted = Power(output, rate, 2100);
        double image = Power(output, rate, 2 * centreHz + 300 - 1800 is var mirror && mirror < rate / 2 ? mirror : 1);
        // The tone kept its level (a sine of 0.5 has power 0.0625 by this measure).
        Assert.InRange(wanted, 0.0625 * 0.9, 0.0625 * 1.1);
        // The mirror image a plain mix would give is 40 dB down or more.
        Assert.True(image < wanted * 1e-4, $"image {10 * Math.Log10(image / wanted):F1} dB");
    }

    [Fact]
    public void AfterAReset_ItIsAsIfNewlyMade()
    {
        var used = new StreamShift(48000, 3800, OnAir.CentreAudioHz, OnAir.HalfWidthHz);
        var fresh = new StreamShift(48000, 3800, OnAir.CentreAudioHz, OnAir.HalfWidthHz);
        var before = Sine(48000, 3900, 4800);
        var scratch = new float[before.Length];
        used.Process(before, scratch);

        used.Reset();
        var after = Sine(48000, 3700, 4800);
        var fromUsed = new float[after.Length];
        var fromFresh = new float[after.Length];
        used.Process(after, fromUsed);
        fresh.Process(after, fromFresh);

        Assert.Equal(fromFresh, fromUsed);
    }
}
