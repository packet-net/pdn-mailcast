namespace Packet.Mailcast.Tests;

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
            var mail = reader.Mail.NewestFirst;
            Assert.Equal([refused.Bid, alreadyHad.Bid, accepted.Bid], mail.Select(m => m.Bid)); // newest answer first
            Assert.All(mail, m => Assert.False(m.Waiting));
            Assert.Equal([BbsVerdict.Refused, BbsVerdict.AlreadyHad, BbsVerdict.Accepted], mail.Select(m => m.Verdict!.Value));
            Assert.Equal("FS R: no such  category", mail[0].Detail); // one line, whatever the BBS said
            Assert.Equal(Start + TimeSpan.FromMinutes(2), mail[0].Time); // answered
            Assert.All(mail, m => Assert.Equal(Start, m.Completed)); // all three rebuilt before any answer
            Assert.Equal(refused.Title, mail[0].Title);
            Assert.Equal(refused.Serialize().Length, mail[0].Size);

            var (entry, serialized) = reader.ReadMail(ids[0])!.Value;
            Assert.Equal(BbsVerdict.Accepted, entry.Verdict);
            Assert.Equal(accepted.Serialize(), serialized);
        }
        Assert.Equal(3, Directory.EnumerateFiles(ArchiveFolder(dir), "*.mail").Count());
    }

    /// <summary>
    /// A file from before issue #47, with no Completed line at all. It must load without
    /// throwing, both at start-up (headers only) and when the whole file is read, and give
    /// Completed as null (unknown) rather than guessing at some other time.
    /// </summary>
    [Fact]
    public void OldArchiveFile_WithoutACompletedLine_LoadsWithCompletedUnknown()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bulletin = TestBulletins.Make(90, 1500);
        ulong id = Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        string file = Path.Combine(ArchiveFolder(dir), ObjectId.Format(id) + ".mail");
        string text = File.ReadAllText(file, System.Text.Encoding.Latin1);
        Assert.Contains("Completed: ", text, StringComparison.Ordinal); // the current format has one
        text = System.Text.RegularExpressions.Regex.Replace(text, "Completed: [^\n]*\n", "");
        File.WriteAllText(file, text, System.Text.Encoding.Latin1); // as a pre-#47 receiver would have written it

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });

        var fromHeader = Assert.Single(reopened.Mail.NewestFirst);
        Assert.Null(fromHeader.Completed);
        Assert.Equal(BbsVerdict.Accepted, fromHeader.Verdict);
        Assert.Equal(Start, fromHeader.Time);
        var (whole, serialized) = reopened.ReadMail(id)!.Value;
        Assert.Null(whole.Completed); // reading the file whole agrees with the header-only read
        Assert.Equal(bulletin.Serialize(), serialized);
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
        Assert.Equal(3, store.Mail.NewestFirst.Count(m => !m.Waiting)); // day 0 is exactly 30 days old: still kept

        time.Now = Start + TimeSpan.FromDays(35);
        Assert.Equal(3, store.Mail.Archived); // listing never prunes
        store.Expire();
        Assert.Equal([TestBulletins.Make(13, 1000).Bid, TestBulletins.Make(12, 1000).Bid], store.Mail.NewestFirst.Where(m => !m.Waiting).Select(m => m.Bid));
        Assert.Equal(2, Directory.EnumerateFiles(ArchiveFolder(dir)).Count());

        time.Now = Start + TimeSpan.FromDays(400);
        store.Expire();
        Assert.Equal([waiting.Bid], store.Mail.NewestFirst.Select(m => m.Bid));
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

        var archived = store.Mail.NewestFirst.Where(m => !m.Waiting).Select(m => m.Bid).ToList();
        Assert.Equal([bulletins[4].Bid, bulletins[3].Bid], archived);
        Assert.True(Directory.EnumerateFiles(ArchiveFolder(dir)).Sum(f => new FileInfo(f).Length) <= 2 * fileSize);
        Assert.Equal([waiting], store.Pending());

        // The cap holds across a restart with a smaller one.
        var smaller = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, ArchiveMaxBytes = fileSize });
        Assert.Equal([bulletins[4].Bid], smaller.Mail.NewestFirst.Where(m => !m.Waiting).Select(m => m.Bid));
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
        Assert.True(Assert.Single(store.Mail.NewestFirst).Waiting); // listed once, as waiting
        Assert.Single(Directory.EnumerateFiles(ArchiveFolder(dir))); // the archive keeps its copy meanwhile
        Assert.Equal(ResendOutcome.AlreadyWaiting, store.Resend(id).Outcome);

        // The BBS still has it: it stays down as accepted, which says more.
        time.Now += TimeSpan.FromHours(1);
        store.Acknowledge(bulletin.Bid, BbsVerdict.AlreadyHad);
        var entry = Assert.Single(store.Mail.NewestFirst);
        Assert.Equal(BbsVerdict.Accepted, entry.Verdict);
        Assert.Equal("offered again; the BBS already had it", entry.Detail);
        Assert.Equal(time.Now, entry.Time);

        // Refused after that is recorded as it is.
        Assert.Equal(ResendOutcome.Resent, store.Resend(id).Outcome);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Refused, "FS R");
        Assert.Equal(BbsVerdict.Refused, Assert.Single(store.Mail.NewestFirst).Verdict);
        Assert.Equal(BbsVerdict.Refused, Assert.Single(new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast).Mail.NewestFirst).Verdict);
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

        Assert.True(Assert.Single(reopened.Mail.NewestFirst).Waiting);
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

        Assert.Equal([bulletin.Bid], reopened.Mail.NewestFirst.Select(m => m.Bid));
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
        Assert.Empty(store.Mail.NewestFirst);
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

        Assert.Empty(store.Mail.NewestFirst);
        Assert.False(Directory.Exists(ArchiveFolder(dir)));
    }

    [Theory]
    [InlineData(BbsVerdict.Accepted)]
    [InlineData(BbsVerdict.AlreadyHad)]
    public void ArchiveThatCannotBeWritten_ForMailTheBbsHas_StillEmptiesTheOutbox(BbsVerdict verdict)
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        var bulletin = TestBulletins.Make(70, 1500);
        Rebuild(store, bulletin);
        File.WriteAllText(ArchiveFolder(dir), "a file where the archive folder should be"); // as good as a full disk

        store.Acknowledge(bulletin.Bid, verdict);

        Assert.Empty(store.Pending());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir.Path, "outbox")));
        Assert.Empty(store.Mail.NewestFirst);
        Assert.Single(log, l => l.Contains("cannot keep a copy of " + bulletin.Bid, StringComparison.Ordinal));
        Assert.Empty(new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast).Pending());
    }

    [Fact]
    public void ArchiveThatCannotBeWritten_ForRefusedMail_MovesItToQuarantine_SoNewerMailIsNotHeldUp()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        var refused = TestBulletins.Make(71, 1500);
        var newer = TestBulletins.Make(72, 1500);
        ulong id = Rebuild(store, refused);
        Rebuild(store, newer);
        File.WriteAllText(ArchiveFolder(dir), "a file where the archive folder should be");

        store.Acknowledge(refused.Bid, BbsVerdict.Refused);

        Assert.Equal([newer], store.Pending());
        string kept = Path.Combine(dir.Path, "quarantine", ObjectId.Format(id) + ".bulletin");
        Assert.Equal(refused, Bulletin.Parse(File.ReadAllBytes(kept))); // the BBS does not have it, so it is kept
        Assert.Single(log, l => l.Contains("refused " + refused.Bid, StringComparison.Ordinal) && l.Contains("quarantine/", StringComparison.Ordinal));
        Assert.Equal([newer], new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast).Pending());
    }

    [Fact]
    public void Prune_PastAFileThatWillNotGo_CarriesOn()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, Log = log.Add });
        var ids = new List<ulong>();
        for (int i = 0; i < 4; i++)
        {
            var b = TestBulletins.Make(100 + i, 800);
            ids.Add(Rebuild(store, b));
            store.Acknowledge(b.Bid, BbsVerdict.Accepted);
            time.Now += TimeSpan.FromDays(1);
        }
        // The oldest two cannot be removed: a folder stands where each file was.
        foreach (ulong id in ids.Take(2))
        {
            string file = Path.Combine(ArchiveFolder(dir), ObjectId.Format(id) + ".mail");
            File.Delete(file);
            Directory.CreateDirectory(Path.Combine(file, "in the way"));
        }

        time.Now = Start + TimeSpan.FromDays(32.5); // past 30 days for the first three
        store.Expire();

        Assert.Equal([ids[3]], store.Mail.NewestFirst.Select(m => m.ObjectId));
        Assert.False(File.Exists(Path.Combine(ArchiveFolder(dir), ObjectId.Format(ids[2]) + ".mail")));
        Assert.Single(log, l => l.Contains("2 old files could not be removed", StringComparison.Ordinal));
    }

    [Fact]
    public void Resend_IsCappedAtFiftyWaiting()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var ids = new List<ulong>();
        for (int i = 0; i <= ReceiverStore.MaxResentWaiting; i++)
        {
            var b = TestBulletins.Make(200 + i, 300);
            ids.Add(Rebuild(store, b));
            store.Acknowledge(b.Bid, BbsVerdict.Accepted);
        }

        Assert.All(ids.Take(ReceiverStore.MaxResentWaiting), id => Assert.Equal(ResendOutcome.Resent, store.Resend(id).Outcome));
        Assert.Equal(ResendOutcome.TooMany, store.Resend(ids[^1]).Outcome);
        Assert.Equal(ReceiverStore.MaxResentWaiting, store.Pending().Count);

        store.Acknowledge(store.Pending()[0].Bid, BbsVerdict.AlreadyHad); // the BBS answers for one
        Assert.Equal(ResendOutcome.Resent, store.Resend(ids[^1]).Outcome);
    }

    [Fact]
    public void ForgetUnreadable_ReadsTheFileAgain_AndLeavesACopyThatNowReads()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var bulletin = TestBulletins.Make(73, 1500);
        ulong id = Rebuild(store, bulletin);
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        string file = Path.Combine(ArchiveFolder(dir), ObjectId.Format(id) + ".mail");
        byte[] good = File.ReadAllBytes(file);
        File.WriteAllBytes(file, good[..10]); // caught half rewritten by a read on another thread

        Assert.Null(store.ReadMail(id, out bool unreadable));
        Assert.True(unreadable);
        File.WriteAllBytes(file, good); // the rewrite finishes
        store.ForgetUnreadable(id);

        Assert.True(File.Exists(file));
        Assert.Equal(bulletin.Serialize(), store.ReadMail(id)!.Value.Serialized);
        Assert.Single(store.Mail.NewestFirst);
    }

    [Fact]
    public void StartUp_ReadsOnlyTheHeaders_AndABadBodyIsFoundWhenRead()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var good = TestBulletins.Make(80, 20000);
        var spoiled = TestBulletins.Make(81, 1500);
        ulong goodId = Rebuild(store, good);
        ulong spoiledId = Rebuild(store, spoiled);
        store.Acknowledge(good.Bid, BbsVerdict.Accepted, "fine");
        store.Acknowledge(spoiled.Bid, BbsVerdict.Refused);
        var before = store.Mail.NewestFirst.ToDictionary(m => m.ObjectId);
        // One that only a whole read finds wrong: a sender with a space in it, which the header
        // lines carry well enough but no bulletin may have.
        string spoiledFile = Path.Combine(ArchiveFolder(dir), ObjectId.Format(spoiledId) + ".mail");
        string text = File.ReadAllText(spoiledFile, System.Text.Encoding.Latin1);
        File.WriteAllText(spoiledFile, text.Replace("From: G4ABC\n", "From: G4 ABC\n", StringComparison.Ordinal), System.Text.Encoding.Latin1);
        // One whose bulletin header never ends within what start-up reads.
        string endless = Path.Combine(ArchiveFolder(dir), "00000000000000ee.mail");
        File.WriteAllText(endless, "Mailcast-Archive: 1\nAnswered: 2026-10-04T12:00:00Z\nVerdict: accepted\n\nTitle: " + new string('x', MailArchive.MaxHeaderBytes) + "\n\n");

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, Log = log.Add });

        var after = reopened.Mail.NewestFirst.ToDictionary(m => m.ObjectId);
        Assert.Equal([goodId, spoiledId], after.Keys.Order());
        Assert.Equal(before[goodId], after[goodId]); // the same entry, size and all, from its headers alone
        Assert.Equal(before[spoiledId] with { From = "G4 ABC", Size = before[spoiledId].Size + 1 }, after[spoiledId]);
        Assert.Single(log, l => l.Contains("00000000000000ee.mail unreadable, quarantined", StringComparison.Ordinal));

        Assert.Equal(good.Serialize(), reopened.ReadMail(goodId)!.Value.Serialized);
        Assert.Null(reopened.ReadMail(spoiledId)); // read whole only now, and quarantined
        Assert.Equal([goodId], reopened.Mail.NewestFirst.Select(m => m.ObjectId));
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", ObjectId.Format(spoiledId) + ".mail")));
    }

    [Fact]
    public void Snapshot_PagesNewestFirst_WithoutCopying()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(Start);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bids = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var b = TestBulletins.Make(90 + i, 800);
            Rebuild(store, b);
            if (i % 2 == 0)
            {
                store.Acknowledge(b.Bid, BbsVerdict.Accepted);
            }
            bids.Insert(0, b.Bid);
            time.Now += TimeSpan.FromMinutes(1);
        }

        var mail = store.Mail;
        Assert.Equal(bids, mail.NewestFirst.Select(m => m.Bid));
        Assert.Equal((6, 3, 3), (mail.Count, mail.Waiting, mail.Archived));
        Assert.Equal(bids[2..4], mail.Page(2, 2).Select(m => m.Bid));
        Assert.Empty(mail.Page(10, 5));
        Assert.Equal([bids[5]], mail.Page(5, 50).Select(m => m.Bid));

        // A snapshot taken earlier stays as it was.
        store.Acknowledge(bids[0], BbsVerdict.Accepted);
        Assert.Equal(3, mail.Waiting);
        Assert.Equal(2, store.Mail.Waiting);
    }
}
