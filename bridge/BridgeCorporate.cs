// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6): the module loader of Bridge and the generic model gateway. The file keeps its old name only
// because bridge/build.cmd (a protected path) lists it; it becomes BridgeModules.cs with the owner's yes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AiWorkbenchBridge
{
    // One installed module as Bridge sees it: the local folder <modules root>/<id>/ (module.json, context/...) written by
    // install/freedom_modules.py after it checked the signature, plus the switch of the server (db.php modules_host list:
    // enabled, people). A module the server does not list is off.
    internal sealed class ModuleSpec
    {
        public string Id = "";
        public string Version = "";
        public string Dir = "";
        public string Scope = "";
        public Dictionary<string, object> Manifest = new Dictionary<string, object>();
        public bool Enabled;
        public Dictionary<string, string> People = new Dictionary<string, string>(StringComparer.Ordinal); // env -> auto|ask|off

        public Dictionary<string, object> Provides
        {
            get { return Modules.Obj(Manifest, "provides") ?? new Dictionary<string, object>(); }
        }

        public bool Personal
        {
            get { return Modules.Obj(Manifest, "personal") != null; }
        }

        public bool Has(string permission)
        {
            foreach (object p in Modules.Arr(Manifest, "permissions"))
                if (p as string == permission)
                    return true;
            return false;
        }
    }

    // ext/1 points Bridge serves (SPEC §3.6): service (daemons under the Bridge supervisor), device.tools (refused while the
    // module is off; risk class), models (model_resources of a core agent), context (instruction fragments of a new
    // session), events (job.before / job.after hooks, push through the core), tools (the switch of toolbox's module state).
    internal static class Modules
    {
        public const string StateFile = ".server-state.json";
        public const string HeartbeatFile = ".bridge-supervisor.json";
        private static readonly Regex IdRe = new Regex(@"\A[a-z0-9][a-z0-9-]{0,62}\z");
        private static readonly object Gate = new object();
        private static Dictionary<string, ModuleSpec> _mods = new Dictionary<string, ModuleSpec>(StringComparer.Ordinal);
        private static bool _fixed;          // tests: Set() wins over the folder until the next Set(null)
        private static string _ownerEnv = "";
        private static bool _serverKnown;   // the server's switches were read (live or cached): "off" is a real answer

        public static string Root()
        {
            string env = Environment.GetEnvironmentVariable("AIWB_MODULES_DIR");
            if (!String.IsNullOrWhiteSpace(env))
                return env;
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbench"), "modules");
        }

        // The tool library (module services and hooks run its tools): AIWB_TOOLS_DIR, else the standard place.
        public static string ToolsRoot()
        {
            string env = Environment.GetEnvironmentVariable("AIWB_TOOLS");   // the installer's and the relay's name
            if (String.IsNullOrWhiteSpace(env))
                env = Environment.GetEnvironmentVariable("AIWB_TOOLS_DIR");
            if (!String.IsNullOrWhiteSpace(env))
                return env;
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AIWorkbench", "tools");
#endif
            return @"C:\AIWorkbench\tools";
        }

        public static bool ServerKnown
        {
            get { return _serverKnown || _fixed; }
        }

        public static string OwnerEnv
        {
            get { return _ownerEnv; }
            set { _ownerEnv = value ?? ""; }
        }

        // ---------------------------------------------------------------- loading

        // Rescans the local folders and applies the last server state (cached in <root>/.server-state.json).
        public static void Reload()
        {
            if (_fixed)
                return;
            string root = Root();
            Dictionary<string, ModuleSpec> found = new Dictionary<string, ModuleSpec>(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(root))
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string id = Path.GetFileName(dir);
                        string file = Path.Combine(dir, "module.json");
                        if (!IdRe.IsMatch(id) || !File.Exists(file))
                            continue;
                        ModuleSpec m = Parse(File.ReadAllText(file, Encoding.UTF8), dir);
                        if (m != null && m.Id == id)
                            found[id] = m;
                    }
            }
            catch
            {
                return; // keep what was loaded
            }
            object[] server = ReadServerState(root);
            if (server != null)
                _serverKnown = true;
            lock (Gate)
            {
                ApplyServerTo(found, server);
                _mods = found;
            }
        }

        private static ModuleSpec Copy(ModuleSpec m)
        {
            ModuleSpec c = new ModuleSpec();
            c.Id = m.Id;
            c.Version = m.Version;
            c.Dir = m.Dir;
            c.Scope = m.Scope;
            c.Manifest = m.Manifest;   // never changed after parsing
            c.Enabled = m.Enabled;
            c.People = new Dictionary<string, string>(m.People, StringComparer.Ordinal);
            return c;
        }

        public static ModuleSpec Parse(string json, string dir)
        {
            Dictionary<string, object> man;
            try
            {
                man = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
            if (man == null)
                return null;
            ModuleSpec m = new ModuleSpec();
            m.Id = DeviceWire.S(man, "id");
            if (!IdRe.IsMatch(m.Id))
                return null;
            m.Version = DeviceWire.S(man, "version");
            m.Scope = DeviceWire.S(man, "scope");
            m.Manifest = man;
            m.Dir = dir ?? "";
            return m;
        }

        // The server's list (db.php modules_host action list): [{id, version, enabled, people {env: mode}}].
        public static void ApplyServer(object[] list)
        {
            if (list == null)
                return;
            try
            {
                string root = Root();
                Directory.CreateDirectory(root);
                Dictionary<string, object> st = new Dictionary<string, object>();
                st["utc"] = DateTime.UtcNow.ToString("o");
                st["modules"] = list;
                AtomicFile.WriteJson(Path.Combine(root, StateFile), st);
            }
            catch
            {
            }
            lock (Gate)
            {
                // readers on device-call threads use _mods without the lock: switch to new objects in one assignment
                Dictionary<string, ModuleSpec> next = new Dictionary<string, ModuleSpec>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, ModuleSpec> kv in _mods)
                    next[kv.Key] = Copy(kv.Value);
                ApplyServerTo(next, list);
                _mods = next;
            }
            _serverKnown = true;
        }

        private static object[] ReadServerState(string root)
        {
            try
            {
                string f = Path.Combine(root, StateFile);
                if (!File.Exists(f))
                    return null;
                Dictionary<string, object> st = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                return Arr(st, "modules");
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyServerTo(Dictionary<string, ModuleSpec> mods, object[] list)
        {
            foreach (ModuleSpec m in mods.Values)
            {
                m.Enabled = false;
                m.People.Clear();
            }
            if (list == null)
                return;
            foreach (object o in list)
            {
                Dictionary<string, object> r = o as Dictionary<string, object>;
                ModuleSpec m;
                if (r == null || !mods.TryGetValue(DeviceWire.S(r, "id"), out m))
                    continue;
                m.Enabled = DeviceWire.B(r, "enabled");
                Dictionary<string, object> people = Obj(r, "people");
                if (people != null)
                    foreach (KeyValuePair<string, object> p in people)
                        if (p.Value is string)
                            m.People[p.Key] = (string)p.Value;
            }
        }

        // Tests: modules from manifest texts with their switches; null = back to the folder.
        public static void Set(IEnumerable<string> manifests, IDictionary<string, bool> enabled)
        {
            lock (Gate)
            {
                _mods = new Dictionary<string, ModuleSpec>(StringComparer.Ordinal);
                _fixed = manifests != null;
                if (manifests == null)
                    return;
                foreach (string j in manifests)
                {
                    ModuleSpec m = Parse(j, "");
                    if (m == null)
                        continue;
                    bool on;
                    m.Enabled = enabled != null && enabled.TryGetValue(m.Id, out on) && on;
                    _mods[m.Id] = m;
                }
            }
            Gateway.Reset();
        }

        public static void SetPeople(string id, string env, string mode)
        {
            lock (Gate)
            {
                ModuleSpec m;
                if (!_mods.TryGetValue(id, out m))
                    return;
                Dictionary<string, ModuleSpec> next = new Dictionary<string, ModuleSpec>(_mods, StringComparer.Ordinal);
                ModuleSpec c = Copy(m);
                c.People[env] = mode;
                next[id] = c;
                _mods = next;
            }
        }

        public static List<ModuleSpec> All()
        {
            lock (Gate)
                return _mods.Values.OrderBy(delegate(ModuleSpec m) { return m.Id; }, StringComparer.Ordinal).ToList();
        }

        public static ModuleSpec Get(string id)
        {
            lock (Gate)
            {
                ModuleSpec m;
                return id != null && _mods.TryGetValue(id, out m) ? m : null;
            }
        }

        // ---------------------------------------------------------------- who the module works for

        // The module works for this environment: it is on and, for a personal module, the person's switch is "auto"
        // (ask = not agreed yet); a module of scope "environment" without a personal switch serves the owner only.
        // env null/empty = a job without an environment (owner's own Bridge jobs).
        public static bool EnabledFor(ModuleSpec m, string env)
        {
            if (m == null || !m.Enabled)
                return false;
            if (String.IsNullOrEmpty(env))
                return true;
            if (m.Personal)
            {
                string mode;
                if (!m.People.TryGetValue(env, out mode))
                    mode = DeviceWire.S(Obj(m.Manifest, "personal"), "default");
                return mode == "auto";
            }
            if (m.Scope == "environment")
                return _ownerEnv.Length > 0 && env == _ownerEnv; // owner not known yet: nobody else
            return true;
        }

        public static List<ModuleSpec> ActiveFor(string env)
        {
            return All().Where(delegate(ModuleSpec m) { return EnabledFor(m, env); }).ToList();
        }

        // "id version, id version" of the modules a job runs with (its event log keeps it: behaviour can be replayed).
        public static string ActiveSummary(string env)
        {
            return String.Join(", ", ActiveFor(env).Select(delegate(ModuleSpec m) { return m.Id + " " + m.Version; }).ToArray());
        }

        // ---------------------------------------------------------------- device.tools

        // The installed module that declares this device tool (provides.deviceTools[].name), or null for a core tool.
        public static ModuleSpec ToolOwner(string tool)
        {
            if (String.IsNullOrEmpty(tool))
                return null;
            foreach (ModuleSpec m in All())
                foreach (object o in Arr(m.Provides, "deviceTools"))
                    if (DeviceWire.S(o as Dictionary<string, object>, "name") == tool)
                        return m;
            return null;
        }

        // Risk class of a module device tool as Bridge uses it: R (reads), D (deletes or acts for the user), else W; "" = core tool.
        public static string ToolRisk(string tool)
        {
            ModuleSpec m = ToolOwner(tool);
            if (m == null)
                return "";
            foreach (object o in Arr(m.Provides, "deviceTools"))
            {
                Dictionary<string, object> t = o as Dictionary<string, object>;
                if (DeviceWire.S(t, "name") == tool)
                {
                    string r = DeviceWire.S(t, "class");   // ext/1 name; "risk" is the other name of it
                    if (r.Length == 0)
                        r = DeviceWire.S(t, "risk");
                    if (r == "read" || r == "ask")
                        return "R";
                    r = r.ToUpperInvariant();
                    return r == "R" || r == "W" || r == "D" ? r : "W"; // act, upload or undeclared count as writing
                }
            }
            return "W";
        }

        // Why a call of a module device tool is refused (null = go on): the module is off for the job's environment;
        // a "D" tool in a chat that read untrusted text (read-guard taint) waits for a clean chat.
        public static string Refusal(string tool, string env, bool tainted, out string code)
        {
            code = null;
            ModuleSpec m = ToolOwner(tool);
            if (m == null)
                return null;
            if (!EnabledFor(m, env))
            {
                code = "module_disabled";
                return "The tool " + tool + " belongs to the module '" + Title(m) + "', which is disabled" +
                    (m.Enabled ? " for this environment" : "") + ". The owner turns it on in CO Web, Host > Modules.";
            }
            if (tainted && ToolRisk(tool) == "D")
            {
                code = "module_risk";
                return "The tool " + tool + " (module '" + Title(m) + "') changes or deletes data for the user and this chat read untrusted text: " +
                    "ask the owner to run it from a clean chat.";
            }
            return null;
        }

        public static string Title(ModuleSpec m)
        {
            string t = DeviceWire.S(m.Manifest, "title");
            return t.Length > 0 ? t : m.Id;
        }

        // ---------------------------------------------------------------- context

        // Instruction fragments (provides.context[].file, Markdown inside the module folder) of the modules that work for
        // this environment and declare the permission "context"; at most 8000 characters each.
        public static string Context(string env)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ModuleSpec m in ActiveFor(env))
            {
                if (!m.Has("context"))
                    continue;
                foreach (object o in Arr(m.Provides, "context"))
                {
                    string text = ContextText(m, o as Dictionary<string, object>);
                    if (text.Length == 0)
                        continue;
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.AppendLine(text.TrimEnd());
                }
            }
            return sb.ToString();
        }

        private static string ContextText(ModuleSpec m, Dictionary<string, object> c)
        {
            string text = DeviceWire.S(c, "text");
            string file = DeviceWire.S(c, "file");
            if (text.Length == 0 && file.Length > 0 && m.Dir.Length > 0 && Regex.IsMatch(file, @"\A[A-Za-z0-9._/-]+\z") && file.IndexOf("..", StringComparison.Ordinal) < 0)
            {
                try
                {
                    string p = Path.GetFullPath(Path.Combine(m.Dir, file.Replace('/', Path.DirectorySeparatorChar)));
                    if (p.StartsWith(Path.GetFullPath(m.Dir), StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                        text = File.ReadAllText(p, Encoding.UTF8);
                }
                catch
                {
                    text = "";
                }
            }
            return text.Length > 8000 ? text.Substring(0, 8000) : text;
        }

        // ---------------------------------------------------------------- services, hooks, models

        // provides.services[] run by the supervisor: {id, tool, run} (a tool of the library with its arguments).
        public static List<Dictionary<string, object>> Services(ModuleSpec m)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            foreach (object o in Arr(m.Provides, "services"))
            {
                Dictionary<string, object> s = o as Dictionary<string, object>;
                if (s != null && DeviceWire.S(s, "kind") != "schedule" && Regex.IsMatch(DeviceWire.S(s, "id"), @"\A[a-z0-9][a-z0-9-]{0,31}\z") && ToolName(DeviceWire.S(s, "tool")) && DeviceWire.S(s, "run").Length > 0)
                    l.Add(s);
            }
            return l;
        }

        // provides.services[] of kind "schedule" that Bridge runs itself: {id, cron | every, tool | cwd, run} without a
        // windowsTask (a schedule with one stays with the Windows task scheduler, switched by the installer).
        public static List<Dictionary<string, object>> Schedules(ModuleSpec m)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            foreach (object o in Arr(m.Provides, "services"))
            {
                Dictionary<string, object> s = o as Dictionary<string, object>;
                if (s == null || DeviceWire.S(s, "kind") != "schedule" || DeviceWire.S(s, "windowsTask").Length > 0)
                    continue;
                if (!Regex.IsMatch(DeviceWire.S(s, "id"), @"\A[a-z0-9][a-z0-9-]{0,31}\z") || DeviceWire.S(s, "run").Length == 0)
                    continue;
                if (DeviceWire.S(s, "cron").Length == 0 && Every(DeviceWire.S(s, "every")) == null)
                    continue;
                if (ToolName(DeviceWire.S(s, "tool")) || Regex.IsMatch(DeviceWire.S(s, "cwd"), @"\Ahome(/[A-Za-z0-9._-]+)*\z"))
                    l.Add(s);
            }
            return l;
        }

        // ISO-8601 period of a schedule's "every" (P1D, PT1H, PT15M); null when malformed or under a minute.
        public static TimeSpan? Every(string iso)
        {
            Match mt = Regex.Match(iso ?? "", @"\AP(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?\z");
            if (!mt.Success)
                return null;
            int[] v = new int[5];
            for (int g = 1; g <= 4; g++)
                if (mt.Groups[g].Success && (!Int32.TryParse(mt.Groups[g].Value, NumberStyles.None, CultureInfo.InvariantCulture, out v[g]) || v[g] > 100000))
                    return null;
            TimeSpan t = new TimeSpan(v[1], v[2], v[3], v[4]);
            return t.TotalSeconds >= 60 ? (TimeSpan?)t : null;
        }

        // When a schedule is due: the latest due time after lastDue (cron in local time, up to 2 h late), or null.
        public static DateTime? DueUtc(Dictionary<string, object> spec, DateTime nowUtc, DateTime lastDueUtc)
        {
            string cron = DeviceWire.S(spec, "cron");
            if (cron.Length > 0)
            {
                DateTime? due = CronExpression.Parse(cron).LastMatchUtc(nowUtc, TimeZoneInfo.Local, 120);
                return due.HasValue && due.Value > lastDueUtc ? due : null;
            }
            TimeSpan? every = Every(DeviceWire.S(spec, "every"));
            return every.HasValue && nowUtc - lastDueUtc >= every.Value ? (DateTime?)nowUtc : null;
        }

        // provides.hooks[] of an event: {event: job.before|job.after, tool, run}.
        public static List<KeyValuePair<ModuleSpec, Dictionary<string, object>>> Hooks(string ev, string env)
        {
            List<KeyValuePair<ModuleSpec, Dictionary<string, object>>> l = new List<KeyValuePair<ModuleSpec, Dictionary<string, object>>>();
            foreach (ModuleSpec m in ActiveFor(env))
                foreach (object o in Arr(m.Provides, "hooks"))
                {
                    Dictionary<string, object> h = o as Dictionary<string, object>;
                    if (h != null && DeviceWire.S(h, "event") == ev && ToolName(DeviceWire.S(h, "tool")) && DeviceWire.S(h, "run").Length > 0)
                        l.Add(new KeyValuePair<ModuleSpec, Dictionary<string, object>>(m, h));
                }
            return l;
        }

        // provides.models[] that run on a core agent ({id, agent, title, models, defaultModel, card, slaveAllowed}) are
        // registered as model_resources "<owner env>/<module>-<id>"; gateway models (kind llm|dispatcher) are read by the
        // server from the manifest itself.
        public static List<Dictionary<string, object>> Resources(ModuleSpec m)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            foreach (object o in Arr(m.Provides, "models"))
            {
                Dictionary<string, object> r = o as Dictionary<string, object>;
                if (r != null && DeviceWire.S(r, "agent").Length > 0 && Regex.IsMatch(DeviceWire.S(r, "id"), @"\A[a-z0-9][a-z0-9.-]{0,40}\z"))
                    l.Add(r);
            }
            return l;
        }

        public static bool ToolName(string s)
        {
            return Regex.IsMatch(s ?? "", @"\A[a-z0-9][a-z0-9-]{0,62}\z");
        }

        // Library tools of the module (provides.tools[].name or plain names).
        public static List<string> LibraryTools(ModuleSpec m)
        {
            List<string> l = new List<string>();
            foreach (object o in Arr(m.Provides, "tools"))
            {
                string n = o is string ? (string)o : DeviceWire.S(o as Dictionary<string, object>, "name");
                if (ToolName(n))
                    l.Add(n);
            }
            return l;
        }

        // ---------------------------------------------------------------- JSON helpers

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static object[] Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return new object[0];
            object[] a = v as object[];
            if (a != null)
                return a;
            System.Collections.ArrayList al = v as System.Collections.ArrayList;
            return al != null ? al.ToArray() : new object[0];
        }
    }

    // The model gateway of a module (SPEC §3.7, ext/1 "models" + "device.tools"): a confidential model reached through
    // a device tool of kind "llm", optionally with a dispatcher agent (a CLI agent that delegates writing to it). Names,
    // tools, agents and texts come from the installed module that declares provides.gateway (installed but disabled =
    // known, its tools refused). None = neutral texts and no gateway. Legacy wire names that deployed Bridges, devices and
    // old rows still use are read and written next to the new ones (runtime kinds, blocked markers, ledger kind).
    internal static class Gateway
    {
        public const string LlmKind = "gateway-llm";
        public const string FetchKind = "gateway-fetch";
        public const string DispatcherKind = "gateway-dispatcher";
        public const string BlockedMarker = "[[gateway-blocked]]";
        // legacy wire (Bridge 1.5.1 .. 1.16): markers the dispatcher policy of config.json still asks for
        private static readonly string[] LegacyMarkers = { "[[corporate-blocked]]", "[[engie-blocked]]" };
        // legacy wire: config.json runtime kinds and usage-ledger kinds written before F10
        private static readonly string[] LegacyRuntimeKinds = { "corporate", "engie" };

        private static readonly object Gate = new object();
        private static bool _loaded;
        private static bool _fixed;
        private static DateTime _checkedUtc = DateTime.MinValue;
        private static string _module, _title, _portal, _signin, _usage;
        private static Dictionary<string, string> _tools, _agents;
        private static string[] _domains, _markers;

        public static string ModuleId { get { Load(); return _module; } }
        public static string Title { get { Load(); return _title; } }
        public static string PortalTitle { get { Load(); return _portal; } }
        public static string SigninTitle { get { Load(); return _signin; } }
        // limits.json source of the dispatcher's savings row (CO Web reads usageSource of the same module)
        public static string UsageSource { get { Load(); return _usage; } }
        public static string[] Domains { get { Load(); return (string[])_domains.Clone(); } }

        public static void Reset()
        {
            lock (Gate)
            {
                _loaded = false;
                _fixed = false;
            }
        }

        // The description is rebuilt from the loaded modules at most once a minute.
        private static void Load()
        {
            if (_loaded && (_fixed || (DateTime.UtcNow - _checkedUtc).TotalSeconds < 60))
                return;
            lock (Gate)
            {
                if (_loaded && (_fixed || (DateTime.UtcNow - _checkedUtc).TotalSeconds < 60))
                    return;
                _checkedUtc = DateTime.UtcNow;
                ModuleSpec m = null;
                foreach (ModuleSpec x in Modules.All())
                    if (Modules.Obj(x.Provides, "gateway") != null)
                    {
                        m = x;
                        break;
                    }
                Apply(m ?? LegacyConnector());
            }
        }

        // Compatibility until the gateway module is installed on this PC: the connector manifest of Freedom F1 5b
        // (<LocalAppData>/AIWorkbench/connectors/<id>/manifest.json, or AIWB_GATEWAY_MANIFEST) seen as a module.
        private static ModuleSpec LegacyConnector()
        {
            try
            {
                string file = Environment.GetEnvironmentVariable("AIWB_GATEWAY_MANIFEST");
                if (String.IsNullOrWhiteSpace(file))
                    file = Environment.GetEnvironmentVariable("AIWB_CORPORATE_MANIFEST"); // legacy name of the override
                if (String.IsNullOrWhiteSpace(file))
                {
                    string dir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbench"), "connectors");
                    file = null;
                    if (Directory.Exists(dir))
                        foreach (string d in Directory.GetDirectories(dir).OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal))
                            if (File.Exists(Path.Combine(d, "manifest.json")))
                            {
                                file = Path.Combine(d, "manifest.json");
                                break;
                            }
                }
                return file != null && File.Exists(file) ? FromLegacy(File.ReadAllText(file, Encoding.UTF8)) : null;
            }
            catch
            {
                return null;
            }
        }

        // Old connector manifest {title, portalTitle, signinTitle, domains, usageSource, agents[{id, kind *-llm|*-dispatcher}],
        // tools[{name, kind *-llm|*-fetch}]} -> the module form.
        internal static ModuleSpec FromLegacy(string json)
        {
            Dictionary<string, object> old = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (old == null)
                return null;
            Dictionary<string, object> gw = new Dictionary<string, object>();
            foreach (string k in new string[] { "title", "portalTitle", "signinTitle", "usageSource", "domains" })
                if (old.ContainsKey(k))
                    gw[k] = old[k];
            List<object> tools = new List<object>();
            foreach (object o in Modules.Arr(old, "tools"))
            {
                Dictionary<string, object> t = o as Dictionary<string, object>;
                string k = DeviceWire.S(t, "kind");
                Dictionary<string, object> n = new Dictionary<string, object>();
                n["name"] = DeviceWire.S(t, "name");
                n["kind"] = k.EndsWith("-llm", StringComparison.Ordinal) ? "llm" : k.EndsWith("-fetch", StringComparison.Ordinal) ? "fetch" : "";
                n["risk"] = "R";
                tools.Add(n);
            }
            List<object> models = new List<object>();
            foreach (object o in Modules.Arr(old, "agents"))
            {
                Dictionary<string, object> a = o as Dictionary<string, object>;
                string k = DeviceWire.S(a, "kind");
                Dictionary<string, object> n = new Dictionary<string, object>();
                n["id"] = DeviceWire.S(a, "id");
                n["kind"] = k.EndsWith("-dispatcher", StringComparison.Ordinal) ? "dispatcher" : "llm";
                models.Add(n);
            }
            Dictionary<string, object> prov = new Dictionary<string, object>();
            prov["gateway"] = gw;
            prov["deviceTools"] = tools.ToArray();
            prov["models"] = models.ToArray();
            ModuleSpec m = new ModuleSpec();
            m.Id = "";  // not a module: its tools are not refused by the loader
            m.Manifest["provides"] = prov;
            return m;
        }

        // Tests: an old connector manifest, fixed until the next Set.
        public static void SetLegacy(string json)
        {
            lock (Gate)
            {
                Apply(FromLegacy(json));
                _fixed = true;
            }
        }

        // Tests: a module manifest text (null = no gateway), fixed until the next Set.
        public static void Set(string moduleJson)
        {
            lock (Gate)
            {
                Apply(moduleJson == null ? null : Modules.Parse(moduleJson, ""));
                _fixed = true;
            }
        }

        private static void Apply(ModuleSpec m)
        {
            // built in locals and published at the end: readers past Load() never see half-filled tables
            Dictionary<string, object> p = m == null ? null : m.Provides;
            Dictionary<string, object> g = Modules.Obj(p, "gateway");
            Dictionary<string, string> tools = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> agents = new Dictionary<string, string>(StringComparer.Ordinal);
            if (g != null)
            {
                foreach (object o in Modules.Arr(p, "deviceTools"))
                {
                    Dictionary<string, object> t = o as Dictionary<string, object>;
                    string name = DeviceWire.S(t, "name");
                    if (name.Length > 0)
                        tools[name] = Kind(DeviceWire.S(t, "kind"));
                }
                foreach (object o in Modules.Arr(p, "models"))
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    string id = DeviceWire.S(a, "id");
                    string k = DeviceWire.S(a, "kind");
                    if (id.Length > 0 && (k == "llm" || k == "dispatcher"))
                        agents[id] = Kind(k);
                }
            }
            List<string> domains = new List<string>();
            foreach (object o in Modules.Arr(g, "domains"))
                if (o is string && ((string)o).Length > 0)
                    domains.Add((string)o);
            List<string> markers = new List<string>();
            markers.Add(BlockedMarker);
            markers.AddRange(LegacyMarkers);
            foreach (object o in Modules.Arr(g, "blockedMarkers"))
                if (o is string && Regex.IsMatch((string)o, @"\A\[\[[a-z0-9-]{1,40}\]\]\z") && !markers.Contains((string)o))
                    markers.Add((string)o);
            _module = m == null ? "" : m.Id;
            _title = Str(g, "title", "Employer");
            _portal = Str(g, "portalTitle", "the employer portal");
            _signin = Str(g, "signinTitle", "");
            _usage = Str(g, "usageSource", "gateway-savings");
            _domains = domains.ToArray();
            _markers = markers.ToArray();
            _tools = tools;
            _agents = agents;
            _loaded = true;
        }

        private static string Kind(string k)
        {
            return k == "llm" ? LlmKind : k == "fetch" ? FetchKind : k == "dispatcher" ? DispatcherKind : "";
        }

        private static string Str(Dictionary<string, object> m, string k, string def)
        {
            string s = m == null ? "" : DeviceWire.S(m, k);
            return s.Length > 0 ? s : def;
        }

        // A tool of the gateway's module (any kind, or one kind).
        public static bool IsTool(string tool, string kind)
        {
            Load();
            string k;
            return tool != null && _tools.TryGetValue(tool, out k) && (kind == null || k == kind);
        }

        // First tool of a kind (the model gateway for LlmKind), or null.
        public static string ToolOf(string kind)
        {
            Load();
            foreach (KeyValuePair<string, string> t in _tools)
                if (t.Value == kind)
                    return t.Key;
            return null;
        }

        // The module's dispatcher (a CLI agent that delegates writing to the gateway), or null.
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

        // The agent whose runtime is the model gateway itself (no CLI).
        public static bool IsLlmAgent(string agent)
        {
            Load();
            string k;
            return agent != null && _agents.TryGetValue(agent, out k) && k == LlmKind;
        }

        // config.json runtime kind of an agent driven by Bridge over the gateway (no process).
        public static bool IsGatewayRuntime(string kind)
        {
            return kind == "gateway" || Array.IndexOf(LegacyRuntimeKinds, kind) >= 0;
        }

        public static bool HasMarker(string text)
        {
            Load();
            if (text == null)
                return false;
            foreach (string mk in _markers)
                if (text.IndexOf(mk, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        public static string StripMarkers(string text)
        {
            Load();
            foreach (string mk in _markers)
                text = text.Replace(mk, String.Empty);
            return text;
        }

        // Ledger kind of a gateway model call (usage-ledger.jsonl); older lines carry the legacy kinds.
        public const string LedgerKind = "gateway";

        public static bool IsLedgerKind(string kind)
        {
            return kind == LedgerKind || Array.IndexOf(LegacyRuntimeKinds, kind) >= 0;
        }
    }

    // ================================================================ the supervisor side
    internal sealed partial class BridgeApplicationContext
    {
        private sealed class ModuleService
        {
            public string Key;            // <module>/<service>
            public string ModuleId;
            public string Task;           // legacy scheduled task the supervisor took over (Windows), or null
            public Process Proc;
            public DateTime StartedUtc;
            public DateTime NextStartUtc;
            public int QuickFails;        // exits within 2 min of the start, in a row
            public bool HandedBack;       // gave the job back to the scheduled task after repeated failures
            public StreamWriter LogWriter;
        }

        private readonly object _modLock = new object();
        private readonly Dictionary<string, ModuleService> _modServices = new Dictionary<string, ModuleService>(StringComparer.Ordinal);
        private DateTime _modTickUtc = DateTime.MinValue;
        private int _modPolling;
        private Dictionary<string, bool> _modToolState = new Dictionary<string, bool>(StringComparer.Ordinal);

        // From the scan loop of the host owner: every 60 s the server's switches (in the background), then the services.
        private void MaybeTickModules()
        {
            if ((DateTime.UtcNow - _modTickUtc).TotalSeconds < 60 || _exiting)
                return;
            _modTickUtc = DateTime.UtcNow;
            if (Interlocked.CompareExchange(ref _modPolling, 1, 0) != 0)
                return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    TickModules();
                }
                catch (Exception ex)
                {
                    LogThrottled("modules-tick", "Modules: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _modPolling, 0);
                }
            });
        }

        private void TickModules()
        {
            Modules.OwnerEnv = OwnerPushEnv() ?? "";
            Modules.Reload();
            if (DbqEnabled)
            {
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["action"] = "list";
                    Dictionary<string, object> r = DbqCall("modules_host", body, true, 15000);
                    Modules.ApplyServer(DbqArray(r, "modules"));
                }
                catch (Exception ex)
                {
                    // offline: the cached switches stay
                    LogThrottled("modules-list", "Modules: server list unavailable (" + ex.Message + "); cached switches kept.");
                }
            }
            Gateway.Reset();
            WriteModuleHeartbeat();
            ReconcileModuleServices();
            SyncModuleTools();
            SyncModuleResources();
        }

        // install/freedom_modules.py leaves services to Bridge while this file is fresh.
        private void WriteModuleHeartbeat()
        {
            try
            {
                string root = Modules.Root();
                if (!Directory.Exists(root))
                    return;
                Dictionary<string, object> hb = new Dictionary<string, object>();
                hb["utc"] = DateTime.UtcNow.ToString("o");
                hb["pid"] = Process.GetCurrentProcess().Id;
                hb["version"] = BridgeVersion;
                List<object> svcs = new List<object>();
                lock (_modLock)
                    foreach (ModuleService s in _modServices.Values)
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["key"] = s.Key;
                        d["running"] = s.Proc != null && !HasExited(s.Proc);
                        d["handedBack"] = s.HandedBack;
                        svcs.Add(d);
                    }
                hb["services"] = svcs;
                AtomicFile.WriteJson(Path.Combine(root, Modules.HeartbeatFile), hb);
            }
            catch
            {
            }
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        // ---------------------------------------------------------------- service

        private readonly HashSet<string> _modOffDone = new HashSet<string>(StringComparer.Ordinal);

        private void ReconcileModuleServices()
        {
            // services of installed modules that are off: end what an earlier Bridge or the legacy scheduled task left
            // running (once per switch-off; only when the server's answer is known, never on a cold start offline)
            if (Modules.ServerKnown)
                foreach (ModuleSpec m in Modules.All())
                    foreach (Dictionary<string, object> sp in Modules.Services(m))
                    {
                        string key = m.Id + "/" + DeviceWire.S(sp, "id");
                        if (m.Enabled)
                        {
                            _modOffDone.Remove(key);
                            continue;
                        }
                        if (!_modOffDone.Add(key))
                            continue;
                        bool known;
                        lock (_modLock)
                            known = _modServices.ContainsKey(key);
                        if (known)
                            continue; // stopped below
                        ModuleService orphan = new ModuleService();
                        orphan.Key = key;
                        KillStaleModuleService(orphan);
                        // the legacy task only when a Bridge supervisor took it over earlier (else it is the owner's own)
                        string task = DeviceWire.S(sp, "windowsTask");
                        string mark = Path.Combine(ModuleLogDir(m.Id), DeviceWire.S(sp, "id") + ".takeover");
                        if (task.Length > 0 && File.Exists(mark))
                        {
                            ScheduledTask(task, "/End");
                            ScheduledTask(task, "/DISABLE");
                        }
                    }
            Dictionary<string, KeyValuePair<ModuleSpec, Dictionary<string, object>>> want = new Dictionary<string, KeyValuePair<ModuleSpec, Dictionary<string, object>>>(StringComparer.Ordinal);
            foreach (ModuleSpec m in Modules.All())
                if (m.Enabled)
                    foreach (Dictionary<string, object> s in Modules.Services(m))
                        want[m.Id + "/" + DeviceWire.S(s, "id")] = new KeyValuePair<ModuleSpec, Dictionary<string, object>>(m, s);
            lock (_modLock)
            {
                foreach (string key in _modServices.Keys.ToList())
                    if (!want.ContainsKey(key))
                    {
                        StopModuleService(_modServices[key], "module disabled or removed", true);
                        _modServices.Remove(key);
                    }
                foreach (KeyValuePair<string, KeyValuePair<ModuleSpec, Dictionary<string, object>>> w in want)
                {
                    ModuleService s;
                    if (!_modServices.TryGetValue(w.Key, out s))
                    {
                        s = new ModuleService();
                        s.Key = w.Key;
                        s.ModuleId = w.Value.Key.Id;
                        string task = DeviceWire.S(w.Value.Value, "windowsTask");
                        s.Task = task.Length > 0 ? task : null;
                        _modServices[w.Key] = s;
                    }
                    if (s.HandedBack || (s.Proc != null && !HasExited(s.Proc)))
                        continue;
                    if (s.Proc != null)
                        ModuleServiceExited(s);
                    if (s.HandedBack)
                        continue; // the scheduled task runs it now (ModuleServiceExited gave it back)
                    if (DateTime.UtcNow >= s.NextStartUtc && !_exiting)
                        StartModuleService(s, w.Value.Key, w.Value.Value);
                }
                TickModuleSchedules();
            }
        }

        private void ModuleServiceExited(ModuleService s)
        {
            int code = -1;
            try { code = s.Proc.ExitCode; } catch { }
            bool quick = (DateTime.UtcNow - s.StartedUtc).TotalSeconds < 120;
            s.QuickFails = quick ? s.QuickFails + 1 : 0;
            Log("Module service " + s.Key + " exited (code " + code + ")" + (quick ? ", " + s.QuickFails + " quick exit(s) in a row" : "") + ".");
            CloseModuleLog(s);
            try { s.Proc.Dispose(); } catch { }
            s.Proc = null;
            // 5 s, 10 s, 20 s ... up to 5 min between restarts
            int delay = Math.Min(300, 5 << Math.Min(6, s.QuickFails));
            s.NextStartUtc = DateTime.UtcNow.AddSeconds(delay);
            MaybeHandBack(s);
        }

        // After 3 quick failures (exits or launches) the old scheduled task gets the job back rather than leave it down.
        private void MaybeHandBack(ModuleService s)
        {
            if (s.QuickFails < 3 || s.Task == null || s.HandedBack)
                return;
            s.HandedBack = true;
            ScheduledTask(s.Task, "/ENABLE");
            ScheduledTask(s.Task, "/Run");
            Log("Module service " + s.Key + ": failed 3 times in a row; handed back to the scheduled task " + s.Task + ".");
        }

        private void StartModuleService(ModuleService s, ModuleSpec m, Dictionary<string, object> spec)
        {
            string tool = DeviceWire.S(spec, "tool");
            string script = Path.Combine(Path.Combine(Modules.ToolsRoot(), tool), tool + ".py");
            if (!File.Exists(script))
            {
                s.NextStartUtc = DateTime.UtcNow.AddMinutes(5);
                LogThrottled("module-svc-" + s.Key, "Module service " + s.Key + ": tool not found (" + script + "); retry in 5 min.");
                return;
            }
            string python = ModulePython();
            if (python == null)
            {
                s.NextStartUtc = DateTime.UtcNow.AddMinutes(5);
                LogThrottled("module-svc-" + s.Key, "Module service " + s.Key + ": no python found; retry in 5 min.");
                return;
            }
            // the supervisor takes over from the scheduled task that ran it before F10 (one receiver per port)
            if (s.Task != null)
            {
                ScheduledTask(s.Task, "/End");
                ScheduledTask(s.Task, "/DISABLE");
                try
                {
                    Directory.CreateDirectory(ModuleLogDir(m.Id));
                    File.WriteAllText(Path.Combine(ModuleLogDir(m.Id), DeviceWire.S(spec, "id") + ".takeover"), s.Task, Encoding.UTF8);
                }
                catch
                {
                }
            }
            KillStaleModuleService(s);
            ProcessStartInfo psi = ModuleServiceStartInfo(m, spec, python, script);
            try
            {
                s.LogWriter = OpenModuleLog(m, DeviceWire.S(spec, "id"));
                Process p = new Process();
                p.StartInfo = psi;
                StreamWriter w = s.LogWriter;
                DataReceivedEventHandler sink = delegate(object o, DataReceivedEventArgs e)
                {
                    if (e.Data == null || w == null)
                        return;
                    try
                    {
                        lock (w)
                            w.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + e.Data);
                    }
                    catch
                    {
                    }
                };
                p.OutputDataReceived += sink;
                p.ErrorDataReceived += sink;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                s.Proc = p;
                s.StartedUtc = DateTime.UtcNow;
                WritePidFile(s, p);
                Log("Module service " + s.Key + " started (pid " + p.Id + ").");
            }
            catch (Exception ex)
            {
                CloseModuleLog(s);
                s.Proc = null;
                s.QuickFails++;
                s.NextStartUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 << Math.Min(6, s.QuickFails)));
                Log("Module service " + s.Key + " could not start: " + ex.Message);
                MaybeHandBack(s);
            }
        }

        // A schedule run: a library tool (python <tools>/<tool>/<tool>.py <run...>) or the module's own home/ code
        // (cwd inside the module folder, run = "python <args...>"); null when it cannot run.
        internal static ProcessStartInfo ModuleScheduleStartInfo(ModuleSpec m, Dictionary<string, object> spec, string python, string toolsRoot)
        {
            string tool = DeviceWire.S(spec, "tool");
            if (tool.Length > 0)
            {
                string script = Path.Combine(Path.Combine(toolsRoot, tool), tool + ".py");
                return File.Exists(script) ? ModuleServiceStartInfo(m, spec, python, script) : null;
            }
            string[] words = DeviceWire.S(spec, "run").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cwd = DeviceWire.S(spec, "cwd");
            if (words.Length < 2 || (words[0] != "python" && words[0] != "python3") || m.Dir.Length == 0 || cwd.IndexOf("..", StringComparison.Ordinal) >= 0)
                return null;
            string dir = Path.Combine(m.Dir, cwd.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
                return null;
            StringBuilder args = new StringBuilder();
            for (int i = 1; i < words.Length; i++)
                args.Append(i > 1 ? " " : "").Append(QuoteWinArg(words[i]));
            ProcessStartInfo psi = new ProcessStartInfo(python, args.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = dir;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["AIWB_MODULE"] = m.Id;
            psi.EnvironmentVariables["AIWB_MODULE_DIR"] = m.Dir;
            return psi;
        }

        private readonly Dictionary<string, Process> _modScheduleRuns = new Dictionary<string, Process>(StringComparer.Ordinal);
        private readonly Dictionary<string, StreamWriter> _modScheduleLogs = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);

        private void EndScheduleRun(string key)
        {
            Process p;
            if (_modScheduleRuns.TryGetValue(key, out p))
            {
                try
                {
                    if (p.HasExited)
                    {
                        p.WaitForExit();
                        Log("Module schedule " + key + " finished (code " + p.ExitCode + ").");
                    }
                }
                catch
                {
                }
                try { p.Dispose(); } catch { }
                _modScheduleRuns.Remove(key);
            }
            StreamWriter w;
            if (_modScheduleLogs.TryGetValue(key, out w))
            {
                try { lock (w) w.Dispose(); } catch { }
                _modScheduleLogs.Remove(key);
            }
        }

        // Schedules of the enabled modules: one run per due time, never two at once; the first sight only remembers
        // the time (no run for the past). Last due time in <modules>/.logs/<id>/<service>.last.
        private void TickModuleSchedules()
        {
            DateTime now = DateTime.UtcNow;
            HashSet<string> wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModuleSpec m in Modules.All())
                if (m.Enabled)
                    foreach (Dictionary<string, object> spec in Modules.Schedules(m))
                        wanted.Add(m.Id + "/" + DeviceWire.S(spec, "id"));
            foreach (string key in _modScheduleRuns.Keys.ToList())
            {
                Process p = _modScheduleRuns[key];
                bool done = HasExited(p);
                if (!done && wanted.Contains(key))
                    continue;
                if (!done)
                {
                    try { KillProcessTreeFallback(p); } catch { }
                    Log("Module schedule " + key + " stopped (module disabled or removed).");
                }
                EndScheduleRun(key);
            }
            foreach (ModuleSpec m in Modules.All())
            {
                if (!m.Enabled)
                    continue;
                foreach (Dictionary<string, object> spec in Modules.Schedules(m))
                {
                    string id = DeviceWire.S(spec, "id");
                    string key = m.Id + "/" + id;
                    if (_modScheduleRuns.ContainsKey(key))
                        continue; // still running: never two at once
                    try
                    {
                        string lastFile = Path.Combine(ModuleLogDir(m.Id), id + ".last");
                        DateTime last = DateTime.MinValue;
                        bool known = File.Exists(lastFile) && DateTime.TryParse(File.ReadAllText(lastFile, Encoding.ASCII).Trim(),
                            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last);
                        if (!known)
                        {
                            Directory.CreateDirectory(ModuleLogDir(m.Id));
                            File.WriteAllText(lastFile, now.ToString("o"), Encoding.ASCII);
                            continue;
                        }
                        DateTime? due = Modules.DueUtc(spec, now, last);
                        if (!due.HasValue || _exiting)
                            continue;
                        File.WriteAllText(lastFile, due.Value.ToString("o"), Encoding.ASCII);
                        string python = ModulePython();
                        ProcessStartInfo psi = python == null ? null : ModuleScheduleStartInfo(m, spec, python, Modules.ToolsRoot());
                        if (psi == null)
                        {
                            LogThrottled("module-sched-" + key, "Module schedule " + key + ": cannot run (python, tool or home/ code missing).");
                            continue;
                        }
                        StreamWriter w = OpenModuleLog(m, id);
                        Process p = new Process();
                        p.StartInfo = psi;
                        DataReceivedEventHandler sink = delegate(object o, DataReceivedEventArgs e)
                        {
                            if (e.Data == null)
                                return;
                            try { lock (w) w.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + e.Data); }
                            catch { }
                        };
                        p.OutputDataReceived += sink;
                        p.ErrorDataReceived += sink;
                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        _modScheduleRuns[key] = p;
                        _modScheduleLogs[key] = w;
                        Log("Module schedule " + key + " started (pid " + p.Id + ").");
                    }
                    catch (Exception ex)
                    {
                        LogThrottled("module-sched-" + key, "Module schedule " + key + ": " + ex.Message);
                    }
                }
            }
        }

        // How a service of a module starts: python <tools>/<tool>/<tool>.py <run...> in the tool's folder, no window.
        internal static ProcessStartInfo ModuleServiceStartInfo(ModuleSpec m, Dictionary<string, object> spec, string python, string script)
        {
            StringBuilder args = new StringBuilder(QuoteWinArg(script));
            foreach (string a in DeviceWire.S(spec, "run").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                args.Append(' ').Append(QuoteWinArg(a));
            ProcessStartInfo psi = new ProcessStartInfo(python, args.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = Path.GetDirectoryName(script);
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["AIWB_MODULE"] = m.Id;
            psi.EnvironmentVariables["AIWB_MODULE_DIR"] = m.Dir;
            return psi;
        }

        // moduleOff: the module was switched off or removed - a scheduled task the service was handed back to stops too.
        private void StopModuleService(ModuleService s, string why, bool moduleOff)
        {
            if (moduleOff && s.HandedBack && s.Task != null)
            {
                ScheduledTask(s.Task, "/End");
                ScheduledTask(s.Task, "/DISABLE");
                Log("Module service " + s.Key + ": scheduled task " + s.Task + " stopped and disabled (" + why + ").");
            }
            if (s.Proc != null && !HasExited(s.Proc))
            {
                try { KillProcessTreeFallback(s.Proc); } catch { }
                Log("Module service " + s.Key + " stopped (" + why + ").");
            }
            CloseModuleLog(s);
            s.Proc = null;
            try { File.Delete(PidFile(s)); } catch { }
        }

        // Bridge exits: its services stop with it and start again with the next Bridge.
        private void StopAllModuleServices()
        {
            lock (_modLock)
            {
                foreach (ModuleService s in _modServices.Values)
                    StopModuleService(s, "Bridge exits", false); // a handed-back task keeps running
                _modServices.Clear();
                foreach (string key in _modScheduleRuns.Keys.ToList())
                {
                    try { if (!_modScheduleRuns[key].HasExited) KillProcessTreeFallback(_modScheduleRuns[key]); } catch { }
                    EndScheduleRun(key);
                }
            }
        }

        private static string PidFile(ModuleService s)
        {
            string[] parts = s.Key.Split('/');
            return Path.Combine(ModuleLogDir(parts[0]), parts[1] + ".pid");
        }

        private static void WritePidFile(ModuleService s, Process p)
        {
            try
            {
                File.WriteAllText(PidFile(s), p.Id + " " + p.StartTime.ToUniversalTime().ToString("o"), Encoding.ASCII);
            }
            catch
            {
            }
        }

        // A service left running by a Bridge that crashed holds the port: end it when its pid and start time still match.
        private void KillStaleModuleService(ModuleService s)
        {
            try
            {
                string f = PidFile(s);
                if (!File.Exists(f))
                    return;
                string[] t = File.ReadAllText(f, Encoding.ASCII).Trim().Split(' ');
                File.Delete(f);
                int pid;
                if (t.Length != 2 || !Int32.TryParse(t[0], out pid))
                    return;
                using (Process old = Process.GetProcessById(pid))
                {
                    if (old.StartTime.ToUniversalTime().ToString("o") == t[1])
                    {
                        KillProcessTreeFallback(old);
                        Log("Module service " + s.Key + ": ended the copy left by an earlier Bridge (pid " + pid + ").");
                    }
                }
            }
            catch
            {
            }
        }

        // Logs and pid files live outside the module folder (<modules>/.logs/<id>), so an update can replace the folder
        // while the service runs.
        private static string ModuleLogDir(string id)
        {
            return Path.Combine(Path.Combine(Modules.Root(), ".logs"), id);
        }

        private static StreamWriter OpenModuleLog(ModuleSpec m, string service)
        {
            string dir = ModuleLogDir(m.Id);
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, service + ".log");
            try
            {
                if (File.Exists(f) && new FileInfo(f).Length > 2 * 1024 * 1024)
                {
                    string old = f + ".1";
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(f, old);
                }
            }
            catch
            {
            }
            StreamWriter w = new StreamWriter(new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            w.AutoFlush = true;
            return w;
        }

        private static void CloseModuleLog(ModuleService s)
        {
            StreamWriter w = s.LogWriter;
            s.LogWriter = null;
            if (w == null)
                return;
            try
            {
                lock (w)
                    w.Dispose();
            }
            catch
            {
            }
        }

        // python for module services and hooks: AIWB_MODULES_PYTHON, else python.exe from PATH (Windows) / python3.
        private static string ModulePython()
        {
            string env = Environment.GetEnvironmentVariable("AIWB_MODULES_PYTHON");
            if (!String.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return BridgePlatform.Current.Executables.Python;
#endif
            return PcPython();
        }

        private void ScheduledTask(string task, string verb)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return;
#endif
            if (!Regex.IsMatch(task, @"\A[\\A-Za-z0-9 ._-]{1,120}\z"))
                return;
            try
            {
                string args = verb == "/ENABLE" || verb == "/DISABLE"
                    ? "/Change /TN " + QuoteWinArg(task) + " " + verb
                    : verb + " /TN " + QuoteWinArg(task);
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                }
            }
            catch (Exception ex)
            {
                LogThrottled("schtasks-" + task, "Scheduled task " + task + " " + verb + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- tools (toolbox module state)

        // The owner switches a module in CO Web: the tool library's MCP server learns it through toolbox's module state
        // (toolbox 1.7.0: tools of a switched-off module are refused with the reason).
        private void SyncModuleTools()
        {
            string toolbox = Path.Combine(Path.Combine(Modules.ToolsRoot(), "toolbox"), "toolbox.py");
            string python = ModulePython();
            if (!File.Exists(toolbox) || python == null)
                return;
            foreach (ModuleSpec m in Modules.All())
            {
                List<string> tools = Modules.LibraryTools(m);
                bool was;
                if (tools.Count == 0 || (_modToolState.TryGetValue(m.Id, out was) && was == m.Enabled))
                    continue;
                string args = QuoteWinArg(toolbox) + " module " + m.Id + " --state " + (m.Enabled ? "enabled" : "disabled") +
                    " --tools " + String.Join(",", tools.ToArray()) + (m.Version.Length > 0 && Regex.IsMatch(m.Version, @"\A[0-9A-Za-z.+-]{1,32}\z") ? " --version " + m.Version : "");
                if (RunModuleTool(python, args, null, 30000) != null)
                    _modToolState[m.Id] = m.Enabled;
            }
        }

        // ---------------------------------------------------------------- models

        private void SyncModuleResources()
        {
            if (!DbqEnabled || String.IsNullOrEmpty(Modules.OwnerEnv))
                return;
            string file = Path.Combine(Modules.Root(), ".resources.json");
            Dictionary<string, object> done = null;
            try { done = File.Exists(file) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object> : null; }
            catch { done = null; }
            if (done == null)
                done = new Dictionary<string, object>();
            Dictionary<string, object> now = new Dictionary<string, object>();
            foreach (ModuleSpec m in Modules.All())
            {
                if (!m.Enabled || !m.Has("models.register"))
                    continue;
                foreach (Dictionary<string, object> r in Modules.Resources(m))
                {
                    string id = Modules.OwnerEnv + "/" + m.Id + "-" + DeviceWire.S(r, "id");
                    string sig = m.Version + "|" + JsonUtil.Serialize(r);
                    now[id] = sig;
                    if (done.ContainsKey(id) && done[id] as string == sig)
                        continue;
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["id"] = id;
                    body["env"] = Modules.OwnerEnv;
                    body["kind"] = "subscription";
                    body["agent"] = DeviceWire.S(r, "agent");
                    body["host"] = _config.hostId ?? "";
                    body["title"] = DeviceWire.S(r, "title").Length > 0 ? DeviceWire.S(r, "title") : id;
                    foreach (string k in new string[] { "models", "defaultModel", "card", "confidential", "slaveAllowed" })
                        if (r.ContainsKey(k))
                            body[k] = r[k];
                    try
                    {
                        DbqCall("model_register", body, true, 15000);
                        Log("Module " + m.Id + ": model resource " + id + " registered.");
                    }
                    catch (Exception ex)
                    {
                        if (done.ContainsKey(id))
                            now[id] = done[id]; // keep the registered resource; the update is retried next time
                        else
                            now.Remove(id);
                        LogThrottled("module-model-" + id, "Module " + m.Id + ": model resource " + id + " not registered: " + ex.Message);
                    }
                }
            }
            foreach (string id in done.Keys.ToList())
            {
                if (now.ContainsKey(id))
                    continue;
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["id"] = id;
                    DbqCall("model_unregister", body, true, 15000);
                    Log("Module model resource " + id + " unregistered.");
                }
                catch (Exception ex)
                {
                    now[id] = done[id]; // try again next time
                    LogThrottled("module-model-" + id, "Module model resource " + id + " not unregistered: " + ex.Message);
                }
            }
            try { AtomicFile.WriteJson(file, now); } catch { }
        }

        // ---------------------------------------------------------------- events (job.before / job.after)

        // Hooks of the modules that work for the job's environment: a library tool gets the job as JSON on stdin. A
        // job.before hook may answer {"context": "..."} (needs the permission "context"); any hook may answer
        // {"push": {"title", "body", "url"}} (needs "push.send"), which the core sends to the job's environment.
        // job.before waits up to 10 s per hook, job.after runs in the background; a failing hook never fails the job.
        private string RunModuleHooks(string ev, JobRequest request, string status)
        {
            List<KeyValuePair<ModuleSpec, Dictionary<string, object>>> hooks = Modules.Hooks(ev, request.environmentId);
            if (hooks.Count == 0)
                return "";
            string python = ModulePython();
            if (python == null)
                return "";
            Dictionary<string, object> input = new Dictionary<string, object>();
            input["event"] = ev;
            input["jobId"] = request.jobId;
            input["env"] = request.environmentId ?? "";
            input["chatId"] = request.chatId ?? "";
            input["projectId"] = request.projectId ?? "";
            input["agent"] = request.agent ?? "";
            if (status != null)
                input["status"] = status;
            string stdin = JsonUtil.Serialize(input);
            StringBuilder context = new StringBuilder();
            foreach (KeyValuePair<ModuleSpec, Dictionary<string, object>> h in hooks)
            {
                ModuleSpec m = h.Key;
                string tool = DeviceWire.S(h.Value, "tool");
                string script = Path.Combine(Path.Combine(Modules.ToolsRoot(), tool), tool + ".py");
                if (!File.Exists(script))
                    continue;
                StringBuilder args = new StringBuilder(QuoteWinArg(script));
                foreach (string a in DeviceWire.S(h.Value, "run").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    args.Append(' ').Append(QuoteWinArg(a));
                string env = request.environmentId;
                string jobId = request.jobId;
                if (ev == "job.after")
                {
                    string a2 = args.ToString();
                    ThreadPool.QueueUserWorkItem(delegate { ModuleHookAnswer(m, env, jobId, RunModuleTool(python, a2, stdin, 30000)); });
                    continue;
                }
                string c = ModuleHookAnswer(m, env, jobId, RunModuleTool(python, args.ToString(), stdin, 10000));
                if (c.Length > 0)
                    context.AppendLine(c.Length > 4000 ? c.Substring(0, 4000) : c);
            }
            return context.ToString();
        }

        private string ModuleHookAnswer(ModuleSpec m, string env, string jobId, string output)
        {
            if (String.IsNullOrWhiteSpace(output))
                return "";
            Dictionary<string, object> r;
            try { r = new JavaScriptSerializer().DeserializeObject(output.Trim()) as Dictionary<string, object>; }
            catch { return ""; }
            Dictionary<string, object> push = Modules.Obj(r, "push");
            if (push != null && m.Has("push.send"))
            {
                string url = DeviceWire.S(push, "url");
                Notify(String.IsNullOrEmpty(env) ? OwnerPushEnv() : env, Cut(DeviceWire.S(push, "title"), 80), Cut(DeviceWire.S(push, "body"), 300),
                    url.StartsWith("/", StringComparison.Ordinal) ? url : "/", "module-" + m.Id);
            }
            return m.Has("context") ? DeviceWire.S(r, "context").Trim() : "";
        }

        private static string Cut(string s, int n)
        {
            return s.Length > n ? s.Substring(0, n) : s;
        }

        // Runs python with args (stdin text optional); stdout, or null when it failed or timed out.
        private string RunModuleTool(string python, string args, string stdin, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(python, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.WorkingDirectory = Modules.ToolsRoot();
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> e = p.StandardError.ReadToEndAsync();
                    try
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(stdin ?? "");
                        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        p.StandardInput.Close();
                    }
                    catch
                    {
                    }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { KillProcessTreeFallback(p); } catch { }
                        LogThrottled("module-tool", "Module hook/tool timed out: " + args);
                        return null;
                    }
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        LogThrottled("module-tool", "Module hook/tool exit " + p.ExitCode + ": " + args + " " + Cut(e.Result.Trim(), 300));
                        return null;
                    }
                    return o.Result;
                }
            }
            catch (Exception ex)
            {
                LogThrottled("module-tool", "Module hook/tool failed: " + ex.Message);
                return null;
            }
        }

        // Called once per job before its agent starts: event "modules" (which modules and versions it runs with), the
        // context of a new session (module fragments + job.before answers) for BuildAgentPrompt.
        private void PrepareModulesForJob(JobRequest request, EventBatchWriter eventWriter)
        {
            try
            {
                string active = Modules.ActiveSummary(request.environmentId);
                if (active.Length == 0)
                    return;
                eventWriter.Write("bridge", "modules", "Modules: " + active + ".");
                string op = AgentRuntime.ConversationOperation(request);
                string before = RunModuleHooks("job.before", request, null);
                string ctx = (op == "new" ? Modules.Context(request.environmentId) : "") + before;
                request.moduleContext = ctx.Trim().Length > 0 ? ctx.Trim() : null;
            }
            catch (Exception ex)
            {
                LogThrottled("modules-job", "Modules: job preparation skipped: " + ex.Message);
            }
        }
    }
}
