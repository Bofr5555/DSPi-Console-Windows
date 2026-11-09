using Org.BouncyCastle.Math.EC.Rfc8032;

namespace DSPiConsole.Core.Updates;

/// <summary>
/// The Ed25519 signature that tells a genuine release zip from a tampered one.
/// The zip's ".sig" holds the base64 signature over the whole file, the same
/// scheme as the macOS Console. The private key lives only on the machine that
/// cuts releases (Tools/UpdateSigning); the app embeds the public half.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Public half of the Windows release signing key.</summary>
    public const string PublicKeyBase64 = "1t7cd7Q0neVR6j76BaGPdYlku4nDxDviv53jA645XT0=";

    public static byte[] PublicKey => Convert.FromBase64String(PublicKeyBase64);

    /// <summary>Whether <paramref name="signatureText"/> (base64, surrounding
    /// whitespace allowed) is a valid signature of <paramref name="data"/>
    /// under <paramref name="publicKey"/>. Anything malformed is simply invalid.</summary>
    public static bool IsValid(byte[] data, string signatureText, byte[] publicKey)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText.Trim()); }
        catch (FormatException) { return false; }
        if (signature.Length != Ed25519.SignatureSize || publicKey.Length != Ed25519.PublicKeySize) return false;
        try { return Ed25519.Verify(signature, 0, publicKey, 0, data, 0, data.Length); }
        catch (ArgumentException) { return false; }
    }

    public static bool IsValid(byte[] data, string signatureText) => IsValid(data, signatureText, PublicKey);
}
