// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Browser;

namespace CO.Corporate.Tools;

// 0.8.8: SAP Fiori of Tractebel (internal host, Okta SSO). The "sap" tab of the CO browser is kept on the SAP origin so
// fetch() from sap_get / sap_write carries the user's session. Same sign-in loop as the Tractebel Portal: buttons
// without a secret (FastPass, push) are pressed; otherwise the user finishes the sign-in in the CO browser.
public static partial class SapTools
{
    public const string Host = "tl0tp4pc.tractebel.suez";
    public const string Base = "https://" + Host + ":44385";
    // A light JSON page of the origin; after the Okta round trip the tab lands back here.
    public const string Entry = Base + "/sap/opu/odata/sap/HCMFAB_COMMON_SRV/?$format=json";

    public static TimeSpan SignInWait { get; set; } = TimeSpan.FromSeconds(90);

    internal static async Task EnsureOnSapAsync(IBrowser b, CancellationToken ct, bool force = false)
    {
        if (!force && SharePointApi.OnHost(await b.CurrentUrlAsync(ct), Host)) return;
        string here = await b.NavigateAsync(Entry, ct);
        if (SharePointApi.OnHost(here, Host) && !force) return;
        DateTime until = DateTime.UtcNow + SignInWait;
        bool shown = false;
        int quiet = 0;
        while (DateTime.UtcNow < until)
        {
            string url = await b.CurrentUrlAsync(ct);
            if (SharePointApi.OnHost(url, Host)) return;
            await Task.Delay(SharePointApi.SignInPoll, ct);
            JsonElement step = default;
            try { step = await b.EvaluateAsync(SharePointApi.AutoStepJs, ct); } catch (InvalidOperationException) { }
            quiet = step.ValueKind == JsonValueKind.String && step.GetString()!.Length > 0 ? 0 : quiet + 1;
            if (!shown && quiet >= 4) { await b.BringToFrontAsync(ct); shown = true; }
        }
        throw new ToolException("sign_in_needed",
            "SAP Fiori (" + Host + ") needs the Okta sign-in; the CO browser shows the sign-in page. Ask the user to complete it there, then retry.");
    }

    // The request was redirected to the identity provider or answered with a sign-in form instead of data.
    internal static bool NeedsSession(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return false;
        // An expired session redirects fetch() to the identity provider; the browser blocks that cross-origin answer
        // and fetch() throws (review 0.8.8): treat it as a session problem, the retry signs in again.
        if ((Str(r, "error") ?? "").StartsWith("fetch_failed", StringComparison.Ordinal)) return true;
        string url = Str(r, "url") ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return true;
        string type = Str(r, "type") ?? "";
        string text = Str(r, "text") ?? "";
        return type.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"<form[^>]+(login|okta|saml)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
