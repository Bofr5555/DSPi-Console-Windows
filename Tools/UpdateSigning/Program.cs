// Signs DSPi Console release zips so the in-app updater can tell a genuine
// release from a tampered one. The private key lives only on the machine that
// cuts releases, encrypted to this Windows user (DPAPI) in
// %APPDATA%\DSPiConsole\update-signing.key; the app embeds the public half
// (DSPiConsole.Core/Updates/UpdateSignature.cs). Port of the macOS Console's
// Tools/UpdateSigning/update-signing.swift.
//
//   dotnet run --project Tools/UpdateSigning -- generate
//   dotnet run --project Tools/UpdateSigning -- public-key
//   dotnet run --project Tools/UpdateSigning -- sign <zip>      (writes <zip>.sig)
//   dotnet run --project Tools/UpdateSigning -- verify <zip>
//   dotnet run --project Tools/UpdateSigning -- export > backup.txt
//   dotnet run --project Tools/UpdateSigning -- import < backup.txt
//
// Losing the key means users have to update by hand once, to a build that
// embeds a new public key, so keep a backup from `export` somewhere safe.

using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "DSPiConsole", "update-signing.key");
var entropy = "DSPi Console update signing key"u8.ToArray();

switch (args.FirstOrDefault())
{
    case "generate":
    {
        if (File.Exists(keyPath)) return Fail("A signing key already exists; refusing to replace it.");
        var key = new byte[Ed25519.SecretKeySize];
        RandomNumberGenerator.Fill(key);
        Store(key);
        Console.WriteLine(PublicOf(key));
        return 0;
    }
    case "public-key":
        return WithKey(key => Console.WriteLine(PublicOf(key)));
    case "sign" when args.Length == 2:
        return WithKey(key =>
        {
            var data = File.ReadAllBytes(args[1]);
            var signature = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(key, 0, data, 0, data.Length, signature, 0);
            File.WriteAllText(args[1] + ".sig", Convert.ToBase64String(signature));
            Console.WriteLine($"Wrote {args[1]}.sig");
        });
    case "verify" when args.Length == 2:
    {
        int result = 0;
        int status = WithKey(key =>
        {
            var data = File.ReadAllBytes(args[1]);
            var signature = Convert.FromBase64String(File.ReadAllText(args[1] + ".sig").Trim());
            var pub = Convert.FromBase64String(PublicOf(key));
            if (signature.Length == Ed25519.SignatureSize && Ed25519.Verify(signature, 0, pub, 0, data, 0, data.Length))
                Console.WriteLine("Signature OK");
            else
                result = Fail("Signature does NOT match.");
        });
        return status != 0 ? status : result;
    }
    case "export":
        return WithKey(key => Console.WriteLine(Convert.ToBase64String(key)));
    case "import":
    {
        if (File.Exists(keyPath)) return Fail("A signing key already exists; refusing to replace it.");
        byte[] key;
        try { key = Convert.FromBase64String((Console.ReadLine() ?? "").Trim()); }
        catch (FormatException) { return Fail("Not a valid key backup."); }
        if (key.Length != Ed25519.SecretKeySize) return Fail("Not a valid key backup.");
        Store(key);
        Console.WriteLine(PublicOf(key));
        return 0;
    }
    default:
        return Fail("usage: generate | public-key | sign <zip> | verify <zip> | export | import");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string PublicOf(byte[] key)
{
    var pub = new byte[Ed25519.PublicKeySize];
    Ed25519.GeneratePublicKey(key, 0, pub, 0);
    return Convert.ToBase64String(pub);
}

void Store(byte[] key)
{
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllBytes(keyPath, ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser));
}

int WithKey(Action<byte[]> use)
{
    if (!File.Exists(keyPath)) return Fail("No signing key. Run `generate`, or `import` a backup.");
    byte[] key;
    try { key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), entropy, DataProtectionScope.CurrentUser); }
    catch (CryptographicException) { return Fail("The signing key can't be read by this Windows user."); }
    use(key);
    return 0;
}
