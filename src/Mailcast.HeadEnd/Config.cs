using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.Mailcast;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;

namespace Mailcast.HeadEnd;

/// <summary>
/// The head end's configuration, <c>/etc/pdn-mailcast-headend/headend.json</c>. Every key but the
/// station's API key has a default; see <c>packaging/headend/headend.example.json</c>.
/// </summary>
public sealed record HeadEndConfig
{
    /// <summary>The AX.25 source of every frame.</summary>
    public string Callsign { get; init; } = "GB7RDG";

    /// <summary>The AX.25 destination of every frame.</summary>
    public string Destination { get; init; } = "MCAST";

    /// <summary>Where the bulletins, the ledger and the last slot's report are kept.</summary>
    public string StateDirectory { get; init; } = "/var/lib/pdn-mailcast-headend";

    public SlotConfig Slot { get; init; } = new();

    public StationConfig Station { get; init; } = new();

    public FlexConfig Flex { get; init; } = new();

    public ScheduleConfig Schedule { get; init; } = new();

    public IntakeConfig Intake { get; init; } = new();

    public StatusConfig Status { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads a file. Throws <see cref="ConfigException"/> with a sentence an operator can act on.</summary>
    public static HeadEndConfig Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"cannot read {path}: {e.Message}");
        }
        return Parse(text, path);
    }

    /// <summary>Reads configuration text.</summary>
    public static HeadEndConfig Parse(string json, string source = "the configuration")
    {
        HeadEndConfig config;
        try
        {
            config = JsonSerializer.Deserialize<HeadEndConfig>(json, Options) ?? new HeadEndConfig();
        }
        catch (JsonException e)
        {
            throw new ConfigException($"{source}: {e.Message}");
        }
        config.Validate(source);
        return config;
    }

    /// <summary>The settings the slot runs with.</summary>
    public SlotSettings ToSlotSettings() => new()
    {
        Callsign = Callsign,
        Destination = Destination,
        Mode = Station.Mode,
        SubChannel = Station.SubChannel,
        LeaseLength = TimeSpan.FromSeconds(Station.LeaseSeconds),
        MaxCarrierWait = TimeSpan.FromSeconds(Station.MaxCarrierWaitSeconds),
        RenewEvery = TimeSpan.FromSeconds(Station.RenewSeconds),
        ChannelWait = TimeSpan.FromSeconds(Slot.ChannelWaitSeconds),
        RequireClockSync = Slot.RequireClockSync,
        WhenStillBusy = Slot.WhenStillBusy,
        ToneLength = TimeSpan.FromSeconds(Slot.Tone),
        ToneHz = Slot.ToneHz,
        PauseAfterTone = TimeSpan.FromSeconds(Slot.PauseAfterToneSeconds),
        MaxBurst = TimeSpan.FromSeconds(Station.MaxBurstSeconds),
        FramesPerBurst = Station.FramesPerBurst,
        BurstGap = TimeSpan.FromSeconds(Slot.BurstGapSeconds),
        AckGrace = TimeSpan.FromSeconds(Station.AckGraceSeconds),
        MaxSlotLength = TimeSpan.FromMinutes(Slot.Max),
        PaTemperatureLimitC = Flex.PaTemperatureLimitC,
        WhenFlexUnreachable = Flex.WhenUnreachable,
    };

    /// <summary>
    /// How bulletins are carried: <see cref="ScheduleConfig.Rule"/> when given; otherwise the
    /// shares rule for a daily station or a configuration that sets its keys, and the budget rule
    /// for any other.
    /// </summary>
    public ScheduleRule Rule => Schedule.Rule
        ?? (Slot.EveryMinutes == MinutesPerDay || Schedule.UsesShareKeys ? ScheduleRule.Shares : ScheduleRule.Budget);

    /// <summary>
    /// What the budget rule fills each slot to, tone and idents included, when it starts on time:
    /// <see cref="FillLimitAfter"/> with no lateness.
    /// </summary>
    public TimeSpan FillLimit => FillLimitAfter(TimeSpan.Zero);

    /// <summary>
    /// What the budget rule fills a slot to when it starts <paramref name="late"/> after its time
    /// (a catch-up or a retry): <c>schedule.budgetMinutes</c> (<c>slot.maxMinutes</c> if left out),
    /// but never more than <c>slot.maxMinutes</c> less the lateness and <see cref="Margin"/>, so a
    /// slot that waits its longest for a clear channel still ends before the hard stop, which counts
    /// from the slot's own time. Zero or less fills nothing.
    /// </summary>
    public TimeSpan FillLimitAfter(TimeSpan late)
    {
        TimeSpan budget = TimeSpan.FromMinutes(Schedule.BudgetMinutes ?? Slot.Max);
        TimeSpan most = TimeSpan.FromMinutes(Slot.Max) - (late > TimeSpan.Zero ? late : TimeSpan.Zero) - Margin;
        return budget < most ? budget : most;
    }

    /// <summary>
    /// Time kept back from the hard stop: <c>schedule.marginSeconds</c>, or left out the longest the
    /// slot waits for a clear channel before its tone (<c>slot.channelWaitSeconds</c>; none without a
    /// tone), and always the room the runner's hard-stop check needs for the last burst: the modem's
    /// gather and one carrier wait (<c>station.maxCarrierWaitSeconds</c>).
    /// </summary>
    public TimeSpan Margin =>
        TimeSpan.FromSeconds(Schedule.MarginSeconds ?? (Slot.Tone > 0 ? Slot.ChannelWaitSeconds : 0))
        + SlotAirtime.Gather + TimeSpan.FromSeconds(Station.MaxCarrierWaitSeconds);

    /// <summary>The waveforms the slots take in turn: <c>schedule.modes</c>, or the broadcast modem's own mode alone.</summary>
    public IReadOnlyList<string> Modes => Schedule.Modes ?? [Station.Mode];

    /// <summary>Which waveform each slot uses. With <c>schedule.modes</c> set, each slot switches the modem to its own.</summary>
    public Waveforms ToWaveforms(Func<string, IAirtime> measure) =>
        new(Modes, Schedule.Modes is not null, ToSlotTimetable(), measure);

    /// <summary>
    /// The scheduler's options. A daily station's defaults are the core's; any shorter interval
    /// starts from <see cref="ScheduleOptions.Hourly"/>, its offsets kept in hours. The daily
    /// station's old keys (<c>daysCarried</c>, <c>totalOverhead</c>, <c>dayShares</c>) still work:
    /// each day's share of the total becomes a slot share, the carryings a day apart.
    /// </summary>
    public ScheduleOptions ToScheduleOptions()
    {
        int every = Slot.EveryMinutes;
        ScheduleOptions defaults = every == MinutesPerDay ? new ScheduleOptions()
            : HourlyScaledTo(Slot.Daylight is null ? ScheduleOptions.Hourly : ScheduleOptions.HourlyDaylight, every);
        IReadOnlyList<double> shares = Schedule.SlotShares ?? defaults.SlotShares;
        IReadOnlyList<int> offsets = Schedule.SlotOffsets ?? defaults.SlotOffsets;
        if (Schedule.SlotShares is not null && Schedule.SlotOffsets is null && shares.Count != offsets.Count)
        {
            // New shares without offsets: carried in that many slots, spread as evenly as the
            // default's span allows.
            int span = Math.Max(defaults.SlotOffsets[^1], shares.Count - 1);
            offsets = [.. Enumerable.Range(0, shares.Count).Select(c => shares.Count == 1 ? 0 : (int)Math.Round(c * span / (double)(shares.Count - 1)))];
        }
        int carryOver = Schedule.CarryOverSlots ?? defaults.CarryOverSlots;
        if (Schedule.UsesDailyKeys)
        {
            int days = Schedule.DaysCarried ?? 3;
            double overhead = Schedule.TotalOverhead ?? 2.0;
            IReadOnlyList<double> dayShares = Schedule.DayShares ?? (days == 3 ? [0.7, 0.15, 0.15] : [.. Enumerable.Repeat(1.0 / days, days)]);
            shares = [.. dayShares.Select(d => d * overhead)];
            offsets = [.. Enumerable.Range(0, days).Select(d => d * (MinutesPerDay / every))];
            carryOver = 0;
        }
        return defaults with
        {
            SlotMinutes = every,
            SlotShares = shares,
            SlotOffsets = offsets,
            CarryOverSlots = carryOver,
            ExtraSymbols = Schedule.ExtraSymbols ?? defaults.ExtraSymbols,
            DirectoryEvery = Schedule.DirectoryEvery ?? defaults.DirectoryEvery,
            RememberDays = Schedule.RememberDays ?? defaults.RememberDays,
            SymbolSize = Schedule.SymbolSize ?? defaults.SymbolSize,
            MaxBulletinSize = Intake.MaxBulletinBytes,
            Timetable = TimetableIfValid(),
            Budget = Rule == ScheduleRule.Budget
                ? new BudgetRule
                {
                    SlotCap = Schedule.SlotCap ?? 0.6,
                    SpreadSlots = Schedule.SpreadSlots ?? 3,
                    RetireCoverage = Schedule.RetireCoverage ?? 6.0,
                    RetireAfter = TimeSpan.FromHours(Schedule.RetireHours is > 0 and < 100_000 ? Schedule.RetireHours.Value : 36),
                }
                : null,
        };
    }

    /// <summary>The hourly defaults for another interval: the same hours, in that interval's slots.</summary>
    private static ScheduleOptions HourlyScaledTo(ScheduleOptions hourly, int every)
    {
        var offsets = new List<int>();
        foreach (int hours in hourly.SlotOffsets)
        {
            int slot = (int)Math.Round(hours * 60.0 / every);
            offsets.Add(offsets.Count == 0 ? 0 : Math.Max(slot, offsets[^1] + 1));
        }
        return hourly with
        {
            SlotMinutes = every,
            SlotOffsets = offsets,
            CarryOverSlots = (int)Math.Round(hourly.CarryOverSlots * 60.0 / every),
        };
    }

    /// <summary>The first slot's time of day, UTC, which the others are counted from.</summary>
    public TimeOnly SlotTime => TimeOnly.ParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>When the slots are.</summary>
    public Service.SlotSchedule ToSlotSchedule() => new(SlotTime, TimeSpan.FromMinutes(Slot.EveryMinutes), Slot.Daylight?.ToRule());

    /// <summary>The timetable, or null while validation still has something to say about it.</summary>
    private SlotTimetable? TimetableIfValid()
    {
        if (!TimeOnly.TryParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || Slot.EveryMinutes < 1 || MinutesPerDay % Slot.EveryMinutes != 0)
        {
            return null;
        }
        try
        {
            return ToSlotTimetable();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>When the slots are, as the directory carries them.</summary>
    public SlotTimetable ToSlotTimetable() => new(SlotTime, Slot.EveryMinutes, Slot.Daylight?.ToRule());

    private const int MinutesPerDay = 1440;

    private void Validate(string source)
    {
        var problems = new List<string>();
        if (!Ax25Ui.IsValidAddress(Callsign))
        {
            problems.Add($"\"callsign\": '{Callsign}' is not an AX.25 callsign");
        }
        if (!Ax25Ui.IsValidAddress(Destination))
        {
            problems.Add($"\"destination\": '{Destination}' is not an AX.25 address");
        }
        if (!TimeOnly.TryParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            problems.Add($"\"slot\".\"timeUtc\": '{Slot.TimeUtc}' is not HH:mm");
        }
        if (Slot.Max is <= 0 or > 180)
        {
            problems.Add("\"slot\".\"maxMinutes\" must be above 0 and at most 180");
        }
        bool everyFine = Slot.EveryMinutes >= 15 && Slot.EveryMinutes <= MinutesPerDay && MinutesPerDay % Slot.EveryMinutes == 0;
        if (!everyFine)
        {
            problems.Add("\"slot\".\"everyMinutes\" must be at least 15 and divide 1440 (a day): 15, 20, 30, 60, 120, 180, 240, 360, 480, 720 or 1440");
        }
        else
        {
            if (Slot.Max > Slot.EveryMinutes)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"\"slot\".\"maxMinutes\" {Slot.Max} is longer than the {Slot.EveryMinutes} minutes between slots"));
            }
            if (Slot.CatchUp < 0 || Slot.CatchUp >= Slot.EveryMinutes)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"\"slot\".\"catchUpMinutes\" must be 0 or more and less than the {Slot.EveryMinutes} minutes between slots, so a late slot never meets the next"));
            }
        }
        bool daylightFine = true;
        if (Slot.Daylight is { } daylight && TimeOnly.TryParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor)
            && daylight.Problem(everyFine ? Slot.EveryMinutes : 0, anchor) is { } daylightProblem)
        {
            problems.Add($"\"slot\".\"daylight\": {daylightProblem}");
            daylightFine = false;
        }
        ValidateSchedule(problems, everyFine && daylightFine && TimeOnly.TryParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        if (Slot.Tone is < 0 or > 60)
        {
            problems.Add("\"slot\".\"toneSeconds\" must be 0 to 60 (the station caps a test at 60 s, 30 s unless its txTest.maxSeconds says more)");
        }
        if (Station.SubChannel is < 0 or > 15)
        {
            problems.Add("\"station\".\"subChannel\" must be 0 to 15");
        }
        if (Station.KissPortNibble is < 0 or > 15)
        {
            problems.Add("\"station\".\"kissPortNibble\" must be 0 to 15");
        }
        if (Station.MaxBurstSeconds is < 1 or > 120)
        {
            problems.Add("\"station\".\"maxBurstSeconds\" must be 1 to 120, as on the modem entry");
        }
        if (Station.FramesPerBurst is <= 0)
        {
            problems.Add("\"station\".\"framesPerBurst\" must be above 0, or left out");
        }
        if (Station.RenewSeconds <= 0 || Station.LeaseSeconds > 300)
        {
            problems.Add("\"station\": \"renewSeconds\" must be above 0 and \"leaseSeconds\" at most 300 (the station's cap)");
        }
        // A burst queued just after a renewal must finish inside the lease even if the next renewal
        // fails, so the lease must outlast a renewal interval plus a burst plus the margin.
        double needed = Station.RenewSeconds + Station.MaxBurstSeconds + 15;
        if (Station.LeaseSeconds < needed)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"\"station\".\"leaseSeconds\" {Station.LeaseSeconds} is too short: a {Station.MaxBurstSeconds} s burst queued after a renewal must finish inside the lease even if the next renewal {Station.RenewSeconds} s later fails, which needs at least {needed} s"));
        }
        if (Station.MaxCarrierWaitSeconds is < 1 or > SlotSettingsDefaults.LeaseMarginSeconds)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"\"station\".\"maxCarrierWaitSeconds\" must be 1 to {SlotSettingsDefaults.LeaseMarginSeconds}: a burst that waits its longest for a clear channel must still end inside the lease"));
        }
        if (Slot.RetryMinutes <= 0)
        {
            problems.Add("\"slot\".\"retryMinutes\" must be above 0");
        }
        if (Flex.PaStaleSeconds <= 0)
        {
            problems.Add("\"flex\".\"paStaleSeconds\" must be above 0");
        }
        if (Intake.PreSlotSeconds <= 0)
        {
            problems.Add("\"intake\".\"preSlotSeconds\" must be above 0");
        }
        if (string.IsNullOrWhiteSpace(Station.ApiKey))
        {
            problems.Add("\"station\".\"apiKey\" is required: the station's api.key, which the lease and the tone need");
        }
        if (!Uri.TryCreate(Station.ApiUrl, UriKind.Absolute, out _))
        {
            problems.Add($"\"station\".\"apiUrl\": '{Station.ApiUrl}' is not a URL");
        }
        if (Flex.Enabled && string.IsNullOrWhiteSpace(Flex.Host))
        {
            problems.Add("\"flex\".\"host\" is required when \"flex\".\"enabled\" is true");
        }
        if (Schedule.SymbolSize is int symbolSize && (symbolSize < 64 || symbolSize > Packet.Mailcast.MailcastFrame.StandardSymbolSize || symbolSize % Packet.Mailcast.MailcastFrame.StandardAlignment != 0))
        {
            problems.Add("\"schedule\".\"symbolSize\" must be 64 to 940 and a multiple of 4");
        }
        if (Intake.MaxBulletinBytes <= 0)
        {
            problems.Add("\"intake\".\"maxBulletinBytes\" must be above 0");
        }
        if (Intake.Fbb is { } fbb)
        {
            if (string.IsNullOrWhiteSpace(fbb.Host) || fbb.Port is <= 0 or > 65535)
            {
                problems.Add("\"intake\".\"fbb\" needs a \"host\" and a \"port\"");
            }
            if (!Ax25Ui.IsValidAddress(fbb.PartnerCallsign))
            {
                problems.Add($"\"intake\".\"fbb\".\"partnerCallsign\": '{fbb.PartnerCallsign}' is not a callsign");
            }
            if (fbb.PollMinutes <= 0)
            {
                problems.Add("\"intake\".\"fbb\".\"pollMinutes\" must be above 0");
            }
        }
        if (problems.Count > 0)
        {
            throw new ConfigException($"{source}: {string.Join("; ", problems)}");
        }
    }

    private void ValidateSchedule(List<string> problems, bool everyFine)
    {
        if (Schedule.Rule == ScheduleRule.Budget && Schedule.UsesShareKeys)
        {
            problems.Add("\"schedule\": \"rule\" is \"budget\", which fills each slot to its airtime budget; \"slotShares\", \"slotOffsets\", \"carryOverSlots\" and the daily keys belong to the \"shares\" rule");
            return;
        }
        if (Rule == ScheduleRule.Shares && Schedule.UsesBudgetKeys)
        {
            problems.Add("\"schedule\": \"budgetMinutes\", \"slotCap\", \"spreadSlots\", \"retireCoverage\", \"retireHours\" and \"marginSeconds\" belong to the \"budget\" rule; set \"rule\": \"budget\" to use them");
            return;
        }
        if (Schedule.BudgetMinutes is { } budgetMinutes && !(budgetMinutes > 0 && budgetMinutes <= 180))
        {
            problems.Add("\"schedule\".\"budgetMinutes\" must be above 0 and at most 180");
            return;
        }
        if (Schedule.MarginSeconds is { } margin && !(margin >= 0 && margin < 10_800))
        {
            problems.Add("\"schedule\".\"marginSeconds\" must be 0 or more");
            return;
        }
        if (Schedule.SlotCap is { } cap && !(cap > 0 && cap <= 100))
        {
            problems.Add("\"schedule\".\"slotCap\" must be above 0 (a multiple of a bulletin's K)");
            return;
        }
        if (Schedule.SpreadSlots is < 1 or > 100)
        {
            problems.Add("\"schedule\".\"spreadSlots\" must be 1 to 100");
            return;
        }
        if (Schedule.RetireCoverage is { } coverage && !(coverage > 0 && coverage <= 1000))
        {
            problems.Add("\"schedule\".\"retireCoverage\" must be above 0 (a multiple of a bulletin's K)");
            return;
        }
        if (Schedule.RetireHours is { } hours && !(hours > 0 && hours < 100_000))
        {
            problems.Add("\"schedule\".\"retireHours\" must be above 0");
            return;
        }
        if (Rule == ScheduleRule.Budget && Slot.Max is > 0 and <= 180 && FillLimit <= TimeSpan.Zero)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"\"schedule\": nothing would fit in a slot: \"slot\".\"maxMinutes\" {Slot.Max} less the {Margin.TotalSeconds:0} s margin (\"schedule\".\"marginSeconds\", or \"slot\".\"channelWaitSeconds\" if left out) leaves no time"));
            return;
        }
        if (Schedule.Modes is { } modes)
        {
            if (modes.Count == 0)
            {
                problems.Add("\"schedule\".\"modes\" must list at least one mode, or be left out");
                return;
            }
            if (modes.FirstOrDefault(m => Waveforms.SetHardwarePayload(m) is null) is { } bad)
            {
                problems.Add($"\"schedule\".\"modes\": '{bad}' is not an MS110D waveform the modem can switch to (ms110d-wn0 to ms110d-wn8, or ms110d-wn13)");
                return;
            }
            if (Waveforms.SetHardwarePayload(Station.Mode) is null)
            {
                problems.Add($"\"schedule\".\"modes\" needs an MS110D broadcast modem, and \"station\".\"mode\" is '{Station.Mode}'");
                return;
            }
        }
        if (Schedule.UsesDailyKeys)
        {
            if (Schedule.SlotShares is not null || Schedule.SlotOffsets is not null || Schedule.CarryOverSlots is not null)
            {
                problems.Add("\"schedule\": use either \"slotShares\" and \"slotOffsets\" or the daily station's \"daysCarried\", \"totalOverhead\" and \"dayShares\", not both");
                return;
            }
            int days = Schedule.DaysCarried ?? 3;
            if (days < 1)
            {
                problems.Add("\"schedule\".\"daysCarried\" must be at least 1");
                return;
            }
            if (Schedule.DayShares is { } d && (d.Count != days || d.Any(x => x < 0) || Math.Abs(d.Sum() - 1) > 1e-9))
            {
                problems.Add("\"schedule\".\"dayShares\" must have one share for each day carried, none negative, summing to 1");
                return;
            }
            if (Schedule.TotalOverhead is < 1)
            {
                problems.Add("\"schedule\".\"totalOverhead\" must be at least 1");
                return;
            }
        }
        if (Schedule.SlotShares is { } shares && (shares.Count == 0 || shares.Any(x => x < 0 || double.IsNaN(x)) || shares[0] <= 0))
        {
            problems.Add("\"schedule\".\"slotShares\" must have at least one entry, none negative and the first above 0");
            return;
        }
        if (Schedule.SlotOffsets is { } offsets)
        {
            int count = Schedule.SlotShares?.Count ?? ToScheduleOptions().SlotShares.Count;
            if (offsets.Count != count || offsets.Count == 0 || offsets[0] != 0 || offsets.Zip(offsets.Skip(1)).Any(p => p.Second <= p.First))
            {
                problems.Add("\"schedule\".\"slotOffsets\" must have one entry for each of \"slotShares\", the first 0 and each later one above the one before");
                return;
            }
        }
        if (Schedule.CarryOverSlots is < 0)
        {
            problems.Add("\"schedule\".\"carryOverSlots\" must be 0 or more");
            return;
        }
        if (Schedule.ExtraSymbols is < 0)
        {
            problems.Add("\"schedule\".\"extraSymbols\" must be 0 or more");
            return;
        }
        if (Schedule.DirectoryEvery is < 2)
        {
            problems.Add("\"schedule\".\"directoryEvery\" must be at least 2");
            return;
        }
        if (everyFine)
        {
            try
            {
                BroadcastScheduler.Validate(ToScheduleOptions());
            }
            catch (ArgumentException e)
            {
                problems.Add($"\"schedule\": {e.Message}");
            }
        }
    }
}

