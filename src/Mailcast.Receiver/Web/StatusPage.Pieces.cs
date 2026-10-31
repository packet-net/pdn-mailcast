using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Packet.Mailcast;

namespace Mailcast.Receiver.Web;

/// <summary>
/// The API for objects and pieces (issue #214): the index of objects kept, one object's pieces and
/// rebuilt octets, and a way for a program to hand frames in. The reads are open like the rest of the
/// page (they follow its password, if there is one); handing frames in needs the API key.
/// </summary>
public sealed partial class StatusPage
{
    private const string ObjectsPath = "/api/objects";
    private const string ObjectsBase = "/api/objects/";

    /// <summary>The most objects one call of <c>GET /api/objects</c> gives, and the default.</summary>
    public const int MaxObjectsPage = 1000;

    /// <summary>The default number of objects in a page of the index.</summary>
    public const int DefaultObjectsPage = 200;

    /// <summary>The most pieces one call of <c>GET /api/objects/{id}/pieces</c> gives.</summary>
    public const int MaxPiecesPage = 500;

    /// <summary>The default number of pieces in a page.</summary>
    public const int DefaultPiecesPage = 100;

    /// <summary>The most frames one <c>POST /api/pieces</c> takes.</summary>
    public const int MaxPostedFrames = 500;

    /// <summary>The largest frame, in octets, a post may hold: IL2P's 1023 less the 16 of an AX.25 UI frame's addresses, control and PID.</summary>
    public const int MaxPostedFrameLength = 1007;

    /// <summary>The largest body <c>POST /api/pieces</c> reads.</summary>
    public const int MaxPostBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The public object and piece endpoints, as method and path template, exactly as the OpenAPI document
    /// (<c>/api/openapi.json</c>) lists them. A test keeps the two together.
    /// </summary>
    internal static readonly (string Method, string Path)[] PublicApiRoutes =
    [
        ("GET", "/api/objects"),
        ("GET", "/api/objects/{id}/pieces"),
        ("GET", "/api/objects/{id}/data"),
        ("POST", "/api/pieces"),
    ];

    private static readonly Lazy<string> OpenApi = new(() => Embedded("openapi.json"));

