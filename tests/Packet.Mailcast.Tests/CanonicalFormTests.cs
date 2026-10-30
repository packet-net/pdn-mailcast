using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Packet.Mailcast.Tests;

public sealed class CanonicalFormTests(ITestOutputHelper output)
{
    private static readonly Encoding L1 = Encoding.Latin1;

    private static byte[] B(string s) => L1.GetBytes(s);

    private static CanonicalBulletin Canon(string text, string title = "t") => CanonicalForm.Canonicalise(B(text), title);

    private static string Body(string text, string title = "t") => L1.GetString(Canon(text, title).Body);

    private static void RoundTrips(byte[] original, string title)
    {
        var c = CanonicalForm.Canonicalise(original, title);
        var (rebuilt, rebuiltTitle) = CanonicalForm.Rebuild(c.Body, c.Envelope);
        Assert.Equal(original, rebuilt);
        Assert.Equal(title, rebuiltTitle);

        var again = BulletinEnvelope.Parse(c.Envelope.Serialise());
        Assert.Equal(c.Envelope, again);
        var (rebuilt2, _) = CanonicalForm.Rebuild(c.Body, again);
        Assert.Equal(original, rebuilt2);
    }

    // ---- Rules ----

    [Fact]
    public void LinBpq_RouteLinesAndTheBlankAfterThem_AreDropped()
    {
        const string text = "R:261019/0850Z 14961@GB7BEX.#38.GBR.EURO LinBPQ6.0.25\r\nR:261019/0850z 49399@GB7OSW.#16.GBR.EU LinBPQ6.0.25\r\n\r\nPlain bulletin.\r\nSecond line.\r\n";
        var c = Canon(text);
        Assert.Equal("Plain bulletin.\nSecond line.\n", L1.GetString(c.Body));
        Assert.Equal(LineEnding.CrLf, c.Envelope.LineEnding);
        Assert.Equal("R:261019/0850Z 14961@GB7BEX.#38.GBR.EURO LinBPQ6.0.25\r\nR:261019/0850z 49399@GB7OSW.#16.GBR.EU LinBPQ6.0.25\r\n\r\n", L1.GetString(c.Envelope.Top.Span));
        Assert.Equal("\r\n", L1.GetString(c.Envelope.Bottom.Span));
        RoundTrips(B(text), "t");
    }

    [Fact]
    public void Fbb_RouteLineWithAtColon_IsDropped()
    {
        Assert.Equal("Body\n", Body("R:261029/2112Z @:GB7LAF.#99.GBR.EURO #:101 [Labtown] $:2_GB7LAA-1\r\n\r\nBody\r\n"));
    }

    [Fact]
    public void RouteLineWithoutTheZone_IsDropped()
    {
        Assert.Equal("Body\n", Body("R:261024/2145 @:LU4ECL.LP.BA.SOAM Sally 7.2.061 Hola\r\n\r\nBody\r\n"));
    }

    [Fact]
    public void Fbb_OriginBlock_IsDroppedAfterRouteLines()
    {
        const string text = "R:261029/2112Z @:GB7LAF.#99.GBR.EURO #:102 [Labtown] $:102_GB7LAF\r\n\r\nFrom: G0LAB@GB7LAF.#99.GBR.EURO\r\nTo  : LABTST@WW\r\n\r\nPlain bulletin.\r\n";
        Assert.Equal("Plain bulletin.\n", Body(text));
        RoundTrips(B(text), "t");
    }

    [Fact]
    public void Fbb_OriginBlock_OnlyTheFirstPairIsDropped()
    {
        const string text = "R:261029/2112Z @:GB7LAF.#99.GBR.EURO #:102 [X] $:102_GB7LAF\r\n\r\nFrom: G0LAB@GB7LAF.#99.GBR.EURO\r\nTo  : LABTST@WW\r\n\r\nFrom: me@home\r\nTo  : you@there\r\n\r\nText\r\n";
        Assert.Equal("From: me@home\nTo  : you@there\n\nText\n", Body(text));
    }

    [Fact]
    public void Fbb_FromToPairWithoutRouteLines_IsAnAuthorsAndStays()
    {
        const string text = "From: G0LAB@GB7LAF.#99.GBR.EURO\r\nTo  : LABTST@WW\r\n\r\nText\r\n";
        Assert.Equal("From: G0LAB@GB7LAF.#99.GBR.EURO\nTo  : LABTST@WW\n\nText\n", Body(text));
    }

