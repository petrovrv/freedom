// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Host;
using CO.Core.Policy;

namespace CO.Core.Tools;

// TZ §8.2: Claude Code --permission-prompt-tool. The Host-side CLI asks whether it may use one of
// its own tools; the question is shown on the device the turn came from. Output follows the
// Claude Code contract: {"behavior":"allow","updatedInput":{...}} or {"behavior":"deny","message":"..."}.
public sealed class ApproveTool : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "approve", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Permission prompt for the agent's own tools on the Host (used by Claude Code, not called directly).",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool_name"] = new JsonObject { ["type"] = "string" },
                ["input"] = new JsonObject { ["type"] = "object" },
                ["tool_use_id"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("tool_name", "input"),
        },
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env) =>
        new() { Tool = "approve", Class = RiskClass.R, Summary = "Host tool " + Args.Str(a, "tool_name") };

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        if (env.Approvals == null || env.Call == null) throw new ToolException("unsupported", "No approval UI.");
        string tool = Args.Str(a, "tool_name");
        JsonElement input = a.TryGetProperty("input", out var i) ? i : default;
        // Owner opted into auto-approval: allow the Host CLI's tool without prompting.
        if (env.Trusted)
            return new ToolOutput { Content = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(input.GetRawText()) }.ToJsonString() };
        var plan = new ActionPlan
        {
            Tool = "host:" + tool, Class = RiskClass.X,
            Summary = $"The agent on the Host wants to use its tool '{tool}'.",
        };
        var answer = await env.Approvals.RequestAsync(new ApprovalRequest
        {
            Kind = ApprovalKind.Action, Call = env.Call, Plan = plan, Tainted = env.Tainted,
            Decision = Decision.Confirm("Permission request from the Host CLI.", false),
        }, ct);
        var result = answer.Choice == ApprovalChoice.Deny
            ? new JsonObject { ["behavior"] = "deny", ["message"] = string.IsNullOrWhiteSpace(answer.Comment) ? "Declined by the user." : answer.Comment }
            : new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(input.GetRawText()) };
        return new ToolOutput { Content = result.ToJsonString() };
    }
}
