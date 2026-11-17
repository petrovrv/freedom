// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CO.Core;
using CO.Core.Auth;
using CO.Core.Host;
using CO.Core.Runtime;
using CO.Core.Tools;
using CO.Core.Transport;

namespace CO.Mac;

// co-resident: CO Desktop without a window for macOS. Commands:
//   setup --env <environment>           sign in with Google, write the pairing request, install and start the LaunchAgent
//   run                                   the LaunchAgent: wait for pairing, then serve agent tool calls
//   app                                   the CO window (R4): menu-bar icon + CO Web, from ~/Applications/CO.app (MacApp.cs)
//   status | folders [list|add <path> [id] [--read-only]|rm <id>] | shell on|off | browser on|read|off | browser forms ask|auto | browser otp on|off|default | window on|off | signin | uninstall [--purge] | version
//   subs [status|install <cli>|connect <claude|codex> [--email <e-mail>]|check <cli>|register]   own CLI subscriptions (S4)
public static class Program
{
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // Freedom F1: owner and members of this install from the install.json built in (CO.Core InstallDefaults)
    public static string OwnerEmail => InstallDefaults.OwnerEmail;
    public const string MemberScopes = "https://www.googleapis.com/auth/drive.file";
    public const string OwnerScopes = "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/pubsub";
    public const string GoogleTarget = "google";
    public const string ResultsFolderId = "co-results";
    public static readonly Dictionary<string, string> Members = InstallDefaults.Members;

