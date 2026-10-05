using System.Globalization;
using System.Threading.Channels;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Station;

namespace Mailcast.HeadEnd.Slot;

/// <summary>
/// Runs one daily slot against the station: channel check and calibration tone, the transmit
/// lease, the frames in bursts, and the hard stops.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> Everything that can fail without keying the radio is done first: the Flex is
/// read, the KISS port is opened, and only then is the lease taken. The calibration tone goes out
/// as the lease holder's own transmitter test, so the station's carrier sense governs it, and the
/// station answers "the channel did not clear" when it never got to send it: that is the channel
/// check. The station's CW ident falls due with the first transmission, so a short pause after
/// the tone lets it go before the first burst.</para>
/// <para><b>Pacing.</b> One burst's worth of frames is queued at a time, written together so the
/// modem packs them (it gathers frames queued together for <c>burstGatherSeconds</c>), and the
/// next burst is queued only once every frame of this one has been acknowledged (ACKMODE: the
/// station answers each frame when its audio has been handed to the sound card, all of a packed
/// burst's at once). The station's queue never holds more than one burst of ours.</para>
/// <para><b>Inside the lease.</b> Before queueing a burst the runner checks that the lease it
/// holds has at least the burst's airtime and <see cref="SlotSettings.LeaseMargin"/> left, and
/// renews first if not. So a burst always finishes inside the lease, even when the renewal after
/// it fails.</para>
/// <para><b>Stopping.</b> A refused renewal, a hot PA or the hard stop at
/// <see cref="SlotSettings.MaxSlotLength"/> stop the runner queueing more; a burst already queued
/// finishes, because the station cannot take frames back. Frames not sent are the planner's to
/// carry to tomorrow.</para>
/// </remarks>
public sealed class SlotRunner
{
    private readonly SlotSettings _settings;
    private readonly IStationApi _station;
    private readonly IKissConnector _kiss;
    private readonly IFlexMonitor? _flex;
    private readonly IAirtime _airtime;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;

    public SlotRunner(SlotSettings settings, IStationApi station, IKissConnector kiss, IFlexMonitor? flex, IAirtime airtime, IJournal journal, TimeProvider time)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _station = station ?? throw new ArgumentNullException(nameof(station));
        _kiss = kiss ?? throw new ArgumentNullException(nameof(kiss));
        _flex = flex;
        _airtime = airtime ?? throw new ArgumentNullException(nameof(airtime));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The frames as the modem will be handed them: AX.25 UI frames from the callsign.</summary>
    public byte[] Ax25(SlotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Ax25Ui.Encode(_settings.Destination, _settings.Callsign, frame.Payload);
    }

    /// <summary>
    /// How the frames split into bursts: as many as fit in <see cref="SlotSettings.MaxBurst"/>
    /// less two seconds, or <see cref="SlotSettings.FramesPerBurst"/> when set. At least one each.
    /// </summary>
    public IReadOnlyList<int> BurstSizes(IReadOnlyList<SlotFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var sizes = new List<int>();
        TimeSpan target = _settings.MaxBurst - TimeSpan.FromSeconds(2);
        int next = 0;
        while (next < frames.Count)
        {
            int count = 0;
            var lengths = new List<int>();
            while (next + count < frames.Count)
            {
                if (_settings.FramesPerBurst is int fixedCount)
                {
                    if (count == fixedCount)
                    {
                        break;
                    }
                }
                else
                {
                    lengths.Add(Ax25(frames[next + count]).Length);
                    if (count > 0 && _airtime.Burst(lengths) > target)
                    {
                        break;
                    }
                }
                count++;
            }
            sizes.Add(count);
            next += count;
        }
        return sizes;
    }

    /// <summary>Runs the slot. Never throws for anything the station or the radio does.</summary>
    /// <param name="day">The broadcast day.</param>
    /// <param name="frames">The frames, in sending order.</param>
    /// <param name="bulletinsInRotation">For the log and the report.</param>
    /// <param name="cancellation">Stops the slot: nothing more is queued, and the lease is released.</param>
    /// <param name="queued">
    /// Told how many frames have been handed to the modem so far, each time more are: the planner
    /// records them as it goes, so a crash mid-slot never repeats one.
    /// </param>
    public async Task<SlotReport> RunAsync(DateOnly day, IReadOnlyList<SlotFrame> frames, int bulletinsInRotation, CancellationToken cancellation, Action<int>? queued = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using var run = new Run(this, day, frames, bulletinsInRotation, queued);
        return await run.ExecuteAsync(cancellation);
    }

