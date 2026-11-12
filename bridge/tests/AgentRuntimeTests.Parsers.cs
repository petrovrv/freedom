// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        // ObserveClaudeLimits stamps DateTime.UtcNow, so observedUtc is masked.
        static string Canon(AgentInspectionSnapshot s)
        {
            return Regex.Replace(JsonUtil.Serialize(s), "\"observedUtc\":\"[^\"]*\"", "\"observedUtc\":null");
        }

        // Every sample is fed to every protocol (cross-protocol feeding is intended).
        static void TestParsers(List<string> dirs)
        {
            string[] protocols = { "raw", "claude-stream-json", "codex-jsonl" };
            foreach (string dir in dirs)
            {
                string[] files = Directory.GetFiles(dir, "*.jsonl");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string path in files)
                {
                    ParserFiles++;
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    string name = Path.GetFileName(path);
                    foreach (string protocol in protocols)
                    {
                        AgentProfile p = new AgentProfile();
                        p.outputProtocol = protocol;
                        var legacy = new LegacyAgentOutputInspector(protocol);
                        var fresh = AgentRuntime.Get(protocol).CreateParser(p, new JobRequest());
                        foreach (string line in lines) { legacy.Observe(line); fresh.Observe(line); }
                        Check("parser", name + " as " + protocol, Canon(legacy.Snapshot()), Canon(fresh.Snapshot()));
                        CheckExpected(path, protocol, fresh.Snapshot());
                        if (protocol != "codex-jsonl") continue;
                        UsageRecord B = new UsageRecord();
                        B.inputTokens = 1000;
                        B.cachedInputTokens = 500;
                        B.outputTokens = 100;
                        B.reasoningOutputTokens = 50;
                        var legacyB = new LegacyAgentOutputInspector(protocol);
                        legacyB.SetUsageBaseline(B);
                        var freshB = new CodexStreamParser(B);
                        foreach (string line in lines) { legacyB.Observe(line); freshB.Observe(line); }
                        Check("parser", name + " as " + protocol + " +baseline", Canon(legacyB.Snapshot()), Canon(freshB.Snapshot()));
                    }
                }
            }
        }

        // Informational: sessionId / finalResponse recorded in result.json of the job (database samples may be truncated).
        static void CheckExpected(string path, string protocol, AgentInspectionSnapshot s)
        {
            string sidecar = Path.ChangeExtension(path, ".expected.json");
            if (!File.Exists(sidecar)) return;
            Dictionary<string, object> exp = JsonUtil.DeserializeObject(File.ReadAllText(sidecar, Encoding.UTF8)) as Dictionary<string, object>;
            if (exp == null || !(exp.ContainsKey("protocol") && (exp["protocol"] as string) == protocol)) return;
            string[] keys = { "sessionId", "finalResponse" };
            string[] actuals = { s.sessionId, s.finalResponse };
            for (int i = 0; i < keys.Length; i++)
            {
                object v;
                if (!exp.TryGetValue(keys[i], out v) || v == null) continue;
                if (String.Equals(Convert.ToString(v), actuals[i], StringComparison.Ordinal)) ExpectedMatch++;
                else { ExpectedMismatch++; Console.WriteLine("expected mismatch: " + Path.GetFileName(path) + " " + keys[i]); }
            }
        }
    }
}
