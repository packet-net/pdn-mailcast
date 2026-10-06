using System.Threading.Channels;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;

namespace Mailcast.HeadEnd.Tests;

/// <summary>One stretch of time the fake radio was keyed, and what for.</summary>
public sealed record Keyup(DateTimeOffset Start, DateTimeOffset End, string What, bool InsideLease);

/// <summary>
/// pdn-soundmodem as the head end sees it - the lease and tone API and the broadcast modem's KISS
/// port - on a virtual clock, with #545's lease as the head end codes against it: a channel-busy
/// flag, maxCarrierWaitSeconds (frames go anyway after it), dropQueued on release and on its own, and the holder's unkeyed
/// frames dropped when the lease runs out. Its transmitter keys whatever is still queued once the
/// channel clears, lease or no lease, so a frame the head end failed to get dropped shows up as a
/// keyup outside the lease. It keeps a record of every keyup and whether a lease held by the
/// broadcast sub-channel covered all of it.
/// </summary>
public sealed class FakeStation : IStationApi, IKissConnector
{
    private readonly VirtualTime _time;
    private readonly int _subChannel;
    private int? _holder;
    private DateTimeOffset _expires;
    private TimeSpan _maxCarrierWait = TimeSpan.MaxValue;
    private int _dropGeneration;
    private int _keying;
    private int _busyReads;

    public FakeStation(VirtualTime time, int subChannel)
    {
        _time = time;
        _subChannel = subChannel;
    }

    /// <summary>Each lease request's time and whether it was granted.</summary>
    public List<(DateTimeOffset At, bool Granted)> LeaseRequests { get; } = [];

    public List<DateTimeOffset> Releases { get; } = [];

    public List<DateTimeOffset> DropRequests { get; } = [];

    public List<Keyup> Keyups { get; } = [];

    /// <summary>When each frame reached the station, with whether the lease was ours then.</summary>
    public List<(DateTimeOffset At, bool LeaseHeld, byte[] Frame)> Frames { get; } = [];

    /// <summary>Each SETHW the head end sent, with when and whether the lease was held then.</summary>
    public List<(DateTimeOffset At, bool LeaseHeld, byte[] Payload)> SetHardware { get; } = [];

    /// <summary>Whether the modem applies SETHW and echoes it, as pdn-soundmodem's MS110D modem does; false is a modem that refuses it.</summary>
    public bool AppliesSetHardware { get; set; } = true;

    /// <summary>The next SETHW is applied but its echo never comes, as if lost: the head end hears nothing, yet the modem has changed.</summary>
    public bool NextEchoLost { get; set; }

    /// <summary>The next SETHW is applied, then the KISS connection it came on breaks before the echo.</summary>
    public bool NextSetHardwareBreaksLink { get; set; }

    /// <summary>The waveform number the modem transmits on now, as the last SETHW it applied left it.</summary>
    public int? Waveform { get; private set; }

    /// <summary>Frames the station dropped without keying them.</summary>
    public int Dropped { get; private set; }

    /// <summary>Lease requests after this many are refused, as if another sub-channel had taken it.</summary>
    public int? RefuseAfterRequests { get; set; }

    /// <summary>The first lease request is refused.</summary>
    public bool RefuseFirstLease { get; set; }

    /// <summary>The station's carrier sense says busy until then.</summary>
    public DateTimeOffset ChannelBusyUntil { get; set; }

    /// <summary>Whether the station answers tones at all: false answers 404 as with txTest off.</summary>
    public bool ToneAvailable { get; set; } = true;

    /// <summary>The modem stops acknowledging after this many frames, though it still transmits.</summary>
    public int? SilentAfterFrames { get; set; }

    /// <summary>A station that ignores dropQueued, lease expiry and the carrier limit, to show the keyup check catches it.</summary>
    public bool IgnoreDrops { get; set; }

    /// <summary>The KISS link fails on this write (1-based), as a closed socket would.</summary>
    public int? FailWriteNumber { get; set; }

    public double SecondsPerFrame { get; set; } = 7.25;

    public double SecondsPerBurst { get; set; } = 1.2;

    public bool LeaseHeld => _holder == _subChannel && _time.GetUtcNow() < _expires;

    /// <summary>As #545's flag: busy for somebody else's signal, and while this station itself transmits.</summary>
    public bool ChannelBusy => _keying > 0 || _time.GetUtcNow() < ChannelBusyUntil;

