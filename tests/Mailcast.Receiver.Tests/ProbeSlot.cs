using System.Numerics;
using Packet.SoundModem.Audio;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The start of a slot as GB7RDG sends it since the head end asks for the channel probe: quiet,
/// the 10 s tone, 1.5 s of silence and pdn-soundmodem's own zc255 probe, then quiet again, all
/// through a channel of discrete paths and into white noise. Made without a radio.
/// </summary>
internal static class ProbeSlot
{
    public const int Rate = OnAir.SampleRate;

    /// <summary>A path: its delay, power, a steady Doppler shift and a Gaussian Doppler spread (two sigma, 0 for none).</summary>
    public sealed record Path(double DelayMs, double PowerDb, double ShiftHz = 0, double SpreadHz = 0);

    /// <summary>What was made, with where the tone ended and the probe began (as sent), in seconds.</summary>
    public sealed record Made(float[] Audio, double ToneEndSeconds, double ProbeStartSeconds);

    /// <summary>
    /// Makes the audio. <paramref name="snrDb"/> is the probe's power over the noise in 3 kHz, as
    /// step 1 defined it. Path delays are applied exactly, below a sample, to the probe; the whole
    /// signal is <paramref name="offsetHz"/> off frequency as a mistuned radio would put it.
    /// <paramref name="extra"/> is more audio added as it is, <paramref name="extraAfterToneSeconds"/>
    /// after the tone ends: the first burst, as an older head end sends it 8 s after the tone.
    /// </summary>
    public static Made Make(Path[] paths, double snrDb, int seed, double offsetHz = 0, bool probe = true, double leadSeconds = 3, double tailSeconds = 3, float[]? extra = null, double extraAfterToneSeconds = 8)
    {
        var rng = new Random(seed);
        var descriptor = ProbeSignal.Zc255;
        var envelope = ProbeSignal.Envelope(descriptor, Rate);
        int toneStart = (int)(leadSeconds * Rate), toneLength = 10 * Rate, gap = (int)(1.5 * Rate);
        int probeStart = toneStart + toneLength + gap;
        int length = probeStart + envelope.Length + (int)(tailSeconds * Rate);
        int m = 1;
        while (m < envelope.Length + (Rate / 10))
        {
            m <<= 1;
        }
        var spectrum = new Complex[m];
        if (probe)
        {
            Array.Copy(envelope, spectrum, envelope.Length);
            ChannelMaths.Fft(spectrum, false);
        }
        double total = paths.Sum(p => Math.Pow(10, p.PowerDb / 10));
        var sum = new Complex[length];
        foreach (var p in paths)
        {
            double amp = Math.Sqrt(Math.Pow(10, p.PowerDb / 10) / total);
            var fade = ChannelTests.Fading(length, p.SpreadHz, rng);
            var turn = Complex.FromPolarCoordinates(1, 2 * Math.PI * rng.NextDouble());
            int whole = (int)Math.Round(p.DelayMs * Rate / 1000);
            Complex Gain(int i) => amp * turn * fade(i) * Complex.FromPolarCoordinates(1, 2 * Math.PI * p.ShiftHz * i / Rate);
            // The tone, to the nearest sample: its delay does not matter to anything measured.
            for (int i = toneStart + whole; i < toneStart + toneLength + whole; i++)
            {
                sum[i] += Gain(i);
            }
            if (probe)
            {
                var delayed = new Complex[m];
                double tau = p.DelayMs / 1000;
                for (int k = 0; k < m; k++)
                {
                    double f = (k < m / 2 ? k : k - m) * (double)Rate / m;
                    delayed[k] = spectrum[k] * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * tau);
                }
                ChannelMaths.Fft(delayed, true);
                for (int i = 0; i < m && probeStart + i < length; i++)
                {
                    sum[probeStart + i] += Gain(probeStart + i) * delayed[i];
                }
            }
        }
        var audio = new float[length];
        double power = 0;
        int probeEnd = probeStart + envelope.Length;
        for (int i = 0; i < length; i++)
        {
            double v = (sum[i] * Complex.FromPolarCoordinates(1, 2 * Math.PI * (OnAir.CentreAudioHz + offsetHz) * i / Rate)).Real;
            audio[i] = (float)v;
            if (i >= probeStart && i < probeEnd)
            {
                power += v * v;
            }
        }
        // With no probe, the noise is set as though there were one.
        power = probe ? power / envelope.Length : 0.5 * 0.5;
        if (extra is not null)
        {
            int at = toneStart + toneLength + (int)(extraAfterToneSeconds * Rate);
            for (int i = 0; i < extra.Length && at + i < length; i++)
            {
                audio[at + i] += extra[i];
            }
        }
        double sigma = Math.Sqrt(power / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 3000));
        double scale = 0.1 / Math.Sqrt(power);
        for (int i = 0; i < length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            audio[i] = (float)((audio[i] + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2))) * scale);
        }
        return new Made(audio, (toneStart + toneLength) / (double)Rate, probeStart / (double)Rate);
    }

    /// <summary>
    /// The audio the receiver keeps after the tone, as <see cref="CapturedProbe"/> has it: from 1 s
    /// before where the tone detector put the tone's end to 10 s after, with that end
    /// <paramref name="endErrorSeconds"/> off the truth.
    /// </summary>
    public static (float[] Audio, double ToneEndSeconds) Kept(Made made, double endErrorSeconds = 0)
    {
        double end = made.ToneEndSeconds + endErrorSeconds;
        int from = (int)Math.Round((end - CapturedProbe.BeforeSeconds) * Rate);
        int until = Math.Min(made.Audio.Length, (int)Math.Round((end + CapturedProbe.AfterSeconds) * Rate));
        return (made.Audio[from..until], end - (from / (double)Rate));
    }
}
