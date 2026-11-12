// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Connectors;
using CO.Core.Policy;
using Xunit;

namespace CO.Core.Tests;

public class CloudConnectorTests
{
    private const string Sample = """
    {
      "schemaVersion": 1,
      "connectors": [
        { "id": "gmail", "name": "Gmail", "description": "mail", "rights": "Read, Create",
          "tools": { "Read": ["mcp__claude_ai_Gmail__search_threads"], "Create": ["mcp__claude_ai_Gmail__create_draft"],
                     "Edit": ["mcp__claude_ai_Gmail__label_thread"], "Delete": [], "Never": ["mcp__claude_ai_Gmail__send_message"] } }
      ]
    }
    """;

    [Fact]
    public void RowsComeFromBridgeFileAndBoxesAreWrittenBack()
    {
        string file = Path.Combine(Path.GetTempPath(), "co-cloud-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, Sample);
            var g = Assert.Single(CloudConnectors.Load(file));
            Assert.Equal(Rights.Read | Rights.Create | Rights.Edit, g.Supported);   // Delete has no tools
            Assert.Equal(Rights.Read | Rights.Create, g.Rights);
            Assert.DoesNotContain("mcp__claude_ai_Gmail__send_message", g.Tools);
            g.Rights = Rights.All;
            var node = JsonNode.Parse(File.ReadAllText(file))!["connectors"]![0]!;
            Assert.Equal("Read, Create, Edit", node["rights"]!.GetValue<string>());
            Assert.NotNull(node["tools"]!["Never"]);                                 // Bridge's catalog stays intact
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void NoFileNoRows() => Assert.Empty(CloudConnectors.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".json")));
}
