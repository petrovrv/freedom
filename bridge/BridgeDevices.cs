// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace AiWorkbenchBridge
{
    /*
     * DEVICE TOOLS (v1.0, TZ-CO-DESKTOP v1.2)
     * =======================================
     * A Run may use tools of paired CO Desktop devices. Chain:
     *
     *   CLI (Claude/Codex) --stdio MCP--> BridgeAgent.exe --mcp-device (proxy process)
     *       --named pipe--> Bridge main process (DeviceHub): signs tool.call, publishes it
     *       --Pub/Sub co-<host>-dev-<id>-calls | Drive slot--> CO Desktop Tool Host
     *       <--Pub/Sub co-<host>-dev-results | Drive results/-- signed tool.result
     *
     * Trust: the device executes only calls signed with the Bridge device-call key
     * (CNG, non-exportable) and pinned at pairing. Bridge accepts only results signed with
     * the key from the device record written by the owner. Device records live in
     * transport/hosts/<host>/devices/<deviceId>.json; revoked records are ignored.
     *
     * C# 5 / .NET Framework 4: no string interpolation, no ?. operator.
     */

    internal sealed class DeviceSettings
    {
        public bool enabled { get; set; }
        public int maxCallsPerRun { get; set; }
        public int degradedMaxCallsPerRun { get; set; }
        public int callTimeoutSeconds { get; set; }
        public int askUserTimeoutSeconds { get; set; }
        public int mcpOutputTokens { get; set; }
        public int scanSeconds { get; set; }
        public bool permissionPromptViaDevice { get; set; }
        public bool driveFallback { get; set; }
        public string[] corporateDomains { get; set; } // config.json key (legacy name): employer mail domains; default = the gateway module's
        // v1.9.2: environments whose CO Desktop devices auto-approve agent actions (signed "trusted" in bridge.json),
        // like the owner's own "I trust this environment" box. Submit/purchase/delete in the agent browser still ask.
        public string[] trustedEnvironments { get; set; }
        // v1.12.0 (S1): serve calls of site jobs to these devices through the server (db.php relay_*), and the home PC's
        // own tool library as device "h-<host>" (tool_run only for the tools listed here, owner's environment only).
        public bool relay { get; set; }
        public string[] relayTools { get; set; }
        // v1.19.0: short name of device h-<host> (the site agent's MCP server dev_<short>); default "home". A second
        // computer of the owner (a Linux server) needs its own, else its tools hide the first one's.
        public string relayHomeShort { get; set; }
        // v1.15.0: pc_* tools of h-<host> (BridgePcTools.cs) for the owner's site jobs; pcFamily = environment -> Linux
        // user of the WSL sandbox pcSandbox, each with its own device h-<host>-<user> (pc_* only, inside the sandbox).
        public bool pcTools { get; set; }
        public bool pcFamilyTools { get; set; }
        public Dictionary<string, string> pcFamily { get; set; }
        public string pcSandbox { get; set; }
        public string pcRipgrep { get; set; }
        public PcQuotaSettings pcFamilyQuota { get; set; }  // limits of each family member in the sandbox

        public DeviceSettings()
        {
            enabled = true;
            maxCallsPerRun = 400;
            degradedMaxCallsPerRun = 60;
            callTimeoutSeconds = 120;
            askUserTimeoutSeconds = 1800;
            mcpOutputTokens = 25000;
            scanSeconds = 15;
            permissionPromptViaDevice = true;
            driveFallback = true;
            corporateDomains = null; // F10: null = the domains of the gateway module (read when needed)
            trustedEnvironments = new string[0];
            relay = true;
            relayTools = new string[] { "browser-agent", "linkedin-web", "whatsapp-web", "2dehands", "doccle", "buildwise-norms",
                                        "gmail-attachments", "gmail-draft", "job-boards" };
            pcTools = true;
            pcFamilyTools = true;
            pcFamily = new Dictionary<string, string>();
            pcSandbox = "aiwb-family";
            pcFamilyQuota = new PcQuotaSettings();
        }
    }

    internal sealed class PcQuotaSettings
    {
        public int maxBackground { get; set; }
        public int maxTimeoutSec { get; set; }
        public int maxCallsPerJob { get; set; }

        public PcQuotaSettings()
        {
            maxBackground = 3;
            maxTimeoutSec = 1800;
            maxCallsPerJob = 300;
        }
    }

    internal sealed class RunAsSettings
    {
        public string user { get; set; }            // e.g. "co-agent" (local account without Outlook profile)
        public string domain { get; set; }          // "." for a local account
        public string credentialTarget { get; set; } // Credential Manager target holding the password

        public RunAsSettings()
        {
            domain = ".";
            credentialTarget = "AIWorkbench.Bridge.RunAs";
        }
    }

    internal sealed class OwnerDeviceKey
    {
        public string publicKey { get; set; }
        public string fingerprint { get; set; }
    }

    // ============================================================= keys

    internal static class DeviceCrypto
    {
        public const string CallKeyName = "AIWorkbench.Bridge.DeviceCalls";
        private static readonly byte[] EccPublicMagicP256 = { 0x45, 0x43, 0x53, 0x31, 0x20, 0x00, 0x00, 0x00 }; // "ECS1", 32

        // Windows: non-exportable ECDSA P-256 key in the user's software key store. v1.19.0, Linux/macOS (.NET 8): the
        // private key (PKCS#8) in the secret store of the platform under the same name - keyring, never a file.
        public static ECDsa OpenOrCreateCallKey()
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return OpenOrCreateStoredKey(BridgePlatform.Current.Secrets);
#endif
            if (CngKey.Exists(CallKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                return new ECDsaCng(CngKey.Open(CallKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider));
            CngKeyCreationParameters p = new CngKeyCreationParameters();
            p.Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
            p.ExportPolicy = CngExportPolicies.None;
            p.KeyUsage = CngKeyUsages.Signing;
            return new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, CallKeyName, p));
        }

#if NET8_0_OR_GREATER
        internal static ECDsa OpenOrCreateStoredKey(ISecretStore store)
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8 = store.Read(CallKeyName);
            if (!String.IsNullOrEmpty(pkcs8))
            {
                int read;
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8.Trim()), out read);
                return key;
            }
            store.Write(CallKeyName, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "AI Workbench Bridge: device-call key");
            return key;
        }
