using Mailcast.Receiver.Delivery;

namespace Mailcast.Receiver;

/// <summary>
/// The whole receiver: the audio pipeline, the store it feeds, the slot and tone record, and the
/// delivery into the BBS.
/// </summary>
public sealed class ReceiverHost : IAsyncDisposable
{
    /// <summary>How long to wait before reopening an audio source that failed or was lost.</summary>
    public static readonly TimeSpan AudioRetry = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private ReceiverConfig _config;
    private AudioPipeline? _pipeline;
    private CancellationTokenSource? _audioRestart;

    /// <summary>Creates the receiver; nothing runs until <see cref="RunAsync"/> or <see cref="DecodeOnceAsync"/>.</summary>
    public ReceiverHost(ReceiverConfig config, TimeProvider time, Action<string> log)
    {
        _config = config;
        _time = time;
        _log = log;
        Intake = new Intake(config.StateDirectory, log);
        Ledger = new DeliveryLedger(config.StateDirectory);
        Slots = new SlotTracker(time, log);
        Bbs = new BbsClient(config.Bbs, time, log) { Version = Version };
        Delivery = new DeliveryService(Intake, new SwitchableSession(this), Ledger, time, log);
        Intake.FrameHeard += Slots.OnFrame;
    }

    /// <summary>This program's version, for the SID and the log.</summary>
    public static string Version { get; } =
        typeof(ReceiverHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>The configuration in force.</summary>
    public ReceiverConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>The store of pieces and rebuilt bulletins.</summary>
    public Intake Intake { get; }

    /// <summary>What the BBS has said about each bulletin.</summary>
    public DeliveryLedger Ledger { get; }

    /// <summary>The slots heard, with their tones.</summary>
    public SlotTracker Slots { get; }

    /// <summary>The delivery loop.</summary>
    public DeliveryService Delivery { get; }

    /// <summary>The client for the BBS in the current configuration.</summary>
    public BbsClient Bbs { get; private set; }

    /// <summary>The pipeline running now, if any.</summary>
    public AudioPipeline? Pipeline
    {
        get
        {
            lock (_gate)
            {
                return _pipeline;
            }
        }
    }

    /// <summary>Raised on the audio supervisor whenever a new pipeline has started, so the page can attach to it.</summary>
    public event Action<AudioPipeline>? PipelineStarted;

    /// <summary>Runs the service until <paramref name="cancellation"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        _log($"pdn-mailcast receiver {Version}: listening on {Config.Audio}, delivering to {DescribeBbs(Config.Bbs)}");
        int pending = Intake.Pending().Count;
        if (pending > 0)
        {
            _log($"store: {pending} rebuilt bulletin{(pending == 1 ? "" : "s")} waiting for the BBS from before");
        }

        Task delivery = Delivery.RunAsync(cancellation);
        Task audio = SuperviseAudioAsync(cancellation);
        await Task.WhenAll(delivery, audio).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes one recording and delivers what it completes, then returns: the command line's
    /// --decode. Returns null when everything rebuilt was answered for, or the reason it was not.
    /// </summary>
    public async Task<string?> DecodeOnceAsync(string wavPath, CancellationToken cancellation)
    {
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.Wav, wavPath), _log);
        Attach(pipeline);
        await pipeline.StartAsync(cancellation).ConfigureAwait(false);
        await pipeline.Finished.WaitAsync(cancellation).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        await Intake.DrainAsync(cancellation).ConfigureAwait(false);
        _log($"decode: {Intake.FramesHeard} broadcast frames heard, {Intake.Pending().Count} bulletins to deliver");
        return await Delivery.DeliverPendingAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a new configuration in force: the BBS settings at once, and the audio pipeline is
    /// restarted if its source changed.
    /// </summary>
    public void Reconfigure(ReceiverConfig config)
    {
        CancellationTokenSource? restart = null;
        lock (_gate)
        {
            bool audioChanged = !string.Equals(_config.Audio, config.Audio, StringComparison.Ordinal);
            _config = config;
            Bbs = new BbsClient(config.Bbs, _time, _log) { Version = Version };
            if (audioChanged)
            {
                restart = _audioRestart;
            }
        }
        _log($"config: now listening on {config.Audio}, delivering to {DescribeBbs(config.Bbs)}");
        restart?.Cancel();
        Delivery.Nudge();
    }

    private async Task SuperviseAudioAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            AudioPipeline pipeline;
            lock (_gate)
            {
                _audioRestart = restart;
                pipeline = AudioPipeline.Create(AudioSource.Parse(_config.Audio), _log);
                _pipeline = pipeline;
            }

            string? why;
            try
            {
                Attach(pipeline);
                await pipeline.StartAsync(restart.Token).ConfigureAwait(false);
                PipelineStarted?.Invoke(pipeline);
                await pipeline.Finished.WaitAsync(restart.Token).ConfigureAwait(false);
                why = pipeline.EndReason ?? "the audio stopped";
            }
            catch (AudioSourceException e)
            {
                why = e.Message;
            }
            catch (OperationCanceledException)
            {
                why = null; // a new source, or shutting down
            }
            finally
            {
                lock (_gate)
                {
                    _audioRestart = null;
                    _pipeline = null;
                }
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            if (why is null || cancellation.IsCancellationRequested)
            {
                continue;
            }
            if (pipeline.Source.Kind == AudioSourceKind.Wav)
            {
                _log($"audio: {why}; the receiver keeps running to deliver what it rebuilt");
                await WaitForRestartAsync(cancellation).ConfigureAwait(false);
                continue;
            }
            _log($"audio: {why}. Trying again in {AudioRetry.TotalSeconds:F0} s.");
            try
            {
                await Task.Delay(AudioRetry, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>After a recording has played: wait until the source is changed or the service stops.</summary>
    private async Task WaitForRestartAsync(CancellationToken cancellation)
    {
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (_gate)
        {
            _audioRestart = restart;
        }
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, restart.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        lock (_gate)
        {
            _audioRestart = null;
        }
    }

    private void Attach(AudioPipeline pipeline)
    {
        pipeline.Channel.FrameReceived += (_, frame) => Intake.Offer(frame);
        pipeline.Tone.ToneMeasured += Slots.OnTone;
    }

    internal static string DescribeBbs(BbsSettings bbs) =>
        $"{(bbs.Type == BbsKind.LinBpq ? "LinBPQ" : "FBB")} at {bbs.Host}:{bbs.Port} as {bbs.Login}";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Intake.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Always delivers through the client for the configuration in force.</summary>
    private sealed class SwitchableSession(ReceiverHost host) : IBbsSession
    {
        public Task<SessionReport> DeliverAsync(IReadOnlyList<Mailcast.Core.Bulletin> bulletins, CancellationToken cancellation) =>
            host.Bbs.DeliverAsync(bulletins, cancellation);
    }
}
