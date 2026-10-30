using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace Packet.Mailcast;

/// <summary>
/// The short name of an Ed25519 public key, as a signed directory gives it: the first eight
/// octets, big-endian, of the SHA-256 of the key's 32 raw octets, written as 16 hex digits.
/// </summary>
public static class KeyId
{
    /// <summary>The ID of a 32-octet Ed25519 public key.</summary>
    public static ulong Of(ReadOnlySpan<byte> publicKey) => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(publicKey));

    /// <summary>Sixteen hex digits.</summary>
    public static string Format(ulong keyId) => keyId.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Reads sixteen hex digits.</summary>
    public static bool TryParse(string text, out ulong keyId) => ObjectId.TryParse(text, out keyId);
}

/// <summary>
/// The head end's directory signing key: an Ed25519 private key, read from a PEM file of the kind
/// <c>openssl genpkey -algorithm ed25519</c> writes (PKCS#8, "BEGIN PRIVATE KEY").
/// </summary>
public sealed class DirectorySigner
{
    private readonly Ed25519PrivateKeyParameters _key;

    private DirectorySigner(Ed25519PrivateKeyParameters key)
    {
        _key = key;
        PublicKey = key.GeneratePublicKey().GetEncoded();
        KeyId = Packet.Mailcast.KeyId.Of(PublicKey);
    }

    /// <summary>The 32 raw octets of the public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The public key in base64, as a receiver's <c>trustedKeys</c> takes it.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    /// <summary>The public key's ID (<see cref="Packet.Mailcast.KeyId"/>).</summary>
    public ulong KeyId { get; }

    /// <summary>Reads a PEM PKCS#8 Ed25519 private key. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static DirectorySigner FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        if (!PemEncoding.TryFind(pem, out var fields) || pem[fields.Label] is not "PRIVATE KEY")
        {
            throw new FormatException("not a PEM private key (\"BEGIN PRIVATE KEY\")");
        }
        try
        {
            if (PrivateKeyFactory.CreateKey(Convert.FromBase64String(pem[fields.Base64Data])) is Ed25519PrivateKeyParameters key)
            {
                return new DirectorySigner(key);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new FormatException("not a readable PKCS#8 private key: " + e.Message, e);
        }
        throw new FormatException("not an Ed25519 key (make one with: openssl genpkey -algorithm ed25519)");
    }

    /// <summary>
    /// Reads the key file at <paramref name="path"/>: null when there is no such file. Throws
    /// <see cref="FormatException"/> when the file is there but cannot be read or is not a key.
    /// </summary>
    public static DirectorySigner? LoadIfPresent(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return null;
        }
        string pem;
        try
        {
            pem = File.ReadAllText(path, Encoding.ASCII);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"{path}: cannot read it: {e.Message}", e);
        }
        try
        {
            return FromPem(pem);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{path}: {e.Message}", e);
        }
    }

    /// <summary>
    /// A new key in the PEM form <c>openssl genpkey -algorithm ed25519</c> writes: what a head
    /// end's key file holds (<see cref="CreateKeyFile"/>), and what tests use.
    /// </summary>
    public static string NewKeyPem()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        byte[] der = PrivateKeyInfoFactory.CreatePrivateKeyInfo(pair.Private).GetDerEncoded();
        return new string(PemEncoding.Write("PRIVATE KEY", der)) + "\n";
    }

    /// <summary>
    /// Makes a new key at <paramref name="path"/>, readable and writable by its owner only, and
    /// returns true; does nothing and returns false if the file is already there (whatever it holds).
    /// </summary>
    public static bool CreateKeyFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (File.Exists(path))
        {
            return false;
        }
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using var stream = new FileStream(path, options);
            stream.Write(Encoding.ASCII.GetBytes(NewKeyPem()));
        }
        catch (IOException) when (File.Exists(path))
        {
            return false; // made by someone else meanwhile
        }
        return true;
    }

    /// <summary>The 64-octet Ed25519 signature of <paramref name="message"/>.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }
}

/// <summary>A transmitter's public key that a receiver trusts, with the name the page shows for it.</summary>
public sealed class TrustedKey
{
    // The fixed DER prefix of an Ed25519 SubjectPublicKeyInfo, before its 32 key octets.
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");

    private readonly Ed25519PublicKeyParameters _key;

    private TrustedKey(string label, byte[] publicKey, Ed25519PublicKeyParameters key)
    {
        Label = label;
        PublicKey = publicKey;
        KeyId = Packet.Mailcast.KeyId.Of(publicKey);
        _key = key;
    }

