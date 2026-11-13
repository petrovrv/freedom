// SPDX-License-Identifier: AGPL-3.0-or-later
// Frozen copy of the Bridge 1.3.5 agent code (BridgeAgent.cs / BridgeDevices.cs before the AgentRuntime split).
// The test harness compares it with the new AgentRuntime classes; do not edit except to keep it compiling.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static class Legacy135
    {
        internal static string GetConversationOperation(JobRequest request)
        {
            return request == null || request.conversation == null ||
                String.IsNullOrWhiteSpace(request.conversation.operation)
                ? "new" : request.conversation.operation;
        }

        // Builds the agent command line from local configuration only. The request
        // contributes validated ids (session id, catalog model/effort), never raw arguments.
        internal static string[] BuildAgentArguments(AgentProfile agent, ModeProfile modeProfile, JobRequest request, List<string> cloudDeniedTools)
        {
            string operation = GetConversationOperation(request);
            List<string> selection = BuildSelectionArguments(agent, request, cloudDeniedTools);

            string[] baseArguments =
                modeProfile != null && modeProfile.arguments != null && modeProfile.arguments.Length > 0
                    ? modeProfile.arguments
                    : (agent.arguments ?? new string[0]);

            string[] resumeExtra =
                modeProfile != null && modeProfile.resumeExtraArguments != null
                    ? modeProfile.resumeExtraArguments
                    : new string[0];

            if (operation == "new")
                return InsertBeforeStdinMarker(baseArguments, selection);

            if (agent.outputProtocol == "codex-jsonl")
            {
                if (operation == "resume" || operation == "fork")
                {
                    // codex exec resume|fork [OPTIONS] <SESSION_ID> -
                    List<string> args = new List<string> { "exec", operation, "--json" };
                    args.AddRange(resumeExtra);
                    args.AddRange(selection);
                    args.Add(request.conversation.sessionId);
                    args.Add("-");
                    return args.ToArray();
                }
            }

            if (agent.outputProtocol == "claude-stream-json")
            {
                if (operation == "fork")
                    throw new InvalidOperationException(
                        "Native Claude fork is not supported by this Bridge release. " +
                        "Create a new session from the selected branch context instead.");

                List<string> args = new List<string>(InsertBeforeStdinMarker(baseArguments, selection));
                args.AddRange(resumeExtra);
                args.Add("--resume");
                args.Add(request.conversation.sessionId);
                return args.ToArray();
            }

            throw new InvalidOperationException(
                "Agent protocol '" + agent.outputProtocol + "' does not support conversation " + operation + ".");
        }

        internal static List<string> BuildSelectionArguments(AgentProfile agent, JobRequest request, List<string> cloudDeniedTools)
        {
            List<string> args = new List<string>();

            if (request.resolvedContexts != null && request.resolvedContexts.Count > 0)
            {
                if (agent.outputProtocol == "claude-stream-json")
                {
                    foreach (ResolvedContext rc in request.resolvedContexts)
                    {
                        args.Add("--add-dir");
                        args.Add(rc.fullPath);
                    }
                }
                else if (agent.outputProtocol == "codex-jsonl")
                {
                    // Codex reads the whole disk in workspace-write; only writable folders
                    // need to be listed. TOML literal strings avoid backslash escaping.
                    List<string> writable = request.resolvedContexts
                        .Where(delegate(ResolvedContext rc) { return rc.write; })
                        .Select(delegate(ResolvedContext rc) { return "'" + rc.fullPath + "'"; })
                        .ToList();

                    if (writable.Count > 0)
                    {
                        args.Add("-c");
                        args.Add("sandbox_workspace_write.writable_roots=[" + String.Join(",", writable.ToArray()) + "]");
                    }
                }
            }

            args.AddRange(BuildPermissionArguments(agent, request.permissions,
                agent.outputProtocol == "claude-stream-json" ? cloudDeniedTools : null));

            // v1.2.1: a Claude-based agent with a defaultModel (dispatcher) gets its modelArguments also when the user
            // left "Default": they carry the gateway delegation policy, and without them the dispatcher ran as plain Claude.
            string model = request.model;
            if (String.IsNullOrEmpty(model) && agent.outputProtocol == "claude-stream-json")
                model = agent.defaultModel;
            if (!String.IsNullOrEmpty(model) && agent.modelArguments != null)
            {
                foreach (string template in agent.modelArguments)
                    args.Add(template.Replace("{value}", model));
            }

            // v1.2.4: the agent's policy reaches its subagents through a SubagentStart hook (--settings file).
            if (agent.outputProtocol == "claude-stream-json" && !String.IsNullOrEmpty(agent.subagentContext))
            {
                string text = agent.subagentContext.Replace("{value}", model ?? "");
                args.Add("--settings");
                args.Add(SubagentHookFiles.Ensure(text));
            }

            if (!String.IsNullOrEmpty(request.effort) && agent.effortArguments != null)
            {
                foreach (string template in agent.effortArguments)
                    args.Add(template.Replace("{value}", request.effort));
            }

            return args;
        }

        // v1.1 per-chat permissions. Claude: denied tools are removed with --disallowedTools; since 1.1.2 Claude runs
        // with --permission-mode bypassPermissions, so this list is the only gate on the Host side.
        // Codex read-only: approval_policy="never" rejects every escalation and every app tool that asks for approval.
        // Codex write: approvals go to Codex's automatic reviewer (same as --approve-for-me). The Windows sandbox
        // cannot write to Google Drive for Desktop (G:), so writes there run as reviewed escalations, and
        // connector tools (codex_apps google_drive.upload_file etc.) need an approval too; "never" rejected both.
        // v1.1.6: cloudDenied = tools of the Claude account connectors (Gmail, Google Calendar) the user did not tick.
        internal static List<string> BuildPermissionArguments(AgentProfile agent, PermissionsRequest p, List<string> cloudDenied)
        {
            List<string> args = new List<string>();
            if (p == null)
            {
                // No chat permissions: only the connector boxes apply (Claude); other agents keep their defaults.
                if (agent.outputProtocol != "claude-stream-json" || cloudDenied == null || cloudDenied.Count == 0)
                    return args;
                p = new PermissionsRequest();
            }
            bool readOnly = p.files == "read";
            bool shell = !p.shell.HasValue || p.shell.Value;
            bool web = !p.web.HasValue || p.web.Value;

            if (agent.outputProtocol == "codex-jsonl")
            {
                args.Add("-c");
                args.Add(readOnly ? "approval_policy=\"never\"" : "approval_policy=\"on-request\"");
                args.Add("-c");
                args.Add(readOnly ? "approvals_reviewer=\"user\"" : "approvals_reviewer=\"auto_review\"");
                args.Add("-c");
                args.Add(readOnly ? "sandbox_mode=\"read-only\"" : "sandbox_mode=\"workspace-write\"");
                if (!readOnly && web)
                {
                    // Not 1.3.5 code: intended change of Bridge 1.5.3 (network for the Codex shell when web is allowed).
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
            }
            else if (agent.outputProtocol == "claude-stream-json")
            {
                List<string> denied = new List<string>();
                if (readOnly)
                    denied.AddRange(new string[] { "Write", "Edit", "MultiEdit", "NotebookEdit", "mcp__aiwb-tools__tool_run" }); // 1.3.4: tool library may write
                if (!shell)
                    denied.AddRange(new string[] { "Bash", "PowerShell" });
                if (!web)
                    denied.AddRange(new string[] { "WebSearch", "WebFetch" });
                if (cloudDenied != null)
                    denied.AddRange(cloudDenied);
                if (denied.Count > 0)
                {
                    args.Add("--disallowedTools");
                    args.Add(String.Join(",", denied.ToArray()));
                }
            }
            return args;
        }

        // "-" as the last argument means "prompt from stdin" for Codex; options must
        // precede it. Other argument lists get the options appended.
        internal static string[] InsertBeforeStdinMarker(string[] baseArguments, List<string> extra)
        {
            List<string> args = new List<string>(baseArguments);

            if (extra.Count == 0)
                return args.ToArray();

            if (args.Count > 0 && args[args.Count - 1] == "-")
                args.InsertRange(args.Count - 1, extra);
            else
                args.AddRange(extra);

            return args.ToArray();
        }


        // Protocol part of BridgeDevices.AddDeviceArguments; paired-device lookups replaced by DeviceLaunch.
        internal static string[] AddDeviceArguments(AgentProfile agent, JobRequest request, string[] args, DeviceLaunch launch)
        {
            List<string> list = new List<string>(args);
            if (agent.outputProtocol == "claude-stream-json")
            {
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
                string cfgPath = launch.McpConfigPath;
                File.WriteAllText(cfgPath, JsonUtil.Serialize(cfg), new UTF8Encoding(false));

                List<string> extra = new List<string> { "--mcp-config", cfgPath };

                // Device tools enforce their own confirmations; Claude must not prompt for them again.
                int ai = list.FindIndex(delegate(string a) { return a == "--allowedTools" || a == "--allowed-tools"; });
                if (ai >= 0 && ai + 1 < list.Count)
                    list[ai + 1] = list[ai + 1] + "," + String.Join(",", allow.ToArray());
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

            if (agent.outputProtocol == "codex-jsonl")
            {
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
                string op = GetConversationOperation(request);
                int at = list.Count;
                if (at > 0 && list[at - 1] == "-")
                    at--;
                if ((op == "resume" || op == "fork") && at > 0)
                    at--;
                list.InsertRange(at, extra);
                return list.ToArray();
            }

            throw new InvalidOperationException("Agent protocol '" + agent.outputProtocol + "' does not support device tools.");
        }
    }

    internal sealed class LegacyAgentOutputInspector
    {
        private readonly string _protocol;
        private readonly object _sync = new object();

        private const int MaxRecordedErrorMessages = 20;

        private bool _finalSeen;
        private bool _success;
        private bool _failureSeen;
        private string _sessionId;
        private string _finalResponse;
        private string _failureMessage;
        private int _errorEventCount;
        private readonly List<string> _errorMessages = new List<string>();
        private UsageRecord _usage;
        private string _modelReported;
        private AgentLimits _limits;

        private UsageRecord _baseline;

        public void SetUsageBaseline(UsageRecord baseline)
        {
            lock (_sync)
                _baseline = baseline;
        }

        public LegacyAgentOutputInspector(string protocol)
        {
            _protocol = protocol ?? "raw";
        }

        public void Observe(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
                return;

            lock (_sync)
            {
                try
                {
                    object raw = JsonUtil.DeserializeObject(line);
                    Dictionary<string, object> obj =
                        raw as Dictionary<string, object>;

                    if (obj == null)
                        return;

                    if (_protocol == "codex-jsonl")
                        ObserveCodex(obj);
                    else if (_protocol == "claude-stream-json")
                        ObserveClaude(obj);
                }
                catch
                {
                    // Human-readable or malformed output is still kept in stdout.log.
                    // It simply does not participate in structured success detection.
                }
            }
        }

        public AgentInspectionSnapshot Snapshot()
        {
            lock (_sync)
            {
                AgentInspectionSnapshot snapshot =
                    new AgentInspectionSnapshot();

                snapshot.finalSeen = _finalSeen;
                snapshot.success = _success;
                snapshot.sessionId = _sessionId;
                snapshot.finalResponse = _finalResponse;
                snapshot.failureMessage = _failureMessage;
                snapshot.errorEventCount = _errorEventCount;
                snapshot.errorMessages = new List<string>(_errorMessages);
                snapshot.usage = _usage;
                snapshot.modelReported = _modelReported;
                snapshot.limits = _limits;
                return snapshot;
            }
        }

        private void ObserveCodex(
            Dictionary<string, object> obj)
        {
            string type = GetString(obj, "type");

            if (type == "thread.started")
            {
                _sessionId = GetString(obj, "thread_id");
                return;
            }

            if (type == "turn.completed")
            {
                _finalSeen = true;
                _success = !_failureSeen;

                Dictionary<string, object> usage = GetDictionary(obj, "usage");
                if (usage != null)
                {
                    if (_usage == null)
                        _usage = new UsageRecord();

                    UsageRecord b = _baseline ?? new UsageRecord();
                    _usage.inputTokens += Math.Max(0, GetLong(usage, "input_tokens") - b.inputTokens);
                    _usage.cachedInputTokens += Math.Max(0, GetLong(usage, "cached_input_tokens") - b.cachedInputTokens);
                    _usage.outputTokens += Math.Max(0, GetLong(usage, "output_tokens") - b.outputTokens);
                    _usage.reasoningOutputTokens += Math.Max(0, GetLong(usage, "reasoning_output_tokens") - b.reasoningOutputTokens);
                }

                return;
            }

            if (type == "turn.failed")
            {
                _failureSeen = true;
                _finalSeen = true;
                _success = false;
                _failureMessage = ExtractErrorMessage(obj);
                return;
            }

            if (type == "error")
            {
                // Codex emits "error" both for fatal stream failures and for transient
                // reconnect notices ("Reconnecting... 1/5") after which the turn continues.
                // The event is counted and its message recorded; it does not decide the
                // job status. A fatal error ends the process without turn.completed, which
                // is reported as "no structured final event".
                _errorEventCount++;

                string message = ExtractErrorMessage(obj);

                if (!String.IsNullOrWhiteSpace(message) &&
                    _errorMessages.Count < MaxRecordedErrorMessages)
                {
                    _errorMessages.Add(message);
                }

                if (!_finalSeen)
                    _failureMessage = message;

                return;
            }

            if (type == "item.completed")
            {
                Dictionary<string, object> item =
                    GetDictionary(obj, "item");

                if (item != null &&
                    GetString(item, "type") == "agent_message")
                {
                    string text = GetString(item, "text");

                    if (!String.IsNullOrEmpty(text))
                        _finalResponse = text;
                }
            }
        }

        private void ObserveClaude(
            Dictionary<string, object> obj)
        {
            string type = GetString(obj, "type");

            if (type == "system" &&
                GetString(obj, "subtype") == "init")
            {
                string initSession = GetString(obj, "session_id");

                if (!String.IsNullOrWhiteSpace(initSession))
                    _sessionId = initSession;

                string initModel = GetString(obj, "model");
                if (!String.IsNullOrWhiteSpace(initModel))
                    _modelReported = initModel;

                return;
            }

            if (type == "rate_limit_event")
            {
                ObserveClaudeLimits(GetDictionary(obj, "rate_limit_info"));
                return;
            }

            if (type != "result")
                return;

            _finalSeen = true;

            bool isError = GetBool(obj, "is_error");
            string subtype = GetString(obj, "subtype");

            _success =
                !isError &&
                String.Equals(
                    subtype,
                    "success",
                    StringComparison.Ordinal);

            string session = GetString(obj, "session_id");
            if (!String.IsNullOrWhiteSpace(session))
                _sessionId = session;

            string result = GetString(obj, "result");
            if (!String.IsNullOrEmpty(result))
                _finalResponse = result;

            UsageRecord record = new UsageRecord();
            Dictionary<string, object> usage = GetDictionary(obj, "usage");
            if (usage != null)
            {
                record.inputTokens = GetLong(usage, "input_tokens");
                record.cachedInputTokens = GetLong(usage, "cache_read_input_tokens");
                record.cacheCreationInputTokens = GetLong(usage, "cache_creation_input_tokens");
                record.outputTokens = GetLong(usage, "output_tokens");
            }

            object costObj;
            double cost;
            if (obj.TryGetValue("total_cost_usd", out costObj) && costObj != null &&
                Double.TryParse(Convert.ToString(costObj, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out cost))
            {
                record.costUsd = cost;
            }

            if (obj.ContainsKey("duration_ms"))
                record.durationMs = GetLong(obj, "duration_ms");

            if (obj.ContainsKey("num_turns"))
                record.numTurns = (int)GetLong(obj, "num_turns");

            // v1.2.4: background subagents produce several "result" events per run. Tokens and turns are
            // per turn and summed; cost and duration are cumulative and take the maximum.
            if (_usage == null)
            {
                _usage = record;
            }
            else
            {
                _usage.inputTokens += record.inputTokens;
                _usage.cachedInputTokens += record.cachedInputTokens;
                _usage.cacheCreationInputTokens += record.cacheCreationInputTokens;
                _usage.outputTokens += record.outputTokens;

                if (_usage.numTurns.HasValue || record.numTurns.HasValue)
                    _usage.numTurns = (_usage.numTurns ?? 0) + (record.numTurns ?? 0);

                if (record.costUsd.HasValue && (!_usage.costUsd.HasValue || record.costUsd.Value > _usage.costUsd.Value))
                    _usage.costUsd = record.costUsd;

                if (record.durationMs.HasValue && (!_usage.durationMs.HasValue || record.durationMs.Value > _usage.durationMs.Value))
                    _usage.durationMs = record.durationMs;
            }

            if (!_success)
            {
                _failureMessage =
                    String.IsNullOrWhiteSpace(subtype)
                    ? "Claude structured result indicates failure."
                    : "Claude result subtype: " + subtype;
            }
        }

        // Claude Code stream-json: rate_limit_event.rate_limit_info.unifiedWindows
        //   { five_hour: { utilization: 0.06, resetsAt: <unix> }, seven_day: {...}, ... }
        private void ObserveClaudeLimits(Dictionary<string, object> info)
        {
            if (info == null)
                return;

            AgentLimits limits = new AgentLimits();
            limits.source = "claude-code";
            limits.observedUtc = DateTime.UtcNow.ToString("o");
            limits.status = GetString(info, "status");
            limits.windows = new List<LimitWindow>();

            Dictionary<string, object> windows = GetDictionary(info, "unifiedWindows");
            if (windows != null)
            {
                foreach (KeyValuePair<string, object> w in windows)
                {
                    Dictionary<string, object> d = w.Value as Dictionary<string, object>;
                    if (d == null)
                        continue;
                    limits.windows.Add(LimitWindow.Create(
                        w.Key,
                        GetDouble(d, "utilization") * 100.0,
                        GetLong(d, "resetsAt"),
                        w.Key == "five_hour" ? 300 : (w.Key.StartsWith("seven_day", StringComparison.Ordinal) ? 10080 : 0)));
                }
            }

            if (limits.windows.Count > 0)
                _limits = limits;
        }

        private static double GetDouble(Dictionary<string, object> obj, string key)
        {
            object value;
            double d;
            if (obj.TryGetValue(key, out value) && value != null &&
                Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return 0;
        }

        private static string ExtractErrorMessage(
            Dictionary<string, object> obj)
        {
            string message = GetString(obj, "message");

            if (String.IsNullOrWhiteSpace(message))
            {
                Dictionary<string, object> error =
                    GetDictionary(obj, "error");

                if (error != null)
                    message = GetString(error, "message");
            }

            return message;
        }

        private static string GetString(
            Dictionary<string, object> obj,
            string key)
        {
            object value;

            if (!obj.TryGetValue(key, out value) ||
                value == null)
                return null;

            return Convert.ToString(value);
        }

        private static long GetLong(
            Dictionary<string, object> obj,
            string key)
        {
            object value;

            if (!obj.TryGetValue(key, out value) || value == null)
                return 0;

            long parsed;
            if (Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            double d;
            if (Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return (long)d;

            return 0;
        }

        private static bool GetBool(
            Dictionary<string, object> obj,
            string key)
        {
            object value;

            if (!obj.TryGetValue(key, out value) ||
                value == null)
                return false;

            if (value is bool)
                return (bool)value;

            bool parsed;
            if (Boolean.TryParse(
                    Convert.ToString(value),
                    out parsed))
            {
                return parsed;
            }

            return false;
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> obj,
            string key)
        {
            object value;

            if (!obj.TryGetValue(key, out value) ||
                value == null)
                return null;

            return value as Dictionary<string, object>;
        }
    }
}
