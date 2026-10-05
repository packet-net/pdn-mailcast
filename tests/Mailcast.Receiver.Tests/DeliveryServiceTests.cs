using System.Threading.Channels;
using Mailcast.Core;
using Mailcast.Receiver.Delivery;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

public class DeliveryServiceTests
{
    /// <summary>A BBS whose every session the test answers by hand.</summary>
    private sealed class ScriptedBbs : IBbsSession
    {
        public Channel<(IReadOnlyList<Bulletin> Offered, TaskCompletionSource<SessionReport> Answer)> Calls { get; } =
            Channel.CreateUnbounded<(IReadOnlyList<Bulletin>, TaskCompletionSource<SessionReport>)>();

        public Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
        {
            var answer = new TaskCompletionSource<SessionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Writer.TryWrite((bulletins, answer));
            return answer.Task.WaitAsync(cancellation);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public Rig(params Bulletin[] bulletins)
        {
            Intake = new Intake(Dir.Path, _ => { });
            foreach (var frame in Samples.Frames(bulletins))
            {
                Intake.Offer(frame);
            }
            Intake.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();
            Ledger = new DeliveryLedger(Dir.Path);
            Service = new DeliveryService(Intake, Bbs, Ledger, Time, _ => { });
            Service.Waiting += (delay, timer) => Waits.Writer.TryWrite((delay, timer));
            _run = Service.RunAsync(_stop.Token);
        }

        public TempDirectory Dir { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

        public ScriptedBbs Bbs { get; } = new();

        public Intake Intake { get; }

        public DeliveryLedger Ledger { get; }

        public DeliveryService Service { get; }

        public Channel<(TimeSpan? Delay, Task? Timer)> Waits { get; } = Channel.CreateUnbounded<(TimeSpan?, Task?)>();

        public async Task<(IReadOnlyList<Bulletin> Offered, TaskCompletionSource<SessionReport> Answer)> NextCallAsync() =>
            await Bbs.Calls.Reader.ReadAsync();

        public async Task<TimeSpan?> NextWaitAsync() => (await Waits.Reader.ReadAsync()).Delay;

        public async Task<(TimeSpan? Delay, Task? Timer)> NextTimedWaitAsync() => await Waits.Reader.ReadAsync();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run;
            await Intake.DisposeAsync();
            Dir.Dispose();
        }
    }

    private static SessionReport Answer(IReadOnlyList<Bulletin> offered, params DeliveryVerdict[] verdicts) =>
        new(true, null, [.. offered.Select((b, i) => new DeliveryOutcome(b.Bid, verdicts[i]))], 0);

    private static SessionReport Unreachable(IReadOnlyList<Bulletin> offered) =>
        new(false, "connection refused", [.. offered.Select(b => new DeliveryOutcome(b.Bid, DeliveryVerdict.NotOffered))], 0);

    [Fact]
    public async Task FinalAnswers_LeaveTheOutbox_OthersStayAndAreOfferedLater()
    {
        await using var rig = new Rig(Samples.Bulletin(1), Samples.Bulletin(2), Samples.Bulletin(3), Samples.Bulletin(4));

        var (offered, answer) = await rig.NextCallAsync();
        Assert.Equal(4, offered.Count);
        var verdicts = offered.Select(b => b.Bid switch
        {
            "1_GB7RDG" => DeliveryVerdict.Accepted,
            "2_GB7RDG" => DeliveryVerdict.AlreadyHad,
            "3_GB7RDG" => DeliveryVerdict.Deferred,
            _ => DeliveryVerdict.Refused,
        }).ToArray();
        answer.SetResult(Answer(offered, verdicts));

        Assert.Equal(DeliveryService.LaterRetry, await rig.NextWaitAsync());
        Assert.Equal(["3_GB7RDG"], rig.Intake.Pending().Select(b => b.Bid));
        Assert.Equal(DeliveryVerdict.Accepted, rig.Ledger.Latest("1_GB7RDG")!.Verdict);
        Assert.Equal(DeliveryVerdict.AlreadyHad, rig.Ledger.Latest("2_GB7RDG")!.Verdict);
        Assert.Equal(DeliveryVerdict.Deferred, rig.Ledger.Latest("3_GB7RDG")!.Verdict);
        Assert.Equal(DeliveryVerdict.Refused, rig.Ledger.Latest("4_GB7RDG")!.Verdict);

        rig.Time.Advance(DeliveryService.LaterRetry);
        (offered, answer) = await rig.NextCallAsync();
        Assert.Equal(["3_GB7RDG"], offered.Select(b => b.Bid));
        answer.SetResult(Answer(offered, DeliveryVerdict.Accepted));

        Assert.Null(await rig.NextWaitAsync());
        Assert.Empty(rig.Intake.Pending());
    }

