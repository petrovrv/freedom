// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AiWorkbenchBridge;

namespace AiWorkbenchBridge.Tests
{
    internal static partial class AgentRuntimeTests
    {
        static string Run(Func<string[]> f)
        {
            try { return String.Join("\u001f", f()); }
            catch (Exception ex) { return "EXC:" + ex.GetType().Name + ":" + ex.Message; }
        }

        static List<PermissionsRequest> PermissionVariants()
        {
            return new List<PermissionsRequest>
            {
                null,
                new PermissionsRequest { files = "read" },
                new PermissionsRequest { files = "write", shell = false, web = false },
                new PermissionsRequest { files = "write", shell = true, web = true },
                new PermissionsRequest { files = "read", shell = false }
            };
        }

        static List<List<ResolvedContext>> ContextVariants()
        {
            return new List<List<ResolvedContext>>
            {
                null,
                new List<ResolvedContext>(),
                new List<ResolvedContext>
                {
                    new ResolvedContext { fullPath = @"D:\Drive\A", write = false, label = "A", rootId = "drive", relativePath = "A" },
                    new ResolvedContext { fullPath = @"C:\Work\B", write = true, label = "B", rootId = "work", relativePath = "B" }
                }
            };
        }

        static List<ConversationRequest> OperationVariants()
        {
            var list = new List<ConversationRequest> { null };
            foreach (var op in new[] { "new", "resume", "fork", "branch" })
                list.Add(new ConversationRequest { operation = op, sessionId = "0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee" });
            return list;
        }

        static List<DeviceLaunch> DeviceVariants(string mcpPath, string operation)
        {
            var a = new DeviceLaunch { Exe = @"C:\Bridge\BridgeAgent.exe", ToolTimeoutSec = 900, Operation = operation, McpConfigPath = mcpPath, ApproveShortName = null };
            a.Servers.Add(new DeviceServer { ShortName = "shuttle", ProxyArgs = new[] { "--mcp-device", "D1", "--job", "JOB-1", "--pipe", "p1" } });
            var b = new DeviceLaunch { Exe = @"C:\Bridge\BridgeAgent.exe", ToolTimeoutSec = 900, Operation = operation, McpConfigPath = mcpPath, ApproveShortName = "shuttle" };
            b.Servers.Add(new DeviceServer { ShortName = "shuttle", ProxyArgs = new[] { "--mcp-device", "D1", "--job", "JOB-1", "--pipe", "p1" } });
            b.Servers.Add(new DeviceServer { ShortName = "laptop", ProxyArgs = new[] { "--mcp-device", "D2", "--job", "JOB-1", "--pipe", "p1" } });
            return new List<DeviceLaunch> { a, b };
        }

        // Command lines of every agent in the given config.json files: legacy BuildAgentArguments / AddDeviceArguments vs AgentRuntime.
        // Claude agents with subagentContext write content-addressed hook files to %LOCALAPPDATA%\AIWorkbenchBridge\subagent-hooks.
        static void TestArguments(List<string> configPaths)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aiwb-runtime-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string mcp = Path.Combine(tmp, "mcp-devices.json");
            try
            {
                foreach (string path in configPaths)
                {
                    BridgeConfig cfg = JsonUtil.Deserialize<BridgeConfig>(File.ReadAllText(path, Encoding.UTF8));
                    foreach (KeyValuePair<string, AgentProfile> pair in cfg.agents)
                    {
                        AgentProfile agent = pair.Value;
                        if (Corporate.IsGatewayRuntime(agent.kind)) continue;
                        if (String.IsNullOrWhiteSpace(agent.outputProtocol)) agent.outputProtocol = "raw";
                        if (agent.arguments == null) agent.arguments = new string[0];
                        var runtime = AgentRuntime.Get(agent.outputProtocol);
                        List<KeyValuePair<string, ModeProfile>> modes = new List<KeyValuePair<string, ModeProfile>>();
                        modes.Add(new KeyValuePair<string, ModeProfile>("-", null));
                        if (agent.modes != null) foreach (KeyValuePair<string, ModeProfile> m in agent.modes) modes.Add(m);
                        List<string> models = new List<string> { null, "model-x" };
                        if (agent.models != null && agent.models.Count > 0) models.Add(agent.models[0].id);
                        string[] efforts = { null, "high" };
                        List<List<string>> denials = new List<List<string>> { null, new List<string> { "mcp__claude_ai_Gmail__send", "mcp__claude_ai_Gmail__draft" } };
                        var perms = PermissionVariants();
                        var ctxs = ContextVariants();
                        foreach (KeyValuePair<string, ModeProfile> mode in modes)
                        foreach (var conv in OperationVariants())
                        for (int pi = 0; pi < perms.Count; pi++)
                        for (int ci = 0; ci < ctxs.Count; ci++)
                        foreach (string model in models)
                        foreach (string effort in efforts)
                        for (int di = 0; di < denials.Count; di++)
                        {
                            var perm = perms[pi];
                            var ctx = ctxs[ci];
                            List<string> denied = denials[di];
                            JobRequest req = new JobRequest();
                            req.jobId = "JOB-1"; req.agent = pair.Key; req.model = model; req.effort = effort;
                            req.permissions = perm; req.resolvedContexts = ctx; req.conversation = conv;
                            List<string> newDenied = runtime.UsesCloudDeniedTools ? denied : null;
                            string[] la = null, na = null;
                            string legacy = Run(delegate { la = Legacy135.BuildAgentArguments(agent, mode.Value, req, denied); return la; });
                            string fresh = Run(delegate { na = runtime.BuildArguments(agent, mode.Value, req, newDenied); return na; });
                            string name = Path.GetFileName(path) + " " + pair.Key + " mode=" + mode.Key + " op=" + (conv == null ? "-" : conv.operation) + " perm=" + pi + " ctx=" + ci + " model=" + (model ?? "-") + " effort=" + (effort ?? "-") + " denied=" + di;
                            Check("arguments", name, legacy, fresh);
                            if (la == null || na == null || pi > 1 || ci == 1 || effort != null || di != 0) continue;
                            string op = AgentRuntime.ConversationOperation(req);
                            List<DeviceLaunch> legacyLaunches = DeviceVariants(mcp, op);
                            List<DeviceLaunch> freshLaunches = DeviceVariants(mcp, op);
                            for (int vi = 0; vi < legacyLaunches.Count; vi++)
                            {
                                DeviceLaunch legacyLaunch = legacyLaunches[vi];
                                DeviceLaunch freshLaunch = freshLaunches[vi];
                                string[] laCopy = la, naCopy = na;
                                string legacyDev = Run(() => Legacy135.AddDeviceArguments(agent, req, laCopy, legacyLaunch));
                                string lf = File.Exists(mcp) ? File.ReadAllText(mcp) : "<none>";
                                if (File.Exists(mcp)) File.Delete(mcp);
                                string freshDev = Run(() => runtime.AddDeviceArguments(naCopy, freshLaunch));
                                string nf = File.Exists(mcp) ? File.ReadAllText(mcp) : "<none>";
                                if (File.Exists(mcp)) File.Delete(mcp);
                                Check("devices", name + " dev=" + vi, legacyDev + "\n" + lf, freshDev + "\n" + nf);
                            }
                        }
                    }
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
