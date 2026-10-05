using Packet.Mailcast;

namespace Mailcast.HeadEnd.Intake;

/// <summary>What one intake pass did.</summary>
public sealed record IntakeResult(int Accepted, int Refused, string? Problem)
{
    /// <summary>Nothing happened.</summary>
    public static IntakeResult Nothing { get; } = new(0, 0, null);
}

/// <summary>Where bulletins come from.</summary>
public interface IBulletinIntake
{
    /// <summary>A short name for the journal.</summary>
    string Name { get; }

    /// <summary>Collects whatever is waiting, storing each new bulletin as first seen on <paramref name="today"/>.</summary>
    Task<IntakeResult> CollectAsync(DateOnly today, CancellationToken cancellation);
}

/// <summary>Which offered messages the head end takes: bulletins only, up to a size.</summary>
public sealed class IntakePolicy(int maxBulletinBytes)
{
    /// <summary>The size cap on a bulletin as received.</summary>
    public int MaxBulletinBytes { get; } = maxBulletinBytes;

    /// <summary>Why a message would be refused, or null to take it.</summary>
    public string? Refusal(char type, string bid, int size)
    {
        if (char.ToUpperInvariant(type) != 'B')
        {
            return $"type {type} is not a bulletin";
        }
        if (size > MaxBulletinBytes)
        {
            return $"{size} octets is over the {MaxBulletinBytes} octet cap";
        }
        return string.IsNullOrWhiteSpace(bid) ? "it has no BID" : null;
    }
}

/// <summary>
/// Bulletins dropped as files into a directory, one per file in the serialised form of
/// <see cref="Bulletin"/> (seven header lines, then the message text). A file taken in is deleted;
/// one that cannot be read or is refused moves to <c>rejected/</c> beside it. Files whose names
/// start with a dot or end in <c>.tmp</c> are left alone, so a writer can create one and rename it.
/// </summary>
public sealed class FileDropIntake(string directory, RotationStore store, IntakePolicy policy, IJournal journal) : IBulletinIntake
{
    /// <inheritdoc />
    public string Name => "file drop";

    /// <inheritdoc />
    public Task<IntakeResult> CollectAsync(DateOnly today, CancellationToken cancellation)
    {
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(IntakeResult.Nothing);
        }
        int accepted = 0;
        int refused = 0;
        foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string? why;
            Bulletin? bulletin = null;
            try
            {
                byte[] content = File.ReadAllBytes(path);
                bulletin = Bulletin.Parse(content);
                why = policy.Refusal(bulletin.Type, bulletin.Bid, bulletin.MessageText.Length);
            }
            catch (FormatException e)
            {
                why = $"not a bulletin file: {e.Message}";
            }

            OfferOutcome outcome = why is null && bulletin is not null ? store.Offer(bulletin, today) : OfferOutcome.TooLarge;
            if (why is null && outcome == OfferOutcome.Added)
            {
                File.Delete(path);
                accepted++;
                journal.Write($"intake: {bulletin!.Bid} from {bulletin.From} to {bulletin.To}@{bulletin.At}, {bulletin.MessageText.Length} octets: {bulletin.Title}");
            }
            else
            {
                refused++;
                Reject(path, why ?? (outcome == OfferOutcome.AlreadyHeld ? "its BID is already held" : "over the size cap once serialised"));
            }
        }
        return Task.FromResult(new IntakeResult(accepted, refused, null));
    }

    private void Reject(string path, string why)
    {
        string rejected = Path.Combine(directory, "rejected");
        Directory.CreateDirectory(rejected);
        File.Move(path, Path.Combine(rejected, Path.GetFileName(path)), overwrite: true);
        journal.Write($"intake: refused {Path.GetFileName(path)}: {why}");
    }
}
