using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Fbb;
using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;

namespace Mailcast.HeadEnd.Tests;

public class IntakeTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static RotationStore Store(TempDirectory dir, MemoryJournal journal, int cap = 32 * 1024) =>
        new(dir.Path, Compression.Default, new ScheduleOptions { MaxBulletinSize = cap + 512 }, journal);

    [Fact]
    public async Task FileDrop_TakesBulletinsAndSetsTheRestAside()
    {
        using var state = new TempDirectory();
        using var drop = new TempDirectory();
        var journal = new MemoryJournal();
        var store = Store(state, journal, cap: 6000);
        File.WriteAllBytes(Path.Combine(drop.Path, "a.bul"), Bulletins.Make(1, 3000).Serialize());
        File.WriteAllBytes(Path.Combine(drop.Path, "b.bul"), Bulletins.Make(2, 3000, type: 'P').Serialize());
        File.WriteAllBytes(Path.Combine(drop.Path, "c.bul"), Bulletins.Make(3, 9000).Serialize());
        File.WriteAllText(Path.Combine(drop.Path, "d.bul"), "not a bulletin");
        File.WriteAllBytes(Path.Combine(drop.Path, "e.bul.tmp"), Bulletins.Make(4, 3000).Serialize());
        File.WriteAllBytes(Path.Combine(drop.Path, "f.bul"), Bulletins.Make(1, 3000).Serialize());

        var intake = new FileDropIntake(drop.Path, store, new IntakePolicy(6000), journal);
        var result = await intake.CollectAsync(Today, CancellationToken.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(4, result.Refused);
        Assert.Equal(1, store.Count);
        Assert.Equal(["e.bul.tmp"], Directory.EnumerateFiles(drop.Path).Select(Path.GetFileName));
        Assert.Equal(["b.bul", "c.bul", "d.bul", "f.bul"], Directory.EnumerateFiles(Path.Combine(drop.Path, "rejected")).Select(Path.GetFileName).Order());
        Assert.Contains(journal.Lines, l => l.Contains("type P is not a bulletin", StringComparison.Ordinal));
        Assert.Contains(journal.Lines, l => l.Contains("already held", StringComparison.Ordinal));
    }

    /// <summary>
    /// A BBS with a telnet login in front of Packet.Fbb's answering side, the way a LinBPQ telnet
    /// port with a BBS user behaves: it prompts, checks the login, sends its SID, and once the
    /// caller has nothing to send, proposes what it holds for the partner.
    /// </summary>
    private sealed class FakeBbs : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public FakeBbs(IReadOnlyList<FbbOutboundMessage> queued)
        {
            _listener.Start();
            _serving = ServeAsync(queued);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Logins { get; } = [];

        public Dictionary<string, FsAnswerKind> Answers { get; } = [];

        private async Task ServeAsync(IReadOnlyList<FbbOutboundMessage> queued)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using TcpClient client = await _listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            // Telnet negotiation before the prompt, which the head end must ignore.
            await stream.WriteAsync(new byte[] { 0xFF, 0xFB, 0x01 }, timeout.Token);
            await stream.WriteAsync("user:"u8.ToArray(), timeout.Token);
            Logins.Add(await ReadLineAsync(stream, timeout.Token));
            await stream.WriteAsync("password:"u8.ToArray(), timeout.Token);
            Logins.Add(await ReadLineAsync(stream, timeout.Token));
            await stream.WriteAsync("\r\nConnected to GB7RDG's Telnet Server\r\n"u8.ToArray(), timeout.Token);

            var session = new FbbSession(new FbbSessionConfig { Role = FbbRole.Answerer, OwnCallsign = "GB7RDG" }, queued);
            bool over = await ApplyAsync(stream, session.Advance(new FbbStart()), timeout.Token);
            var buffer = new byte[4096];
            while (!over)
            {
                int n = await stream.ReadAsync(buffer, timeout.Token);
                if (n == 0)
                {
                    break;
                }
                over = await ApplyAsync(stream, session.Advance(new FbbPeerData(buffer.AsMemory(0, n))), timeout.Token);
            }
        }

        private async Task<bool> ApplyAsync(NetworkStream stream, IReadOnlyList<FbbAction> actions, CancellationToken cancellation)
        {
            bool over = false;
            foreach (var action in actions)
            {
                switch (action)
                {
                    case FbbSendLine line:
                        await stream.WriteAsync(Encoding.Latin1.GetBytes(line.Line + "\r"), cancellation);
                        break;
                    case FbbSendBytes bytes:
                        await stream.WriteAsync(bytes.Data, cancellation);
                        break;
                    case FbbOutboundResult result:
                        Answers[result.Message.Bid] = result.Answer.Kind;
                        break;
                    case FbbSessionOver:
                        over = true;
                        break;
                }
            }
            return over;
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellation)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (await stream.ReadAsync(one, cancellation) == 1 && one[0] != '\r')
            {
                line.Append((char)one[0]);
            }
            return line.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _serving;
            }
            finally
            {
                _listener.Stop();
                _listener.Dispose();
            }
        }
    }

    private static FbbOutboundMessage Queued(Bulletin b) => new()
    {
        MessageType = b.Type,
        From = b.From,
        AtBbs = b.At,
        To = b.To,
        Bid = b.Bid,
        Title = b.Title,
        Body = Bulletin.TextEncoding.GetBytes(b.MessageText),
    };

    [Fact]
    public async Task Fbb_LogsInTakesBulletinsAndRefusesEverythingElse()
    {
        var bulletin = Bulletins.Make(11, 3000);
        var personal = Bulletins.Make(12, 1000, type: 'P');
        var huge = Bulletins.Make(13, 9000);
        var held = Bulletins.Make(14, 2000);
        await using var bbs = new FakeBbs([Queued(bulletin), Queued(personal), Queued(huge), Queued(held)]);

        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var store = Store(state, journal, cap: 6000);
        store.Offer(held, Today.AddDays(-1));
        var config = new FbbIntakeConfig
        {
            Host = "127.0.0.1",
            Port = bbs.Port,
            User = "Q0HEAD",
            Password = "pw",
            Login = [new("user:", "{user}"), new("password:", "{password}")],
            SessionTimeoutSeconds = 60,
        };
        using var intake = new FbbIntake(config, store, new IntakePolicy(6000), journal, TimeProvider.System);

        var result = await intake.CollectAsync(Today, CancellationToken.None);
        await bbs.DisposeAsync();

        Assert.Null(result.Problem);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(3, result.Refused);
        Assert.Equal(["Q0HEAD", "pw"], bbs.Logins);
        Assert.Equal(FsAnswerKind.AlreadyHave, bbs.Answers[held.Bid]);
        Assert.Equal(FsAnswerKind.Accept, bbs.Answers[bulletin.Bid]);
        Assert.Equal(FsAnswerKind.Defer, bbs.Answers[personal.Bid]);
        Assert.Equal(FsAnswerKind.AlreadyHave, bbs.Answers[huge.Bid]);
        Assert.Equal(2, store.Count);
        Assert.Contains(journal.Lines, l => l.Contains("WARNING", StringComparison.Ordinal) && l.Contains(personal.Bid, StringComparison.Ordinal));

        // What was kept is the bulletin as the BBS sent it: same routing lines and text.
        var plan = store.Plan(BroadcastScheduler.Midnight(Today).AddHours(12), 1, Compression.Default, new ScheduleOptions());
        var entry = Assert.Single(plan.Directory.Entries, e => e.Bid == bulletin.Bid);
        Assert.Equal(bulletin.Title, entry.Title);
    }

    /// <summary>A listener's daily report as it reaches GB7RDG: a bulletin to MCAST at GB7RDG's full address.</summary>
    private static Bulletin Report(string call, string to = "MCAST", string at = "GB7RDG.#42.GBR.EURO") => Bulletin.FromMessageText('B', call, to, at,
        $"6279T1{call}", $"MCR {call} 2026-10-06", new DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero),
        "R:261006/1800Z 6279T1@GB7XYZ.#42.GBR.EURO BPQ6.0.25\r\n\r\nMCR1 0.8.1 IO91lk wessex.zapto.org 3/3 0\r\n10 W4 212 18 +1.2 IG\r\n");

    [Fact]
    public async Task Fbb_NeverTakesADailyReport_AndSaysSoOnceForEachBid()
    {
        // One to MCAST, refused at the proposal; one addressed anywhere else but shaped like a
        // report, found once its title and body arrive; and an ordinary bulletin whose title
        // happens to start "MCR ", which is taken.
        var toMcast = Report("G4ABC");
        var lookalike = Report("G4XYZ", to: "ALL", at: "GBR");
        var ordinary = Bulletin.FromMessageText('B', "G4ABC", "ALL", "GBR", "2001_GB7XYZ", "MCR meeting tonight",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), "R:261006/0900Z 2001@GB7XYZ.#42.GBR.EURO BPQ6.0.25\r\n\r\nThe MCR group meets at 8.\r\n");

        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var store = Store(state, journal, cap: 6000);
        int port = 0;
        var config = new FbbIntakeConfig
        {
            Host = "127.0.0.1",
            Port = 1,
            User = "Q0HEAD",
            Password = "pw",
            Login = [new("user:", "{user}"), new("password:", "{password}")],
            SessionTimeoutSeconds = 60,
        };
        using var intake = new FbbIntake(config, store, new IntakePolicy(6000), journal, TimeProvider.System, async cancellation =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellation);
            return client.GetStream();
        });

        // Twice, as a BBS that offered them again would: the same answers, and one line each.
        for (int session = 0; session < 2; session++)
        {
            await using var bbs = new FakeBbs([Queued(toMcast), Queued(lookalike), Queued(ordinary)]);
            port = bbs.Port;

            var result = await intake.CollectAsync(Today, CancellationToken.None);
            await bbs.DisposeAsync();

            Assert.Null(result.Problem);
            // "-" for the one to MCAST, so the BBS counts it handed over and does not offer it again.
            Assert.Equal(FsAnswerKind.AlreadyHave, bbs.Answers[toMcast.Bid]);
            // Asked for, since nothing in the proposal gives it away, then not kept.
            Assert.Equal(FsAnswerKind.Accept, bbs.Answers[lookalike.Bid]);
            Assert.Equal(session == 0 ? FsAnswerKind.Accept : FsAnswerKind.AlreadyHave, bbs.Answers[ordinary.Bid]);
            Assert.Equal(session == 0 ? 1 : 0, result.Accepted);
            Assert.Equal(session == 0 ? 2 : 3, result.Refused);
        }

        Assert.Equal(1, store.Count);
        Assert.False(store.Holds(toMcast.Bid));
        Assert.False(store.Holds(lookalike.Bid));
        Assert.True(store.Holds(ordinary.Bid));
        var plan = store.Plan(BroadcastScheduler.Midnight(Today).AddHours(12), 1, Compression.Default, new ScheduleOptions());
        Assert.DoesNotContain(plan.Directory.Entries, e => e.Title.StartsWith("MCR G4", StringComparison.Ordinal));

        var said = Assert.Single(journal.Lines, l => l.Contains(toMcast.Bid, StringComparison.Ordinal));
        Assert.Contains("refused", said, StringComparison.Ordinal);
        Assert.Contains("addressed to MCAST", said, StringComparison.Ordinal);
        said = Assert.Single(journal.Lines, l => l.Contains(lookalike.Bid, StringComparison.Ordinal));
        Assert.Contains("daily report", said, StringComparison.Ordinal);
        Assert.All(journal.Lines, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));
    }

    /// <summary>
    /// Issue #136: with a report log, a bulletin to MCAST is asked for, so its title says whose
    /// report it is and for which day; that is noted for the directory's list, and the report is
    /// still never kept or broadcast. Something else to MCAST is dropped as before.
    /// </summary>
    [Fact]
    public async Task Fbb_NotesADailyReportForTheDirectory_AndStillNeverKeepsIt()
    {
        var toMcast = Report("G4ABC");
        var lookalike = Report("G4XYZ", to: "ALL", at: "GBR");
        var notReport = Bulletin.FromMessageText('B', "G4DEF", "MCAST", "GB7RDG.#42.GBR.EURO", "3001_GB7XYZ", "Hello GB7RDG",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), "R:261006/0900Z 3001@GB7XYZ.#42.GBR.EURO BPQ6.0.25\r\n\r\nJust saying hello.\r\n");

        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var store = Store(state, journal, cap: 6000);
        var reports = new ReportLog(state.Path, journal);
        int port = 0;
        var config = new FbbIntakeConfig
        {
            Host = "127.0.0.1",
            Port = 1,
            User = "Q0HEAD",
            Password = "pw",
            Login = [new("user:", "{user}"), new("password:", "{password}")],
            SessionTimeoutSeconds = 60,
        };
        using var intake = new FbbIntake(config, store, new IntakePolicy(6000), journal, TimeProvider.System, async cancellation =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellation);
            return client.GetStream();
        }, reports);

        for (int session = 0; session < 2; session++)
        {
            await using var bbs = new FakeBbs([Queued(toMcast), Queued(lookalike), Queued(notReport)]);
            port = bbs.Port;

            var result = await intake.CollectAsync(Today, CancellationToken.None);
            await bbs.DisposeAsync();

            Assert.Null(result.Problem);
            Assert.Equal(FsAnswerKind.Accept, bbs.Answers[toMcast.Bid]);
            Assert.Equal(FsAnswerKind.Accept, bbs.Answers[lookalike.Bid]);
            Assert.Equal(FsAnswerKind.Accept, bbs.Answers[notReport.Bid]);
            Assert.Equal(0, result.Accepted);
            Assert.Equal(3, result.Refused);
            Assert.Equal(2, result.Reports);
        }

        Assert.Equal(0, store.Count);
        Assert.Equal(["G4ABC", "G4XYZ"], reports.All.Select(p => p.Key));
        Assert.All(reports.All, p => Assert.Equal(new DateOnly(2026, 10, 6), p.Value.Day));

        // Said once for each BID, in plain ASCII.
        var said = Assert.Single(journal.Lines, l => l.Contains(toMcast.Bid, StringComparison.Ordinal));
        Assert.Contains("G4ABC for 2026-10-06: noted", said, StringComparison.Ordinal);
        Assert.Contains("never broadcast", said, StringComparison.Ordinal);
        said = Assert.Single(journal.Lines, l => l.Contains(notReport.Bid, StringComparison.Ordinal));
        Assert.Contains("addressed to MCAST", said, StringComparison.Ordinal);
        Assert.All(journal.Lines, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));

        // Kept across a restart, and listed by the next day's directory, which has no report in it.
        var again = new ReportLog(state.Path, journal);
        store.Offer(Bulletins.Make(21, 1000), new DateOnly(2026, 10, 7));
        var plan = store.Plan(BroadcastScheduler.Midnight(new DateOnly(2026, 10, 7)).AddHours(12), 1, Compression.Default, new ScheduleOptions(), reports: again.For(new DateOnly(2026, 10, 7)));
        Assert.Equal(new DateOnly(2026, 10, 6), plan.Directory.Reports!.For("G4ABC"));
        Assert.DoesNotContain(plan.Directory.Entries, e => e.Title.StartsWith("MCR ", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportLog_KeepsEachCallsignsNewest_AndIgnoresAFutureDay()
    {
        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var log = new ReportLog(state.Path, journal);
        // Nothing listed, not even "none", until a report has ever arrived.
        Assert.Null(log.For(new DateOnly(2026, 10, 16)));
        var at = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        Assert.True(log.Note("G4ABC", new DateOnly(2026, 10, 14), at, "A"));
        Assert.True(log.Note("G4ABC", new DateOnly(2026, 10, 15), at, "B"));
        Assert.False(log.Note("G4ABC", new DateOnly(2026, 10, 15), at, "B"));
        Assert.False(log.Note("G4ABC", new DateOnly(2026, 10, 13), at, "C"));
        Assert.False(log.Note("G4XYZ", new DateOnly(2026, 10, 17), at, "D")); // a receiver with its clock wrong
        Assert.False(log.Note("G4-XYZ", new DateOnly(2026, 10, 15), at, "E"));
        Assert.Equal("reports=2026-10-15:G4ABC", log.For(new DateOnly(2026, 10, 16))!.ToField());
        Assert.Equal("reports=", log.For(new DateOnly(2026, 10, 18))!.ToField());
        Assert.Equal("reports=", new ReportLog(state.Path, journal).For(new DateOnly(2026, 10, 18))!.ToField());

        // Offline work reads it and never writes.
        var offline = new ReportLog(state.Path, journal, readOnly: true);
        Assert.True(offline.Note("G4DEF", new DateOnly(2026, 10, 15), at, "F"));
        Assert.DoesNotContain("G4DEF", File.ReadAllText(Path.Combine(state.Path, ReportLog.FileName)), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(state.Path, ReportLog.FileName), "not json");
        Assert.Empty(new ReportLog(state.Path, journal).All);
        Assert.Contains(journal.Lines, l => l.Contains("starting afresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileDrop_NeverTakesADailyReport()
    {
        using var state = new TempDirectory();
        using var drop = new TempDirectory();
        var journal = new MemoryJournal();
        var store = Store(state, journal, cap: 6000);
        File.WriteAllBytes(Path.Combine(drop.Path, "a.bul"), Report("G4ABC").Serialize());
        File.WriteAllBytes(Path.Combine(drop.Path, "b.bul"), Report("G4XYZ", to: "ALL", at: "WW").Serialize());

        var intake = new FileDropIntake(drop.Path, store, new IntakePolicy(6000), journal);
        var result = await intake.CollectAsync(Today, CancellationToken.None);

        Assert.Equal((0, 2), (result.Accepted, result.Refused));
        Assert.Equal(0, store.Count);
        Assert.Equal(["a.bul", "b.bul"], Directory.EnumerateFiles(Path.Combine(drop.Path, "rejected")).Select(Path.GetFileName).Order());
        Assert.Contains(journal.Lines, l => l.Contains("a.bul", StringComparison.Ordinal) && l.Contains("addressed to MCAST", StringComparison.Ordinal));
        Assert.Contains(journal.Lines, l => l.Contains("b.bul", StringComparison.Ordinal) && l.Contains("daily report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fbb_SaysWhyWhenTheBbsCannotBeReached()
    {
        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        // A port nothing listens on: bind one, note it, close it.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var config = new FbbIntakeConfig { Host = "127.0.0.1", Port = port, SessionTimeoutSeconds = 60 };
        using var intake = new FbbIntake(config, Store(state, journal), new IntakePolicy(32 * 1024), journal, TimeProvider.System);
        var result = await intake.CollectAsync(Today, CancellationToken.None);
        Assert.NotNull(result.Problem);
        Assert.Equal(0, result.Accepted);
    }
}
