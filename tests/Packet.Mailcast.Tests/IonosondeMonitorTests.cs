using System.Net;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The ionosonde poller on a fake clock and a fake network: the order it asks in, backing off,
/// and that a failure only ever leaves the reading UNKNOWN. No real network.
/// </summary>
public class IonosondeMonitorTests
{
    private const string Agent = "pdn-mailcast-headend/0.6.0 (+https://github.com/packet-net/pdn-mailcast; GB7RDG)";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Propagation", name));

    /// <summary>Answers by host, and keeps every request.</summary>
    private sealed class FakeNetwork : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Giro { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> PropQuest { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        public int To(string host) => Requests.Count(r => r.RequestUri!.Host == host);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            return request.RequestUri!.Host == "lgdc.uml.edu" ? Giro(request) : PropQuest(request);
        }
    }

    private static Task<HttpResponseMessage> Text(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    /// <summary>The real GIRO answer, its times moved so the last sounding is <paramref name="minutesOld"/> before <paramref name="now"/>.</summary>
    private static string GiroAt(DateTimeOffset now, int minutesOld)
    {
        var last = new DateTimeOffset(2026, 10, 6, 13, 15, 0, TimeSpan.Zero);
        var shift = now.AddMinutes(-minutesOld) - last;
        var lines = Fixture("giro-FF051-2026-10-06.txt").Split('\n').Select(l =>
            l.Length > 24 && l[4] == '-' && DateTimeOffset.TryParse(l[..24], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? (t + shift).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'.000Z'", System.Globalization.CultureInfo.InvariantCulture) + l[24..]
                : l);
        return string.Join('\n', lines);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 6, 13, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshGiro_IsOneRequest_WithTheUserAgent_AndNoPropQuest()
    {
        var time = new FakeTimeProvider(Start);
        var net = new FakeNetwork { Giro = r => Text(r.RequestUri!.Query.Contains("ursiCode=RL052", StringComparison.Ordinal) ? GiroAt(Start, 5).Replace("FF051, FAIRFORD", "RL052, CHILTON", StringComparison.Ordinal) : "") };
        using var monitor = new IonosondeMonitor(new IonoSettings(), Agent, time, handler: net);
        await monitor.PollAsync(CancellationToken.None);
        var request = Assert.Single(net.Requests);
        Assert.Equal(Agent, string.Join(' ', request.Headers.UserAgent.Select(p => p.ToString())));
        Assert.DoesNotContain('@', request.Headers.UserAgent.ToString());
        Assert.Equal(
            "https://lgdc.uml.edu/fastchar/getbest?ursiCode=RL052&charName=foF2,MUF(D),M(D)&DMUF=3000&fromDate=2026/10/06+07:20:00&toDate=2026/10/06+13:20:00",
            request.RequestUri!.ToString());
        var r = monitor.Current;
        Assert.Equal(("RL052", IonoSource.Giro, 5), (r.Station, r.Source, r.AgeMinutes));
        Assert.NotEqual(IonoState.Unknown, r.State);
        Assert.Equal(Agent, IonosondeMonitor.UserAgentFor("0.6.0", "GB7RDG"));
    }

    [Fact]
    public async Task GiroGone_FallsBackToPropQuest_AndBacksOffGiro()
    {
        var time = new FakeTimeProvider(Start);
        var net = new FakeNetwork { PropQuest = _ => Text(Fixture("propquest-RL052-FF051-2026-10-05-06.json")) };
        var log = new List<string>();
        using var monitor = new IonosondeMonitor(new IonoSettings(), Agent, time, log.Add, net);
        await monitor.PollAsync(CancellationToken.None);
        Assert.Equal((1, 1), (net.To("lgdc.uml.edu"), net.To("propquest.org")));
        Assert.Equal(
            "https://propquest.org/JSON-grab-ionosphere?DATE1=2026-10-06&DATE2=2026-10-06&OBSERVATORY=RL052,FF051,DB049",
            net.Requests[1].RequestUri!.ToString());
        var r = monitor.Current;
        Assert.Equal(("FF051", IonoSource.PropQuest, 20, IonoState.Marginal), (r.Station, r.Source, r.AgeMinutes, r.State));
        Assert.Single(log, l => l.Contains("GIRO answered 404", StringComparison.Ordinal));

        // Still failing 15 minutes on: tried again, then left for 30 minutes, logged only the once.
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PollAsync(CancellationToken.None);
        Assert.Equal(2, net.To("lgdc.uml.edu"));
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PollAsync(CancellationToken.None);
        Assert.Equal(2, net.To("lgdc.uml.edu"));
        Assert.Equal(3, net.To("propquest.org"));
        Assert.Single(log, l => l.Contains("GIRO", StringComparison.Ordinal));

        // By now the last PROPquest sounding (13:00) is 50 minutes old: UNKNOWN, with its values.
        r = monitor.Current;
        Assert.Equal((IonoState.Unknown, 50, 5.7), (r.State, r.AgeMinutes, r.FoF2));
    }

    [Fact]
    public async Task TooManyRequests_HonoursRetryAfter()
    {
        var time = new FakeTimeProvider(Start);
        var net = new FakeNetwork
        {
            Giro = _ =>
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
                return Task.FromResult(busy);
            },
            PropQuest = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };
        using var monitor = new IonosondeMonitor(new IonoSettings(), Agent, time, handler: net);
        for (int i = 0; i < 9; i++)
        {
            await monitor.PollAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(15));
        }
        // GIRO at 0 and 2 h; PROPquest at 0, 15, 45 min and 1 h 45 (15, 30, 60 minutes apart).
        Assert.Equal(2, net.To("lgdc.uml.edu"));
        Assert.Equal(4, net.To("propquest.org"));
        Assert.False(monitor.Current.HasSounding);
        Assert.Equal(IonoState.Unknown, monitor.Current.State);
    }

    [Fact]
    public async Task NothingItMeets_EverThrows_AndCurrentNeverWaitsForTheNetwork()
    {
        var time = new FakeTimeProvider(Start);
        var hang = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var net = new FakeNetwork { Giro = _ => hang.Task };
        using var monitor = new IonosondeMonitor(new IonoSettings(), Agent, time, handler: net);
        using var stop = new CancellationTokenSource();
        Task poll = monitor.PollAsync(stop.Token);
        Assert.False(poll.IsCompleted);
        Assert.Equal(IonoState.Unknown, monitor.Current.State); // at once, while the request hangs
        hang.SetException(new HttpRequestException("no route to host"));
        await poll;
        Assert.Equal(IonoState.Unknown, monitor.Current.State);

        // Junk where the data should be is a failure too, not an exception.
        net.Giro = _ => Text("<html>moved</html>");
        net.PropQuest = _ => Text("{\"oops\": 1}");
        time.Advance(TimeSpan.FromHours(5));
        await monitor.PollAsync(CancellationToken.None);
        Assert.False(monitor.Current.HasSounding);
    }

    [Fact]
    public async Task Runs_OnlyWhenWanted_AndAtMostEveryQuarterHour()
    {
        var time = new FakeTimeProvider(Start);
        var net = new FakeNetwork();
        using var monitor = new IonosondeMonitor(new IonoSettings(), Agent, time, handler: net);
        Assert.False(monitor.ShouldPoll(Start, _ => false));
        Assert.True(monitor.ShouldPoll(Start, _ => true));
        using var stop = new CancellationTokenSource();
        Task loop = monitor.RunAsync(_ => true, stop.Token);
        for (int i = 0; i < 100 && monitor.LastPoll is null; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(Start, monitor.LastPoll);
        Assert.False(monitor.ShouldPoll(Start.AddMinutes(14), null));
        Assert.True(monitor.ShouldPoll(Start.AddMinutes(15), null));
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        // Off when no stations are listed.
        using var off = new IonosondeMonitor(new IonoSettings { Stations = [] }, Agent, time, handler: net);
        await off.RunAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(off.LastPoll);
    }

    [Fact]
    public void PropQuest_AsksForYesterdayToo_InTheSmallHours()
    {
        var uri = IonosondeMonitor.PropQuestQuery(["RL052"], new DateTimeOffset(2026, 10, 7, 2, 0, 0, TimeSpan.Zero));
        Assert.Equal("https://propquest.org/JSON-grab-ionosphere?DATE1=2026-10-06&DATE2=2026-10-07&OBSERVATORY=RL052", uri.ToString());
    }
}
