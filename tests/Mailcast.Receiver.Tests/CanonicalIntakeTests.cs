using Packet.Mailcast;
using Packet.SoundModem.Waterfall;
using Mailcast.Receiver.Delivery;
using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>What the intake tells the rest of the receiver when a canonical bulletin completes and when it later reaches the outbox (issue #145).</summary>
public class CanonicalIntakeTests
{
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());

    private static void Offer(Intake intake, TransferObject o)
    {
        for (uint i = 0; i < o.SourceSymbols; i++)
        {
            intake.Offer(Ax25UiFrame.Build(Samples.Source, OnAir.Destination, o.Frame(i).ToBytes()));
        }
    }

    [Fact]
    public async Task ABulletinHeldForItsEnvelope_CountsWhereItCompleted_AndIsReleasedWithoutCountingAgain()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        await using var intake = new Intake(dir.Path, log.Add, new ReceiverStoreOptions { Time = time, TrustedKeys = [TrustedKey.Parse("ZZ1AAA", Signer.PublicKeyBase64)] });
        var completed = new List<string>();
        var released = new List<string>();
        intake.BulletinCompleted += b => completed.Add(b.Bid);
        intake.BulletinReleased += b => released.Add(b.Bid);

        var (canonical, _) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", "88_TEST", "A title",
            System.Text.Encoding.Latin1.GetBytes("R:261019/0850Z 88@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody.\r\n"));
        var (_, envelope) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", "88_TEST", "A title",
            System.Text.Encoding.Latin1.GetBytes("R:261019/0850Z 88@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody.\r\n"));
        var bulletin = TransferObject.ForBulletin(canonical, ZstdDictionary.Dictionary1Id, Compression.Default);
        var env = TransferObject.ForEnvelope(envelope, ZstdDictionary.Dictionary1Id, Compression.Default);
        var entry = new DirectoryEntry(bulletin.ObjectId, bulletin.DictionaryId, 100, "88_TEST", "A title",
            Hash: ObjectId.LongHash(bulletin.DictionaryId, bulletin.Bytes),
            Envelope: new EnvelopeLink(env.ObjectId, ObjectId.LongHash(env.DictionaryId, env.Bytes)));
        var directory = TransferObject.ForDirectory(new BroadcastDirectory(new DateOnly(2026, 10, 30), [entry], signer: Signer), ZstdDictionary.Dictionary1Id, Compression.Default);

        // The bulletin completes, then its envelope, which is held until a directory lists it.
        Offer(intake, bulletin);
        await intake.DrainAsync(CancellationToken.None);
        Assert.Equal(["88_TEST"], completed);
        Assert.Empty(released);
        Offer(intake, env);
        await intake.DrainAsync(CancellationToken.None);
        Assert.Empty(intake.Pending());

        // The directory admits the envelope; the bulletin leaves its wait, and the delivery loop is told.
        Offer(intake, directory);
        await intake.DrainAsync(CancellationToken.None);
        Assert.Equal(["88_TEST"], released);
        Assert.Equal(["88_TEST"], completed);
        Assert.Single(intake.Pending());
        Assert.Single(log, l => l.StartsWith("bulletin complete: 88_TEST", StringComparison.Ordinal));
    }
}
