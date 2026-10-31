using System.Collections.Concurrent;
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
public sealed class ReceiverReader
{
    /// <summary>How often the whole index is read again from the start.</summary>
    public static readonly TimeSpan ResyncEvery = TimeSpan.FromMinutes(30);

    /// <summary>Rows asked for at a time.</summary>
    public const int ObjectsPerRequest = 500;

    /// <summary>Pieces asked for at a time (the receiver's most).</summary>
    public const int PiecesPerRequest = 500;

    /// <summary>The most requests one read makes in all, of every kind: a receiver with more is caught up over several reads.</summary>
    public const int MaxRequestsPerRead = 200;

    private sealed class ObjectState
    {
        public long RowCursor;
        public long PieceCursor;
        public readonly HashSet<uint> Seen = [];
    }

    private readonly ReceiverApiClient _client;
    private readonly ObjectCatalogue _catalogue;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<ulong, ObjectState> _objects = [];
    private long _cursor;
    private bool _resync = true;
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;
    private int _requests;

    /// <summary>Makes a reader for one receiver, giving what it reads to <paramref name="catalogue"/>.</summary>
    public ReceiverReader(ReceiverApiClient client, ObjectCatalogue catalogue, TimeProvider time)
    {
        _client = client;
        _catalogue = catalogue;
        _time = time;
    }

    /// <summary>The receiver's address.</summary>
    public Uri Url => _client.BaseUrl;

    /// <summary>Objects this receiver has listed.</summary>
    public int ObjectsKnown => _objects.Count;

    /// <summary>The next read starts from the start of the index (after a failure or a long while).</summary>
    public void ReadAgainFromStart() => _resync = true;

    /// <summary>
    /// Forgets what it knows of one object, because the catalogue dropped it: when the receiver next lists it as changed, or the
    /// index is read again from the start, the object is read afresh rather than taken as already given.
    /// </summary>
    public void Forget(ulong objectId) => _objects.TryRemove(objectId, out _);

    // True while the read still has requests to spend; every request of a read, index, pieces or octets, draws on the one budget.
    private bool Spend() => _requests++ < MaxRequestsPerRead;

    /// <summary>Reads what changed. Throws <see cref="ReceiverReadException"/> if the receiver cannot be read (and the next read starts from the start).</summary>
    public async Task<ReadResult> ReadAsync(CancellationToken cancellation)
    {
        var now = _time.GetUtcNow();
        if (_resync || now - _lastFull >= ResyncEvery)
        {
            _cursor = 0;
        }
        bool full = _cursor == 0;
        int rows = 0, pieces = 0, rebuilt = 0;
        _requests = 0;
        try
        {
            while (Spend())
            {
                var page = await _client.GetObjectsAsync(_cursor, ObjectsPerRequest, cancellation).ConfigureAwait(false);
                bool finished = true;
                foreach (var row in page.Objects)
                {
                    rows++;
                    var state = _objects.GetOrAdd(row.Id, _ => new ObjectState());
                    if (_objects.Count > _catalogue.MaxObjects)
                    {
                        // Never more state than the catalogue could hold objects: an object it would not keep is not remembered as read.
                        foreach (ulong stale in _objects.Keys.Where(k => k != row.Id).Take(_objects.Count - _catalogue.MaxObjects))
                        {
                            _objects.TryRemove(stale, out _);
                        }
                    }
                    if (row.Cursor < state.RowCursor || row.Held < state.Seen.Count)
                    {
                        state.PieceCursor = 0; // numbered again from the start
                        state.Seen.Clear();
                    }
                    if (row.Cursor != state.RowCursor || row.Held != state.Seen.Count)
                    {
                        pieces += await ReadPiecesAsync(row, state, cancellation).ConfigureAwait(false);
                    }
                    if (row.Complete && !_catalogue.HasData(row.Id))
                    {
                        if (!Spend())
                        {
                            finished = false;
                            break;
                        }
                        if (await _client.GetDataAsync(row.Id, cancellation).ConfigureAwait(false) is { } data && _catalogue.SetData(row.Id, data.Dictionary, data.Data))
                        {
                            rebuilt++;
                        }
                    }
                    if (_requests > MaxRequestsPerRead)
                    {
                        finished = false; // pieces not all read: this row is looked at again
                        break;
                    }
                    _catalogue.Note(row);
                    state.RowCursor = row.Cursor;
                }
                if (!finished)
                {
                    break; // the budget is spent; the cursor stays, and the rows done are cheap to pass over next time
                }
                if (page.Next < _cursor)
                {
                    _cursor = 0; // its numbering went back (even an empty page says so): start again next time
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

    private async Task<int> ReadPiecesAsync(RemoteObject row, ObjectState state, CancellationToken cancellation)
    {
        int added = 0;
        while (Spend())
        {
            var page = await _client.GetPiecesAsync(row.Id, state.PieceCursor, PiecesPerRequest, cancellation).ConfigureAwait(false);
            if (page is null)
            {
                break; // dropped by the receiver meanwhile
            }
            foreach (var piece in page.Pieces)
            {
                state.Seen.Add(piece.Esi);
                if (_catalogue.AddPiece(row.Id, piece))
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
        return added;
    }
}
