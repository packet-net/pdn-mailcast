using System.Globalization;
using System.Net;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// Keeps the ionosonde reading up to date in the background: asks GIRO, then PROPquest, at most
/// every <see cref="PollEvery"/>, and holds what they said. <see cref="Current"/> never waits for
/// the network, and a source that fails only ever leaves the reading older, then UNKNOWN.
/// </summary>
/// <remarks>
/// <para>Each poll asks GIRO for the stations in order, for the last <see cref="Window"/>, and
/// stops at the first with a fresh sounding. If none has one, it asks PROPquest once for all of
/// them. A source that answers 429, 5xx or not at all is left alone for 15 minutes, then 30, 60
/// and so on up to <see cref="MostBackoff"/>, or as long as its Retry-After says if that is
/// longer; it is asked as usual again once it answers.</para>
/// </remarks>
public sealed class IonosondeMonitor : IDisposable
{
    /// <summary>The most often the sources are asked.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(15);

    /// <summary>How far back each poll asks for: soundings often reach the sources hours late.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>The longest a failing source is left alone.</summary>
    public static readonly TimeSpan MostBackoff = TimeSpan.FromHours(4);

    /// <summary>How long one request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>GIRO's DIDBase, as the scaled-data web form uses it.</summary>
    public static readonly Uri GiroUrl = new("https://lgdc.uml.edu/fastchar/getbest");

    /// <summary>PROPquest's ionosphere data.</summary>
    public static readonly Uri PropQuestUrl = new("https://propquest.org/JSON-grab-ionosphere");

    private readonly IonoSettings _settings;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly Source _giro = new("GIRO");
    private readonly Source _propQuest = new("PROPquest");
    private volatile IReadOnlyList<IonoSounding> _soundings = [];
    private DateTimeOffset? _lastPoll;

