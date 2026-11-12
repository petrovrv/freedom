// SPDX-License-Identifier: AGPL-3.0-or-later
// v1.12.0 (S1): the home PC's tool library as relay device h-<host> - which tool_run calls the site may make
// (allow-list, no --confirm, forced cwd, timeout clamp) and how the toolbox MCP answer becomes a device reply.
using System;
using System.Collections.Generic;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        static void TestRelay()
        {
            const string g = "relay";
            HashSet<string> allowed = new HashSet<string>(new string[] { "browser-agent", "linkedin-web" });
            int timeout;
            Dictionary<string, object> refusal;

            Dictionary<string, object> a = Obj("{\"name\":\"browser-agent\",\"args\":[\"open\",\"https://www.linkedin.com/\"],\"timeout_sec\":5000,\"cwd\":\"C:\\\\Windows\"}");
            Dictionary<string, object> c = BridgeApplicationContext.RelayHomeArgs("tool_run", a, allowed, @"C:\relay\owner", out timeout, out refusal);
            Check(g, "run allowed", "ok", c != null && refusal == null ? "ok" : "refused");
            Check(g, "cwd forced", @"C:\relay\owner", c == null ? "" : (string)c["cwd"]);
            Check(g, "timeout clamped", "900", timeout.ToString());
            Check(g, "args kept", "2", c == null ? "" : ((List<string>)c["args"]).Count.ToString());

            c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"gmail-label\",\"args\":[]}"), allowed, "x", out timeout, out refusal);
            Check(g, "tool outside the list", "not_allowed", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"browser-agent\",\"args\":[\"click\",\"n12\",\"--confirm\"]}"), allowed, "x", out timeout, out refusal);
            Check(g, "--confirm refused", "confirm_refused", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"browser-agent\",\"args\":[\"--confirm=yes\"]}"), allowed, "x", out timeout, out refusal);
            Check(g, "--confirm= refused", "confirm_refused", Code(refusal));
            // R2 (review S1-01): argparse abbreviations of --confirm
            foreach (string abbr in new string[] { "--conf", "--c", "--co", "--confir", "--CONF", " --conf ", "--conf=1", "--confirm-clicks" })
            {
                c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"browser-agent\",\"args\":[\"click\",\"e12\"," + JsonUtil.Serialize(abbr) + "]}"), allowed, "x", out timeout, out refusal);
                Check(g, "abbreviation " + abbr.Trim() + " refused", "confirm_refused", Code(refusal));
            }
            foreach (string fine in new string[] { "--count", "--context", "--", "-c", "confirm", "--session" })
                Check(g, "not confirm: " + fine, "False", BridgeApplicationContext.RelayConfirmLike(fine).ToString());
            c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"browser-agent\",\"args\":\"open x\"}"), allowed, "x", out timeout, out refusal);
            Check(g, "string args refused", "bad_args", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("tool_run", Obj("{\"name\":\"browser-agent\",\"args\":[1]}"), allowed, "x", out timeout, out refusal);
            Check(g, "non-string arg refused", "bad_args", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("tool_help", Obj("{\"name\":\"../toolbox\"}"), allowed, "x", out timeout, out refusal);
            Check(g, "help outside the list", "not_allowed", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("shell_run", Obj("{}"), allowed, "x", out timeout, out refusal);
            Check(g, "unknown tool", "unknown_tool", Code(refusal));
            c = BridgeApplicationContext.RelayHomeArgs("tool_list", Obj("{\"query\":\"linkedin\"}"), allowed, "x", out timeout, out refusal);
            Check(g, "list query", "linkedin", c == null ? "" : (string)c["query"]);

            Dictionary<string, object> r = BridgeApplicationContext.RelayHomeReply("tool_list",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"browser-agent 0.15.2 - x\\ngmail-label 0.6.1 - y\\nlinkedin-web 0.6.0 - z\"}],\"isError\":false}}\n",
                "", allowed);
            Check(g, "list filtered", "browser-agent 0.15.2 - x\nlinkedin-web 0.6.0 - z", (string)r["content"]);
            r = BridgeApplicationContext.RelayHomeReply("tool_run",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"exit code 1\"}],\"isError\":true}}", "", allowed);
            Check(g, "run error", "False/True/tool_error", r["ok"] + "/" + r["untrusted"] + "/" + Code(r));
            r = BridgeApplicationContext.RelayHomeReply("tool_run", "", "Traceback", allowed);
            Check(g, "no answer", "tool_failed", Code(r));
        }

        static Dictionary<string, object> Obj(string json)
        {
            return JsonUtil.DeserializeObject(json) as Dictionary<string, object>;
        }

        static string Code(Dictionary<string, object> reply)
        {
            if (reply == null || !reply.ContainsKey("error")) return "";
            Dictionary<string, object> e = reply["error"] as Dictionary<string, object>;
            return e == null ? "" : (string)e["code"];
        }
    }
}