    /// <summary>The first channel-busy read answers clear whatever the truth, as a flag read just before a keyup begins would.</summary>
    public bool FirstBusyReadMisses { get; set; }

    /// <summary>Keys the radio for something else that was already on the air when the slot began, as a keyup a lease does not cut short.</summary>
    public void KeyOtherTraffic(TimeSpan length) => _ = KeyAsync(length, "other traffic, already on the air", CancellationToken.None);

    public async Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, CancellationToken cancellation)
    {
        await Task.Yield();
        bool refuse = (RefuseFirstLease && LeaseRequests.Count == 0)
            || (RefuseAfterRequests is int n && LeaseRequests.Count >= n);
        LeaseRequests.Add((_time.GetUtcNow(), !refuse));
        if (refuse)
        {
            return new LeaseAnswer(false, 0, "sub-channel 2 holds the transmit lease until later", ChannelBusy);
        }
        _holder = subChannel;
        _maxCarrierWait = TimeSpan.FromSeconds(maxCarrierWaitSeconds);
        int granted = Math.Min(seconds, 300);
        _expires = _time.GetUtcNow() + TimeSpan.FromSeconds(granted);
        return new LeaseAnswer(true, granted, null, ChannelBusy);
    }

    public async Task<LeaseAnswer> ReadLeaseAsync(CancellationToken cancellation)
    {
        await Task.Yield();
        bool busy = ChannelBusy && !(FirstBusyReadMisses && _busyReads == 0);
        _busyReads++;
        return new LeaseAnswer(LeaseHeld, 0, null, busy);
    }

    public async Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation)
    {
        await Task.Yield();
        Releases.Add(_time.GetUtcNow());
        bool held = LeaseHeld;
        _holder = null;
        DropUnkeyed();
        return held;
    }

    public async Task<bool> DropQueuedAsync(int subChannel, CancellationToken cancellation)
    {
        await Task.Yield();
        DropRequests.Add(_time.GetUtcNow());
        DropUnkeyed();
        return true;
    }

    public async Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, CancellationToken cancellation)
    {
        if (!ToneAvailable)
        {
            await Task.Yield();
            return new ToneAnswer(ToneOutcome.Refused, "HTTP 404: no transmitter test here");
        }
        DateTimeOffset giveUp = _time.GetUtcNow() + TimeSpan.FromSeconds(60);
        while (ChannelBusy)
        {
            if (_time.GetUtcNow() >= giveUp)
            {
                return new ToneAnswer(ToneOutcome.Refused, "HTTP 409: the test was withdrawn and nothing was transmitted");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellation);
        }
        await KeyAsync(TimeSpan.FromSeconds(seconds), $"tone {toneHz} Hz", cancellation);
        return new ToneAnswer(ToneOutcome.Sent, "tone");
    }

    public Task<IKissLink> ConnectAsync(CancellationToken cancellation) => Task.FromResult<IKissLink>(new FakeLink(this));

    private void DropUnkeyed()
    {
        if (!IgnoreDrops)
        {
            _dropGeneration++;
        }
    }

    private async Task KeyAsync(TimeSpan length, string what, CancellationToken cancellation)
    {
        DateTimeOffset start = _time.GetUtcNow();
        bool inside = LeaseHeld;
        _keying++;
        try
        {
            await Task.Delay(length, _time, cancellation);
        }
        finally
        {
            _keying--;
        }
        inside &= LeaseHeld;
        Keyups.Add(new Keyup(start, _time.GetUtcNow(), what, inside));
    }

    private sealed class FakeLink(FakeStation station) : IKissLink
    {
        private readonly Channel<ushort> _acks = Channel.CreateUnbounded<ushort>();
        private readonly List<(ushort Id, int Generation)> _queued = [];
        private bool _transmitting;
        private bool _broken;
        private int _writes;
        private int _acked;

        public ChannelReader<ushort> Acks => _acks.Reader;

        public async Task<bool> SetHardwareAsync(ReadOnlyMemory<byte> payload, TimeSpan wait, TimeProvider time, CancellationToken cancellation)
        {
            if (_broken)
            {
                throw new IOException("Broken pipe");
            }
            station.SetHardware.Add((station._time.GetUtcNow(), station.LeaseHeld, payload.ToArray()));
            if (station.NextSetHardwareBreaksLink)
            {
                station.NextSetHardwareBreaksLink = false;
                station.Waveform = payload.Span[0];
                _broken = true;
                _acks.Writer.TryComplete();
                await Task.Yield();
                return false;
            }
            if (station.NextEchoLost)
            {
                station.NextEchoLost = false;
                station.Waveform = payload.Span[0];
                await Task.Delay(wait, time, cancellation);
                return false;
            }
            if (station.AppliesSetHardware)
            {
                await Task.Yield();
                station.Waveform = payload.Span[0];
                return true;
            }
            // Refused: nothing comes back, so the head end waits out its time.
            await Task.Delay(wait, time, cancellation);
            return false;
        }

        public Task SendAsync(ushort id, ReadOnlyMemory<byte> ax25, CancellationToken cancellation)
        {
            if (++_writes == station.FailWriteNumber)
            {
                throw new IOException("Broken pipe");
            }
            station.Frames.Add((station._time.GetUtcNow(), station.LeaseHeld, ax25.ToArray()));
            _queued.Add((id, station._dropGeneration));
            if (!_transmitting)
            {
                _transmitting = true;
                _ = TransmitAsync();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gathers for half a second, then sends everything queued as one burst once the channel is
        /// clear, dropping it if it waited past the lease's carrier limit, was dropped by request, or
        /// the lease ran out first.
        /// </summary>
        private async Task TransmitAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5), station._time);
            while (_queued.Count > 0)
            {
                var burst = _queued.ToList();
                _queued.Clear();
                // #545: the holder's frames wait for a clear channel no longer than the lease's
                // maxCarrierWaitSeconds, then go anyway.
                DateTimeOffset waitFrom = station._time.GetUtcNow();
                while (station.ChannelBusy && (station.IgnoreDrops || station._time.GetUtcNow() - waitFrom < station._maxCarrierWait))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), station._time);
                }
                bool drop = burst.Any(b => b.Generation < station._dropGeneration);
                drop |= !station.IgnoreDrops && !station.LeaseHeld;
                if (drop)
                {
                    station.Dropped += burst.Count;
                    continue;
                }
                await station.KeyAsync(TimeSpan.FromSeconds(station.SecondsPerBurst + (station.SecondsPerFrame * burst.Count)), $"burst of {burst.Count}", CancellationToken.None);
                foreach (var (id, _) in burst)
                {
                    if (station.SilentAfterFrames is int quiet && _acked >= quiet)
                    {
                        continue;
                    }
                    _acked++;
                    _acks.Writer.TryWrite(id);
                }
            }
            _transmitting = false;
        }

        public ValueTask DisposeAsync()
        {
            _acks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A Flex whose PA temperature follows a script and whose reference is fixed.</summary>
public sealed class FakeFlex(TimeProvider time, Func<TimeSpan, double?> temperature, ReferenceReading reference, bool reachable = true) : IFlexMonitor
{
    private DateTimeOffset _connectedAt;

    public string? Problem { get; private set; }

    public bool Connected { get; private set; }

    public ReferenceReading Reference => reference;

    public double? PaTemperatureC => Connected ? temperature(time.GetUtcNow() - _connectedAt) : null;

    public Task<bool> ConnectAsync(CancellationToken cancellation)
    {
        Connected = reachable;
        Problem = reachable ? null : "no route to host";
        _connectedAt = time.GetUtcNow();
        return Task.FromResult(reachable);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A system clock that is, or is not, synchronised.</summary>
public sealed class FakeClockSync(bool synchronised = true) : IClockSync
{
    public bool Synchronised { get; set; } = synchronised;

    public ClockState Check() => new(Synchronised, Synchronised ? "synchronised" : "the kernel says the clock is not synchronised");
}

/// <summary>The airtime the fake station uses, so plans and keyups agree.</summary>
public static class FakeAirtime
{
    public static LinearAirtime For(FakeStation station) =>
        new(TimeSpan.FromSeconds(station.SecondsPerBurst), TimeSpan.Zero, station.SecondsPerFrame / 1003.0);

    public static SlotFrame Frame(int i, int length = 987) =>
        new(Enumerable.Range(0, length).Select(b => (byte)(b + i)).ToArray(), (ulong)(i % 5), (uint)i);
}
