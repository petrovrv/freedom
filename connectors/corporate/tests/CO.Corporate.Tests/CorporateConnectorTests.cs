// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace CO.Corporate.Tests;

// Connectors of the corporate module (moved from CO.Core ConnectorTests / CloudConnectorTests, Freedom F1).
public class CorporateConnectorTests
{
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
    public async Task EngieIsReadByDefaultAndCanBeTurnedOff()
    {
        var reg = new ConnectorRegistry(null);
        var engie = reg.Add(new EngieConnector(new FakeBrowser()));
        Assert.Equal(Rights.Read, engie.Rights);
        using var f = new Fixture(managed: true, extraTools: EngieTools.All(new FakeBrowser()).Select(t => reg.Gate(engie, t)));
        Assert.NotEqual("connector_off", (await f.Run("engie_chat", new { model = "gpt-6-luna", prompt = "hi" })).Error?.Code);
        engie.Rights = Rights.None;
        var r = await f.Run("engie_chat", new { model = "gpt-6-luna", prompt = "hi" });
        Assert.Equal("connector_off", r.Error!.Code);
        Assert.Contains("Connectors → ENGIE AI Studio", r.Error.Message);
    }

    [Fact]
    public void OutlookBoxesMapOntoOutlookSettings()
    {
        var s = new OutlookSettings();
        var o = new ConnectorRegistry(null).Add(new OutlookConnector(new FakeOutlook(), s, new[] { "outlook_search" }));
        Assert.Equal(Rights.None, o.Rights);
        o.Rights = Rights.All;                                   // Delete: own calendar events (mail is never deleted)
        Assert.Equal(Rights.All, o.Rights);
        Assert.True(s.CanDelete);
        o.Rights = Rights.Read | Rights.Create;
        Assert.True(s.Read && s.CanCreate && !s.CanEdit && !s.CanDelete);
        // 0.6.0 settings: Write meant drafts and changes.
        Assert.True(new OutlookSettings { Read = true, Write = true }.CanEdit);
    }

    [Fact]
    public async Task OutlookDraftNeedsCreateMoveNeedsEdit()
    {
        var o = new FakeOutlook();
        var s = new OutlookSettings { Read = true, Create = true, Edit = false };
        using var f = new Fixture(managed: true, extraTools: OutlookTools.All(o, s));
        Assert.True((await f.Run("outlook_draft", new { to = "a@b.c", subject = "s", body = "b" })).Ok);
        Assert.Equal("outlook_disabled", (await f.Run("outlook_move", new { id = "m1", folder = "Inbox/Projects" })).Error!.Code);
    }

    [Fact]
    public async Task StatusToolReportsBoxesAndLiveHealth()
    {
        var reg = new ConnectorRegistry(null);
        reg.Add(new OutlookConnector(new FakeOutlook(), new OutlookSettings { Read = true }, new[] { "outlook_search" }));
        reg.Add(new PlannedConnector("teams", "Teams", "chats"));
        reg.Add(new DemoConnector());
        using var f = new Fixture(managed: true, extraTools: new IDeviceTool[] { new ConnectorsStatusTool(reg) });

        var r = await f.Run("connectors_status", new { check = true });
        Assert.True(r.Ok, r.Error?.Message);
        using var doc = JsonDocument.Parse(r.Content!);
        var byId = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal("Ready", byId["outlook"].GetProperty("health").GetString());
        Assert.Equal("3 mail folders", byId["outlook"].GetProperty("detail").GetString());
        Assert.Equal("read", byId["outlook"].GetProperty("rights").GetString());
        Assert.True(byId["teams"].GetProperty("planned").GetBoolean());
        Assert.Equal("Off", byId["demo"].GetProperty("health").GetString());
    }

    [Fact]
    public void OneDrivePersonalSiteIsRecognized()
    {
        Assert.True(OneDriveConnector.IsPersonal("https://engie-my.sharepoint.com/personal/anna_smith/Documents/X"));
        Assert.False(OneDriveConnector.IsPersonal("https://engie.sharepoint.com/sites/B/Shared%20Documents"));
    }
    [Fact]
    public void RightsAreStoredAndUnsupportedBoxesNeverStick()
    {
        string file = Path.Combine(Path.GetTempPath(), "co-conn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var reg = new ConnectorRegistry(file);
            var sp = reg.Add(new SharePointConnector(new CO.Core.Files.FolderRegistry()));
            sp.Rights = Rights.All;   // SharePoint tools only read
            Assert.Equal(Rights.Read, sp.Rights);
            sp.Rights = Rights.None;
            var again = new ConnectorRegistry(file).Add(new SharePointConnector(new CO.Core.Files.FolderRegistry()));
            Assert.Equal(Rights.None, again.Rights);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void EngieUsageTrailerCarriesTokenCounts()
    {
        using var doc = JsonDocument.Parse("""{ "ok": true, "text": "hi", "usage": { "input_tokens": 1200, "output_tokens": 340 } }""");
        string t = EngieChatUsage.Trailer("claude-opus-5.5", doc.RootElement);
        Assert.Equal("\n\n<!-- co:usage {\"model\":\"claude-opus-5.5\",\"input\":1200,\"output\":340} -->", t);
        using var none = JsonDocument.Parse("""{ "ok": true, "text": "hi" }""");
        Assert.Equal("", EngieChatUsage.Trailer("m", none.RootElement));
    }
}
