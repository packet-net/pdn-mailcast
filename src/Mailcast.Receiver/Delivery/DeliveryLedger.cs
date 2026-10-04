using System.Text.Json;
using Mailcast.Core;

namespace Mailcast.Receiver.Delivery;

/// <summary>One line of the delivery record.</summary>
/// <param name="Time">When the BBS answered.</param>
/// <param name="Bid">The bulletin's BID.</param>
/// <param name="Title">Its title.</param>
/// <param name="Verdict">What the BBS said.</param>
/// <param name="Detail">Why, where there is more to say.</param>
public sealed record DeliveryRecord(DateTimeOffset Time, string Bid, string Title, DeliveryVerdict Verdict, string? Detail);

/// <summary>
/// What the BBS has said about each bulletin, kept in deliveries.jsonl in the state directory,
/// one JSON object per line, so the page and the sysop can see what went in and what did not.
/// </summary>
public sealed class DeliveryLedger
{
    private const int RecentKept = 200;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeliveryRecord> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeliveryRecord> _recent = [];

    /// <summary>Opens the record under <paramref name="stateDirectory"/>, reading what is already there.</summary>
    public DeliveryLedger(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "deliveries.jsonl");
        if (!File.Exists(_path))
        {
            return;
        }
        foreach (string line in File.ReadLines(_path))
        {
            DeliveryRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<DeliveryRecord>(line, ReceiverConfig.Json);
            }
            catch (JsonException)
            {
                continue; // a line cut short by a crash
            }
            if (record is not null)
            {
                Remember(record);
            }
        }
    }

    /// <summary>The newest records, newest first.</summary>
    public IReadOnlyList<DeliveryRecord> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. Enumerable.Reverse(_recent)];
            }
        }
    }

    /// <summary>The newest record for a BID, if any.</summary>
    public DeliveryRecord? Latest(string bid)
    {
        lock (_gate)
        {
            return _latest.GetValueOrDefault(bid);
        }
    }

    /// <summary>
    /// Records the BBS's answer for <paramref name="bulletin"/> and returns the record written. An
    /// FS - for a bulletin whose last transfer was unconfirmed is the BBS confirming it kept that
    /// transfer, so it is recorded as accepted.
    /// </summary>
    public DeliveryRecord Record(Bulletin bulletin, DeliveryOutcome outcome, DateTimeOffset now)
    {
        lock (_gate)
        {
            var verdict = outcome.Verdict;
            string? detail = outcome.Detail;
            if (verdict == DeliveryVerdict.AlreadyHad
                && _latest.TryGetValue(bulletin.Bid, out var before)
                && before.Verdict == DeliveryVerdict.Unconfirmed)
            {
                verdict = DeliveryVerdict.Accepted;
                detail = "confirmed on the next session: the BBS had kept the transfer";
            }
            var record = new DeliveryRecord(now, bulletin.Bid, bulletin.Title, verdict, detail);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, ReceiverConfig.JsonLine) + "\n");
            Remember(record);
            return record;
        }
    }

    private void Remember(DeliveryRecord record)
    {
        _latest[record.Bid] = record;
        _recent.Add(record);
        if (_recent.Count > RecentKept)
        {
            _recent.RemoveAt(0);
        }
    }
}