public sealed record SlotConfig
{
    /// <summary>A slot's start, UTC, HH:mm: the first of the day's slots, or the only one for a daily station.</summary>
    public string TimeUtc { get; init; } = "12:00";

    /// <summary>Minutes from one slot to the next, at least 15 and dividing a day: 1440 is daily, 60 hourly.</summary>
    public int EveryMinutes { get; init; } = 1440;

    /// <summary>A head end started this many minutes late still runs the slot it missed. Left out, 30 for a daily station and 5 otherwise.</summary>
    public int? CatchUpMinutes { get; init; }

    /// <summary><see cref="CatchUpMinutes"/>, or its default for the interval.</summary>
    [JsonIgnore]
    public int CatchUp => CatchUpMinutes ?? (EveryMinutes == 1440 ? 30 : 5);

    /// <summary>A slot skipped for a reason at the station is tried again after this long, within the catch-up window.</summary>
    public double RetryMinutes { get; init; } = 5;

    /// <summary>
    /// The hard stop, from the slot's start. Left out, 40 for a daily station and 10 otherwise,
    /// so an hourly slot ends while a web SDR receiver, which listens to 12 minutes past, is
    /// still there.
    /// </summary>
    public double? MaxMinutes { get; init; }

    /// <summary><see cref="MaxMinutes"/>, or its default for the interval.</summary>
    [JsonIgnore]
    public double Max => MaxMinutes ?? (EveryMinutes == 1440 ? 40 : 10);

