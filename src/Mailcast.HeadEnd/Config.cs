using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailcast.Core;
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
        SubChannel = Station.SubChannel,
        LeaseLength = TimeSpan.FromSeconds(Station.LeaseSeconds),
        RenewEvery = TimeSpan.FromSeconds(Station.RenewSeconds),
        ChannelWait = TimeSpan.FromSeconds(Slot.ChannelWaitSeconds),
        WhenStillBusy = Slot.WhenStillBusy,
        ToneLength = TimeSpan.FromSeconds(Slot.ToneSeconds),
        ToneHz = Slot.ToneHz,
        PauseAfterTone = TimeSpan.FromSeconds(Slot.PauseAfterToneSeconds),
        MaxBurst = TimeSpan.FromSeconds(Station.MaxBurstSeconds),
        FramesPerBurst = Station.FramesPerBurst,
        BurstGap = TimeSpan.FromSeconds(Slot.BurstGapSeconds),
        AckGrace = TimeSpan.FromSeconds(Station.AckGraceSeconds),
        MaxSlotLength = TimeSpan.FromMinutes(Slot.MaxMinutes),
        PaTemperatureLimitC = Flex.PaTemperatureLimitC,
        WhenFlexUnreachable = Flex.WhenUnreachable,
    };

    /// <summary>The scheduler's options.</summary>
    public ScheduleOptions ToScheduleOptions()
    {
        var defaults = new ScheduleOptions();
        return defaults with
        {
            DaysCarried = Schedule.DaysCarried ?? defaults.DaysCarried,
            TotalOverhead = Schedule.TotalOverhead ?? defaults.TotalOverhead,
            ExtraSymbols = Schedule.ExtraSymbols ?? defaults.ExtraSymbols,
            DayShares = Schedule.DayShares ?? defaults.DayShares,
            DirectoryEvery = Schedule.DirectoryEvery ?? defaults.DirectoryEvery,
            RememberDays = Schedule.RememberDays ?? defaults.RememberDays,
            MaxBulletinSize = Intake.MaxBulletinBytes,
        };
    }

    /// <summary>The slot's start time of day, UTC.</summary>
    public TimeOnly SlotTime => TimeOnly.ParseExact(Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture);

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
        if (Slot.MaxMinutes is <= 0 or > 180)
        {
            problems.Add("\"slot\".\"maxMinutes\" must be above 0 and at most 180");
        }
        if (Slot.ToneSeconds is < 0 or > 60)
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
}

public sealed record SlotConfig
{
    /// <summary>The slot's start, UTC, HH:mm.</summary>
    public string TimeUtc { get; init; } = "12:00";

    /// <summary>A head end started this many minutes late still runs today's slot.</summary>
    public int CatchUpMinutes { get; init; } = 30;

    /// <summary>The hard stop.</summary>
    public double MaxMinutes { get; init; } = 40;

    public double ChannelWaitSeconds { get; init; } = 120;

    public BusyPolicy WhenStillBusy { get; init; } = BusyPolicy.Go;

    public double ToneSeconds { get; init; } = 30;

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

    public double RenewSeconds { get; init; } = 30;

    public double AckGraceSeconds { get; init; } = 120;
}

public sealed record FlexConfig
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 4992;

    public double PaTemperatureLimitC { get; init; } = 70;

    public FlexUnreachablePolicy WhenUnreachable { get; init; } = FlexUnreachablePolicy.CarryOn;
}

/// <summary>Overrides of the scheduler's options; each left out keeps the core's default.</summary>
public sealed record ScheduleConfig
{
    public int? DaysCarried { get; init; }

    public double? TotalOverhead { get; init; }

    public int? ExtraSymbols { get; init; }

    public IReadOnlyList<double>? DayShares { get; init; }

    public int? DirectoryEvery { get; init; }

    public int? RememberDays { get; init; }
}

public sealed record IntakeConfig
{
    /// <summary>A directory of bulletin files, checked every minute. Empty for none.</summary>
    public string DropDirectory { get; init; } = "/var/lib/pdn-mailcast-headend/drop";

    public int MaxBulletinBytes { get; init; } = 32 * 1024;

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

/// <summary>A configuration the head end will not run with.</summary>
public sealed class ConfigException(string message) : Exception(message);
