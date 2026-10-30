using System.Text;
using System.Text.RegularExpressions;

namespace Packet.Mailcast;

/// <summary>How the lines of a bulletin body were ended before canonicalisation.</summary>
public enum LineEnding : byte
{
    /// <summary>Every line break between body lines was a line feed.</summary>
    Lf = 0,

    /// <summary>Every line break between body lines was a carriage return and line feed.</summary>
    CrLf = 1,

    /// <summary>Every line break between body lines was a lone carriage return.</summary>
    Cr = 2,

    /// <summary>The body mixed line endings, so it was left exactly as it was (it will not combine with other copies).</summary>
    Raw = 3,
}

/// <summary>
/// What canonicalisation took off one station's copy of a bulletin: the bytes above the canonical body,
/// the bytes below it, the full title and the line-ending style. With the canonical body it rebuilds that
/// copy exactly (<see cref="CanonicalForm.Rebuild"/>). Issue #141, Model B.
/// </summary>
public sealed class BulletinEnvelope : IEquatable<BulletinEnvelope>
{
    /// <summary>The serialisation version this code writes and reads.</summary>
    public const byte CurrentVersion = 1;

    private readonly byte[] _top;
    private readonly byte[] _bottom;

    /// <summary>Creates an envelope.</summary>
    /// <param name="top">The exact bytes removed above the canonical body.</param>
    /// <param name="bottom">The exact bytes removed below the canonical body.</param>
    /// <param name="title">The full title.</param>
    /// <param name="lineEnding">How the body's lines were ended.</param>
    public BulletinEnvelope(ReadOnlySpan<byte> top, ReadOnlySpan<byte> bottom, string title, LineEnding lineEnding)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!Enum.IsDefined(lineEnding))
        {
            throw new ArgumentException("Unknown line ending.", nameof(lineEnding));
        }

        foreach (var c in title)
        {
            if (c > 0xFF)
            {
                throw new ArgumentException("The title holds a character outside Latin-1.", nameof(title));
            }
        }

        _top = top.ToArray();
        _bottom = bottom.ToArray();
        Title = title;
        LineEnding = lineEnding;
    }

    /// <summary>The exact bytes removed above the canonical body (routing lines, origin block, leading blank lines).</summary>
    public ReadOnlyMemory<byte> Top => _top;

    /// <summary>The exact bytes removed below the canonical body (the last line's ending and any trailing blank lines).</summary>
    public ReadOnlyMemory<byte> Bottom => _bottom;

    /// <summary>The full title, one character per octet.</summary>
    public string Title { get; }

    /// <summary>How the body's lines were ended.</summary>
    public LineEnding LineEnding { get; }

    /// <summary>
    /// Serialises the envelope: a version byte, a line-ending byte, then the top, the bottom and the title,
    /// each as an unsigned LEB128 length followed by that many bytes (the title in Latin-1). Deterministic.
    /// </summary>
    public byte[] Serialise()
    {
        var title = Bulletin.TextEncoding.GetBytes(Title);
        using var ms = new MemoryStream();
        ms.WriteByte(CurrentVersion);
        ms.WriteByte((byte)LineEnding);
        WriteField(ms, _top);
        WriteField(ms, _bottom);
        WriteField(ms, title);
        return ms.ToArray();
    }

    /// <summary>Reads an envelope written by <see cref="Serialise"/>.</summary>
    /// <exception cref="FormatException">The data is truncated, has trailing bytes or is a version this code does not know.</exception>
    public static BulletinEnvelope Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new FormatException("The envelope is too short.");
        }

        if (data[0] != CurrentVersion)
        {
            throw new FormatException($"Unknown envelope version {data[0]}.");
        }

        if (data[1] > (byte)LineEnding.Raw)
        {
            throw new FormatException("Unknown line ending.");
        }

        var pos = 2;
        var top = ReadField(data, ref pos);
        var bottom = ReadField(data, ref pos);
        var title = ReadField(data, ref pos);
        if (pos != data.Length)
        {
            throw new FormatException("The envelope has trailing bytes.");
        }

        return new BulletinEnvelope(top, bottom, Bulletin.TextEncoding.GetString(title), (LineEnding)data[1]);
    }

    /// <inheritdoc />
    public bool Equals(BulletinEnvelope? other) =>
        other is not null && LineEnding == other.LineEnding && Title == other.Title
        && _top.AsSpan().SequenceEqual(other._top) && _bottom.AsSpan().SequenceEqual(other._bottom);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as BulletinEnvelope);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, LineEnding, _top.Length, _bottom.Length);

    private static void WriteField(MemoryStream ms, byte[] bytes)
    {
        var n = (uint)bytes.Length;
        while (n >= 0x80)
        {
            ms.WriteByte((byte)(n | 0x80));
            n >>= 7;
        }

        ms.WriteByte((byte)n);
        ms.Write(bytes);
    }

    private static ReadOnlySpan<byte> ReadField(ReadOnlySpan<byte> data, ref int pos)
    {
        long length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (pos >= data.Length || shift > 28)
            {
                throw new FormatException("The envelope is truncated or malformed.");
            }

            var b = data[pos++];
            length |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (b == 0 && shift > 0)
                {
                    throw new FormatException("The envelope has a non-minimal length.");
                }

                break;
            }
        }

        if (length > data.Length - pos)
        {
            throw new FormatException("The envelope is truncated.");
        }

        var field = data.Slice(pos, (int)length);
        pos += (int)length;
        return field;
    }
}

