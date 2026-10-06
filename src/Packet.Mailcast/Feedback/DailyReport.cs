using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Packet.Mailcast.Feedback;

/// <summary>
/// The radio path over one slot, as the receiver's channel measurement gave it: from the bursts
/// decoded, or from a sounding probe, which fills the same fields.
/// </summary>
/// <param name="Modes">How many propagation modes (paths) were seen.</param>
/// <param name="TwoFDelayMs">The 2F mode's delay after the first, ms; without hop labels, the second mode's. Null with one mode.</param>
/// <param name="TwoFPowerDb">The same mode's power against the first mode's, dB (negative: weaker).</param>
/// <param name="DelaySpreadMs">RMS delay spread, ms.</param>
/// <param name="DopplerSpreadHz">Doppler spread of all paths together, Hz.</param>
/// <param name="VirtualHeightKm">The F layer's virtual height, km.</param>
/// <param name="Basis">What it was measured from: <c>b</c> bursts, <c>p</c> probe.</param>
public sealed record ReportChannel(
    int Modes,
    double? TwoFDelayMs,
    double? TwoFPowerDb,
    double? DelaySpreadMs,
    double? DopplerSpreadHz,
    double? VirtualHeightKm,
    char Basis);

/// <summary>One slot the receiver listened to.</summary>
/// <param name="Start">The slot's scheduled start, UTC time of day.</param>
/// <param name="Waveform">The MS110D waveform most frames came on, as <c>W4</c> or <c>W3</c>; <c>WX</c> for a tie; null if none is known.</param>
/// <param name="Frames">Frames decoded in the slot.</param>
/// <param name="SnrDb">The tone's signal to noise in 3 kHz, dB; null if no tone was heard.</param>
/// <param name="OffsetHz">The tone's offset from 1800 Hz; null if no tone was heard.</param>
/// <param name="Verdicts">
/// The propagation verdicts heard for the slot, each two letters: the source (<c>I</c> the
/// ionosonde) and its state (<c>G</c> good, <c>M</c> marginal, <c>P</c> poor, <c>U</c> unknown).
/// Empty if none was heard.
/// </param>
/// <param name="Channel">The channel measurement, if there was one.</param>
public sealed record ReportSlot(
    TimeOnly Start,
    string? Waveform,
    int Frames,
    double? SnrDb,
    double? OffsetHz,
    IReadOnlyList<string> Verdicts,
    ReportChannel? Channel = null);

/// <summary>
/// A listener's daily feedback report: what one receiver heard of GB7RDG's slots in a UTC day,
/// small enough to go as a packet mail. See docs/receiver.md, "The daily report's format".
/// </summary>
/// <remarks>
/// <para>The title is <c>MCR CALL YYYY-MM-DD</c>. The body is plain ASCII: one header line, then
/// one line per slot listened to, fields separated by single spaces, <c>-</c> for a value not
/// known.</para>
/// <code>
/// MCR1 0.6.0 IO91lk wessex.zapto.org 12/12 2:BBS1,AUD1
/// 09 W4 212 18 +1.2 IG 2 1.9/-17 0.35 0.21 290 b
/// 10 W3 0 4 +1.1 IM
/// </code>
/// <para>Header: the format (<c>MCR1</c>), the receiver's version, its locator, the audio
/// (<c>sc</c> for a sound card, or the web SDR's host), bulletins rebuilt/delivered, and the
/// errors (<c>0</c>, or the count, a colon and each code with its count).</para>
/// <para>Slot: the hour (HHMM if the slot is not on the hour), waveform, frames, tone SNR (dB),
/// tone offset (Hz), verdicts (joined by <c>+</c>), then, only with a channel measurement, the
/// modes, the 2F delay/power (ms/dB), delay spread (ms), Doppler spread (Hz), virtual height (km)
/// and basis. A reader ignores fields after the sixth on the header line and after the twelfth on
/// a slot line, so format 1 can grow at the ends of its lines.</para>
/// </remarks>
public sealed partial record DailyReport(string Callsign, DateOnly Day, ReportHeader Header, IReadOnlyList<ReportSlot> Slots)
{
    /// <summary>The format this writes, and the newest it reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>What the title and the header line start with.</summary>
    public const string Tag = "MCR";

    private const string Missing = "-";

    /// <summary>The message title: <c>MCR G4ABC 2026-10-06</c>.</summary>
    public string Title => string.Create(CultureInfo.InvariantCulture, $"{Tag} {Callsign} {Day:yyyy-MM-dd}");

