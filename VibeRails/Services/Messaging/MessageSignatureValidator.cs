using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VibeRails.Services.Messaging;

public class MessageSignatureValidator
{
    private readonly X509Certificate2 _publicCert;

    public MessageSignatureValidator(X509Certificate2 publicCert)
    {
        _publicCert = publicCert;
    }

    public bool VerifyMessage(string message, string base64Signature)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(base64Signature))
            return false;

        using RSA publicRsa = _publicCert.GetRSAPublicKey()
            ?? throw new InvalidOperationException("Failed to get RSA public key from certificate.");

        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var signatureBytes = Convert.FromBase64String(base64Signature);

        return publicRsa.VerifyData(messageBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}
