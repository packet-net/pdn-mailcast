using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.HeadEnd.Tests;

/// <summary>The signed station list at the head end (issue #178): signing it offline, carrying it, and sending it.</summary>
public class StationListTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    // Throwaway keys: never a real list key.
    private static readonly string ListKeyPem = DirectorySigner.NewKeyPem();
    private static readonly DirectorySigner ListSigner = DirectorySigner.FromPem(ListKeyPem);
    private static readonly TrustedKey ListKey = TrustedKey.Parse("LIST", ListSigner.PublicKeyBase64);
    private static readonly string BpqKey = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()).PublicKeyBase64;

    private static readonly HeadEndConfig Config = HeadEndConfig.Parse("""
        {
          "callsign": "M0LTE",
          "station": { "apiKey": "k", "mode": "ms110d-wn4", "maxBurstSeconds": 18 },
          "slot": { "timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10, "toneSeconds": 10, "channelWaitSeconds": 120 },
          "schedule": { "symbolSize": 240 }
        }
        """);

    private static string Json(long serial = 2026102501) => $$"""
        {
          // The list Tom signs.
          "serial": {{serial}},
          "stations": [
            { "callsign": "GB7RDG", "locator": "IO91lk", "frequencyHz": 7053800, "sideband": "usb",
              "slot": { "timeUtc": "00:00", "everyMinutes": 60, "daylight": { "locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 } },
              "modes": ["ms110d-wn3", "ms110d-wn4"], "publicKey": "MCowBQYDK2VwAyEAe1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=" },
            { "callsign": "G8BPQ", "locator": "IO93fb", "frequencyHz": 7047000, "sideband": "usb",
              "slot": { "timeUtc": "00:30", "everyMinutes": 60 },
              "modes": ["ms110d-wn4"], "publicKey": "{{BpqKey}}" },
          ],
        }
        """;

    [Fact]
    public void SignList_FromJson_GivesAListReceiversCheck()
    {
        var list = StationListFile.Sign(Json(), ListSigner);
        Assert.Equal(2026102501, list.Serial);
        Assert.Equal(["GB7RDG", "G8BPQ"], list.Stations.Select(s => s.Callsign));
        Assert.Equal(TrustedKey.Gb7rdg.KeyId, list.Stations[0].Key.KeyId);
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg), list.Stations[0].Timetable);
        Assert.Equal(new SlotTimetable(new TimeOnly(0, 30), 60), list.Stations[1].Timetable);
        Assert.Equal(SignatureState.Trusted, StationList.Parse(list.Serialize()).Check([ListKey]).State);
    }

    [Theory]
    [InlineData("\"serial\": 2026102501", "\"serial\": 0", "serial")]
    [InlineData("\"sideband\": \"usb\",\n      \"slot\": { \"timeUtc\": \"00:30\"", "\"sideband\": \"am\",\n      \"slot\": { \"timeUtc\": \"00:30\"", "sideband")]
    [InlineData("\"timeUtc\": \"00:30\"", "\"timeUtc\": \"half past\"", "timeUtc")]
    [InlineData("\"locator\": \"IO93fb\"", "\"locator\": \"nowhere\"", "locator")]
    [InlineData("\"modes\": [\"ms110d-wn4\"]", "\"modes\": []", "mode")]
    [InlineData("\"frequencyHz\": 7047000", "\"frequency\": 7047000", "frequency")]
    public void SignList_SaysWhatIsWrong(string from, string to, string mentions)
    {
        string json = Json().Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Json(), json);
        var e = Assert.Throws<FormatException>(() => StationListFile.Sign(json, ListSigner));
        Assert.Contains(mentions, e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_SendsOnlyAListThatChecks_AndSaysWhy()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "station-list.txt");
        Assert.Equal($"station list: none at {path}, so none is sent", StationListFile.Load(path, [ListKey]).Line);

        File.WriteAllBytes(path, StationListFile.Sign(Json(), ListSigner).Serialize());
        var (list, line) = StationListFile.Load(path, [ListKey]);
        Assert.NotNull(list);
        Assert.Equal($"station list: sends {path}, serial 2026102501, 2 stations: GB7RDG, G8BPQ, signed by the station list key", line);

        var (none, why) = StationListFile.Load(path, StationListKey.BuiltIn);
        Assert.Null(none);
        Assert.EndsWith("is not sent: no station list key is built in yet", why, StringComparison.Ordinal);

        File.WriteAllText(path, "rubbish\n");
        Assert.Null(StationListFile.Load(path, [ListKey]).List);
        Assert.All(StationListFile.Load(path, [ListKey]).Line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Planner_CarriesTwoFreshPieces_InASlotThatKeys_AndNoneInOneThatDoesNot()
    {
        using var state = new TempDirectory();
        var list = StationListFile.Sign(Json(), ListSigner);
        var options = Config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        var planner = new StoreSlotPlanner(store, Compression.Default, options, Config.ToWaveforms(LinearAirtime.Measure), Config.ToSlotSettings(), Config.FillLimitAfter, new FakeTimeProvider(Slot.AddSeconds(30)), stationList: list);
        Assert.Empty(planner.Plan(Slot).Frames);

        for (int i = 0; i < 10; i++)
        {
            store.Offer(Bulletins.Make(700 + i, 6000), new DateOnly(2026, 10, 25));
        }
        var plan = planner.Plan(Slot);
        Assert.Equal(2, plan.StationListFrames);
        ulong id = TransferObject.ForStationList(list).ObjectId;
        var pieces = plan.Frames.Where(f => f.ObjectId == id).ToList();
        Assert.Equal([0u, 1u], pieces.Select(f => f.Esi));
        planner.RecordQueued(plan, plan.Frames.Count);

        // The next slot carries on with fresh pieces, never repeating one.
        var next = planner.Plan(Slot.AddHours(1));
        Assert.All(next.Frames.Where(f => f.ObjectId == id), f => Assert.True(f.Esi >= 2));

        // And the pieces rebuild it at a receiver that trusts the list key.
        using var rx = new TempDirectory();
        var receiver = new ReceiverStore(rx.Path, Compression.Default, new ReceiverStoreOptions { ListKeys = [ListKey] });
        Assert.Equal(FrameOutcome.CompletedStationList, receiver.Accept(pieces[1].Payload).Outcome);
        Assert.Equal(list.Serialize(), receiver.Stations!.Serialize());
    }

    [Fact]
    public void Arguments_SignListNeedsAKey_AndKeyAndOutNeedSignList()
    {
        var ok = Program.ParseArguments(["--sign-list", "l.json", "--key", "k.pem", "--out", "o.txt"], out string? error);
        Assert.Null(error);
        Assert.Equal("l.json", ok["--sign-list"]);
        Program.ParseArguments(["--sign-list", "l.json"], out error);
        Assert.Equal("--sign-list needs --key, the station list key", error);
        Program.ParseArguments(["--key", "k.pem"], out error);
        Assert.Equal("--key and --out go with --sign-list", error);
    }

    [Fact]
    public async Task SignList_Command_WritesTheSignedList()
    {
        using var dir = new TempDirectory();
        string json = Path.Combine(dir.Path, "list.json"), key = Path.Combine(dir.Path, "list-key.pem"), output = Path.Combine(dir.Path, "station-list.txt");
        File.WriteAllText(json, Json());
        File.WriteAllText(key, ListKeyPem);
        Assert.Equal(0, await Program.MainAsync(["--sign-list", json, "--key", key, "--out", output]));
        var list = StationList.Parse(File.ReadAllBytes(output));
        Assert.Equal(SignatureState.Trusted, list.Check([ListKey]).State);
        Assert.Equal(2, await Program.MainAsync(["--sign-list", json, "--key", Path.Combine(dir.Path, "missing.pem"), "--out", output]));
    }
}