    [Fact]
    public void Fbb_ToWithOneSpace_IsNotTheOriginBlock()
    {
        const string text = "R:261029/2112Z @:GB7LAF.#99.GBR.EURO $:1_X\r\n\r\nFrom: G0LAB@GB7LAF.#99.GBR.EURO\r\nTo: LABTST@WW\r\n\r\nText\r\n";
        Assert.Equal("From: G0LAB@GB7LAF.#99.GBR.EURO\nTo: LABTST@WW\n\nText\n", Body(text));
    }

    [Fact]
    public void AuthorsFromLineInTheMiddle_IsNeverTouched()
    {
        const string text = "Intro\r\nFrom: someone@somewhere\r\nTo  : x@y\r\n\r\nR:261029/2112Z 5@X LinBPQ6.0.25\r\nEnd\r\n";
        Assert.Equal("Intro\nFrom: someone@somewhere\nTo  : x@y\n\nR:261029/2112Z 5@X LinBPQ6.0.25\nEnd\n", Body(text));
    }

    [Fact]
    public void RouteLineNotAtTheTop_IsKept()
    {
        Assert.Equal("Hello\nR:261019/0850Z 1@X LinBPQ6.0.25\nBye\n", Body("Hello\r\nR:261019/0850Z 1@X LinBPQ6.0.25\r\nBye\r\n"));
    }

    [Fact]
    public void LinesThatLookLikeRouteLinesButAreNot_AreKept()
    {
        Assert.Equal("R: not a route line\nR:261019 nope\nBody\n", Body("R: not a route line\nR:261019 nope\nBody\n"));
    }

    [Fact]
    public void FbbReaderPath_WithIndentedContinuations_IsDropped()
    {
        const string text = "Path: !VK2RZ!VE3CGR!LU9DCE!\r\n      !LU1DBQ!IR0EQ!\r\n\r\nR:261019/0850Z 1@X LinBPQ6.0.25\r\n\r\nBody\r\n";
        Assert.Equal("Body\n", Body(text));
        RoundTrips(B(text), "t");
    }

    [Fact]
    public void PathLineInTheMiddle_IsKept()
    {
        Assert.Equal("Body\nPath: !A!B!\n", Body("Body\nPath: !A!B!\n"));
    }

    [Fact]
    public void XRouter_HeaderBlock_IsDropped()
    {
        const string text = "R:261030/0048Z @:GB7LAX.#99.GBR.EURO [] #:2 XrLin505p\nDate: Thu, 29 Oct 2026 23:58:25 +0000\nFrom: g0lab@gb7lax.#99.gbr.euro <g0lab@gb7lax-2.#99.gbr.euro>\nTo: labtst@ww\nSubject: Plain bulletin\nMessage-ID: AE3DDA002LAX\n\nPlain bulletin.\n";
        var c = Canon(text);
        Assert.Equal("Plain bulletin.\n", L1.GetString(c.Body));
        Assert.Equal(LineEnding.Lf, c.Envelope.LineEnding);
        RoundTrips(B(text), "Plain bulletin");
    }

    [Fact]
    public void XRouterLikeHeadersWithoutRouteLines_AreAnAuthorsAndStay()
    {
        const string text = "Date: Thu, 29 Oct 2026 23:58:25 +0000\nFrom: a@b <a@b>\nTo: c\nSubject: s\nMessage-ID: X\n\nText\n";
        Assert.Equal(text, Body(text));
    }

    [Fact]
    public void OpenBcm_HeaderBlock_IsDropped()
    {
        const string text = "R:261030/0136z @:GB7LAM.#99.GBR.EU [OpenBCM] obcm1.13-38-g65b7\r\nFrom: G0LAB @ GB7LAM.#99.GBR.EU\r\nTo:   LABTST @ WW\r\nX-Info: No login password\r\n\r\n0001 text\r\n";
        Assert.Equal("0001 text\n", Body(text));
        RoundTrips(B(text), "t");
    }

    [Fact]
    public void OpenBcm_BlockWithoutXInfo_IsDropped()
    {
        Assert.Equal("Hi\n", Body("R:261030/0136z @:GB7LAM.#99.GBR.EU $:1_X\nFrom: G0LAB @ GB7LAM\nTo:   LABTST @ WW\n\nHi\n"));
    }

    [Fact]
    public void OpenBcm_ToWithOneSpace_IsNotTheBlock()
    {
        const string text = "R:261030/0136z @:GB7LAM.#99.GBR.EU $:1_X\nFrom: G0LAB @ GB7LAM\nTo: LABTST @ WW\n\nHi\n";
        Assert.Equal("From: G0LAB @ GB7LAM\nTo: LABTST @ WW\n\nHi\n", Body(text));
    }

