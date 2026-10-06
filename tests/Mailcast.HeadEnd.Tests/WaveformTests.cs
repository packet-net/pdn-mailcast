using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Offline;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Mailcast.HeadEnd.Tests;

/// <summary>A waveform per slot: which slot gets which, switching the modem, and what says so afterwards.</summary>
public class WaveformTests
{
    private static readonly SlotTimetable Hourly = new(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);

    private static Waveforms Alternating(bool setOnModem = true) =>
        new(["ms110d-wn4", "ms110d-wn3"], setOnModem, Hourly, mode => new LinearAirtime(TimeSpan.FromSeconds(1), TimeSpan.Zero, mode.EndsWith('3') ? 0.014 : 0.007));

    [Fact]
    public void Modes_TakeTurnsSlotBySlot_AndTheNextDayStartsOneOn()
    {
        var waveforms = Alternating();
        var monday = Hourly.ActiveSlotsOn(new DateOnly(2026, 10, 5));
        var tuesday = Hourly.ActiveSlotsOn(new DateOnly(2026, 10, 6));
        Assert.Equal(9, monday.Count);
        var a = monday.Select(waveforms.ModeFor).ToList();
        var b = tuesday.Select(waveforms.ModeFor).ToList();
        for (int i = 1; i < a.Count; i++)
        {
            Assert.NotEqual(a[i - 1], a[i]);
            Assert.NotEqual(b[i - 1], b[i]);
        }
        // The same hour gets the other mode the next day, so neither mode keeps the best hours.
        Assert.All(Enumerable.Range(0, 9), i => Assert.NotEqual(a[i], b[i]));
        // A slot run again, or a one-off later in the hour, gets the same mode as its hour.
        Assert.Equal(waveforms.ModeFor(monday[3]), waveforms.ModeFor(monday[3]));
        Assert.Equal(waveforms.ModeFor(monday[3]), waveforms.ModeFor(monday[3].AddMinutes(37)));
    }

    [Fact]
    public void Modes_CycleThroughThree_AndOneModeIsEverySlot()
    {
        var three = new Waveforms(["ms110d-wn4", "ms110d-wn3", "ms110d-wn2"], true, Hourly, _ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01));
        var day = Hourly.ActiveSlotsOn(new DateOnly(2026, 10, 5)).Select(three.ModeFor).ToList();
        for (int i = 3; i < day.Count; i++)
        {
            Assert.Equal(day[i - 3], day[i]);
        }
        Assert.Equal(3, day.Distinct(StringComparer.Ordinal).Count());

