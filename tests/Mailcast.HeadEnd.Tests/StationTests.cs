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
        public List<(string Path, string? Key, JsonNode Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.Single() : null, JsonNode.Parse(body)!));
            var (status, text) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task StationApiClient_TakesRenewsAndReleasesTheLease()
    {
        using var handler = new Handler((_, body) => JsonNode.Parse(body)!["release"] is null
            ? (HttpStatusCode.OK, """{"held": true, "subChannel": 4, "expires": "2026-10-05T12:02:00Z", "renewed": false, "seconds": 120, "capped": false}""")
            : (HttpStatusCode.OK, """{"released": true}"""));
        using var api = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", handler);

        var lease = await api.TakeLeaseAsync(4, 120, CancellationToken.None);
        Assert.True(lease.Held);
        Assert.Equal(120, lease.Seconds);
        Assert.True(await api.ReleaseLeaseAsync(4, CancellationToken.None));

        Assert.Equal("/api/txlease", handler.Requests[0].Path);
        Assert.Equal("secret", handler.Requests[0].Key);
        Assert.Equal(4, handler.Requests[0].Body["subChannel"]!.GetValue<int>());
        Assert.Equal(120, handler.Requests[0].Body["seconds"]!.GetValue<int>());
        Assert.True(handler.Requests[1].Body["release"]!.GetValue<bool>());
        Assert.Equal(4, handler.Requests[1].Body["subChannel"]!.GetValue<int>());
    }

    [Fact]
    public async Task StationApiClient_ReportsAnotherHoldersLease()
    {
        using var handler = new Handler((_, _) => (HttpStatusCode.Conflict, """{"held": true, "subChannel": 2, "expires": "2026-10-05T12:01:00Z", "refused": "sub-channel 2 holds the transmit lease"}"""));
        using var api = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", handler);
        var lease = await api.TakeLeaseAsync(4, 120, CancellationToken.None);
        Assert.False(lease.Held);
        Assert.Equal("sub-channel 2 holds the transmit lease until 2026-10-05T12:01:00Z", lease.Problem);
    }

    [Theory]
    [InlineData(200, """{"transmitted": true, "sent": "1800 Hz for 30 s", "refused": null, "failed": null}""", ToneOutcome.Sent)]
    [InlineData(409, """{"transmitted": false, "sent": null, "refused": "the channel did not clear within 60 s, so the test was withdrawn and nothing was transmitted", "failed": null}""", ToneOutcome.ChannelBusy)]
    [InlineData(409, """{"transmitted": false, "sent": null, "refused": "a test transmission is already running", "failed": null}""", ToneOutcome.Refused)]
    [InlineData(404, "txTest is off", ToneOutcome.Refused)]
    [InlineData(500, """{"transmitted": false, "failed": "the card went away"}""", ToneOutcome.Failed)]
    public async Task StationApiClient_ReadsEveryToneAnswer(int status, string body, ToneOutcome expected)
    {
        using var handler = new Handler((_, _) => ((HttpStatusCode)status, body));
        using var api = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", handler);
        var answer = await api.SendToneAsync(4, 1800, 30, CancellationToken.None);
        Assert.Equal(expected, answer.Outcome);
        var request = handler.Requests.Single();
        Assert.Equal("/api/txtest", request.Path);
        Assert.False(request.Body["twoTone"]!.GetValue<bool>());
        Assert.Equal(1800, request.Body["toneHz"]!.GetValue<double>());
        Assert.Equal(30, request.Body["seconds"]!.GetValue<double>());
        Assert.Equal(4, request.Body["subChannel"]!.GetValue<int>());
    }
}