    /// <summary>
    /// Send only in daylight: a slot runs only if it starts between
    /// <see cref="DaylightSettings.AfterSunriseMinutes"/> after sunrise and
    /// <see cref="DaylightSettings.BeforeSunsetMinutes"/> before sunset at the locator. Left out,
    /// every slot runs. A slot on demand (<c>--run-now</c>) runs whatever the time.
    /// </summary>
    public DaylightSettings? Daylight { get; init; }

    /// <summary>Key nothing until the kernel says the clock is synchronised.</summary>
    public bool RequireClockSync { get; init; } = true;

    public double ChannelWaitSeconds { get; init; } = 120;

    public BusyPolicy WhenStillBusy { get; init; } = BusyPolicy.Go;

    /// <summary>The calibration tone. Left out, 30 s for a daily station and 10 s otherwise, which receivers expect from an hourly one.</summary>
    public double? ToneSeconds { get; init; }

    /// <summary><see cref="ToneSeconds"/>, or its default for the interval.</summary>
    [JsonIgnore]
    public double Tone => ToneSeconds ?? (EveryMinutes == 1440 ? 30 : 10);

    public double ToneHz { get; init; } = 1800;

    public double PauseAfterToneSeconds { get; init; } = 8;

    public double BurstGapSeconds { get; init; } = 1;
}

