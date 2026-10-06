using System.Net;
using System.Reflection;

namespace Mailcast.Receiver.Updates;

/// <summary>What the last check that worked found.</summary>
internal sealed record UpdateFinding(string Latest, bool Newer, DateTimeOffset CheckedAt, bool FromAptRepo);

/// <summary>
/// Looks in packet-net's apt repository for a newer receiver, for the status page: once soon
/// after start-up, then every <see cref="Every"/>. One small GET of the repository's
/// <c>Packages</c> index; nothing is downloaded or installed. It never holds anything else up,
/// and when it cannot check (offline, no DNS, an error page) the page shows nothing and the log
/// says so once for each kind of failure, not at every check.
/// </summary>
public sealed class UpdateCheck : IDisposable
{
    /// <summary>The repository's index.</summary>
    public const string PackagesUrl = "https://packet-net.github.io/apt/Packages";

    /// <summary>Where each version's release notes and .debs are.</summary>
    public const string ReleasesUrl = "https://github.com/packet-net/pdn-mailcast/releases";

    /// <summary>The command that upgrades a receiver installed from the apt repository.</summary>
    public const string AptCommand = "sudo apt update && sudo apt install --only-upgrade " + AptPackages.ReceiverPackage;

    /// <summary>How often it looks.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    /// <summary>How long one look may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly Func<bool> _fromAptRepo;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _logged = [];
    private string? _announced;
    private volatile UpdateFinding? _found;

