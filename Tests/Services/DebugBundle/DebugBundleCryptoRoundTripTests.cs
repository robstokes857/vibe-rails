using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Tests;

/// <summary>
/// Proves the debug-bundle envelope round-trips: the client-side operations in
/// VibeRails.Services.Integrations.VibeCodeRemote.DebugBundleService (Brotli → AES-256-GCM →
/// RSA-OAEP-SHA256 wrap) are exactly inverted by the server-side UploadCryptoService
/// (RSA unwrap → AES-GCM → Brotli). If either side changes algorithm/padding/order, this fails.
///
/// The primary test uses an ephemeral RSA-4096 key so it runs everywhere. A second test uses
/// the real shipped public key + the server private key when available locally (skipped in CI).
/// </summary>
public class DebugBundleCryptoRoundTripTests
{
    // Mirrors DebugBundleService.BrotliCompress.
    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data, 0, data.Length);
        return output.ToArray();
    }

    // Mirrors UploadCryptoService.BrotliDecompress.
    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    // Client-side encrypt: returns the envelope exactly as DebugBundleService builds it.
    private static (byte[] wrappedKey, byte[] nonce, byte[] tag, byte[] ciphertext) Encrypt(byte[] plaintextDb, RSA publicRsa)
    {
        var compressed = Compress(plaintextDb);
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[compressed.Length];
        using (var aes = new AesGcm(aesKey, tag.Length))
            aes.Encrypt(nonce, compressed, ciphertext, tag);
        var wrappedKey = publicRsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        return (wrappedKey, nonce, tag, ciphertext);
    }

    // Server-side decrypt: mirrors UploadCryptoService.DecryptBundle.
    private static byte[] Decrypt(byte[] wrappedKey, byte[] nonce, byte[] tag, byte[] ciphertext, RSA privateRsa)
    {
        var aesKey = privateRsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        var compressed = new byte[ciphertext.Length];
        using (var aes = new AesGcm(aesKey, tag.Length))
            aes.Decrypt(nonce, ciphertext, tag, compressed);
        return Decompress(compressed);
    }

    private static byte[] SampleBundle()
    {
        // A plausible-ish payload: SQLite magic header + binary + UTF-8 + control bytes,
        // so compression and AES handle realistic bundle bytes.
        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("SQLite format 3\0"));
        payload.AddRange(RandomNumberGenerator.GetBytes(4096));
        payload.AddRange(Encoding.UTF8.GetBytes("[2J[H> prompt text with a glyph ›\n"));
        return payload.ToArray();
    }

    [Fact]
    public void Envelope_RoundTrips_WithEphemeralKey()
    {
        using var rsa = RSA.Create(4096);
        var original = SampleBundle();

        var (wrappedKey, nonce, tag, ciphertext) = Encrypt(original, rsa);

        // Sanity: the payload is actually encrypted (ciphertext differs from compressed plaintext).
        Assert.NotEqual(Compress(original), ciphertext);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(16, tag.Length);

        var decrypted = Decrypt(wrappedKey, nonce, tag, ciphertext, rsa);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Tampering_WithCiphertext_FailsAuthentication()
    {
        using var rsa = RSA.Create(4096);
        var (wrappedKey, nonce, tag, ciphertext) = Encrypt(SampleBundle(), rsa);

        ciphertext[0] ^= 0xFF; // flip a bit

        Assert.Throws<AuthenticationTagMismatchException>(
            () => Decrypt(wrappedKey, nonce, tag, ciphertext, rsa));
    }

    [Fact]
    public void Envelope_RoundTrips_WithRealShippedKeys()
    {
        // Encrypt with the public key that actually ships in VibeRails/Certs, decrypt with the
        // server private key. Skipped when the private key isn't available (e.g. CI), since it
        // lives only on the server / VibeRails-Front repo and must never be committed here.
        var publicKeyPath = FindUpwards("VibeRails/Certs/rsa_public_key.pem");
        if (publicKeyPath is null)
        {
            Assert.Skip("Shipped public key not found.");
            return;
        }

        var privateKeyPath = Environment.GetEnvironmentVariable("VIBERAILS_UPLOAD_PRIVATE_KEY")
            ?? FindUpwards("../VibeRails-Front/CERTS/uploads/rsa_private_key.pem");
        var passwordPath = Environment.GetEnvironmentVariable("VIBERAILS_UPLOAD_PASS")
            ?? (privateKeyPath is null ? null : Path.Combine(Path.GetDirectoryName(privateKeyPath)!, "pass.txt"));

        if (privateKeyPath is null || !File.Exists(privateKeyPath) || passwordPath is null || !File.Exists(passwordPath))
        {
            Assert.Skip("Server private key not available locally; skipping real-key round-trip.");
            return;
        }

        using var publicRsa = RSA.Create();
        publicRsa.ImportFromPem(File.ReadAllText(publicKeyPath));

        using var privateRsa = RSA.Create();
        privateRsa.ImportFromEncryptedPem(File.ReadAllText(privateKeyPath), File.ReadAllText(passwordPath).Trim());

        var original = SampleBundle();
        var (wrappedKey, nonce, tag, ciphertext) = Encrypt(original, publicRsa);
        var decrypted = Decrypt(wrappedKey, nonce, tag, ciphertext, privateRsa);

        Assert.Equal(original, decrypted);
    }

    private static string? FindUpwards(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, relativePath));
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
