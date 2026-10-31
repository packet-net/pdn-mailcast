using System.Globalization;
using System.Net;
using System.Text.Json;
using Mailcast.RaptorQ;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Relay;

/// <summary>One row of a receiver's index of objects (<c>GET /api/objects</c>).</summary>
public sealed record RemoteObject(
    ulong Id,
    byte? Type,
    ushort Dictionary,
    ObjectTransmissionInformation Oti,
    int K,
    int Held,
    bool Complete,
    IReadOnlyList<ObjectListing> Listings,
    long Cursor);

/// <summary>A page of <see cref="RemoteObject"/>.</summary>
public sealed record RemoteObjectPage(IReadOnlyList<RemoteObject> Objects, long Next, bool More);

/// <summary>One piece from a receiver: the whole frame, and whether it was heard on air (as against handed in) and from whom.</summary>
public sealed record RemotePiece(uint Esi, byte[] Frame, DateTimeOffset Heard, bool OnAir, string? Label, long Cursor);

/// <summary>A page of <see cref="RemotePiece"/>.</summary>
public sealed record RemotePiecePage(IReadOnlyList<RemotePiece> Pieces, long Next, bool More);

/// <summary>A receiver could not be read; <see cref="NeedsPassword"/> says it wants the page's password.</summary>
public sealed class ReceiverReadException(string message, bool needsPassword = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The receiver answered 401: its page has a password, which the head end does not have.</summary>
    public bool NeedsPassword { get; } = needsPassword;
}

/// <summary>
/// Reads a receiver's API for objects and pieces (docs/receiver.md): open GETs only, so nothing here can change a
/// receiver. What comes back is checked for shape and nothing else; whether a piece is believed is for the
/// catalogue and, later, the signed directories.
/// </summary>
public sealed class ReceiverApiClient(HttpClient http, Uri baseUrl)
{
    /// <summary>The receiver's address.</summary>
    public Uri BaseUrl { get; } = baseUrl;

    /// <summary><c>GET /api/objects?since=&amp;limit=</c>.</summary>
    public async Task<RemoteObjectPage> GetObjectsAsync(long since, int limit, CancellationToken cancellation)
    {
        using var doc = await GetJsonAsync(string.Create(CultureInfo.InvariantCulture, $"api/objects?since={since}&limit={limit}"), cancellation).ConfigureAwait(false);
        var root = doc.RootElement;
        try
        {
            var rows = new List<RemoteObject>();
            foreach (var o in root.GetProperty("objects").EnumerateArray())
            {
                if (!ObjectId.TryParse(o.GetProperty("id").GetString() ?? "", out ulong id))
                {
                    continue;
                }
                var oti = ObjectTransmissionInformation.Read(Convert.FromHexString(o.GetProperty("oti").GetProperty("hex").GetString()!));
                var listings = new List<ObjectListing>();
                if (o.TryGetProperty("listings", out var l) && l.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in l.EnumerateArray())
                    {
                        listings.Add(new ObjectListing(item.GetProperty("station").GetString() ?? "", item.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null));
                    }
                }
                else if (o.TryGetProperty("listedBy", out var by) && by.ValueKind == JsonValueKind.Array)
                {
                    // A receiver from before the listings: the stations, without the hashes.
                    listings.AddRange(by.EnumerateArray().Select(s => new ObjectListing(s.GetString() ?? "", null)));
                }
                rows.Add(new RemoteObject(
                    id,
                    o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? (byte)t.GetInt32() : null,
                    (ushort)o.GetProperty("dictionary").GetInt32(),
                    oti,
                    o.GetProperty("k").GetInt32(),
                    o.GetProperty("held").GetInt32(),
                    o.GetProperty("complete").GetBoolean(),
                    listings,
                    o.GetProperty("cursor").GetInt64()));
            }
            return new RemoteObjectPage(rows, root.GetProperty("next").GetInt64(), root.GetProperty("more").GetBoolean());
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new ReceiverReadException("its list of objects is not in the expected form", inner: e);
        }
    }

    /// <summary><c>GET /api/objects/{id}/pieces?since=&amp;limit=</c>; null if the receiver does not hold the object (any more).</summary>
    public async Task<RemotePiecePage?> GetPiecesAsync(ulong id, long since, int limit, CancellationToken cancellation)
    {
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(string.Create(CultureInfo.InvariantCulture, $"api/objects/{ObjectId.Format(id)}/pieces?since={since}&limit={limit}"), cancellation).ConfigureAwait(false);
        }
        catch (ReceiverReadException e) when (e.Data["status"] is HttpStatusCode.NotFound)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            try
            {
                var pieces = new List<RemotePiece>();
                foreach (var p in root.GetProperty("pieces").EnumerateArray())
                {
                    pieces.Add(new RemotePiece(
                        p.GetProperty("esi").GetUInt32(),
                        Convert.FromHexString(p.GetProperty("frame").GetString()!),
                        p.GetProperty("heard").GetDateTimeOffset(),
                        p.GetProperty("from").GetString() == "air",
                        p.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null,
                        p.GetProperty("cursor").GetInt64()));
                }
                return new RemotePiecePage(pieces, root.GetProperty("next").GetInt64(), root.GetProperty("more").GetBoolean());
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                throw new ReceiverReadException("its list of pieces is not in the expected form", inner: e);
            }
        }
    }

    /// <summary><c>GET /api/objects/{id}/data</c>: the rebuilt octets and their dictionary; null if the object is not complete there.</summary>
    public async Task<(byte[] Data, ushort Dictionary)?> GetDataAsync(ulong id, CancellationToken cancellation)
    {
        using var response = await SendAsync($"api/objects/{ObjectId.Format(id)}/data", cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        Check(response);
        ushort dictionary = response.Headers.TryGetValues("X-Mailcast-Dictionary", out var values) && ushort.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort d) ? d : (ushort)0;
        return (await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false), dictionary);
    }

    private async Task<HttpResponseMessage> SendAsync(string relative, CancellationToken cancellation)
    {
        try
        {
            return await http.GetAsync(new Uri(BaseUrl, relative), HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new ReceiverReadException(e is HttpRequestException { InnerException: System.Net.Sockets.SocketException s } ? s.Message : e.Message, inner: e);
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ReceiverReadException("it has a password on its page", needsPassword: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            var e = new ReceiverReadException($"it answered {(int)response.StatusCode}");
            e.Data["status"] = response.StatusCode;
            throw e;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken cancellation)
    {
        using var response = await SendAsync(relative, cancellation).ConfigureAwait(false);
        Check(response);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), cancellationToken: cancellation).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new ReceiverReadException("it did not answer with JSON", inner: e);
        }
    }
}
