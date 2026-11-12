// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CO.Core.Protocol;

// Wire format shared with BridgeAgent (C# 5, .NET Framework 4). Keep field names and the
// signing payload layout in sync with bridge/BridgeDevices.cs (class DeviceWire).

public sealed class ToolCall
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    // Task the Run belongs to; equals RunId for a plain chat turn. Grants "for this Task" bind to it.
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    // Arguments travel as the exact JSON text that was hashed, so both sides hash identical bytes.
    [JsonPropertyName("argsJson")] public string ArgsJson { get; set; } = "{}";
    [JsonPropertyName("issuedUtc")] public string IssuedUtc { get; set; } = "";
    // Taint inherited from the Run on the Host side (TZ §9.4.2).
    [JsonPropertyName("tainted")] public bool Tainted { get; set; }
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 120_000;
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";
    // 0.9.8: chat of the Run (Bridge 1.9.2 sends it to devices 0.9.8+); the agent browser keeps one tab per chat.
    // Signed as co.tool.call.v2 when present, so older devices never see it.
    [JsonPropertyName("chatId")] public string ChatId { get; set; } = "";
    // 0.9.9: agent browser settings of the chat (Bridge 1.10.0+ resolves them per job), NOT signed: they only tune tab limits and lifetimes,
    // never what a tool may do. Read leniently and clamped by AgentTabRules.Parse; ToolEnv.Browser exposes them.
    [JsonPropertyName("browser")] public JsonElement? Browser { get; set; }

    public byte[] SigningPayload() => ChatId.Length == 0
        ? Wire.Payload("co.tool.call.v1", CallId, RunId, TaskId, DeviceId, Tool, Wire.Sha256Hex(ArgsJson), IssuedUtc,
            Tainted ? "1" : "0")
        : Wire.Payload("co.tool.call.v2", CallId, RunId, TaskId, DeviceId, Tool, Wire.Sha256Hex(ArgsJson), IssuedUtc,
            Tainted ? "1" : "0", ChatId);
}

public sealed class ToolError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class ToolResult
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public ToolError? Error { get; set; }
    // Text returned to the model. Untrusted content is already wrapped in delimiters.
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    // True when this result made the Run tainted (TZ §9.4.1); Bridge propagates it to the Run.
    [JsonPropertyName("untrusted")] public bool Untrusted { get; set; }
    [JsonPropertyName("issuedUtc")] public string IssuedUtc { get; set; } = "";
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";

    public byte[] SigningPayload() => Wire.Payload(
        "co.tool.result.v1", CallId, RunId, DeviceId, Tool, Ok ? "1" : "0",
        Wire.Sha256Hex(Error == null ? "" : Error.Code + "\n" + Error.Message),
        Wire.Sha256Hex(Content), Untrusted ? "1" : "0", IssuedUtc);

    public static ToolResult Fail(ToolCall call, string code, string message) => new()
    {
        CallId = call.CallId, RunId = call.RunId, DeviceId = call.DeviceId, Tool = call.Tool,
        Ok = false, Error = new ToolError { Code = code, Message = message },
    };
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static string Now(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static bool TryParseTime(string s, out DateTime utc) => DateTime.TryParseExact(
        s, TimeFormat, CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    // Fields joined by LF; fields never contain LF because hashes, ids and timestamps are used.
    public static byte[] Payload(params string[] fields)
    {
        foreach (string f in fields)
            if (f.Contains('\n')) throw new ArgumentException("Signed field contains LF.");
        return Encoding.UTF8.GetBytes(string.Join("\n", fields));
    }

    public static string Sha256Hex(string s) => Sha256Hex(Encoding.UTF8.GetBytes(s));

    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new FormatException("Empty JSON.");
}
