// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Policy;

namespace CO.Core.Connectors;

// Connectors of the Claude account (claude.ai Gmail, Google Calendar): they run inside Claude agents on the Bridge
// host, not on this device. Bridge publishes them with the tools behind each box in
// %LOCALAPPDATA%\AIWorkbenchBridge\cloud-connectors.json and, before every Claude job, removes the tools of unticked
// boxes and of "Never" (sending mail, invitations) with --disallowedTools. CO Desktop on the same computer shows the
// rows and writes back only "rights"; on other computers the file is absent and the rows are not shown.
public static class CloudConnectors
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbenchBridge", "cloud-connectors.json");

    public static IReadOnlyList<CloudConnector> Load(string path)
    {
        var list = new List<CloudConnector>();
        if (!File.Exists(path)) return list;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); } catch (Exception) { return list; }
        if (root?["connectors"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            string id = n?["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0 || n!["tools"] is not JsonObject tools) continue;
            Rights supported = Rights.None;
            var names = new List<string>();
            foreach (var r in RightsText.Columns)
                if (tools[r.ToString()] is JsonArray a && a.Count > 0)
                {
                    supported |= r;
                    names.AddRange(a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
                }
            list.Add(new CloudConnector(path, id, n["name"]?.GetValue<string>() ?? id, n["description"]?.GetValue<string>() ?? "",
                supported, names, ConnectorRegistry.Parse(n["rights"]?.GetValue<string>() ?? "")));
        }
        return list;
    }
}

public sealed class CloudConnector : Connector
{
    private static readonly object FileLock = new();
    private readonly string _path, _id, _name, _desc;
    private readonly Rights _supported;
    private readonly string[] _tools;
    private Rights _rights;

    public CloudConnector(string path, string id, string name, string description, Rights supported, IEnumerable<string> tools, Rights rights)
    {
        _path = path; _id = id; _name = name; _desc = description; _supported = supported; _tools = tools.ToArray(); _rights = rights & supported;
    }

    public override string Id => _id;
    public override string Name => _name;
    public override string Description => _desc + " Account connector of Claude: Bridge removes the tools of unticked boxes before each Claude job.";
    public override Rights Supported => _supported;
    public override IReadOnlyCollection<string> Tools => _tools;

    public override Rights Rights
    {
        get => _rights;
        set
        {
            _rights = value & _supported;
            Write(_rights);
            Registry?.RaiseChanged(this);
        }
    }

    private void Write(Rights rights)
    {
        lock (FileLock)
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) ?? throw new InvalidOperationException("cloud-connectors.json is empty.");
            var item = (root["connectors"] as JsonArray)?.FirstOrDefault(n => n?["id"]?.GetValue<string>() == _id)
                ?? throw new InvalidOperationException($"'{_id}' is not in cloud-connectors.json.");
            item["rights"] = rights == Rights.None ? "None" : rights.ToString();
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
    }

    // The session lives in the Claude account; there is nothing to probe from here.
    protected override Task<ConnectorStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(Ready("Claude account connector; applies to Claude and Dispatcher jobs on this computer"));
}
