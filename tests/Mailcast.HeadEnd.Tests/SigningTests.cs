using System.Text.Json.Nodes;
using Mailcast.HeadEnd.Status;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>The head end's directory signing key: the start-up line, and the status document.</summary>
public class SigningTests
{
    [Fact]
    public void StartUpLine_GivesThePublicKeyAndItsId_OrSaysUnsigned()
    {
        var signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        string line = Program.DescribeSigning(signer, Program.SigningKeyPath);
        Assert.Equal($"signing: directories are signed with key {KeyId.Format(signer.KeyId)}, public key {signer.PublicKeyBase64}", line);
        Assert.Equal("signing: no key at /etc/pdn-mailcast-headend/signing-key.pem, so offline runs go out unsigned", Program.DescribeSigning(null, Program.SigningKeyPath));
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        // What the head end logs is what a receiver's trustedKeys takes.
        Assert.Equal(signer.KeyId, TrustedKey.Parse("X", signer.PublicKeyBase64).KeyId);
    }

    [Fact]
    public async Task MakeKey_MakesAKeyOnlyIfMissing_Private_AndTheServiceRefusesWithoutOne()
    {
        string dir = Directory.CreateTempSubdirectory("mailcast-key").FullName;
        string oldPath = Program.KeyPath;
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            Program.KeyPath = Path.Combine(dir, "signing-key.pem");
            string config = Path.Combine(dir, "headend.json");
            File.WriteAllText(config, """{"callsign": "N0CALL", "station": {"apiKey": "x"}}""");
            var err = new StringWriter();
            Console.SetError(err);
            Console.SetOut(new StringWriter());

            // No key: the service refuses, in one plain line, and the offline modes still run.
            Assert.Equal(2, await Program.MainAsync(["--check-config", "--config", config]));
            string said = err.ToString().Trim();
            Assert.DoesNotContain('\n', said);
            Assert.Contains("no signing key", said);
            Assert.All(said, c => Assert.InRange(c, ' ', '~'));
            Assert.Equal(0, await Program.MainAsync(["--plan", "--config", config]));

            Assert.Equal(0, await Program.MainAsync(["--make-key"]));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Program.KeyPath));
            }
            var made = DirectorySigner.LoadIfPresent(Program.KeyPath)!;
            string before = File.ReadAllText(Program.KeyPath);

            // Again: nothing replaced.
            Assert.Equal(0, await Program.MainAsync(["--make-key"]));
            Assert.Equal(before, File.ReadAllText(Program.KeyPath));
            Assert.Equal(0, await Program.MainAsync(["--check-config", "--config", config]));

            // An openssl-made key (the same PEM) is kept as it is.
            string pem = DirectorySigner.NewKeyPem();
            File.WriteAllText(Program.KeyPath, pem);
            Assert.Equal(0, await Program.MainAsync(["--make-key"]));
            Assert.Equal(pem, File.ReadAllText(Program.KeyPath));
            Assert.NotEqual(made.KeyId, DirectorySigner.LoadIfPresent(Program.KeyPath)!.KeyId);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            Program.KeyPath = oldPath;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Status_SaysWhetherItSigns_AndWithWhichKey()
    {
        var store = new StatusStore(null, TimeProvider.System);
        var unsigned = JsonNode.Parse(store.Render())!["signing"]!;
        Assert.False(unsigned["signs"]!.GetValue<bool>());
        Assert.Null(unsigned["keyId"]);

        store.SigningKeyId = 0x6f39d04c925c01cf;
        var signing = JsonNode.Parse(store.Render())!["signing"]!;
        Assert.True(signing["signs"]!.GetValue<bool>());
        Assert.Equal("6f39d04c925c01cf", signing["keyId"]!.GetValue<string>());
    }
}