    /// <summary>What the page and the log call the key's owner.</summary>
    public string Label { get; }

    /// <summary>The 32 raw octets of the key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The key's ID (<see cref="Packet.Mailcast.KeyId"/>).</summary>
    public ulong KeyId { get; }

    /// <summary>
    /// GB7RDG's own key, the first of <see cref="BuiltIn"/>: SubjectPublicKeyInfo
    /// <c>MCowBQYDK2VwAyEAe1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=</c>.
    /// </summary>
    public static TrustedKey Gb7rdg { get; } = Parse("GB7RDG", "MCowBQYDK2VwAyEAe1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=");

    /// <summary>The keys every receiver trusts, before any of its own.</summary>
    public static IReadOnlyList<TrustedKey> BuiltIn { get; } = [Gb7rdg];

    /// <summary>
    /// A key from its label and its base64: the 32 raw octets (as the head end logs it), or a
    /// whole SubjectPublicKeyInfo (the body of <c>openssl pkey -pubout</c>'s PEM). Throws
    /// <see cref="FormatException"/>, saying what is wrong, if it is neither.
    /// </summary>
    public static TrustedKey Parse(string label, string base64)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Any(c => c is < ' ' or > '~'))
        {
            throw new FormatException("a key's label must be plain printable ASCII, not empty");
        }
        byte[] octets;
        try
        {
            octets = Convert.FromBase64String(base64 ?? "");
        }
        catch (FormatException)
        {
            throw new FormatException($"the key for {label} is not base64");
        }
        if (octets.Length == SpkiPrefix.Length + 32 && octets.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix))
        {
            octets = octets[SpkiPrefix.Length..];
        }
        if (octets.Length != 32)
        {
            throw new FormatException($"the key for {label} is {octets.Length} octets, not an Ed25519 public key's 32");
        }
        Ed25519PublicKeyParameters key;
        try
        {
            key = new Ed25519PublicKeyParameters(octets);
        }
        catch (ArgumentException)
        {
            throw new FormatException($"the key for {label} is not a valid Ed25519 public key");
        }
        return new TrustedKey(label.Trim(), octets, key);
    }

    /// <summary>Whether <paramref name="signature"/> is this key's signature of <paramref name="message"/>.</summary>
    public bool Verifies(ReadOnlySpan<byte> message, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var verifier = new Ed25519Signer();
        verifier.Init(false, _key);
        verifier.BlockUpdate(message);
        return verifier.VerifySignature(signature);
    }
}

/// <summary>Whether a directory was signed, and what that means for the receiver.</summary>
public enum SignatureState
{
    /// <summary>No signature: a receiver does not use it (issue #146), though <see cref="BroadcastDirectory.Check"/> only reports the fact.</summary>
    None,

    /// <summary>A good signature by a trusted key.</summary>
    Trusted,

    /// <summary>A signature that does not check, or is by a key not trusted: the directory is not used.</summary>
    Rejected,
}

/// <summary>What checking a directory's signature found.</summary>
/// <param name="State">Signed, unsigned or rejected.</param>
/// <param name="SignedBy">The trusted key's label, when signed.</param>
/// <param name="Reason">Why it was rejected, in words, when rejected or refused.</param>
/// <param name="Refused">An unsigned directory the receiver does not use (issue #146): see <see cref="Usable"/>.</param>
public sealed record DirectoryCheck(SignatureState State, string? SignedBy = null, string? Reason = null, bool Refused = false)
{
    /// <summary>What a receiver makes of an unsigned directory: not used for anything.</summary>
    public static DirectoryCheck UnsignedNotUsed { get; } = new(SignatureState.None, Reason: "it has no signature", Refused: true);

    /// <summary>The check of a directory with no signature.</summary>
    public static DirectoryCheck NotSigned { get; } = new(SignatureState.None);

    /// <summary>
    /// Whether the directory may be used: signed by a trusted key. An unsigned one is not, once
    /// the receiver has refused it (<see cref="Refused"/>; issue #146).
    /// </summary>
    public bool Usable => State == SignatureState.Trusted || (State == SignatureState.None && !Refused);

    /// <summary>A few plain words: "signed by GB7RDG", "not signed", or "rejected: ...".</summary>
    public string Describe() => State switch
    {
        SignatureState.Trusted => $"signed by {SignedBy}",
        SignatureState.None => Refused ? "unsigned, not used" : "not signed",
        _ => $"rejected: {Reason}",
    };
}
