// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Connectors;
using CO.Core.Policy;
using CO.Core.Tools;
using Xunit;

namespace CO.Core.Tests;

public class ConnectorTests
{
    private sealed class WriteTool : IDeviceTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = "demo_change", Class = RiskClass.W, Untrusted = UntrustedMode.Never, Description = "demo", InputSchema = Schema.Obj(),
        };
        public ActionPlan Plan(JsonElement a, ToolEnv env) => new() { Tool = "demo_change", Class = RiskClass.W, Summary = "demo" };
        public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct) => Task.FromResult(new ToolOutput { Content = "done" });
    }

    private sealed class DemoConnector : Connector
    {
        public override string Id => "demo";
        public override string Name => "Demo";
        public override string Description => "demo";
        public override Rights Supported => Rights.Read | Rights.Create;
        public override IReadOnlyCollection<string> Tools => new[] { "demo_change" };
        protected override Task<ConnectorStatus> ProbeAsync(CancellationToken ct) => Task.FromResult(Ready("ok"));
    }

    [Fact]
    public async Task BoxesGateTheConnectorTools()
    {
        var reg = new ConnectorRegistry(null);
        var c = reg.Add(new DemoConnector());
        using var f = new Fixture(extraTools: new[] { reg.Gate(c, new WriteTool()) });

        Assert.Equal("connector_off", (await f.Run("demo_change", new { })).Error!.Code);
        c.Rights = Rights.Read;
        Assert.Equal("connector_read_only", (await f.Run("demo_change", new { })).Error!.Code);
        c.Rights = Rights.Read | Rights.Create;
        var ok = await f.Run("demo_change", new { });
        Assert.True(ok.Ok, ok.Error?.Message);
        Assert.Empty(f.Approvals.Seen);
    }

    [Fact]
    public void RightsAreStoredAndUnsupportedBoxesNeverStick()
    {
        string file = Path.Combine(Path.GetTempPath(), "co-conn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var reg = new ConnectorRegistry(file);
            var c = reg.Add(new DemoConnector());
            c.Rights = Rights.All;   // Edit and Delete do not apply to the demo service
            Assert.Equal(Rights.Read | Rights.Create, c.Rights);
            c.Rights = Rights.None;
            var again = new ConnectorRegistry(file).Add(new DemoConnector());
            Assert.Equal(Rights.None, again.Rights);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task StatusToolReportsBoxesAndLiveHealth()
    {
        var reg = new ConnectorRegistry(null);
        var demo = reg.Add(new DemoConnector());
        demo.Rights = Rights.Read;
        reg.Add(new PlannedConnector("later", "Later", "comes later"));
        using var f = new Fixture(managed: true, extraTools: new IDeviceTool[] { new ConnectorsStatusTool(reg) });

        var r = await f.Run("connectors_status", new { check = true });
        Assert.True(r.Ok, r.Error?.Message);
        using var doc = JsonDocument.Parse(r.Content!);
        var byId = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal("Ready", byId["demo"].GetProperty("health").GetString());
        Assert.Equal("ok", byId["demo"].GetProperty("detail").GetString());
        Assert.Equal("read", byId["demo"].GetProperty("rights").GetString());
        Assert.True(byId["later"].GetProperty("planned").GetBoolean());
        demo.Rights = Rights.None;
        using var off = JsonDocument.Parse((await f.Run("connectors_status", new { check = true })).Content!);
        Assert.Equal("Off", off.RootElement.EnumerateArray().First(e => e.GetProperty("id").GetString() == "demo").GetProperty("health").GetString());
    }

    [Fact]
    public void SettingsOf060AreReadAsBoxes()
    {
        Assert.Equal(Rights.None, ConnectorRegistry.Parse("Off"));
        Assert.Equal(Rights.Read, ConnectorRegistry.Parse("Read"));
        Assert.Equal(Rights.Read | Rights.Create | Rights.Edit, ConnectorRegistry.Parse("Write"));
        Assert.Equal(Rights.Read | Rights.Create, ConnectorRegistry.Parse("Read, Create"));
    }

}