    /// <summary>
    /// A monitor that asks with <paramref name="userAgent"/> (which names the software and the
    /// station, and carries no e-mail address).
    /// </summary>
    /// <param name="settings">The stations and thresholds.</param>
    /// <param name="userAgent">The User-Agent header.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Where problems are said, once each time a source starts or stops failing.</param>
    /// <param name="handler">The HTTP handler, for tests; null uses the network.</param>
    public IonosondeMonitor(IonoSettings settings, string userAgent, TimeProvider time, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _settings = settings;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? (_ => { });
#pragma warning disable CA2000 // the client owns a handler it makes, and is disposed with the monitor
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
#pragma warning restore CA2000
        _http.Timeout = RequestTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>The User-Agent the head end uses: <c>pdn-mailcast-headend/VERSION (+https://github.com/packet-net/pdn-mailcast; CALL)</c>.</summary>
    public static string UserAgentFor(string version, string callsign) =>
        $"pdn-mailcast-headend/{Plain(version)} (+https://github.com/packet-net/pdn-mailcast; {Plain(callsign)})";

    /// <summary>The settings the reading is judged by.</summary>
    public IonoSettings Settings => _settings;

    /// <summary>The reading as of now, from what the sources last said. Never waits, never throws.</summary>
    public IonoReading Current => IonoEvaluator.Evaluate(_soundings, _settings, _time.GetUtcNow());

    /// <summary>When the sources were last polled, if ever.</summary>
    public DateTimeOffset? LastPoll => _lastPoll;

    /// <summary>
    /// Polls every <see cref="PollEvery"/> while <paramref name="wanted"/> says a reading is
    /// wanted (near a slot, say), until cancelled. Nothing it meets ends it but cancellation.
    /// </summary>
    public async Task RunAsync(Func<DateTimeOffset, bool>? wanted, CancellationToken cancellation)
    {
        if (_settings.Stations.Count == 0)
        {
            return;
        }
        while (!cancellation.IsCancellationRequested)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (ShouldPoll(now, wanted))
            {
                try
                {
                    await PollAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // a reading is observe only: nothing here may stop the head end
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _log($"ionosonde: poll failed: {Plain(e.Message)}");
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Whether <see cref="RunAsync"/> polls at <paramref name="now"/>: when wanted, and <see cref="PollEvery"/> or more since the last poll.</summary>
    public bool ShouldPoll(DateTimeOffset now, Func<DateTimeOffset, bool>? wanted) =>
        (wanted?.Invoke(now) ?? true) && (_lastPoll is not { } last || now - last >= PollEvery);

    /// <summary>Asks the sources once, as the remarks say, and keeps what they answer.</summary>
    public async Task PollAsync(CancellationToken cancellation)
    {
        DateTimeOffset now = _time.GetUtcNow();
        _lastPoll = now;
        var held = _soundings.Where(s => now - s.Time <= TimeSpan.FromDays(1)).ToList();
        bool fresh = false;
        if (_giro.MayAsk(now))
        {
            foreach (string station in _settings.Stations)
            {
                var answer = await GetAsync(_giro, GiroQuery(station, now), cancellation).ConfigureAwait(false);
                if (answer is null)
                {
                    break; // failing: leave the rest of the stations alone too
                }
                IReadOnlyList<IonoSounding> got;
                try
                {
                    got = GiroParser.Parse(answer, station);
                }
                catch (FormatException e)
                {
                    Failed(_giro, now, $"answered something that is not DIDBase text ({Plain(e.Message)})", null);
                    break;
                }
                held.RemoveAll(s => s.Source == IonoSource.Giro && string.Equals(s.Station, station, StringComparison.OrdinalIgnoreCase));
                held.AddRange(got);
                if (got.Any(s => TimeSpan.FromMinutes(IonoEvaluator.AgeMinutes(s.Time, now)) <= _settings.StaleAfter))
                {
                    fresh = true;
                    break;
                }
            }
        }
        if (!fresh && _propQuest.MayAsk(now))
        {
            var answer = await GetAsync(_propQuest, PropQuestQuery(_settings.Stations, now), cancellation).ConfigureAwait(false);
            if (answer is not null)
            {
                try
                {
                    var got = PropQuestParser.Parse(answer);
                    held.RemoveAll(s => s.Source == IonoSource.PropQuest);
                    held.AddRange(got);
                }
                catch (FormatException e)
                {
                    Failed(_propQuest, now, $"answered something that is not its JSON ({Plain(e.Message)})", null);
                }
            }
        }
        _soundings = held;
    }

    /// <summary>The GIRO request for one station's last <see cref="Window"/>.</summary>
    public static Uri GiroQuery(string station, DateTimeOffset now)
    {
        string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy'/'MM'/'dd'+'HH':'mm':'ss", CultureInfo.InvariantCulture);
        return new Uri(GiroUrl, string.Create(CultureInfo.InvariantCulture,
            $"?ursiCode={Uri.EscapeDataString(station)}&charName=foF2,MUF(D),M(D)&DMUF=3000&fromDate={Stamp(now - Window)}&toDate={Stamp(now)}"));
    }

    /// <summary>The PROPquest request: the day of the window's start and today, which are the same day but in the small hours.</summary>
    public static Uri PropQuestQuery(IReadOnlyList<string> stations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stations);
        string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Uri(PropQuestUrl, string.Create(CultureInfo.InvariantCulture,
            $"?DATE1={Day(now - Window)}&DATE2={Day(now)}&OBSERVATORY={string.Join(',', stations.Select(Uri.EscapeDataString))}"));
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<string?> GetAsync(Source source, Uri url, CancellationToken cancellation)
    {
        DateTimeOffset now = _time.GetUtcNow();
        try
        {
            using var response = await _http.GetAsync(url, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500 || !response.IsSuccessStatusCode)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter is { } r
                    ? r.Delta ?? (r.Date is { } date ? date - now : null)
                    : null;
                Failed(source, now, string.Create(CultureInfo.InvariantCulture, $"answered {(int)response.StatusCode}"), retryAfter);
                return null;
            }
            string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (source.Failures > 0)
            {
                _log($"ionosonde: {source.Name} answers again");
            }
            source.Failures = 0;
            source.NotBefore = null;
            return body;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            Failed(source, now, e is TaskCanceledException ? "did not answer in time" : $"cannot be reached ({Plain(e.Message)})", null);
            return null;
        }
    }

    private void Failed(Source source, DateTimeOffset now, string what, TimeSpan? retryAfter)
    {
        source.Failures++;
        double minutes = PollEvery.TotalMinutes * Math.Pow(2, Math.Min(source.Failures - 1, 10));
        TimeSpan wait = TimeSpan.FromMinutes(Math.Min(minutes, MostBackoff.TotalMinutes));
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked < MostBackoff ? asked : MostBackoff;
        }
        source.NotBefore = now + wait;
        if (source.Failures == 1)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"ionosonde: {source.Name} {what}; asking again in {wait.TotalMinutes:0} min, and less often while it fails"));
        }
    }

    private static string Plain(string text)
    {
        var chars = text.Select(c => c is < ' ' or > '~' ? '?' : c).ToArray();
        return new string(chars);
    }

    private sealed class Source(string name)
    {
        public string Name { get; } = name;

        public int Failures { get; set; }

        public DateTimeOffset? NotBefore { get; set; }

        public bool MayAsk(DateTimeOffset now) => NotBefore is not { } wait || now >= wait;
    }
}
