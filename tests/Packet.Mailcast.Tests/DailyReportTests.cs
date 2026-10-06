using System.Text;
using Packet.Mailcast.Feedback;

namespace Packet.Mailcast.Tests;

/// <summary>The listener's daily report: small, plain ASCII, and read back exactly as written.</summary>
public class DailyReportTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);

    /// <summary>A full 9-slot daylight day from a web SDR, every field filled in, as worst as is likely.</summary>
    internal static DailyReport FullDay()
    {
        var slots = new List<ReportSlot>();
        for (int i = 0; i < 9; i++)
        {
            slots.Add(new ReportSlot(
                new TimeOnly(8 + i, 0),
                i % 2 == 0 ? "W4" : "W3",
                180 + (i * 11),
                12.4 + i,
                i % 3 == 0 ? -1.26 : 1.04,
                [i < 4 ? "IG" : "IM"],
                new ReportChannel(2, 1.94, -16.6, 0.347, 0.214, 287, 'b')));
        }
        var header = new ReportHeader("0.6.0", "IO91lk", "wessex.zapto.org", 14, 13,
            new Dictionary<string, int> { [ReportErrors.Bbs] = 2, [ReportErrors.Audio] = 1 });
        return new DailyReport("G4ABC", Day, header, slots);
    }

    [Fact]
    public void FullDay_IsCompactPlainAscii()
    {
        var report = FullDay();
        string body = report.Body;

        Assert.Equal("MCR G4ABC 2026-10-06", report.Title);
        Assert.All(body, c => Assert.True(c is '\r' or '\n' or (>= ' ' and <= '~'), $"not plain ASCII: {(int)c}"));
        Assert.InRange(Encoding.ASCII.GetByteCount(body), 300, 600);
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
        Assert.Equal("MCR1 0.6.0 IO91lk wessex.zapto.org 14/13 3:AUD1,BBS2", lines[0]);
        Assert.Equal("08 W4 180 12 -1.3 IG 2 1.9/-17 0.35 0.21 287 b", lines[1]);
        Assert.All(lines.Skip(1), l => Assert.InRange(l.Length, 30, 60));
    }

    [Fact]
    public void WorstCaseDay_StaysSmall()
    {
        // Every hourly slot round the clock (no daylight rule), every field at its widest.
        var slots = Enumerable.Range(0, 24).Select(h => new ReportSlot(
            new TimeOnly(h, 30), "WX", 9999, -12.3, -123.45, ["IU", "PG"],
            new ReportChannel(4, 12.34, -25.4, 1.234, 12.345, 999, 'p'))).ToList();
        var header = new ReportHeader("10.20.30", "IO91lk", "some-long-web-sdr-name.example.org:8073", 999, 999,
            new Dictionary<string, int> { ["BBS"] = 99, ["AUD"] = 99, ["RIG"] = 99, ["HOOK"] = 99 });
        var report = new DailyReport("2E0ABC", Day, header, slots);

        var lines = report.Body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines.Skip(1), l => Assert.True(l.Length <= 60, l));
        Assert.True(lines[0].Length <= 120, lines[0]);
        Assert.True(Encoding.ASCII.GetByteCount(report.Body) < 1700);
        Assert.Equal(report.Body, DailyReport.Parse(report.Title, report.Body).Body);
    }

    [Fact]
    public void Parse_RoundTrip_GivesTheSameReport()
    {
        var report = FullDay();
        var read = DailyReport.Parse(report.Title, report.Body);

        Assert.Equal(report.Callsign, read.Callsign);
        Assert.Equal(report.Day, read.Day);
        Assert.Equal(report.Header with { Errors = read.Header.Errors }, read.Header);
        Assert.Equal(new Dictionary<string, int> { ["BBS"] = 2, ["AUD"] = 1 }, read.Header.Errors);
        Assert.Equal(9, read.Slots.Count);
        var first = read.Slots[0];
        Assert.Equal((new TimeOnly(8, 0), "W4", 180, 12.0, -1.3), (first.Start, first.Waveform, first.Frames, first.SnrDb, first.OffsetHz));
        Assert.Equal(["IG"], first.Verdicts);
        Assert.Equal(new ReportChannel(2, 1.9, -17, 0.35, 0.21, 287, 'b'), first.Channel);
        Assert.Equal(report.Body, read.Body);
    }

    [Fact]
    public void Parse_SlotsWithLittleHeard_AndNoChannel()
    {
        var header = new ReportHeader("0.6.0", null, "sc", 0, 0, new Dictionary<string, int>());
        var report = new DailyReport("M0XYZ", Day, header,
        [
            new ReportSlot(new TimeOnly(9, 0), null, 0, null, null, []),
            new ReportSlot(new TimeOnly(10, 30), null, 0, 3.2, 0.04, ["IP"]),
            new ReportSlot(new TimeOnly(11, 0), "W3", 40, 8, 0, ["IM"], new ReportChannel(1, null, null, 0.05, 0.4, null, 'b')),
        ]);

        Assert.Equal("MCR1 0.6.0 - sc 0/0 0\r\n09 - 0 - - -\r\n1030 - 0 3 +0.0 IP\r\n11 W3 40 8 +0.0 IM 1 - 0.05 0.4 - b\r\n", report.Body);
        var read = DailyReport.Parse(report.Title, report.Body);
        Assert.Null(read.Header.Locator);
        Assert.Empty(read.Header.Errors);
        Assert.Equal(new TimeOnly(10, 30), read.Slots[1].Start);
        Assert.Null(read.Slots[0].Channel);
        Assert.Empty(read.Slots[0].Verdicts);
        Assert.Equal(new ReportChannel(1, null, null, 0.05, 0.4, null, 'b'), read.Slots[2].Channel);
        Assert.Equal(report.Body, read.Body);
    }

    [Fact]
    public void Parse_AsABbsShowsIt_WithRoutingLinesAndBareLineEnds()
    {
        string body = "R:261006/1631Z 4711@GB7XYZ.#24.GBR.EURO BPQ6.0.25\n\nMCR1 0.6.0 IO91lk sc 3/3 0\n08 W4 180 12 -1.3 IG\n09 W3 22 6 +0.4 IM 1 - 0.1 0.3 - b fields-from-a-newer-receiver\n\n73 de a signature\n";

        var read = DailyReport.Parse("MCR G4ABC 2026-10-06", body);

        Assert.Equal(2, read.Slots.Count);
        Assert.Equal("sc", read.Header.Audio);
        Assert.Equal(22, read.Slots[1].Frames);
        Assert.Equal('b', read.Slots[1].Channel!.Basis);
    }

    [Theory]
    [InlineData("Hello", "MCR1 0.6.0 - sc 0/0 0\r\n")]
    [InlineData("MCR G4ABC 2026-13-01", "MCR1 0.6.0 - sc 0/0 0\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "no header here\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "MCR2 0.9.0 - sc 0/0 0\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "MCR1 0.6.0 - sc 0 0\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "MCR1 0.6.0 - sc 0/0 3:BBS2\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "MCR1 0.6.0 - sc 0/0 0\r\n25 W4 1 2 +0.1 IG\r\n")]
    [InlineData("MCR G4ABC 2026-10-06", "MCR1 0.6.0 - sc 0/0 0\r\n09 W4 1 2 +0.1 IG 2 1.9\r\n")]
    public void Parse_NotAReport_SaysWhy(string title, string body)
    {
        Assert.Throws<FormatException>(() => DailyReport.Parse(title, body));
        Assert.False(DailyReport.TryParse(title, body, out var report));
        Assert.Null(report);
    }

    [Fact]
    public void DocsExample_ReadsBackAsWritten()
    {
        // The example in docs/receiver.md, "The daily report's format".
        string body = string.Join("\r\n",
            "MCR1 0.6.0 IO91lk wessex.zapto.org 12/12 1:BBS1",
            "09 W4 196 11 +1.3 IM 2 2.1/-14 0.42 0.31 301 b",
            "10 W3 188 15 +1.2 IG 2 1.9/-17 0.35 0.21 287 b",
            "11 W4 214 18 +1.2 IG 2 1.8/-16 0.31 0.18 279 b",
            "12 W3 190 19 +1.1 IG 1 - 0.12 0.15 - b",
            "13 W4 220 19 +1.1 IG 2 1.8/-19 0.29 0.17 276 b",
            "14 W3 185 17 +1.0 IG 2 1.9/-15 0.36 0.22 284 b",
            "15 W4 162 13 +1.0 IM 2 2.0/-12 0.47 0.38 296 b",
            "16 W3 97 8 +0.9 IM 3 2.2/-10 0.61 0.55 310 b",
            "17 - 0 3 +0.8 IP") + "\r\n";

        var report = DailyReport.Parse("MCR G4ABC 2026-10-06", body);

        Assert.Equal(body, report.Body);
        Assert.Equal(9, report.Slots.Count);
        Assert.True(Encoding.ASCII.GetByteCount(body) < 600);
        Assert.Null(report.Slots[3].Channel!.TwoFDelayMs);
        Assert.Null(report.Slots[8].Channel);
        Assert.Equal(["IP"], report.Slots[8].Verdicts);
    }
}
