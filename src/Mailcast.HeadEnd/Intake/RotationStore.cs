using Packet.Mailcast;

namespace Mailcast.HeadEnd.Intake;

/// <summary>What happened to an offered bulletin.</summary>
public enum OfferOutcome
{
    /// <summary>New: kept, first seen today.</summary>
    Added,

    /// <summary>Its BID was already held; nothing changed.</summary>
    AlreadyHeld,

    /// <summary>Over the size cap; not kept.</summary>
    TooLarge,
}

/// <summary>
/// Packet.Mailcast's <see cref="HeadEndStore"/>, which keeps each bulletin's object from when it is
/// first seen, its first slot and its next ESI, made safe to share between the intakes and the slot.
/// </summary>
public sealed class RotationStore
{
    private readonly HeadEndStore _store;
    private readonly Lock _gate = new();

    public RotationStore(string root, Compression compression, ScheduleOptions options, IJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _store = new HeadEndStore(root, compression, options, journal.Write);
    }

    /// <summary>How many bulletins are remembered.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _store.Count;
            }
        }
    }

    /// <summary>Whether a bulletin with this BID is remembered.</summary>
    public bool Holds(string bid)
    {
        lock (_gate)
        {
            return _store.Holds(bid);
        }
    }

    /// <summary>Offers a bulletin received on <paramref name="today"/>.</summary>
    public OfferOutcome Offer(Bulletin bulletin, DateOnly today)
    {
        lock (_gate)
        {
            int before = _store.Count;
            CarriedBulletin? carried = _store.Offer(bulletin, today);
            return carried is null ? OfferOutcome.TooLarge
                : _store.Count > before ? OfferOutcome.Added
                : OfferOutcome.AlreadyHeld;
        }
    }

    /// <summary>Plans a slot from the bulletins in rotation, filling it to <paramref name="budget"/> under the budget rule.</summary>
    public SlotBroadcast Plan(DateTimeOffset slot, int seed, Compression compression, ScheduleOptions options, SlotBudget? budget = null, string? mode = null)
    {
        lock (_gate)
        {
            return BroadcastScheduler.Plan(_store.InRotation(slot), slot, seed, compression, options, _store.DirectoryNextEsi, budget, mode);
        }
    }

    /// <summary>The bulletins in rotation in a slot, with their next ESIs and first slots.</summary>
    public IReadOnlyList<CarriedBulletin> InRotation(DateTimeOffset slot)
    {
        lock (_gate)
        {
            return _store.InRotation(slot);
        }
    }

    /// <summary>Records that the first <paramref name="framesQueued"/> frames of a plan were handed to the modem.</summary>
    public void Commit(SlotBroadcast plan, int framesQueued)
    {
        lock (_gate)
        {
            _store.Commit(plan, framesQueued);
        }
    }

    /// <summary>Forgets bulletins past the store's remembering days.</summary>
    public int Expire(DateOnly today)
    {
        lock (_gate)
        {
            return _store.Expire(today);
        }
    }
}
