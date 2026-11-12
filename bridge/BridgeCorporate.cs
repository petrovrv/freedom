// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AiWorkbenchBridge
{
    // Freedom F1 step 5b (stage B): what Bridge knows about the corporate connector comes from its manifest
    // (connectors/corporate/manifest.json of the repository, installed as %LOCALAPPDATA%\AIWorkbench\connectors\corporate\
    // manifest.json; AIWB_CORPORATE_MANIFEST = another file): names for pushes and messages, tools with their kind
    // (corporate-llm = the model gateway), agents, Outlook domains of the employer, the limits source of the savings row.
    // No manifest = neutral texts and no corporate tools. Legacy wire names (engie, engieBlocked, [[engie-blocked]], the
    // runtime kind "engie" of config.json) are still read and written next to the new ones.
    internal static class Corporate
    {
        public const string LlmKind = "corporate-llm";
        public const string BlockedMarker = "[[corporate-blocked]]";
        public const string LegacyBlockedMarker = "[[engie-blocked]]";

        private static readonly object Gate = new object();
        private static volatile bool _loaded;
        private static bool _fromFile;
        private static DateTime _checkedUtc = DateTime.MinValue;
        private static string _title, _portal, _signin, _usage;
        private static Dictionary<string, string> _tools, _agents;
        private static string[] _domains;

        public static string Title { get { Load(); return _title; } }
        public static string PortalTitle { get { Load(); return _portal; } }
        public static string SigninTitle { get { Load(); return _signin; } }
        // limits.json source of the corporate dispatcher's savings row (CO Web reads usageSource of the same manifest)
        public static string UsageSource { get { Load(); return _usage; } }
        public static string[] Domains { get { Load(); return (string[])_domains.Clone(); } }

        public static string ManifestPath()
        {
            string env = Environment.GetEnvironmentVariable("AIWB_CORPORATE_MANIFEST");
            if (!String.IsNullOrWhiteSpace(env))
                return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"AIWorkbench\connectors\corporate\manifest.json");
        }

        // A missing or unreadable manifest is looked for again once a minute (CO Desktop may install it later).
        private static void Load()
        {
            if (_loaded && (_fromFile || (DateTime.UtcNow - _checkedUtc).TotalSeconds < 60))
                return;
            lock (Gate)
            {
                if (_loaded && (_fromFile || (DateTime.UtcNow - _checkedUtc).TotalSeconds < 60))
                    return;
                _checkedUtc = DateTime.UtcNow;
                string json = null;
                try
                {
                    string p = ManifestPath();
                    if (File.Exists(p))
                        json = File.ReadAllText(p, Encoding.UTF8);
                }
                catch
                {
                    json = null;
                }
                Apply(json);
                _fromFile = json != null;
            }
        }

        // Tests (and a reload) set the manifest text; null = no manifest.
        public static void Set(string json)
        {
            lock (Gate)
            {
                Apply(json);
                _fromFile = true; // fixed until the next Set
            }
        }

        private static void Apply(string json)
        {
            Dictionary<string, object> m = null;
            try
            {
                if (!String.IsNullOrWhiteSpace(json))
                    m = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                m = null;
            }
            _title = Str(m, "title", "Corporate");
            _portal = Str(m, "portalTitle", "the corporate portal");
            _signin = Str(m, "signinTitle", "");
            _usage = Str(m, "usageSource", "corporate-savings");
            _tools = Kinds(m, "tools", "name");
            _agents = Kinds(m, "agents", "id");
            List<string> domains = new List<string>();
            object[] arr = m != null && m.ContainsKey("domains") ? m["domains"] as object[] : null;
            if (arr != null)
                foreach (object o in arr)
                    if (o is string && ((string)o).Length > 0)
                        domains.Add((string)o);
            _domains = domains.ToArray();
            _loaded = true;
        }

        private static string Str(Dictionary<string, object> m, string k, string def)
        {
            string s = m == null ? "" : DeviceWire.S(m, k);
            return s.Length > 0 ? s : def;
        }

        private static Dictionary<string, string> Kinds(Dictionary<string, object> m, string list, string key)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            object[] arr = m != null && m.ContainsKey(list) ? m[list] as object[] : null;
            if (arr == null)
                return d;
            foreach (object o in arr)
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                string id = DeviceWire.S(e, key);
                if (id.Length > 0)
                    d[id] = DeviceWire.S(e, "kind");
            }
            return d;
        }

        // A tool of the connector (any kind, or one kind).
        public static bool IsTool(string tool, string kind)
        {
            Load();
            string k;
            return tool != null && _tools.TryGetValue(tool, out k) && (kind == null || k == kind);
        }

        // First tool of a kind (the model gateway for corporate-llm), or null.
        public static string ToolOf(string kind)
        {
            Load();
            foreach (KeyValuePair<string, string> t in _tools)
                if (t.Value == kind)
                    return t.Key;
            return null;
        }

        public const string DispatcherKind = "corporate-dispatcher";

        // The connector's dispatcher (a CLI agent that delegates writing to the gateway), or null.
        public static string DispatcherId
        {
            get
            {
                Load();
                foreach (KeyValuePair<string, string> a in _agents)
                    if (a.Value == DispatcherKind)
                        return a.Key;
                return null;
            }
        }

        public static bool IsDispatcher(string agent)
        {
            Load();
            string k;
            return agent != null && _agents.TryGetValue(agent, out k) && k == DispatcherKind;
        }

        public static bool IsAgent(string agent)
        {
            Load();
            return agent != null && _agents.ContainsKey(agent);
        }

        // The agent whose runtime is the model gateway itself (no CLI): kind corporate-llm.
        public static bool IsLlmAgent(string agent)
        {
            Load();
            string k;
            return agent != null && _agents.TryGetValue(agent, out k) && k == LlmKind;
        }

        // config.json runtime kind of an agent driven by Bridge over the gateway (no process); "engie" = legacy name.
        public static bool IsGatewayRuntime(string kind)
        {
            return kind == "corporate" || kind == "engie";
        }

        public static bool HasMarker(string text)
        {
            return text != null && (text.IndexOf(BlockedMarker, StringComparison.Ordinal) >= 0
                || text.IndexOf(LegacyBlockedMarker, StringComparison.Ordinal) >= 0);
        }

        public static string StripMarkers(string text)
        {
            return text.Replace(BlockedMarker, String.Empty).Replace(LegacyBlockedMarker, String.Empty);
        }

        // Ledger kind of a corporate model call (usage-ledger.jsonl); "engie" = lines written before 5b.
        public const string LedgerKind = "corporate";

        public static bool IsLedgerKind(string kind)
        {
            return kind == LedgerKind || kind == "engie";
        }
    }
}
