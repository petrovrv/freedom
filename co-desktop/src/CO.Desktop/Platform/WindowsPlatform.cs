// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CO.Core.Auth;
using CO.Core.Crypto;
using CO.Core.Runtime;

namespace CO.Desktop.Platform;

// TZ §5 / §6.2.2: device keys live in CNG. TPM (Microsoft Platform Crypto Provider) when present,
// otherwise the software provider, whose key files Windows protects with DPAPI for the current user.
public sealed class WindowsKeyStore : IDeviceKeyStore
{
    private const string SigningName = "CO.Desktop.Device.Signing";
    private const string ServiceAccountName = "CO.Desktop.Device.ServiceAccount";
    private readonly EcdsaSigner _signer;
    private readonly RSACng _rsa;

    public WindowsKeyStore()
    {
        (CngKey ec, bool hw1) = OpenOrCreate(SigningName, CngAlgorithm.ECDsaP256, 0);
        (CngKey rsa, bool hw2) = OpenOrCreate(ServiceAccountName, CngAlgorithm.Rsa, 2048);
        _signer = new EcdsaSigner(new ECDsaCng(ec));
        _rsa = new RSACng(rsa);
        HardwareBacked = hw1 && hw2;
    }

    public bool HardwareBacked { get; }
    public ISigner SigningKey() => _signer;
    public RSA ServiceAccountKey() => _rsa;

    private static (CngKey key, bool hardware) OpenOrCreate(string name, CngAlgorithm alg, int bits)
    {
        foreach (var (provider, hw) in new[] { (new CngProvider("Microsoft Platform Crypto Provider"), true), (CngProvider.MicrosoftSoftwareKeyStorageProvider, false) })
        {
            try
            {
                if (CngKey.Exists(name, provider, CngKeyOpenOptions.None))
                    return (CngKey.Open(name, provider, CngKeyOpenOptions.None), hw);
            }
            catch (CryptographicException) { /* provider missing */ }
        }
        foreach (var (provider, hw) in new[] { (new CngProvider("Microsoft Platform Crypto Provider"), true), (CngProvider.MicrosoftSoftwareKeyStorageProvider, false) })
        {
            try
            {
                var p = new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyUsage = CngKeyUsages.Signing,
                    ExportPolicy = CngExportPolicies.None,        // private key never leaves the provider
                    KeyCreationOptions = CngKeyCreationOptions.None,
                };
                if (bits > 0) p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(bits), CngPropertyOptions.None));
                return (CngKey.Create(alg, name, p), hw);
            }
            catch (CryptographicException) when (hw) { /* no TPM: fall back to software */ }
        }
        throw new CryptographicException("Could not create device key " + name);
    }

    public static void DeleteKeys()
    {
        foreach (string name in new[] { SigningName, ServiceAccountName })
            foreach (var provider in new[] { new CngProvider("Microsoft Platform Crypto Provider"), CngProvider.MicrosoftSoftwareKeyStorageProvider })
                try { if (CngKey.Exists(name, provider)) CngKey.Open(name, provider).Delete(); } catch (CryptographicException) { }
    }
}

// Windows Credential Manager, generic credentials (same store as Bridge v0.9).
public sealed class CredentialManagerStore : ISecretStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private const int CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    public string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            byte[] b = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, b, 0, b.Length);
            return Encoding.UTF8.GetString(b);
        }
        finally { CredFree(p); }
    }

    public void Write(string target, string secret)
    {
        byte[] b = Encoding.UTF8.GetBytes(secret);
        IntPtr blob = Marshal.AllocHGlobal(b.Length);
        try
        {
            Marshal.Copy(b, 0, blob, b.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC, TargetName = target, CredentialBlobSize = b.Length,
                CredentialBlob = blob, Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = Environment.UserName,
            };
            if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[b.Length], 0, blob, b.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
}

// TZ §9.6: managed profile by domain / Entra join.
public static class ManagedDetector
{
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetAadJoinInformation(string? tenantId, out IntPtr info);
    [DllImport("netapi32.dll")]
    private static extern void NetFreeAadJoinInformation(IntPtr info);

    private const int NetSetupDomainName = 3;

    public static bool IsDomainOrEntraJoined()
    {
        try
        {
            if (NetGetJoinInformation(null, out IntPtr n, out int status) == 0)
            {
                NetApiBufferFree(n);
                if (status == NetSetupDomainName) return true;
            }
            if (NetGetAadJoinInformation(null, out IntPtr aad) == 0 && aad != IntPtr.Zero)
            {
                NetFreeAadJoinInformation(aad);
                return true;
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return false;
    }
}
