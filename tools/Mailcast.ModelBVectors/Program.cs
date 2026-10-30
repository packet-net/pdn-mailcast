// Writes the Model B vector file (issue #142):
//
//   dotnet run --project tools/Mailcast.ModelBVectors -- <vectors.json>
//       rebuilds every expected value from the inputs already in the file
//   dotnet run --project tools/Mailcast.ModelBVectors -- --new-inputs <repo-root> <vectors.json>
//       also makes the inputs afresh (corpus files from <repo-root>/research/138/corpus, and made-up ones)
//
// Regenerate only when the format is meant to change: the file is the definition of the format, and
// Packet.Mailcast.Tests fails when the library stops matching it.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailcast.ModelBVectors;

string output;
List<VectorInput> inputs;
if (args.Length == 3 && args[0] == "--new-inputs")
{
    inputs = NewInputs(args[1]);
    output = args[2];
}
else if (args.Length == 1)
{
    output = args[0];
    inputs = [.. JsonNode.Parse(File.ReadAllText(output))!["cases"]!.AsArray().Select(c => VectorInput.FromJson(c!))];
}
else
{
    Console.Error.WriteLine("usage: Mailcast.ModelBVectors [--new-inputs <repo-root>] <vectors.json>");
    return 2;
}

var doc = ModelBVectors.Document(inputs);
// Always LF newlines, whatever the platform (the folder has eol=lf in .gitattributes).
var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal);
File.WriteAllText(output, json + "\n", new UTF8Encoding(false));
Console.WriteLine($"wrote {inputs.Count} cases to {output}");
return 0;

static byte[] L1(string s) => Encoding.Latin1.GetBytes(s);

static VectorInput Make(string name, string description, string bid, string title, byte[] text, string from = "G8BPQ", string to = "ALL", string at = "WW") =>
    new(name, description, 'B', from, to, at, bid, L1(title), text);

