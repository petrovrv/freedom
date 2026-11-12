// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CO.Core.Protocol;

namespace CO.Core.Transport;

public delegate Task<ToolResult> CallHandler(ToolCall call, CancellationToken ct);

public enum ChannelState { Off, Starting, Up, Degraded, Down }

public interface IDeviceChannel : IAsyncDisposable
{
    string Name { get; }
    ChannelState State { get; }
    Task StartAsync(CallHandler handler, CancellationToken ct);
}

public sealed class Heartbeat
{
    [JsonPropertyName("type")] public string Type { get; set; } = "device.heartbeat";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("utc")] public string Utc { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("activeCalls")] public int ActiveCalls { get; set; }
    // 0.7.13: session of the browser-based AI connector for Bridge/CO Web: online|signin|offline|unknown|off; "" on older
    // devices and without that connector (ConnectorHooks). The wire names come from its first connector (protocol).
    [JsonPropertyName("engie")] public string ConnectorSession { get; set; } = "";
    // 0.8.2: current sign-in number challenge of that connector, as "<id>:<NN>" so Bridge pushes each number once to the
    // owner's phone; "" when no sign-in is waiting for a number.
    [JsonPropertyName("engieNum")] public string ConnectorNumber { get; set; } = "";
    // Freedom F1 5b: the same as {id, state, num} under the new name (Bridge 1.13 reads it first); engie / engieNum stay
    // the legacy wire for older Bridge. Null (not written) without a connector session.
    [JsonPropertyName("corp")] public CorpHeartbeat? Corp { get; set; }

    // Fills ConnectorSession / ConnectorNumber and Corp from the connector hooks.
    public Heartbeat WithConnector()
    {
        ConnectorSession = CO.Core.Connectors.ConnectorHooks.HeartbeatSession();
        ConnectorNumber = CO.Core.Connectors.ConnectorHooks.HeartbeatNumber();
        Corp = ConnectorSession.Length == 0 ? null : new CorpHeartbeat
        {
            Id = CO.Core.Connectors.ConnectorHooks.HeartbeatConnectorId(), State = ConnectorSession, Num = ConnectorNumber,
        };
        return this;
    }
}

public sealed class CorpHeartbeat
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("num")] public string Num { get; set; } = "";
}

// Names from TZ §4.3 / §4.2; one place so Bridge and device agree.
public static class Names
{
    public static string CallsTopic(string host, string deviceId) => $"co-{host}-dev-{deviceId}-calls";
    public static string CallsSubscription(string host, string deviceId) => CallsTopic(host, deviceId);
    public static string ResultsTopic(string host) => $"co-{host}-dev-results";
    public static string ResultsBridgeSubscription(string host) => $"co-{host}-dev-results-bridge";
    public static string EventsTopic(string host, string env) => $"co-{host}-events-{env}";
    public static string EventsDeviceSubscription(string host, string env, string deviceId) => $"co-{host}-events-{env}-dev-{deviceId}";
    public static string DeviceFolder(string deviceId) => $"CO-Device-{deviceId}";
    public const int SlotCount = 8;
    public static string SlotName(int i) => $"slot-{i:00}.json";

    public static bool IsValidDeviceId(string id) =>
        id.Length is >= 6 and <= 40 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

// Slot file of the Drive fallback (TZ §4.2): created by the device, filled by Bridge.
public sealed class SlotContent
{
    [JsonPropertyName("state")] public string State { get; set; } = "empty";   // empty | call | done
    [JsonPropertyName("call")] public ToolCall? Call { get; set; }
    [JsonPropertyName("doneCallId")] public string? DoneCallId { get; set; }
    // 0.9.8: state "cancelled": Bridge gave up on DoneCallId; signature over "co.tool.cancel.v1\n<callId>\n<deviceId>".
    [JsonPropertyName("cancelSig")] public string? CancelSig { get; set; }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Wire.Serialize(this));

    public static SlotContent Parse(byte[] b)
    {
        if (b.Length == 0) return new SlotContent();
        try { return JsonSerializer.Deserialize<SlotContent>(b, Wire.Json) ?? new SlotContent(); }
        catch (JsonException) { return new SlotContent(); }
    }
}