public sealed record StationConfig
{
    /// <summary>The station page's address, where its API is.</summary>
    public string ApiUrl { get; init; } = "http://127.0.0.1:8107/";

    /// <summary>The station's <c>api.key</c>.</summary>
    public string ApiKey { get; init; } = "";

    public string KissHost { get; init; } = "127.0.0.1";

    /// <summary>The broadcast modem's own KISS port (<c>modems[].port</c>), or the shared one.</summary>
    public int KissPort { get; init; } = 8112;

    /// <summary>The KISS port nibble to write: 0 on a per-modem port, the sub-channel on the shared one.</summary>
    public int KissPortNibble { get; init; }

    /// <summary>The broadcast modem's <c>subChannel</c>.</summary>
    public int SubChannel { get; init; } = 4;

    /// <summary>The broadcast modem's mode, for the airtime model.</summary>
    public string Mode { get; init; } = "ms110d-wn4";

    /// <summary>The modem entry's <c>maxBurstSeconds</c>.</summary>
    public double MaxBurstSeconds { get; init; } = 60;

    /// <summary>Frames per burst; left out, worked out from the mode.</summary>
    public int? FramesPerBurst { get; init; }

    public double LeaseSeconds { get; init; } = 120;

    /// <summary>The lease's maxCarrierWaitSeconds: frames that have waited this long for a clear channel go anyway.</summary>
    public double MaxCarrierWaitSeconds { get; init; } = 10;