static List<VectorInput> NewInputs(string root)
{
    var inputs = new List<VectorInput>
    {
        Make("empty-body", "No body at all.", "EMPTY_G8BPQ", "Empty body", []),
        Make("one-line", "One line, LF ended, no routing lines.", "ONELINE_G8BPQ", "One line", L1("Hello from the vectors.\n")),
    };

    // A multi-kilobyte bulletin from GB7RDG's copy in the #138 corpus, routing lines and all.
    var (rdgHead, rdgText) = Corpus(Path.Combine(root, "research", "138", "corpus", "GB7RDG", "msg-4327.txt"));
    inputs.Add(new VectorInput("corpus-gb7rdg-4327", "A real bulletin from GB7RDG's store (research/138/corpus/GB7RDG/msg-4327.txt), a few kilobytes with R: lines on top.",
        'B', rdgHead["From"], rdgHead["To"], "", rdgHead["Bid"], L1(rdgHead["Title"]), rdgText));

    // An FBB style copy: the Path line from a real FBB copy (GB7CIP msg-598285), with an origin block above it.
    var (cipHead, cipText) = Corpus(Path.Combine(root, "research", "138", "corpus", "GB7CIP", "msg-598285.txt"));
    var cipAll = Encoding.Latin1.GetString(cipText);
    var split = cipAll.IndexOf("\n\n", StringComparison.Ordinal);
    var fbb = "R:261019/0251Z @:GB7CIP.#32.GBR.EURO #:598285 [Caterham Surrey GBR] $:" + cipHead["Bid"] + "\n"
        + cipAll[..split] + "\n\n"
        + "From: " + cipHead["From"] + "@HP2DFA.#PAN.PAN.NOAM\n"
        + "To  : " + cipHead["To"] + "@WW\n\n"
        + cipAll[(split + 2)..];
    inputs.Add(new VectorInput("fbb-origin-and-path", "A copy as FBB hands it over: an R: line and the Path line, then the From: and To: origin block and the body (body from research/138/corpus/GB7CIP/msg-598285.txt).",
        'B', cipHead["From"], cipHead["To"], "WW", cipHead["Bid"], L1(cipHead["Title"]), L1(fbb)));

    inputs.Add(Make("eight-bit-and-ansi", "Octets above 127 (Latin-1 accents, a pound sign) in the title and the body, and ANSI colour escapes in the body.", "EIGHTBIT_G8BPQ", "Café £ ANSI test",
        L1("R:261030/1000Z 1@GB7XXX.#1.GBR.EURO BPQ6.0.25\n\nCafé crème £5 üñîçødé\n\u001b[1;31mRED\u001b[0m and \u001b[32mgreen\u001b[0m text\nÿ\u0080\u009f end\n")));

    inputs.Add(Make("raw-mixed-line-endings", "A body that mixes CR LF, LF and lone CR line endings, so it is kept exactly as it is (Raw).", "RAWMIX_G8BPQ", "Mixed line endings",
        L1("first line\r\nsecond line\nthird line\rfourth line\r\n\r\nlast\n")));

    inputs.Add(Make("title-60-characters", "A title of 60 characters whose 58th, 59th and 60th are all non-blank: the shared title keeps exactly the first 59 (cutting at 58 or 60 gives other bytes), the envelope keeps all 60.", "TITLE60_G8BPQ",
        "0123456789012345678901234567890123456789012345678901234567XYZ",
        L1("A short body under a long title.\n")));

    inputs.Add(Make("title-trailing-blanks", "A title whose cut at 59 octets ends in a space and a tab, which are trimmed from the shared title; the envelope keeps the whole title.", "TITLEWS_G8BPQ",
        "012345678901234567890123456789012345678901234567890123456 \tZ tail",
        L1("A short body under a title with blanks at the cut.\n")));

    inputs.Add(Make("line-endings-crlf", "Every line ends in CR LF, routing lines included, and the oldest R: line has no zone letter (the newer one has a lower-case z).", "CRLF_G8BPQ", "CR LF body",
        L1("R:261030/1100z @:GB7AAA.#1.GBR.EURO #:11 $:CRLF_G8BPQ\r\nR:261029/0930 @:GB7BBB.#2.GBR.EURO #:22 $:CRLF_G8BPQ\r\n\r\nFirst line.\r\nSecond line.\r\n\r\nFourth line.\r\n")));

    inputs.Add(Make("line-endings-cr", "Every line ends in a lone CR (an old style of reader), routing lines included.", "CRONLY_G8BPQ", "Lone CR body",
        L1("R:261030/1100Z @:GB7AAA.#1.GBR.EURO #:11 $:CRONLY_G8BPQ\rR:261028/2359Z @:GB7BBB.#2.GBR.EURO #:22 $:CRONLY_G8BPQ\r\rFirst line.\rSecond line.\r\rFourth line.\r")));

    // Over 128 KB (zstd's block size), so the output has several blocks: GB7RDG corpus files one after another.
    var big = new List<byte>();
    foreach (var file in Directory.GetFiles(Path.Combine(root, "research", "138", "corpus", "GB7RDG"), "msg-*.txt").OrderBy(f => f, StringComparer.Ordinal))
    {
        if (big.Count > 131072 + 1024)
        {
            break;
        }

        big.AddRange(Corpus(file).Text);
    }

    inputs.Add(Make("large-over-128k", "A body over 128 KB made of GB7RDG corpus bulletins one after another, so zstd writes several blocks; only a few symbols are listed.", "BIG128K_G8BPQ", "Large body", [.. big]));

    // An envelope bigger than one 240 octet piece: many R: lines that do not compress.
    var rng = new Random(142);
    var sb = new StringBuilder();
    for (int i = 0; i < 24; i++)
    {
        var token = Convert.ToHexStringLower(Enumerable.Range(0, 12).Select(_ => (byte)rng.Next(256)).ToArray());
        sb.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"R:2610{rng.Next(20, 30):00}/{rng.Next(24):00}{rng.Next(60):00}Z @:{token[..6].ToUpperInvariant()}.#{rng.Next(1, 99)}.GBR.EURO #:{rng.Next(100000)} [{token[6..]}] $:BIGENV_G8BPQ\n"));
    }
    sb.Append("\nThe routing above is more than 240 octets even after compression.\n");
    inputs.Add(Make("envelope-over-240-octets", "Many incompressible R: lines, so the envelope object needs several pieces.", "BIGENV_G8BPQ", "Big envelope", L1(sb.ToString())));
    return inputs;
}

// Splits a corpus file (a BBS reader's output) into its header fields and the text after them.
static (Dictionary<string, string> Head, byte[] Text) Corpus(string path)
{
    var lines = Encoding.Latin1.GetString(File.ReadAllBytes(path)).Split('\n');
    var head = new Dictionary<string, string>();
    var pathLines = new List<string>();
    int i = 0;
    while (i < lines.Length && lines[i].Length > 0)
    {
        var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
        var key = lines[i][..colon];
        var value = lines[i][(colon + 1)..].TrimStart(' ');
        head[key] = value;
        if (key == "Path")
        {
            pathLines.Add(lines[i]);
        }

        i++;
    }

    while (i < lines.Length && (lines[i].Length == 0 || lines[i].StartsWith("Body: ", StringComparison.Ordinal)))
    {
        i++;
    }

    var text = string.Join('\n', lines[i..]);
    if (pathLines.Count > 0)
    {
        text = string.Join('\n', pathLines) + "\n\n" + text;
    }

    return (head, Encoding.Latin1.GetBytes(text));
}
