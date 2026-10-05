using System.Globalization;
using System.Reflection;
using Packet.Mailcast;
using Mailcast.HeadEnd;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Offline;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;
using Mailcast.HeadEnd.Status;

return await Program.MainAsync(args);

/// <summary>The command line.</summary>
public static partial class Program
{
    private const string DefaultConfig = "/etc/pdn-mailcast-headend/headend.json";

    private const string Usage = """
        pdn-mailcast-headend: the GB7RDG mailcast head end.

          pdn-mailcast-headend [--config FILE]
              Runs the service: takes bulletins in, and sends them in each slot.
          pdn-mailcast-headend --plan [--date YYYY-MM-DD] [--time HH:MM] [--config FILE]
              Prints what a slot would send, and changes nothing. The slot is the one running
              at --time (slot.timeUtc if left out) on --date (today if left out).
          pdn-mailcast-headend --wav OUT.wav [--date YYYY-MM-DD] [--time HH:MM] [--bulletins DIR] [--rate HZ] [--config FILE]
              Renders that slot to a WAV file, offline. --bulletins takes a directory of
              bulletin files (the file drop format), all new in that slot; without it the head
              end's own store is used. Opens no connection to anything.
          pdn-mailcast-headend --run-now [--config FILE]
              Asks the running service for a one-off slot now, through its status listener
              (POST /run). It is a whole slot with the usual checks, counted like any other, and
              the schedule carries on afterwards.
          pdn-mailcast-headend --check-config [--config FILE]
          pdn-mailcast-headend --version

        Exit codes: 0 fine, 1 failed, 2 the configuration or the command line is wrong.
        """;

