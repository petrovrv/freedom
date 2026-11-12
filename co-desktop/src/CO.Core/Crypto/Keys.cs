// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using CO.Core.Protocol;

namespace CO.Core.Crypto;

// Public keys travel as base64 of raw X||Y (64 bytes, P-256). This is the one format both
// .NET 8 (ECParameters) and .NET Framework 4 (ECCPUBLICBLOB via CngKey.Import) read without
// ASN.1 code. Signatures are IEEE P1363 (r||s, 64 bytes), the default of both ECDsaCng and ECDsa.

public interface ISigner
{
    string PublicKey { get; }            // base64(X||Y)
    byte[] Sign(byte[] data);
}

public sealed class EcdsaSigner : ISigner, IDisposable
{
    private readonly ECDsa _key;

    public EcdsaSigner(ECDsa key)
    {
        _key = key;
        ECParameters p = key.ExportParameters(false);
        PublicKey = Convert.ToBase64String(p.Q.X!.Concat(p.Q.Y!).ToArray());
    }

    public static EcdsaSigner CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public string PublicKey { get; }

    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

public static class KeyUtil
{
    public static bool Verify(string publicKeyB64, byte[] data, string signatureB64)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(publicKeyB64);
            byte[] sig = Convert.FromBase64String(signatureB64);
            if (raw.Length != 64 || sig.Length != 64) return false;
            using ECDsa ec = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = raw[..32], Y = raw[32..] },
            });
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static string Fingerprint(string publicKeyB64) => Wire.Sha256Hex(Convert.FromBase64String(publicKeyB64));

    // TZ §6.2.5: first 20 bits of SHA-256(devicePub || bridgePub), modulo 10^6, as 6 digits.
    public static string PairingCode(string devicePublicKeyB64, string bridgePublicKeyB64)
    {
        byte[] a = Convert.FromBase64String(devicePublicKeyB64);
        byte[] b = Convert.FromBase64String(bridgePublicKeyB64);
        byte[] h = SHA256.HashData(a.Concat(b).ToArray());
        int v = (h[0] << 12) | (h[1] << 4) | (h[2] >> 4);
        return (v % 1_000_000).ToString("D6");
    }

    public static void SignCall(ToolCall call, ISigner signer) =>
        call.Signature = Convert.ToBase64String(signer.Sign(call.SigningPayload()));

    public static void SignResult(ToolResult result, ISigner signer) =>
        result.Signature = Convert.ToBase64String(signer.Sign(result.SigningPayload()));
}
