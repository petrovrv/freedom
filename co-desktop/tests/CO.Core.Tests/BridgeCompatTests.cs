// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using AiWorkbenchBridge;
using CO.Core.Crypto;
using CO.Core.Protocol;
using Xunit;

namespace CO.Core.Tests;

// Bridge (C# 5, JavaScriptSerializer dictionaries) and CO.Core (System.Text.Json) must sign identical bytes.
public class BridgeCompatTests
{
    private static Dictionary<string, object> ToDict(JsonElement e)
    {
        var d = new Dictionary<string, object>();
        foreach (var p in e.EnumerateObject())
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => p.Value.GetInt32(),
                JsonValueKind.Object => ToDict(p.Value),
                _ => null!,
            };
        return d;
    }

    [Fact]
    public async Task BridgeSignedCallIsAcceptedByDevice()
    {
        using var f = new Fixture();
        var call = new Dictionary<string, object>
        {
            ["v"] = 1, ["callId"] = "call-0001", ["runId"] = "JOB-1", ["taskId"] = "JOB-1", ["deviceId"] = "d-test01",
            ["tool"] = "fs_list", ["argsJson"] = "{\"path\":\"\"}", ["issuedUtc"] = Wire.Now(f.Now), ["tainted"] = false, ["timeoutMs"] = 120000,
        };
        call["sig"] = Convert.ToBase64String(f.Bridge.Sign(DeviceWire.CallPayload(call)));
        string json = JsonSerializer.Serialize(call);

        ToolCall parsed = Wire.Deserialize<ToolCall>(json);
        ToolResult r = await f.Host.HandleAsync(parsed, default);
        Assert.True(r.Ok, r.Error?.Message);
    }

    // 0.9.8 / Bridge 1.9.2: a call with a chat id is signed as v2; the device accepts it, a changed chat id fails.
    [Fact]
    public async Task BridgeSignedChatCallIsAcceptedAndChatIdIsBound()
    {
        using var f = new Fixture();
        var call = new Dictionary<string, object>
        {
            ["v"] = 1, ["callId"] = "call-0002", ["runId"] = "JOB-2", ["taskId"] = "JOB-2", ["deviceId"] = "d-test01",
            ["tool"] = "fs_list", ["argsJson"] = "{\"path\":\"\"}", ["issuedUtc"] = Wire.Now(f.Now), ["tainted"] = false,
            ["timeoutMs"] = 120000, ["chatId"] = "CHAT-20261108145334-0VDF",
        };
        call["sig"] = Convert.ToBase64String(f.Bridge.Sign(DeviceWire.CallPayload(call)));
        ToolCall parsed = Wire.Deserialize<ToolCall>(JsonSerializer.Serialize(call));
        Assert.Equal("CHAT-20261108145334-0VDF", parsed.ChatId);
        Assert.Equal(Encoding.UTF8.GetString(parsed.SigningPayload()), Encoding.UTF8.GetString(DeviceWire.CallPayload(call)));
        ToolResult r = await f.Host.HandleAsync(parsed, default);
        Assert.True(r.Ok, r.Error?.Message);

        call["callId"] = "call-0003";
        call["sig"] = Convert.ToBase64String(f.Bridge.Sign(DeviceWire.CallPayload(call)));
        ToolCall forged = Wire.Deserialize<ToolCall>(JsonSerializer.Serialize(call));
        forged.ChatId = "CHAT-OTHER";
        ToolResult bad = await f.Host.HandleAsync(forged, default);
        Assert.False(bad.Ok);
        Assert.Equal("bad_signature", bad.Error?.Code);

        // A call Bridge cancelled (signed tool.cancel) is refused when it arrives later.
        call["callId"] = "call-0004";
        call["sig"] = Convert.ToBase64String(f.Bridge.Sign(DeviceWire.CallPayload(call)));
        Assert.False(f.Host.Cancel("call-0004", "AAAA"));
        Assert.True(f.Host.Cancel("call-0004", Convert.ToBase64String(f.Bridge.Sign(DeviceWire.Payload("co.tool.cancel.v1", "call-0004", "d-test01")))));
        ToolResult late = await f.Host.HandleAsync(Wire.Deserialize<ToolCall>(JsonSerializer.Serialize(call)), default);
        Assert.Equal("replay", late.Error?.Code);

        var env = new CO.Core.Tools.ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-2", Call = parsed };
        Assert.Equal("chat-CHAT-20261108145334-0VDF", env.TabOwner);
        Assert.Equal("JOB-9", new CO.Core.Tools.ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-9" }.TabOwner);
    }

    [Fact]
    public async Task DeviceResultVerifiesOnBridgeSide()
    {
        using var f = new Fixture();
        foreach (var args in new object[] { new { path = "" }, new { path = "missing/x" } })   // ok and error results
        {
            ToolResult r = await f.Run("fs_list", args);
            var dict = ToDict(JsonDocument.Parse(Wire.Serialize(r)).RootElement);
            Assert.Equal(Encoding.UTF8.GetString(r.SigningPayload()), Encoding.UTF8.GetString(DeviceWire.ResultPayload(dict)));
            Assert.True(KeyUtil.Verify(f.Device.PublicKey, DeviceWire.ResultPayload(dict), DeviceWire.S(dict, "sig")));
        }
    }
}
