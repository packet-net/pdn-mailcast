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

    /// <summary>
    /// The longest the modem may stay locked on one burst before it is made to listen afresh:
    /// longer than any burst GB7RDG sends (its <c>maxBurstSeconds</c> is at most 120).
    /// </summary>
    /// <remarks>
    /// pdn-soundmodem's MS110D receiver (0.83.0) can lock on a burst too weak to decode, a
    /// preamble heard through a closed band, and then never let go: noise keeps its probes about
    /// as strong as the weak signal was, so it never decides the signal has gone, and with no
    /// EOM it demodulates noise for ever. That costs about nine times the idle CPU (a whole core
    /// on a Pi) and leaves the receiver deaf to every later slot until it is restarted. Counted
    /// in samples, so it does not depend on how fast the audio arrives.
    /// </remarks>
    public static readonly TimeSpan LongestBurst = TimeSpan.FromSeconds(150);

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
    private long _lockLimitSamples = (long)(LongestBurst.TotalSeconds * OnAir.SampleRate);
    private long _lockedSamples;
    private int _locksReleased;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Ms110dDemodulator? _receiver;

    private AudioPipeline(AudioSource source, double dialHz, Action<string> log, TimeProvider time)
    {
        _source = source;
        DialHz = dialHz;
        _log = log;
        _time = time;
        Channel = new SoundModemChannel(OnAir.SampleRate);
        Channel.ReceiveOnlyReason = "pdn-mailcast's receiver never transmits";
        Ms110dModem? modem = null;
        Channel.AddModem(0, sink => modem = new Ms110dModem(OnAir.SampleRate, sink));
        _receiver = BurstWatch.ReceiverOf(modem!);
        if (_receiver is null)
        {
            _log("audio: this pdn-soundmodem does not say which MS110D waveform it locked to, so the page cannot show the speed");
        }
        Burst = new BurstWatch(time);
        Capture = new BurstCapture(time, burst => BurstCaptured?.Invoke(burst));
        if (_receiver is not null)
        {
            Capture.Attach(_receiver);
        }
        Channel.FrameReceived += (_, _) => Burst.OnFrame(LockedWaveform);
        Channel.FrameReceived += (_, _) => Capture.OnFrame();
        Tone = new ToneDetector(OnAir.SampleRate, OnAir.AudioCentreHz(dialHz));
        Channel.AddReceiveTap(Tone.Process);
        // The channel probe follows the tone: its audio is kept from the same ring once it is all in.
        Tone.ToneMeasured += tone =>
        {
            long from = tone.EndSample - (long)(CapturedProbe.BeforeSeconds * OnAir.SampleRate);
            long until = tone.EndSample + (long)(CapturedProbe.AfterSeconds * OnAir.SampleRate);
            Capture.Keep(from, until, (audio, first) => ProbeCaptured?.Invoke(
                new CapturedProbe(audio, tone, (tone.EndSample - first) / (double)OnAir.SampleRate, _time.GetUtcNow(), first)));
        };
    }

    /// <summary>The bursts heard, by the waveform the modem's autobaud locked to: for the speed tile.</summary>
    public BurstWatch Burst { get; }

    /// <summary>Keeps each decoded burst's audio for the channel measurement.</summary>
    internal BurstCapture Capture { get; }

    /// <summary>Raised on the audio thread with each decoded burst and its audio, for the channel measurement.</summary>
    public event Action<CapturedBurst>? BurstCaptured;

    /// <summary>Raised on the audio thread with the audio after each tone, where the channel probe should be.</summary>
    public event Action<CapturedProbe>? ProbeCaptured;

    /// <summary>
    /// The waveform number the MS110D receiver is locked to now, or null between bursts (or if it
    /// cannot say). Read on the audio thread while a frame is being delivered, it is the waveform
    /// that frame came on: the lock is only dropped after the burst's last frame.
    /// </summary>
    internal int? LockedWaveform => _receiver?.Lock is { WaveformNumber: >= 0 and int wn } ? wn : null;

    /// <summary>The waveform the frame being delivered now came on, by name; see <see cref="LockedWaveform"/>.</summary>
    public string? FrameWaveform => LockedWaveform is { } wn ? Waveform.Name(wn) : null;

    /// <summary>The channel; its <see cref="SoundModemChannel.FrameReceived"/> carries every decoded frame.</summary>
    public SoundModemChannel Channel { get; }

    /// <summary>The tone detector on this pipeline's audio.</summary>
    public ToneDetector Tone { get; }

    /// <summary>What this pipeline listens to.</summary>
    public AudioSource Source => _source;

    /// <summary>The USB dial this pipeline's audio is heard on, in Hz: a web SDR is tuned to it.</summary>
    public double DialHz { get; }

    /// <summary>How a web SDR is asked to tune: USB on <see cref="DialHz"/>, at the modem's rate.</summary>
    internal UberSdrTuning WebSdrTuning => new() { FrequencyHz = (int)Math.Round(DialHz), Sideband = Sideband.Upper, OutputRate = OnAir.SampleRate };

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

    /// <summary>
    /// What the web SDR says about itself once open, as its <c>/api/description</c> gives it:
    /// callsign, name and location. Null for other sources, or a web SDR that would not say.
    /// </summary>
    public string? WebSdrDescription { get; private set; }

    /// <summary>Why the audio ended, if it ended on its own.</summary>
    public string? EndReason { get; private set; }

    /// <summary>How many times the modem has been made to let go of a lock that outlasted <see cref="LongestBurst"/>.</summary>
    public int LocksReleased => Volatile.Read(ref _locksReleased);

    /// <summary>For tests: a pipeline over an input that is already open, watched as a sound card would be.</summary>
    internal static AudioPipeline ForInput(IAudioInput input, Action<string> log, TimeProvider time, AudioSource? source = null, bool watch = true, TimeSpan? longestBurst = null, string? webSdrDescription = null) =>
        new(source ?? new AudioSource(AudioSourceKind.Alsa, "test"), ReceiverConfig.DefaultDialKHz * 1000, log, time)
        {
            _input = input,
            WebSdrDescription = webSdrDescription,
            _watchOff = !watch,
            _lockLimitSamples = (long)((longestBurst ?? LongestBurst).TotalSeconds * OnAir.SampleRate),
        };

    /// <summary>
    /// Builds a pipeline for <paramref name="source"/>, heard on the USB dial <paramref name="dialHz"/>.
    /// Call <see cref="StartAsync"/> once anything that listens is attached.
    /// </summary>
    public static AudioPipeline Create(AudioSource source, double dialHz, Action<string> log, TimeProvider? time = null) => new(source, dialHz, log, time ?? TimeProvider.System);

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
                _log($"audio: sound card {_source.Target} at {OnAir.SampleRate} Hz, the radio on USB dial {OnAir.Mhz(DialHz)} MHz");
                break;

            case AudioSourceKind.UberSdr:
                var endpoint = UberSdrDevice.Parse(_source.Target);
                var tuning = WebSdrTuning;
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
                WebSdrDescription = web.ReceiverDescription is { Length: > 0 } about ? Ascii.Clean(about) : null;
                _log($"audio: web receiver {endpoint}, USB dial {OnAir.Mhz(DialHz)} MHz, signal centre {OnAir.Mhz(OnAir.TransmitHz)} MHz"
                    + (WebSdrDescription is { } said ? $" ({said})" : ""));
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
                        // A probe right at the end of a recording is measured on what there is of it.
                        Capture.Flush();
                        break;
                    }
                    continue;
                }
                Interlocked.Exchange(ref _lastAudio, _time.GetTimestamp());
                Feed(block.AsSpan(0, read), soundCard: _input is AlsaAudioInput);
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

    /// <summary>One block through the modem and everything beside it, on the audio thread (or a test's).</summary>
    internal void Feed(ReadOnlySpan<float> samples, bool soundCard = false)
    {
        SourceBlock?.Invoke(samples);
        if (soundCard)
        {
            Channel.NoteCardClipping(samples);
        }
        Capture.Write(samples);
        Channel.ProcessReceive(samples);
        ReleaseStuckLock(samples.Length);
        Burst.AfterBlock(LockedWaveform);
        Capture.AfterBlock();
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Asks a web SDR where it is, for naming the hops; null if it will not say.</summary>
    internal static async Task<GroundPlace?> FetchWebSdrPlaceAsync(UberSdrEndpoint endpoint, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.HttpBase}/api/description");
            request.Headers.TryAddWithoutValidation("User-Agent", $"pdn-mailcast-receiver/{ReceiverHost.Version}");
            using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return PlaceInDescription(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The place in an UberSDR's <c>/api/description</c>: its <c>receiver.gps</c> latitude and
    /// longitude, or failing those its locator. Null if it gives neither, or will not parse.
    /// </summary>
    internal static GroundPlace? PlaceInDescription(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || System.Text.Json.Nodes.JsonNode.Parse(json)?["receiver"]?["gps"] is not System.Text.Json.Nodes.JsonObject gps)
            {
                return null;
            }
            string? locator = gps["maidenhead"]?.GetValue<string>() is { Length: > 0 } m ? Ascii.Clean(m) : null;
            double? lat = gps["lat"]?.GetValue<double>(), lon = gps["lon"]?.GetValue<double>();
            if (lat is { } la && lon is { } lo && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && (la != 0 || lo != 0))
            {
                return new GroundPlace(la, lo, locator ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{la:F2},{lo:F2}"));
            }
            return GroundPlace.FromLocator(locator);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes the modem listen afresh once it has been locked on one burst for longer than
    /// <see cref="LongestBurst"/> (see there for why it would not by itself). On the audio
    /// thread, between blocks, which is where resetting the receiver is safe.
    /// </summary>
    private void ReleaseStuckLock(int samples)
    {
        if (!Channel.CarrierDetect)
        {
            _lockedSamples = 0;
            return;
        }
        _lockedSamples += samples;
        if (_lockedSamples < _lockLimitSamples)
        {
            return;
        }
        foreach (var modem in Channel.Modems.Values)
        {
            modem.ResetCarrierState();
        }
        Interlocked.Increment(ref _locksReleased);
        _log($"audio: the modem had been locked on one burst for {_lockedSamples / OnAir.SampleRate} s, longer than any GB7RDG sends, "
            + "so it was a signal too weak to read; listening afresh");
        _lockedSamples = 0;
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

    /// <summary>Audio at <paramref name="rate"/> converted to the pipeline's 48 kHz, as a recording is: for tests.</summary>
    internal static float[] ToPipelineRate(float[] samples, int rate) => WavInput.ToPipelineRate(samples, rate, "audio");

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
