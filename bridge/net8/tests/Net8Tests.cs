// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a: tests of the .NET 8 Bridge - platform adapters, the headless UI, JavaScriptSerializer parity with
// .NET Framework (json-golden.txt), and then the existing Bridge suite (tests\AgentRuntimeTests) on .NET 8.
// Run: dotnet run --project bridge/net8/tests -f net8.0-windows   (or -f net8.0; Linux/macOS: net8.0 only)
// from the bridge folder or with --bridge <dir>. No agent CLI starts; secrets are a dummy value on a test target.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        internal static int RunExistingSuite(string[] argv)
        {
            return Main(argv);
        }
    }

    internal static class Net8Tests
    {
        private static readonly Dictionary<string, int[]> Groups = new Dictionary<string, int[]>();
        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> Skipped = new List<string>();

        private static void Check(string group, string name, bool ok, string detail = null)
        {
            int[] c;
            if (!Groups.TryGetValue(group, out c))
                Groups[group] = c = new int[2];
            c[0]++;
            if (ok)
                return;
            c[1]++;
            Failures.Add("[" + group + "] " + name + (detail == null ? "" : "\n  " + detail));
        }

        private static void Eq(string group, string name, string expected, string actual)
        {
            Check(group, name, String.Equals(expected, actual, StringComparison.Ordinal), "expected: " + expected + "\n  actual:   " + actual);
        }

        private static void Throws<T>(string group, string name, Action a) where T : Exception
        {
            try
            {
                a();
                Check(group, name, false, "no exception, expected " + typeof(T).Name);
            }
            catch (T)
            {
                Check(group, name, true);
            }
            catch (Exception ex)
            {
                Check(group, name, false, "expected " + typeof(T).Name + ", got " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int Main(string[] argv)
        {
            string bridgeDir = null;
            bool adaptersOnly = false;
            List<string> rest = new List<string>();
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--bridge" && i + 1 < argv.Length)
                    bridgeDir = argv[++i];
                else if (argv[i] == "--adapters-only")
                    adaptersOnly = true;
                else
                    rest.Add(argv[i]);
            }
            bridgeDir = Path.GetFullPath(bridgeDir ?? FindBridgeDir());
            Directory.SetCurrentDirectory(bridgeDir);
            Console.WriteLine("Bridge .NET 8 tests: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " " +
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + ", build " +
#if BRIDGE_HEADLESS
                "net8.0 (headless)"
#else
                "net8.0-windows (WinForms)"
#endif
                + ", platform " + BridgePlatform.Current.Id);

            TestJsonGolden(bridgeDir);
            TestJsonDetails();
            TestPlatformSelection();
            TestPaths();
            TestKeychain();
            TestSecretTool();
            TestLaunchAgent();
            TestSystemd();
            TestExecutables();
            TestSharedSourceHooks();
            TestProcessTree();
            TestProcessTreePidReuse();
            TestPrivateDirectory();
            TestCredentialManager();
            TestHeadlessUi();
            TestPlatformInfo();
            TestReleaseChannel();

            foreach (KeyValuePair<string, int[]> g in Groups)
                Console.WriteLine("net8 " + g.Key + ": checked " + g.Value[0] + ", failed " + g.Value[1]);
            foreach (string s in Skipped)
                Console.WriteLine("skipped: " + s);
            foreach (string f in Failures.Take(40))
                Console.WriteLine(f);
            bool fail = Groups.Values.Any(v => v[1] > 0);
            Console.WriteLine(fail ? "NET8 ADAPTERS: FAIL" : "NET8 ADAPTERS: PASS");
            if (adaptersOnly)
                return fail ? 1 : 0;

            Console.WriteLine("--- existing Bridge suite (tests\\AgentRuntimeTests) on .NET 8 ---");
            List<string> legacyArgs = rest.Count > 0 ? rest : new List<string> { "--config", "config.json.example", "--samples", Path.Combine("tests", "samples") };
            int legacy = AgentRuntimeTests.RunExistingSuite(legacyArgs.ToArray());
            return fail || legacy != 0 ? 1 : 0;
        }

        private static string FindBridgeDir()
        {
            string d = Directory.GetCurrentDirectory();
            while (d != null)
            {
                if (File.Exists(Path.Combine(d, "BridgeAgent.cs")))
                    return d;
                if (File.Exists(Path.Combine(d, "bridge", "BridgeAgent.cs")))
                    return Path.Combine(d, "bridge");
                d = Path.GetDirectoryName(d);
            }
            throw new InvalidOperationException("Bridge folder not found; pass --bridge <dir>.");
        }

        // ------------------------------------------------------------------ JSON

        private static void TestJsonGolden(string bridgeDir)
        {
            string golden = Path.Combine(bridgeDir, "net8", "tests", "json-golden.txt");
            string[] expected = File.ReadAllText(golden, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            List<string> actual = JsonGolden.Produce(bridgeDir);
            Check("json-golden", "line count", expected.Length == actual.Count, expected.Length + " vs " + actual.Count);
            int orderOnly = 0;
            for (int i = 0; i < Math.Min(expected.Length, actual.Count); i++)
            {
                string[] e = expected[i].Split(new[] { " | " }, 2, StringSplitOptions.None);
                string[] a = actual[i].Split(new[] { " | " }, 2, StringSplitOptions.None);
                // Member order of a typed object: .NET Framework returns properties in reflection-cache order, which
                // even differs between two objects of one type in one run (AgentProfile in config.json.example);
                // JSON readers go by name, so the same members and values in another order count as equal.
                if (expected[i] != actual[i] && e.Length == 2 && a.Length == 2 && e[0] == a[0] && Canonical(e[1]) != null && Canonical(e[1]) == Canonical(a[1]))
                {
                    orderOnly++;
                    Check("json-golden", e[0], true);
                    continue;
                }
                Eq("json-golden", e[0], expected[i], actual[i]);
            }
            if (orderOnly > 0)
                Skipped.Add("json-golden: " + orderOnly + " case(s) equal up to member order (net48 reflection order)");
        }

        private static string Canonical(string json)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                CanonicalValue(sb, JsonUtil.DeserializeObject(json));
                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CanonicalValue(StringBuilder sb, object v)
        {
            Dictionary<string, object> d = v as Dictionary<string, object>;
            object[] l = v as object[];
            if (d != null)
            {
                sb.Append('{');
                foreach (string k in d.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    sb.Append(JsonUtil.Serialize(k)).Append(':');
                    CanonicalValue(sb, d[k]);
                    sb.Append(',');
                }
                sb.Append('}');
            }
            else if (l != null)
            {
                sb.Append('[');
                foreach (object x in l)
                {
                    CanonicalValue(sb, x);
                    sb.Append(',');
                }
                sb.Append(']');
            }
            else
                sb.Append(JsonUtil.Serialize(v));
        }

        private static void TestJsonDetails()
        {
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
            Check("json", "default MaxJsonLength", js.MaxJsonLength == 2097152);
            Check("json", "default RecursionLimit", js.RecursionLimit == 100);
            string big = "\"" + new string('a', 2097152) + "\"";
            Throws<ArgumentException>("json", "input over MaxJsonLength", delegate { js.DeserializeObject(big); });
            Throws<InvalidOperationException>("json", "output over MaxJsonLength", delegate { js.Serialize(new string('a', 2097152)); });
            List<object> loop = new List<object>();
            loop.Add(loop);
            Throws<InvalidOperationException>("json", "circular reference", delegate { js.Serialize(loop); });
            Eq("json", "R 1/3", "0.33333333333333331", js.Serialize(1.0 / 3));
            Eq("json", "R -0", "0", js.Serialize(-0.0));
            Eq("json", "R 0.1", "0.1", js.Serialize(0.1));
            Eq("json", "JsonUtil round trip", "{\"a\":[1,\"x\"]}", JsonUtil.Serialize(JsonUtil.DeserializeObject("{\"a\":[1,\"x\"]}")));
            Check("json", "BridgeConfig of example", JsonUtil.Deserialize<BridgeConfig>(File.ReadAllText("config.json.example")).hostId != null);
        }

        // ------------------------------------------------------------------ platform

        private sealed class FakeRunner : CommandRunner
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Stdins = new List<string>();
            public Func<string, IList<string>, Result> Answer = delegate { return new Result(); };

            public override Result Run(string file, IList<string> args, string stdin, int timeoutMs)
            {
                Calls.Add(file + " " + String.Join(" ", args));
                Stdins.Add(stdin);
                return Answer(file, args);
            }
        }

        private static CommandRunner.Result R(int code, string stdout = "", string stderr = "")
        {
            CommandRunner.Result r = new CommandRunner.Result();
            r.Code = code;
            r.Stdout = stdout;
            r.Stderr = stderr;
            return r;
        }

        private static void TestPlatformSelection()
        {
            IBridgePlatform p = BridgePlatform.Detect();
            string expected = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            Eq("platform", "detected id", expected, p.Id);
            Check("platform", "tray only on Windows", p.HasTray == OperatingSystem.IsWindows());
            Check("platform", "NotSupported empty on Windows only", (p.NotSupported.Length == 0) == OperatingSystem.IsWindows());
            Check("platform", "mac secrets = Keychain", new MacPlatform(new FakeRunner(), "/Users/u").Secrets is KeychainStore);
            Check("platform", "mac autostart = LaunchAgent", new MacPlatform(new FakeRunner(), "/Users/u").Autostart is LaunchAgentAutostart);
            Check("platform", "linux secrets = secret-tool", new LinuxPlatform(new FakeRunner(), "/home/u").Secrets is SecretToolStore);
            Check("platform", "linux autostart = systemd", new LinuxPlatform(new FakeRunner(), "/home/u").Autostart is SystemdUserAutostart);
        }

        private static void TestPaths()
        {
            IPlatformPaths mac = UnixPaths.Mac("/Users/u");
            Eq("paths", "mac state root", "/Users/u/Library/Application Support/AIWorkbenchBridge", mac.StateRoot.Replace('\\', '/'));
            Eq("paths", "mac log root", "/Users/u/Library/Logs/AIWorkbenchBridge", mac.LogRoot.Replace('\\', '/'));
            Eq("paths", "mac shared = state", mac.StateRoot, mac.SharedRoot);
            Eq("paths", "mac %LOCALAPPDATA%", "/Users/u/Library/Application Support/AIWorkbenchBridge", mac.Normalize("%LOCALAPPDATA%\\AIWorkbenchBridge"));
            Eq("paths", "mac %localappdata% (case)", "/Users/u/Library/Application Support/x", mac.Normalize("%localappdata%\\x"));
            Eq("paths", "mac %USERPROFILE%", "/Users/u/Library/CloudStorage/My Drive", mac.Normalize("%USERPROFILE%\\Library\\CloudStorage\\My Drive"));
            Eq("paths", "mac ~/", "/Users/u/AI-Workbench/x", mac.Normalize("~/AI-Workbench\\x"));
            Eq("paths", "mac ~ alone", "/Users/u", mac.Normalize("~"));
            Eq("paths", "mac unknown var kept", "%AIWB_NO_SUCH_VAR_X%/a", mac.Normalize("%AIWB_NO_SUCH_VAR_X%\\a"));
            Eq("paths", "mac absolute untouched", "/opt/x", mac.Normalize("/opt/x"));
            Eq("paths", "mac empty", "", mac.Normalize(""));
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            try
            {
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
                Eq("paths", "linux state root", "/home/u/.local/share/AIWorkbenchBridge", UnixPaths.Linux("/home/u").StateRoot.Replace('\\', '/'));
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/data/x");
                Eq("paths", "linux XDG_DATA_HOME", "/data/x/AIWorkbenchBridge", UnixPaths.Linux("/home/u").StateRoot.Replace('\\', '/'));
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative");
                Eq("paths", "linux relative XDG ignored", "/home/u/.local/share/AIWorkbenchBridge", UnixPaths.Linux("/home/u").StateRoot.Replace('\\', '/'));
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
            }
            if (OperatingSystem.IsWindows())
            {
                Eq("paths", "windows Normalize is identity", "%LOCALAPPDATA%\\x", new WindowsPaths().Normalize("%LOCALAPPDATA%\\x"));
                Check("paths", "windows state root under LOCALAPPDATA", new WindowsPaths().StateRoot.EndsWith("\\AIWorkbenchBridge", StringComparison.Ordinal));
            }
            else
            {
                // The shared ExpandPath (BridgeAgent.cs) goes through the adapter on macOS/Linux.
                MethodInfo expand = typeof(BridgeApplicationContext).GetMethod("ExpandPath", BindingFlags.NonPublic | BindingFlags.Static);
                string state = (string)expand.Invoke(null, new object[] { "%LOCALAPPDATA%\\AIWorkbenchBridge", "/base" });
                Eq("paths", "ExpandPath default localStateRoot", BridgePlatform.Current.Paths.StateRoot, state);
                Eq("paths", "ExpandPath relative", "/base/x/y", (string)expand.Invoke(null, new object[] { "x\\y", "/base" }));
                Eq("paths", "config.json location", Path.Combine(BridgePlatform.Current.Paths.StateRoot, "config.json"), ConfigLocationProbe());
            }
        }

        private static string ConfigLocationProbe()
        {
            string installed = Path.Combine(BridgePlatform.Current.Paths.StateRoot, "config.json");
            return File.Exists(installed) ? ConfigLocation.Resolve() : installed;
        }

        private static void TestKeychain()
        {
            FakeRunner f = new FakeRunner();
            KeychainStore k = new KeychainStore(f);
            f.Answer = delegate { return R(0, "s3cret\n"); };
            Eq("keychain", "read value", "s3cret", k.Read("AIWorkbench.Bridge.Google"));
            Eq("keychain", "read argv", "/usr/bin/security find-generic-password -s AIWorkbench.Bridge.Google -w", f.Calls[0]);
            f.Answer = delegate { return R(44, "", "The specified item could not be found in the keychain."); };
            Check("keychain", "read missing -> null", k.Read("x") == null);
            f.Answer = delegate { return R(51, "", "User interaction is not allowed."); };
            Throws<InvalidOperationException>("keychain", "locked keychain throws", delegate { k.Read("x"); });

            f.Calls.Clear();
            f.Stdins.Clear();
            string secret = "{\"refresh_token\":\"1//a\\\"b\",\n\"x\":\"é\"}";
            f.Answer = delegate(string file, IList<string> args) { return args[0] == "find-generic-password" ? R(0, secret + "\n") : R(0); };
            k.Write("AIWorkbench.Bridge.Google", secret, "AI Workbench Bridge: Google token");
            Eq("keychain", "write argv has no secret", "/usr/bin/security -i", f.Calls[0]);
            Eq("keychain", "write is read back", "/usr/bin/security find-generic-password -s AIWorkbench.Bridge.Google -w", f.Calls[1]);
            string line = f.Stdins[0];
            Check("keychain", "write line shape", line.StartsWith("add-generic-password -U -s \"AIWorkbench.Bridge.Google\" -a \"default\" -l \"AI Workbench Bridge: Google token\" -X ", StringComparison.Ordinal) && line.EndsWith("\n", StringComparison.Ordinal), line);
            string hex = line.Substring(line.IndexOf(" -X ", StringComparison.Ordinal) + 4).TrimEnd('\n');
            Eq("keychain", "hex decodes to the secret", secret, Encoding.UTF8.GetString(Convert.FromHexString(hex)));
            Check("keychain", "secret not in clear text on stdin", line.IndexOf("refresh_token", StringComparison.Ordinal) < 0);
            f.Answer = delegate(string file, IList<string> args) { return args[0] == "find-generic-password" ? R(0, "7b22") : R(0); };
            Throws<InvalidOperationException>("keychain", "read-back differs -> throws", delegate { k.Write("t", "{\"", null); });
            f.Answer = delegate { return R(45, "", "duplicate"); };
            Throws<InvalidOperationException>("keychain", "write failure throws", delegate { k.Write("t", "v", null); });
            Throws<ArgumentException>("keychain", "target with quote refused", delegate { k.Read("a\"b"); });
            f.Answer = delegate { return R(44); };
            k.Delete("t");
            Check("keychain", "delete missing is fine", true);
            f.Answer = delegate { return R(1, "", "err"); };
            Throws<InvalidOperationException>("keychain", "delete failure throws", delegate { k.Delete("t"); });
        }

        private static void TestSecretTool()
        {
            FakeRunner f = new FakeRunner();
            SecretToolStore s = new SecretToolStore(f);
            f.Answer = delegate { return R(0, "tok"); };
            Eq("secret-tool", "lookup value", "tok", s.Read("AIWorkbench.Bridge.Google"));
            Eq("secret-tool", "lookup argv", "secret-tool lookup service AIWorkbench.Bridge.Google", f.Calls[0]);
            f.Answer = delegate { return R(1); };
            Check("secret-tool", "missing -> null", s.Read("x") == null);
            f.Answer = delegate { return R(1, "", "Cannot autolaunch D-Bus"); };
            Throws<InvalidOperationException>("secret-tool", "no D-Bus throws", delegate { s.Read("x"); });
            f.Calls.Clear();
            f.Stdins.Clear();
            f.Answer = delegate { return R(0); };
            s.Write("t", "val\"ue", "label");
            Eq("secret-tool", "store argv", "secret-tool store --label=label service t username default", f.Calls[0]);
            Eq("secret-tool", "secret on stdin", "val\"ue", f.Stdins[0]);
            f.Answer = delegate { throw new System.ComponentModel.Win32Exception(2, "No such file or directory"); };
            Throws<NotSupportedException>("secret-tool", "not installed -> NotSupported", delegate { s.Read("x"); });
        }

        private static void TestLaunchAgent()
        {
            string plist = LaunchAgentAutostart.BuildPlist("org.aiworkbench.bridge", "/Applications/AI Workbench/BridgeAgent",
                new[] { "--config", "/Users/u/a&b<c>.json" }, "/Users/u/Library/Logs/AIWorkbenchBridge", "/opt/homebrew/bin:/usr/bin");
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            try
            {
                XmlReaderSettings rs = new XmlReaderSettings();
                rs.DtdProcessing = DtdProcessing.Ignore;
                using (XmlReader r = XmlReader.Create(new StringReader(plist), rs))
                    doc.Load(r);
                Check("launchagent", "plist is well-formed XML", true);
            }
            catch (XmlException ex)
            {
                Check("launchagent", "plist is well-formed XML", false, ex.Message);
            }
            Check("launchagent", "label", plist.Contains("<key>Label</key><string>org.aiworkbench.bridge</string>"));
            Check("launchagent", "program arguments escaped", plist.Contains("<array><string>/Applications/AI Workbench/BridgeAgent</string><string>--config</string><string>/Users/u/a&amp;b&lt;c&gt;.json</string></array>"), plist);
            Check("launchagent", "restart only after a failure", plist.Contains("<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>"));
            Check("launchagent", "run at load", plist.Contains("<key>RunAtLoad</key><true/>"));
            Check("launchagent", "PATH for the CLIs", plist.Contains("<key>PATH</key><string>/opt/homebrew/bin:/usr/bin</string>"));
            Check("launchagent", "log file", plist.Contains("/Users/u/Library/Logs/AIWorkbenchBridge") && plist.Contains("bridge.out"));
            string agentPath = LaunchAgentAutostart.AgentPath("/Users/u");
            Check("launchagent", "agent PATH has ~/.local/bin and Homebrew", agentPath.StartsWith("/Users/u/.local/bin:/opt/homebrew/bin", StringComparison.Ordinal) && agentPath.Contains("/Users/u/.npm-global/bin"), agentPath);

            string home = Path.Combine(Path.GetTempPath(), "aiwb-net8-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                FakeRunner f = new FakeRunner();
                LaunchAgentAutostart la = new LaunchAgentAutostart(f, home, Path.Combine(home, "Logs"), 501);
                Check("launchagent", "not installed at first", !la.IsInstalled());
                Check("launchagent", "kick when missing says so", la.Kick().Contains("not installed"));
                la.Install("/x/BridgeAgent", new[] { "--config", "/c.json" });
                Check("launchagent", "plist written", File.Exists(la.Location) && la.Location.EndsWith(Path.Combine("Library", "LaunchAgents", "org.aiworkbench.bridge.plist"), StringComparison.Ordinal), la.Location);
                Eq("launchagent", "bootout first", "/bin/launchctl bootout gui/501/org.aiworkbench.bridge", f.Calls[0]);
                Eq("launchagent", "bootstrap", "/bin/launchctl bootstrap gui/501 " + la.Location, f.Calls[1]);
                f.Calls.Clear();
                Eq("launchagent", "kick result", "task org.aiworkbench.bridge started", la.Kick());
                Eq("launchagent", "kickstart argv", "/bin/launchctl kickstart gui/501/org.aiworkbench.bridge", f.Calls[0]);
                f.Answer = delegate(string file, IList<string> args) { return args[0] == "bootstrap" ? R(5, "", "Input/output error") : R(0); };
                Throws<InvalidOperationException>("launchagent", "bootstrap failure throws", delegate { la.Install("/x", new string[0]); });
                f.Answer = delegate { return R(0); };
                la.Uninstall();
                Check("launchagent", "uninstall removes plist", !File.Exists(la.Location));
                string inst = Environment.GetEnvironmentVariable("AIWB_BRIDGE_INSTANCE");
                Environment.SetEnvironmentVariable("AIWB_BRIDGE_INSTANCE", "test2");
                Eq("launchagent", "test instance label", "org.aiworkbench.bridge-test2", LaunchAgentAutostart.Label);
                Environment.SetEnvironmentVariable("AIWB_BRIDGE_INSTANCE", inst);
            }
            finally
            {
                try { Directory.Delete(home, true); } catch { }
            }
        }

        private static void TestSystemd()
        {
            string unit = SystemdUserAutostart.BuildUnit("/opt/aiwb/BridgeAgent", new[] { "--config", "/home/u/My \"Drive\"/50%/$x.json" }, SystemdUserAutostart.UnitPath("/home/u"));
            Check("systemd", "PATH with ~/.local/bin and npm global", unit.Contains("Environment=\"PATH=/home/u/.local/bin:/home/u/.npm-global/bin:/usr/local/sbin:/usr/local/bin:"), unit);
            Throws<ArgumentException>("systemd", "newline in an argument refused", delegate { SystemdUserAutostart.BuildUnit("/x", new[] { "a\nExecStartPre=/bin/evil" }, "/bin"); });
            Check("systemd", "ExecStart quoted", unit.Contains("ExecStart=\"/opt/aiwb/BridgeAgent\" \"--config\" \"/home/u/My \\\"Drive\\\"/50%%/$$x.json\""), unit);
            Check("systemd", "restart on failure only", unit.Contains("Restart=on-failure"));
            Check("systemd", "wanted by default.target", unit.Contains("WantedBy=default.target"));
            string cfg = Path.Combine(Path.GetTempPath(), "aiwb-net8-test-" + Guid.NewGuid().ToString("N"));
            string old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", cfg);
                FakeRunner f = new FakeRunner();
                SystemdUserAutostart s = new SystemdUserAutostart(f, "/home/u");
                s.Install("/opt/BridgeAgent", new string[0]);
                Check("systemd", "unit written", File.Exists(Path.Combine(cfg, "systemd", "user", SystemdUserAutostart.Unit)));
                Eq("systemd", "daemon-reload", "systemctl --user daemon-reload", f.Calls[0]);
                Eq("systemd", "enable --now", "systemctl --user enable --now aiworkbench-bridge.service", f.Calls[1]);
                Eq("systemd", "kick", "task aiworkbench-bridge.service started", s.Kick());
                s.Uninstall();
                Check("systemd", "uninstall removes unit", !s.IsInstalled());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
                try { Directory.Delete(cfg, true); } catch { }
            }
        }

        private static void TestExecutables()
        {
            UnixExecutables u = new UnixExecutables();
            Eq("exes", "unix git", "git", u.Git);
            Eq("exes", "unix python", "python3", u.Python);
            Eq("exes", "unix cat quoting", "cat '/a/it'\\''s file.json'", u.CatFileCommand("/a/it's file.json"));
            WindowsExecutables w = new WindowsExecutables();
            Eq("exes", "windows git", "git.exe", w.Git);
            Eq("exes", "windows cat = the original PowerShell command",
                "powershell -NoProfile -NonInteractive -Command \"Get-Content -Raw -Encoding UTF8 -LiteralPath 'C:\\a''b\\h.json'\"",
                w.CatFileCommand("C:\\a'b\\h.json"));
            if (!OperatingSystem.IsWindows())
            {
                string dir = Path.Combine(Path.GetTempPath(), "aiwb it's " + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string file = Path.Combine(dir, "h.json");
                    File.WriteAllText(file, "{\"x\":\"é\"}");
                    CommandRunner.Result r = CommandRunner.Default.Run("/bin/sh", new[] { "-c", u.CatFileCommand(file) }, null, 10000);
                    Eq("exes", "sh runs the cat hook command", "{\"x\":\"é\"}", r.Stdout);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        // The "#if NET8_0_OR_GREATER" spots of the shared sources.
        private static void TestSharedSourceHooks()
        {
            string cmd = ReadGuard.Command(OperatingSystem.IsWindows() ? "C:\\t\\read-guard.py" : "/t/read-guard.py", "claude-post");
            if (OperatingSystem.IsWindows())
                Eq("shared", "read-guard command (Windows, as net48)", "python \"C:/t/read-guard.py\" hook claude-post", cmd);
            else
                Eq("shared", "read-guard command (Unix)", "python3 '/t/read-guard.py' hook claude-post", cmd);

            // Subagent hook settings: the hook command prints the hook file on this platform.
            string settings = SubagentHookFiles.Ensure("net8 test context " + Guid.NewGuid().ToString("N"), false);
            string text = File.ReadAllText(settings);
            if (OperatingSystem.IsWindows())
                Check("shared", "subagent hook uses PowerShell on Windows", text.Contains("powershell -NoProfile"), text);
            else
            {
                Check("shared", "subagent hook uses cat on Unix", text.Contains("\"command\":\"cat \\u0027"), text);
                Dictionary<string, object> root = (Dictionary<string, object>)JsonUtil.DeserializeObject(text);
                string command = (string)((Dictionary<string, object>)((object[])((Dictionary<string, object>)((object[])((Dictionary<string, object>)root["hooks"])["SubagentStart"])[0])["hooks"])[0])["command"];
                CommandRunner.Result r = CommandRunner.Default.Run("/bin/sh", new[] { "-c", command }, null, 10000);
                Check("shared", "subagent hook command prints the hook JSON", r.Code == 0 && r.Stdout.Contains("SubagentStart"), r.Stdout + r.Stderr);
            }
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(settings), Path.GetFileName(settings).Replace(".settings.json", ".*")))
                File.Delete(f);

            BridgeApplicationContext.RunAsSettingsProbe(out string runAsError);
            if (OperatingSystem.IsWindows())
                Check("shared", "runAs path reachable on Windows", runAsError == null || !runAsError.Contains("Windows-only"), runAsError);
            else
                Check("shared", "runAs is NotSupported on Unix", runAsError != null && runAsError.Contains("Windows-only"), runAsError);
        }

        // ------------------------------------------------------------------ process tree

        private static void TestProcessTree()
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
                psi = new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul & ping -n 60 127.0.0.1 >nul");
            else
                psi = new ProcessStartInfo("/bin/sh", "-c \"sleep 60 & sleep 60 & wait\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            using (WindowsJobObject job = new WindowsJobObject())
            {
                job.Assign(p);
                int active = 0;
                for (int i = 0; i < 50 && active < 2; i++)
                {
                    Thread.Sleep(100);
                    active = job.ActiveProcesses();
                }
                Check("process-tree", "root and child counted (" + active + ")", active >= 2);
                job.Terminate(9);
                bool exited = p.WaitForExit(10000);
                Check("process-tree", "root killed", exited);
                int left = -2;
                for (int i = 0; i < 50 && left != 0; i++)
                {
                    Thread.Sleep(100);
                    left = job.ActiveProcesses();
                }
                Check("process-tree", "no process left (" + left + ")", left == 0);
            }
            if (!OperatingSystem.IsWindows())
            {
                // Dispose kills as the Job Object's kill-on-close, also a child that was seen and then lost its parent
                // (reparented to init/launchd). A child that leaves before the first look escapes (documented).
                string marker = "61." + new Random().Next(100, 999);
                ProcessStartInfo orphan = new ProcessStartInfo("/bin/sh", "-c \"sleep " + marker + " & sleep 1\"");
                orphan.UseShellExecute = false;
                using (Process p = Process.Start(orphan))
                {
                    WindowsJobObject job = new WindowsJobObject();
                    job.Assign(p);
                    Thread.Sleep(400);
                    int seen = job.ActiveProcesses();
                    bool parentGone = p.WaitForExit(5000);
                    int orphans = job.ActiveProcesses();
                    job.Dispose();
                    Thread.Sleep(300);
                    bool left = Directory.Exists("/proc/self") && Directory.GetDirectories("/proc").Any(d => ArgsOf(Path.GetFileName(d)).Contains(marker));
                    Check("process-tree", "orphan still counted after its parent ended (seen " + seen + ", then " + orphans + ")", parentGone && seen >= 3 && orphans == 1);
                    Check("process-tree", "dispose kills the reparented child", !left);
                }
            }
        }

        // Fake process table: pid reuse cases for the Unix process tree (review F3a-1).
        private sealed class FakeTable : IProcessTable
        {
            public Dictionary<int, ProcessEntry> Procs = new Dictionary<int, ProcessEntry>();
            public Dictionary<int, long> StartAtKill;   // TryGetStart answer at kill time (null = from Procs)

            public Dictionary<int, ProcessEntry> Snapshot() { return new Dictionary<int, ProcessEntry>(Procs); }

            public bool TryGetStart(int pid, out long start)
            {
                if (StartAtKill != null)
                    return StartAtKill.TryGetValue(pid, out start);
                ProcessEntry e;
                bool ok = Procs.TryGetValue(pid, out e);
                start = e.Start;
                return ok;
            }
        }

        private static void TestProcessTreePidReuse()
        {
            const string g = "process-tree-reuse";
            FakeTable t = new FakeTable();
            List<int> killed = new List<int>();
            t.Procs[1] = new ProcessEntry(0, 1);
            t.Procs[100] = new ProcessEntry(50, 1000);
            t.Procs[101] = new ProcessEntry(100, 1010);
            t.Procs[102] = new ProcessEntry(101, 1020);
            t.Procs[200] = new ProcessEntry(1, 500);   // unrelated
            UnixProcessTree tree = new UnixProcessTree(t, delegate(int pid) { killed.Add(pid); });
            tree.AssignPid(100);
            Eq(g, "root, child and grandchild", "3", tree.ActiveProcesses().ToString());

            // the root ends; its child is reparented to init and stays in the job
            t.Procs.Remove(100);
            t.Procs[101] = new ProcessEntry(1, 1010);
            Eq(g, "reparented descendants kept", "2", tree.ActiveProcesses().ToString());

            // pid 100 is reused by an unrelated process with its own children: never adopted
            t.Procs[100] = new ProcessEntry(1, 3000);
            t.Procs[103] = new ProcessEntry(100, 3010);
            Eq(g, "reused root pid not adopted", "2", tree.ActiveProcesses().ToString());
            Check(g, "reused pid's child not adopted", !tree.Known.ContainsKey(103) && !tree.Known.ContainsKey(100));

            // a known child ends and its pid is reused; the new process' children are not walked
            t.Procs.Remove(102);
            t.Procs[102] = new ProcessEntry(1, 4000);
            t.Procs[104] = new ProcessEntry(102, 4010);
            tree.ActiveProcesses();
            Check(g, "stale entry dropped before the walk", !tree.Known.ContainsKey(102) && !tree.Known.ContainsKey(104));

            // a "child" older than its parent (parent pid reused after the child started) is not taken
            UnixProcessTree t2 = new UnixProcessTree(t, delegate(int pid) { killed.Add(pid); });
            t.Procs[300] = new ProcessEntry(1, 5000);
            t.Procs[301] = new ProcessEntry(300, 4999);
            t2.AssignPid(300);
            Eq(g, "older child refused", "1", t2.ActiveProcesses().ToString());

            // kill: only pids whose start time still matches right before the kill
            t.StartAtKill = new Dictionary<int, long> { { 101, 1010 } };   // 300 changed between snapshot and kill
            killed.Clear();
            tree.Dispose();
            t2.Dispose();
            Eq(g, "killed only verified pids", "101", String.Join(",", killed));
            Check(g, "unrelated processes untouched", !killed.Contains(200) && !killed.Contains(100) && !killed.Contains(103) && !killed.Contains(104));
            Eq(g, "disposed -> -1", "-1", tree.ActiveProcesses().ToString());

            ProcessEntry e;
            Check(g, "stat parse with ') ' in comm", UnixProcessTable.TryParseStat("42 (a) b (c)) S 7 42 42 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 98765 1000 10", out e) && e.Ppid == 7 && e.Start == 98765, e.Ppid + "/" + e.Start);
            Check(g, "zombie not counted", !UnixProcessTable.TryParseStat("43 (sleep) Z 7 43 43 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 98766 0 0", out e));
            int pid2;
            Check(g, "ps line parse", UnixProcessTable.TryParsePsLine("  612     1 Ss   Thu Nov  5 09:01:02 2026", out pid2, out e) && pid2 == 612 && e.Ppid == 1 && e.Start > 0);
            Check(g, "ps zombie skipped", !UnixProcessTable.TryParsePsLine("  613     1 Z    Thu Nov  5 09:01:02 2026", out pid2, out e));
        }

        private static void TestPrivateDirectory()
        {
            if (OperatingSystem.IsWindows())
            {
                Skipped.Add("state root mode 0700 (Unix only)");
                return;
            }
            string home = Path.Combine(Path.GetTempPath(), "aiwb-net8-home-" + Guid.NewGuid().ToString("N"));
            try
            {
                UnixPaths p = UnixPaths.Linux(home);
                p.EnsurePrivateDirectory(p.StateRoot);
                Eq("paths", "state root created 0700", "700", Convert.ToString((int)File.GetUnixFileMode(p.StateRoot), 8));
                File.SetUnixFileMode(p.StateRoot, (UnixFileMode)Convert.ToInt32("755", 8));
                p.EnsurePrivateDirectory(p.StateRoot);
                Eq("paths", "existing 0755 narrowed to 0700", "700", Convert.ToString((int)File.GetUnixFileMode(p.StateRoot), 8));
            }
            finally
            {
                try { Directory.Delete(home, true); } catch { }
            }
        }

        private static string ArgsOf(string pid)
        {
            try { return File.ReadAllText("/proc/" + pid + "/cmdline").Replace('\0', ' '); }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ secrets on this machine

        private static void TestCredentialManager()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skipped.Add("Credential Manager round trip (Windows only); Keychain/libsecret: fake runner above, real store needs a Mac / a desktop session");
                return;
            }
            const string target = "AIWorkbench.Bridge.Net8Test";
            ISecretStore s = BridgePlatform.Current.Secrets;
            try
            {
                s.Write(target, "dummy-test-value-é", "Bridge .NET 8 test (dummy)");
                Eq("credman", "round trip", "dummy-test-value-é", s.Read(target));
            }
            finally
            {
                s.Delete(target);
            }
            Check("credman", "deleted", s.Read(target) == null);
        }

        // ------------------------------------------------------------------ headless UI

        private static void TestHeadlessUi()
        {
#if BRIDGE_HEADLESS
            Check("headless", "YesNo -> No", HeadlessUi.MessageBox.Show("t", "c", HeadlessUi.MessageBoxButtons.YesNo, HeadlessUi.MessageBoxIcon.Warning) == HeadlessUi.DialogResult.No);
            Check("headless", "OKCancel -> Cancel", HeadlessUi.MessageBox.Show("t", "c", HeadlessUi.MessageBoxButtons.OKCancel, HeadlessUi.MessageBoxIcon.Question) == HeadlessUi.DialogResult.Cancel);
            Check("headless", "OK -> OK", HeadlessUi.MessageBox.Show("t", "c", HeadlessUi.MessageBoxButtons.OK, HeadlessUi.MessageBoxIcon.Error) == HeadlessUi.DialogResult.OK);
            using (HeadlessUi.OpenFileDialog d = new HeadlessUi.OpenFileDialog())
                Check("headless", "file dialog cancelled", d.ShowDialog() == HeadlessUi.DialogResult.Cancel);

            string state = Path.Combine(Path.GetTempPath(), "aiwb-net8-tray-" + Guid.NewGuid().ToString("N"));
            IBridgePlatform real = BridgePlatform.Current;
            BridgePlatform.Override = new TempStatePlatform(real, state);
            try
            {
                HeadlessUi.ContextMenuStrip menu = new HeadlessUi.ContextMenuStrip();
                HeadlessUi.ToolStripMenuItem status = new HeadlessUi.ToolStripMenuItem("ONLINE | running 0");
                status.Enabled = false;
                menu.Items.Add(status);
                menu.Items.Add(new HeadlessUi.ToolStripSeparator());
                menu.Items.Add(new HeadlessUi.ToolStripMenuItem("Exit"));
                HeadlessUi.NotifyIcon tray = new HeadlessUi.NotifyIcon();
                tray.ContextMenuStrip = menu;
                tray.Icon = "Busy";
                tray.Text = "AI Bridge: ONLINE";
                tray.Visible = true;
                string json = File.ReadAllText(Path.Combine(state, "tray.json"));
                Eq("headless", "tray.json", "{\"visible\":true,\"state\":\"Busy\",\"text\":\"AI Bridge: ONLINE\",\"status\":[\"ONLINE | running 0\"]}", json);
                tray.Dispose();
                Check("headless", "tray.json hidden after dispose", File.ReadAllText(Path.Combine(state, "tray.json")).Contains("\"visible\":false"));
            }
            finally
            {
                BridgePlatform.Override = real;
                try { Directory.Delete(state, true); } catch { }
            }

            // Application.Run: timer ticks on the loop thread one at a time; ExitThread from a tick ends Run.
            ProbeContext ctx = new ProbeContext();
            Thread loop = new Thread(delegate() { HeadlessUi.Application.Run(ctx); });
            loop.IsBackground = true;
            loop.Start();
            bool ended = loop.Join(10000);
            Check("headless", "Run ends after ExitThread", ended);
            Check("headless", "ticks on the loop thread (" + ctx.Ticks + ")", ctx.Ticks >= 3 && ctx.WrongThread == 0 && ctx.MaxConcurrent == 1);
            Check("headless", "ExitThreadCore ran", ctx.Exited);
#else
            Skipped.Add("headless UI (net8.0 build only; this run is net8.0-windows with WinForms)");
#endif
        }

#if BRIDGE_HEADLESS
        private sealed class ProbeContext : HeadlessUi.ApplicationContext
        {
            private readonly HeadlessUi.Timer _t = new HeadlessUi.Timer();
            private int _loopThread;
            private int _inTick;
            public int Ticks, WrongThread, MaxConcurrent;
            public bool Exited;

            public ProbeContext()
            {
                _t.Interval = 20;
                _t.Tick += delegate
                {
                    if (_loopThread == 0)
                        _loopThread = Environment.CurrentManagedThreadId;
                    else if (_loopThread != Environment.CurrentManagedThreadId)
                        WrongThread++;
                    int c = Interlocked.Increment(ref _inTick);
                    MaxConcurrent = Math.Max(MaxConcurrent, c);
                    Thread.Sleep(30);   // longer than the interval: ticks must not pile up or overlap
                    Interlocked.Decrement(ref _inTick);
                    if (++Ticks == 3)
                        ExitThread();
                };
                _t.Start();
            }

            protected override void ExitThreadCore()
            {
                _t.Dispose();
                Exited = true;
                base.ExitThreadCore();
            }
        }
#endif

        private sealed class TempStatePlatform : IBridgePlatform
        {
            private readonly IBridgePlatform _inner;
            private readonly IPlatformPaths _paths;

            public TempStatePlatform(IBridgePlatform inner, string state)
            {
                _inner = inner;
                _paths = new FixedPaths(state);
            }

            public string Id { get { return _inner.Id; } }
            public bool HasTray { get { return _inner.HasTray; } }
            public ISecretStore Secrets { get { return _inner.Secrets; } }
            public IAutostart Autostart { get { return _inner.Autostart; } }
            public IPlatformPaths Paths { get { return _paths; } }
            public IDesktopShell Shell { get { return _inner.Shell; } }
            public IExecutables Executables { get { return _inner.Executables; } }
            public string[] NotSupported { get { return _inner.NotSupported; } }

            private sealed class FixedPaths : IPlatformPaths
            {
                private readonly string _state;
                public FixedPaths(string state) { _state = state; }
                public string Home { get { return _state; } }
                public string StateRoot { get { return _state; } }
                public string SharedRoot { get { return _state; } }
                public string LogRoot { get { return _state; } }
                public string Normalize(string path) { return path; }
                public void EnsurePrivateDirectory(string path) { Directory.CreateDirectory(path); }
            }
        }

        // ------------------------------------------------------------------ release channel of Linux/macOS (UnixLauncher.cs)

        private sealed class MemorySecrets : ISecretStore
        {
            public readonly Dictionary<string, string> Items = new Dictionary<string, string>();
            public string Name { get { return "memory"; } }
            public string Read(string target) { string v; return Items.TryGetValue(target, out v) ? v : null; }
            public void Write(string target, string secret, string comment) { Items[target] = secret; }
            public void Delete(string target) { Items.Remove(target); }
        }

        private static void TestReleaseChannel()
        {
            MemorySecrets store = new MemorySecrets();
            Throws<InvalidOperationException>("release", "no key without --create", delegate { ReleaseCrypto.LoadKey(store, false); });
            string pub;
            using (System.Security.Cryptography.ECDsa k = ReleaseCrypto.LoadKey(store, true))
                pub = ReleaseCrypto.PublicBlob(k);
            Check("release", "private key kept in the secret store", store.Items.ContainsKey(ReleaseCrypto.KeyTarget));
            using (System.Security.Cryptography.ECDsa k = ReleaseCrypto.LoadKey(store, false))
            {
                Eq("release", "same key on the next run", pub, ReleaseCrypto.PublicBlob(k));
                string sha = new string('a', 64);
                string sig = ReleaseCrypto.Sign(k, "1.19.0", sha);
                Check("release", "signature verifies", ReleaseCrypto.Verify(pub, "1.19.0", sha, sig));
                Check("release", "other hash refused", !ReleaseCrypto.Verify(pub, "1.19.0", new string('b', 64), sig));
                Check("release", "other version refused", !ReleaseCrypto.Verify(pub, "1.19.1", sha, sig));
                Check("release", "hash case ignored (signed lower case)", ReleaseCrypto.Verify(pub, "1.19.0", sha.ToUpperInvariant(), sig));
                Check("release", "bad key blob refused", !ReleaseCrypto.Verify("AAAA", "1.19.0", sha, sig));
                Check("release", "bad signature text refused", !ReleaseCrypto.Verify(pub, "1.19.0", sha, "not base64!"));
            }
            Check("release", "public blob is a P-256 EccPublicBlob (72 bytes)", Convert.FromBase64String(pub).Length == 72);
            Eq("release", "signed text as Launcher.cs", "AIWB-BRIDGE-RELEASE\n1.2.3\nab\n", ReleaseCrypto.SignedText("1.2.3", "AB"));

#if !BRIDGE_HEADLESS
            // The key of release.ps1 (CNG, EccPublicBlob, ECDsaCng over the same text) verifies here, and back.
            using (System.Security.Cryptography.ECDsaCng cng = new System.Security.Cryptography.ECDsaCng(256))
            {
                cng.HashAlgorithm = System.Security.Cryptography.CngAlgorithm.Sha256;
                string cngPub = Convert.ToBase64String(cng.Key.Export(System.Security.Cryptography.CngKeyBlobFormat.EccPublicBlob));
                string sha = new string('c', 64);
                string sig = Convert.ToBase64String(cng.SignData(Encoding.UTF8.GetBytes(ReleaseCrypto.SignedText("2.0.0", sha))));
                Check("release", "release.ps1 signature verifies (CNG key)", ReleaseCrypto.Verify(cngPub, "2.0.0", sha, sig));
                Eq("release", "same blob as CNG export", cngPub, ReleaseCrypto.PublicBlob(cng));
            }
#endif

            // v1.19.0: the device-call key outside Windows lives in the secret store; signatures verify as CO.Core's
            using (System.Security.Cryptography.ECDsa dk = DeviceCrypto.OpenOrCreateStoredKey(store))
            {
                Check("devices", "device-call key kept in the secret store", store.Items.ContainsKey(DeviceCrypto.CallKeyName));
                byte[] data = Encoding.UTF8.GetBytes("co.tool.call.v1|x");
                string pk = DeviceCrypto.PublicKey(dk);
                string sg = DeviceCrypto.Sign(dk, data);
                Check("devices", "public key is X||Y (64 bytes)", Convert.FromBase64String(pk).Length == 64);
                Check("devices", "signature verifies", DeviceCrypto.Verify(pk, data, sg));
                Check("devices", "other data refused", !DeviceCrypto.Verify(pk, Encoding.UTF8.GetBytes("other"), sg));
                using (System.Security.Cryptography.ECDsa again = DeviceCrypto.OpenOrCreateStoredKey(store))
                    Eq("devices", "same key on the next start", pk, DeviceCrypto.PublicKey(again));
            }

            string[] files =
            {
                "/c/BridgeAgent-1.19.0-linux-x64.sig.json", "/c/BridgeAgent-1.20.0-linux-x64.sig.json",
                "/c/BridgeAgent-1.21.0-linux-arm64.sig.json", "/c/BridgeAgent-1.22.0.sig.json",
                "/c/BridgeAgent-1.23.0-linux-x64.sig.json", "/c/BridgeAgent-x.y-linux-x64.sig.json",
            };
            Eq("release", "newest of this runtime id, bad skipped", "1.20.0", UnixLauncher.Newest(files, "linux-x64", new List<string> { "1.23.0" }));
            Eq("release", "arm64 sees its own", "1.21.0", UnixLauncher.Newest(files, "linux-arm64", new List<string>()));
            Check("release", "nothing for another runtime id", UnixLauncher.Newest(files, "osx-arm64", new List<string>()) == null);

            if (OperatingSystem.IsWindows())
            {
                Skipped.Add("release publish + launcher check-once (Unix file modes)");
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), "aiwb-release-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string channel = Path.Combine(dir, "updates");
                string bin = Path.Combine(dir, "BridgeAgent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(bin, "#!/bin/sh\nexit 0\n");
                ReleaseCrypto.Publish(store, channel, bin, "1.2.3", "abc", "linux-x64", false);
                string sigFile = Path.Combine(channel, "BridgeAgent-1.2.3-linux-x64.sig.json");
                Check("release", "publish writes the build and .sig.json", File.Exists(sigFile) && File.Exists(Path.Combine(channel, "BridgeAgent-1.2.3-linux-x64")));
                UnixReleaseSig s = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<UnixReleaseSig>(File.ReadAllText(sigFile));
                Check("release", "published signature verifies", ReleaseCrypto.Verify(pub, s.version, s.sha256, s.signature));
                Check("release", "same build again is fine", ReleaseCrypto.Publish(store, channel, bin, "1.2.3", "abc", "linux-x64", false).Contains("already published"));
                File.AppendAllText(bin, "#");
                Throws<InvalidOperationException>("release", "other build of a published version refused", delegate { ReleaseCrypto.Publish(store, channel, bin, "1.2.3", "abc", "linux-x64", false); });
                Throws<ArgumentException>("release", "version must be x.y.z", delegate { ReleaseCrypto.Publish(store, channel, bin, "1.2", "abc", "linux-x64", false); });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestPlatformInfo()
        {
            StringWriter w = new StringWriter();
            TextWriter old = Console.Out;
            Console.SetOut(w);
            try
            {
                PlatformCommands.TryRun(new[] { "BridgeAgent", "--platform-info" });
            }
            finally
            {
                Console.SetOut(old);
            }
            Dictionary<string, object> info = JsonUtil.DeserializeObject(w.ToString()) as Dictionary<string, object>;
            Check("platform-info", "JSON object", info != null, w.ToString());
            if (info != null)
            {
                Eq("platform-info", "platform", BridgePlatform.Current.Id, info["platform"] as string);
#if BRIDGE_HEADLESS
                Eq("platform-info", "tray", "headless", info["tray"] as string);
#else
                Eq("platform-info", "tray", "winforms", info["tray"] as string);
#endif
            }
            Check("platform-info", "unknown args not taken", !PlatformCommands.TryRun(new[] { "BridgeAgent", "--validate-config" }));
        }
    }
}

namespace AiWorkbenchBridge
{
    // Test hook: ApplyRunAs (BridgeDevices.cs) on an uninitialised context with only a runAs config.
    internal sealed partial class BridgeApplicationContext
    {
        internal static void RunAsSettingsProbe(out string error)
        {
            BridgeApplicationContext c = (BridgeApplicationContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BridgeApplicationContext));
            c._config = new BridgeConfig();
            c._config.runAs = new RunAsSettings();
            c._config.runAs.user = "aiwb-no-such-user-net8";
            c._config.runAs.credentialTarget = "AIWorkbench.Bridge.Net8Test.None";
            try
            {
                c.ApplyRunAs(new System.Diagnostics.ProcessStartInfo("x"));
                error = null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
    }
}
