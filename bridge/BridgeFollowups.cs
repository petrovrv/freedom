// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AiWorkbenchBridge
{
    /*
     * FOLLOW-UPS (v1.7.0, schema 13, CO Web 0.60.0)
     * =============================================
     * A message the user sends while a job of the chat runs is not a new parallel job. CO Web stores it on the server
     * (db.php followup_add); job_claim with followups=true hands this host the pending ones of its jobs. Claude runs
     * with --input-format stream-json: the prompt is the first stdin line, stdin stays open, and a follow-up is one more
     * user line; Claude reads it after its current step (tool call) and answers in the same turn. Bridge closes stdin
     * once Claude's result is seen, so the process ends as before.
     * Outcome per follow-up (followup_ack): delivered (written; the server adds it to the chat), late (stdin already
     * closed: CO Web sends it as the next message after the job), retry (the job has not started here yet).
     * Codex (codex exec) takes no input while it runs: CO Web queues its follow-ups as late from the start.
     */
    internal sealed class FollowupSink
    {
        private const string Header = "[Follow-up from the user, sent while you were working on this request]\n";

        private readonly object _lock = new object();
        private readonly AgentStreamParser _parser;
        private readonly EventBatchWriter _events;
        private Stream _stdin;
        private bool _closed;

        public FollowupSink(AgentStreamParser parser, EventBatchWriter events)
        {
            _parser = parser;
            _events = events;
        }

        public static string[] AddStreamInput(string[] args)
        {
            List<string> extra = new List<string> { "--input-format", "stream-json" };
            return AgentRuntime.InsertBeforeStdinMarker(args, extra);
        }

        // One Claude stream-json user message.
        public static byte[] UserLine(string text)
        {
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["role"] = "user";
            message["content"] = text ?? "";
            Dictionary<string, object> line = new Dictionary<string, object>();
            line["type"] = "user";
            line["message"] = message;
            return new UTF8Encoding(false).GetBytes(JsonUtil.Serialize(line) + "\n");
        }

        public void Open(Stream stdin)
        {
            lock (_lock)
            {
                if (_closed)
                {
                    try { stdin.Close(); } catch { }
                    return;
                }
                _stdin = stdin;
            }
        }

        // "delivered" | "late" | "retry"
        public string TryWrite(string text)
        {
            lock (_lock)
            {
                if (_closed)
                    return "late";
                if (_stdin == null)
                    return "retry";
                try
                {
                    byte[] line = UserLine(Header + text);
                    _stdin.Write(line, 0, line.Length);
                    _stdin.Flush();
                }
                catch
                {
                    CloseLocked();
                    return "late";
                }
                _parser.NoteFollowup();
            }
            try { _events.Write("bridge", "followup", "Follow-up from the user delivered to the agent."); } catch { }
            return "delivered";
        }

        public void Close()
        {
            lock (_lock)
            {
                CloseLocked();
            }
        }

        private void CloseLocked()
        {
            if (_closed)
                return;
            _closed = true;
            if (_stdin != null)
            {
                try { _stdin.Close(); } catch { }
                _stdin = null;
            }
        }
    }

    internal sealed partial class BridgeApplicationContext
    {
        private readonly ConcurrentDictionary<string, FollowupSink> _followupSinks =
            new ConcurrentDictionary<string, FollowupSink>(StringComparer.Ordinal);
        // Final outcome per follow-up id: the server offers one again when its ack was lost; it is not written twice.
        private readonly ConcurrentDictionary<long, string> _followupOutcomes = new ConcurrentDictionary<long, string>();

        private bool FollowupsEnabled
        {
            get { return DbqEnabled && _config.dbQueue.followups; }
        }

        // job_claim "followups": [{id, jobId, text}] -> followup_ack {host, results: [{id, outcome}]}
        private void DeliverFollowups(object[] offered)
        {
            if (offered == null || offered.Length == 0)
                return;
            List<object> results = new List<object>();
            foreach (object o in offered)
            {
                Dictionary<string, object> f = o as Dictionary<string, object>;
                if (f == null)
                    continue;
                object idObj;
                if (!f.TryGetValue("id", out idObj) || idObj == null)
                    continue;
                long id;
                if (!Int64.TryParse(Convert.ToString(idObj), out id))
                    continue;
                string jobId = DictString(f, "jobId");
                string text = DictString(f, "text");
                string outcome;
                FollowupSink sink;
                if (!_followupOutcomes.TryGetValue(id, out outcome))
                {
                    outcome = "retry";
                    if (!String.IsNullOrEmpty(jobId) && !String.IsNullOrEmpty(text) && _followupSinks.TryGetValue(jobId, out sink))
                        outcome = sink.TryWrite(text);
                    if (outcome != "retry")
                    {
                        if (_followupOutcomes.Count > 2000)
                            _followupOutcomes.Clear();
                        _followupOutcomes[id] = outcome;
                    }
                }
                Log("Follow-up " + id + " for " + jobId + ": " + outcome + ".");
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["id"] = id;
                r["outcome"] = outcome;
                results.Add(r);
            }
            if (results.Count == 0)
                return;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["host"] = _config.hostId;
            body["results"] = results.ToArray();
            try
            {
                DbqCall("followup_ack", body, false);
            }
            catch (Exception ex)
            {
                // The server offers an unacknowledged follow-up again after 2 minutes.
                LogThrottled("dbq-followup", "Database queue: followup_ack failed: " + ex.Message);
            }
        }
    }
}