    /// <summary>The API reference page: Scalar's standalone bundle (vendored, so no internet is needed), reading <c>openapi.json</c>.</summary>
    private const string DocsPage = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>pdn-mailcast receiver API</title>
        <meta name="viewport" content="width=device-width, initial-scale=1"></head>
        <body>
        <script id="api-reference" data-url="openapi.json" data-configuration='{"hideClientButton":true,"telemetry":false,"agent":{"disabled":true},"withDefaultFonts":false,"showDeveloperTools":"never"}'></script>
        <script src="docs/scalar.js"></script>
        </body></html>
        """;

    /// <summary>The vendored Scalar bundle, sent still gzipped (every browser takes it).</summary>
    private static async Task ServeScalarAsync(HttpListenerContext context)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Mailcast.Receiver.Web.scalar.js.gz")!;
        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes).ConfigureAwait(false);
        if (!(context.Request.Headers["Accept-Encoding"] ?? "").Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var plain = new MemoryStream();
            using (var unzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes.ToArray()), System.IO.Compression.CompressionMode.Decompress))
            {
                await unzip.CopyToAsync(plain).ConfigureAwait(false);
            }
            await RespondBytesAsync(context, 200, "text/javascript; charset=utf-8", plain.ToArray()).ConfigureAwait(false);
            return;
        }
        context.Response.Headers["Content-Encoding"] = "gzip";
        await RespondBytesAsync(context, 200, "text/javascript; charset=utf-8", bytes.ToArray()).ConfigureAwait(false);
    }

    private static Task ErrorAsync(HttpListenerContext context, int status, string message) =>
        RespondAsync(context, status, "application/json", JsonSerializer.Serialize(new { error = message }));

    private static bool TryCursor(string? text, out long value)
    {
        value = 0;
        return string.IsNullOrEmpty(text) || long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static int Limit(string? text, int fallback, int most) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int l) ? Math.Clamp(l, 1, most) : fallback;

    private static object ObjectView(ObjectRow row) => new
    {
        id = ObjectId.Format(row.Kept.ObjectId),
        type = row.ContentType,
        typeName = row.ContentType is { } t ? ContentType.Describe(t) : null,
        dictionary = row.Kept.DictionaryId,
        oti = new
        {
            transferLength = row.Kept.Oti.TransferLength,
            symbolSize = row.Kept.Oti.SymbolSize,
            sourceBlocks = row.Kept.Oti.SourceBlocks,
            subBlocks = row.Kept.Oti.SubBlocks,
            alignment = row.Kept.Oti.Alignment,
            hex = Convert.ToHexStringLower(row.Kept.Oti.ToBytes()),
        },
        k = row.Kept.Oti.SourceBlockSymbols(0),
        held = row.Kept.PiecesHeld,
        outside = row.Kept.PiecesOutside,
        complete = row.Kept.Complete,
        listedBy = row.ListedBy,
        updated = row.Kept.Updated,
        cursor = row.Kept.Cursor,
    };

    /// <summary>GET /api/objects?since=N&amp;limit=M: the index of objects kept.</summary>
    private async Task ServeObjectsAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        if (!TryCursor(query["since"], out long since))
        {
            await ErrorAsync(context, 400, "since is the cursor a previous call gave, a whole number.").ConfigureAwait(false);
            return;
        }
        var page = _host.Intake.ObjectIndex(since, Limit(query["limit"], DefaultObjectsPage, MaxObjectsPage));
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { objects = page.Rows.Select(ObjectView), next = page.Next, more = page.More }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    /// <summary>GET /api/objects/{id}/pieces and /api/objects/{id}/data.</summary>
    private async Task ServeObjectAsync(HttpListenerContext context, string rest)
    {
        var parts = rest.Split('/');
        if (parts.Length != 2 || !ObjectId.TryParse(parts[0], out ulong id) || parts[1] is not ("pieces" or "data"))
        {
            await ErrorAsync(context, 404, "Not found: ask for /api/objects/{id}/pieces or /api/objects/{id}/data, the id being 16 hex digits.").ConfigureAwait(false);
            return;
        }
        if (parts[1] == "data")
        {
            if (_host.Intake.ObjectData(id) is not { } held)
            {
                await ErrorAsync(context, 404, "That object is not complete here (or not held at all).").ConfigureAwait(false);
                return;
            }
            context.Response.Headers["X-Mailcast-Object-Id"] = ObjectId.Format(id);
            context.Response.Headers["X-Mailcast-Dictionary"] = held.DictionaryId.ToString(CultureInfo.InvariantCulture);
            await RespondBytesAsync(context, 200, "application/octet-stream", held.Data).ConfigureAwait(false);
            return;
        }
        var query = context.Request.QueryString;
        if (!TryCursor(query["since"], out long since))
        {
            await ErrorAsync(context, 400, "since is the cursor a previous call gave, a whole number.").ConfigureAwait(false);
            return;
        }
        if (_host.Intake.ObjectPieces(id, since, Limit(query["limit"], DefaultPiecesPage, MaxPiecesPage)) is not { } page)
        {
            await ErrorAsync(context, 404, "That object is not held here.").ConfigureAwait(false);
            return;
        }
        var body = new
        {
            id = ObjectId.Format(id),
            complete = page.Kept.Kept.Complete,
            k = page.Kept.Kept.Oti.SourceBlockSymbols(0),
            pieces = page.Pieces.Select(p => new
            {
                esi = p.EncodingSymbolId,
                frame = Convert.ToHexStringLower(p.Frame),
                heard = p.Heard,
                from = p.Source.Outside ? "outside" : "air",
                label = p.Source.Label,
                cursor = p.Cursor,
            }),
            next = page.Next,
            more = page.More,
        };
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(body, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    private static async Task RespondBytesAsync(HttpListenerContext context, int status, string type, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>The key a request gives: <c>Authorization: Bearer</c>, or else <c>X-Api-Key</c>; null if neither.</summary>
    internal static string? GivenApiKey(HttpListenerRequest request)
    {
        string? auth = request.Headers["Authorization"];
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth[7..].Trim();
        }
        return request.Headers["X-Api-Key"]?.Trim();
    }

    /// <summary>
    /// POST /api/pieces: <c>{"source": "label", "frames": ["hex", ...]}</c>, or just a JSON list of hex frames.
    /// The page's Host and Origin checks have been made (<see cref="Refusal"/>), so a browser on another
    /// site cannot get here; the API key is what lets a program in. It is not the page's password: a program
    /// that has the key does not need that.
    /// </summary>
    private async Task PostPiecesAsync(HttpListenerContext context)
    {
        var request = context.Request;
        if (_host.Intake.ApiKey() is not { } key)
        {
            await ErrorAsync(context, 403, "This receiver has no API key set, so it takes no pieces. Make one in the \"Sharing pieces\" section of its status page.").ConfigureAwait(false);
            return;
        }
        string? given = GivenApiKey(request);
        if (given is null)
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"pdn-mailcast receiver\"";
            await ErrorAsync(context, 401, "Give the API key as \"Authorization: Bearer KEY\" or \"X-Api-Key: KEY\".").ConfigureAwait(false);
            return;
        }
        var from = RemoteAddress(request);
        // The same wrong-guess limits as the page's password, so a key cannot be guessed at.
        var verdict = _keyThrottle.Check(from, given, key, out var wait);
        if (verdict == SignInThrottle.Verdict.Wrong)
        {
            await PauseAsync().ConfigureAwait(false);
            await ErrorAsync(context, 401, "That is not this receiver's API key.").ConfigureAwait(false);
            return;
        }
        if (verdict != SignInThrottle.Verdict.Right)
        {
            context.Response.Headers["Retry-After"] = RetryAfter(wait);
            await ErrorAsync(context, 429, TooMany(verdict, wait)).ConfigureAwait(false);
            return;
        }
        if (MediaType(request) != "application/json")
        {
            await ErrorAsync(context, 415, "Send this as application/json.").ConfigureAwait(false);
            return;
        }
        var (answered, body) = await ReadBodyAsync(context, MaxPostBytes).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        string label;
        var hex = new List<string?>();
        try
        {
            using var doc = JsonDocument.Parse(body!);
            JsonElement list = doc.RootElement;
            string? source = null;
            if (list.ValueKind == JsonValueKind.Object)
            {
                if (list.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    source = s.GetString();
                }
                if (!list.TryGetProperty("frames", out list))
                {
                    await ErrorAsync(context, 400, "Send {\"frames\": [\"hex\", ...]}.").ConfigureAwait(false);
                    return;
                }
            }
            if (list.ValueKind != JsonValueKind.Array)
            {
                await ErrorAsync(context, 400, "Send a list of frames, each as hex.").ConfigureAwait(false);
                return;
            }
            if (list.GetArrayLength() > MaxPostedFrames)
            {
                await ErrorAsync(context, 413, $"Send at most {MaxPostedFrames} frames at a time.").ConfigureAwait(false);
                return;
            }
            foreach (var item in list.EnumerateArray())
            {
                hex.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
            }
            label = PieceKeepLabel(source) ?? PieceKeepLabel(from.ToString()) ?? "outside";
        }
        catch (JsonException)
        {
            await ErrorAsync(context, 400, "That is not JSON.").ConfigureAwait(false);
            return;
        }

        var frames = new List<byte[]>();
        var slot = new int[hex.Count];
        var outcomes = new OutsideOutcome?[hex.Count];
        for (int i = 0; i < hex.Count; i++)
        {
            byte[]? frame = null;
            string? why = null;
            if (hex[i] is not { } text)
            {
                why = "a frame is a string of hex digits";
            }
            else if (text.Length > MaxPostedFrameLength * 2)
            {
                why = "too long for a frame";
            }
            else
            {
                try
                {
                    frame = Convert.FromHexString(text);
                }
                catch (FormatException)
                {
                    why = "not hex";
                }
            }
            if (frame is null)
            {
                outcomes[i] = new OutsideOutcome("notAFrame", null, null, why);
                slot[i] = -1;
            }
            else
            {
                slot[i] = frames.Count;
                frames.Add(frame);
            }
        }
        var taken = frames.Count > 0 ? _host.Intake.AcceptOutside(frames, label) : [];
        for (int i = 0; i < hex.Count; i++)
        {
            outcomes[i] ??= taken[slot[i]];
        }
        int stored = taken.Count(o => o.Kept);
        if (hex.Count > 0)
        {
            _log($"web: {hex.Count} piece(s) handed in as \"{label}\" from {from}: {stored} new");
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { source = label, accepted = stored, results = outcomes }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }

    private static string? PieceKeepLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var sb = new StringBuilder();
        foreach (char c in text.Trim())
        {
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' or ':' or '/' ? c : '_');
            if (sb.Length >= 48)
            {
                break;
            }
        }
        return sb.ToString();
    }

    /// <summary>What the page shows of the keep and the key.</summary>
    internal object SharingView()
    {
        var stats = _host.Intake.KeepStats();
        return new
        {
            objects = stats.Objects,
            pieces = stats.Pieces,
            outsidePieces = stats.OutsidePieces,
            megabytes = Math.Round(stats.Bytes / 1048576.0, 1),
            keySet = _host.Intake.ApiKey() is not null,
        };
    }

    /// <summary>GET /api/key: the key, to show it on the page. Behind the page's sign-in like everything else.</summary>
    private async Task ServeKeyAsync(HttpListenerContext context) =>
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { key = _host.Intake.ApiKey() }, ReceiverConfig.JsonLine)).ConfigureAwait(false);

    /// <summary>POST /api/key/new: makes a new key, replacing any. POST /api/key/clear: removes it.</summary>
    private async Task ChangeKeyAsync(HttpListenerContext context, bool clear)
    {
        var (answered, _) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (answered)
        {
            return;
        }
        string? key = null;
        if (clear)
        {
            _host.Intake.ClearApiKey();
            _log($"web: the API key was removed from the web page ({context.Request.RemoteEndPoint?.Address})");
        }
        else
        {
            key = _host.Intake.NewApiKey();
            _log($"web: a new API key was made from the web page ({context.Request.RemoteEndPoint?.Address})");
        }
        await RespondAsync(context, 200, "application/json", JsonSerializer.Serialize(new { key }, ReceiverConfig.JsonLine)).ConfigureAwait(false);
    }
}
