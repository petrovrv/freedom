// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
#if BRIDGE_HEADLESS
// .NET 8 on macOS/Linux (Freedom F3a): the WinForms subset of the tray without a desktop, net8/HeadlessUi.cs.
using AiWorkbenchBridge.HeadlessUi;
using UiTimer = AiWorkbenchBridge.HeadlessUi.Timer;
#else
using System.Drawing;
using System.Windows.Forms;
using UiTimer = System.Windows.Forms.Timer;
#endif
using Microsoft.Win32.SafeHandles;

namespace AiWorkbenchBridge
{
    /*
     * AUDIT CONTEXT
     * =============
     * This program is intentionally an owner-controlled remote execution bridge.
     * A committed job in transport/hosts/<hostId>/inbox is a command from the owner
     * and may instruct a locally configured AI agent to edit the selected workspace
     * and use locally enabled tools. That capability is the product requirement.
     *
     * Audit for unintended authority: an untrusted writer reaching the inbox, a job
     * selecting an executable/path/capability outside local config, workspace escape,
     * credential export, lifecycle failures, or protocol corruption. environmentId is
     * a context partition for work performed by the same owner; it is not an auth user.
     *
     * Content read by an agent (repository files, dependencies, web content) can itself
     * contain instructions. The owner accepts that residual prompt-injection risk for
     * projects where shell/tool access is enabled. See AUDIT-CONTEXT.md for the canonical
     * trust model and accepted residual risks. Do not report the intended command-channel
     * behavior itself as an authorization defect.
     */

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
#if NET8_0_OR_GREATER
            // .NET 8 (Freedom F3a): --platform-info, --install-autostart, --uninstall-autostart, --google-connect.
            if (PlatformCommands.TryRun(Environment.GetCommandLineArgs()))
                return;
#endif
            // v1.0: "--mcp-device" runs the stdio MCP proxy for one device (no tray, no mutex).
            if (McpDeviceProxy.TryRun(Environment.GetCommandLineArgs()))
                return;

