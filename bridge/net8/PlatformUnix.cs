// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a: macOS and Linux adapters of the .NET 8 Bridge (see Platform.cs). Patterns from CO Resident
// (co-desktop/src/CO.Mac): secrets through /usr/bin/security with the value on stdin, LaunchAgent via launchctl
// bootstrap/bootout/kickstart in gui/<uid>, data under ~/Library/Application Support.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace AiWorkbenchBridge
{
    internal static class UnixNative
    {
        [DllImport("libc", EntryPoint = "getuid")]
        private static extern uint getuid();

        public static uint Uid()
        {
            try { return getuid(); }
            catch (DllNotFoundException) { return 0; }
            catch (EntryPointNotFoundException) { return 0; }
        }
    }

    // ==================================================================== macOS

    internal sealed class MacPlatform : IBridgePlatform
    {
        private readonly ISecretStore _secrets;
        private readonly IAutostart _autostart;
        private readonly IPlatformPaths _paths;
        private readonly IDesktopShell _shell;
        private readonly IExecutables _exes = new UnixExecutables();

        public MacPlatform(CommandRunner runner, string home)
        {
            _paths = UnixPaths.Mac(home);
            _secrets = new KeychainStore(runner);
            _autostart = new LaunchAgentAutostart(runner, home, _paths.LogRoot, UnixNative.Uid());
            _shell = new UnixShell(runner, "/usr/bin/open");
        }

        public string Id { get { return "macos"; } }
        public bool HasTray { get { return false; } }
        public ISecretStore Secrets { get { return _secrets; } }
        public IAutostart Autostart { get { return _autostart; } }
        public IPlatformPaths Paths { get { return _paths; } }
        public IDesktopShell Shell { get { return _shell; } }
        public IExecutables Executables { get { return _exes; } }
        public string[] NotSupported { get { return UnixLimits.Common; } }
    }

    internal static class UnixLimits
    {
        public static readonly string[] Common = new string[]
        {
            "tray menu (headless: status in tray.json; menu bar belongs to CO Resident)",
            "Connect Google from the tray (use --google-connect <client.json>)",
            "runAs (CLI under another OS account)",
            "launcher/updater (bridge/launcher is Windows-only; LaunchAgent/systemd restart instead)",
            "employer Outlook profile check (Windows registry)",
        };
    }

    // Login Keychain through /usr/bin/security. The secret goes through stdin (security -i) as hex (-X), so quotes,
    // backslashes and newlines of a JSON token need no escaping. Item: service = target, account = "default"
    // (as the aiwb/<service> keyring items of the tool library); reads match any account.
    internal sealed class KeychainStore : ISecretStore
    {
        public const string Security = "/usr/bin/security";
        public const string Account = "default";
        private const int ErrSecItemNotFound = 44;
        private readonly CommandRunner _runner;

        public KeychainStore(CommandRunner runner)
        {
            _runner = runner;
        }

        public string Name { get { return "macOS Keychain"; } }

        public string Read(string target)
        {
            CheckTarget(target);
            CommandRunner.Result r = _runner.Run(Security, new string[] { "find-generic-password", "-s", target, "-w" }, null, 30000);
            if (r.Code == 0)
                return r.Stdout.TrimEnd('\n', '\r');
            if (r.Code == ErrSecItemNotFound)
                return null;
            // Locked or refused Keychain, timeout: never report "missing".
            throw new InvalidOperationException("Keychain read failed (" + r.Code + "): " + r.Stderr.Trim());
        }

        public void Write(string target, string secret, string comment)
        {
            CheckTarget(target);
            if (secret == null)
                throw new ArgumentNullException("secret");
            string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(secret)).ToLowerInvariant();
            string label = String.IsNullOrEmpty(comment) ? target : comment;
            string line = "add-generic-password -U -s " + Quote(target) + " -a " + Quote(Account) + " -l " + Quote(label) + " -X " + hex + "\n";
            CommandRunner.Result r = _runner.Run(Security, new string[] { "-i" }, line, 30000);
            if (r.Code != 0)
                throw new InvalidOperationException("Keychain write failed (" + r.Code + "): " + r.Stderr.Trim());
            // security -w prints non-printable data as hex and a trailing newline is cut on read: check the item.
            if (Read(target) != secret.TrimEnd('\n', '\r'))
                throw new InvalidOperationException("Keychain write could not be read back unchanged (target " + target + ").");
        }

        public void Delete(string target)
        {
            CheckTarget(target);
            CommandRunner.Result r = _runner.Run(Security, new string[] { "delete-generic-password", "-s", target }, null, 30000);
            if (r.Code != 0 && r.Code != ErrSecItemNotFound)
                throw new InvalidOperationException("Keychain delete failed (" + r.Code + "): " + r.Stderr.Trim());
        }

        // security -i splits its input line like a shell; names are plain (no quotes, backslashes, newlines).
        internal static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        private static void CheckTarget(string target)
        {
            if (String.IsNullOrEmpty(target) || target.IndexOfAny(new char[] { '"', '\\', '\n', '\r' }) >= 0)
                throw new ArgumentException("Secret target name is empty or contains quotes, backslashes or newlines.");
        }
    }

    // launchd LaunchAgent: starts at login, restarts after a non-zero exit (crash 1, drained for update 3,
    // already running 4) but not after the user's Exit (0).
    internal sealed class LaunchAgentAutostart : IAutostart
    {
        public const string Launchctl = "/bin/launchctl";
        private readonly CommandRunner _runner;
        private readonly string _home;
        private readonly string _logRoot;
        private readonly uint _uid;

        public LaunchAgentAutostart(CommandRunner runner, string home, string logRoot, uint uid)
        {
            _runner = runner;
            _home = home;
            _logRoot = logRoot;
            _uid = uid;
        }

        public static string Label
        {
            get
            {
                string instance = Environment.GetEnvironmentVariable("AIWB_BRIDGE_INSTANCE");
                return !String.IsNullOrEmpty(instance) && Regex.IsMatch(instance, "^[a-z0-9]{1,20}$")
                    ? "org.aiworkbench.bridge-" + instance
                    : "org.aiworkbench.bridge";
            }
        }

        public string Name { get { return "macOS LaunchAgent"; } }
        public string Location { get { return Path.Combine(_home, "Library", "LaunchAgents", Label + ".plist"); } }
        private string Domain { get { return "gui/" + _uid.ToString(CultureInfo.InvariantCulture); } }

        public bool IsInstalled()
        {
            return File.Exists(Location);
        }

        public string Install(string executable, string[] arguments)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location));
            Directory.CreateDirectory(_logRoot);
            File.WriteAllText(Location, BuildPlist(Label, executable, arguments, _logRoot, AgentPath(_home)), new UTF8Encoding(false));
            _runner.Run(Launchctl, new string[] { "bootout", Domain + "/" + Label }, null, 30000);
            CommandRunner.Result r = _runner.Run(Launchctl, new string[] { "bootstrap", Domain, Location }, null, 30000);
            if (r.Code != 0)
                throw new InvalidOperationException("launchctl bootstrap failed (" + r.Code + "): " + r.Stderr.Trim());
            return "LaunchAgent " + Label + " installed";
        }

        public string Uninstall()
        {
            _runner.Run(Launchctl, new string[] { "bootout", Domain + "/" + Label }, null, 30000);
            if (File.Exists(Location))
                File.Delete(Location);
            return "LaunchAgent " + Label + " removed";
        }

        public string Kick()
        {
            if (!IsInstalled())
                return "LaunchAgent " + Label + " is not installed";
            CommandRunner.Result r = _runner.Run(Launchctl, new string[] { "kickstart", Domain + "/" + Label }, null, 30000);
            return r.Code == 0 ? "task " + Label + " started" : "launchctl kickstart exit " + r.Code + " " + r.Stderr.Trim();
        }

        // A LaunchAgent gets PATH=/usr/bin:/bin:/usr/sbin:/sbin; claude/codex/git/python live in Homebrew,
        // ~/.local/bin (claude installer) or the npm prefix.
        internal static string AgentPath(string home)
        {
            return String.Join(":", new string[]
            {
                home + "/.local/bin", "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin",
                home + "/.npm-global/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin",
            });
        }

        internal static string BuildPlist(string label, string executable, string[] arguments, string logRoot, string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            sb.Append("<plist version=\"1.0\">\n<dict>\n");
            sb.Append("  <key>Label</key><string>").Append(Esc(label)).Append("</string>\n");
            sb.Append("  <key>ProgramArguments</key><array><string>").Append(Esc(executable)).Append("</string>");
            foreach (string a in arguments ?? new string[0])
                sb.Append("<string>").Append(Esc(a)).Append("</string>");
            sb.Append("</array>\n");
            sb.Append("  <key>RunAtLoad</key><true/>\n");
            sb.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
            sb.Append("  <key>ThrottleInterval</key><integer>30</integer>\n");
            sb.Append("  <key>ProcessType</key><string>Interactive</string>\n");
            sb.Append("  <key>EnvironmentVariables</key><dict><key>PATH</key><string>").Append(Esc(path)).Append("</string></dict>\n");
            sb.Append("  <key>StandardOutPath</key><string>").Append(Esc(Path.Combine(logRoot, "bridge.out"))).Append("</string>\n");
            sb.Append("  <key>StandardErrorPath</key><string>").Append(Esc(Path.Combine(logRoot, "bridge.out"))).Append("</string>\n");
            sb.Append("</dict>\n</plist>\n");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            return SecurityElement.Escape(s ?? "");
        }
    }

    // ==================================================================== Linux

    internal sealed class LinuxPlatform : IBridgePlatform
    {
        private readonly ISecretStore _secrets;
        private readonly IAutostart _autostart;
        private readonly IPlatformPaths _paths;
        private readonly IDesktopShell _shell;
        private readonly IExecutables _exes = new UnixExecutables();

        public LinuxPlatform(CommandRunner runner, string home)
        {
            _paths = UnixPaths.Linux(home);
            _secrets = new SecretToolStore(runner);
            _autostart = new SystemdUserAutostart(runner, home);
            _shell = new UnixShell(runner, "xdg-open");
        }

        public string Id { get { return "linux"; } }
        public bool HasTray { get { return false; } }
        public ISecretStore Secrets { get { return _secrets; } }
        public IAutostart Autostart { get { return _autostart; } }
        public IPlatformPaths Paths { get { return _paths; } }
        public IDesktopShell Shell { get { return _shell; } }
        public IExecutables Executables { get { return _exes; } }
        public string[] NotSupported { get { return UnixLimits.Common; } }
    }

    // libsecret (GNOME Keyring, KWallet via the Secret Service API) through secret-tool; attributes as the Python
    // keyring library: service = target, username = "default". No secret-tool: NotSupportedException (no file fallback).
    internal sealed class SecretToolStore : ISecretStore
    {
        public const string Tool = "secret-tool";
        private readonly CommandRunner _runner;

        public SecretToolStore(CommandRunner runner)
        {
            _runner = runner;
        }

        public string Name { get { return "libsecret (secret-tool)"; } }

        public string Read(string target)
        {
            CommandRunner.Result r = Run(new string[] { "lookup", "service", target }, null);
            if (r.Code == 0)
                return r.Stdout.Length == 0 ? null : r.Stdout;
            if (r.Code == 1 && r.Stderr.Trim().Length == 0)
                return null;
            throw new InvalidOperationException("secret-tool lookup failed (" + r.Code + "): " + r.Stderr.Trim());
        }

        public void Write(string target, string secret, string comment)
        {
            if (secret == null)
                throw new ArgumentNullException("secret");
            string label = String.IsNullOrEmpty(comment) ? target : comment;
            CommandRunner.Result r = Run(new string[] { "store", "--label=" + label, "service", target, "username", "default" }, secret);
            if (r.Code != 0)
                throw new InvalidOperationException("secret-tool store failed (" + r.Code + "): " + r.Stderr.Trim());
        }

        public void Delete(string target)
        {
            CommandRunner.Result r = Run(new string[] { "clear", "service", target }, null);
            if (r.Code != 0 && r.Code != 1)
                throw new InvalidOperationException("secret-tool clear failed (" + r.Code + "): " + r.Stderr.Trim());
        }

        private CommandRunner.Result Run(string[] args, string stdin)
        {
            try
            {
                return _runner.Run(Tool, args, stdin, 30000);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new NotSupportedException("No secret store: secret-tool (libsecret) is not installed (" + ex.Message +
                    "). Install libsecret-tools and a Secret Service (GNOME Keyring); Bridge keeps no secrets in files.");
            }
        }
    }

    internal sealed class SystemdUserAutostart : IAutostart
    {
        public const string Unit = "aiworkbench-bridge.service";
        private readonly CommandRunner _runner;
        private readonly string _home;

        public SystemdUserAutostart(CommandRunner runner, string home)
        {
            _runner = runner;
            _home = home;
        }

        public string Name { get { return "systemd user unit"; } }

        public string Location
        {
            get
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (String.IsNullOrEmpty(config))
                    config = Path.Combine(_home, ".config");
                return Path.Combine(config, "systemd", "user", Unit);
            }
        }

        public bool IsInstalled()
        {
            return File.Exists(Location);
        }

        public string Install(string executable, string[] arguments)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location));
            File.WriteAllText(Location, BuildUnit(executable, arguments, UnitPath(_home)), new UTF8Encoding(false));
            Systemctl("daemon-reload");
            CommandRunner.Result r = Systemctl("enable", "--now", Unit);
            if (r.Code != 0)
                throw new InvalidOperationException("systemctl --user enable failed (" + r.Code + "): " + r.Stderr.Trim());
            return "systemd user unit " + Unit + " installed";
        }

        public string Uninstall()
        {
            Systemctl("disable", "--now", Unit);
            if (File.Exists(Location))
                File.Delete(Location);
            Systemctl("daemon-reload");
            return "systemd user unit " + Unit + " removed";
        }

        public string Kick()
        {
            if (!IsInstalled())
                return "systemd user unit " + Unit + " is not installed";
            CommandRunner.Result r = Systemctl("start", Unit);
            return r.Code == 0 ? "task " + Unit + " started" : "systemctl start exit " + r.Code + " " + r.Stderr.Trim();
        }

        private CommandRunner.Result Systemctl(params string[] args)
        {
            List<string> all = new List<string>();
            all.Add("--user");
            all.AddRange(args);
            return _runner.Run("systemctl", all, null, 30000);
        }

        // The user manager's PATH misses the claude installer (~/.local/bin) and the npm prefix, as on macOS.
        internal static string UnitPath(string home)
        {
            return String.Join(":", new string[]
            {
                home + "/.local/bin", home + "/.npm-global/bin", "/usr/local/sbin", "/usr/local/bin",
                "/usr/sbin", "/usr/bin", "/sbin", "/bin",
            });
        }

        // Restart=on-failure: as the LaunchAgent, a clean exit (tray Exit, code 0) stays stopped.
        internal static string BuildUnit(string executable, string[] arguments, string path)
        {
            StringBuilder cmd = new StringBuilder(QuoteArg(executable));
            foreach (string a in arguments ?? new string[0])
                cmd.Append(' ').Append(QuoteArg(a));
            return "[Unit]\nDescription=AI Workbench Bridge\nAfter=network-online.target\n\n" +
                "[Service]\nExecStart=" + cmd + "\nEnvironment=" + QuoteArg("PATH=" + path) +
                "\nRestart=on-failure\nRestartSec=30\n\n" +
                "[Install]\nWantedBy=default.target\n";
        }

        // systemd unit quoting; a newline would end the directive, so it is refused.
        internal static string QuoteArg(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new char[] { '\n', '\r' }) >= 0)
                throw new ArgumentException("A systemd unit argument cannot contain a newline.");
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";
        }
    }

    // ==================================================================== shared Unix pieces

    internal sealed class UnixPaths : IPlatformPaths
    {
        private readonly string _home;
        private readonly string _appData;     // %LOCALAPPDATA% / %APPDATA% / %ProgramData% of Windows configs
        private readonly string _stateRoot;
        private readonly string _logRoot;

        private UnixPaths(string home, string appData, string logRoot)
        {
            _home = home;
            _appData = appData;
            _stateRoot = Path.Combine(appData, "AIWorkbenchBridge");
            _logRoot = logRoot ?? Path.Combine(_stateRoot, "logs");
        }

        public static string HomeDirectory()
        {
            string home = Environment.GetEnvironmentVariable("HOME");
            if (String.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }

        public static UnixPaths Mac(string home)
        {
            return new UnixPaths(home, Path.Combine(home, "Library", "Application Support"),
                Path.Combine(home, "Library", "Logs", "AIWorkbenchBridge"));
        }

        public static UnixPaths Linux(string home)
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (String.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
                data = Path.Combine(home, ".local", "share");
            return new UnixPaths(home, data, null);
        }

        public string Home { get { return _home; } }
        public string StateRoot { get { return _stateRoot; } }

        public void EnsurePrivateDirectory(string path)
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path, Private);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, Private);   // an existing folder is narrowed too
        }
        public string SharedRoot { get { return _stateRoot; } }
        public string LogRoot { get { return _logRoot; } }

        private static readonly Regex WinVar = new Regex(@"%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.CultureInvariant);

        public string Normalize(string path)
        {
            if (String.IsNullOrEmpty(path))
                return path;
            string p = path;
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
                p = _home + p.Substring(1);
            p = WinVar.Replace(p, delegate(Match m)
            {
                string name = m.Groups[1].Value;
                switch (name.ToUpperInvariant())
                {
                    case "LOCALAPPDATA":
                    case "APPDATA":
                    case "PROGRAMDATA":
                        return _appData;
                    case "USERPROFILE":
                    case "HOME":
                        return _home;
                    case "TEMP":
                    case "TMP":
                        return Path.GetTempPath().TrimEnd('/');
                    default:
                        string v = Environment.GetEnvironmentVariable(name);
                        return v ?? m.Value;
                }
            });
            return p.Replace('\\', '/');
        }
    }

    internal sealed class UnixShell : IDesktopShell
    {
        private readonly CommandRunner _runner;
        private readonly string _opener;

        public UnixShell(CommandRunner runner, string opener)
        {
            _runner = runner;
            _opener = opener;
        }

        public void OpenUrl(string url)
        {
            Start(url);
        }

        public void OpenFolder(string path)
        {
            Start(path);
        }

        private void Start(string target)
        {
            // Not waited for: the opener returns at once, the browser stays.
            ProcessStartInfo psi = new ProcessStartInfo(_opener);
            psi.ArgumentList.Add(target);
            psi.UseShellExecute = false;
            using (Process.Start(psi)) { }
        }
    }

    internal sealed class UnixExecutables : IExecutables
    {
        public string Git { get { return "git"; } }
        public string Python { get { return "python3"; } }

        // Claude Code runs hook commands through sh; single quotes inside are closed, escaped and reopened.
        public string CatFileCommand(string path)
        {
            return "cat " + ShellQuote(path);
        }

        public static string ShellQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }
    }

    // The Job Object of macOS/Linux: the root process and every descendant seen while it ran. Terminate and Dispose
    // (kill-on-close as the Windows job) kill them. Every process is kept with its start time from the process
    // table; a reused pid never matches (review F3a-1):
    //   - a look first drops entries whose pid is gone or has another start time, and walks only from the rest;
    //   - a child is taken only if it started at or after its parent (a reused parent pid has no older children);
    //   - pid, parent and start time come from one snapshot; right before a kill the start time is read again.
    // A process that left the tree before it was seen (double fork between two looks) escapes.
    internal sealed class UnixProcessTree : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, long> _known = new Dictionary<int, long>();
        private readonly IProcessTable _table;
        private readonly Action<int> _kill;
        private bool _disposed;

        public UnixProcessTree() : this(UnixProcessTable.Default, KillPid) { }

        internal UnixProcessTree(IProcessTable table, Action<int> kill)
        {
            _table = table;
            _kill = kill;
        }

        public void Assign(Process process)
        {
            AssignPid(process.Id);
        }

        internal void AssignPid(int pid)
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException("UnixProcessTree");
                long start;
                if (_table.TryGetStart(pid, out start))
                    _known[pid] = start;
            }
        }

        public int ActiveProcesses()
        {
            lock (_sync)
            {
                if (_disposed)
                    return -1;
                try
                {
                    Refresh(_table.Snapshot());
                    return _known.Count;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public void Terminate(uint exitCode)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                KillAll();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                KillAll();
                _disposed = true;
            }
        }

        private void KillAll()
        {
            try { Refresh(_table.Snapshot()); } catch { }
            foreach (KeyValuePair<int, long> kv in new List<KeyValuePair<int, long>>(_known))
            {
                try
                {
                    long now;
                    if (_table.TryGetStart(kv.Key, out now) && now == kv.Value)
                        _kill(kv.Key);
                }
                catch { }
            }
        }

        // Drops gone or reused pids, then adds the descendants of the remaining ones from the same snapshot.
        internal void Refresh(Dictionary<int, ProcessEntry> snapshot)
        {
            foreach (KeyValuePair<int, long> kv in new List<KeyValuePair<int, long>>(_known))
            {
                ProcessEntry e;
                if (!snapshot.TryGetValue(kv.Key, out e) || e.Start != kv.Value)
                    _known.Remove(kv.Key);
            }

            Dictionary<int, List<int>> children = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<int, ProcessEntry> kv in snapshot)
            {
                List<int> l;
                if (!children.TryGetValue(kv.Value.Ppid, out l))
                    children[kv.Value.Ppid] = l = new List<int>();
                l.Add(kv.Key);
            }

            Queue<int> queue = new Queue<int>(_known.Keys);
            while (queue.Count > 0)
            {
                int pid = queue.Dequeue();
                long parentStart = _known[pid];
                List<int> kids;
                if (!children.TryGetValue(pid, out kids))
                    continue;
                foreach (int kid in kids)
                {
                    if (_known.ContainsKey(kid))
                        continue;
                    long kidStart = snapshot[kid].Start;
                    if (kidStart < parentStart)
                        continue;   // older than its "parent": the parent pid was reused
                    _known[kid] = kidStart;
                    queue.Enqueue(kid);
                }
            }
        }

        internal IDictionary<int, long> Known { get { return _known; } }

        private static void KillPid(int pid)
        {
            using (Process p = Process.GetProcessById(pid))
                p.Kill();   // SIGKILL; the tree is walked here, not by .NET
        }
    }

    internal struct ProcessEntry
    {
        public int Ppid;
        public long Start;   // Linux: clock ticks after boot (/proc stat field 22); macOS: Unix seconds of ps lstart

        public ProcessEntry(int ppid, long start)
        {
            Ppid = ppid;
            Start = start;
        }
    }

    internal interface IProcessTable
    {
        Dictionary<int, ProcessEntry> Snapshot();
        bool TryGetStart(int pid, out long start);
    }

    // Linux: /proc/<pid>/stat; macOS: ps (LC_ALL=C so lstart parses).
    internal sealed class UnixProcessTable : IProcessTable
    {
        public static readonly UnixProcessTable Default = new UnixProcessTable();

        private static bool HasProc { get { return Directory.Exists("/proc/self"); } }

        public Dictionary<int, ProcessEntry> Snapshot()
        {
            Dictionary<int, ProcessEntry> map = new Dictionary<int, ProcessEntry>();
            if (HasProc)
            {
                foreach (string dir in Directory.GetDirectories("/proc"))
                {
                    int pid;
                    ProcessEntry e;
                    if (Int32.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out pid) && TryReadStat(pid, out e))
                        map[pid] = e;
                }
                return map;
            }
            foreach (KeyValuePair<int, ProcessEntry> kv in RunPs(null))
                map[kv.Key] = kv.Value;
            return map;
        }

        public bool TryGetStart(int pid, out long start)
        {
            start = 0;
            ProcessEntry e;
            if (HasProc)
            {
                if (!TryReadStat(pid, out e))
                    return false;
                start = e.Start;
                return true;
            }
            foreach (KeyValuePair<int, ProcessEntry> kv in RunPs(pid))
            {
                if (kv.Key == pid)
                {
                    start = kv.Value.Start;
                    return true;
                }
            }
            return false;
        }

        // "pid (comm) state ppid ... starttime": comm may hold spaces and ')', so fields count from the last ')'.
        internal static bool TryParseStat(string stat, out ProcessEntry e)
        {
            e = new ProcessEntry();
            int close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 > stat.Length)
                return false;
            string[] f = stat.Substring(close + 2).Split(' ');
            int ppid;
            long start;
            if (f.Length < 20 || !Int32.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ppid) ||
                !Int64.TryParse(f[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                return false;
            if (f[0] == "Z" || f[0] == "X")
                return false;   // zombie or dead: nothing left to kill or count
            e = new ProcessEntry(ppid, start);
            return true;
        }

        private static bool TryReadStat(int pid, out ProcessEntry e)
        {
            e = new ProcessEntry();
            try
            {
                return TryParseStat(File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"), out e);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static List<KeyValuePair<int, ProcessEntry>> RunPs(int? pid)
        {
            List<string> args = new List<string> { "LC_ALL=C", "/bin/ps" };
            if (pid.HasValue)
            {
                args.Add("-p");
                args.Add(pid.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
                args.Add("-A");
            args.AddRange(new string[] { "-o", "pid=", "-o", "ppid=", "-o", "stat=", "-o", "lstart=" });
            CommandRunner.Result r = CommandRunner.Default.Run("/usr/bin/env", args, null, 15000);
            List<KeyValuePair<int, ProcessEntry>> list = new List<KeyValuePair<int, ProcessEntry>>();
            foreach (string line in r.Stdout.Split('\n'))
            {
                int p;
                ProcessEntry e;
                if (TryParsePsLine(line, out p, out e))
                    list.Add(new KeyValuePair<int, ProcessEntry>(p, e));
            }
            return list;
        }

        // "  123     1 Ss   Wed Nov 12 10:00:00 2026"
        internal static bool TryParsePsLine(string line, out int pid, out ProcessEntry e)
        {
            pid = 0;
            e = new ProcessEntry();
            string[] f = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int ppid;
            DateTime started;
            if (f.Length != 8 || !Int32.TryParse(f[0], out pid) || !Int32.TryParse(f[1], out ppid) || f[2].StartsWith("Z", StringComparison.Ordinal))
                return false;
            if (!DateTime.TryParseExact(String.Join(" ", f, 3, 5), "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out started))
                return false;
            e = new ProcessEntry(ppid, new DateTimeOffset(started).ToUnixTimeSeconds());
            return true;
        }
    }
}
