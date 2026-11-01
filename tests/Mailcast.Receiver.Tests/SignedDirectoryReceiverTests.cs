using Packet.SoundModem.Waterfall;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Signed directories at the receiver: its trustedKeys setting, and what its page shows.</summary>
public class SignedDirectoryReceiverTests
{
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Stranger = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());

    [Fact]
    public void TrustedKeys_AreAddedToTheBuiltInOnes_AndKeptWhenSaved()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, $$"""{ "trustedKeys": [ { "label": "M0XYZ", "publicKey": "{{Signer.PublicKeyBase64}}" } ] }""");
        var config = ReceiverConfig.Load(path);
        Assert.Equal(["GB7RDG", "M0XYZ"], config.AllTrustedKeys.Select(k => k.Label));
        Assert.Equal(Signer.KeyId, config.AllTrustedKeys[1].KeyId);

        config.Save(path);
        Assert.Equal(["GB7RDG", "M0XYZ"], ReceiverConfig.Load(path).AllTrustedKeys.Select(k => k.Label));

        // Left out, only the built-in key, and nothing is written for it.
        File.WriteAllText(path, "{}");
        var plain = ReceiverConfig.Load(path);
        Assert.Equal([StationFixture.Key.KeyId], plain.AllTrustedKeys.Select(k => k.KeyId));
        plain.Save(path);
        Assert.DoesNotContain("trustedKeys", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "trustedKeys": [ { "label": "M0XYZ", "publicKey": "AAAA" } ] }""", "entry 1: the key for M0XYZ is 3 octets")]
    [InlineData("""{ "trustedKeys": [ { "label": "M0XYZ", "publicKey": "not base64!" } ] }""", "not base64")]
    [InlineData("""{ "trustedKeys": [ { "publicKey": "e1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=" } ] }""", "label")]
    [InlineData("""{ "trustedKeys": [ null ] }""", "entry 1 is null")]
    public void TrustedKeys_ThatCannotWork_AreRefusedAtStartUp(string json, string expected)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
        Assert.Contains("\"trustedKeys\"", e.Message, StringComparison.Ordinal);
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_SaysSignedBy_AndWhyADirectoryWasNotUsed()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero));
        var lines = new List<string>();
        var (host, page) = await StartAsync(dir.Path, time, lines);
        await using var _h = host;
        await using var _p = page;

        var slot = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        var mine = Samples.Bulletin(1);
        foreach (var frame in Samples.Frames([mine], ScheduleOptions.Hourly with { Signer = Signer }, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.Equal("signed by TEST", status.GetProperty("directory").GetProperty("signature").GetString());
        Assert.Equal("signed", status.GetProperty("lastDirectory").GetProperty("state").GetString());
        Assert.Equal("TEST", status.GetProperty("lastDirectory").GetProperty("signedBy").GetString());
        Assert.Equal("signed by TEST", status.GetProperty("bulletins")[0].GetProperty("signature").GetString());
        Assert.Contains(lines, l => l.Contains("1 bulletins in rotation", StringComparison.Ordinal) && l.EndsWith(", signed by TEST", StringComparison.Ordinal));

        // Another transmitter's directory, signed with a key this receiver does not trust: not
        // used, said once, and its bulletin held rather than delivered.
        var theirs = Samples.Bulletin(2);
        foreach (var frame in Samples.Frames([theirs], ScheduleOptions.Hourly with { Signer = Stranger }, slot.AddHours(1)))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        var last = status.GetProperty("lastDirectory");
        Assert.Equal("rejected", last.GetProperty("state").GetString());
        Assert.Contains("not a trusted key", last.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("signed by TEST", status.GetProperty("directory").GetProperty("signature").GetString()); // still the one in use
        Assert.Single(lines, l => l.Contains("not used: it is signed by key", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Intake.Pending(), b => b.Bid == theirs.Bid);

        var mail = JsonSerializer.SerializeToElement(page.MailList(new System.Collections.Specialized.NameValueCollection()), ReceiverConfig.JsonLine);
        Assert.Equal("signed by TEST", mail.GetProperty("signature").GetString());
        Assert.Contains("not a trusted key", mail.GetProperty("rejected").GetString(), StringComparison.Ordinal);
        Assert.True(Assert.Single(mail.GetProperty("partial").EnumerateArray()).GetProperty("withheld").GetBoolean());
        Assert.Equal("signed by TEST", Assert.Single(mail.GetProperty("items").EnumerateArray()).GetProperty("signature").GetString());
        Assert.All(lines, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
    }

    [Fact]
    public async Task TheObjectApi_GivesTheHashEachTrustedDirectoryListsAnObjectWith()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero));
        var (host, page) = await StartAsync(dir.Path, time, []);
        await using var _h = host;
        await using var _p = page;
        var slot = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        var mine = Samples.Bulletin(1);
        foreach (var frame in Samples.Frames([mine], ScheduleOptions.Hourly with { Signer = Signer }, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        using var http = new HttpClient { BaseAddress = new Uri(page.Url) };
        var index = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("api/objects"));
        var listed = index.GetProperty("objects").EnumerateArray().Where(o => o.GetProperty("listedBy").GetArrayLength() > 0).ToList();

        Assert.NotEmpty(listed);
        foreach (var row in listed)
        {
            var listing = Assert.Single(row.GetProperty("listings").EnumerateArray());
            Assert.Equal("TEST", listing.GetProperty("station").GetString());
            Assert.Matches("^[0-9a-f]{16}$", listing.GetProperty("hash").GetString());
        }
    }

    [Fact]
    public async Task TheObjectApi_LinksABulletinsEnvelopePerListingStation_AndListsTheEnvelopeAsAnObject()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero));
        var (host, page) = await StartAsync(dir.Path, time, []);
        await using var _h = host;
        await using var _p = page;
        var slot = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        // A head end's store takes the bulletin in canonical form with an envelope, and its directory links it.
        var options = ScheduleOptions.Hourly with { Signer = Signer };
        using var store = new TempDirectory();
        var head = new HeadEndStore(store.Path, Compression.Default, options);
        head.Offer(Samples.Bulletin(1), DateOnly.FromDateTime(slot.UtcDateTime));
        var plan = BroadcastScheduler.Plan(head.InRotation(slot), slot, 1, Compression.Default, options);
        foreach (var frame in plan.Frames)
        {
            host.Intake.Offer(Ax25UiFrame.Build(Samples.Source, OnAir.Destination, frame.ToBytes()));
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        using var http = new HttpClient { BaseAddress = new Uri(page.Url) };
        var index = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("api/objects"));
        var rows = index.GetProperty("objects").EnumerateArray().ToList();
        var linked = rows.SelectMany(o => o.GetProperty("listings").EnumerateArray())
            .Where(l => l.TryGetProperty("envelope", out var e) && e.ValueKind == JsonValueKind.Object)
            .ToList();
        Assert.NotEmpty(linked);
        var link = linked[0].GetProperty("envelope");
        Assert.Matches("^[0-9a-f]{16}$", link.GetProperty("id").GetString());
        Assert.Matches("^[0-9a-f]{16}$", link.GetProperty("hash").GetString());
        var envelope = Assert.Single(rows, o => o.GetProperty("id").GetString() == link.GetProperty("id").GetString());
        Assert.Equal(5, envelope.GetProperty("type").GetInt32());
    }

    private static async Task<(ReceiverHost Host, StatusPage Page)> StartAsync(string dir, TimeProvider time, List<string> lines)
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var config = new ReceiverConfig
            {
                Audio = "wav:/nonexistent.wav",
                StateDirectory = dir,
                Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
                Web = new WebSettings { Port = port },
                TrustedKeys = [new TrustedKeySettings { Label = "TEST", PublicKey = Signer.PublicKeyBase64 }],
            };
            var host = new ReceiverHost(config, time, line => { lock (lines) { lines.Add(line); } });
            var page = new StatusPage(host, null, _ => { });
            try
            {
                page.Start();
                return (host, page);
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }
}
