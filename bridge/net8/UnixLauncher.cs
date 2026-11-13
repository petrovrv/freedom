// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a (SPEC §4.2): the update launcher of Linux (and macOS) inside the .NET 8 Bridge. The same channel as the
// Windows launcher (bridge/launcher/Launcher.cs): signed releases in updatesDir, ECDSA P-256 / SHA-256 over
// "AIWB-BRIDGE-RELEASE\n<v>\n<sha256>\n", the public key as a base64 CNG EccPublicBlob in launcher.json, drain.request
// and exit code 3, probation and roll-back, state.json. The release files carry the runtime id:
//   <channel>/BridgeAgent-<v>-<rid> + BridgeAgent-<v>-<rid>.sig.json     (Windows: BridgeAgent-<v>.exe + .sig.json;
//   the Windows launcher skips the names with a runtime id, they are no x.y.z version)
//
// Installed layout (bridge/install.sh):
//   ~/.local/lib/aiworkbench-bridge/BridgeAgent            a Bridge build run as the launcher (--launcher)
//   ~/.local/lib/aiworkbench-bridge/launcher.json          updatesDir, releaseKey, stateRoot, configPath, rid
//   ~/.local/lib/aiworkbench-bridge/versions/<v>/BridgeAgent
//   ~/.local/lib/aiworkbench-bridge/state.json             current / previous / pending / bad
//   ~/.local/share/AIWorkbenchBridge/config.json, logs/launcher.log, drain.request, updates/ (the local channel)
//
//   BridgeAgent --launcher [--root DIR] [--check-once]
//   BridgeAgent --release-key [--create]                   public part of this computer's release key (base64 blob);
//                                                          the private key is in the secret store (aiwb/bridge-release)
//   BridgeAgent --release-publish --channel DIR --file BIN --version X.Y.Z [--commit C] [--rid RID]
//                                                          sign BIN into the channel (refuses a published version)
//   BridgeAgent --version                                  the Bridge version of this build
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AiWorkbenchBridge
{
    internal sealed class UnixLauncherSettings
    {
        public string updatesDir { get; set; }
        public string releaseKey { get; set; }
        public string stateRoot { get; set; }
        public string configPath { get; set; }
        public string rid { get; set; }
        public int checkSeconds { get; set; }
        public int probationSeconds { get; set; }
        public int keepVersions { get; set; }
    }

    internal sealed class UnixLauncherState
    {
        public string current { get; set; }
        public string previous { get; set; }
        public string pending { get; set; }
        public List<string> bad { get; set; }
    }

    internal sealed class UnixReleaseSig
    {
        public string version { get; set; }
        public string file { get; set; }
        public string rid { get; set; }
        public string sha256 { get; set; }
        public string commit { get; set; }
        public string builtUtc { get; set; }
        public string signature { get; set; }
    }

    // Keys and signatures in the formats of the Windows launcher: CNG EccPublicBlob (P-256) and IEEE P1363 r||s.
    internal static class ReleaseCrypto
    {
        public const string KeyTarget = "aiwb/bridge-release";
        private const uint EcdsaPublicP256Magic = 0x31534345;   // "ECS1" (BCRYPT_ECDSA_PUBLIC_P256_MAGIC)

        public static string SignedText(string version, string sha256)
        {
            return "AIWB-BRIDGE-RELEASE\n" + version + "\n" + sha256.ToLowerInvariant() + "\n";
        }

        public static string PublicBlob(ECDsa key)
        {
            ECParameters p = key.ExportParameters(false);
            byte[] blob = new byte[8 + 64];
            BitConverter.GetBytes(EcdsaPublicP256Magic).CopyTo(blob, 0);
            BitConverter.GetBytes(32).CopyTo(blob, 4);
            p.Q.X.CopyTo(blob, 8);
            p.Q.Y.CopyTo(blob, 40);
            return Convert.ToBase64String(blob);
        }

        public static ECDsa FromPublicBlob(string base64)
        {
            byte[] blob = Convert.FromBase64String(base64 ?? "");
            if (blob.Length != 72 || BitConverter.ToUInt32(blob, 0) != EcdsaPublicP256Magic || BitConverter.ToInt32(blob, 4) != 32)
                throw new CryptographicException("releaseKey is no P-256 EccPublicBlob");
            ECParameters p = new ECParameters();
            p.Curve = ECCurve.NamedCurves.nistP256;
            p.Q = new ECPoint { X = blob[8..40], Y = blob[40..72] };
            return ECDsa.Create(p);
        }

        public static string Sign(ECDsa key, string version, string sha256)
        {
            byte[] sig = key.SignData(Encoding.UTF8.GetBytes(SignedText(version, sha256)), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Convert.ToBase64String(sig);
        }

        public static bool Verify(string publicBlob, string version, string sha256, string signature)
        {
            try
            {
                using (ECDsa k = FromPublicBlob(publicBlob))
                    return k.VerifyData(Encoding.UTF8.GetBytes(SignedText(version, sha256)), Convert.FromBase64String(signature),
                        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (FormatException) { return false; }
            catch (CryptographicException) { return false; }
        }

        // The private key of this computer: PKCS#8 base64 in the secret store, never in a file.
        public static ECDsa LoadKey(ISecretStore store, bool create)
        {
            string pkcs8 = store.Read(KeyTarget);
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (!String.IsNullOrEmpty(pkcs8))
            {
                int read;
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8.Trim()), out read);
                return key;
            }
            if (!create)
            {
                key.Dispose();
                throw new InvalidOperationException("No release key in " + store.Name + " (" + KeyTarget + "): --release-key --create once.");
            }
            store.Write(KeyTarget, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "AI Workbench Bridge release key of this computer");
            return key;
        }

        public static string Sha256File(string path)
        {
            using (SHA256 h = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
                return Convert.ToHexString(h.ComputeHash(f)).ToLowerInvariant();
        }

        public static string CurrentRid()
        {
            string os = OperatingSystem.IsMacOS() ? "osx" : "linux";
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64: return os + "-x64";
                case Architecture.Arm64: return os + "-arm64";
                default: return os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            }
        }

        // Publishes a signed build into a channel; the build of a version is never replaced (as release.ps1).
        public static string Publish(ISecretStore store, string channel, string file, string version, string commit, string rid, bool createKey)
        {
            if (!UnixLauncher.VersionPattern.IsMatch(version ?? ""))
                throw new ArgumentException("--version must be x.y.z");
            if (!Regex.IsMatch(rid ?? "", @"\A[a-z]+-[a-z0-9]+\z"))
                throw new ArgumentException("bad runtime id " + rid);
            Directory.CreateDirectory(channel);
            string name = "BridgeAgent-" + version + "-" + rid;
            string binOut = Path.Combine(channel, name);
            string sigOut = binOut + ".sig.json";
            string sha = Sha256File(file);
            if (File.Exists(sigOut))
            {
                UnixReleaseSig old = new JavaScriptSerializer().Deserialize<UnixReleaseSig>(File.ReadAllText(sigOut, Encoding.UTF8));
                if (old != null && String.Equals(old.sha256, sha, StringComparison.OrdinalIgnoreCase) && File.Exists(binOut))
                    return "Bridge " + version + " (" + rid + ") is already published, same build";
                throw new InvalidOperationException("Bridge " + version + " (" + rid + ") is already published (" + sigOut + "). Raise BridgeVersion.");
            }
            UnixReleaseSig sig = new UnixReleaseSig();
            using (ECDsa key = LoadKey(store, createKey))
                sig.signature = Sign(key, version, sha);
            sig.version = version;
            sig.file = name;
            sig.rid = rid;
            sig.sha256 = sha;
            sig.commit = String.IsNullOrEmpty(commit) ? "src" : commit;
            sig.builtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string tmp = binOut + ".tmp";
            File.Copy(file, tmp, true);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(tmp, binOut, true);
            File.WriteAllText(sigOut, new JavaScriptSerializer().Serialize(sig), new UTF8Encoding(false));   // last: the launcher looks for .sig.json
            return "Published Bridge " + version + " (" + rid + ", commit " + sig.commit + ") to " + channel;
        }
    }

    internal sealed class UnixLauncher
    {
        public const string Version = "1.0.0";
        public static readonly Regex VersionPattern = new Regex(@"\A\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant);
        private const int ExitUser = 0, ExitDrained = 3, ExitAlreadyRunning = 4;

        private readonly string _root;
        private readonly string _versionsDir, _statePath;
        private string _logPath, _drainPath;
        private UnixLauncherSettings _s;
        private UnixLauncherState _st;
        private bool _probation;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        // systemctl stop / shutdown: SIGTERM reaches the launcher and Bridge together (the unit's control group);
        // the launcher then waits for Bridge and exits instead of restarting it.
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);

        public UnixLauncher(string root)
        {
            _root = Path.GetFullPath(root).TrimEnd('/');
            _versionsDir = Path.Combine(_root, "versions");
            _statePath = Path.Combine(_root, "state.json");
        }

        public UnixLauncherState State { get { return _st; } }

        public static int Main(string[] argv)
        {
            int i = Array.IndexOf(argv, "--root");
            string root = i >= 0 && i + 1 < argv.Length ? argv[i + 1] : AppContext.BaseDirectory;
            UnixLauncher l = new UnixLauncher(root);
            FileStream lockFile;
            try
            {
                Directory.CreateDirectory(l._root);
                // One launcher per root; .NET takes an exclusive flock for FileShare.None.
                lockFile = new FileStream(Path.Combine(l._root, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Another launcher runs for " + l._root);
                return 0;
            }
            using (lockFile)
            {
                using (PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, delegate(PosixSignalContext c)
                {
                    c.Cancel = true;
                    l._stop.Set();
                }))
                try
                {
                    l.LoadSettings();
                    l.Log("Launcher " + Version + " (Bridge " + BridgeApplicationContext.VersionText + " build, " + ReleaseCrypto.CurrentRid() + ") started. root=" + l._root);
                    if (Array.IndexOf(argv, "--check-once") >= 0)
                    {
                        l.CheckForUpdate();
                        if (l._st.current == null && l._st.pending != null)
                            l.Promote();
                        l.Log("check-once: current=" + l._st.current + " pending=" + l._st.pending);
                        Console.WriteLine("current=" + l._st.current + " pending=" + l._st.pending);
                        return 0;
                    }
                    return l.Run();
                }
                catch (Exception ex)
                {
                    l.Log("Launcher fatal: " + ex);
                    Console.Error.WriteLine("Launcher fatal: " + ex.Message);
                    return 1;
                }
            }
        }

        // ---------- main loop (as Launcher.cs)

        private int Run()
        {
            DateTime lastCheck = DateTime.MinValue;
            int quickFailures = 0;
            while (!Stopping)
            {
                if ((DateTime.UtcNow - lastCheck).TotalSeconds >= _s.checkSeconds || _st.current == null)
                {
                    lastCheck = DateTime.UtcNow;
                    CheckForUpdate();
                }
                if (_st.current == null && _st.pending != null)
                    Promote();
                if (_st.current == null)
                {
                    Log("No installed Bridge version yet; waiting for a release in " + _s.updatesDir);
                    _stop.WaitOne(60000);
                    continue;
                }

                TryDelete(_drainPath);
                string exe = ExePath(_st.current);
                if (!File.Exists(exe))
                {
                    Log("Missing " + exe + "; rolling back.");
                    MarkBadAndRollBack();
                    continue;
                }

                Process p = Start(exe);
                DateTime started = DateTime.UtcNow;
                Log("Started Bridge " + _st.current + " pid=" + p.Id);
                bool drainRequested = false;
                while (!p.WaitForExit(15000))
                {
                    if (_probation && (DateTime.UtcNow - started).TotalSeconds >= _s.probationSeconds)
                    {
                        _probation = false;
                        Log("Bridge " + _st.current + " passed probation.");
                    }
                    if ((DateTime.UtcNow - lastCheck).TotalSeconds >= _s.checkSeconds)
                    {
                        lastCheck = DateTime.UtcNow;
                        CheckForUpdate();
                    }
                    if (!drainRequested && _st.pending != null)
                    {
                        File.WriteAllText(_drainPath, _st.pending + " " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        drainRequested = true;
                        Log("Update " + _st.pending + " ready; asked Bridge to drain.");
                    }
                }

                int code = p.ExitCode;
                p.Dispose();
                double lived = (DateTime.UtcNow - started).TotalSeconds;
                TryDelete(_drainPath);
                Log("Bridge " + _st.current + " exited code=" + code + " after " + (int)lived + " s.");
                if (Stopping)
                    break;

                if (code == ExitDrained)
                {
                    quickFailures = 0;
                    if (_st.pending != null) Promote();
                    continue;
                }
                if (code == ExitUser)
                {
                    Log("Bridge was closed by the user; launcher stops.");
                    return 0;
                }
                if (code == ExitAlreadyRunning)
                {
                    _stop.WaitOne(60000);
                    continue;
                }
                if (lived < _s.probationSeconds && _st.previous != null && _probation)
                {
                    MarkBadAndRollBack();
                    quickFailures = 0;
                    continue;
                }
                quickFailures = lived < _s.probationSeconds ? quickFailures + 1 : 0;
                int wait = Math.Min(300, 10 * (1 << Math.Min(quickFailures, 5)));
                Log("Restarting in " + wait + " s.");
                _stop.WaitOne(wait * 1000);
            }
            Log("Launcher stopped (SIGTERM).");
            return 0;
        }

        private bool Stopping { get { return _stop.WaitOne(0); } }

        private Process Start(string exe)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(_s.configPath);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            return Process.Start(psi);
        }

        // ---------- versions

        internal void Promote()
        {
            if (_st.current != null && _st.current != _st.pending) _st.previous = _st.current;
            _st.current = _st.pending;
            _st.pending = null;
            _probation = _st.previous != null;
            SaveState();
            Log("Switched to Bridge " + _st.current + (_st.previous != null ? " (previous " + _st.previous + ")" : ""));
            CleanupVersions();
        }

        internal void MarkBadAndRollBack()
        {
            string bad = _st.current;
            if (bad != null && !_st.bad.Contains(bad)) _st.bad.Add(bad);
            _st.current = _st.previous;
            _st.previous = null;
            _probation = false;
            SaveState();
            Log("Bridge " + bad + " marked bad; back to " + (_st.current ?? "nothing"));
        }

        // The newest version of this runtime id in the channel that is not bad (null: none).
        internal static string Newest(IEnumerable<string> sigFiles, string rid, ICollection<string> bad)
        {
            Version best = null;
            string bestV = null;
            string suffix = "-" + rid + ".sig.json";
            foreach (string sigPath in sigFiles)
            {
                string n = Path.GetFileName(sigPath);
                if (!n.StartsWith("BridgeAgent-", StringComparison.Ordinal) || !n.EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                string v = n.Substring("BridgeAgent-".Length, n.Length - "BridgeAgent-".Length - suffix.Length);
                if (!VersionPattern.IsMatch(v) || bad.Contains(v)) continue;
                Version pv = new Version(v);
                if (best == null || pv > best) { best = pv; bestV = v; }
            }
            return bestV;
        }

        internal void CheckForUpdate()
        {
            try
            {
                if (String.IsNullOrEmpty(_s.updatesDir) || !Directory.Exists(_s.updatesDir))
                    return;
                string bestV = Newest(Directory.GetFiles(_s.updatesDir, "BridgeAgent-*.sig.json"), _s.rid, _st.bad);
                if (bestV == null) return;
                Version have = new Version(_st.pending ?? _st.current ?? "0.0.0");
                if (new Version(bestV) <= have) return;
                Stage(bestV);
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
            }
        }

        private void Stage(string v)
        {
            string name = "BridgeAgent-" + v + "-" + _s.rid;
            string sigPath = Path.Combine(_s.updatesDir, name + ".sig.json");
            UnixReleaseSig sig = _json.Deserialize<UnixReleaseSig>(File.ReadAllText(sigPath, Encoding.UTF8));
            if (sig == null || sig.version != v || String.IsNullOrEmpty(sig.signature) || String.IsNullOrEmpty(sig.sha256))
                throw new InvalidDataException("bad signature file for " + v);
            if (!ReleaseCrypto.Verify(_s.releaseKey, v, sig.sha256, sig.signature))
            {
                Reject(v, "signature does not verify");
                return;
            }

            string dir = Path.Combine(_versionsDir, v);
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string dst = Path.Combine(tmp, "BridgeAgent");
            File.Copy(Path.Combine(_s.updatesDir, name), dst, true);
            // Hash the local copy: what runs is exactly what was signed.
            if (!String.Equals(ReleaseCrypto.Sha256File(dst), sig.sha256, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(tmp, true);
                Log("Release " + v + ": hash mismatch (channel still being written?); retry later.");
                return;
            }
            File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            File.WriteAllText(Path.Combine(dir, "release.sig.json"), _json.Serialize(sig), new UTF8Encoding(false));

            int vc;
            try { vc = RunAndWait(ExePath(v), new string[] { "--validate-config", "--config", _s.configPath }, 60000); }
            catch (Exception ex) { Reject(v, "does not start: " + ex.Message); return; }   // e.g. a build of another CPU
            if (vc != 0)
            {
                string why = "";
                try { why = File.ReadAllText(Path.Combine(Path.GetDirectoryName(_s.configPath), "config-check.txt")); } catch { }
                Reject(v, "--validate-config exit " + vc + " " + why);
                return;
            }
            _st.pending = v;
            SaveState();
            Log("Release " + v + " staged and validated (commit " + sig.commit + ").");
        }

        private void Reject(string v, string why)
        {
            if (!_st.bad.Contains(v)) _st.bad.Add(v);
            SaveState();
            Log("Release " + v + " rejected: " + why);
        }

        private void CleanupVersions()
        {
            try
            {
                List<string> keep = new List<string> { _st.current, _st.previous, _st.pending };
                List<Version> all = new List<Version>();
                foreach (string d in Directory.GetDirectories(_versionsDir))
                {
                    string n = Path.GetFileName(d);
                    if (VersionPattern.IsMatch(n)) all.Add(new Version(n));
                }
                all.Sort(); all.Reverse();
                for (int k = 0; k < all.Count; k++)
                {
                    string n = all[k].ToString();
                    if (keep.Contains(n) || (k < _s.keepVersions && !_st.bad.Contains(n))) continue;
                    try { Directory.Delete(Path.Combine(_versionsDir, n), true); Log("Removed old version " + n); }
                    catch { }
                }
            }
            catch { }
        }

        // ---------- settings, state, log

        internal void LoadSettings()
        {
            Directory.CreateDirectory(_versionsDir);
            _s = _json.Deserialize<UnixLauncherSettings>(File.ReadAllText(Path.Combine(_root, "launcher.json"), Encoding.UTF8));
            IPlatformPaths paths = BridgePlatform.Current.Paths;
            if (String.IsNullOrEmpty(_s.stateRoot))
                _s.stateRoot = paths.StateRoot;
            _s.stateRoot = paths.Normalize(_s.stateRoot);
            _s.updatesDir = paths.Normalize(_s.updatesDir ?? "");
            _s.configPath = paths.Normalize(String.IsNullOrEmpty(_s.configPath) ? Path.Combine(_s.stateRoot, "config.json") : _s.configPath);
            if (String.IsNullOrEmpty(_s.rid)) _s.rid = ReleaseCrypto.CurrentRid();
            if (_s.checkSeconds <= 0) _s.checkSeconds = 600;
            if (_s.probationSeconds <= 0) _s.probationSeconds = 120;
            if (_s.keepVersions <= 0) _s.keepVersions = 3;

            paths.EnsurePrivateDirectory(_s.stateRoot);
            Directory.CreateDirectory(Path.Combine(_s.stateRoot, "logs"));
            _logPath = Path.Combine(_s.stateRoot, "logs", "launcher.log");
            _drainPath = Path.Combine(_s.stateRoot, "drain.request");

            _st = File.Exists(_statePath)
                ? _json.Deserialize<UnixLauncherState>(File.ReadAllText(_statePath, Encoding.UTF8))
                : new UnixLauncherState();
            if (_st == null) _st = new UnixLauncherState();
            if (_st.bad == null) _st.bad = new List<string>();
        }

        private void SaveState()
        {
            string tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, _json.Serialize(_st), new UTF8Encoding(false));
            File.Move(tmp, _statePath, true);   // rename(2): atomic
        }

        private string ExePath(string v) { return Path.Combine(_versionsDir, v, "BridgeAgent"); }

        private static int RunAndWait(string exe, string[] args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private readonly object _logLock = new object();

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    if (_logPath == null) return;
                    FileInfo fi = new FileInfo(_logPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                        File.Move(_logPath, _logPath + ".1", true);
                    File.AppendAllText(_logPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " | " + message + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
