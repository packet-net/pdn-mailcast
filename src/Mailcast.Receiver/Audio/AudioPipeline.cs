using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;

namespace Mailcast.Receiver;

/// <summary>
/// Audio in, frames out: a source (sound card, web receiver or recording), pdn-soundmodem's
/// channel with one MS110D modem, and the tone detector beside it.
/// </summary>
/// <remarks>
/// <para>MS110D receive is autobaud, so the one modem decodes whichever waveform the head end
/// chose. Everything runs at 48 kHz, the rate pdn-soundmodem's daemon runs MS110D at.</para>
/// <para>Built whole and thrown away whole: changing the audio source on the web page stops this
/// pipeline and starts a new one. The store and the delivery side are outside it and carry on.</para>
/// </remarks>
public sealed class AudioPipeline : IAsyncDisposable
{
    /// <summary>Samples read from the source at a time: 100 ms.</summary>
    private const int BlockSamples = OnAir.SampleRate / 10;

    private readonly AudioSource _source;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private IAudioInput? _input;
    private Thread? _thread;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AudioPipeline(AudioSource source, Action<string> log)
    {
        _source = source;
        _log = log;
        Channel = new SoundModemChannel(OnAir.SampleRate);
        Channel.ReceiveOnlyReason = "pdn-mailcast's receiver never transmits";
        Channel.AddModem(0, sink => new Ms110dModem(OnAir.SampleRate, sink));
        Tone = new ToneDetector(OnAir.SampleRate);
        Channel.AddReceiveTap(Tone.Process);
    }

    /// <summary>The channel; its <see cref="SoundModemChannel.FrameReceived"/> carries every decoded frame.</summary>
    public SoundModemChannel Channel { get; }

    /// <summary>The tone detector on this pipeline's audio.</summary>
    public ToneDetector Tone { get; }

    /// <summary>What this pipeline listens to.</summary>
    public AudioSource Source => _source;

    /// <summary>Raised on the audio thread with each block as the source delivered it, before the modem.</summary>
    public event ReceiveTap? SourceBlock;

    /// <summary>Completes when the audio ends: a recording played through, a source lost, or <see cref="DisposeAsync"/>.</summary>
    public Task Finished => _finished.Task;

    /// <summary>Why the audio ended, if it ended on its own.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Builds a pipeline for <paramref name="source"/>. Call <see cref="Start"/> once anything that listens is attached.</summary>
    public static AudioPipeline Create(AudioSource source, Action<string> log) => new(source, log);

    /// <summary>Opens the source and starts the audio thread.</summary>
    /// <exception cref="AudioSourceException">The source could not be opened; the message says why.</exception>
    public async Task StartAsync(CancellationToken cancellation)
    {
        switch (_source.Kind)
        {
            case AudioSourceKind.Alsa:
                try
                {
                    _input = new AlsaAudioInput(_source.Target, OnAir.SampleRate);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or DllNotFoundException)
                {
                    throw new AudioSourceException($"cannot open the sound card {_source.Target}: {Ascii.Clean(e.Message)}");
                }
                _log($"audio: sound card {_source.Target} at {OnAir.SampleRate} Hz");
                break;

            case AudioSourceKind.UberSdr:
                var endpoint = UberSdrDevice.Parse(_source.Target);
                var tuning = new UberSdrTuning { FrequencyHz = (int)OnAir.DialHz, Sideband = Sideband.Upper, OutputRate = OnAir.SampleRate };
                UberSdrAudioInput web;
                try
                {
                    web = await UberSdrAudioInput.OpenAsync(endpoint, tuning, line => _log(Ascii.Clean(line)), cancellation).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or System.Net.WebSockets.WebSocketException)
                {
                    throw new AudioSourceException($"cannot open the web receiver {endpoint}: {Ascii.Clean(e.Message)}");
                }
                web.Lost += reason =>
                {
                    EndReason = Ascii.Clean(reason);
                    _stop.Cancel();
                };
                _input = web;
                _log($"audio: web receiver {endpoint}, USB dial {OnAir.DialHz / 1e6:F4} MHz"
                    + (web.ReceiverDescription is { } about ? $" ({Ascii.Clean(about)})" : ""));
                break;

            case AudioSourceKind.Wav:
                _input = WavInput.Open(_source.Target);
                _log($"audio: recording {_source.Target}");
                break;
        }

        _thread = new Thread(Pump) { IsBackground = true, Name = "mailcast audio" };
        _thread.Start();
    }

    private void Pump()
    {
        var block = new float[BlockSamples];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int read = _input!.Read(block);
                if (read == 0)
                {
                    if (_input is WavInput)
                    {
                        EndReason = "the recording has been played through";
                        break;
                    }
                    continue;
                }
                var samples = block.AsSpan(0, read);
                SourceBlock?.Invoke(samples);
                if (_input is AlsaAudioInput)
                {
                    Channel.NoteCardClipping(samples);
                }
                Channel.ProcessReceive(samples);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            EndReason = "the audio source failed: " + Ascii.Clean(e.Message);
        }
        finally
        {
            _finished.TrySetResult();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_thread is not null)
        {
            // The read blocks for at most a block's worth of audio on every source.
            await _finished.Task.ConfigureAwait(false);
        }
        (_input as IDisposable)?.Dispose();
        _stop.Dispose();
    }

    /// <summary>A recording as an audio input, converted to 48 kHz, read as fast as it will go.</summary>
    private sealed class WavInput : IAudioInput
    {
        private readonly float[] _samples;
        private int _position;

        private WavInput(float[] samples) => _samples = samples;

        public int SampleRate => OnAir.SampleRate;

        public static WavInput Open(string path)
        {
            float[] samples;
            int rate;
            try
            {
                (samples, rate) = WavFile.ReadMono(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
            {
                throw new AudioSourceException($"cannot read the recording {path}: {Ascii.Clean(e.Message)}");
            }
            return new WavInput(ToPipelineRate(samples, rate, path));
        }

        /// <summary>Converts by whole factors only: 8, 12, 16, 24 and 48 kHz up, 96 and 192 kHz down.</summary>
        internal static float[] ToPipelineRate(float[] samples, int rate, string name)
        {
            if (rate == OnAir.SampleRate)
            {
                return samples;
            }
            if (rate > OnAir.SampleRate && rate % OnAir.SampleRate == 0)
            {
                var decimator = new Decimator(rate, rate / OnAir.SampleRate);
                var output = new float[decimator.MaxOutput(samples.Length)];
                return output[..decimator.Process(samples, output)];
            }
            if (rate < OnAir.SampleRate && OnAir.SampleRate % rate == 0)
            {
                var upsampler = new Upsampler(OnAir.SampleRate, OnAir.SampleRate / rate);
                var output = new float[upsampler.OutputLength(samples.Length)];
                upsampler.Process(samples, output);
                return output;
            }
            throw new AudioSourceException($"the recording {name} is at {rate} Hz; it needs to be a whole factor of 48 kHz (8, 12, 16, 24 or 48 kHz) or a whole multiple of it");
        }

        public int Read(Span<float> destination)
        {
            int take = Math.Min(destination.Length, _samples.Length - _position);
            _samples.AsSpan(_position, take).CopyTo(destination);
            _position += take;
            return take;
        }
    }
}

/// <summary>An audio source that could not be opened, with a sentence saying why.</summary>
public sealed class AudioSourceException(string message) : Exception(message);
