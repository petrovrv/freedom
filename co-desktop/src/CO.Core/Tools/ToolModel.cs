// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Core.Tools;

public enum UntrustedMode { Never, Always, OutsideProject }

public sealed class ToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required RiskClass Class { get; init; }
    public required UntrustedMode Untrusted { get; init; }
    public required JsonObject InputSchema { get; init; }
}

public sealed class ToolOutput
{
    public required string Content { get; init; }
    public bool Untrusted { get; init; }
}

public sealed class ToolException : Exception
{
    public ToolException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed class ToolEnv
{
    public required FolderRegistry Folders { get; init; }
    public required SnapshotStore Snapshots { get; init; }
    public required string RunId { get; init; }
    public CO.Core.Protocol.ToolCall? Call { get; init; }
    public CO.Core.Host.IApprovalService? Approvals { get; init; }
    public bool Tainted { get; init; }
    public bool Trusted { get; init; }   // owner opted into auto-approval; tools that prompt directly should skip it

    // 0.9.8: whose agent browser tab a call drives: the chat (calls from Bridge 1.9.2 carry a signed chat id), so the
    // next turn of the same chat continues in the same tab; otherwise the Run.
    public string TabOwner => Call is { } c && c.ChatId.Length is > 0 and <= 80
        && c.ChatId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? "chat-" + c.ChatId : RunId;

    // 0.9.9: the chat's agent browser settings (unsigned "browser" field of the call); defaults when missing or broken.
    public CO.Core.Browser.AgentTabRules Browser => CO.Core.Browser.AgentTabRules.Parse(Call?.Browser);
}

public interface IDeviceTool
{
    ToolDescriptor Descriptor { get; }
    ActionPlan Plan(JsonElement args, ToolEnv env);
    Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct);
}

public static class Args
{
    public static string Str(JsonElement a, string name)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new ToolException("invalid_args", $"Missing string argument '{name}'.");
    }

    public static string? OptStr(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long OptLong(JsonElement a, string name, long dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : dflt;

    public static bool OptBool(JsonElement a, string name, bool dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : dflt;
}

public static class Schema
{
    public static JsonObject Obj(params (string name, string type, string desc, bool required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, type, desc, required) in props)
        {
            p[name] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
    }
}

public sealed class ToolCatalog
{
    private readonly Dictionary<string, IDeviceTool> _tools = new(StringComparer.Ordinal);

    public void Add(IDeviceTool t)
    {
        if (!ToolNames.IsValid(t.Descriptor.Name))
            throw new ArgumentException($"Invalid tool name '{t.Descriptor.Name}'.");
        _tools.Add(t.Descriptor.Name, t);
    }

    public bool TryGet(string name, out IDeviceTool tool) => _tools.TryGetValue(name, out tool!);

    public IEnumerable<ToolDescriptor> Descriptors => _tools.Values.Select(t => t.Descriptor);

    // Catalog published in the pairing request and in devices/<id>.json (MCP tools/list shape).
    public JsonArray ToMcpList()
    {
        var arr = new JsonArray();
        foreach (var d in Descriptors.OrderBy(d => d.Name, StringComparer.Ordinal))
            arr.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = $"[{d.Class}] {d.Description}",
                ["inputSchema"] = d.InputSchema.DeepClone(),
            });
        return arr;
    }
}

public static class ToolNames
{
    // TZ §22: Claude API tool names ^[a-zA-Z0-9_-]{1,64}$; Claude Code adds "mcp__dev_<short>__".
    public const int MaxShort = 12;

    public static bool IsValid(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool IsValidShort(string s) =>
        s.Length is > 0 and <= MaxShort && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    public static string FullName(string deviceShort, string tool) => $"mcp__dev_{deviceShort}__{tool}";
}
