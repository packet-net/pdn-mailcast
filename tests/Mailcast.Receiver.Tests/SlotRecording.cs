using Packet.SoundModem.Audio;
using Packet.SoundModem.Ms110d;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A slot as the receiver would hear it, made without a radio: the 10 s tone, then each frame as
/// its own MS110D burst, all in seeded Gaussian noise, with some bursts lost to a "fade".
/// </summary>
internal static class SlotRecording
{
    private const int Rate = OnAir.SampleRate;

    /// <summary>Writes the slot to <paramref name="path"/>.</summary>
    /// <param name="path">Where the WAV goes.</param>
    /// <param name="frames">The AX.25 frames, in sending order.</param>
    /// <param name="faded">Indexes of frames whose bursts are lost: noise only where they would be.</param>
    /// <param name="snrDb">Signal power over noise in 3 kHz, during the bursts.</param>
    /// <param name="toneOffsetHz">How far the whole signal is off frequency.</param>
    /// <param name="seed">The noise's seed.</param>
    public static void Write(string path, IReadOnlyList<byte[]> frames, ISet<int> faded, double snrDb, double toneOffsetHz, int seed)
    {
        var modem = new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = 4 });
        var bursts = frames.Select(f => modem.Modulate(f, 0)).ToList();
        double burstPower = bursts.Average(b => b.Average(s => (double)s * s));

        var audio = new List<float>();
        void Silence(double seconds) => audio.AddRange(new float[(int)(seconds * Rate)]);

        Silence(3);
        double toneAmplitude = Math.Sqrt(2 * burstPower);
        double toneHz = OnAir.CentreAudioHz + toneOffsetHz;
        for (int i = 0; i < 10 * Rate; i++)
        {
            audio.Add((float)(toneAmplitude * Math.Sin(2 * Math.PI * toneHz * i / Rate)));
        }
        Silence(2);
        for (int f = 0; f < bursts.Count; f++)
        {
            if (faded.Contains(f))
            {
                Silence(bursts[f].Length / (double)Rate);
            }
            else
            {
                audio.AddRange(Shift(bursts[f], toneOffsetHz));
            }
            Silence(0.4);
        }
        Silence(3);

        // Noise at the SNR asked for: its power in 3 kHz is the bursts' power less snrDb.
        double sigma = Math.Sqrt(burstPower / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 3000.0));
        var rng = new Random(seed);
        var samples = new float[audio.Count];
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            samples[i] = (float)(audio[i] + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
        }
        WavFile.WriteMono(path, samples, Rate);
    }

    /// <summary>Moves a real signal's spectrum up by <paramref name="hz"/>, as a transmitter off frequency would.</summary>
    private static float[] Shift(float[] burst, double hz)
    {
        if (hz == 0)
        {
            return burst;
        }
        // A 255-tap Hilbert transformer, Hann windowed, then I cos - Q sin.
        const int Taps = 255;
        const int Half = Taps / 2;
        var h = new double[Taps];
        for (int n = 0; n < Taps; n++)
        {
            int k = n - Half;
            double w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / (Taps - 1)));
            h[n] = k % 2 == 0 ? 0 : 2 / (Math.PI * k) * w;
        }
        var output = new float[burst.Length];
        for (int i = 0; i < burst.Length; i++)
        {
            double q = 0;
            for (int n = 0; n < Taps; n++)
            {
                int j = i - n + Half;
                if (j >= 0 && j < burst.Length)
                {
                    q += h[n] * burst[j];
                }
            }
            double phase = 2 * Math.PI * hz * i / Rate;
            output[i] = (float)((burst[i] * Math.Cos(phase)) - (q * Math.Sin(phase)));
        }
        return output;
    }
}
