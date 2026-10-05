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