    [Fact]
    public void OpenBcm_TitleOverflowLine_IsDroppedOnlyForAFullTitle()
    {
        const string text = "R:261030/0136z @:GB7LAM.#99.GBR.EU [OpenBCM] x\nFrom: G0LAB @ GB7LAM\nTo:   LABTST @ WW\nX-Info: No login password\n\nthe rest of the title\nReal body\n";
        var full = new string('t', CanonicalForm.OpenBcmTitleLimit);
        Assert.Equal("Real body\n", Body(text, full));
        Assert.Equal("the rest of the title\nReal body\n", Body(text, "short"));
        RoundTrips(B(text), full);
    }

    [Fact]
    public void LeadingAndTrailingBlankLines_AreTrimmed_AndSoAreSpaceOnlyOnes_ButNotTheMiddle()
    {
        var c = Canon("\r\n \r\nText  \r\n \r\n \t\r\n\r\n");
        Assert.Equal("Text  \n", L1.GetString(c.Body));
        Assert.Equal("\r\n \r\n", L1.GetString(c.Envelope.Top.Span));
        Assert.Equal("\r\n \r\n \t\r\n\r\n", L1.GetString(c.Envelope.Bottom.Span));
        Assert.Equal("a\n \nb\n", Body("a\r\n \r\nb\r\n"));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\n", LineEnding.CrLf)]
    [InlineData("a\nb\nc\n", LineEnding.Lf)]
    [InlineData("a\rb\rc\r", LineEnding.Cr)]
    [InlineData("a\r\nb\nc\r\n", LineEnding.Raw)]
    public void LineEndings_AreNormalisedToLf_OrKeptRawWhenMixed(string text, LineEnding style)
    {
        var c = Canon(text);
        Assert.Equal(style, c.Envelope.LineEnding);
        if (style != LineEnding.Raw)
        {
            Assert.Equal("a\nb\nc\n", L1.GetString(c.Body));
        }

        RoundTrips(B(text), "t");
    }

    [Fact]
    public void SameTextInThreeLineEndings_GivesTheSameBody()
    {
        Assert.Equal(Body("a\r\nb\r\n"), Body("a\nb\n"));
        Assert.Equal(Body("a\r\nb\r\n"), Body("a\rb\r"));
        Assert.Equal(Body("a\r\nb\r\n"), Body("a\r\nb"));
    }

    [Fact]
    public void FullTitle_IsKeptInTheEnvelope()
    {
        var title = new string('x', 100) + "é";
        Assert.Equal(title, Canon("a\n", title).Envelope.Title);
    }

    [Fact]
    public void TitleOutsideLatin1_IsRejected() =>
        Assert.Throws<ArgumentException>(() => Canon("a\n", "☃"));

    [Fact]
    public void EmptyAndBlankOnlyInputs_RoundTrip()
    {
        foreach (var s in new[] { "", "\n", "\r\n\r\n", "\r", "\r\r\n\n" })
        {
            var c = Canon(s);
            Assert.Empty(c.Body);
            RoundTrips(B(s), "t");
        }
    }

    [Fact]
    public void RouteLinesOnly_RoundTrip() => RoundTrips(B("R:261019/0850Z 1@X LinBPQ6.0.25\r\n\r\n"), "t");

    [Fact]
    public void EightBitBytesAndControlCharacters_AreKept()
    {
        var original = new byte[] { (byte)'a', 0xE9, 0x07, 0x1B, 0x0C, 0xFF, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n' };
        var c = CanonicalForm.Canonicalise(original, "t");
        Assert.Equal(new byte[] { (byte)'a', 0xE9, 0x07, 0x1B, 0x0C, 0xFF, (byte)'\n', (byte)'b', (byte)'\n' }, c.Body);
        RoundTrips(original, "t");
    }

    [Fact]
    public void SameBulletinFromDifferentBbses_HasOneBody()
    {
        const string content = "Line one\nLine two\n";
        var bpq = "R:261019/0850Z 14961@GB7BEX.#38.GBR.EURO LinBPQ6.0.25\r\n\r\n" + content.Replace("\n", "\r\n");
        var fbb = "R:261019/0851Z @:GB7CIP.#32.GBR.EURO #:5 [X] $:5_GB7CIP\r\n" + bpq + "\r\n";
        var jnos = "\r\n" + content + "\n";
        Assert.Equal(content, Body(bpq));
        Assert.Equal(content, Body(fbb));
        Assert.Equal(content, Body(jnos));
    }

    // ---- Envelope serialisation ----

    [Fact]
    public void Envelope_Serialisation_IsCompactAndDeterministic()
    {
        var e = new BulletinEnvelope(B("R:x\r\n"), B("\r\n"), "Hi", LineEnding.CrLf);
        Assert.Equal(e.Serialise(), e.Serialise());
        Assert.Equal(new byte[] { 1, 1, 5, (byte)'R', (byte)':', (byte)'x', 13, 10, 2, 13, 10, 2, (byte)'H', (byte)'i' }, e.Serialise());
    }

    [Fact]
    public void Envelope_LongFields_UseMultiByteLengths()
    {
        var e = new BulletinEnvelope(new byte[300], new byte[16384], new string('t', 200), LineEnding.Raw);
        Assert.Equal(e, BulletinEnvelope.Parse(e.Serialise()));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 2, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 9, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 5, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 0x80 })]
    public void Envelope_Malformed_IsRejected(byte[] data) => Assert.Throws<FormatException>(() => BulletinEnvelope.Parse(data));

