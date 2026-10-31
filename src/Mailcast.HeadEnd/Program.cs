using System.Globalization;
using System.Reflection;
using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Mailcast.HeadEnd;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Offline;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Relay;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;
using Mailcast.HeadEnd.Status;

return await Program.MainAsync(args);

/// <summary>The command line.</summary>
public static partial class Program
{
    private const string DefaultConfig = "/etc/pdn-mailcast-headend/headend.json";

    /// <summary>
    /// The directory signing key: every directory is signed with it, and the service does not start
    /// without it. A fixed place, not a setting. The package makes it at install (<c>--make-key</c>);
    /// one made with <c>openssl genpkey -algorithm ed25519</c> works too, see docs/headend.md.
    /// </summary>
    public const string SigningKeyPath = "/etc/pdn-mailcast-headend/signing-key.pem";

    /// <summary>Where the key is read and made; <see cref="SigningKeyPath"/> except in tests.</summary>
    internal static string KeyPath { get; set; } = SigningKeyPath;

    /// <summary>--make-key: makes the key if there is none; says its ID and public key either way.</summary>
    internal static int MakeKey()
    {
        try
        {
            bool made = DirectorySigner.CreateKeyFile(KeyPath);
            if (made)
            {
                HandOverToServiceUser(KeyPath);
            }
            var signer = DirectorySigner.LoadIfPresent(KeyPath)!;
            Console.WriteLine($"{(made ? "made the signing key" : "the signing key is already there")} at {KeyPath}");
            Console.WriteLine(DescribeSigning(signer, KeyPath));
            return 0;
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"pdn-mailcast-headend: the signing key {Ascii.Plain(e.Message)}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"pdn-mailcast-headend: the signing key at {KeyPath}: {Ascii.Plain(e.Message)}; run it with sudo");
            return 1;
        }
    }