#endif

        // base64 of X||Y, the format CO.Core uses.
        public static string PublicKey(ECDsa key)
        {
            ECDsaCng cng = key as ECDsaCng;
            if (cng != null)
            {
                byte[] blob = cng.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                byte[] xy = new byte[64];
                Buffer.BlockCopy(blob, 8, xy, 0, 64);
                return Convert.ToBase64String(xy);
            }
            ECParameters prm = key.ExportParameters(false);
            byte[] q = new byte[64];
            Buffer.BlockCopy(prm.Q.X, 0, q, 0, 32);
            Buffer.BlockCopy(prm.Q.Y, 0, q, 32, 32);
            return Convert.ToBase64String(q);
        }

        public static string Fingerprint(string publicKeyB64)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(Convert.FromBase64String(publicKeyB64));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in d)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Sign(ECDsa key, byte[] data)
        {
            return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));   // IEEE P1363 r||s
        }

        public static bool Verify(string publicKeyB64, byte[] data, string signatureB64)
        {
            try
            {
                byte[] xy = Convert.FromBase64String(publicKeyB64);
                byte[] sig = Convert.FromBase64String(signatureB64);
                if (xy.Length != 64 || sig.Length != 64)
                    return false;
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                {
                    ECParameters prm = new ECParameters();
                    prm.Curve = ECCurve.NamedCurves.nistP256;
                    prm.Q = new ECPoint { X = xy[0..32], Y = xy[32..64] };
                    using (ECDsa e = ECDsa.Create(prm))
                        return e.VerifyData(data, sig, HashAlgorithmName.SHA256);
                }
#endif
                byte[] blob = new byte[72];
                Buffer.BlockCopy(EccPublicMagicP256, 0, blob, 0, 8);
                Buffer.BlockCopy(xy, 0, blob, 8, 64);
                using (CngKey k = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (ECDsaCng ec = new ECDsaCng(k))
                {
                    ec.HashAlgorithm = CngAlgorithm.Sha256;
                    return ec.VerifyData(data, sig);
                }
            }
            catch (FormatException) { return false; }
            catch (CryptographicException) { return false; }
        }
    }

    // ============================================================= device registry records

    internal sealed class PairedDevice
    {
        public string deviceId;
        public string shortName;
        public string name;
        public string environmentId;
        public string account;
        public string signingKey;
        public string serviceAccount;
        public string serviceAccountKeyId;
        public string folderId;
        public string resultsFolderId;
        public List<string> slotIds = new List<string>();
        public object tools;                  // MCP tools/list array as parsed JSON (device record)
        public object liveTools;              // newer catalog the device published in pairing-request.json
        public DateTime toolsCheckedUtc = DateTime.MinValue;
        public DateTime updateCheckedUtc = DateTime.MinValue;
        public int updateBusy;                // 1 while an installer upload runs
        public bool revoked;
        public DateTime lastHeartbeatUtc = DateTime.MinValue;
        public DateTime lastPsHeartbeatUtc = DateTime.MinValue;   // v1.10.2: Pub/Sub heartbeats only, not results
        public DateTime resourcesEnsuredUtc = DateTime.MinValue;
        public bool bridgeInfoWritten;
        public string trustWritten;           // v1.9.2: "1"/"0" last written to bridge.json, null = not yet
        public DateTime trustUntilUtc;        // v1.9.2: expiry of the written grant (renewed a day before)
        // v1.4.1: gateway session from the device heartbeat (online|signin|offline|unknown|off, "" = old
        // device); Freedom F1 5b: corp {id, state, num}, older devices: legacy state and number fields
        public string corpState = "";
        public string corpId = "";
        public DateTime corpChangedUtc = DateTime.MinValue;
        public DateTime corpNotifiedUtc = DateTime.MinValue;    // last "sign-in needed" push
        public string corpNumId = "";                           // v1.5.2: last sign-in number id pushed to the owner
        public string version = "";                             // CO Desktop version from the heartbeat
        public DateTime offlineNotifiedUtc = DateTime.MinValue; // "CO Desktop offline" push, reset by a heartbeat
        // v1.5.0: web devices (CO Web tab, db.php webdev_list); no keys, folders or Pub/Sub
        public string kind = "";                            // "" = CO Desktop, "web" = CO Web browser tab
        public string userEmail = "";
        public bool webOnline;                              // server "online" at the last webdev_list
        public string webLastSeenUtc = "";                  // last_seen_utc as reported by the server
        public DateTime webListedUtc = DateTime.MinValue;   // local time of the webdev_list that reported it
        public bool IsWeb { get { return kind == "web"; } }
    }

    internal sealed class JobDeviceState
    {
        public string jobId;
        public string taskId;
        public string chatId;                // v1.9.2: sent to devices 0.9.8+ (agent browser tab per chat)
        public string projectDir;            // v1.9.2: fs_write sourcePath must be inside it
        public BridgeApplicationContext.BrowserSettings browser; // v1.10.0: unsigned "browser" of calls to devices 0.9.9+
        public string environmentId;
        public string agent;                 // v1.1.6: agent of the job (usage ledger)
        public List<string> devices = new List<string>();
        public volatile bool tainted;
        public int calls;
        public HashSet<string> denyTools = new HashSet<string>(StringComparer.Ordinal); // v1.3.5
        // v1.5.1: code of the last failed call of a gateway module tool; null after a successful one
        public volatile string corpFailure;

        // v1.3.5: valid tool names only, at most 32
        internal static HashSet<string> DenySet(List<string> names)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            if (names == null) return set;
            foreach (string n in names)
            {
                if (set.Count >= 32) break;
                if (n != null && Regex.IsMatch(n, @"\A[a-zA-Z0-9_-]{1,64}\z"))
                    set.Add(n);
            }
            return set;
        }
    }

    internal sealed class PendingCall
    {
        public string callId;
        public string deviceId;
        public string tool;
        public string runId;
        public readonly ManualResetEvent done = new ManualResetEvent(false);
        public Dictionary<string, object> result;
        public int slotIndex = -1;
        public volatile bool received;        // v1.9.2: the device said it got the call (tool.received)
    }

    // ============================================================= Bridge side

    internal sealed partial class BridgeApplicationContext
    {
        private ECDsa _deviceCallKey;
        private string _deviceCallPublicKey;
        private readonly ConcurrentDictionary<string, PairedDevice> _pairedDevices =
            new ConcurrentDictionary<string, PairedDevice>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, JobDeviceState> _deviceJobs =
            new ConcurrentDictionary<string, JobDeviceState>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PendingCall> _pendingCalls =
            new ConcurrentDictionary<string, PendingCall>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string[]> _slotOwners =
            new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        private Thread _deviceScanThread;
        private Thread _deviceResultsThread;
        private Thread _deviceDriveThread;
        private Thread _devicePipeThread;
        private string _runAsWarning;
        private DateTime _deviceHubStartedUtc = DateTime.MaxValue;
        private readonly HashSet<string> _pendingInfoWritten = new HashSet<string>(StringComparer.Ordinal);

        private DeviceSettings D
        {
            get { return _config.devices ?? new DeviceSettings(); }
        }

        private string DevicesDir { get { return Path.Combine(_hostTransportRoot, "devices"); } }
        private string DevicesPendingDir { get { return Path.Combine(DevicesDir, "pending"); } }
        private string DeviceResultsTopic { get { return "co-" + _config.hostId + "-dev-results"; } }
        private string DeviceResultsSubscription { get { return "co-" + _config.hostId + "-dev-results-bridge"; } }
        private string DeviceCallsTopic(string deviceId) { return "co-" + _config.hostId + "-dev-" + deviceId + "-calls"; }
        private string DeviceEventsSubscription(string env, string deviceId) { return EnvEventsTopic(env) + "-dev-" + deviceId; }
        internal string DevicePipeName { get { return "AIWorkbench.Bridge.Devices." + _config.hostId; } }

        // ---------------------------------------------------------------- lifecycle

        private void StartDeviceHub()
        {
            _runAsWarning = DetectRunAsWarning();
            if (_runAsWarning != null)
                Log("WARNING: " + _runAsWarning);

            if (!D.enabled)
            {
                Log("Device tools disabled in config.");
                return;
            }

            try
            {
                _deviceCallKey = DeviceCrypto.OpenOrCreateCallKey();
                _deviceCallPublicKey = DeviceCrypto.PublicKey(_deviceCallKey);
                Log("Device-call key fingerprint: " + DeviceCrypto.Fingerprint(_deviceCallPublicKey));
            }
            catch (Exception ex)
            {
                Log("Device tools disabled: cannot open the device-call key: " + ex.Message);
                return;
            }

            _deviceHubStartedUtc = DateTime.UtcNow;
            _deviceScanThread = StartThread("device-scan", DeviceScanLoop);
            _deviceResultsThread = StartThread("device-results", DeviceResultsLoop);
            _deviceDriveThread = StartThread("device-drive", DeviceDriveLoop);
            _devicePipeThread = StartThread("device-pipe", DevicePipeLoop);
            StartWebDevices();   // v1.5.0
            StartRelay();        // v1.12.0
        }

        private Thread StartThread(string name, ThreadStart body)
        {
            // A device loop that fails ends its thread with a log line; the Bridge process keeps running.
            Thread t = new Thread(delegate()
            {
                try { body(); }
                catch (Exception ex) { Log("Thread " + name + " stopped: " + ex); }
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
            return t;
        }

        private OwnerDeviceKey BuildOwnerDeviceKey()
        {
            if (_deviceCallPublicKey == null)
                return null;
            OwnerDeviceKey k = new OwnerDeviceKey();
            k.publicKey = _deviceCallPublicKey;
            k.fingerprint = DeviceCrypto.Fingerprint(_deviceCallPublicKey);
            return k;
        }

        // ---------------------------------------------------------------- registry and pairing

        private void DeviceScanLoop()
        {
            while (!_exiting)
            {
                try
                {
                    LoadDeviceRecords();
                    if (GoogleReady())
                    {
                        string token = _gAuth.GetAccessToken();
                        DiscoverPairingRequests(token);
                        foreach (PairedDevice d in _pairedDevices.Values.ToList())
                            if (!d.IsWeb)
                                MaintainDevice(d, token);
                    }
                }
                catch (Exception ex)
                {
                    Log("Device scan: " + ex.Message);
                }
                for (int i = 0; i < Math.Max(5, D.scanSeconds) * 4 && !_exiting; i++)
                    Thread.Sleep(250);
            }
        }

        private void LoadDeviceRecords()
        {
            if (!Directory.Exists(DevicesDir))
                return;
            foreach (string file in Directory.GetFiles(DevicesDir, "*.json"))
            {
                try
                {
                    Dictionary<string, object> rec = JsonUtil.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                    Dictionary<string, object> req = rec == null ? null : rec["request"] as Dictionary<string, object>;
                    string id = DeviceWire.S(req, "deviceId");
                    if (!IsValidDeviceId(id) || IsWebDeviceId(id) || !String.Equals(Path.GetFileNameWithoutExtension(file), id, StringComparison.Ordinal))
                        continue;

                    PairedDevice d = _pairedDevices.GetOrAdd(id, delegate(string k) { PairedDevice n = new PairedDevice(); n.deviceId = k; return n; });
                    if (d.IsWeb)
                        continue;
                    d.shortName = DeviceWire.S(req, "short");
                    d.name = DeviceWire.S(req, "name");
                    d.environmentId = DeviceWire.S(req, "environmentId");
                    d.account = DeviceWire.S(req, "account");
                    d.signingKey = DeviceWire.S(req, "signingKey");
                    d.folderId = DeviceWire.S(req, "deviceFolderId");
                    d.resultsFolderId = DeviceWire.S(req, "resultsFolderId");
                    d.slotIds = GHttp.Items(req.ContainsKey("slotIds") ? req["slotIds"] : null).Select(delegate(object o) { return Convert.ToString(o); }).ToList();
                    d.tools = req.ContainsKey("tools") ? req["tools"] : null;
                    d.serviceAccount = DeviceWire.S(rec, "serviceAccount");
                    d.serviceAccountKeyId = DeviceWire.S(rec, "serviceAccountKeyId");
                    bool wasRevoked = d.revoked;
                    d.revoked = DeviceWire.B(rec, "revoked");
                    if (d.revoked && !wasRevoked)
                        d.bridgeInfoWritten = false;
                }
                catch (Exception ex)
                {
                    Log("Device record " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }

        private static bool IsValidDeviceId(string id)
        {
            return id != null && Regex.IsMatch(id, @"\A[a-z0-9-]{6,40}\z");
        }

        // Device folders are created by CO Desktop in the user's Drive (shared with the owner
        // for participants). Bridge copies pairing-request.json to devices/pending/ and fills
        // bridge.json so the device can show the pairing code (TZ §6.2).
        private void DiscoverPairingRequests(string token)
        {
            DriveLite drive = new DriveLite(token);
            List<DriveItem> folders = drive.Query(
                "name contains 'CO-Device-' and mimeType='" + DriveLite.FolderMime + "' and trashed=false");
            foreach (DriveItem f in folders)
            {
                string id = f.name.StartsWith("CO-Device-", StringComparison.Ordinal) ? f.name.Substring(10) : null;
                if (!IsValidDeviceId(id) || IsWebDeviceId(id))
                    continue;
                PairedDevice known;
                if (_pairedDevices.TryGetValue(id, out known) && (known.bridgeInfoWritten || known.revoked))
                    continue;

                string reqId = drive.FindChildId(f.id, "pairing-request.json", false);
                string bridgeId = drive.FindChildId(f.id, "bridge.json", false);
                if (reqId == null || bridgeId == null)
                    continue;

                string reqText = Encoding.UTF8.GetString(drive.Download(reqId));
                Dictionary<string, object> req = JsonUtil.DeserializeObject(reqText) as Dictionary<string, object>;
                if (req == null || DeviceWire.S(req, "deviceId") != id || DeviceWire.S(req, "deviceFolderId") != f.id)
                {
                    Log("Pairing request in " + f.name + " does not match its folder; ignored.");
                    continue;
                }

                string env = DeviceWire.S(req, "environmentId");
                if (!IsValidEnvironment(env))
                {
                    Log("Pairing request " + id + " names unknown environment '" + env + "'; ignored.");
                    continue;
                }

                if (known == null)
                {
                    Directory.CreateDirectory(DevicesPendingDir);
                    string pendingPath = Path.Combine(DevicesPendingDir, id + ".json");
                    if (!File.Exists(pendingPath) || File.ReadAllText(pendingPath, Encoding.UTF8) != reqText)
                    {
                        AtomicFile.WriteText(pendingPath, reqText);
                        Log("Pairing request from device " + id + " (" + DeviceWire.S(req, "name") + ", " + DeviceWire.S(req, "account") + ").");
                    }
                    // Written once per request and Bridge start; the device keeps polling it.
                    if (_pendingInfoWritten.Add(id + "|" + DeviceWire.Sha256Hex(reqText)))
                        WriteBridgeInfo(drive, token, bridgeId, "pending", null);
                }
            }
        }

        private bool IsValidEnvironment(string env)
        {
            if (String.IsNullOrEmpty(env) || !Regex.IsMatch(env, @"\A[A-Za-z0-9_-]{1,40}\z"))
                return false;
            // Known environments: those with a context policy or already used by jobs; an empty map accepts any.
            return _config.chatProjectEnvironments == null || _config.chatProjectEnvironments.Count == 0 ||
                   _config.chatProjectEnvironments.ContainsKey(env);
        }

        private void WriteBridgeInfo(DriveLite drive, string token, string bridgeFileId, string state, PairedDevice d)
        {
            Dictionary<string, object> info = new Dictionary<string, object>();
            info["state"] = state;
            info["hostId"] = _config.hostId;
            info["projectId"] = G.projectId ?? "";
            info["ownerEmail"] = drive.AccountEmail();
            info["bridgePublicKey"] = _deviceCallPublicKey;
            info["serviceAccount"] = d == null ? "" : d.serviceAccount;
            info["serviceAccountKeyId"] = d == null ? "" : d.serviceAccountKeyId;
            // v1.9.2: owner-granted auto-approval, signed so only Bridge can grant it (CO Desktop 0.9.8+ verifies).
            // A grant expires after 2 days and is renewed daily, so an old bridge.json put back after a withdrawal
            // stops working within 2 days at most.
            string trusted = null;
            DateTime until = DateTime.UtcNow.AddDays(2);
            if (d != null && state == "paired")
            {
                trusted = IsTrustedEnvironment(d.environmentId) ? "1" : "0";
                string untilText = until.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                info["trusted"] = trusted == "1";
                info["trustUntil"] = untilText;
                info["trustSig"] = DeviceCrypto.Sign(_deviceCallKey, DeviceWire.Payload("co.device.trust.v2", d.deviceId, trusted, untilText));
            }
            DriveWrite.UpdateContent(token, bridgeFileId, Encoding.UTF8.GetBytes(JsonUtil.Serialize(info)));
            // Recorded only after a successful write, so a failed grant or withdrawal is retried at the next scan.
            if (trusted != null)
            {
                d.trustWritten = trusted;
                d.trustUntilUtc = until;
            }
        }

        private bool IsTrustedEnvironment(string env)
        {
            return !String.IsNullOrEmpty(env) && D.trustedEnvironments != null &&
                   D.trustedEnvironments.Contains(env, StringComparer.Ordinal);
        }

        private void MaintainDevice(PairedDevice d, string token)
        {
            PubSubLite ps = String.IsNullOrWhiteSpace(G.projectId) ? null : new PubSubLite(G.projectId, token);

            if (d.revoked)
            {
                if (!d.bridgeInfoWritten)
                {
                    if (ps != null)
                    {
                        PubSubAdmin.DeleteSubscription(G.projectId, token, DeviceCallsTopic(d.deviceId));
                        PubSubAdmin.DeleteTopic(G.projectId, token, DeviceCallsTopic(d.deviceId));
                        if (!String.IsNullOrEmpty(d.environmentId))
                            PubSubAdmin.DeleteSubscription(G.projectId, token, DeviceEventsSubscription(d.environmentId, d.deviceId));
                    }
                    string bridgeId = FindDeviceFile(token, d, "bridge.json");
                    if (bridgeId != null)
                        WriteBridgeInfo(new DriveLite(token), token, bridgeId, "revoked", d);
                    d.bridgeInfoWritten = true;
                    Log("Device " + d.deviceId + " revoked: channels removed.");
                }
                return;
            }

            if ((DateTime.UtcNow - d.toolsCheckedUtc).TotalMinutes >= 2)
            {
                d.toolsCheckedUtc = DateTime.UtcNow;
                try { RefreshDeviceTools(d, token); }
                catch (Exception ex) { Log("Device " + d.deviceId + " tool catalog: " + ex.Message); }
            }

            // v1.9.2: the trusted flag is new or changed in config.json: rewrite bridge.json (the device reads it every 5 min).
            if (d.bridgeInfoWritten && (d.trustWritten != (IsTrustedEnvironment(d.environmentId) ? "1" : "0") ||
                                        (d.trustWritten == "1" && d.trustUntilUtc - DateTime.UtcNow < TimeSpan.FromDays(1))))
            {
                string bridgeId = FindDeviceFile(token, d, "bridge.json");
                if (bridgeId != null)
                {
                    WriteBridgeInfo(new DriveLite(token), token, bridgeId, "paired", d);
                    Log("Device " + d.deviceId + " (" + d.name + "): auto-approval " + (d.trustWritten == "1" ? "granted" : "off") + ".");
                }
            }

            if ((DateTime.UtcNow - d.updateCheckedUtc).TotalMinutes >= 10)
            {
                d.updateCheckedUtc = DateTime.UtcNow;
                OfferDesktopUpdate(d, token);
            }

            bool due = (DateTime.UtcNow - d.resourcesEnsuredUtc).TotalMinutes >= 30;
            if (!due || ps == null || String.IsNullOrEmpty(d.serviceAccount))
                return;

            string member = "serviceAccount:" + d.serviceAccount;
            string calls = DeviceCallsTopic(d.deviceId);
            ps.EnsureTopic(calls);
            ps.EnsureSubscription(calls, calls, 60, "600s", null);
            ps.EnsureTopic(DeviceResultsTopic);
            ps.EnsureSubscription(DeviceResultsSubscription, DeviceResultsTopic, 30, "600s", null);
            ps.EnsureTopic(EnvEventsTopic(d.environmentId));
            ps.EnsureSubscription(DeviceEventsSubscription(d.environmentId, d.deviceId), EnvEventsTopic(d.environmentId), 10, "600s", "604800s");

            PubSubAdmin.AddBinding(G.projectId, token, "subscriptions/" + calls, "roles/pubsub.subscriber", member);
            PubSubAdmin.AddBinding(G.projectId, token, "subscriptions/" + DeviceEventsSubscription(d.environmentId, d.deviceId), "roles/pubsub.subscriber", member);
            PubSubAdmin.AddBinding(G.projectId, token, "topics/" + DeviceResultsTopic, "roles/pubsub.publisher", member);

            // Participant accounts publish jobs and read their environment's web subscription (TZ §6.1).
            DriveLite drive = new DriveLite(token);
            string owner = drive.AccountEmail();
            if (!String.IsNullOrEmpty(d.account) && !String.Equals(d.account, owner, StringComparison.OrdinalIgnoreCase))
            {
                ps.EnsureTopic(JobsTopic);
                ps.EnsureSubscription(EnvWebSubscription(d.environmentId), EnvEventsTopic(d.environmentId), 10, "600s", null);
                PubSubAdmin.AddBinding(G.projectId, token, "topics/" + JobsTopic, "roles/pubsub.publisher", "user:" + d.account);
                PubSubAdmin.AddBinding(G.projectId, token, "subscriptions/" + EnvWebSubscription(d.environmentId), "roles/pubsub.subscriber", "user:" + d.account);
            }

            if (!d.bridgeInfoWritten)
            {
                string bridgeId = FindDeviceFile(token, d, "bridge.json");
                if (bridgeId != null)
                {
                    WriteBridgeInfo(drive, token, bridgeId, "paired", d);
                    d.bridgeInfoWritten = true;
                    string pending = Path.Combine(DevicesPendingDir, d.deviceId + ".json");
                    if (File.Exists(pending))
                        File.Delete(pending);
                    Log("Device " + d.deviceId + " (" + d.name + ") paired: channels ready.");
                }
            }
            d.resourcesEnsuredUtc = DateTime.UtcNow;
        }

        // ---------------------------------------------------------------- CO Desktop updates (TZ §6.5)

        private sealed class DesktopRelease
        {
            public string path;
            public string version;
            public long size;
            public DateTime writeUtc;
            public string sha256;
            public string manifestJson;
        }

        private DesktopRelease _desktopRelease, _residentRelease;
        private readonly object _desktopReleaseLock = new object();
        // 1.7.1: releases are taken from the local setup folder, not from Drive; Google Drive marks the installer as
        // malware (31.10: CO-Desktop-Setup-0.8.9.exe), after which devices get HTTP 403 for update.bin. The installer
        // itself is served by the CO site (publish_site.py); devices from 0.9.1 download it there.
        // Freedom 0.4.0: config desktopUpdatesDir (setup\ and mac\ under it); empty = the folder of the first installs when
        // it exists, else <localStateRoot>\desktop-updates (install/freedom_setup.py desktop-update fills it).
        private const string LegacyDesktopUpdatesDir = @"C:\BridgeAgent-v0.6\CO-Desktop";
        private string DesktopUpdatesRoot
        {
            get
            {
                if (_config != null && !String.IsNullOrWhiteSpace(_config.desktopUpdatesDir))
                    return ExpandPath(_config.desktopUpdatesDir, _localStateRoot);
                return Directory.Exists(LegacyDesktopUpdatesDir) ? LegacyDesktopUpdatesDir : Path.Combine(_localStateRoot ?? "", "desktop-updates");
            }
        }
        private string DesktopReleaseDir { get { return Path.Combine(DesktopUpdatesRoot, "setup"); } }
        // 1.9.1: CO Resident for macOS (SPEC R2). publish_mac.py puts the archive here and on the site (mac.php?op=update);
        // the manifest goes into resident-update.json, a file only Mac residents create. Own signing domain, so a
        // Windows manifest never passes as a Mac one or the other way round.
        private string ResidentReleaseDir { get { return Path.Combine(DesktopUpdatesRoot, "mac"); } }

        private DesktopRelease CurrentDesktopRelease()
        {
            return CurrentRelease(DesktopReleaseDir, "CO-Desktop-Setup-*.exe", @"\ACO-Desktop-Setup-(\d+\.\d+\.\d+)\.exe\z",
                "co.desktop.update.v1", SiteBase(_config) + "/agent/desktop.php?op=download", ref _desktopRelease, "CO Desktop");
        }

        private DesktopRelease CurrentResidentRelease()
        {
            return CurrentRelease(ResidentReleaseDir, "co-resident-*-osx-arm64.tar.gz", @"\Aco-resident-(\d+\.\d+\.\d+)-osx-arm64\.tar\.gz\z",
                "co.resident.update.v1", SiteBase(_config) + "/agent/mac.php?op=update", ref _residentRelease, "CO Resident (Mac)");
        }

        // Newest release file in the folder, signed with the device-call key the devices pinned at pairing.
        // A file still being written (within the last minute) is skipped.
        private DesktopRelease CurrentRelease(string dir, string glob, string pattern, string domain, string url, ref DesktopRelease cache, string label)
        {
            if (!Directory.Exists(dir) || _deviceCallKey == null)
                return null;
            FileInfo best = null;
            Version bestVersion = null;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles(glob))
            {
                Match m = Regex.Match(f.Name, pattern);
                if (!m.Success || (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 60 || f.Length == 0)
                    continue;
                Version v = new Version(m.Groups[1].Value);
                if (bestVersion == null || v > bestVersion)
                {
                    best = f;
                    bestVersion = v;
                }
            }
            if (best == null)
                return null;

            lock (_desktopReleaseLock)
            {
                DesktopRelease r = cache;
                if (r != null && r.path == best.FullName && r.size == best.Length && r.writeUtc == best.LastWriteTimeUtc)
                    return r;
                r = new DesktopRelease();
                r.path = best.FullName;
                r.version = bestVersion.ToString(3);
                r.size = best.Length;
                r.writeUtc = best.LastWriteTimeUtc;
                using (FileStream fs = File.OpenRead(best.FullName))
                using (SHA256 sha = SHA256.Create())
                    r.sha256 = DeviceWire.Hex(sha.ComputeHash(fs));
                Dictionary<string, object> man = new Dictionary<string, object>();
                man["version"] = r.version;
                man["sha256"] = r.sha256;
                man["size"] = r.size;
                man["fileName"] = best.Name;
                man["publishedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                man["sig"] = DeviceCrypto.Sign(_deviceCallKey, DeviceWire.Payload(domain, r.version, r.sha256, r.size.ToString(CultureInfo.InvariantCulture)));
                man["url"] = url;
                r.manifestJson = JsonUtil.Serialize(man);
                cache = r;
                Log(label + " release " + r.version + " ready for devices (" + r.size + " bytes).");
                return r;
            }
        }

        // The device creates update.json in its folder (drive.file lets it read only files it created); Bridge writes
        // the signed manifest there. Since 1.7.1 update.bin is no longer filled: the installer comes from the site.
        private void OfferDesktopUpdate(PairedDevice d, string token)
        {
            DesktopRelease r, mac;
            try { r = CurrentDesktopRelease(); mac = CurrentResidentRelease(); }
            catch (Exception ex) { Log("CO Desktop release: " + ex.Message); return; }
            if ((r == null && mac == null) || Interlocked.CompareExchange(ref d.updateBusy, 1, 0) != 0)
                return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Windows devices create update.json, Mac residents resident-update.json (never both).
                    string manId = r == null ? null : FindDeviceFile(token, d, "update.json");
                    DesktopRelease offer = r;
                    string label = "CO Desktop ";
                    if (manId == null && mac != null)
                    {
                        manId = FindDeviceFile(token, d, "resident-update.json");
                        offer = mac;
                        label = "CO Resident ";
                    }
                    if (manId == null)
                        return;     // device older than 0.4.0, or a resident before 0.1.2
                    Dictionary<string, object> cur = null;
                    try { cur = JsonUtil.DeserializeObject(Encoding.UTF8.GetString(new DriveLite(token).Download(manId))) as Dictionary<string, object>; }
                    catch (Exception) { }
                    if (cur != null && DeviceWire.S(cur, "sha256") == offer.sha256)
                        return;
                    DriveWrite.UpdateContent(token, manId, Encoding.UTF8.GetBytes(offer.manifestJson));
                    Log(label + offer.version + " offered to device " + d.deviceId + " (" + d.name + ").");
                }
                catch (Exception ex)
                {
                    d.updateCheckedUtc = DateTime.UtcNow.AddMinutes(-8);   // retry in about 2 minutes
                    Log("CO Desktop update for " + d.deviceId + ": " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref d.updateBusy, 0);
                }
            });
        }

        // A paired device republishes its tool catalog after an update (TZ §7.4). The request must keep
        // the device id and signing key of the record; only the tool list is taken from it.
        private void RefreshDeviceTools(PairedDevice d, string token)
        {
            string reqId = FindDeviceFile(token, d, "pairing-request.json");
            if (reqId == null)
                return;
            Dictionary<string, object> req = JsonUtil.DeserializeObject(
                Encoding.UTF8.GetString(new DriveLite(token).Download(reqId))) as Dictionary<string, object>;
            if (req == null || DeviceWire.S(req, "deviceId") != d.deviceId || DeviceWire.S(req, "signingKey") != d.signingKey)
                return;
            List<object> tools = GHttp.Items(req.ContainsKey("tools") ? req["tools"] : null).ToList();
            foreach (object o in tools)
            {
                Dictionary<string, object> t = o as Dictionary<string, object>;
                string name = t == null ? null : DeviceWire.S(t, "name");
                if (name == null || !Regex.IsMatch(name, @"\A[a-zA-Z0-9_-]{1,64}\z") || DeviceWire.S(t, "description").Length > 2000)
                    return;
            }
            int before = GHttp.Items(d.liveTools ?? d.tools).Count();
            d.liveTools = tools;
            if (tools.Count != before)
                Log("Device " + d.deviceId + " publishes " + tools.Count + " tools (was " + before + ").");
        }

        private static string FindDeviceFile(string token, PairedDevice d, string name)
        {
            if (String.IsNullOrEmpty(d.folderId))
                return null;
            return new DriveLite(token).FindChildId(d.folderId, name, false);
        }

        // ---------------------------------------------------------------- job integration

        // Called at the end of ReadAndValidateJob.
        private void ValidateDeviceRequest(JobRequest request)
        {
            if (request.devices == null || request.devices.Count == 0)
                return;
            if (!D.enabled || _deviceCallKey == null)
                throw new InvalidOperationException("Device tools are not enabled on this Bridge.");
            if (request.devices.Count > 4)
                throw new InvalidOperationException("At most 4 devices per Run.");
            if (request.task != null && !String.IsNullOrEmpty(request.task.taskId))
                ValidateSafeId(request.task.taskId, "task.taskId");

            // v1.5.0: web devices come from the database; refresh the registry before checking them.
            if (request.devices.Any(delegate(string x) { return x != null && x.StartsWith("w-", StringComparison.Ordinal); }))
                EnsureWebDevicesFresh(0);

            HashSet<string> shorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in request.devices)
            {
                PairedDevice d;
                if (!IsValidDeviceId(id) || !_pairedDevices.TryGetValue(id, out d) || d.revoked ||
                    (d.IsWeb ? !IsWebDeviceId(id) : String.IsNullOrEmpty(d.serviceAccount)))
                    throw new InvalidOperationException("Device " + id + " is not paired with this Bridge.");
                // TZ §8.3: a participant's device serves only Runs of its environment.
                if (!String.Equals(d.environmentId, request.environmentId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Device " + id + " belongs to environment '" + d.environmentId + "'.");
                if (!Regex.IsMatch(d.shortName ?? "", @"\A[a-z0-9]{1,12}\z") || !shorts.Add(d.shortName))
                    throw new InvalidOperationException("Device " + id + " has an invalid or duplicate short name.");
            }
        }

        // v1.10.0: agent browser settings of the job's chat (CO Web 0.87.0, db.php browser_settings): tabs per chat
        // (0 = unlimited), idle minutes, idle minutes with typed form data, captcha tab kept. Read once per job; the
        // built-in defaults when the database is off or does not answer in 5 s. Devices and browser-agent clamp again.
        internal sealed class BrowserSettings
        {
            public int tabs;
            public int idleMin = 120;
            public int formIdleMin = 360;
            public bool captchaKeep = true;

            public Dictionary<string, object> ToWire()
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["tabs"] = tabs;
                d["idleMin"] = idleMin;
                d["formIdleMin"] = formIdleMin;
                d["captchaKeep"] = captchaKeep;
                return d;
            }

            public static int Clamp(object v, int lo, int hi, int fallback)
            {
                if (v is int) return Math.Max(lo, Math.Min(hi, (int)v));
                if (v is long) return (int)Math.Max(lo, Math.Min(hi, (long)v));
                if (v is decimal) return (int)Math.Max(lo, Math.Min(hi, (decimal)v));
                return fallback;
            }

            public static BrowserSettings From(Dictionary<string, object> d)
            {
                BrowserSettings s = new BrowserSettings();
                if (d == null) return s;
                object v;
                if (d.TryGetValue("tabs", out v)) s.tabs = Clamp(v, 0, 20, s.tabs);
                if (d.TryGetValue("idleMin", out v)) s.idleMin = Clamp(v, 5, 1440, s.idleMin);
                if (d.TryGetValue("formIdleMin", out v)) s.formIdleMin = Clamp(v, 5, 2880, s.formIdleMin);
                s.formIdleMin = Math.Max(s.idleMin, s.formIdleMin);
                if (d.TryGetValue("captchaKeep", out v) && v is bool) s.captchaKeep = (bool)v;
                return s;
            }
        }

        private BrowserSettings ResolveBrowserSettings(JobRequest request)
        {
            if (_config.dbQueue == null || !_config.dbQueue.enabled || String.IsNullOrEmpty(_config.dbQueue.url) ||
                String.IsNullOrEmpty(request.environmentId))
                return new BrowserSettings();
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["env"] = request.environmentId;
                if (!String.IsNullOrEmpty(request.chatId)) body["chatId"] = request.chatId;
                Dictionary<string, object> r = DbqCall("browser_settings", body, false, 5000);
                object b;
                return BrowserSettings.From(r != null && r.TryGetValue("browser", out b) ? b as Dictionary<string, object> : null);
            }
            catch (Exception ex)
            {
                LogThrottled("browser-settings", "Browser settings: defaults (" + ex.Message + ")");
                return new BrowserSettings();
            }
        }

        // Adds MCP servers for the selected devices to the CLI command line (TZ §8.1).
        private string[] AddDeviceArguments(AgentRuntime runtime, JobRequest request, string localJobDir, string[] args, ProcessStartInfo psi)
        {
            if (request.devices == null || request.devices.Count == 0)
                return args;

            JobDeviceState js = new JobDeviceState();
            js.jobId = request.jobId;
            js.taskId = request.task != null && !String.IsNullOrEmpty(request.task.taskId) ? request.task.taskId : request.jobId;
            js.chatId = request.chatId != null && Regex.IsMatch(request.chatId, @"\A[A-Za-z0-9_-]{1,80}\z") ? request.chatId : "";
            js.environmentId = request.environmentId;
            js.projectDir = String.IsNullOrEmpty(request.projectId) ? null : Path.GetFullPath(Path.Combine(_projectsDir, request.projectId));
            js.devices.AddRange(request.devices);
            js.tainted = request.tainted;
            js.agent = request.agent;
            js.denyTools = JobDeviceState.DenySet(request.deviceToolsDeny);
            js.browser = request.browserSettings;
            _deviceJobs[request.jobId] = js;

            psi.EnvironmentVariables["MAX_MCP_OUTPUT_TOKENS"] = D.mcpOutputTokens.ToString(CultureInfo.InvariantCulture);

            // v1.4.0: the protocol-specific part (Claude --mcp-config file, Codex -c mcp_servers.*) is AgentRuntime.AddDeviceArguments.
            DeviceLaunch launch = new DeviceLaunch();
            launch.Exe = Process.GetCurrentProcess().MainModule.FileName;
            foreach (string id in request.devices)
            {
                DeviceServer server = new DeviceServer();
                server.ShortName = _pairedDevices[id].shortName;
                server.ProxyArgs = ProxyArgs(id, request.jobId);
                launch.Servers.Add(server);
            }
            launch.McpConfigPath = McpConfigPath(request.jobId, localJobDir);
            // v1.5.0: the permission prompt stays on a CO Desktop device; a web device has no approve tool.
            string approveId = request.devices.FirstOrDefault(delegate(string x) { return !_pairedDevices[x].IsWeb; });
            launch.ApproveShortName = D.permissionPromptViaDevice && approveId != null ? _pairedDevices[approveId].shortName : null;
            launch.ToolTimeoutSec = Math.Max(D.callTimeoutSeconds, D.askUserTimeoutSeconds);
            // v1.5.0: a web device call waits up to callTimeoutSeconds + 120 s (confirmation) + 15 s
            if (request.devices.Any(delegate(string x) { return _pairedDevices[x].IsWeb; }))
                launch.ToolTimeoutSec = Math.Max(launch.ToolTimeoutSec, Math.Max(10, D.callTimeoutSeconds) + 135);
            launch.Operation = AgentRuntime.ConversationOperation(request);
            return runtime.AddDeviceArguments(args, launch);
        }

        // Under runAs the CLI cannot read the owner's profile; the config (no secrets: exe path,
        // job id, pipe name) then goes to %ProgramData%\AIWorkbenchBridge\mcp, readable by local users.
        private string McpConfigPath(string jobId, string localJobDir)
        {
            if (_config.runAs == null || String.IsNullOrWhiteSpace(_config.runAs.user))
                return Path.Combine(localJobDir, "mcp-devices.json");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIWorkbenchBridge", "mcp");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                dir = Path.Combine(BridgePlatform.Current.Paths.SharedRoot, "mcp");
#endif
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, jobId + ".json");
        }

        private string[] ProxyArgs(string deviceId, string jobId)
        {
            return new string[] { "--mcp-device", deviceId, "--job", jobId, "--pipe", DevicePipeName };
        }

        private bool IsJobTainted(string jobId)
        {
            JobDeviceState js;
            return _deviceJobs.TryGetValue(jobId, out js) && js.tainted;
        }

        private void EndDeviceJob(string jobId)
        {
            JobDeviceState js;
            _deviceJobs.TryRemove(jobId, out js);
            try
            {
                string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIWorkbenchBridge", "mcp", jobId + ".json");
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                    shared = Path.Combine(BridgePlatform.Current.Paths.SharedRoot, "mcp", jobId + ".json");
#endif
                if (File.Exists(shared))
                    File.Delete(shared);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // TZ §9.7: CLI under a separate Windows account without the employer's Outlook profile.
        private void ApplyRunAs(ProcessStartInfo psi)
        {
            RunAsSettings ra = _config.runAs;
            if (ra == null || String.IsNullOrWhiteSpace(ra.user))
                return;
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                throw new NotSupportedException("runAs is Windows-only (CLI under another Windows account); remove runAs from config.json on " + BridgePlatform.Current.Id + ".");
#endif
            string password = RunAsCredential.ReadPassword(ra.credentialTarget);
            if (password == null)
                throw new InvalidOperationException("runAs.user is set but Credential Manager target '" + ra.credentialTarget + "' holds no password.");
            SecureString ss = new SecureString();
            foreach (char c in password)
                ss.AppendChar(c);
            ss.MakeReadOnly();
            psi.UserName = ra.user;
            psi.Domain = String.IsNullOrEmpty(ra.domain) ? "." : ra.domain;
            psi.Password = ss;
            psi.LoadUserProfile = true;

            // The environment block is copied from this process once EnvironmentVariables is touched,
            // so profile paths must point to the runAs user's profile (CLI logins live there).
            string domain = psi.Domain == "." ? Environment.MachineName : psi.Domain;
            string sid = new NTAccount(domain, ra.user).Translate(typeof(SecurityIdentifier)).Value;
            string profile;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                profile = k == null ? null : Environment.ExpandEnvironmentVariables(Convert.ToString(k.GetValue("ProfileImagePath")));
            if (String.IsNullOrEmpty(profile) || !Directory.Exists(profile))
                throw new InvalidOperationException("runAs user '" + ra.user + "' has no profile yet; sign in to Windows as that user once and log in Claude/Codex there.");
            psi.EnvironmentVariables["USERPROFILE"] = profile;
            psi.EnvironmentVariables["HOMEDRIVE"] = Path.GetPathRoot(profile).TrimEnd('\\');
            psi.EnvironmentVariables["HOMEPATH"] = profile.Substring(Path.GetPathRoot(profile).Length - 1);
            psi.EnvironmentVariables["APPDATA"] = Path.Combine(profile, @"AppData\Roaming");
            psi.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(profile, @"AppData\Local");
            psi.EnvironmentVariables["TEMP"] = Path.Combine(profile, @"AppData\Local\Temp");
            psi.EnvironmentVariables["TMP"] = Path.Combine(profile, @"AppData\Local\Temp");
            psi.EnvironmentVariables["USERNAME"] = ra.user;
            psi.EnvironmentVariables["USERDOMAIN"] = domain;
        }

        private string DetectRunAsWarning()
        {
            if (_config.runAs != null && !String.IsNullOrWhiteSpace(_config.runAs.user))
                return null;
            string found = FindEmployerOutlookAccount(D.corporateDomains != null && D.corporateDomains.Length > 0 ? D.corporateDomains : Gateway.Domains);
            return found == null ? null
                : "Outlook profile of this Windows user contains the employer account " + found +
                  "; CLI agents run under the same user and can reach it through COM. Set runAs in config.json (TZ §9.7).";
        }

        private static string FindEmployerOutlookAccount(string[] domains)
        {
            try
            {
                using (RegistryKey profiles = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles"))
                {
                    if (profiles == null)
                        return null;
                    return ScanForDomain(profiles, domains, 0);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ScanForDomain(RegistryKey key, string[] domains, int depth)
        {
            if (depth > 4)
                return null;
            foreach (string vn in key.GetValueNames())
            {
                object v = key.GetValue(vn);
                string text = v as string;
                if (text == null && v is byte[])
                    text = Encoding.Unicode.GetString((byte[])v).TrimEnd('\0');
                if (text == null || text.IndexOf('@') < 0)
                    continue;
                foreach (string dom in domains)
                    if (text.EndsWith("@" + dom, StringComparison.OrdinalIgnoreCase) || text.EndsWith("." + dom, StringComparison.OrdinalIgnoreCase))
                        return text;
            }
            foreach (string sub in key.GetSubKeyNames())
            {
                using (RegistryKey k = key.OpenSubKey(sub))
                {
                    if (k == null)
                        continue;
                    string f = ScanForDomain(k, domains, depth + 1);
                    if (f != null)
                        return f;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- calls

        // v1.5.1: failures of gateway module tools that mean the gateway model itself was unavailable
        // (see GatewayBlockedReason)
        private static readonly HashSet<string> GatewayBlockingCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "sign_in_needed", "tool_off", "device_unreachable", "device_timeout",
            "not_paired", "not_allowed", "connector_off", "unknown_tool", "module_disabled"
        };

        // Returns the JSON reply for the proxy: {ok, content, error:{code,message}, untrusted}.
        // v1.5.1: remembers the outcome of gateway module tool calls for the job result (gatewayBlocked).
        internal Dictionary<string, object> ExecuteDeviceCall(string jobId, string deviceId, string tool, string argsJson)
        {
            Dictionary<string, object> reply = ExecuteDeviceCallCore(jobId, deviceId, tool, argsJson);
            if (jobId == null || reply == null || !Gateway.IsTool(tool, null))
                return reply;
            JobDeviceState js;
            if (!_deviceJobs.TryGetValue(jobId, out js) || js == null)
                return reply;
            if (DeviceWire.B(reply, "ok"))
            {
                js.corpFailure = null;
                return reply;
            }
            Dictionary<string, object> error = reply.ContainsKey("error") ? reply["error"] as Dictionary<string, object> : null;
            string code = error != null ? DeviceWire.S(error, "code") : null;
            if (code != null && GatewayBlockingCodes.Contains(code))
                js.corpFailure = code;
            return reply;
        }

        // v1.5.1: line the dispatcher agent appends when it stopped because the gateway tool was missing or failing:
        // Gateway.BlockedMarker, the legacy markers and the module's own blockedMarkers.
        // Removes every occurrence (ordinal), trims trailing whitespace left behind, reports if found.
        internal static string StripBlockedMarker(string text, out bool found)
        {
            found = false;
            if (text == null)
                return null;
            if (!Gateway.HasMarker(text))
                return text;
            found = true;
            return Gateway.StripMarkers(text).TrimEnd();
        }

        // v1.5.1: why the gateway model was unavailable to this job, or null. Call before EndDeviceJob.
        // Completed jobs are only marked when the agent itself reported the block (marker).
        private string GatewayBlockedReason(JobRequest request, string finalStatus, bool agentReported)
        {
            if (finalStatus == "cancelled" || request == null)
                return null;
            if (agentReported)
                return "agent_reported";
            if (finalStatus != "failed")
                return null;
            JobDeviceState js = null;
            if (request.jobId != null)
                _deviceJobs.TryGetValue(request.jobId, out js);
            if (js != null && js.corpFailure != null)
                return js.corpFailure;
            // the dispatcher reports a block itself (marker); the gateway agent fails exactly when it cannot reach it
            if (IsGatewayAgent(request.agent))
            {
                if (request.devices == null || request.devices.Count == 0)
                    return "no_device";
                string gw = Gateway.ToolOf(Gateway.LlmKind);
                if (gw != null && request.deviceToolsDeny != null && request.deviceToolsDeny.Contains(gw))
                    return "gateway_off"; // the chat turned the gateway tool off
            }
            return null;
        }

        // The agent of a job runs on the gateway (config runtime kind gateway or a legacy kind, or a model of kind llm of
        // the gateway module).
        private bool IsGatewayAgent(string agentId)
        {
            AgentProfile a;
            if (agentId != null && _config.agents != null && _config.agents.TryGetValue(agentId, out a) && a != null
                && Gateway.IsGatewayRuntime(a.kind))
                return true;
            return Gateway.IsLlmAgent(agentId);
        }

        private Dictionary<string, object> ExecuteDeviceCallCore(string jobId, string deviceId, string tool, string argsJson)
        {
            JobDeviceState js;
            PairedDevice d;
            if (!_deviceJobs.TryGetValue(jobId, out js) || !js.devices.Contains(deviceId))
                return Fail("not_allowed", "Device " + deviceId + " is not selected for this Run.");
            if (!_pairedDevices.TryGetValue(deviceId, out d) || d.revoked)
                return Fail("not_paired", "Device is not paired.");
            if (!Regex.IsMatch(tool ?? "", @"\A[a-zA-Z0-9_-]{1,64}\z"))
                return Fail("invalid_tool", "Invalid tool name.");
            if (js.denyTools.Contains(tool))
                return Fail("tool_off", "This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.");
            string modCode;
            string modWhy = Modules.Refusal(tool, js.environmentId, js.tainted, out modCode); // F10: module off / risk class
            if (modWhy != null)
                return Fail(modCode, modWhy);

            // Web devices use neither Pub/Sub nor Drive: the normal limit applies to them.
            bool fastUp = _psState == "ok" || d.IsWeb;
            int limit = fastUp ? D.maxCallsPerRun : D.degradedMaxCallsPerRun;
            int n = Interlocked.Increment(ref js.calls);
            if (n > limit)
                return Fail("call_limit", "This Run reached its limit of " + limit + " device calls" + (fastUp ? "." : " (Drive fallback mode)."));
            if (d.IsWeb)
                return ExecuteWebDeviceCall(js, d, tool, argsJson);   // v1.5.0

            // v1.9.2: fs_write with sourcePath: Bridge reads the file (only inside the job's project folder) and sends
            // its bytes as contentBase64, so agents can put PDFs and other binary files on a device (CO Desktop 0.9.8+).
            if (tool == "fs_write" && !d.IsWeb && HasSourcePath(argsJson))
            {
                string why;
                string replaced = AttachSourceFile(js, argsJson, out why);
                if (replaced == null)
                    return Fail("source_refused", why);
                argsJson = replaced;
            }

            // timeoutMs bounds execution on the device; a confirmation may take up to the ask-user budget,
            // after which the device refuses to execute (ToolHost approval deadline).
            int timeoutSec = tool == "ask_user" || tool == "approve" ? D.askUserTimeoutSeconds : D.callTimeoutSeconds;
            int waitSec = Math.Max(timeoutSec, D.askUserTimeoutSeconds) + timeoutSec;

            Dictionary<string, object> call = new Dictionary<string, object>();
            call["v"] = 1;
            call["callId"] = "call-" + Guid.NewGuid().ToString("N");
            call["runId"] = jobId;
            call["taskId"] = js.taskId;
            call["deviceId"] = deviceId;
            call["tool"] = tool;
            call["argsJson"] = String.IsNullOrEmpty(argsJson) ? "{}" : argsJson;
            call["issuedUtc"] = DeviceWire.Now();
            call["tainted"] = js.tainted;
            call["timeoutMs"] = timeoutSec * 1000;
            bool modern = VersionAtLeast(d.version, 0, 9, 8);
            if (modern && !String.IsNullOrEmpty(js.chatId))
                call["chatId"] = js.chatId;
            call["sig"] = DeviceCrypto.Sign(_deviceCallKey, DeviceWire.CallPayload(call));
            // v1.10.0: not signed (tab lifetimes, not a permission); devices 0.9.9+ clamp it, older ones ignore it
            if (modern && js.browser != null)
                call["browser"] = js.browser.ToWire();

            PendingCall pc = new PendingCall();
            pc.callId = (string)call["callId"];
            pc.deviceId = deviceId;
            pc.tool = tool;
            pc.runId = jobId;
            _pendingCalls[pc.callId] = pc;
            try
            {
                // v1.12.0: a site job that ended (relay) before this call was registered never sends it
                if (_relayEnded.ContainsKey(jobId))
                    return Fail("cancelled", "The site job has ended.");
                bool viaPubSub = false;
                if (fastUp)
                {
                    try
                    {
                        PubSubAdmin.Publish(G.projectId, _gAuth.GetAccessToken(), DeviceCallsTopic(deviceId),
                            Encoding.UTF8.GetBytes(JsonUtil.Serialize(call)), "tool.call", deviceId, pc.callId);
                        viaPubSub = true;
                    }
                    catch (Exception ex)
                    {
                        Log("Device call via Pub/Sub failed, using Drive: " + ex.Message);
                    }
                }

                DateTime started = DateTime.UtcNow;
                DateTime deadline = started.AddSeconds(waitSec + 15);
                bool viaDrive = false;
                if (!viaPubSub)
                    viaDrive = SendViaDrive(d, pc, call);

                if (!viaPubSub && !viaDrive)
                    return Fail("device_unreachable", "Neither Pub/Sub nor Drive reached the device.");

                // The same signed call may go through both channels: the device executes a callId once, and its "replay"
                // answer to the second copy is dropped by RouteDeviceResult, so only the real result completes the call.
                // v1.9.2: a call the device has not confirmed (tool.received, devices 0.9.8+) within 20 s also goes
                // through Drive, even with fresh heartbeats: on 08.11 a device kept publishing heartbeats while its
                // calls subscription had died, and every call hung for 30 min.
                if (viaPubSub && !pc.done.WaitOne(TimeSpan.FromSeconds(Math.Min(20, timeoutSec))) && !pc.received && D.driveFallback)
                {
                    // Devices before 0.9.8 never confirm: for them only silent heartbeats count, as before.
                    bool stale = (DateTime.UtcNow - d.lastHeartbeatUtc).TotalSeconds > 90 &&
                                 (DateTime.UtcNow - _deviceHubStartedUtc).TotalSeconds > 90;
                    if ((modern || stale) && SendViaDrive(d, pc, call) && modern)
                        Log("Device " + d.shortName + ": " + tool + " not confirmed via Pub/Sub in 20 s, sent via Drive too.");
                }

                // v1.9.2: a modern device confirms every call it picks up; none within 150 s (Drive is polled every
                // 60 s while Pub/Sub looks up) means CO Desktop does not get calls: fail now instead of after 32 min.
                bool heard = (DateTime.UtcNow - d.lastHeartbeatUtc).TotalSeconds <= 90;   // the device can publish
                TimeSpan pickup = deadline - DateTime.UtcNow;
                if (pickup > TimeSpan.FromSeconds(130)) pickup = TimeSpan.FromSeconds(130);
                if (pickup < TimeSpan.Zero) pickup = TimeSpan.Zero;
                if (modern && viaPubSub && heard && !pc.received && !pc.done.WaitOne(pickup) && !pc.received)
                {
                    Log("Device " + d.shortName + ": " + tool + " not picked up within 150 s; call cancelled.");
                    CancelDeviceCall(d, pc);
                    return Fail("device_unreachable", "CO Desktop on " + d.name + " did not pick up the call within 150 s. " +
                        "Ask the user to check that the computer is awake and CO Desktop is running (restart it from the tray if it is).");
                }

                // v1.10.1: the long wait (ask-user budget) is only for calls a person may have to confirm; a call that
                // never asks ends on the device after timeoutSec. A device that picked the call up and then went silent
                // (no heartbeat for 120 s: a Mac asleep or offline) fails now: on 09.11 such calls hung for 32 min.
                if (NeverAsks(tool))
                    deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, timeoutSec + 15 - (DateTime.UtcNow - started).TotalSeconds));
                while (!pc.done.WaitOne(0))
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                    {
                        CancelDeviceCall(d, pc);
                        return Fail("device_timeout", "The device did not answer within " +
                            (int)(DateTime.UtcNow - started).TotalSeconds + " s.");
                    }
                    if (pc.done.WaitOne(left < TimeSpan.FromSeconds(10) ? left : TimeSpan.FromSeconds(10)))
                        break;
                    // v1.10.2: only a device that publishes heartbeats over Pub/Sub can fall silent. On 09.11 a member's Mac
                    // was awake but reached Bridge through Drive only (no Pub/Sub heartbeat at all), and 1.10.1 cancelled
                    // its calls as "asleep" while it was answering them.
                    bool silent = _psState == "ok" &&
                                  d.lastPsHeartbeatUtc > _deviceHubStartedUtc &&
                                  (DateTime.UtcNow - d.lastPsHeartbeatUtc).TotalSeconds > DeviceSilentSeconds &&
                                  (DateTime.UtcNow - d.lastHeartbeatUtc).TotalSeconds > DeviceSilentSeconds &&
                                  (DateTime.UtcNow - _deviceHubStartedUtc).TotalSeconds > DeviceSilentSeconds &&
                                  (DateTime.UtcNow - started).TotalSeconds > DeviceSilentSeconds;
                    if (silent && !pc.done.WaitOne(0))
                    {
                        Log("Device " + d.shortName + ": " + tool + " abandoned, no heartbeat for " +
                            (int)(DateTime.UtcNow - d.lastHeartbeatUtc).TotalSeconds + " s; call cancelled.");
                        CancelDeviceCall(d, pc);
                        return Fail("device_unreachable", d.name + " stopped answering (no signal for " +
                            (int)(DateTime.UtcNow - d.lastHeartbeatUtc).TotalSeconds + " s): it is probably asleep or offline. " +
                            "Do not retry now; tell the user to wake the computer and check that CO is connected, then try again.");
                    }
                }

                Dictionary<string, object> r = pc.result;
                if (DeviceWire.B(r, "untrusted"))
                    js.tainted = true;     // TZ §9.4: later calls and child Runs inherit the taint
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["ok"] = DeviceWire.B(r, "ok");
                reply["content"] = DeviceWire.S(r, "content");
                reply["untrusted"] = DeviceWire.B(r, "untrusted");
                if (r.ContainsKey("error") && r["error"] != null)
                    reply["error"] = r["error"];
                if (Gateway.IsTool(tool, Gateway.LlmKind) && DeviceWire.B(reply, "ok"))
                    TakeGatewayUsage(js, reply);   // v1.1.6: token counts leave the text, enter the ledger
                return reply;
            }
            finally
            {
                PendingCall removed;
                _pendingCalls.TryRemove(pc.callId, out removed);
                ReleaseSlot(pc);
            }
        }

        private static Dictionary<string, object> Fail(string code, string message)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["code"] = code;
            e["message"] = message;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = false;
            r["content"] = "";
            r["error"] = e;
            r["untrusted"] = false;
            return r;
        }

        // v1.10.1: devices publish a heartbeat every 30 s (CO.Core PubSubChannel), also while a person confirms a call
        private const int DeviceSilentSeconds = 120;

        // v1.10.1: read-only status tools that no device ever asks a person about
        private static bool NeverAsks(string tool)
        {
            switch (tool)
            {
                case "fs_list": case "fs_stat": case "browser_status": case "sys_info":
                case "log_read": case "connectors_status": case "mac_check":
                    return true;
                default:
                    return false;
            }
        }

        private const int MaxSourceBytes = 7 * 1024 * 1024;   // base64 stays under the 10 MB Pub/Sub message limit

        // v1.9.2: a call Bridge gave up on must never run later (the agent may already have retried it). A signed
        // tool.cancel goes to the device's calls topic and into the Drive slot of the call; CO Desktop 0.9.8+ puts the
        // call id into its replay guard, so a copy that arrives afterwards is refused as a replay.
        private void CancelDeviceCall(PairedDevice d, PendingCall pc)
        {
            string sig = DeviceCrypto.Sign(_deviceCallKey, DeviceWire.Payload("co.tool.cancel.v1", pc.callId, d.deviceId));
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["callId"] = pc.callId;
            msg["deviceId"] = d.deviceId;
            msg["sig"] = sig;
            try
            {
                PubSubAdmin.Publish(G.projectId, _gAuth.GetAccessToken(), DeviceCallsTopic(d.deviceId),
                    Encoding.UTF8.GetBytes(JsonUtil.Serialize(msg)), "tool.cancel", d.deviceId, pc.callId);
            }
            catch (Exception ex) { Log("Device call cancel via Pub/Sub failed: " + ex.Message); }
            if (pc.slotIndex >= 0 && pc.slotIndex < d.slotIds.Count)
            {
                try
                {
                    Dictionary<string, object> content = new Dictionary<string, object>();
                    content["state"] = "cancelled";
                    content["doneCallId"] = pc.callId;
                    content["cancelSig"] = sig;
                    DriveWrite.UpdateContent(_gAuth.GetAccessToken(), d.slotIds[pc.slotIndex], Encoding.UTF8.GetBytes(JsonUtil.Serialize(content)));
                }
                catch (Exception ex) { Log("Device call cancel via Drive failed: " + ex.Message); }
            }
        }

        private static bool HasSourcePath(string argsJson)
        {
            if (String.IsNullOrEmpty(argsJson)) return false;
            try
            {
                Dictionary<string, object> args = JsonUtil.DeserializeObject(argsJson) as Dictionary<string, object>;
                return args != null && args.ContainsKey("sourcePath");
            }
            catch (Exception) { return false; }
        }

        // Returns argsJson with sourcePath replaced by contentBase64, or null and the reason.
        private static string AttachSourceFile(JobDeviceState js, string argsJson, out string why)
        {
            why = null;
            Dictionary<string, object> args = JsonUtil.DeserializeObject(argsJson) as Dictionary<string, object>;
            string src = args == null ? null : DeviceWire.S(args, "sourcePath");
            if (String.IsNullOrEmpty(src))
            {
                why = "sourcePath is empty.";
                return null;
            }
            if (String.IsNullOrEmpty(js.projectDir))
            {
                why = "This job has no project folder.";
                return null;
            }
            string full;
            try { full = Path.GetFullPath(src); }
            catch (Exception ex) { why = "Bad sourcePath: " + ex.Message; return null; }
            string root = js.projectDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                why = "sourcePath must be inside this project's folder " + js.projectDir + ".";
                return null;
            }
            FileInfo fi = new FileInfo(full);
            if (!fi.Exists)
            {
                why = "File not found: " + full;
                return null;
            }
            // The file and every folder up to the project folder must be real (no junction or link leading outside).
            for (FileSystemInfo part = fi; part != null; part = part is FileInfo ? (FileSystemInfo)((FileInfo)part).Directory : ((DirectoryInfo)part).Parent)
            {
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    why = "sourcePath goes through a link or junction: " + part.FullName;
                    return null;
                }
                if (String.Equals(part.FullName.TrimEnd(Path.DirectorySeparatorChar), js.projectDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    break;
            }
            if (fi.Length > MaxSourceBytes)
            {
                why = "File is " + fi.Length + " bytes; the limit is " + MaxSourceBytes + ".";
                return null;
            }
            args.Remove("sourcePath");
            args.Remove("content");
            args["contentBase64"] = Convert.ToBase64String(File.ReadAllBytes(full));
            return JsonUtil.Serialize(args);
        }

        // v1.9.2: devices 0.9.8+ publish tool.received (attributes deviceId, callId) when they pick up a call from
        // either channel. Unsigned: it only stops the early "not picked up" failure and the extra Drive copy.
        private void MarkReceived(string deviceId, Dictionary<string, string> attributes)
        {
            string callId;
            PendingCall pc;
            if (attributes != null && attributes.TryGetValue("callId", out callId) && callId != null &&
                _pendingCalls.TryGetValue(callId, out pc) && pc.deviceId == deviceId)
                pc.received = true;
        }

        internal static bool VersionAtLeast(string version, int major, int minor, int build)
        {
            Version v;
            if (String.IsNullOrEmpty(version) || !Version.TryParse(version.Split('-', '+')[0], out v))
                return false;
            return v >= new Version(major, minor, build);
        }

        // Accepts a result only for a pending call, from the addressed device, with a valid device signature.
        private void RouteDeviceResult(byte[] data, string channel)
        {
            Dictionary<string, object> r = JsonUtil.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
            if (r == null)
                return;
            string callId = DeviceWire.S(r, "callId");
            PendingCall pc;
            if (!_pendingCalls.TryGetValue(callId, out pc))
                return;
            // A duplicate delivered through the second channel is answered with "replay"; the real result follows.
            Dictionary<string, object> err = r.ContainsKey("error") ? r["error"] as Dictionary<string, object> : null;
            if (!DeviceWire.B(r, "ok") && DeviceWire.S(err, "code") == "replay")
                return;
            PairedDevice d;
            if (!_pairedDevices.TryGetValue(pc.deviceId, out d) ||
                DeviceWire.S(r, "deviceId") != pc.deviceId || DeviceWire.S(r, "tool") != pc.tool || DeviceWire.S(r, "runId") != pc.runId ||
                !DeviceCrypto.Verify(d.signingKey, DeviceWire.ResultPayload(r), DeviceWire.S(r, "sig")))
            {
                Log("Rejected device result " + callId + " via " + channel + ": identity or signature mismatch.");
                return;
            }
            d.lastHeartbeatUtc = DateTime.UtcNow;
            pc.result = r;
            pc.done.Set();
        }

        private void DeviceResultsLoop()
        {
            int delay = 1000;
            while (!_exiting)
            {
                if (!GoogleReady() || String.IsNullOrWhiteSpace(G.projectId) || _pairedDevices.IsEmpty)
                {
                    Thread.Sleep(2000);
                    continue;
                }
                try
                {
                    PubSubLite ps = new PubSubLite(G.projectId, _gAuth.GetAccessToken());
                    List<PubSubInMessage> msgs = ps.Pull(DeviceResultsSubscription, 50, 90000);
                    List<string> acks = new List<string>();
                    foreach (PubSubInMessage m in msgs)
                    {
                        acks.Add(m.ackId);
                        string type;
                        m.attributes.TryGetValue("type", out type);
                        string dev;
                        m.attributes.TryGetValue("deviceId", out dev);
                        try
                        {
                            if (type == "tool.result")
                                RouteDeviceResult(m.data, "pubsub");
                            else if (type == "tool.received")
                                MarkReceived(dev, m.attributes);
                            else if (type == "device.heartbeat")
                                OnDeviceHeartbeat(dev, m.data); // v1.4.1: payload carries the connector state and version
                        }
                        catch (Exception ex)
                        {
                            Log("Device message: " + ex.Message);
                        }
                    }
                    ps.Acknowledge(DeviceResultsSubscription, acks);
                    delay = 1000;
                }
                catch (System.Net.WebException wex)
                {
                    // A held pull that times out on our side is an empty pull, not a failure.
                    if (wex.Status == System.Net.WebExceptionStatus.Timeout)
                        continue;
                    Log("Device results pull: " + wex.Message);
                    Backoff(ref delay);
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("404", StringComparison.Ordinal) < 0)
                        Log("Device results pull: " + ex.Message);
                    Backoff(ref delay);
                }
            }
        }

        private void OnDeviceHeartbeat(string deviceId, byte[] data)
        {
            PairedDevice d;
            if (deviceId == null || !_pairedDevices.TryGetValue(deviceId, out d) || d.IsWeb)
                return;
            // A heartbeat after a long gap: the events subscription may have expired (TTL 7 days).
            if ((DateTime.UtcNow - d.lastHeartbeatUtc).TotalHours > 6)
                d.resourcesEnsuredUtc = DateTime.MinValue;
            d.lastHeartbeatUtc = DateTime.UtcNow;
            d.lastPsHeartbeatUtc = d.lastHeartbeatUtc;
            // v1.4.1: the device is alive again, allow a future offline notification
            d.offlineNotifiedUtc = DateTime.MinValue;

            string state = "";
            string version = "";
            string num = "";
            string corpId = "";
            try
            {
                if (data != null && data.Length > 0)
                {
                    Dictionary<string, object> r = JsonUtil.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
                    if (r != null)
                    {
                        ReadCorpHeartbeat(r, out corpId, out state, out num);
                        version = (DeviceWire.S(r, "version") ?? "").Trim();
                    }
                }
            }
            catch
            {
                // v1.4.1: malformed heartbeat payload, ignore
                state = "";
                version = "";
                num = "";
            }

            if (version.Length > 0)
                d.version = version;
            if (corpId.Length > 0)
                d.corpId = corpId;

            // v1.5.2: a number challenge ("<id>:<NN>") during an automatic sign-in to the connector's portal. Push each
            // id once so the owner sees on the phone the number to choose in the authenticator. Done before the
            // state-change returns below, as the state stays "signin" across heartbeats while the number is waiting.
            PushSigninNumber(d, num);

            // v1.4.1: missing/empty/unknown value (old device) leaves corpState unchanged
            if (state != "online" && state != "signin" && state != "offline" && state != "unknown" && state != "off")
                return;

            string old = d.corpState ?? "";
            if (old == state)
                return;

            d.corpState = state;
            d.corpChangedUtc = DateTime.UtcNow;
            Log("Device " + d.shortName + ": " + Gateway.Title + " " + (old.Length > 0 ? old : "(none)") + " -> " + state);

            if (state == "signin" && (DateTime.UtcNow - d.corpNotifiedUtc).TotalHours > 3)
            {
                d.corpNotifiedUtc = DateTime.UtcNow;
                string label = !String.IsNullOrEmpty(d.name) ? d.name : d.shortName;
                string[] t = SigninNeededText(label);
                Notify(OwnerPushEnv(), t[0], t[1], "/", "gateway-signin");
            }
        }

        // Freedom F1 5b: corp {id, state, num} of the heartbeat; older devices send the state and number in legacy fields.
        internal static void ReadCorpHeartbeat(Dictionary<string, object> r, out string id, out string state, out string num)
        {
            Dictionary<string, object> corp = r != null && r.ContainsKey("corp") ? r["corp"] as Dictionary<string, object> : null;
            id = corp != null ? DeviceWire.S(corp, "id").Trim() : "";
            state = (corp != null ? DeviceWire.S(corp, "state") : "").Trim().ToLowerInvariant();
            num = (corp != null ? DeviceWire.S(corp, "num") : "").Trim();
            if (state.Length == 0)
                state = DeviceWire.S(r, "engie").Trim().ToLowerInvariant();
            if (num.Length == 0)
                num = DeviceWire.S(r, "engieNum").Trim();
        }

        // Push texts from the gateway module: [title, body].
        internal static string[] SigninNeededText(string label)
        {
            string how = Gateway.SigninTitle.Length > 0 ? " (" + Gateway.SigninTitle + ")" : "";
            return new string[] {
                Gateway.PortalTitle + ": sign-in needed",
                Gateway.PortalTitle + " on " + label + " needs a sign-in" + how + ". " + Gateway.Title +
                    " models are unavailable to agents until then." };
        }

        internal static string[] SigninNumberText(string label, string number)
        {
            string app = Gateway.SigninTitle.Length > 0 ? Gateway.SigninTitle : "the authenticator app";
            return new string[] {
                Gateway.PortalTitle + ": tap " + number + " in " + app,
                "Signing in to " + Gateway.PortalTitle + " on " + label + ". Open " + app + " and choose the number " + number + "." };
        }

        // v1.5.2: push the sign-in number to the owner's phone, each number id at most once.
        private void PushSigninNumber(PairedDevice d, string num)
        {
            try
            {
                int colon = num.IndexOf(':');
                if (colon <= 0 || colon >= num.Length - 1)
                    return;
                string id = num.Substring(0, colon);
                string number = num.Substring(colon + 1).Trim();
                if (number.Length == 0 || number.Length > 8)
                    return;
                if (d.corpNumId == id)
                    return;   // already pushed this challenge
                d.corpNumId = id;
                string label = !String.IsNullOrEmpty(d.name) ? d.name : d.shortName;
                string[] t = SigninNumberText(label, number);
                Notify(OwnerPushEnv(), t[0], t[1], "/", "gateway-signin-number");
            }
            catch (Exception ex)
            {
                Log("PushSigninNumber failed: " + ex.Message);
            }
        }

        // v1.4.1: environment that receives owner pushes
        private string OwnerPushEnv()
        {
            return OwnerEnvOf(_config);
        }

        // v1.4.1: notify the owner once when a device that served the gateway models stops sending heartbeats
        private void CheckDeviceHeartbeats()
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                foreach (PairedDevice d in _pairedDevices.Values)
                {
                    if (d == null || d.revoked || d.lastHeartbeatUtc == DateTime.MinValue)
                        continue;
                    if ((now - d.lastHeartbeatUtc).TotalMinutes <= 15 || d.offlineNotifiedUtc != DateTime.MinValue)
                        continue;
                    if (d.corpState != "online" && d.corpState != "signin")
                        continue;

                    d.offlineNotifiedUtc = now;
                    string label = !String.IsNullOrEmpty(d.name) ? d.name : d.shortName;
                    Log("Device " + d.shortName + ": no heartbeat for 15 min, offline notification sent");
                    Notify(OwnerPushEnv(),
                        "CO Desktop offline",
                        "No heartbeat from " + label + " for 15 min: CO Desktop is not running or offline, " + Gateway.Title + " models are unavailable.",
                        "/",
                        "device-offline-" + d.deviceId);
                }
            }
            catch (Exception ex)
            {
                Log("CheckDeviceHeartbeats failed: " + ex.Message);
            }
        }

        // v1.4.1: paired devices for owner.json (CO Web without CO Desktop reads online + corp / the legacy state field)
        private List<OwnerDeviceInfo> BuildOwnerDevices()
        {
            List<OwnerDeviceInfo> list = new List<OwnerDeviceInfo>();
            try
            {
                DateTime now = DateTime.UtcNow;
                foreach (PairedDevice d in _pairedDevices.Values)
                {
                    if (d == null || d.revoked || d.IsWeb || String.IsNullOrEmpty(d.serviceAccount))
                        continue;

                    bool hasHb = d.lastHeartbeatUtc != DateTime.MinValue;
                    OwnerDeviceInfo info = new OwnerDeviceInfo();
                    info.deviceId = d.deviceId;
                    info.name = !String.IsNullOrEmpty(d.name) ? d.name : d.shortName;
                    info.shortName = d.shortName;
                    info.@short = d.shortName;
                    info.kind = "desktop";
                    info.environmentId = d.environmentId;
                    info.version = d.version ?? "";
                    info.lastHeartbeatUtc = hasHb ? d.lastHeartbeatUtc.ToString("o") : null;
                    info.online = hasHb && (now - d.lastHeartbeatUtc).TotalSeconds <= 120;
                    info.engie = d.corpState ?? "";
                    if (!String.IsNullOrEmpty(d.corpState))
                    {
                        info.corp = new OwnerDeviceCorp();
                        info.corp.id = d.corpId ?? "";
                        info.corp.state = d.corpState;
                    }
                    list.Add(info);
                }
                AddWebOwnerDevices(list);   // v1.5.0

                list.Sort(delegate(OwnerDeviceInfo a, OwnerDeviceInfo b)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(a.name ?? "", b.name ?? "");
                });
            }
            catch (Exception ex)
            {
                Log("BuildOwnerDevices failed: " + ex.Message);
            }
            return list;
        }

        // ---------------------------------------------------------------- Drive fallback (TZ §4.2)

        private bool SendViaDrive(PairedDevice d, PendingCall pc, Dictionary<string, object> call)
        {
            if (!D.driveFallback || !GoogleReady() || d.slotIds.Count == 0)
                return false;
            string[] owners = _slotOwners.GetOrAdd(d.deviceId, delegate(string k) { return new string[d.slotIds.Count]; });
            int slot = -1;
            lock (owners)
            {
                for (int i = 0; i < owners.Length; i++)
                {
                    if (owners[i] == null || SlotCooledDown(owners[i]))
                    {
                        owners[i] = pc.callId;
                        slot = i;
                        break;
                    }
                }
            }
            if (slot < 0)
                return false;
            pc.slotIndex = slot;
            try
            {
                Dictionary<string, object> content = new Dictionary<string, object>();
                content["state"] = "call";
                content["call"] = call;
                DriveWrite.UpdateContent(_gAuth.GetAccessToken(), d.slotIds[slot], Encoding.UTF8.GetBytes(JsonUtil.Serialize(content)));
                return true;
            }
            catch (Exception ex)
            {
                Log("Device call via Drive failed: " + ex.Message);
                ReleaseSlot(pc);
                return false;
            }
        }

        // A slot is reused 10 minutes after its call finished: by then the device has either handled the
        // copy or rejects it as expired (5 min), so a late "done" cannot overwrite a newer call.
        private void ReleaseSlot(PendingCall pc)
        {
            if (pc.slotIndex < 0)
                return;
            string[] owners;
            if (_slotOwners.TryGetValue(pc.deviceId, out owners))
                lock (owners)
                    if (owners[pc.slotIndex] == pc.callId)
                        owners[pc.slotIndex] = "cooldown:" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            pc.slotIndex = -1;
        }

        private static bool SlotCooledDown(string owner)
        {
            long ticks;
            return owner.StartsWith("cooldown:", StringComparison.Ordinal) &&
                   Int64.TryParse(owner.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) &&
                   (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes >= 10;
        }

        // Polls results/ of devices that have calls in flight through Drive (every 2 s).
        private void DeviceDriveLoop()
        {
            while (!_exiting)
            {
                Thread.Sleep(2000);
                try
                {
                    List<PendingCall> viaDrive = _pendingCalls.Values.Where(delegate(PendingCall p) { return p.slotIndex >= 0; }).ToList();
                    if (viaDrive.Count == 0 || !GoogleReady())
                        continue;
                    string token = _gAuth.GetAccessToken();
                    foreach (string deviceId in viaDrive.Select(delegate(PendingCall p) { return p.deviceId; }).Distinct())
                    {
                        PairedDevice d;
                        if (!_pairedDevices.TryGetValue(deviceId, out d) || String.IsNullOrEmpty(d.resultsFolderId))
                            continue;
                        foreach (Dictionary<string, object> f in DriveWrite.ListWithProperties(token, d.resultsFolderId))
                        {
                            Dictionary<string, object> props = f.ContainsKey("appProperties") ? f["appProperties"] as Dictionary<string, object> : null;
                            if (props != null && DeviceWire.S(props, "consumed") == "true")
                                continue;
                            string id = DeviceWire.S(f, "id");
                            RouteDeviceResult(new DriveLite(token).Download(id), "drive");
                            DriveWrite.SetAppProperty(token, id, "consumed", "true");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Device Drive poll: " + ex.Message);
                }
            }
        }

        // ---------------------------------------------------------------- local pipe for proxies

        private void DevicePipeLoop()
        {
            while (!_exiting)
            {
                NamedPipeServerStream server = null;
                try
                {
#if NET8_0_OR_GREATER
                    // .NET 8 (Freedom F3a): the ACL overload lives in NamedPipeServerStreamAcl (Windows); on macOS/Linux
                    // the pipe is a Unix socket, CurrentUserOnly makes it owner-only and checks the peer's uid.
                    server = OperatingSystem.IsWindows()
                        ? NamedPipeServerStreamAcl.Create(DevicePipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, BuildPipeSecurity())
                        : new NamedPipeServerStream(DevicePipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
#else
                    server = new NamedPipeServerStream(DevicePipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, BuildPipeSecurity());
#endif
                    server.WaitForConnection();
                    NamedPipeServerStream connected = server;
                    server = null;
                    Thread t = new Thread(delegate() { ServeProxy(connected); });
                    t.IsBackground = true;
                    t.Name = "device-proxy-conn";
                    t.Start();
                }
                catch (Exception ex)
                {
                    Log("Device pipe: " + ex.Message);
                    if (server != null)
                        server.Dispose();
                    Thread.Sleep(1000);
                }
            }
        }

        // Only this Windows user and the configured runAs user may connect; remote clients are denied.
        private PipeSecurity BuildPipeSecurity()
        {
            PipeSecurity ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            if (_config.runAs != null && !String.IsNullOrWhiteSpace(_config.runAs.user))
            {
                string account = (String.IsNullOrEmpty(_config.runAs.domain) || _config.runAs.domain == "." ? Environment.MachineName : _config.runAs.domain) + "\\" + _config.runAs.user;
                ps.AddAccessRule(new PipeAccessRule(new NTAccount(account), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        private void ServeProxy(NamedPipeServerStream pipe)
        {
            // Runs on its own thread: any exception here, including one from closing a pipe the proxy already
            // dropped (Claude gave up on a slow device call), must not reach the runtime and end the Bridge process.
            StreamReader reader = null;
            StreamWriter writer = null;
            try
            {
                reader = new StreamReader(pipe, new UTF8Encoding(false));
                writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.AutoFlush = true;
                string jobId = null, deviceId = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Dictionary<string, object> msg = JsonUtil.DeserializeObject(line) as Dictionary<string, object>;
                    string op = DeviceWire.S(msg, "op");
                    Dictionary<string, object> reply;
                    if (op == "hello")
                    {
                        jobId = DeviceWire.S(msg, "jobId");
                        deviceId = DeviceWire.S(msg, "deviceId");
                        reply = Hello(jobId, deviceId);
                    }
                    else if (op == "call" && jobId != null)
                    {
                        reply = ExecuteDeviceCall(jobId, deviceId, DeviceWire.S(msg, "tool"), DeviceWire.S(msg, "argsJson"));
                    }
                    else
                    {
                        reply = Fail("bad_request", "Unknown operation.");
                    }
                    writer.WriteLine(JsonUtil.Serialize(reply));
                }
            }
            catch (IOException)
            {
                // proxy exited
            }
            catch (Exception ex)
            {
                Log("Device proxy session: " + ex.Message);
            }
            finally
            {
                try { if (writer != null) writer.Dispose(); } catch (Exception) { }
                try { if (reader != null) reader.Dispose(); } catch (Exception) { }
                try { pipe.Dispose(); } catch (Exception) { }
            }
        }

        private Dictionary<string, object> Hello(string jobId, string deviceId)
        {
            JobDeviceState js;
            PairedDevice d;
            if (!_deviceJobs.TryGetValue(jobId ?? "", out js) || !js.devices.Contains(deviceId ?? "") ||
                !_pairedDevices.TryGetValue(deviceId, out d) || d.revoked)
                return Fail("not_allowed", "Job " + jobId + " has no device " + deviceId + ".");
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true;
            r["short"] = d.shortName;
            r["name"] = d.name;
            object tools = d.liveTools ?? d.tools ?? new object[0];
            // v1.3.5: tools the job's request denies (connector off) and tools of disabled modules (F10) are not listed
            System.Collections.IEnumerable all = tools as System.Collections.IEnumerable;
            if (all != null)
            {
                List<object> kept = new List<object>();
                foreach (object t in all)
                {
                    Dictionary<string, object> td = t as Dictionary<string, object>;
                    object name;
                    string ignored;
                    if (td != null && td.TryGetValue("name", out name) && name is string && (js.denyTools.Contains((string)name)
                        || Modules.Refusal((string)name, js.environmentId, false, out ignored) != null))
                        continue;
                    kept.Add(t);
                }
                tools = kept;
            }
            r["tools"] = tools;
            return r;
        }
    }

    // ============================================================= web devices (v1.5.0, lab/web-device/SPEC.md)
    // A CO Web tab registers itself as a file device in the database (db.php webdev_*). Bridge merges
    // webdev_list into the device registry as kind=web; calls go through webdev_call_put / webdev_call_get with
    // the push key. No signature: trust comes from HTTPS plus the browser session (server) and the push key.

    internal sealed partial class BridgeApplicationContext
    {
        private Thread _webDevicesThread;
        private DateTime _webListedUtc = DateTime.MinValue;
        private readonly object _webListLock = new object();
        private static readonly Regex WebIdRx = new Regex(@"\Aw-[0-9a-f]{16}\z", RegexOptions.Compiled);
        private static readonly Regex WebShortRx = new Regex(@"\A[a-z0-9]{1,12}\z", RegexOptions.Compiled);
        private static readonly Regex WebToolRx = new Regex(@"\A[a-zA-Z0-9_-]{1,64}\z", RegexOptions.Compiled);

        private static bool IsWebDeviceId(string id) { return id != null && WebIdRx.IsMatch(id); }

        private void StartWebDevices()
        {
            if (!DbqEnabled) { Log("Web devices off: database queue disabled."); return; }
            _webDevicesThread = StartThread("web-devices", WebDevicesLoop);
        }

        // Every 30 s; also on demand before a job that names a w- device is validated.
        private void WebDevicesLoop()
        {
            while (!_exiting)
            {
                try { RefreshWebDevices(); }
                catch (Exception ex) { LogThrottled("webdev-list", "Web devices: webdev_list failed: " + ex.Message); }
                for (int i = 0; i < 120 && !_exiting; i++) Thread.Sleep(250);
            }
        }

        private void RefreshWebDevices()
        {
            lock (_webListLock)
            {
                Dictionary<string, object> resp = DbqCall("webdev_list", new Dictionary<string, object>(), false);
                HashSet<string> seen = new HashSet<string>(), shorts = new HashSet<string>();
                foreach (object o in DbqArray(resp, "devices"))
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string id = DeviceWire.S(d, "deviceId"), sh = DeviceWire.S(d, "short");
                    string env = DeviceWire.S(d, "environmentId"), reason = null;
                    List<object> tools = new List<object>();
                    if (!IsWebDeviceId(id)) reason = "invalid device id";
                    else if (sh == null || !WebShortRx.IsMatch(sh)) reason = "invalid short name";
                    else if (!IsValidEnvironment(env)) reason = "invalid environment";
                    else if (shorts.Contains(sh)) reason = "duplicate short name " + sh;
                    else
                    {
                        object t;
                        if (d.TryGetValue("tools", out t) && t != null)
                            foreach (object to in GHttp.Items(t))
                            {
                                Dictionary<string, object> td = to as Dictionary<string, object>;
                                if (td == null) { reason = "invalid tool entry"; break; }
                                string tn = DeviceWire.S(td, "name"), desc = DeviceWire.S(td, "description");
                                if (tn == null || !WebToolRx.IsMatch(tn)) { reason = "invalid tool name"; break; }
                                if (desc != null && desc.Length > 2000) { reason = "tool description too long"; break; }
                                if (tn == "approve" || tn == "ask_user") continue;   // prompts stay on CO Desktop
                                tools.Add(td);
                            }
                    }
                    if (reason != null) { LogThrottled("webdev-bad-" + id, "Web device " + id + " ignored: " + reason + "."); continue; }
                    shorts.Add(sh);
                    bool created = false;
                    PairedDevice existing = _pairedDevices.GetOrAdd(id, delegate(string k) { created = true; PairedDevice n = new PairedDevice(); n.deviceId = k; n.kind = "web"; return n; });
                    if (!existing.IsWeb) continue;   // never touch a CO Desktop record
                    seen.Add(id);
                    existing.shortName = sh;
                    existing.name = DeviceWire.S(d, "name");
                    existing.environmentId = env;
                    existing.userEmail = DeviceWire.S(d, "userEmail") ?? "";
                    existing.tools = tools;
                    existing.liveTools = null;
                    existing.webOnline = DeviceWire.B(d, "online");
                    existing.webLastSeenUtc = DeviceWire.S(d, "lastSeenUtc") ?? "";
                    existing.webListedUtc = DateTime.UtcNow;
                    if (created || existing.revoked)
                        Log("Web device " + id + " (" + existing.name + ", " + existing.userEmail + ") registered.");
                    existing.revoked = false;
                }
                foreach (KeyValuePair<string, PairedDevice> kv in _pairedDevices)
                {
                    PairedDevice p = kv.Value;
                    if (p == null || !p.IsWeb || p.revoked || seen.Contains(kv.Key)) continue;
                    p.revoked = true;
                    p.webOnline = false;
                    Log("Web device " + kv.Key + " removed or revoked.");
                }
                _webListedUtc = DateTime.UtcNow;
            }
        }

        private void EnsureWebDevicesFresh(int maxAgeSeconds)
        {
            if (DbqEnabled && (DateTime.UtcNow - _webListedUtc).TotalSeconds > maxAgeSeconds)
            {
                try { RefreshWebDevices(); }
                catch (Exception ex) { Log("Web devices: webdev_list failed: " + ex.Message); }
            }
        }

        // The server's online flag (last_seen < 60 s) counts only while the list itself is younger than 60 s.
        private bool WebDeviceOnline(PairedDevice d)
        {
            return d != null && !d.revoked && d.webOnline && (DateTime.UtcNow - d.webListedUtc).TotalSeconds < 60;
        }

        // Called by ExecuteDeviceCall after the selection, deny-list and call-limit checks.
        private Dictionary<string, object> ExecuteWebDeviceCall(JobDeviceState js, PairedDevice d, string tool, string argsJson)
        {
            if (!WebDeviceOnline(d))
            {
                EnsureWebDevicesFresh(5);
                if (!WebDeviceOnline(d))
                    return Fail("device_unreachable", "Open CO in a browser tab on that computer (web device " + d.shortName + " is offline).");
            }
            string args = String.IsNullOrEmpty(argsJson) ? "{}" : argsJson;
            if (Encoding.UTF8.GetByteCount(args) > 1048576) return Fail("too_large", "Tool arguments exceed 1 MB.");
            // The browser asks the user to confirm writes, deletes and moves for up to 120 s before it executes them.
            int timeoutSec = Math.Max(10, D.callTimeoutSeconds);
            if (tool == "fs_write" || tool == "fs_edit" || tool == "fs_mkdir" || tool == "fs_delete" || tool == "fs_move") timeoutSec += 120;
            string callId = "call-" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> call = new Dictionary<string, object>();
            call["v"] = 1; call["callId"] = callId; call["runId"] = js.jobId; call["taskId"] = js.taskId;
            call["deviceId"] = d.deviceId; call["tool"] = tool; call["argsJson"] = args; call["issuedUtc"] = DeviceWire.Now();
            call["tainted"] = js.tainted; call["timeoutMs"] = timeoutSec * 1000;
            Dictionary<string, object> putBody = new Dictionary<string, object> { { "call", call } };
            try
            {
                try { DbqCall("webdev_call_put", putBody, true); }
                catch (InvalidOperationException gzEx)
                {
                    // A server that does not take this op gzip-wrapped answers gz_ops_only: send it plain.
                    if (gzEx.Message.IndexOf("gz_ops_only", StringComparison.Ordinal) < 0) throw;
                    DbqCall("webdev_call_put", putBody, false);
                }
            }
            catch (Exception ex) { return Fail("device_unreachable", "Could not queue the call for the web device: " + ex.Message); }

            string timeoutMsg = "The web device did not answer within " + timeoutSec + " s.";
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec + 15);
            while (DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < 7; i++)
                {
                    if (_exiting) return Fail("bridge_exiting", "Bridge is shutting down.");
                    Thread.Sleep(100);
                }
                Dictionary<string, object> resp;
                try { resp = DbqCall("webdev_call_get", new Dictionary<string, object> { { "callIds", new object[] { callId } } }, false); }
                catch (Exception ex) { LogThrottled("webdev-get", "Web device poll failed: " + ex.Message); continue; }
                Dictionary<string, object> entry = null;
                foreach (object o in DbqArray(resp, "results"))
                {
                    Dictionary<string, object> e = o as Dictionary<string, object>;
                    if (e != null && DeviceWire.S(e, "callId") == callId) { entry = e; break; }
                }
                if (entry == null) continue;
                string state = DeviceWire.S(entry, "state");
                if (state == "expired") return Fail("device_timeout", timeoutMsg);
                if (state != "done") continue;
                object raw;
                entry.TryGetValue("result", out raw);
                Dictionary<string, object> r = raw as Dictionary<string, object>;
                if (r == null && raw is string)
                {
                    try { r = JsonUtil.DeserializeObject((string)raw) as Dictionary<string, object>; }
                    catch (Exception) { r = null; }
                }
                if (r == null || DeviceWire.S(r, "callId") != callId || DeviceWire.S(r, "deviceId") != d.deviceId
                    || DeviceWire.S(r, "tool") != tool || DeviceWire.S(r, "runId") != js.jobId)
                {
                    Log("Rejected web device result " + callId + ": identity mismatch.");
                    return Fail("bad_result", "The web device returned a mismatched result.");
                }
                bool untrusted = DeviceWire.B(r, "untrusted");
                if (untrusted) js.tainted = true;     // TZ §9.4, same as desktop devices
                string content = DeviceWire.S(r, "content") ?? "";
                if (content.Length > 1048576) return Fail("too_large", "The web device result exceeds 1 MB.");
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["ok"] = DeviceWire.B(r, "ok"); reply["content"] = content; reply["untrusted"] = untrusted;
                object err;
                if (r.TryGetValue("error", out err) && err != null) reply["error"] = err;
                return reply;
            }
            return Fail("device_timeout", timeoutMsg);
        }

        private void AddWebOwnerDevices(List<OwnerDeviceInfo> list)
        {
            foreach (PairedDevice d in _pairedDevices.Values)
            {
                if (d == null || !d.IsWeb || d.revoked) continue;
                OwnerDeviceInfo info = new OwnerDeviceInfo();
                info.deviceId = d.deviceId;
                info.name = String.IsNullOrEmpty(d.name) ? d.shortName : d.name;
                info.shortName = d.shortName;
                info.@short = d.shortName;
                info.kind = "web";
                info.userEmail = d.userEmail ?? "";
                info.environmentId = d.environmentId;
                info.version = "";
                info.lastHeartbeatUtc = String.IsNullOrEmpty(d.webLastSeenUtc) ? null : d.webLastSeenUtc;
                info.online = WebDeviceOnline(d);
                info.engie = "";
                list.Add(info);
            }
        }
    }

    // cmdkey and the Credential Manager UI store generic passwords as UTF-16LE.
    internal static class RunAsCredential
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public static string ReadPassword(string target)
        {
            IntPtr p;
            if (!CredRead(target, 1, 0, out p))                       // CRED_TYPE_GENERIC
            {
                if (!CredRead(target, 2, 0, out p))                   // CRED_TYPE_DOMAIN_PASSWORD (cmdkey /add)
                    return null;
            }
            try
            {
                CREDENTIAL c = (CREDENTIAL)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                byte[] b = new byte[c.CredentialBlobSize];
                System.Runtime.InteropServices.Marshal.Copy(c.CredentialBlob, b, 0, b.Length);
                return Encoding.Unicode.GetString(b).TrimEnd('\0');
            }
            finally
            {
                CredFree(p);
            }
        }
    }

    // ============================================================= REST helpers not in GoogleTransport

    internal static class DriveWrite
    {
        public static void UpdateContent(string token, string fileId, byte[] content)
        {
            GResponse r = GHttp.Send("PATCH", "https://www.googleapis.com/upload/drive/v3/files/" + Uri.EscapeDataString(fileId) +
                "?uploadType=media&supportsAllDrives=true", token, "application/json", content, 60000);
            GHttp.Ensure(r, "Drive update");
        }

        // Resumable upload (simple uploads are limited to 5 MB): one session, one PUT of the whole file.
        public static void UpdateContentResumable(string token, string fileId, string path)
        {
            HttpWebRequest init = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files/" +
                Uri.EscapeDataString(fileId) + "?uploadType=resumable&supportsAllDrives=true");
            init.Method = "PATCH";
            init.Timeout = 60000;
            init.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            init.Headers["X-Upload-Content-Type"] = "application/octet-stream";
            init.ContentType = "application/json; charset=UTF-8";
            byte[] empty = Encoding.UTF8.GetBytes("{}");
            init.ContentLength = empty.Length;
            using (Stream s = init.GetRequestStream())
                s.Write(empty, 0, empty.Length);
            string session;
            using (HttpWebResponse resp = (HttpWebResponse)init.GetResponse())
                session = resp.Headers["Location"];
            if (String.IsNullOrEmpty(session))
                throw new InvalidOperationException("Drive resumable upload: no session URL.");

            HttpWebRequest put = (HttpWebRequest)WebRequest.Create(session);
            put.Method = "PUT";
            put.Timeout = 30 * 60000;
            put.ReadWriteTimeout = 30 * 60000;
            put.ContentType = "application/octet-stream";
            put.AllowWriteStreamBuffering = false;
            using (FileStream fs = File.OpenRead(path))
            {
                put.ContentLength = fs.Length;
                using (Stream s = put.GetRequestStream())
                    fs.CopyTo(s, 1 << 20);
            }
            using (HttpWebResponse resp = (HttpWebResponse)put.GetResponse())
            {
                int st = (int)resp.StatusCode;
                if (st < 200 || st >= 300)
                    throw new InvalidOperationException("Drive resumable upload: HTTP " + st);
            }
        }

        public static void SetAppProperty(string token, string fileId, string key, string value)
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props[key] = value;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["appProperties"] = props;
            GResponse r = GHttp.Json("PATCH", "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) +
                "?supportsAllDrives=true", token, body, 30000);
            GHttp.Ensure(r, "Drive properties");
        }

        public static List<Dictionary<string, object>> ListWithProperties(string token, string parentId)
        {
            string q = "'" + parentId.Replace("'", "\\'") + "' in parents and trashed=false";
            GResponse r = GHttp.Send("GET", "https://www.googleapis.com/drive/v3/files?q=" + Uri.EscapeDataString(q) +
                "&pageSize=100&fields=" + Uri.EscapeDataString("files(id,name,appProperties)") +
                "&supportsAllDrives=true&includeItemsFromAllDrives=true", token, null, null, 30000);
            GHttp.Ensure(r, "Drive list");
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, object> j = GHttp.Parse(r);
            if (j.ContainsKey("files"))
                foreach (object o in GHttp.Items(j["files"]))
                    if (o is Dictionary<string, object>)
                        list.Add((Dictionary<string, object>)o);
            return list;
        }
    }

    internal static class PubSubAdmin
    {
        private const string Api = "https://pubsub.googleapis.com/v1/projects/";

        public static void Publish(string project, string token, string topic, byte[] data, string type, string deviceId, string callId)
        {
            Dictionary<string, object> attrs = new Dictionary<string, object>();
            attrs["type"] = type;
            attrs["deviceId"] = deviceId;
            attrs["callId"] = callId;
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["data"] = Convert.ToBase64String(data);
            msg["attributes"] = attrs;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["messages"] = new object[] { msg };
            GResponse r = GHttp.Json("POST", Api + project + "/topics/" + topic + ":publish", token, body, 30000);
            GHttp.Ensure(r, "publish " + topic);
        }

        public static void DeleteTopic(string project, string token, string topic)
        {
            GResponse r = GHttp.Send("DELETE", Api + project + "/topics/" + topic, token, null, null, 30000);
            if (r.status != 404)
                GHttp.Ensure(r, "delete topic " + topic);
        }

        public static void DeleteSubscription(string project, string token, string sub)
        {
            GResponse r = GHttp.Send("DELETE", Api + project + "/subscriptions/" + sub, token, null, null, 30000);
            if (r.status != 404)
                GHttp.Ensure(r, "delete subscription " + sub);
        }

        // Read-modify-write of the resource policy; etag protects against concurrent edits.
        public static void AddBinding(string project, string token, string resource, string role, string member)
        {
            string url = Api + project + "/" + resource;
            GResponse g = GHttp.Send("GET", url + ":getIamPolicy", token, null, null, 30000);
            GHttp.Ensure(g, "getIamPolicy " + resource);
            Dictionary<string, object> policy = GHttp.Parse(g);
            List<object> bindings = GHttp.Items(policy.ContainsKey("bindings") ? policy["bindings"] : null).ToList();
            Dictionary<string, object> target = null;
            foreach (object o in bindings)
            {
                Dictionary<string, object> b = o as Dictionary<string, object>;
                if (b != null && DeviceWire.S(b, "role") == role)
                    target = b;
            }
            if (target == null)
            {
                target = new Dictionary<string, object>();
                target["role"] = role;
                target["members"] = new object[0];
                bindings.Add(target);
            }
            List<object> members = GHttp.Items(target["members"]).ToList();
            if (members.Any(delegate(object m) { return String.Equals(Convert.ToString(m), member, StringComparison.OrdinalIgnoreCase); }))
                return;
            members.Add(member);
            target["members"] = members.ToArray();
            policy["bindings"] = bindings.ToArray();
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["policy"] = policy;
            GResponse s = GHttp.Json("POST", url + ":setIamPolicy", token, body, 30000);
            GHttp.Ensure(s, "setIamPolicy " + resource);
        }
    }

    // ============================================================= MCP stdio proxy (runs as a separate process)

    internal static class McpDeviceProxy
    {
        public static bool TryRun(string[] argv)
        {
            int i = Array.IndexOf(argv, "--mcp-device");
            if (i < 0)
                return false;
            try
            {
                new ProxySession(Arg(argv, "--mcp-device"), Arg(argv, "--job"), Arg(argv, "--pipe")).Run();
            }
            catch (Exception ex)
            {
                BootstrapLog.Write("mcp-device proxy: " + ex);
            }
            return true;
        }

        private static string Arg(string[] argv, string name)
        {
            int i = Array.IndexOf(argv, name);
            return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
        }
    }

    // One JSON-RPC session over stdio. Each tools/call runs on its own thread with its own pipe
    // connection, so a call waiting for the user (approve, ask_user) does not block the others.
    internal sealed class ProxySession
    {
        private readonly string _deviceId, _jobId, _pipeName;
        private readonly object _writeLock = new object();
        private StreamWriter _out;

        public ProxySession(string deviceId, string jobId, string pipeName)
        {
            _deviceId = deviceId;
            _jobId = jobId;
            _pipeName = pipeName;
        }

        // Opens a pipe connection, sends hello and optionally one call; returns null when Bridge is gone.
        private Dictionary<string, object> Exchange(Dictionary<string, object> call, out Dictionary<string, object> hello)
        {
            hello = null;
            try
            {
#if NET8_0_OR_GREATER
                // .NET 8 on macOS/Linux: the server is CurrentUserOnly; the client checks the server's owner too.
                using (NamedPipeClientStream pipe = OperatingSystem.IsWindows()
                    ? new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut)
                    : new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly))
#else
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
#endif
                {
                    pipe.Connect(10000);
                    StreamReader pr = new StreamReader(pipe, new UTF8Encoding(false));
                    StreamWriter pw = new StreamWriter(pipe, new UTF8Encoding(false));
                    pw.AutoFlush = true;

                    Dictionary<string, object> h = new Dictionary<string, object>();
                    h["op"] = "hello";
                    h["jobId"] = _jobId;
                    h["deviceId"] = _deviceId;
                    pw.WriteLine(JsonUtil.Serialize(h));
                    string line = pr.ReadLine();
                    if (line == null)
                        return null;
                    hello = JsonUtil.DeserializeObject(line) as Dictionary<string, object>;
                    if (call == null)
                        return hello;

                    pw.WriteLine(JsonUtil.Serialize(call));
                    line = pr.ReadLine();
                    return line == null ? null : JsonUtil.DeserializeObject(line) as Dictionary<string, object>;
                }
            }
            catch (IOException) { return null; }
            catch (TimeoutException) { return null; }
        }

        private void Send(object id, Dictionary<string, object> result, Dictionary<string, object> error)
        {
            Dictionary<string, object> resp = new Dictionary<string, object>();
            resp["jsonrpc"] = "2.0";
            resp["id"] = id;
            if (error != null)
                resp["error"] = error;
            else
                resp["result"] = result;
            string text = JsonUtil.Serialize(resp);
            lock (_writeLock)
            {
                _out.Write(text);
                _out.Write("\n");
                _out.Flush();
            }
        }

        public void Run()
        {
            StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));

            Dictionary<string, object> hello;
            Dictionary<string, object> info = Exchange(null, out hello);
            bool ok = info != null && DeviceWire.B(info, "ok");
            string serverName = "dev_" + (ok ? DeviceWire.S(info, "short") : "unavailable");
            string problem = info == null ? "Bridge is not running."
                : DeviceWire.S(info.ContainsKey("error") ? info["error"] as Dictionary<string, object> : null, "message");

            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0)
                    continue;
                Dictionary<string, object> req;
                try { req = JsonUtil.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (req == null)
                    continue;

                object id;
                if (!req.TryGetValue("id", out id))
                    continue;                  // notifications (initialized, cancelled) need no reply
                string method = DeviceWire.S(req, "method");
                Dictionary<string, object> prm = req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null;

                if (method == "initialize")
                {
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    string pv = DeviceWire.S(prm, "protocolVersion");
                    result["protocolVersion"] = String.IsNullOrEmpty(pv) ? "2025-06-18" : pv;
                    Dictionary<string, object> caps = new Dictionary<string, object>();
                    caps["tools"] = new Dictionary<string, object>();
                    result["capabilities"] = caps;
                    Dictionary<string, object> si = new Dictionary<string, object>();
                    si["name"] = serverName;
                    si["version"] = "1.1.0";
                    result["serverInfo"] = si;
                    if (!ok)
                        result["instructions"] = "Device unavailable: " + problem;
                    Send(id, result, null);
                }
                else if (method == "ping")
                {
                    Send(id, new Dictionary<string, object>(), null);
                }
                else if (method == "tools/list")
                {
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    result["tools"] = ok && info.ContainsKey("tools") ? info["tools"] : new object[0];
                    Send(id, result, null);
                }
                else if (method == "tools/call")
                {
                    Dictionary<string, object> call = new Dictionary<string, object>();
                    call["op"] = "call";
                    call["tool"] = DeviceWire.S(prm, "name");
                    call["argsJson"] = prm != null && prm.ContainsKey("arguments") && prm["arguments"] != null
                        ? JsonUtil.Serialize(prm["arguments"]) : "{}";
                    object callIdRef = id;
                    Thread t = new Thread(delegate() { CallAndReply(callIdRef, call); });
                    t.IsBackground = true;
                    t.Start();
                }
                else
                {
                    Dictionary<string, object> error = new Dictionary<string, object>();
                    error["code"] = -32601;
                    error["message"] = "Method not found: " + method;
                    Send(id, null, error);
                }
            }
        }

        private void CallAndReply(object id, Dictionary<string, object> call)
        {
            Dictionary<string, object> hello;
            Dictionary<string, object> reply = Exchange(call, out hello);
            bool success = reply != null && DeviceWire.B(reply, "ok");
            string text;
            if (reply == null)
                text = "Error bridge_gone: Bridge closed the connection.";
            else if (!success)
            {
                Dictionary<string, object> err = reply.ContainsKey("error") ? reply["error"] as Dictionary<string, object> : null;
                text = "Error " + DeviceWire.S(err, "code") + ": " + DeviceWire.S(err, "message");
            }
            else
                text = DeviceWire.S(reply, "content");

            Dictionary<string, object> content = new Dictionary<string, object>();
            content["type"] = "text";
            content["text"] = text;
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["content"] = new object[] { content };
            result["isError"] = !success;
            Send(id, result, null);
        }
    }

    // ============================================================= device relay (v1.12.0, S1)
    // The site agent (co-agent/executor relay-mcp.mjs) calls tools of the home devices through the server: this Bridge
    // publishes the devices it serves (db.php relay_devices_put, every 30 s), long-polls relay_claim and answers each call
    // with relay_result. A call to a paired device runs through ExecuteDeviceCall under a per-site-job state
    // ("relay-<jobId>", devices of the call's environment only), so signing, rights, confirmations, trust, call limits and
    // taint work as for a Home job. Device "h-<host>" is this PC's tool library: tool_list / tool_help / tool_run for the
    // tools in devices.relayTools, owner's environment only, never with --confirm (submit/purchase stay on Home).

    internal sealed partial class BridgeApplicationContext
    {
        private const int RelayMaxActive = 8;
        private const int RelayStateIdleMinutes = 30;
        private string RelayHomeShort
        {
            get
            {
                string s = D.relayHomeShort;
                return !String.IsNullOrEmpty(s) && Regex.IsMatch(s, @"\A[a-z0-9]{1,12}\z") ? s : "home";
            }
        }
        private int _relayActive;
        private int _relayClaiming;      // 1 while a relay_claim request is out (its calls are not counted yet)
        private readonly ConcurrentDictionary<string, DateTime> _relayStates = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Process, string> _relayProcs = new ConcurrentDictionary<Process, string>();
        private readonly ConcurrentDictionary<string, DateTime> _relayEnded = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _relayCancelLock = new object();   // ended-check + process start vs. cancel sweep
        private List<string> _relayAck = new List<string>();       // cancelled job ids to acknowledge in the next poll

        private string RelayHomeId { get { return "h-" + _config.hostId; } }

        // Update drain waits for relay calls as for jobs; a draining Bridge claims no new calls.
        private bool RelayBusy { get { return Thread.VolatileRead(ref _relayActive) > 0 || Thread.VolatileRead(ref _relayClaiming) > 0; } }
        private int RelayActiveCount { get { return Thread.VolatileRead(ref _relayActive) + Thread.VolatileRead(ref _relayClaiming); } }

        private void StartRelay()
        {
            if (!D.relay || !DbqEnabled)
            {
                Log("Device relay off (devices.relay or dbQueue disabled).");
                return;
            }
            if (!Regex.IsMatch(_config.hostId ?? "", @"\A[A-Za-z0-9_-]{1,38}\z"))
            {
                Log("Device relay off: hostId does not fit a relay device id.");
                return;
            }
            StartThread("relay-devices", RelayDevicesLoop);
            StartThread("relay-claim", RelayClaimLoop);
            Log("Device relay started: site jobs may call devices of this Bridge.");
        }

        private void RelayDevicesLoop()
        {
            int failures = 0;
            while (!_exiting)
            {
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["host"] = _config.hostId;
                    body["devices"] = BuildRelayDevices();
                    DbqCall("relay_devices_put", body, true, 15000);
                    PcPublishSandboxes();       // v1.15.0: the family sandbox as a sandbox provider (schema 26)
                    if (failures > 0)
                        Log("Device relay: server reachable again.");
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    LogThrottled("relay-devices", "Device relay: devices not published (" + ex.Message + ")");
                }
                for (int i = 0; i < 30 && !_exiting; i++)
                    Thread.Sleep(1000);
                RelayDropIdleStates();
            }
        }

        private List<object> BuildRelayDevices()
        {
            List<object> list = new List<object>();
            DateTime now = DateTime.UtcNow;
            HashSet<string> shorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (PairedDevice d in _pairedDevices.Values)
            {
                if (d == null || d.revoked || d.IsWeb || String.IsNullOrEmpty(d.serviceAccount) ||
                    !Regex.IsMatch(d.deviceId ?? "", @"\Ad-[0-9a-f]{16}\z") || !Regex.IsMatch(d.shortName ?? "", @"\A[a-z0-9]{1,12}\z") ||
                    !IsValidEnvironment(d.environmentId))
                    continue;
                if (!shorts.Add(d.environmentId + "/" + d.shortName))
                    continue;
                List<object> tools = new List<object>();
                bool mac = false;
                System.Collections.IEnumerable all = (d.liveTools ?? d.tools) as System.Collections.IEnumerable;
                if (all != null)
                    foreach (object t in all)
                    {
                        string name = DeviceWire.S(t as Dictionary<string, object>, "name");
                        if (String.IsNullOrEmpty(name) || name == "approve")
                            continue;      // approve is Claude's permission prompt on Home, not a tool for the agent
                        if (name == "mac_check")
                            mac = true;
                        tools.Add(t);
                    }
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["deviceId"] = d.deviceId;
                r["environmentId"] = d.environmentId;
                r["short"] = d.shortName;
                r["name"] = !String.IsNullOrEmpty(d.name) ? d.name : d.shortName;
                r["kind"] = mac ? "mac" : "desktop";
                r["online"] = d.lastHeartbeatUtc != DateTime.MinValue && (now - d.lastHeartbeatUtc).TotalSeconds <= 120;
                r["tools"] = tools;
                list.Add(r);
            }
            string owner = OwnerPushEnv();
            bool lib = D.relayTools != null && D.relayTools.Length > 0;
            Dictionary<string, object> h = null;
            if ((lib || PcOwnerEnabled) && IsValidEnvironment(owner) && shorts.Add(owner + "/" + RelayHomeShort))
            {
                h = new Dictionary<string, object>();
                h["deviceId"] = RelayHomeId;
                h["environmentId"] = owner;
                h["short"] = RelayHomeShort;
                h["name"] = Environment.MachineName;
                h["kind"] = "home";
                h["online"] = true;
                h["tools"] = lib ? RelayHomeTools() : new object[0];
                list.Add(h);
            }
            AddPcRelayDevices(list, shorts, h);     // v1.15.0: pc_* of the owner and the family sandboxes
            return list;
        }

        private static Dictionary<string, object> RelayToolDef(string name, string description, Dictionary<string, object> properties, string[] required)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "object";
            schema["properties"] = properties;
            if (required != null)
                schema["required"] = required;
            Dictionary<string, object> t = new Dictionary<string, object>();
            t["name"] = name;
            t["description"] = description;
            t["inputSchema"] = schema;
            return t;
        }

        private static Dictionary<string, object> RelayProp(string type)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = type;
            return p;
        }

        private object[] RelayHomeTools()
        {
            string names = String.Join(", ", D.relayTools);
            Dictionary<string, object> listProps = new Dictionary<string, object>();
            listProps["query"] = RelayProp("string");
            Dictionary<string, object> helpProps = new Dictionary<string, object>();
            helpProps["name"] = RelayProp("string");
            Dictionary<string, object> runProps = new Dictionary<string, object>();
            runProps["name"] = RelayProp("string");
            Dictionary<string, object> arr = RelayProp("array");
            arr["items"] = RelayProp("string");
            runProps["args"] = arr;
            Dictionary<string, object> to = RelayProp("integer");
            to["default"] = 300;
            to["minimum"] = 1;
            to["maximum"] = 900;
            runProps["timeout_sec"] = to;
            runProps["stdin"] = RelayProp("string");
            return new object[]
            {
                RelayToolDef("tool_list", "List the tools of the home PC's tool library that the site may run (" + names + "). Optional query filters by words.", listProps, null),
                RelayToolDef("tool_help", "Full SKILL.md of a home PC tool. Read it before tool_run.", helpProps, new string[] { "name" }),
                RelayToolDef("tool_run", "Run a tool on the home PC (its CO browser and signed-in sessions) with arguments; returns exit code, stdout and stderr. " +
                    "Only " + names + "; --confirm (submit, purchase, publish) is refused: fill the form and ask the user to press the button.",
                    runProps, new string[] { "name", "args" })
            };
        }

        private void RelayClaimLoop()
        {
            int failures = 0;
            while (!_exiting)
            {
                // With every slot busy or while draining the poll goes on with max 0: Stop of a site job must reach
                // its running calls (cancelJobs) at once, not when a slot frees up.
                // S8: a drain still serves calls while jobs run (the exit waits for those jobs anyway), so
                // site jobs keep the home devices during a long update wait; with no job left it claims none.
                // The claim flag goes up before the job count is read: the drain exit reads the count, then RelayBusy.
                int free = Math.Max(0, RelayMaxActive - Thread.VolatileRead(ref _relayActive));
                if (_draining && free > 0)
                {
                    Interlocked.Exchange(ref _relayClaiming, 1);
                    if (_runningJobs.Count == 0)
                    {
                        free = 0;
                        Interlocked.Exchange(ref _relayClaiming, 0);
                    }
                }
                else if (free > 0)
                    Interlocked.Exchange(ref _relayClaiming, 1);
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["host"] = _config.hostId;
                    body["waitSec"] = free > 0 ? 20 : 5;
                    body["max"] = free;
                    List<string> ack;
                    lock (_relayCancelLock)
                    {
                        ack = _relayAck;
                        _relayAck = new List<string>();
                    }
                    if (ack.Count > 0)
                        body["ack"] = ack;
                    Dictionary<string, object> resp;
                    try
                    {
                        resp = DbqCall("relay_claim", body, false, 35000);
                    }
                    catch
                    {
                        lock (_relayCancelLock) _relayAck.AddRange(ack);   // acknowledged next time
                        throw;
                    }
                    if (failures > 0)
                        Log("Device relay: claiming again.");
                    failures = 0;
                    foreach (object j in DbqArray(resp, "cancelJobs"))
                    {
                        string cj = j as string;
                        RelayCancelJob(cj);
                        if (cj != null)
                            lock (_relayCancelLock) _relayAck.Add(cj);
                    }
                    foreach (object o in DbqArray(resp, "calls"))
                    {
                        Dictionary<string, object> call = o as Dictionary<string, object>;
                        if (call == null)
                            continue;
                        Interlocked.Increment(ref _relayActive);
                        Thread t = new Thread(delegate() { RelayServe(call); });
                        t.IsBackground = true;
                        t.Name = "relay-call";
                        t.Start();
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    LogThrottled("relay-claim", "Device relay: claim failed (" + ex.Message + ")");
                    Interlocked.Exchange(ref _relayClaiming, 0);
                    Thread.Sleep(Math.Min(60000, 2000 << Math.Min(failures, 5)));
                }
                finally
                {
                    Interlocked.Exchange(ref _relayClaiming, 0);
                }
            }
        }

        private void RelayServe(Dictionary<string, object> call)
        {
            string callId = DeviceWire.S(call, "callId");
            try
            {
                Dictionary<string, object> reply;
                try
                {
                    reply = RelayExecute(call);
                }
                catch (Exception ex)
                {
                    reply = Fail("bridge_error", ex.Message);
                }
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["host"] = _config.hostId;
                body["callId"] = callId;
                body["result"] = reply;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        DbqCall("relay_result", body, true, 30000);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= 3)
                        {
                            Log("Device relay: result of " + callId + " not stored (" + ex.Message + ")");
                            break;
                        }
                        Thread.Sleep(2000 * attempt);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Device relay: call " + callId + " failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _relayActive);
            }
        }

        private Dictionary<string, object> RelayExecute(Dictionary<string, object> call)
        {
            string deviceId = DeviceWire.S(call, "deviceId") ?? "";
            string jobId = DeviceWire.S(call, "jobId") ?? "";
            string env = DeviceWire.S(call, "environmentId");
            string chatId = DeviceWire.S(call, "chatId") ?? "";
            string tool = DeviceWire.S(call, "tool");
            string argsJson = DeviceWire.S(call, "argsJson");
            if (!Regex.IsMatch(jobId, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z") || !IsValidEnvironment(env))
                return Fail("not_allowed", "Invalid job or environment.");
            if (!Regex.IsMatch(chatId, @"\A[A-Za-z0-9_-]{0,80}\z"))
                chatId = "";
            Log("Device relay: site job " + jobId + " (" + env + ") -> " + deviceId + " " + tool);

            string key = "relay-" + jobId;
            if (_relayEnded.ContainsKey(key))
                return Fail("cancelled", "The site job has ended.");
            // v1.15.0: pc_* tools - the owner's PC, or a family member's sandbox (own device id, pc_* only)
            List<string> callDeny = new List<string>();
            object dv;
            if (call.TryGetValue("deny", out dv) && dv is System.Collections.IEnumerable && !(dv is string))
                foreach (object x in (System.Collections.IEnumerable)dv)
                    if (x is string) callDeny.Add((string)x);
            PcBox box = deviceId.StartsWith(RelayHomeId + "-", StringComparison.Ordinal) ? PcBoxOfDevice(deviceId) : null;
            Dictionary<string, object> refused;
            string route = PcTools.Route(deviceId, RelayHomeId, tool, env, OwnerPushEnv(), box, callDeny, out refused);
            if (refused != null)
                return refused;
            if (route == "pc-family")
                return PcCall(key, env, chatId, tool, argsJson, box, callDeny, DeviceWire.B(call, "tainted"));
            if (route == "pc-owner")
                return PcCall(key, env, chatId, tool, argsJson, null, callDeny, DeviceWire.B(call, "tainted"));
            if (route == "library")
            {
                Dictionary<string, object> hr = RelayHomeCall(key, env, chatId, tool, argsJson);
                // web pages and mail read through the tool library taint the job: later device writes ask again
                if (DeviceWire.B(hr, "untrusted"))
                    RelayState(key, env, chatId).tainted = true;
                return hr;
            }

            PairedDevice d;
            if (!_pairedDevices.TryGetValue(deviceId, out d) || d.revoked || d.IsWeb)
                return Fail("not_paired", "Device " + deviceId + " is not paired with this Bridge.");
            // TZ §8.3: a member's device serves only jobs of its environment.
            if (!String.Equals(d.environmentId, env, StringComparison.Ordinal))
                return Fail("not_allowed", "Device " + deviceId + " belongs to another environment.");

            JobDeviceState js = RelayState(key, env, chatId);
            if (!String.Equals(js.environmentId, env, StringComparison.Ordinal))
                return Fail("not_allowed", "Job " + jobId + " runs in another environment.");
            lock (js.devices)
            {
                if (!js.devices.Contains(deviceId))
                    js.devices.Add(deviceId);
            }
            if (DeviceWire.B(call, "tainted"))
                js.tainted = true;
            // v1.3.5 parity: tools the chat turned off (a connector) are refused for site jobs too
            object deny;
            List<string> denyNames = new List<string>();
            if (call.TryGetValue("deny", out deny) && deny is System.Collections.IEnumerable && !(deny is string))
                foreach (object x in (System.Collections.IEnumerable)deny)
                    if (x is string) denyNames.Add((string)x);
            if (JobDeviceState.DenySet(denyNames).Contains(tool ?? ""))
                return Fail("tool_off", "This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.");
            return ExecuteDeviceCall(key, deviceId, tool, argsJson);
        }

        // Shared device state of a site job: taint, call limit and browser settings of all its calls.
        private JobDeviceState RelayState(string key, string env, string chatId)
        {
            _relayStates[key] = DateTime.UtcNow;
            return _deviceJobs.GetOrAdd(key, delegate(string k)
            {
                JobDeviceState n = new JobDeviceState();
                n.jobId = k;
                n.taskId = k;
                n.chatId = chatId;
                n.environmentId = env;
                n.agent = "claude";
                JobRequest jr = new JobRequest();
                jr.environmentId = env;
                jr.chatId = chatId;
                n.browser = ResolveBrowserSettings(jr);
                return n;
            });
        }

        // The site job ended (Stop, timeout) while calls of it run here: device calls get tool.cancel and return,
        // tool library processes are killed with their whole tree, later calls of the job are refused.
        private void RelayCancelJob(string jobId)
        {
            if (String.IsNullOrEmpty(jobId))
                return;
            string key = "relay-" + jobId;
            if (_relayEnded.ContainsKey(key))
                return;                          // repeated until acknowledged: the first sweep did the work
            List<Process> procs = new List<Process>();
            lock (_relayCancelLock)
            {
                _relayEnded[key] = DateTime.UtcNow;
                foreach (KeyValuePair<Process, string> kv in _relayProcs)
                    if (kv.Value == key)
                        procs.Add(kv.Key);
            }
            int n = 0;
            foreach (PendingCall pc in _pendingCalls.Values)
            {
                if (pc == null || pc.runId != key)
                    continue;
                PairedDevice d;
                if (_pairedDevices.TryGetValue(pc.deviceId ?? "", out d) && !d.IsWeb)
                    CancelDeviceCall(d, pc);
                pc.result = Fail("cancelled", "The site job has ended; the call was cancelled.");
                pc.done.Set();
                n++;
            }
            foreach (Process p in procs)
            {
                KillProcessTreeFallback(p);
                n++;
            }
            Log("Device relay: site job " + jobId + " ended; " + n + " running call(s) cancelled.");
        }

        private void RelayDropIdleStates()
        {
            foreach (KeyValuePair<string, DateTime> kv in _relayEnded)
                if ((DateTime.UtcNow - kv.Value).TotalHours >= 6)
                {
                    DateTime ignored;
                    _relayEnded.TryRemove(kv.Key, out ignored);
                }
            foreach (KeyValuePair<string, DateTime> kv in _relayStates)
            {
                if ((DateTime.UtcNow - kv.Value).TotalMinutes < RelayStateIdleMinutes)
                    continue;
                DateTime ignored;
                JobDeviceState js;
                _relayStates.TryRemove(kv.Key, out ignored);
                _deviceJobs.TryRemove(kv.Key, out js);
            }
        }

        // ---------------------------------------------------------------- home tool library (h-<host>)

        private static string RelayToolsRoot()
        {
            string env = Environment.GetEnvironmentVariable("AIWB_TOOLS");
            return !String.IsNullOrEmpty(env) && Directory.Exists(env) ? env : @"C:\AIWorkbench\tools";
        }

        // Builds the arguments for toolbox/mcp_server.py, or returns a refusal.
        internal static Dictionary<string, object> RelayHomeArgs(string tool, Dictionary<string, object> args, ICollection<string> allowed,
                                                                  string cwd, out int timeoutSec, out Dictionary<string, object> refusal)
        {
            refusal = null;
            timeoutSec = 300;
            Dictionary<string, object> clean = new Dictionary<string, object>();
            if (tool == "tool_list")
            {
                string q = DeviceWire.S(args, "query");
                if (!String.IsNullOrEmpty(q)) clean["query"] = q;
                return clean;
            }
            if (tool != "tool_help" && tool != "tool_run")
            {
                refusal = Fail("unknown_tool", "Unknown tool " + tool + ".");
                return null;
            }
            string name = DeviceWire.S(args, "name");
            if (String.IsNullOrEmpty(name) || !allowed.Contains(name))
            {
                refusal = Fail("not_allowed", "Tool '" + name + "' may not be run from the site. Allowed: " + String.Join(", ", allowed.ToArray()) + ".");
                return null;
            }
            clean["name"] = name;
            if (tool == "tool_help")
                return clean;
            object a;
            System.Collections.IEnumerable list = args.TryGetValue("args", out a) && !(a is string) ? a as System.Collections.IEnumerable : null;
            if (list == null)
            {
                refusal = Fail("bad_args", "'args' must be an array of strings.");
                return null;
            }
            List<string> runArgs = new List<string>();
            foreach (object x in list)
            {
                string sx = x as string;
                if (sx == null)
                {
                    refusal = Fail("bad_args", "'args' must be an array of strings.");
                    return null;
                }
                if (RelayConfirmLike(sx))
                {
                    refusal = Fail("confirm_refused", "--confirm (submit, purchase, publish) is not allowed from the site. " +
                        "Fill the form and ask the user to press the button, or send the job to Home.");
                    return null;
                }
                runArgs.Add(sx);
            }
            clean["args"] = runArgs;
            object t;
            if (args.TryGetValue("timeout_sec", out t) && (t is int || t is long || t is decimal))
                timeoutSec = (int)Math.Max(1, Math.Min(900, Convert.ToInt64(t, CultureInfo.InvariantCulture)));
            clean["timeout_sec"] = timeoutSec;
            string stdin = DeviceWire.S(args, "stdin");
            if (stdin != null) clean["stdin"] = stdin;
            clean["cwd"] = cwd;
            return clean;
        }

        // v1.14.0 (R2, review S1-01): argparse takes any unique prefix of a long option, so "--conf" or "--c=1" is
        // --confirm too. The option name (before '=') is canonical when it is --confirm..., or a prefix of --confirm
        // that argparse could expand to it. The tools of the list also turn abbreviations off (allow_abbrev=False).
        internal static bool RelayConfirmLike(string arg)
        {
            string t = (arg ?? "").Trim();
            if (!t.StartsWith("--", StringComparison.Ordinal))
                return false;
            int eq = t.IndexOf('=');
            string name = (eq >= 0 ? t.Substring(0, eq) : t).ToLowerInvariant();
            return name.Length >= 3 && (name.StartsWith("--confirm", StringComparison.Ordinal) || "--confirm".StartsWith(name, StringComparison.Ordinal));
        }

        private Dictionary<string, object> RelayHomeCall(string key, string env, string chatId, string tool, string argsJson)
        {
            Dictionary<string, object> args = String.IsNullOrEmpty(argsJson) ? new Dictionary<string, object>()
                : JsonUtil.DeserializeObject(argsJson) as Dictionary<string, object>;
            if (args == null)
                return Fail("bad_args", "Arguments must be a JSON object.");
            HashSet<string> allowed = new HashSet<string>(D.relayTools ?? new string[0], StringComparer.Ordinal);
            string work = Path.Combine(_localStateRoot, "relay");
            string cwd = Path.Combine(work, env);
            Directory.CreateDirectory(cwd);
            int timeoutSec;
            Dictionary<string, object> refusal;
            Dictionary<string, object> clean = RelayHomeArgs(tool, args, allowed, cwd, out timeoutSec, out refusal);
            if (clean == null)
                return refusal;

            string script = Path.Combine(RelayToolsRoot(), "toolbox", "mcp_server.py");
            if (!File.Exists(script))
                return Fail("unavailable", "The tool library is not installed on the home PC.");
            Dictionary<string, object> prm = new Dictionary<string, object>();
            prm["name"] = tool;
            prm["arguments"] = clean;
            Dictionary<string, object> rpc = new Dictionary<string, object>();
            rpc["jsonrpc"] = "2.0";
            rpc["id"] = 1;
            rpc["method"] = "tools/call";
            rpc["params"] = prm;

            ProcessStartInfo psi = new ProcessStartInfo("python", "\"" + script + "\"");
#if NET8_0_OR_GREATER
            psi.FileName = BridgePlatform.Current.Executables.Python;
#endif
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = work;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["AIWB_JOB_ID"] = key;
            psi.EnvironmentVariables["AIWB_CHAT_ID"] = chatId;
            JobRequest jr = new JobRequest();
            jr.environmentId = env;
            jr.chatId = chatId;
            BrowserSettings bs = ResolveBrowserSettings(jr);
            psi.EnvironmentVariables["AIWB_BROWSER_TABS"] = bs.tabs.ToString(CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["AIWB_BROWSER_IDLE_MIN"] = bs.idleMin.ToString(CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["AIWB_BROWSER_FORM_IDLE_MIN"] = bs.formIdleMin.ToString(CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["AIWB_BROWSER_CAPTCHA_KEEP"] = bs.captchaKeep ? "1" : "0";

            Process started;
            lock (_relayCancelLock)
            {
                // checked and registered under the lock the cancel sweep takes: a cancel either sees this process or
                // happened before it, and then it never starts
                if (_relayEnded.ContainsKey(key))
                    return Fail("cancelled", "The site job has ended.");
                started = Process.Start(psi);
                _relayProcs[started] = key;
            }
            using (Process p = started)
            {
                try
                {
                System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                p.StandardInput.Write(JsonUtil.Serialize(rpc) + "\n");
                p.StandardInput.Close();
                if (!p.WaitForExit((timeoutSec + 30) * 1000))
                {
                    KillProcessTreeFallback(p);     // the tool's own processes too, not only mcp_server.py
                    return Fail("timeout", "The home tool did not finish in " + (timeoutSec + 30) + " s.");
                }
                if (_relayEnded.ContainsKey(key))
                    return Fail("cancelled", "The site job has ended; the tool was stopped.");
                outTask.Wait(5000);
                errTask.Wait(1000);
                return RelayHomeReply(tool, outTask.IsCompleted ? outTask.Result : "", errTask.IsCompleted ? errTask.Result : "", allowed);
                }
                finally
                {
                    string ignored;
                    _relayProcs.TryRemove(p, out ignored);
                }
            }
        }

        // Turns the JSON-RPC answer of mcp_server.py into a device reply.
        internal static Dictionary<string, object> RelayHomeReply(string tool, string stdout, string stderr, ICollection<string> allowed)
        {
            Dictionary<string, object> resp = null;
            foreach (string l in (stdout ?? "").Split('\n'))
                if (l.Trim().StartsWith("{", StringComparison.Ordinal))
                    resp = JsonUtil.DeserializeObject(l.Trim()) as Dictionary<string, object>;
            Dictionary<string, object> result = resp != null && resp.ContainsKey("result") ? resp["result"] as Dictionary<string, object> : null;
            if (result == null)
                return Fail("tool_failed", "The tool library gave no answer. " + PushCut(stderr ?? "", 400));
            StringBuilder text = new StringBuilder();
            System.Collections.IEnumerable content = result.ContainsKey("content") ? result["content"] as System.Collections.IEnumerable : null;
            if (content != null)
                foreach (object c in content)
                    text.Append(DeviceWire.S(c as Dictionary<string, object>, "text"));
            string body = text.ToString();
            if (tool == "tool_list")
            {
                List<string> kept = new List<string>();
                foreach (string l in body.Split('\n'))
                {
                    int sp = l.IndexOf(' ');
                    if (sp > 0 && allowed.Contains(l.Substring(0, sp)))
                        kept.Add(l.TrimEnd('\r'));
                }
                body = kept.Count > 0 ? String.Join("\n", kept.ToArray()) : "no allowed tools found";
            }
            bool isError = DeviceWire.B(result, "isError");
            Dictionary<string, object> reply = new Dictionary<string, object>();
            reply["ok"] = !isError;
            reply["content"] = body;
            reply["untrusted"] = tool == "tool_run";     // output of web pages and mail
            if (isError)
            {
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["code"] = "tool_error";
                e["message"] = body.Length > 20000 ? body.Substring(0, 20000) : body;
                reply["error"] = e;
            }
            return reply;
        }
    }
}
