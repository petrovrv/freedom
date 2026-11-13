// SPDX-License-Identifier: AGPL-3.0-or-later
// Test harness for the AgentRuntime split (Bridge 1.4.0): the frozen 1.3.5 code (Legacy135.cs) and the new
// runtimes must produce the same snapshots for real agent streams and the same command lines. No CLI is started.
// Build and run: tests\run-tests.cmd
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        // group name -> { compared, mismatches }
        static readonly Dictionary<string, int[]> Groups = new Dictionary<string, int[]>();
        static readonly List<string> Failures = new List<string>();
        static int ExpectedMatch, ExpectedMismatch, ParserFiles;

        static string Cut(string s, int max)
        {
            if (s == null) return "<null>";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        static void Check(string group, string caseName, string legacy, string fresh)
        {
            int[] c;
            if (!Groups.TryGetValue(group, out c)) { c = new int[2]; Groups[group] = c; }
            c[0]++;
            if (string.Equals(legacy, fresh, StringComparison.Ordinal)) return;
            c[1]++;
            Failures.Add("[" + group + "] " + caseName + "\n  legacy: " + Cut(legacy, 400) + "\n  new:    " + Cut(fresh, 400));
        }

        static int Usage()
        {
            Console.WriteLine("usage: AgentRuntimeTests [--samples <dir>]... [--config <path>]...");
            return 2;
        }

        static int Main(string[] argv)
        {
            List<string> samples = new List<string>();
            List<string> configs = new List<string>();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                if ((a == "--samples" || a == "--config") && i + 1 < argv.Length)
                {
                    if (a == "--samples") samples.Add(argv[++i]); else configs.Add(argv[++i]);
                }
                else return Usage();
            }

            // v1.8.0: the legacy comparison runs without read-guard hooks; TestReadGuard checks them on their own.
            Environment.SetEnvironmentVariable("AIWB_READ_GUARD", "off");
            TestRegistry();
            TestParsers(samples);
            TestArguments(configs);
            TestReadGuard();
            TestJobRole();
            TestSupervisor(configs);
            TestRelay();
            TestPcTools();
            TestBrowserSettings();
            TestModules();
            TestGateway(); // last: it leaves the neutral gateway set

            foreach (KeyValuePair<string, int[]> g in Groups)
                Console.WriteLine(g.Key + ": compared " + g.Value[0] + ", mismatches " + g.Value[1]);
            Console.WriteLine("parser sample files: " + ParserFiles);
            Console.WriteLine("expected (informational): " + ExpectedMatch + " match, " + ExpectedMismatch + " mismatch");
            foreach (string f in Failures.Take(40)) Console.WriteLine(f);
            if (Failures.Count > 40) Console.WriteLine("... " + (Failures.Count - 40) + " more");

            bool fail = Groups.Values.Any(v => v[1] > 0);
            Console.WriteLine(fail ? "RESULT: FAIL" : "RESULT: PASS");
            return fail ? 1 : 0;
        }

        // v1.10.0: db.php browser_settings -> device call "browser" and AIWB_BROWSER_* (clamped, defaults on junk)
        static void TestBrowserSettings()
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            Func<string, string> wire = json => ser.Serialize(BridgeApplicationContext.BrowserSettings.From(
                json == null ? null : ser.Deserialize<Dictionary<string, object>>(json)).ToWire());
            Check("browser", "missing", "{\"tabs\":0,\"idleMin\":120,\"formIdleMin\":360,\"captchaKeep\":true}", wire(null));
            Check("browser", "sam", "{\"tabs\":1,\"idleMin\":120,\"formIdleMin\":360,\"captchaKeep\":true}", wire("{\"tabs\":1}"));
            Check("browser", "clamp", "{\"tabs\":20,\"idleMin\":5,\"formIdleMin\":2880,\"captchaKeep\":false}",
                wire("{\"tabs\":99,\"idleMin\":1,\"formIdleMin\":99999,\"captchaKeep\":false}"));
            Check("browser", "form not below idle", "{\"tabs\":0,\"idleMin\":600,\"formIdleMin\":600,\"captchaKeep\":true}",
                wire("{\"idleMin\":600,\"formIdleMin\":60}"));
            Check("browser", "junk", "{\"tabs\":0,\"idleMin\":120,\"formIdleMin\":360,\"captchaKeep\":true}",
                wire("{\"tabs\":\"5\",\"idleMin\":null,\"captchaKeep\":\"no\"}"));
        }

        static void TestReadGuard()
        {
            string script = Path.Combine(Path.GetTempPath(), "aiwb-test-read-guard.py");
            File.WriteAllText(script, "# test stub");
            try
            {
                Check("read-guard", "off: no script", "True", (ReadGuard.ScriptPath == null).ToString());
                Environment.SetEnvironmentVariable("AIWB_READ_GUARD", script);
                Check("read-guard", "env path", Path.GetFullPath(script), ReadGuard.ScriptPath ?? "<null>");
                string settings = File.ReadAllText(SubagentHookFiles.Ensure(null, true));
                Check("read-guard", "claude settings post", "True", settings.Contains("PostToolUse").ToString());
                Check("read-guard", "claude settings pre", "True", settings.Contains("hook claude-pre").ToString());
                Check("read-guard", "claude settings failure", "True", settings.Contains("PostToolUseFailure").ToString());
                Check("read-guard", "claude pre covers Read", "True", settings.Contains("WebFetch|Read|Write|Edit|MultiEdit|NotebookEdit|mcp__.*\",\"hooks\":[{\"type\":\"command\",\"command\":\"python").ToString());
                Check("read-guard", "claude settings without context has no SubagentStart", "False", settings.Contains("SubagentStart").ToString());
                string both = File.ReadAllText(SubagentHookFiles.Ensure("policy text", true));
                Check("read-guard", "claude settings with context", "True", (both.Contains("SubagentStart") && both.Contains("PostToolUse")).ToString());
                Check("read-guard", "codex runtime HasReadGuardHooks (no Codex hooks)", "False", AgentRuntime.Get("codex-jsonl").HasReadGuardHooks.ToString());
                Check("read-guard", "raw runtime HasReadGuardHooks", "False", AgentRuntime.Get("raw").HasReadGuardHooks.ToString());
                ReadGuard.RunAsActive = true;
                Check("read-guard", "runAs: off", "True", (ReadGuard.ScriptPath == null).ToString());
                ReadGuard.RunAsActive = false;
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIWB_READ_GUARD", "off");
                try { File.Delete(script); } catch (IOException) { }
            }
        }

        // v1.18.0: own keyring per person - a household member's job gets role member and the refusing keyring backend
        static void TestJobRole()
        {
            Check("job-role", "owner's env", "owner", JobRole.Of("anna", "anna"));
            Check("job-role", "member's env", "member", JobRole.Of("kid", "anna"));
            Check("job-role", "no env = owner's own job", "owner", JobRole.Of("", "anna"));
            Check("job-role", "owner unknown = owner", "owner", JobRole.Of("kid", ""));
            string saved = Environment.GetEnvironmentVariable("AIWB_SECRET_DIR");
            try
            {
                Environment.SetEnvironmentVariable("AIWB_SECRET_DIR", @"C:\lib\secret-prompt");
                var owner = new System.Diagnostics.ProcessStartInfo("x");
                owner.EnvironmentVariables.Remove("PYTHON_KEYRING_BACKEND");
                owner.EnvironmentVariables["PYTHONPATH"] = @"C:\old";
                JobRole.Apply(owner, "anna", "anna");
                Check("job-role", "owner: AIWB_JOB_ROLE", "owner", owner.EnvironmentVariables["AIWB_JOB_ROLE"]);
                Check("job-role", "owner: AIWB_JOB_ENV", "anna", owner.EnvironmentVariables["AIWB_JOB_ENV"]);
                Check("job-role", "owner: keyring as before", "False", owner.EnvironmentVariables.ContainsKey("PYTHON_KEYRING_BACKEND").ToString());
                Check("job-role", "owner: PYTHONPATH untouched", @"C:\old", owner.EnvironmentVariables["PYTHONPATH"]);
                var member = new System.Diagnostics.ProcessStartInfo("x");
                member.EnvironmentVariables["PYTHONPATH"] = @"C:\old";
                JobRole.Apply(member, "kid", "anna");
                Check("job-role", "member: AIWB_JOB_ROLE", "member", member.EnvironmentVariables["AIWB_JOB_ROLE"]);
                Check("job-role", "member: AIWB_JOB_ENV", "kid", member.EnvironmentVariables["AIWB_JOB_ENV"]);
                Check("job-role", "member: refusing keyring backend", JobRole.RefusingBackend, member.EnvironmentVariables["PYTHON_KEYRING_BACKEND"]);
                Check("job-role", "member: aiwb_secret and member_site first on PYTHONPATH",
                    @"C:\lib\secret-prompt" + Path.PathSeparator + @"C:\lib\secret-prompt\member_site" + Path.PathSeparator + @"C:\old",
                    member.EnvironmentVariables["PYTHONPATH"]);
                Environment.SetEnvironmentVariable("AIWB_READ_GUARD", Path.Combine(Path.GetTempPath(), "aiwb-test-read-guard-role.py"));
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "aiwb-test-read-guard-role.py"), "# test stub");
                string ownerSettings = File.ReadAllText(SubagentHookFiles.Ensure(null, true, false));
                string memberSettings = File.ReadAllText(SubagentHookFiles.Ensure(null, true, true));
                Check("job-role", "owner: PreToolUse as in 1.17 (no Grep/Glob)", "False", ownerSettings.Contains("Grep|Glob").ToString());
                Check("job-role", "member: PreToolUse covers Grep/Glob", "True", memberSettings.Contains("NotebookEdit|Grep|Glob|mcp__.*").ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIWB_SECRET_DIR", saved);
                Environment.SetEnvironmentVariable("AIWB_READ_GUARD", "off");
                try { File.Delete(Path.Combine(Path.GetTempPath(), "aiwb-test-read-guard-role.py")); } catch (IOException) { }
            }
        }

        static void TestRegistry()
        {
            string[] known;
            try { known = AgentRuntime.KnownProtocols() ?? new string[0]; }
            catch (Exception ex) { known = new string[0]; Check("registry", "KnownProtocols()", "ok", "throws " + ex.GetType().Name); }

            foreach (string p in new[] { "raw", "claude-stream-json", "codex-jsonl" })
                Check("registry", "KnownProtocols contains " + p, "True", known.Contains(p).ToString());

            string proto;
            try
            {
                var rt = AgentRuntime.Get(null);
                proto = rt == null ? "<null runtime>" : rt.Protocol;
            }
            catch (Exception ex) { proto = "throws " + ex.GetType().Name; }
            Check("registry", "Get(null).Protocol", "raw", proto);

            string isKnown;
            try { isKnown = AgentRuntime.IsKnown("nope").ToString(); }
            catch (Exception ex) { isKnown = "throws " + ex.GetType().Name; }
            Check("registry", "IsKnown(\"nope\")", "False", isKnown);

            string thrown;
            try { AgentRuntime.Get("nope"); thrown = "no throw"; }
            catch (InvalidOperationException) { thrown = "throws"; }
            catch (Exception ex) { thrown = "throws " + ex.GetType().Name; }
            Check("registry", "Get(\"nope\") throws InvalidOperationException", "throws", thrown);
        }
    }
}
