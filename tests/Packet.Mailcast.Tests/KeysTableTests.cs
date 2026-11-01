using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>The keys table: sources, last heard, adding by hand and forgetting (issue #146).</summary>
public class KeysTableTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);
    private static readonly DirectorySigner Bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());

    private static TransferObject Small(int n) => TransferObject.ForBulletin(TestBulletins.Make(n, 400), ZstdDictionary.Dictionary1Id, Compression.Default);

    private static BroadcastDirectory Directory(DirectorySigner signer, string? station, params TransferObject[] listed) =>
        new(Day, listed.Select(o => new DirectoryEntry(o.ObjectId, o.DictionaryId, 400, $"{o.ObjectId % 100000}_TEST", "Bulletin", Hash: ObjectId.LongHash(o.DictionaryId, o.Bytes))),
            new SlotTimetable(TimeOnly.MinValue, 60), "ms110d-wn4", signer, station: station);

    private static AcceptResult Feed(ReceiverStore store, TransferObject obj)
    {
        AcceptResult result = null!;
        for (uint esi = 0; esi < obj.SourceSymbols + 4; esi++)
        {
            result = store.Accept(obj.Frame(esi).ToBytes());
            if (result.Outcome != FrameOutcome.Stored)
            {
                return result;
            }
        }
        return result;
    }

    private static AcceptResult Feed(ReceiverStore store, BroadcastDirectory d) =>
        Feed(store, TransferObject.ForDirectory(d, ZstdDictionary.Dictionary1Id, Compression.Default));

    private static ReceiverStore Open(TempDirectory dir, FakeTimeProvider time, params TrustedKey[] configured) => new(dir.Path, Compression.Default,
        new ReceiverStoreOptions { FlushToDisk = false, Time = time, TrustedKeys = [.. new[] { StationFixture.Key }, .. configured] });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Rows_SayWhereEachKeyCameFrom_AndWhenItsDirectoryWasLastHeard()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var configured = TrustedKey.Parse("G9CFG", Bpq.PublicKeyBase64);
        var store = Open(dir, time, configured);
        Assert.Equal([KeySource.Config, KeySource.StationsFile], store.KeyRows().Select(r => r.Source)); // by callsign: G9CFG, GB7RDG
        Assert.All(store.KeyRows(), r => Assert.False(r.CanForget));
        Assert.All(store.KeyRows(), r => Assert.Null(r.LastHeard));

        // Learnt on first hearing, with the time of its directory.
        var learnt = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, Directory(learnt, "G4NEW", Small(1))).Outcome);
        var row = Assert.Single(store.KeyRows(), r => r.Source == KeySource.Learnt);
        Assert.Equal("G4NEW", row.Station);
        Assert.Equal(time.GetUtcNow(), row.LastHeard);
        Assert.True(row.CanForget);

        // By callsign, no station first.
        Assert.Equal(["G4NEW", "G9CFG", "GB7RDG"], store.KeyRows().Select(r => r.Station));
    }

    [Fact]
    public void Forget_RefusesStationsFileAndConfigKeys_AndALearntKeyIsLearntAgainOnTheNextHearing()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var configured = TrustedKey.Parse("G9CFG", Bpq.PublicKeyBase64);
        var store = Open(dir, time, configured);
        Assert.NotNull(store.ForgetKey(StationFixture.Key.KeyId).Error);
        Assert.NotNull(store.ForgetKey(configured.KeyId).Error);
        Assert.NotNull(store.ForgetKey(0x1234).Error);

        var learnt = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        Feed(store, Directory(learnt, "G4NEW", Small(1)));
        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, Directory(learnt, "G4NEW", Small(2))).Outcome);
        Assert.Null(store.ForgetKey(learnt.KeyId).Error);
        Assert.DoesNotContain(store.KeyRows(), r => r.Key.KeyId == learnt.KeyId);
        Assert.Null(store.Directory); // the directory it signed is not used any more

        // Not learnt again by a restart: its directories were dropped with it.
        var restarted = Open(dir, time, configured);
        Assert.DoesNotContain(restarted.KeyRows(), r => r.Key.KeyId == learnt.KeyId);

        // But the next time its station's directory is heard, it is learnt again.
        Feed(restarted, Directory(learnt, "G4NEW", Small(3)));
        Assert.Contains(restarted.KeyRows(), r => r.Key.KeyId == learnt.KeyId && r.Source == KeySource.Learnt);
    }

    [Fact]
    public void Add_ValidatesCallsignAndKey_KeepsTheKeyAcrossARestart_AndReleasesWhatWasHeld()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir, time);
        var other = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        Assert.NotNull(store.AddKey("", other.PublicKeyBase64).Error);
        Assert.NotNull(store.AddKey("has space", other.PublicKeyBase64).Error);
        Assert.NotNull(store.AddKey("G4ABC", "not base64!").Error);
        Assert.NotNull(store.AddKey("G4ABC", Convert.ToBase64String(new byte[10])).Error);
        Assert.NotNull(store.AddKey("G4ABC", Convert.ToBase64String(StationFixture.Key.PublicKey)).Error); // already trusted
        Assert.Single(store.KeyRows());

        // A bulletin only a directory from a not yet trusted key lists is held ...
        var a = Small(5);
        Assert.Equal(FrameOutcome.RejectedDirectory, Feed(store, Directory(other, null, a)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, Feed(store, a).Outcome);
        Assert.Empty(store.Pending());

        // ... until the key is added: then it is delivered.
        var change = store.AddKey(" g4abc ", other.PublicKeyBase64);
        Assert.Null(change.Error);
        Assert.Single(change.Released);
        var row = Assert.Single(store.KeyRows(), r => r.Source == KeySource.Added);
        Assert.Equal("G4ABC", row.Station);
        Assert.Single(store.Pending());

        var restarted = Open(dir, time);
        Assert.Equal(KeySource.Added, Assert.Single(restarted.KeyRows(), r => r.Key.KeyId == other.KeyId).Source);
        Assert.Null(restarted.ForgetKey(other.KeyId).Error);
        Assert.DoesNotContain(Open(dir, time).KeyRows(), r => r.Key.KeyId == other.KeyId);
    }

    [Fact]
    public void Forget_UndoesWhatItsDirectoriesGave_AndTheSameDirectoryIsLearntAgain()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir, time);
        var learnt = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var advert = new StationAdvert("G8BPQ", new SlotTimetable(new TimeOnly(0, 30), 60), 7_053_800, Sideband.Usb);
        var a = Small(1);
        var withAdvert = new BroadcastDirectory(Day, [new DirectoryEntry(a.ObjectId, a.DictionaryId, 400, "1_TEST", "Bulletin", Hash: ObjectId.LongHash(a.DictionaryId, a.Bytes))],
            new SlotTimetable(TimeOnly.MinValue, 60), "ms110d-wn4", learnt, station: "G4NEW", adverts: [advert]);
        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, withAdvert).Outcome);
        Assert.NotEmpty(store.Adverts());
        Assert.NotNull(store.Directory);

        Assert.Null(store.ForgetKey(learnt.KeyId).Error);
        Assert.Empty(store.Adverts());
        Assert.Null(store.Directory);

        // The very same directory, heard again, is rebuilt, checked and learnt again.
        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, withAdvert).Outcome);
        Assert.Contains(store.KeyRows(), r => r.Key.KeyId == learnt.KeyId && r.Source == KeySource.Learnt);
        Assert.NotEmpty(store.Adverts());
    }

    [Fact]
    public void Add_ForACallsignThatHasAKey_SaysBothAreTrusted_AndClearsItsConflict()
    {
        using var dir = new TempDirectory();
        var store = Open(dir, Clock());
        var second = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        // A directory naming GB7RDG under another key is a conflict.
        Feed(store, Directory(second, "GB7RDG", Small(1)));
        Assert.NotEmpty(store.KeyConflicts);
        var change = store.AddKey("GB7RDG", second.PublicKeyBase64);
        Assert.Null(change.Error);
        Assert.Equal($"GB7RDG already has key {KeyId.Format(StationFixture.Key.KeyId)}; both are now trusted.", change.Note);
        Assert.Empty(store.KeyConflicts);
    }

    [Fact]
    public void ScheduleFile_NotSetByASignedDirectory_IsDroppedAtStart()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "schedule.txt"), "2026-10-29\n00:00/60\n");
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, new ReceiverStoreOptions { FlushToDisk = false, Time = Clock(), Log = log.Add });
        Assert.Empty(store.HeardSchedules);
        Assert.False(File.Exists(Path.Combine(dir.Path, "schedule.txt")));
        Assert.Contains(log, l => l.Contains("schedule.txt was not set by a signed directory", StringComparison.Ordinal));
    }
}
