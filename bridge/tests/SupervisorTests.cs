// SPDX-License-Identifier: AGPL-3.0-or-later
// v1.9.0: supervisor helpers — error classes (no restart for network/server outages), restart counters kept on
// disk, the config section, and the live-process count of a Windows Job Object (stuck-job check).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Web.Script.Serialization;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        static void TestSupervisor(List<string> configPaths)
        {
            const string g = "supervisor";
            Check(g, "dns", "1", BridgeApplicationContext.SupErrorClass(
                new WebException("The remote name could not be resolved: 'co.example.org'", WebExceptionStatus.NameResolutionFailure)).ToString());
            Check(g, "timeout", "1", BridgeApplicationContext.SupErrorClass(
                new WebException("The operation has timed out", WebExceptionStatus.Timeout)).ToString());
            Check(g, "http 503", "2", BridgeApplicationContext.SupErrorClass(
                new InvalidOperationException("HTTP 503 Service Unavailable")).ToString());
            Check(g, "http 403", "0", BridgeApplicationContext.SupErrorClass(
                new InvalidOperationException("HTTP 403 {\"error\":\"forbidden\"}")).ToString());
            Check(g, "no key", "0", BridgeApplicationContext.SupErrorClass(
                new InvalidOperationException("no key in Credential Manager (aiwb/co-push)")).ToString());
            Check(g, "bad answer", "0", BridgeApplicationContext.SupErrorClass(
                new InvalidOperationException("unexpected answer")).ToString());

            // v1.16.0: update drain -> pause after updateDrainMinutes, continuation of the paused job
            Check(g, "launcher drain is an update", "True", BridgeApplicationContext.IsUpdateDrain("1.16.0 2026-11-12T11:23:02.0393955Z").ToString());
            Check(g, "supervisor drain is not", "False", BridgeApplicationContext.IsUpdateDrain("1.15.0 2026-11-12T11:23:02Z supervisor restart claim").ToString());
            Check(g, "handover drain is not", "False", BridgeApplicationContext.IsUpdateDrain("1.15.0 2026-11-12T11:23:02Z supervisor handover").ToString());
            Check(g, "no drain text", "False", BridgeApplicationContext.IsUpdateDrain(null).ToString());
            DateTime d0 = new DateTime(2026, 11, 12, 11, 23, 0, DateTimeKind.Utc);
            Check(g, "pause default 30 min: 29", "False", BridgeApplicationContext.UpdatePauseDue(d0, d0.AddMinutes(29), 0).ToString());
            Check(g, "pause default 30 min: 30", "True", BridgeApplicationContext.UpdatePauseDue(d0, d0.AddMinutes(30), 0).ToString());
            Check(g, "pause configured 5", "True", BridgeApplicationContext.UpdatePauseDue(d0, d0.AddMinutes(5), 5).ToString());
            Check(g, "pause off (< 0)", "False", BridgeApplicationContext.UpdatePauseDue(d0, d0.AddHours(5), -1).ToString());
            Check(g, "pause without drain", "False", BridgeApplicationContext.UpdatePauseDue(DateTime.MinValue, d0, 0).ToString());
            Dictionary<string, object> orig = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                "{\"jobId\":\"JOB-20261112111721-6GGP0E\",\"chatId\":\"CHAT-1\",\"instruction\":\"write the report\"," +
                "\"conversation\":{\"operation\":\"new\",\"sessionId\":null},\"attachments\":[{\"path\":\"a.docx\"}]," +
                "\"task\":{\"parentChatId\":\"CHAT-0\"},\"timeoutMinutes\":90}");
            Dictionary<string, object> cont = BridgeApplicationContext.BuildUpdateContinuation(orig, "JOB-NEW", "sess-1", true, "1.16.0");
            Dictionary<string, object> cconv = (Dictionary<string, object>)cont["conversation"];
            Check(g, "continuation id", "JOB-NEW", (string)cont["jobId"]);
            Check(g, "continuation resumes", "resume sess-1", cconv["operation"] + " " + cconv["sessionId"]);
            Check(g, "continuation prompt", "True", ((string)cont["instruction"]).StartsWith("[Bridge update]") && ((string)cont["instruction"]).Contains("1.16.0") ? "True" : "False");
            Check(g, "continuation no attachments", "0", ((object[])cont["attachments"]).Length.ToString());
            Check(g, "continuation keeps fields", "90 True", cont["timeoutMinutes"] + " " + cont.ContainsKey("task"));
            Check(g, "continuation taint", "True", cont["tainted"].ToString());
            Check(g, "continuation of", "JOB-20261112111721-6GGP0E", (string)cont["updateContinuationOf"]);
            Check(g, "original untouched", "JOB-20261112111721-6GGP0E new", orig["jobId"] + " " + ((Dictionary<string, object>)orig["conversation"])["operation"]);
            Dictionary<string, object> again = BridgeApplicationContext.BuildUpdateContinuation(orig, "JOB-NEW2", null, false, null);
            Check(g, "not started: same request again", "write the report new False",
                again["instruction"] + " " + ((Dictionary<string, object>)again["conversation"])["operation"] + " " + again.ContainsKey("tainted"));
            Check(g, "pause message", "Paused for the Bridge update to 1.16.0: the job was stopped and continues as job JOB-NEW in this chat after the restart.",
                BridgeApplicationContext.UpdatePauseMessage("1.16.0", "JOB-NEW"));
            Check(g, "pause message without continuation", "True",
                BridgeApplicationContext.UpdatePauseMessage("", null).StartsWith(BridgeApplicationContext.UpdatePauseText + ": the job was stopped and could not") ? "True" : "False");
            cont["agent"] = "claude";
            Check(g, "continuation session key", "claude:sess-1", BridgeApplicationContext.ContinuationSessionKey(cont));
            Check(g, "new session has no key", "True", (BridgeApplicationContext.ContinuationSessionKey(again) == null).ToString());
            Check(g, "no conversation no key", "True", (BridgeApplicationContext.ContinuationSessionKey(new Dictionary<string, object>()) == null).ToString());
            SupervisorConfig supc =new JavaScriptSerializer().Deserialize<SupervisorConfig>("{\"updateDrainMinutes\":-1}");
            Check(g, "config updateDrainMinutes", "-1", supc.updateDrainMinutes.ToString());

            // Freedom F1: site and owner come from the config, never from constants
            BridgeConfig sc = new JavaScriptSerializer().Deserialize<BridgeConfig>(
                "{\"dbQueue\":{\"enabled\":true,\"url\":\"https://co.example.org/agent/db.php\"},\"push\":{\"enabled\":true}," +
                "\"chatProjectEnvironments\":{\"anna\":{},\"boris\":{}}}");
            BridgeApplicationContext.NormalizeSite(sc);
            Check(g, "site from dbQueue.url", "https://co.example.org", BridgeApplicationContext.SiteBase(sc));
            Check(g, "push url from site", "https://co.example.org/agent/push.php", sc.push.url);
            Check(g, "owner = first environment", "anna", sc.push.ownerEnv);
            BridgeConfig s2 = new JavaScriptSerializer().Deserialize<BridgeConfig>(
                "{\"siteUrl\":\"https://co.other.net/\",\"dbQueue\":{\"enabled\":true},\"push\":{\"enabled\":true,\"ownerEnv\":\"boris\"}}");
            BridgeApplicationContext.NormalizeSite(s2);
            Check(g, "siteUrl wins, slash trimmed", "https://co.other.net/agent/db.php", s2.dbQueue.url);
            Check(g, "explicit owner kept", "boris", BridgeApplicationContext.OwnerEnvOf(s2));
            Check(g, "no site, no url", "", BridgeApplicationContext.SiteBase(new BridgeConfig()));

            DateTime now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            List<string> list = new List<string>();
            list = BridgeApplicationContext.SupAppend(list, now.AddDays(-3));      // dropped by the next append
            list = BridgeApplicationContext.SupAppend(list, now.AddMinutes(-90));
            list = BridgeApplicationContext.SupAppend(list, now.AddMinutes(-20));
            list = BridgeApplicationContext.SupAppend(list, now.AddMinutes(-5));
            Check(g, "append keeps 2 days", "3", list.Count.ToString());
            Check(g, "count 1 h", "2", BridgeApplicationContext.SupCount(list, now.AddHours(-1)).ToString());
            Check(g, "count 24 h", "3", BridgeApplicationContext.SupCount(list, now.AddDays(-1)).ToString());
            SupervisorState st = new JavaScriptSerializer().Deserialize<SupervisorState>(
                new JavaScriptSerializer().Serialize(new SupervisorState { restartsUtc = list }));
            Check(g, "state roundtrip", "2", BridgeApplicationContext.SupCount(st.restartsUtc, now.AddHours(-1)).ToString());

            foreach (string path in configPaths)
            {
                try
                {
                    BridgeConfig c = new JavaScriptSerializer().Deserialize<BridgeConfig>(File.ReadAllText(path));
                    Check(g, Path.GetFileName(path) + " supervisor section", "True",
                        (c.supervisor != null && c.supervisor.enabled == true && c.supervisor.restartMode == "dry" &&
                         c.supervisor.maxRestartsPerHour == 2 && c.supervisor.claimFailures == 5).ToString());
                }
                catch (Exception ex)
                {
                    Check(g, Path.GetFileName(path) + " parse", "ok", ex.Message);
                }
            }

            using (WindowsJobObject jo = new WindowsJobObject())
            {
                Check(g, "job object empty", "0", jo.ActiveProcesses().ToString());
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1");
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())   // .NET 8 on macOS/Linux: the process tree adapter
                    psi = new ProcessStartInfo("/bin/sleep", "30");
#endif
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    jo.Assign(p);
                    Check(g, "job object one process", "1", jo.ActiveProcesses().ToString());
                    jo.Terminate(4);
                    p.WaitForExit(5000);
                    System.Threading.Thread.Sleep(200);
                    Check(g, "job object after terminate", "0", jo.ActiveProcesses().ToString());
                }
            }
        }
    }
}
