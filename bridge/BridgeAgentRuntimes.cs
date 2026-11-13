// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AiWorkbenchBridge
{
    // v1.4.0: common contract of the agent CLIs (command line, stream parsing, resume, devices, limits).
    // How to add a CLI agent:
    // 1. Subclass AgentRuntime (non-abstract, public parameterless ctor) with a unique Protocol string.
    // 2. Implement CreateParser: map the CLI's JSON lines to sessionId / finalResponse / usage / limits /
    //    success / errors (subclass AgentStreamParser, see BridgeAgentParsers.cs).
    // 3. Optionally override:
    //    - ContextFolderArguments, PermissionArguments, DefaultModelFillsModelArguments, SubagentArguments
    //    - BuildContinueArguments (resume/fork)
    //    - AddDeviceArguments (MCP servers of CO Desktop devices)
    //    - LimitsAgentId, ReadOfflineLimits
    // 4. The registry finds the class by reflection; no registration code is needed.
    // 5. In config.json set the agent's "executable", "arguments" (end with "-" when the prompt goes to stdin,
    //    with "promptToStdin": true), "outputProtocol": <Protocol>, "requireStructuredSuccess".
    // Example: a stream-json CLI such as Antigravity agy = one AgentRuntime subclass + its parser
    // + one agent entry in config.json with "outputProtocol" set to the new Protocol.

    internal abstract class AgentRuntime
    {
        private static readonly object RegistryLock = new object();
        private static Dictionary<string, AgentRuntime> registry;

        public abstract string Protocol { get; }

        private static Dictionary<string, AgentRuntime> Registry()
        {
            lock (RegistryLock)
            {
                if (registry == null)
                {
                    Dictionary<string, AgentRuntime> map = new Dictionary<string, AgentRuntime>(StringComparer.Ordinal);
                    Type[] types;
                    try { types = typeof(AgentRuntime).Assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(delegate(Type t) { return t != null; }).ToArray(); }
                    foreach (Type t in types)
                    {
                        if (!t.IsClass || t.IsAbstract || !t.IsSubclassOf(typeof(AgentRuntime))) continue;
                        ConstructorInfo ctor = t.GetConstructor(Type.EmptyTypes);
                        if (ctor == null || !ctor.IsPublic) continue;
                        AgentRuntime runtime = (AgentRuntime)ctor.Invoke(null);
                        string protocol = runtime.Protocol;
                        if (map.ContainsKey(protocol))
                            throw new InvalidOperationException("Duplicate outputProtocol '" + protocol + "': " + map[protocol].GetType().FullName + ", " + t.FullName);
                        map[protocol] = runtime;
                    }
                    registry = map;
                }
                return registry;
            }
        }

        public static AgentRuntime Get(string protocol)
        {
            string key = String.IsNullOrEmpty(protocol) ? "raw" : protocol;
            AgentRuntime runtime;
            if (Registry().TryGetValue(key, out runtime)) return runtime;
            throw new InvalidOperationException("Unsupported outputProtocol: " + protocol);
        }

        public static bool IsKnown(string protocol)
        {
            if (String.IsNullOrEmpty(protocol)) return false;
            return Registry().ContainsKey(protocol);
        }

        public static string[] KnownProtocols()
        {
            return Registry().Keys.OrderBy(delegate(string k) { return k; }, StringComparer.Ordinal).ToArray();
        }

        public static string ConversationOperation(JobRequest request)
        {
            return request.conversation == null || String.IsNullOrEmpty(request.conversation.operation) ? "new" : request.conversation.operation;
        }

        public virtual string LimitsAgentId(string agentId)
        {
            return agentId;
        }

        // Claude true; caller passes BridgeApplicationContext.CloudDeniedTools() only then, else null.
        public virtual bool UsesCloudDeniedTools
        {
            get { return false; }
        }

        // Builds the agent command line from local configuration only. The request
        // contributes validated ids (session id, catalog model/effort), never raw arguments.
        public string[] BuildArguments(AgentProfile agent, ModeProfile modeProfile, JobRequest request, List<string> cloudDenied)
        {
            string operation = ConversationOperation(request);
            List<string> selection = BuildSelectionArguments(agent, request, cloudDenied);
            string[] baseArguments = modeProfile != null && modeProfile.arguments != null && modeProfile.arguments.Length > 0
                ? modeProfile.arguments
                : (agent.arguments ?? new string[0]);
            string[] resumeExtra = modeProfile != null && modeProfile.resumeExtraArguments != null
                ? modeProfile.resumeExtraArguments
                : new string[0];
            if (operation == "new") return InsertBeforeStdinMarker(baseArguments, selection);
            return BuildContinueArguments(operation, baseArguments, resumeExtra, selection, request.conversation.sessionId);
        }

        protected virtual string[] BuildContinueArguments(string operation, string[] baseArguments, string[] resumeExtra, List<string> selection, string sessionId)
        {
            throw new InvalidOperationException("Agent protocol '" + Protocol + "' does not support conversation " + operation + ".");
        }

        public List<string> BuildSelectionArguments(AgentProfile agent, JobRequest request, List<string> cloudDenied)
        {
            List<string> args = new List<string>();
            if (request.resolvedContexts != null && request.resolvedContexts.Count > 0)
                args.AddRange(ContextFolderArguments(request.resolvedContexts));
            args.AddRange(PermissionArguments(request.permissions, cloudDenied));
            // v1.2.1: a Claude-based agent with a defaultModel (dispatcher) gets its modelArguments also when the user left "Default": they carry the gateway delegation policy.
            string model = request.model;
            if (String.IsNullOrEmpty(model) && DefaultModelFillsModelArguments) model = agent.defaultModel;
            if (!String.IsNullOrEmpty(model) && agent.modelArguments != null)
                foreach (string template in agent.modelArguments) args.Add(template.Replace("{value}", model));
            args.AddRange(SubagentArguments(agent, model, JobRole.Of(request.environmentId, Modules.OwnerEnv) == "member"));
            if (!String.IsNullOrEmpty(request.effort) && agent.effortArguments != null)
                foreach (string template in agent.effortArguments) args.Add(template.Replace("{value}", request.effort));
            return args;
        }

        protected virtual List<string> ContextFolderArguments(List<ResolvedContext> contexts)
        {
            return new List<string>();
        }

        protected virtual List<string> PermissionArguments(PermissionsRequest p, List<string> cloudDenied)
        {
            return new List<string>();
        }

        // v1.8.0: true when SubagentArguments install the read-guard hooks (the agent's tools then leave wrapping to them).
        public virtual bool HasReadGuardHooks
        {
            get { return false; }
        }

        protected virtual bool DefaultModelFillsModelArguments
        {
            get { return false; }
        }

        protected virtual List<string> SubagentArguments(AgentProfile agent, string model, bool member)
        {
            return new List<string>();
        }

        public virtual string[] AddDeviceArguments(string[] args, DeviceLaunch launch)
        {
            throw new InvalidOperationException("Agent protocol '" + Protocol + "' does not support device tools.");
        }

        public abstract AgentStreamParser CreateParser(AgentProfile agent, JobRequest request);

        // Exceptions propagate to the caller.
        public virtual AgentLimits ReadOfflineLimits(AgentProfile agent)
        {
            return null;
        }

        // "-" last = prompt from stdin (Codex); options precede it.
        public static string[] InsertBeforeStdinMarker(string[] baseArguments, List<string> extra)
        {
            List<string> args = new List<string>(baseArguments);
            if (extra.Count == 0) return args.ToArray();
            if (args.Count > 0 && args[args.Count - 1] == "-") args.InsertRange(args.Count - 1, extra);
            else args.AddRange(extra);
            return args.ToArray();
        }
    }

    internal sealed class DeviceLaunch
    {
        public string Exe;
        public List<DeviceServer> Servers;
        public string McpConfigPath;
        public string ApproveShortName; // null = no --permission-prompt-tool
        public int ToolTimeoutSec;
        public string Operation;

        public DeviceLaunch()
        {
            Servers = new List<DeviceServer>();
        }
    }

    internal sealed class DeviceServer
    {
        public string ShortName;
        public string[] ProxyArgs;
    }

    internal sealed class RawAgentRuntime : AgentRuntime
    {
        public override string Protocol
        {
            get { return "raw"; }
        }

        public override AgentStreamParser CreateParser(AgentProfile agent, JobRequest request)
        {
            return new RawStreamParser();
        }
    }

    internal sealed class ClaudeAgentRuntime : AgentRuntime
    {
        public override string Protocol
        {
            get { return "claude-stream-json"; }
        }

        // v1.1.6: every Claude-based agent (claude, dispatcher) reports the same subscription: one "claude" entry.
        public override string LimitsAgentId(string agentId)
        {
            return "claude";
        }

        public override bool UsesCloudDeniedTools
        {
            get { return true; }
        }

        protected override bool DefaultModelFillsModelArguments
        {
            get { return true; }
        }

        public override AgentStreamParser CreateParser(AgentProfile agent, JobRequest request)
        {
            return new ClaudeStreamParser();
        }

        protected override List<string> ContextFolderArguments(List<ResolvedContext> contexts)
        {
            List<string> args = new List<string>();
            foreach (ResolvedContext rc in contexts)
            {
                args.Add("--add-dir");
                args.Add(rc.fullPath);
            }
            return args;
        }

        // Since 1.1.2 Claude runs with --permission-mode bypassPermissions, so this list is the only gate on the Host side.
        // cloudDenied = tools of Claude account connectors the user did not tick (v1.1.6).
        protected override List<string> PermissionArguments(PermissionsRequest p, List<string> cloudDenied)
        {
            List<string> args = new List<string>();
            if (p == null)
            {
                // No chat permissions: only the connector boxes apply.
                if (cloudDenied == null || cloudDenied.Count == 0) return args;
                p = new PermissionsRequest();
            }
            bool readOnly = p.files == "read";
            bool shell = !p.shell.HasValue || p.shell.Value;
            bool web = !p.web.HasValue || p.web.Value;
            List<string> denied = new List<string>();
            if (readOnly) denied.AddRange(new string[] { "Write", "Edit", "MultiEdit", "NotebookEdit", "mcp__aiwb-tools__tool_run" }); // 1.3.4: tool library may write
            if (!shell) denied.AddRange(new string[] { "Bash", "PowerShell" });
            if (!web) denied.AddRange(new string[] { "WebSearch", "WebFetch" });
            if (cloudDenied != null) denied.AddRange(cloudDenied);
            if (denied.Count > 0)
            {
                args.Add("--disallowedTools");
                args.Add(String.Join(",", denied.ToArray()));
            }
            return args;
        }

        public override bool HasReadGuardHooks
        {
            get { return ReadGuard.ScriptPath != null; }
        }

        // v1.2.4: the agent's policy reaches its subagents through a SubagentStart hook (--settings file).
        // v1.8.0: the same settings file carries the read-guard hooks (PostToolUse/PreToolUse).
        // v1.18.0: member = a household member's job; its PreToolUse also covers Grep/Glob (own keyring per person).
        protected override List<string> SubagentArguments(AgentProfile agent, string model, bool member)
        {
            List<string> args = new List<string>();
            bool guard = ReadGuard.ScriptPath != null;
            if (!String.IsNullOrEmpty(agent.subagentContext) || guard)
            {
                string context = String.IsNullOrEmpty(agent.subagentContext)
                    ? null
                    : agent.subagentContext.Replace("{value}", model ?? "");
                args.Add("--settings");
                args.Add(SubagentHookFiles.Ensure(context, guard, member));
            }
            return args;
        }

        protected override string[] BuildContinueArguments(string operation, string[] baseArguments, string[] resumeExtra, List<string> selection, string sessionId)
        {
            if (operation == "fork")
                throw new InvalidOperationException("Native Claude fork is not supported by this Bridge release. " + "Create a new session from the selected branch context instead.");
            List<string> args = new List<string>(InsertBeforeStdinMarker(baseArguments, selection));
            args.AddRange(resumeExtra);
            args.Add("--resume");
            args.Add(sessionId);
            return args.ToArray();
        }

        public override string[] AddDeviceArguments(string[] args, DeviceLaunch launch)
        {
            List<string> list = new List<string>(args);
            Dictionary<string, object> servers = new Dictionary<string, object>();
            List<string> allow = new List<string>();
            foreach (DeviceServer d in launch.Servers)
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["type"] = "stdio";
                s["command"] = launch.Exe;
                s["args"] = d.ProxyArgs;
                servers["dev_" + d.ShortName] = s;
                allow.Add("mcp__dev_" + d.ShortName);
            }
            Dictionary<string, object> cfg = new Dictionary<string, object>();
            cfg["mcpServers"] = servers;
            File.WriteAllText(launch.McpConfigPath, JsonUtil.Serialize(cfg), new UTF8Encoding(false));
            List<string> extra = new List<string> { "--mcp-config", launch.McpConfigPath };
            // Device tools enforce their own confirmations; Claude must not prompt for them again.
            int ai = list.FindIndex(delegate(string a) { return a == "--allowedTools" || a == "--allowed-tools"; });
            if (ai >= 0 && ai + 1 < list.Count)
            {
                list[ai + 1] = list[ai + 1] + "," + String.Join(",", allow.ToArray());
            }
            else
            {
                extra.Add("--allowedTools");
                extra.Add(String.Join(",", allow.ToArray()));
            }
            if (launch.ApproveShortName != null)
            {
                extra.Add("--permission-prompt-tool");
                extra.Add("mcp__dev_" + launch.ApproveShortName + "__approve");
            }
            return InsertBeforeStdinMarker(list.ToArray(), extra);
        }
    }

    internal sealed class CodexAgentRuntime : AgentRuntime
    {
        public override string Protocol
        {
            get { return "codex-jsonl"; }
        }

        // Codex reads the whole disk in workspace-write; only writable folders need to be listed.
        // TOML literal strings avoid backslash escaping.
        protected override List<string> ContextFolderArguments(List<ResolvedContext> contexts)
        {
            List<string> args = new List<string>();
            List<string> writable = contexts
                .Where(delegate(ResolvedContext rc) { return rc.write; })
                .Select(delegate(ResolvedContext rc) { return "'" + rc.fullPath + "'"; })
                .ToList();
            if (writable.Count > 0)
            {
                args.Add("-c");
                args.Add("sandbox_workspace_write.writable_roots=[" + String.Join(",", writable.ToArray()) + "]");
            }
            return args;
        }

        // Read-only: approval_policy="never" rejects every escalation and every app tool that asks for approval.
        // Write: approvals go to Codex's automatic reviewer (same as --approve-for-me). The Windows sandbox
        // cannot write to Google Drive for Desktop (G:), so writes there run as reviewed escalations, and
        // connector tools (codex_apps google_drive.upload_file etc.) need an approval too; "never" rejected both.
        protected override List<string> PermissionArguments(PermissionsRequest p, List<string> cloudDenied)
        {
            List<string> args = new List<string>();
            if (p == null) return args; // No chat permissions: Codex keeps its defaults.
            bool readOnly = p.files == "read";
            bool shell = !p.shell.HasValue || p.shell.Value;
            bool web = !p.web.HasValue || p.web.Value;
            args.Add("-c");
            args.Add(readOnly ? "approval_policy=\"never\"" : "approval_policy=\"on-request\"");
            args.Add("-c");
            args.Add(readOnly ? "approvals_reviewer=\"user\"" : "approvals_reviewer=\"auto_review\"");
            args.Add("-c");
            args.Add(readOnly ? "sandbox_mode=\"read-only\"" : "sandbox_mode=\"workspace-write\"");
            if (!readOnly && web)
            {
                // 1.5.3: workspace-write blocks outbound connections from the shell (aiwb-chat to the CO database
                // failed with WinError 10061); web allowed = network allowed, as for Claude.
                args.Add("-c");
                args.Add("sandbox_workspace_write.network_access=true");
            }
            if (readOnly)
            {
                // 1.3.4: tool library (aiwb-tools MCP) may write files; under "never" an approval request = blocked.
                args.Add("-c");
                args.Add("mcp_servers.aiwb-tools.default_tools_approval_mode=\"prompt\"");
            }
            if (!shell)
            {
                args.Add("--disable");
                args.Add("shell_tool");
                args.Add("--disable");
                args.Add("unified_exec");
            }
            if (!web)
            {
                args.Add("-c");
                args.Add("web_search=\"disabled\"");
            }
            return args;
        }

        protected override string[] BuildContinueArguments(string operation, string[] baseArguments, string[] resumeExtra, List<string> selection, string sessionId)
        {
            if (operation == "resume" || operation == "fork")
            {
                // codex exec resume|fork [OPTIONS] <SESSION_ID> -
                List<string> args = new List<string> { "exec", operation, "--json" };
                args.AddRange(resumeExtra);
                args.AddRange(selection);
                args.Add(sessionId);
                args.Add("-");
                return args.ToArray();
            }
            return base.BuildContinueArguments(operation, baseArguments, resumeExtra, selection, sessionId);
        }

        public override string[] AddDeviceArguments(string[] args, DeviceLaunch launch)
        {
            List<string> list = new List<string>(args);
            List<string> extra = new List<string>();
            foreach (DeviceServer d in launch.Servers)
            {
                string key = "mcp_servers.dev_" + d.ShortName;
                extra.Add("-c");
                extra.Add(key + ".command='" + launch.Exe + "'");
                extra.Add("-c");
                extra.Add(key + ".args=[" + String.Join(",", d.ProxyArgs.Select(delegate(string a) { return "'" + a + "'"; }).ToArray()) + "]");
                extra.Add("-c");
                extra.Add(key + ".tool_timeout_sec=" + launch.ToolTimeoutSec.ToString(CultureInfo.InvariantCulture));
            }
            // Options precede "<SESSION_ID> -" for resume/fork and "-" for a new session.
            string op = launch.Operation;
            int at = list.Count;
            if (at > 0 && list[at - 1] == "-") at--;
            if ((op == "resume" || op == "fork") && at > 0) at--;
            list.InsertRange(at, extra);
            return list.ToArray();
        }

        // codex exec reports session totals in turn.completed; the job shows this turn only.
        public override AgentStreamParser CreateParser(AgentProfile agent, JobRequest request)
        {
            UsageRecord baseline = null;
            if (request.conversation != null && request.conversation.operation == "resume" && !String.IsNullOrEmpty(request.conversation.sessionId))
                baseline = CodexSessionTotals(agent.rateLimitSessionsDir, request.conversation.sessionId);
            return new CodexStreamParser(baseline);
        }

        // Codex writes rate_limits into its session logs for every turn, including turns
        // run outside CO (Codex app, CLI). The newest log gives the current state.
        public override AgentLimits ReadOfflineLimits(AgentProfile agent)
        {
            string dir = agent.rateLimitSessionsDir;
            if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            FileInfo newest = new DirectoryInfo(dir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(delegate(FileInfo f) { return f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-8); })
                .OrderByDescending(delegate(FileInfo f) { return f.LastWriteTimeUtc; })
                .FirstOrDefault();
            if (newest == null) return null;
            return ReadCodexLimits(newest.FullName);
        }

        internal static UsageRecord CodexSessionTotals(string sessionsDir, string sessionId)
        {
            try
            {
                if (String.IsNullOrEmpty(sessionsDir) || !Directory.Exists(sessionsDir) ||
                    !Regex.IsMatch(sessionId, @"\A[0-9a-fA-F-]{8,64}\z"))
                    return null;
                string file = Directory.GetFiles(sessionsDir, "*" + sessionId + ".jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(delegate(string f) { return File.GetLastWriteTimeUtc(f); }).FirstOrDefault();
                if (file == null)
                    return null;
                string last = null;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                        if (line.IndexOf("\"total_token_usage\"", StringComparison.Ordinal) >= 0)
                            last = line;
                }
                if (last == null)
                    return null;
                Dictionary<string, object> o = JsonUtil.DeserializeObject(last) as Dictionary<string, object>;
                Dictionary<string, object> payload = o == null ? null : o["payload"] as Dictionary<string, object>;
                Dictionary<string, object> info = payload == null ? null : payload["info"] as Dictionary<string, object>;
                Dictionary<string, object> t = info == null ? null : info["total_token_usage"] as Dictionary<string, object>;
                if (t == null)
                    return null;
                UsageRecord u = new UsageRecord();
                u.inputTokens = Convert.ToInt64(t.ContainsKey("input_tokens") ? t["input_tokens"] : 0);
                u.cachedInputTokens = Convert.ToInt64(t.ContainsKey("cached_input_tokens") ? t["cached_input_tokens"] : 0);
                u.outputTokens = Convert.ToInt64(t.ContainsKey("output_tokens") ? t["output_tokens"] : 0);
                u.reasoningOutputTokens = Convert.ToInt64(t.ContainsKey("reasoning_output_tokens") ? t["reasoning_output_tokens"] : 0);
                return u;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static AgentLimits ReadCodexLimits(string path)
        {
            string tail;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 512 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (StreamReader r = new StreamReader(fs, new UTF8Encoding(false)))
                    tail = r.ReadToEnd();
            }

            string[] lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
                    continue;

                Dictionary<string, object> obj;
                try
                {
                    obj = JsonUtil.DeserializeObject(line) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }

                if (obj == null)
                    continue;

                object payloadObj;
                Dictionary<string, object> payload = obj.TryGetValue("payload", out payloadObj) ? payloadObj as Dictionary<string, object> : obj;
                object rlObj;
                Dictionary<string, object> rl = payload != null && payload.TryGetValue("rate_limits", out rlObj) ? rlObj as Dictionary<string, object> : null;
                if (rl == null)
                    continue;

                AgentLimits limits = new AgentLimits();
                limits.source = "codex-session-log";
                object ts;
                limits.observedUtc = obj.TryGetValue("timestamp", out ts) && ts != null ? Convert.ToString(ts, CultureInfo.InvariantCulture) : DateTime.UtcNow.ToString("o");
                object plan;
                limits.plan = rl.TryGetValue("plan_type", out plan) && plan != null ? Convert.ToString(plan, CultureInfo.InvariantCulture) : null;
                object reached;
                limits.status = rl.TryGetValue("rate_limit_reached_type", out reached) && reached != null ? "limited" : "allowed";
                limits.windows = new List<LimitWindow>();

                foreach (string key in new string[] { "primary", "secondary" })
                {
                    object wObj;
                    Dictionary<string, object> w = rl.TryGetValue(key, out wObj) ? wObj as Dictionary<string, object> : null;
                    if (w == null)
                        continue;
                    limits.windows.Add(LimitWindow.Create(key, Num(w, "used_percent"), (long)Num(w, "resets_at"), (int)Num(w, "window_minutes")));
                }

                return limits.windows.Count > 0 ? limits : null;
            }

            return null;
        }

        private static double Num(Dictionary<string, object> obj, string key)
        {
            object v;
            double d;
            if (obj.TryGetValue(key, out v) && v != null &&
                Double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return 0;
        }
    }
}