    /// <summary>The version, as built.</summary>
    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static async Task<int> MainAsync(string[] args)
    {
        var options = ParseArguments(args, out string? error);
        if (error is not null)
        {
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: {error}\n\n{Usage}");
            return 2;
        }
        if (options.ContainsKey("--help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (options.ContainsKey("--version"))
        {
            Console.WriteLine($"pdn-mailcast-headend {Version}");
            return 0;
        }

        string configPath = options.GetValueOrDefault("--config") ?? DefaultConfig;
        bool offline = options.ContainsKey("--wav") || options.ContainsKey("--plan");
        HeadEndConfig config;
        try
        {
            // Offline work needs no station, so a missing file is fine there.
            config = offline && !File.Exists(configPath) && !options.ContainsKey("--config")
                ? HeadEndConfig.Parse("""{"station": {"apiKey": "offline"}}""")
                : HeadEndConfig.Load(configPath);
        }
        catch (ConfigException e)
        {
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: {Ascii.Plain(e.Message)}");
            return 2;
        }

        if (options.ContainsKey("--check-config"))
        {
            Console.WriteLine($"{configPath}: fine");
            return 0;
        }

        if (options.ContainsKey("--run-now"))
        {
            return await RunNowAsync(config);
        }

        DateOnly? date = null;
        if (options.TryGetValue("--date", out string? dateText))
        {
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                await Console.Error.WriteLineAsync($"pdn-mailcast-headend: --date '{dateText}' is not YYYY-MM-DD");
                return 2;
            }
            date = parsed;
        }

        TimeOnly at = config.SlotTime;
        if (options.TryGetValue("--time", out string? timeText))
        {
            if (!TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
            {
                await Console.Error.WriteLineAsync($"pdn-mailcast-headend: --time '{timeText}' is not HH:MM");
                return 2;
            }
        }

        var journal = new ConsoleJournal();
        if (offline)
        {
            var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
            DateTimeOffset slot = config.ToSlotSchedule().SlotAtOrBefore(new DateTimeOffset(day.ToDateTime(at, DateTimeKind.Utc)));
            return Offline(config, options, slot, journal);
        }
        return await ServiceAsync(config, journal);
    }

    private static int Offline(HeadEndConfig config, Dictionary<string, string?> options, DateTimeOffset slot, ConsoleJournal journal)
    {
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        // Offline work never touches the head end's own state: it runs on a copy, or on a store
        // made from the files given, in a scratch directory that is removed afterwards.
        string scratch = Directory.CreateTempSubdirectory("mailcast-offline-").FullName;
        try
        {
            ScheduleOptions scheduleOptions = config.ToScheduleOptions();
            if (options.GetValueOrDefault("--bulletins") is string source)
            {
                var fresh = new RotationStore(scratch, Compression.Default, scheduleOptions, journal);
                var intake = new FileDropIntake(
                    CopyOf(source, Path.Combine(scratch, "drop")), fresh,
                    new IntakePolicy(config.Intake.MaxBulletinBytes), journal);
                intake.CollectAsync(day, CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                string held = Path.Combine(config.StateDirectory, "bulletins");
                if (Directory.Exists(held))
                {
                    CopyTree(held, Path.Combine(scratch, "bulletins"));
                }
            }
            var store = new RotationStore(scratch, Compression.Default, scheduleOptions, journal);
            var planner = new StoreSlotPlanner(store, Compression.Default, scheduleOptions);

            SlotPlan plan = planner.Plan(slot);
            SlotSettings settings = config.ToSlotSettings();
            var airtime = LinearAirtime.Measure(config.Station.Mode);
            var runner = new SlotRunner(settings, new NullStation(), new NullKiss(), null, airtime, journal, TimeProvider.System);
            var bursts = runner.BurstSizes(plan.Frames);
            journal.Write(string.Create(CultureInfo.InvariantCulture,
                $"airtime on {config.Station.Mode}: {airtime.PerBurst.TotalSeconds:0.00} s a burst, {airtime.Burst([1003]).TotalSeconds - airtime.PerBurst.TotalSeconds:0.00} s a full frame (1003 octets)"));
            journal.Write(string.Create(CultureInfo.InvariantCulture,
                $"plan {SlotRunner.Name(slot)}: {plan.BulletinsInRotation} bulletins in rotation, {plan.Frames.Count} frames, {bursts.Count} bursts of up to {bursts.DefaultIfEmpty(0).Max()} frames on {config.Station.Mode}, about {runner.Airtime(plan.Frames, bursts).TotalMinutes:0.0} min on the air"));

            if (options.GetValueOrDefault("--wav") is string wavPath)
            {
                int rate = int.Parse(options.GetValueOrDefault("--rate") ?? "48000", CultureInfo.InvariantCulture);
                var summary = new WavRenderer(settings, config.Station.Mode, rate)
                    .Render(plan.Frames, wavPath, slot);
                journal.Write(string.Create(CultureInfo.InvariantCulture,
                    $"wav: {wavPath}, {summary.Length.TotalMinutes:0.0} min at {rate} Hz: tone {(summary.Tone ? "yes" : "no")}, {summary.Frames} frames, one per burst (the published pdn-soundmodem cannot pack them yet), {summary.Idents} CW idents"));
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            journal.Write($"offline: {e.Message}");
            return 1;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static string CopyOf(string source, string into)
    {
        Directory.CreateDirectory(into);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(into, Path.GetFileName(file)));
        }
        return into;
    }

    private static async Task<int> ServiceAsync(HeadEndConfig config, ConsoleJournal journal)
    {
        journal.Write($"pdn-mailcast-headend {Version} starting");
        TimeProvider time = TimeProvider.System;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                stop.Cancel();
            });

        ScheduleOptions scheduleOptions = config.ToScheduleOptions();
        var store = new RotationStore(config.StateDirectory, Compression.Default, scheduleOptions, journal);
        var planner = new StoreSlotPlanner(store, Compression.Default, scheduleOptions);
        var policy = new IntakePolicy(config.Intake.MaxBulletinBytes);
        var intakes = new List<ScheduledIntake>();
        FbbIntake? fbbIntake = null;
        if (!string.IsNullOrWhiteSpace(config.Intake.DropDirectory))
        {
            Directory.CreateDirectory(config.Intake.DropDirectory);
            intakes.Add(new ScheduledIntake(new FileDropIntake(config.Intake.DropDirectory, store, policy, journal), TimeSpan.FromMinutes(1)));
            journal.Write($"intake: file drop {config.Intake.DropDirectory}, checked every minute");
        }
        if (config.Intake.Fbb is FbbIntakeConfig fbb)
        {
            fbbIntake = new FbbIntake(fbb, store, policy, journal, time);
            intakes.Add(new ScheduledIntake(fbbIntake, TimeSpan.FromMinutes(fbb.PollMinutes)));
            journal.Write($"intake: FBB forwarding from {fbb.Host}:{fbb.Port} as {fbb.PartnerCallsign}, every {fbb.PollMinutes:0.#} min");
        }

        SlotSettings settings = config.ToSlotSettings();
        IAirtime airtime = LinearAirtime.Measure(config.Station.Mode);
        using var api = new StationApiClient(new Uri(config.Station.ApiUrl), config.Station.ApiKey, settings.RenewEvery);
        var kiss = new KissTcpConnector(config.Station.KissHost, config.Station.KissPort, config.Station.KissPortNibble);
        await using var flex = config.Flex.Enabled ? new FlexMonitor(config.Flex.Host, config.Flex.Port, time: time, staleAfter: TimeSpan.FromSeconds(config.Flex.PaStaleSeconds)) : null;
        var runner = new SlotRunner(settings, api, kiss, flex, airtime, journal, time, new KernelClockSync());
        journal.Write($"station: KISS {config.Station.KissHost}:{config.Station.KissPort}, API {config.Station.ApiUrl}, sub-channel {config.Station.SubChannel}, {config.Station.Mode}, bursts up to {config.Station.MaxBurstSeconds:0} s");
        journal.Write(config.Flex.Enabled
            ? $"flex: reading PA temperature and reference from {config.Flex.Host}, stopping at {config.Flex.PaTemperatureLimitC:0.#} C"
            : "flex: not configured, so no PA temperature watch and no reference check");

        var status = new StatusStore(config.StateDirectory, time);
        journal.Write(config.Slot.EveryMinutes == 1440
            ? $"schedule: a slot every day at {config.Slot.TimeUtc}Z"
            : $"schedule: a slot every {config.Slot.EveryMinutes} min, counted from {config.Slot.TimeUtc}Z");
        using var service = new HeadEndService(
            config.ToSlotSchedule(), TimeSpan.FromMinutes(config.Slot.CatchUp), TimeSpan.FromMinutes(config.Slot.RetryMinutes), TimeSpan.FromSeconds(config.Intake.PreSlotSeconds),
            planner, store, intakes, runner, status, journal, time);
        await using StatusServer? server = string.IsNullOrWhiteSpace(config.Status.Bind) ? null : new StatusServer(config.Status.Bind, config.Status.Port, status, service.RequestRunNow);
        if (server is not null)
        {
            try
            {
                server.Start();
                journal.Write($"status: {server.Address}");
            }
            catch (System.Net.HttpListenerException e)
            {
                journal.Write($"status: cannot listen on {server.Address}: {e.Message}; carrying on without it");
            }
        }

        await service.RunAsync(stop.Token);
        fbbIntake?.Dispose();
        journal.Write("pdn-mailcast-headend stopped");
        return 0;
    }

    private static async Task<int> RunNowAsync(HeadEndConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Status.Bind))
        {
            await Console.Error.WriteLineAsync("pdn-mailcast-headend: the status listener is off (\"status\".\"bind\" is empty), so there is nothing to ask");
            return 2;
        }
        string host = config.Status.Bind is "*" or "+" or "0.0.0.0" ? "127.0.0.1" : config.Status.Bind is "::" ? "[::1]" : config.Status.Bind;
        var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{config.Status.Port}/run"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add(StatusServer.RequestedByHeader, $"{Environment.UserName} with --run-now");
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"{(int)response.StatusCode} {Ascii.Plain(body)}");
            return response.StatusCode == System.Net.HttpStatusCode.Accepted ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: cannot reach the head end at {url}: {Ascii.Plain(e.Message)}");
            return 1;
        }
    }

    internal static Dictionary<string, string?> ParseArguments(string[] args, out string? error)
    {
        string[] withValue = ["--config", "--wav", "--date", "--time", "--bulletins", "--rate"];
        string[] flags = ["--plan", "--check-config", "--run-now", "--version", "--help"];
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (withValue.Contains(a))
            {
                if (i + 1 >= args.Length)
                {
                    error = $"{a} needs a value";
                    return options;
                }
                options[a] = args[++i];
            }
            else if (flags.Contains(a))
            {
                options[a] = null;
            }
            else
            {
                error = $"unknown argument '{a}'";
                return options;
            }
        }
        if (options.ContainsKey("--rate") && !options.ContainsKey("--wav"))
        {
            error = "--rate goes with --wav";
        }
        if (options.ContainsKey("--bulletins") && !options.ContainsKey("--wav") && !options.ContainsKey("--plan"))
        {
            error = "--bulletins goes with --wav or --plan";
        }
        return options;
    }

    private sealed class NullStation : IStationApi
    {
        public Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<LeaseAnswer> ReadLeaseAsync(CancellationToken cancellation) => throw new NotSupportedException();

        public Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<bool> DropQueuedAsync(int subChannel, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private sealed class NullKiss : IKissConnector
    {
        public Task<IKissLink> ConnectAsync(CancellationToken cancellation) => throw new NotSupportedException();
    }
}
