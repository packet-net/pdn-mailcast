using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailcast.HeadEnd.Station;

/// <summary>What the station said to a lease request.</summary>
/// <param name="Held">True when the lease is ours.</param>
/// <param name="Seconds">How long it was granted for, which the station may have capped.</param>
/// <param name="Problem">Why not, in the station's words or ours, when <paramref name="Held"/> is false.</param>
/// <param name="ChannelBusy">The station's carrier sense for the holder's sub-channel; null if it did not say.</param>
public sealed record LeaseAnswer(bool Held, double Seconds, string? Problem, bool? ChannelBusy = null)
{
    /// <summary>A refusal or failure.</summary>
    public static LeaseAnswer No(string problem) => new(false, 0, problem);
}

/// <summary>How a calibration tone request ended.</summary>
public enum ToneOutcome
{
    /// <summary>The tone went out.</summary>
    Sent,

    /// <summary>The station would not send it; whether that was a busy channel is the lease's channel-busy flag.</summary>
    Refused,

    /// <summary>The request or the station failed.</summary>
    Failed,
}

/// <summary>What the station said to a tone request.</summary>
public sealed record ToneAnswer(ToneOutcome Outcome, string Message)
{
    /// <summary>The HTTP status, or null when the request itself failed.</summary>
    public int? Status { get; init; }

    /// <summary>
    /// True when the answer carried the probe's keys (<c>probe</c> and <c>probeComplete</c>), as
    /// pdn-soundmodem 0.87.2 and later always do. An older station leaves them out and sends the tone alone.
    /// </summary>
    public bool KnowsProbe { get; init; }

    /// <summary>The probe's id when all of it went out, else null.</summary>
    public string? ProbeId { get; init; }

    /// <summary>True when all of the probe went out, false when it was cut short or kept off the air, null when the station did not say.</summary>
    public bool? ProbeComplete { get; init; }
}

/// <summary>A channel probe to follow the tone, as <c>POST /api/txtest</c> takes it.</summary>
/// <param name="Kind">The probe's kind, <c>zc255</c>.</param>
/// <param name="GapSeconds">The silence between the tone and the probe.</param>
/// <param name="AudioHz">The probe's audio centre.</param>
public sealed record ProbeRequest(string Kind, double GapSeconds, double AudioHz);

/// <summary>The parts of pdn-soundmodem's HTTP API the head end uses.</summary>
public interface IStationApi
{
    /// <summary>
    /// Takes or renews the transmit lease for a sub-channel (<c>POST /api/txlease</c>), telling the
    /// station to send the holder's frames anyway once they have waited
    /// <paramref name="maxCarrierWaitSeconds"/> for a clear channel.
    /// </summary>
    Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, CancellationToken cancellation);

    /// <summary>Reads the lease and the channel-busy flag (<c>GET /api/txlease</c>).</summary>
    Task<LeaseAnswer> ReadLeaseAsync(CancellationToken cancellation);

    /// <summary>
    /// Gives the lease back, dropping any of the holder's frames not yet keyed
    /// (<c>{"release": true, "dropQueued": true}</c>). The station answers at once, then keeps the
    /// lease "closing" for up to 60 s while the holder's closing ident goes out.
    /// </summary>
    Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation);

    /// <summary>Drops the holder's frames not yet keyed (<c>{"dropQueued": true}</c>), keeping the lease.</summary>
    Task<bool> DropQueuedAsync(int subChannel, CancellationToken cancellation);

    /// <summary>
    /// Sends a single tone as the lease holder (<c>POST /api/txtest</c> naming the sub-channel),
    /// followed in the same keyup by <paramref name="probe"/> when one is given. The station waits
    /// for a clear channel first, up to its own limit (60 s), and answers when the keyup is over.
    /// </summary>
    Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, ProbeRequest? probe, CancellationToken cancellation);
}

