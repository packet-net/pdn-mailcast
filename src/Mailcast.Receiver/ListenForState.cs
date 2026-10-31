using System.Globalization;
using System.Text.Json;

namespace Mailcast.Receiver;

/// <summary>
/// Issue #224: what the receiver knows about each <c>listenFor</c> entry that the config file does
/// not say: when it was first seen, and whether it is retired (its station's own directory was heard,
/// or 7 days passed unheard). Kept in a small file in the state directory, keyed by callsign,
/// frequency and sideband, so the receiver never rewrites the operator's config file by itself.
/// </summary>
public sealed class ListenForState
{
    /// <summary>The file's name in the state directory.</summary>
    public const string FileName = "listen-for.json";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<string> _log;
    private Dictionary<string, Record> _records = [];

    /// <summary>What is kept for one entry.</summary>
    public sealed record Record
    {
        /// <summary>When the entry was first seen, UTC.</summary>
        public DateTimeOffset AddedUtc { get; init; }

        /// <summary>Why it is no longer needed, or null while it is in use.</summary>
        public string? Retired { get; init; }
    }

    /// <summary>Reads the file in <paramref name="stateDirectory"/>, if any; an unreadable one is treated as empty.</summary>
    public ListenForState(string stateDirectory, Action<string> log)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _log = log;
        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(_path)) ?? [];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"listen-for: cannot read {_path}: {Ascii.Clean(e.Message)}; starting afresh");
        }
    }

    /// <summary>The key an entry is kept under.</summary>
    public static string KeyOf(ListenForEntry e) => string.Create(CultureInfo.InvariantCulture, $"{e.Callsign}|{e.FrequencyHz}|{e.Sideband.ToLowerInvariant()}");

    /// <summary>What is kept for <paramref name="entry"/>, or null if it has not been seen.</summary>
    public Record? Get(ListenForEntry entry)
    {
        lock (_gate)
        {
            return _records.GetValueOrDefault(KeyOf(entry));
        }
    }

    /// <summary>Notes entries not seen before as added at <paramref name="now"/>, and forgets those no longer in <paramref name="entries"/>.</summary>
    public void Sync(IReadOnlyList<ListenForEntry>? entries, DateTimeOffset now)
    {
        lock (_gate)
        {
            var keys = (entries ?? []).Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            bool changed = _records.Keys.ToList().Where(k => !keys.Contains(k)).Select(k => _records.Remove(k)).Any();
            foreach (string key in keys.Where(k => !_records.ContainsKey(k)))
            {
                _records[key] = new Record { AddedUtc = now };
                changed = true;
            }
            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>Starts <paramref name="entry"/> afresh at <paramref name="now"/> (added again by the operator).</summary>
    public void Restart(ListenForEntry entry, DateTimeOffset now)
    {
        lock (_gate)
        {
            _records[KeyOf(entry)] = new Record { AddedUtc = now };
            Save();
        }
    }

    /// <summary>Retires <paramref name="entry"/> for <paramref name="reason"/>; false if it was retired already or is not known.</summary>
    public bool Retire(ListenForEntry entry, string reason)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(KeyOf(entry), out var record) || record.Retired is not null)
            {
                return false;
            }
            _records[KeyOf(entry)] = record with { Retired = reason };
            Save();
            return true;
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_records));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"listen-for: cannot write {_path}: {Ascii.Clean(e.Message)}");
        }
    }
}