    public static async Task<int> Main(string[] argv)
    {
        // CO.app opened from Finder, Spotlight or the Dock starts its executable without arguments: open the window.
        bool fromBundle = (Environment.ProcessPath ?? "").Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
        string cmd = argv.Length > 0 && !argv[0].StartsWith("-psn", StringComparison.Ordinal) ? argv[0] : fromBundle ? "open" : "status";
        try
        {
            if (!OperatingSystem.IsMacOS() && cmd is not ("version" or "--version"))
                throw new PlatformNotSupportedException("CO Resident runs on macOS only.");
            return cmd switch
            {
                "version" or "--version" => Print(Version),
                "setup" => await SetupAsync(Opt(argv, "--env") ?? throw new ArgumentException("setup needs --env <name>")),
                "run" => await RunAsync(),
                "app" => MacApp.Run(fromLaunchd: true, show: false),
                "open" => MacApp.Run(fromLaunchd: false, show: true),
                "window" => Window(argv.Length > 1 ? argv[1] : ""),
                "signin" => await SignInAgainAsync(),
                "status" => Status(),
                "folders" => Folders(argv.Skip(1).ToArray()),
                "shell" => Shell(argv.Length > 1 ? argv[1] : ""),
                "browser" => argv.Length > 1 && argv[1] == "forms" ? BrowserForms(argv.Length > 2 ? argv[2] : "")
                    : argv.Length > 1 && argv[1] == "otp" ? BrowserOtp(argv.Length > 2 ? argv[2] : "")
                    : Browser(argv.Length > 1 ? argv[1] : ""),
                "subs" => await SubsAsync(argv.Skip(1).ToArray()),
                "uninstall" => Uninstall(argv.Contains("--purge")),
                _ => throw new ArgumentException("Unknown command " + cmd + ". Commands: setup, run, status, folders, shell, browser, subs, window, signin, uninstall, version."),
            };
        }
        catch (Exception ex)
        {
            Log.Write($"{cmd} failed: {ex}");
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Print(string s) { Console.WriteLine(s); return 0; }
    private static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    // ------------------------------------------------------------------ composition

    private sealed class Parts
    {
        public required HttpClient Http { get; init; }
        public required KeychainStore Secrets { get; init; }
        public required InstalledAppAuth Auth { get; init; }
        public required DeviceRuntime Runtime { get; init; }
        public required ShellSettings Shell { get; init; }
        public required CO.Core.Subscriptions.SubscriptionManager Subs { get; init; }
        // Freedom F10 (SPEC §3.6, ext/1 device.tools): tools of the workbench modules that are on for this Mac
        public required CO.Core.Modules.ModuleLoader Modules { get; init; }
    }

    private static OAuthClient ReadClient()
    {
        string clientFile = Path.Combine(Paths.AppDir, "oauth-client.json");
        if (!File.Exists(clientFile)) throw new FileNotFoundException("oauth-client.json is missing next to co-resident (reinstall).", clientFile);
        return OAuthClient.FromJson(File.ReadAllText(clientFile));
    }

    private static string DeviceFile => Path.Combine(Paths.DataDir, "device.json");
    private static string StartedFile => Path.Combine(Paths.DataDir, "resident.started");

    // device.json has one writer at a time: setup (with the agent stopped) or the LaunchAgent. .NET takes an
    // advisory flock for FileShare.None on macOS, so a second writer waits here instead of racing on device.json.tmp.
    private static FileStream AcquireWriter(TimeSpan wait)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(Path.Combine(Paths.DataDir, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(1000); }
            catch (IOException) { throw new InvalidOperationException("Another CO Resident process is changing the device state; stop it first (launchctl bootout gui/$(id -u)/" + Paths.Label + ")."); }
        }
    }

    // writer = setup or run (holding the writer lock): may save device.json and register the default folders.
    // Other commands only read device.json (folder commands change folders.json and restart the agent).
    private static Parts Compose(bool writer)
    {
        Directory.CreateDirectory(Paths.DataDir);
        bool firstStart = !File.Exists(Path.Combine(Paths.DataDir, "folders.json"));
        bool newDevice = !File.Exists(DeviceFile);
        if (newDevice && !writer) throw new InvalidOperationException("CO Resident is not set up on this Mac yet: run the install command from CO.");
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var secrets = new KeychainStore();
        var auth = new InstalledAppAuth(http, ReadClient(), secrets, GoogleTarget);
        var runtime = new DeviceRuntime(Paths.DataDir, new MacKeyStore(secrets, allowCreate: newDevice), new DriveRest(http, auth), new MacApprovalService())
        {
            AutoUpdate = false,   // the Windows installer channel; Mac builds update through the install link for now
            // 0.3.3: gRPC (HTTP/2) aborted the process on a member's Mac (AccessViolationException in SslStream, 09.11).
            PubSubRest = true,
        };
        var shell = new ShellSettings(Path.Combine(Paths.DataDir, "shell.json"));
        foreach (var t in MacTools.All(shell, http, auth)) runtime.Catalog.Add(t);
        // R3: the agent browser (Chrome for Testing in its own profile); the Mac's user switches it with 'browser on|read|off'.
        // 0.5.3: the device role decides the default of one-time codes from chat (on for a household member's Mac).
        foreach (var t in MacBrowser.All(new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")), http, () => runtime.State.Role)) runtime.Catalog.Add(t);
        // S4: own CLI subscriptions (claude, codex): sign-in page in the agent browser, registration on the CO server.
        var subs = new CO.Core.Subscriptions.SubscriptionManager(http) { Log = Log.Write };
        // only while the agent browser is fully on ('co-resident browser on'); otherwise the person opens the link
        var browserMode = new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json"));
        subs.LoginTab = cli => browserMode.Mode != "on" ? null : (MacBrowser.Browser(http).Tab("subs-login-" + cli), async () =>
        {
            await MacBrowser.Browser(http).CloseTabAsync("subs-login-" + cli, CancellationToken.None);
            await MacBrowser.Browser(http).CloseTabAsync("subs-mail", CancellationToken.None);
        });
        subs.Mail = new CO.Core.Subscriptions.GmailWebMail(() => MacBrowser.Browser(http).Tab("subs-mail"));
        if (InstallDefaults.SiteUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            var registry = new CO.Core.Subscriptions.SubsRegistry(auth, () => (runtime.State.DeviceId, runtime.State.Short, runtime.State.Name), InstallDefaults.SiteUrl);
            subs.Report = (all, ct) => auth.HasRefreshToken && runtime.State.Account.Length > 0 ? registry.PutAsync(all, ct) : Task.FromResult("not signed in to CO");
        }
        runtime.Catalog.Add(new CO.Core.Subscriptions.CliSubsTool(subs));
        // F10: module tools from the list kept in modules.json, so the catalog has them before the site answers
        var modules = new CO.Core.Modules.ModuleLoader(runtime.Catalog, Paths.DataDir,
            InstallDefaults.SiteUrl.StartsWith("https://", StringComparison.Ordinal) ? new CO.Core.Modules.ModuleRegistryClient(auth, InstallDefaults.SiteUrl) : null,
            log: Log.Write);
        modules.Managed = () => runtime.State.Managed;
        modules.LoadCached();
        if (!writer) return new Parts { Http = http, Secrets = secrets, Auth = auth, Runtime = runtime, Shell = shell, Subs = subs, Modules = modules };

        runtime.State.OwnerEmail = OwnerEmail;
        if (runtime.State.Name == Environment.MachineName || runtime.State.Name.Length == 0)
            runtime.State.Name = Environment.MachineName + " (Mac)";
        runtime.Save();
        // ~/CO only on the very first start (no folders.json yet): a folder the user removed stays removed.
        if (firstStart)
        {
            Directory.CreateDirectory(Paths.WorkFolder);
            try { runtime.Folders.Add(Paths.WorkFolder, "co", writable: true, project: false, synced: false); }
            catch (Exception ex) { Log.Write("~/CO not registered: " + ex.Message); }
        }
        string results = Path.Combine(Paths.DataDir, "results");
        Directory.CreateDirectory(results);
        // Never fatal: a folder problem must not keep the resident (and the folder commands) from starting.
        if (runtime.Folders.Find(ResultsFolderId) == null)
        {
            try { runtime.Folders.Add(results, ResultsFolderId, writable: false, project: false, synced: false); }
            catch (Exception ex) { Log.Write("results folder not registered: " + ex.Message); }
        }
        return new Parts { Http = http, Secrets = secrets, Auth = auth, Runtime = runtime, Shell = shell, Subs = subs, Modules = modules };
    }

    // Read-only view of device.json (written atomically by the writer) and the pairing code it implies.
    private static (DeviceState? state, string? code) ReadPairing(ISecretStore secrets)
    {
        if (!File.Exists(DeviceFile)) return (null, null);
        var st = System.Text.Json.JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(DeviceFile));
        string? code = st?.Bridge?.BridgePublicKey is { Length: > 0 } bk
            ? CO.Core.Crypto.KeyUtil.PairingCode(new MacKeyStore(secrets, allowCreate: false).SigningKey().PublicKey, bk) : null;
        return (st, code);
    }

    private static void StopAgent()
    {
        string service = $"gui/{Uid()}/{Paths.Label}";
        Proc.Run("/bin/launchctl", new[] { "bootout", service });
        for (int i = 0; i < 20 && Proc.Run("/bin/launchctl", new[] { "print", service }).code == 0; i++) Thread.Sleep(500);
    }

    // ------------------------------------------------------------------ setup (from the install command in Terminal)

    private static async Task<int> SetupAsync(string env)
    {
        if (!Members.TryGetValue(env, out string? expected)) throw new ArgumentException("Unknown environment " + env);
        StopAgent();   // single writer of device.json while setup changes it
        var secrets = new KeychainStore();
        using (AcquireWriter(TimeSpan.FromSeconds(30)))
            await SetupStateAsync(env, expected);
        var started = DateTime.UtcNow;
        InstallLaunchAgent();
        VerifyStarted(started);
        Console.WriteLine("The resident runs in the background now and starts with every login.");
        Console.WriteLine("Agents may use the folder " + Paths.WorkFolder + "; every command on this Mac asks you first.");
        if (ReadPairing(secrets).state?.Paired == true) { Console.WriteLine("This Mac is already paired. Done."); return 0; }

        // The LaunchAgent polls Bridge and writes device.json; this window only reads it. The owner's computer pairs
        // this Mac by itself (the device folder in Drive belongs to this Google account); the code is the fallback.
        Console.WriteLine("Connecting to AI Workbench (usually 1-3 minutes, nothing to do)…");
        var until = DateTime.UtcNow.AddMinutes(6);
        string? lastCode = null;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            (DeviceState? state, string? code) seen;
            try { seen = ReadPairing(secrets); }
            catch (Exception ex) { Log.Write("setup: pairing check failed: " + ex.Message); continue; }
            if (seen.state?.Paired == true) { Console.WriteLine("Connected. Done."); return 0; }
            lastCode = seen.code ?? lastCode;
        }
        Console.WriteLine();
        Console.WriteLine("Not connected yet. It may still connect by itself (a notification appears); you can close this window.");
        if (lastCode != null)
        {
            Console.WriteLine($"If nothing happens within 15 minutes, send this code to {InstallDefaults.OwnerName}:");
            Console.WriteLine();
            Console.WriteLine("    Pairing code:  " + lastCode);
            Console.WriteLine();
        }
        return 0;
    }

    // The resident writes resident.started after it composed itself; it must still run a few seconds later. 0.2.1: a copy that
    // got as far as serving tool calls stays even if it then died (launchd restarts it): putting the previous version back
    // did not help on 08.11 (0.1.1 died the same way) and removed the copy that records why. Crash details go to the log.
    private static void VerifyStarted(DateTime since)
    {
        string service = $"gui/{Uid()}/{Paths.Label}";
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(1000);
            if (File.Exists(StartedFile) && File.GetLastWriteTimeUtc(StartedFile) >= since.AddSeconds(-1))
            {
                Thread.Sleep(3000);
                var (code, so, _) = Proc.Run("/bin/launchctl", new[] { "print", service });
                if (code == 0 && so.Contains("state = running")) return;
                if (ServedSince(since))
                {
                    Log.Write("setup: the resident served tool calls and then exited; keeping this version (launchd restarts it)");
                    ReportCrash(since);
                    return;
                }
                break;
            }
        }
        ReportCrash(since);
        throw new InvalidOperationException("The resident did not start (see " + Paths.LogFile + " and " + Path.Combine(Paths.LogDir, "resident.out") + ").");
    }

