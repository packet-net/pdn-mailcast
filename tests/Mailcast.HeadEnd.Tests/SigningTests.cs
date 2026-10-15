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
        Assert.Equal("signing: no key at /etc/pdn-mailcast-headend/signing-key.pem, so directories go out unsigned", Program.DescribeSigning(null, Program.SigningKeyPath));
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        // What the head end logs is what a receiver's trustedKeys takes.
        Assert.Equal(signer.KeyId, TrustedKey.Parse("X", signer.PublicKeyBase64).KeyId);
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
