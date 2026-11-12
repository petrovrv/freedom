// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a (SPEC §4.2): platform adapters of the .NET 8 Bridge.
//
// The Bridge sources are shared with the net48 release build (build.cmd), which keeps its Windows code unchanged.
// In the .NET 8 build the Windows-only spots ask BridgePlatform.Current when !OperatingSystem.IsWindows()
// (blocks "#if NET8_0_OR_GREATER" in the shared sources); on Windows the original code runs, and the Windows
// adapters below wrap that same code for callers that want one interface on every platform.
//
//   adapter        Windows                          macOS                                   Linux
//   Secrets        Credential Manager (generic)     login Keychain (/usr/bin/security)      libsecret (secret-tool)
//   Autostart      logon task AIWB-BridgeAutostart  LaunchAgent ~/Library/LaunchAgents      systemd user unit
//   Paths          %LOCALAPPDATA%, %ProgramData%    ~/Library/Application Support, Logs     $XDG_DATA_HOME or ~/.local/share
//   Shell          explorer.exe, default browser    /usr/bin/open                           xdg-open
//   Executables    git.exe, python                  git, python3                            git, python3
//   ProcessTree    Job Object (kill on close)       Process.Kill(entireProcessTree)         same as macOS
//   Tray           WinForms NotifyIcon              headless (HeadlessUi.cs, status file)   headless
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AiWorkbenchBridge
{
    internal interface ISecretStore
    {
        string Name { get; }
        // null when the target holds no secret; throws when the store itself fails (locked, missing tool).
        string Read(string target);
        void Write(string target, string secret, string comment);
        void Delete(string target);
    }

    internal interface IAutostart
    {
        string Name { get; }
        // Where the registration lives (task name, plist path, unit path).
        string Location { get; }
        bool IsInstalled();
        // Registers the Bridge to start at logon with these arguments and restart after a crash.
        string Install(string executable, string[] arguments);
        string Uninstall();
        // Starts the registered Bridge now (the supervisor's "launcher missing" path). Returns a short outcome text;
        // "task ..." means started (same convention as the Windows text).
        string Kick();
    }

    internal interface IPlatformPaths
    {
        string Home { get; }
        // Per-user state root of the Bridge (config.json, logs, jobs).
        string StateRoot { get; }
        // Files a runAs CLI user must read (Windows %ProgramData%); per-user elsewhere (runAs is Windows-only).
        string SharedRoot { get; }
        string LogRoot { get; }
        // Windows-style config paths (%LOCALAPPDATA%\x, %USERPROFILE%\x, ~/x) as paths of this platform.
        string Normalize(string path);
        // Creates the folder (and parents) readable only by this user: mode 0700 on Unix; Windows keeps the
        // inherited profile ACL (%LOCALAPPDATA% is already per-user).
        void EnsurePrivateDirectory(string path);
    }

    internal interface IDesktopShell
    {
        void OpenUrl(string url);
        void OpenFolder(string path);
    }

    internal interface IExecutables
    {
        string Git { get; }
        string Python { get; }
        // Shell command that prints a file (Claude Code hook "command").
        string CatFileCommand(string path);
    }

    internal interface IBridgePlatform
    {
        string Id { get; }               // windows | macos | linux
        bool HasTray { get; }            // WinForms tray; false = headless UI
        ISecretStore Secrets { get; }
        IAutostart Autostart { get; }
        IPlatformPaths Paths { get; }
        IDesktopShell Shell { get; }
        IExecutables Executables { get; }
        // Features that are not available on this platform (shown by --platform-info, listed in the F3a report).
        string[] NotSupported { get; }
    }

    internal static class BridgePlatform
    {
        private static IBridgePlatform _current;
        private static readonly object Sync = new object();

        public static IBridgePlatform Current
        {
            get
            {
                if (_current != null)
                    return _current;
                lock (Sync)
                {
                    if (_current == null)
                        _current = Detect();
                    return _current;
                }
            }
        }

        // Tests replace the platform (for example the macOS adapters with a fake command runner on Windows).
        internal static IBridgePlatform Override
        {
            set { lock (Sync) { _current = value; } }
        }

        public static IBridgePlatform Detect()
        {
            if (OperatingSystem.IsWindows())
                return new WindowsPlatform();
            if (OperatingSystem.IsMacOS())
                return new MacPlatform(CommandRunner.Default, UnixPaths.HomeDirectory());
            return new LinuxPlatform(CommandRunner.Default, UnixPaths.HomeDirectory());
        }

        // --platform-info: one JSON object for the installer and the tests.
        public static string Describe(IBridgePlatform p)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["platform"] = p.Id;
            bool tray = p.HasTray;
#if BRIDGE_HEADLESS
            tray = false;   // the net8.0 build has no WinForms, also on Windows
#endif
            d["tray"] = tray ? "winforms" : "headless";
            d["secrets"] = p.Secrets.Name;
            d["autostart"] = p.Autostart.Name;
            d["autostartLocation"] = p.Autostart.Location;
            d["stateRoot"] = p.Paths.StateRoot;
            d["logRoot"] = p.Paths.LogRoot;
            d["git"] = p.Executables.Git;
            d["python"] = p.Executables.Python;
            d["notSupported"] = p.NotSupported;
            d["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " " +
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            return JsonUtil.Serialize(d);
        }
    }

    // Runs a program without a shell; stdin text optional (secrets go through stdin, never argv).
    internal class CommandRunner
    {
        public static readonly CommandRunner Default = new CommandRunner();

        public sealed class Result
        {
            public int Code;
            public string Stdout = "";
            public string Stderr = "";
        }

        public virtual Result Run(string file, IList<string> args, string stdin, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            Result r = new Result();
            using (Process p = Process.Start(psi))
            {
                if (p == null)
                    throw new InvalidOperationException("Could not start " + file);
                if (stdin != null)
                    p.StandardInput.Write(stdin);
                p.StandardInput.Close();
                System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch (InvalidOperationException) { }
                    r.Code = -1;
                    r.Stderr = "timeout";
                    return r;
                }
                r.Code = p.ExitCode;
                r.Stdout = o.Result;
                r.Stderr = e.Result;
            }
            return r;
        }
    }

    // ==================================================================== Windows

    internal sealed class WindowsPlatform : IBridgePlatform
    {
        private readonly ISecretStore _secrets = new WindowsCredentialStore();
        private readonly IAutostart _autostart = new WindowsLogonTask();
        private readonly IPlatformPaths _paths = new WindowsPaths();
        private readonly IDesktopShell _shell = new WindowsShell();
        private readonly IExecutables _exes = new WindowsExecutables();

        public string Id { get { return "windows"; } }
        public bool HasTray { get { return true; } }
        public ISecretStore Secrets { get { return _secrets; } }
        public IAutostart Autostart { get { return _autostart; } }
        public IPlatformPaths Paths { get { return _paths; } }
        public IDesktopShell Shell { get { return _shell; } }
        public IExecutables Executables { get { return _exes; } }
        public string[] NotSupported { get { return new string[0]; } }
    }

    // The Credential Manager code of the Bridge (GoogleTransport.cs CredentialStore), generic credentials.
    internal sealed class WindowsCredentialStore : ISecretStore
    {
        public string Name { get { return "Windows Credential Manager"; } }
        public string Read(string target) { return CredentialStore.Read(target); }
        public void Write(string target, string secret, string comment) { CredentialStore.Write(target, secret); }
        public void Delete(string target) { CredentialStore.Delete(target); }
    }

    // install.ps1 registers the logon task (protected path); the Bridge only starts it.
    internal sealed class WindowsLogonTask : IAutostart
    {
        public const string TaskName = "AIWB-BridgeAutostart";

        public string Name { get { return "Windows scheduled task"; } }
        public string Location { get { return TaskName; } }

        public bool IsInstalled()
        {
            CommandRunner.Result r = CommandRunner.Default.Run(SchTasks, new string[] { "/Query", "/TN", TaskName }, null, 30000);
            return r.Code == 0;
        }

        public string Install(string executable, string[] arguments)
        {
            throw new NotSupportedException("On Windows bridge\\install.ps1 registers the logon task " + TaskName + ".");
        }

        public string Uninstall()
        {
            throw new NotSupportedException("On Windows bridge\\install.ps1 manages the logon task " + TaskName + ".");
        }

        public string Kick()
        {
            CommandRunner.Result r = CommandRunner.Default.Run(SchTasks, new string[] { "/Run", "/TN", TaskName }, null, 30000);
            if (r.Code == -1)
                return "schtasks did not finish in 30 s";
            return r.Code == 0 ? "task " + TaskName + " started" : "schtasks exit " + r.Code + " " + r.Stderr.Trim();
        }

        private static string SchTasks { get { return Path.Combine(Environment.SystemDirectory, "schtasks.exe"); } }
    }

    internal sealed class WindowsPaths : IPlatformPaths
    {
        public string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        public string StateRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbenchBridge"); } }
        public string SharedRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIWorkbenchBridge"); } }
        public string LogRoot { get { return Path.Combine(StateRoot, "logs"); } }
        public string Normalize(string path) { return path; }
        public void EnsurePrivateDirectory(string path) { Directory.CreateDirectory(path); }
    }

    internal sealed class WindowsShell : IDesktopShell
    {
        public void OpenUrl(string url)
        {
            ProcessStartInfo psi = new ProcessStartInfo(url);
            psi.UseShellExecute = true;   // .NET 8 default is false; net48 Process.Start(url) used the shell
            Process.Start(psi);
        }

        public void OpenFolder(string path)
        {
            ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", CommandLine.Quote(path));
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
    }

    internal sealed class WindowsExecutables : IExecutables
    {
        public string Git { get { return "git.exe"; } }
        public string Python { get { return "python"; } }

        public string CatFileCommand(string path)
        {
            return "powershell -NoProfile -NonInteractive -Command \"Get-Content -Raw -Encoding UTF8 -LiteralPath '"
                + path.Replace("'", "''") + "'\"";
        }
    }
}

