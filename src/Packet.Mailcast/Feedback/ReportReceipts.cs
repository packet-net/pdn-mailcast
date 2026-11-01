using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Packet.Mailcast.Feedback;

/// <summary>
/// The listeners whose daily reports have reached the station lately, as the directory lists them
/// (issue #136): each callsign with the day of the newest report the head end has from it.
/// </summary>
/// <remarks>
/// <para>Carried as one field on the directory's first entry line, after the timetable and the
/// mode: <c>reports=</c>, then the days, newest first, separated by <c>;</c>, each a date, a
/// <c>:</c> and its callsigns separated by <c>,</c>:</para>
/// <code>
/// reports=2026-10-15:G4ABC,G8BPQ,GM8BPQ;2026-10-14:M9TWM
/// </code>
/// <para>A callsign appears once, under the day of its newest report. <c>reports=</c> with nothing
/// after it says the head end lists reports and has none lately, which is not the same as a
/// directory without the field, from a head end that does not say. Since the field is on an entry
/// line, before the signature, a signed directory's signature covers it. Readers from before
/// ignore it, as they ignore any field after the title.</para>
/// </remarks>
public sealed partial class ReportReceipts
{
    /// <summary>What the field starts with.</summary>
    public const string Field = "reports=";

    /// <summary>
    /// How many days back from the directory's own date a report is listed: a report for the
    /// 15th is listed in the directories of the 15th, 16th and 17th.
    /// </summary>
    public const int DaysListed = 2;

    /// <summary>
    /// The most callsigns a directory lists, so that a flood of made-up reports cannot grow the
    /// directory without limit: 100 is about 650 octets before compression.
    /// </summary>
    public const int MostListed = 100;

    private readonly SortedDictionary<string, DateOnly> _newest;

    /// <summary>Lists each callsign with the day of its newest report. Throws <see cref="ArgumentException"/> for a callsign that is not 1 to 12 capital letters and digits.</summary>
    public ReportReceipts(IEnumerable<KeyValuePair<string, DateOnly>> newest)
    {
        ArgumentNullException.ThrowIfNull(newest);
        _newest = new SortedDictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var (call, day) in newest)
        {
            if (!IsListable(call))
            {
                throw new ArgumentException($"The callsign '{call}' cannot be listed.", nameof(newest));
            }
            if (!_newest.TryGetValue(call, out var had) || day > had)
            {
                _newest[call] = day;
            }
        }
    }

    /// <summary>None: a head end that lists reports and has none lately.</summary>
    public static ReportReceipts None { get; } = new([]);

    /// <summary>Each callsign listed, with the day of its newest report, in callsign order.</summary>
    public IReadOnlyDictionary<string, DateOnly> Newest => _newest;

    /// <summary>The day of the newest report listed for <paramref name="callsign"/> (any case), or null if it is not listed.</summary>
    public DateOnly? For(string? callsign) =>
        callsign is not null && _newest.TryGetValue(callsign.Trim().ToUpperInvariant(), out var day) ? day : null;

    /// <summary>Whether a callsign can go in the field: 1 to 12 capital letters and digits, as a report's title has it.</summary>
    public static bool IsListable(string? callsign) => callsign is not null && Callsign().IsMatch(callsign);

    /// <summary>The field as the directory carries it.</summary>
    public string ToField()
    {
        var text = new StringBuilder(Field);
        bool first = true;
        foreach (var day in _newest.GroupBy(p => p.Value).OrderByDescending(g => g.Key))
        {
            text.Append(first ? "" : ";").Append(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(':');
            text.AppendJoin(',', day.Select(p => p.Key));
            first = false;
        }
        return text.ToString();
    }

    /// <summary>
    /// Reads the field from among an entry line's fields: null when there is none, or it cannot be
    /// read (a reader then knows nothing, rather than taking it as no reports).
    /// </summary>
    public static ReportReceipts? FromFields(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        string? value = fields.FirstOrDefault(f => f.StartsWith(Field, StringComparison.Ordinal))?[Field.Length..];
        if (value is null)
        {
            return null;
        }
        var newest = new List<KeyValuePair<string, DateOnly>>();
        if (value.Length == 0)
        {
            return None;
        }
        foreach (string group in value.Split(';'))
        {
            int colon = group.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || !DateOnly.TryParseExact(group[..colon], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return null;
            }
            foreach (string call in group[(colon + 1)..].Split(','))
            {
                if (!IsListable(call))
                {
                    return null;
                }
                newest.Add(new(call, day));
            }
        }
        return new ReportReceipts(newest);
    }

    /// <summary>
    /// The reports to list in a directory dated <paramref name="today"/>: of <paramref name="newest"/>,
    /// those from <see cref="DaysListed"/> days before it up to it, at most <see cref="MostListed"/>
    /// of them, the newest days first. Within a day the order given decides, so give the
    /// latest to arrive first.
    /// </summary>
    public static ReportReceipts ForDay(DateOnly today, IEnumerable<KeyValuePair<string, DateOnly>> newest)
    {
        ArgumentNullException.ThrowIfNull(newest);
        return new ReportReceipts(newest
            .Where(p => p.Value >= today.AddDays(-DaysListed) && p.Value <= today)
            .OrderByDescending(p => p.Value) // stable, so within a day the order given stays
            .Take(MostListed));
    }

    [GeneratedRegex("^[A-Z0-9]{1,12}$")]
    private static partial Regex Callsign();
}