    [Fact]
    public void Rebuild_BodyWithoutFinalLineFeed_IsRejected() =>
        Assert.Throws<ArgumentException>(() => CanonicalForm.Rebuild(B("abc"), new BulletinEnvelope([], [], "t", LineEnding.Lf)));

    // ---- Property: Rebuild(Canonicalise(x)) == x ----

    [Fact]
    public void RoundTrip_RandomInputs()
    {
        var rng = new Random(141);
        string[] pieces =
        [
            "\r\n", "\n", "\r", "R:261019/0850Z 1@X LinBPQ6.0.25", "R:261019/0850z @:X.#1 $:1_X", "From: a@b", "To  : c@d", "To: c@d",
            "Date: Thu, 29 Oct 2026 23:58:25 +0000", "From: a @ b", "To:   a @ b", "X-Info: x", "Path: !A!", "  !B!", "text", " ", "", "\t", "é", "\u001a", "/EX", ">From x",
        ];
        for (var n = 0; n < 20000; n++)
        {
            var sb = new StringBuilder();
            var count = rng.Next(0, 24);
            for (var k = 0; k < count; k++)
            {
                sb.Append(pieces[rng.Next(pieces.Length)]);
                if (rng.Next(3) > 0)
                {
                    sb.Append(rng.Next(3) switch { 0 => "\r\n", 1 => "\n", _ => "\r" });
                }
            }

            RoundTrips(B(sb.ToString()), rng.Next(3) == 0 ? new string('x', CanonicalForm.OpenBcmTitleLimit) : "title");
        }
    }

    [Fact]
    public void RoundTrip_RandomBytes()
    {
        var rng = new Random(142);
        for (var n = 0; n < 5000; n++)
        {
            var data = new byte[rng.Next(0, 80)];
            for (var k = 0; k < data.Length; k++)
            {
                data[k] = rng.Next(4) == 0 ? (byte)"\r\n:R"[rng.Next(4)] : (byte)rng.Next(256);
            }

            RoundTrips(data, "t");
        }
    }

