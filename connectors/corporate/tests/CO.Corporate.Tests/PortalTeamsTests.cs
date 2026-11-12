// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Connectors;
using CO.Core.Policy;
using CO.Core.Tools;
using Xunit;

namespace CO.Corporate.Tests;

public class PortalTeamsTests
{
    [Fact]
    public void SearchUrlHasThePortalFormFields()
    {
        string u = TractebelTools.SearchUrl("0768841", "P.012345", "site layout", "", "", "", "", allRevisions: true);
        Assert.StartsWith("https://teportal.tractebel.myengie.com/HyperInfoIIS.dll?action=tblsql&object=165833&", u);
        Assert.Contains("&NUMBER=0768841&", u);
        Assert.Contains("PROJ_NUMBERname=P.012345", u);
        Assert.Contains("SUBJECT=site%20layout", u);
        Assert.Contains("CHOICEBTN_ALL_REV=ALL_REV&ALL_REV=ALL_REV", u);
    }

    [Fact]
    public async Task SearchReturnsDocumentsFoundOnThePortalPage()
    {
        var b = new FakeBrowser
        {
            Url = "https://teportal.tractebel.myengie.com/HyperInfoIIS.dll",
            Answer = js => js == TractebelTools.UserJs ? """{ "user": "XZ6485" }""" : """{ "status": 200, "docs": [ { "id": "8120001", "url": "https://teportal.tractebel.myengie.com/HyperInfoIIS.dll?object=128500;:A=8120001", "row": "DECOM/4SR/0768841/000/06 Site layout" } ], "total": 1 }""",
        };
        using var f = new Fixture(managed: true, extraTools: TractebelTools.All(b));
        var r = await f.Run("te_search", new { number = "0768841" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Contains("8120001", r.Content);
        Assert.Contains(b.Scripts, s => s.Contains("object=165833"));
        Assert.Empty(f.Approvals.Seen);   // connector ticked = consent, also on a managed device
    }

    [Fact]
    public async Task PortalLoginPageAsksTheUserToSignIn()
    {
        // The portal keeps answering as a guest: after the sign-in wait the user is asked to sign in.
        var b = new FakeBrowser { Url = "https://teportal.tractebel.myengie.com/x", Answer = js => js == TractebelTools.UserJs ? """{ "user": "GUEST_SUEZ" }""" : "\"\"" };
        TractebelTools.SignInWait = TimeSpan.FromSeconds(2);
        using var f = new Fixture(extraTools: TractebelTools.All(b));
        var r = await f.Run("te_doc", new { id = "8120001" });
        Assert.Equal("sign_in_needed", r.Error!.Code);
    }

    [Fact]
    public void DocumentIdComesFromIdOrCardLink()
    {
        Assert.Equal("8120001", TractebelTools.DocId("8120001"));
        Assert.Equal("8120001", TractebelTools.DocId("https://teportal.tractebel.myengie.com/HyperInfoIIS.dll?object=128500;:A=8120001"));
        Assert.Throws<ToolException>(() => TractebelTools.DocId("abc"));
    }

    [Fact]
    public async Task TeamsChatsComeFromTheChatServiceOfTheWebClient()
    {
        var b = new FakeBrowser
        {
            Url = "https://teams.microsoft.com/v2/",
            Answer = _ => """{ "chats": [ { "id": "19:abc@thread.v2", "topic": "Chertal layout", "type": "Chat", "last": { "from": "Quentin", "time": "2026-10-15T09:00:00Z", "text": "Can you send the layout note?" } } ] }""",
        };
        using var f = new Fixture(managed: true, extraTools: TeamsTools.All(b));
        var r = await f.Run("teams_chats", new { });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Contains("Can you send the layout note?", r.Content);
        string js = b.Scripts.Single();
        Assert.Contains("api/authsvc/v1.0/authz", js);          // Skype token from the client's api.spaces.skype.com token
        Assert.Contains("/v1/users/ME/conversations", js);
        Assert.DoesNotContain("Bearer", r.Content);
        Assert.Empty(f.Approvals.Seen);
    }

    [Fact]
    public async Task TeamsWithoutReadIsOff()
    {
        var reg = new ConnectorRegistry(null);
        var b = new FakeBrowser { Url = "https://teams.microsoft.com/v2/" };
        var teams = reg.Add(new TeamsConnector(b));
        teams.Rights = Rights.None;
        using var f = new Fixture(extraTools: TeamsTools.All(b).Select(t => reg.Gate(teams, t, Rights.Read)));
        Assert.Equal("connector_off", (await f.Run("teams_chats", new { })).Error!.Code);
    }
}