    /// <summary>The body: the header line and one line per slot, each ended by CR LF.</summary>
    public string Body
    {
        get
        {
            var text = new StringBuilder();
            text.Append(HeaderLine()).Append("\r\n");
            foreach (var slot in Slots)
            {
                text.Append(SlotLine(slot)).Append("\r\n");
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// Reads a report from its title and body. The body may carry routing (R:) lines, blank lines
    /// or anything else before the header, as a BBS shows a message, anything after the slot
    /// lines that does not start with a digit, and any line ending.
    /// Throws <see cref="FormatException"/> for one that is not a report this can read.
    /// </summary>
    public static DailyReport Parse(string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        var t = TitlePattern().Match(title.Trim());
        if (!t.Success || !DateOnly.TryParseExact(t.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new FormatException($"\"{title}\" is not a report title such as \"MCR G4ABC 2026-10-06\".");
        }

        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim())
            .ToList();
        int at = lines.FindIndex(l => l.StartsWith(Tag, StringComparison.Ordinal) && l.Length > Tag.Length && char.IsAsciiDigit(l[Tag.Length]));
        if (at < 0)
        {
            throw new FormatException("There is no MCR header line in the body.");
        }
        var header = ParseHeader(lines[at]);
        var slots = new List<ReportSlot>();
        foreach (string line in lines.Skip(at + 1))
        {
            if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
            {
                // The slots end at a blank line or anything else a BBS adds, such as its
                // "[End of Message]" or a signature.
                break;
            }
            slots.Add(ParseSlot(line));
        }
        return new DailyReport(t.Groups[1].Value, day, header, slots);
    }

    /// <summary>As <see cref="Parse"/>, but false for anything it cannot read.</summary>
    public static bool TryParse(string title, string body, out DailyReport? report)
    {
        try
        {
            report = Parse(title, body);
            return true;
        }
        catch (FormatException)
        {
            report = null;
            return false;
        }
    }

    private string HeaderLine()
    {
        var h = Header;
        string errors = h.Errors.Count == 0 || h.Errors.Values.Sum() == 0
            ? "0"
            : string.Create(CultureInfo.InvariantCulture, $"{h.Errors.Values.Sum()}:{string.Join(',', h.Errors.Where(e => e.Value > 0).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key}{e.Value}")))}");
        return string.Create(CultureInfo.InvariantCulture,
            $"{Tag}{FormatVersion} {Token(h.ReceiverVersion)} {Token(h.Locator)} {Token(h.Audio)} {h.Rebuilt}/{h.Delivered} {errors}");
    }

    private static string SlotLine(ReportSlot s)
    {
        var c = CultureInfo.InvariantCulture;
        var line = new StringBuilder();
        line.Append(s.Start.Minute == 0 ? s.Start.ToString("HH", c) : s.Start.ToString("HHmm", c));
        line.Append(' ').Append(Token(s.Waveform));
        line.Append(' ').Append(s.Frames.ToString(c));
        line.Append(' ').Append(s.SnrDb is { } snr ? Math.Round(snr).ToString("0", c) : Missing);
        line.Append(' ').Append(s.OffsetHz is { } offset ? (Math.Round(offset, 1) is var o && o != 0 ? o : 0).ToString("+0.0;-0.0;+0.0", c) : Missing);
        line.Append(' ').Append(s.Verdicts.Count == 0 ? Missing : string.Join('+', s.Verdicts));
        if (s.Channel is { } ch)
        {
            line.Append(' ').Append(ch.Modes.ToString(c));
            line.Append(' ').Append(ch.TwoFDelayMs is { } d
                ? Math.Round(d, 1).ToString("0.0", c) + "/" + (ch.TwoFPowerDb is { } p ? Math.Round(p).ToString("0", c) : Missing)
                : Missing);
            line.Append(' ').Append(Number(ch.DelaySpreadMs, "0.##"));
            line.Append(' ').Append(Number(ch.DopplerSpreadHz, "0.##"));
            line.Append(' ').Append(Number(ch.VirtualHeightKm, "0"));
            line.Append(' ').Append(char.IsAsciiLetterLower(ch.Basis) ? ch.Basis : '-');
        }
        return line.ToString();
    }