    // Run as root, the new key is made the service user's, so the service can read it.
    private static void HandOverToServiceUser(string path)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName != "root")
        {
            return;
        }
        try
        {
            using var chown = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chown", ["pdn-mailcast-headend:pdn-mailcast-headend", path]) { RedirectStandardError = true });
            chown?.WaitForExit(5000);
            if (chown is { ExitCode: not 0 })
            {
                Console.Error.WriteLine($"pdn-mailcast-headend: could not give {path} to the service user; run: chown pdn-mailcast-headend:pdn-mailcast-headend {path}");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"pdn-mailcast-headend: could not give {path} to the service user: {Ascii.Plain(e.Message)}");
        }
    }

    private const string Usage = """
        pdn-mailcast-headend: a mailcast head end, which sends a BBS's bulletins on the air.

          pdn-mailcast-headend [--config FILE]
              Runs the service: takes bulletins in, and sends them in each slot.
          pdn-mailcast-headend --plan [--date YYYY-MM-DD] [--time HH:MM] [--config FILE]
              Prints what a slot would send, and changes nothing. The slot is the one running
              at --time (slot.minute, with the hour 00, if left out) on --date (today if left out).
          pdn-mailcast-headend --wav OUT.wav [--date YYYY-MM-DD] [--time HH:MM] [--bulletins DIR] [--rate HZ] [--config FILE]
              Renders that slot to a WAV file, offline. --bulletins takes a directory of
              bulletin files (the file drop format), all new in that slot; without it the head
              end's own store is used. Opens no connection to anything.
          pdn-mailcast-headend --run-now [--config FILE]
              Asks the running service for a one-off slot now, through its status listener
              (POST /run). It is a whole slot with the usual checks, counted like any other, and
              the schedule carries on afterwards.
          pdn-mailcast-headend --check-config [--config FILE]
              Checks the configuration, and says whether directories will be signed.
          pdn-mailcast-headend --make-key
              Makes the directory signing key if there is none yet (the package does this when it
              is installed), and says its ID and public key. Never replaces a key.
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

        if (options.ContainsKey("--make-key"))
        {
            return MakeKey();
        }

        string configPath = options.GetValueOrDefault("--config") ?? DefaultConfig;
        bool offline = options.ContainsKey("--wav") || options.ContainsKey("--plan");
        HeadEndConfig config;
        bool placeholder;
        try
        {
            config = LoadConfig(configPath, offline, options.ContainsKey("--config"), out placeholder);
        }
        catch (ConfigException e)
        {
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: {Ascii.Plain(e.Message)}");
            return 2;
        }

        // Needs no key: it only asks the running service.
        if (options.ContainsKey("--run-now"))
        {
            return await RunNowAsync(config);
        }

        DirectorySigner? signer;
        try
        {
            signer = DirectorySigner.LoadIfPresent(KeyPath);
        }
        catch (FormatException e)
        {
            if (!offline)
            {
                await Console.Error.WriteLineAsync($"pdn-mailcast-headend: the signing key {Ascii.Plain(e.Message)}; fix it, or move it away and run: sudo pdn-mailcast-headend --make-key");
                return 2;
            }
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: the signing key {Ascii.Plain(e.Message)}; this run's directories are unsigned");
            signer = null;
        }
        if (signer is null && !offline)
        {
            // Receivers do not use an unsigned directory, so a head end without a key would send nothing useful.
            await Console.Error.WriteLineAsync($"pdn-mailcast-headend: no signing key at {KeyPath}; make one with: sudo pdn-mailcast-headend --make-key");
            return 2;
        }

        if (options.ContainsKey("--check-config"))
        {
            Console.WriteLine($"{configPath}: fine");
            Console.WriteLine(DescribeSigning(signer, SigningKeyPath));
            Console.WriteLine(DescribeSelf(config));
            Console.WriteLine(DescribeAdverts(config));
            return 0;
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
            if (placeholder)
            {
                journal.Write($"offline: no config at {configPath}, so the frames are made as {PlaceholderCallsign}, a placeholder callsign; give --config for your own");
            }
            return Offline(config, options, slot, journal, signer);
        }
        return await ServiceAsync(config, journal, signer);
    }

    /// <summary>
    /// The callsign offline work uses with no config at all: plainly not a real one. It can never
    /// key a radio, since offline work has no station.
    /// </summary>
    internal const string PlaceholderCallsign = "N0CALL";

    /// <summary>
    /// Reads the configuration. Offline work (<c>--plan</c>, <c>--wav</c>) needs no station, so with
    /// no file at the usual place and no <c>--config</c> it runs on the defaults, with
    /// <see cref="PlaceholderCallsign"/>, and <paramref name="placeholder"/> says so.
    /// </summary>
    internal static HeadEndConfig LoadConfig(string path, bool offline, bool pathGiven, out bool placeholder)
    {
        placeholder = offline && !pathGiven && !File.Exists(path);
        return placeholder
            ? HeadEndConfig.Parse($$$"""{"callsign": "{{{PlaceholderCallsign}}}", "station": {"apiKey": "offline"}}""")
            : HeadEndConfig.Load(path);
    }

    /// <summary>One plain line on the other stations each directory tells listeners about (issue #178).</summary>
    /// <summary>One plain line on what the directories say about this station's own frequency and place (issue #179).</summary>
    internal static string DescribeSelf(HeadEndConfig config) => config.ToDetails() switch
    {
        null => "this station: no \"frequencyHz\" or \"locator\" given, so directories do not say where it is on the air or where it is; receivers then rely on what other head ends advertise",
        var d => "this station: " + string.Join(", ", new[]
        {
            d.FrequencyHz is { } hz ? string.Create(CultureInfo.InvariantCulture, $"{hz / 1e6:0.0000} MHz {(d.Sideband == Sideband.Lsb ? "LSB" : "USB")}") : null,
            d.Locator is { } l ? "locator " + l : null,
        }.OfType<string>()) + " in every directory",
    };

    internal static string DescribeAdverts(HeadEndConfig config)
    {
        var adverts = config.ToScheduleOptions().Adverts;
        return adverts.Count == 0
            ? "advertising: no other stations (\"advertise\" in the configuration)"
            : "advertising: " + string.Join("; ", adverts.Select(a => string.Create(CultureInfo.InvariantCulture,
                $"{a.Callsign} {string.Join(" ", a.Timetable.ToFields())} on {a.FrequencyHz / 1e6:0.0000} MHz {(a.Sideband == Sideband.Usb ? "USB" : "LSB")}")));
    }

    /// <summary>One plain line on whether directories are signed, and with which key.</summary>
    internal static string DescribeSigning(DirectorySigner? signer, string path) => signer is null
        ? $"signing: no key at {path}, so offline runs go out unsigned"
        : $"signing: directories are signed with key {KeyId.Format(signer.KeyId)}, public key {signer.PublicKeyBase64}";

    internal static int Offline(HeadEndConfig config, Dictionary<string, string?> options, DateTimeOffset slot, ConsoleJournal journal, DirectorySigner? signer)
    {
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        // Offline work never touches the head end's own state: it runs on a copy, or on a store
        // made from the files given, in a scratch directory that is removed afterwards.
        string scratch = Directory.CreateTempSubdirectory("mailcast-offline-").FullName;
        try
        {
            ScheduleOptions scheduleOptions = config.ToScheduleOptions() with { Signer = signer };
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
            SlotSettings settings = config.ToSlotSettings();
            Waveforms waveforms = config.ToWaveforms(LinearAirtime.Measure);
            // The head end's own list of reports, read but never written, when it reads the BBS's.
            ReportLog? reports = config.Intake.Reports is null ? null
                : new ReportLog(Directory.Exists(config.StateDirectory) ? config.StateDirectory : scratch, journal, readOnly: true);
            var planner = new StoreSlotPlanner(store, Compression.Default, scheduleOptions, waveforms, settings, config.FillLimitAfter, reports: reports);

            SlotPlan plan = planner.Plan(slot);
            SlotWaveform waveform = plan.Waveform ?? waveforms.For(slot);
            var airtime = (LinearAirtime)waveform.Airtime;
            var runner = new SlotRunner(settings, new NullStation(), new NullKiss(), null, airtime, journal, TimeProvider.System);
            var bursts = runner.BurstSizes(plan.Frames, airtime);
            journal.Write(string.Create(CultureInfo.InvariantCulture,
                $"airtime on {waveform.Mode}: {airtime.PerBurst.TotalSeconds:0.00} s a burst, {airtime.Burst([1003]).TotalSeconds - airtime.PerBurst.TotalSeconds:0.00} s a full frame (1003 octets)"));
            journal.Write(string.Create(CultureInfo.InvariantCulture,
                $"plan {SlotRunner.Name(slot)}: {plan.BulletinsInRotation} bulletins in rotation{(plan.LeftoverInRotation > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({plan.LeftoverInRotation} leftover)") : "")}, {plan.Frames.Count} frames, {bursts.Count} bursts of up to {bursts.DefaultIfEmpty(0).Max()} frames on {waveform.Mode}, about {runner.Airtime(plan.Frames, bursts, airtime).TotalMinutes:0.0} min of bursts, {runner.Model(airtime).Slot([.. plan.Frames.Select(f => runner.Ax25(f).Length)], bursts).TotalMinutes:0.0} min on the air with the tone and idents{(scheduleOptions.Budget is null ? "" : string.Create(CultureInfo.InvariantCulture, $" (filled to {config.FillLimit.TotalMinutes:0.0})"))}"));

            if (options.GetValueOrDefault("--wav") is string wavPath)
            {
                int rate = int.Parse(options.GetValueOrDefault("--rate") ?? "48000", CultureInfo.InvariantCulture);
                var summary = new WavRenderer(settings, waveform.Mode, rate)
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

    private static async Task<int> ServiceAsync(HeadEndConfig config, ConsoleJournal journal, DirectorySigner? signer)
    {
        journal.Write($"pdn-mailcast-headend {Version} starting");
        journal.Write(DescribeSigning(signer, SigningKeyPath));
        journal.Write(DescribeSelf(config));
        journal.Write(DescribeAdverts(config));
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

        ScheduleOptions scheduleOptions = config.ToScheduleOptions() with { Signer = signer };
        if (config.Schedule.SymbolSize is not null)
        {
            journal.Write("config: schedule.symbolSize is ignored (pieces are always 240 octets); harmless to keep");
        }
        SlotSettings settings = config.ToSlotSettings();
        Waveforms waveforms = config.ToWaveforms(LinearAirtime.Measure);
        var store = new RotationStore(config.StateDirectory, Compression.Default, scheduleOptions, journal);
        var schedule = config.ToSlotSchedule();
        var ionoSettings = config.Ionosphere.ToSettings();
        using var ionosonde = new IonosondeMonitor(ionoSettings, IonosondeMonitor.UserAgentFor(Version, config.Callsign), time, journal.Write);
        Func<IonoReading>? reading = ionoSettings.Stations.Count == 0 ? null : () => ionosonde.Current;
        var pskReporter = config.PskReporter.Enabled ? new PskReporterMonitor(config.Callsign, time, journal.Write, host: config.PskReporter.Host, port: config.PskReporter.Port) : null;
        Func<PskReading>? spots = pskReporter is null ? null : () => pskReporter.Current;
        ReportLog? reports = config.Intake.Reports is null ? null : new ReportLog(config.StateDirectory, journal);
        // Receivers on this machine and on the network, read-only (issue #218); no setting, and nothing waits for it. What they hear of
        // the advertised stations joins the rotation, ranked least covered first.
        var catalogue = new ObjectCatalogue(time);
        DateTimeOffset startedAt = time.GetUtcNow();
        // Until a receiver has been read (or an hour has passed), what the catalogue does not hold says nothing about what is on offer.
        store.StillOffered = id => catalogue.Get(id) is not null || !(catalogue.ReadOnce || time.GetUtcNow() - startedAt >= TimeSpan.FromHours(1));
        var relay = new RelaySelector(catalogue, Compression.Default, config.Callsign, scheduleOptions.Adverts, time, journal, scheduleOptions.Budget);
        var planner = new StoreSlotPlanner(store, Compression.Default, scheduleOptions, waveforms, settings, config.FillLimitAfter, time, reading, spots, reports, relay);
        var policy = new IntakePolicy(config.Intake.MaxBulletinBytes);
        var intakes = new List<ScheduledIntake>();
        FbbIntake? fbbIntake = null;
        ReportReader? reportReader = null;
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
        if (config.Intake.Reports is { } reader && reports is not null)
        {
            reportReader = new ReportReader(reader, reports, journal, time);
            intakes.Add(new ScheduledIntake(reportReader, ReportReader.Every));
            journal.Write($"reports: reading the list of daily reports ({ReportReader.ListCommand}) from {reader.Host}:{reader.Port} as {reader.User}, before each slot and at most every {ReportReader.Every.TotalMinutes:0} min");
        }

        IAirtime airtime = LinearAirtime.Measure(config.Station.Mode);
        using var api = new StationApiClient(new Uri(config.Station.ApiUrl), config.Station.ApiKey, settings.RenewEvery);
        var kiss = new KissTcpConnector(config.Station.KissHost, config.Station.KissPort, config.Station.KissPortNibble);
        await using var flex = config.Flex.Enabled ? new FlexMonitor(config.Flex.Host, config.Flex.Port, time: time, staleAfter: TimeSpan.FromSeconds(config.Flex.PaStaleSeconds)) : null;
        var runner = new SlotRunner(settings, api, kiss, flex, airtime, journal, time, new KernelClockSync());
        journal.Write($"station: KISS {config.Station.KissHost}:{config.Station.KissPort}, API {config.Station.ApiUrl}, sub-channel {config.Station.SubChannel}, {config.Station.Mode}, bursts up to {config.Station.MaxBurstSeconds:0} s");
        journal.Write(config.Flex.Enabled
            ? $"flex: reading PA temperature and reference from {config.Flex.Host}, stopping at {config.Flex.PaTemperatureLimitC:0.#} C"
            : "flex: not configured, so no PA temperature watch and no reference check");

        var status = new StatusStore(config.StateDirectory, time) { Ionosphere = reading, PskReporter = spots, PskReporterFeed = pskReporter is null ? null : () => pskReporter.Feed, SigningKeyId = signer?.KeyId };
        journal.Write(reading is null
            ? "ionosonde: off (\"ionosphere\".\"stations\" is empty)"
            : string.Create(CultureInfo.InvariantCulture, $"ionosonde: observe only, from {string.Join(", ", ionoSettings.Stations)} by GIRO then PROPquest, at most every {IonosondeMonitor.PollEvery.TotalMinutes:0} min near a slot; open at {ionoSettings.OpenMhz:0.###} MHz, reliable at {ionoSettings.ReliableFactor:0.###} of the MUF, stale after {ionoSettings.StaleAfter.TotalMinutes:0} min"));
        journal.Write(pskReporter is null
            ? "pskreporter: off (\"pskReporter\".\"enabled\" is false)"
            : string.Create(CultureInfo.InvariantCulture, $"pskreporter: observe only, live spots from {config.PskReporter.Host}:{config.PskReporter.Port} near a slot, 40 and 80 m FT8/FT4/WSPR with both ends in the UK or Ireland, judged over the last {PskEvaluator.Window.TotalMinutes:0} min; 40 m sent"));
        journal.Write(DescribeSchedule(config));
        journal.Write(Describe(config, scheduleOptions, waveforms));
        using var receivers = new ReceiverPool(catalogue, journal, time);
        status.Receivers = receivers.StatusView;
        status.Relay = relay.StatusView;
        using var service = new HeadEndService(
            schedule, TimeSpan.FromMinutes(config.Slot.CatchUp), TimeSpan.FromMinutes(config.Slot.RetryMinutes), TimeSpan.FromSeconds(config.Intake.PreSlotSeconds),
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

        if (Waveforms.SetHardwarePayload(config.Station.Mode) is not null)
        {
            // A slot cut off by a crash or a stop may have left the modem on another waveform, and
            // "modes" may since have been taken out of the configuration: put it back whatever it says.
            await runner.PutWaveformBackAsync(stop.Token);
        }
        // In the background and observe only: a slot never waits for it.
        Task ionosondeLoop = reading is null ? Task.CompletedTask : Task.Run(() => ionosonde.RunAsync(t => WantReading(schedule, t), stop.Token));
        Task pskReporterLoop = pskReporter is null ? Task.CompletedTask : Task.Run(() => pskReporter.RunAsync(t => WantReading(schedule, t), stop.Token));
        Task receiversLoop = Task.Run(() => receivers.RunAsync(stop.Token));
        await service.RunAsync(stop.Token);
        await receiversLoop;
        await ionosondeLoop;
        await pskReporterLoop;
        fbbIntake?.Dispose();
        reportReader?.Dispose();
        journal.Write("pdn-mailcast-headend stopped");
        return 0;
    }

    /// <summary>Whether to keep the ionosonde reading fresh at <paramref name="t"/>: from an hour before a slot to a quarter of an hour after.</summary>
    internal static bool WantReading(SlotSchedule schedule, DateTimeOffset t) =>
        schedule.NextActiveAfter(t) - t <= TimeSpan.FromHours(1)
        || (schedule.ActiveAtOrBefore(t) is { } last && t - last <= TimeSpan.FromMinutes(15));

    /// <summary>
    /// The start-up line on when slots are: "schedule: a slot every 60 min, counted from 00:30Z,
    /// so at :30 each hour". The minutes past the hour are given when every hour has the same
    /// slots, so a head end started off the hour says so plainly.
    /// </summary>
    internal static string DescribeSchedule(HeadEndConfig config)
    {
        int every = config.Slot.EveryMinutes;
        if (every == 1440)
        {
            return $"schedule: a slot every day at {config.Slot.Anchor}Z";
        }
        string line = config.SlotTime.Hour == 0
            ? $"schedule: a slot every {every} min, counted from minute {config.SlotTime.Minute} past 00:00Z"
            : $"schedule: a slot every {every} min, counted from {config.Slot.Anchor}Z";
        if (60 % every != 0)
        {
            return line;
        }
        int first = config.SlotTime.Minute % every;
        var minutes = Enumerable.Range(0, 60 / every).Select(i => ":" + (first + (i * every)).ToString("00", CultureInfo.InvariantCulture)).ToList();
        string at = minutes.Count == 1 ? minutes[0] : string.Join(", ", minutes[..^1]) + " and " + minutes[^1];
        return $"{line}, so at {at} each hour";
    }

    /// <summary>One journal line on how bulletins are carried and on which waveforms.</summary>
    internal static string Describe(HeadEndConfig config, ScheduleOptions options, Waveforms waveforms)
    {
        string modes = waveforms.Modes.Count == 1
            ? $"every slot on {waveforms.Modes[0]}{(waveforms.SetOnModem ? " (set on the modem each slot)" : "")}"
            : $"slots take turns on {string.Join(", ", waveforms.Modes)} (set on the modem each slot)";
        if (options.Budget is not { } rule)
        {
            return $"carrying: fixed shares {string.Join(", ", options.SlotShares.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture)))} K in slots {string.Join(", ", options.SlotOffsets)} after the first; {modes}";
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"carrying: each slot filled to {config.FillLimit.TotalMinutes:0.0} min on the air ({config.Schedule.BudgetMinutes ?? config.Slot.Max:0.#} min budget, at most {config.Slot.Max:0.#} min less {config.Margin.TotalSeconds:0} s for the clear-channel and carrier waits, and less any lateness), least covered first, each bulletin enough a slot that any {rule.SpreadSlots} slots rebuild it and at most {rule.SlotCap:0.##} K, after {rule.RetireCoverage:0.##} K or {rule.RetireAfter.TotalHours:0.#} h only in airtime nothing else wants (the leftover tier), retired after {Math.Max(BroadcastScheduler.LeftoverCoverage, rule.RetireCoverage):0.##} K or {BroadcastScheduler.RetireAge(rule).TotalHours:0.#} h; {modes}");
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
        string[] flags = ["--plan", "--check-config", "--run-now", "--make-key", "--version", "--help"];
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

        public Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, ProbeRequest? probe, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<KeepAudioAnswer> KeepAudioAsync(DateTimeOffset keepFrom, DateTimeOffset keepTo, string label, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private sealed class NullKiss : IKissConnector
    {
        public Task<IKissLink> ConnectAsync(CancellationToken cancellation) => throw new NotSupportedException();
    }
}
