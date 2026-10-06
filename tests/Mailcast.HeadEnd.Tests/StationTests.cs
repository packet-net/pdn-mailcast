using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mailcast.HeadEnd.Station;

namespace Mailcast.HeadEnd.Tests;

public class StationTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public void Ax25Ui_EncodesAddressesControlAndPid()
    {
        byte[] frame = Ax25Ui.Encode("MCAST", "GB7RDG-3", [1, 2, 3]);
        Assert.Equal(19, frame.Length);
        Assert.Equal("MCAST ", Encoding.ASCII.GetString([.. frame.Take(6).Select(b => (byte)(b >> 1))]));
        Assert.Equal(0xE0, frame[6]);
        Assert.Equal("GB7RDG", Encoding.ASCII.GetString([.. frame.Skip(7).Take(6).Select(b => (byte)(b >> 1))]));
        Assert.Equal(0x60 | (3 << 1) | 1, frame[13]);
        Assert.Equal(0x03, frame[14]);
        Assert.Equal(0xF0, frame[15]);
        Assert.Equal([1, 2, 3], frame[16..]);
        Assert.False(Ax25Ui.IsValidAddress("TOOLONGCALL"));
        Assert.False(Ax25Ui.IsValidAddress("G4ABC-16"));
    }

    [Fact]
    public void Kiss_AckModeFramesRoundTripThroughTheDecoder()
    {
        byte[] data = [0xC0, 0xDB, 0x01, 0xC0];
        byte[] wire = Kiss.EncodeAckMode(0, 0x1234, data);
        Assert.Equal(0xC0, wire[0]);
        Assert.Equal(0x0C, wire[1]);
        var decoder = new KissDecoder();
        var frames = wire.Select(b => decoder.Feed([b])).SelectMany(f => f).ToList();
        var frame = Assert.Single(frames);
        Assert.Equal(12, frame.Command);
        Assert.Equal([0x34, 0x12, 0xC0, 0xDB, 0x01, 0xC0], frame.Payload);
    }

    [Fact]
    public async Task KissTcpLink_SendsAckModeFramesAndReadsTheAcknowledgements()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connector = new KissTcpConnector("127.0.0.1", port, portNibble: 0);

        using var timeout = new CancellationTokenSource(Generous);
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await using IKissLink link = await connector.ConnectAsync(timeout.Token);
        using TcpClient modem = await accepted;
        var stream = modem.GetStream();

        await link.SendAsync(7, new byte[] { 0x82, 0xC0 }, timeout.Token);
        await link.SendAsync(8, new byte[] { 0x01 }, timeout.Token);

        // The fake modem reads both frames, then acknowledges them, with a received data frame between.
        var decoder = new KissDecoder();
        var got = new List<KissFrame>();
        var buffer = new byte[256];
        while (got.Count < 2)
        {
            int n = await stream.ReadAsync(buffer, timeout.Token);
            got.AddRange(decoder.Feed(buffer.AsSpan(0, n)));
        }
        Assert.Equal([7, 0, 0x82, 0xC0], got[0].Payload);
        Assert.Equal(12, got[0].Command);
        await stream.WriteAsync(Kiss.Encode(0, Kiss.AckModeCommand, [7, 0]), timeout.Token);
        await stream.WriteAsync(Kiss.Encode(0, Kiss.DataCommand, [1, 2, 3, 4]), timeout.Token);
        await stream.WriteAsync(Kiss.Encode(0, Kiss.AckModeCommand, [8, 0]), timeout.Token);

        Assert.Equal(7, await link.Acks.ReadAsync(timeout.Token));
        Assert.Equal(8, await link.Acks.ReadAsync(timeout.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(string Path, string? Key, JsonNode? Body)> Requests { get; } = [];

        public bool Hang { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.Single() : null, body.Length > 0 ? JsonNode.Parse(body) : null));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var (status, text) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static StationApiClient Client(Handler handler, TimeSpan? limit = null) =>
        new(new Uri("http://127.0.0.1:8107/"), "secret", limit ?? TimeSpan.FromSeconds(30), handler);

    [Fact]
    public async Task StationApiClient_TakesRenewsReadsDropsAndReleasesTheLease()
    {
        using var handler = new Handler((request, body) => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, """{"held": true, "subChannel": 4, "expires": "2026-10-05T12:02:00Z", "channelBusy": true}""")
            : JsonNode.Parse(body)!["release"] is not null
                ? (HttpStatusCode.OK, """{"released": true}""")
                : JsonNode.Parse(body)!["seconds"] is null
                    ? (HttpStatusCode.OK, """{"dropped": 3}""")
                    : (HttpStatusCode.OK, """{"held": true, "subChannel": 4, "expires": "2026-10-05T12:02:00Z", "renewed": false, "seconds": 120, "capped": false, "channelBusy": false}"""));
        using var api = Client(handler);

        var lease = await api.TakeLeaseAsync(4, 120, 10, CancellationToken.None);
        Assert.True(lease.Held);
        Assert.Equal(120, lease.Seconds);
        Assert.False(lease.ChannelBusy);
        Assert.True((await api.ReadLeaseAsync(CancellationToken.None)).ChannelBusy);
        Assert.True(await api.DropQueuedAsync(4, CancellationToken.None));
        Assert.True(await api.ReleaseLeaseAsync(4, CancellationToken.None));

        Assert.All(handler.Requests, r => Assert.Equal("/api/txlease", r.Path));
        Assert.All(handler.Requests, r => Assert.Equal("secret", r.Key));
        var take = handler.Requests[0].Body!;
        Assert.Equal(4, take["subChannel"]!.GetValue<int>());
        Assert.Equal(120, take["seconds"]!.GetValue<int>());
        Assert.Equal(10, take["maxCarrierWaitSeconds"]!.GetValue<int>());
        Assert.Null(handler.Requests[1].Body);
        var drop = handler.Requests[2].Body!;
        Assert.True(drop["dropQueued"]!.GetValue<bool>());
        Assert.Equal(4, drop["subChannel"]!.GetValue<int>());
        Assert.Null(drop["release"]);
        var release = handler.Requests[3].Body!;
        Assert.True(release["release"]!.GetValue<bool>());
        Assert.True(release["dropQueued"]!.GetValue<bool>());
        Assert.Equal(4, release["subChannel"]!.GetValue<int>());
    }

    [Fact]
    public async Task StationApiClient_ReportsAnotherHoldersLease()
    {
        using var handler = new Handler((_, _) => (HttpStatusCode.Conflict, """{"held": true, "subChannel": 2, "expires": "2026-10-05T12:01:00Z", "refused": "sub-channel 2 holds the transmit lease"}"""));
        using var api = Client(handler);
        var lease = await api.TakeLeaseAsync(4, 120, 10, CancellationToken.None);
        Assert.False(lease.Held);
        Assert.Equal("sub-channel 2 holds the transmit lease until 2026-10-05T12:01:00Z", lease.Problem);
        Assert.Null(lease.ChannelBusy);
    }

    [Fact]
    public async Task StationApiClient_GivesUpOnALeaseCallThatIsNotAnswered()
    {
        using var handler = new Handler((_, _) => (HttpStatusCode.OK, "{}")) { Hang = true };
        using var api = Client(handler, TimeSpan.FromMilliseconds(50));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => api.TakeLeaseAsync(4, 120, 10, CancellationToken.None));
        Assert.Contains("did not answer", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, """{"transmitted": true, "sent": "1800 Hz for 30 s", "refused": null, "failed": null}""", ToneOutcome.Sent)]
    [InlineData(409, """{"transmitted": false, "sent": null, "refused": "the channel did not clear within 60 s", "failed": null}""", ToneOutcome.Refused)]
    [InlineData(404, "txTest is off", ToneOutcome.Refused)]
    [InlineData(500, """{"transmitted": false, "failed": "the card went away"}""", ToneOutcome.Failed)]
    public async Task StationApiClient_ReadsEveryToneAnswer(int status, string body, ToneOutcome expected)
    {
        using var handler = new Handler((_, _) => ((HttpStatusCode)status, body));
        using var api = Client(handler);
        var answer = await api.SendToneAsync(4, 1800, 30, null, CancellationToken.None);
        Assert.Equal(expected, answer.Outcome);
        Assert.Equal(status, answer.Status);
        Assert.False(answer.KnowsProbe);
        var request = handler.Requests.Single();
        Assert.Equal("/api/txtest", request.Path);
        Assert.False(request.Body!["twoTone"]!.GetValue<bool>());
        Assert.Equal(1800, request.Body["toneHz"]!.GetValue<double>());
        Assert.Equal(30, request.Body["seconds"]!.GetValue<double>());
        Assert.Equal(4, request.Body["subChannel"]!.GetValue<int>());
        Assert.Null(request.Body["probe"]);
    }

    [Theory]
    [InlineData(200, """{"transmitted": true, "sent": "4050 Hz for 10 s, then the probe", "probe": "zc255-2400-rrc015-v1", "probeComplete": true}""", ToneOutcome.Sent, true, "zc255-2400-rrc015-v1", true)]
    [InlineData(200, """{"transmitted": true, "sent": "4050 Hz for 10 s", "probe": null, "probeComplete": false}""", ToneOutcome.Sent, true, null, false)]
    [InlineData(200, """{"transmitted": true, "sent": "4050 Hz for 10 s", "refused": null, "failed": null}""", ToneOutcome.Sent, false, null, null)]
    [InlineData(409, """{"transmitted": false, "refused": "a 18.0 s test is over this station's 15 s limit (txTest.maxSeconds), so nothing was transmitted", "probe": null, "probeComplete": null}""", ToneOutcome.Refused, true, null, null)]
    public async Task StationApiClient_AsksForTheProbeAfterTheTone_AndReadsWhatBecameOfIt(int status, string body, ToneOutcome expected, bool knows, string? id, bool? complete)
    {
        using var handler = new Handler((_, _) => ((HttpStatusCode)status, body));
        using var api = Client(handler);
        var answer = await api.SendToneAsync(4, 4050, 10, new ProbeRequest("zc255", 1.5, 4050), CancellationToken.None);
        Assert.Equal(expected, answer.Outcome);
        Assert.Equal(knows, answer.KnowsProbe);
        Assert.Equal(id, answer.ProbeId);
        Assert.Equal(complete, answer.ProbeComplete);
        var probe = handler.Requests.Single().Body!["probe"]!.AsObject();
        Assert.Equal(["kind", "gapSeconds", "audioHz"], probe.Select(p => p.Key));
        Assert.Equal("zc255", probe["kind"]!.GetValue<string>());
        Assert.Equal(1.5, probe["gapSeconds"]!.GetValue<double>());
        Assert.Equal(4050, probe["audioHz"]!.GetValue<double>());
    }
}
