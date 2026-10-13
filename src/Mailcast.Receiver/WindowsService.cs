using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;

namespace Mailcast.Receiver;

/// <summary>
/// The receiver as a Windows service (<c>--service</c>), the counterpart of its systemd unit:
/// it runs until Windows stops it, and when it ends by itself it stops with the receiver's exit
/// code, so the service's recovery settings (install-service.ps1 sets them) start it again after
/// a failure just as systemd's Restart=on-failure does.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsService : ServiceBase
{
    /// <summary>The service's name, as install-service.ps1 creates it.</summary>
    public const string Name = "pdn-mailcast-receiver";

    /// <summary>How long a stop waits for the receiver to finish what it is doing; Windows allows a service about 20 s.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<int>> _run;
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _running;

    private WindowsService(Func<CancellationToken, Task<int>> run)
    {
        _run = run;
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    /// <summary>Runs <paramref name="run"/> as the service until Windows stops it or it ends; returns its exit code.</summary>
    public static int Run(Func<CancellationToken, Task<int>> run)
    {
        using var service = new WindowsService(run);
        ServiceBase.Run(service);
        // Never started: not run by Windows' service manager (the log says so).
        return service._running is null ? 1 : service.ExitCode;
    }

    /// <summary>
    /// Sends the console, which a service does not have, to <paramref name="path"/>, each line
    /// stamped with the time as journald would. A log over 10 MB is moved to <c>.old</c> first,
    /// so there are never more than two.
    /// </summary>
    public static void LogTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
        {
            File.Move(path, path + ".old", overwrite: true);
        }
        var file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        var stamped = TextWriter.Synchronized(new StampedWriter(file));
        Console.SetOut(stamped);
        Console.SetError(stamped);
    }

    /// <inheritdoc />
    protected override void OnStart(string[] args)
    {
        _running = Task.Run(() => _run(_stop.Token));
        // Ended by itself, not by Windows: stop the service, with the receiver's exit code.
        _ = _running.ContinueWith(_ =>
        {
            if (!_stop.IsCancellationRequested)
            {
                Stop();
            }
        }, TaskScheduler.Default);
    }

    /// <inheritdoc />
    protected override void OnStop() => Finish();

    /// <inheritdoc />
    protected override void OnShutdown() => Finish();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Finish()
    {
        _stop.Cancel();
        if (_running is null)
        {
            return;
        }
        try
        {
            ExitCode = _running.Wait(StopWait) ? _running.Result : 1;
        }
        catch (AggregateException)
        {
            ExitCode = 1;
        }
    }

    /// <summary>Puts the time (UTC) in front of each line, writing whole lines.</summary>
    private sealed class StampedWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                inner.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z '", System.Globalization.CultureInfo.InvariantCulture) + _line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (char c in value ?? "")
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
