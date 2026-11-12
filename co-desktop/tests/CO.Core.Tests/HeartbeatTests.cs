// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Connectors;
using CO.Core.Protocol;
using CO.Core.Transport;
using Xunit;

namespace CO.Core.Tests;

// Freedom F1 5b (stage C): the heartbeat carries the connector session as corp {id, state, num} and, for older Bridge,
// as engie / engieNum; without a connector session corp is left out.
public class HeartbeatTests
{
    [Fact]
    public void ConnectorSessionGoesUnderBothNames()
    {
        var (s, n, i) = (ConnectorHooks.HeartbeatSession, ConnectorHooks.HeartbeatNumber, ConnectorHooks.HeartbeatConnectorId);
        try
        {
            ConnectorHooks.HeartbeatSession = () => "signin";
            ConnectorHooks.HeartbeatNumber = () => "x1:42";
            ConnectorHooks.HeartbeatConnectorId = () => "acme";
            using var j = JsonDocument.Parse(Wire.Serialize(new Heartbeat { DeviceId = "d" }.WithConnector()));
            var r = j.RootElement;
            Assert.Equal("signin", r.GetProperty("engie").GetString());
            Assert.Equal("x1:42", r.GetProperty("engieNum").GetString());
            Assert.Equal("acme", r.GetProperty("corp").GetProperty("id").GetString());
            Assert.Equal("signin", r.GetProperty("corp").GetProperty("state").GetString());
            Assert.Equal("x1:42", r.GetProperty("corp").GetProperty("num").GetString());

            ConnectorHooks.HeartbeatSession = () => "";
            ConnectorHooks.HeartbeatNumber = () => "";
            using var j2 = JsonDocument.Parse(Wire.Serialize(new Heartbeat { DeviceId = "d" }.WithConnector()));
            Assert.False(j2.RootElement.TryGetProperty("corp", out _));
            Assert.Equal("", j2.RootElement.GetProperty("engie").GetString());
        }
        finally
        {
            (ConnectorHooks.HeartbeatSession, ConnectorHooks.HeartbeatNumber, ConnectorHooks.HeartbeatConnectorId) = (s, n, i);
        }
    }
}
