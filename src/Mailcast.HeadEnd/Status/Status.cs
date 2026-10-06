using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Status;

/// <summary>The last intake pass, for the status endpoint.</summary>
public sealed record IntakeStatus(DateTimeOffset At, string Source, int Accepted, int Refused, string? Problem);

/// <summary>
/// The slots run on one UTC day, each counted once by how its latest run ended: a slot retried
/// after a skip, or carried on after a restart, is one slot.
/// </summary>
public sealed record SlotsToday(DateOnly Date, int Slots, int Completed, int Aborted, int Skipped);

/// <summary>What the head end is doing and what it last did. The last slot, and the slots of the last two days, survive a restart.</summary>
public sealed class StatusStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // A NaN or an infinity in a double? (a PA reading, an estimate) is written as null, which
        // any JSON reader takes. The report is saved after the slot has keyed and must never fail,
        // so a plain double, should one ever be added, is still written as a string, not refused.
        Converters = { new FiniteOrNullConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>How many slot reports are kept for <see cref="SlotsToday"/>: two days of 15-minute slots, with retries.</summary>
    private const int RecentKept = 400;

    private readonly string? _path;
    private readonly string? _recentPath;
    private readonly List<SlotReport> _recent = [];
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private SlotReport? _lastSlot;
    private IntakeStatus? _lastIntake;
    private string _state = "starting";
    private DateTimeOffset? _nextSlot;
    private int _bulletinsHeld;

    /// <summary>A store that keeps the last slot in <paramref name="stateDirectory"/>, or only in memory when null.</summary>
    public StatusStore(string? stateDirectory, TimeProvider time)
    {
        _time = time;
        if (stateDirectory is not null)
        {
            Directory.CreateDirectory(stateDirectory);
            _path = Path.Combine(stateDirectory, "last-slot.json");
            _recentPath = Path.Combine(stateDirectory, "recent-slots.json");
            if (File.Exists(_recentPath))
            {
                try
                {
                    _recent.AddRange(JsonSerializer.Deserialize<List<SlotReport>>(File.ReadAllText(_recentPath), Json) ?? []);
                }
                catch (JsonException)
                {
                    _recent.Clear();
                }
            }
            if (File.Exists(_path))
            {
                try
                {
                    _lastSlot = JsonSerializer.Deserialize<SlotReport>(File.ReadAllText(_path), Json);
                }
                catch (JsonException)
                {
                    _lastSlot = null;
                }
            }
        }
    }

    /// <summary>The ionosonde reading as of now, for <c>iono</c> in the status document; null leaves it out.</summary>
    public Func<Packet.Mailcast.Propagation.IonoReading>? Ionosphere { get; set; }

    /// <summary>The PSK Reporter reading as of now, for <c>pskReporter</c> in the status document; null leaves it out.</summary>
    public Func<Packet.Mailcast.Propagation.PskReading>? PskReporter { get; set; }

    /// <summary>How the PSK Reporter feed is doing, for <c>pskReporterFeed</c>; null leaves it out.</summary>
    public Func<Packet.Mailcast.Propagation.PskFeedStatus>? PskReporterFeed { get; set; }

    /// <summary>Why the last slot report could not be saved, if it could not; null when it was.</summary>
    public string? LastWriteProblem { get; private set; }

    /// <summary>The last slot, if any.</summary>
    public SlotReport? LastSlot
    {
        get
        {
            lock (_gate)
            {
                return _lastSlot;
            }
        }
    }

    /// <summary>The slots run so far on the current UTC day.</summary>
    public SlotsToday SlotsToday
    {
        get
        {
            lock (_gate)
            {
                return CountToday();
            }
        }
    }

    private SlotsToday CountToday()
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var latest = _recent
            .Where(r => r.Day == today)
            .GroupBy(r => r.Slot != default ? r.Slot : new DateTimeOffset(r.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)))
            .Select(g => g.MaxBy(r => r.End)!)
            .ToList();
        return new SlotsToday(
            today,
            latest.Count,
            latest.Count(r => r.Outcome == SlotOutcome.Completed),
            latest.Count(r => r.Outcome == SlotOutcome.Aborted),
            latest.Count(r => r.Outcome == SlotOutcome.Skipped));
    }

    public void SetState(string state, DateTimeOffset? nextSlot)
    {
        lock (_gate)
        {
            _state = state;
            _nextSlot = nextSlot;
        }
    }

    public void SetBulletinsHeld(int count)
    {
        lock (_gate)
        {
            _bulletinsHeld = count;
        }
    }

    public void RecordIntake(string source, IntakeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _lastIntake = new IntakeStatus(_time.GetUtcNow(), source, result.Accepted, result.Refused, result.Problem);
        }
    }

    public void RecordSlot(SlotReport report)
    {
        lock (_gate)
        {
            _lastSlot = report;
            _recent.Add(report);
            var keepFrom = report.Day.AddDays(-1);
            _recent.RemoveAll(r => r.Day < keepFrom);
            if (_recent.Count > RecentKept)
            {
                _recent.RemoveRange(0, _recent.Count - RecentKept);
            }
            LastWriteProblem = null;
            try
            {
                if (_path is not null)
                {
                    WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(report, Json));
                }
                if (_recentPath is not null)
                {
                    WriteAtomically(_recentPath, JsonSerializer.SerializeToUtf8Bytes(_recent, Json));
                }
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                // Something in the report cannot be written: it is kept in memory, and the slot,
                // which has already gone out, counts all the same.
                LastWriteProblem = e.Message;
            }
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static T? Observed<T>(Func<T>? read)
        where T : class
    {
        try
        {
            return read?.Invoke();
        }
#pragma warning disable CA1031 // observe only: the status page must not fail for it
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private Packet.Mailcast.Propagation.IonoReading? Reading()
    {
        try
        {
            return Ionosphere?.Invoke();
        }
#pragma warning disable CA1031 // observe only: the status page must not fail for it
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The status document.</summary>
    public string Render()
    {
        lock (_gate)
        {
            var document = new
            {
                service = "pdn-mailcast-headend",
                version = Program.Version,
                now = _time.GetUtcNow(),
                state = _state,
                nextSlot = _nextSlot,
                slotsToday = CountToday(),
                bulletinsHeld = _bulletinsHeld,
                lastIntake = _lastIntake,
                lastSlot = _lastSlot,
                iono = Reading(),
                pskReporter = Observed(PskReporter),
                pskReporterFeed = Observed(PskReporterFeed),
            };
            try
            {
                return JsonSerializer.Serialize(document, Json);
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                return JsonSerializer.Serialize(new { service = "pdn-mailcast-headend", version = Program.Version, state = _state, error = e.Message }, Json);
            }
        }
    }
}

/// <summary>
/// Serves <see cref="StatusStore.Render"/> at <c>/</c> and <c>/status</c>, and, when given a way to
/// start one, a one-off slot on <c>POST /run</c>, from this machine only: 202 with the slot it
/// started, 409 when it cannot start one now, 403 from anywhere else.
/// </summary>
public sealed class StatusServer : IAsyncDisposable
{
    /// <summary>The header naming who asked for a one-off slot; the remote address is logged with it.</summary>
    public const string RequestedByHeader = "X-Requested-By";

    private readonly HttpListener _listener = new();
    private readonly StatusStore _status;
    private readonly Func<string, Service.RunNowAnswer>? _runNow;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public StatusServer(string bind, int port, StatusStore status, Func<string, Service.RunNowAnswer>? runNow = null)
    {
        _status = status;
        _runNow = runNow;
        string host = bind is "*" or "0.0.0.0" ? "+" : bind;
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
        Address = string.Create(CultureInfo.InvariantCulture, $"http://{bind}:{port}/status");
    }

    /// <summary>Where it listens, for the journal.</summary>
    public string Address { get; }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "/";
                (int code, string text) = path == "/run" && _runNow is not null
                    ? Run(context.Request)
                    : context.Request.HttpMethod == "GET" && path is "/" or "/status"
                        ? (200, _status.Render())
                        : (404, "{\"error\": \"not found\"}");
                byte[] body = Encoding.UTF8.GetBytes(text);
                context.Response.StatusCode = code;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-cache";
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or IOException)
            {
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private (int Code, string Body) Run(HttpListenerRequest request)
    {
        if (request.HttpMethod != "POST")
        {
            return (405, Error("POST to start a one-off slot"));
        }
        IPEndPoint? remote = request.RemoteEndPoint;
        if (remote is null || !IPAddress.IsLoopback(remote.Address))
        {
            return (403, Error("a one-off slot can only be asked for from this machine"));
        }
        string who = Ascii.Plain(request.Headers[RequestedByHeader] ?? "").Trim();
        if (who.Length > 80)
        {
            who = who[..80];
        }
        who = string.Create(CultureInfo.InvariantCulture, $"{(who.Length > 0 ? who : "someone")} ({remote})");
        Service.RunNowAnswer answer = _runNow!(who);
        return answer.Accepted
            ? (202, JsonSerializer.Serialize(new { slot = answer.Slot, requestedBy = who }, StatusStore.Json))
            : (409, Error(answer.Problem ?? "not now"));
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, StatusStore.Json);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
        _stop.Dispose();
    }
}