    public double RenewSeconds { get; init; } = 30;

    public double AckGraceSeconds { get; init; } = 120;
}

public sealed record FlexConfig
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 4992;

    public double PaTemperatureLimitC { get; init; } = 70;

    /// <summary>A PA temperature older than this counts as no reading.</summary>
    public double PaStaleSeconds { get; init; } = 15;

    public FlexUnreachablePolicy WhenUnreachable { get; init; } = FlexUnreachablePolicy.CarryOn;
}

/// <summary>Overrides of the scheduler's options; each left out keeps the default for the slot interval.</summary>
public sealed record ScheduleConfig
{
    /// <summary>
    /// Symbols for each carrying of a bulletin, as multiples of its K, the first for the slot after
    /// it is taken in. As many entries as carryings.
    /// </summary>
    public IReadOnlyList<double>? SlotShares { get; init; }

    /// <summary>Which slot each carrying is in, counted from the first: 0, then rising. One for each of <see cref="SlotShares"/>.</summary>
    public IReadOnlyList<int>? SlotOffsets { get; init; }

    /// <summary>Slots after the last carrying in which a bulletin may still make up pieces a skipped or cut-short slot did not send.</summary>
    public int? CarryOverSlots { get; init; }

    /// <summary>Spare symbols on top of the first carrying's share.</summary>
    public int? ExtraSymbols { get; init; }

