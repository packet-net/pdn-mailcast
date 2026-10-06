using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The PSK Reporter monitor against a fake MQTT broker on in-memory streams, on a fake clock: the
/// handshake and subscription, spots taken, backoff on failure, a silent broker given up on, and
/// unsubscribing when no reading is wanted. No network.
/// </summary>
public class PskReporterMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ItConnects_SubscribesNarrowly_AndKeepsTheSpots()
    {
        var time = new FakeTimeProvider(Start);
        var broker = new FakeBroker(time);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>(); // written on the monitor's thread
        var monitor = new PskReporterMonitor("GB7RDG", time, log.Enqueue, broker.Connect);
        using var stop = new CancellationTokenSource();
        Task run = monitor.RunAsync(null, stop.Token);

        var c = await broker.NextAsync(time);
        var (clientId, keepAlive, cleanSession) = await c.AcceptAsync();
        Assert.Equal(monitor.ClientId, clientId);
        Assert.Matches("^mailcast-GB7RDG-[0-9a-f]{4}$", clientId);
        Assert.True(cleanSession);
        Assert.Equal(60, keepAlive);
        Assert.Equal(PskFeed.TopicFilters, c.Filters);
        Assert.All(c.Qos, q => Assert.Equal(0, q));

        await c.PublishAsync("pskr/filter/v2/40m/FT8/G4AAA/M0BBB/IO91/IO86/223/279", Payload(1, Start.AddMinutes(-1), "G4AAA", "IO91lk", "M0BBB", "IO86ha"));
        await c.PublishAsync("pskr/filter/v2/40m/FT8/G4AAA/M0BBB/IO91/IO86/223/279", Payload(1, Start.AddMinutes(-1), "G4AAA", "IO91lk", "M0BBB", "IO86ha")); // the same spot again
        await c.PublishAsync("pskr/filter/v2/40m/CW/G4AAA/M0BBB/IO91/IO86/223/279", Payload(2, Start.AddMinutes(-1), "G4AAA", "IO91lk", "M0BBB", "IO86ha", mode: "CW"));
        await Eventually(() => monitor.Feed.Messages == 3);
        Assert.Equal(1, monitor.Feed.SpotsHeld);
        Assert.True(monitor.Feed.Connected);
        Assert.Equal(1, monitor.Current.Forty!.At(500)!.Spots);
        Assert.True(monitor.Current.FeedDown); // up for no time at all yet
        Assert.Contains(log, l => l.StartsWith("pskreporter: subscribed at mqtt.pskreporter.info:1883 as mailcast-GB7RDG-", StringComparison.Ordinal));

        // Twenty minutes of a broker with a spot every 15 s: judged from then on. Each step waits
        // for the monitor to take its spot, so a busy machine cannot make the broker look silent.
        for (int i = 0; i < 20 * 4; i++)
        {
            long taken = monitor.Feed.Messages;
            await c.PublishAsync("pskr/filter/v2/40m/FT8/G4AAA/M0BBB/IO91/IO86/223/279", Payload(10 + i, time.GetUtcNow().AddMinutes(-1), "G4AAA", "IO91lk", "M0BBB", "IO86ha"));
            await Eventually(() => monitor.Feed.Messages > taken);
            time.Advance(TimeSpan.FromSeconds(15));
        }
        await Eventually(() => c.Received.Contains((byte)12)); // PINGREQ, every 30 s however busy the feed
        Assert.False(monitor.Current.FeedDown);
        Assert.Equal(81, monitor.Current.Forty!.At(500)!.Spots);
        Assert.Equal(1, broker.Connections);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ALostConnection_IsMadeAgain_After5Then10Then20Seconds_AndAfresh_OnceOneLasts()
    {
        var time = new FakeTimeProvider(Start);
        var broker = new FakeBroker(time) { Refuse = 2 };
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>(); // written on the monitor's thread
        var monitor = new PskReporterMonitor("GB7RDG", time, log.Enqueue, broker.Connect);
        using var stop = new CancellationTokenSource();
        Task run = monitor.RunAsync(null, stop.Token);

        // Refused, refused, then answered: 5 and 10 s apart (the test moves the clock a second at a time).
        var c = await broker.NextAsync(time);
        Assert.Equal(3, broker.Attempts.Count);
        Assert.Equal(Start, broker.Attempts[0]);
        Gap(broker.Attempts[0], broker.Attempts[1], 5);
        Gap(broker.Attempts[1], broker.Attempts[2], 10);
        await c.AcceptAsync();
        await Eventually(() => monitor.Feed.Connected);
        Assert.Single(log, l => l.Contains("trying again in 5 s", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("pskreporter: subscribed at mqtt.pskreporter.info:1883 as " + monitor.ClientId, StringComparison.Ordinal));

        // The broker hangs up at once: the third failure in a row, so 20 s.
        DateTimeOffset dropped = time.GetUtcNow();
        c.Close();
        await Eventually(() => !monitor.Feed.Connected);
        Assert.NotNull(monitor.Feed.Problem);
        c = await broker.NextAsync(time);
        Gap(dropped, broker.Attempts[^1], 20);
        await c.AcceptAsync();
        await Eventually(() => monitor.Feed.Connected);
        Assert.Equal(1, monitor.Feed.Reconnects);

        // Up for over a minute, then lost: back to 5 s.
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(2);
        }
        dropped = time.GetUtcNow();
        c.Close();
        await Eventually(() => !monitor.Feed.Connected);
        await broker.NextAsync(time);
        Gap(dropped, broker.Attempts[^1], 5);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ASilentBroker_IsPinged_ThenGivenUpOn_After90Seconds()
    {
        var time = new FakeTimeProvider(Start);
        var broker = new FakeBroker(time);
        var monitor = new PskReporterMonitor("GB7RDG", time, null, broker.Connect);
        using var stop = new CancellationTokenSource();
        Task run = monitor.RunAsync(null, stop.Token);

        var c = await broker.NextAsync(time);
        await c.AcceptAsync(answerPings: false);
        DateTimeOffset subscribed = time.GetUtcNow();
        var next = await broker.NextAsync(time);
        Assert.Contains((byte)12, c.Received);
        // 90 s of silence, found at the next 15 s check, then the first 5 s wait.
        TimeSpan gap = broker.Attempts[^1] - subscribed;
        Assert.InRange(gap.TotalSeconds, 95, 112);
        Assert.Contains("no word from the broker", monitor.Feed.Problem, StringComparison.Ordinal);
        await next.AcceptAsync();

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenNoReadingIsWanted_ItSaysDisconnect_AndStaysAway_UntilWantedAgain()
    {
        var time = new FakeTimeProvider(Start);
        var broker = new FakeBroker(time);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>(); // written on the monitor's thread
        var monitor = new PskReporterMonitor("GB7RDG", time, log.Enqueue, broker.Connect);
        bool wanted = true;
        using var stop = new CancellationTokenSource();
        Task run = monitor.RunAsync(_ => wanted, stop.Token);

        var c = await broker.NextAsync(time);
        await c.AcceptAsync();
        wanted = false;
        await Eventually(() => c.Received.Contains((byte)14), time); // DISCONNECT
        await Eventually(() => !monitor.Feed.Connected);
        Assert.Contains("pskreporter: unsubscribed, no slot within the hour", log);
        Assert.True(monitor.Current.FeedDown || monitor.Current.State == IonoState.Unknown);
        for (int i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(2);
        }
        Assert.Equal(1, broker.Connections);

        wanted = true;
        await broker.NextAsync(time);
        Assert.Equal(2, broker.Connections);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NothingItMeets_EverThrows_AndCurrentNeverWaits()
    {
        var time = new FakeTimeProvider(Start);
        var broker = new FakeBroker(time);
        var monitor = new PskReporterMonitor("GB7RDG", time, null, broker.Connect);
        using var stop = new CancellationTokenSource();
        Task run = monitor.RunAsync(null, stop.Token);

        // A broker that says CONNACK "not authorised", then one that sends garbage.
        var c = await broker.NextAsync(time);
        await c.ReadPacketAsync();
        await c.SendAsync([0x20, 0x02, 0x00, 0x05]);
        c = await broker.NextAsync(time);
        await c.ReadPacketAsync();
        await c.SendAsync([0x20, 0xFF, 0xFF, 0xFF, 0xFF]);
        c = await broker.NextAsync(time);
        // Never up, so no reading at all: nothing to send.
        Assert.Same(PskReading.None, monitor.Current);
        Assert.False(run.IsCompleted);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(100, 300)]
    public void Backoff_Doubles_UpToFiveMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PskReporterMonitor.Backoff(failures));

    /// <summary>From <paramref name="from"/> to <paramref name="to"/> is <paramref name="seconds"/>, give or take the second the test moves the clock by.</summary>
    private static void Gap(DateTimeOffset from, DateTimeOffset to, int seconds) =>
        Assert.InRange((to - from).TotalSeconds, seconds, seconds + 1);

    private static byte[] Payload(long sq, DateTimeOffset at, string sc, string sl, string rc, string rl, string mode = "FT8", string band = "40m") =>
        Encoding.UTF8.GetBytes($"{{\"sq\":{sq},\"f\":7075000,\"md\":\"{mode}\",\"rp\":-10,\"t\":{at.ToUnixTimeSeconds()},\"t_tx\":{at.ToUnixTimeSeconds()},\"sc\":\"{sc}\",\"sl\":\"{sl}\",\"rc\":\"{rc}\",\"rl\":\"{rl}\",\"sa\":223,\"ra\":279,\"b\":\"{band}\"}}");

    /// <summary>Waits, by the wall clock, for something the monitor does on its own thread; with <paramref name="time"/>, nudging the fake clock on as it waits.</summary>
    private static async Task Eventually(Func<bool> condition, FakeTimeProvider? time = null)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            time?.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    /// <summary>A broker that hands each connection to the test, and refuses the first <see cref="Refuse"/>.</summary>
    private sealed class FakeBroker(TimeProvider clock)
    {
        private readonly Channel<FakeConnection> _connections = Channel.CreateUnbounded<FakeConnection>();
        private int _attempts;

        public int Refuse { get; init; }

        public List<DateTimeOffset> Attempts { get; } = [];

        public int Connections { get; private set; }

        public Task<Stream> Connect(CancellationToken cancellation)
        {
            lock (Attempts)
            {
                Attempts.Add(clock.GetUtcNow());
                if (++_attempts <= Refuse)
                {
                    throw new IOException("connection refused");
                }
                Connections++;
            }
            var toBroker = Channel.CreateUnbounded<byte[]>();
            var toClient = Channel.CreateUnbounded<byte[]>();
            var connection = new FakeConnection(new DuplexStream(toBroker.Reader, toClient.Writer));
            _connections.Writer.TryWrite(connection);
            return Task.FromResult<Stream>(new DuplexStream(toClient.Reader, toBroker.Writer));
        }

        /// <summary>The next connection, moving the fake clock on a second at a time until it comes.</summary>
        public async Task<FakeConnection> NextAsync(FakeTimeProvider time)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                if (_connections.Reader.TryRead(out var c))
                {
                    return c;
                }
                Assert.True(DateTime.UtcNow < until, "no connection came");
                await Task.Delay(5);
                if (!_connections.Reader.TryPeek(out _))
                {
                    time.Advance(TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    /// <summary>The broker's end of one connection.</summary>
    private sealed class FakeConnection(DuplexStream stream)
    {
        public List<string> Filters { get; } = [];

        public List<int> Qos { get; } = [];

        private readonly List<byte> _received = [];

        /// <summary>The types of the packets read after the handshake.</summary>
        public IReadOnlyList<byte> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        public async Task<(string ClientId, int KeepAlive, bool CleanSession)> AcceptAsync(bool answerPings = true)
        {
            var (type, connect) = await ReadPacketAsync();
            Assert.Equal(1, type);
            int at = 0;
            Assert.Equal("MQTT", ReadString(connect, ref at));
            Assert.Equal(4, connect[at]);
            byte flags = connect[at + 1];
            int keepAlive = BinaryPrimitives.ReadUInt16BigEndian(connect.AsSpan(at + 2));
            at += 4;
            string clientId = ReadString(connect, ref at);
            await SendAsync([0x20, 0x02, 0x00, 0x00]);

            var (subType, subscribe) = await ReadPacketAsync();
            Assert.Equal(8, subType);
            at = 2;
            while (at < subscribe.Length)
            {
                Filters.Add(ReadString(subscribe, ref at));
                Qos.Add(subscribe[at++]);
            }
            var suback = new List<byte> { 0x90 };
            int length = 2 + Filters.Count;
            do
            {
                byte d = (byte)(length % 128);
                length /= 128;
                suback.Add(length > 0 ? (byte)(d | 0x80) : d);
            }
            while (length > 0);
            suback.AddRange([subscribe[0], subscribe[1]]);
            suback.AddRange(Enumerable.Repeat((byte)0, Filters.Count));
            await SendAsync([.. suback]);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var (t, _) = await ReadPacketAsync();
                        lock (_received)
                        {
                            _received.Add(t);
                        }
                        if (t == 12 && answerPings)
                        {
                            await SendAsync([0xD0, 0x00]);
                        }
                    }
                }
                catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or ChannelClosedException)
                {
                    // the client went
                }
            });
            return (clientId, keepAlive, (flags & 0x02) != 0);
        }

        public Task PublishAsync(string topic, byte[] payload)
        {
            byte[] t = Encoding.UTF8.GetBytes(topic);
            var body = new List<byte> { (byte)(t.Length >> 8), (byte)t.Length };
            body.AddRange(t);
            body.AddRange(payload);
            var packet = new List<byte> { 0x30 };
            int length = body.Count;
            do
            {
                byte d = (byte)(length % 128);
                length /= 128;
                packet.Add(length > 0 ? (byte)(d | 0x80) : d);
            }
            while (length > 0);
            packet.AddRange(body);
            return SendAsync([.. packet]);
        }

        public async Task SendAsync(byte[] bytes) => await stream.WriteAsync(bytes);

        public void Close() => stream.Dispose();

        public async Task<(byte Type, byte[] Body)> ReadPacketAsync()
        {
            var one = new byte[1];
            await stream.ReadExactlyAsync(one);
            byte header = one[0];
            int length = 0;
            for (int shift = 0; ; shift += 7)
            {
                await stream.ReadExactlyAsync(one);
                length |= (one[0] & 0x7F) << shift;
                if ((one[0] & 0x80) == 0)
                {
                    break;
                }
            }
            var body = new byte[length];
            await stream.ReadExactlyAsync(body);
            return ((byte)(header >> 4), body);
        }

        private static string ReadString(byte[] body, ref int at)
        {
            int n = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
            string s = Encoding.UTF8.GetString(body, at + 2, n);
            at += 2 + n;
            return s;
        }
    }

    /// <summary>One end of an in-memory connection: reads one channel, writes the other; disposing closes what it writes.</summary>
    private sealed class DuplexStream(ChannelReader<byte[]> reads, ChannelWriter<byte[]> writes) : Stream
    {
        private ReadOnlyMemory<byte> _left;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left.IsEmpty)
            {
                if (!await reads.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
                if (!reads.TryRead(out var next))
                {
                    return 0;
                }
                _left = next;
            }
            int n = Math.Min(buffer.Length, _left.Length);
            _left[..n].CopyTo(buffer);
            _left = _left[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!writes.TryWrite(buffer.ToArray()))
            {
                throw new IOException("closed");
            }
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            writes.TryComplete();
            base.Dispose(disposing);
        }
    }
}