/// <summary><see cref="IStationApi"/> over HTTP.</summary>
public sealed class StationApiClient : IStationApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _leaseCallLimit;

    /// <summary>Talks to the station at <paramref name="baseUrl"/>, such as http://127.0.0.1:8107/.</summary>
    /// <param name="baseUrl">The station page's address.</param>
    /// <param name="apiKey">The station's api.key.</param>
    /// <param name="leaseCallLimit">
    /// How long one lease call may take: about the renewal interval, so a station that stops
    /// answering is noticed before the lease it granted runs out.
    /// </param>
    /// <param name="handler">For tests.</param>
    public StationApiClient(Uri baseUrl, string apiKey, TimeSpan leaseCallLimit, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        _leaseCallLimit = leaseCallLimit;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUrl;
        // A tone request is answered when the tone is over, after up to a minute's wait for a
        // clear channel; the slot's own deadlines bound everything else.
        _http.Timeout = TimeSpan.FromMinutes(4);
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public async Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["subChannel"] = subChannel,
            ["seconds"] = seconds,
            ["maxCarrierWaitSeconds"] = maxCarrierWaitSeconds,
        };
        var (status, json, text) = await LeaseCallAsync(HttpMethod.Post, body, cancellation).ConfigureAwait(false);
        bool? busy = Busy(json);
        if (status == HttpStatusCode.OK && json?["held"]?.GetValue<bool>() == true)
        {
            double granted = json["seconds"] is JsonNode s ? s.GetValue<double>() : seconds;
            return new LeaseAnswer(true, granted, null, busy);
        }
        if (status == HttpStatusCode.Conflict && json?["subChannel"] is JsonNode holder)
        {
            return new LeaseAnswer(false, 0, $"sub-channel {holder.ToJsonString()} holds the transmit lease until {json["expires"]?.ToString() ?? "unknown"}", busy);
        }
        return new LeaseAnswer(false, 0, $"HTTP {(int)status}: {Describe(json, text)}", busy);
    }

    /// <inheritdoc />
    public async Task<LeaseAnswer> ReadLeaseAsync(CancellationToken cancellation)
    {
        var (status, json, text) = await LeaseCallAsync(HttpMethod.Get, null, cancellation).ConfigureAwait(false);
        return status == HttpStatusCode.OK
            ? new LeaseAnswer(json?["held"]?.GetValue<bool>() == true, 0, null, Busy(json))
            : LeaseAnswer.No($"HTTP {(int)status}: {Describe(json, text)}");
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation)
    {
        var body = new JsonObject { ["release"] = true, ["subChannel"] = subChannel, ["dropQueued"] = true };
        // Answered at once; the lease then stays "closing" for up to 60 s while the closing ident goes.
        var (status, json, _) = await LeaseCallAsync(HttpMethod.Post, body, cancellation).ConfigureAwait(false);
        return status == HttpStatusCode.OK && json?["released"]?.GetValue<bool>() == true;
    }

    /// <inheritdoc />
    public async Task<bool> DropQueuedAsync(int subChannel, CancellationToken cancellation)
    {
        var body = new JsonObject { ["dropQueued"] = true, ["subChannel"] = subChannel };
        var (status, _, _) = await LeaseCallAsync(HttpMethod.Post, body, cancellation).ConfigureAwait(false);
        return status == HttpStatusCode.OK;
    }

    /// <inheritdoc />
    public async Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, ProbeRequest? probe, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["twoTone"] = false,
            ["toneHz"] = toneHz,
            ["seconds"] = seconds,
            ["subChannel"] = subChannel,
        };
        if (probe is not null)
        {
            body["probe"] = new JsonObject
            {
                ["kind"] = probe.Kind,
                ["gapSeconds"] = probe.GapSeconds,
                ["audioHz"] = probe.AudioHz,
            };
        }
        HttpStatusCode status;
        JsonNode? json;
        string text;
        try
        {
            (status, json, text) = await SendAsync(HttpMethod.Post, "api/txtest", body, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new ToneAnswer(ToneOutcome.Failed, e.Message);
        }

        bool knowsProbe = json is JsonObject answer && answer.ContainsKey("probe") && answer.ContainsKey("probeComplete");
        string? probeId = knowsProbe && json!["probe"] is JsonNode id && id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : null;
        bool? probeComplete = knowsProbe && json!["probeComplete"] is JsonNode done && done.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? done.GetValue<bool>() : null;
        if (status == HttpStatusCode.OK && json?["transmitted"]?.GetValue<bool>() == true)
        {
            return new ToneAnswer(ToneOutcome.Sent, json["sent"]?.ToString() ?? "sent")
            {
                Status = (int)status,
                KnowsProbe = knowsProbe,
                ProbeId = probeId,
                ProbeComplete = probeComplete,
            };
        }
        string why = Describe(json, text);
        ToneOutcome outcome = status == HttpStatusCode.Conflict || status == HttpStatusCode.NotFound ? ToneOutcome.Refused : ToneOutcome.Failed;
        return new ToneAnswer(outcome, $"HTTP {(int)status}: {why}") { Status = (int)status, KnowsProbe = knowsProbe };
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json, string Text)> LeaseCallAsync(HttpMethod method, JsonObject? body, CancellationToken cancellation)
    {
        TimeSpan allowed = _leaseCallLimit;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(allowed);
        try
        {
            return await SendAsync(method, "api/txlease", body, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new HttpRequestException($"the station did not answer within {allowed.TotalSeconds:0} s");
        }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json, string Text)> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        JsonNode? json = null;
        try
        {
            json = text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
        }
        return (response.StatusCode, json, text);
    }

    private static bool? Busy(JsonNode? json) =>
        json?["channelBusy"] is JsonNode b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? b.GetValue<bool>() : null;

    private static string Describe(JsonNode? json, string text)
    {
        foreach (string key in new[] { "refused", "failed", "error", "note" })
        {
            if (json?[key] is JsonNode value && value.GetValueKind() == JsonValueKind.String)
            {
                return value.GetValue<string>();
            }
        }
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? "no explanation" : trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
