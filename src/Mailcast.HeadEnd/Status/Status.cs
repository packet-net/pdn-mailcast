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

/// <summary>What the head end is doing and what it last did. The last slot survives a restart.</summary>
public sealed class StatusStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string? _path;
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
            if (_path is not null)
            {
                WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(report, Json));
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
                bulletinsHeld = _bulletinsHeld,
                lastIntake = _lastIntake,
                lastSlot = _lastSlot,
            };
            return JsonSerializer.Serialize(document, Json);
        }
    }
}

/// <summary>Serves <see cref="StatusStore.Render"/> at <c>/</c> and <c>/status</c>, read-only.</summary>
public sealed class StatusServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly StatusStore _status;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public StatusServer(string bind, int port, StatusStore status)
    {
        _status = status;
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
                bool known = context.Request.HttpMethod == "GET" && path is "/" or "/status";
                byte[] body = Encoding.UTF8.GetBytes(known ? _status.Render() : "{\"error\": \"not found\"}");
                context.Response.StatusCode = known ? 200 : 404;
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