    private static string Clock(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan t) => t.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Degrees(double c) => c.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>One slot's state.</summary>
    private sealed class Run(SlotRunner owner, DateOnly day, IReadOnlyList<SlotFrame> frames, int bulletins, Action<int>? onQueued) : IDisposable
    {
        public void Dispose() => _leaseGate.Dispose();

        private readonly SemaphoreSlim _leaseGate = new(1, 1);
        private SlotSettings S => owner._settings;
        private DateTimeOffset Now => owner._time.GetUtcNow();
        private DateTimeOffset _start;
        private long _leaseUntilTicks;
        private string? _abort;

        private DateTimeOffset LeaseUntil
        {
            get => new(Interlocked.Read(ref _leaseUntilTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _leaseUntilTicks, value.UtcTicks);
        }
        private string _reference = "not read (no Flex configured)";
        private double? _paMax;
        private bool _flexLost;
        private int _queued;
        private int _sent;
        private int _bursts;
        private int _renewals;
        private bool _leaseTaken;
        private bool _toneSent;

        private void Say(string line) => owner._journal.Write($"slot {day:yyyy-MM-dd}: {line}");

        public async Task<SlotReport> ExecuteAsync(CancellationToken cancellation)
        {
            _start = Now;
            var sizes = owner.BurstSizes(frames);
            TimeSpan estimate = Estimate(sizes);
            Say($"starting at {Clock(_start)}Z, {frames.Count} frames for {bulletins} bulletins in {sizes.Count} bursts, about {Minutes(estimate)} min on the air");

            if (frames.Count == 0)
            {
                return Finish(SlotOutcome.Skipped, "nothing to send");
            }

            if (owner._flex is not null)
            {
                string? flexSkip = await ReadFlexAsync(cancellation);
                if (flexSkip is not null)
                {
                    return Finish(SlotOutcome.Skipped, flexSkip);
                }
            }

            IKissLink link;
            try
            {
                link = await owner._kiss.ConnectAsync(cancellation);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Finish(SlotOutcome.Skipped, $"cannot reach the station's KISS port: {e.Message}");
            }

            await using (link)
            {
                DateTimeOffset asked = Now;
                LeaseAnswer first = await AskForLeaseAsync(cancellation);
                if (!first.Held)
                {
                    return Finish(SlotOutcome.Skipped, $"the station refused the transmit lease: {first.Problem}");
                }
                _leaseTaken = true;
                LeaseUntil = asked + TimeSpan.FromSeconds(first.Seconds);
                Say($"transmit lease taken for sub-channel {S.SubChannel}, {first.Seconds:0} s, renewed every {S.RenewEvery.TotalSeconds:0} s");

                using var slot = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                Task renewing = RenewLoopAsync(slot.Token);
                Task watching = owner._flex is { Connected: true } ? PaLoopAsync(slot.Token) : Task.CompletedTask;
                SlotOutcome? skipped = null;
                string? skipReason = null;
                try
                {
                    (skipped, skipReason) = await ToneAsync(cancellation);
                    if (skipped is null)
                    {
                        await BurstsAsync(link, sizes, cancellation);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Abort("the head end is stopping");
                }
                finally
                {
                    await slot.CancelAsync();
                    await Quietly(renewing);
                    await Quietly(watching);
                    await ReleaseAsync();
                }

                if (skipped is not null)
                {
                    return Finish(skipped.Value, skipReason);
                }
            }

            return _sent == frames.Count && _abort is null
                ? Finish(SlotOutcome.Completed, null)
                : Finish(SlotOutcome.Aborted, _abort ?? "stopped early");
        }

        private TimeSpan Estimate(IReadOnlyList<int> sizes)
        {
            TimeSpan total = TimeSpan.Zero;
            int next = 0;
            foreach (int n in sizes)
            {
                total += owner._airtime.Burst([.. frames.Skip(next).Take(n).Select(f => owner.Ax25(f).Length)]);
                next += n;
            }
            return total;
        }

        private async Task<string?> ReadFlexAsync(CancellationToken cancellation)
        {
            IFlexMonitor flex = owner._flex!;
            if (!await flex.ConnectAsync(cancellation))
            {
                _reference = "Flex unreachable";
                Say($"Flex API unreachable: {flex.Problem}");
                if (S.WhenFlexUnreachable == FlexUnreachablePolicy.Skip)
                {
                    return $"the Flex API could not be reached ({flex.Problem}) and the configuration says not to broadcast without it";
                }
                Say("carrying on without the PA temperature watch");
                return null;
            }

            ReferenceReading reference = flex.Reference;
            _reference = reference.Summary;
            Say($"Flex reference {reference.Summary}");
            if (reference.GpsLocked == false)
            {
                Say("WARNING - the Flex is not GPS locked, so today's tone is not a true frequency reference");
            }
            if (flex.PaTemperatureC is double pa)
            {
                _paMax = pa;
                Say($"Flex PA temperature {Degrees(pa)} C, limit {Degrees(S.PaTemperatureLimitC)} C");
                if (pa > S.PaTemperatureLimitC)
                {
                    return $"the PA is already at {Degrees(pa)} C, over the {Degrees(S.PaTemperatureLimitC)} C limit";
                }
            }
            else
            {
                Say("Flex PA temperature not reported yet");
            }
            return null;
        }

        private async Task<LeaseAnswer> AskForLeaseAsync(CancellationToken cancellation)
        {
            try
            {
                return await owner._station.TakeLeaseAsync(S.SubChannel, (int)Math.Ceiling(S.LeaseLength.TotalSeconds), cancellation);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
            {
                return LeaseAnswer.No(e.Message);
            }
        }

        private async Task<bool> RenewAsync(CancellationToken cancellation)
        {
            await _leaseGate.WaitAsync(cancellation);
            try
            {
                if (_abort is not null && _abort.StartsWith("lease", StringComparison.Ordinal))
                {
                    return false;
                }
                DateTimeOffset asked = Now;
                LeaseAnswer answer = await AskForLeaseAsync(cancellation);
                if (!answer.Held)
                {
                    Abort($"lease renewal failed: {answer.Problem}");
                    return false;
                }
                LeaseUntil = asked + TimeSpan.FromSeconds(answer.Seconds);
                _renewals++;
                return true;
            }
            finally
            {
                _leaseGate.Release();
            }
        }

        private async Task RenewLoopAsync(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(S.RenewEvery, owner._time, token);
                if (!await RenewAsync(token))
                {
                    return;
                }
            }
        }

        private async Task PaLoopAsync(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(S.PaCheckEvery, owner._time, token);
                CheckPa();
            }
        }

        private void CheckPa()
        {
            IFlexMonitor? flex = owner._flex;
            if (flex is null)
            {
                return;
            }
            if (!flex.Connected)
            {
                if (!_flexLost)
                {
                    _flexLost = true;
                    Say($"Flex API session lost ({flex.Problem ?? "no reason given"}); carrying on without the PA temperature watch");
                }
                return;
            }
            if (flex.PaTemperatureC is double pa)
            {
                _paMax = Math.Max(_paMax ?? pa, pa);
                if (pa > S.PaTemperatureLimitC)
                {
                    Abort($"PA temperature {Degrees(pa)} C passed the {Degrees(S.PaTemperatureLimitC)} C limit");
                }
            }
        }

        private async Task<(SlotOutcome?, string?)> ToneAsync(CancellationToken cancellation)
        {
            if (S.ToneLength <= TimeSpan.Zero)
            {
                return (null, null);
            }
            DateTimeOffset giveUp = Now + S.ChannelWait;
            while (true)
            {
                ToneAnswer answer;
                try
                {
                    answer = await owner._station.SendToneAsync(S.SubChannel, S.ToneHz, S.ToneLength.TotalSeconds, cancellation);
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
                {
                    answer = new ToneAnswer(ToneOutcome.Failed, e.Message);
                }

                switch (answer.Outcome)
                {
                    case ToneOutcome.Sent:
                        _toneSent = true;
                        Say($"calibration tone sent, {S.ToneLength.TotalSeconds:0} s at {S.ToneHz:0} Hz");
                        if (_abort is null && S.PauseAfterTone > TimeSpan.Zero)
                        {
                            await Task.Delay(S.PauseAfterTone, owner._time, cancellation);
                        }
                        return (null, null);

                    case ToneOutcome.ChannelBusy when Now < giveUp:
                        Say($"channel busy, no tone yet ({answer.Message}); trying again");
                        await Task.Delay(TimeSpan.FromSeconds(5), owner._time, cancellation);
                        continue;

                    case ToneOutcome.ChannelBusy when S.WhenStillBusy == BusyPolicy.Skip:
                        return (SlotOutcome.Skipped, $"the channel stayed busy for {S.ChannelWait.TotalMinutes:0.#} min");

                    case ToneOutcome.ChannelBusy:
                        Say($"channel still busy after {S.ChannelWait.TotalMinutes:0.#} min; going ahead without the tone (the modem still waits for a clear channel before each burst)");
                        return (null, null);

                    default:
                        Say($"the station did not send the tone ({answer.Message}); carrying on without it");
                        return (null, null);
                }
            }
        }

        private async Task BurstsAsync(IKissLink link, IReadOnlyList<int> sizes, CancellationToken cancellation)
        {
            ushort id = 0;
            int next = 0;
            foreach (int count in sizes)
            {
                if (_abort is not null)
                {
                    return;
                }
                var batch = frames.Skip(next).Take(count).Select(owner.Ax25).ToList();
                TimeSpan airtime = owner._airtime.Burst([.. batch.Select(b => b.Length)]);

                if (Now + airtime > _start + S.MaxSlotLength)
                {
                    Abort($"the {S.MaxSlotLength.TotalMinutes:0} min maximum slot length would be passed by the next burst");
                    return;
                }
                if (LeaseUntil - Now < airtime + S.LeaseMargin)
                {
                    if (!await RenewAsync(cancellation))
                    {
                        return;
                    }
                    if (LeaseUntil - Now < airtime + S.LeaseMargin)
                    {
                        Abort($"the station grants too short a lease ({(LeaseUntil - Now).TotalSeconds:0} s) for a {airtime.TotalSeconds:0} s burst");
                        return;
                    }
                }

                var waiting = new HashSet<ushort>();
                foreach (byte[] frame in batch)
                {
                    id++;
                    waiting.Add(id);
                    await link.SendAsync(id, frame, cancellation);
                    _queued++;
                }
                _bursts++;
                onQueued?.Invoke(_queued);

                int acknowledged = await AcksAsync(link, waiting, airtime + S.AckGrace, cancellation);
                _sent += acknowledged;
                next += count;
                if (acknowledged < batch.Count)
                {
                    Abort($"the modem acknowledged {acknowledged} of the {batch.Count} frames of burst {_bursts} within {(airtime + S.AckGrace).TotalSeconds:0} s");
                    return;
                }
                if (_bursts % 5 == 0 || next == frames.Count)
                {
                    Say($"{_sent} of {frames.Count} frames sent in {_bursts} bursts");
                }
                if (next < frames.Count && S.BurstGap > TimeSpan.Zero)
                {
                    await Task.Delay(S.BurstGap, owner._time, cancellation);
                }
            }
        }

        private async Task<int> AcksAsync(IKissLink link, HashSet<ushort> waiting, TimeSpan within, CancellationToken cancellation)
        {
            int total = waiting.Count;
            using var timeout = new CancellationTokenSource(within, owner._time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            try
            {
                while (waiting.Count > 0)
                {
                    waiting.Remove(await link.Acks.ReadAsync(either.Token));
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // Out of time; the caller says so.
            }
            catch (ChannelClosedException)
            {
                Say("the KISS connection closed");
            }
            return total - waiting.Count;
        }

        private async Task ReleaseAsync()
        {
            if (!_leaseTaken)
            {
                return;
            }
            try
            {
                using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30), owner._time);
                bool released = await owner._station.ReleaseLeaseAsync(S.SubChannel, bounded.Token);
                Say(released ? "transmit lease released" : "the station had no lease of ours to release (it may have run out)");
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                Say($"could not release the transmit lease ({e.Message}); it runs out by itself within {S.LeaseLength.TotalSeconds:0} s");
            }
        }

        private void Abort(string reason) => Interlocked.CompareExchange(ref _abort, reason, null);

        private static async Task Quietly(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private SlotReport Finish(SlotOutcome outcome, string? reason)
        {
            DateTimeOffset end = Now;
            var report = new SlotReport
            {
                Day = day,
                Start = _start,
                End = end,
                Outcome = outcome,
                Reason = reason,
                FramesPlanned = frames.Count,
                FramesSent = _sent,
                FramesQueued = _queued,
                Bursts = _bursts,
                BulletinsInRotation = bulletins,
                ToneSent = _toneSent,
                Reference = _reference,
                PaTemperatureMaxC = _paMax,
                LeaseTaken = _leaseTaken,
                LeaseRenewals = _renewals,
            };
            string pa = _paMax is double max ? $"{Degrees(max)} C" : "not read";
            switch (outcome)
            {
                case SlotOutcome.Completed:
                    Say($"done, {Clock(_start)} to {Clock(end)}Z, {_sent} of {frames.Count} frames sent in {_bursts} bursts, {bulletins} bulletins in rotation, tone {(_toneSent ? "sent" : "not sent")}, PA max {pa}, reference {_reference}");
                    break;
                case SlotOutcome.Aborted:
                    Say($"ABORTED at {Clock(end)}Z: {reason}. {_sent} of {frames.Count} frames sent in {_bursts} bursts ({_queued} queued); the rest roll to tomorrow. PA max {pa}, reference {_reference}");
                    break;
                default:
                    Say($"skipped: {reason}. {(_toneSent || _queued > 0 ? "Something was sent" : "Nothing was transmitted")}; everything rolls to tomorrow");
                    break;
            }
            if (_toneSent || _queued > 0)
            {
                Say("the modem sends its closing CW ident when its 10 minute ident clock next falls due, not at once");
            }
            return report;
        }
    }
}