    /// <summary>A daily station's old setting: how many days running a bulletin is carried. Use <see cref="SlotShares"/> instead.</summary>
    public int? DaysCarried { get; init; }

    /// <summary>A daily station's old setting: symbols over all days, as a multiple of K. Use <see cref="SlotShares"/> instead.</summary>
    public double? TotalOverhead { get; init; }

    /// <summary>A daily station's old setting: each day's fraction of the total, summing to 1. Use <see cref="SlotShares"/> instead.</summary>
    public IReadOnlyList<double>? DayShares { get; init; }

    /// <summary>Whether any of the daily station's old keys is set.</summary>
    [JsonIgnore]
    public bool UsesDailyKeys => DaysCarried is not null || TotalOverhead is not null || DayShares is not null;

    /// <summary>Whether any key of the shares rule is set, the daily station's old keys included.</summary>
    [JsonIgnore]
    public bool UsesShareKeys => SlotShares is not null || SlotOffsets is not null || CarryOverSlots is not null || UsesDailyKeys;

    /// <summary>Whether any key of the budget rule is set.</summary>
    [JsonIgnore]
    public bool UsesBudgetKeys => BudgetMinutes is not null || SlotCap is not null || SpreadSlots is not null || RetireCoverage is not null || RetireHours is not null || MarginSeconds is not null;

