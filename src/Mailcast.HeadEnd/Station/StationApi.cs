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
    /// <summary>
    /// Whether the station tuned the rig for the lease: true or false when its answer carried a
    /// <c>tuned</c> field, null when it did not (a pdn-soundmodem that does not retune) or none was asked.
    /// </summary>
    public bool? Tuned { get; init; }

    /// <summary>The dial the station tuned to, in Hz, when it said.</summary>
    public double? DialHz { get; init; }

    /// <summary>The audio centre of the holder's modem in Hz (<c>audioCentreHz</c>), when the station tuned and said.</summary>
    public double? AudioCentreHz { get; init; }

    /// <summary>The station's note on the tuning (why it did not tune), when it gave one.</summary>
    public string? TuneNote { get; init; }

    /// <summary>True when the take was refused because the station could not tune the rig, so nothing is tuned and nothing may be sent.</summary>
    public bool TuneFailed { get; init; }

    /// <summary>
    /// What the station's 10 MHz reference feed says (<c>reference</c> in <c>GET /api/txlease</c>), or
    /// null when the answer had none: a station with no feed configured, or an older pdn-soundmodem.
    /// </summary>
    public ReferenceFeed? Reference { get; init; }

    /// <summary>A refusal or failure.</summary>
    public static LeaseAnswer No(string problem) => new(false, 0, problem);
}

/// <summary>
/// The state of the station's 10 MHz reference as its reference feed (a GPSDO display) reports it.
/// The Flex's own view of its reference is <see cref="Flex.ReferenceReading"/>, which is another thing:
/// this one caught a GPSDO that went bad while still saying it was locked.
/// </summary>
/// <param name="State"><see cref="Ok"/>, <see cref="Unreliable"/> or <see cref="Unknown"/>; any other word the station sends is read as unknown.</param>
/// <param name="Detail">The station's words on it (<c>GPS LOST</c>, <c>display offline</c>), plain ASCII, or null.</param>
/// <param name="Since">When it entered this state, when the station said.</param>
public sealed record ReferenceFeed(string State, string? Detail, DateTimeOffset? Since)
{
    /// <summary>The reference is good.</summary>
    public const string Ok = "ok";

    /// <summary>The reference is bad: nothing is sent while it is.</summary>
    public const string Unreliable = "unreliable";

    /// <summary>Nobody knows: the slot goes, marked.</summary>
    public const string Unknown = "unknown";

    /// <summary>The longest detail kept from the station.</summary>
    private const int DetailLength = 80;

    public bool IsOk => State == Ok;

    public bool IsUnreliable => State == Unreliable;

    public bool IsUnknown => State == Unknown;

    /// <summary>The state with the detail in brackets, for the journal: <c>unreliable (GPS LOST)</c>.</summary>
    public string Describe() => Detail is null ? State : $"{State} ({Detail})";

    /// <summary>The detail, or "no detail given", for a journal line's brackets.</summary>
    public string DetailOrNone => Detail ?? "no detail given";

    /// <summary>
    /// Reads the <c>reference</c> object of a lease answer, or null when there is none (or it is JSON
    /// null). Anything else that is not an object reads as unknown. The detail is cut to plain
    /// printable ASCII, since it goes into journal lines.
    /// </summary>
    public static ReferenceFeed? From(JsonNode? json)
    {
        if (json is not JsonObject answer || !answer.TryGetPropertyValue("reference", out JsonNode? value) || value is null)
        {
            return null;
        }
        if (value is not JsonObject reference)
        {
            return new ReferenceFeed(Unknown, "the station's reference field could not be read", null);
        }
        string state = reference["state"] is JsonNode s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>().Trim().ToLowerInvariant() : "";
        state = state is Ok or Unreliable ? state : Unknown;
        string? detail = null;
        if (reference["detail"] is JsonNode d && d.GetValueKind() == JsonValueKind.String)
        {
            string plain = new string([.. d.GetValue<string>().Select(c => c is >= ' ' and <= '~' ? c : ' ')]).Trim();
            detail = plain.Length == 0 ? null : plain.Length > DetailLength ? plain[..DetailLength] : plain;
        }
        DateTimeOffset? since = reference["since"] is JsonNode t && t.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(t.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;
        return new ReferenceFeed(state, detail, since);
    }
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

    /// <summary>
    /// The marker the station says it sent (<c>marker</c> in its answer), or null. A station that knows
    /// the marker echoes it only when it sent it; an older one ignores the field and sends the plain tone.
    /// </summary>
    public string? Marker { get; init; }
}

/// <summary>The markers a tone request may ask for instead of the steady tone (<c>marker</c> in <c>POST /api/txtest</c>).</summary>
public static class ToneMarker
{
    /// <summary>A run of Morse E (dits) at the tone's audio frequency for the tone's length: the slot's reference is not known to be good.</summary>
    public const string CwE = "cw-e";
}

/// <summary>A channel probe to follow the tone, as <c>POST /api/txtest</c> takes it.</summary>
/// <param name="Kind">The probe's kind, <c>zc255</c>.</param>
/// <param name="GapSeconds">The silence between the tone and the probe.</param>
/// <param name="AudioHz">The probe's audio centre.</param>
public sealed record ProbeRequest(string Kind, double GapSeconds, double AudioHz);

/// <summary>What the station said to a request to keep a stretch of its receive audio.</summary>
/// <param name="Kept">True when the station took the window.</param>
/// <param name="Status">The HTTP status, or null when the request itself failed.</param>
/// <param name="Directory">Where the station will write the window, when kept.</param>
/// <param name="Problem">Why not, in the station's words or ours, when <paramref name="Kept"/> is false.</param>
/// <param name="Note">Why the window starts later than asked (the station had not been listening that long), or null.</param>
public sealed record KeepAudioAnswer(bool Kept, int? Status, string? Directory, string? Problem, string? Note = null)
{
    /// <summary>
    /// True when the station does not offer the call at all: a pdn-soundmodem from before
    /// <c>/api/keepaudio</c>, or one with no <c>api.key</c>, answers 404.
    /// </summary>
    public bool NotOffered => Status == 404;
}

/// <summary>The parts of pdn-soundmodem's HTTP API the head end uses.</summary>
public interface IStationApi
{
    /// <summary>
    /// Takes or renews the transmit lease for a sub-channel (<c>POST /api/txlease</c>), telling the
    /// station to send the holder's frames anyway once they have waited
    /// <paramref name="maxCarrierWaitSeconds"/> for a clear channel.
    /// </summary>
    /// <remarks>
    /// <paramref name="tune"/> adds <c>"tune": true</c>: the station moves the rig so the holder's modem sits
    /// at its own dial for as long as the lease is held (sent with every take, renewals included, so a late renewal that becomes a fresh take is tuned too).
    /// </remarks>
    Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, bool tune, CancellationToken cancellation);

