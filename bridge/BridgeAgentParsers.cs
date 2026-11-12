// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AiWorkbenchBridge
{
    // v1.4.0: stream parsers of the agent CLIs (one line of stdout -> structured state). Created by AgentRuntime.CreateParser.
    internal abstract class AgentStreamParser
    {
        protected readonly object _sync = new object();
        protected const int MaxRecordedErrorMessages = 20;
        protected bool _finalSeen; protected bool _success; protected bool _failureSeen; protected string _sessionId; protected string _finalResponse; protected string _failureMessage; protected int _errorEventCount;
        protected readonly List<string> _errorMessages = new List<string>(); protected UsageRecord _usage; protected string _modelReported; protected AgentLimits _limits;

        public void Observe(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            lock (_sync)
            {
                try
                {
                    object raw = JsonUtil.DeserializeObject(line);
                    Dictionary<string, object> obj = raw as Dictionary<string, object>;
                    if (obj == null) return;
                    ObserveEvent(obj);
                }
                catch
                {
                    // Human-readable or malformed output is still kept in stdout.log.
                    // It simply does not participate in structured success detection.
                }
            }
        }

        // Called under _sync with a parsed JSON object.
        protected abstract void ObserveEvent(Dictionary<string, object> obj);

        // v1.7.0: a final event was seen (Bridge then closes the follow-up input of Claude).
        public bool FinalSeen
        {
            get { lock (_sync) { return _finalSeen; } }
        }

        // v1.7.0: a follow-up of the user was written into the agent's stdin; a later turn's result is then kept
        // together with the earlier one instead of replacing it.
        protected int _followups;
        public void NoteFollowup()
        {
            lock (_sync) { _followups++; }
        }

        public AgentInspectionSnapshot Snapshot()
        {
            lock (_sync)
            {
                AgentInspectionSnapshot snapshot = new AgentInspectionSnapshot();
                snapshot.finalSeen = _finalSeen; snapshot.success = _success; snapshot.sessionId = _sessionId; snapshot.finalResponse = _finalResponse; snapshot.failureMessage = _failureMessage; snapshot.errorEventCount = _errorEventCount; snapshot.errorMessages = new List<string>(_errorMessages); snapshot.usage = _usage; snapshot.modelReported = _modelReported; snapshot.limits = _limits;
                return snapshot;
            }
        }

        protected static double GetDouble(Dictionary<string, object> obj, string key) { object value; double d; if (obj.TryGetValue(key, out value) && value != null && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d; return 0; }
        protected static string ExtractErrorMessage(Dictionary<string, object> obj) { string message = GetString(obj, "message"); if (String.IsNullOrWhiteSpace(message)) { Dictionary<string, object> error = GetDictionary(obj, "error"); if (error != null) message = GetString(error, "message"); } return message; }
        protected static string GetString(Dictionary<string, object> obj, string key) { object value; if (!obj.TryGetValue(key, out value) || value == null) return null; return Convert.ToString(value); }
        protected static long GetLong(Dictionary<string, object> obj, string key) { object value; if (!obj.TryGetValue(key, out value) || value == null) return 0; long parsed; if (Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed; double d; if (Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d; return 0; }
        protected static bool GetBool(Dictionary<string, object> obj, string key) { object value; if (!obj.TryGetValue(key, out value) || value == null) return false; if (value is bool) return (bool)value; bool parsed; if (Boolean.TryParse(Convert.ToString(value), out parsed)) return parsed; return false; }
        protected static Dictionary<string, object> GetDictionary(Dictionary<string, object> obj, string key) { object value; if (!obj.TryGetValue(key, out value) || value == null) return null; return value as Dictionary<string, object>; }
    }

    // Unstructured output: lines are logged only, no structured detection.
    internal sealed class RawStreamParser : AgentStreamParser
    {
        protected override void ObserveEvent(Dictionary<string, object> obj)
        {
        }
    }

    // Claude Code stream-json protocol.
    internal sealed class ClaudeStreamParser : AgentStreamParser
    {
        protected override void ObserveEvent(Dictionary<string, object> obj)
        {
            string type = GetString(obj, "type");
            if (type == "system" && GetString(obj, "subtype") == "init")
            {
                string initSession = GetString(obj, "session_id");
                if (!String.IsNullOrWhiteSpace(initSession)) _sessionId = initSession;
                string initModel = GetString(obj, "model");
                if (!String.IsNullOrWhiteSpace(initModel)) _modelReported = initModel;
                return;
            }
            if (type == "rate_limit_event") { ObserveClaudeLimits(GetDictionary(obj, "rate_limit_info")); return; }
            if (type != "result") return;
            _finalSeen = true;
            bool isError = GetBool(obj, "is_error");
            string subtype = GetString(obj, "subtype");
            _success = !isError && String.Equals(subtype, "success", StringComparison.Ordinal);
            string session = GetString(obj, "session_id");
            if (!String.IsNullOrWhiteSpace(session)) _sessionId = session;
            string result = GetString(obj, "result");
            if (!String.IsNullOrEmpty(result))
                _finalResponse = _followups > 0 && !String.IsNullOrEmpty(_finalResponse) && _finalResponse != result
                    ? _finalResponse + "\n\n" + result
                    : result;
            UsageRecord record = new UsageRecord();
            Dictionary<string, object> usage = GetDictionary(obj, "usage");
            if (usage != null)
            {
                record.inputTokens = GetLong(usage, "input_tokens");
                record.cachedInputTokens = GetLong(usage, "cache_read_input_tokens");
                record.cacheCreationInputTokens = GetLong(usage, "cache_creation_input_tokens");
                record.outputTokens = GetLong(usage, "output_tokens");
            }
            object costObj; double cost;
            if (obj.TryGetValue("total_cost_usd", out costObj) && costObj != null &&
                Double.TryParse(Convert.ToString(costObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out cost))
                record.costUsd = cost;
            if (obj.ContainsKey("duration_ms")) record.durationMs = GetLong(obj, "duration_ms");
            if (obj.ContainsKey("num_turns")) record.numTurns = (int)GetLong(obj, "num_turns");
            // v1.2.4: background subagents produce several "result" events per run. Tokens and turns are
            // per turn and summed; cost and duration are cumulative and take the maximum.
            if (_usage == null) _usage = record;
            else
            {
                _usage.inputTokens += record.inputTokens; _usage.cachedInputTokens += record.cachedInputTokens; _usage.cacheCreationInputTokens += record.cacheCreationInputTokens; _usage.outputTokens += record.outputTokens;
                if (_usage.numTurns.HasValue || record.numTurns.HasValue) _usage.numTurns = (_usage.numTurns ?? 0) + (record.numTurns ?? 0);
                if (record.costUsd.HasValue && (!_usage.costUsd.HasValue || record.costUsd.Value > _usage.costUsd.Value)) _usage.costUsd = record.costUsd;
                if (record.durationMs.HasValue && (!_usage.durationMs.HasValue || record.durationMs.Value > _usage.durationMs.Value)) _usage.durationMs = record.durationMs;
            }
            if (!_success)
                _failureMessage = String.IsNullOrWhiteSpace(subtype) ? "Claude structured result indicates failure." : "Claude result subtype: " + subtype;
        }

        // Claude Code stream-json: rate_limit_event.rate_limit_info.unifiedWindows
        //   { five_hour: { utilization: 0.06, resetsAt: <unix> }, seven_day: {...}, ... }
        private void ObserveClaudeLimits(Dictionary<string, object> info)
        {
            if (info == null) return;
            AgentLimits limits = new AgentLimits();
            limits.source = "claude-code"; limits.observedUtc = DateTime.UtcNow.ToString("o"); limits.status = GetString(info, "status"); limits.windows = new List<LimitWindow>();
            Dictionary<string, object> windows = GetDictionary(info, "unifiedWindows");
            if (windows != null)
            {
                foreach (KeyValuePair<string, object> w in windows)
                {
                    Dictionary<string, object> d = w.Value as Dictionary<string, object>;
                    if (d == null) continue;
                    limits.windows.Add(LimitWindow.Create(w.Key, GetDouble(d, "utilization") * 100.0, GetLong(d, "resetsAt"),
                        w.Key == "five_hour" ? 300 : (w.Key.StartsWith("seven_day", StringComparison.Ordinal) ? 10080 : 0)));
                }
            }
            if (limits.windows.Count > 0) _limits = limits;
        }
    }

    // Codex JSONL protocol.
    internal sealed class CodexStreamParser : AgentStreamParser
    {
        private readonly UsageRecord _baseline;

        public CodexStreamParser(UsageRecord baseline)
        {
            _baseline = baseline;
        }

        protected override void ObserveEvent(Dictionary<string, object> obj)
        {
            string type = GetString(obj, "type");
            if (type == "thread.started") { _sessionId = GetString(obj, "thread_id"); return; }
            if (type == "turn.completed")
            {
                _finalSeen = true; _success = !_failureSeen;
                Dictionary<string, object> usage = GetDictionary(obj, "usage");
                if (usage != null)
                {
                    if (_usage == null) _usage = new UsageRecord();
                    UsageRecord b = _baseline ?? new UsageRecord();
                    _usage.inputTokens += Math.Max(0, GetLong(usage, "input_tokens") - b.inputTokens);
                    _usage.cachedInputTokens += Math.Max(0, GetLong(usage, "cached_input_tokens") - b.cachedInputTokens);
                    _usage.outputTokens += Math.Max(0, GetLong(usage, "output_tokens") - b.outputTokens);
                    _usage.reasoningOutputTokens += Math.Max(0, GetLong(usage, "reasoning_output_tokens") - b.reasoningOutputTokens);
                }
                return;
            }
            if (type == "turn.failed") { _failureSeen = true; _finalSeen = true; _success = false; _failureMessage = ExtractErrorMessage(obj); return; }
            if (type == "error")
            {
                // Codex emits "error" both for fatal stream failures and for transient
                // reconnect notices ("Reconnecting... 1/5") after which the turn continues.
                // The event is counted and its message recorded; it does not decide the
                // job status. A fatal error ends the process without turn.completed, which
                // is reported as "no structured final event".
                _errorEventCount++;
                string message = ExtractErrorMessage(obj);
                if (!String.IsNullOrWhiteSpace(message) && _errorMessages.Count < MaxRecordedErrorMessages) _errorMessages.Add(message);
                if (!_finalSeen) _failureMessage = message;
                return;
            }
            if (type == "item.completed")
            {
                Dictionary<string, object> item = GetDictionary(obj, "item");
                if (item != null && GetString(item, "type") == "agent_message")
                {
                    string text = GetString(item, "text");
                    if (!String.IsNullOrEmpty(text)) _finalResponse = text;
                }
            }
        }
    }
}
