// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Browser;

namespace CO.Core.Tests;

// Stands in for the CO browser: answers page scripts with canned results (e.g. a site's REST answers).
public sealed class FakeBrowser : IBrowser
{
    public string Url { get; set; } = "about:blank";
    public List<string> Scripts { get; } = new();
    public Func<string, string> Answer { get; set; } = _ => "null";

    public Task<string> NavigateAsync(string url, CancellationToken ct) { Url = url; return Task.FromResult(url); }
    public Task<string> CurrentUrlAsync(CancellationToken ct) => Task.FromResult(Url);

    public Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        Scripts.Add(expression);
        return Task.FromResult(JsonDocument.Parse(Answer(expression)).RootElement.Clone());
    }
}