    /// <summary>
    /// A check for this machine. <paramref name="handler"/> is for tests (no network);
    /// <paramref name="fromAptRepo"/> says whether this machine installs from the apt repository,
    /// by default by looking in <see cref="AptSources.SourcesDirectory"/>.
    /// </summary>
    internal UpdateCheck(
        TimeProvider time,
        Action<string> log,
        string current,
        string? architecture,
        HttpMessageHandler? handler = null,
        Func<bool>? fromAptRepo = null)
    {
        _time = time;
        _log = log;
        Current = DebianVersion.WithoutBuild(current);
        Architecture = architecture;
        _fromAptRepo = fromAptRepo ?? AptSources.ThisMachine;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            // The timeout is this class's own, on its clock, so a test can run it out.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            // The index is about 75 kB; anything far bigger is not it.
            MaxResponseContentBufferSize = 8 * 1024 * 1024,
        };
    }

    /// <summary>A check for this receiver, on this machine, against the real repository.</summary>
    public UpdateCheck(TimeProvider time, Action<string> log)
        : this(time, log, ThisVersion(), AptPackages.ThisArchitecture())
    {
    }

    /// <summary>This receiver's version, in full (0.6.0, or 0.7.0-rc1), without a local build's <c>+</c> suffix.</summary>
    public string Current { get; }

    /// <summary>Debian's name for this machine's architecture, or null when packet-net builds none for it (then nothing is checked).</summary>
    public string? Architecture { get; }

    /// <summary>How long after start-up the first look is; by default a random time from 20 s to 2 min, so receivers started together do not all ask at once.</summary>
    internal TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(Random.Shared.Next(20, 121));

    /// <summary>Raised after each look, whether it worked or not, for tests.</summary>
    internal event Action? Checked;

    /// <summary>What the last look that worked found, or null before one has.</summary>
    internal UpdateFinding? Found => _found;

    /// <summary>The User-Agent it sends: the receiver and its version, and where it comes from.</summary>
    internal string UserAgent => $"{AptPackages.ReceiverPackage}/{Current} (+https://github.com/packet-net/pdn-mailcast)";

    /// <summary>The version this assembly was built as, without the <c>+commit</c> the SDK adds to a local build.</summary>
    internal static string ThisVersion() =>
        DebianVersion.WithoutBuild(typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? ReceiverHost.Version);

    /// <summary>
    /// Looks after <see cref="FirstDelay"/>, then every <see cref="Every"/>, until
    /// <paramref name="cancellation"/> is cancelled or this is disposed. Never throws.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        if (Architecture is null)
        {
            return;
        }
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
            // Due times are kept absolute, so a slow look does not push the next one later.
            var due = _time.GetUtcNow() + FirstDelay;
            while (true)
            {
                var wait = due - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, stop.Token).ConfigureAwait(false);
                }
                due += Every;
                await CheckOnceAsync(stop.Token).ConfigureAwait(false);
                if (due <= _time.GetUtcNow())
                {
                    due = _time.GetUtcNow() + Every;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>One look. Never throws but for <paramref name="cancellation"/>.</summary>
    internal async Task CheckOnceAsync(CancellationToken cancellation)
    {
        try
        {
            string text = await FetchAsync(cancellation).ConfigureAwait(false);
            var newest = AptPackages.Newest(AptPackages.Parse(text), AptPackages.ReceiverPackage, Architecture!)
                ?? throw new UpdateCheckFailure("missing", $"its package list has no {AptPackages.ReceiverPackage} for {Architecture}");
            bool newer = DebianVersion.Compare(newest.Version, Current) > 0;
            _found = new UpdateFinding(newest.Version, newer, _time.GetUtcNow(), _fromAptRepo());
            _logged.Clear();
            if (newer && _announced != newest.Version)
            {
                _announced = newest.Version;
                _log($"update: version {newest.Version} is available (you have {Current}); the status page says how to upgrade");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateCheckFailure e)
        {
            Failed(e.Kind, e.Message);
        }
        catch (OperationCanceledException)
        {
            Failed("timeout", $"no answer from {AptSources.RepoHost} in {Timeout.TotalSeconds:F0} s");
        }
        catch (HttpRequestException e)
        {
            Failed("network", Ascii.Clean(e.Message));
        }
        catch (FormatException)
        {
            Failed("parse", "its answer was not a package list");
        }
#pragma warning disable CA1031 // observe only: nothing about this check may stop the receiver
        catch (Exception e)
#pragma warning restore CA1031
        {
            Failed("error:" + e.GetType().Name, Ascii.Clean(e.Message));
        }
        finally
        {
            Checked?.Invoke();
        }
    }

    private async Task<string> FetchAsync(CancellationToken cancellation)
    {
        using var timeout = new CancellationTokenSource(Timeout, _time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, PackagesUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, both.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new UpdateCheckFailure("status", $"{AptSources.RepoHost} answered {(int)response.StatusCode}");
        }
        return await response.Content.ReadAsStringAsync(both.Token).ConfigureAwait(false);
    }

    /// <summary>Logs a failure the first time its kind happens; the page shows nothing for it.</summary>
    private void Failed(string kind, string why)
    {
        if (_logged.Add(kind))
        {
            _log($"update: cannot check for a newer version: {why}. It tries again every {Every.TotalHours:F0} hours, "
                + "and says nothing more about this until it works");
        }
    }

    /// <summary>The <c>update</c> part of <c>/api/status</c>, and what the page's banner shows.</summary>
    internal object View()
    {
        var f = _found;
        bool newer = f is { Newer: true };
        string? deb = newer && !f!.FromAptRepo ? $"{AptPackages.ReceiverPackage}_{f.Latest}_{Architecture}.deb" : null;
        return new
        {
            latest = f?.Latest,
            current = Current,
            newer,
            checkedAt = f?.CheckedAt,
            aptRepo = f?.FromAptRepo,
            release = newer ? $"{ReleasesUrl}/tag/v{f!.Latest}" : null,
            download = deb is null ? null : $"{ReleasesUrl}/download/v{f!.Latest}/{deb}",
            command = !newer ? null : deb is null ? AptCommand : $"sudo apt install ./{deb}",
        };
    }

    /// <summary>Stops <see cref="RunAsync"/>.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _http.Dispose();
    }

    private sealed class UpdateCheckFailure(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }
}
