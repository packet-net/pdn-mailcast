using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mailcast.RaptorQ;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// A stand-in for a receiver's API for objects and pieces (docs/receiver.md), answering with the same JSON
/// the real StatusPage gives, so the head end's reading can be tried with cursors the test controls.
/// </summary>
internal sealed class FakeReceiver : IDisposable
{
    internal sealed class Obj
    {
        public required TransferObject Source;
        public long Cursor;
        public bool Complete;
        public List<(uint Esi, bool Air, string? Label, long Cursor)> Pieces = [];
        public List<(string Station, string? Hash)> Listings = [];
        public byte[]? DataOverride;
    }

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private long _next = 100;

    public FakeReceiver(int? port = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            int p = port ?? FreePort();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{p}/");
            try
            {
                _listener.Start();
                Port = p;
                break;
            }
            catch (HttpListenerException) when (port is null && attempt < 20)
            {
            }
        }
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public Uri Url => new($"http://127.0.0.1:{Port}/");

    public List<Obj> Objects { get; } = [];

    public List<string> Requests { get; } = [];

    public bool Password { get; set; }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static TransferObject Make(int seed, int size = 2000, byte type = 0x71)
    {
        var content = new byte[size];
        new Random(seed).NextBytes(content);
        return TransferObject.ForContent(type, content, default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
    }

    public long Tick() => _next++;

    public Obj Add(TransferObject o, IEnumerable<(uint Esi, bool Air, string? Label)> pieces, bool complete = false)
    {
        var obj = new Obj { Source = o, Cursor = Tick(), Complete = complete };
        obj.Pieces.AddRange(pieces.Select(p => (p.Esi, p.Air, p.Label, Tick())));
        Objects.Add(obj);
        return obj;
    }

    public void AddPiece(Obj o, uint esi, bool air, string? label)
    {
        long c = Tick();
        o.Pieces.RemoveAll(p => p.Esi == esi);
        o.Pieces.Add((esi, air, label, c));
        o.Cursor = c;
    }

    /// <summary>Forgets everything and numbers it again from 1, as a receiver whose state was cleared would.</summary>
    public void Wipe()
    {
        Objects.Clear();
        _next = 1;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => Serve(context));
        }
    }

    private static int IntOf(string? text, int fallback) => int.TryParse(text, out int v) ? v : fallback;

    private void Serve(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath;
        lock (Requests)
        {
            Requests.Add(path + context.Request.Url.Query);
        }
        try
        {
            if (Password)
            {
                Reply(context, 401, "application/json", """{"error":"Sign in first: this page needs its password."}""");
                return;
            }
            var query = context.Request.QueryString;
            long since = long.TryParse(query["since"], out long s) ? s : 0;
            int limit = IntOf(query["limit"], 100);
            if (path == "/api/objects")
            {
                Obj[] rows;
                lock (Objects)
                {
                    rows = [.. Objects.Where(o => o.Cursor > since).OrderBy(o => o.Cursor)];
                }
                bool more = rows.Length > limit;
                rows = [.. rows.Take(limit)];
                var body = new
                {
                    objects = rows.Select(o => new
                    {
                        id = ObjectId.Format(o.Source.ObjectId),
                        type = (int?)o.Source.Bytes[0],
                        typeName = "x",
                        dictionary = (int)o.Source.DictionaryId,
                        oti = new { hex = Convert.ToHexStringLower(o.Source.Oti.ToBytes()) },
                        k = o.Source.SourceSymbols,
                        held = o.Pieces.Count,
                        outside = o.Pieces.Count(p => !p.Air),
                        complete = o.Complete,
                        listedBy = o.Listings.Select(l => l.Station),
                        listings = o.Listings.Select(l => new { station = l.Station, hash = l.Hash }),
                        updated = DateTimeOffset.UnixEpoch,
                        cursor = o.Cursor,
                    }),
                    next = rows.Length > 0 ? rows[^1].Cursor : since,
                    more,
                };
                Reply(context, 200, "application/json", JsonSerializer.Serialize(body));
                return;
            }
            var parts = path["/api/objects/".Length..].Split('/');
            var found = path.StartsWith("/api/objects/", StringComparison.Ordinal) && parts.Length == 2 && ObjectId.TryParse(parts[0], out ulong id)
                ? Objects.FirstOrDefault(o => o.Source.ObjectId == id) : null;
            if (found is null)
            {
                Reply(context, 404, "application/json", """{"error":"not held"}""");
            }
            else if (parts[1] == "data")
            {
                if (!found.Complete)
                {
                    Reply(context, 404, "application/json", """{"error":"not complete"}""");
                    return;
                }
                context.Response.Headers["X-Mailcast-Dictionary"] = found.Source.DictionaryId.ToString();
                Reply(context, 200, "application/octet-stream", found.DataOverride ?? found.Source.Bytes.ToArray());
            }
            else
            {
                var all = found.Pieces.Where(p => p.Cursor > since).OrderBy(p => p.Cursor).ToList();
                var page = all.Take(limit).ToList();
                var body = new
                {
                    id = ObjectId.Format(found.Source.ObjectId),
                    complete = found.Complete,
                    k = found.Source.SourceSymbols,
                    pieces = page.Select(p => new
                    {
                        esi = p.Esi,
                        frame = Convert.ToHexStringLower(found.Source.Frame(p.Esi).ToBytes()),
                        heard = DateTimeOffset.UnixEpoch,
                        from = p.Air ? "air" : "outside",
                        label = p.Label,
                        cursor = p.Cursor,
                    }),
                    next = page.Count > 0 ? page[^1].Cursor : since,
                    more = all.Count > page.Count,
                };
                Reply(context, 200, "application/json", JsonSerializer.Serialize(body));
            }
        }
        catch (Exception e) when (e is HttpListenerException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static void Reply(HttpListenerContext context, int status, string type, object body)
    {
        byte[] bytes = body as byte[] ?? Encoding.UTF8.GetBytes((string)body);
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
