// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Tools;
using Xunit;

namespace CO.Corporate.Tests;

public class SapToolsTests
{
    const string TimesheetPath = "/sap/opu/odata/sap/HCMFAB_TIMESHEET_MAINT_SRV/TimeEntryCollection";

    static FakeBrowser Browser(string answer) => new()
    {
        Url = SapTools.Base + "/sap/x",
        Answer = _ => answer
    };

    [Fact]
    public void CheckPath_AcceptsODataPath()
    {
        var path = TimesheetPath + "?$filter=Pernr%20eq%20'1'&$format=json";
        Assert.Equal(SapTools.Base + path, SapTools.CheckPath(path));
    }

    public static IEnumerable<object[]> BadPaths()
    {
        yield return new object[] { "/sapx/opu/odata" };                       // wrong prefix
        yield return new object[] { "https://evil/sap/x" };                   // not a path
        yield return new object[] { "/sap/opu/" + "." + "./x" };              // two dots
        yield return new object[] { "/sap/x?u=http" + ":" + "/" + "/evil" };  // colon + two slashes
        yield return new object[] { "/sap/x" + "\\" + "y" };                  // backslash
        yield return new object[] { "/sap/x" + " " + "y" };                   // space
        yield return new object[] { "/sap/x" + "\t" + "y" };                  // tab
        yield return new object[] { "/sap/x" + "\n" };                        // newline
    }