namespace AiWorkbenchBridge
{
    // Command-line actions of the .NET 8 Bridge, run before the mutex and the tray (Program.Main):
    //   --platform-info                      JSON: platform, adapters, paths, what is not supported here
    //   --install-autostart [--config PATH]  LaunchAgent / systemd user unit for this executable (Windows: install.ps1)
    //   --uninstall-autostart
    //   --google-connect CLIENT.json         Google sign-in in the browser, refresh token into the secret store
    //                                        (the tray's "Connect Google..." for a headless Bridge)
    internal static class PlatformCommands
    {
        public static bool TryRun(string[] argv)
        {
            if (Has(argv, "--platform-info"))
                return Done(delegate { Console.WriteLine(BridgePlatform.Describe(BridgePlatform.Current)); });

            if (Has(argv, "--install-autostart"))
            {
                return Done(delegate
                {
                    List<string> args = new List<string>();
                    int i = Array.IndexOf(argv, "--config");
                    if (i >= 0 && i + 1 < argv.Length)
                    {
                        args.Add("--config");
                        args.Add(Path.GetFullPath(argv[i + 1]));
                    }
                    Console.WriteLine(BridgePlatform.Current.Autostart.Install(Environment.ProcessPath, args.ToArray()));
                });
            }

            if (Has(argv, "--uninstall-autostart"))
                return Done(delegate { Console.WriteLine(BridgePlatform.Current.Autostart.Uninstall()); });

            int g = Array.IndexOf(argv, "--google-connect");
            if (g >= 0)
            {
                return Done(delegate
                {
                    if (g + 1 >= argv.Length)
                        throw new ArgumentException("--google-connect needs the OAuth client JSON (Desktop app).");
                    BridgeApplicationContext.ConnectGoogleFromCommandLine(Path.GetFullPath(argv[g + 1]));
                    Console.WriteLine("Google connected; the refresh token is in " + BridgePlatform.Current.Secrets.Name + ".");
                });
            }
            return false;
        }

        private static bool Has(string[] argv, string flag)
        {
            return Array.IndexOf(argv, flag) >= 0;
        }

        private static bool Done(Action action)
        {
            try
            {
                action();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }
    }

    internal sealed partial class BridgeApplicationContext
    {
        internal static void ConnectGoogleFromCommandLine(string clientFile)
        {
            GoogleAuth.RunInstalledAppFlow(clientFile, GoogleCredentialTarget, GoogleScopes);
        }
    }
}