    private static ReportHeader ParseHeader(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(f[0].AsSpan(Tag.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            throw new FormatException($"\"{f[0]}\" is not a report format.");
        }
        if (version != FormatVersion)
        {
            throw new FormatException($"This reads report format {FormatVersion}, not {version}.");
        }
        if (f.Length < 6)
        {
            throw new FormatException($"The header line has {f.Length} fields, not 6: \"{line}\".");
        }
        var counts = f[4].Split('/');
        if (counts.Length != 2 || !TryInt(counts[0], out int rebuilt) || !TryInt(counts[1], out int delivered))
        {
            throw new FormatException($"\"{f[4]}\" is not bulletins rebuilt/delivered.");
        }
        return new ReportHeader(Value(f[1]) ?? "", Value(f[2]), Value(f[3]) ?? "", rebuilt, delivered, ParseErrors(f[5]));
    }

    private static Dictionary<string, int> ParseErrors(string field)
    {
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        if (field == "0")
        {
            return errors;
        }
        int colon = field.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !TryInt(field[..colon], out int total))
        {
            throw new FormatException($"\"{field}\" is not an error count such as 0 or 3:BBS2,AUD1.");
        }
        foreach (string part in field[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = ErrorPattern().Match(part);
            if (!m.Success)
            {
                throw new FormatException($"\"{part}\" is not an error code and count such as BBS2.");
            }
            errors[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        if (errors.Values.Sum() != total)
        {
            throw new FormatException($"\"{field}\": the codes add up to {errors.Values.Sum()}, not {total}.");
        }
        return errors;
    }

    private static ReportSlot ParseSlot(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 6)
        {
            throw new FormatException($"The slot line \"{line}\" has {f.Length} fields, not at least 6.");
        }
        if (!TimeOnly.TryParseExact(f[0], f[0].Length == 2 ? "HH" : "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            throw new FormatException($"\"{f[0]}\" is not a slot's hour.");
        }
        if (!TryInt(f[2], out int frames))
        {
            throw new FormatException($"\"{f[2]}\" is not a frame count.");
        }
        ReportChannel? channel = null;
        if (f.Length >= 12)
        {
            if (!TryInt(f[6], out int modes))
            {
                throw new FormatException($"\"{f[6]}\" is not a mode count.");
            }
            double? delay = null, power = null;
            if (f[7] != Missing)
            {
                var dp = f[7].Split('/');
                if (dp.Length != 2)
                {
                    throw new FormatException($"\"{f[7]}\" is not a 2F delay/power such as 1.9/-17.");
                }
                delay = Double(dp[0]);
                power = Double(dp[1]);
            }
            if (f[11].Length != 1)
            {
                throw new FormatException($"\"{f[11]}\" is not a basis such as b.");
            }
            channel = new ReportChannel(modes, delay, power, Double(f[8]), Double(f[9]), Double(f[10]), f[11][0]);
        }
        else if (f.Length != 6)
        {
            throw new FormatException($"The slot line \"{line}\" has {f.Length} fields: 6 without a channel measurement, 12 with.");
        }
        return new ReportSlot(start, Value(f[1]), frames, Double(f[3]), Double(f[4]),
            f[5] == Missing ? [] : f[5].Split('+'), channel);
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static double? Double(string s) =>
        s == Missing ? null
        : double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) ? v
        : throw new FormatException($"\"{s}\" is not a number.");

    private static string? Value(string s) => s == Missing ? null : s;

    private static string Number(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : Missing;

    /// <summary>A value as one field: printable ASCII with no spaces, or <c>-</c> for none.</summary>
    private static string Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Missing;
        }
        var token = new StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            token.Append(ch is > ' ' and <= '~' ? ch : '_');
        }
        return token.ToString();
    }

    [GeneratedRegex(@"^MCR ([A-Z0-9]+) (\d{4}-\d{2}-\d{2})$")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"^([A-Z]+)(\d+)$")]
    private static partial Regex ErrorPattern();
}

/// <summary>The codes for the errors a <see cref="DailyReport"/>'s header counts. A reader takes any capital letters as a code.</summary>
public static class ReportErrors
{
    /// <summary>A session with the BBS failed.</summary>
    public const string Bbs = "BBS";

    /// <summary>The audio source failed or was lost.</summary>
    public const string Audio = "AUD";

    /// <summary>Something went wrong retuning the rig, or around it.</summary>
    public const string Rig = "RIG";

    /// <summary>A hook command failed.</summary>
    public const string Hook = "HOOK";
}

/// <summary>The header line of a <see cref="DailyReport"/>.</summary>
/// <param name="ReceiverVersion">The receiver's version, such as 0.6.0.</param>
/// <param name="Locator">The receiver's Maidenhead locator, if it knows it.</param>
/// <param name="Audio">Where the audio came from: <c>sc</c> for a sound card, or the web SDR's host.</param>
/// <param name="Rebuilt">Bulletins rebuilt in the day.</param>
/// <param name="Delivered">Bulletins the BBS accepted in the day.</param>
/// <param name="Errors">How many of each kind of error there were, by code (see <see cref="ReportErrors"/>).</param>
public sealed record ReportHeader(string ReceiverVersion, string? Locator, string Audio, int Rebuilt, int Delivered, IReadOnlyDictionary<string, int> Errors);