    /// <summary>
    /// How bulletins are carried: <c>budget</c> fills each slot to its airtime budget, <c>shares</c>
    /// sends fixed shares in fixed slots. Left out, <c>shares</c> for a daily station or when its keys
    /// are set, and <c>budget</c> otherwise.
    /// </summary>
    public ScheduleRule? Rule { get; init; }

    /// <summary>The budget rule's airtime budget for a slot, tone and idents included. Left out, <c>slot.maxMinutes</c>.</summary>
    public double? BudgetMinutes { get; init; }

    /// <summary>Time kept back from <c>slot.maxMinutes</c>. Left out, <c>slot.channelWaitSeconds</c> (none without a tone).</summary>
    public double? MarginSeconds { get; init; }

    /// <summary>The most of one bulletin a slot carries, as a multiple of its K. Left out, 0.6.</summary>
    public double? SlotCap { get; init; }

    /// <summary>Each slot first gives every bulletin in rotation enough that any this many slots rebuild it. Left out, 3.</summary>
    public int? SpreadSlots { get; init; }

    /// <summary>A bulletin retires once this many K of it have been sent. Left out, 6.</summary>
    public double? RetireCoverage { get; init; }

    /// <summary>A bulletin retires this many hours after its first slot. Left out, 36.</summary>
    public double? RetireHours { get; init; }

