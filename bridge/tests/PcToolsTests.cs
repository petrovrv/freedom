// SPDX-License-Identifier: AGPL-3.0-or-later
// v1.15.0: pc_* tools of the relay device h-<host> - who may call what (owner, family sandbox, no tool library for the
// family), paths outside /home/<user>, variables that may not be set, output cut, timeout and kill of real processes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        static void TestPcTools()
        {
            const string g = "pc-tools";
            PcBox boris = new PcBox();
            boris.env = "boris"; boris.user = "boris"; boris.distro = "aiwb-family";
            Dictionary<string, object> refusal;
            List<string> none = new List<string>();
            const string home = "h-home-main";

            // ---- rights
            Check(g, "owner pc_shell", "pc-owner/", PcTools.Route(home, home, "pc_shell", "anna", "anna", null, none, out refusal) + "/" + Code(refusal));
            Check(g, "owner tool_run", "library/", PcTools.Route(home, home, "tool_run", "anna", "anna", null, none, out refusal) + "/" + Code(refusal));
            Check(g, "child on the owner's device: pc_shell", "not_allowed", Route(home, "pc_shell", "boris", null));
            Check(g, "child on the owner's device: tool_run", "not_allowed", Route(home, "tool_run", "boris", null));
            Check(g, "child in own sandbox: pc_read", "pc-family/", PcTools.Route(home + "-boris", home, "pc_read", "boris", "anna", boris, none, out refusal) + "/" + Code(refusal));
            Check(g, "child in own sandbox: tool_run refused", "not_allowed", Route(home + "-boris", "tool_run", "boris", boris));
            Check(g, "child in own sandbox: tool_list refused", "not_allowed", Route(home + "-boris", "tool_list", "boris", boris));
            Check(g, "other child in the sandbox", "not_allowed", Route(home + "-boris", "pc_shell", "cora", boris));
            Check(g, "owner in a child's sandbox", "not_allowed", Route(home + "-boris", "pc_shell", "anna", boris));
            Check(g, "unknown sandbox device", "not_allowed", Route(home + "-boris", "pc_shell", "boris", null));
            Check(g, "deny list", "tool_off", Code(Refusal(PcTools.Route(home, home, "pc_shell", "anna", "anna", null, new List<string> { "pc_shell" }, out refusal), refusal)));
            Check(g, "paired device untouched", "device", PcTools.Route("d-0123456789abcdef", home, "fs_read", "boris", "anna", null, none, out refusal));
            Check(g, "pc tool names", "True/False/False", PcTools.IsPcTool("pc_edit") + "/" + PcTools.IsPcTool("tool_run") + "/" + PcTools.IsPcTool("pc_"));

            // ---- variables
            string why;
            Check(g, "env ok", "1", Count(PcTools.EnvVars(Obj("{\"env\":{\"FOO\":\"1\"}}"), out why)));
            foreach (string bad in new string[] { "AIWB_READ_GUARD", "aiwb_chat_id", "CO_LIVE_BROWSER", "PATH", "LD_PRELOAD", "BASH_ENV", "A B" })
                Check(g, "env " + bad + " refused", "refused", PcTools.EnvVars(Obj("{\"env\":{" + JsonUtil.Serialize(bad) + ":\"1\"}}"), out why) == null ? "refused" : "taken");
            Check(g, "env non-string refused", "refused", PcTools.EnvVars(Obj("{\"env\":{\"N\":1}}"), out why) == null ? "refused" : "taken");

            // ---- paths
            Check(g, "home itself", "True", PcTools.InHome("/home/boris", "/home/boris").ToString());
            Check(g, "inside home", "True", PcTools.InHome("/home/boris/src/a.c", "/home/boris").ToString());
            Check(g, "neighbour prefix", "False", PcTools.InHome("/home/boris2/x", "/home/boris").ToString());
            Check(g, "other home", "False", PcTools.InHome("/home/cora", "/home/boris").ToString());
            Check(g, "windows drive", "False", PcTools.InHome("/mnt/c/Windows", "/home/boris").ToString());
            Check(g, "empty", "False", PcTools.InHome("", "/home/boris").ToString());
            Check(g, "linux user root refused", "False", PcTools.ValidLinuxUser("root").ToString());
            Check(g, "linux user with space refused", "False", PcTools.ValidLinuxUser("so fia").ToString());
            Check(g, "owner path relative refused", "null", PcTools.OwnerPath("src\\a.cs", out why) ?? "null");
            Check(g, "owner path device refused", "null", PcTools.OwnerPath("\\\\.\\PhysicalDrive0", out why) ?? "null");
            Check(g, "owner path git-bash form", @"C:\AIWorkbench\x.txt", PcTools.OwnerPath("/c/AIWorkbench/x.txt", out why) ?? "null");
            Check(g, "sh quote", "'a'\\''b'", PcTools.ShQuote("a'b"));

            // realpath check of the sandbox prelude, run by Git Bash (same realpath -m as in the distro)
            string bash = @"C:\Program Files\Git\bin\bash.exe";
            if (File.Exists(bash))
            {
                string pre = PcTools.FamilyPrelude(boris);
                Check(g, "realpath: relative", "/home/boris/docs/a.txt", Bash(bash, pre + "aiwb_in docs/a.txt"));
                Check(g, "realpath: dots inside", "/home/boris/c", Bash(bash, pre + "aiwb_in /home/boris/./b/../c"));
                Check(g, "realpath: escape by ..", "OUT", Bash(bash, pre + "aiwb_in /home/boris/../cora/x || echo OUT"));
                Check(g, "realpath: relative escape", "OUT", Bash(bash, pre + "aiwb_in ../../mnt/c || echo OUT"));
                Check(g, "realpath: /mnt", "OUT", Bash(bash, pre + "aiwb_in /mnt/c/Windows || echo OUT"));
                Check(g, "realpath: quote in name", "/home/boris/it's", Bash(bash, pre + "aiwb_in " + PcTools.ShQuote("it's")));
            }
            if (File.Exists(bash))
            {
                // the command line WslArguments builds, run by Git Bash through cmd.exe: process substitution, no quoting
                string src = "x='a b'; echo \"$x|\" $((6*7)) %PATH% \\\\ end";
                string wa = PcTools.WslArguments(boris, src);
                string tail = wa.Substring(wa.IndexOf("--norc -c ") + 10);
                int ex2; string er2;
                byte[] o2 = PcExec.Capture(PcExec.WithStdin(bash, "--noprofile --norc -c " + tail, null), 30, out ex2, out er2);
                Check(g, "sandbox command line through cmd", "a b| 42 %PATH% \\ end", o2 == null ? er2 : Encoding.UTF8.GetString(o2).Trim());
            }
            string script = PcTools.FamilyShellScript(boris, "pc-0123456789ab", "src", new Dictionary<string, string> { { "CC", "gcc '-O2'" } }, "make && ./a.out");
            Check(g, "sandbox script: cwd checked", "True", script.Contains("aiwb_in 'src'").ToString());
            Check(g, "sandbox script: variable quoted", "True", script.Contains("export CC='gcc '\\''-O2'\\'''").ToString());
            string wargs = PcTools.WslArguments(boris, script);
            Check(g, "wsl args: distro and user", "True", wargs.StartsWith("-d aiwb-family -u boris --cd ~ -- bash --noprofile --norc -c ").ToString());
            int b0 = wargs.IndexOf("<<<") + 3;
            string b64 = wargs.Substring(b0, wargs.IndexOf(')', b0) - b0);
            Check(g, "wsl args: no percent sign", "False", wargs.Contains("%").ToString());
            Check(g, "wsl args: script round trip", "True", (Encoding.UTF8.GetString(Convert.FromBase64String(b64)) == script).ToString());
            Check(g, "kill script uses the pid file", "True", PcTools.FamilyKillScript(boris, "pc-1").Contains("/home/boris/.aiwb-pc/pc-1.pid").ToString());

            // ---- schemas
            object[] own = PcTools.Schemas(null);
            object[] fam = PcTools.Schemas(boris);
            Check(g, "owner schemas", "10", own.Length.ToString());
            Check(g, "family schemas", "10", fam.Length.ToString());
            Check(g, "family pc_shell has no shell choice", "False", JsonUtil.Serialize(fam[0]).Contains("\"shell\"").ToString());
            Check(g, "schemas fit relay_devices_put", "True", (JsonUtil.Serialize(own).Length < 200000).ToString());

            // ---- text helpers
            string cut = PcTools.Cut(new string('a', 5000) + new string('z', 5000), 1000, @"C:\log.txt");
            Check(g, "cut keeps head and tail", "True/True/True", cut.StartsWith("aaaa") + "/" + cut.EndsWith("zzzz") + "/" + cut.Contains("9000 characters cut; full output: C:\\log.txt"));
            long total; bool more;
            string nl = PcTools.NumberLines("a\r\nb\nc\n", 2, 1, out total, out more);
            Check(g, "numbered lines", "2\tb\n/3/True", nl + "/" + total + "/" + more);
            int count;
            Check(g, "edit unique", "x-Y-z", PcTools.Replace("x-y-z", "y", "Y", false, out count, out why));
            Check(g, "edit ambiguous refused", "null", PcTools.Replace("y y", "y", "Y", false, out count, out why) ?? "null");
            Check(g, "edit replace_all", "Y Y/2", PcTools.Replace("y y", "y", "Y", true, out count, out why) + "/" + count);
            Check(g, "edit not found", "null", PcTools.Replace("abc", "q", "Y", false, out count, out why) ?? "null");

            // ---- real processes: output cut, timeout, kill
            string dir = Path.Combine(Path.GetTempPath(), "aiwb-pc-tests");
            Directory.CreateDirectory(dir);
            PcRun r = NewRun(dir, "big", 60);
            PcExec.Start(r, PcExec.WithStdin(PcExec.SystemExe(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -Command \"[Console]::Out.Write('A' * 50000 + 'END'); exit 3\"", null), 2000);
            PcExec.Wait(r, null);
            string text = PcExec.Text(r, 2000);
            Check(g, "exit code", "True", text.StartsWith("exit code 3").ToString());
            Check(g, "stdout cut with log path", "True/True", text.Contains("characters cut; full output: " + r.outLog) + "/" + text.Contains("AEND"));
            Check(g, "stdout cut short", "True", (text.Length < 2600).ToString());
            Check(g, "full log kept", "50003", new FileInfo(r.outLog).Length.ToString());

            r = NewRun(dir, "stdin", 60);
            string stdinFile = PcExec.StdinFile(dir, Encoding.UTF8.GetBytes("hello"));
            PcExec.Start(r, PcExec.WithStdin(@"C:\Program Files\Git\bin\bash.exe", "--noprofile --norc -c \"wc -c\"", stdinFile), 2000);
            PcExec.Wait(r, null);
            Check(g, "stdin passed without BOM", "5", PcExec.Text(r, 2000).Replace("--- stdout ---", "|").Split('|')[1].Trim());

            r = NewRun(dir, "timeout", 2);
            Stopwatch sw = Stopwatch.StartNew();
            PcExec.Start(r, PcExec.WithStdin(PcExec.SystemExe(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"", null), 2000);
            PcExec.Wait(r, null);
            Check(g, "timeout kills", "True/True", (sw.Elapsed.TotalSeconds < 20) + "/" + (r.killReason == "timeout after 2 s"));

            r = NewRun(dir, "kill", 120);
            PcExec.Start(r, PcExec.WithStdin(PcExec.SystemExe("ping.exe"), "-n 60 127.0.0.1", null), 2000);
            PcRun bg = r;
            System.Threading.Thread w = new System.Threading.Thread(delegate() { PcExec.Wait(bg, null); });
            w.Start();
            System.Threading.Thread.Sleep(1000);
            sw = Stopwatch.StartNew();
            PcExec.Kill(r, null);
            bool exited = r.exited.WaitOne(20000);
            Check(g, "kill stops the tree", "True/True/True", exited + "/" + r.killed + "/" + (sw.Elapsed.TotalSeconds < 20));
        }

        static string Route(string device, string tool, string env, PcBox box)
        {
            Dictionary<string, object> refusal;
            PcTools.Route(device, "h-home-main", tool, env, "anna", box, new List<string>(), out refusal);
            return refusal == null ? "allowed" : Code(refusal);
        }

        static Dictionary<string, object> Refusal(string route, Dictionary<string, object> refusal)
        {
            return refusal;
        }

        static string Count(Dictionary<string, string> d)
        {
            return d == null ? "null" : d.Count.ToString();
        }

        static PcRun NewRun(string dir, string name, int timeoutSec)
        {
            PcRun r = new PcRun();
            r.id = "pc-test-" + name;
            r.outLog = Path.Combine(dir, name + ".out.log");
            r.errLog = Path.Combine(dir, name + ".err.log");
            r.timeoutSec = timeoutSec;
            return r;
        }

        static string Bash(string bash, string script)
        {
            string f = PcExec.StdinFile(Path.Combine(Path.GetTempPath(), "aiwb-pc-tests"), new UTF8Encoding(false).GetBytes(script.Replace("\r\n", "\n") + "\n"));
            int exit;
            string err;
            byte[] o = PcExec.Capture(PcExec.WithStdin(bash, "--noprofile --norc -s", f), 30, out exit, out err);
            PcExec.DeleteQuietly(f);
            return o == null ? "(no answer)" : Encoding.UTF8.GetString(o).Trim();
        }
    }
}
