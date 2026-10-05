using Mailcast.HeadEnd.Slot;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Modems;

namespace Mailcast.HeadEnd.Offline;

/// <summary>What a render produced.</summary>
public sealed record WavSummary(TimeSpan Length, int Frames, int Bursts, int Idents, bool Tone);

/// <summary>
/// Renders a whole slot to a WAV file with pdn-soundmodem's own modem, as the station would send
/// it: the calibration tone, the CW ident as the station's identifier schedules it, and the frames.
/// </summary>
/// <remarks>
/// The published pdn-soundmodem package (0.83.0) cannot pack frames into one burst yet (that is
/// pdn-soundmodem #544), so offline every frame is its own burst, each with its own preamble.
/// That costs about 0.8 s a frame more than the air will, and decodes the same way.
/// </remarks>
public sealed class WavRenderer
{
    private readonly SlotSettings _settings;
    private readonly string _mode;
    private readonly int _sampleRate;

    public WavRenderer(SlotSettings settings, string mode, int sampleRate = 48000)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mode = mode;
        _sampleRate = sampleRate;
    }

    /// <summary>Writes the slot to <paramref name="path"/>, 16-bit mono at the renderer's rate.</summary>
    public WavSummary Render(IReadOnlyList<SlotFrame> frames, string path, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(frames);
        IModem modem = ModemCatalog.Create(_mode, _sampleRate, _ => { });
        var clock = new SampleClock(start, _sampleRate);
        var identifier = new StationIdentifier(
            _settings.Callsign, modeSuffix: null, toneHz: _settings.ToneHz, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(10), sampleRate: _sampleRate, time: clock);

        using var wav = new WavWriter(path, _sampleRate);
        int idents = 0;
        void Write(float[] samples)
        {
            wav.Write(samples);
            clock.Advance(samples.Length);
        }
        void Silence(double seconds) => Write(new float[(int)Math.Round(seconds * _sampleRate)]);
        void IdentIfDue()
        {
            // The station polls its identifier every 5 s between transmissions.
            if (identifier.IdentificationDue)
            {
                Silence(2.5);
                Write(identifier.Render());
                identifier.NoteIdentified();
                idents++;
                Silence(0.5);
            }
        }

        Silence(1);
        bool tone = _settings.ToneLength > TimeSpan.Zero;
        if (tone)
        {
            Write(new TestTone([_settings.ToneHz], 0.5, _sampleRate, _settings.ToneLength.TotalSeconds).Render());
            identifier.NoteTransmission();
            IdentIfDue();
            Silence(Math.Max(0, _settings.PauseAfterTone.TotalSeconds - 3));
        }
        foreach (SlotFrame frame in frames)
        {
            Write(modem.Modulate(Station.Ax25Ui.Encode(_settings.Destination, _settings.Callsign, frame.Payload), 300));
            identifier.NoteTransmission();
            IdentIfDue();
            Silence(_settings.BurstGap.TotalSeconds);
        }
        Silence(2);
        return new WavSummary(TimeSpan.FromSeconds(clock.Samples / (double)_sampleRate), frames.Count, frames.Count, idents, tone);
    }

    /// <summary>A clock that is wherever the render has got to.</summary>
    private sealed class SampleClock(DateTimeOffset start, int rate) : TimeProvider
    {
        public long Samples { get; private set; }

        public void Advance(int samples) => Samples += samples;

        public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromSeconds(Samples / (double)rate);
    }
}

/// <summary>A 16-bit mono PCM WAV writer that fills in its lengths when disposed.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _samples;

    public WavWriter(string path, int sampleRate)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _writer = new BinaryWriter(_stream);
        _writer.Write("RIFF"u8);
        _writer.Write(0);
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1);
        _writer.Write((short)1);
        _writer.Write(sampleRate);
        _writer.Write(sampleRate * 2);
        _writer.Write((short)2);
        _writer.Write((short)16);
        _writer.Write("data"u8);
        _writer.Write(0);
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            _writer.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        }
        _samples += samples.Length;
    }

    public void Dispose()
    {
        long data = _samples * 2;
        _writer.Flush();
        _stream.Seek(4, SeekOrigin.Begin);
        _writer.Write((int)(36 + data));
        _stream.Seek(40, SeekOrigin.Begin);
        _writer.Write((int)data);
        _writer.Flush();
        _writer.Dispose();
    }
}
