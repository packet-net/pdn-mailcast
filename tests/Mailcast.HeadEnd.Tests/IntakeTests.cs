using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Fbb;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;
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

    [Fact]
    public void ReportReader_TakesEachReportFromTheListing_WhenItsSenderIsItsCallsign()
    {
        const string Listing = "13414  16-Oct B$     480 MCAST  @GB7RDG G8BPQ  MCR G8BPQ 2026-10-15\r"
            + "13410  15-Oct BF     512 MCAST  @GB7RDG GM8BPQ MCR GM8BPQ 2026-10-14\r"
            + "13409  15-Oct B$     300 MCAST  @GB7RDG G4XYZ  MCR G8BPQ 2026-10-16\r" // not the sender's
            + "13400  14-Oct B$     100 MCAST  @GB7RDG G4ABC  Hello GB7RDG\r"
            + "de GB7RDG>\r";
        Assert.Equal(
            [("13414", "G8BPQ", new DateOnly(2026, 10, 15)), ("13410", "GM8BPQ", new DateOnly(2026, 10, 14))],
            ReportReader.Reports(Listing));
    }

    [Fact]
    public async Task ReportReader_ReadsAtMostEveryFifteenMinutes()
    {
        using var state = new TempDirectory();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 59, 0, TimeSpan.Zero));
        int tries = 0;
        using var reader = new ReportReader(new ReportsReaderConfig { Password = "secret" }, new ReportLog(state.Path, new MemoryJournal()), new MemoryJournal(), time,
            _ => { tries++; throw new IOException("refused secret"); });
        Assert.Contains("<password>", (await reader.CollectAsync(Today, CancellationToken.None)).Problem, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(IntakeResult.Nothing, await reader.CollectAsync(Today, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull((await reader.CollectAsync(Today, CancellationToken.None)).Problem);
        Assert.Equal(2, tries);
    }

    /// <summary>
    /// A BBS that answers the reader's lines as told: each line received (CR ended) gets the next
    /// reply, the login's three lines the first.
    /// </summary>
    private static async Task<(int Port, Task<List<string>> Received)> ScriptedBbs(params string[] replies)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        async Task<List<string>> Serve()
        {
            var received = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = client.GetStream();
                var line = new StringBuilder();
                int next = 0;
                var one = new byte[1];
                while (await stream.ReadAsync(one, timeout.Token) == 1)
                {
                    if (one[0] != '\r')
                    {
                        line.Append((char)one[0]);
                        continue;
                    }
                    received.Add(line.ToString());
                    line.Clear();
                    if (received.Count >= 3 && next < replies.Length)
                    {
                        await stream.WriteAsync(Encoding.Latin1.GetBytes(replies[next++]), timeout.Token);
                    }
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                listener.Stop();
            }
            return received;
        }
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await Task.Yield();
        return (port, Serve());
    }

    [Fact]
    public async Task ReportReader_AnswersLinBpqsPagePrompt_AndTakesAnyPromptAsTheEnd()
    {
        // The welcome comes after the third login line (BBS); then the list in two pages, and a
        // prompt with a time in it, unlike the one at login.
        var (port, received) = await ScriptedBbs(
            "Hello Mailcast receiver.\rde GB7RDG>\r",
            "13414  16-Oct B$     480 MCAST  @GB7RDG G8BPQ  MCR G8BPQ 2026-10-15\r13413  16-Oct B$     480 MCAST  @GB7RDG G4ABC  MCR G4ABC 2026-10-15\r<A>bort, <CR> Continue..>",
            "13410  15-Oct BF     512 MCAST  @GB7RDG GM8BPQ MCR GM8BPQ 2026-10-14>\r12:01 de GB7RDG>\r",
            "Disconnected\r");
        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var log = new ReportLog(state.Path, journal);
        using var reader = new ReportReader(new ReportsReaderConfig { Port = port, Password = "long-enough" }, log, journal,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero)));

        var result = await reader.CollectAsync(Today, CancellationToken.None);

        Assert.True(result.Problem is null, result.Problem);
        Assert.Equal(["Q0CAST", "long-enough", "BBS", ReportReader.ListCommand, "", "B"], await received);
        Assert.Equal(["G4ABC", "G8BPQ"], log.All.Select(p => p.Key));
        Assert.Contains("reports: 2 new of 2 listed", Assert.Single(journal.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportReader_Prompt_IsNeverAListingLine()
    {
        Assert.Equal("de GB7RDG>", ReportReader.Prompt("x\rde GB7RDG>\r"));
        Assert.Equal("12:01 de GB7RDG>", ReportReader.Prompt("x\r12:01 de GB7RDG>\r"));
        Assert.Null(ReportReader.Prompt("13410  15-Oct BF     512 MCAST  @GB7RDG GM8BPQ Look here>\r"));
        Assert.True(ReportReader.IsPagePrompt(ReportReader.Prompt("x\r<A>bort, <CR> Continue..>")!));
    }

    [Theory]
    [InlineData("pw7", false)]
    [InlineData("long-enough", true)]
    public async Task ReportReader_QuotesTheBbsOnlyWithAPasswordLongEnoughToTakeOut(string password, bool quoted)
    {
        var (port, _) = await ScriptedBbs("Hmm, " + password + " is wrong. password:");
        using var state = new TempDirectory();
        using var reader = new ReportReader(new ReportsReaderConfig { Port = port, Password = password }, new ReportLog(state.Path, new MemoryJournal()), new MemoryJournal(), TimeProvider.System);

        string problem = (await reader.CollectAsync(Today, CancellationToken.None)).Problem!;

        Assert.Contains("did not take the password", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(password, problem, StringComparison.Ordinal);
        Assert.Equal(!quoted, problem.Contains("(reply not shown)", StringComparison.Ordinal));
        Assert.Equal(quoted, problem.Contains("<password> is wrong", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportLog_HoldsAtMostAThousandCallsigns_AndListsTheHundredLatest()
    {
        using var state = new TempDirectory();
        var log = new ReportLog(state.Path, new MemoryJournal());
        var day = new DateOnly(2026, 10, 15);
        var at = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < ReportLog.MostRemembered + 5; i++)
        {
            log.Note($"G{i:D4}X", day, at.AddSeconds(i), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        log.MarkRead();
        Assert.Equal(ReportLog.MostRemembered, log.All.Count);
        Assert.DoesNotContain(log.All, p => p.Key == "G0000X"); // the first to arrive went first
        var listed = log.For(new DateOnly(2026, 10, 16))!;
        Assert.Equal(ReportReceipts.MostListed, listed.Newest.Count);
        Assert.Contains("G1004X", listed.Newest.Keys); // the latest to arrive are listed
        Assert.DoesNotContain("G0500X", listed.Newest.Keys);
        Assert.Equal(ReportLog.MostRemembered, new ReportLog(state.Path, new MemoryJournal()).All.Count);

        // Full of newer ones: an older day's report is let go at once, so it is not new.
        Assert.False(log.Note("G9OLD", day.AddDays(-1), at.AddHours(1), "x"));
        Assert.DoesNotContain(log.All, p => p.Key == "G9OLD");
    }

    [Fact]
    public void ReportLog_KeepsEachCallsignsNewest_AndIgnoresAFutureDay()
    {
        using var state = new TempDirectory();
        var journal = new MemoryJournal();
        var log = new ReportLog(state.Path, journal);
        // Nothing listed, not even "none", until the BBS's list has been read.
        Assert.Null(log.For(new DateOnly(2026, 10, 16)));
        var at = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        Assert.True(log.Note("G4ABC", new DateOnly(2026, 10, 14), at, "A"));
        Assert.True(log.Note("G4ABC", new DateOnly(2026, 10, 15), at, "B"));
        Assert.False(log.Note("G4ABC", new DateOnly(2026, 10, 15), at, "B"));
        Assert.False(log.Note("G4ABC", new DateOnly(2026, 10, 13), at, "C"));
        Assert.False(log.Note("G4XYZ", new DateOnly(2026, 10, 17), at, "D")); // a receiver with its clock wrong
        Assert.False(log.Note("G4-XYZ", new DateOnly(2026, 10, 15), at, "E"));
        Assert.Null(log.For(new DateOnly(2026, 10, 16)));
        log.MarkRead();
        Assert.Equal("reports=2026-10-15:G4ABC", log.For(new DateOnly(2026, 10, 16))!.ToField());
        Assert.Equal("reports=", log.For(new DateOnly(2026, 10, 18))!.ToField());
        Assert.Equal("reports=", new ReportLog(state.Path, journal).For(new DateOnly(2026, 10, 18))!.ToField());

        // Offline work reads it and never writes.
        var offline = new ReportLog(state.Path, journal, readOnly: true);
        Assert.True(offline.Note("G4DEF", new DateOnly(2026, 10, 15), at, "F"));
        offline.MarkRead();
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
