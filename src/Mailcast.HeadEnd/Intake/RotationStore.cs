using Mailcast.Core;

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
/// Mailcast.Core's <see cref="HeadEndStore"/>, which keeps each bulletin's object from the day it
/// is first seen and its next ESI, made safe to share between the intakes and the slot.
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

    /// <summary>Plans a day from the bulletins in rotation.</summary>
    public DailyBroadcast Plan(DateOnly day, int seed, Compression compression, ScheduleOptions options)
    {
        lock (_gate)
        {
            return BroadcastScheduler.Plan(_store.InRotation(day), day, seed, compression, options);
        }
    }

    /// <summary>Records that the first <paramref name="framesQueued"/> frames of a plan were handed to the modem.</summary>
    public void Commit(DailyBroadcast plan, int framesQueued)
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