    [Fact]
    public async Task UnreachableBbs_BacksOffFurtherEachTime_ThenDelivers()
    {
        await using var rig = new Rig(Samples.Bulletin(1));
        var started = rig.Time.GetUtcNow();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (offered, answer) = await rig.NextCallAsync();
            answer.SetResult(Unreachable(offered));
            var (wait, timer) = await rig.NextTimedWaitAsync();
            Assert.Equal(DeliveryService.Backoff[attempt], wait);
            Assert.Equal("connection refused", rig.Service.LastFailure);

            // Not a moment before the wait is up: the fake clock fires its timers inside Advance.
            rig.Time.Advance(wait!.Value - TimeSpan.FromSeconds(1));
            Assert.False(timer!.IsCompleted);
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(timer.IsCompleted);
        }

        var (last, lastAnswer) = await rig.NextCallAsync();
        Assert.Equal(started + TimeSpan.FromSeconds(30 + 60 + 120), rig.Time.GetUtcNow());
        lastAnswer.SetResult(Answer(last, DeliveryVerdict.Accepted));

        Assert.Null(await rig.NextWaitAsync());
        Assert.Null(rig.Service.LastFailure);
        Assert.Empty(rig.Intake.Pending());
    }

    [Fact]
    public async Task UnconfirmedTransfer_ConfirmedByAlreadyHadNextTime_IsRecordedAsAccepted()
    {
        await using var rig = new Rig(Samples.Bulletin(1));

        var (offered, answer) = await rig.NextCallAsync();
        answer.SetResult(Answer(offered, DeliveryVerdict.Unconfirmed));
        Assert.Equal(DeliveryService.LaterRetry, await rig.NextWaitAsync());
        Assert.Single(rig.Intake.Pending());

        rig.Time.Advance(DeliveryService.LaterRetry);
        (offered, answer) = await rig.NextCallAsync();
        answer.SetResult(Answer(offered, DeliveryVerdict.AlreadyHad));
        Assert.Null(await rig.NextWaitAsync());

        Assert.Equal(DeliveryVerdict.Accepted, rig.Ledger.Latest("1_GB7RDG")!.Verdict);
        Assert.Equal(DeliveryVerdict.Accepted, new DeliveryLedger(rig.Dir.Path).Latest("1_GB7RDG")!.Verdict);
    }

    [Fact]
    public async Task NewBulletin_WakesAnIdleService()
    {
        await using var rig = new Rig();
        Assert.Null(await rig.NextWaitAsync());

        foreach (var frame in Samples.Frames([Samples.Bulletin(9)]))
        {
            rig.Intake.Offer(frame);
        }

        var (offered, answer) = await rig.NextCallAsync();
        Assert.Equal(["9_GB7RDG"], offered.Select(b => b.Bid));
        answer.SetResult(Answer(offered, DeliveryVerdict.Accepted));
        Assert.Null(await rig.NextWaitAsync());
    }
}

public class DeliveryLedgerTests
{
    [Fact]
    public void Open_CompactsToTheNewestAnswerPerBid()
    {
        using var dir = new TempDirectory();
        var ledger = new DeliveryLedger(dir.Path);
        var t = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        ledger.Record(Samples.Bulletin(1), new DeliveryOutcome("1_GB7RDG", DeliveryVerdict.Deferred), t);
        ledger.Record(Samples.Bulletin(1), new DeliveryOutcome("1_GB7RDG", DeliveryVerdict.Accepted), t.AddMinutes(10));
        ledger.Record(Samples.Bulletin(2), new DeliveryOutcome("2_GB7RDG", DeliveryVerdict.AlreadyHad), t.AddMinutes(11));
        File.AppendAllText(Path.Combine(dir.Path, "deliveries.jsonl"), "{\"time\":\"2026-10-05T12:2");

        var reopened = new DeliveryLedger(dir.Path);

        Assert.Equal(2, File.ReadAllLines(Path.Combine(dir.Path, "deliveries.jsonl")).Length);
        Assert.Equal(DeliveryVerdict.Accepted, reopened.Latest("1_GB7RDG")!.Verdict);
        Assert.Equal(DeliveryVerdict.AlreadyHad, reopened.Latest("2_GB7RDG")!.Verdict);
    }
}
