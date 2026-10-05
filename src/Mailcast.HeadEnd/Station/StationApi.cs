using System.Globalization;
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
public sealed record LeaseAnswer(bool Held, double Seconds, string? Problem)
{
    /// <summary>A refusal or failure.</summary>
    public static LeaseAnswer No(string problem) => new(false, 0, problem);
}

/// <summary>How a calibration tone request ended.</summary>
public enum ToneOutcome
{
    /// <summary>The tone went out.</summary>
    Sent,

    /// <summary>The channel did not clear within the station's own wait, so nothing was sent.</summary>
    ChannelBusy,

    /// <summary>The station would not send it, for some other reason.</summary>
    Refused,

    /// <summary>The request or the station failed.</summary>
    Failed,
}

/// <summary>What the station said to a tone request.</summary>
public sealed record ToneAnswer(ToneOutcome Outcome, string Message);

/// <summary>The parts of pdn-soundmodem's HTTP API the head end uses.</summary>
public interface IStationApi
{
    /// <summary>Takes or renews the transmit lease for a sub-channel (<c>POST /api/txlease</c>).</summary>
    Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, CancellationToken cancellation);

    /// <summary>Gives the lease back (<c>POST /api/txlease</c> with <c>release</c>).</summary>
    Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation);

    /// <summary>
    /// Sends a single tone as the lease holder (<c>POST /api/txtest</c> naming the sub-channel).
    /// The station waits for a clear channel first, up to its own limit (60 s), and answers when
    /// the tone is over.
    /// </summary>
    Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, CancellationToken cancellation);
}

/// <summary><see cref="IStationApi"/> over HTTP.</summary>
public sealed class StationApiClient : IStationApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>Talks to the station at <paramref name="baseUrl"/>, such as http://127.0.0.1:8107/.</summary>
    public StationApiClient(Uri baseUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseUrl;
        // A tone request is answered when the tone is over, after up to a minute's wait for a
        // clear channel; the slot's own deadlines bound everything else.
        _http.Timeout = TimeSpan.FromMinutes(4);
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public async Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, CancellationToken cancellation)
    {
        var body = new JsonObject { ["subChannel"] = subChannel, ["seconds"] = seconds };
        var (status, json, text) = await PostAsync("api/txlease", body, cancellation).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && json?["held"]?.GetValue<bool>() == true)
        {
            double granted = json["seconds"] is JsonNode s ? s.GetValue<double>() : seconds;
            return new LeaseAnswer(true, granted, null);
        }
        if (status == HttpStatusCode.Conflict && json?["subChannel"] is JsonNode holder)
        {
            return LeaseAnswer.No($"sub-channel {holder.ToJsonString()} holds the transmit lease until {json["expires"]?.ToString() ?? "unknown"}");
        }
        return LeaseAnswer.No($"HTTP {(int)status}: {Describe(json, text)}");
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseLeaseAsync(int subChannel, CancellationToken cancellation)
    {
        var body = new JsonObject { ["release"] = true, ["subChannel"] = subChannel };
        var (status, json, _) = await PostAsync("api/txlease", body, cancellation).ConfigureAwait(false);
        return status == HttpStatusCode.OK && json?["released"]?.GetValue<bool>() == true;
    }

    /// <inheritdoc />
    public async Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["twoTone"] = false,
            ["toneHz"] = toneHz,
            ["seconds"] = seconds,
            ["subChannel"] = subChannel,
        };
        HttpStatusCode status;
        JsonNode? json;
        string text;
        try
        {
            (status, json, text) = await PostAsync("api/txtest", body, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new ToneAnswer(ToneOutcome.Failed, e.Message);
        }

        if (status == HttpStatusCode.OK && json?["transmitted"]?.GetValue<bool>() == true)
        {
            return new ToneAnswer(ToneOutcome.Sent, json["sent"]?.ToString() ?? "sent");
        }
        string why = Describe(json, text);
        if (status == HttpStatusCode.Conflict && why.Contains("did not clear", StringComparison.OrdinalIgnoreCase))
        {
            return new ToneAnswer(ToneOutcome.ChannelBusy, why);
        }
        return status == HttpStatusCode.Conflict || status == HttpStatusCode.NotFound
            ? new ToneAnswer(ToneOutcome.Refused, $"HTTP {(int)status}: {why}")
            : new ToneAnswer(ToneOutcome.Failed, $"HTTP {(int)status}: {why}");
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json, string Text)> PostAsync(string path, JsonObject body, CancellationToken cancellation)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(path, UriKind.Relative), content, cancellation).ConfigureAwait(false);
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
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    internal static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture);
}
