// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Core.Tools;

// TZ §7.4: tools of the CO browser (open a page, read it) in this job's own tab. Connector modules add their own tools
// on the same tab source (BrowserSource).
public static class BrowserTools
{
    // One shared browser (tests, single-tab hosts).
    public static IEnumerable<IDeviceTool> All(IBrowser browser) => All(_ => new BrowserUse(browser, null));

    // 0.9.0: each job (RunId) uses its own tab "agent:<RunId>" of the CO browser, closed after 30 min idle.
    public static IEnumerable<IDeviceTool> All(RunTabs<EdgeBrowser.EdgeTab> tabs) => All(JobTabs(tabs));

    // The job's own tab as a BrowserSource (also handed to connector modules).
    public static BrowserSource JobTabs(RunTabs<EdgeBrowser.EdgeTab> tabs) => env =>
    {
        var lease = tabs.Use(env.RunId);
        return new BrowserUse(lease.Value, lease);
    };

    public static IEnumerable<IDeviceTool> All(BrowserSource source) => new IDeviceTool[]
    {
        new BrowserOpen(source), new BrowserRead(source),
    };

    public static string Domain(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

    public static JsonObject UrlSchema(string desc, params (string name, string type, string d, bool req)[] more)
    {
        var all = new List<(string, string, string, bool)> { ("url", "string", desc, true) };
        all.AddRange(more);
        return Schema.Obj(all.ToArray());
    }
}

// 0.9.0: the browser a tool call works with, and the lease that keeps the job's tab from being closed meanwhile.
public sealed class BrowserUse : IDisposable
{
    private IDisposable? _lease;
    public BrowserUse(IBrowser browser, IDisposable? lease) { Browser = browser; _lease = lease; }
    public IBrowser Browser { get; }
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

public delegate BrowserUse BrowserSource(ToolEnv env);

internal sealed class BrowserOpen : IDeviceTool
{
    private readonly BrowserSource _src;
    public BrowserOpen(BrowserSource src) => _src = src;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "browser_open", Class = RiskClass.N, Untrusted = UntrustedMode.Always,
        Description = "Open a URL in the CO browser (the user's separate Edge profile, signed in to their sites). Each job has its own tab. Returns the final address and title.",
        InputSchema = BrowserTools.UrlSchema("https URL"),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string url = Args.Str(a, "url");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new ToolException("invalid_args", "Only https URLs.");
        var p = new ActionPlan { Tool = "browser_open", Class = RiskClass.N, NavigationDomain = BrowserTools.Domain(url), Summary = "open " + url };
        p.Scopes.Add(new Scope(ScopeKind.Domain, BrowserTools.Domain(url)));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        using var use = _src(env);
        var _b = use.Browser;
        string landed = await _b.NavigateAsync(Args.Str(a, "url"), ct);
        var title = await _b.EvaluateAsync("document.title", ct);
        return new ToolOutput { Content = $"Opened {landed}\nTitle: {title.GetString()}", Untrusted = true };
    }
}

internal sealed class BrowserRead : IDeviceTool
{
    private readonly BrowserSource _src;
    public BrowserRead(BrowserSource src) => _src = src;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "browser_read", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Text and links of the page open in this job's tab of the CO browser (the page browser_open opened).",
        InputSchema = Schema.Obj(("maxChars", "integer", "Default 60000", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env) => new() { Tool = "browser_read", Class = RiskClass.R, Summary = "read current page" };

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        using var use = _src(env);
        var _b = use.Browser;
        long max = Math.Clamp(Args.OptLong(a, "maxChars", 60_000), 1000, 200_000);
        var r = await _b.EvaluateAsync(
            "({ url: location.href, title: document.title, text: (document.body ? document.body.innerText : '').slice(0, " + max + ")," +
            " links: Array.from(document.links).slice(0, 300).map(l => (l.innerText || '').trim().slice(0, 80) + ' -> ' + l.href) })", ct);
        var sb = new StringBuilder();
        sb.AppendLine("URL: " + r.GetProperty("url").GetString()).AppendLine("Title: " + r.GetProperty("title").GetString()).AppendLine();
        sb.AppendLine(r.GetProperty("text").GetString()).AppendLine().AppendLine("Links:");
        foreach (var l in r.GetProperty("links").EnumerateArray()) sb.AppendLine(l.GetString());
        return new ToolOutput { Content = sb.ToString(), Untrusted = true };
    }
}