            // v1.1.5: "--validate-config" loads+validates config.json, writes config-check.txt next to it and
            // exits 0 (valid) / 1 (invalid). No tray, no mutex, no Drive. Used by the updater to gate a new build.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--validate-config") >= 0)
            {
                string resultPath = Path.Combine(Path.GetDirectoryName(ConfigLocation.Resolve()), "config-check.txt");
                try
                {
                    BridgeApplicationContext ctx = new BridgeApplicationContext(true);
                    ctx.Dispose();
                    try { File.WriteAllText(resultPath, "OK"); } catch { }
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(resultPath, "ERROR: " + ex.Message); } catch { }
                    Environment.Exit(1);
                }
                return;
            }

            BootstrapLog.Write("BridgeAgent process starting.");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                BootstrapLog.Write("UI thread exception: " + e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                BootstrapLog.Write("Unhandled exception: " + Convert.ToString(e.ExceptionObject));
            };

            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                BootstrapLog.Write("Unobserved task exception: " + e.Exception);
                e.SetObserved();
            };

            // Global\ so a logon-task launch and an interactive launch in different sessions still collide.
            // v1.3.0: AIWB_BRIDGE_INSTANCE=<a-z0-9> runs a separate test instance (own --config, own hostId).
            string instance = Environment.GetEnvironmentVariable("AIWB_BRIDGE_INSTANCE");
            string mutexName = "Global\\AiWorkbenchBridgeAgent";
            if (!String.IsNullOrEmpty(instance) && System.Text.RegularExpressions.Regex.IsMatch(instance, "^[a-z0-9]{1,20}$"))
                mutexName += "-" + instance;
            bool createdNew;
            using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // A redundant instance (autostart firing while already running, a double launch, a watchdog).
                    // Exit immediately and never block on a modal dialog: launched headless nobody clicks OK, so the
                    // process would linger at 0% CPU forever and pile up as duplicates.
                    BootstrapLog.Write("Another BridgeAgent instance is already running; this one exits.");
                    Environment.ExitCode = ExitCodes.AlreadyRunning;
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                BridgeApplicationContext context;
                try
                {
                    context = new BridgeApplicationContext();
                }
                catch (OperationCanceledException)
                {
                    // The user chose Exit from the tray while startup was waiting for Drive.
                    BootstrapLog.Write("Startup cancelled by the user.");
                    Environment.ExitCode = ExitCodes.UserExit;
                    return;
                }
                catch (Exception ex)
                {
                    BootstrapLog.Write("Fatal startup error: " + ex);
                    Environment.ExitCode = ExitCodes.Fatal;
                    MessageBox.Show(
                        ex.Message,
                        "AI Workbench Bridge - startup error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                Application.Run(context);
            }
        }
    }

    // v1.1.7: process exit codes, read by the launcher (AIWorkbenchBridge.exe).
    internal static class ExitCodes
    {
        public const int UserExit = 0;          // tray Exit: the launcher stops too
        public const int Fatal = 1;             // startup error or crash: the launcher restarts or rolls back
        public const int DrainedForUpdate = 3;  // drain.request honoured, no job running: start the new version
        public const int AlreadyRunning = 4;    // another Bridge holds the mutex: the launcher waits and retries
    }

    // v1.1.7: config.json location. Order: "--config <path>", %LOCALAPPDATA%\AIWorkbenchBridge\config.json
    // (installed layout: the exe lives in a per-version folder), config.json next to the exe (old layout).
    // Relative paths inside the config resolve against the config folder.
    internal static class ConfigLocation
    {
        public static string Resolve()
        {
            string[] argv = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(argv, "--config");
            if (i >= 0 && i + 1 < argv.Length && !String.IsNullOrWhiteSpace(argv[i + 1]))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(argv[i + 1]));

            string installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIWorkbenchBridge", "config.json");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                installed = Path.Combine(BridgePlatform.Current.Paths.StateRoot, "config.json");
#endif
            if (File.Exists(installed))
                return installed;

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }
    }

    // v1.8.0: read-guard (tools\read-guard) checks what agents read from outside (web, mail, chats, downloads) for
    // prompt injection: Claude Code hooks wrap suspicious tool results as data and hold sending/buying/secret
    // actions of a tainted chat until the owner confirms in the chat. Codex gets no hooks: -c hooks need
    // --dangerously-bypass-hook-trust, which would also trust unvetted user/project hooks; its tools check on their own. AIWB_READ_GUARD = script path, or "off".
    // Default: the tool library working copy, else its Drive mirror under driveRoot\tools.
    internal static class ReadGuard
    {
        public const string DefaultScript = @"C:\AIWorkbench\tools\read-guard\read-guard.py";
        private const int MaxForwardBytes = 16 * 1024;

        // Set once at startup from config.driveRoot.
        public static string DriveRoot;

        // Set once at startup: agents under runAs (another Windows account) may not read Bridge's settings file or
        // write into its job folder, so read-guard stays off for them until that is supported.
        public static bool RunAsActive;

        public static string ScriptPath
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("AIWB_READ_GUARD");
                if (RunAsActive || String.Equals(path, "off", StringComparison.OrdinalIgnoreCase))
                    return null;
                try
                {
                    if (!String.IsNullOrWhiteSpace(path))
                        return File.Exists(path) ? Path.GetFullPath(path) : null;
                    if (File.Exists(DefaultScript))
                        return DefaultScript;
                    if (!String.IsNullOrEmpty(DriveRoot))
                    {
                        path = Path.Combine(Path.Combine(Path.Combine(DriveRoot, "tools"), "read-guard"), "read-guard.py");
                        if (File.Exists(path))
                            return Path.GetFullPath(path);
                    }
                    return null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        // Forward slashes: the command runs in Git Bash or cmd alike.
        public static string Command(string script, string kind)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return BridgePlatform.Current.Executables.Python + " " + UnixExecutables.ShellQuote(script) + " hook " + kind;
#endif
            return "python \"" + script.Replace('\\', '/') + "\" hook " + kind;
        }

        public static Dictionary<string, object> ClaudeMatcher(string toolPattern, string kind, string script)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["type"] = "command";
            entry["command"] = Command(script, kind);
            entry["timeout"] = 30;
            Dictionary<string, object> matcher = new Dictionary<string, object>();
            matcher["matcher"] = toolPattern;
            matcher["hooks"] = new object[] { entry };
            return matcher;
        }

        // Each *.json file read-guard dropped = one event "guard"/"read_guard"; the file is deleted after it.
        public static void ForwardEvents(string dir, EventBatchWriter writer)
        {
            ForwardEvents(dir, writer, false);
        }

        // final = last pass before the job folder is cleaned up: events the writer cannot take any more are moved to
        // %LOCALAPPDATA%\AIWorkbenchGuard\unsent (read-guard's own events.jsonl journal keeps them as well).
        public static void ForwardEvents(string dir, EventBatchWriter writer, bool final)
        {
            try
            {
                if (writer == null || !Directory.Exists(dir))
                    return;
                string[] files = Directory.GetFiles(dir, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(file, Encoding.UTF8);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (text.Length > MaxForwardBytes)
                        text = text.Substring(0, MaxForwardBytes);
                    // The file goes only after the writer took the event; a full buffer leaves it for the next pass.
                    if (!writer.TryWrite("guard", "read_guard", text.Trim()))
                    {
                        if (final)
                            KeepUnsent(dir);
                        return;
                    }
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception)
            {
                // the guard's events must never break a job
            }
        }

        private static void KeepUnsent(string dir)
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrEmpty(root))
                    root = Path.GetTempPath();
                string keep = Path.Combine(Path.Combine(root, "AIWorkbenchGuard"), "unsent");
                Directory.CreateDirectory(keep);
                // <job dir>\guard-events: the job folder name keeps files of different jobs apart
                string prefix = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\'))) + "-";
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        File.Move(file, Path.Combine(keep, prefix + Path.GetFileName(file)));
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }


    // v1.2.4: content-addressed hook and settings files that inject context into Claude subagents.
    // %LOCALAPPDATA%\AIWorkbenchBridge\subagent-hooks\<key>.settings.json is passed with --settings; its
    // SubagentStart hook prints <key>.hook.json ({"hookSpecificOutput":{"additionalContext":...}}).
    internal static class SubagentHookFiles
    {
        private static readonly object Sync = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Ensure(string context)
        {
            return Ensure(context, false);
        }

        // v1.8.0: context null/empty = no SubagentStart hook; guard = read-guard hooks (PostToolUse/PreToolUse) in the same file.
        public static string Ensure(string context, bool guard)
        {
            if (context == null)
                context = "";
            string guardScript = guard ? ReadGuard.ScriptPath : null;

            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(root))
                root = Path.GetTempPath();
            string dir = Path.Combine(Path.Combine(root, "AIWorkbenchBridge"), "subagent-hooks");

            string key = ComputeKey(context + "\n" + (guardScript ?? ""));
            string hookPath = Path.GetFullPath(Path.Combine(dir, key + ".hook.json"));
            string settingsPath = Path.GetFullPath(Path.Combine(dir, key + ".settings.json"));

            Dictionary<string, object> hookOutput = new Dictionary<string, object>();
            hookOutput["hookEventName"] = "SubagentStart";
            hookOutput["additionalContext"] = context;
            Dictionary<string, object> hookRoot = new Dictionary<string, object>();
            hookRoot["hookSpecificOutput"] = hookOutput;
            string hookJson = JsonUtil.Serialize(hookRoot);

            // Single quotes inside a PowerShell single-quoted literal are doubled.
            string command = "powershell -NoProfile -NonInteractive -Command \"Get-Content -Raw -Encoding UTF8 -LiteralPath '"
                + hookPath.Replace("'", "''") + "'\"";
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                command = BridgePlatform.Current.Executables.CatFileCommand(hookPath);
#endif

            Dictionary<string, object> hooks = new Dictionary<string, object>();
            if (context.Length > 0)
            {
                Dictionary<string, object> hookEntry = new Dictionary<string, object>();
                hookEntry["type"] = "command";
                hookEntry["command"] = command;
                Dictionary<string, object> matcher = new Dictionary<string, object>();
                matcher["hooks"] = new object[] { hookEntry };
                hooks["SubagentStart"] = new object[] { matcher };
            }
            if (guardScript != null)
            {
                hooks["PostToolUse"] = new object[] { ReadGuard.ClaudeMatcher(
                    "Bash|PowerShell|WebFetch|WebSearch|Read|mcp__.*", "claude-post", guardScript) };
                // failed calls carry their output in the error text, which only this event sees
                hooks["PostToolUseFailure"] = new object[] { ReadGuard.ClaudeMatcher(
                    "Bash|PowerShell|WebFetch|WebSearch|Read|mcp__.*", "claude-fail", guardScript) };
                hooks["PreToolUse"] = new object[] { ReadGuard.ClaudeMatcher(
                    "Bash|PowerShell|WebFetch|Read|Write|Edit|MultiEdit|NotebookEdit|mcp__.*", "claude-pre", guardScript) };
            }
            Dictionary<string, object> settingsRoot = new Dictionary<string, object>();
            settingsRoot["hooks"] = hooks;
            string settingsJson = JsonUtil.Serialize(settingsRoot);

            lock (Sync)
            {
                Directory.CreateDirectory(dir);
                if (context.Length > 0)
                    WriteIfChanged(dir, hookPath, hookJson);
                WriteIfChanged(dir, settingsPath, settingsJson);
            }
            return settingsPath;
        }

        private static string ComputeKey(string context)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(context));
            StringBuilder sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void WriteIfChanged(string dir, string path, string content)
        {
            byte[] bytes = Utf8NoBom.GetBytes(content);
            if (File.Exists(path))
            {
                try
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length == bytes.Length && existing.SequenceEqual(bytes))
                        return;
                }
                catch (IOException)
                {
                }
            }

            // Atomic write through a temp file in the same directory.
            string temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (IOException)
                    {
                        File.Copy(temp, path, true);
                    }
                }
                else
                {
                    File.Copy(temp, path, true);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static class BootstrapLog
    {
        private static readonly object Sync = new object();

        public static void Write(string message)
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AIWorkbenchBridge");
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "bootstrap.log");

                lock (Sync)
                {
                    File.AppendAllText(
                        path,
                        DateTime.UtcNow.ToString("o") + " | " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }

    internal sealed partial class BridgeApplicationContext : ApplicationContext
    {
        private const string BridgeVersion = "1.16.0";
        private const int ProtocolMinor = 2;

        private static readonly string[] KnownModes = new string[] { "chat", "cowork" };

        // Catalog ids go into agent arguments after validation; keep them conservative.
        private static readonly Regex CatalogIdPattern =
            new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._:\-]{0,79}\z", RegexOptions.CultureInvariant);

        private readonly string _appDir;
        private readonly string _configPath;

        private BridgeConfig _config;

        private string _driveRoot;
        private string _projectsDir;
        private string _hostTransportRoot;
        private string _inboxDir;
        private string _claimedDir;
        private string _eventsDir;
        private string _completedDir;
        private string _failedDir;
        private string _rejectedDir;
        private string _cancelDir;

        private string _localStateRoot;
        private string _localLogDir;
        private string _localJobsDir;
        private readonly ConcurrentDictionary<string, object> _sessionLocks = new ConcurrentDictionary<string, object>();
        private string _usedJobIdsPath;
        private string _hostOwnerPath;

        private NotifyIcon _tray;
        private ToolStripMenuItem _statusItem;
        private ToolStripMenuItem _pauseItem;
        private ToolStripMenuItem _transportItem;
        private FileSystemWatcher _watcher;
        private System.Threading.Timer _scanTimer;
        private UiTimer _uiTimer;

        private readonly object _scanLock = new object();
        private readonly object _logLock = new object();
        private readonly object _usedJobIdsLock = new object();

        private readonly HashSet<string> _usedJobIds =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, byte> _runningJobs =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, WindowsJobObject> _runningJobObjects =
            new ConcurrentDictionary<string, WindowsJobObject>(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, byte> _cancelRequested =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private volatile bool _paused;
        private volatile bool _exiting;
        private volatile bool _driveOnline;
        private volatile bool _hostConflict;
        private volatile int _queuedJobs;
        private volatile bool _draining;
        private volatile string _updateTo;          // v1.4.2: version named in drain.request
        private DateTime _stateSinceUtc = DateTime.UtcNow; // v1.4.2: when running/paused/updating last changed
        private DateTime _lastDrainCheckUtc = DateTime.MinValue;
        private DateTime _lastCleanupUtc = DateTime.MinValue;
        private DateTime _lastHostHeartbeatUtc = DateTime.MinValue;

        private readonly object _strandedLogLock = new object();
        private readonly Dictionary<string, DateTime> _strandedLoggedUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public BridgeApplicationContext() : this(false) { }

        public BridgeApplicationContext(bool validateOnly)
        {
            _configPath = ConfigLocation.Resolve();
            _appDir = Path.GetDirectoryName(_configPath);

            LoadConfiguration();
            // v1.2.7: push notification when Google sign-in breaks
            GoogleAuth.AuthFailed += NotifyGoogleAuthFailed;

            // --validate-config: parse + validate config (incl. per-agent checks) then stop before any side effect
            // (no tray, no Drive wait, no job claiming). Lets the updater verify a new build accepts the live config
            // BEFORE it replaces the running Bridge — the real-start check an earlier gateway-runtime incident lacked.
            if (validateOnly)
                return;

            _localStateRoot = ExpandPath(
                String.IsNullOrWhiteSpace(_config.localStateRoot)
                    ? "%LOCALAPPDATA%\\AIWorkbenchBridge"
                    : _config.localStateRoot,
                _appDir);

            _localLogDir = Path.Combine(_localStateRoot, "logs");
            _localJobsDir = Path.Combine(_localStateRoot, "jobs");
            _usedJobIdsPath = Path.Combine(_localStateRoot, "used-jobids.log");

#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                BridgePlatform.Current.Paths.EnsurePrivateDirectory(_localStateRoot);   // 0700: tokens, logs, jobs
#endif
            Directory.CreateDirectory(_localLogDir);
            Directory.CreateDirectory(_localJobsDir);
            LoadUsedJobIds();

            _driveRoot = ExpandPath(_config.driveRoot, _appDir);
            ReadGuard.DriveRoot = _driveRoot;
            ReadGuard.RunAsActive = _config.runAs != null && !String.IsNullOrWhiteSpace(_config.runAs.user);
            InitializeTray();

            try
            {
                WaitForDriveReadyPath();
                PrepareDirectories();
                EnsureHostOwnership(true);
                RecoverClaimedJobs();
                LoadUpdateContinuations(); // v1.16.0: jobs paused by the previous process for its update go first
                CleanupLocalLogs();
            }
            catch
            {
                try
                {
                    if (_tray != null)
                    {
                        _tray.Visible = false;
                        _tray.Dispose();
                        _tray = null;
                    }
                }
                catch
                {
                }

                throw;
            }

            InitializeWatcher();
            InitializeTimers();

            Log("BridgeAgent v" + BridgeVersion + " started. hostId=" + _config.hostId);
            EnsureCloudConnectorsFile();
            StartFastTransport();
            StartDbQueue(); // v1.3.0: job queue in the database
            StartDeviceHub();
            StartSupervisor(); // v1.9.0: launcher watchdog, "alive but not working", stuck jobs
            QueueScan();
        }

        // Freedom F1: the CO site of this install (no domain in the code). siteUrl, else the origin of dbQueue.url or
        // push.url; empty urls of dbQueue and push are made from it. The owner's environment: push.ownerEnv, else the
        // first environment of chatProjectEnvironments.
        internal static void NormalizeSite(BridgeConfig c)
        {
            string site = SiteBase(c);
            if (c.dbQueue != null && String.IsNullOrEmpty(c.dbQueue.url) && site.Length > 0)
                c.dbQueue.url = site + "/agent/db.php";
            if (c.push != null && String.IsNullOrEmpty(c.push.url) && site.Length > 0)
                c.push.url = site + "/agent/push.php";
            if (c.push != null && String.IsNullOrEmpty(c.push.ownerEnv))
                c.push.ownerEnv = OwnerEnvOf(c);
        }

        internal static string SiteBase(BridgeConfig c)
        {
            if (c == null) return "";
            if (!String.IsNullOrEmpty(c.siteUrl)) return c.siteUrl.TrimEnd('/');
            foreach (string u in new[] { c.dbQueue != null ? c.dbQueue.url : null, c.push != null ? c.push.url : null })
            {
                Uri uri;
                if (!String.IsNullOrEmpty(u) && Uri.TryCreate(u, UriKind.Absolute, out uri))
                    return uri.GetLeftPart(UriPartial.Authority);
            }
            return "";
        }

        internal static string OwnerEnvOf(BridgeConfig c)
        {
            if (c == null) return "";
            if (c.push != null && !String.IsNullOrEmpty(c.push.ownerEnv)) return c.push.ownerEnv;
            if (c.chatProjectEnvironments != null)
                foreach (string k in c.chatProjectEnvironments.Keys) return k;
            return "";
        }

        private void LoadConfiguration()
        {
            if (!File.Exists(_configPath))
            {
                throw new InvalidOperationException(
                    "config.json was not found: " + _configPath + "\r\n" +
                    "Copy config.json.example to config.json and edit driveRoot/workspaces.");
            }

            string json = File.ReadAllText(_configPath, Encoding.UTF8);
            _config = JsonUtil.Deserialize<BridgeConfig>(json);

            if (_config == null)
                throw new InvalidOperationException("config.json could not be parsed.");

            if (_config.schemaVersion != 2)
                throw new InvalidOperationException("Unsupported config schemaVersion. Expected 2.");

            ValidateSafeId(_config.hostId, "hostId");

            if (String.IsNullOrWhiteSpace(_config.driveRoot))
                throw new InvalidOperationException("config.driveRoot is required.");

            NormalizeSite(_config);

            if (_config.scanIntervalSeconds <= 0)
                _config.scanIntervalSeconds = 10;

            if (_config.eventFlushSeconds <= 0)
                _config.eventFlushSeconds = 2;

            if (_config.eventFlushBytes <= 0)
                _config.eventFlushBytes = 65536;

            if (_config.eventMaxPendingBytes <= 0)
                _config.eventMaxPendingBytes = 16 * 1024 * 1024;

            if (_config.outputDrainTimeoutSeconds <= 0)
                _config.outputDrainTimeoutSeconds = 30;

            if (_config.hostLeaseSeconds <= 0)
                _config.hostLeaseSeconds = 300;

            if (_config.maxParallelJobs <= 0)
                _config.maxParallelJobs = 2;

            if (_config.maxProcessTimeoutMinutes <= 0)
                _config.maxProcessTimeoutMinutes = 120;

            if (_config.startupDriveWaitSeconds < 0)
                _config.startupDriveWaitSeconds = 0;

            if (_config.maxArtifactDestinationPathChars <= 0)
                _config.maxArtifactDestinationPathChars = 240;

            if (_config.transportRetentionDays <= 0)
                _config.transportRetentionDays = 30;

            if (_config.localLogRetentionDays <= 0)
                _config.localLogRetentionDays = 14;

            if (_config.autoExportMaxFiles <= 0)
                _config.autoExportMaxFiles = 50;

            if (_config.autoExportMaxBytes <= 0)
                _config.autoExportMaxBytes = 200L * 1024 * 1024;

            if (_config.autoExportMaxFileBytes <= 0)
                _config.autoExportMaxFileBytes = 100L * 1024 * 1024;

            if (_config.autoExportMaxScanEntries <= 0)
                _config.autoExportMaxScanEntries = 200000;

            if (_config.contextIndexMaxEntries <= 0)
                _config.contextIndexMaxEntries = 20000;

            if (_config.contextIndexRefreshHours <= 0)
                _config.contextIndexRefreshHours = 6;

            if (_config.maxContextFolders <= 0)
                _config.maxContextFolders = 10;

            if (_config.agents == null || _config.agents.Count == 0)
                throw new InvalidOperationException("At least one agent profile is required.");

            if (_config.projects == null || _config.projects.Count == 0)
                throw new InvalidOperationException("At least one project profile is required.");

            foreach (KeyValuePair<string, AgentProfile> pair in _config.agents)
            {
                ValidateSafeId(pair.Key, "agent profile id");

                AgentProfile agent = pair.Value;
                if (agent == null)
                    throw new InvalidOperationException("Agent '" + pair.Key + "' is null.");
                // The corporate gateway runtime (kind "corporate", legacy "engie") is driven by Bridge itself — no executable.
                if (!Corporate.IsGatewayRuntime(agent.kind) && String.IsNullOrWhiteSpace(agent.executable))
                    throw new InvalidOperationException(
                        "Agent '" + pair.Key + "' has no executable.");

                if (agent.arguments == null)
                    agent.arguments = new string[0];

                if (String.IsNullOrWhiteSpace(agent.outputProtocol))
                    agent.outputProtocol = "raw";

                // v1.4.0: protocols are the AgentRuntime classes (BridgeAgentRuntimes.cs).
                string protocol = agent.outputProtocol;
                if (!AgentRuntime.IsKnown(protocol))
                {
                    throw new InvalidOperationException(
                        "Unsupported outputProtocol for agent '" + pair.Key + "': " + protocol);
                }

                ValidateAgentV06(pair.Key, agent);
            }

            foreach (KeyValuePair<string, ProjectProfile> pair in _config.projects)
            {
                ValidateSafeId(pair.Key, "project profile id");

                ProjectProfile project = pair.Value;
                if (project == null)
                    throw new InvalidOperationException(
                        "Project '" + pair.Key + "' is null.");

                ValidateSafeId(project.environmentId, "project.environmentId");

                if (String.IsNullOrWhiteSpace(project.workspace))
                    throw new InvalidOperationException(
                        "Project '" + pair.Key + "' has no workspace.");

                project.workspace = ExpandPath(project.workspace, _appDir);

                if (project.allowedAgents == null || project.allowedAgents.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Project '" + pair.Key + "' must have at least one allowed agent.");
                }

                for (int i = 0; i < project.allowedAgents.Length; i++)
                {
                    ValidateSafeId(project.allowedAgents[i], "allowed agent id");

                    if (!_config.agents.ContainsKey(project.allowedAgents[i]))
                    {
                        throw new InvalidOperationException(
                            "Project '" + pair.Key + "' references unknown agent '" +
                            project.allowedAgents[i] + "'.");
                    }
                }

                // v0.5 configs have no allowedModes: those projects keep the v0.5
                // behaviour (agent runs in the project workspace).
                if (project.allowedModes == null || project.allowedModes.Length == 0)
                    project.allowedModes = new string[] { "cowork" };

                foreach (string mode in project.allowedModes)
                {
                    if (Array.IndexOf(KnownModes, mode) < 0)
                    {
                        throw new InvalidOperationException(
                            "Project '" + pair.Key + "' has unknown mode '" + mode +
                            "'. Allowed: chat, cowork.");
                    }
                }
            }

            ValidateChatProjectPolicies();
            ValidateContextRoots();
        }

        private void ValidateContextRoots()
        {
            if (_config.contextRoots == null)
                return;

            foreach (KeyValuePair<string, ContextRoot> pair in _config.contextRoots)
            {
                ValidateSafeId(pair.Key, "contextRoots key");
                ContextRoot root = pair.Value;
                if (root == null || String.IsNullOrWhiteSpace(root.path))
                    throw new InvalidOperationException("contextRoots['" + pair.Key + "'] needs a path.");
                root.path = ExpandPath(root.path, _appDir);
                if (String.IsNullOrWhiteSpace(root.label))
                    root.label = pair.Key;
                if (root.kind != "drive")
                    root.kind = "local";
                if (root.indexDepth <= 0)
                    root.indexDepth = 3;
                if (root.excludeNames == null)
                    root.excludeNames = new string[0];
            }
        }

        private void ValidateChatProjectPolicies()
        {
            if (_config.chatProjectEnvironments == null)
                return;

            foreach (KeyValuePair<string, ChatProjectPolicy> pair in _config.chatProjectEnvironments)
            {
                ValidateSafeId(pair.Key, "chatProjectEnvironments key");

                if (pair.Value == null || pair.Value.allowedAgents == null || pair.Value.allowedAgents.Length == 0)
                {
                    throw new InvalidOperationException(
                        "chatProjectEnvironments['" + pair.Key + "'] needs allowedAgents.");
                }

                foreach (string agentId in pair.Value.allowedAgents)
                {
                    AgentProfile agent;
                    if (!_config.agents.TryGetValue(agentId, out agent))
                    {
                        throw new InvalidOperationException(
                            "chatProjectEnvironments['" + pair.Key + "'] references unknown agent '" + agentId + "'.");
                    }

                    if (Array.IndexOf(GetAgentModes(agent), "chat") < 0)
                    {
                        throw new InvalidOperationException(
                            "Agent '" + agentId + "' has no chat mode profile (chatProjectEnvironments['" + pair.Key + "']).");
                    }
                }
            }
        }

        // v0.6.1: projects that exist only in Drive (for example imported chats) may run
        // in chat mode when the environment is listed in chatProjectEnvironments. Chat
        // mode works in the per-chat scratch folder under driveRoot; such projects never
        // get a local workspace.
        private ProjectProfile ResolveProject(JobRequest request)
        {
            ProjectProfile configured;
            if (_config.projects.TryGetValue(request.projectId, out configured))
                return configured;

            if (_config.chatProjectEnvironments == null)
                return null;

            ChatProjectPolicy policy;
            if (String.IsNullOrEmpty(request.environmentId) ||
                !_config.chatProjectEnvironments.TryGetValue(request.environmentId, out policy) ||
                policy == null)
                return null;

            ProjectProfile synthetic = new ProjectProfile();
            synthetic.environmentId = request.environmentId;
            synthetic.workspace = null;
            synthetic.allowedAgents = policy.allowedAgents;
            // v1.1: Cowork runs the full agent profile in the same per-chat scratch folder.
            synthetic.allowedModes = new string[] { "chat", "cowork" };
            synthetic.autoExport = policy.autoExport;
            return synthetic;
        }

        private void ValidateAgentV06(string agentId, AgentProfile agent)
        {
            if (agent.modes != null)
            {
                foreach (KeyValuePair<string, ModeProfile> m in agent.modes)
                {
                    if (Array.IndexOf(KnownModes, m.Key) < 0)
                    {
                        throw new InvalidOperationException(
                            "Agent '" + agentId + "' has unknown mode '" + m.Key + "'.");
                    }

                    if (m.Value == null)
                    {
                        throw new InvalidOperationException(
                            "Agent '" + agentId + "' mode '" + m.Key + "' is null.");
                    }
                }
            }

            if (agent.models != null)
            {
                foreach (CatalogModel model in agent.models)
                    ValidateCatalogModel(agentId, model);
            }

            if (agent.efforts != null)
            {
                foreach (string effort in agent.efforts)
                    ValidateCatalogId(agentId, effort, "effort");
            }

            if (!String.IsNullOrEmpty(agent.modelCatalogFile))
                agent.modelCatalogFile = ExpandPath(agent.modelCatalogFile, _appDir);

            if (!String.IsNullOrEmpty(agent.rateLimitSessionsDir))
                agent.rateLimitSessionsDir = ExpandPath(agent.rateLimitSessionsDir, _appDir);

            if (!Corporate.IsGatewayRuntime(agent.kind) &&
                (agent.models != null && agent.models.Count > 0 ||
                 !String.IsNullOrEmpty(agent.modelCatalogFile)) &&
                (agent.modelArguments == null || agent.modelArguments.Length == 0))
            {
                throw new InvalidOperationException(
                    "Agent '" + agentId + "' lists models but has no modelArguments.");
            }

            if (agent.efforts != null && agent.efforts.Length > 0 &&
                (agent.effortArguments == null || agent.effortArguments.Length == 0))
            {
                throw new InvalidOperationException(
                    "Agent '" + agentId + "' lists efforts but has no effortArguments.");
            }
        }

        private static void ValidateCatalogModel(string agentId, CatalogModel model)
        {
            if (model == null)
                throw new InvalidOperationException("Agent '" + agentId + "' has a null model entry.");

            ValidateCatalogId(agentId, model.id, "model");

            if (model.efforts != null)
            {
                foreach (string effort in model.efforts)
                    ValidateCatalogId(agentId, effort, "effort");
            }
        }

        private static void ValidateCatalogId(string agentId, string value, string label)
        {
            if (String.IsNullOrEmpty(value) || !CatalogIdPattern.IsMatch(value))
            {
                throw new InvalidOperationException(
                    "Agent '" + agentId + "' has an invalid " + label + " id: '" + value + "'.");
            }
        }

        // ---------- v0.6: model catalog ----------

        private readonly object _catalogLock = new object();
        private readonly Dictionary<string, List<CatalogModel>> _fileCatalogCache =
            new Dictionary<string, List<CatalogModel>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _fileCatalogStamp =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Effective model list for an agent: explicit config models first, then models
        // from the provider cache file (visibility "list", ordered by priority).
        private List<CatalogModel> GetAgentModels(AgentProfile agent)
        {
            List<CatalogModel> result = new List<CatalogModel>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            if (agent.models != null)
            {
                foreach (CatalogModel m in agent.models)
                {
                    if (seen.Add(m.id))
                        result.Add(m);
                }
            }

            if (!String.IsNullOrEmpty(agent.modelCatalogFile))
            {
                foreach (CatalogModel m in LoadCatalogFile(agent.modelCatalogFile))
                {
                    if (seen.Add(m.id))
                        result.Add(m);
                }
            }

            return result;
        }

        private List<CatalogModel> LoadCatalogFile(string path)
        {
            lock (_catalogLock)
            {
                try
                {
                    if (!File.Exists(path))
                        return new List<CatalogModel>();

                    DateTime stamp = File.GetLastWriteTimeUtc(path);
                    DateTime cachedStamp;
                    List<CatalogModel> cached;

                    if (_fileCatalogStamp.TryGetValue(path, out cachedStamp) &&
                        cachedStamp == stamp &&
                        _fileCatalogCache.TryGetValue(path, out cached))
                    {
                        return cached;
                    }

                    List<CatalogModel> parsed = ParseCodexModelsCache(
                        File.ReadAllText(path, Encoding.UTF8));

                    _fileCatalogCache[path] = parsed;
                    _fileCatalogStamp[path] = stamp;
                    return parsed;
                }
                catch (Exception ex)
                {
                    Log("Model catalog file could not be read (" + path + "): " + ex.Message);
                    List<CatalogModel> previous;
                    return _fileCatalogCache.TryGetValue(path, out previous)
                        ? previous
                        : new List<CatalogModel>();
                }
            }
        }

        private static List<CatalogModel> ParseCodexModelsCache(string json)
        {
            List<KeyValuePair<int, CatalogModel>> ordered =
                new List<KeyValuePair<int, CatalogModel>>();

            Dictionary<string, object> root =
                JsonUtil.DeserializeObject(json) as Dictionary<string, object>;

            object modelsObj;
            if (root == null || !root.TryGetValue("models", out modelsObj))
                return new List<CatalogModel>();

            object[] models = modelsObj as object[];
            if (models == null)
            {
                System.Collections.ArrayList list = modelsObj as System.Collections.ArrayList;
                if (list != null)
                    models = list.ToArray();
            }

            if (models == null)
                return new List<CatalogModel>();

            foreach (object o in models)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null)
                    continue;

                string slug = DictString(m, "slug");
                string visibility = DictString(m, "visibility");

                if (String.IsNullOrEmpty(slug) || !CatalogIdPattern.IsMatch(slug))
                    continue;

                if (!String.IsNullOrEmpty(visibility) &&
                    !String.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase))
                    continue;

                CatalogModel model = new CatalogModel();
                model.id = slug;
                model.label = DictString(m, "display_name") ?? slug;
                model.description = DictString(m, "description");
                model.defaultEffort = DictString(m, "default_reasoning_level");

                List<string> efforts = new List<string>();
                object levelsObj;
                if (m.TryGetValue("supported_reasoning_levels", out levelsObj))
                {
                    System.Collections.IEnumerable levels = levelsObj as System.Collections.IEnumerable;
                    if (levels != null)
                    {
                        foreach (object lo in levels)
                        {
                            Dictionary<string, object> level = lo as Dictionary<string, object>;
                            string effort = level == null ? null : DictString(level, "effort");
                            if (!String.IsNullOrEmpty(effort) && CatalogIdPattern.IsMatch(effort))
                                efforts.Add(effort);
                        }
                    }
                }

                model.efforts = efforts.Count > 0 ? efforts.ToArray() : null;

                if (model.defaultEffort != null && !CatalogIdPattern.IsMatch(model.defaultEffort))
                    model.defaultEffort = null;

                int priority = 1000;
                object pObj;
                if (m.TryGetValue("priority", out pObj) && pObj != null)
                {
                    int p;
                    if (Int32.TryParse(Convert.ToString(pObj, CultureInfo.InvariantCulture), out p))
                        priority = p;
                }

                ordered.Add(new KeyValuePair<int, CatalogModel>(priority, model));
            }

            return ordered
                .OrderBy(delegate(KeyValuePair<int, CatalogModel> kv) { return kv.Key; })
                .Select(delegate(KeyValuePair<int, CatalogModel> kv) { return kv.Value; })
                .ToList();
        }

        private static string DictString(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null)
                return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string[] GetAgentModes(AgentProfile agent)
        {
            if (agent.modes == null || agent.modes.Count == 0)
                return new string[] { "cowork" };

            return KnownModes
                .Where(delegate(string m) { return agent.modes.ContainsKey(m); })
                .ToArray();
        }

        private static ModeProfile GetModeProfile(AgentProfile agent, string mode)
        {
            ModeProfile profile;
            if (agent.modes != null && agent.modes.TryGetValue(mode, out profile))
                return profile;
            return null;
        }

        private void WaitForDriveReadyPath()
        {
            string readyPath;

            if (!String.IsNullOrWhiteSpace(_config.driveReadyPath))
            {
                readyPath = ExpandPath(_config.driveReadyPath, _appDir);
            }
            else
            {
                readyPath = Path.GetDirectoryName(_driveRoot);

                if (String.IsNullOrWhiteSpace(readyPath))
                    readyPath = Path.GetPathRoot(_driveRoot);
            }

            DateTime deadline =
                DateTime.UtcNow.AddSeconds(_config.startupDriveWaitSeconds);

            DateTime nextProbeUtc = DateTime.MinValue;

            while (true)
            {
                if (_exiting)
                    throw new OperationCanceledException("Startup cancelled by the user.");

                if (DateTime.UtcNow >= nextProbeUtc)
                {
                    bool available =
                        !String.IsNullOrWhiteSpace(readyPath) &&
                        Directory.Exists(readyPath);

                    if (available)
                    {
                        _driveOnline = true;
                        return;
                    }

                    _driveOnline = false;

                    if (_statusItem != null)
                        _statusItem.Text = "WAITING FOR DRIVE | " + readyPath;

                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new DirectoryNotFoundException(
                            "Drive ready path is not available: " + readyPath +
                            "\r\nGoogle Drive for Desktop may not be mounted yet.");
                    }

                    BootstrapLog.Write(
                        "Waiting for Drive ready path '" + readyPath + "'.");

                    nextProbeUtc = DateTime.UtcNow.AddSeconds(10);
                }

                // Application.Run has not started yet; pump the tray menu in short slices
                // so that "Exit" answers within 250 ms instead of after the 10 s probe interval.
                Application.DoEvents();
                Thread.Sleep(250);
            }
        }

        private void PrepareDirectories()
        {
            Directory.CreateDirectory(_driveRoot);

            _projectsDir = Path.Combine(_driveRoot, "projects");
            _hostTransportRoot = Path.Combine(
                _driveRoot,
                "transport",
                "hosts",
                _config.hostId);

            _inboxDir = Path.Combine(_hostTransportRoot, "inbox");
            _claimedDir = Path.Combine(_hostTransportRoot, "claimed");
            _eventsDir = Path.Combine(_hostTransportRoot, "events");
            _completedDir = Path.Combine(_hostTransportRoot, "completed");
            _failedDir = Path.Combine(_hostTransportRoot, "failed");
            _rejectedDir = Path.Combine(_hostTransportRoot, "rejected");
            _cancelDir = Path.Combine(_hostTransportRoot, "cancel");

            Directory.CreateDirectory(_projectsDir);
            Directory.CreateDirectory(_inboxDir);
            Directory.CreateDirectory(_claimedDir);
            Directory.CreateDirectory(_eventsDir);
            Directory.CreateDirectory(_completedDir);
            Directory.CreateDirectory(_failedDir);
            Directory.CreateDirectory(_rejectedDir);
            Directory.CreateDirectory(_cancelDir);

            _hostOwnerPath = Path.Combine(_hostTransportRoot, "owner.json");
        }

        private void InitializeTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("Starting...");
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);

            _transportItem = new ToolStripMenuItem("Transport: starting...");
            _transportItem.Enabled = false;
            menu.Items.Add(_transportItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem scanItem = new ToolStripMenuItem("Scan now");
            scanItem.Click += delegate { QueueScan(); };
            menu.Items.Add(scanItem);

            _pauseItem = new ToolStripMenuItem("Pause");
            _pauseItem.Click += delegate
            {
                _paused = !_paused;
                _pauseItem.Text = _paused ? "Resume" : "Pause";
                Log(_paused ? "Bridge paused." : "Bridge resumed.");
                HostStateChanged();

                if (!_paused)
                    QueueScan();
            };
            menu.Items.Add(_pauseItem);

            ToolStripMenuItem openRoot = new ToolStripMenuItem("Open Drive root");
            openRoot.Click += delegate { OpenExplorer(_driveRoot); };
            menu.Items.Add(openRoot);

            ToolStripMenuItem openLogs = new ToolStripMenuItem("Open local logs");
            openLogs.Click += delegate { OpenExplorer(_localLogDir); };
            menu.Items.Add(openLogs);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem gConnect = new ToolStripMenuItem("Connect Google (Pub/Sub + Drive API)...");
            gConnect.Click += delegate { ConnectGoogleInteractive(); };
            menu.Items.Add(gConnect);

            ToolStripMenuItem gDisconnect = new ToolStripMenuItem("Disconnect Google");
            gDisconnect.Click += delegate { DisconnectGoogle(); };
            menu.Items.Add(gDisconnect);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { RequestExit(); };
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon();
            _tray.Icon = GetTrayIcon(TrayState.Idle);
            _tray.Text = "AI Workbench Bridge";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { OpenExplorer(_driveRoot); };
        }

        private void InitializeWatcher()
        {
            _watcher = new FileSystemWatcher(_inboxDir);
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.CreationTime;

            _watcher.Created += WatcherChanged;
            _watcher.Changed += WatcherChanged;
            _watcher.Renamed += WatcherChanged;
            _watcher.Error += delegate(object sender, ErrorEventArgs e)
            {
                Log("FileSystemWatcher error: " + e.GetException().Message);
                QueueScan();
            };
            _watcher.EnableRaisingEvents = true;
        }

        private void InitializeTimers()
        {
            StartPushHeartbeat(); // v1.2.8: "Bridge offline" watchdog on the CO site
            int intervalMs = checked(_config.scanIntervalSeconds * 1000);

            _scanTimer = new System.Threading.Timer(
                delegate { QueueScan(); },
                null,
                intervalMs,
                intervalMs);

            _uiTimer = new UiTimer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += delegate { UpdateTrayStatus(); CheckDrainRequest(); };
            _uiTimer.Start();
        }

        private void WatcherChanged(object sender, FileSystemEventArgs e)
        {
            if (_exiting || _paused)
                return;

            if (e.FullPath.EndsWith(".ready", StringComparison.OrdinalIgnoreCase) ||
                e.ChangeType == WatcherChangeTypes.Created ||
                e.ChangeType == WatcherChangeTypes.Renamed)
            {
                QueueScan();
            }
        }

        private void QueueScan()
        {
            // v1.4.2: also while paused or draining, so ScanInbox keeps owner.json fresh
            if (_exiting)
                return;

            Task.Factory.StartNew(
                ScanInbox,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
        }

        private void ScanInbox()
        {
            if (_exiting)
                return;

            if (!Monitor.TryEnter(_scanLock))
                return;

            try
            {
                // v1.4.2: paused or waiting for an update — no jobs, but keep the owner.json heartbeat
                // (with state) so CO Web shows "updating"/"paused" instead of stale after 4 min.
                if (_paused)
                {
                    EnsureHostOwnership(false);
                    return;
                }

                if (!EnsureHostOwnership(false))
                {
                    _queuedJobs = 0;
                    return;
                }

                MaybeCleanupTransport();
                ProcessFolderRequests();
                MaybePublishFolderIndex();
                MaybeRefreshCodexLimits();
                MaybeRunSchedules();
                ProcessCancelRequests();
                FinalizeStrandedClaimedJobs();

                if (!Directory.Exists(_inboxDir))
                {
                    _driveOnline = false;
                    _queuedJobs = 0;
                    return;
                }

                string[] directories;

                try
                {
                    directories = Directory.GetDirectories(_inboxDir);
                    _driveOnline = true;
                }
                catch (Exception ex)
                {
                    _driveOnline = false;
                    _queuedJobs = 0;
                    Log("Inbox scan failed: " + ex.Message);
                    return;
                }

                Array.Sort(directories, StringComparer.Ordinal);
                int readyCount = 0;

                foreach (string jobDir in directories)
                {
                    if (_exiting || _paused)
                        break;

                    string folderJobId = Path.GetFileName(jobDir);

                    try
                    {
                        ValidateSafeId(folderJobId, "job folder");
                    }
                    catch (Exception ex)
                    {
                        Log("Rejecting invalid job folder '" + folderJobId + "': " + ex.Message);
                        RejectJobFolder(jobDir, folderJobId, ex.Message);
                        continue;
                    }

                    if (ConsumeLateInboxCopy(folderJobId, jobDir))
                        continue;

                    string verificationReason;
                    byte[] verifiedRequestBytes;
                    if (!CommitMarkerUtil.TryReadVerifiedPayload(
                            Path.Combine(jobDir, "request.json"),
                            Path.Combine(jobDir, "request.ready"),
                            out verifiedRequestBytes,
                            out verificationReason))
                    {
                        // A commit marker can arrive before its payload through Drive sync.
                        // Incomplete jobs remain in inbox and are retried on the next scan.
                        continue;
                    }

                    bool attachmentPackageIncomplete;
                    string attachmentPackageReason;
                    if (!TryVerifyAttachmentPackage(
                            jobDir,
                            verifiedRequestBytes,
                            out attachmentPackageIncomplete,
                            out attachmentPackageReason))
                    {
                        if (attachmentPackageIncomplete)
                        {
                            // request.ready is written last by the producer, but Drive can
                            // expose files out of order. Wait rather than reject a valid job.
                            continue;
                        }

                        RejectJobFolder(
                            jobDir,
                            folderJobId,
                            "Invalid attachment package: " + attachmentPackageReason);
                        continue;
                    }

                    readyCount++;

                    if (_runningJobs.Count >= _config.maxParallelJobs)
                        continue;

                    if (_runningJobs.ContainsKey(folderJobId))
                        continue;

                    if (JobIdAlreadyExists(folderJobId))
                    {
                        RejectJobFolder(
                            jobDir,
                            folderJobId,
                            "Duplicate jobId. A job with this ID already exists in host history.");
                        continue;
                    }

                    // v1.8.1: a Drive copy of a job the database knows as cancelled or finished never runs.
                    int driveClaim = DbqDriveClaim(folderJobId, File.Exists(Path.Combine(jobDir, "db-fallback")));
                    if (driveClaim == DriveClaimSkip)
                    {
                        readyCount--;
                        TryDeleteDirectory(jobDir);
                        continue;
                    }
                    if (driveClaim == DriveClaimDefer)
                        continue;

                    string claimedPath = Path.Combine(_claimedDir, folderJobId);

                    try
                    {
                        MoveDirectoryWithRetry(jobDir, claimedPath, 6, 250);
                    }
                    catch (Exception ex)
                    {
                        Log("Could not claim " + folderJobId + ": " + ex.Message);
                        continue;
                    }

                    try
                    {
                        RecordUsedJobId(folderJobId);
                    }
                    catch (Exception ex)
                    {
                        RejectJobFolder(
                            claimedPath,
                            folderJobId,
                            "Could not persist jobId reservation: " + ex.Message);
                        continue;
                    }

                    if (!_runningJobs.TryAdd(folderJobId, 0))
                    {
                        RejectJobFolder(
                            claimedPath,
                            folderJobId,
                            "Internal duplicate running jobId.");
                        continue;
                    }

                    Log("Claimed job " + folderJobId + ".");

                    string jobIdCopy = folderJobId;
                    string claimedCopy = claimedPath;

                    Task.Factory.StartNew(
                        delegate { ProcessJob(jobIdCopy, claimedCopy); },
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        TaskScheduler.Default);
                }

                _queuedJobs = readyCount;
            }
            finally
            {
                Monitor.Exit(_scanLock);
            }
        }

        private bool JobIdAlreadyExists(string jobId)
        {
            lock (_usedJobIdsLock)
            {
                if (_usedJobIds.Contains(jobId))
                    return true;
            }

            if (Directory.Exists(Path.Combine(_claimedDir, jobId)))
                return true;

            if (Directory.Exists(Path.Combine(_completedDir, jobId)))
                return true;

            if (Directory.Exists(Path.Combine(_failedDir, jobId)))
                return true;

            if (Directory.Exists(Path.Combine(_eventsDir, jobId)))
                return true;

            return false;
        }

        private void RejectJobFolder(string sourceDir, string jobId, string reason)
        {
            try
            {
                string safeBase;

                try
                {
                    ValidateSafeId(jobId, "jobId");
                    safeBase = jobId;
                }
                catch
                {
                    safeBase = "INVALID";
                }

                string target = Path.Combine(
                    _rejectedDir,
                    safeBase + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));

                MoveDirectoryWithRetry(sourceDir, target, 4, 250);

                // v1.3.0: the database shows the reason too; a duplicate is a second copy of a job that runs.
                if (safeBase != "INVALID" && !reason.StartsWith("Duplicate jobId", StringComparison.Ordinal))
                    DbqReject(jobId, reason);

                RejectionRecord record = new RejectionRecord();
                record.schemaVersion = 2;
                record.hostId = _config.hostId;
                record.createdUtc = DateTime.UtcNow.ToString("o");
                record.reason = reason;

                AtomicFile.WriteJson(
                    Path.Combine(target, "rejection.json"),
                    record);
            }
            catch (Exception ex)
            {
                Log("Could not move rejected job '" + jobId + "': " + ex);
            }
        }

        private void ProcessJob(string jobId, string claimedPath)
        {
            EventBatchWriter eventWriter = null;
            string localJobDir = null;
            string stagedAttachmentRoot = null;
            bool keepLocalJobDir = false;
            JobRequest request = null;
            string finalStatus = "failed";
            bool committedResult = false;
            List<ArtifactRecord> artifacts = new List<ArtifactRecord>();
            List<string> missingArtifacts = new List<string>();
            List<ArtifactFailure> failedArtifacts = new List<ArtifactFailure>();
            byte[] requestBytes = null;

            try
            {
                string commitReason;
                if (!CommitMarkerUtil.TryReadVerifiedPayload(
                        Path.Combine(claimedPath, "request.json"),
                        Path.Combine(claimedPath, "request.ready"),
                        out requestBytes,
                        out commitReason))
                {
                    throw new InvalidOperationException(
                        "Claimed request failed commit verification: " + commitReason);
                }

                eventWriter = new EventBatchWriter(
                    Path.Combine(_eventsDir, jobId),
                    _config.eventFlushSeconds,
                    _config.eventFlushBytes,
                    _config.eventMaxPendingBytes,
                    Log);
                string publishJobId = jobId;
                eventWriter.OnBatchWritten = delegate(EventBatch b) { FastPublishEvents(publishJobId, b); DbqPostEvents(publishJobId, b); };

                eventWriter.Write("bridge", "claimed", "Job claimed by BridgeAgent.");

                request = ReadAndValidateJob(jobId, requestBytes);
                RememberJobEnvironment(jobId, request.environmentId);

                AgentProfile agent = _config.agents[request.agent];
                ProjectProfile project = ResolveProject(request);
                ModeProfile modeProfile = GetModeProfile(agent, request.mode);

                if (request.mode != "chat" && project.workspace != null && !Directory.Exists(project.workspace))
                {
                    throw new DirectoryNotFoundException(
                        "Workspace does not exist: " + project.workspace);
                }

                string workspace = GetJobWorkspace(request, project);

                bool requiresGit =
                    modeProfile != null && modeProfile.requiresGitRepository.HasValue
                        ? modeProfile.requiresGitRepository.Value
                        : agent.requiresGitRepository;

                if (requiresGit)
                {
                    string gitReason;

                    if (!IsInsideGitWorkTree(workspace, out gitReason))
                    {
                        throw new InvalidOperationException(
                            "Agent '" + request.agent + "' requires a Git work tree: " +
                            gitReason);
                    }
                }

                // Reject a reused jobId before the agent runs, so that no work is
                // performed for a job whose artifacts could not be exported anyway.
                string artifactRoot = GetArtifactRoot(request);

                if (Directory.Exists(artifactRoot))
                {
                    throw new InvalidOperationException(
                        "Artifact root already exists for jobId '" +
                        request.jobId + "'. jobId values are immutable and never reused.");
                }

                int timeoutMinutes = _config.maxProcessTimeoutMinutes;

                if (request.timeoutMinutes > 0)
                    timeoutMinutes = Math.Min(request.timeoutMinutes, _config.maxProcessTimeoutMinutes);
                else if (modeProfile != null && modeProfile.defaultTimeoutMinutes > 0)
                    timeoutMinutes = Math.Min(modeProfile.defaultTimeoutMinutes, _config.maxProcessTimeoutMinutes);

                localJobDir = Path.Combine(
                    _localJobsDir,
                    jobId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") +
                    "-" + Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(localJobDir);

                stagedAttachmentRoot = StageAttachments(
                    claimedPath,
                    request,
                    workspace,
                    eventWriter);

                bool autoExport = IsAutoExport(request, project);
                Dictionary<string, FileStamp> before = null;

                if (autoExport)
                {
                    string snapshotReason;
                    before = SnapshotWorkspace(workspace, out snapshotReason);

                    if (before == null)
                    {
                        autoExport = false;
                        eventWriter.Write("bridge", "auto_export_disabled", snapshotReason);
                    }
                }

                eventWriter.Write(
                    "bridge",
                    "started",
                    "Starting agent '" + request.agent + "' in project '" +
                    request.projectId + "' (mode " + request.mode +
                    (request.model != null ? ", model " + request.model : String.Empty) +
                    (request.effort != null ? ", effort " + request.effort : String.Empty) + ").");

                // Two jobs resuming one provider session collide ("thread already has an active writer");
                // a second message sent while the first still runs waits for it.
                string sessionKey = request.conversation != null && request.conversation.operation != "new" &&
                                    !String.IsNullOrEmpty(request.conversation.sessionId)
                    ? request.agent + ":" + request.conversation.sessionId
                    : null;
                object sessionLock = sessionKey == null ? null : _sessionLocks.GetOrAdd(sessionKey, delegate(string k) { return new object(); });
                bool sessionHeld = false;
                AgentRunResult runResult;
                try
                {
                    if (sessionLock != null)
                    {
                        sessionHeld = Monitor.TryEnter(sessionLock);
                        if (!sessionHeld)
                        {
                            eventWriter.Write("bridge", "queued", "Waiting for the previous message in this chat to finish.");
                            SupNoProcessOk(jobId, true); // v1.9.0: no process while waiting, not stuck
                            try { sessionHeld = Monitor.TryEnter(sessionLock, TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes) + 5)); }
                            finally { SupNoProcessOk(jobId, false); }
                            if (!sessionHeld)
                                throw new InvalidOperationException("The previous message in this chat is still running.");
                        }
                    }
                    if (Corporate.IsGatewayRuntime(agent.kind))
                        SupNoProcessOk(jobId, true); // v1.9.0: the gateway runtime runs over HTTP without a process
                    runResult = Corporate.IsGatewayRuntime(agent.kind)
                        ? RunGatewayAgent(jobId, localJobDir, request, agent, eventWriter)
                        : RunAgent(
                            jobId,
                            localJobDir,
                            request,
                            workspace,
                            agent,
                            modeProfile,
                            timeoutMinutes,
                            eventWriter);
                }
                finally
                {
                    if (sessionHeld)
                        Monitor.Exit(sessionLock);
                }

                // v1.1.6: every Claude-based agent (claude, dispatcher) reports the same subscription: one "claude" entry.
                // The corporate dispatcher's entry is its savings share, refreshed from the usage ledger.
                if (runResult.limits != null)
                    UpdateLimits(AgentRuntime.Get(agent.outputProtocol).LimitsAgentId(request.agent), runResult.limits);
                if (Corporate.IsDispatcher(request.agent) && runResult.usage != null)
                {
                    BookUsage(request.agent, request.jobId, "claude", request.model, runResult.usage.inputTokens, runResult.usage.outputTokens);
                    // v1.2.6: warn when the dispatcher generated content itself instead of delegating to the gateway.
                    CheckDispatcherPolicy(request.jobId, runResult.usage.outputTokens, eventWriter);
                }
                else if (Corporate.IsDispatcher(request.agent))
                {
                    RefreshDispatcherSavings();
                }

                CollectArtifacts(
                    request,
                    workspace,
                    eventWriter,
                    out artifacts,
                    out missingArtifacts,
                    out failedArtifacts);

                List<ExportSkip> exportSkipped = new List<ExportSkip>();

                if (autoExport)
                {
                    AutoExportArtifacts(
                        request,
                        workspace,
                        before,
                        artifacts,
                        exportSkipped,
                        eventWriter);
                }

                bool artifactsOk =
                    missingArtifacts.Count == 0 &&
                    failedArtifacts.Count == 0;

                bool processOk =
                    runResult.exitCode == 0 &&
                    !runResult.timedOut &&
                    runResult.outputDrained;

                bool structuredOk =
                    !agent.requireStructuredSuccess ||
                    (runResult.structuredFinalSeen && runResult.structuredSuccess);

                bool wasCancelled = _cancelRequested.ContainsKey(jobId);

                bool success =
                    !wasCancelled &&
                    processOk &&
                    structuredOk &&
                    artifactsOk;

                // v1.16.0: stopped for the Bridge update: "cancelled" (not a failure), continued by a new job of the chat
                bool pausedForUpdate =
                    !success && !wasCancelled && UpdatePauseRequested(jobId) &&
                    !Corporate.IsGatewayRuntime(agent.kind);
                string continuationJobId = pausedForUpdate
                    ? QueueUpdateContinuation(requestBytes, request, runResult.sessionId, eventWriter)
                    : null;
                string pauseMessage = pausedForUpdate
                    ? UpdatePauseMessage(_updatePaused[jobId], continuationJobId)
                    : null;

                finalStatus = wasCancelled || pausedForUpdate
                    ? "cancelled"
                    : (success ? "completed" : "failed");

                bool stdoutCopyFailed;
                string stdoutLogName = TryCopyLocalLog(
                    Path.Combine(localJobDir, "stdout.log"),
                    Path.Combine(claimedPath, "stdout.log"),
                    out stdoutCopyFailed);

                bool stderrCopyFailed;
                string stderrLogName = TryCopyLocalLog(
                    Path.Combine(localJobDir, "stderr.log"),
                    Path.Combine(claimedPath, "stderr.log"),
                    out stderrCopyFailed);

                if (stdoutCopyFailed || stderrCopyFailed)
                {
                    // The local copy is the only remaining copy of the agent output:
                    // keep it under jobs\unsynced instead of deleting it in finally.
                    keepLocalJobDir = true;
                    eventWriter.Write(
                        "bridge",
                        "log_copy_failed",
                        "Local copy kept at: " + GetUnsyncedJobDir(localJobDir));
                }

                if (success)
                {
                    eventWriter.Write(
                        "bridge",
                        "completed",
                        "Job completed successfully.");
                }
                else if (pausedForUpdate)
                {
                    eventWriter.Write("bridge", "paused_for_update", pauseMessage);
                }
                else if (finalStatus == "cancelled")
                {
                    eventWriter.Write(
                        "bridge",
                        "cancelled",
                        "Job cancelled by remote request.");
                }
                else
                {
                    eventWriter.Write(
                        "bridge",
                        "failed",
                        BuildFailureSummary(
                            runResult,
                            agent,
                            missingArtifacts,
                            failedArtifacts));
                }

                eventWriter.DisposeAndFlush();
                eventWriter = null;

                // v1.2.7: Web Push for the finished job (BuildFailureSummary is evaluated inside the guard)
                try
                {
                    NotifyJobFinished(request, finalStatus, runResult,
                        success ? null : BuildFailureSummary(runResult, agent, missingArtifacts, failedArtifacts));
                }
                catch (Exception pushEx)
                {
                    Log("Push: " + pushEx.Message);
                }

                JobResult result = new JobResult();
                result.schemaVersion = 3;
                result.tainted = IsJobTainted(request.jobId);
                // v1.5.1: strip the agent's marker before it reaches the user and use it as the block signal.
                bool blockedMarker;
                string finalText = StripBlockedMarker(runResult.finalResponse, out blockedMarker);
                result.corporateBlocked = CorporateBlockedReason(request, finalStatus, blockedMarker);
                result.engieBlocked = result.corporateBlocked; // legacy wire (server and CO Web before Freedom F1 5b)
                EndDeviceJob(request.jobId);
                result.hostId = _config.hostId;
                result.jobId = request.jobId;
                result.environmentId = request.environmentId;
                result.projectId = request.projectId;
                result.chatId = request.chatId;
                result.branchId = request.branchId;
                result.agent = request.agent;
                result.conversationOperation = GetConversationOperation(request);
                result.status = finalStatus;
                result.startedUtc = runResult.startedUtc;
                result.completedUtc = DateTime.UtcNow.ToString("o");
                result.exitCode = runResult.exitCode;
                result.timedOut = runResult.timedOut;
                result.structuredFinalSeen = runResult.structuredFinalSeen;
                result.structuredSuccess = runResult.structuredSuccess;
                result.sessionId = runResult.sessionId;
                result.agentFinalResponse = finalText;
                result.agentErrorEvents = runResult.agentErrorEvents;
                result.agentErrorMessages = runResult.agentErrorMessages ?? new List<string>();
                result.pausedForUpdate = pausedForUpdate ? _updatePaused[jobId] : null;
                result.continuationJobId = continuationJobId;
                result.error = success
                    ? null
                    : pausedForUpdate
                    ? pauseMessage
                    : (finalStatus == "cancelled"
                        ? "Cancellation requested by client."
                        : BuildFailureSummary(
                            runResult,
                            agent,
                            missingArtifacts,
                            failedArtifacts));
                result.artifacts = artifacts;
                result.missingArtifacts = missingArtifacts;
                result.failedArtifacts = failedArtifacts;
                result.stdoutLog = stdoutLogName;
                result.stderrLog = stderrLogName;
                result.outputDrained = runResult.outputDrained;
                result.mode = request.mode;
                result.model = request.model;
                result.effort = request.effort;
                result.modelReported = runResult.modelReported;
                result.usage = runResult.usage;
                result.autoExport = autoExport;
                result.exportSkipped = exportSkipped;

                string resultPath = Path.Combine(claimedPath, "result.json");
                string readyPath = Path.Combine(claimedPath, "result.ready");

                AtomicFile.WriteJson(resultPath, result);
                CommitMarkerUtil.WriteForPayload(resultPath, readyPath);
                FastPublishResult(jobId, resultPath, readyPath);

                // Contract: result.json and result.ready are immutable after this point.
                committedResult = true;
            }
            catch (Exception ex)
            {
                // 1.8.2: an error caused by the user's Stop (the process was killed) is a cancellation, not a failure
                bool cancelledByClient = _cancelRequested.ContainsKey(jobId);
                // v1.16.0: a job stopped by the update before its agent started runs again as a new job of the chat
                string pauseMessage = null;
                string continuationJobId = null;
                if (!cancelledByClient && ex is UpdatePausedException && request != null && requestBytes != null)
                {
                    continuationJobId = QueueUpdateContinuation(requestBytes, request, null, eventWriter);
                    pauseMessage = UpdatePauseMessage(_updatePaused[jobId], continuationJobId);
                    cancelledByClient = true; // ends "cancelled" with pauseMessage, not "failed"
                }
                Log("Job " + jobId + (pauseMessage != null ? " paused: " + ex.Message : (cancelledByClient ? " cancelled: " : " failed: ") + ex));

                if (eventWriter != null)
                {
                    eventWriter.Write("bridge", pauseMessage != null ? "paused_for_update" : (cancelledByClient ? "cancelled" : "failed"),
                        pauseMessage ?? ex.Message);
                    eventWriter.DisposeAndFlush();
                    eventWriter = null;
                }

                if (!committedResult)
                {
                    try
                    {
                        JobResult failed = new JobResult();
                        failed.schemaVersion = 3;
                        failed.hostId = _config.hostId;
                        failed.jobId = jobId;
                        failed.environmentId = request == null ? null : request.environmentId;
                        failed.projectId = request == null ? null : request.projectId;
                        failed.chatId = request == null ? null : request.chatId;
                        failed.branchId = request == null ? null : request.branchId;
                        failed.agent = request == null ? null : request.agent;
                        failed.mode = request == null ? null : request.mode;
                        failed.model = request == null ? null : request.model;
                        failed.effort = request == null ? null : request.effort;
                        failed.exportSkipped = new List<ExportSkip>();
                        failed.status = cancelledByClient ? "cancelled" : "failed";
                        failed.completedUtc = DateTime.UtcNow.ToString("o");
                        failed.exitCode = -1;
                        failed.timedOut = false;
                        failed.structuredFinalSeen = false;
                        failed.structuredSuccess = false;
                        failed.error = pauseMessage ?? (cancelledByClient ? "Cancellation requested by client." : ex.Message);
                        failed.pausedForUpdate = pauseMessage != null ? _updatePaused[jobId] : null;
                        failed.continuationJobId = continuationJobId;
                        failed.artifacts = artifacts;
                        failed.missingArtifacts = missingArtifacts;
                        failed.failedArtifacts = failedArtifacts;
                        failed.outputDrained = false;
                        failed.agentErrorEvents = 0;
                        failed.agentErrorMessages = new List<string>();

                        string resultPath = Path.Combine(claimedPath, "result.json");
                        string readyPath = Path.Combine(claimedPath, "result.ready");

                        AtomicFile.WriteJson(resultPath, failed);
                        CommitMarkerUtil.WriteForPayload(resultPath, readyPath);
                        committedResult = true;
                        FastPublishResult(jobId, resultPath, readyPath);
                    }
                    catch (Exception writeEx)
                    {
                        Log(
                            "Could not commit failure result for " + jobId + ": " +
                            writeEx);
                    }
                }

                finalStatus = cancelledByClient ? "cancelled" : "failed";
            }
            finally
            {
                if (eventWriter != null)
                {
                    eventWriter.DisposeAndFlush();
                    eventWriter = null;
                }

                WindowsJobObject jobObject;
                if (_runningJobObjects.TryRemove(jobId, out jobObject))
                {
                    try
                    {
                        jobObject.Terminate(9);
                        jobObject.Dispose();
                    }
                    catch
                    {
                    }
                }

                if (committedResult && Directory.Exists(claimedPath))
                {
                    // Do not rewrite result.* after result.ready. If the move fails now,
                    // FinalizeStrandedClaimedJobs retries it on every inbox scan.
                    string finalizeReason;
                    if (!TryFinalizeCommittedClaimed(claimedPath, jobId, out finalizeReason))
                    {
                        Log(
                            "Committed job " + jobId +
                            " stays in claimed and will be retried by the scan: " +
                            finalizeReason);
                    }
                }

                // Remove from the running set only after the final move, so that the
                // scan-time finalizer never races with this move.
                byte ignored;
                _runningJobs.TryRemove(jobId, out ignored);
                SupNoProcessOk(jobId, false);
                if (_paused)
                    _lastHostHeartbeatUtc = DateTime.MinValue; // v1.4.2: publish the smaller job count on the next scan

                if (!String.IsNullOrEmpty(localJobDir))
                {
                    if (keepLocalJobDir)
                        TryKeepUnsyncedJobDir(localJobDir);
                    else
                        TryDeleteDirectory(localJobDir);
                }

                if (!String.IsNullOrEmpty(stagedAttachmentRoot))
                    TryDeleteDirectory(stagedAttachmentRoot);

                byte cancelIgnored;
                _cancelRequested.TryRemove(jobId, out cancelIgnored);
                TryDeleteFile(Path.Combine(_cancelDir, jobId + ".cancel"));

                QueueScan();
            }
        }

        private void ProcessCancelRequests()
        {
            if (!Directory.Exists(_cancelDir))
                return;

            foreach (string path in Directory.GetFiles(_cancelDir, "*.cancel"))
            {
                string fileName = Path.GetFileName(path);
                string jobId = fileName.Substring(0, fileName.Length - ".cancel".Length);

                try
                {
                    ValidateSafeId(jobId, "cancel jobId");
                    _cancelRequested[jobId] = 0;

                    WindowsJobObject jobObject;
                    if (_runningJobObjects.TryGetValue(jobId, out jobObject))
                    {
                        Log("Cancellation requested for running job " + jobId + ".");
                        try { jobObject.Terminate(4); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log("Invalid cancel marker '" + fileName + "': " + ex.Message);
                    TryDeleteFile(path);
                }
            }
        }

        private bool TryVerifyAttachmentPackage(
            string jobDir,
            byte[] requestBytes,
            out bool incomplete,
            out string reason)
        {
            incomplete = false;
            reason = null;

            JobRequest preview;
            try
            {
                int offset = requestBytes.Length >= 3 &&
                    requestBytes[0] == 0xEF && requestBytes[1] == 0xBB && requestBytes[2] == 0xBF
                    ? 3 : 0;
                string json = new UTF8Encoding(false, true).GetString(
                    requestBytes, offset, requestBytes.Length - offset);
                preview = JsonUtil.Deserialize<JobRequest>(json);
            }
            catch (Exception ex)
            {
                reason = "request.json cannot be parsed for attachment verification: " + ex.Message;
                return false;
            }

            if (preview == null || preview.attachments == null || preview.attachments.Count == 0)
                return true;

            foreach (AttachmentRequest attachment in preview.attachments)
            {
                try
                {
                    ValidateAttachmentRequest(attachment);
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    return false;
                }

                string relative = NormalizeRelativePath(attachment.path);
                string full = SafeCombineUnderRoot(Path.Combine(jobDir, "attachments"), relative);

                if (!File.Exists(full))
                {
                    incomplete = true;
                    reason = "attachment not synced yet: " + attachment.path;
                    return false;
                }

                FileInfo info = new FileInfo(full);
                if (info.Length != attachment.sizeBytes)
                {
                    incomplete = true;
                    reason = "attachment size does not match yet: " + attachment.path;
                    return false;
                }

                string actualHash = HashUtil.ComputeSha256(full);
                if (!String.Equals(actualHash, attachment.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    incomplete = true;
                    reason = "attachment hash does not match yet: " + attachment.path;
                    return false;
                }
            }

            return true;
        }

        private static void ValidateAttachmentRequest(AttachmentRequest attachment)
        {
            if (attachment == null)
                throw new InvalidOperationException("Attachment entry is null.");
            if (String.IsNullOrWhiteSpace(attachment.path))
                throw new InvalidOperationException("Attachment path is required.");
            NormalizeRelativePath(attachment.path);
            if (attachment.sizeBytes < 0)
                throw new InvalidOperationException("Attachment sizeBytes cannot be negative.");
            if (String.IsNullOrWhiteSpace(attachment.sha256) ||
                !Regex.IsMatch(attachment.sha256, "^[0-9a-fA-F]{64}$"))
                throw new InvalidOperationException("Attachment sha256 must be 64 hexadecimal characters.");
        }

        private static void ValidateConversationRequest(JobRequest request)
        {
            if (request.conversation == null)
            {
                request.conversation = new ConversationRequest();
                request.conversation.operation = "new";
                return;
            }

            string operation = String.IsNullOrWhiteSpace(request.conversation.operation)
                ? "new" : request.conversation.operation;

            if (operation != "new" && operation != "resume" && operation != "fork")
                throw new InvalidOperationException("conversation.operation must be new, resume or fork.");

            request.conversation.operation = operation;

            if ((operation == "resume" || operation == "fork") &&
                String.IsNullOrWhiteSpace(request.conversation.sessionId))
                throw new InvalidOperationException("conversation.sessionId is required for " + operation + ".");
        }

        // v0.6: mode, model and effort are ids validated against local config only.
        private void ValidateSelectionRequest(JobRequest request, ProjectProfile project, AgentProfile agent)
        {
            string mode = String.IsNullOrWhiteSpace(request.mode) ? "cowork" : request.mode;

            if (Array.IndexOf(KnownModes, mode) < 0)
                throw new InvalidOperationException("mode must be chat or cowork.");

            if (Array.IndexOf(project.allowedModes, mode) < 0)
            {
                throw new InvalidOperationException(
                    "Mode '" + mode + "' is not allowed for project '" + request.projectId + "'.");
            }

            if (Array.IndexOf(GetAgentModes(agent), mode) < 0)
            {
                throw new InvalidOperationException(
                    "Agent '" + request.agent + "' has no profile for mode '" + mode + "'.");
            }

            request.mode = mode;

            CatalogModel selected = null;

            if (!String.IsNullOrWhiteSpace(request.model))
            {
                selected = GetAgentModels(agent).FirstOrDefault(
                    delegate(CatalogModel m) { return String.Equals(m.id, request.model, StringComparison.Ordinal); });

                if (selected == null)
                {
                    throw new InvalidOperationException(
                        "Model '" + request.model + "' is not in the catalog of agent '" + request.agent + "'.");
                }
            }
            else
            {
                request.model = null;
            }

            if (!String.IsNullOrWhiteSpace(request.effort))
            {
                string[] allowed = selected != null && selected.efforts != null
                    ? selected.efforts
                    : (agent.efforts ?? new string[0]);

                if (Array.IndexOf(allowed, request.effort) < 0 ||
                    agent.effortArguments == null || agent.effortArguments.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Effort '" + request.effort + "' is not allowed for agent '" + request.agent +
                        (selected != null ? "' model '" + selected.id : String.Empty) + "'.");
                }
            }
            else
            {
                request.effort = null;
            }

            if (request.output != null)
            {
                if (request.output.maxFiles < 0 || request.output.maxBytes < 0)
                    throw new InvalidOperationException("output limits cannot be negative.");
            }

            request.resolvedContexts = ResolveContextFolders(request.contextFolders);

            if (request.permissions != null)
            {
                string files = NormalizeFilesPermission(request.permissions.files);
                if (files == "")
                    throw new InvalidOperationException("permissions.files must be read or write (got '" + request.permissions.files + "').");
                request.permissions.files = files;
            }
        }

        // 1.9.3: hand-written schedules.json and chats made from it carried "files": true, which the serializer
        // turns into "True". true/rw -> write, false/none/empty -> no restriction (null); anything else -> "" (invalid).
        internal static string NormalizeFilesPermission(string files)
        {
            if (files == null) return null;
            string v = files.Trim().ToLowerInvariant();
            if (v == "read" || v == "ro" || v == "readonly" || v == "read-only") return "read";
            if (v == "write" || v == "rw" || v == "readwrite" || v == "read-write" || v == "true") return "write";
            if (v.Length == 0 || v == "false" || v == "none" || v == "null") return null;
            return "";
        }

        // ---------- v0.7: context folders ----------

        // Names never indexed or attached: hidden/system folders and credential stores.
        private static readonly string[] ContextExcludedNames = new string[]
        {
            "AppData", "$RECYCLE.BIN", "System Volume Information", "node_modules", "__pycache__",
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs"
        };

        private static bool IsExcludedContextName(ContextRoot root, string name)
        {
            if (String.IsNullOrEmpty(name))
                return true;
            if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("$", StringComparison.Ordinal))
                return true;
            foreach (string x in ContextExcludedNames)
                if (String.Equals(x, name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string x in root.excludeNames)
                if (String.Equals(x, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private string ResolveContextPath(string rootId, string relativePath, out ContextRoot root, out string normalized)
        {
            root = null;
            normalized = String.Empty;

            if (_config.contextRoots == null || String.IsNullOrEmpty(rootId) ||
                !_config.contextRoots.TryGetValue(rootId, out root))
            {
                throw new InvalidOperationException("Unknown context root '" + rootId + "'.");
            }

            string full = Path.GetFullPath(root.path);

            if (!String.IsNullOrWhiteSpace(relativePath))
            {
                normalized = NormalizeRelativePath(relativePath);

                foreach (string part in normalized.Split(Path.DirectorySeparatorChar))
                {
                    if (IsExcludedContextName(root, part))
                        throw new InvalidOperationException("Context path contains an excluded folder name: " + part);
                }

                full = SafeCombineUnderRoot(root.path, normalized);
                EnsureNoReparsePoints(root.path, normalized);
            }

            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("Context folder not found: " + rootId + "/" + normalized.Replace('\\', '/'));

            return full;
        }

        private List<ResolvedContext> ResolveContextFolders(List<ContextFolderRequest> folders)
        {
            List<ResolvedContext> result = new List<ResolvedContext>();

            if (folders == null || folders.Count == 0)
                return result;

            if (folders.Count > _config.maxContextFolders)
                throw new InvalidOperationException("Too many context folders (max " + _config.maxContextFolders + ").");

            foreach (ContextFolderRequest f in folders)
            {
                if (f == null)
                    continue;

                string access = String.IsNullOrEmpty(f.access) ? "read" : f.access;
                if (access != "read" && access != "write")
                    throw new InvalidOperationException("Context folder access must be read or write.");

                ContextRoot root;
                string normalized;
                string full = ResolveContextPath(f.rootId, f.relativePath, out root, out normalized);

                if (access == "write" && full.IndexOf('\'') >= 0)
                    throw new InvalidOperationException("Writable context folder paths cannot contain an apostrophe: " + normalized);

                ResolvedContext rc = new ResolvedContext();
                rc.rootId = f.rootId;
                rc.relativePath = normalized.Replace('\\', '/');
                rc.fullPath = full;
                rc.write = access == "write";
                rc.label = String.IsNullOrWhiteSpace(f.label)
                    ? root.label + (rc.relativePath.Length > 0 ? " / " + rc.relativePath : String.Empty)
                    : f.label.Trim();
                if (rc.label.Length > 200)
                    rc.label = rc.label.Substring(0, 200);
                result.Add(rc);
            }

            return result;
        }

        private bool IsAutoExport(JobRequest request, ProjectProfile project)
        {
            if (request.output != null && request.output.autoExport.HasValue)
                return request.output.autoExport.Value;

            return project.autoExport;
        }

        // Working directory of the agent for this job.
        //   cowork: the project workspace from config.json;
        //   chat:   a per-chat scratch folder under driveRoot, created by Bridge from
        //           validated ids (the request never supplies a path).
        private string GetJobWorkspace(JobRequest request, ProjectProfile project)
        {
            if (request.mode != "chat" && project.workspace != null)
                return project.workspace;

            string scratch = SafeCombineUnderRoot(
                _projectsDir,
                Path.Combine(request.projectId, "chats", request.chatId, "scratch"));

            WaitForCloudChatFolder(request, scratch);
            Directory.CreateDirectory(scratch);
            return scratch;
        }

        // v1.6.1: CO Web creates the chat folder (and, from 0.59.0, its scratch) through the Drive API seconds before the
        // first job; Drive for desktop shows it on G: only after its next pull. Creating the path locally before that
        // makes a second cloud folder of the same name (29.10: chat files went to one, chat.json to the other).
        // So a missing chat folder, or a missing scratch of a chat folder younger than 2 min, is waited for first.
        private void WaitForCloudChatFolder(JobRequest request, string scratch)
        {
            if (Directory.Exists(scratch))
                return;
            string chatDir = Path.GetDirectoryName(scratch);
            DateTime started = DateTime.UtcNow;
            DateTime chatDeadline = started.AddSeconds(90);
            while (!Directory.Exists(chatDir) && DateTime.UtcNow < chatDeadline)
                Thread.Sleep(2000);
            if (Directory.Exists(chatDir) && DateTime.UtcNow - Directory.GetCreationTimeUtc(chatDir) < TimeSpan.FromMinutes(2))
            {
                DateTime scratchDeadline = DateTime.UtcNow.AddSeconds(20);
                while (!Directory.Exists(scratch) && DateTime.UtcNow < scratchDeadline)
                    Thread.Sleep(1000);
            }
            double waited = (DateTime.UtcNow - started).TotalSeconds;
            if (waited >= 1)
                Log("Chat folder wait " + request.chatId + ": " + Math.Round(waited) + " s, chat folder " +
                    (Directory.Exists(chatDir) ? "present" : "missing") + ", scratch " +
                    (Directory.Exists(scratch) ? "present" : "missing (creating)"));
        }

        private static string GetConversationOperation(JobRequest request)
        {
            return request == null || request.conversation == null ||
                String.IsNullOrWhiteSpace(request.conversation.operation)
                ? "new" : request.conversation.operation;
        }

        private string StageAttachments(
            string claimedPath,
            JobRequest request,
            string workspace,
            EventBatchWriter eventWriter)
        {
            if (request.attachments == null || request.attachments.Count == 0)
                return null;

            string root = SafeCombineUnderRoot(
                workspace,
                Path.Combine(".co-attachments", request.jobId));

            // v1.6.0: attachments with a target become files of the chat (a new version when the file exists);
            // only the rest are staged under .co-attachments/<jobId>/.
            bool chatFiles = UsesChatTargets(request, workspace);
            bool needRoot = request.attachments.Any(delegate(AttachmentRequest a) { return !chatFiles || String.IsNullOrWhiteSpace(a.target); });

            if (needRoot && Directory.Exists(root))
                throw new InvalidOperationException("Attachment staging directory already exists: " + root);

            if (needRoot)
                Directory.CreateDirectory(root);

            try
            {
                foreach (AttachmentRequest attachment in request.attachments)
                {
                    string relative = NormalizeRelativePath(attachment.path);
                    string source = SafeCombineUnderRoot(Path.Combine(claimedPath, "attachments"), relative);

                    if (chatFiles && !String.IsNullOrWhiteSpace(attachment.target))
                    {
                        string targetRelative = NormalizeRelativePath(attachment.target);
                        string target = SafeCombineUnderRoot(workspace, targetRelative);
                        string targetDir = Path.GetDirectoryName(targetRelative);
                        if (!String.IsNullOrEmpty(targetDir))
                        {
                            // The existing part of the path is checked before any directory is created under it.
                            EnsureNoExistingReparsePoints(workspace, targetDir);
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            EnsureNoReparsePoints(workspace, targetDir);
                        }
                        if (File.Exists(target))
                        {
                            EnsureNoReparsePoints(workspace, targetRelative);
                            EnsureSingleLinkFile(target);
                            if (new FileInfo(target).Length == attachment.sizeBytes &&
                                String.Equals(HashUtil.ComputeSha256(target), attachment.sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                eventWriter.Write("bridge", "attachment_staged", targetRelative + " (unchanged)");
                                NoteChatFile(request, targetRelative, "unchanged, same as the current file");
                                continue;
                            }
                        }
                        bool existed = File.Exists(target);
                        AtomicFile.CopyFile(source, target);
                        if (new FileInfo(target).Length != attachment.sizeBytes ||
                            !String.Equals(HashUtil.ComputeSha256(target), attachment.sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Attachment verification failed while staging: " + attachment.target);
                        eventWriter.Write("bridge", "attachment_staged", targetRelative);
                        NoteChatFile(request, targetRelative, existed ? "new version from the user" : "new file");
                        continue;
                    }

                    string destination = SafeCombineUnderRoot(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(source, destination, false);

                    if (new FileInfo(destination).Length != attachment.sizeBytes ||
                        !String.Equals(HashUtil.ComputeSha256(destination), attachment.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Attachment verification failed while staging: " + attachment.path);

                    eventWriter.Write("bridge", "attachment_staged", relative);
                }

                return needRoot ? root : null;
            }
            catch
            {
                if (needRoot)
                    TryDeleteDirectory(root);
                throw;
            }
        }

        private static void NoteChatFile(JobRequest request, string relative, string what)
        {
            if (request.chatFileNotes == null)
                request.chatFileNotes = new List<string>();
            request.chatFileNotes.Add(relative.Replace('\\', '/') + " (" + what + ")");
        }

        // v1.6.0: attachment targets apply when the workspace is the chat scratch on Drive (chat files live there).
        private bool UsesChatTargets(JobRequest request, string workspace)
        {
            if (request.attachments == null || !IsUnderDriveRoot(workspace))
                return false;
            string scratch = Path.Combine(_projectsDir, request.projectId, "chats", request.chatId, "scratch");
            return String.Equals(EnsureTrailingSeparator(Path.GetFullPath(workspace)), EnsureTrailingSeparator(Path.GetFullPath(scratch)),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildAgentPrompt(JobRequest request)
        {
            bool hasAttachments = request.attachments != null && request.attachments.Count > 0;
            bool hasContexts = request.resolvedContexts != null && request.resolvedContexts.Count > 0;

            if (!hasAttachments && !hasContexts)
                return request.instruction;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(request.instruction);

            List<string> notes = request.chatFileNotes ?? new List<string>();
            List<AttachmentRequest> staged = !hasAttachments ? new List<AttachmentRequest>() : notes.Count == 0
                ? request.attachments
                : request.attachments.Where(delegate(AttachmentRequest a) { return String.IsNullOrWhiteSpace(a.target); }).ToList();

            if (notes.Count > 0)
            {
                // v1.6.0: the files are files of the chat; an edited version replaced the file, the older one stays in the
                // Drive version history.
                sb.AppendLine();
                sb.AppendLine("Files the user attached are now files of this chat in the current workspace:");
                foreach (string note in notes)
                    sb.AppendLine("- " + note);
            }

            if (staged.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Attached files are staged inside the current workspace under:");
                sb.AppendLine(".co-attachments/" + request.jobId + "/");
                sb.AppendLine("Files:");
                foreach (AttachmentRequest attachment in staged)
                    sb.AppendLine("- .co-attachments/" + request.jobId + "/" + attachment.path.Replace('\\', '/'));
            }

            if (hasContexts)
            {
                sb.AppendLine();
                sb.AppendLine("Connected context folders (use them as reference material; do not copy them wholesale):");
                foreach (ResolvedContext rc in request.resolvedContexts)
                {
                    sb.AppendLine("- " + rc.label + " [" + (rc.write ? "read-write" : "read-only: do not modify") + "]: " + rc.fullPath);
                }
            }

            return sb.ToString();
        }

        private JobRequest ReadAndValidateJob(string folderJobId, byte[] requestBytes)
        {
            // Skip a UTF-8 byte order mark if the producer wrote one (for example
            // PowerShell 5.1 Set-Content -Encoding UTF8). The commit hash still covers
            // the raw bytes, so the marker verification is unaffected.
            int offset = 0;
            if (requestBytes.Length >= 3 &&
                requestBytes[0] == 0xEF &&
                requestBytes[1] == 0xBB &&
                requestBytes[2] == 0xBF)
            {
                offset = 3;
            }

            string json = new UTF8Encoding(false, true).GetString(
                requestBytes,
                offset,
                requestBytes.Length - offset);
            JobRequest request = JsonUtil.Deserialize<JobRequest>(json);

            if (request == null)
                throw new InvalidOperationException("request.json could not be parsed.");

            if (request.schemaVersion != 2 && request.schemaVersion != 3)
                throw new InvalidOperationException(
                    "Unsupported job schemaVersion. Expected 2 or 3.");

            ValidateSafeId(request.hostId, "hostId");
            ValidateSafeId(request.jobId, "jobId");
            ValidateSafeId(request.environmentId, "environmentId");
            ValidateSafeId(request.projectId, "projectId");
            ValidateSafeId(request.chatId, "chatId");
            ValidateSafeId(request.agent, "agent");

            if (!String.IsNullOrWhiteSpace(request.branchId))
                ValidateSafeId(request.branchId, "branchId");

            if (!String.Equals(
                    request.hostId,
                    _config.hostId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Job hostId does not match this BridgeAgent hostId.");
            }

            if (!String.Equals(
                    folderJobId,
                    request.jobId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "jobId does not match the job folder name.");
            }

            if (String.IsNullOrWhiteSpace(request.instruction))
                throw new InvalidOperationException("instruction is required.");

            if (!_config.agents.ContainsKey(request.agent))
            {
                throw new InvalidOperationException(
                    "Unknown agent profile: " + request.agent);
            }

            ProjectProfile project = ResolveProject(request);

            if (project == null)
            {
                throw new InvalidOperationException(
                    "Unknown project profile: " + request.projectId);
            }

            if (!String.Equals(
                    project.environmentId,
                    request.environmentId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Job environment does not match the project environment.");
            }

            bool agentAllowed = project.allowedAgents.Any(
                delegate(string a)
                {
                    return String.Equals(
                        a,
                        request.agent,
                        StringComparison.Ordinal);
                });

            if (!agentAllowed)
            {
                throw new InvalidOperationException(
                    "Agent '" + request.agent +
                    "' is not allowed for project '" + request.projectId + "'.");
            }

            if (request.artifactPaths == null)
                request.artifactPaths = new List<string>();

            if (request.attachments == null)
                request.attachments = new List<AttachmentRequest>();

            ValidateConversationRequest(request);
            ValidateSelectionRequest(request, project, _config.agents[request.agent]);

            foreach (AttachmentRequest attachment in request.attachments)
                ValidateAttachmentRequest(attachment);

            foreach (string artifactPath in request.artifactPaths)
            {
                NormalizeRelativePath(artifactPath);
            }

            ValidateDeviceRequest(request);

            return request;
        }

        // Last total_token_usage recorded in the Codex rollout of a session (null when not found).
        // v1.1: corporate gateway runtime (config kind "corporate", legacy "engie"). No CLI process: Bridge calls the
        // device tool of kind corporate-llm (connector manifest; it reaches the gateway with the user's portal session)
        // and returns its reply as the job's final response. The gateway is a locked chat assistant, so the model cannot drive tools — the harness passes the prompt (and any
        // document text already assembled by BuildAgentPrompt) and streams back the answer.
        private AgentRunResult RunGatewayAgent(
            string jobId,
            string localJobDir,
            JobRequest request,
            AgentProfile agent,
            EventBatchWriter eventWriter)
        {
            AgentRunResult result = new AgentRunResult();
            result.startedUtc = DateTime.UtcNow.ToString("o");
            result.exitCode = -1;

            if (request.devices == null || request.devices.Count == 0)
            {
                result.error = "The " + Corporate.Title + " agent needs a device: pick the computer signed in to " + Corporate.PortalTitle + ".";
                return result;
            }
            string gatewayTool = Corporate.ToolOf(Corporate.LlmKind);
            if (gatewayTool == null)
            {
                result.error = Corporate.Title + " call failed: no gateway tool (kind " + Corporate.LlmKind + ") in the corporate connector manifest " + Corporate.ManifestPath() + ".";
                return result;
            }
            string deviceId = request.devices[0];

            // Register the job's device ownership so ExecuteDeviceCall's guard passes (mirrors AddDeviceArguments).
            JobDeviceState js = new JobDeviceState();
            js.jobId = request.jobId;
            js.taskId = request.task != null && !String.IsNullOrEmpty(request.task.taskId) ? request.task.taskId : request.jobId;
            js.environmentId = request.environmentId;
            js.devices.AddRange(request.devices);
            js.tainted = request.tainted;
            js.agent = request.agent;
            js.denyTools = JobDeviceState.DenySet(request.deviceToolsDeny); // v1.3.5: a chat that turned the connector off refuses the gateway here too
            _deviceJobs[request.jobId] = js;

            string model = !String.IsNullOrWhiteSpace(request.model) ? request.model
                : (!String.IsNullOrEmpty(agent.defaultModel) ? agent.defaultModel : "gpt-6-luna");

            Dictionary<string, object> callArgs = new Dictionary<string, object>();
            callArgs["model"] = model;
            callArgs["prompt"] = BuildAgentPrompt(request);
            string argsJson = JsonUtil.Serialize(callArgs);

            eventWriter.Write("bridge", "corporate", "Asking " + Corporate.Title + " model '" + model + "' via device " + deviceId + "…");

            Dictionary<string, object> reply;
            try
            {
                reply = ExecuteDeviceCall(request.jobId, deviceId, gatewayTool, argsJson);
            }
            catch (Exception ex)
            {
                result.error = Corporate.Title + " call failed: " + ex.Message;
                return result;
            }

            bool ok = DeviceWire.B(reply, "ok");
            string content = DeviceWire.S(reply, "content");

            if (!ok)
            {
                string errMsg = null;
                object errObj;
                if (reply.TryGetValue("error", out errObj) && errObj is Dictionary<string, object>)
                    errMsg = DeviceWire.S((Dictionary<string, object>)errObj, "message");
                result.error = !String.IsNullOrEmpty(errMsg) ? errMsg
                    : (Corporate.Title + " call failed" + (String.IsNullOrEmpty(content) ? "." : ": " + content));
                return result;
            }

            if (!String.IsNullOrEmpty(content))
                eventWriter.Write("agent", "stdout", content);

            result.exitCode = 0;
            result.outputDrained = true;
            result.structuredFinalSeen = true;
            result.structuredSuccess = true;
            result.finalResponse = content;
            result.modelReported = model;
            return result;
        }

        private AgentRunResult RunAgent(
            string jobId,
            string localJobDir,
            JobRequest request,
            string workspace,
            AgentProfile agent,
            ModeProfile modeProfile,
            int timeoutMinutes,
            EventBatchWriter eventWriter)
        {
            AgentRunResult result = new AgentRunResult();
            result.startedUtc = DateTime.UtcNow.ToString("o");
            result.exitCode = -1;

            string stdoutPath = Path.Combine(localJobDir, "stdout.log");
            string stderrPath = Path.Combine(localJobDir, "stderr.log");

            object stdoutLock = new object();
            object stderrLock = new object();

            AgentRuntime runtime = AgentRuntime.Get(agent.outputProtocol);
            AgentStreamParser inspector = runtime.CreateParser(agent, request);

            using (StreamWriter stdoutWriter = new StreamWriter(
                stdoutPath,
                false,
                new UTF8Encoding(false)))
            using (StreamWriter stderrWriter = new StreamWriter(
                stderrPath,
                false,
                new UTF8Encoding(false)))
            using (ManualResetEvent stdoutClosed = new ManualResetEvent(false))
            using (ManualResetEvent stderrClosed = new ManualResetEvent(false))
            {
                stdoutWriter.AutoFlush = true;
                stderrWriter.AutoFlush = true;

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ResolveExecutable(agent.executable);
                string[] agentArgs = runtime.BuildArguments(agent, modeProfile, request,
                    runtime.UsesCloudDeniedTools ? CloudDeniedTools() : null);
                // v1.7.0: Claude reads the prompt and the user's follow-ups as stream-json lines from stdin
                FollowupSink followupSink = null;
                if (FollowupsEnabled && agent.promptToStdin && runtime is ClaudeAgentRuntime &&
                    Array.IndexOf(agentArgs, "--input-format") < 0)
                {
                    agentArgs = FollowupSink.AddStreamInput(agentArgs);
                    followupSink = new FollowupSink(inspector, eventWriter);
                }
                request.browserSettings = ResolveBrowserSettings(request);
                psi.Arguments = CommandLine.Build(
                    AddDeviceArguments(runtime, request, localJobDir, agentArgs, psi));
                psi.WorkingDirectory = workspace;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = agent.promptToStdin;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);

                if (agent.environment != null)
                {
                    foreach (KeyValuePair<string, string> pair in agent.environment)
                    {
                        psi.EnvironmentVariables[pair.Key] =
                            pair.Value ?? String.Empty;
                    }
                }

                // v1.8.0: the agent's tools and hooks know their job and chat; read-guard drops its events into
                // guard-events\, which this loop forwards into the job's event stream (job_events).
                string guardEventsDir = Path.Combine(localJobDir, "guard-events");
                psi.EnvironmentVariables["AIWB_JOB_ID"] = jobId ?? String.Empty;
                psi.EnvironmentVariables["AIWB_CHAT_ID"] = request.chatId ?? String.Empty;
                psi.EnvironmentVariables["AIWB_GUARD_EVENTS"] = guardEventsDir;
                // v1.10.0: agent browser settings of the chat for browser-agent 0.15.0 (and linkedin-web/whatsapp-web)
                BrowserSettings bs = request.browserSettings ?? new BrowserSettings();
                psi.EnvironmentVariables["AIWB_BROWSER_TABS"] = bs.tabs.ToString(CultureInfo.InvariantCulture);
                psi.EnvironmentVariables["AIWB_BROWSER_IDLE_MIN"] = bs.idleMin.ToString(CultureInfo.InvariantCulture);
                psi.EnvironmentVariables["AIWB_BROWSER_FORM_IDLE_MIN"] = bs.formIdleMin.ToString(CultureInfo.InvariantCulture);
                psi.EnvironmentVariables["AIWB_BROWSER_CAPTCHA_KEEP"] = bs.captchaKeep ? "1" : "0";
                if (runtime.HasReadGuardHooks)
                    psi.EnvironmentVariables["AIWB_READ_GUARD_HOOK"] = "1";

                ApplyRunAs(psi);

                Process process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = true;

                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                    {
                        try
                        {
                            stdoutClosed.Set();
                        }
                        catch (ObjectDisposedException)
                        {
                            // RunAgent already returned after the bounded drain wait.
                        }
                        return;
                    }

                    try
                    {
                        lock (stdoutLock)
                        {
                            stdoutWriter.WriteLine(e.Data);
                        }

                        inspector.Observe(e.Data);
                        eventWriter.Write("agent", "stdout", e.Data);
                    }
                    catch (Exception ex)
                    {
                        // I/O failure in a thread-pool callback must never escape.
                        Log(
                            "stdout event dropped for " + jobId + ": " +
                            ex.Message);
                    }
                };

                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                    {
                        try
                        {
                            stderrClosed.Set();
                        }
                        catch (ObjectDisposedException)
                        {
                            // RunAgent already returned after the bounded drain wait.
                        }
                        return;
                    }

                    try
                    {
                        lock (stderrLock)
                        {
                            stderrWriter.WriteLine(e.Data);
                        }

                        eventWriter.Write("agent", "stderr", e.Data);
                    }
                    catch (Exception ex)
                    {
                        // I/O failure in a thread-pool callback must never escape.
                        Log(
                            "stderr event dropped for " + jobId + ": " +
                            ex.Message);
                    }
                };

                WindowsJobObject jobObject = null;
                bool assignedToJobObject = false;

                try
                {
                    jobObject = new WindowsJobObject();

                    // v1.16.0: a job that waited for its chat while the update paused the others does not start now
                    if (UpdatePauseRequested(jobId))
                        throw new UpdatePausedException();

                    Log(
                        "Starting " + request.agent + " for " + jobId +
                        ": " + psi.FileName + " " + psi.Arguments);

                    if (!process.Start())
                        throw new InvalidOperationException(
                            "Process.Start returned false.");

                    try
                    {
                        jobObject.Assign(process);
                        assignedToJobObject = true;
                    }
                    catch
                    {
                        KillProcessTreeFallback(process);
                        throw;
                    }

                    if (!_runningJobObjects.TryAdd(jobId, jobObject))
                    {
                        throw new InvalidOperationException(
                            "Could not register Windows Job Object for " + jobId + ".");
                    }
                    if (followupSink != null)
                        _followupSinks[jobId] = followupSink;

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // 1.8.2: Stop pressed while the prompt is still being written (01.11, 2 s after the start) kills the
                    // process and the write throws "The pipe has been ended"; the wait loop below then reports the
                    // job as cancelled instead of failed.
                    try
                    {
                        if (followupSink != null)
                        {
                            // stdin stays open for follow-ups until Claude's result (or the process end)
                            byte[] line = FollowupSink.UserLine(BuildAgentPrompt(request));
                            process.StandardInput.BaseStream.Write(line, 0, line.Length);
                            process.StandardInput.BaseStream.Flush();
                            followupSink.Open(process.StandardInput.BaseStream);
                        }
                        else if (agent.promptToStdin)
                        {
                            string prompt = BuildAgentPrompt(request);

                            if (!prompt.EndsWith("\n", StringComparison.Ordinal))
                                prompt += "\n";

                            byte[] bytes = new UTF8Encoding(false).GetBytes(prompt);
                            process.StandardInput.BaseStream.Write(
                                bytes,
                                0,
                                bytes.Length);
                            process.StandardInput.BaseStream.Flush();
                            process.StandardInput.Close();
                        }
                    }
                    catch (IOException)
                    {
                        if (!_cancelRequested.ContainsKey(jobId) && !UpdatePauseRequested(jobId))
                            throw;
                    }

                    long timeoutMsLong =
                        (long)timeoutMinutes * 60L * 1000L;

                    int timeoutMs =
                        timeoutMsLong > Int32.MaxValue
                        ? Int32.MaxValue
                        : (int)timeoutMsLong;

                    bool exited = false;
                    Stopwatch processWatch = Stopwatch.StartNew();
                    while (!exited && processWatch.ElapsedMilliseconds < timeoutMs)
                    {
                        exited = process.WaitForExit(500);
                        ReadGuard.ForwardEvents(guardEventsDir, eventWriter);
                        // Claude answered: no more follow-ups; EOF lets it exit (one written meanwhile is its next turn).
                        // Background subagents may still run after this first result; a follow-up arriving then is
                        // "late" and CO Web sends it as the next message after the job (queued, not lost): Claude
                        // exits only on EOF, so stdin cannot stay open until the last subagent result.
                        if (followupSink != null && inspector.FinalSeen)
                            followupSink.Close();
                        if (_cancelRequested.ContainsKey(jobId))
                        {
                            eventWriter.Write(
                                "bridge",
                                "cancel_requested",
                                "Cancellation requested; terminating process tree.");
                            if (jobObject != null && assignedToJobObject)
                                jobObject.Terminate(4);
                            else
                                KillProcessTreeFallback(process);
                            process.WaitForExit(5000);
                            exited = true;
                            break;
                        }
                        // v1.16.0: the update waited long enough; stopped like Stop, ProcessJob queues the continuation.
                        // An agent that already gave its final answer is let finish.
                        if (UpdatePauseRequested(jobId) && !inspector.FinalSeen)
                        {
                            eventWriter.Write(
                                "bridge",
                                "update_pause",
                                "Bridge update: stopping the agent; the job continues after the restart.");
                            if (jobObject != null && assignedToJobObject)
                                jobObject.Terminate(4);
                            else
                                KillProcessTreeFallback(process);
                            process.WaitForExit(5000);
                            exited = true;
                            break;
                        }
                    }

                    if (!exited)
                    {
                        result.timedOut = true;
                        result.error =
                            "Agent exceeded timeout of " +
                            timeoutMinutes + " minutes.";

                        eventWriter.Write(
                            "bridge",
                            "timeout",
                            result.error);

                        jobObject.Terminate(2);
                        process.WaitForExit(5000);
                    }
                    else
                    {
                        // A child may inherit stdout/stderr handles and outlive the CLI.
                        // Terminating the per-job Windows Job Object closes residual
                        // descendants, after which pipe EOF must arrive within a bound.
                        jobObject.Terminate(0);
                    }

                    int drainTimeoutMs =
                        Math.Max(1, _config.outputDrainTimeoutSeconds) * 1000;

                    Stopwatch drainWatch = Stopwatch.StartNew();
                    bool stdoutDrained = stdoutClosed.WaitOne(drainTimeoutMs);
                    int remainingDrainMs = Math.Max(
                        0,
                        drainTimeoutMs - (int)Math.Min(Int32.MaxValue, drainWatch.ElapsedMilliseconds));
                    bool stderrDrained = stderrClosed.WaitOne(remainingDrainMs);

                    result.outputDrained = stdoutDrained && stderrDrained;

                    if (!result.outputDrained)
                    {
                        result.error =
                            "Agent output was not drained within " +
                            _config.outputDrainTimeoutSeconds + " seconds.";
                        eventWriter.Write(
                            "bridge",
                            "output_drain_timeout",
                            result.error);
                    }

                    if (!process.HasExited)
                    {
                        jobObject.Terminate(3);
                        process.WaitForExit(5000);
                    }
                    ReadGuard.ForwardEvents(guardEventsDir, eventWriter, true);

                    result.exitCode =
                        process.HasExited && !result.timedOut
                        ? process.ExitCode
                        : -2;

                    AgentInspectionSnapshot snapshot = inspector.Snapshot();
                    result.structuredFinalSeen = snapshot.finalSeen;
                    result.structuredSuccess = snapshot.success && result.outputDrained;
                    result.sessionId = snapshot.sessionId;
                    result.finalResponse = snapshot.finalResponse;
                    result.agentErrorEvents = snapshot.errorEventCount;
                    result.agentErrorMessages = snapshot.errorMessages;
                    result.usage = snapshot.usage;
                    result.modelReported = snapshot.modelReported;
                    result.limits = snapshot.limits;

                    if (result.timedOut)
                    {
                        result.structuredSuccess = false;
                    }
                    else if (!result.outputDrained)
                    {
                        result.structuredSuccess = false;
                    }
                    else if (result.exitCode != 0)
                    {
                        result.error =
                            "Agent process exited with code " +
                            result.exitCode + ".";
                    }
                    else if (agent.requireStructuredSuccess &&
                             !result.structuredFinalSeen)
                    {
                        result.error =
                            "Agent exited with code 0 but no structured final event was observed." +
                            (String.IsNullOrWhiteSpace(snapshot.failureMessage)
                                ? String.Empty
                                : " Last agent error: " + snapshot.failureMessage);
                    }
                    else if (agent.requireStructuredSuccess &&
                             !result.structuredSuccess)
                    {
                        result.error =
                            String.IsNullOrWhiteSpace(snapshot.failureMessage)
                            ? "Agent structured result indicates failure."
                            : snapshot.failureMessage;
                    }

                    return result;
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            if (jobObject != null && assignedToJobObject)
                                jobObject.Terminate(9);
                            else
                                KillProcessTreeFallback(process);
                        }
                    }
                    catch
                    {
                        KillProcessTreeFallback(process);
                    }

                    if (followupSink != null)
                    {
                        FollowupSink removedSink;
                        _followupSinks.TryRemove(jobId, out removedSink);
                        followupSink.Close();
                    }

                    WindowsJobObject removed;
                    if (_runningJobObjects.TryRemove(jobId, out removed))
                    {
                        try
                        {
                            removed.Dispose();
                        }
                        catch
                        {
                        }
                    }

                    try
                    {
                        if (jobObject != null)
                            jobObject.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private string BuildFailureSummary(
            AgentRunResult runResult,
            AgentProfile agent,
            List<string> missingArtifacts,
            List<ArtifactFailure> failedArtifacts)
        {
            List<string> parts = new List<string>();

            if (runResult.timedOut)
                parts.Add("timeout");

            if (runResult.exitCode != 0)
                parts.Add("exitCode=" + runResult.exitCode);

            if (!runResult.outputDrained)
                parts.Add("output not drained");

            if (agent.requireStructuredSuccess &&
                !runResult.structuredFinalSeen)
            {
                parts.Add("structured final event missing");
            }
            else if (agent.requireStructuredSuccess &&
                     !runResult.structuredSuccess)
            {
                parts.Add("structured result failed");
            }

            if (runResult.agentErrorEvents > 0)
                parts.Add("agent error events: " + runResult.agentErrorEvents);

            if (missingArtifacts != null && missingArtifacts.Count > 0)
            {
                parts.Add(
                    "missing artifacts: " +
                    String.Join(", ", missingArtifacts.ToArray()));
            }

            if (failedArtifacts != null && failedArtifacts.Count > 0)
            {
                parts.Add(
                    "artifact export failures: " +
                    failedArtifacts.Count);
            }

            if (!String.IsNullOrWhiteSpace(runResult.error))
                parts.Add(runResult.error);

            if (parts.Count == 0)
                return "Job did not satisfy completion criteria.";

            return String.Join("; ", parts.ToArray());
        }

        private void CollectArtifacts(
            JobRequest request,
            string workspace,
            EventBatchWriter eventWriter,
            out List<ArtifactRecord> records,
            out List<string> missingArtifacts,
            out List<ArtifactFailure> failedArtifacts)
        {
            records = new List<ArtifactRecord>();
            missingArtifacts = new List<string>();
            failedArtifacts = new List<ArtifactFailure>();

            string artifactRoot = GetArtifactRoot(request);
            bool byReference = IsUnderDriveRoot(workspace);

            // Checked before the agent ran as well; repeated here because Drive sync
            // could have materialized the directory in the meantime.
            if (Directory.Exists(artifactRoot))
            {
                throw new InvalidOperationException(
                    "Artifact root already exists for jobId '" +
                    request.jobId + "'. jobId values are immutable and never reused.");
            }

            // v1.6.0: a workspace on Drive needs no copies; artifacts are references to the live files.
            if (!byReference)
                Directory.CreateDirectory(artifactRoot);

            foreach (string artifactPath in request.artifactPaths)
            {
                if (String.IsNullOrWhiteSpace(artifactPath))
                    continue;

                string normalizedRelative;

                try
                {
                    normalizedRelative = NormalizeRelativePath(artifactPath);
                    string source = SafeCombineUnderRoot(
                        workspace,
                        normalizedRelative);

                    if (!File.Exists(source))
                    {
                        missingArtifacts.Add(normalizedRelative);
                        eventWriter.Write(
                            "bridge",
                            "artifact_missing",
                            normalizedRelative);
                        continue;
                    }

                    EnsureNoReparsePoints(
                        workspace,
                        normalizedRelative);

                    EnsureSingleLinkFile(source);

                    if (byReference)
                    {
                        ArtifactRecord live = MakeReferenceRecord(source, normalizedRelative, "declared");
                        records.Add(live);
                        eventWriter.Write("bridge", "artifact", live.driveRelativePath);
                        continue;
                    }

                    string destination = SafeCombineUnderRoot(
                        artifactRoot,
                        normalizedRelative);

                    int effectivePathLimit = Math.Min(
                        _config.maxArtifactDestinationPathChars,
                        259 - AtomicFile.TempSuffixLength);

                    if (destination.Length > effectivePathLimit)
                    {
                        throw new PathTooLongException(
                            "Artifact destination path exceeds effective limit of " +
                            effectivePathLimit +
                            " characters (including atomic-copy temporary suffix).");
                    }

                    Directory.CreateDirectory(
                        Path.GetDirectoryName(destination));

                    AtomicFile.CopyFile(source, destination);

                    FileInfo info = new FileInfo(destination);

                    ArtifactRecord record = new ArtifactRecord();
                    record.sourceRelativePath = normalizedRelative;
                    record.driveRelativePath = MakeDriveRelative(destination);
                    record.sizeBytes = info.Length;
                    record.sha256 = HashUtil.ComputeSha256(destination);
                    record.md5 = HashUtil.ComputeMd5(destination);
                    record.origin = "declared";

                    records.Add(record);

                    eventWriter.Write(
                        "bridge",
                        "artifact",
                        record.driveRelativePath);
                }
                catch (Exception ex)
                {
                    ArtifactFailure failure = new ArtifactFailure();
                    failure.path = artifactPath;
                    failure.error = ex.Message;
                    failedArtifacts.Add(failure);

                    eventWriter.Write(
                        "bridge",
                        "artifact_failed",
                        artifactPath + ": " + ex.Message);

                    Log(
                        "Artifact export failed for job " +
                        request.jobId + ", path '" +
                        artifactPath + "': " + ex);
                }
            }
        }

        // ---------- v0.6: auto-export of changed workspace files ----------

        private static readonly string[] AutoExportExcludedDirs = new string[]
        {
            ".git", ".co-attachments", "node_modules", ".venv", "venv", "__pycache__",
            ".pytest_cache", ".mypy_cache", ".idea", ".vs", "obj", ".claude", ".codex"
        };

        private static bool IsExcludedFileName(string name)
        {
            // Office lock files, temporary files and shell metadata.
            return name.StartsWith("~$", StringComparison.Ordinal) ||
                   name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(name, ".coignore", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Regex> LoadCoIgnore(string root)
        {
            List<Regex> patterns = new List<Regex>();
            string path = Path.Combine(root, ".coignore");

            if (!File.Exists(path))
                return patterns;

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                line = line.Replace('\\', '/').TrimStart('/');
                bool dirOnly = line.EndsWith("/", StringComparison.Ordinal);
                line = line.TrimEnd('/');

                string body = Regex.Escape(line).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");

                // A pattern without '/' matches a name at any depth; with '/' it is
                // anchored at the workspace root. Directory patterns cover contents.
                string prefix = line.Contains("/") ? "^" : "(^|/)";
                string suffix = dirOnly ? "(/|$)" : "(/.*)?$";

                patterns.Add(new Regex(prefix + body + suffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }

            return patterns;
        }

        // Relative path -> (length, last write). Returns null when the workspace is
        // too large to scan within autoExportMaxScanEntries.
        private Dictionary<string, FileStamp> SnapshotWorkspace(string root, out string reason)
        {
            reason = null;
            Dictionary<string, FileStamp> result =
                new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

            List<Regex> ignore;
            try
            {
                ignore = LoadCoIgnore(root);
            }
            catch (Exception ex)
            {
                reason = ".coignore could not be read: " + ex.Message;
                return null;
            }

            string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
            Stack<string> pending = new Stack<string>();
            pending.Push(fullRoot);
            int entries = 0;

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<FileSystemInfo> items;

                try
                {
                    items = new DirectoryInfo(dir).EnumerateFileSystemInfos();
                }
                catch (Exception ex)
                {
                    Log("Snapshot skipped directory '" + dir + "': " + ex.Message);
                    continue;
                }

                foreach (FileSystemInfo item in items)
                {
                    if (++entries > _config.autoExportMaxScanEntries)
                    {
                        reason = "workspace has more than " + _config.autoExportMaxScanEntries +
                                 " entries; auto-export skipped (use artifactPaths or .coignore)";
                        return null;
                    }

                    string relative = item.FullName.Substring(fullRoot.Length).Replace('\\', '/');

                    if (ignore.Any(delegate(Regex r) { return r.IsMatch(relative); }))
                        continue;

                    if ((item.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (Array.Exists(AutoExportExcludedDirs,
                                delegate(string d) { return String.Equals(d, item.Name, StringComparison.OrdinalIgnoreCase); }))
                            continue;

                        if ((item.Attributes & FileAttributes.ReparsePoint) != 0 &&
                            (NativePathInfo.GetReparseTag(item.FullName) & 0x20000000u) != 0)
                            continue; // never follow symlinks / junctions

                        pending.Push(item.FullName);
                        continue;
                    }

                    if (IsExcludedFileName(item.Name))
                        continue;

                    FileInfo file = (FileInfo)item;
                    FileStamp stamp = new FileStamp();
                    stamp.length = file.Length;
                    stamp.lastWriteTicks = file.LastWriteTimeUtc.Ticks;
                    result[relative] = stamp;
                }
            }

            return result;
        }

        private void AutoExportArtifacts(
            JobRequest request,
            string workspace,
            Dictionary<string, FileStamp> before,
            List<ArtifactRecord> records,
            List<ExportSkip> skipped,
            EventBatchWriter eventWriter)
        {
            string reason;
            Dictionary<string, FileStamp> after = SnapshotWorkspace(workspace, out reason);

            if (after == null)
            {
                eventWriter.Write("bridge", "auto_export_disabled", reason);
                return;
            }

            int maxFiles = _config.autoExportMaxFiles;
            long maxBytes = _config.autoExportMaxBytes;

            if (request.output != null)
            {
                if (request.output.maxFiles > 0)
                    maxFiles = Math.Min(maxFiles, request.output.maxFiles);
                if (request.output.maxBytes > 0)
                    maxBytes = Math.Min(maxBytes, request.output.maxBytes);
            }

            HashSet<string> declared = new HashSet<string>(
                records.Select(delegate(ArtifactRecord r) { return r.sourceRelativePath.Replace('\\', '/'); }),
                StringComparer.OrdinalIgnoreCase);

            // Newest first, so that the limits keep the most recent output.
            List<KeyValuePair<string, FileStamp>> changed = after
                .Where(delegate(KeyValuePair<string, FileStamp> kv)
                {
                    FileStamp old;
                    bool isNew = !before.TryGetValue(kv.Key, out old);
                    bool modified = !isNew &&
                        (old.length != kv.Value.length || old.lastWriteTicks != kv.Value.lastWriteTicks);
                    return (isNew || modified) && !declared.Contains(kv.Key);
                })
                .OrderByDescending(delegate(KeyValuePair<string, FileStamp> kv) { return kv.Value.lastWriteTicks; })
                .ToList();

            if (changed.Count == 0)
                return;

            string artifactRoot = GetArtifactRoot(request);
            bool byReference = IsUnderDriveRoot(workspace);
            int exported = 0;
            long exportedBytes = 0;

            int effectivePathLimit = Math.Min(
                _config.maxArtifactDestinationPathChars,
                259 - AtomicFile.TempSuffixLength);

            foreach (KeyValuePair<string, FileStamp> kv in changed)
            {
                string relative = kv.Key;

                if (kv.Value.length > _config.autoExportMaxFileBytes)
                {
                    AddSkip(skipped, eventWriter, relative, "file larger than " + _config.autoExportMaxFileBytes + " bytes");
                    continue;
                }

                if (exported >= maxFiles || exportedBytes + kv.Value.length > maxBytes)
                {
                    AddSkip(skipped, eventWriter, relative, "auto-export limit reached (" + maxFiles + " files / " + maxBytes + " bytes)");
                    continue;
                }

                try
                {
                    string normalizedRelative = NormalizeRelativePath(relative);
                    string source = SafeCombineUnderRoot(workspace, normalizedRelative);

                    EnsureNoReparsePoints(workspace, normalizedRelative);
                    EnsureSingleLinkFile(source);

                    if (byReference)
                    {
                        ArtifactRecord live = MakeReferenceRecord(source, normalizedRelative, "auto");
                        records.Add(live);
                        exported++;
                        exportedBytes += live.sizeBytes;
                        eventWriter.Write("bridge", "artifact", live.driveRelativePath);
                        continue;
                    }

                    string destination = SafeCombineUnderRoot(artifactRoot, normalizedRelative);

                    if (destination.Length > effectivePathLimit)
                    {
                        throw new PathTooLongException(
                            "destination path exceeds " + effectivePathLimit + " characters");
                    }

                    AtomicFile.CopyFile(source, destination);

                    FileInfo info = new FileInfo(destination);
                    ArtifactRecord record = new ArtifactRecord();
                    record.sourceRelativePath = normalizedRelative;
                    record.driveRelativePath = MakeDriveRelative(destination);
                    record.sizeBytes = info.Length;
                    record.sha256 = HashUtil.ComputeSha256(destination);
                    record.md5 = HashUtil.ComputeMd5(destination);
                    record.origin = "auto";
                    records.Add(record);

                    exported++;
                    exportedBytes += info.Length;

                    eventWriter.Write("bridge", "artifact", record.driveRelativePath);
                }
                catch (Exception ex)
                {
                    // Auto-exported files are a convenience: a failure is reported,
                    // it does not change the job status.
                    AddSkip(skipped, eventWriter, relative, ex.Message);
                }
            }
        }

        // v1.6.0: true when the path lies in the Drive folder (driveRoot): Drive for desktop uploads every change as a
        // new revision, so the live file is the one copy and its revisions are the versions.
        private bool IsUnderDriveRoot(string path)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(_driveRoot))
                return false;
            string root = EnsureTrailingSeparator(Path.GetFullPath(_driveRoot));
            return EnsureTrailingSeparator(Path.GetFullPath(path)).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private ArtifactRecord MakeReferenceRecord(string source, string normalizedRelative, string origin)
        {
            ArtifactRecord record = new ArtifactRecord();
            record.sourceRelativePath = normalizedRelative;
            record.driveRelativePath = MakeDriveRelative(source);
            record.sizeBytes = new FileInfo(source).Length;
            record.sha256 = HashUtil.ComputeSha256(source);
            record.md5 = HashUtil.ComputeMd5(source);
            record.origin = origin;
            record.@ref = true;
            return record;
        }

        private static void AddSkip(List<ExportSkip> skipped, EventBatchWriter eventWriter, string path, string reason)
        {
            ExportSkip skip = new ExportSkip();
            skip.path = path;
            skip.reason = reason;
            skipped.Add(skip);
            eventWriter.Write("bridge", "artifact_skipped", path + ": " + reason);
        }

        private string TryCopyLocalLog(
            string source,
            string destination,
            out bool copyFailed)
        {
            copyFailed = false;

            if (!File.Exists(source))
                return null;

            try
            {
                AtomicFile.CopyFile(source, destination);
                return Path.GetFileName(destination);
            }
            catch (Exception ex)
            {
                copyFailed = true;
                Log(
                    "Non-critical log copy failed for '" +
                    source + "': " + ex.Message);
                return null;
            }
        }

        private string GetUnsyncedJobDir(string localJobDir)
        {
            return Path.Combine(
                _localJobsDir,
                "unsynced",
                Path.GetFileName(localJobDir));
        }

        private void TryKeepUnsyncedJobDir(string localJobDir)
        {
            try
            {
                string target = GetUnsyncedJobDir(localJobDir);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Directory.Move(localJobDir, target);
                Log("Kept local agent output at: " + target);
            }
            catch (Exception ex)
            {
                // Leave the directory where it is; it is still not deleted.
                Log("Could not move local job directory to unsynced: " + ex.Message);
            }
        }

        private string GetArtifactRoot(JobRequest request)
        {
            string chatRoot = Path.Combine(
                _projectsDir,
                request.projectId,
                "chats",
                request.chatId);

            if (String.IsNullOrWhiteSpace(request.branchId))
            {
                return Path.Combine(
                    chatRoot,
                    "artifacts",
                    request.jobId);
            }

            return Path.Combine(
                chatRoot,
                "branches",
                request.branchId,
                "artifacts",
                request.jobId);
        }

        // Moves a claimed job directory whose result.* is committed and verifiable into
        // completed/ or failed/. Never rewrites result.json or result.ready.
        private bool TryFinalizeCommittedClaimed(
            string claimedPath,
            string jobId,
            out string reason)
        {
            reason = null;

            string resultPath = Path.Combine(claimedPath, "result.json");
            string readyPath = Path.Combine(claimedPath, "result.ready");

            if (!File.Exists(readyPath))
            {
                reason = "result.ready is absent";
                return false;
            }

            if (!CommitMarkerUtil.TryVerify(resultPath, readyPath, out reason))
                return false;

            try
            {
                JobResult existing = JsonUtil.Deserialize<JobResult>(
                    File.ReadAllText(resultPath, Encoding.UTF8));

                string target =
                    existing != null &&
                    existing.status == "completed"
                    ? Path.Combine(_completedDir, jobId)
                    : Path.Combine(_failedDir, jobId);

                if (Directory.Exists(target))
                {
                    target += ".recovered-" +
                        DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                }

                MoveDirectoryWithRetry(claimedPath, target, 8, 500);
                Log("Finalized committed job " + jobId + " into " + target);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        // Scan-time pass over claimed/: a committed job that could not be moved earlier
        // (DriveFS hiccup, occupied target) is finalized here, so that a consumer never
        // has to wait for a BridgeAgent restart.
        private void FinalizeStrandedClaimedJobs()
        {
            string[] dirs;

            try
            {
                if (!Directory.Exists(_claimedDir))
                    return;

                dirs = Directory.GetDirectories(_claimedDir);
            }
            catch (Exception ex)
            {
                Log("Claimed scan failed: " + ex.Message);
                return;
            }

            foreach (string dir in dirs)
            {
                if (_exiting)
                    return;

                string jobId = Path.GetFileName(dir);

                try
                {
                    ValidateSafeId(jobId, "claimed job folder");
                }
                catch
                {
                    continue;
                }

                if (_runningJobs.ContainsKey(jobId))
                    continue;

                if (!File.Exists(Path.Combine(dir, "result.ready")))
                    continue;

                string reason;
                if (TryFinalizeCommittedClaimed(dir, jobId, out reason))
                {
                    lock (_strandedLogLock)
                    {
                        _strandedLoggedUtc.Remove(jobId);
                    }
                    continue;
                }

                // Log a persistent failure at most once per hour per job.
                bool log;
                lock (_strandedLogLock)
                {
                    DateTime last;
                    log = !_strandedLoggedUtc.TryGetValue(jobId, out last) ||
                          (DateTime.UtcNow - last).TotalHours >= 1;

                    if (log)
                        _strandedLoggedUtc[jobId] = DateTime.UtcNow;
                }

                if (log)
                {
                    Log(
                        "Committed job " + jobId +
                        " could not be finalized yet: " + reason);
                }
            }
        }

        private void RecoverClaimedJobs()
        {
            if (!Directory.Exists(_claimedDir))
                return;

            foreach (string dir in Directory.GetDirectories(_claimedDir))
            {
                string jobId = Path.GetFileName(dir);

                try
                {
                    ValidateSafeId(jobId, "claimed job folder");
                    RecordUsedJobId(jobId);

                    string resultPath = Path.Combine(dir, "result.json");
                    string readyPath = Path.Combine(dir, "result.ready");

                    if (File.Exists(readyPath))
                    {
                        // A committed result is immutable. Temporary DriveFS read
                        // failures must not convert a successful result into failed;
                        // the scan-time finalizer retries every scanIntervalSeconds.
                        string reason;

                        if (!TryFinalizeCommittedClaimed(dir, jobId, out reason))
                        {
                            Log(
                                "Committed result for " + jobId +
                                " is not currently verifiable; leaving it in claimed: " +
                                reason);
                        }

                        continue;
                    }

                    // No result.ready exists, so no result has been committed yet.
                    // The previous Bridge process is gone; its kill-on-close Job Object
                    // should have terminated descendants. Commit an interrupted result.
                    JobResult failed = new JobResult();
                    failed.schemaVersion = 2;
                    failed.hostId = _config.hostId;
                    failed.jobId = jobId;
                    failed.status = "failed";
                    failed.completedUtc = DateTime.UtcNow.ToString("o");
                    failed.exitCode = -3;
                    failed.timedOut = false;
                    failed.outputDrained = false;
                    failed.structuredFinalSeen = false;
                    failed.structuredSuccess = false;
                    failed.agentErrorEvents = 0;
                    failed.agentErrorMessages = new List<string>();
                    failed.error =
                        "BridgeAgent restarted while this job was in claimed state.";
                    failed.artifacts = new List<ArtifactRecord>();
                    failed.missingArtifacts = new List<string>();
                    failed.failedArtifacts = new List<ArtifactFailure>();

                    AtomicFile.WriteJson(resultPath, failed);
                    CommitMarkerUtil.WriteForPayload(resultPath, readyPath);

                    string failedTarget = Path.Combine(_failedDir, jobId);

                    if (Directory.Exists(failedTarget))
                    {
                        failedTarget += ".recovered-" +
                            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    }

                    MoveDirectoryWithRetry(
                        dir,
                        failedTarget,
                        8,
                        500);

                    Log(
                        "Recovered interrupted job " +
                        jobId + " as failed.");

                    // v1.10.3: the database gets this result too (before, the job stayed "running" there).
                    _recoveredResults.Enqueue(new string[] { jobId, Path.Combine(failedTarget, "result.json") });
                }
                catch (Exception ex)
                {
                    Log(
                        "Failed to recover claimed job '" +
                        jobId + "': " + ex);
                }
            }
        }

        private void TryMoveBackToInbox(
            string claimedPath,
            string jobId)
        {
            try
            {
                string target = Path.Combine(_inboxDir, jobId);

                if (!Directory.Exists(target))
                {
                    MoveDirectoryWithRetry(
                        claimedPath,
                        target,
                        4,
                        200);
                }
            }
            catch (Exception ex)
            {
                Log(
                    "Could not return unclaimed job " +
                    jobId + " to inbox: " + ex.Message);
            }
        }

        private void LoadUsedJobIds()
        {
            if (!File.Exists(_usedJobIdsPath))
                return;

            foreach (string raw in File.ReadAllLines(_usedJobIdsPath, Encoding.UTF8))
            {
                string id = raw.Trim();
                if (id.Length == 0)
                    continue;

                try
                {
                    ValidateSafeId(id, "persisted jobId");
                    _usedJobIds.Add(id);
                }
                catch
                {
                    Log("Ignoring invalid persisted jobId: " + id);
                }
            }
        }

        private void RecordUsedJobId(string jobId)
        {
            lock (_usedJobIdsLock)
            {
                if (_usedJobIds.Contains(jobId))
                    return;

                File.AppendAllText(
                    _usedJobIdsPath,
                    jobId + Environment.NewLine,
                    new UTF8Encoding(false));

                _usedJobIds.Add(jobId);
            }
        }

        private bool EnsureHostOwnership(bool failOnConflict)
        {
            try
            {
                if (File.Exists(_hostOwnerPath))
                {
                    HostOwnerRecord existing = JsonUtil.Deserialize<HostOwnerRecord>(
                        File.ReadAllText(_hostOwnerPath, Encoding.UTF8));

                    if (existing != null &&
                        !String.IsNullOrWhiteSpace(existing.machineName) &&
                        !String.Equals(
                            existing.machineName,
                            Environment.MachineName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime updated;
                        bool parsed = DateTime.TryParse(
                            existing.updatedUtc,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out updated);

                        bool fresh =
                            parsed &&
                            updated.ToUniversalTime() >=
                                DateTime.UtcNow.AddSeconds(-_config.hostLeaseSeconds);

                        // Deterministic tie-break: when two machines share one hostId,
                        // the machine whose upper-case name sorts first (Ordinal) keeps
                        // claiming; the other stays passive. Without a winner both sides
                        // would go passive together and resume together after each lease.
                        bool otherWins =
                            String.CompareOrdinal(
                                existing.machineName.ToUpperInvariant(),
                                Environment.MachineName.ToUpperInvariant()) < 0;

                        if (fresh && otherWins)
                        {
                            _hostConflict = true;
                            string message =
                                "hostId '" + _config.hostId +
                                "' has a fresh owner heartbeat from machine '" +
                                existing.machineName + "', which wins the tie-break.";

                            if (failOnConflict)
                                throw new InvalidOperationException(message);

                            Log("Host ownership conflict: " + message);
                            return false;
                        }

                        if (fresh)
                        {
                            Log(
                                "hostId '" + _config.hostId +
                                "' also has a fresh heartbeat from machine '" +
                                existing.machineName +
                                "'; this machine wins the tie-break and keeps claiming.");
                        }
                    }
                }

                _hostConflict = false;

                int heartbeatInterval =
                    Math.Max(5, _config.hostLeaseSeconds / 3);

                if ((DateTime.UtcNow - _lastHostHeartbeatUtc).TotalSeconds >=
                    heartbeatInterval)
                {
                    HostOwnerRecord owner = new HostOwnerRecord();
                    owner.schemaVersion = 2;
                    owner.hostId = _config.hostId;
                    owner.machineName = Environment.MachineName;
                    owner.processId = Process.GetCurrentProcess().Id;
                    owner.bridgeVersion = BridgeVersion;
                    owner.capabilities = new string[]
                    {
                        "protocol-v3",
                        "conversation-resume",
                        "codex-fork",
                        "attachments",
                        // v1.6.0: attachments with "target" become files of the chat scratch; artifacts are references
                        "chat-files",
                        "cancel",
                        "modes",
                        "model-select",
                        "effort-select",
                        "auto-export",
                        "usage",
                        "project-catalog",
                        "open-chat-projects",
                        "context-folders",
                        "usage-limits",
                        "fast-transport",
                        "device-tools",
                        "permissions",
                        "chat-cowork",
                        "device-tools-deny"
                    };
                    if (DbqEnabled)
                    {
                        List<string> caps = new List<string>(owner.capabilities);
                        caps.Add("db-queue");
                        if (FollowupsEnabled)
                            caps.Add("followups"); // v1.7.0
                        owner.capabilities = caps.ToArray();
                    }
                    owner.fastTransport = BuildOwnerFastTransport();
                    owner.deviceCallKey = BuildOwnerDeviceKey();
                    owner.runAsWarning = _runAsWarning;
                    owner.contextRoots = BuildOwnerContextRoots();
                    owner.chatProjectEnvironments = _config.chatProjectEnvironments;
                    owner.protocolMinor = ProtocolMinor;
                    owner.agents = BuildOwnerAgents();
                    owner.projects = BuildOwnerProjects();
                    owner.devices = BuildOwnerDevices(); // v1.4.1: paired devices for CO Web on a phone
                    // v1.4.2: why no jobs are taken, so CO Web does not show a paused/draining Bridge as stale
                    owner.state = _draining ? "updating" : (_paused ? "paused" : "running");
                    owner.updateTo = _draining ? _updateTo : null;
                    owner.runningJobs = _runningJobs.Count;
                    owner.relayActive = RelayActiveCount; // v1.14.0: the CO Desktop doctor never ends a Bridge serving relay calls
                    owner.stateSinceUtc = _stateSinceUtc.ToString("o");
                    CheckDeviceHeartbeats();             // v1.4.1: push when a device of the corporate connector goes silent
                    owner.updatedUtc = DateTime.UtcNow.ToString("o");

                    AtomicFile.WriteJson(_hostOwnerPath, owner);
                    _lastHostHeartbeatUtc = DateTime.UtcNow;
                }

                SupOwnerOk(); // v1.9.0
                return true;
            }
            catch (Exception ex)
            {
                if (failOnConflict)
                    throw;

                // owner.json could not be read or written: report Drive as offline and
                // skip this scan; a conflict is only declared on a verified foreign heartbeat.
                _driveOnline = false;
                SupOwnerFailed(ex); // v1.9.0
                Log("Host ownership check failed: " + ex.Message);
                return false;
            }
        }

        // ---------- v0.8: usage limits (limits.json) ----------

        private readonly object _limitsLock = new object();
        private Dictionary<string, AgentLimits> _limitsByAgent;
        private string _limitsWritten;
        private DateTime _lastCodexLimitsUtc = DateTime.MinValue;

        private string LimitsPath()
        {
            return Path.Combine(_hostTransportRoot, "limits.json");
        }

        private void UpdateLimits(string agentId, AgentLimits limits)
        {
            lock (_limitsLock)
            {
                if (_limitsByAgent == null)
                {
                    _limitsByAgent = new Dictionary<string, AgentLimits>(StringComparer.Ordinal);
                    try
                    {
                        if (File.Exists(LimitsPath()))
                        {
                            LimitsFile existing = JsonUtil.Deserialize<LimitsFile>(File.ReadAllText(LimitsPath(), Encoding.UTF8));
                            if (existing != null && existing.agents != null)
                                foreach (KeyValuePair<string, AgentLimits> kv in existing.agents)
                                    _limitsByAgent[kv.Key] = kv.Value;
                        }
                    }
                    catch
                    {
                    }
                }

                AgentLimits previous;
                if (_limitsByAgent.TryGetValue(agentId, out previous) &&
                    String.CompareOrdinal(previous.observedUtc ?? String.Empty, limits.observedUtc ?? String.Empty) > 0)
                    return; // keep the newer observation

                _limitsByAgent[agentId] = limits;

                LimitsFile file = new LimitsFile();
                file.schemaVersion = 1;
                file.agents = _limitsByAgent;
                string body = JsonUtil.Serialize(file.agents);
                if (body == _limitsWritten)
                    return;

                file.updatedUtc = DateTime.UtcNow.ToString("o");
                try
                {
                    AtomicFile.WriteJson(LimitsPath(), file);
                    _limitsWritten = body;
                }
                catch (Exception ex)
                {
                    Log("limits.json could not be written: " + ex.Message);
                }
            }
        }

        // Limits observed outside a job (v1.4.0: AgentRuntime.ReadOfflineLimits; Codex reads its session logs).
        private void MaybeRefreshCodexLimits()
        {
            if ((DateTime.UtcNow - _lastCodexLimitsUtc).TotalSeconds < 60)
                return;
            _lastCodexLimitsUtc = DateTime.UtcNow;

            foreach (KeyValuePair<string, AgentProfile> pair in _config.agents)
            {
                try
                {
                    AgentLimits limits = AgentRuntime.Get(pair.Value.outputProtocol).ReadOfflineLimits(pair.Value);
                    if (limits != null)
                        UpdateLimits(pair.Key, limits);
                }
                catch (Exception ex)
                {
                    Log("Codex limits refresh failed: " + ex.Message);
                }
            }
        }

        // ---------- v0.7: folder index for the web folder browser ----------

        private string _foldersDir;
        private DateTime _lastFolderIndexUtc = DateTime.MinValue;
        private int _folderIndexRunning;

        private string FoldersDir()
        {
            if (_foldersDir == null)
                _foldersDir = Path.Combine(_hostTransportRoot, "folders");
            Directory.CreateDirectory(_foldersDir);
            Directory.CreateDirectory(Path.Combine(_foldersDir, "requests"));
            Directory.CreateDirectory(Path.Combine(_foldersDir, "results"));
            return _foldersDir;
        }

        private void MaybePublishFolderIndex()
        {
            if (_config.contextRoots == null || _config.contextRoots.Count == 0)
                return;

            string dir = FoldersDir();
            string marker = Path.Combine(dir, "refresh.request");
            bool requested = File.Exists(marker);
            bool due = (DateTime.UtcNow - _lastFolderIndexUtc).TotalHours >= _config.contextIndexRefreshHours;

            if (!requested && !due)
                return;

            if (Interlocked.CompareExchange(ref _folderIndexRunning, 1, 0) != 0)
                return;

            _lastFolderIndexUtc = DateTime.UtcNow;
            TryDeleteFile(marker);

            Task.Factory.StartNew(delegate
            {
                try
                {
                    foreach (KeyValuePair<string, ContextRoot> pair in _config.contextRoots)
                    {
                        FolderIndex index = BuildFolderIndex(pair.Key, pair.Value, String.Empty, pair.Value.indexDepth, _config.contextIndexMaxEntries);
                        AtomicFile.WriteJson(Path.Combine(dir, pair.Key + ".json"), index);
                    }
                    Log("Folder index published for " + _config.contextRoots.Count + " context root(s).");
                }
                catch (Exception ex)
                {
                    Log("Folder index failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _folderIndexRunning, 0);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private FolderIndex BuildFolderIndex(string rootId, ContextRoot root, string relativePath, int depth, int maxEntries)
        {
            FolderIndex index = new FolderIndex();
            index.schemaVersion = 1;
            index.rootId = rootId;
            index.relativePath = relativePath;
            index.depth = depth;
            index.generatedUtc = DateTime.UtcNow.ToString("o");

            ContextRoot ignored;
            string normalized;
            string start = ResolveContextPath(rootId, relativePath, out ignored, out normalized);

            int budget = maxEntries;
            index.tree = new FolderNode();
            index.tree.n = String.Empty;
            bool truncated = false;
            index.tree.c = ListFolders(root, start, depth, ref budget, ref truncated);
            index.truncated = truncated;
            return index;
        }

        private static List<FolderNode> ListFolders(ContextRoot root, string dir, int depth, ref int budget, ref bool truncated)
        {
            List<FolderNode> nodes = new List<FolderNode>();
            DirectoryInfo[] subdirs;

            try
            {
                subdirs = new DirectoryInfo(dir).GetDirectories();
            }
            catch
            {
                return nodes;
            }

            Array.Sort(subdirs, delegate(DirectoryInfo a, DirectoryInfo b) { return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });

            foreach (DirectoryInfo d in subdirs)
            {
                if (IsExcludedContextName(root, d.Name))
                    continue;

                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
                    continue;

                if ((d.Attributes & FileAttributes.ReparsePoint) != 0 &&
                    (NativePathInfo.GetReparseTag(d.FullName) & 0x20000000u) != 0)
                    continue; // symlinks and junctions are not offered

                if (budget-- <= 0)
                {
                    truncated = true;
                    break;
                }

                FolderNode node = new FolderNode();
                node.n = d.Name;

                if (depth > 1)
                {
                    node.c = ListFolders(root, d.FullName, depth - 1, ref budget, ref truncated);
                }
                else
                {
                    try
                    {
                        node.m = d.EnumerateDirectories().Any();
                    }
                    catch
                    {
                        node.m = false;
                    }
                }

                nodes.Add(node);
            }

            return nodes;
        }

        // Web client asks for deeper levels: folders/requests/<id>.json {rootId, relativePath}
        private void ProcessFolderRequests()
        {
            if (_config.contextRoots == null || _config.contextRoots.Count == 0)
                return;

            string dir = FoldersDir();
            string[] requests;

            try
            {
                requests = Directory.GetFiles(Path.Combine(dir, "requests"), "*.json");
            }
            catch
            {
                return;
            }

            foreach (string file in requests.Take(20))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                FolderIndex result;

                try
                {
                    ValidateSafeId(name, "folder request id");
                    FolderRequest req = JsonUtil.Deserialize<FolderRequest>(File.ReadAllText(file, Encoding.UTF8));
                    ContextRoot root;
                    if (req == null || !_config.contextRoots.TryGetValue(req.rootId ?? String.Empty, out root))
                        throw new InvalidOperationException("Unknown context root.");
                    result = BuildFolderIndex(req.rootId, root, req.relativePath, 2, 5000);
                }
                catch (Exception ex)
                {
                    result = new FolderIndex();
                    result.schemaVersion = 1;
                    result.generatedUtc = DateTime.UtcNow.ToString("o");
                    result.error = ex.Message;
                }

                try
                {
                    if (!String.IsNullOrEmpty(name) && Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,39}\z"))
                        AtomicFile.WriteJson(Path.Combine(dir, "results", name + ".json"), result);
                }
                catch (Exception ex)
                {
                    Log("Folder request result could not be written: " + ex.Message);
                }

                TryDeleteFile(file);
            }

            // Results are one-shot; keep them for one hour.
            try
            {
                foreach (string old in Directory.GetFiles(Path.Combine(dir, "results"), "*.json"))
                {
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(old)).TotalHours > 1)
                        TryDeleteFile(old);
                }
            }
            catch
            {
            }
        }

        private Dictionary<string, OwnerContextRoot> BuildOwnerContextRoots()
        {
            Dictionary<string, OwnerContextRoot> result = new Dictionary<string, OwnerContextRoot>(StringComparer.Ordinal);
            if (_config.contextRoots == null)
                return result;
            foreach (KeyValuePair<string, ContextRoot> pair in _config.contextRoots)
            {
                OwnerContextRoot r = new OwnerContextRoot();
                r.label = pair.Value.label;
                r.kind = pair.Value.kind;
                result[pair.Key] = r;
            }
            return result;
        }

        private Dictionary<string, OwnerAgentInfo> BuildOwnerAgents()
        {
            Dictionary<string, OwnerAgentInfo> result = new Dictionary<string, OwnerAgentInfo>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, AgentProfile> pair in _config.agents)
            {
                AgentProfile agent = pair.Value;
                OwnerAgentInfo info = new OwnerAgentInfo();
                info.displayName = String.IsNullOrWhiteSpace(agent.displayName) ? pair.Key : agent.displayName;
                info.modes = GetAgentModes(agent);
                info.models = GetAgentModels(agent);
                info.efforts = agent.efforts ?? new string[0];
                info.defaultModel = agent.defaultModel;
                info.defaultEffort = agent.defaultEffort;
                if (Corporate.IsAgent(pair.Key) || Corporate.IsGatewayRuntime(agent.kind))
                    info.corporate = true;
                result[pair.Key] = info;
            }

            return result;
        }

        private Dictionary<string, OwnerProjectInfo> BuildOwnerProjects()
        {
            Dictionary<string, OwnerProjectInfo> result = new Dictionary<string, OwnerProjectInfo>(StringComparer.Ordinal);
            string driveRootFull = EnsureTrailingSeparator(Path.GetFullPath(_driveRoot));

            foreach (KeyValuePair<string, ProjectProfile> pair in _config.projects)
            {
                ProjectProfile project = pair.Value;
                OwnerProjectInfo info = new OwnerProjectInfo();
                info.environmentId = project.environmentId;
                info.allowedAgents = project.allowedAgents;
                info.allowedModes = project.allowedModes;
                info.autoExport = project.autoExport;

                string ws = Path.GetFullPath(project.workspace);
                info.workspaceDriveRelativePath =
                    EnsureTrailingSeparator(ws).StartsWith(driveRootFull, StringComparison.OrdinalIgnoreCase)
                        ? EnsureTrailingSeparator(ws).Substring(driveRootFull.Length).Replace('\\', '/').TrimEnd('/')
                        : null;

                result[pair.Key] = info;
            }

            return result;
        }

        private static bool IsInsideGitWorkTree(
            string workspace,
            out string reason)
        {
            reason = null;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "git.exe";
#if NET8_0_OR_GREATER
            psi.FileName = BridgePlatform.Current.Executables.Git;
#endif
            psi.Arguments =
                "-C " + CommandLine.Quote(workspace) +
                " rev-parse --is-inside-work-tree";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);

            using (Process process = new Process())
            {
                process.StartInfo = psi;

                try
                {
                    if (!process.Start())
                    {
                        reason = "git.exe could not be started.";
                        return false;
                    }

                    // Read stderr on a separate thread so that neither pipe can fill up
                    // while the other one is being drained.
                    string errorText = String.Empty;
                    Thread errorReader = new Thread(delegate()
                    {
                        try { errorText = process.StandardError.ReadToEnd(); }
                        catch { }
                    });
                    errorReader.IsBackground = true;
                    errorReader.Start();

                    string output = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(5000))
                    {
                        KillProcessTreeFallback(process);
                        reason = "git rev-parse did not finish within 5 seconds.";
                        return false;
                    }

                    errorReader.Join(1000);

                    bool inside =
                        process.ExitCode == 0 &&
                        String.Equals(
                            output.Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);

                    if (!inside)
                    {
                        string detail = (errorText ?? String.Empty).Trim();
                        reason =
                            "'" + workspace + "' is not inside a Git work tree " +
                            "(git exit code " + process.ExitCode + ")." +
                            (detail.Length > 0 ? " " + detail : String.Empty) +
                            " Run 'git init' in or above the workspace.";
                    }

                    return inside;
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Process.Start failed: git.exe is missing from PATH or not executable.
                    reason =
                        "git.exe was not found on PATH (" + ex.Message +
                        "). Install Git for Windows or add it to PATH.";
                    return false;
                }
                catch (Exception ex)
                {
                    try
                    {
                        KillProcessTreeFallback(process);
                    }
                    catch
                    {
                    }

                    reason = "git rev-parse failed: " + ex.Message;
                    return false;
                }
            }
        }

        // ---------- v0.8: tray icon with status dot ----------

        private enum TrayState { Idle, Busy, Paused, Problem }

#if !BRIDGE_HEADLESS
        // net8/HeadlessUi.cs has the headless GetTrayIcon (macOS/Linux).
        private readonly Dictionary<TrayState, Icon> _trayIcons = new Dictionary<TrayState, Icon>();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon LoadAppIcon(Size size)
        {
            using (Stream s = typeof(BridgeApplicationContext).Assembly.GetManifestResourceStream("AiWorkbenchBridge.BridgeAgent.ico"))
            {
                if (s == null)
                    return new Icon(SystemIcons.Application, size);
                return new Icon(s, size);
            }
        }

        // Icons are built once per state and reused, so no GDI handles leak.
        private Icon GetTrayIcon(TrayState state)
        {
            Icon cached;
            if (_trayIcons.TryGetValue(state, out cached))
                return cached;

            Size size = SystemInformation.SmallIconSize;
            Icon result;

            using (Icon baseIcon = LoadAppIcon(size))
            {
                if (state == TrayState.Idle)
                {
                    result = (Icon)baseIcon.Clone();
                }
                else
                {
                    using (Bitmap bmp = new Bitmap(size.Width, size.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));
                            Color dot = state == TrayState.Busy ? Color.FromArgb(46, 204, 64)
                                : state == TrayState.Paused ? Color.FromArgb(150, 150, 150)
                                : Color.FromArgb(230, 57, 53);
                            int d = Math.Max(6, size.Width / 2);
                            Rectangle r = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
                            using (SolidBrush b = new SolidBrush(dot))
                                g.FillEllipse(b, r);
                            using (Pen pen = new Pen(Color.White, Math.Max(1f, size.Width / 16f)))
                                g.DrawEllipse(pen, r);
                        }

                        IntPtr h = bmp.GetHicon();
                        try
                        {
                            using (Icon tmp = Icon.FromHandle(h))
                                result = (Icon)tmp.Clone();
                        }
                        finally
                        {
                            DestroyIcon(h);
                        }
                    }
                }
            }

            _trayIcons[state] = result;
            return result;
        }
#endif

        private void UpdateTrayStatus()
        {
            if (_tray == null)
                return;

            string mode =
                _hostConflict
                ? "HOST CONFLICT"
                : (_paused ? "PAUSED" : "ONLINE");

            string status =
                mode +
                " | running " + _runningJobs.Count +
                " | queue " + _queuedJobs +
                " | Drive " + (_driveOnline ? "OK" : "OFFLINE");

            if (_transportItem != null)
                _transportItem.Text = "Transport: " + FastTransportStatus();

            _statusItem.Text = status;

            TrayState state =
                _hostConflict || !_driveOnline ? TrayState.Problem
                : _paused ? TrayState.Paused
                : _runningJobs.Count > 0 ? TrayState.Busy
                : TrayState.Idle;

            try
            {
                _tray.Icon = GetTrayIcon(state);
            }
            catch
            {
            }

            string tooltip = "AI Bridge: " + status;

            if (tooltip.Length > 63)
                tooltip = tooltip.Substring(0, 63);

            try
            {
                _tray.Text = tooltip;
            }
            catch
            {
            }
        }

        // v1.1.7: the launcher asks for a restart onto a new version by creating drain.request in the state folder.
        // Bridge stops taking jobs (pause) and exits with DrainedForUpdate once none is running. Every claim path
        // checks _paused and registers the job under _scanLock, so holding the lock makes the "no job" check final.
        private void CheckDrainRequest()
        {
            if (_exiting || _localStateRoot == null)
                return;
            if ((DateTime.UtcNow - _lastDrainCheckUtc).TotalSeconds < 5)
                return;
            _lastDrainCheckUtc = DateTime.UtcNow;

            bool requested;
            try { requested = File.Exists(Path.Combine(_localStateRoot, "drain.request")); }
            catch { return; }

            if (!requested)
            {
                if (_draining)
                {
                    _draining = false;
                    _paused = false;
                    _updateTo = null;
                    UpdateDrainCancelled();
                    if (_pauseItem != null) _pauseItem.Text = "Pause";
                    Log("Update drain cancelled; taking jobs again.");
                    HostStateChanged();
                }
                return;
            }

            if (!_draining)
            {
                _updateTo = ReadDrainVersion();
                _draining = true;
                UpdateDrainStarted();
                _paused = true;
                Log("Update waiting: no new jobs are taken; Bridge restarts when running jobs finish.");
                HostStateChanged();
            }
            _paused = true;
            if (_pauseItem != null) _pauseItem.Text = "Resume";

            if (!Monitor.TryEnter(_scanLock))
                return;
            try
            {
                if (_runningJobs.Count > 0 || RelayBusy) // v1.12.0: relay calls of site jobs finish too
                {
                    if (_runningJobs.Count > 0)
                        MaybePauseForUpdate(); // v1.16.0: after updateDrainMinutes the running jobs pause and continue later
                    return;
                }
                if (SupBlockDrainExit()) // v1.9.0: no launcher -> nobody would start Bridge again
                    return;
                Log("No running jobs; exiting for the update.");
                Environment.ExitCode = ExitCodes.DrainedForUpdate;
                _exiting = true;
            }
            finally
            {
                Monitor.Exit(_scanLock);
            }
            ExitThread();
        }

        // v1.4.2: drain.request holds "<version> <utc>" (launcher); null when unreadable.
        private string ReadDrainVersion()
        {
            try
            {
                string text = File.ReadAllText(Path.Combine(_localStateRoot, "drain.request"), Encoding.UTF8).Trim();
                string first = text.Split(new char[] { ' ' }, 2)[0];
                return Regex.IsMatch(first, @"\A\d+\.\d+\.\d+\z") ? first : null;
            }
            catch
            {
                return null;
            }
        }

        // v1.4.2: running/paused/updating changed — write owner.json on the next scan, which runs at once.
        private void HostStateChanged()
        {
            _stateSinceUtc = DateTime.UtcNow;
            _lastHostHeartbeatUtc = DateTime.MinValue;
            QueueScan();
        }

        private void RequestExit()
        {
            if (_runningJobs.Count > 0)
            {
                DialogResult answer = MessageBox.Show(
                    "There are " + _runningJobs.Count +
                    " running job(s). Stop them and exit?",
                    "AI Workbench Bridge",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                    return;
            }

            _exiting = true;

            foreach (KeyValuePair<string, WindowsJobObject> pair in _runningJobObjects)
            {
                try
                {
                    pair.Value.Terminate(10);
                }
                catch
                {
                }
            }

            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            _exiting = true;
            StopFastTransport();

            try
            {
                if (_uiTimer != null)
                {
                    _uiTimer.Stop();
                    _uiTimer.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                if (_scanTimer != null)
                    _scanTimer.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_watcher != null)
                    _watcher.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch
            {
            }

            Log("BridgeAgent stopped.");
            base.ExitThreadCore();
        }

        private void MaybeCleanupTransport()
        {
            if ((DateTime.UtcNow - _lastCleanupUtc).TotalHours < 24)
                return;

            _lastCleanupUtc = DateTime.UtcNow;

            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(
                    -_config.transportRetentionDays);

                CleanupOldDirectories(_completedDir, cutoff);
                CleanupOldDirectories(_failedDir, cutoff);
                CleanupOldDirectories(_rejectedDir, cutoff);
                CleanupOldDirectories(_eventsDir, cutoff);
            }
            catch (Exception ex)
            {
                Log("Transport cleanup failed: " + ex.Message);
            }
        }

        private static void CleanupOldDirectories(
            string root,
            DateTime cutoffUtc)
        {
            if (!Directory.Exists(root))
                return;

            foreach (string dir in Directory.GetDirectories(root))
            {
                try
                {
                    DirectoryInfo info = new DirectoryInfo(dir);

                    if (info.LastWriteTimeUtc < cutoffUtc)
                        Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        private void CleanupLocalLogs()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(
                    -_config.localLogRetentionDays);

                if (Directory.Exists(_localLogDir))
                {
                    foreach (string file in Directory.GetFiles(_localLogDir, "*.log"))
                    {
                        try
                        {
                            FileInfo info = new FileInfo(file);

                            if (info.LastWriteTimeUtc < cutoff)
                                info.Delete();
                        }
                        catch
                        {
                        }
                    }
                }

                // Agent output kept locally after a failed Drive copy follows the same
                // retention as the local logs.
                CleanupOldDirectories(
                    Path.Combine(_localJobsDir, "unsynced"),
                    cutoff);
            }
            catch
            {
            }
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    string file = Path.Combine(
                        _localLogDir,
                        DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");

                    File.AppendAllText(
                        file,
                        DateTime.UtcNow.ToString("o") +
                        " | " + message +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                BootstrapLog.Write(message);
            }
        }

        private static void OpenExplorer(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = CommandLine.Quote(path);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        private string ResolveExecutable(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable))
                return executable;

            if (Path.IsPathRooted(executable) ||
                executable.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                executable.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return ExpandPath(executable, _appDir);
            }

            // A bare executable name (for example cmd.exe) is intentionally
            // resolved by Windows through PATH/System32.
            return executable;
        }

        private static string ExpandPath(
            string path,
            string baseDir)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                path = BridgePlatform.Current.Paths.Normalize(path);
#endif
            string expanded = Environment.ExpandEnvironmentVariables(
                path ?? String.Empty);

            if (String.IsNullOrWhiteSpace(expanded))
                return expanded;

            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(baseDir, expanded);

            return Path.GetFullPath(expanded);
        }

        private static void ValidateSafeId(
            string value,
            string label)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(label + " is required.");

            if (!Regex.IsMatch(
                    value,
                    @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,39}\z"))
            {
                throw new InvalidOperationException(
                    label +
                    " must be 1-40 ASCII characters: letters, digits, '_' or '-'.");
            }

            if (IsReservedWindowsName(value))
            {
                throw new InvalidOperationException(
                    label + " is a reserved Windows name: " + value);
            }
        }

        private static bool IsReservedWindowsName(string value)
        {
            string upper = value.ToUpperInvariant();

            if (upper == "CON" ||
                upper == "PRN" ||
                upper == "AUX" ||
                upper == "NUL" ||
                upper == "CLOCK$")
                return true;

            if (Regex.IsMatch(upper, @"\ACOM[1-9]\z"))
                return true;

            if (Regex.IsMatch(upper, @"\ALPT[1-9]\z"))
                return true;

            return false;
        }

        private static string NormalizeRelativePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "Artifact path cannot be empty.");
            }

            string p = path
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(p))
            {
                throw new InvalidOperationException(
                    "Artifact path must be relative: " + path);
            }

            string[] parts = p.Split(
                new char[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                throw new InvalidOperationException(
                    "Artifact path cannot be empty: " + path);
            }

            foreach (string part in parts)
            {
                if (part == "." || part == "..")
                {
                    throw new InvalidOperationException(
                        "Artifact path traversal is not allowed: " + path);
                }

                if (part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Artifact path component cannot end with dot or space: " + path);
                }

                if (IsReservedWindowsName(part))
                {
                    throw new InvalidOperationException(
                        "Artifact path contains a reserved Windows name: " + path);
                }

                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new InvalidOperationException(
                        "Artifact path contains invalid characters: " + path);
                }
            }

            return String.Join(
                Path.DirectorySeparatorChar.ToString(),
                parts);
        }

        private static string SafeCombineUnderRoot(
            string root,
            string relative)
        {
            string normalizedRoot = EnsureTrailingSeparator(
                Path.GetFullPath(root));

            string combined = Path.GetFullPath(
                Path.Combine(root, relative));

            if (!combined.StartsWith(
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Path escapes its allowed root: " + relative);
            }

            return combined;
        }

        // v1.6.0: EnsureNoReparsePoints for the components that already exist (stops at the first missing one).
        private static void EnsureNoExistingReparsePoints(string root, string relative)
        {
            string current = Path.GetFullPath(root);
            foreach (string part in relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!File.Exists(current) && !Directory.Exists(current))
                    return;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 &&
                    (NativePathInfo.GetReparseTag(current) & 0x20000000u) != 0)
                    throw new InvalidOperationException("Attachment target crosses a pathname-surrogate reparse point: " + relative);
            }
        }

        private static void EnsureNoReparsePoints(
            string root,
            string relative)
        {
            string current = Path.GetFullPath(root);

            string[] parts = relative.Split(
                new char[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                current = Path.Combine(current, part);

                FileAttributes attributes = File.GetAttributes(current);

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    continue;

                uint tag = NativePathInfo.GetReparseTag(current);

                // IO_REPARSE_TAG_NAME_SURROGATE (0x20000000) marks reparse
                // points that substitute another pathname, including symlinks and
                // junction/mount points. Cloud Files placeholders can carry the
                // ReparsePoint attribute without being pathname surrogates.
                if ((tag & 0x20000000u) != 0)
                {
                    throw new InvalidOperationException(
                        "Artifact path crosses a pathname-surrogate reparse point: " +
                        relative);
                }
            }
        }

        private static void EnsureSingleLinkFile(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                ByHandleFileInformation info;

                if (!NativeFileInfo.GetFileInformationByHandle(
                        stream.SafeFileHandle,
                        out info))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "GetFileInformationByHandle failed.");
                }

                if (info.NumberOfLinks > 1)
                {
                    throw new InvalidOperationException(
                        "Artifact is a multi-link file and will not be exported: " +
                        path);
                }
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.EndsWith(
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private string MakeDriveRelative(string fullPath)
        {
            string root = EnsureTrailingSeparator(
                Path.GetFullPath(_driveRoot));

            string full = Path.GetFullPath(fullPath);

            if (!full.StartsWith(
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            return full
                .Substring(root.Length)
                .Replace('\\', '/');
        }

        private static string ReadTextWithRetry(
            string path,
            int attempts,
            int delayMs)
        {
            Exception last = null;

            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    return File.ReadAllText(
                        path,
                        Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(delayMs);
                }
            }

            throw last ?? new IOException(
                "Could not read " + path);
        }

        private static void MoveDirectoryWithRetry(
            string source,
            string destination,
            int attempts,
            int delayMs)
        {
            Exception last = null;

            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(delayMs);
                }
            }

            throw last ?? new IOException(
                "Could not move directory from " +
                source + " to " + destination);
        }

        private static void KillProcessTreeFallback(Process process)
        {
            if (process == null)
                return;
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                try { process.Kill(true); } catch { }
                return;
            }
#endif

            try
            {
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill.exe";
                psi.Arguments = "/PID " + process.Id + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (Process killer = Process.Start(psi))
                {
                    if (killer != null)
                        killer.WaitForExit(10000);
                }
            }
            catch
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }

    internal sealed class EventBatchWriter : IDisposable
    {
        private readonly string _eventDir;
        private readonly int _flushBytes;
        private readonly int _maxPendingBytes;
        private readonly Action<string> _localLog;
        private readonly object _sync = new object();
        private readonly object _ioLock = new object();

        private List<JobEvent> _pending = new List<JobEvent>();
        private System.Threading.Timer _timer;
        private int _pendingBytes;
        private int _nextSequence;
        private int _flushQueued;
        private int _droppedEvents;
        private bool _disposed;

        // v0.9: live copy of each written batch (Pub/Sub). Must never throw into the writer.
        public Action<EventBatch> OnBatchWritten;

        public EventBatchWriter(
            string eventDir,
            int flushSeconds,
            int flushBytes,
            int maxPendingBytes,
            Action<string> localLog)
        {
            _eventDir = eventDir;
            _flushBytes = Math.Max(1024, flushBytes);
            _maxPendingBytes = Math.Max(_flushBytes, maxPendingBytes);
            _localLog = localLog;

            Directory.CreateDirectory(_eventDir);

            int intervalMs = Math.Max(1, flushSeconds) * 1000;

            _timer = new System.Threading.Timer(
                delegate { ScheduleFlush(); },
                null,
                intervalMs,
                intervalMs);
        }

        public void Write(
            string source,
            string type,
            string content)
        {
            TryWrite(source, type, content);
        }

        // v1.8.0: false when the event was not taken (writer disposed or buffer full) - the caller may keep it for later.
        public bool TryWrite(
            string source,
            string type,
            string content)
        {
            bool schedule = false;

            lock (_sync)
            {
                if (_disposed)
                    return false;

                int estimated =
                    Encoding.UTF8.GetByteCount(content ?? String.Empty) + 256;

                if (_pendingBytes + estimated > _maxPendingBytes)
                {
                    _droppedEvents++;

                    if (_droppedEvents == 1 && _localLog != null)
                    {
                        _localLog(
                            "Event buffer reached " + _maxPendingBytes +
                            " bytes; subsequent agent events will be dropped until " +
                            "Drive event batches can be flushed.");
                    }

                    return false;
                }

                InjectDroppedNoticeLocked();

                if (_pendingBytes + estimated > _maxPendingBytes)
                {
                    _droppedEvents++;
                    return false;
                }

                JobEvent item = NewEventLocked(
                    source,
                    type,
                    content ?? String.Empty);

                _pending.Add(item);
                _pendingBytes += EstimateBytes(item);

                schedule = _pendingBytes >= _flushBytes;
            }

            if (schedule)
                ScheduleFlush();
            return true;
        }

        public void DisposeAndFlush()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;

                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                InjectDroppedNoticeLocked();
            }

            // Wait for any current Drive I/O, then perform the terminal flush
            // synchronously. Drive I/O never holds _sync, so stdout callbacks remain
            // independent from DriveFS latency.
            lock (_ioLock)
            {
                FlushOneCore();
            }
        }

        public void Dispose()
        {
            DisposeAndFlush();
        }

        private JobEvent NewEventLocked(
            string source,
            string type,
            string content)
        {
            JobEvent item = new JobEvent();
            item.schemaVersion = 2;
            item.sequence = ++_nextSequence;
            item.createdUtc = DateTime.UtcNow.ToString("o");
            item.source = source;
            item.type = type;
            item.content = content;
            return item;
        }

        private int EstimateBytes(JobEvent item)
        {
            return Encoding.UTF8.GetByteCount(item.content ?? String.Empty) + 256;
        }

        private void InjectDroppedNoticeLocked()
        {
            if (_droppedEvents <= 0)
                return;

            string text =
                _droppedEvents +
                " event(s) were dropped while the local event buffer was full.";

            JobEvent notice = NewEventLocked(
                "bridge",
                "events_dropped",
                text);

            int size = EstimateBytes(notice);

            if (_pendingBytes + size > _maxPendingBytes)
            {
                // Keep the counter; the notice will be emitted after capacity returns.
                _nextSequence--;
                return;
            }

            _pending.Add(notice);
            _pendingBytes += size;
            _droppedEvents = 0;
        }

        private void ScheduleFlush()
        {
            if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    try
                    {
                        lock (_ioLock)
                        {
                            FlushOneCore();
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _flushQueued, 0);

                        bool more;
                        lock (_sync)
                        {
                            more = !_disposed && _pendingBytes >= _flushBytes;
                        }

                        if (more)
                            ScheduleFlush();
                    }
                });
        }

        private void FlushOneCore()
        {
            List<JobEvent> batchEvents;
            int batchBytes;

            lock (_sync)
            {
                InjectDroppedNoticeLocked();

                if (_pending.Count == 0)
                    return;

                batchEvents = _pending;
                batchBytes = _pendingBytes;
                _pending = new List<JobEvent>();
                _pendingBytes = 0;
            }

            try
            {
                int first = batchEvents[0].sequence;
                int last = batchEvents[batchEvents.Count - 1].sequence;

                EventBatch batch = new EventBatch();
                batch.schemaVersion = 2;
                batch.firstSequence = first;
                batch.lastSequence = last;
                batch.events = batchEvents;

                string path = Path.Combine(
                    _eventDir,
                    first.ToString("D6") + "-" +
                    last.ToString("D6") + ".json");

                AtomicFile.WriteJson(path, batch);

                Action<EventBatch> hook = OnBatchWritten;
                if (hook != null)
                {
                    try { hook(batch); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (_localLog != null)
                {
                    _localLog(
                        "Drive event batch flush failed: " +
                        ex.Message);
                }

                lock (_sync)
                {
                    List<JobEvent> combined = new List<JobEvent>(
                        batchEvents.Count + _pending.Count);
                    combined.AddRange(batchEvents);
                    combined.AddRange(_pending);

                    int totalBytes = batchBytes + _pendingBytes;
                    int dropped = 0;

                    while (combined.Count > 0 &&
                           totalBytes > _maxPendingBytes)
                    {
                        JobEvent last = combined[combined.Count - 1];
                        totalBytes -= EstimateBytes(last);
                        combined.RemoveAt(combined.Count - 1);
                        dropped++;
                    }

                    _pending = combined;
                    _pendingBytes = Math.Max(0, totalBytes);
                    _droppedEvents += dropped;
                }
            }
        }
    }

    internal sealed class WindowsJobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private IntPtr _handle;
        private bool _disposed;
#if NET8_0_OR_GREATER
        // macOS/Linux (Freedom F3a): the process tree adapter, net8/PlatformUnix.cs.
        private readonly UnixProcessTree _unix;
#endif

        public WindowsJobObject()
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                _unix = new UnixProcessTree();
                return;
            }
#endif
            _handle = NativeJob.CreateJobObject(
                IntPtr.Zero,
                null);

            if (_handle == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "CreateJobObject failed.");
            }

            JobObjectExtendedLimitInformation info =
                new JobObjectExtendedLimitInformation();

            info.BasicLimitInformation.LimitFlags =
                JobObjectLimitKillOnJobClose;

            int length = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(
                    info,
                    ptr,
                    false);

                if (!NativeJob.SetInformationJobObject(
                        _handle,
                        JobObjectExtendedLimitInformationClass,
                        ptr,
                        (uint)length))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "SetInformationJobObject failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Assign(Process process)
        {
            if (_disposed)
                throw new ObjectDisposedException("WindowsJobObject");
#if NET8_0_OR_GREATER
            if (_unix != null)
            {
                _unix.Assign(process);
                return;
            }
#endif

            if (!NativeJob.AssignProcessToJobObject(
                    _handle,
                    process.Handle))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "AssignProcessToJobObject failed.");
            }
        }

        // v1.9.0: number of live processes in the job (JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses);
        // -1 when unknown (disposed, query failed).
        public int ActiveProcesses()
        {
#if NET8_0_OR_GREATER
            if (_unix != null)
                return _disposed ? -1 : _unix.ActiveProcesses();
#endif
            if (_disposed || _handle == IntPtr.Zero)
                return -1;
            IntPtr ptr = Marshal.AllocHGlobal(48);
            try
            {
                uint returned;
                if (!NativeJob.QueryInformationJobObject(_handle, 1, ptr, 48, out returned))
                    return -1;
                return Marshal.ReadInt32(ptr, 40);
            }
            catch
            {
                return -1;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Terminate(uint exitCode)
        {
#if NET8_0_OR_GREATER
            if (_unix != null)
            {
                if (!_disposed)
                    _unix.Terminate(exitCode);
                return;
            }
#endif
            if (_disposed || _handle == IntPtr.Zero)
                return;

            NativeJob.TerminateJobObject(
                _handle,
                exitCode);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
#if NET8_0_OR_GREATER
            if (_unix != null)
                _unix.Dispose();
#endif

            if (_handle != IntPtr.Zero)
            {
                NativeJob.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    internal static class NativeJob
    {
        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength,
            out uint lpReturnLength);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr CreateJobObject(
            IntPtr lpJobAttributes,
            string lpName);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            IntPtr hJob,
            IntPtr hProcess);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(
            IntPtr hJob,
            uint uExitCode);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(
            IntPtr hObject);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    internal static class NativeFileInfo
    {
        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);
    }

    internal static class NativePathInfo
    {
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr FindFirstFile(
            string lpFileName,
            out Win32FindData lpFindFileData);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr hFindFile);

        internal static uint GetReparseTag(string path)
        {
            Win32FindData data;
            IntPtr handle = FindFirstFile(path, out data);

            if (handle == InvalidHandleValue)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "FindFirstFile failed for reparse-point inspection.");
            }

            try
            {
                return data.dwReserved0;
            }
            finally
            {
                FindClose(handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Win32FindData
    {
        public FileAttributes dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    internal static class CommitMarkerUtil
    {
        // v1.3.1: size and hash come from the bytes read back. On G: Drive for Desktop can report a stale
        // FileInfo.Length right after a write (4096 for a 4146-byte request.json on 2026-10-15), and such a marker
        // never verifies: the job waits in the inbox for good.
        public static void WriteForPayload(
            string payloadPath,
            string markerPath)
        {
            WriteForBytes(File.ReadAllBytes(payloadPath), Path.GetFileName(payloadPath), markerPath);
        }

        // Marker for bytes the caller has just written as payloadName next to markerPath.
        public static void WriteForBytes(
            byte[] payload,
            string payloadName,
            string markerPath)
        {
            CommitMarker marker = new CommitMarker();
            marker.schemaVersion = 2;
            marker.payload = payloadName;
            marker.sizeBytes = payload.LongLength;
            marker.sha256 = HashUtil.ComputeSha256(payload);

            AtomicFile.WriteJson(markerPath, marker);
        }

        public static bool TryVerify(
            string payloadPath,
            string markerPath,
            out string reason)
        {
            byte[] ignored;
            return TryReadVerifiedPayload(
                payloadPath,
                markerPath,
                out ignored,
                out reason);
        }

        public static bool TryReadVerifiedPayload(
            string payloadPath,
            string markerPath,
            out byte[] payloadBytes,
            out string reason)
        {
            payloadBytes = null;
            reason = null;

            try
            {
                if (!File.Exists(payloadPath))
                {
                    reason = "payload is not present yet";
                    return false;
                }

                if (!File.Exists(markerPath))
                {
                    reason = "commit marker is not present yet";
                    return false;
                }

                string json = File.ReadAllText(
                    markerPath,
                    Encoding.UTF8);

                CommitMarker marker =
                    JsonUtil.Deserialize<CommitMarker>(json);

                if (marker == null)
                {
                    reason = "commit marker cannot be parsed";
                    return false;
                }

                if (marker.schemaVersion != 2)
                {
                    reason = "commit marker schemaVersion is not 2";
                    return false;
                }

                if (!String.Equals(
                        marker.payload,
                        Path.GetFileName(payloadPath),
                        StringComparison.Ordinal))
                {
                    reason = "commit marker payload name mismatch";
                    return false;
                }

                payloadBytes = File.ReadAllBytes(payloadPath);

                if ((long)payloadBytes.Length != marker.sizeBytes)
                {
                    reason = "payload size does not match commit marker";
                    payloadBytes = null;
                    return false;
                }

                string actualHash =
                    HashUtil.ComputeSha256(payloadBytes);

                if (!String.Equals(
                        actualHash,
                        marker.sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = "payload SHA-256 does not match commit marker";
                    payloadBytes = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                payloadBytes = null;
                return false;
            }
        }
    }

    internal static class HashUtil
    {
        public static string ComputeMd5(string path)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hash = md5.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);

                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));

                return sb.ToString();
            }
        }

        public static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(
                    hash.Length * 2);

                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));

                return sb.ToString();
            }
        }
    }

    internal static class AtomicFile
    {
        public const int TempSuffixLength = 10; // ".t" + 8 hex chars

        public static void WriteJson(
            string path,
            object value)
        {
            WriteText(
                path,
                JsonUtil.Serialize(value));
        }

        public static void WriteText(
            string path,
            string content)
        {
            string dir = Path.GetDirectoryName(path);

            if (!String.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = path + NewTempSuffix();

            File.WriteAllText(
                tmp,
                content ?? String.Empty,
                new UTF8Encoding(false));

            ReplaceOrMove(tmp, path);
        }

        public static void CopyFile(
            string source,
            string destination)
        {
            string dir = Path.GetDirectoryName(destination);

            if (!String.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = destination + NewTempSuffix();

            File.Copy(source, tmp, true);
            ReplaceOrMove(tmp, destination);
        }

        private static string NewTempSuffix()
        {
            return ".t" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void ReplaceOrMove(
            string tmp,
            string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(tmp, destination);
                return;
            }

            try
            {
                File.Replace(
                    tmp,
                    destination,
                    null,
                    true);
            }
            catch
            {
                // Fallback for filesystems that do not implement File.Replace.
                // This path is never used to mutate a payload after its *.ready
                // commit marker has been written.
                File.Delete(destination);
                File.Move(tmp, destination);
            }
        }
    }

    internal static class JsonUtil
    {
        public static T Deserialize<T>(string json)
        {
            JavaScriptSerializer serializer =
                CreateSerializer();

            return serializer.Deserialize<T>(json);
        }

        public static object DeserializeObject(string json)
        {
            JavaScriptSerializer serializer =
                CreateSerializer();

            return serializer.DeserializeObject(json);
        }

        public static string Serialize(object value)
        {
            JavaScriptSerializer serializer =
                CreateSerializer();

            return serializer.Serialize(value);
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer =
                new JavaScriptSerializer();

            serializer.MaxJsonLength = Int32.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }
    }

    internal static class CommandLine
    {
        public static string Build(string[] arguments)
        {
            if (arguments == null ||
                arguments.Length == 0)
            {
                return String.Empty;
            }

            return String.Join(
                " ",
                arguments.Select(
                    delegate(string value)
                    {
                        return Quote(
                            value ?? String.Empty);
                    }).ToArray());
        }

        public static string Quote(string value)
        {
            if (value == null)
                return "\"\"";

            if (value.Length > 0 &&
                !value.Any(
                    delegate(char c)
                    {
                        return Char.IsWhiteSpace(c) ||
                               c == '"';
                    }))
            {
                return value;
            }

            StringBuilder result =
                new StringBuilder();

            result.Append('"');

            int backslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    result.Append(
                        '\\',
                        backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    result.Append(
                        '\\',
                        backslashes);
                    backslashes = 0;
                }

                result.Append(c);
            }

            if (backslashes > 0)
            {
                result.Append(
                    '\\',
                    backslashes * 2);
            }

            result.Append('"');
            return result.ToString();
        }
    }

    internal sealed class BridgeConfig
    {
        public int schemaVersion { get; set; }
        public string hostId { get; set; }
        public string driveRoot { get; set; }
        public string driveReadyPath { get; set; }
        public string localStateRoot { get; set; }
        public int startupDriveWaitSeconds { get; set; }
        public int scanIntervalSeconds { get; set; }
        public int eventFlushSeconds { get; set; }
        public int eventFlushBytes { get; set; }
        public int eventMaxPendingBytes { get; set; }
        public int outputDrainTimeoutSeconds { get; set; }
        public int hostLeaseSeconds { get; set; }
        public int maxParallelJobs { get; set; }
        // v1.2.6: Claude output tokens above which a corporate dispatcher job with 0 gateway tokens gets a policy_warning event;
        // 0 = default 4000, negative = off.
        public int dispatcherPolicyWarnTokens { get; set; }
        // Freedom F1: base URL of the CO site (https://co.example.org); empty = the origin of dbQueue.url or push.url
        public string siteUrl { get; set; }
        // v1.2.7: Web Push through <site>/agent/push.php (null or enabled=false -> no push)
        public PushConfig push { get; set; }
        // v1.3.0: job queue in the database (<site>/agent/db.php); null or enabled=false -> Drive inbox only
        public DbQueueConfig dbQueue { get; set; }
        // v1.9.0: Bridge supervision (BridgePush.cs, SupervisorConfig); null -> defaults, restartMode "dry"
        public SupervisorConfig supervisor { get; set; }
        public int maxProcessTimeoutMinutes { get; set; }
        public int maxArtifactDestinationPathChars { get; set; }
        public int transportRetentionDays { get; set; }
        public int localLogRetentionDays { get; set; }
        public Dictionary<string, AgentProfile> agents { get; set; }
        public Dictionary<string, ProjectProfile> projects { get; set; }

        // v0.6 auto-export limits (hard caps; a request can only lower them).
        public int autoExportMaxFiles { get; set; }
        public long autoExportMaxBytes { get; set; }
        public long autoExportMaxFileBytes { get; set; }
        public int autoExportMaxScanEntries { get; set; }

        // v0.6.1: environments whose Drive-only projects may run in chat mode.
        public Dictionary<string, ChatProjectPolicy> chatProjectEnvironments { get; set; }

        // v0.7: local folders that jobs may attach as context (allowlist of roots).
        public Dictionary<string, ContextRoot> contextRoots { get; set; }
        public int contextIndexMaxEntries { get; set; }
        public int contextIndexRefreshHours { get; set; }
        public int maxContextFolders { get; set; }

        // v0.9: Pub/Sub + Drive API fast transport; null means defaults (enabled).
        public GoogleSettings google { get; set; }

        // v1.0: CO Desktop device tools and the separate CLI account (TZ-CO-DESKTOP §8, §9.7).
        public DeviceSettings devices { get; set; }
        public RunAsSettings runAs { get; set; }
    }

    internal sealed class ContextRoot
    {
        public string path { get; set; }
        public string label { get; set; }
        public string kind { get; set; }          // "local" or "drive"
        public string[] excludeNames { get; set; } // extra names excluded at any depth
        public int indexDepth { get; set; }
    }

    // v1.1: what the agent may do in this job, chosen per chat in CO Web. Absent = everything allowed.
    internal sealed class PermissionsRequest
    {
        public string files { get; set; }         // "read" or "write"
        public bool? shell { get; set; }
        public bool? web { get; set; }
    }

    internal sealed class ContextFolderRequest
    {
        public string rootId { get; set; }
        public string relativePath { get; set; }
        public string access { get; set; }        // "read" or "write"
        public string label { get; set; }
    }

    internal sealed class ResolvedContext
    {
        public string rootId;
        public string relativePath;
        public string fullPath;
        public bool write;
        public string label;
    }

    internal sealed class FolderNode
    {
        public string n { get; set; }
        public List<FolderNode> c { get; set; }
        public bool m { get; set; }                // more levels exist below the index depth
    }

    internal sealed class FolderIndex
    {
        public int schemaVersion { get; set; }
        public string rootId { get; set; }
        public string relativePath { get; set; }
        public string generatedUtc { get; set; }
        public int depth { get; set; }
        public bool truncated { get; set; }
        public string error { get; set; }
        public FolderNode tree { get; set; }
    }

    internal sealed class FolderRequest
    {
        public string rootId { get; set; }
        public string relativePath { get; set; }
    }

    internal sealed class OwnerContextRoot
    {
        public string label { get; set; }
        public string kind { get; set; }
    }

    internal sealed class ChatProjectPolicy
    {
        public string[] allowedAgents { get; set; }
        public bool autoExport { get; set; }
    }

    internal sealed class AgentProfile
    {
        public string executable { get; set; }
        public string[] arguments { get; set; }
        public bool promptToStdin { get; set; }
        public string outputProtocol { get; set; }
        public bool requireStructuredSuccess { get; set; }
        public bool requiresGitRepository { get; set; }
        public Dictionary<string, string> environment { get; set; }

        // v1.1: runtime kind. Empty/absent = launch "executable" (claude/codex CLI). "corporate" (legacy "engie") = no
        // process; Bridge calls the connector's gateway tool and returns its reply (corporate models via the gateway).
        public string kind { get; set; }

        // v0.6: optional label shown by the web client.
        public string displayName { get; set; }

        // v0.6: per-mode overrides ("chat", "cowork"). Without "modes" the base
        // arguments serve the "cowork" mode only.
        public Dictionary<string, ModeProfile> modes { get; set; }

        // v0.6: model / effort selection. "{value}" in the templates is replaced by
        // the validated catalog id; the browser never supplies raw arguments.
        public string[] modelArguments { get; set; }
        public string[] effortArguments { get; set; }
        public List<CatalogModel> models { get; set; }
        public string[] efforts { get; set; }
        public string defaultModel { get; set; }
        public string defaultEffort { get; set; }

        // v0.6: optional provider model list (Codex models_cache.json).
        public string modelCatalogFile { get; set; }

        // v0.8: Codex session logs (~/.codex/sessions) carrying rate_limits snapshots.
        public string rateLimitSessionsDir { get; set; }

        // v1.2.4: Claude agents only; "{value}" = resolved model. Injected into every subagent through a
        // SubagentStart hook because subagents do not inherit --append-system-prompt.
        public string subagentContext { get; set; }
    }

    internal sealed class ModeProfile
    {
        public string[] arguments { get; set; }
        public string[] resumeExtraArguments { get; set; }
        public bool? requiresGitRepository { get; set; }
        public int defaultTimeoutMinutes { get; set; }
    }

    internal sealed class CatalogModel
    {
        public string id { get; set; }
        public string label { get; set; }
        public string description { get; set; }
        public string[] efforts { get; set; }
        public string defaultEffort { get; set; }
    }

    internal sealed class ProjectProfile
    {
        public string environmentId { get; set; }
        public string workspace { get; set; }
        public string[] allowedAgents { get; set; }

        // v0.6
        public string[] allowedModes { get; set; }
        public bool autoExport { get; set; }
    }

    internal sealed class OutputRequest
    {
        public bool? autoExport { get; set; }
        public int maxFiles { get; set; }
        public long maxBytes { get; set; }
    }

    internal sealed class UsageRecord
    {
        public long inputTokens { get; set; }
        public long cachedInputTokens { get; set; }
        public long cacheCreationInputTokens { get; set; }
        public long outputTokens { get; set; }
        public long reasoningOutputTokens { get; set; }
        public double? costUsd { get; set; }
        public long? durationMs { get; set; }
        public int? numTurns { get; set; }
    }

    internal sealed class ExportSkip
    {
        public string path { get; set; }
        public string reason { get; set; }
    }

    internal sealed class OwnerAgentInfo
    {
        public string displayName { get; set; }
        public string[] modes { get; set; }
        public List<CatalogModel> models { get; set; }
        public string[] efforts { get; set; }
        public string defaultModel { get; set; }
        public string defaultEffort { get; set; }
        public bool? corporate { get; set; } // Freedom F1 5b: an agent of the corporate connector (server: confidential)
    }

    internal sealed class OwnerProjectInfo
    {
        public string environmentId { get; set; }
        public string[] allowedAgents { get; set; }
        public string[] allowedModes { get; set; }
        public bool autoExport { get; set; }
        // Drive-relative workspace path when the workspace lives under driveRoot;
        // null for local workspaces (local paths are never published).
        public string workspaceDriveRelativePath { get; set; }
    }

    internal sealed class JobRequest
    {
        // v1.6.0: attachments written into the chat scratch by StageAttachments ("report.docx (new version)"), for the prompt.
        [ScriptIgnore]
        public List<string> chatFileNotes { get; set; }

        public int schemaVersion { get; set; }
        public string hostId { get; set; }
        public string jobId { get; set; }
        public string environmentId { get; set; }
        public string projectId { get; set; }
        public string chatId { get; set; }
        public string branchId { get; set; }
        public string agent { get; set; }
        public string instruction { get; set; }
        public ConversationRequest conversation { get; set; }
        public List<AttachmentRequest> attachments { get; set; }
        public List<string> artifactPaths { get; set; }
        public int timeoutMinutes { get; set; }

        // v0.6 (protocol v3, minor 1). All optional.
        public int protocolMinor { get; set; }
        public string mode { get; set; }
        public string model { get; set; }
        public string effort { get; set; }
        public OutputRequest output { get; set; }

        // v0.7
        public List<ContextFolderRequest> contextFolders { get; set; }
        public PermissionsRequest permissions { get; set; }

        // v1.0 (protocol v3, minor 2)
        public List<string> devices { get; set; }
        // v1.3.5: device tools hidden from this job (CO Web: a paused or switched-off corporate connector)
        public List<string> deviceToolsDeny { get; set; }
        public bool tainted { get; set; }
        public JobTaskRequest task { get; set; }

        // Filled by Bridge after validation; never read from request.json.
        [ScriptIgnore]
        public List<ResolvedContext> resolvedContexts { get; set; }
        // v1.10.0: agent browser settings of the chat (db.php browser_settings), filled before the agent starts
        [ScriptIgnore]
        public BridgeApplicationContext.BrowserSettings browserSettings { get; set; }
    }


    internal sealed class JobTaskRequest
    {
        public string taskId { get; set; }
        public string parentRunId { get; set; }
        public int budget { get; set; }
    }

    internal sealed class ConversationRequest
    {
        public string operation { get; set; }
        public string sessionId { get; set; }
    }

    internal sealed class AttachmentRequest
    {
        public string path { get; set; }
        public long sizeBytes { get; set; }
        public string sha256 { get; set; }

        // v1.6.0 (CO Web 0.55.0): path inside the chat scratch the file is written to (a new version of that chat file);
        // null = staged under .co-attachments/<jobId>/ as before.
        public string target { get; set; }
    }
    internal sealed class JobResult
    {
        // v1.16.0: stopped for the Bridge update to this version ("" unknown); the job continues as continuationJobId
        public string pausedForUpdate { get; set; }
        public string continuationJobId { get; set; }
        public int schemaVersion { get; set; }
        public string hostId { get; set; }
        public string jobId { get; set; }
        public string environmentId { get; set; }
        public string projectId { get; set; }
        public string chatId { get; set; }
        public string branchId { get; set; }
        public string agent { get; set; }
        public string conversationOperation { get; set; }
        public string status { get; set; }
        public string startedUtc { get; set; }
        public string completedUtc { get; set; }
        public int exitCode { get; set; }
        public bool timedOut { get; set; }
        public bool outputDrained { get; set; }
        public bool structuredFinalSeen { get; set; }
        public bool structuredSuccess { get; set; }
        public string sessionId { get; set; }
        public string agentFinalResponse { get; set; }
        public int agentErrorEvents { get; set; }
        public List<string> agentErrorMessages { get; set; }
        public string error { get; set; }
        public string stdoutLog { get; set; }
        public string stderrLog { get; set; }
        public List<ArtifactRecord> artifacts { get; set; }
        public List<string> missingArtifacts { get; set; }
        public List<ArtifactFailure> failedArtifacts { get; set; }

        // v0.6
        public string mode { get; set; }
        public string model { get; set; }
        public string effort { get; set; }
        public string modelReported { get; set; }
        public UsageRecord usage { get; set; }
        public bool autoExport { get; set; }
        public List<ExportSkip> exportSkipped { get; set; }

        // v1.0: the Run read untrusted content through device tools (TZ-CO-DESKTOP §9.4).
        public bool tainted { get; set; }

        // v1.5.1: why the corporate model was unavailable to this job (CO Web offers to resume the chat when it is back);
        // null otherwise. engieBlocked = the same value under the legacy wire name.
        public string corporateBlocked { get; set; }
        public string engieBlocked { get; set; }
    }

    internal sealed class ArtifactRecord
    {
        public string sourceRelativePath { get; set; }
        public string driveRelativePath { get; set; }
        public long sizeBytes { get; set; }
        public string sha256 { get; set; }

        // v1.6.0: md5 of the content (= Drive md5Checksum of the revision it becomes).
        public string md5 { get; set; }

        // v0.6: "declared" (artifactPaths) or "auto" (workspace change detection).
        public string origin { get; set; }

        // v1.6.0: true = driveRelativePath is the live workspace file itself (no copy); its versions are Drive revisions.
        public bool @ref { get; set; }
    }

    internal sealed class ArtifactFailure
    {
        public string path { get; set; }
        public string error { get; set; }
    }

    internal sealed class JobEvent
    {
        public int schemaVersion { get; set; }
        public int sequence { get; set; }
        public string createdUtc { get; set; }
        public string source { get; set; }
        public string type { get; set; }
        public string content { get; set; }
    }

    internal sealed class EventBatch
    {
        public int schemaVersion { get; set; }
        public int firstSequence { get; set; }
        public int lastSequence { get; set; }
        public List<JobEvent> events { get; set; }
    }

    internal sealed class CommitMarker
    {
        public int schemaVersion { get; set; }
        public string payload { get; set; }
        public long sizeBytes { get; set; }
        public string sha256 { get; set; }
    }

    internal sealed class AgentRunResult
    {
        public string startedUtc { get; set; }
        public int exitCode { get; set; }
        public bool timedOut { get; set; }
        public bool outputDrained { get; set; }
        public bool structuredFinalSeen { get; set; }
        public bool structuredSuccess { get; set; }
        public string sessionId { get; set; }
        public string finalResponse { get; set; }
        public int agentErrorEvents { get; set; }
        public List<string> agentErrorMessages { get; set; }
        public string error { get; set; }
        public UsageRecord usage { get; set; }
        public string modelReported { get; set; }
        public AgentLimits limits { get; set; }
    }

    internal sealed class AgentInspectionSnapshot
    {
        public bool finalSeen { get; set; }
        public bool success { get; set; }
        public string sessionId { get; set; }
        public string finalResponse { get; set; }
        public string failureMessage { get; set; }
        public int errorEventCount { get; set; }
        public List<string> errorMessages { get; set; }
        public UsageRecord usage { get; set; }
        public string modelReported { get; set; }
        public AgentLimits limits { get; set; }
    }

    internal sealed class LimitWindow
    {
        public string id { get; set; }            // "session", "week", or the provider key
        public string key { get; set; }           // provider window name
        public int windowMinutes { get; set; }
        public double usedPercent { get; set; }
        public string resetsAtUtc { get; set; }
        public long? corpTokens { get; set; }     // v1.1.6, corporate dispatcher savings windows only
        public long? engieTokens { get; set; }    // legacy wire name of corpTokens
        public long? claudeTokens { get; set; }
        public int? jobs { get; set; }

        public static LimitWindow Create(string key, double usedPercent, long resetsAtUnix, int windowMinutes)
        {
            LimitWindow w = new LimitWindow();
            w.key = key;
            w.windowMinutes = windowMinutes;
            w.id = windowMinutes > 0 && windowMinutes <= 1440 ? "session"
                : (key == "seven_day" || key == "secondary" || windowMinutes == 10080 ? "week" : key);
            w.usedPercent = Math.Round(Math.Max(0, Math.Min(100, usedPercent)), 1);
            w.resetsAtUtc = resetsAtUnix > 0
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(resetsAtUnix).ToString("o")
                : null;
            return w;
        }
    }

    internal sealed class AgentLimits
    {
        public string source { get; set; }
        public string observedUtc { get; set; }
        public string status { get; set; }
        public string plan { get; set; }
        public List<LimitWindow> windows { get; set; }
    }

    internal sealed class LimitsFile
    {
        public int schemaVersion { get; set; }
        public string updatedUtc { get; set; }
        public Dictionary<string, AgentLimits> agents { get; set; }
    }

    internal sealed class HostOwnerRecord
    {
        public int schemaVersion { get; set; }
        public string hostId { get; set; }
        public string machineName { get; set; }
        public int processId { get; set; }
        public string bridgeVersion { get; set; }
        public string[] capabilities { get; set; }
        public string updatedUtc { get; set; }

        // v0.6
        public int protocolMinor { get; set; }
        public Dictionary<string, OwnerAgentInfo> agents { get; set; }
        public Dictionary<string, OwnerProjectInfo> projects { get; set; }
        public Dictionary<string, ChatProjectPolicy> chatProjectEnvironments { get; set; }
        public Dictionary<string, OwnerContextRoot> contextRoots { get; set; }

        // v0.9
        public OwnerFastTransport fastTransport { get; set; }

        // v1.0
        public OwnerDeviceKey deviceCallKey { get; set; }
        public string runAsWarning { get; set; }

        // v1.4.1: paired devices for CO Web without CO Desktop, e.g. a phone
        public List<OwnerDeviceInfo> devices { get; set; }

        // v1.4.2: running | paused | updating (draining for updateTo); runningJobs = jobs still running
        public string state { get; set; }
        public string updateTo { get; set; }
        public int runningJobs { get; set; }
        // v1.14.0: device relay calls of site jobs running or being claimed (the drain exit waits for them as for jobs)
        public int relayActive { get; set; }
        public string stateSinceUtc { get; set; }
    }

    internal sealed class OwnerDeviceCorp
    {
        public string id { get; set; }
        public string state { get; set; }
    }

    // v1.4.1: paired device summary written to owner.json
    internal sealed class OwnerDeviceInfo
    {
        public string deviceId { get; set; }
        public string name { get; set; }
        public string shortName { get; set; }
        public string environmentId { get; set; }
        public string version { get; set; }
        public string lastHeartbeatUtc { get; set; }
        public bool online { get; set; }
        public OwnerDeviceCorp corp { get; set; } // Freedom F1 5b: corporate connector session {id, state}
        public string engie { get; set; }         // legacy wire of corp.state
        public string kind { get; set; }        // v1.5.0: desktop | web
        public string @short { get; set; }      // v1.5.0: same as shortName (web device spec field)
        public string userEmail { get; set; }   // v1.5.0: web devices only
    }

    internal sealed class FileStamp
    {
        public long length;
        public long lastWriteTicks;
    }

    internal sealed class RejectionRecord
    {
        public int schemaVersion { get; set; }
        public string hostId { get; set; }
        public string createdUtc { get; set; }
        public string reason { get; set; }
    }
}