    /// <summary>
    /// The waveforms the slots take in turn, as pdn-soundmodem names them (<c>ms110d-wn4</c>,
    /// <c>ms110d-wn3</c>). Each slot switches the broadcast modem to its own with KISS SETHW. Left
    /// out, every slot goes on <c>station.mode</c> and the modem is left as it is.
    /// </summary>
    public IReadOnlyList<string>? Modes { get; init; }

    public int? DirectoryEvery { get; init; }

    public int? RememberDays { get; init; }

    /// <summary>
    /// RaptorQ symbol size in bytes, so the on-air frame size: 64 to 940, a multiple of 4. Smaller
    /// frames survive fades and collisions better at the cost of more header per byte. Receivers
    /// read it from each frame, so it can change at any time; a bulletin already held keeps the
    /// size it was first encoded with.
    /// </summary>
    public int? SymbolSize { get; init; }
}

public sealed record IntakeConfig
{
    /// <summary>A directory of bulletin files, checked every minute. Empty for none.</summary>
    public string DropDirectory { get; init; } = "/var/lib/pdn-mailcast-headend/drop";

    public int MaxBulletinBytes { get; init; } = 32 * 1024;

    /// <summary>The longest the collection just before a slot may take, so a BBS that does not answer cannot hold the slot up.</summary>
    public double PreSlotSeconds { get; init; } = 30;

    /// <summary>Forwarding from the BBS. Left out, the file drop is the only source.</summary>
    public FbbIntakeConfig? Fbb { get; init; }
}

/// <summary>One step of logging in to the BBS: wait for some text, then send a line.</summary>
public sealed record LoginStep(string? Expect, string? Send);

public sealed record FbbIntakeConfig
{
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The BBS's forwarding port: LinBPQ's FBBPORT, or pdn-bbs's fbbTcp port.</summary>
    public int Port { get; init; } = 8011;

    /// <summary>
    /// The callsign the head end is known by as a forwarding partner. A Q callsign is never issued,
    /// so it cannot clash with a station, and FBB-style BBSs only take logins shaped like a callsign.
    /// </summary>
    public string PartnerCallsign { get; init; } = "Q0HEAD";

    /// <summary>
    /// The login, in order: wait for <see cref="LoginStep.Expect"/> (when given), then send the line.
    /// <c>{user}</c>, <c>{password}</c> and <c>{call}</c> are filled in. The default is LinBPQ's
    /// FBBPORT, which sends no prompts: the user, the password, then the node command for the
    /// mail application.
    /// </summary>
    public IReadOnlyList<LoginStep> Login { get; init; } = LinBpqFbbPort;

    /// <summary>LinBPQ's FBBPORT: no prompts; user, password, <c>BBS</c>.</summary>
    public static IReadOnlyList<LoginStep> LinBpqFbbPort { get; } =
        [new(null, "{user}"), new(null, "{password}"), new(null, "BBS")];

    /// <summary>pdn-bbs's fbbTcp listener: it asks for the callsign and nothing else.</summary>
    public static IReadOnlyList<LoginStep> PdnBbsFbbTcp { get; } = [new("Callsign :", "{call}")];

    public string User { get; init; } = "Q0HEAD";

    public string Password { get; init; } = "";

    /// <summary>How often to collect.</summary>
    public double PollMinutes { get; init; } = 30;

    /// <summary>How long one session may take before it is dropped.</summary>
    public double SessionTimeoutSeconds { get; init; } = 300;
}

public sealed record StatusConfig
{
    /// <summary>Where the status endpoint listens; empty turns it off.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8216;
}

/// <summary>How the head end carries bulletins from slot to slot.</summary>
public enum ScheduleRule
{
    /// <summary>Fill each slot to its airtime budget, the least covered bulletins first.</summary>
    Budget,

    /// <summary>Fixed shares of each bulletin in fixed slots after its first, as before the budget rule.</summary>
    Shares,
}

/// <summary>Fixed parts of the slot that the configuration is checked against.</summary>
public static class SlotSettingsDefaults
{
    /// <summary>The slot's <see cref="SlotSettings.LeaseMargin"/>, in seconds.</summary>
    public const int LeaseMarginSeconds = 15;
}

/// <summary>A configuration the head end will not run with.</summary>
public sealed class ConfigException(string message) : Exception(message);