/// <summary>A bulletin split into the shared canonical body and one station's envelope.</summary>
/// <param name="Body">The canonical body: content lines only, each ended by a line feed.</param>
/// <param name="Envelope">What was removed, to rebuild that station's copy.</param>
public sealed record CanonicalBulletin(byte[] Body, BulletinEnvelope Envelope);

/// <summary>
/// Canonical form of a bulletin body (issue #141, Model B). Copies of one bulletin held by different BBSes differ
/// at the edges: forwarding hops prepend routing lines, FBB, XRouter and openBCM add an origin block, and the
/// BBSes end lines differently. <see cref="Canonicalise"/> removes those edges into an envelope and never touches
/// the middle, so <see cref="Rebuild"/> returns exactly the original bytes for any input, whether or not a rule
/// matched. A rule therefore only has to be good at making copies match, not safe.
/// </summary>
/// <remarks>The rules and the envelope serialisation are described in docs/canonical-form.md.</remarks>
public static class CanonicalForm
{
    private static readonly Regex RouteLine = new(@"^R:\d{6}/\d{4}[Zz]? ", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PathLine = new(@"^Path: ", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FbbFrom = new(@"^From: \S+@\S+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FbbTo = new(@"^To  : \S+@\S+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XrDate = new(@"^Date: [A-Z][a-z]{2}, \d{1,2} [A-Z][a-z]{2} \d{4} \d\d:\d\d:\d\d [+-]\d{4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XrFrom = new(@"^From: \S+ <\S+>$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XrTo = new(@"^To: \S+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XrSubject = new(@"^Subject:( .*)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XrMessageId = new(@"^Message-ID: \S+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BcmFrom = new(@"^From: \S+ @ \S+( \([^()]*\))?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BcmTo = new(@"^To:   \S+ @ \S+ ?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BcmInfo = new(@"^X-Info: .*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly record struct Line(int Start, int TextEnd, int Next, string Text, bool LoneCr = false);

    /// <summary>Splits a copy of a bulletin into its canonical body and an envelope.</summary>
    /// <param name="original">The body as the BBS holds it, exactly (routing lines included).</param>
    /// <param name="title">The full title.</param>
    public static CanonicalBulletin Canonicalise(ReadOnlySpan<byte> original, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var lines = Split(original);

        var i = StripTop(lines, title);
        var last = lines.Count - 1;
        while (last >= i && IsBlank(lines[last].Text))
        {
            last--;
        }

        if (last < i)
        {
            // No content line at all: everything is envelope.
            return new CanonicalBulletin([], new BulletinEnvelope(original, [], title, LineEnding.Lf));
        }

        var style = DetectStyle(lines, i, last);
        var top = original[..lines[i].Start];
        var bottom = original[lines[last].TextEnd..];

        byte[] body;
        if (style == LineEnding.Raw)
        {
            body = new byte[lines[last].TextEnd - lines[i].Start + 1];
            original[lines[i].Start..lines[last].TextEnd].CopyTo(body);
            body[^1] = (byte)'\n';
        }
        else
        {
            using var ms = new MemoryStream();
            for (var k = i; k <= last; k++)
            {
                ms.Write(original[lines[k].Start..lines[k].TextEnd]);
                ms.WriteByte((byte)'\n');
            }

            body = ms.ToArray();
        }

        return new CanonicalBulletin(body, new BulletinEnvelope(top, bottom, title, style));
    }

    /// <summary>Rebuilds exactly the bytes and title that were passed to <see cref="Canonicalise"/>.</summary>
    /// <exception cref="ArgumentException">The canonical body is not empty and does not end with a line feed.</exception>
    public static (byte[] Original, string Title) Rebuild(ReadOnlySpan<byte> canonical, BulletinEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var ms = new MemoryStream();
        ms.Write(envelope.Top.Span);
        if (canonical.Length > 0)
        {
            if (canonical[^1] != (byte)'\n')
            {
                throw new ArgumentException("A canonical body ends with a line feed.", nameof(canonical));
            }

            var text = canonical[..^1];
            if (envelope.LineEnding is LineEnding.Lf or LineEnding.Raw)
            {
                ms.Write(text);
            }
            else
            {
                byte[] ending = envelope.LineEnding == LineEnding.CrLf ? [(byte)'\r', (byte)'\n'] : [(byte)'\r'];
                foreach (var b in text)
                {
                    if (b == (byte)'\n')
                    {
                        ms.Write(ending);
                    }
                    else
                    {
                        ms.WriteByte(b);
                    }
                }
            }
        }

        ms.Write(envelope.Bottom.Span);
        return (ms.ToArray(), envelope.Title);
    }

    /// <summary>The most octets of a title the canonical bulletin keeps; the full title is in the envelope.</summary>
    public const int SharedTitleOctets = 59;

    /// <summary>
    /// Turns one station's copy of a bulletin into the shared canonical bulletin (a type 1
    /// <see cref="Bulletin"/> with <see cref="Bulletin.Canonical"/> set) and that station's
    /// envelope (issue #142). Every station that holds the bulletin makes the same canonical
    /// bulletin from its own copy, as long as <see cref="Canonicalise"/> finds the same body.
    /// </summary>
    /// <remarks>
    /// The title is the first <see cref="SharedTitleOctets"/> octets of the full title, less trailing
    /// spaces and tabs, and any CR or LF in it (not allowed in a header) becomes a space. The date is always
    /// 1970-01-01T00:00:00Z, so every head end agrees whatever route the bulletin took. The body is the canonical
    /// body with each LF written as CR LF, or as it is for a <see cref="LineEnding.Raw"/> envelope. The
    /// canonical bulletin has no routing lines, with one exception: a body line that looks like one
    /// (R: and CR LF, which <see cref="Bulletin"/> would read as routing) is held there, which puts the same octets in
    /// <see cref="Bulletin.MessageText"/>.
    /// </remarks>
    /// <param name="type">The message type, B for a bulletin.</param>
    /// <param name="from">The sender's callsign.</param>
    /// <param name="to">The addressee or category.</param>
    /// <param name="at">The distribution, may be empty.</param>
    /// <param name="bid">The bulletin ID.</param>
    /// <param name="title">The full title.</param>
    /// <param name="messageText">The text exactly as the BBS gave it, routing lines included.</param>
    public static (Bulletin Bulletin, BulletinEnvelope Envelope) ToCanonicalBulletin(char type, string from, string to, string at, string bid, string title, ReadOnlySpan<byte> messageText)
    {
        ArgumentNullException.ThrowIfNull(title);
        var canonical = Canonicalise(messageText, title);
        var envelope = canonical.Envelope;
        var body = new List<byte>(canonical.Body.Length + 16);
        foreach (var b in canonical.Body)
        {
            if (b == (byte)'\n' && envelope.LineEnding != LineEnding.Raw)
            {
                body.Add((byte)'\r');
            }

            body.Add(b);
        }

        string shared = title.Length > SharedTitleOctets ? title[..SharedTitleOctets] : title;
        shared = shared.Replace('\r', ' ').Replace('\n', ' ').TrimEnd(' ', '\t');
        var bulletin = Bulletin.FromMessageText(type, from, to, at, bid, shared, DateTimeOffset.UnixEpoch, Bulletin.TextEncoding.GetString(body.ToArray()), canonical: true);
        return (bulletin, envelope);
    }

    /// <summary>
    /// The reverse of <see cref="ToCanonicalBulletin"/>: the station's message text and full title
    /// from the canonical bulletin and that station's envelope (CR LF back to LF unless the envelope
    /// is Raw, then <see cref="Rebuild"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The bulletin's text does not rebuild: it is not canonical, or does not suit the envelope.</exception>
    public static (byte[] MessageText, string Title) FromCanonicalBulletin(Bulletin canonical, BulletinEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(envelope);
        var text = Bulletin.TextEncoding.GetBytes(canonical.MessageText);
        if (envelope.LineEnding != LineEnding.Raw)
        {
            using var ms = new MemoryStream(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == (byte)'\r' && i + 1 < text.Length && text[i + 1] == (byte)'\n')
                {
                    continue;
                }

                ms.WriteByte(text[i]);
            }

            text = ms.ToArray();
        }

        return Rebuild(text, envelope);
    }

    private static List<Line> Split(ReadOnlySpan<byte> data)
    {
        var lines = new List<Line>();
        var start = 0;
        var p = 0;
        while (p < data.Length)
        {
            var b = data[p];
            if (b == (byte)'\n' || b == (byte)'\r')
            {
                var next = p + 1;
                if (b == (byte)'\r' && next < data.Length && data[next] == (byte)'\n')
                {
                    next++;
                }

                lines.Add(new Line(start, p, next, Bulletin.TextEncoding.GetString(data[start..p]), b == (byte)'\r' && next == p + 1));
                start = next;
                p = next;
            }
            else
            {
                p++;
            }
        }

        if (start < data.Length)
        {
            lines.Add(new Line(start, data.Length, data.Length, Bulletin.TextEncoding.GetString(data[start..])));
        }

        return lines;
    }

    /// <summary>A line with nothing on it but spaces and tabs; some readers show trailing ones, some do not.</summary>
    private static bool IsBlank(string text) => text.AsSpan().IndexOfAnyExcept(' ', '\t') < 0;

    private static LineEnding DetectStyle(List<Line> lines, int first, int last)
    {
        LineEnding? seen = null;
        for (var k = first; k < last; k++)
        {
            var len = lines[k].Next - lines[k].TextEnd;
            var kind = len == 2 ? LineEnding.CrLf : lines[k].LoneCr ? LineEnding.Cr : LineEnding.Lf;
            seen ??= kind;
            if (seen != kind)
            {
                return LineEnding.Raw;
            }
        }

        return seen ?? LineEnding.Lf;
    }

    /// <summary>Returns the index of the first content line.</summary>
    private static int StripTop(List<Line> lines, string title)
    {
        var i = 0;
        var sawRouting = false;
        while (true)
        {
            var blankBefore = false;
            while (i < lines.Count && IsBlank(lines[i].Text))
            {
                i++;
                blankBefore = true;
            }

            if (i >= lines.Count)
            {
                return i;
            }

            var text = lines[i].Text;
            if (RouteLine.IsMatch(text))
            {
                while (i < lines.Count && RouteLine.IsMatch(lines[i].Text))
                {
                    i++;
                }

                sawRouting = true;
            }
            else if (PathLine.IsMatch(text))
            {
                i++;
                while (i < lines.Count && lines[i].Text.Length > 0 && (lines[i].Text[0] == ' ' || lines[i].Text[0] == '\t'))
                {
                    i++;
                }

                sawRouting = true;
            }
            else if (sawRouting && MatchOriginBlock(lines, i, blankBefore, title) is { } end)
            {
                i = end;
                while (i < lines.Count && IsBlank(lines[i].Text))
                {
                    i++;
                }

                return i;
            }
            else
            {
                return i;
            }
        }
    }

    /// <summary>Returns the index after an origin block starting at <paramref name="i"/>, or null.</summary>
    private static int? MatchOriginBlock(List<Line> lines, int i, bool blankBefore, string title)
    {
        bool At(int k, Regex r) => k < lines.Count && r.IsMatch(lines[k].Text);
        bool Blank(int k) => k < lines.Count && lines[k].Text.Length == 0;

        // FBB: the blank after the routing lines, From: CALL@HROUTE, "To  : X@ROUTE", blank.
        if (blankBefore && At(i, FbbFrom) && At(i + 1, FbbTo) && Blank(i + 2))
        {
            return i + 3;
        }

        // XRouter: Date, From, To, Subject, Message-ID, blank.
        if (At(i, XrDate) && At(i + 1, XrFrom) && At(i + 2, XrTo) && At(i + 3, XrSubject) && At(i + 4, XrMessageId) && Blank(i + 5))
        {
            return i + 6;
        }

        // openBCM: From: X @ Y, "To:   X @ Y", optional X-Info, blank.
        if (At(i, BcmFrom) && At(i + 1, BcmTo))
        {
            var k = i + 2;
            if (At(k, BcmInfo))
            {
                k++;
            }

            if (Blank(k))
            {
                return k + 1;
            }
        }

        return null;
    }
}