    // "serving tool calls" written by this version after 'since'.
    private static bool ServedSince(DateTime since)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return File.ReadLines(Paths.LogFile).Reverse().Take(200).Any(l => l.EndsWith(" serving tool calls") && l.Length > 28
                && DateTime.TryParse(l[..28], inv, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) && t >= since.AddSeconds(-1));
        }
        catch (Exception) { return false; }
    }

    // Waits for macOS's crash report of the process that just died and puts its summary into the log and the Terminal.
    private static void ReportCrash(DateTime since)
    {
        Console.WriteLine("Checking how the resident ended (up to 30 s)…");
        // ignoreSeen: the restarted resident may have logged this report already; the list it returns cannot be emptied
        // by a report deleted meanwhile (Summarize says so instead of throwing).
        var fresh = MacCrash.LogNew(since.AddSeconds(-5), waitSec: 30, ignoreSeen: true);
        if (fresh.Count == 0) { Log.Write("setup: no crash report"); return; }
        Console.WriteLine(MacCrash.Summarize(fresh[0], frames: 12));
    }

    private static async Task SetupStateAsync(string env, string expected)
    {
        var p = Compose(writer: true);
        var st = p.Runtime.State;
        bool owner = expected.Equals(OwnerEmail, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"CO Resident {Version} — setup for {env} ({expected})");
        // One device = one account: the pairing, folders and Bridge key belong to it. Switching needs a clean device.
        if (st.Account.Length > 0 && !st.Account.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"This Mac is already set up for {st.Account} ({st.EnvironmentId}). To switch to {expected}: " +
                $"\"{Paths.Binary}\" uninstall --purge, then run the install command again.");
        if (!p.Auth.HasRefreshToken || !st.Account.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("A Google sign-in page opens in your browser. Sign in as " + expected + " and allow access.");
            string? email = await p.Auth.SignInAsync(owner ? OwnerScopes : MemberScopes, Dialog.Open, CancellationToken.None, expected);
            if (!string.Equals(email, expected, StringComparison.OrdinalIgnoreCase))
            {
                p.Auth.SignOut();
                throw new InvalidOperationException($"Signed in as {email}, but {env} uses {expected}. Run the command again and choose {expected}.");
            }
            st.Account = email!;
            st.Role = owner ? "owner" : "member";
            st.EnvironmentId = env;
            p.Runtime.Save();
            Console.WriteLine("Signed in as " + email + ".");
        }
        if (!st.Paired && (st.Folder == null || st.PairingRequestFileId.Length == 0))
        {
            Console.WriteLine("Connecting this Mac to AI Workbench…");
            await p.Runtime.CreatePairingRequestAsync(st.Account, st.EnvironmentId, CancellationToken.None);
        }
    }

    private static void InstallLaunchAgent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.LaunchAgent)!);
        string esc(string s) => System.Security.SecurityElement.Escape(s)!;
        string plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Paths.Label}</string>
              <key>ProgramArguments</key><array><string>{esc(Paths.Binary)}</string><string>run</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ThrottleInterval</key><integer>30</integer>
              <key>ProcessType</key><string>Interactive</string>
              <key>StandardOutPath</key><string>{esc(Path.Combine(Paths.LogDir, "resident.out"))}</string>
              <key>StandardErrorPath</key><string>{esc(Path.Combine(Paths.LogDir, "resident.out"))}</string>
            </dict>
            </plist>
            """;
        Directory.CreateDirectory(Paths.LogDir);
        File.WriteAllText(Paths.LaunchAgent, plist);
        string domain = "gui/" + Uid();
        Proc.Run("/bin/launchctl", new[] { "bootout", domain + "/" + Paths.Label });
        var (code, _, se) = Proc.Run("/bin/launchctl", new[] { "bootstrap", domain, Paths.LaunchAgent });
        if (code != 0) throw new InvalidOperationException("launchctl bootstrap failed: " + se.Trim());
        Log.Write("LaunchAgent installed: " + Paths.LaunchAgent);
    }

    internal static string Uid() => Proc.Run("/usr/bin/id", new[] { "-u" }).stdout.Trim();

    // Folder changes take effect only after the running resident restarts: a failed restart must not look like success.
    private static void Restart()
    {
        var (code, _, se) = Proc.Run("/bin/launchctl", new[] { "kickstart", "-k", $"gui/{Uid()}/{Paths.Label}" });
        if (code != 0)
            throw new InvalidOperationException($"Saved, but the resident did not restart (launchctl {code}: {se.Trim()}); " +
                "the change applies after the next login or 'launchctl kickstart -k gui/$(id -u)/" + Paths.Label + "'.");
    }

    // ------------------------------------------------------------------ run (LaunchAgent)

    private static async Task<int> RunAsync()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (!e.Exception.ToString().Contains("SubscriberClientImpl")) Log.Write("Unobserved: " + e.Exception);
            e.SetObserved();
        };
        var stop = new CancellationTokenSource();
        // 07.11 the first Mac restarted every ~30 s for 25 minutes with nothing in this log: every way out is logged now,
        // and each start records how launchd saw the previous process end (exit code or signal).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("crash: " + e.ExceptionObject);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGQUIT }
            .Select(s => PosixSignalRegistration.Create(s, ctx => { Log.Write($"signal {ctx.Signal}: stopping"); ctx.Cancel = true; stop.Cancel(); }))
            .ToList();
        bool quickRestarts = LogPreviousRun();
        // Crash reports of earlier runs (also those of the version before an update) that are not in the log yet.
        try { MacCrash.LogNew(DateTime.UtcNow.AddDays(-7)); } catch (Exception ex) { Log.Write("crash reports not read: " + ex.Message); }
        // Held for the life of the process: setup and a second copy wait instead of writing device.json too.
        using var writer = AcquireWriter(TimeSpan.FromMinutes(5));
        if (!MacUpdater.OnStart(Version)) return 1;   // a failed update put the previous copy back; launchd starts it
        var p = Compose(writer: true);
        if (quickRestarts)
        {
            // Several starts in a row ended within two minutes: run on the Drive mailbox only for this process, so that
            // the Mac stays reachable and the log shows whether the Pub/Sub (gRPC) start is what kills it.
            p.Runtime.UsePubSub = false;
            Log.Write("safe start: crashed 3 times within 2 minutes; Pub/Sub off for 30 minutes, Drive mailbox only");
        }
        // Safe start lasts 30 quiet minutes, then a planned restart brings Pub/Sub back (that restart does not count).
        DateTime? safeUntil = quickRestarts ? DateTime.UtcNow.AddMinutes(30) : null;
        MacCheckTool.AfterRestart();
        File.WriteAllText(StartedFile, $"{Environment.ProcessId} {Version} {DateTime.UtcNow:O}" + "\n");
        // R4: ~/Applications/CO.app and its menu at login, refreshed when this version is new; never fatal for the device.
        _ = Task.Run(() =>
        {
            try { MacAppInstall.Ensure(Version, Paths.Binary); }
            catch (Exception ex) { Log.Write("window app not installed: " + ex.Message); }
        });
        var rt = p.Runtime;
        rt.StatusChanged += s => Log.Write("status: " + s);
        bool reauthAsked = false;
        p.Auth.ReauthRequired += kind => reauthAsked = true;
        Log.Write($"CO Resident {Version} started (core {DeviceRuntime.Version}), pid {Environment.ProcessId}, device {rt.State.DeviceId}, account {rt.State.Account}");
        var updater = new MacUpdater(new DriveRest(p.Http, p.Auth), p.Auth, p.Http);
        string? staged = null, updateStatus = null;
        var nextUpdateCheck = DateTime.UtcNow.AddMinutes(2);
        string? codeShown = null;
        DateTime? codeSeenUtc = null;
        bool modulesLoop = false;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!p.Auth.HasRefreshToken || rt.State.Account.Length == 0)
                {
                    if (rt.State.Account.Length == 0) { Log.Write("not set up; waiting (run the install command again)"); await Task.Delay(TimeSpan.FromMinutes(10), stop.Token); continue; }
                    await AskSignInAsync(p, stop.Token);
                    continue;
                }
                if (rt.State.Bridge?.State == "revoked")
                {
                    Log.Write("device revoked by the owner; idle");
                    await Task.Delay(TimeSpan.FromHours(1), stop.Token);
                    continue;
                }
                if (!rt.State.Paired)
                {
                    if (rt.State.Folder == null || rt.State.PairingRequestFileId.Length == 0)
                        await rt.CreatePairingRequestAsync(rt.State.Account, rt.State.EnvironmentId, stop.Token);
                    await rt.RefreshBridgeInfoAsync(stop.Token);
                    // The owner's computer pairs this Mac by itself; the code is shown only if that has not happened in 15 minutes.
                    if (rt.PairingCode() is string code && code != codeShown && !rt.State.Paired)
                    {
                        if (codeSeenUtc == null) { codeSeenUtc = DateTime.UtcNow; Log.Write("pairing request seen by Bridge; waiting for the owner's computer"); }
                        else if (DateTime.UtcNow - codeSeenUtc > TimeSpan.FromMinutes(15))
                        {
                            codeShown = code;
                            Log.Write("not paired after 15 minutes; showing the pairing code");
                            _ = Task.Run(() => Dialog.Ask($"This Mac is not connected to AI Workbench yet.\n\nSend this code to {InstallDefaults.OwnerName}:\n\n{code}",
                                "CO Resident", new[] { "OK" }, "OK", 600, caution: false));
                        }
                    }
                    if (!rt.State.Paired) { await Task.Delay(TimeSpan.FromSeconds(30), stop.Token); continue; }
                    Dialog.Notify("This Mac is connected to AI Workbench.");
                }
                var sink = new LocalResultSink(rt.Folders, ResultsFolderId);
                await rt.StartAsync(sink, stop.Token);
                rt.Host!.Completed += (call, r) => Log.Write($"{(r.Ok ? "ok " : "ERR")} {call.Tool} run={call.RunId} {(r.Ok ? "" : r.Error?.Code)}");
                Log.Write("serving tool calls");
                MacUpdater.Confirm(Version);
                // F10: module list every 5 minutes (switches the CO window changed come in this way too); a changed tool
                // set is published to Bridge. Once per process.
                if (!modulesLoop)
                {
                    modulesLoop = true;
                    p.Modules.CatalogChanged += () => _ = Task.Run(async () =>
                    {
                        try { await rt.PublishCatalogAsync(stop.Token); }
                        catch (Exception ex) when (!stop.IsCancellationRequested) { Log.Write("module tools: catalog not published: " + ex.Message); }
                    });
                    _ = Task.Run(async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            try { await p.Modules.RefreshAsync(stop.Token); }
                            catch (Exception ex) when (!stop.IsCancellationRequested) { Log.Write("modules: " + ex.Message); }
                            try { await Task.Delay(TimeSpan.FromMinutes(5), stop.Token); } catch (OperationCanceledException) { return; }
                        }
                    });
                }
                // S4: subscriptions registered on the server at start and every 6 h
                _ = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try { await p.Subs.RefreshAndReportAsync(stop.Token); }
                        catch (Exception ex) when (!stop.IsCancellationRequested) { Log.Write("subscriptions: " + ex.Message); }
                        try { await Task.Delay(TimeSpan.FromHours(6), stop.Token); } catch (OperationCanceledException) { return; }
                    }
                });
                // Until revoked, a lost Google sign-in, an update, or exit; the runtime's channels reconnect by themselves.
                while (!stop.IsCancellationRequested && rt.State.Bridge?.State != "revoked" && !reauthAsked)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
                    if (staged == null && DateTime.UtcNow >= nextUpdateCheck)
                    {
                        nextUpdateCheck = DateTime.UtcNow.AddMinutes(30);
                        try { staged = await updater.CheckAsync(rt.State, Version, stop.Token); }
                        catch (Exception ex) when (!stop.IsCancellationRequested) { Log.Write("update check failed: " + ex.Message); }
                        if (updater.Status != updateStatus) Log.Write("update: " + (updateStatus = updater.Status));
                    }
                    // 0.5.2: not while a secret dialog waits; review 13.11: not while a Mac check runs (launchd would stop it with us)
                    bool idle = rt.Host!.ActiveCalls == 0 && !SecretPrompts.DialogOpen && !MacCheckTool.Busy;
                    if (safeUntil != null && DateTime.UtcNow >= safeUntil && idle)
                    {
                        await rt.DisposeAsync();
                        Log.Write("safe start: 30 minutes without a crash; restarting with Pub/Sub");
                        return 0;   // launchd (KeepAlive) starts a fresh process; exit 0 is not counted as a crash
                    }
                    if (staged != null && idle)
                    {
                        await rt.DisposeAsync();
                        // a secret_prompt call may have opened its dialog just before the runtime stopped: let the person finish
                        for (int i = 0; i < 150 && SecretPrompts.DialogOpen; i++) await Task.Delay(TimeSpan.FromSeconds(5));
                        MacUpdater.Swap(Version, staged);
                        Log.Write($"updated {Version} -> {staged}; restarting");
                        return 0;   // launchd (KeepAlive) starts the new copy
                    }
                }
                if (reauthAsked && !stop.IsCancellationRequested)
                {
                    await rt.DisposeAsync();
                    await AskSignInAsync(p, stop.Token);
                    reauthAsked = false;
                    Log.Write("restarting after sign-in");
                    return 0;   // launchd starts a fresh process
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Write("loop: " + ex);
                if (rt.Host != null) return 1;   // started once: launchd restarts a clean process (ThrottleInterval 30 s)
                try { await Task.Delay(TimeSpan.FromMinutes(1), stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        await rt.DisposeAsync();
        Log.Write("stopped");
        GC.KeepAlive(signals);
        return 0;
    }

    // How the previous process ended, as launchd saw it ("last exit code", "last terminating signal"), and when it started.
    // Returns true when this is the 3rd start in a row less than two minutes after a previous process that crashed. Review
    // 13.11: planned restarts (setup, sign-in, update swap, 'folders add') end with exit 0 and no signal and do not count.
    private static bool LogPreviousRun()
    {
        bool quick = false;
        try
        {
            string prev = File.Exists(StartedFile) ? File.ReadAllText(StartedFile).Trim() : "-";
            var (code, so, _) = Proc.Run("/bin/launchctl", new[] { "print", $"gui/{Uid()}/{Paths.Label}" }, timeoutMs: 10_000);
            var lines = code != 0 ? new List<string>() : so.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("last ") || l.StartsWith("runs =")).ToList();
            string seen = lines.Count > 0 ? string.Join("; ", lines) : "-";
            bool crashed = MacRestartRules.Crashed(lines);
            Log.Write($"previous run: {prev}; launchd: {seen}{(crashed ? " (crashed)" : "")}");
            string streakFile = Path.Combine(Paths.DataDir, "quick-starts.txt");
            int streak = 0;
            if (crashed && File.Exists(StartedFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(StartedFile) < TimeSpan.FromMinutes(2))
                streak = (File.Exists(streakFile) && int.TryParse(File.ReadAllText(streakFile).Trim(), out int n) ? n : 0) + 1;
            File.WriteAllText(streakFile, streak.ToString(System.Globalization.CultureInfo.InvariantCulture));
            quick = streak >= 3;
        }
        catch (Exception ex) { Log.Write("previous run unknown: " + ex.Message); }
        return quick;
    }

    private static async Task AskSignInAsync(Parts p, CancellationToken ct)
    {
        Log.Write("Google sign-in needed");
        string? b = await Task.Run(() => Dialog.Ask(
            $"CO Resident: the Google sign-in for {p.Runtime.State.Account} expired.\n\nSign in again in the browser to keep this Mac connected to AI Workbench.",
            "CO Resident", new[] { "Later", "Sign in" }, "Sign in", 3600, caution: false), ct);
        if (b != "Sign in") { await Task.Delay(TimeSpan.FromHours(2), ct); return; }
        bool owner = p.Runtime.State.Role == "owner";
        string? email = await p.Auth.SignInAsync(owner ? OwnerScopes : MemberScopes, Dialog.Open, ct, p.Runtime.State.Account);
        if (!string.Equals(email, p.Runtime.State.Account, StringComparison.OrdinalIgnoreCase))
        {
            p.Auth.SignOut();
            Dialog.Notify($"Signed in as {email}; this Mac belongs to {p.Runtime.State.Account}.");
        }
        else Log.Write("Google sign-in renewed");
    }

    private static async Task<int> SignInAgainAsync()
    {
        var p = Compose(writer: false);
        if (p.Runtime.State.Account.Length == 0) throw new InvalidOperationException("Not set up yet: run the install command from CO.");
        bool owner = p.Runtime.State.Role == "owner";
        // The stored token is replaced only when the same account signed in (another one would break Drive access).
        var (_, refresh, email) = await InstalledAppAuth.AuthorizeAsync(p.Http, ReadClient(), owner ? OwnerScopes : MemberScopes,
            true, Dialog.Open, CancellationToken.None, p.Runtime.State.Account);
        if (!string.Equals(email, p.Runtime.State.Account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Signed in as {email}, but this Mac belongs to {p.Runtime.State.Account}. Nothing changed.");
        if (refresh == null) throw new InvalidOperationException("Google did not return a refresh token. Nothing changed.");
        p.Secrets.Write(GoogleTarget, refresh);
        Console.WriteLine("Signed in as " + email);
        Restart();
        return 0;
    }

    // ------------------------------------------------------------------ subscriptions (S4)

    private static async Task<int> SubsAsync(string[] a)
    {
        var m = Compose(writer: false).Subs;
        string cmd = a.Length > 0 ? a[0] : "status";
        string Cli() => a.Length > 1 ? a[1] : throw new ArgumentException("subs " + cmd + " <claude|codex|agy>");
        switch (cmd)
        {
            case "status":
                await m.StatusAllAsync(CancellationToken.None);
                Console.WriteLine(m.Describe());
                return 0;
            case "install":
                Console.WriteLine(await m.InstallAsync(Cli(), CancellationToken.None));
                return 0;
            case "check":
                Console.WriteLine(await m.VerifyAsync(Cli(), CancellationToken.None));
                return 0;
            case "register":
                Console.WriteLine(await m.RefreshAndReportAsync(CancellationToken.None));
                return 0;
            case "connect":
            {
                string id = Cli();
                if (!(await m.StatusAsync(id, CancellationToken.None)).Installed) Console.WriteLine(await m.InstallAsync(id, CancellationToken.None));
                var s = await m.StartLoginAsync(id, CancellationToken.None, email: Opt(a, "--email"));
                Console.WriteLine(s.Describe());
                string last = "";
                while (!s.Ended)
                {
                    await Task.Delay(1000);
                    if (s.Message != last) Console.WriteLine("  " + (last = s.Message));
                }
                Console.WriteLine(s.Describe());
                if (s.State == "done") Console.WriteLine(m.LastReport.Length > 0 ? m.LastReport : await m.RefreshAndReportAsync(CancellationToken.None));
                return s.State == "done" ? 0 : 1;
            }
            default:
                throw new ArgumentException("subs status | install <cli> | connect <cli> [--email E] | check <cli> | register");
        }
    }

    // ------------------------------------------------------------------ small commands

    private static int Status()
    {
        var p = Compose(writer: false);
        var st = p.Runtime.State;
        Console.WriteLine($"CO Resident {Version} (core {DeviceRuntime.Version})");
        Console.WriteLine($"device:   {st.DeviceId}  tools: dev_{st.Short}");
        Console.WriteLine($"account:  {(st.Account.Length > 0 ? st.Account : "-")} ({st.EnvironmentId}), signed in: {p.Auth.HasRefreshToken}");
        Console.WriteLine($"paired:   {st.Paired}{(st.Bridge?.State is { Length: > 0 } s ? " (" + s + ")" : "")}");
        if (!st.Paired && p.Runtime.PairingCode() is string code) Console.WriteLine($"code:     {code}");
        Console.WriteLine($"shell:    {(p.Shell.Enabled ? "on (asks before every command)" : "off")}");
        Console.WriteLine($"browser:  {new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")).Mode} ({MacBrowser.Describe()})");
        Console.WriteLine($"window:   {MacAppInstall.Describe()} ({MacAppInstall.Bundle})");
        Console.WriteLine("folders:");
        foreach (var f in p.Runtime.Folders.All) Console.WriteLine($"  {f.Id,-12} {f.Path}  [{f.Granted}]");
        var (code2, so, _) = Proc.Run("/bin/launchctl", new[] { "print", $"gui/{Uid()}/{Paths.Label}" });
        Console.WriteLine($"agent:    {(code2 == 0 ? (so.Contains("state = running") ? "running" : "loaded") : "not loaded")}");
        Console.WriteLine($"log:      {Paths.LogFile}");
        return 0;
    }

    private static int Folders(string[] a)
    {
        var p = Compose(writer: false);
        string sub = a.Length > 0 ? a[0] : "list";
        switch (sub)
        {
            case "list":
                foreach (var f in p.Runtime.Folders.All) Console.WriteLine($"{f.Id,-12} {f.Path}  [{f.Granted}]");
                return 0;
            case "add":
                if (a.Length < 2) throw new ArgumentException("folders add <path> [id] [--read-only]");
                string path = CO.Core.Policy.PathUtil.Normalize(a[1] == "~" ? Paths.Home : a[1].StartsWith("~/") ? Path.Combine(Paths.Home, a[1][2..]) : a[1]);
                // The resident's own data (keys are in the Keychain, but grants, audit and results are here) stays out of reach.
                if (CO.Core.Policy.PathUtil.IsUnder(Paths.DataDir, path) || CO.Core.Policy.PathUtil.IsUnder(path, Paths.DataDir))
                    throw new ArgumentException($"{path} contains or is inside CO's own data ({Paths.DataDir}); choose another folder, e.g. ~/Documents.");
                var rights = a.Contains("--read-only") ? CO.Core.Policy.Rights.Read
                    : CO.Core.Policy.Rights.Read | CO.Core.Policy.Rights.Create | CO.Core.Policy.Rights.Edit;
                string id = p.Runtime.Folders.MakeId(a.Length > 2 && !a[2].StartsWith("--") ? a[2] : null, path);
                var added = p.Runtime.Folders.Add(path, id, rights, project: false);
                if (!CO.Core.Policy.PathUtil.Normalize(added.Path).Equals(path, StringComparison.Ordinal))
                {
                    // Inside a connected folder the path becomes a rule of that folder: give it the requested rights.
                    string rule = Path.GetRelativePath(added.Path, path).Replace('\\', '/');
                    p.Runtime.Folders.SetRights(added.Id, rule, rights);
                    Console.WriteLine($"added {added.Id}/{rule} [{rights}] (inside {added.Path})");
                }
                else Console.WriteLine($"added {added.Id} = {added.Path} [{rights}]");
                break;
            case "rm":
                if (a.Length < 2) throw new ArgumentException("folders rm <id>");
                p.Runtime.Folders.Remove(a[1]);
                Console.WriteLine("removed " + a[1]);
                break;
            default: throw new ArgumentException("folders list | add <path> [id] [--read-only] | rm <id>");
        }
        Restart();
        return 0;
    }

    private static int Shell(string mode)
    {
        if (mode is not ("on" or "off")) throw new ArgumentException("shell on | off");
        var s = new ShellSettings(Path.Combine(Paths.DataDir, "shell.json")) { Enabled = mode == "on" };
        Console.WriteLine("shell_run is " + (s.Enabled ? "on (every command asks you first)" : "off"));
        return 0;
    }

    private static int Browser(string mode)
    {
        if (mode is not ("on" or "read" or "off")) throw new ArgumentException("browser on | read | off");
        new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")).Mode = mode;
        Console.WriteLine("agent browser is " + (mode switch
        {
            "on" => "on (agents open, read and use pages in their own browser window; forms and purchases ask you first)",
            "read" => "read only (agents open and read pages, no clicks or typing)",
            _ => "off",
        }));
        return 0;
    }

    // 0.5.1: form submits, choices and uploads of the agent browser ask first (ask, default) or run without asking (auto).
    private static int BrowserForms(string mode)
    {
        if (mode is not ("ask" or "auto")) throw new ArgumentException("browser forms ask | auto");
        MacBrowser.ConfirmForms = mode == "ask";
        Console.WriteLine(mode == "ask"
            ? "Agent browser: form submits, choices and uploads ask you first."
            : "Agent browser: form submits, choices and uploads run without asking; purchases and deletions still ask, sending stays refused.");
        return 0;
    }

    // 0.5.3: one-time codes the user sends in chat: the agent may type them (on), never (off), or the device default
    // (default: on for a household member's Mac, off for the owner's). Passwords, card and bank fields are never typed.
    private static int BrowserOtp(string mode)
    {
        if (mode is not ("on" or "off" or "default")) throw new ArgumentException("browser otp on | off | default");
        MacBrowser.OtpSetting = mode switch { "on" => true, "off" => false, _ => null };
        string role = "";
        try
        {
            if (File.Exists(DeviceFile)) role = System.Text.Json.JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(DeviceFile))?.Role ?? "";
        }
        catch (Exception) { }
        bool on = MacBrowser.AllowOtpFromChat(role);
        Console.WriteLine((on
            ? "Agent browser: the agent may type a one-time code you send in chat into the code field of its tab; passwords, card and bank fields never."
            : "Agent browser: one-time code pages pause the agent tab; you type the code yourself.")
            + (mode == "default" ? " (device default)" : ""));
        return 0;
    }

    private static int Window(string mode)
    {
        if (mode is not ("on" or "off")) throw new ArgumentException("window on | off");
        MacAppInstall.Enabled = mode == "on";
        if (mode == "on") MacAppInstall.Ensure(Version, Path.Combine(Paths.Home, "Applications", "CO Resident", "co-resident"));   // the resident, also when called from CO.app
        else MacAppInstall.Stop();
        Console.WriteLine(mode == "on" ? "The CO menu and window are on (CO in the menu bar, ~/Applications/CO.app)." : "The CO menu is off and does not start at login.");
        return 0;
    }

    private static int Uninstall(bool purge)
    {
        string service = $"gui/{Uid()}/{Paths.Label}";
        Proc.Run("/bin/launchctl", new[] { "bootout", service });
        // Nothing is deleted while the resident still runs (it would keep its access and write into removed state).
        bool loaded = true;
        for (int i = 0; i < 10 && loaded; i++)
        {
            loaded = Proc.Run("/bin/launchctl", new[] { "print", service }).code == 0;
            if (loaded) Thread.Sleep(500);
        }
        if (loaded) throw new InvalidOperationException("The resident did not stop (launchctl bootout). Nothing was removed; try again or log out first.");
        if (File.Exists(Paths.LaunchAgent)) File.Delete(Paths.LaunchAgent);
        try { MacAppInstall.Remove(); } catch (Exception ex) { Console.Error.WriteLine("CO.app not removed: " + ex.Message); }
        if (purge)
        {
            var store = new KeychainStore();
            store.Delete(GoogleTarget);
            MacKeyStore.DeleteKeys(store);
            if (Directory.Exists(Paths.DataDir)) Directory.Delete(Paths.DataDir, true);
        }
        Console.WriteLine("CO Resident stopped and removed from login items" + (purge ? "; sign-in, keys and settings deleted" : "") + ".");
        Console.WriteLine("Delete the app folder too:  rm -rf \"" + Paths.AppDir + "\"   (your files in " + Paths.WorkFolder + " stay).");
        return 0;
    }
}
