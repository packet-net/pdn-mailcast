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

    /// <summary>A source that should be delivering and has said nothing for this long is reopened.</summary>
    public static readonly TimeSpan StarvedAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="DisposeAsync"/> waits for the audio thread to stop.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    private readonly AudioSource _source;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private IAudioInput? _input;
    private UberSdrAudioInput? _webSdr;
    private Thread? _thread;
    private Task? _watch;
    private long _lastAudio;
    private bool _watchOff;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AudioPipeline(AudioSource source, Action<string> log, TimeProvider time)
    {
        _source = source;
        _log = log;
        _time = time;
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

    /// <summary>
    /// The audio thread was still stuck in a read when the pipeline was disposed, so the device was
    /// left open. Nothing in this process can open it again (it would get EBUSY): the supervisor
    /// ends the process, and systemd starts it afresh.
    /// </summary>
    public bool LeftStuck { get; private set; }

    /// <summary>For tests: raised each time the starvation watch has set its next timer on the clock.</summary>
    internal event Action? WatchWaiting;

    /// <summary>For tests: raised once <see cref="DisposeAsync"/> has set its timer for the audio thread.</summary>
    internal event Action? StopWaiting;

    /// <summary>Why the audio ended, if it ended on its own.</summary>
    public string? EndReason { get; private set; }

    /// <summary>For tests: a pipeline over an input that is already open, watched as a sound card would be.</summary>
    internal static AudioPipeline ForInput(IAudioInput input, Action<string> log, TimeProvider time, AudioSource? source = null, bool watch = true) =>
        new(source ?? new AudioSource(AudioSourceKind.Alsa, "test"), log, time) { _input = input, _watchOff = !watch };

    /// <summary>Builds a pipeline for <paramref name="source"/>. Call <see cref="Start"/> once anything that listens is attached.</summary>
    public static AudioPipeline Create(AudioSource source, Action<string> log, TimeProvider? time = null) => new(source, log, time ?? TimeProvider.System);

    /// <summary>Opens the source and starts the audio thread.</summary>
    /// <exception cref="AudioSourceException">The source could not be opened; the message says why.</exception>
    public async Task StartAsync(CancellationToken cancellation)
    {
        switch (_input is null ? _source.Kind : (AudioSourceKind?)null)
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
                catch (Exception e) when (e.GetType().Name == "UberSdrRefusedException")
                {
                    // pdn-soundmodem's type for an HTTP 429 (rate limited, or the address's daily
                    // allowance spent) is internal, so it is known here by name.
                    throw new AudioSourceException($"the web receiver {endpoint} refused us for now ({Ascii.Clean(e.Message)})", refused: true);
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
                _webSdr = web;
                _log($"audio: web receiver {endpoint}, USB dial {OnAir.DialHz / 1e6:F4} MHz"
                    + (web.ReceiverDescription is { } about ? $" ({Ascii.Clean(about)})" : ""));
                break;

            case AudioSourceKind.Wav:
                _input = WavInput.Open(_source.Target);
                _log($"audio: recording {_source.Target}");
                break;
        }

        Interlocked.Exchange(ref _lastAudio, _time.GetTimestamp());
        _thread = new Thread(Pump) { IsBackground = true, Name = "mailcast audio" };
        _thread.Start();
        if (_source.Kind != AudioSourceKind.Wav && !_watchOff)
        {
            _watch = WatchAsync();
        }
    }

    /// <summary>
    /// Ends the pipeline if a source that should be delivering has said nothing for
    /// <see cref="StarvedAfter"/>, so the supervisor reopens it. A web receiver between sessions
    /// (reconnecting, or refused until its allowance resets) is quiet on purpose and not counted.
    /// A blocked read cannot be interrupted, so this works beside the audio thread, not in it.
    /// </summary>
    private async Task WatchAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(5), _time, _stop.Token);
                WatchWaiting?.Invoke();
                await tick.ConfigureAwait(false);
                if (_webSdr is { SessionLive: false })
                {
                    Interlocked.Exchange(ref _lastAudio, _time.GetTimestamp());
                    continue;
                }
                if (_time.GetElapsedTime(Interlocked.Read(ref _lastAudio)) >= StarvedAfter)
                {
                    EndReason = $"no audio from {_source} for {StarvedAfter.TotalSeconds:F0} s";
                    await _stop.CancelAsync().ConfigureAwait(false);
                    _finished.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
                Interlocked.Exchange(ref _lastAudio, _time.GetTimestamp());
                var samples = block.AsSpan(0, read);
                SourceBlock?.Invoke(samples);
                if (_input is AlsaAudioInput)
                {
                    Channel.NoteCardClipping(samples);
                }
                Channel.ProcessReceive(samples);
            }
        }
        catch (Exception e)
        {
            // Anything at all: the thread must not die silently, and the supervisor reopens.
            EndReason = "the audio failed: " + Ascii.Clean(e.Message);
        }
        finally
        {
            _threadDone.TrySetResult();
            _finished.TrySetResult();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_watch is not null)
        {
            await _watch.ConfigureAwait(false);
        }
        bool stopped = true;
        if (_thread is not null)
        {
            // A read normally returns within a block's worth of audio. One that is stuck in the
            // driver is left behind, and so is its device: closing a device under a read that is
            // still in it is worse than leaking it until the service restarts.
            try
            {
                var wait = _threadDone.Task.WaitAsync(StopWait, _time);
                StopWaiting?.Invoke();
                await wait.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                stopped = false;
                LeftStuck = true;
                _log($"audio: the read from {_source} did not return within {StopWait.TotalSeconds:F0} s; leaving it");
            }
        }
        if (stopped)
        {
            (_input as IDisposable)?.Dispose();
            _stop.Dispose();
        }
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
public sealed class AudioSourceException(string message, bool refused = false) : Exception(message)
{
    /// <summary>A web receiver refused us for now (HTTP 429): wait longer before asking again.</summary>
    public bool Refused { get; } = refused;
}