    // ---- Evidence from research/138 ----

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "research", "138")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("research/138 not found above the test binaries.");
    }

    private static readonly Regex Header = new(@"^(From|To|Type/Status|Date/Time|Bid|Title|Body|Path): ?(.*)$", RegexOptions.CultureInvariant);

    /// <summary>Splits a corpus file (a BBS reader's output) into its header fields and the text after them, as research/138/compare.py does.</summary>
    private static (Dictionary<string, string> Head, byte[] Text) ParseCorpus(string path)
    {
        var lines = L1.GetString(File.ReadAllBytes(path)).Split('\n');
        var head = new Dictionary<string, string>();
        var path1 = new List<string>();
        var i = 0;
        while (i < lines.Length && Header.Match(lines[i]) is { Success: true } m)
        {
            head[m.Groups[1].Value] = m.Groups[2].Value;
            if (m.Groups[1].Value == "Path")
            {
                path1.Add(lines[i]);
            }

            i++;
        }

        var j = i;
        while (j < lines.Length && lines[j].Length == 0)
        {
            j++;
        }

        if (j < lines.Length && lines[j].StartsWith("Body: ", StringComparison.Ordinal))
        {
            i = j + 1;
        }

        while (i < lines.Length && lines[i].Length == 0)
        {
            i++;
        }

        // FBB wraps a long Path onto indented continuation lines. The Path is routing, so it stays at the top of
        // the text for the canonical rules to remove, as a head end reading from FBB would see it.
        while (head.ContainsKey("Path") && i < lines.Length && Regex.IsMatch(lines[i], @"^\s+![A-Z0-9!.#-]+!$"))
        {
            path1.Add(lines[i]);
            i++;
        }

        if (path1.Count > 0)
        {
            return (head, B(string.Join("\n", path1) + "\n\n" + string.Join("\n", lines[i..])));
        }

        return (head, B(string.Join("\n", lines[i..])));
    }

    [Fact]
    public void Corpus_EveryBulletin_RoundTripsExactly()
    {
        var root = Path.Combine(RepoRoot(), "research", "138", "corpus");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "msg-*.txt", SearchOption.AllDirectories))
        {
            var (head, text) = ParseCorpus(file);
            RoundTrips(text, head.GetValueOrDefault("Title", ""));
            RoundTrips(File.ReadAllBytes(file), "");
            count++;
        }

        Assert.True(count > 900, $"only {count} corpus files");
        output.WriteLine($"{count} corpus bulletins round-trip exactly.");
    }

    [Fact]
    public void LabCaptures_EveryStoredFile_RoundTripsExactly()
    {
        var root = Path.Combine(RepoRoot(), "research", "138", "lab");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "captured"), "m_*.mes", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.Contains("captured") && (f.Contains("/PMS/") || f.Contains("/info/")) && !f.EndsWith(".Q", StringComparison.Ordinal)))
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "bulletins"), "*", SearchOption.AllDirectories)))
        {
            RoundTrips(File.ReadAllBytes(file), "title");
            count++;
        }

        Assert.True(count > 300, $"only {count} lab files");
        output.WriteLine($"{count} lab stored files round-trip exactly.");
    }

    [Fact]
    public void LabCaptures_ForwardedCopies_HaveTheOriginsBody()
    {
        var root = Path.Combine(RepoRoot(), "research", "138", "lab", "captured", "b1f");
        string Canon1(string rel) => L1.GetString(CanonicalForm.Canonicalise(File.ReadAllBytes(Path.Combine(root, rel)), "t").Body);

        // t01 posted on LinBPQ, held at LAA (no R: line), at LAF (one hop) and at LAB (two hops).
        var origin = Canon1("laa/Mail/m_000002.mes");
        Assert.Equal("Plain bulletin.\nSecond line.\nThird line.\n", origin);
        Assert.Equal(origin, Canon1("laf/mail/mail1/m_000101.mes"));
        Assert.Equal(origin, Canon1("lab/Mail/m_000001.mes"));

        // Posted on FBB (LAF): LAF's own copy has no origin block, LAB's copy has FBB's R: line and From/To block.
        Assert.Equal(Canon1("laf/mail/mail2/m_000102.mes"), Canon1("lab/Mail/m_000002.mes"));
    }

    [Fact]
    public void Corpus_CopiesOfTheSameBid_ShareACanonicalBody()
    {
        var root = Path.Combine(RepoRoot(), "research", "138", "corpus");
        var copies = new Dictionary<string, List<(string Station, string File, byte[] Body)>>();
        foreach (var file in Directory.EnumerateFiles(root, "msg-*.txt", SearchOption.AllDirectories))
        {
            var (head, text) = ParseCorpus(file);
            if (head.TryGetValue("Bid", out var bid))
            {
                var station = Path.GetFileName(Path.GetDirectoryName(file))!;
                if (!copies.TryGetValue(bid, out var list))
                {
                    copies[bid] = list = [];
                }

                list.Add((station, Path.GetFileName(file), CanonicalForm.Canonicalise(text, head.GetValueOrDefault("Title", "")).Body));
            }
        }

        var shared = copies.Where(kv => kv.Value.Select(c => c.Station).Distinct().Count() > 1).ToList();
        var same = shared.Count(kv => kv.Value.Select(c => L1.GetString(c.Body)).Distinct().Count() == 1);
        var differing = shared.Where(kv => kv.Value.Select(c => L1.GetString(c.Body)).Distinct().Count() > 1).Select(kv => kv.Key).Order().ToList();
        output.WriteLine($"{same} of {shared.Count} BIDs held by two or more stations have one canonical body. Differing: {string.Join(", ", differing)}");

        // research/138/README.md: 112 shared BIDs, 111 expected identical once the rules are applied; ARLK079 carries
        // genuinely different content, and JNOS-affected ones (>From, folded lines) may differ.
        Assert.True(shared.Count >= 100);
        Assert.True(same >= shared.Count - 1, $"{same} of {shared.Count}; differing: {string.Join(", ", differing)}");
    }
}
