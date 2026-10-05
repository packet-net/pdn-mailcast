using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>
/// The head end's memory of the bulletins in rotation. A bulletin is compressed and coded once,
/// on the day it is first offered, and the same object is sent on every later day, continuing
/// from the next unsent ESI. Nothing is ever compressed twice, so a change of compression
/// settings or dictionary cannot make the pieces of one bulletin disagree.
/// </summary>
/// <remarks>
/// <para>One folder per bulletin under <c>bulletins/</c>, named from a hash of its BID in capitals:</para>
/// <code>
/// object.bin   the object's octets, exactly as coded
/// state.txt    Key: value lines: Bid, Title, Size, FirstSeen, Object, Dictionary, Oti, NextEsi
/// </code>
/// <para>
/// Both are written to a temporary name and renamed, object first, so a folder whose state is
/// missing or does not match its object is an interrupted first offer and is discarded.
/// </para>
/// <para>Not thread-safe: use one store from one thread.</para>
/// </remarks>
public sealed class HeadEndStore
{
    private readonly string _bulletins;
    private readonly Compression _compression;
    private readonly ScheduleOptions _options;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Opens or creates a store.</summary>
    public HeadEndStore(string root, Compression compression, ScheduleOptions? options = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(compression);
        _compression = compression;
        _options = options ?? new ScheduleOptions();
        _log = log;
        _bulletins = Path.Combine(root, "bulletins");
        Directory.CreateDirectory(_bulletins);
        foreach (var dir in Directory.EnumerateDirectories(_bulletins))
        {
            foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                File.Delete(tmp);
            }
            var entry = Load(dir);
            if (entry is null)
            {
                _log?.Invoke($"head end store: discarding incomplete {Path.GetFileName(dir)}");
                Directory.Delete(dir, recursive: true);
                continue;
            }
            _entries[Path.GetFileName(dir)] = entry;
        }
    }

    /// <summary>How many bulletins the store remembers.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Offers a bulletin. The first time a BID is offered it is compressed, coded and kept, with
    /// <paramref name="seen"/> as its first-seen day; later offers of the same BID return what
    /// was kept, unchanged, whatever their content. Returns null if the bulletin is over
    /// <see cref="ScheduleOptions.MaxBulletinSize"/>.
    /// </summary>
    public CarriedBulletin? Offer(Bulletin bulletin, DateOnly seen)
    {
        ArgumentNullException.ThrowIfNull(bulletin);
        string key = KeyOf(bulletin.Bid);
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing.Carried;
        }
        var serialized = bulletin.Serialize();
        if (serialized.Length > _options.MaxBulletinSize)
        {
            return null;
        }
        var transfer = TransferObject.ForBulletin(bulletin, _options.DictionaryId, _compression, _options.SymbolSize, _options.Alignment);
        var carried = new CarriedBulletin(bulletin.Bid, bulletin.Title, serialized.Length, seen, transfer, 0);
        var dir = Path.Combine(_bulletins, key);
        Directory.CreateDirectory(dir);
        DurableFile.WriteAtomically(Path.Combine(dir, "object.bin"), transfer.Bytes);
        var entry = new Entry(dir, carried);
        SaveState(entry);
        _entries[key] = entry;
        return carried;
    }

    /// <summary>The bulletins in their carrying days on <paramref name="today"/>, to pass to <see cref="BroadcastScheduler.Plan(IEnumerable{CarriedBulletin}, DateOnly, int, Compression, ScheduleOptions?)"/>.</summary>
    public IReadOnlyList<CarriedBulletin> InRotation(DateOnly today) =>
        _entries.Values
            .Select(e => e.Carried)
            .Where(c => today.DayNumber - c.FirstSeen.DayNumber is var d && d >= 0 && d < _options.DaysCarried)
            .ToList();

    /// <summary>Records that a plan's frames have been sent, so the next plan continues with fresh ESIs.</summary>
    public void Commit(DailyBroadcast broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        foreach (var o in broadcast.Objects)
        {
            if (o.Bid is null || !_entries.TryGetValue(KeyOf(o.Bid), out var entry) || entry.Carried.Transfer.ObjectId != o.Transfer.ObjectId)
            {
                continue;
            }
            if (o.NextEsi > entry.Carried.NextEsi)
            {
                entry.Carried = entry.Carried with { NextEsi = o.NextEsi };
                SaveState(entry);
            }
        }
    }

    /// <summary>
    /// Forgets bulletins first seen <see cref="ScheduleOptions.RememberDays"/> or more days
    /// before <paramref name="today"/>. Returns how many.
    /// </summary>
    public int Expire(DateOnly today)
    {
        int removed = 0;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (today.DayNumber - entry.Carried.FirstSeen.DayNumber >= _options.RememberDays)
            {
                Directory.Delete(entry.Dir, recursive: true);
                _entries.Remove(key);
                removed++;
            }
        }
        return removed;
    }

    private static string KeyOf(string bid) =>
        Convert.ToHexStringLower(SHA256.HashData(Bulletin.TextEncoding.GetBytes(bid.ToUpperInvariant()))[..8]);

    private static void SaveState(Entry entry)
    {
        var c = entry.Carried;
        var text = new StringBuilder();
        text.Append("Bid: ").Append(c.Bid).Append('\n');
        text.Append("Title: ").Append(c.Title).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Size: {c.Size}\n");
        text.Append(CultureInfo.InvariantCulture, $"FirstSeen: {c.FirstSeen:yyyy-MM-dd}\n");
        text.Append("Object: ").Append(ObjectId.Format(c.Transfer.ObjectId)).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Dictionary: {c.Transfer.DictionaryId}\n");
        text.Append("Oti: ").Append(Convert.ToHexStringLower(c.Transfer.Oti.ToBytes())).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"NextEsi: {c.NextEsi}\n");
        DurableFile.WriteAtomically(Path.Combine(entry.Dir, "state.txt"), Bulletin.TextEncoding.GetBytes(text.ToString()));
    }

    private Entry? Load(string dir)
    {
        var statePath = Path.Combine(dir, "state.txt");
        var objectPath = Path.Combine(dir, "object.bin");
        if (!File.Exists(statePath) || !File.Exists(objectPath))
        {
            return null;
        }
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(statePath)).Split('\n'))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[line[..colon]] = line[(colon + 2)..];
                }
            }
            var oti = ObjectTransmissionInformation.Read(Convert.FromHexString(fields["Oti"]));
            var transfer = TransferObject.FromStored(File.ReadAllBytes(objectPath), ushort.Parse(fields["Dictionary"], CultureInfo.InvariantCulture), oti);
            if (ObjectId.Format(transfer.ObjectId) != fields["Object"])
            {
                return null;
            }
            var carried = new CarriedBulletin(
                fields["Bid"],
                fields["Title"],
                int.Parse(fields["Size"], CultureInfo.InvariantCulture),
                DateOnly.ParseExact(fields["FirstSeen"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                transfer,
                uint.Parse(fields["NextEsi"], CultureInfo.InvariantCulture));
            return new Entry(dir, carried);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or KeyNotFoundException or OverflowException)
        {
            _log?.Invoke($"head end store: unreadable state in {Path.GetFileName(dir)}: {e.Message}");
            return null;
        }
    }

    private sealed class Entry(string dir, CarriedBulletin carried)
    {
        public string Dir { get; } = dir;

        public CarriedBulletin Carried { get; set; } = carried;
    }
}
