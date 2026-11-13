// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10: the module loader (local folders + the server's switches; device tools of a disabled module refused,
// risk class, context fragments, services and hooks, personal switches) and the model gateway of a module (tool kinds,
// agents, push texts, blocked markers, heartbeat in the old and the new form).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        const string AcmeModule = "{\"schema\":1,\"id\":\"acme\",\"version\":\"0.3.0\",\"title\":\"Acme assistants\",\"scope\":\"environment\","
            + "\"permissions\":[\"device.tools\",\"context\",\"models.register\"],"
            + "\"provides\":{\"gateway\":{\"title\":\"Acme\",\"portalTitle\":\"acme.ai\",\"signinTitle\":\"Acme Verify\",\"domains\":[\"acme.example\"],"
            + "\"usageSource\":\"acme-savings\",\"blockedMarkers\":[\"[[acme-blocked]]\"]},"
            + "\"models\":[{\"id\":\"acmebot\",\"kind\":\"llm\"},{\"id\":\"acmedisp\",\"kind\":\"dispatcher\"},{\"id\":\"fast\",\"agent\":\"claude\",\"title\":\"Fast\"}],"
            + "\"deviceTools\":[{\"name\":\"acme_chat\",\"kind\":\"llm\",\"risk\":\"R\"},{\"name\":\"acme_fetch\",\"kind\":\"fetch\",\"risk\":\"R\"},"
            + "{\"name\":\"acme_mail_delete\",\"risk\":\"D\"},{\"name\":\"acme_note\"}],"
            + "\"context\":[{\"id\":\"rules\",\"text\":\"Acme data stays here.\"}],\"tools\":[{\"name\":\"acme-hr\"}]}}";

        const string HomeModule = "{\"schema\":1,\"id\":\"home\",\"version\":\"1.0.0\",\"title\":\"Home\",\"scope\":\"household\","
            + "\"permissions\":[\"context\",\"push.send\"],\"personal\":{\"about\":\"members\",\"default\":\"ask\"},"
            + "\"provides\":{\"services\":[{\"id\":\"receiver\",\"tool\":\"home-tool\",\"run\":\"serve --port 8093\",\"windowsTask\":\"\\\\AIWorkbench\\\\Home\"},"
            + "{\"id\":\"bad id\",\"tool\":\"x\",\"run\":\"y\"}],"
            + "\"hooks\":[{\"event\":\"job.after\",\"tool\":\"home-tool\",\"run\":\"after\"},{\"event\":\"job.before\",\"tool\":\"home-tool\",\"run\":\"before\"}],"
            + "\"context\":[{\"id\":\"c\",\"text\":\"Home rule.\"}],\"deviceTools\":[{\"name\":\"home_lamp\",\"risk\":\"W\"}]}}";

        static void TestModules()
        {
            const string g = "modules";
            string code;

            Modules.Set(new string[] { AcmeModule, HomeModule }, new Dictionary<string, bool> { { "acme", false }, { "home", true } });
            Modules.OwnerEnv = "owner";
            Check(g, "two modules", "acme,home", String.Join(",", Modules.All().ConvertAll(delegate(ModuleSpec m) { return m.Id; }).ToArray()));
            Check(g, "tool owner", "acme/home/<core>", Modules.ToolOwner("acme_chat").Id + "/" + Modules.ToolOwner("home_lamp").Id + "/" + (Modules.ToolOwner("fs_read") == null ? "<core>" : "?"));
            Check(g, "risk classes", "R/D/W/", Modules.ToolRisk("acme_fetch") + "/" + Modules.ToolRisk("acme_mail_delete") + "/" + Modules.ToolRisk("acme_note") + "/" + Modules.ToolRisk("fs_read"));

            // disabled module: its tools fail with a clear reason; core tools are untouched
            string why = Modules.Refusal("acme_chat", "owner", false, out code);
            Check(g, "disabled module refuses", "module_disabled", code ?? "");
            Check(g, "refusal names the module", "True", (why != null && why.Contains("Acme assistants") && why.Contains("disabled")).ToString());
            Check(g, "core tool passes", "<null>", Modules.Refusal("fs_read", "owner", true, out code) ?? "<null>");

            // personal module: only a person whose switch is "auto" gets it (default ask = not yet)
            Check(g, "personal default ask", "module_disabled", Refusal("home_lamp", "kid"));
            Modules.SetPeople("home", "kid", "auto");
            Check(g, "personal auto", "<ok>", Refusal("home_lamp", "kid"));
            Modules.SetPeople("home", "kid", "off");
            Check(g, "personal off", "module_disabled", Refusal("home_lamp", "kid"));
            Check(g, "no environment = the owner's own job", "<ok>", Refusal("home_lamp", ""));

            // enabled environment-scope module: the owner only; a "D" tool waits for a clean chat
            Modules.Set(new string[] { AcmeModule, HomeModule }, new Dictionary<string, bool> { { "acme", true }, { "home", true } });
            Check(g, "enabled for the owner", "<ok>", Refusal("acme_chat", "owner"));
            Check(g, "environment scope: other member", "module_disabled", Refusal("acme_chat", "member2"));
            Modules.Refusal("acme_mail_delete", "owner", true, out code);
            Check(g, "D tool in a tainted chat", "module_risk", code ?? "");
            Check(g, "D tool in a clean chat", "<ok>", Refusal("acme_mail_delete", "owner"));
            Check(g, "W tool in a tainted chat", "<null>", Modules.Refusal("acme_note", "owner", true, out code) ?? "<null>");

            // context: fragments of the modules that work for the environment and declare "context"
            Modules.SetPeople("home", "owner", "auto");
            Check(g, "context of the owner", "Acme data stays here.|Home rule.", Modules.Context("owner").Trim().Replace("\r\n", "\n").Replace("\n\n", "|"));
            Check(g, "context of another member", "", Modules.Context("member2").Trim());
            Check(g, "active summary", "acme 0.3.0, home 1.0.0", Modules.ActiveSummary("owner"));

            // services and hooks: only well-formed entries; hooks by event
            ModuleSpec home = Modules.Get("home");
            List<Dictionary<string, object>> svcs = Modules.Services(home);
            Check(g, "services", "1/receiver/serve --port 8093", svcs.Count + "/" + DeviceWire.S(svcs[0], "id") + "/" + DeviceWire.S(svcs[0], "run"));
            Check(g, "hooks by event", "1/1/0", Modules.Hooks("job.after", "owner").Count + "/" + Modules.Hooks("job.before", "owner").Count + "/" + Modules.Hooks("job.after", "member2").Count);
            Check(g, "library tools", "acme-hr", String.Join(",", Modules.LibraryTools(Modules.Get("acme")).ToArray()));
            Check(g, "registered resources: core agents only", "fast", String.Join(",", Modules.Resources(Modules.Get("acme")).ConvertAll(delegate(Dictionary<string, object> r) { return DeviceWire.S(r, "id"); }).ToArray()));
            ProcessStartInfo psi = BridgeApplicationContext.ModuleServiceStartInfo(home, svcs[0], @"C:\py\python.exe", @"C:\tools\home-tool\home-tool.py");
            Check(g, "service command line", "C:\\tools\\home-tool\\home-tool.py serve --port 8093|C:\\tools\\home-tool|home", psi.Arguments + "|" + psi.WorkingDirectory + "|" + psi.EnvironmentVariables["AIWB_MODULE"]);

            // schedules: kind schedule with cron or every, tool or home/ code; a schedule with a windowsTask stays with Windows
            ModuleSpec sched = Modules.Parse("{\"id\":\"sched\",\"version\":\"1\",\"provides\":{\"services\":["
                + "{\"id\":\"tick\",\"kind\":\"schedule\",\"cron\":\"0 7 * * *\",\"cwd\":\"home\",\"run\":\"python -m community tick\"},"
                + "{\"id\":\"idx\",\"kind\":\"schedule\",\"every\":\"PT1H\",\"tool\":\"aiwb-docs\",\"run\":\"index\"},"
                + "{\"id\":\"win\",\"kind\":\"schedule\",\"cron\":\"0 5 * * 0\",\"tool\":\"x-tool\",\"run\":\"run\",\"windowsTask\":\"AIWorkbench-X\"},"
                + "{\"id\":\"bad\",\"kind\":\"schedule\",\"every\":\"PT10S\",\"tool\":\"x-tool\",\"run\":\"run\"},"
                + "{\"id\":\"rx\",\"tool\":\"x-tool\",\"run\":\"serve\"}]}}", Path.GetTempPath());
            Check(g, "schedules Bridge runs", "tick,idx", String.Join(",", Modules.Schedules(sched).ConvertAll(delegate(Dictionary<string, object> x) { return DeviceWire.S(x, "id"); }).ToArray()));
            Check(g, "daemons are not schedules", "rx", String.Join(",", Modules.Services(sched).ConvertAll(delegate(Dictionary<string, object> x) { return DeviceWire.S(x, "id"); }).ToArray()));
            Check(g, "every", "01:00:00/1.00:00:00/<null>/<null>", Modules.Every("PT1H") + "/" + Modules.Every("P1D") + "/" + (Modules.Every("PT30S") == null ? "<null>" : "?") + "/" + (Modules.Every("1h") == null ? "<null>" : "?"));
            DateTime t0 = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
            Dictionary<string, object> idx = Modules.Schedules(sched)[1];
            Check(g, "every: due after the period", "False/True", Modules.DueUtc(idx, t0.AddMinutes(59), t0).HasValue + "/" + Modules.DueUtc(idx, t0.AddMinutes(60), t0).HasValue);
            Dictionary<string, object> hourly = Obj("{\"cron\":\"5 * * * *\"}");
            DateTime? due = Modules.DueUtc(hourly, t0.AddMinutes(30), t0);
            Check(g, "cron: due once per match", "True/False", (due.HasValue) + "/" + Modules.DueUtc(hourly, t0.AddMinutes(30), due ?? t0).HasValue);
            string homeDir = Path.Combine(Path.GetTempPath(), "home");
            Directory.CreateDirectory(homeDir);
            ProcessStartInfo sp = BridgeApplicationContext.ModuleScheduleStartInfo(sched, Modules.Schedules(sched)[0], @"C:\py\python.exe", @"C:\tools");
            Check(g, "home/ code schedule", "-m community tick|" + homeDir, sp == null ? "<null>" : sp.Arguments + "|" + sp.WorkingDirectory);

            // the local folders + the server's cached switches
            string root = Path.Combine(Path.GetTempPath(), "aiwb-modules-test-" + Guid.NewGuid().ToString("N"));
            string oldRoot = Environment.GetEnvironmentVariable("AIWB_MODULES_DIR");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "acme", "context"));
                File.WriteAllText(Path.Combine(root, "acme", "module.json"), AcmeModule.Replace("\"text\":\"Acme data stays here.\"", "\"file\":\"context/rules.md\""));
                File.WriteAllText(Path.Combine(root, "acme", "context", "rules.md"), "From the file.\n");
                Directory.CreateDirectory(Path.Combine(root, "wrong-id"));
                File.WriteAllText(Path.Combine(root, "wrong-id", "module.json"), HomeModule); // id does not match the folder
                Environment.SetEnvironmentVariable("AIWB_MODULES_DIR", root);
                Modules.Set(null, null);
                Modules.Reload();
                Check(g, "folder: one module, off until the server says", "acme/False", String.Join(",", Modules.All().ConvertAll(delegate(ModuleSpec m) { return m.Id + "/" + m.Enabled; }).ToArray()));
                Modules.ApplyServer(new object[] { Obj("{\"id\":\"acme\",\"enabled\":true,\"version\":\"0.3.0\"}"), Obj("{\"id\":\"ghost\",\"enabled\":true}") });
                Check(g, "server switch", "True", Modules.Get("acme").Enabled.ToString());
                Check(g, "context from a file", "From the file.", Modules.Context("owner").Trim());
                Modules.Reload();
                Check(g, "switch cached across a reload", "True", Modules.Get("acme").Enabled.ToString());
                Modules.ApplyServer(new object[0]);
                Check(g, "a module the server does not list is off", "module_disabled", Refusal("acme_chat", "owner"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIWB_MODULES_DIR", oldRoot);
                try { Directory.Delete(root, true); } catch { }
                Modules.Set(new string[0], null);
                Modules.OwnerEnv = "";
            }
        }

        static string Refusal(string tool, string env)
        {
            string code;
            string why = Modules.Refusal(tool, env, false, out code);
            return why == null ? "<ok>" : code;
        }

        static void TestGateway()
        {
            const string g = "gateway";
            Gateway.Set(AcmeModule);
            Check(g, "gateway tool", "acme_chat", Gateway.ToolOf(Gateway.LlmKind) ?? "");
            Check(g, "tool of any kind", "True/True/False/False", Gateway.IsTool("acme_fetch", null) + "/" + Gateway.IsTool("acme_chat", Gateway.LlmKind) + "/" + Gateway.IsTool("browser_read", null) + "/" + Gateway.IsTool("acme_fetch", Gateway.LlmKind));
            Check(g, "dispatcher from the module", "acmedisp/True/False", Gateway.DispatcherId + "/" + Gateway.IsDispatcher("acmedisp") + "/" + Gateway.IsDispatcher("claude"));
            Check(g, "gateway agent", "True/False/False", Gateway.IsLlmAgent("acmebot") + "/" + Gateway.IsLlmAgent("acmedisp") + "/" + Gateway.IsAgent("fast"));
            Check(g, "domains", "acme.example", String.Join(",", Gateway.Domains));
            Check(g, "usage source", "acme-savings", Gateway.UsageSource);
            Check(g, "runtime kinds (new + legacy)", "True/True/True/False", Gateway.IsGatewayRuntime("gateway") + "/" + Gateway.IsGatewayRuntime("corporate") + "/" + Gateway.IsGatewayRuntime("engie") + "/" + Gateway.IsGatewayRuntime(""));
            Check(g, "ledger kinds (new + legacy)", "True/True/True/False", Gateway.IsLedgerKind("gateway") + "/" + Gateway.IsLedgerKind("corporate") + "/" + Gateway.IsLedgerKind("engie") + "/" + Gateway.IsLedgerKind("claude"));

            bool found;
            Check(g, "new marker", "done/True", BridgeApplicationContext.StripBlockedMarker("done\n[[gateway-blocked]]", out found) + "/" + found);
            Check(g, "legacy marker 1", "done/True", BridgeApplicationContext.StripBlockedMarker("done\n[[corporate-blocked]]", out found) + "/" + found);
            Check(g, "legacy marker 2", "done/True", BridgeApplicationContext.StripBlockedMarker("done [[engie-blocked]]  ", out found) + "/" + found);
            Check(g, "module's own marker", "done/True", BridgeApplicationContext.StripBlockedMarker("done [[acme-blocked]]", out found) + "/" + found);
            Check(g, "no marker", "plain/False", BridgeApplicationContext.StripBlockedMarker("plain", out found) + "/" + found);

            string id, state, num;
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"version\":\"0.11.0\",\"engie\":\"SignIn\",\"engieNum\":\"x1:42\"}"), out id, out state, out num);
            Check(g, "old heartbeat", "/signin/x1:42", id + "/" + state + "/" + num);
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"corp\":{\"id\":\"acme\",\"state\":\"online\",\"num\":\"\"},\"engie\":\"offline\"}"), out id, out state, out num);
            Check(g, "new heartbeat wins", "acme/online/", id + "/" + state + "/" + num);
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"version\":\"0.5.0\"}"), out id, out state, out num);
            Check(g, "heartbeat without a gateway", "//", id + "/" + state + "/" + num);

            string[] t = BridgeApplicationContext.SigninNeededText("Laptop");
            Check(g, "sign-in push", "acme.ai: sign-in needed|acme.ai on Laptop needs a sign-in (Acme Verify). Acme models are unavailable to agents until then.", t[0] + "|" + t[1]);
            t = BridgeApplicationContext.SigninNumberText("Laptop", "42");
            Check(g, "number push", "acme.ai: tap 42 in Acme Verify", t[0]);

            // compatibility: the connector manifest of an install that has not installed the gateway module yet
            Gateway.SetLegacy("{\"title\":\"Acme\",\"portalTitle\":\"acme.ai\",\"domains\":[\"acme.example\"],"
                + "\"agents\":[{\"id\":\"acmebot\",\"kind\":\"x-llm\"},{\"id\":\"acmedisp\",\"kind\":\"x-dispatcher\"}],"
                + "\"tools\":[{\"name\":\"acme_chat\",\"kind\":\"x-llm\"},{\"name\":\"acme_fetch\",\"kind\":\"x-fetch\"}]}");
            Check(g, "legacy connector manifest", "acme_chat/acmedisp/True/acme.ai/acme.example", (Gateway.ToolOf(Gateway.LlmKind) ?? "") + "/" + Gateway.DispatcherId + "/" + Gateway.IsLlmAgent("acmebot") + "/" + Gateway.PortalTitle + "/" + String.Join(",", Gateway.Domains));
            Check(g, "legacy tools are not module tools", "<null>", Modules.ToolOwner("acme_chat") == null ? "<null>" : "module");

            Gateway.Set(null); // an install without a gateway module
            Check(g, "neutral: no tools", "<none>/False/<none>", (Gateway.ToolOf(Gateway.LlmKind) ?? "<none>") + "/" + Gateway.IsTool("acme_chat", null) + "/" + (Gateway.DispatcherId ?? "<none>"));
            Check(g, "neutral texts", "Employer/the employer portal/0", Gateway.Title + "/" + Gateway.PortalTitle + "/" + Gateway.Domains.Length);
            t = BridgeApplicationContext.SigninNumberText("PC", "7");
            Check(g, "neutral number push", "the employer portal: tap 7 in the authenticator app", t[0]);

            // the gateway module of the repository parses and names a gateway tool and a dispatcher
            string repo = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\modules\corporate\module.json"));
            if (File.Exists(repo))
            {
                Gateway.Set(File.ReadAllText(repo));
                Check(g, "repo module", "True/True/False", (Gateway.ToolOf(Gateway.LlmKind) != null) + "/" + (Gateway.DispatcherId != null) + "/" + (Gateway.ModuleId.Length == 0));
                Modules.Set(new string[] { File.ReadAllText(repo) }, null);
                string code;
                Modules.Refusal(Gateway.ToolOf(Gateway.LlmKind), "owner", false, out code);
                Check(g, "repo module disabled by default: tools refused", "module_disabled", code ?? "");
                Modules.Set(new string[0], null);
            }
            Gateway.Set(null);
        }
    }
}
