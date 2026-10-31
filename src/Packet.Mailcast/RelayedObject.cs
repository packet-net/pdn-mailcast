using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>How a relayed object is listed in the head end's directory: the fields the original listing would give, taken from the checked object.</summary>
/// <param name="Bid">The bulletin ID for a bulletin; the object ID in hex for any other type.</param>
/// <param name="Title">The subject line for a bulletin; a plain description of the type for any other.</param>
/// <param name="Size">The serialised bulletin's length, or the object's octets for any other type.</param>
public sealed record RelayListing(string Bid, string Title, int Size);

/// <summary>
/// What this head end has already sent of an object it relays (issue #218): every ESI of its own, so that it never
/// repeats one, when it first sent a piece, and how far its envelope has got. Kept by <see cref="HeadEndStore"/>.
/// </summary>
/// <param name="Sent">The ESIs this head end has sent of the object, unchanged and fresh alike.</param>
/// <param name="FirstSlot">The slot it first sent a piece in; null until it has.</param>
/// <param name="EnvelopeNextEsi">The first envelope ESI not yet sent.</param>
public sealed record RelayProgress(IReadOnlySet<uint> Sent, DateTimeOffset? FirstSlot, uint EnvelopeNextEsi)
{
    /// <summary>Nothing sent yet.</summary>
    public static RelayProgress None { get; } = new(new HashSet<uint>(), null, 0);
}

/// <summary>
/// An object another station sends, which this head end may relay in its own rotation (issue #218): what the receivers on its
/// LAN have heard of it, and what this head end has sent of it. The head end builds these from its catalogue; the scheduler ranks
/// them with its own bulletins and picks the pieces.
/// </summary>
/// <remarks>
/// Before the object is complete (<see cref="Transfer"/> null) only <see cref="HeardFrames"/> can be sent, unchanged. Once it is
/// complete, checked against its ID, the scheduler makes fresh pieces from it in this station's own order, never repeating an ESI
/// this station has sent, and lists the object in the directory (if <see cref="Listing"/> is given) with a long hash computed from
/// <see cref="Transfer"/> itself. Fresh pieces come before unchanged ones, so once complete the unchanged ones are not needed.
/// </remarks>
public sealed class RelayedObject
{
    /// <summary>The object ID.</summary>
    public required ulong ObjectId { get; init; }

    /// <summary>The zstd dictionary ID.</summary>
    public ushort DictionaryId { get; init; }

    /// <summary>The content type (the low 7 bits of the first octet).</summary>
    public byte Type { get; init; }

    /// <summary>How the object is RaptorQ coded, as the original station coded it.</summary>
    public required ObjectTransmissionInformation Oti { get; init; }

    /// <summary>How many on-air pieces of the object (from any station but this one) the receivers have heard: what coverage counts besides this head end's own.</summary>
    public int HeardOnAir { get; init; }

    /// <summary>Frames of pieces the receivers heard on air, one for each ESI, never ones handed in through the API. Used only until the object is complete.</summary>
    public IReadOnlyList<MailcastFrame> HeardFrames { get; init; } = [];

    /// <summary>The object, once complete and checked against its ID (and its listed hash, if known); null until then.</summary>
    public TransferObject? Transfer { get; init; }

    /// <summary>How the object is listed once complete; null leaves it unlisted although it is sent.</summary>
    public RelayListing? Listing { get; init; }

    /// <summary>The envelope object of a relayed bulletin, if held and checked; sent and linked from the listing only once the object is complete.</summary>
    public TransferObject? Envelope { get; init; }

    /// <summary>What this head end has sent of it.</summary>
    public RelayProgress Progress { get; init; } = RelayProgress.None;

    /// <summary>K: pieces needed to rebuild it.</summary>
    public int K => Oti.SourceBlockSymbols(0);

    /// <summary>Whether the object is complete and checked.</summary>
    public bool Complete => Transfer is not null;

    /// <summary>On-air coverage in pieces: heard from others plus this head end's own.</summary>
    public int Total => HeardOnAir + Progress.Sent.Count;

    /// <summary>How many pieces could be sent now: unlimited once complete, else the heard ones not yet sent.</summary>
    public int Available => Complete ? int.MaxValue : HeardFrames.Count(f => !Progress.Sent.Contains(f.EncodingSymbolId));

    /// <summary>The next <paramref name="count"/> pieces to send: fresh ones in this station's own order if complete, else unchanged heard ones in ESI order.</summary>
    internal List<MailcastFrame> NextPieces(int count, EsiShuffle? shuffle)
    {
        var frames = new List<MailcastFrame>(count);
        if (Transfer is null)
        {
            foreach (var f in HeardFrames.OrderBy(f => f.EncodingSymbolId))
            {
                if (frames.Count >= count)
                {
                    break;
                }
                if (!Progress.Sent.Contains(f.EncodingSymbolId))
                {
                    frames.Add(f);
                }
            }
            return frames;
        }
        for (uint n = 0; frames.Count < count && n <= PayloadId.MaxEncodingSymbolId; n++)
        {
            uint esi = shuffle?.Map(n) ?? n;
            if (!Progress.Sent.Contains(esi))
            {
                frames.Add(Transfer.Frame(esi));
            }
        }
        return frames;
    }
}

/// <summary>One relayed object's part in a slot.</summary>
/// <param name="ObjectId">The object.</param>
/// <param name="Type">Its content type.</param>
/// <param name="Frames">The pieces sent, in sending order.</param>
/// <param name="Fresh">Whether they are fresh pieces made from the checked object (else unchanged ones heard on air).</param>
/// <param name="Listed">Whether the slot's directory lists it.</param>
/// <param name="Envelope">The envelope object sent with it, or null.</param>
/// <param name="EnvelopeFirstEsi">The first envelope ESI sent.</param>
/// <param name="EnvelopeCount">How many envelope pieces are sent.</param>
public sealed record ScheduledRelay(ulong ObjectId, byte Type, IReadOnlyList<MailcastFrame> Frames, bool Fresh, bool Listed, TransferObject? Envelope = null, uint EnvelopeFirstEsi = 0, int EnvelopeCount = 0);
