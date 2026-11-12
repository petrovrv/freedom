// SPDX-License-Identifier: AGPL-3.0-or-later
// AI Workbench Bridge launcher (AIWorkbenchBridge.exe). C# 5, .NET Framework 4, no UI.
//
// Installed layout:
//   %LOCALAPPDATA%\Programs\AIWorkbenchBridge\AIWorkbenchBridge.exe   this launcher (changes rarely)
//   %LOCALAPPDATA%\Programs\AIWorkbenchBridge\launcher.json           updatesDir, release public key
//   %LOCALAPPDATA%\Programs\AIWorkbenchBridge\versions\<v>\BridgeAgent.exe
//   %LOCALAPPDATA%\Programs\AIWorkbenchBridge\state.json              current / previous / bad versions
//   %LOCALAPPDATA%\AIWorkbenchBridge\config.json, logs\launcher.log, drain.request
//
// Loop: start the current version; every 10 minutes (checkSeconds) look for a newer signed release in updatesDir
// (BridgeAgent-<v>.exe + BridgeAgent-<v>.sig.json), copy it into versions\<v>, gate it with --validate-config,
// then create drain.request. Bridge (>= 1.1.7) exits with code 3 once no job runs; the launcher starts the new
// version. A new version that dies within the probation time is marked bad and the previous one comes back.
// The launcher never waits for G: before starting Bridge: Bridge waits for Drive itself, updates are optional.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyTitle("AI Workbench Bridge launcher")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace AiWorkbenchBridge
{
#pragma warning disable 649   // fields below are filled by JavaScriptSerializer
    internal sealed class LauncherSettings
    {
        public string updatesDir;       // signed releases (local folder since 1.7.1; was Drive)
        public string releaseKey;       // base64 CNG EccPublicBlob (P-256) of the release signing key
        public string stateRoot;        // default %LOCALAPPDATA%\AIWorkbenchBridge
        public string configPath;       // default <stateRoot>\config.json
        public int checkSeconds;        // default 600
        public int probationSeconds;    // default 120
        public int keepVersions;        // default 3
    }

    internal sealed class LauncherState
    {
        public string current;
        public string previous;
        public string pending;          // staged, validated, waiting for the drain
        public List<string> bad = new List<string>();
    }

    internal sealed class ReleaseSig
    {
        public string version;
        public string file;
        public string sha256;
        public string commit;
        public string builtUtc;
        public string signature;        // base64 IEEE P1363 ECDSA P-256 / SHA-256 over SignedText()
    }

#pragma warning restore 649

    internal static class Program
    {
        public const string Version = "1.0.0";

        private const int ExitUser = 0, ExitDrained = 3, ExitAlreadyRunning = 4;
        private static readonly Regex VersionPattern = new Regex(@"\A\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant);

        private static string _root, _versionsDir, _statePath, _logPath, _drainPath;
        private static LauncherSettings _s;
        private static LauncherState _st;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        [STAThread]
        private static int Main(string[] args)
        {
            _root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            int i = Array.IndexOf(args, "--root");      // tests run a copy against a scratch root
            if (i >= 0 && i + 1 < args.Length) _root = Path.GetFullPath(args[i + 1]);

            bool createdNew;
            using (Mutex m = new Mutex(true, "Global\\AiWorkbenchBridgeLauncher" + (i >= 0 ? "-test" : ""), out createdNew))
            {
                if (!createdNew) return 0;
                try
                {
                    LoadSettings();
                    Log("Launcher " + Version + " started. root=" + _root);
                    if (Array.IndexOf(args, "--check-once") >= 0)
                    {
                        CheckForUpdate();
                        Log("check-once: current=" + _st.current + " pending=" + _st.pending);
                        return 0;
                    }
                    return Run();
                }
                catch (Exception ex)
                {
                    Log("Launcher fatal: " + ex);
                    return 1;
                }
            }
        }

        // ---------- main loop

        private static int Run()
        {
            DateTime lastCheck = DateTime.MinValue;
            int quickFailures = 0;

            while (true)
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
                    Thread.Sleep(60000);
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
                        File.WriteAllText(_drainPath, _st.pending + " " + DateTime.UtcNow.ToString("o"));
                        drainRequested = true;
                        Log("Update " + _st.pending + " ready; asked Bridge to drain.");
                    }
                }

                int code = p.ExitCode;
                p.Dispose();
                double lived = (DateTime.UtcNow - started).TotalSeconds;
                TryDelete(_drainPath);
                Log("Bridge " + _st.current + " exited code=" + code + " after " + (int)lived + " s.");

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
                    // An old Bridge outside the launcher still runs; start when it is gone.
                    Thread.Sleep(60000);
                    continue;
                }

                // Crash or fatal startup error.
                if (lived < _s.probationSeconds && _st.previous != null && _probation)
                {
                    MarkBadAndRollBack();
                    quickFailures = 0;
                    continue;
                }
                quickFailures = lived < _s.probationSeconds ? quickFailures + 1 : 0;
                int wait = Math.Min(300, 10 * (1 << Math.Min(quickFailures, 5)));
                Log("Restarting in " + wait + " s.");
                Thread.Sleep(wait * 1000);
            }
        }

        private static Process Start(string exe)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--config \"" + _s.configPath + "\"");
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            return Process.Start(psi);
        }

        // ---------- versions

        private static bool _probation;   // the current version was just switched to and has not run probationSeconds yet

        private static void Promote()
        {
            if (_st.current != null && _st.current != _st.pending) _st.previous = _st.current;
            _st.current = _st.pending;
            _st.pending = null;
            _probation = _st.previous != null;
            SaveState();
            Log("Switched to Bridge " + _st.current + (_st.previous != null ? " (previous " + _st.previous + ")" : ""));
            CleanupVersions();
        }

        private static void MarkBadAndRollBack()
        {
            string bad = _st.current;
            if (bad != null && !_st.bad.Contains(bad)) _st.bad.Add(bad);
            _st.current = _st.previous;
            _st.previous = null;
            _probation = false;
            SaveState();
            Log("Bridge " + bad + " marked bad; back to " + (_st.current ?? "nothing"));
        }

        private static void CheckForUpdate()
        {
            try
            {
                if (String.IsNullOrEmpty(_s.updatesDir) || !Directory.Exists(_s.updatesDir))
                    return;   // Drive not mounted yet, or no channel: keep what is installed

                Version best = null; string bestV = null;
                foreach (string sigPath in Directory.GetFiles(_s.updatesDir, "BridgeAgent-*.sig.json"))
                {
                    string v = Path.GetFileName(sigPath).Substring("BridgeAgent-".Length);
                    v = v.Substring(0, v.Length - ".sig.json".Length);
                    if (!VersionPattern.IsMatch(v) || _st.bad.Contains(v)) continue;
                    Version pv = new Version(v);
                    if (best == null || pv > best) { best = pv; bestV = v; }
                }
                if (bestV == null) return;

                Version have = new Version(_st.pending ?? _st.current ?? "0.0.0");
                if (best <= have) return;

                Stage(bestV);
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
            }
        }

        private static void Stage(string v)
        {
            string sigPath = Path.Combine(_s.updatesDir, "BridgeAgent-" + v + ".sig.json");
            ReleaseSig sig = Json.Deserialize<ReleaseSig>(File.ReadAllText(sigPath, Encoding.UTF8));
            if (sig == null || sig.version != v || String.IsNullOrEmpty(sig.signature) || String.IsNullOrEmpty(sig.sha256))
                throw new InvalidDataException("bad signature file for " + v);
            if (!VerifySignature(sig))
            {
                Reject(v, "signature does not verify");
                return;
            }

            string dir = Path.Combine(_versionsDir, v);
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string src = Path.Combine(_s.updatesDir, "BridgeAgent-" + v + ".exe");
            string dst = Path.Combine(tmp, "BridgeAgent.exe");
            File.Copy(src, dst, true);

            // Hash the local copy: what runs is exactly what was signed.
            if (!String.Equals(Sha256(dst), sig.sha256, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(tmp, true);
                Log("Release " + v + ": hash mismatch (Drive may still be syncing); retry later.");
                return;
            }

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            File.WriteAllText(Path.Combine(dir, "release.sig.json"), Json.Serialize(sig));

            int vc = RunAndWait(ExePath(v), "--validate-config --config \"" + _s.configPath + "\"", 60000);
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

        private static void Reject(string v, string why)
        {
            if (!_st.bad.Contains(v)) _st.bad.Add(v);
            SaveState();
            Log("Release " + v + " rejected: " + why);
        }

        private static void CleanupVersions()
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
                    catch { }   // an MCP proxy of that version may still run; next time
                }
            }
            catch { }
        }

        // ---------- crypto

        public static string SignedText(ReleaseSig s)
        {
            return "AIWB-BRIDGE-RELEASE\n" + s.version + "\n" + s.sha256.ToLowerInvariant() + "\n";
        }

        private static bool VerifySignature(ReleaseSig sig)
        {
            try
            {
                byte[] blob = Convert.FromBase64String(_s.releaseKey);
                using (CngKey k = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (ECDsaCng ec = new ECDsaCng(k))
                {
                    ec.HashAlgorithm = CngAlgorithm.Sha256;
                    return ec.VerifyData(Encoding.UTF8.GetBytes(SignedText(sig)), Convert.FromBase64String(sig.signature));
                }
            }
            catch (Exception ex)
            {
                Log("Signature check error: " + ex.Message);
                return false;
            }
        }

        private static string Sha256(string path)
        {
            using (SHA256 h = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
            {
                byte[] d = h.ComputeHash(f);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in d) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ---------- settings, state, log

        private static void LoadSettings()
        {
            _versionsDir = Path.Combine(_root, "versions");
            _statePath = Path.Combine(_root, "state.json");
            Directory.CreateDirectory(_versionsDir);

            _s = Json.Deserialize<LauncherSettings>(File.ReadAllText(Path.Combine(_root, "launcher.json"), Encoding.UTF8));
            if (String.IsNullOrEmpty(_s.stateRoot))
                _s.stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbenchBridge");
            _s.stateRoot = Environment.ExpandEnvironmentVariables(_s.stateRoot);
            _s.updatesDir = Environment.ExpandEnvironmentVariables(_s.updatesDir ?? "");
            _s.configPath = Environment.ExpandEnvironmentVariables(_s.configPath ?? Path.Combine(_s.stateRoot, "config.json"));
            if (_s.checkSeconds <= 0) _s.checkSeconds = 600;
            if (_s.probationSeconds <= 0) _s.probationSeconds = 120;
            if (_s.keepVersions <= 0) _s.keepVersions = 3;

            Directory.CreateDirectory(Path.Combine(_s.stateRoot, "logs"));
            _logPath = Path.Combine(_s.stateRoot, "logs", "launcher.log");
            _drainPath = Path.Combine(_s.stateRoot, "drain.request");

            _st = File.Exists(_statePath)
                ? Json.Deserialize<LauncherState>(File.ReadAllText(_statePath, Encoding.UTF8))
                : new LauncherState();
            if (_st.bad == null) _st.bad = new List<string>();
        }

        private static void SaveState()
        {
            string tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(_st), new UTF8Encoding(false));
            if (File.Exists(_statePath)) File.Replace(tmp, _statePath, null);
            else File.Move(tmp, _statePath);
        }

        private static string ExePath(string v) { return Path.Combine(_versionsDir, v, "BridgeAgent.exe"); }

        private static int RunAndWait(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static readonly object LogLock = new object();

        private static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    if (_logPath == null) return;
                    FileInfo fi = new FileInfo(_logPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = _logPath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_logPath, old);
                    }
                    File.AppendAllText(_logPath, DateTime.UtcNow.ToString("o") + " | " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