    /// <summary>Reads the lease, the channel-busy flag and the reference feed, when the station has one (<c>GET /api/txlease</c>).</summary>
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
    /// With <paramref name="marker"/> (<see cref="ToneMarker.CwE"/>) the station sends that marker instead
    /// of the steady tone, for the same time at the same frequency, and echoes the marker in its answer
    /// (<see cref="ToneAnswer.Marker"/>); an older station ignores it and sends the plain tone.
    /// </summary>
    Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, ProbeRequest? probe, string? marker, CancellationToken cancellation);

    /// <summary>
    /// Asks the station to keep its receive audio from <paramref name="keepFrom"/> to <paramref name="keepTo"/>
    /// (<c>POST /api/keepaudio</c>), with its carrier sense second by second and a waterfall, in a
    /// directory named after <paramref name="label"/>. The station holds the last few minutes in
    /// memory, so the window may start in the past. Answers once the request is taken or refused,
    /// not when the window is written.
    /// </summary>
    Task<KeepAudioAnswer> KeepAudioAsync(DateTimeOffset keepFrom, DateTimeOffset keepTo, string label, CancellationToken cancellation);
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
    public async Task<LeaseAnswer> TakeLeaseAsync(int subChannel, int seconds, int maxCarrierWaitSeconds, bool tune, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["subChannel"] = subChannel,
            ["seconds"] = seconds,
            ["maxCarrierWaitSeconds"] = maxCarrierWaitSeconds,
        };
        if (tune)
        {
            body["tune"] = true;
        }
        var (status, json, text) = await LeaseCallAsync(HttpMethod.Post, body, cancellation).ConfigureAwait(false);
        bool? busy = Busy(json);
        if (status == HttpStatusCode.OK && json?["held"]?.GetValue<bool>() == true)
        {
            double granted = json["seconds"] is JsonNode s ? s.GetValue<double>() : seconds;
            return new LeaseAnswer(true, granted, null, busy)
            {
                Tuned = json["tuned"] is JsonNode t && t.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? t.GetValue<bool>() : null,
                AudioCentreHz = json["audioCentreHz"] is JsonNode c && c.GetValueKind() == JsonValueKind.Number ? c.GetValue<double>() : null,
                DialHz = json["dialHz"] is JsonNode d && d.GetValueKind() == JsonValueKind.Number ? d.GetValue<double>() : null,
                TuneNote = Text(json, "note"),
            };
        }
        bool tuneFailed = json?["tuneFailed"] is JsonNode tf && tf.GetValueKind() == JsonValueKind.True;
        if (tuneFailed)
        {
            return new LeaseAnswer(false, 0, $"HTTP {(int)status}: {Describe(json, text)}", busy) { TuneFailed = true };
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
            ? new LeaseAnswer(json?["held"]?.GetValue<bool>() == true, 0, null, Busy(json)) { Reference = ReferenceFeed.From(json) }
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
    public async Task<ToneAnswer> SendToneAsync(int subChannel, double toneHz, double seconds, ProbeRequest? probe, string? marker, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["twoTone"] = false,
            ["toneHz"] = toneHz,
            ["seconds"] = seconds,
            ["subChannel"] = subChannel,
        };
        if (marker is not null)
        {
            body["marker"] = marker;
        }
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
                Marker = Text(json, "marker"),
            };
        }
        string why = Describe(json, text);
        ToneOutcome outcome = status == HttpStatusCode.Conflict || status == HttpStatusCode.NotFound ? ToneOutcome.Refused : ToneOutcome.Failed;
        return new ToneAnswer(outcome, $"HTTP {(int)status}: {why}") { Status = (int)status, KnowsProbe = knowsProbe };
    }

    /// <inheritdoc />
    public async Task<KeepAudioAnswer> KeepAudioAsync(DateTimeOffset keepFrom, DateTimeOffset keepTo, string label, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["from"] = Utc(keepFrom),
            ["to"] = Utc(keepTo),
            ["label"] = label,
        };
        HttpStatusCode status;
        JsonNode? json;
        string text;
        try
        {
            (status, json, text) = await SendAsync(HttpMethod.Post, "api/keepaudio", body, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new KeepAudioAnswer(false, null, null, e.Message);
        }
        if (status == HttpStatusCode.OK && Text(json, "directory") is string directory)
        {
            return new KeepAudioAnswer(true, (int)status, directory, null, Text(json, "note"));
        }
        return new KeepAudioAnswer(false, (int)status, null, $"HTTP {(int)status}: {Describe(json, text)}");
    }

    /// <summary>A time as the station's API writes them: UTC, to the second, with a Z.</summary>
    internal static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? json, string key) =>
        json?[key] is JsonNode value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

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
