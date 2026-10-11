using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;

namespace Mailcast.Receiver;

/// <summary>
/// Audio in, frames out: a source (sound card, web receiver, pdn-soundmodem's channel audio
/// stream or recording), pdn-soundmodem's channel with one MS110D modem, and the tone detector
/// beside it.
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
    private SoundModemAudioInput? _stream;
    private bool _keyed;
    private int _modemResets;
    private int _gapsUnsaid;
    private long _gapSaid = long.MinValue;
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

    /// <summary>
    /// Shifts this dial's audio to the modem's native 1800 Hz before it reaches the channel, so
    /// the modem itself never has to know the dial moved: null at the usual dial (no shift
    /// needed). pdn-soundmodem's own band-plan placement (<c>ModemCatalog</c>) cannot do this
    /// below about 1740 Hz, inside the 1000 to 2000 Hz this receiver otherwise accepts, because
    /// it guards MS110D's occupied band 350 Hz clear of DC; shifting the audio ourselves, this
    /// far upstream, means the modem is never asked to run that close to DC at all.
    /// </summary>
    private FrequencyShifter? _shift;

    /// <summary>Where <see cref="_shift"/> writes, kept from block to block so a shifted dial allocates nothing per block.</summary>
    private float[] _shifted = [];

    /// <summary>
    /// pdn-soundmodem's audio comes moved as well as as it is, because its dial puts the signal's
    /// centre outside the 1000 to 2000 Hz <see cref="_shift"/> is for: the input moves it to the
    /// usual dial's 1800 Hz at the channel's own rate (see <see cref="StreamShift"/>), and
    /// everything here but the level meter and "Measure my filter" reads it there.
    /// </summary>
    private bool _moved;

    /// <summary>Where a moved block is read into, beside the block as it came.</summary>
    private float[]? _movedBlock;

    /// <summary>
    /// Whether <see cref="DialHz"/> is settled: at once for any source but pdn-soundmodem, whose
    /// own dial is only known once its stream has said hello.
    /// </summary>
    public bool DialSettled { get; private set; }

    /// <summary>The config's dial, which pdn-soundmodem's receive window tunes its radio to for each slot.</summary>
    private readonly double _configDialHz;

    /// <summary>The dial pdn-soundmodem's hello said its radio was on, or null.</summary>
    private double? _saidDialHz;

    /// <summary>pdn-soundmodem's channel rate, once its stream is open.</summary>
    private int _channelRate = OnAir.SampleRate;

    /// <summary>Where the input moves the signal from, as last asked of it (null: not moved); a block made otherwise is from before a retune.</summary>
    private double? _movedFromHz;

    /// <summary>A dial <see cref="FollowDial"/> moved to, for the audio thread to place the signal by before its next block.</summary>
    private double? _pendingDialHz;

    private readonly object _dialGate = new();

    private AudioPipeline(AudioSource source, double dialHz, Action<string> log, TimeProvider time)
    {
        _source = source;
        DialHz = dialHz;
        _configDialHz = dialHz;
        DialSettled = source.Kind != AudioSourceKind.SoundModem;
        _log = log;
        _time = time;
        _shift = ShiftFor(dialHz);
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
        // Not Channel.AddReceiveTap: the tone detector reads this dial's own audio (above, at
        // OnAir.AudioCentreHz(dialHz)), while the channel's modem reads it shifted to the
        // modem's native 1800 Hz (see _shift and Feed below); one receive tap cannot see two
        // different versions of the same audio.
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

    /// <summary>
    /// The USB dial this pipeline's audio is heard on, in Hz: a web SDR is tuned to it. For
    /// pdn-soundmodem, the dial it says its radio is on when it knows, else the config's; settled
    /// by <see cref="OpenAsync"/>, before anything reads it.
    /// </summary>
    public double DialHz { get; private set; }

    /// <summary>Whether <see cref="DialHz"/> is the dial pdn-soundmodem said its radio is on, rather than the config's.</summary>
    public bool DialFromSoundModem { get; private set; }

    /// <summary>
    /// Where the signal's centre lies in the audio the tone detector, the channel measurement and
    /// the page's spectrogram read, Hz: <see cref="OnAir.AudioCentreHz"/> of <see cref="DialHz"/>,
    /// except when pdn-soundmodem's dial puts it outside 1000 to 2000 Hz, when its audio is moved
    /// to the usual dial's 1800 Hz before any of them reads it (see <see cref="StreamShift"/>).
    /// </summary>
    public double AudioCentreHz => _moved ? OnAir.CentreAudioHz : OnAir.AudioCentreHz(DialHz);

    /// <summary>
    /// The dial the page's spectrogram labels its audio with: <see cref="DialHz"/>, or the usual
    /// dial when the audio has been moved to where the usual dial would put it.
    /// </summary>
    internal double SpectrumDialHz => _moved ? OnAir.TransmitHz - OnAir.CentreAudioHz : DialHz;

    /// <summary>
    /// Where a pipeline heard on <paramref name="dialHz"/> puts the signal's centre in the audio
    /// it reads, Hz, as <see cref="AudioCentreHz"/> would once it is open: for when there is no
    /// pipeline.
    /// </summary>
    internal static double AudioCentreFor(double dialHz) => OnAir.AudioCentreHz(dialHz) is var centre && UsesOwnCentre(centre) ? centre : OnAir.CentreAudioHz;

    /// <summary>Whether a signal centred on <paramref name="centreHz"/> is read where it is (shifted for the modem alone) rather than moved first.</summary>
    internal static bool UsesOwnCentre(double centreHz) => centreHz >= ReceiverConfig.LowestAudioCentreHz && centreHz <= ReceiverConfig.HighestAudioCentreHz;

    /// <summary>This receiver's own page port, which pdn-soundmodem's page links to.</summary>
    internal int PagePort { get; init; } = new WebSettings().Port;

    /// <summary>
    /// Whether pdn-soundmodem's receive window retunes its radio to the config's dial for each slot
    /// (<c>"retune": true</c>): a dial pdn-soundmodem says that puts the signal out of reach is then
    /// not refused, and the signal is placed by the config's dial until the window moves the radio.
    /// </summary>
    internal bool RetunedByWindow { get; init; }

    /// <summary>Whether pdn-soundmodem's channel carries the signal heard on <paramref name="dialHz"/>, as <see cref="SoundModemReach"/> judges it.</summary>
    private bool Reachable(double dialHz) => SoundModemReach(_channelRate, dialHz, dialHz) is null;

    /// <summary>
    /// With <see cref="RetunedByWindow"/>: pdn-soundmodem's radio has moved, as its receive window
    /// was taken (to the window's dial) or given back or lost (to <paramref name="rigDialHz"/>,
    /// the rig's dial if pdn-soundmodem said it). The signal is placed by that dial when its
    /// channel carries the signal there; by the hello's dial when the rig's is not known; and
    /// otherwise by the config's, the dial the window tunes to, since nothing can be heard until
    /// it does. The audio thread moves to it before its next block: the modem's shift or the
    /// input's move to 1800 Hz (its filters started afresh), and the modem listens afresh. Ignored
    /// until the stream's own dial is settled (<see cref="DialSettled"/>), which would overwrite
    /// it; the retuner places a pipeline that opens while it holds the window once it has. True if
    /// the dial changed.
    /// </summary>
    internal bool FollowDial(double? rigDialHz)
    {
        if (!RetunedByWindow || !DialSettled || _stream is null)
        {
            return false;
        }
        double dial = rigDialHz is { } rig && Reachable(rig) ? rig
            : rigDialHz is null && _saidDialHz is { } said && Reachable(said) ? said
            : _configDialHz;
        lock (_dialGate)
        {
            if (Math.Abs(dial - DialHz) < 0.5)
            {
                return false;
            }
            DialHz = dial;
            DialFromSoundModem = Math.Abs(dial - _configDialHz) >= 0.5;
            _pendingDialHz = dial;
        }
        double centre = OnAir.AudioCentreHz(dial);
        string moved = UsesOwnCentre(centre) ? "" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $", moved to {OnAir.CentreAudioHz:F0} Hz here");
        _log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"audio: pdn-soundmodem's radio is on USB dial {OnAir.Mhz(dial)} MHz now, the signal's centre at {centre:F0} Hz in its audio{moved}"));
        return true;
    }

    /// <summary>On the audio thread, between blocks: places the signal by a dial <see cref="FollowDial"/> moved to.</summary>
    private void PlaceByPendingDial()
    {
        double dial;
        lock (_dialGate)
        {
            if (_pendingDialHz is not { } pending)
            {
                return;
            }
            dial = pending;
            _pendingDialHz = null;
        }
        double centre = OnAir.AudioCentreHz(dial);
        double? from = UsesOwnCentre(centre) ? null : centre;
        if (from is null)
        {
            _moved = false;
            _shift = ShiftFor(dial);
        }
        else
        {
            _moved = true;
            _shift = null;
            _movedBlock ??= new float[BlockSamples];
        }
        if (from != _movedFromHz)
        {
            _movedFromHz = from;
            _stream?.MoveFrom(from);
        }
        Tone.ExpectedHz = AudioCentreHz;
        // The audio either side of a retune is not one signal.
        ResetModem();
    }

    /// <summary>How many times the modem has been made to listen afresh after a gap or a keyed span in pdn-soundmodem's audio.</summary>
    internal int ModemResets => Volatile.Read(ref _modemResets);

    private static FrequencyShifter? ShiftFor(double dialHz)
    {
        double shiftHz = OnAir.CentreAudioHz - OnAir.AudioCentreHz(dialHz);
        return Math.Abs(shiftHz) < 0.5 ? null : new FrequencyShifter(OnAir.SampleRate, shiftHz);
    }

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
    internal static AudioPipeline ForInput(IAudioInput input, Action<string> log, TimeProvider time, AudioSource? source = null, bool watch = true, TimeSpan? longestBurst = null, string? webSdrDescription = null, double? dialHz = null) =>
        new(source ?? new AudioSource(AudioSourceKind.Alsa, "test"), dialHz ?? ReceiverConfig.DefaultDialKHz * 1000, log, time)
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
    public static AudioPipeline Create(AudioSource source, double dialHz, Action<string> log, TimeProvider? time = null, int? pagePort = null, bool retunedByWindow = false) =>
        new(source, dialHz, log, time ?? TimeProvider.System) { PagePort = pagePort ?? new WebSettings().Port, RetunedByWindow = retunedByWindow };

    /// <summary>
    /// Opens a source whose dial is only known once it is open, ahead of <see cref="StartAsync"/>,
    /// so that anything attached to the pipeline sees the dial it really listens on: pdn-soundmodem
    /// says its radio's dial in its hello. Does nothing for any other source.
    /// </summary>
    /// <exception cref="AudioSourceException">The source could not be opened; the message says why.</exception>
    public async Task OpenAsync(CancellationToken cancellation)
    {
        if (_input is null && _source.Kind == AudioSourceKind.SoundModem)
        {
            await OpenSoundModemAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects to pdn-soundmodem's channel audio stream, settles the dial (its own when it says
    /// one, else the config's), refuses one that puts the signal out of the channel's reach, and
    /// asks for the band the signal needs.
    /// </summary>
    private async Task OpenSoundModemAsync(CancellationToken cancellation)
    {
        double configured = DialHz;
        // A dial that puts the signal outside 1000 to 2000 Hz has the input move its audio at the
        // channel's own rate, before a slow channel is brought up to 48 kHz.
        var stream = await SoundModemAudioInput.OpenAsync(_source.SoundModemPort, cancellation, (rate, said) =>
            OnAir.AudioCentreHz(said ?? configured) is var at && SoundModemReach(rate, said, said ?? configured) is null && !UsesOwnCentre(at) ? at : null)
            .ConfigureAwait(false);
        double dial = stream.DialHz ?? configured;
        _channelRate = stream.ChannelRate;
        _saidDialHz = stream.DialHz;
        _movedFromHz = stream.MovedFromHz;
        // With the receive window, a radio out of reach now is tuned to the config's dial for
        // each slot: the signal is placed by that (the input moves nothing, since the dial it was
        // asked about is out of reach), and only a channel that cannot carry it there is refused.
        double? awayHz = RetunedByWindow && stream.DialHz is { } saidDial && !Reachable(saidDial) && Reachable(configured) ? saidDial : null;
        if (awayHz is not null)
        {
            dial = configured;
        }
        double centre = OnAir.AudioCentreHz(dial);
        try
        {
            if (SoundModemReach(stream.ChannelRate, awayHz is null ? stream.DialHz : null, dial) is { } refusal)
            {
                throw new AudioSourceException(refusal, refused: true);
            }
            if (dial != configured)
            {
                DialHz = dial;
                _shift = ShiftFor(dial);
            }
            if (stream.MovedFromHz is not null)
            {
                // As pdn-soundmodem's built-in receiver did (MailcastReceiveShift): the whole of
                // the audio is moved to the usual dial's 1800 Hz, the modem's own centre, so the
                // modem, the tone detector and the channel measurement all read it there.
                _moved = true;
                _movedBlock = new float[BlockSamples];
                _shift = null;
            }
            Tone.ExpectedHz = AudioCentreHz;
            DialFromSoundModem = stream.DialHz is not null && awayHz is null;
            await stream.IdentifyAsync(PagePort, centre - OnAir.HalfWidthHz, centre + OnAir.HalfWidthHz, cancellation).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        stream.Lost += reason =>
        {
            EndReason = Ascii.Clean(reason);
            _stop.Cancel();
        };
        _input = stream;
        _stream = stream;
        DialSettled = true;
        if (stream.LostReason is { } already)
        {
            EndReason = Ascii.Clean(already);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string rate = stream.ChannelRate == OnAir.SampleRate
            ? $"its channel at {OnAir.SampleRate} Hz"
            : $"its channel at {stream.ChannelRate} Hz, brought up to {OnAir.SampleRate} Hz";
        string where = awayHz is { } away
            ? $"the radio on USB dial {OnAir.Mhz(away)} MHz as pdn-soundmodem says, which puts the signal out of reach until its receive window tunes it to {OnAir.Mhz(configured)} MHz (\"dialKHz\") for each slot"
            : stream.DialHz is null
            ? $"the radio on USB dial {OnAir.Mhz(dial)} MHz (\"dialKHz\"; pdn-soundmodem does not say where its radio is)"
            : $"the radio on USB dial {OnAir.Mhz(dial)} MHz, as pdn-soundmodem says"
                + (Math.Abs(dial - configured) >= 1 ? $" (\"dialKHz\", {OnAir.Mhz(configured)} MHz, is not used while it does)" : "");
        string moved = !_moved ? "" : string.Create(invariant,
            $"; that is outside the {ReceiverConfig.LowestAudioCentreHz:F0} to {ReceiverConfig.HighestAudioCentreHz:F0} Hz a radio's own filter usually passes whole, "
            + $"which is fine for a Flex (pdn-soundmodem opens its filter for it) or an SDR, but a radio on a sound card needs its filter open from {centre - OnAir.HalfWidthHz:F0} to {centre + OnAir.HalfWidthHz:F0} Hz. "
            + $"Its audio is moved to {OnAir.CentreAudioHz:F0} Hz here, where the usual dial puts the signal");
        _log(string.Create(invariant,
            $"audio: pdn-soundmodem on {stream.Where}, {rate}, {where}, the signal's centre {(awayHz is null ? "at" : "then at")} {centre:F0} Hz in its audio{moved}"));
    }

    /// <summary>
    /// Why pdn-soundmodem's channel cannot carry the signal, or null when it can, as its built-in
    /// receiver judged it: any dial whose channel holds the whole signal is used where it is.
    /// Refused are a dial it says its radio is on that puts the signal's centre below
    /// <see cref="ReceiverConfig.LowestAudioCentreHz"/>, the lowest this receiver takes from any
    /// radio (the config's own <c>dialKHz</c> is held to 1000 to 2000 Hz when it is read), and a
    /// channel too slow to carry the signal's top edge. Nothing is retuned for it.
    /// </summary>
    internal static string? SoundModemReach(int channelRate, double? saidDialHz, double dialHz)
    {
        double centre = OnAir.AudioCentreHz(dialHz);
        double top = channelRate / 2.0;
        double highestCentre = top - OnAir.HalfWidthHz;
        bool low = centre < ReceiverConfig.LowestAudioCentreHz;
        bool high = centre > highestCentre;
        if (!low && !high)
        {
            return null;
        }
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string signal = $"the signal on {OnAir.Mhz(OnAir.TransmitHz - OnAir.HalfWidthHz)} to {OnAir.Mhz(OnAir.TransmitHz + OnAir.HalfWidthHz)} MHz is outside what pdn-soundmodem hears. ";
        string why = low
            ? string.Create(invariant, $"below the {ReceiverConfig.LowestAudioCentreHz:F0} Hz this receiver needs at least")
            : string.Create(invariant, $"and its channel runs at {channelRate} Hz, which carries audio only up to {top:F0} Hz, while the signal reaches {centre + OnAir.HalfWidthHz:F0} Hz");
        if (saidDialHz is null)
        {
            // The config's own dial, already held to 1000 to 2000 Hz: only a slow channel gets here.
            return signal + string.Create(invariant,
                $"\"dialKHz\" {OnAir.Mhz(dialHz)} MHz puts the signal's centre at {centre:F0} Hz in its audio, {why}. "
                + $"Give pdn-soundmodem a modem that runs at 48 kHz, so its channel does too");
        }
        string range = highestCentre < ReceiverConfig.LowestAudioCentreHz
            ? string.Create(invariant, $"its channel at {channelRate} Hz cannot carry the signal on any dial: give pdn-soundmodem a modem that runs at 48 kHz, so its channel does too")
            : string.Create(invariant,
                $"set the dial pdn-soundmodem gives (its \"waterfall\".\"dialFrequencyHz\" if that is set, else its band plan's dial, which also tunes a Flex, else its \"dialFrequency\") "
                + $"from {OnAir.Mhz(OnAir.TransmitHz - highestCentre)} to {OnAir.Mhz(OnAir.TransmitHz - ReceiverConfig.LowestAudioCentreHz)} MHz, usually {OnAir.Mhz(ReceiverConfig.DefaultDialKHz * 1000)} MHz");
        return signal + string.Create(invariant,
            $"It says its radio is on {OnAir.Mhz(dialHz)} MHz USB, which would put the signal's centre at {centre:F0} Hz in its audio, {why}. "
            + $"Nothing is retuned for it: {range}; or, if pdn-soundmodem has a \"rig\" section, set \"retune\": true here for its receive window to tune the radio for each slot");
    }

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

            case AudioSourceKind.SoundModem:
                await OpenSoundModemAsync(cancellation).ConfigureAwait(false);
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
                bool transmitted = false, gap = false;
                double? movedFrom = null;
                if (_stream is not null)
                {
                    PlaceByPendingDial();
                }
                int read = _stream is { } stream ? stream.ReadBlock(block, _movedBlock, out transmitted, out gap, out movedFrom) : _input!.Read(block);
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
                if (_stream is not null)
                {
                    if (movedFrom != _movedFromHz)
                    {
                        // Made before the radio was retuned, with the move that went with it; the
                        // modem was made to listen afresh at the retune.
                        continue;
                    }
                    FeedStream(block.AsSpan(0, read), movedFrom is null ? default : _movedBlock.AsSpan(0, read), transmitted, gap);
                }
                else
                {
                    Feed(block.AsSpan(0, read), soundCard: _input is AlsaAudioInput);
                }
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

    /// <summary>
    /// One block of pdn-soundmodem's channel audio, on the audio thread (or a test's). A keyed
    /// block is already silence and goes through as it is, so every clock here keeps pace with
    /// pdn-soundmodem's; but MS110D is a stream mode, and the audio either side of a keyup or a
    /// gap is not one signal, so the modem is made to listen afresh after each keyed span and on
    /// every gap, in the same way, rather than decoding across the hole.
    /// </summary>
    internal void FeedStream(ReadOnlySpan<float> samples, bool transmitted, bool gap) => FeedStream(samples, default, transmitted, gap);

    /// <summary>
    /// As <see cref="FeedStream(ReadOnlySpan{float}, bool, bool)"/>, with the same block moved to
    /// the usual dial's 1800 Hz as well (<paramref name="moved"/>, empty when it is not moved).
    /// </summary>
    internal void FeedStream(ReadOnlySpan<float> samples, ReadOnlySpan<float> moved, bool transmitted, bool gap)
    {
        PlaceByPendingDial();
        if (gap)
        {
            ResetModem();
            SayGap();
        }
        else if (_keyed && !transmitted)
        {
            ResetModem();
        }
        _keyed = transmitted;
        // Keyed silence is not the band's audio: the level meter and "Measure my filter" skip it.
        if (moved.IsEmpty)
        {
            Feed(samples, heard: !transmitted);
            return;
        }
        // They read the radio's own audio, as it came; everything else reads it moved.
        if (!transmitted)
        {
            SourceBlock?.Invoke(samples);
        }
        Feed(moved, heard: false);
    }

    private void ResetModem()
    {
        foreach (var modem in Channel.Modems.Values)
        {
            modem.ResetCarrierState();
        }
        _lockedSamples = 0;
        Interlocked.Increment(ref _modemResets);
    }

    /// <summary>The longest between journal lines about gaps in pdn-soundmodem's audio.</summary>
    internal static readonly TimeSpan GapSayEvery = TimeSpan.FromMinutes(1);

    /// <summary>Says a gap in the journal, at most once every <see cref="GapSayEvery"/> with a count of the rest.</summary>
    private void SayGap()
    {
        long now = _time.GetTimestamp();
        if (_gapSaid != long.MinValue && _time.GetElapsedTime(_gapSaid, now) < GapSayEvery)
        {
            _gapsUnsaid++;
            return;
        }
        string more = _gapsUnsaid > 0 ? $" ({_gapsUnsaid} more since the last time this was said)" : "";
        _log($"audio: pdn-soundmodem lost some audio, so the modem listens afresh rather than decoding across the hole{more}");
        _gapSaid = now;
        _gapsUnsaid = 0;
    }

    /// <summary>One block through the modem and everything beside it, on the audio thread (or a test's).</summary>
    /// <param name="samples">The block.</param>
    /// <param name="soundCard">Whether it came from a sound card, whose clipping the channel notes.</param>
    /// <param name="heard">False for silence standing in for a keyup, which <see cref="SourceBlock"/> does not get (nor audio it already had before it was moved).</param>
    internal void Feed(ReadOnlySpan<float> samples, bool soundCard = false, bool heard = true)
    {
        if (heard)
        {
            SourceBlock?.Invoke(samples);
        }
        if (soundCard)
        {
            Channel.NoteCardClipping(samples);
        }
        Capture.Write(samples);
        Tone.Process(samples);
        if (_shift is null)
        {
            Channel.ProcessReceive(samples);
        }
        else
        {
            // Only the channel (the modem) reads the shifted audio: the tone detector and the
            // capture above already read this dial's own, at OnAir.AudioCentreHz(DialHz).
            if (_shifted.Length < samples.Length)
            {
                _shifted = new float[samples.Length];
            }
            var shifted = _shifted.AsSpan(0, samples.Length);
            _shift.Process(samples, shifted);
            Channel.ProcessReceive(shifted);
        }
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
    /// <summary>
    /// It will not come right by itself within seconds, so wait longer before asking again: a
    /// web receiver refused us for now (HTTP 429), or pdn-soundmodem gives a dial, a rate or a
    /// version this receiver cannot use, which only changes when someone changes it.
    /// </summary>
    public bool Refused { get; } = refused;
}
