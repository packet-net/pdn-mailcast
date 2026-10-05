namespace Mailcast.Core.Tests;

public class MailArchiveTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Feeds the store pieces of <paramref name="bulletin"/> until it is rebuilt; returns its object ID.</summary>
    private static ulong Rebuild(ReceiverStore store, Bulletin bulletin)
    {
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        for (uint esi = 0; !store.IsComplete(obj.ObjectId); esi++)
        {
            Assert.True(esi < 100);
            store.Accept(obj.Frame(esi).ToBytes());
        }
        return obj.ObjectId;
    }

    private static string ArchiveFolder(TempDirectory dir) => Path.Combine(dir.Path, "archive");

    [Fact]
    public void Acknowledge_KeepsACopyWithEachVerdict_AndItSurvivesARestart()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var options = TestStores.Fast with { Time = time };
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.False(Directory.Exists(ArchiveFolder(dir))); // an install from before the archive has none, and that is fine
        var accepted = TestBulletins.Make(1, 1500);
        var alreadyHad = TestBulletins.Make(2, 1500);
        var refused = TestBulletins.Make(3, 1500);
        var ids = new[] { accepted, alreadyHad, refused }.Select(b => Rebuild(store, b)).ToArray();

        store.Acknowledge(accepted.Bid, BbsVerdict.Accepted);
        time.Now += TimeSpan.FromMinutes(1);
        store.Acknowledge(alreadyHad.Bid, BbsVerdict.AlreadyHad);
        time.Now += TimeSpan.FromMinutes(1);
        store.Acknowledge(refused.Bid, BbsVerdict.Refused, "FS R: no such\r\ncategory");

        Assert.Empty(store.Pending());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir.Path, "outbox")));
        foreach (var reader in new[] { store, new ReceiverStore(dir.Path, Compression.Default, options) })
        {
            var mail = reader.Mail();
            Assert.Equal([refused.Bid, alreadyHad.Bid, accepted.Bid], mail.Select(m => m.Bid)); // newest answer first
            Assert.All(mail, m => Assert.False(m.Waiting));
            Assert.Equal([BbsVerdict.Refused, BbsVerdict.AlreadyHad, BbsVerdict.Accepted], mail.Select(m => m.Verdict!.Value));
            Assert.Equal("FS R: no such  category", mail[0].Detail); // one line, whatever the BBS said
            Assert.Equal(Start + TimeSpan.FromMinutes(2), mail[0].Time);
            Assert.Equal(refused.Title, mail[0].Title);
            Assert.Equal(refused.Serialize().Length, mail[0].Size);

            var (entry, serialized) = reader.ReadMail(ids[0])!.Value;
            Assert.Equal(BbsVerdict.Accepted, entry.Verdict);
            Assert.Equal(accepted.Serialize(), serialized);
        }
        Assert.Equal(3, Directory.EnumerateFiles(ArchiveFolder(dir), "*.mail").Count());
    }

    [Fact]
    public void Archive_IsPrunedByAge_OldestFirst_AndTheOutboxIsNeverTouched()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var waiting = TestBulletins.Make(10, 1000);
        Rebuild(store, waiting); // never answered for: it stays in the outbox however old it gets
        for (int i = 0; i < 3; i++)
        {
            var b = TestBulletins.Make(11 + i, 1000);
            Rebuild(store, b);
            store.Acknowledge(b.Bid, BbsVerdict.Accepted);
            time.Now += TimeSpan.FromDays(10); // answered at day 0, 10 and 20
        }

        time.Now = Start + TimeSpan.FromDays(30);
        store.Expire();
        Assert.Equal(3, store.Mail().Count(m => !m.Waiting)); // day 0 is exactly 30 days old: still kept

        time.Now = Start + TimeSpan.FromDays(35);
        Assert.Equal([TestBulletins.Make(13, 1000).Bid, TestBulletins.Make(12, 1000).Bid], store.Mail().Where(m => !m.Waiting).Select(m => m.Bid));
        Assert.Equal(2, Directory.EnumerateFiles(ArchiveFolder(dir)).Count());

        time.Now = Start + TimeSpan.FromDays(400);
        Assert.Equal([waiting.Bid], store.Mail().Select(m => m.Bid));
        Assert.Equal([waiting], store.Pending());
        Assert.Empty(Directory.EnumerateFiles(ArchiveFolder(dir)));
    }

    [Fact]
    public void Archive_IsPrunedBySize_OldestFirst_AndTheOutboxIsNeverTouched()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var bulletins = Enumerable.Range(0, 5).Select(i => TestBulletins.Make(20 + i, 2000)).ToList();
        long fileSize = bulletins.Max(b => b.Serialize().Length) + 200; // the archive header is well under 200 bytes
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, ArchiveMaxBytes = 2 * fileSize });
        var waiting = TestBulletins.Make(29, 30000); // bigger than the cap on its own, and still not touched
        Rebuild(store, waiting);
        foreach (var b in bulletins)
        {
            Rebuild(store, b);
            store.Acknowledge(b.Bid, BbsVerdict.Accepted);
            time.Now += TimeSpan.FromMinutes(1);
        }

        var archived = store.Mail().Where(m => !m.Waiting).Select(m => m.Bid).ToList();
        Assert.Equal([bulletins[4].Bid, bulletins[3].Bid], archived);
        Assert.True(Directory.EnumerateFiles(ArchiveFolder(dir)).Sum(f => new FileInfo(f).Length) <= 2 * fileSize);
        Assert.Equal([waiting], store.Pending());

        // The cap holds across a restart with a smaller one.
        var smaller = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, ArchiveMaxBytes = fileSize });
        Assert.Equal([bulletins[4].Bid], smaller.Mail().Where(m => !m.Waiting).Select(m => m.Bid));
    }

    [Fact]
    public void Resend_MovesItBackToTheOutbox_AndTheNextAnswerArchivesItAgain()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bulletin = TestBulletins.Make(40, 1500);
        ulong id = Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);

        Assert.Equal(ResendOutcome.NotFound, store.Resend(id ^ 1).Outcome);
        var (outcome, resent) = store.Resend(id);
        Assert.Equal(ResendOutcome.Resent, outcome);
        Assert.Equal(bulletin, resent);
        Assert.Equal([bulletin], store.Pending());
        Assert.True(Assert.Single(store.Mail()).Waiting);
        Assert.Empty(Directory.EnumerateFiles(ArchiveFolder(dir)));
        Assert.Equal(ResendOutcome.AlreadyWaiting, store.Resend(id).Outcome);

        time.Now += TimeSpan.FromHours(1);
        store.Acknowledge(bulletin.Bid, BbsVerdict.AlreadyHad);
        var entry = Assert.Single(store.Mail());
        Assert.Equal(BbsVerdict.AlreadyHad, entry.Verdict);
        Assert.Equal(time.Now, entry.Time);
    }

    [Fact]
    public void Resend_OfABulletinWhoseDoneMarkerHasExpired_StillWaitsAfterARestart()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var options = TestStores.Fast with { Time = time };
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        var bulletin = TestBulletins.Make(41, 1500);
        ulong id = Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Refused);
        time.Now += TimeSpan.FromDays(20);
        store.Expire();
        Assert.False(store.IsComplete(id));

        Assert.Equal(ResendOutcome.Resent, store.Resend(id).Outcome);
        Assert.True(store.IsComplete(id)); // later frames of it are not collected all over again
        Assert.Equal([bulletin], new ReceiverStore(dir.Path, Compression.Default, options).Pending());
    }

    [Fact]
    public void BulletinInBothPlaces_AfterACrashMidMove_IsListedOnceAsWaiting()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var bulletin = TestBulletins.Make(42, 1500);
        ulong id = Rebuild(store, bulletin);
        string outboxFile = Path.Combine(dir.Path, "outbox", ObjectId.Format(id) + ".bulletin");
        byte[] saved = File.ReadAllBytes(outboxFile);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        File.WriteAllBytes(outboxFile, saved); // as if the delete never reached the disk

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);

        Assert.True(Assert.Single(reopened.Mail()).Waiting);
        Assert.Equal([bulletin], reopened.Pending());
    }

    [Fact]
    public void UnreadableArchiveFile_IsQuarantined_AndStartUpCarriesOn()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var bulletin = TestBulletins.Make(50, 1500);
        Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        File.WriteAllText(Path.Combine(ArchiveFolder(dir), "0000000000000001.mail"), "Mailcast-Archive: 1\nVerdict: accepted\n\nnot a bulletin");
        File.WriteAllText(Path.Combine(ArchiveFolder(dir), "0000000000000002.mail"), "no header at all");
        File.WriteAllText(Path.Combine(ArchiveFolder(dir), "notes.txt"), "the sysop's own file");

        var log = new List<string>();
        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });

        Assert.Equal([bulletin.Bid], reopened.Mail().Select(m => m.Bid));
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", "0000000000000001.mail")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", "0000000000000002.mail")));
        Assert.True(File.Exists(Path.Combine(ArchiveFolder(dir), "notes.txt")));
        Assert.Equal(2, log.Count(l => l.Contains("quarantined", StringComparison.Ordinal)));
        Assert.Null(reopened.ReadMail(1));
    }

    [Fact]
    public void ArchiveFileSpoiledAfterStartUp_IsQuarantinedWhenRead()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        var bulletin = TestBulletins.Make(51, 1500);
        ulong id = Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        string file = Path.Combine(ArchiveFolder(dir), ObjectId.Format(id) + ".mail");
        File.WriteAllText(file, "garbage");

        Assert.Null(store.ReadMail(id));
        Assert.Equal(ResendOutcome.NotFound, store.Resend(id).Outcome);
        Assert.Empty(store.Mail());
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", ObjectId.Format(id) + ".mail")));
        Assert.Single(log, l => l.Contains("quarantined", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveTurnedOff_AcknowledgeRemovesTheBulletinAsBefore()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { ArchiveRetention = TimeSpan.Zero });
        var bulletin = TestBulletins.Make(60, 1500);
        Rebuild(store, bulletin);

        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);

        Assert.Empty(store.Mail());
        Assert.False(Directory.Exists(ArchiveFolder(dir)));
    }
}
