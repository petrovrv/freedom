// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F1 step 5b (stage B): the corporate connector manifest drives tool kinds, agents, push texts and the blocked
// marker; a fake device heartbeat in the old (engie / engieNum) and the new (corp {id, state, num}) form.
using System;
using System.Collections.Generic;
using System.IO;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        static void TestCorporate()
        {
            const string g = "corporate";
            Corporate.Set("{\"id\":\"acme\",\"title\":\"Acme\",\"portalTitle\":\"acme.ai\",\"signinTitle\":\"Acme Verify\","
                + "\"domains\":[\"acme.example\"],\"usageSource\":\"acme-savings\","
                + "\"agents\":[{\"id\":\"acmebot\",\"kind\":\"corporate-llm\"},{\"id\":\"acmedisp\",\"kind\":\"corporate-dispatcher\"}],"
                + "\"tools\":[{\"name\":\"acme_chat\",\"kind\":\"corporate-llm\"},{\"name\":\"acme_fetch\",\"kind\":\"corporate-fetch\"}]}");
            Check(g, "gateway tool", "acme_chat", Corporate.ToolOf(Corporate.LlmKind) ?? "");
            Check(g, "tool of any kind", "True/True/False", Corporate.IsTool("acme_fetch", null) + "/" + Corporate.IsTool("acme_chat", Corporate.LlmKind) + "/" + Corporate.IsTool("browser_read", null));
            Check(g, "dispatcher from the manifest", "acmedisp/True/False", Corporate.DispatcherId + "/" + Corporate.IsDispatcher("acmedisp") + "/" + Corporate.IsDispatcher("claude"));
            Check(g, "gateway agent", "True/False", Corporate.IsLlmAgent("acmebot") + "/" + Corporate.IsLlmAgent("acmedisp"));
            Check(g, "domains", "acme.example", String.Join(",", Corporate.Domains));
            Check(g, "usage source", "acme-savings", Corporate.UsageSource);
            Check(g, "runtime kinds", "True/True/False", Corporate.IsGatewayRuntime("corporate") + "/" + Corporate.IsGatewayRuntime("engie") + "/" + Corporate.IsGatewayRuntime(""));
            Check(g, "ledger kinds", "True/True/False", Corporate.IsLedgerKind("corporate") + "/" + Corporate.IsLedgerKind("engie") + "/" + Corporate.IsLedgerKind("claude"));

            bool found;
            Check(g, "new marker", "done/True", BridgeApplicationContext.StripBlockedMarker("done\n[[corporate-blocked]]", out found) + "/" + found);
            Check(g, "legacy marker", "done/True", BridgeApplicationContext.StripBlockedMarker("done [[engie-blocked]]  ", out found) + "/" + found);
            Check(g, "no marker", "plain/False", BridgeApplicationContext.StripBlockedMarker("plain", out found) + "/" + found);

            string id, state, num;
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"version\":\"0.11.0\",\"engie\":\"SignIn\",\"engieNum\":\"x1:42\"}"), out id, out state, out num);
            Check(g, "old heartbeat", "/signin/x1:42", id + "/" + state + "/" + num);
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"corp\":{\"id\":\"acme\",\"state\":\"online\",\"num\":\"\"},\"engie\":\"offline\"}"), out id, out state, out num);
            Check(g, "new heartbeat wins", "acme/online/", id + "/" + state + "/" + num);
            BridgeApplicationContext.ReadCorpHeartbeat(Obj("{\"version\":\"0.5.0\"}"), out id, out state, out num);
            Check(g, "heartbeat without a connector", "//", id + "/" + state + "/" + num);

            string[] t = BridgeApplicationContext.SigninNeededText("Laptop");
            Check(g, "sign-in push", "acme.ai: sign-in needed|acme.ai on Laptop needs a sign-in (Acme Verify). Acme models are unavailable to agents until then.", t[0] + "|" + t[1]);
            t = BridgeApplicationContext.SigninNumberText("Laptop", "42");
            Check(g, "number push", "acme.ai: tap 42 in Acme Verify", t[0]);

            Corporate.Set(null); // an install without the connector
            Check(g, "neutral: no tools", "<none>/False/<none>", (Corporate.ToolOf(Corporate.LlmKind) ?? "<none>") + "/" + Corporate.IsTool("acme_chat", null) + "/" + (Corporate.DispatcherId ?? "<none>"));
            Check(g, "neutral texts", "Corporate/the corporate portal/0", Corporate.Title + "/" + Corporate.PortalTitle + "/" + Corporate.Domains.Length);
            t = BridgeApplicationContext.SigninNumberText("PC", "7");
            Check(g, "neutral number push", "the corporate portal: tap 7 in the authenticator app", t[0]);

            // the repository manifest of this install parses and names a gateway tool and a dispatcher
            string repo = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\connectors\corporate\manifest.json"));
            if (File.Exists(repo))
            {
                Corporate.Set(File.ReadAllText(repo));
                Check(g, "repo manifest", "True/True", (Corporate.ToolOf(Corporate.LlmKind) != null) + "/" + (Corporate.DispatcherId != null));
            }
            Corporate.Set(null);
        }
    }
}