        var one = new Waveforms(["ms110d-wn4"], false, Hourly, _ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01));
        Assert.All(Enumerable.Range(0, 48), h => Assert.Equal("ms110d-wn4", one.ModeFor(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).AddHours(h))));
        Assert.Null(one.For(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)).SetHardware);
    }

    [Theory]
    [InlineData("ms110d-wn4", new byte[] { 4, 0 })]
    [InlineData("ms110d-wn3", new byte[] { 3, 0 })]
    [InlineData("ms110d-wn13", new byte[] { 13, 0 })]
    [InlineData("ms110d-wn9", null)]
    [InlineData("ms110d-wn4x", null)]
    [InlineData("afsk1200", null)]
    public void SetHardwarePayload_IsTheWaveformNumberAndTheShortInterleaver(string mode, byte[]? payload) =>
        Assert.Equal(payload, Waveforms.SetHardwarePayload(mode));

    [Fact]
    public void Config_ModesDefaultToTheStationsOwn_AndAreChecked()
    {
        var plain = HeadEndConfig.Parse("""{"station": {"apiKey": "k", "mode": "ms110d-wn4"}, "slot": {"everyMinutes": 60}}""");
        Assert.Equal(["ms110d-wn4"], plain.Modes);
        Assert.False(plain.ToWaveforms(_ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01)).SetOnModem);

        var both = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"modes": ["ms110d-wn4", "ms110d-wn3"]}}""");
        Assert.Equal(["ms110d-wn4", "ms110d-wn3"], both.Modes);
        Assert.True(both.ToWaveforms(_ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01)).SetOnModem);

        var bad = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "schedule": {"modes": ["ms110d-wn4", "vara"]}}"""));
        Assert.Contains("'vara' is not an MS110D waveform", bad.Message, StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "schedule": {"modes": []}}"""));
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k", "mode": "afsk1200"}, "schedule": {"modes": ["ms110d-wn4"]}}"""));
    }

    [Fact]
    public void Config_HourlyFillsToBudget_TheSharesRuleStaysAvailable()
    {
        var hourly = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}}""");
        Assert.Equal(ScheduleRule.Budget, hourly.Rule);
        var rule = hourly.ToScheduleOptions().Budget!;
        Assert.Equal(0.6, rule.SlotCap);
        Assert.Equal(6.0, rule.RetireCoverage);
        Assert.Equal(TimeSpan.FromHours(36), rule.RetireAfter);
        // A 10 minute budget and hard stop, less the 2 minute clear-channel wait, the gather and one carrier wait.
        Assert.Equal(TimeSpan.FromSeconds(131), hourly.Margin);
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(131), hourly.FillLimit);
        // A slot that starts late fills less, so it ends when an on-time one would.
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(131), hourly.FillLimitAfter(TimeSpan.FromMinutes(5)));
        // Without a tone there is no clear-channel wait, but the carrier wait is still kept back.
        Assert.Equal(TimeSpan.FromSeconds(11), HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60, "toneSeconds": 0}}""").Margin);

        var tuned = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"budgetMinutes": 6, "slotCap": 0.5, "retireCoverage": 4, "retireHours": 24}}""");
        Assert.Equal(TimeSpan.FromMinutes(6), tuned.FillLimit);
        Assert.Equal(new BudgetRule { SlotCap = 0.5, RetireCoverage = 4, RetireAfter = TimeSpan.FromHours(24) }, tuned.ToScheduleOptions().Budget);
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(41), HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"marginSeconds": 30}}""").FillLimit);

        // The rule before, by name or by its keys, and a daily station by default.
        Assert.Null(HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"rule": "shares"}}""").ToScheduleOptions().Budget);
        Assert.Null(HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"slotShares": [1.5, 0.5]}}""").ToScheduleOptions().Budget);
        Assert.Null(HeadEndConfig.Parse("""{"station": {"apiKey": "k"}}""").ToScheduleOptions().Budget);
        Assert.NotNull(HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "schedule": {"rule": "budget"}}""").ToScheduleOptions().Budget);

        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"rule": "budget", "slotShares": [1.5]}}"""));
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"rule": "shares", "budgetMinutes": 5}}"""));
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"slotCap": 0}}"""));
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": {"marginSeconds": 600}}"""));
    }

    private sealed class Rig
    {
        public Rig()
        {
            Time = new VirtualTime(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
            Station = new FakeStation(Time, 4);
            Runner = new SlotRunner(Settings, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public SlotSettings Settings { get; } = new() { SubChannel = 4, ToneLength = TimeSpan.FromSeconds(10) };

        public MemoryJournal Journal { get; } = new();

        public SlotRunner Runner { get; }

        public SlotReport Run(SlotWaveform? waveform) =>
            Time.Run(() => Runner.RunAsync(Time.GetUtcNow(), [.. Enumerable.Range(0, 6).Select(i => FakeAirtime.Frame(i))], 2, CancellationToken.None, null, null, waveform));
    }

    [Fact]
    public void Run_SwitchesTheModemBeforeTheLease_AndTheReportAndJournalNameTheWaveform()
    {
        var rig = new Rig();
        var waveform = new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]);
        var report = rig.Run(waveform);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("ms110d-wn3", report.Mode);
        Assert.NotNull(report.EstimatedSeconds);
        // Switched before the lease, and put back after the release, so WN3 is never left set outside the slot.
        Assert.Equal(2, rig.Station.SetHardware.Count);
        var set = rig.Station.SetHardware[0];
        Assert.Equal([3, 0], set.Payload);
        Assert.False(set.LeaseHeld);
        Assert.True(set.At <= rig.Station.LeaseRequests[0].At);
        var back = rig.Station.SetHardware[1];
        Assert.Equal([4, 0], back.Payload);
        Assert.True(back.At >= rig.Station.Releases.Single());
        Assert.True(back.At >= rig.Station.Keyups[^1].End);
        Assert.Equal(4, rig.Station.Waveform);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("modem put back on ms110d-wn4", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains("bursts on ms110d-wn3, about", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains("modem switched to ms110d-wn3", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains("done,", StringComparison.Ordinal) && l.Contains("on ms110d-wn3", StringComparison.Ordinal));
        Assert.All(rig.Journal.Lines, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), $"not plain ASCII: {l}"));
    }

    [Fact]
    public void Run_WithoutAWaveform_LeavesTheModemAlone_AndNamesTheConfiguredMode()
    {
        var rig = new Rig();
        var report = rig.Run(null);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("ms110d-wn4", report.Mode);
        Assert.Empty(rig.Station.SetHardware);
    }

    [Fact]
    public void Run_SkipsTheSlot_WhenTheModemDoesNotConfirmTheChange()
    {
        var rig = new Rig();
        rig.Station.AppliesSetHardware = false;
        var report = rig.Run(new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.True(report.Retryable);
        Assert.Contains("did not confirm the change to ms110d-wn3", report.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Station.LeaseRequests);
        Assert.Empty(rig.Station.Keyups);
        Assert.Empty(rig.Station.Frames);
    }

    [Fact]
    public void Run_PutsTheWaveformBack_WhenTheSlotIsSkippedAfterSwitching()
    {
        var rig = new Rig();
        rig.Station.RefuseFirstLease = true;
        var report = rig.Run(new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.Keyups);
        Assert.Equal([[3, 0], [4, 0]], rig.Station.SetHardware.Select(s => s.Payload));
        Assert.Equal(4, rig.Station.Waveform);
    }

    [Fact]
    public void Run_PutsTheWaveformBack_WhenTheHeadEndStopsMidSlot()
    {
        var rig = new Rig();
        CancellationTokenSource? stop = null;
        var report = rig.Time.Run(() =>
        {
            stop = new CancellationTokenSource(TimeSpan.FromSeconds(40), rig.Time);
            return rig.Runner.RunAsync(rig.Time.GetUtcNow(), [.. Enumerable.Range(0, 30).Select(i => FakeAirtime.Frame(i))], 2, stop.Token, null, null, new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));
        });
        stop!.Dispose();
        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.True(report.FramesSent < 30);
        Assert.Equal(4, rig.Station.Waveform);
    }

    [Fact]
    public void Run_PutsTheWaveformBack_WhenTheEchoIsLost()
    {
        // The modem applied WN3 but its echo never came: the slot is skipped, and WN4 goes back anyway.
        var rig = new Rig();
        rig.Station.NextEchoLost = true;
        var report = rig.Run(new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.Keyups);
        Assert.Equal([[3, 0], [4, 0]], rig.Station.SetHardware.Select(s => s.Payload));
        Assert.Equal(4, rig.Station.Waveform);
    }

    [Fact]
    public void Run_PutsTheWaveformBack_WhenStoppedWhileWaitingForTheEcho()
    {
        var rig = new Rig();
        rig.Station.NextEchoLost = true;
        CancellationTokenSource? stop = null;
        Exception? thrown = null;
        rig.Time.Run(async () =>
        {
            stop = new CancellationTokenSource(TimeSpan.FromSeconds(2), rig.Time);
            try
            {
                await rig.Runner.RunAsync(rig.Time.GetUtcNow(), [.. Enumerable.Range(0, 6).Select(i => FakeAirtime.Frame(i))], 2, stop.Token, null, null, new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));
            }
            catch (OperationCanceledException e)
            {
                thrown = e;
            }
            return 0;
        });
        stop!.Dispose();
        Assert.NotNull(thrown);
        Assert.Empty(rig.Station.LeaseRequests);
        Assert.Equal(4, rig.Station.Waveform);
    }

    [Fact]
    public void Run_PutsTheWaveformBack_OnANewConnection_WhenTheLinkFailsAfterTheWrite()
    {
        var rig = new Rig();
        rig.Station.NextSetHardwareBreaksLink = true;
        var report = rig.Run(new SlotWaveform("ms110d-wn3", FakeAirtime.For(rig.Station), [3, 0]));
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.Keyups);
        Assert.Equal(4, rig.Station.Waveform);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("modem put back on ms110d-wn4", StringComparison.Ordinal));
    }

    [Fact]
    public void StartUp_PutsTheModemOnTheConfiguredWaveform()
    {
        // As after a head end that died mid-slot with WN3 set.
        var rig = new Rig();
        rig.Time.Run(async () =>
        {
            await using var link = await rig.Station.ConnectAsync(CancellationToken.None);
            return await link.SetHardwareAsync(new byte[] { 3, 0 }, TimeSpan.FromSeconds(5), rig.Time, CancellationToken.None);
        });
        Assert.Equal(3, rig.Station.Waveform);
        Assert.True(rig.Time.Run(() => rig.Runner.PutWaveformBackAsync(CancellationToken.None)));
        Assert.Equal(4, rig.Station.Waveform);
        Assert.Contains(rig.Journal.Lines, l => l == "station: broadcast modem set to ms110d-wn4");
    }

    [Fact]
    public void Plan_TheDirectoryNamesTheSlotsWaveform()
    {
        using var state = new TempDirectory();
        var config = HeadEndConfig.Parse("""{"station": {"apiKey": "k", "maxBurstSeconds": 18}, "slot": {"timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10}, "schedule": {"symbolSize": 240, "modes": ["ms110d-wn4", "ms110d-wn3"]}}""");
        var options = config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        store.Offer(Bulletins.Make(71, 3000), new DateOnly(2026, 10, 6));
        var waveforms = config.ToWaveforms(LinearAirtime.Measure);
        var planner = new StoreSlotPlanner(store, Compression.Default, options, waveforms, config.ToSlotSettings(), config.FillLimitAfter);
        var slot = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);
        var plan = planner.Plan(slot);

        Assert.Equal(waveforms.ModeFor(slot), plan.Waveform!.Mode);
        Assert.Equal(plan.Waveform.Mode, plan.Broadcast!.Directory.Mode);
        // As a receiver reads it back from the air.
        var parsed = BroadcastDirectory.Parse(plan.Broadcast.Directory.Serialize());
        Assert.Equal(plan.Waveform.Mode, parsed.Mode);
        Assert.Equal(config.ToSlotTimetable(), parsed.Schedule);
    }

    [Fact]
    public void Wn3_DecodesOnAReceiverModemSetUpForWn4()
    {
        // Receivers are autobaud: the mailcast receiver builds a plain MS110D modem, and pdn-soundmodem's
        // MS110D receive path reads the waveform number from each burst's preamble.
        var day = new DateOnly(2026, 10, 6);
        using var state = new TempDirectory();
        using var output = new TempDirectory();
        var options = new ScheduleOptions { SymbolSize = 240 };
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        store.Offer(Bulletins.Make(81, 1500), day);
        var slot = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        var plan = new StoreSlotPlanner(store, Compression.Default, options).Plan(slot);

        string wav = Path.Combine(output.Path, "wn3.wav");
        var settings = new SlotSettings { ToneLength = TimeSpan.Zero, PauseAfterTone = TimeSpan.Zero };
        new WavRenderer(settings, "ms110d-wn3", 48000).Render(plan.Frames, wav, slot);

        var heard = new List<byte[]>();
        IModem modem = ModemCatalog.Create("ms110d-wn4", 48000, heard.Add);
        var (audio, _) = WavFile.ReadMono(wav, 0);
        for (int i = 0; i < audio.Length; i += 4800)
        {
            modem.Process(audio.AsSpan(i, Math.Min(4800, audio.Length - i)));
        }
        modem.Process(new float[48000]);
        Assert.Equal(plan.Frames.Count, heard.Count);
        Assert.Equal(plan.Frames.Select(f => f.Payload), heard.Select(h => h[MailcastFrame.Ax25UiOverhead..]));
    }
}