    [Theory]
    [MemberData(nameof(BadPaths))]
    public void CheckPath_RejectsBadPaths(string path)
    {
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckPath(path));
        Assert.Equal("invalid_args", ex.Code);
    }

    [Fact]
    public void ServiceRoot_StripsCollectionAndQuery()
    {
        var root = SapTools.ServiceRoot(TimesheetPath + "?$top=5&$format=json");
        Assert.Equal("/sap/opu/odata/sap/HCMFAB_TIMESHEET_MAINT_SRV/", root);
    }

    [Fact]
    public void ServiceRoot_RejectsNonODataPath()
    {
        var ex = Assert.Throws<ToolException>(() => SapTools.ServiceRoot("/sap/bc/ui5_ui5/x"));
        Assert.Equal("invalid_args", ex.Code);
    }

    [Fact]
    public async Task SapGet_PagesText()
    {
        var b = Browser("{\"status\":200,\"type\":\"application/json\",\"text\":\"0123456789\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_get", new { path = TimesheetPath + "?$format=json", offset = 2, length = 3 });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Contains("234", r.Content);
        Assert.Matches("\"total\":\\s*10", r.Content);
    }

    [Fact]
    public async Task SapWrite_FetchesTokenAndPosts()
    {
        var b = Browser("{\"status\":201,\"type\":\"application/json\",\"text\":\"{}\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { method = "POST", path = TimesheetPath, body = "{}" });
        Assert.True(r.Ok, r.Error?.Message);
        var js = b.Scripts.Last();
        Assert.Contains("X-CSRF-Token", js);
        Assert.Contains("Fetch", js);
        Assert.Contains("POST", js);
    }

    [Fact]
    public async Task SapGet_ErrorBecomesSapError()
    {
        var b = Browser("{\"error\":\"something else\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_get", new { path = TimesheetPath + "?$format=json" });
        Assert.False(r.Ok);
        Assert.Equal("sap_error", r.Error!.Code);
    }

    [Fact]
    public async Task Blocked_redirect_to_sign_in_asks_for_sign_in()
    {
        var b = Browser("{\"error\":\"fetch_failed Failed to fetch\"}");
        SapTools.SignInWait = TimeSpan.FromSeconds(1);
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_get", new { path = TimesheetPath + "?$format=json" });
        Assert.Equal("sign_in_needed", r.Error!.Code);
    }

    [Fact]
    public async Task RedirectToIdentityProviderAsksForSignIn()
    {
        // Every answer comes from the Okta host: after one fresh sign-in round the user is asked to sign in.
        var b = Browser("{\"status\":200,\"url\":\"https://engie.okta-emea.com/x\",\"type\":\"text/html\",\"text\":\"\"}");
        SapTools.SignInWait = TimeSpan.FromSeconds(1);
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_get", new { path = TimesheetPath + "?$format=json" });
        Assert.Equal("sign_in_needed", r.Error!.Code);
    }

    [Fact]
    public void All_returns_four_sap_tools()
    {
        var names = SapTools.All(new FakeBrowser()).Select(t => t.Descriptor.Name).ToArray();
        Assert.Equal(new[] { "sap_get", "sap_write", "sap_update", "sap_delete" }, names);
    }

    [Fact]
    public async Task Sap_write_to_non_allowlisted_service_is_not_allowed()
    {
        var b = Browser("{\"status\":201,\"text\":\"{}\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = "/sap/opu/odata/sap/ZHR_OTHER_SRV/ItemSet", body = "{}" });
        Assert.Equal("not_allowed", r.Error!.Code);
        Assert.Empty(b.Scripts);
    }

    [Fact]
    public async Task Sap_write_http_400_becomes_tool_error_with_sap_message()
    {
        var b = Browser("{\"status\":400,\"type\":\"application/json\",\"text\":\"{\\\"error\\\":{\\\"message\\\":{\\\"value\\\":\\\"Bad thing\\\"}}}\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = TimesheetPath, body = "{}" });
        Assert.Equal("sap_http_400", r.Error!.Code);
        Assert.Contains("Bad thing", r.Error!.Message);
    }

    [Fact]
    public async Task Sap_get_http_404_becomes_tool_error()
    {
        var b = Browser("{\"status\":404,\"type\":\"text/plain\",\"text\":\"Not found\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_get", new { path = "/sap/opu/odata/sap/ANY_SRV/Nope" });
        Assert.Equal("sap_http_404", r.Error!.Code);
        Assert.Contains("Not found", r.Error!.Message);
    }

    [Fact]
    public async Task Sap_update_with_delete_method_is_invalid()
    {
        var b = Browser("{\"status\":204,\"text\":\"\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_update", new { method = "DELETE", path = "/sap/opu/odata/sap/HCMFAB_LEAVE_REQUEST_CR_SRV/LeaveRequestSet('1')" });
        Assert.Equal("invalid_args", r.Error!.Code);
        Assert.Empty(b.Scripts);
    }

    [Fact]
    public void Sap_delete_plan_is_destructive_and_rejects_other_services()
    {
        var tool = SapTools.All(new FakeBrowser()).Single(t => t.Descriptor.Name == "sap_delete");
        var plan = tool.Plan(JsonDocument.Parse("{\"path\":\"/sap/opu/odata/sap/HCMFAB_LEAVE_REQUEST_CR_SRV/LeaveRequestSet('1')\"}").RootElement, null!);
        Assert.Equal(RiskClass.D, plan.Class);
        Assert.True(plan.PermittedByUser);   // the connector gate requires the Delete box
        var ex = Assert.Throws<ToolException>(() => tool.Plan(JsonDocument.Parse("{\"path\":\"/sap/opu/odata/sap/ZHR_OTHER_SRV/ItemSet('1')\"}").RootElement, null!));
        Assert.Equal("not_allowed", ex.Code);
    }

    const string Ts = "HCMFAB_TIMESHEET_MAINT_SRV";
    const string Lr = "HCMFAB_LEAVE_REQUEST_CR_SRV";
    const string LrPath = "/sap/opu/odata/sap/HCMFAB_LEAVE_REQUEST_CR_SRV/LeaveRequestSet";
    static string Teo(string op) => "{\"Pernr\":\"00001234\",\"TimeEntryOperation\":\"" + op + "\"}";

    [Fact]
    public void SapWrite_Create_Passes() => SapTools.CheckOperation("sap_write", Ts, "POST", TimesheetPath, Teo("C"));

    [Fact]
    public void SapWrite_DeleteWithSpaces_IsRejected_AndPointsToSapDelete()
    {
        const string body = "{\"Pernr\":\"00001234\",\"TimeEntryOperation\" : \"D\"}";
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckOperation("sap_write", Ts, "POST", TimesheetPath, body));
        Assert.Equal("not_allowed", ex.Code);
        Assert.Contains("sap_delete", ex.Message);
    }

    [Fact]
    public void SapDelete_Delete_Passes() => SapTools.CheckOperation("sap_delete", Ts, "POST", TimesheetPath, Teo("D"));

    [Fact]
    public void SapUpdate_Update_Passes() => SapTools.CheckOperation("sap_update", Ts, "POST", TimesheetPath, Teo("U"));

    [Fact]
    public void Batch_MixingCreateAndDelete_IsRejected()
    {
        string body = "--batch_1\r\n\r\nPOST TimeEntryCollection HTTP/1.1\r\n\r\n" + Teo("C") +
            "\r\n--batch_1\r\n\r\nPOST TimeEntryCollection HTTP/1.1\r\n\r\n" + Teo("D") + "\r\n--batch_1--";
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckOperation("sap_write", Ts, "POST", "/sap/opu/odata/sap/HCMFAB_TIMESHEET_MAINT_SRV/$batch", body));
        Assert.Equal("not_allowed", ex.Code);
    }

    [Fact]
    public void LeaveRequest_SapWrite_Withdraw_IsRejected()
    {
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckOperation("sap_write", Lr, "POST", LrPath, "{\"ActionCode\":3}"));
        Assert.Equal("not_allowed", ex.Code);
    }

    [Fact]
    public void CheckPath_RejectsEncodedDotDotAndSlash()
    {
        Assert.Throws<ToolException>(() => SapTools.CheckPath("/sap/opu/odata/sap/" + Ts + "/" + "%" + "2e" + "%" + "2e" + "/OTHER_SRV/X"));
        Assert.Throws<ToolException>(() => SapTools.CheckPath("/sap/opu/odata/sap/" + "A" + "%" + "2f" + "B/X"));
    }

    [Fact]
    public async Task SapWrite_tool_refuses_a_timesheet_delete()
    {
        var b = Browser("{\"status\":201,\"text\":\"{}\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = TimesheetPath, body = Teo("D") });
        Assert.Equal("not_allowed", r.Error!.Code);
        Assert.Empty(b.Scripts);
    }

    [Fact]
    public void Batch_with_inner_delete_needs_sap_delete()
    {
        var body = "--batch_1\r\nContent-Type: multipart/mixed; boundary=changeset_1\r\n\r\n--changeset_1\r\nContent-Type: application/http\r\n\r\n" +
                   "DELETE LeaveRequestSet('1') HTTP/1.1\r\n\r\n--changeset_1--\r\n--batch_1--";
        Assert.Equal("Delete", Assert.Single(SapTools.BatchNeeds(body)).Right);
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckOperation("sap_write", Lr, "POST", "/sap/opu/odata/sap/HCMFAB_LEAVE_REQUEST_CR_SRV/$batch", body));
        Assert.Equal("not_allowed", ex.Code);
    }

    [Fact]
    public void Batch_with_merge_override_is_an_edit()
    {
        var body = "--batch_1\r\nContent-Type: application/http\r\n\r\nPOST TimeEntryCollection HTTP/1.1\r\nx-http-method: merge\r\n\r\n{}\r\n--batch_1--";
        Assert.Equal("Edit", Assert.Single(SapTools.BatchNeeds(body)).Right);
        Assert.Empty(SapTools.BatchNeeds("{\"Hours\":\"8\"}"));
    }

    [Fact]
    public void Escaped_marker_is_refused()
    {
        var body = "{\"TimeEntryOper" + "\\" + "u0061tion\":\"D\"}";
        var ex = Assert.Throws<ToolException>(() => SapTools.CheckOperation("sap_write", Ts, "POST", TimesheetPath, body));
        Assert.Equal("not_allowed", ex.Code);
    }

    [Fact]
    public async Task Token_request_on_the_sign_in_page_asks_for_sign_in()
    {
        var b = Browser("{\"error\":\"no_csrf_token\",\"status\":200,\"url\":\"https://engie.okta-emea.com/x\",\"type\":\"text/html\",\"text\":\"\"}");
        SapTools.SignInWait = TimeSpan.FromSeconds(1);
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = TimesheetPath, body = Teo("C") });
        Assert.Equal("sign_in_needed", r.Error!.Code);
    }

    [Fact]
    public async Task Non_json_body_is_refused()
    {
        var b = Browser("{\"status\":201,\"text\":\"{}\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = TimesheetPath, body = "<x/>", content_type = "application/xml" });
        Assert.Equal("invalid_args", r.Error!.Code);
    }

    [Fact]
    public async Task Lost_answer_of_a_write_is_not_replayed()
    {
        var b = Browser("{\"error\":\"outcome_unknown: x\"}");
        using var f = new Fixture(extraTools: SapTools.All(b));
        var r = await f.Run("sap_write", new { path = TimesheetPath, body = Teo("C") });
        Assert.Equal("sap_error", r.Error!.Code);
        Assert.Single(b.Scripts, s => s.Contains("X-CSRF-Token"));
        Assert.Contains("outcome_unknown", b.Scripts.Last());
    }
}
