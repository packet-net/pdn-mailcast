using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Mailcast.Receiver.Retune;

/// <summary>How a request for pdn-soundmodem's receive window went.</summary>
public enum WindowOutcome
{
    /// <summary>The rig is tuned for us (or the window renewed).</summary>
    Granted,

    /// <summary>
    /// pdn-soundmodem said no for now (HTTP 409): its transmitter is keyed, something else holds
    /// a window, or a transmit lease is held. Worth asking again later in the slot.
    /// </summary>
    Refused,

    /// <summary>pdn-soundmodem has no receive window to give (HTTP 404): it has no <c>rig</c> section.</summary>
    NoWindow,

    /// <summary>pdn-soundmodem is not answering: not running, or restarting.</summary>
    Unreachable,

    /// <summary>Anything else: a 403, a 400, a 500, or an answer that is not a receive window's.</summary>
    Failed,
}

/// <summary>What pdn-soundmodem said to a request, with the rig's dial as it says it now.</summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Why">pdn-soundmodem's reason, or ours, for anything but <see cref="WindowOutcome.Granted"/>.</param>
/// <param name="RigDialHz">The rig's dial in Hz as pdn-soundmodem's answer gives it, or null.</param>
/// <param name="WindowDialHz">The open window's dial in Hz, or null when no window is open.</param>
/// <param name="Released">For a release: whether a window was open and has ended.</param>
public sealed record WindowAnswer(WindowOutcome Outcome, string? Why, double? RigDialHz, double? WindowDialHz, bool Released = false);

/// <summary>
/// pdn-soundmodem's receive window (0.88.0 and later, <c>/rig-window</c> on its page port): a
/// program on the same machine asks it to tune its rig to a dial and hold all its transmitting
/// for a while, renews that before it ends, and releases it to put the rig back.
/// </summary>
/// <remarks>
/// Keyless from loopback, refused with an <c>Origin</c> header or a proxy's forwarding header, so
/// this asks 127.0.0.1 directly, with neither. It never reads pdn-soundmodem's config. A window
/// lasts at most 300 s at a time; pdn-soundmodem puts the rig back by itself when one runs out, so
/// a receiver that dies without releasing leaves the rig away for 5 minutes at most.
/// </remarks>
public sealed class ReceiveWindow : IDisposable
{
    /// <summary>The window's path on pdn-soundmodem's page port.</summary>
    public const string Path = "/rig-window";

    /// <summary>The longest one request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly Func<int> _port;

    /// <summary>A client for the pdn-soundmodem whose page port <paramref name="port"/> says, asked afresh for each request.</summary>
    public ReceiveWindow(Func<int> port)
    {
        _port = port;
        // pdn-soundmodem refuses anything relayed through a proxy, and a proxy from the
        // environment would never be what is wanted for 127.0.0.1 anyway.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pdn-mailcast-receiver", ReceiverHost.Version));
    }

    /// <summary>Where pdn-soundmodem is, for the log and the page: 127.0.0.1:PORT.</summary>
    public string Where => SoundModemAudioInput.Describe(_port());

    /// <summary>Asks for the rig on <paramref name="dialHz"/> with <paramref name="widthHz"/> of audio, for <paramref name="length"/>; the same again renews it.</summary>
    public Task<WindowAnswer> TakeAsync(double dialHz, int widthHz, TimeSpan length, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["dialHz"] = (long)Math.Round(dialHz),
            ["seconds"] = Math.Max(1, (int)Math.Ceiling(length.TotalSeconds)),
            ["widthHz"] = widthHz,
        };
        return SendAsync(HttpMethod.Post, body.ToJsonString(), cancellation);
    }

    /// <summary>Ends the window now, which puts the rig back.</summary>
    public Task<WindowAnswer> ReleaseAsync(CancellationToken cancellation) =>
        SendAsync(HttpMethod.Post, "{\"release\":true}", cancellation);

    /// <summary>Reads the rig's state and the window open on it, if any.</summary>
    public Task<WindowAnswer> ReadAsync(CancellationToken cancellation) =>
        SendAsync(HttpMethod.Get, null, cancellation);

    private async Task<WindowAnswer> SendAsync(HttpMethod method, string? body, CancellationToken cancellation)
    {
        string where = Where;
        using var request = new HttpRequestMessage(method, $"http://{where}{Path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        HttpStatusCode status;
        string text;
        try
        {
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (Refused(e))
        {
            return new(WindowOutcome.Unreachable, $"pdn-soundmodem is not answering on {where} (connection refused)", null, null);
        }
        catch (HttpRequestException e)
        {
            return new(WindowOutcome.Unreachable, $"cannot reach pdn-soundmodem on {where}: {Ascii.Clean(e.Message)}", null, null);
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new(WindowOutcome.Unreachable, $"pdn-soundmodem on {where} did not answer within {RequestTimeout.TotalSeconds:F0} s", null, null);
        }

        JsonObject? answer = null;
        try
        {
            answer = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        string? said = Ascii.Clean(Text(answer, "refused") ?? Text(answer, "failed") ?? "").Trim() is { Length: > 0 } s ? s : null;
        double? rigDial = Number(answer?["dialHz"]);
        double? windowDial = answer?["window"] is JsonObject window ? Number(window["dialHz"]) : null;
        int code = (int)status;
        return code switch
        {
            200 when answer is not null && answer.ContainsKey("connected") => new(WindowOutcome.Granted, null, rigDial, windowDial, Flag(answer, "released")),
            200 => new(WindowOutcome.Failed, $"pdn-soundmodem on {where} did not answer as a receive window does: it needs to be 0.88.0 or later", null, null),
            409 => new(WindowOutcome.Refused, said ?? "it said no (HTTP 409)", rigDial, windowDial),
            404 => new(WindowOutcome.NoWindow, $"pdn-soundmodem on {where} has no receive window to give: it has no \"rig\" section (rigctld), which it needs to retune the radio, or it is older than 0.88.0", null, null),
            403 => new(WindowOutcome.Failed, $"pdn-soundmodem on {where} refused the request (HTTP 403{(text.Length > 0 ? ": " + Ascii.Clean(text.Trim()) : "")})", null, null),
            _ => new(WindowOutcome.Failed, string.Create(CultureInfo.InvariantCulture, $"pdn-soundmodem on {where} answered HTTP {code}{(said is null ? "" : ": " + said)}"), rigDial, windowDial),
        };
    }

    private static string? Text(JsonObject? answer, string key)
    {
        try
        {
            return answer?[key]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Flag(JsonObject answer, string key)
    {
        try
        {
            return answer[key]?.GetValue<bool>() == true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static double? Number(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>() is { } value && value > 0 ? value : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool Refused(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
