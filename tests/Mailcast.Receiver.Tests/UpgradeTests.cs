using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The stations file as shipped, and a receiver upgraded from a version that had one station in its
/// code (0.29.1): it keeps working from its own state, with no change to its config.
/// </summary>
public class UpgradeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheShippedFile_HasThePackagesStation_WithTheKeyThatWasBuiltIn()
    {
        string path = Path.Combine(AppContext.BaseDirectory, StationsFile.FileName);
        Assert.True(File.Exists(path), path);

        var file = StationsFile.Load(path);

        Assert.Equal("MCAST@GB7RDG.#42.GBR.EURO", file.Report);
        var station = Assert.Single(file.Stations);
        Assert.Equal("M0LTE", station.Callsign);
        Assert.Equal("6f39d04c925c01cf", KeyId.Format(station.Key.KeyId));
        Assert.Equal("e1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=", Convert.ToBase64String(station.Key.PublicKey));
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91lk", 120, 30)), station.Slots);
        Assert.Equal((7_053_800L, Sideband.Usb, "IO91lk"), (station.FrequencyHz, station.Sideband, station.Locator));
        Assert.Equal(["GB7RDG"], station.Formerly);
        Assert.Equal("M0LTE", file.Legacy);
        Assert.Equal("M0LTE", file.Current("GB7RDG"));
    }

    [Fact]
    public void WithTheShippedFile_AConfigNeedsNoDial_ToPutTheSignalAt1800Hz()
    {
        var shipped = StationsFile.Load(Path.Combine(AppContext.BaseDirectory, StationsFile.FileName));

        var config = new ReceiverConfig { Audio = Samples.SoundCard, Stations = shipped };
        config.Validate();

        Assert.Equal(7052.0, config.ResolvedDialKHz);
        Assert.Equal(1800, config.AudioCentreHz);
        Assert.Equal(["M0LTE"], config.AllTrustedKeys.Select(k => k.Label));
    }

    /// <summary>The stations file of the package's own station with a throwaway key standing in for the real one.</summary>
    private static StationsFile Shipped(DirectorySigner key) => StationsFile.Parse($$"""
        {
          "report": "MCAST@GB7RDG.#42.GBR.EURO",
          "stations": [ { "callsign": "M0LTE", "publicKey": "{{key.PublicKeyBase64}}",
            "slots": { "minute": 0, "everyMinutes": 60 }, "frequencyHz": 7053800, "sideband": "usb", "locator": "IO91lk", "formerly": ["GB7RDG"] } ]
        }
        """, "stations.json");

    /// <summary>A slot's frames as a head end sends them as M0LTE, which is what has been on the air since 2026-10-11.</summary>
    private static IEnumerable<byte[]> FramesFromM0lte(DirectorySigner signer, DateTimeOffset slot, Bulletin bulletin) =>
        BroadcastScheduler.Plan([new BroadcastBulletin(bulletin, DateOnly.FromDateTime(slot.UtcDateTime), slot)], slot, 1, Compression.Default,
                ScheduleOptions.Hourly with { Signer = signer, Station = "M0LTE" })
            .Frames.Select(f => Ax25UiFrame.Build("M0LTE", OnAir.Destination, f.ToBytes()));

    private static string Iso(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AReceiverUpgradedFromTheBuiltInVersion_TrustsFollowsKeepsAndDeliversAsM0lte()
    {
        var key = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var extra = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();

        // The state directory as 0.29.1 wrote it: everything under GB7RDG, the one station it knew.
        string store = Path.Combine(dir.Path, "store");
        Directory.CreateDirectory(store);
        // Its heard timetable, a signed one: at half past, not the file's on the hour.
        File.WriteAllText(Path.Combine(store, "schedule.txt"), "2026-10-29\n" + string.Join('\t', new SlotTimetable(new TimeOnly(0, 30), 60).ToFields()) + "\nsigned\n");
        // A second key for it that the owner added by hand on the page.
        File.WriteAllText(Path.Combine(store, "pins.txt"), $"GB7RDG\t{extra.PublicKeyBase64}\t{Iso(Now.AddDays(-9))}\tadded\n");
        // Yesterday's slots: the first kept with no station (always the one station's), the second by name; and its receipt.
        File.WriteAllText(Path.Combine(dir.Path, "feedback.json"), $$"""
            {
              "days": [ { "day": "2026-10-30", "slots": [
                  { "slot": "{{Iso(Now.AddHours(-3))}}", "completed": 2, "frames": 150, "snrDb": 12 },
                  { "slot": "{{Iso(Now.AddHours(-2))}}", "station": "GB7RDG", "completed": 0, "frames": 40 } ],
                "rebuilt": 3, "delivered": 2 } ],
              "receiverId": "AB",
              "receipt": { "callsign": "G4ABC", "got": "2026-10-29", "gotSeen": "{{Iso(Now.AddHours(-20))}}", "listedOn": "2026-10-30" }
            }
            """);
        // Its last channel measurement, with no station named.
        File.WriteAllText(Path.Combine(dir.Path, "channel.json"), $$"""
            { "slots": [ { "slot": "{{Iso(Now.AddHours(-2))}}", "measured": "{{Iso(Now.AddHours(-2))}}", "basis": "bursts", "measurements": 2, "kept": 2, "enough": false, "words": "Not enough decoded to measure." } ] }
            """);

        // The upgrade: the new version, the shipped stations file, and the config as it was.
        var config = new ReceiverConfig
        {
            Audio = "ubersdr:wessex.zapto.org",
            StateDirectory = dir.Path,
            Stations = Shipped(key),
            Feedback = new Feedback.FeedbackSettings { Enabled = true, Callsign = "g4abc" },
        };
        var log = new List<string>();
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), line => { lock (log) { log.Add(line); } });

        // It follows M0LTE at the timetable it heard, not the file's.
        var followed = Assert.Single(host.Followed.Stations);
        Assert.Equal("M0LTE", followed.Station);
        Assert.Equal(new SlotSchedule(new TimeOnly(0, 30), 60), followed.Schedule);
        Assert.Equal("M0LTE", host.Listening.Single().Station);
        Assert.Contains(log, l => l.EndsWith("schedule.txt is read as M0LTE's timetable", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(store, "schedule.txt")));

        // Both its keys are M0LTE's now, on disk too.
        Assert.Equal(2, host.Intake.Pins().Count(p => p.Station == "M0LTE"));
        Assert.DoesNotContain(host.Intake.Pins(), p => p.Station == "GB7RDG");
        string pins = File.ReadAllText(Path.Combine(store, "pins.txt"));
        Assert.StartsWith("M0LTE\t", pins, StringComparison.Ordinal);
        Assert.DoesNotContain("GB7RDG", pins, StringComparison.Ordinal);

        // Its counts are M0LTE's, both the slot kept with no name and the one kept by one.
        var figure = Assert.Single(host.Feedback.StationFigures(new DateOnly(2026, 10, 30)));
        Assert.Equal(("M0LTE", 2, 190, 2), (figure.Station, figure.Listened, figure.Frames, figure.Completed));
        var view = JsonSerializer.SerializeToElement(host.Feedback.View(), ReceiverConfig.JsonLine);
        var receipt = Assert.Single(view.GetProperty("receipts").EnumerateArray());
        Assert.Equal("M0LTE got your report for 2026-10-29", receipt.GetProperty("text").GetString());
        Assert.Equal("MCAST@GB7RDG.#42.GBR.EURO", view.GetProperty("to").GetString());
        var measured = Assert.Single(host.ChannelWatch.History);
        Assert.Equal(("M0LTE", "IO91lk"), (measured.Station, measured.StationLocator));

        // And its signed directories are trusted at once, so its bulletin is delivered as before.
        var slot = new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero);
        foreach (byte[] frame in FramesFromM0lte(key, slot, Samples.Bulletin(1)))
        {
            Assert.True(host.Intake.Offer(frame));
        }
        await host.Intake.DrainAsync(CancellationToken.None);
        Assert.Equal(SignatureState.Trusted, host.Intake.DirectoryChecks().Newest!.State);
        Assert.Equal("signed by M0LTE", host.Intake.DirectoryChecks().Newest!.Describe());
        Assert.Equal("1_GB7RDG", Assert.Single(host.Intake.Pending()).Bid);
    }

    [Fact]
    public async Task ASkipKeptUnderTheOldName_StaysTheNoOpItWas_AndIsSaidToBeIgnored()
    {
        var key = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();

        await using var host = new ReceiverHost(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = Shipped(key), SkipStations = ["GB7RDG"] },
            new FakeTimeProvider(Now), _ => { });

        var m0lte = Assert.Single(host.Listening);
        Assert.True(m0lte.Followed);
        Assert.Equal([("GB7RDG", "M0LTE")], host.Config.IgnoredSkips);

        // Skipping M0LTE by its name works, as for any station.
        await using var skipped = new ReceiverHost(host.Config with { SkipStations = ["M0LTE"] }, new FakeTimeProvider(Now), _ => { });
        Assert.Empty(skipped.Followed.Stations);
        Assert.Contains(skipped.SlotLines(), l => l.StartsWith("slots: M0LTE is not listened at: you chose not to listen at its slots (its slots are every hour on the hour", StringComparison.Ordinal) && l.EndsWith(", from the stations file)", StringComparison.Ordinal));

        // And a listenFor of the old name is refused, naming the new.
        var entry = new ListenForEntry { Callsign = "GB7RDG", FrequencyHz = 7_047_000 };
        Assert.Equal("\"callsign\" GB7RDG is now M0LTE, already known", entry.Problem(host.Config.Stations));
        var refused = Assert.Throws<ConfigException>(() => (host.Config with { ListenFor = [entry] }).Validate());
        Assert.Contains("GB7RDG is now M0LTE, already known", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoStationsFileAnywhere_TheStartUpSaysWhereItLookedAndWhatThatMeans()
    {
        using var dir = new TempDirectory();
        var none = StationsFile.Locate(Path.Combine(dir.Path, "receiver.json"), Path.Combine(dir.Path, "missing.json"));
        await using var host = new ReceiverHost(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = none }, new FakeTimeProvider(Now), _ => { });

        string line = Assert.Single(host.SlotLines());
        Assert.Equal($"stations: no stations file found (looked in {Path.Combine(dir.Path, "stations.json")}, {Path.Combine(dir.Path, "missing.json")}); nothing is trusted or followed until a key, an advert or a listenFor entry is added", line);
    }

    [Fact]
    public async Task APinKeptUnderTheOldName_IsRewrittenOnce_AndAnOperatorWhoEmptiedTheFileFollowsNoStation()
    {
        var key = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        string store = Path.Combine(dir.Path, "store");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "schedule.txt"), "2026-10-29\n" + string.Join('\t', new SlotTimetable(new TimeOnly(0, 30), 60).ToFields()) + "\nsigned\n");

        // An operator's own, empty stations file: nothing is trusted or followed, and the old timetable is left alone, unused.
        await using (var host = new ReceiverHost(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = StationsFile.Empty },
            new FakeTimeProvider(Now), _ => { }))
        {
            Assert.Empty(host.Followed.Stations);
            Assert.Empty(host.Listening);
            Assert.Empty(host.Intake.Pins());
            Assert.Empty(host.Intake.HeardSchedules);
            Assert.True(File.Exists(Path.Combine(store, "schedule.txt")));
            Assert.Empty(host.SlotLines());
        }

        // The shipped file back again: the old timetable is the station's.
        await using var back = new ReceiverHost(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = Shipped(key) }, new FakeTimeProvider(Now), _ => { });
        Assert.Equal(new SlotSchedule(new TimeOnly(0, 30), 60), Assert.Single(back.Followed.Stations).Schedule);
    }
}
