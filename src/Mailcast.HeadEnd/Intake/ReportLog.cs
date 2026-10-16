using System.Text.Json;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;

namespace Mailcast.HeadEnd.Intake;

/// <summary>
/// The listeners' daily reports that have reached the head end (issue #136): for each callsign,
/// the day of its newest report and when that arrived, kept in <see cref="FileName"/> in the state
/// directory. The directory lists the recent ones (<see cref="ReportReceipts"/>), so a listener's
/// receiver can say whether GB7RDG got its report. The reports themselves are never kept, and
/// never broadcast.
/// </summary>
public sealed class ReportLog
{
    /// <summary>The state file.</summary>
    public const string FileName = "reports.json";

    /// <summary>How many days a callsign is remembered after its newest report.</summary>
    public const int RememberDays = 14;

    private readonly string? _path;
    private readonly IJournal _journal;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Heard> _heard = new(StringComparer.Ordinal);
    private bool _active;

    /// <summary>
    /// Reads what is kept in <paramref name="stateDirectory"/>. With <paramref name="readOnly"/>
    /// (offline work) nothing is ever written back.
    /// </summary>
    public ReportLog(string stateDirectory, IJournal journal, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        string path = Path.Combine(stateDirectory, FileName);
        _path = readOnly ? null : path;
        if (!File.Exists(path))
        {
            return;
        }
        _active = true;
        try
        {
            foreach (var (call, heard) in JsonSerializer.Deserialize<Dictionary<string, Heard>>(File.ReadAllBytes(path), Json) ?? [])
            {
                if (ReportReceipts.IsListable(call))
                {
                    _heard[call] = heard;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            journal.Write($"reports: {path} cannot be read ({Ascii.Plain(e.Message)}); starting afresh");
        }
    }

    /// <summary>One callsign's newest report: its day, when it arrived and its BID.</summary>
    public sealed record Heard(DateOnly Day, DateTimeOffset Arrived, string Bid);

    /// <summary>Each callsign's newest report, in callsign order.</summary>
    public IReadOnlyList<KeyValuePair<string, Heard>> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _heard.OrderBy(p => p.Key, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// Notes a report from <paramref name="callsign"/> for <paramref name="day"/>. True when it
    /// is the newest yet from that callsign, so the directory will list it; false when one for
    /// that day or a later one was already noted, the callsign cannot be listed, or the day is
    /// after the day it arrived (a receiver with its clock wrong), which would otherwise hide
    /// that callsign's real reports until then.
    /// </summary>
    public bool Note(string callsign, DateOnly day, DateTimeOffset arrived, string bid)
    {
        if (!ReportReceipts.IsListable(callsign) || day > DateOnly.FromDateTime(arrived.UtcDateTime))
        {
            return false;
        }
        lock (_gate)
        {
            if (_heard.TryGetValue(callsign, out var had) && had.Day >= day)
            {
                return false;
            }
            _heard[callsign] = new Heard(day, arrived, bid);
            _active = true;
            foreach (var old in _heard.Where(p => p.Value.Day < day.AddDays(-RememberDays)).Select(p => p.Key).ToList())
            {
                _heard.Remove(old);
            }
            Save();
            return true;
        }
    }

    /// <summary>
    /// What a directory dated <paramref name="today"/> lists (<see cref="ReportReceipts.ForDay"/>):
    /// null, so the directory says nothing, until the first report has ever arrived. A head end
    /// whose BBS does not hand it reports (no route for MCAST) would otherwise tell every listener
    /// that GB7RDG has had none.
    /// </summary>
    public ReportReceipts? For(DateOnly today)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return null;
            }
            return ReportReceipts.ForDay(today, _heard.Select(p => new KeyValuePair<string, DateOnly>(p.Key, p.Value.Day)));
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            Status.StatusStore.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_heard, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Write($"reports: cannot save {_path}: {Ascii.Plain(e.Message)}");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
