// Turns the listeners' daily reports kept in reports/daily into tables (see reports/README.md):
//
//   dotnet run --project tools/Mailcast.ReportArchive -- reports
//
// Reads every reports/daily/YYYY-MM-DD/CALL.txt (a few header lines we write, a blank line, then
// the report as the BBS held it) with the receiver's own parser, and writes reports.csv, slots.csv
// and stations.csv in reports/data. Fails, naming the file, on any report it cannot read.

using System.Globalization;
using System.Text;
using Packet.Mailcast.Feedback;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Mailcast.ReportArchive <reports directory>");
    return 2;
}

string root = args[0];
var files = Directory.GetFiles(Path.Combine(root, "daily"), "*.txt", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal)
    .ToList();

var reports = new StringBuilder("day,callsign,receiver_version,locator,audio,rebuilt,delivered,already_had,given_up,outside_pieces,touched,errors,slots,slots_heard,frames,source_kind,check_kind,file\n");
var slots = new StringBuilder("day,callsign,slot_utc,station,waveform,frames,snr_db,offset_hz,ionosonde,psk_reporter,modes,two_f_delay_ms,two_f_power_db,delay_spread_ms,doppler_spread_hz,virtual_height_km,channel_basis\n");
var stations = new StringBuilder("day,callsign,station,listened,heard,frames,completed,snr_db,virtual_height_km,other_copies\n");
int failed = 0;

foreach (var file in files)
{
    var text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
    int split = text.IndexOf("\n\n", StringComparison.Ordinal);
    var head = (split < 0 ? text : text[..split]).Split('\n')
        .Select(l => l.Split(": ", 2))
        .Where(p => p.Length == 2)
        .GroupBy(p => p[0])
        .ToDictionary(g => g.Key, g => g.First()[1].Trim());
    if (split < 0 || !head.TryGetValue("Title", out var title) || !DailyReport.TryParse(title, text[(split + 2)..], out var r) || r is null)
    {
        Console.Error.WriteLine($"{file}: not a daily report this can read");
        failed++;
        continue;
    }

    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    string day = r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var h = r.Header;
    int heard = r.Slots.Count(s => s.Frames > 0 || s.SnrDb is not null);
    string errors = string.Join(' ', h.Errors.Where(e => e.Value > 0).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}{e.Value}"));
    reports.AppendLine(Row(day, r.Callsign, h.ReceiverVersion, h.Locator, h.Audio, N(h.Rebuilt), N(h.Delivered), N(h.AlreadyHad), N(h.GivenUp),
        N(h.OutsidePieces), N(h.Touched), errors, N(r.Slots.Count), N(heard), N(r.Slots.Sum(s => s.Frames)),
        SourceKind(head.GetValueOrDefault("Source")), CheckKind(head.GetValueOrDefault("Check")), rel));

    foreach (var s in r.Slots)
    {
        var c = s.Channel;
        slots.AppendLine(Row(day, r.Callsign, s.Start.ToString("HH:mm", CultureInfo.InvariantCulture), s.Station, s.Waveform, N(s.Frames), D(s.SnrDb), D(s.OffsetHz),
            Verdict(s.Verdicts, 'I'), Verdict(s.Verdicts, 'P'), N(c?.Modes), D(c?.TwoFDelayMs), D(c?.TwoFPowerDb), D(c?.DelaySpreadMs),
            D(c?.DopplerSpreadHz), D(c?.VirtualHeightKm), c is null ? null : c.Basis == 'p' ? "probe" : "bursts"));
    }

    foreach (var st in r.Stations ?? [])
    {
        stations.AppendLine(Row(day, r.Callsign, st.Station, N(st.Listened), N(st.Heard), N(st.Frames), N(st.Completed), D(st.SnrDb), D(st.VirtualHeightKm), N(st.OtherCopies)));
    }
}

if (failed > 0)
{
    return 1;
}

var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
var utf8 = new UTF8Encoding(false);
File.WriteAllText(Path.Combine(data, "reports.csv"), reports.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), utf8);
File.WriteAllText(Path.Combine(data, "slots.csv"), slots.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), utf8);
File.WriteAllText(Path.Combine(data, "stations.csv"), stations.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), utf8);
Console.WriteLine($"read {files.Count} reports; wrote {data}");
return 0;

static string N(int? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : "";

static string D(double? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : "";

static string Row(params string?[] fields) => string.Join(',', fields.Select(f =>
    f is null ? "" : f.Contains(',', StringComparison.Ordinal) || f.Contains('"', StringComparison.Ordinal) ? $"\"{f.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : f));

// "IG" -> "good" for the ionosonde ('I') or PSK Reporter ('P'); empty when the slot had no verdict from it.
static string? Verdict(IReadOnlyList<string> verdicts, char source) =>
    verdicts.FirstOrDefault(v => v.Length == 2 && v[0] == source) is { } v
        ? v[1] switch { 'G' => "good", 'M' => "marginal", 'P' => "poor", _ => "unknown" }
        : null;

// The Source line's kind, so a table can be filtered by how a report reached the archive.
static string SourceKind(string? source) => source switch
{
    null => "",
    _ when source.Contains("mail store", StringComparison.Ordinal) && source.Contains("session", StringComparison.Ordinal) => "bbs-store-dump",
    _ when source.Contains("mail store", StringComparison.Ordinal) => "bbs-store",
    _ when source.Contains("telnet", StringComparison.Ordinal) => "bbs-read",
    _ when source.Contains("session", StringComparison.Ordinal) => "bbs-read-log",
    _ when source.StartsWith("pasted", StringComparison.Ordinal) => "pasted",
    _ => "other",
};

static string CheckKind(string? check) => check switch
{
    null => "",
    _ when check.Contains("not match", StringComparison.OrdinalIgnoreCase) => "mismatch",
    _ when check.Contains("match", StringComparison.Ordinal) => "size-verified",
    _ => "none",
};
