using Packet.Mailcast;

namespace Mailcast.HeadEnd.Relay;

/// <summary>What one read of a receiver found.</summary>
/// <param name="Objects">Index rows seen.</param>
/// <param name="NewPieces">Pieces that were new to the catalogue.</param>
/// <param name="NewObjects">Rebuilt objects newly fetched.</param>
public readonly record struct ReadResult(int Objects, int NewPieces, int NewObjects);

/// <summary>
/// Follows one receiver: keeps the cursor on its index and one on each object's pieces, and on each read gives
/// the catalogue what changed. Only what changed is fetched. A receiver whose numbering goes back (its state
/// was cleared, so cursors start again) is noticed by a row with a lower cursor than last seen or a smaller
/// count of pieces than the reader has had from it, and that object is read again from the start; and every
/// <see cref="ResyncEvery"/>, and after any failure, the whole index is read again from the start, which
/// costs one request per 500 objects when nothing changed.
/// </summary>
public sealed class ReceiverReader(ReceiverApiClient client, ObjectCatalogue catalogue, TimeProvider time)
{
    /// <summary>How often the whole index is read again from the start.</summary>
    public static readonly TimeSpan ResyncEvery = TimeSpan.FromMinutes(30);

    /// <summary>Rows asked for at a time.</summary>
    public const int ObjectsPerRequest = 500;

    /// <summary>Pieces asked for at a time (the receiver's most).</summary>
    public const int PiecesPerRequest = 500;

    // A read stops after this many requests: a receiver with more is caught up over several reads.
    private const int MaxRequestsPerRead = 200;

    private sealed class ObjectState
    {
        public long RowCursor;
        public long PieceCursor;
        public readonly HashSet<uint> Seen = [];
    }

    private readonly Dictionary<ulong, ObjectState> _objects = [];
    private long _cursor;
    private bool _resync = true;
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;

    /// <summary>The receiver's address.</summary>
    public Uri Url => client.BaseUrl;

    /// <summary>Objects this receiver has listed.</summary>
    public int ObjectsKnown => _objects.Count;

    /// <summary>The next read starts from the start of the index (after a failure or a long while).</summary>
    public void ReadAgainFromStart() => _resync = true;

    /// <summary>Reads what changed. Throws <see cref="ReceiverReadException"/> if the receiver cannot be read (and the next read starts from the start).</summary>
    public async Task<ReadResult> ReadAsync(CancellationToken cancellation)
    {
        var now = time.GetUtcNow();
        if (_resync || now - _lastFull >= ResyncEvery)
        {
            _cursor = 0;
        }
        bool full = _cursor == 0;
        int rows = 0, pieces = 0, rebuilt = 0, requests = 0;
        try
        {
            while (requests++ < MaxRequestsPerRead)
            {
                var page = await client.GetObjectsAsync(_cursor, ObjectsPerRequest, cancellation).ConfigureAwait(false);
                foreach (var row in page.Objects)
                {
                    rows++;
                    catalogue.Note(row);
                    var state = _objects.TryGetValue(row.Id, out var s) ? s : _objects[row.Id] = new ObjectState();
                    if (row.Cursor < state.RowCursor || row.Held < state.Seen.Count)
                    {
                        state.PieceCursor = 0; // numbered again from the start
                        state.Seen.Clear();
                    }
                    if (row.Cursor != state.RowCursor || row.Held != state.Seen.Count)
                    {
                        (int added, int used) = await ReadPiecesAsync(row, state, cancellation).ConfigureAwait(false);
                        pieces += added;
                        requests += used;
                    }
                    state.RowCursor = row.Cursor;
                    if (row.Complete && !catalogue.HasData(row.Id) && await client.GetDataAsync(row.Id, cancellation).ConfigureAwait(false) is { } data)
                    {
                        requests++;
                        if (catalogue.SetData(row.Id, data.Dictionary, data.Data))
                        {
                            rebuilt++;
                        }
                    }
                }
                if (page.Objects.Count > 0 && page.Next < _cursor)
                {
                    _cursor = 0; // its numbering went back: start again next time
                    break;
                }
                _cursor = page.Next;
                if (!page.More)
                {
                    if (full)
                    {
                        _lastFull = now;
                        _resync = false;
                    }
                    break;
                }
            }
        }
        catch (ReceiverReadException)
        {
            _resync = true;
            throw;
        }
        return new ReadResult(rows, pieces, rebuilt);
    }

    private async Task<(int Added, int Requests)> ReadPiecesAsync(RemoteObject row, ObjectState state, CancellationToken cancellation)
    {
        int added = 0, requests = 0;
        while (requests++ < MaxRequestsPerRead)
        {
            var page = await client.GetPiecesAsync(row.Id, state.PieceCursor, PiecesPerRequest, cancellation).ConfigureAwait(false);
            if (page is null)
            {
                break; // dropped by the receiver meanwhile
            }
            foreach (var piece in page.Pieces)
            {
                state.Seen.Add(piece.Esi);
                if (catalogue.AddPiece(row.Id, piece))
                {
                    added++;
                }
            }
            state.PieceCursor = page.Next;
            if (!page.More)
            {
                break;
            }
        }
        return (added, requests);
    }
}
