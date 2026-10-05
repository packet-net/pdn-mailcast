using System.Text;

namespace Mailcast.Receiver;

/// <summary>
/// Makes text plain ASCII before it is logged or shown. A journal read through a pager under a
/// C locale shows anything above 0x7F as escapes, and some text that reaches the log (exception
/// messages from the FBB code, bulletin titles) is not ASCII.
/// </summary>
internal static class Ascii
{
    /// <summary>Returns <paramref name="text"/> with every character outside printable ASCII replaced.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is >= ' ' and <= '~')
            {
                result.Append(c);
            }
            else
            {
                result.Append(c switch
                {
                    '\u00A7' => "section ",
                    '\u2264' => "<=",
                    '\u2265' => ">=",
                    '\u2192' => "->",
                    '\u2014' or '\u2013' => "-",
                    '\t' => " ",
                    _ => "?",
                });
            }
        }

        return result.ToString();
    }
}
