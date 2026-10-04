using System.Globalization;
using System.Text;

namespace Mailcast.Core;

/// <summary>
/// A bulletin as a forwarding partner of an FBB or LinBPQ BBS receives it: the fields of the
/// FBB proposal, the title, and the message text with its routing (R:) lines.
/// </summary>
/// <remarks>
/// <para>
/// Text is held as Latin-1, one character per octet, because BBS mail is octets in no declared
/// character set: most is ASCII, some is UTF-8, some is a DOS code page. Latin-1 maps every octet
/// to a character and back, so whatever was received is sent on unchanged.
/// </para>
/// <para>
/// The serialised form, which is what gets compressed and broadcast, is a header block of
/// "Key: value" lines, each ended by a line feed, an empty line, then the message text exactly
/// as received:
/// </para>
/// <code>
/// Type: B
/// From: G4ABC
/// To: ALL
/// At: WW                  (the value may be empty)
/// Bid: 12345_GB7RDG
/// Date: 2026-10-01T12:34:56Z
/// Title: Title text
///
/// R:261001/1234Z ...      message text: routing lines, each ended by CR LF, then the body
/// </code>
/// <para>
/// The seven keys above are required, each once. A reader ignores keys it does not know, so
/// later versions can add header lines without breaking older receivers. The value is
/// everything after the first ": " on the line.
/// </para>
/// </remarks>
public sealed class Bulletin : IEquatable<Bulletin>
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static readonly string[] RequiredKeys = ["Type", "From", "To", "At", "Bid", "Date", "Title"];

    /// <summary>Latin-1, which maps each octet to the character with the same code.</summary>
    public static readonly Encoding TextEncoding = Encoding.Latin1;

    /// <summary>Creates a bulletin, checking that every field can be serialised and read back unchanged.</summary>
    /// <param name="type">The message type, B for a bulletin.</param>
    /// <param name="from">The sender's callsign.</param>
    /// <param name="to">The addressee or bulletin category, such as ALL or NEWS.</param>
    /// <param name="at">The distribution (the @ field), such as WW or GBR. May be empty.</param>
    /// <param name="bid">The bulletin ID.</param>
    /// <param name="title">The subject line.</param>
    /// <param name="date">When the bulletin was created, in whole seconds.</param>
    /// <param name="routingLines">The R: lines, newest first, without their line endings.</param>
    /// <param name="body">The text after the routing lines, exactly as received.</param>
    public Bulletin(char type, string from, string to, string at, string bid, string title, DateTimeOffset date, IEnumerable<string> routingLines, string body)
    {
        if (type < 'A' || type > 'Z')
        {
            throw new ArgumentException("The type is a capital letter, such as B.", nameof(type));
        }
        CheckToken(from, nameof(from), allowEmpty: false);
        CheckToken(to, nameof(to), allowEmpty: false);
        CheckToken(at, nameof(at), allowEmpty: true);
        CheckToken(bid, nameof(bid), allowEmpty: false);
        CheckLine(title, nameof(title));
        if (date.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The date is in whole seconds.", nameof(date));
        }
        var lines = routingLines.ToArray();
        foreach (var line in lines)
        {
            CheckLine(line, nameof(routingLines));
            if (!line.StartsWith("R:", StringComparison.Ordinal))
            {
                throw new ArgumentException("A routing line starts with R:.", nameof(routingLines));
            }
        }
        CheckLatin1(body, nameof(body));
        if (StartsWithRoutingLine(body))
        {
            throw new ArgumentException("The body starts with a routing line; it belongs in the routing lines.", nameof(body));
        }

        Type = type;
        From = from;
        To = to;
        At = at;
        Bid = bid;
        Title = title;
        Date = date.ToUniversalTime();
        RoutingLines = lines;
        Body = body;
    }

    /// <summary>The message type, B for a bulletin.</summary>
    public char Type { get; }

    /// <summary>The sender's callsign.</summary>
    public string From { get; }

    /// <summary>The addressee or bulletin category.</summary>
    public string To { get; }

    /// <summary>The distribution (the @ field). May be empty.</summary>
    public string At { get; }

    /// <summary>The bulletin ID.</summary>
    public string Bid { get; }

    /// <summary>The subject line.</summary>
    public string Title { get; }

    /// <summary>When the bulletin was created, UTC.</summary>
    public DateTimeOffset Date { get; }

    /// <summary>The R: lines, newest first, without line endings.</summary>
    public IReadOnlyList<string> RoutingLines { get; }

    /// <summary>The text after the routing lines.</summary>
    public string Body { get; }

    /// <summary>The message text as a partner receives it: each routing line and CR LF, then the body.</summary>
    public string MessageText => string.Concat(RoutingLines.Select(l => l + "\r\n")) + Body;

    /// <summary>
    /// Creates a bulletin from message text as stored or forwarded, taking the leading lines that
    /// start with R: and end with CR LF as the routing lines.
    /// </summary>
    public static Bulletin FromMessageText(char type, string from, string to, string at, string bid, string title, DateTimeOffset date, string messageText)
    {
        ArgumentNullException.ThrowIfNull(messageText);
        var lines = new List<string>();
        int position = 0;
        while (StartsWithRoutingLine(messageText.AsSpan(position)))
        {
            int end = messageText.IndexOf("\r\n", position, StringComparison.Ordinal);
            lines.Add(messageText[position..end]);
            position = end + 2;
        }
        return new Bulletin(type, from, to, at, bid, title, date, lines, messageText[position..]);
    }

    /// <summary>Whether the text starts with a complete routing line: R:, no bare CR or LF, then CR LF.</summary>
    private static bool StartsWithRoutingLine(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("R:", StringComparison.Ordinal))
        {
            return false;
        }
        int end = text.IndexOfAny('\r', '\n');
        return end >= 0 && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n';
    }

    /// <summary>The serialised form described in the class remarks.</summary>
    public byte[] Serialize()
    {
        var text = new StringBuilder();
        text.Append("Type: ").Append(Type).Append('\n');
        text.Append("From: ").Append(From).Append('\n');
        text.Append("To: ").Append(To).Append('\n');
        text.Append("At: ").Append(At).Append('\n');
        text.Append("Bid: ").Append(Bid).Append('\n');
        text.Append("Date: ").Append(Date.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Title: ").Append(Title).Append('\n');
        text.Append('\n');
        text.Append(MessageText);
        return TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>Reads the serialised form. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static Bulletin Parse(ReadOnlySpan<byte> serialized)
    {
        string text = TextEncoding.GetString(serialized);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        while (true)
        {
            int end = text.IndexOf('\n', position);
            if (end < 0)
            {
                throw new FormatException("The bulletin header does not end with an empty line.");
            }
            string line = text[position..end];
            position = end + 1;
            if (line.Length == 0)
            {
                break;
            }
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException($"Not a header line: {line}");
            }
            string key = line[..colon];
            if (!fields.TryAdd(key, line[(colon + 2)..]))
            {
                throw new FormatException($"The header has {key} twice.");
            }
        }
        foreach (var key in RequiredKeys)
        {
            if (!fields.ContainsKey(key))
            {
                throw new FormatException($"The header has no {key}.");
            }
        }
        if (fields["Type"].Length != 1)
        {
            throw new FormatException("The type is one letter.");
        }
        if (!DateTimeOffset.TryParseExact(fields["Date"], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            throw new FormatException("The date is not yyyy-MM-ddTHH:mm:ssZ.");
        }
        try
        {
            return FromMessageText(fields["Type"][0], fields["From"], fields["To"], fields["At"], fields["Bid"], fields["Title"], date, text[position..]);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }
    }

    /// <summary>Whether two bulletins serialise identically.</summary>
    public bool Equals(Bulletin? other) =>
        other is not null
        && Type == other.Type
        && From == other.From
        && To == other.To
        && At == other.At
        && Bid == other.Bid
        && Title == other.Title
        && Date == other.Date
        && RoutingLines.SequenceEqual(other.RoutingLines)
        && Body == other.Body;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Bulletin);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, From, To, At, Bid, Title, Date, Body);

    /// <inheritdoc />
    public override string ToString() => $"{Type} {From} > {To} @ {At} {Bid}: {Title}";

    private static void CheckLatin1(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        foreach (char c in value)
        {
            if (c > 'ÿ')
            {
                throw new ArgumentException("Text is octets as Latin-1 characters; this has a character past U+00FF.", name);
            }
        }
    }

    private static void CheckLine(string value, string name)
    {
        CheckLatin1(value, name);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("A header field cannot contain CR or LF.", name);
        }
    }

    private static void CheckToken(string value, string name, bool allowEmpty)
    {
        CheckLine(value, name);
        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException("This field cannot be empty.", name);
        }
        if (value.Contains(' '))
        {
            throw new ArgumentException("This field is one word in an FBB proposal, so it cannot contain spaces.", name);
        }
    }
}
