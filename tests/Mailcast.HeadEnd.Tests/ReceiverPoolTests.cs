using System.Net;
using Mailcast.HeadEnd.Relay;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.Mailcast.Discovery;

namespace Mailcast.HeadEnd.Tests;

/// <summary>Issue #218, part 1: the head end finds receivers with no setting and reads their objects and pieces.</summary>
public class ReceiverPoolTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition(), what);
    }

    private static ReceiverPool Pool(ObjectCatalogue catalogue, MemoryJournal journal, FakeTimeProvider time, int localPort, FakeMdnsNetwork? net = null) =>
        new(catalogue, journal, time, new ReceiverPoolOptions
        {
            LocalPort = localPort,
            Transport = net is null ? null : () => net.Join(),
            IsOwnAddress = a => IPAddress.IsLoopback(a),
        });

    [Fact]
    public async Task AReceiverOnThisMachine_IsFoundFirst_AndItsObjectsAndPiecesAreRead()
    {
        using var receiver = new FakeReceiver();
        var obj = FakeReceiver.Make(1);
        int k = obj.SourceSymbols;
        var row = receiver.Add(obj, Enumerable.Range(0, k).Select(i => ((uint)i, true, (string?)"GB7RDG")), complete: true);
        row.Listings.Add(("GB7RDG", "9c1e5a07d3b2f648"));
        var journal = new MemoryJournal();
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        using var pool = Pool(catalogue, journal, new FakeTimeProvider(Start), receiver.Port);

        await pool.ReadNowAsync(CancellationToken.None);

        var seen = Assert.Single(pool.Receivers);
        Assert.Equal("this machine", seen.Found);
        Assert.Equal("ok", seen.State);
        Assert.Single(journal.Lines, l => l.StartsWith("receiver: found this machine at http://127.0.0.1:", StringComparison.Ordinal));
        var held = catalogue.Get(obj.ObjectId)!;
        Assert.Equal(k, held.Pieces.Count);
        Assert.Equal(k, held.OnAirPieces);
        Assert.Equal(["GB7RDG"], held.HeardFrom);
        Assert.Equal((byte?)0x71, held.ContentType);
        Assert.Equal(obj.Oti, held.Oti);
        Assert.Equal("9c1e5a07d3b2f648", held.ListedBy["GB7RDG"]);
        Assert.True(held.Complete);
        Assert.Equal(obj.Bytes.ToArray(), held.Data);
        // The frames are exactly what the station sent.
        Assert.Equal(obj.Frame(2).ToBytes(), held.Pieces[2].Frame);

        // Nothing changed: the next read asks the index only, no piece or data again.
        int before = receiver.Requests.Count;
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Equal(before + 1, receiver.Requests.Count);
    }

    [Fact]
    public async Task OnlyWhatChanged_IsReadAgain_FromTheCursor()
    {
        using var receiver = new FakeReceiver();
        var obj = FakeReceiver.Make(2);
        var row = receiver.Add(obj, [(0u, true, "G1ABC"), (1u, true, "G1ABC")]);
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        using var pool = Pool(catalogue, new MemoryJournal(), new FakeTimeProvider(Start), receiver.Port);
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Equal(2, catalogue.Get(obj.ObjectId)!.Pieces.Count);

        receiver.AddPiece(row, 2, false, "my tool");
        await pool.ReadNowAsync(CancellationToken.None);

        var held = catalogue.Get(obj.ObjectId)!;
        Assert.Equal(3, held.Pieces.Count);
        Assert.Equal(2, held.OnAirPieces); // the handed-in piece is there to send but is not counted as heard on air
        lock (receiver.Requests)
        {
            Assert.Contains(receiver.Requests, r => r.Contains("/pieces?since=", StringComparison.Ordinal) && !r.Contains("since=0&", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TwoReceivers_ArePooledWithoutDuplicates_AndOnAirBeatsHandedIn()
    {
        using var a = new FakeReceiver();
        using var b = new FakeReceiver();
        var obj = FakeReceiver.Make(3);
        a.Add(obj, [(0u, false, "my tool"), (1u, true, "GB7RDG"), (2u, true, "GB7RDG")]);
        var second = b.Add(obj, [(0u, true, "G8BPQ"), (1u, true, "GB7RDG"), (3u, true, "G8BPQ")]);
        second.Listings.Add(("G8BPQ", "0123456789abcdef"));
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        var journal = new MemoryJournal();
        var net = new FakeMdnsNetwork();
        using var pool = Pool(catalogue, journal, new FakeTimeProvider(Start), FakeReceiver.FreePort(), net);
        using var announceA = new MdnsAnnouncer(net.Join(), "rx a", a.Port, ReceiverService.Text("0.1.0", false), _ => { }, addresses: () => [IPAddress.Loopback]);
        using var announceB = new MdnsAnnouncer(net.Join(), "rx b", b.Port, ReceiverService.Text("0.1.0", false), _ => { }, addresses: () => [IPAddress.Loopback]);
        announceA.Start();
        announceB.Start();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => pool.RunAsync(stop.Token));
        await Eventually(() => pool.Receivers.Count == 2 && pool.Receivers.All(r => r.State == "ok"), "both receivers found and read");

        var held = catalogue.Get(obj.ObjectId)!;
        Assert.Equal([0u, 1u, 2u, 3u], held.Pieces.Select(p => p.Esi));
        Assert.Equal(4, held.OnAirPieces); // piece 0 was only handed in at one, heard at the other
        Assert.Equal(["G8BPQ", "GB7RDG"], held.HeardFrom);
        Assert.Equal("0123456789abcdef", held.ListedBy["G8BPQ"]);
        Assert.All(pool.Receivers, r => Assert.Equal("network", r.Found));
        Assert.Contains(journal.Lines, l => l.StartsWith("receiver: found rx a at http://127.0.0.1:", StringComparison.Ordinal) && l.Contains("version 0.1.0", StringComparison.Ordinal));

        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task AReceiverThatGoesQuiet_IsLostWithOneLine_AndWhatItGaveIsKept()
    {
        using var receiver = new FakeReceiver();
        var obj = FakeReceiver.Make(4);
        receiver.Add(obj, [(0u, true, "GB7RDG")]);
        var journal = new MemoryJournal();
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        var net = new FakeMdnsNetwork();
        using var pool = Pool(catalogue, journal, new FakeTimeProvider(Start), FakeReceiver.FreePort(), net);
        var announce = new MdnsAnnouncer(net.Join(), "rx a", receiver.Port, ReceiverService.Text("0.1.0", false), _ => { }, addresses: () => [IPAddress.Loopback]);
        announce.Start();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => pool.RunAsync(stop.Token));
        await Eventually(() => pool.Receivers.Count == 1, "found");

        announce.Dispose(); // says goodbye
        await Eventually(() => pool.Receivers.Count == 0, "lost");

        Assert.Single(journal.Lines, l => l.StartsWith("receiver: lost rx a", StringComparison.Ordinal));
        Assert.Single(catalogue.Get(obj.ObjectId)!.Pieces);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task TheLocalReceiver_AlsoAnnouncedOnTheNetwork_IsOneReceiver()
    {
        using var receiver = new FakeReceiver();
        var net = new FakeMdnsNetwork();
        var journal = new MemoryJournal();
        using var pool = Pool(new ObjectCatalogue(TimeProvider.System), journal, new FakeTimeProvider(Start), receiver.Port, net);
        using var announce = new MdnsAnnouncer(net.Join(), "same one", receiver.Port, ReceiverService.Text("0.1.0", false), _ => { }, addresses: () => [IPAddress.Loopback]);
        announce.Start();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => pool.RunAsync(stop.Token));
        await Eventually(() => pool.Receivers.Count == 1 && pool.Receivers[0].State == "ok", "found");
        await Task.Delay(300);

        Assert.Single(pool.Receivers); // whichever way it was found first
        Assert.Single(journal.Lines, l => l.StartsWith("receiver: found", StringComparison.Ordinal));
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task ALocalReceiverThatStops_IsLostAfterThreeFailedReads_AndFoundAgainWhenItReturns()
    {
        var receiver = new FakeReceiver();
        int port = receiver.Port;
        var journal = new MemoryJournal();
        using var pool = Pool(new ObjectCatalogue(TimeProvider.System), journal, new FakeTimeProvider(Start), port);
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Single(pool.Receivers);

        receiver.Dispose();
        for (int i = 0; i < 3; i++)
        {
            await pool.ReadNowAsync(CancellationToken.None);
        }
        Assert.Empty(pool.Receivers);
        Assert.Single(journal.Lines, l => l.StartsWith("receiver: lost this machine", StringComparison.Ordinal));

        using var back = new FakeReceiver(port);
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Single(pool.Receivers);
        Assert.Equal(2, journal.Lines.Count(l => l.StartsWith("receiver: found", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NothingOnThisMachine_AndNoMulticast_IsAQuietNoReceivers()
    {
        var net = new FakeMdnsNetwork { Broken = true };
        var journal = new MemoryJournal();
        using var pool = Pool(new ObjectCatalogue(TimeProvider.System), journal, new FakeTimeProvider(Start), FakeReceiver.FreePort(), net);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => pool.RunAsync(stop.Token));
        await Eventually(() => journal.Lines.Count > 0, "a line");
        await Task.Delay(200);
        stop.Cancel();
        await run;

        var line = Assert.Single(journal.Lines);
        Assert.StartsWith("discovery: cannot look for receivers on the network", line, StringComparison.Ordinal);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        Assert.Empty(pool.Receivers);
    }

    [Fact]
    public async Task AReceiverWithAPassword_IsFollowedAndSaidOnce_NotReadFromAndNotLost()
    {
        using var receiver = new FakeReceiver { Password = true };
        var journal = new MemoryJournal();
        using var pool = Pool(new ObjectCatalogue(TimeProvider.System), journal, new FakeTimeProvider(Start), receiver.Port);
        for (int i = 0; i < 5; i++)
        {
            await pool.ReadNowAsync(CancellationToken.None);
        }

        var seen = Assert.Single(pool.Receivers);
        Assert.Equal("not readable", seen.State);
        Assert.Contains("password", seen.Problem, StringComparison.Ordinal);
        Assert.Single(journal.Lines, l => l.Contains("cannot read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARestartedReceiver_NumberingAgainFromOne_IsReadAgainAfterTheFailureOrTheHalfHourlyResync()
    {
        using var receiver = new FakeReceiver();
        var first = FakeReceiver.Make(5);
        receiver.Add(first, [(0u, true, "GB7RDG"), (1u, true, "GB7RDG")]);
        var time = new FakeTimeProvider(Start);
        var catalogue = new ObjectCatalogue(time);
        using var pool = Pool(catalogue, new MemoryJournal(), time, receiver.Port);
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Equal(2, catalogue.Get(first.ObjectId)!.Pieces.Count);

        // Its state is cleared: the same station is heard again, numbered from 1, with a new object too.
        receiver.Wipe();
        var second = FakeReceiver.Make(6);
        receiver.Add(first, [(0u, true, "GB7RDG"), (1u, true, "GB7RDG"), (2u, true, "GB7RDG")]);
        receiver.Add(second, [(0u, true, "GB7RDG")]);
        await pool.ReadNowAsync(CancellationToken.None);
        Assert.Null(catalogue.Get(second.ObjectId)); // cursors below the one held: nothing looks new until a resync

        time.Advance(ReceiverReader.ResyncEvery + TimeSpan.FromMinutes(1));
        await pool.ReadNowAsync(CancellationToken.None);

        Assert.Equal(3, catalogue.Get(first.ObjectId)!.Pieces.Count);
        Assert.Single(catalogue.Get(second.ObjectId)!.Pieces);
        Assert.Equal(2, catalogue.Totals().Objects);
        Assert.Equal(4, catalogue.Totals().Pieces); // no duplicates of what was held already
    }

    [Fact]
    public async Task AFailedRead_StartsTheNextOneFromTheStartOfTheIndex()
    {
        var receiver = new FakeReceiver();
        int port = receiver.Port;
        var obj = FakeReceiver.Make(7);
        receiver.Add(obj, [(0u, true, "GB7RDG")]);
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        using var pool = Pool(catalogue, new MemoryJournal(), new FakeTimeProvider(Start), port);
        await pool.ReadNowAsync(CancellationToken.None);

        receiver.Dispose();
        await pool.ReadNowAsync(CancellationToken.None); // fails once
        using var back = new FakeReceiver(port);
        var again = FakeReceiver.Make(8);
        back.Add(again, [(0u, true, "GB7RDG")]); // new numbering from 100 again, below nothing the reader saw
        await pool.ReadNowAsync(CancellationToken.None);

        Assert.NotNull(catalogue.Get(again.ObjectId));
    }

    [Fact]
    public async Task RebuiltObjectOctets_ThatDoNotMatchTheirId_AreNotKept()
    {
        using var receiver = new FakeReceiver();
        var obj = FakeReceiver.Make(9);
        var row = receiver.Add(obj, [(0u, true, "GB7RDG")], complete: true);
        row.DataOverride = obj.Bytes.ToArray().Select(b => (byte)(b ^ 1)).ToArray();
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        using var pool = Pool(catalogue, new MemoryJournal(), new FakeTimeProvider(Start), receiver.Port);
        await pool.ReadNowAsync(CancellationToken.None);

        Assert.False(catalogue.Get(obj.ObjectId)!.Complete);
    }

    [Fact]
    public void TheCatalogue_IsBounded_ByBytesAndObjects_DroppingTheLeastRecentlyChanged()
    {
        var time = new FakeTimeProvider(Start);
        var catalogue = new ObjectCatalogue(time, maxBytes: 900, maxObjects: 100);
        var objects = Enumerable.Range(20, 5).Select(s => FakeReceiver.Make(s)).ToList();
        foreach (var o in objects)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(catalogue.AddPiece(o.ObjectId, new RemotePiece(0, o.Frame(0).ToBytes(), Start, true, "X", 1)));
        }
        Assert.True(catalogue.Totals().Bytes <= 900);
        Assert.Null(catalogue.Get(objects[0].ObjectId));
        Assert.NotNull(catalogue.Get(objects[^1].ObjectId));

        var small = new ObjectCatalogue(time, maxObjects: 2);
        foreach (var o in objects)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            small.AddPiece(o.ObjectId, new RemotePiece(0, o.Frame(0).ToBytes(), Start, true, "X", 1));
        }
        Assert.Equal(2, small.Totals().Objects);
    }

    [Fact]
    public void TheCatalogue_RefusesAFrameThatIsNotThisObjects()
    {
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        var a = FakeReceiver.Make(30);
        var b = FakeReceiver.Make(31);
        Assert.False(catalogue.AddPiece(a.ObjectId, new RemotePiece(0, b.Frame(0).ToBytes(), Start, true, null, 1)));
        Assert.False(catalogue.AddPiece(a.ObjectId, new RemotePiece(1, a.Frame(0).ToBytes(), Start, true, null, 1)));
        Assert.False(catalogue.AddPiece(a.ObjectId, new RemotePiece(0, [1, 2, 3], Start, true, null, 1)));
        Assert.Null(catalogue.Get(a.ObjectId));
    }

    [Fact]
    public async Task TheStatusDocument_ListsReceiversAndTheCatalogue()
    {
        using var receiver = new FakeReceiver();
        receiver.Add(FakeReceiver.Make(40), [(0u, true, "GB7RDG")]);
        var catalogue = new ObjectCatalogue(TimeProvider.System);
        using var pool = Pool(catalogue, new MemoryJournal(), new FakeTimeProvider(Start), receiver.Port);
        await pool.ReadNowAsync(CancellationToken.None);
        var status = new Mailcast.HeadEnd.Status.StatusStore(null, TimeProvider.System) { Receivers = pool.StatusView };

        using var doc = System.Text.Json.JsonDocument.Parse(status.Render());
        var section = doc.RootElement.GetProperty("receivers");
        Assert.Equal(1, section.GetProperty("objects").GetInt32());
        Assert.Equal(1, section.GetProperty("piecesHeardOnAir").GetInt32());
        var first = section.GetProperty("receivers")[0];
        Assert.Equal("this machine", first.GetProperty("found").GetString());
        Assert.Equal("ok", first.GetProperty("state").GetString());
    }
}
