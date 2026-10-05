using System.Text;

namespace Mailcast.HeadEnd;

/// <summary>Where the head end says what it is doing: one plain ASCII line at a time.</summary>
public interface IJournal
{
    /// <summary>Writes one line.</summary>
    void Write(string line);
}

/// <summary>
/// Lines to standard output, which systemd sends to the journal. Everything is made plain ASCII
/// first, because the journal's pager runs under a C locale and shows anything else as escapes.
/// </summary>
public sealed class ConsoleJournal : IJournal
{
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Write(string line)
    {
        string text = Ascii.Plain(line);
        lock (_gate)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
    }
}

/// <summary>Keeps every line, for tests.</summary>
public sealed class MemoryJournal : IJournal
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    /// <summary>Every line written so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <inheritdoc />
    public void Write(string line)
    {
        lock (_gate)
        {
            _lines.Add(Ascii.Plain(line));
        }
    }
}

/// <summary>Plain ASCII for anything printed.</summary>
public static class Ascii
{
    /// <summary>
    /// The text with every character outside printable ASCII replaced: control characters by a
    /// space and anything above 0x7E by a question mark. Bulletin titles and BBS replies can
    /// carry any octet, and none of it should reach a terminal raw.
    /// </summary>
    public static string Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                < ' ' => ' ',
                > '~' => '?',
                _ => c,
            });
        }
        return builder.ToString();
    }
}
