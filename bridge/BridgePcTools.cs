// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AiWorkbenchBridge
{
    /*
     * PC TOOLS OF THE RELAY DEVICE (v1.15.0, subtask B of chat CHAT-20261112071412-CJZ0)
     * ==================================================================================
     * A site job reaches the home PC through device "h-<host>" (short "home") and may code, build and run there as a
     * Home job does: pc_shell (PowerShell or Git Bash, background runs with pc_shell_status / pc_shell_kill), pc_read,
     * pc_write, pc_edit, pc_list, pc_search, pc_upload / pc_download (files between the site job and the PC in chunks
     * that fit db.php: args <= 1 MiB, result <= 4 MiB).
     *
     * Owner's environment: the whole PC, under the per-site-job state "relay-<jobId>" (call limit, taint, deny list);
     * every call first goes through read-guard's PreToolUse check exactly as the same Bash/PowerShell/Read/Write/Edit
     * call of a Home job would (held actions answer owner_confirm_required with the code for the owner), output is
     * untrusted. Family environments (devices.pcFamily: env -> Linux user): their own device "h-<host>-<user>" with
     * pc_* only, run inside the WSL distro devices.pcSandbox ("aiwb-family") as that user, files only under
     * /home/<user> (checked after realpath on the Linux side); no tool library, no Windows files, no Drive, no keyring.
     * The distro missing -> sandbox_missing; Windows drives or interop visible inside it -> sandbox_unsafe.
     */

    // A family member's sandbox; null = the owner's Windows.
    internal sealed class PcBox
    {
        public string env;
        public string user;
        public string distro;
        public string Home { get { return "/home/" + user; } }
    }

    internal static class PcTools
    {
        public const int MaxReadBytes = 2 * 1024 * 1024;       // one pc_read base64 / pc_download chunk
        public const int MaxTextFileBytes = 32 * 1024 * 1024;  // pc_read text / pc_edit read the whole file
        public const int MaxTextChars = 200000;
        public const int DefaultOutputChars = 30000;
        public const int MaxOutputChars = 200000;
        public const int MaxForegroundSec = 1800;               // relay calls of the site agent wait 2400 s
        public const int MaxBackgroundSec = 6 * 3600;
        public const int MaxBackgroundPerEnv = 8;
        public const int MaxFamilyScriptChars = 16000;          // wsl.exe command line (32767) after base64

        public static readonly string[] Names = { "pc_shell", "pc_shell_status", "pc_shell_kill", "pc_read", "pc_write",
                                                  "pc_edit", "pc_list", "pc_search", "pc_upload", "pc_download" };

        public static bool IsPcTool(string tool)
        {
            return tool != null && Array.IndexOf(Names, tool) >= 0;
        }

        public static Dictionary<string, object> Fail(string code, string message)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["code"] = code;
            e["message"] = message;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = false;
            r["content"] = "";
            r["error"] = e;
            r["untrusted"] = false;
            return r;
        }

        public static Dictionary<string, object> Ok(string content)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true;
            r["content"] = content ?? "";
            r["untrusted"] = true;     // TZ §9.4: what the PC returns may carry text from outside
            return r;
        }

        // ------------------------------------------------------------ arguments

        public static string Str(Dictionary<string, object> a, string key)
        {
            object v;
            return a != null && a.TryGetValue(key, out v) ? v as string : null;
        }

        public static bool Bool(Dictionary<string, object> a, string key)
        {
            object v;
            return a != null && a.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static long Int(Dictionary<string, object> a, string key, long def, long min, long max)
        {
            object v;
            if (a == null || !a.TryGetValue(key, out v) || !(v is int || v is long || v is decimal || v is double))
                return def;
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (Double.IsNaN(d))
                return def;
            return (long)Math.Max(min, Math.Min(max, Math.Floor(d)));
        }

        // Environment variables a call may set: plain names, never the Bridge's / read-guard's own (AIWB_*) nor the
        // switch of live browser tests that read-guard holds (CO_LIVE_BROWSER), nor PATH-like loader settings.
        public static Dictionary<string, string> EnvVars(Dictionary<string, object> a, out string refusal)
        {
            refusal = null;
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object v;
            if (a == null || !a.TryGetValue("env", out v) || v == null)
                return env;
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d == null)
            {
                refusal = "'env' must be an object of strings.";
                return null;
            }
            foreach (KeyValuePair<string, object> kv in d)
            {
                string val = kv.Value as string;
                if (val == null || !Regex.IsMatch(kv.Key, @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z") || env.Count >= 64 || val.Length > 8192)
                {
                    refusal = "'env' must map plain names to strings (at most 64).";
                    return null;
                }
                string k = kv.Key.ToUpperInvariant();
                if (k.StartsWith("AIWB_", StringComparison.Ordinal) || k == "CO_LIVE_BROWSER" || k == "PATH" || k == "PATHEXT" ||
                    k == "COMSPEC" || k == "PYTHONPATH" || k == "PYTHONSTARTUP" || k == "BASH_ENV" || k == "ENV" || k.StartsWith("LD_", StringComparison.Ordinal) ||
                    k == "PSMODULEPATH" || k == "NODE_OPTIONS")
                {
                    refusal = "Variable " + kv.Key + " may not be set through pc_shell; set it inside the script if it is really needed.";
                    return null;
                }
                env[kv.Key] = val;
            }
            return env;
        }

        // Who serves a relay call to the home PC: "pc-owner", "pc-family" (box = the sandbox of that device id),
        // "library" (tool_list/tool_help/tool_run of h-<host>) or "device" (a paired device, checked by the caller);
        // refusal set when the environment may not use it. Family members get pc_* of their own sandbox only.
        public static string Route(string deviceId, string homeId, string tool, string env, string ownerEnv, PcBox box,
                                   List<string> deny, out Dictionary<string, object> refusal)
        {
            refusal = null;
            bool owner = String.Equals(env, ownerEnv, StringComparison.Ordinal);
            if (box != null)
            {
                if (!String.Equals(env, box.env, StringComparison.Ordinal))
                    refusal = Fail("not_allowed", "This sandbox serves only its own environment.");
                else if (!IsPcTool(tool))
                    refusal = Fail("not_allowed", "Only the pc_* tools of the family sandbox are available here (no tool library, Windows files, Drive or keyring).");
                return "pc-family";
            }
            if (deviceId == homeId)
            {
                if (!owner)
                    refusal = Fail("not_allowed", IsPcTool(tool) ? "The home PC serves only the owner's environment." : "The home PC's tool library serves only the owner's environment.");
                else if (JobDeviceState.DenySet(deny).Contains(tool ?? ""))
                    refusal = Fail("tool_off", "This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.");
                return IsPcTool(tool) ? "pc-owner" : "library";
            }
            if (deviceId != null && deviceId.StartsWith(homeId + "-", StringComparison.Ordinal))
            {
                refusal = Fail("not_allowed", "No sandbox of the home PC for this environment.");
                return "pc-family";
            }
            return "device";
        }

        // ------------------------------------------------------------ output

        // Head and tail of a long text with a marker in between (full text in the log file).
        public static string Cut(string text, int max, string logPath)
        {
            if (text == null)
                return "";
            if (text.Length <= max)
                return text;
            int head = max * 2 / 5;
            int tail = max - head;
            return text.Substring(0, head) + "\n... [" + (text.Length - head - tail).ToString(CultureInfo.InvariantCulture) +
                " characters cut" + (logPath != null ? "; full output: " + logPath : "") + "] ...\n" + text.Substring(text.Length - tail);
        }

        // Lines as Claude Code's Read shows them: "<n>\t<line>", 1-based offset, long lines cut.
        public static string NumberLines(string text, long offset, long limit, out long total, out bool more)
        {
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0 && (text ?? "").Length > 0)
                count--;           // a final newline is no extra line
            total = count;
            StringBuilder sb = new StringBuilder();
            long start = Math.Max(1, offset);
            long end = Math.Min(count, start + limit - 1);
            for (long i = start; i <= end; i++)
            {
                string l = lines[i - 1];
                if (l.Length > 2000)
                    l = l.Substring(0, 2000) + " ... [line cut]";
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(l).Append('\n');
                if (sb.Length > MaxTextChars)
                {
                    end = i;
                    break;
                }
            }
            more = end < count;
            return sb.ToString();
        }

        // Exact replacement as Claude Code's Edit: old_string must occur once unless replace_all.
        public static string Replace(string text, string oldS, string newS, bool all, out int count, out string refusal)
        {
            refusal = null;
            count = 0;
            if (String.IsNullOrEmpty(oldS))
            {
                refusal = "old_string is empty.";
                return null;
            }
            if (oldS == newS)
            {
                refusal = "old_string and new_string are the same.";
                return null;
            }
            int at = 0;
            int first = -1;
            while ((at = text.IndexOf(oldS, at, StringComparison.Ordinal)) >= 0)
            {
                if (first < 0) first = at;
                count++;
                at += oldS.Length;
            }
            if (count == 0)
            {
                refusal = "old_string was not found in the file.";
                return null;
            }
            if (count > 1 && !all)
            {
                refusal = "old_string occurs " + count + " times; give more context to make it unique or set replace_all.";
                return null;
            }
            return all ? text.Replace(oldS, newS) : text.Substring(0, first) + newS + text.Substring(first + oldS.Length);
        }

        // ------------------------------------------------------------ sandbox (family)

        public static bool ValidLinuxUser(string user)
        {
            return user != null && Regex.IsMatch(user, @"\A[a-z_][a-z0-9_-]{0,31}\z") && user != "root";
        }

        // POSIX single-quoted string.
        public static string ShQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }

        // Path checked after realpath on the Linux side must be the home or below it (no /mnt, no other homes).
        public static bool InHome(string resolved, string home)
        {
            if (String.IsNullOrEmpty(resolved) || resolved.IndexOf('\0') >= 0)
                return false;
            return resolved == home || resolved.StartsWith(home + "/", StringComparison.Ordinal);
        }

        // Shell prelude of every sandbox command: aiwb_in PATH prints the realpath when it is inside the home.
        public static string FamilyPrelude(PcBox box)
        {
            string home = ShQuote(box.Home);
            return "aiwb_home=" + home + "\n" +
                   "aiwb_in() { local r; case \"$1\" in /*) r=$(realpath -m -- \"$1\") ;; *) r=$(realpath -m -- \"$aiwb_home/$1\") ;; esac || return 1; " +
                   "case \"$r\" in \"$aiwb_home\"|\"$aiwb_home\"/*) printf '%s' \"$r\" ;; *) return 1 ;; esac; }\n";
        }

        // Script of a pc_shell run inside the sandbox: pid file (for kill), cwd inside the home, variables, the body.
        public static string FamilyShellScript(PcBox box, string id, string cwd, Dictionary<string, string> env, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(FamilyPrelude(box));
            sb.Append("mkdir -p \"$aiwb_home/.aiwb-pc\" && echo $$ > \"$aiwb_home/.aiwb-pc/" + id + ".pid\"\n");
            sb.Append("aiwb_cwd=$(aiwb_in " + ShQuote(String.IsNullOrEmpty(cwd) ? box.Home : cwd) + ") || { echo \"pc_shell: cwd must be inside " + box.Home + "\" >&2; exit 126; }\n");
            sb.Append("cd -- \"$aiwb_cwd\" || exit 126\nunset aiwb_cwd\n");
            if (env != null)
                foreach (KeyValuePair<string, string> kv in env)
                    sb.Append("export ").Append(kv.Key).Append('=').Append(ShQuote(kv.Value)).Append('\n');
            sb.Append(body ?? "").Append('\n');
            return sb.ToString();
        }

        // Kills the process tree of a sandbox run by its pid file (the Windows wsl.exe client dying does not stop it).
        public static string FamilyKillScript(PcBox box, string id)
        {
            return "f=" + ShQuote(box.Home + "/.aiwb-pc/" + id + ".pid") + "\n" +
                   "k() { local c; for c in $(cat /proc/$1/task/*/children 2>/dev/null); do k $c; done; kill -KILL $1 2>/dev/null; }\n" +
                   "[ -f \"$f\" ] && p=$(cat \"$f\") && [ -n \"$p\" ] && k \"$p\"; rm -f \"$f\"; true\n";
        }

        // wsl.exe arguments (the interface of the sandbox, subtask C: "--" so the login shell aiwb-shell enters it; never -e
        // or -u root): the script goes base64-encoded and is sourced from a process substitution, so the Windows
        // command line holds no quote, '%' or text of it (safe for cmd.exe of WithStdin as well).
        public static string WslArguments(PcBox box, string script)
        {
            string b64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(script.Replace("\r\n", "\n")));
            return "-d " + box.distro + " -u " + box.user + " --cd ~ -- bash --noprofile --norc -c \". <(base64 -d <<<" + b64 + ")\"";
        }

        // ------------------------------------------------------------ owner's paths

        // Absolute Windows path, no device namespace (\\.\, \\?\) tricks.
        public static string OwnerPath(string p, out string refusal)
        {
            refusal = null;
            if (String.IsNullOrWhiteSpace(p))
            {
                refusal = "'path' is required.";
                return null;
            }
            p = p.Trim();
            if (p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.IndexOf('\0') >= 0)
            {
                refusal = "Device paths are not supported.";
                return null;
            }
            if (Regex.IsMatch(p, @"\A/[a-zA-Z]/"))
                p = p.Substring(1, 1).ToUpperInvariant() + ":" + p.Substring(2);          // Git Bash /c/... form
            if (!Path.IsPathRooted(p) || !(Regex.IsMatch(p, @"\A[A-Za-z]:[\\/]") || p.StartsWith(@"\\", StringComparison.Ordinal)))
            {
                refusal = "The path must be absolute (C:\\... or \\\\server\\share\\...).";
                return null;
            }
            try
            {
                return Path.GetFullPath(p);
            }
            catch (Exception ex)
            {
                refusal = "Bad path: " + ex.Message;
                return null;
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 h = SHA256.Create())
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h.ComputeHash(data))
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Json(Dictionary<string, object> o)
        {
            return JsonUtil.Serialize(o);
        }

        // ------------------------------------------------------------ schemas (relay_devices_put, given to the agent as is)

        private static Dictionary<string, object> P(string type, string description)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = type;
            if (description != null)
                p["description"] = description;
            return p;
        }

        private static Dictionary<string, object> Def(string name, string description, Dictionary<string, object> props, string[] required)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "object";
            schema["properties"] = props;
            if (required != null)
                schema["required"] = required;
            Dictionary<string, object> t = new Dictionary<string, object>();
            t["name"] = name;
            t["description"] = description;
            t["inputSchema"] = schema;
            return t;
        }

        public static object[] Schemas(PcBox box)
        {
            bool fam = box != null;
            string where = fam ? "in the family Linux sandbox on the home PC (WSL '" + box.distro + "', user " + box.user + ", files only under " + box.Home + ")"
                               : "on the owner's home Windows PC (the whole PC, as a Home job)";
            string pathHint = fam ? "Path inside " + box.Home + " (absolute, or relative to it)." : "Absolute Windows path (C:\\...).";

            Dictionary<string, object> sh = new Dictionary<string, object>();
            sh["command"] = P("string", "Script text to run.");
            if (!fam)
            {
                Dictionary<string, object> shell = P("string", "powershell (Windows PowerShell 5.1, default) or bash (Git Bash).");
                shell["enum"] = new string[] { "powershell", "bash" };
                sh["shell"] = shell;
            }
            sh["cwd"] = P("string", fam ? "Working directory inside " + box.Home + " (default: the home)." : "Working directory, absolute (default: a scratch folder of the relay).");
            Dictionary<string, object> to = P("integer", "Seconds before the run is killed with its process tree. Foreground default 120, max " + MaxForegroundSec + "; background default 3600, max " + MaxBackgroundSec + ".");
            to["minimum"] = 1;
            sh["timeout_sec"] = to;
            Dictionary<string, object> env = P("object", "Extra environment variables (strings).");
            env["additionalProperties"] = P("string", null);
            sh["env"] = env;
            sh["stdin"] = P("string", "Text for the script's standard input.");
            sh["background"] = P("boolean", "Start and return an id at once; follow it with pc_shell_status, stop it with pc_shell_kill.");
            Dictionary<string, object> mo = P("integer", "Characters of stdout and of stderr returned (head and tail kept; the full output is in the log file). Default " + DefaultOutputChars + ", max " + MaxOutputChars + ".");
            sh["max_output"] = mo;

            Dictionary<string, object> st = new Dictionary<string, object>();
            st["id"] = P("string", "Id from pc_shell background; without it lists the runs of this chat.");
            st["tail"] = P("integer", "Characters of the output tail to show (default 4000).");
            Dictionary<string, object> kill = new Dictionary<string, object>();
            kill["id"] = P("string", "Id of a background run of this chat.");

            Dictionary<string, object> rd = new Dictionary<string, object>();
            rd["path"] = P("string", pathHint);
            rd["offset"] = P("integer", "Text: first line (1-based). Base64: first byte (0-based).");
            rd["limit"] = P("integer", "Text: number of lines (default 2000). Base64: bytes (default and max " + MaxReadBytes + ").");
            Dictionary<string, object> enc = P("string", "text (default, numbered lines) or base64 (raw bytes, for binary files).");
            enc["enum"] = new string[] { "text", "base64" };
            rd["encoding"] = enc;

            Dictionary<string, object> wr = new Dictionary<string, object>();
            wr["path"] = P("string", pathHint);
            wr["content"] = P("string", "New text of the file (UTF-8).");
            wr["content_base64"] = P("string", "Or the bytes, base64 (binary files).");
            wr["append"] = P("boolean", "Append instead of replacing.");

            Dictionary<string, object> ed = new Dictionary<string, object>();
            ed["path"] = P("string", pathHint);
            ed["old_string"] = P("string", "Exact text to replace; must be unique in the file unless replace_all.");
            ed["new_string"] = P("string", "Replacement text.");
            ed["replace_all"] = P("boolean", "Replace every occurrence.");

            Dictionary<string, object> ls = new Dictionary<string, object>();
            ls["path"] = P("string", pathHint);
            ls["depth"] = P("integer", "Levels below the path (default 1, max 5).");
            ls["max_entries"] = P("integer", "Default 500, max 5000.");

            Dictionary<string, object> se = new Dictionary<string, object>();
            se["pattern"] = P("string", "Regular expression (ripgrep syntax when ripgrep is installed, else grep -E).");
            se["path"] = P("string", pathHint + " File or folder to search.");
            se["glob"] = P("string", "File name filter, e.g. *.cs.");
            se["ignore_case"] = P("boolean", null);
            se["files_only"] = P("boolean", "Only the names of matching files.");
            se["max_results"] = P("integer", "Lines of output (default 300, max 3000).");

            Dictionary<string, object> up = new Dictionary<string, object>();
            up["path"] = P("string", pathHint + " Target file on the PC.");
            up["local_path"] = P("string", "File of the site job to send (the relay reads it and sends it in chunks).");
            up["data_base64"] = P("string", "Or one chunk of bytes, base64 (at most ~700 KB per call).");
            up["append"] = P("boolean", "Append the chunk (later chunks of a big file).");

            Dictionary<string, object> dn = new Dictionary<string, object>();
            dn["path"] = P("string", pathHint + " File on the PC.");
            dn["local_path"] = P("string", "File of the site job to write (the relay fetches every chunk).");
            dn["offset"] = P("integer", "First byte of the chunk (without local_path).");
            dn["length"] = P("integer", "Bytes of the chunk, default and max " + MaxReadBytes + " (without local_path).");

            string held = fam ? "" : " Risky commands wait for the owner's yes as in a Home job (read-guard): the answer then says owner_confirm_required with a code; ask the owner in the chat and run `python C:\\AIWorkbench\\tools\\read-guard\\read-guard.py grant <code>` through pc_shell after the owner's \"<code> да\".";
            return new object[]
            {
                Def("pc_shell", "Run a script " + where + "; returns exit code, stdout and stderr (long output cut in the middle, full log kept on the PC)." + held +
                    (fam ? "" : " Default shell is PowerShell 5.1: no && or ||, use ; and if ($?) { }."), sh, new string[] { "command" }),
                Def("pc_shell_status", "State, exit code and output tail of a background pc_shell run of this chat; without id - all runs of this chat.", st, null),
                Def("pc_shell_kill", "Stop a background pc_shell run of this chat with its process tree.", kill, new string[] { "id" }),
                Def("pc_read", "Read a file " + where + ": text as numbered lines (offset/limit like Read), or bytes as base64.", rd, new string[] { "path" }),
                Def("pc_write", "Create or replace (or append to) a file " + where + "; parent folders are created.", wr, new string[] { "path" }),
                Def("pc_edit", "Exact string replacement in a text file " + where + ", like Edit.", ed, new string[] { "path", "old_string", "new_string" }),
                Def("pc_list", "List a folder " + where + " (type, size, modified, path).", ls, new string[] { "path" }),
                Def("pc_search", "Search file contents " + where + " (ripgrep if installed, else grep).", se, new string[] { "pattern", "path" }),
                Def("pc_upload", "Copy a file from the site job to the PC " + where + ": local_path (whole file) or data_base64 chunks.", up, new string[] { "path" }),
                Def("pc_download", "Copy a file from the PC " + where + " to the site job: local_path (whole file) or one base64 chunk by offset/length.", dn, new string[] { "path" }),
            };
        }
    }

    // A pc_shell run: foreground (the call waits) or background (pc_shell_status / pc_shell_kill).
    internal sealed class PcRun
    {
        public string id;
        public string env;
        public string chatId;
        public string jobKey;
        public PcBox box;
        public Process process;
        public string outLog;
        public string errLog;
        public string command;
        public DateTime startedUtc;
        public DateTime endedUtc;
        public int timeoutSec;
        public volatile bool done;
        public volatile bool killed;
        public volatile string killReason;
        public int exitCode;
        public PcCapture outCap;
        public PcCapture errCap;
        public string stdinFile;
        public readonly ManualResetEvent exited = new ManualResetEvent(false);
    }

    // Copies a stream into its log file and keeps the head and the tail in memory.
    internal sealed class PcCapture
    {
        private readonly StringBuilder _head = new StringBuilder();
        private readonly char[] _tail;
        private long _total;
        private int _tailPos;
        private readonly int _headMax;
        private readonly object _sync = new object();
        public readonly Thread thread;

        public PcCapture(StreamReader reader, string logPath, int keepChars)
        {
            _headMax = Math.Max(1, keepChars * 2 / 5);
            _tail = new char[Math.Max(1, keepChars - _headMax)];
            thread = new Thread(delegate() { Pump(reader, logPath); });
            thread.IsBackground = true;
            thread.Name = "pc-capture";
            thread.Start();
        }

        private void Pump(StreamReader reader, string logPath)
        {
            StreamWriter log = null;
            try
            {
                try { log = new StreamWriter(logPath, false, new UTF8Encoding(false)); }
                catch { log = null; }
                char[] buf = new char[8192];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    if (log != null)
                    {
                        try { log.Write(buf, 0, n); log.Flush(); }
                        catch { }
                    }
                    lock (_sync)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (_head.Length < _headMax)
                                _head.Append(buf[i]);
                            else
                            {
                                _tail[_tailPos] = buf[i];
                                _tailPos = (_tailPos + 1) % _tail.Length;
                            }
                        }
                        _total += n;
                    }
                }
            }
            catch { }
            finally
            {
                if (log != null)
                    try { log.Dispose(); } catch { }
            }
        }

        public long Total { get { lock (_sync) return _total; } }

        // Head + tail of what was read, with the marker of the cut part.
        public string Text(string logPath, int max)
        {
            string all = Text(logPath);
            if (all.Length <= max)
                return all;
            return "... [" + (all.Length - max).ToString(CultureInfo.InvariantCulture) + " characters before; full output: " + logPath + "] ...\n" +
                all.Substring(all.Length - max);
        }

        public string Text(string logPath)
        {
            lock (_sync)
            {
                long tailLen = Math.Min(_tail.Length, Math.Max(0, _total - _head.Length));
                StringBuilder sb = new StringBuilder(_head.ToString());
                long cut = _total - _head.Length - tailLen;
                if (cut > 0)
                    sb.Append("\n... [" + cut.ToString(CultureInfo.InvariantCulture) + " characters cut; full output: " + logPath + "] ...\n");
                if (tailLen > 0)
                {
                    int start = (int)((_tailPos - tailLen + _tail.Length) % _tail.Length);
                    for (int i = 0; i < tailLen; i++)
                        sb.Append(_tail[(start + i) % _tail.Length]);
                }
                return sb.ToString();
            }
        }
    }

    // Waiting, killing and reporting of pc_shell runs (static: the tests drive real processes with it).
    internal static class PcExec
    {
        // Starts the process of a run and the capture of its output; stdin is written and closed.
        // Starts the process of a run (psi from WithStdin) and the capture of its output.
        public static void Start(PcRun run, ProcessStartInfo psi, int maxOut)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            run.startedUtc = DateTime.UtcNow;
            run.process = Process.Start(psi);
            run.outCap = new PcCapture(run.process.StandardOutput, run.outLog, maxOut);
            run.errCap = new PcCapture(run.process.StandardError, run.errLog, maxOut);
        }

        // Runs a short process to its end: stdout bytes, or null when it did not run or timed out (tree killed).
        public static byte[] Capture(ProcessStartInfo psi, int timeoutSec, out int exitCode, out string stderr)
        {
            exitCode = -1;
            stderr = "";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            try
            {
                using (Process p = Process.Start(psi))
                {
                    MemoryStream ms = new MemoryStream();
                    System.Threading.Tasks.Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
                    System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutSec * 1000))
                    {
                        BridgeApplicationContext.PcKillTree(p);
                        stderr = "timeout after " + timeoutSec + " s";
                        return null;
                    }
                    copy.Wait(10000);
                    err.Wait(2000);
                    stderr = err.IsCompleted ? err.Result : "";
                    exitCode = p.ExitCode;
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return null;
            }
        }

        public static string SystemExe(string name)
        {
            return Path.Combine(Environment.SystemDirectory, name);
        }

        // A process whose standard input is a file (or NUL) through cmd.exe. A redirected stdin pipe of .NET Framework
        // starts with the BOM of the console code page when that is UTF-8 (65001), which would reach bash scripts,
        // files written in the sandbox and read-guard's JSON. The arguments must hold no '"' inside quoted parts and
        // no '%' (cmd would expand it): script paths, base64 and fixed text only.
        public static ProcessStartInfo WithStdin(string exe, string arguments, string stdinFile)
        {
            string inner = "\"" + exe + "\" " + arguments + " < " + (stdinFile == null ? "NUL" : "\"" + stdinFile + "\"");
            return new ProcessStartInfo(SystemExe("cmd.exe"), "/d /s /c \"" + inner + "\"");
        }

        // Writes stdin bytes to a temporary file for WithStdin; null when there are none.
        public static string StdinFile(string dir, byte[] data)
        {
            if (data == null)
                return null;
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "in-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(f, data);
            return f;
        }

        public static void DeleteQuietly(string file)
        {
            if (file == null)
                return;
            try { File.Delete(file); } catch { }
        }

        // Waits for a run (its own timeout), kills the tree on timeout, then records the exit. familyKill stops the
        // Linux side of a sandbox run (the Windows wsl.exe client dying does not stop it).
        public static void Wait(PcRun run, Action<PcRun> familyKill)
        {
            Process p = run.process;
            try
            {
                if (!p.WaitForExit(run.timeoutSec * 1000))
                {
                    run.killReason = "timeout after " + run.timeoutSec + " s";
                    Kill(run, familyKill);
                    p.WaitForExit(15000);
                }
                run.outCap.thread.Join(5000);
                run.errCap.thread.Join(2000);
                try { run.exitCode = p.HasExited ? p.ExitCode : -1; } catch { run.exitCode = -1; }
            }
            catch (Exception)
            {
                run.exitCode = -1;
            }
            finally
            {
                if (run.box != null && familyKill != null)
                    try { familyKill(run); } catch { }      // leftovers of the tree (script's own background jobs), pid file
                DeleteQuietly(run.stdinFile);
                run.endedUtc = DateTime.UtcNow;
                run.done = true;
                run.exited.Set();
                try { p.Dispose(); } catch { }
            }
        }

        public static void Kill(PcRun run, Action<PcRun> familyKill)
        {
            run.killed = true;
            if (run.box != null && familyKill != null)
                try { familyKill(run); } catch { }
            try { BridgeApplicationContext.PcKillTree(run.process); } catch { }
        }

        public static string ShownPath(PcBox box, string path)
        {
            return box == null ? path : "(log on the PC; pc_shell_status shows the tail)";
        }

        public static string Text(PcRun run, int maxOut)
        {
            StringBuilder sb = new StringBuilder();
            double sec = ((run.endedUtc == DateTime.MinValue ? DateTime.UtcNow : run.endedUtc) - run.startedUtc).TotalSeconds;
            sb.Append("exit code ").Append(run.done ? run.exitCode.ToString(CultureInfo.InvariantCulture) : "-")
              .Append(" · ").Append(sec.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s");
            if (run.killReason != null)
                sb.Append(" · killed: ").Append(run.killReason);
            sb.Append('\n');
            string o = run.outCap.Text(ShownPath(run.box, run.outLog), maxOut);
            string e = run.errCap.Text(ShownPath(run.box, run.errLog), maxOut);
            if (o.Length > 0)
                sb.Append("--- stdout ---\n").Append(o).Append(o.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n");
            if (e.Length > 0)
                sb.Append("--- stderr ---\n").Append(e).Append(e.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n");
            if (o.Length == 0 && e.Length == 0)
                sb.Append("(no output)\n");
            return sb.ToString();
        }
    }

    internal sealed partial class BridgeApplicationContext
    {
        private readonly ConcurrentDictionary<string, PcRun> _pcRuns = new ConcurrentDictionary<string, PcRun>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _pcGuardTainted = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private DateTime _pcSandboxCheckedUtc = DateTime.MinValue;
        private bool _pcSandboxPresent;
        private readonly ConcurrentDictionary<string, string> _pcSandboxUnsafe = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private DateTime _pcLogsSweptUtc = DateTime.MinValue;

        private bool PcOwnerEnabled
        {
            get
            {
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                    return false;
#endif
                return D.pcTools;
            }
        }

        private string PcSandboxName
        {
            get { return Regex.IsMatch(D.pcSandbox ?? "", @"\A[A-Za-z0-9._-]{1,64}\z") ? D.pcSandbox : "aiwb-family"; }
        }

        // Family sandboxes from devices.pcFamily (env -> Linux user), valid entries only.
        internal List<PcBox> PcFamilyBoxes()
        {
            List<PcBox> list = new List<PcBox>();
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return list;
#endif
            if (!D.pcFamilyTools || D.pcFamily == null)
                return list;
            string owner = OwnerPushEnv();
            foreach (KeyValuePair<string, string> kv in D.pcFamily)
            {
                if (!IsValidEnvironment(kv.Key) || String.Equals(kv.Key, owner, StringComparison.Ordinal) || !PcTools.ValidLinuxUser(kv.Value))
                    continue;
                bool dup = false;
                foreach (PcBox o in list)
                    if (o.user == kv.Value) dup = true;
                if (dup)
                    continue;           // two environments never share one Linux user (one home)
                PcBox b = new PcBox();
                b.env = kv.Key;
                b.user = kv.Value;
                b.distro = PcSandboxName;
                list.Add(b);
            }
            return list;
        }

        private string PcFamilyDeviceId(PcBox b)
        {
            return RelayHomeId + "-" + b.user;
        }

        // The sandbox distro is registered (wsl -l -q, checked at most every 5 min; never starts or stops a distro).
        private bool PcSandboxPresent()
        {
            if ((DateTime.UtcNow - _pcSandboxCheckedUtc).TotalMinutes < 5)
                return _pcSandboxPresent;
            bool present = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("wsl.exe", "-l -q");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.Unicode;      // wsl.exe's own messages are UTF-16
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (p.WaitForExit(15000))
                    {
                        o.Wait(2000);
                        foreach (string l in (o.IsCompleted ? o.Result : "").Split('\n'))
                            if (String.Equals(l.Trim().Trim('\0'), PcSandboxName, StringComparison.OrdinalIgnoreCase))
                                present = true;
                    }
                    else
                        try { p.Kill(); } catch { }
                }
            }
            catch (Exception)
            {
                present = false;
            }
            if (present != _pcSandboxPresent || _pcSandboxCheckedUtc == DateTime.MinValue)
                Log("PC tools: family sandbox '" + PcSandboxName + "' " + (present ? "found" : "not installed") + ".");
            _pcSandboxPresent = present;
            _pcSandboxCheckedUtc = DateTime.UtcNow;
            return present;
        }

        // The sandbox must not see the Windows drives nor run Windows programs (interop); checked as the member's user.
        private string PcSandboxProblem(PcBox box)
        {
            string cached;
            if (_pcSandboxUnsafe.TryGetValue(box.user, out cached) && cached.StartsWith("ok|", StringComparison.Ordinal))
            {
                DateTime at;
                if (DateTime.TryParse(cached.Substring(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) && (DateTime.UtcNow - at).TotalMinutes < 10)
                    return null;
            }
            string probe = "id -un; [ -d " + PcTools.ShQuote(box.Home) + " ] && echo HOME_OK; " +
                           "for m in /mnt/[a-zA-Z]; do [ -e \"$m/Windows\" ] && echo WINDRIVE; done; " +
                           "grep -qs '^enabled' /proc/sys/fs/binfmt_misc/WSLInterop && echo INTEROP; " +
                           "grep -qs '^enabled' /proc/sys/fs/binfmt_misc/WSLInterop-late && echo INTEROP; " +
                           "ls /run/WSL/*interop* >/dev/null 2>&1 && echo INTEROP; [ -n \"$WSL_INTEROP\" ] && echo INTEROP; " +
                           "grep -Eqs ' (9p|drvfs|virtiofs) ' /proc/mounts && echo WINDRIVE; [ \"$(id -u)\" = 0 ] && echo ROOT; " +
                           "for h in /home/*; do [ \"$h\" != " + PcTools.ShQuote(box.Home) + " ] && [ -r \"$h\" ] && echo OPENHOME; done; true";
            int exit;
            string stderr;
            byte[] o = PcFamilyExec(box, probe, null, 60, out exit, out stderr);
            string text = o == null ? "" : Encoding.UTF8.GetString(o);
            string[] lines = text.Replace("\r", "").Split('\n');
            string problem = null;
            if (o == null || lines.Length == 0 || lines[0].Trim() != box.user)
                problem = "The sandbox did not answer as user " + box.user + " in '" + box.distro + "' (missing user, or busy - try again in a minute). " + PushCut(stderr ?? "", 200);
            else if (text.IndexOf("HOME_OK", StringComparison.Ordinal) < 0)
                problem = box.Home + " does not exist in the sandbox.";
            else if (text.IndexOf("WINDRIVE", StringComparison.Ordinal) >= 0)
                problem = "The sandbox sees the Windows drives (/mnt/c): automount must be off in its wsl.conf.";
            else if (text.IndexOf("ROOT", StringComparison.Ordinal) >= 0)
                problem = "The sandbox user runs as root.";
            else if (text.IndexOf("OPENHOME", StringComparison.Ordinal) >= 0)
                problem = "Another home folder in the sandbox is readable by " + box.user + ".";
            else if (text.IndexOf("INTEROP", StringComparison.Ordinal) >= 0)
                problem = "The sandbox can start Windows programs: interop must be off in its wsl.conf.";
            if (problem == null)
                _pcSandboxUnsafe[box.user] = "ok|" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            else
            {
                string ignored;
                _pcSandboxUnsafe.TryRemove(box.user, out ignored);
                Log("PC tools: sandbox of " + box.env + " refused: " + problem);
            }
            return problem;
        }

        // Tools of h-<host> (owner) and of the family devices, for relay_devices_put.
        private void AddPcRelayDevices(List<object> list, HashSet<string> shorts, Dictionary<string, object> ownerHome)
        {
            if (ownerHome != null && PcOwnerEnabled)
            {
                List<object> tools = new List<object>((object[])ownerHome["tools"]);
                tools.AddRange(PcTools.Schemas(null));
                ownerHome["tools"] = tools.ToArray();
            }
            List<PcBox> boxes = PcFamilyBoxes();
            if (boxes.Count == 0 || !PcSandboxPresent())
                return;
            foreach (PcBox b in boxes)
            {
                string id = PcFamilyDeviceId(b);
                if (id.Length > 40 || !shorts.Add(b.env + "/" + RelayHomeShort))
                    continue;
                Dictionary<string, object> h = new Dictionary<string, object>();
                h["deviceId"] = id;
                h["environmentId"] = b.env;
                h["short"] = RelayHomeShort;
                h["name"] = Environment.MachineName + " (sandbox " + b.user + ")";
                h["kind"] = "home";
                h["online"] = true;
                h["tools"] = PcTools.Schemas(b);
                list.Add(h);
            }
        }

        // Quota of each family member in the sandbox (devices.pcFamilyQuota, bounded).
        private PcQuotaSettings PcQuota
        {
            get
            {
                PcQuotaSettings q = D.pcFamilyQuota ?? new PcQuotaSettings();
                PcQuotaSettings r = new PcQuotaSettings();
                r.maxBackground = Math.Max(0, Math.Min(PcTools.MaxBackgroundPerEnv, q.maxBackground));
                r.maxTimeoutSec = Math.Max(10, Math.Min(PcTools.MaxBackgroundSec, q.maxTimeoutSec));
                r.maxCallsPerJob = Math.Max(1, q.maxCallsPerJob);
                return r;
            }
        }

        private Dictionary<string, object> _pcCard;
        private DateTime _pcCardUtc = DateTime.MinValue;

        // Card of the sandbox (OS, compilers and interpreters, CPU, RAM), probed as a member's user at most hourly.
        private Dictionary<string, object> PcSandboxCard(PcBox probeAs)
        {
            if (_pcCard != null && (DateTime.UtcNow - _pcCardUtc).TotalHours < 1)
                return _pcCard;
            Dictionary<string, object> card = new Dictionary<string, object>();
            card["os"] = "Linux (WSL " + probeAs.distro + ")";
            string probe = ". /etc/os-release 2>/dev/null; echo \"OS:$PRETTY_NAME\"; echo \"CPU:$(nproc)\"; " +
                           "echo \"RAM:$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)\"; " +
                           "for t in gcc g++ clang make cmake python3 pip3 node npm java javac go rustc cargo git sqlite3 gdb valgrind; do " +
                           "command -v $t >/dev/null 2>&1 && echo \"T:$t\"; done; true";
            int exit;
            string err;
            byte[] o = PcFamilyExec(probeAs, probe, null, 60, out exit, out err);
            List<string> tools = new List<string>();
            if (o != null)
                foreach (string l in Encoding.UTF8.GetString(o).Replace("\r", "").Split('\n'))
                {
                    if (l.StartsWith("OS:", StringComparison.Ordinal) && l.Length > 3) card["os"] = l.Substring(3);
                    else if (l.StartsWith("CPU:", StringComparison.Ordinal)) card["cpus"] = PcTools.Int(Obj1("v", l.Substring(4)), "v", 0, 0, 1024);
                    else if (l.StartsWith("RAM:", StringComparison.Ordinal)) card["ramMb"] = PcTools.Int(Obj1("v", l.Substring(4)), "v", 0, 0, 1 << 24);
                    else if (l.StartsWith("T:", StringComparison.Ordinal)) tools.Add(l.Substring(2));
                }
            card["tools"] = tools.ToArray();
            card["files"] = "only /home/<user>; no Windows drives, no interop, no Drive, no keyring";
            card["limits"] = "pc_* only; a run up to " + PcQuota.maxTimeoutSec + " s, " + PcQuota.maxBackground + " background runs, " + PcQuota.maxCallsPerJob + " calls per job";
            if (o != null)
            {
                _pcCard = card;
                _pcCardUtc = DateTime.UtcNow;
            }
            return card;
        }

        private static Dictionary<string, object> Obj1(string k, string number)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            long v;
            if (Int64.TryParse((number ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                d[k] = v;
            return d;
        }

        private bool _pcSandboxPutWarned;

        // schema 26 (sandbox-lib.php): this Bridge as a sandbox provider - its family sandbox with the card, who may use
        // it, the quota of each and the seat (relay device) of each; the server picks per environment (own sandbox
        // first, then a family member's). An empty list clears this host's rows.
        private void PcPublishSandboxes()
        {
            List<PcBox> boxes = PcFamilyBoxes();
            List<object> list = new List<object>();
            if (boxes.Count > 0)
            {
                bool present = PcSandboxPresent();
                Dictionary<string, object> seats = new Dictionary<string, object>();
                Dictionary<string, object> quotas = new Dictionary<string, object>();
                List<string> allowed = new List<string>();
                foreach (PcBox b in boxes)
                {
                    string id = PcFamilyDeviceId(b);
                    if (id.Length > 40)
                        continue;
                    allowed.Add(b.env);
                    seats[b.env] = id;
                    PcQuotaSettings q = PcQuota;
                    Dictionary<string, object> qo = new Dictionary<string, object>();
                    qo["user"] = b.user;
                    qo["maxBackground"] = q.maxBackground;
                    qo["maxTimeoutSec"] = q.maxTimeoutSec;
                    qo["maxCallsPerJob"] = q.maxCallsPerJob;
                    quotas[b.env] = qo;
                }
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["id"] = _config.hostId + "/" + PcSandboxName;
                s["deviceId"] = RelayHomeId;
                s["ownerEnv"] = OwnerPushEnv();
                s["kind"] = "wsl";
                s["online"] = present;
                s["card"] = present && boxes.Count > 0 ? PcSandboxCard(boxes[0]) : new Dictionary<string, object>();
                s["allowed"] = allowed.ToArray();
                s["quotas"] = quotas;
                s["seats"] = seats;
                if (allowed.Count > 0 && IsValidEnvironment(OwnerPushEnv()))
                    list.Add(s);
            }
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["host"] = _config.hostId;
            body["sandboxes"] = list;
            try
            {
                DbqCall("sandbox_put", body, true, 15000);
                _pcSandboxPutWarned = false;
            }
            catch (Exception ex)
            {
                if (!_pcSandboxPutWarned)
                    Log("PC tools: sandbox not published (" + ex.Message + "); a server before schema 26 does not know sandbox_put.");
                _pcSandboxPutWarned = true;
            }
        }

        // The family sandbox a relay device id stands for, or null.
        private PcBox PcBoxOfDevice(string deviceId)
        {
            foreach (PcBox b in PcFamilyBoxes())
                if (String.Equals(PcFamilyDeviceId(b), deviceId, StringComparison.Ordinal))
                    return b;
            return null;
        }

        // ------------------------------------------------------------ dispatch

        private Dictionary<string, object> PcCall(string key, string env, string chatId, string tool, string argsJson, PcBox box,
                                                  List<string> deny, bool tainted)
        {
            if (box == null && !PcOwnerEnabled)
                return PcTools.Fail("unknown_tool", "PC tools are turned off on this Bridge (devices.pcTools).");
            if (!PcTools.IsPcTool(tool))
                return PcTools.Fail("unknown_tool", "Unknown tool " + tool + ".");
            if (JobDeviceState.DenySet(deny).Contains(tool))
                return PcTools.Fail("tool_off", "This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.");
            Dictionary<string, object> args = String.IsNullOrEmpty(argsJson) ? new Dictionary<string, object>()
                : JsonUtil.DeserializeObject(argsJson) as Dictionary<string, object>;
            if (args == null)
                return PcTools.Fail("bad_args", "Arguments must be a JSON object.");

            JobDeviceState js = RelayState(key, env, chatId);
            if (!String.Equals(js.environmentId, env, StringComparison.Ordinal))
                return PcTools.Fail("not_allowed", "This job runs in another environment.");
            if (tainted)
                js.tainted = true;
            int n = Interlocked.Increment(ref js.calls);
            int limit = box == null ? D.maxCallsPerRun : Math.Min(D.maxCallsPerRun, PcQuota.maxCallsPerJob);
            if (n > limit)
                return PcTools.Fail("call_limit", "This Run reached its limit of " + limit + " " + (box == null ? "device" : "sandbox") + " calls.");

            if (box != null)
            {
                if (!PcSandboxPresent())
                    return PcTools.Fail("sandbox_missing", "The family sandbox ('" + box.distro + "' in WSL) is not installed on the home PC yet; ask the owner of the PC to set it up.");
                string problem = PcSandboxProblem(box);
                if (problem != null)
                    return PcTools.Fail("sandbox_unsafe", "The family sandbox is not usable now: " + problem + " Ask the owner of the PC to fix it.");
            }
            PcSweepLogs();
            try
            {
                switch (tool)
                {
                    case "pc_shell": return PcShell(key, env, chatId, args, box, js);
                    case "pc_shell_status": return PcShellStatus(env, chatId, key, args);
                    case "pc_shell_kill": return PcShellKill(env, chatId, key, args);
                    case "pc_read": return PcRead(key, chatId, args, box, js);
                    case "pc_write": return PcWrite(key, chatId, args, box, js, false);
                    case "pc_upload": return PcWrite(key, chatId, args, box, js, true);
                    case "pc_edit": return PcEdit(key, chatId, args, box, js);
                    case "pc_list": return PcList(key, chatId, args, box, js);
                    case "pc_search": return PcSearch(key, chatId, args, box, js);
                    case "pc_download": return PcDownload(key, chatId, args, box, js);
                }
            }
            catch (Exception ex)
            {
                return PcTools.Fail("pc_error", ex.GetType().Name + ": " + ex.Message);
            }
            return PcTools.Fail("unknown_tool", "Unknown tool " + tool + ".");
        }

        private string PcRoot
        {
            get { return Path.Combine(Path.Combine(_localStateRoot, "relay"), "pc"); }
        }

        // Log files older than 7 days go (checked hourly).
        private void PcSweepLogs()
        {
            if ((DateTime.UtcNow - _pcLogsSweptUtc).TotalHours < 1)
                return;
            _pcLogsSweptUtc = DateTime.UtcNow;
            try
            {
                string logs = Path.Combine(PcRoot, "logs");
                if (!Directory.Exists(logs))
                    return;
                foreach (string dir in Directory.GetDirectories(logs))
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)).TotalDays > 7)
                        try { Directory.Delete(dir, true); } catch { }
                foreach (KeyValuePair<string, PcRun> kv in _pcRuns)
                    if (kv.Value.done && (DateTime.UtcNow - kv.Value.endedUtc).TotalHours > 24)
                    {
                        PcRun ignored;
                        _pcRuns.TryRemove(kv.Key, out ignored);
                    }
            }
            catch { }
        }

        // ------------------------------------------------------------ read-guard (owner)

        // PreToolUse check of read-guard for the same call of a Home job; null = go on, else the refusal.
        private Dictionary<string, object> PcGuardPre(string key, string chatId, JobDeviceState js, string toolName, Dictionary<string, object> toolInput, string cwd)
        {
            string script = ReadGuard.ScriptPath;
            if (script == null)
                return null;
            // a site job that read suspicious text (relay "tainted") taints the chat for read-guard too, once per job
            if (js.tainted && !String.IsNullOrEmpty(chatId) && _pcGuardTainted.TryAdd(key, true))
                PcGuardRun(script, new string[] { "-c",
                    "import sys; sys.path.insert(0, sys.argv[1]); import read_guard as rg; " +
                    "rg.mark_tainted(rg.scope_key(), 'site job (relay)', rg.Verdict(True, 9, ['site_job_tainted']))",
                    Path.GetDirectoryName(script) }, null, key, chatId, cwd);
            Dictionary<string, object> ev = new Dictionary<string, object>();
            ev["hook_event_name"] = "PreToolUse";
            ev["tool_name"] = toolName;
            ev["tool_input"] = toolInput;
            ev["cwd"] = cwd ?? "";
            string o = PcGuardRun(script, new string[] { script, "hook", "claude-pre" }, JsonUtil.Serialize(ev), key, chatId, cwd);
            if (o == null)          // did not run or timed out: refuse rather than pass unchecked (review B-01)
                return PcTools.Fail("guard_unavailable", "read-guard could not check this call on the home PC; try again in a minute.");
            Dictionary<string, object> r = null;
            try { r = String.IsNullOrWhiteSpace(o) ? null : JsonUtil.DeserializeObject(o.Trim()) as Dictionary<string, object>; }
            catch (Exception) { return PcTools.Fail("guard_unavailable", "read-guard gave an unreadable answer; the call was not run."); }
            Dictionary<string, object> h = r != null && r.ContainsKey("hookSpecificOutput") ? r["hookSpecificOutput"] as Dictionary<string, object> : null;
            if (h == null || DeviceWire.S(h, "permissionDecision") != "deny")
                return null;
            return PcTools.Fail("owner_confirm_required", DeviceWire.S(h, "permissionDecisionReason") ?? "The owner has to confirm this action.");
        }

        // PostToolUse of read-guard: wraps suspicious text read from outside; returns the text to give the agent.
        private string PcGuardPost(string key, string chatId, string toolName, Dictionary<string, object> toolInput, string output, string cwd)
        {
            string script = ReadGuard.ScriptPath;
            if (script == null || String.IsNullOrEmpty(output))
                return output;
            Dictionary<string, object> resp = new Dictionary<string, object>();
            resp["stdout"] = output;
            Dictionary<string, object> ev = new Dictionary<string, object>();
            ev["hook_event_name"] = "PostToolUse";
            ev["tool_name"] = toolName;
            ev["tool_input"] = toolInput;
            ev["tool_response"] = resp;
            ev["cwd"] = cwd ?? "";
            string o = PcGuardRun(script, new string[] { script, "hook", "claude-post" }, JsonUtil.Serialize(ev), key, chatId, cwd);
            Dictionary<string, object> r = String.IsNullOrWhiteSpace(o) ? null : JsonUtil.DeserializeObject(o.Trim()) as Dictionary<string, object>;
            Dictionary<string, object> h = r != null && r.ContainsKey("hookSpecificOutput") ? r["hookSpecificOutput"] as Dictionary<string, object> : null;
            if (h == null)
                return output;
            Dictionary<string, object> upd = h.ContainsKey("updatedToolOutput") ? h["updatedToolOutput"] as Dictionary<string, object> : null;
            string wrapped = upd != null ? DeviceWire.S(upd, "stdout") : null;
            return (DeviceWire.S(h, "additionalContext") ?? "") + "\n" + (wrapped ?? output);
        }

        private string PcGuardRun(string script, string[] argv, string stdin, string key, string chatId, string cwd)
        {
            string inFile = null;
            try
            {
                StringBuilder a = new StringBuilder();
                foreach (string s in argv)
                    a.Append(a.Length > 0 ? " " : "").Append(QuoteWinArg(s));
                string python = PcPython();
                if (python == null)
                    return null;
#if NET8_0_OR_GREATER
                python = BridgePlatform.Current.Executables.Python;
#endif
                inFile = PcExec.StdinFile(Path.Combine(PcRoot, "in"), new UTF8Encoding(false).GetBytes(stdin ?? ""));
                ProcessStartInfo psi = PcExec.WithStdin(python, a.ToString(), inFile);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["AIWB_JOB_ID"] = key;
                psi.EnvironmentVariables["AIWB_CHAT_ID"] = chatId ?? "";
                // never the agent's cwd: cmd.exe would run a python.cmd planted there (review B-01)
                Directory.CreateDirectory(PcRoot);
                psi.WorkingDirectory = PcRoot;
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000))
                    {
                        KillProcessTreeFallback(p);
                        return null;
                    }
                    o.Wait(2000);
                    return o.IsCompleted ? o.Result : null;
                }
            }
            catch (Exception ex)
            {
                LogThrottled("pc-guard", "PC tools: read-guard check failed (" + ex.Message + ")");
                return null;
            }
            finally
            {
                PcExec.DeleteQuietly(inFile);
            }
        }

        // python.exe from PATH by absolute path (directories only, not the current one), cached.
        private static string _pcPython;
        private static string PcPython()
        {
            if (_pcPython != null && File.Exists(_pcPython))
                return _pcPython;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0 || !Path.IsPathRooted(d) || d.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                try
                {
                    string p = Path.Combine(d, "python.exe");
                    if (File.Exists(p))
                        return _pcPython = p;
                }
                catch { }
            }
            return null;
        }

        // CommandLineToArgvW quoting.
        internal static string QuoteWinArg(string s)
        {
            if (!String.IsNullOrEmpty(s) && s.IndexOfAny(new char[] { ' ', '\t', '"', '\n' }) < 0)
                return s;
            StringBuilder sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s ?? "")
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); bs = 0; sb.Append('"'); continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------ pc_shell

        private static string PcGitBash()
        {
            foreach (string p in new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\bin\bash.exe"),
                @"C:\Program Files\Git\bin\bash.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe") })
                if (File.Exists(p))
                    return p;
            return null;
        }

        private Dictionary<string, object> PcShell(string key, string env, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            string command = PcTools.Str(args, "command");
            if (String.IsNullOrEmpty(command))
                return PcTools.Fail("bad_args", "'command' is required.");
            string shell = (PcTools.Str(args, "shell") ?? "powershell").ToLowerInvariant();
            if (box == null && shell != "powershell" && shell != "bash")
                return PcTools.Fail("bad_args", "'shell' must be powershell or bash.");
            bool background = PcTools.Bool(args, "background");
            int maxSec = background ? PcTools.MaxBackgroundSec : PcTools.MaxForegroundSec;
            if (box != null)
                maxSec = Math.Min(maxSec, PcQuota.maxTimeoutSec);        // the sandbox quota of a family member
            int timeout = (int)PcTools.Int(args, "timeout_sec", Math.Min(background ? 3600 : 120, maxSec), 1, maxSec);
            int maxOut = (int)PcTools.Int(args, "max_output", PcTools.DefaultOutputChars, 200, PcTools.MaxOutputChars);
            string refusal;
            Dictionary<string, string> vars = PcTools.EnvVars(args, out refusal);
            if (vars == null)
                return PcTools.Fail("bad_args", refusal);
            string stdin = PcTools.Str(args, "stdin");

            string cwd = PcTools.Str(args, "cwd");
            string id = "pc-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string logDir = Path.Combine(Path.Combine(PcRoot, "logs"), day);
            Directory.CreateDirectory(logDir);
            ProcessStartInfo psi;
            string stdinFile = stdin == null ? null : Path.Combine(logDir, id + ".in");
            if (stdinFile != null)
                File.WriteAllBytes(stdinFile, new UTF8Encoding(false).GetBytes(stdin));
            if (box == null)
            {
                if (String.IsNullOrEmpty(cwd))
                {
                    cwd = Path.Combine(Path.Combine(Path.Combine(_localStateRoot, "relay"), env), "work");
                    Directory.CreateDirectory(cwd);
                }
                else
                {
                    cwd = PcTools.OwnerPath(cwd, out refusal);
                    if (cwd == null)
                        return PcTools.Fail("bad_args", refusal);
                    if (!Directory.Exists(cwd))
                        return PcTools.Fail("not_found", "cwd " + cwd + " does not exist.");
                }
                // read-guard sees the command as the Bash/PowerShell tool of a Home job would send it (variables included)
                StringBuilder seen = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in vars)
                    seen.Append(shell == "bash" ? kv.Key + "=" + PcTools.ShQuote(kv.Value) + " " : "$env:" + kv.Key + "=" + PcTools.ShQuote(kv.Value) + "; ");
                seen.Append(command);
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["command"] = seen.ToString();
                ti["description"] = "pc_shell from a site job";
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, shell == "bash" ? "Bash" : "PowerShell", ti, cwd);
                if (held != null)
                    return held;

                string scriptPath = Path.Combine(logDir, id + (shell == "bash" ? ".sh" : ".ps1"));
                if (shell == "bash")
                {
                    string bash = PcGitBash();
                    if (bash == null)
                        return PcTools.Fail("unavailable", "Git Bash is not installed on the home PC; use shell powershell.");
                    File.WriteAllText(scriptPath, command.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
                    psi = PcExec.WithStdin(bash, "--noprofile --norc \"" + scriptPath.Replace('\\', '/') + "\"", stdinFile);
                    psi.EnvironmentVariables["CHERE_INVOKING"] = "1";
                }
                else
                {
                    string pre = "$ProgressPreference = 'SilentlyContinue'\r\n" +
                                 "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }\r\n";
                    string post = "\r\nif ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }\r\n";
                    File.WriteAllText(scriptPath, pre + command + post, new UTF8Encoding(true));    // BOM: PowerShell 5.1 reads UTF-8
                    psi = PcExec.WithStdin(PcExec.SystemExe(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"", stdinFile);
                }
                psi.WorkingDirectory = cwd;
                psi.EnvironmentVariables["AIWB_JOB_ID"] = key;
                psi.EnvironmentVariables["AIWB_CHAT_ID"] = chatId ?? "";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                foreach (KeyValuePair<string, string> kv in vars)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }
            else
            {
                string script = PcTools.FamilyShellScript(box, id, cwd, vars, command);
                if (script.Length > PcTools.MaxFamilyScriptChars)
                    return PcTools.Fail("script_too_long", "The script is longer than " + PcTools.MaxFamilyScriptChars + " characters; write it to a file with pc_write and run that file.");
                psi = PcExec.WithStdin(PcExec.SystemExe("wsl.exe"), PcTools.WslArguments(box, script), stdinFile);
            }

            PcRun run = new PcRun();
            run.id = id;
            run.env = env;
            run.chatId = chatId ?? "";
            run.jobKey = key;
            run.box = box;
            run.command = command.Length > 300 ? command.Substring(0, 300) + "..." : command;
            run.outLog = Path.Combine(logDir, id + ".out.log");
            run.errLog = Path.Combine(logDir, id + ".err.log");
            run.timeoutSec = timeout;
            run.startedUtc = DateTime.UtcNow;

            // quota: counted and reserved under one lock, foreground runs too (review B-04)
            string busy = PcReserve(env, background, box);
            if (busy != null)
            {
                PcExec.DeleteQuietly(stdinFile);
                return PcTools.Fail("too_many", busy);
            }
            Process p;
            lock (_relayCancelLock)
            {
                if (_relayEnded.ContainsKey(key))
                {
                    PcRelease(env, background);
                    return PcTools.Fail("cancelled", "The site job has ended.");
                }
                run.stdinFile = stdinFile;
                PcExec.Start(run, psi, maxOut);
                p = run.process;
                if (!background)
                    _relayProcs[p] = key;     // Stop of the site job kills a foreground run (RelayCancelJob)
            }
            Log("PC tools: " + (box == null ? "owner" : box.env) + " pc_shell " + id + (background ? " (background)" : "") + " started.");

            if (background)
            {
                _pcRuns[id] = run;
                Thread w = new Thread(delegate() { try { PcWait(run); } finally { PcRelease(env, true); } });
                w.IsBackground = true;
                w.Name = "pc-run";
                w.Start();
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["id"] = id;
                s["state"] = "running";
                s["timeoutSec"] = timeout;
                s["stdoutLog"] = PcShownPath(box, run.outLog);
                s["stderrLog"] = PcShownPath(box, run.errLog);
                return PcTools.Ok("Started in the background: " + id + " (timeout " + timeout + " s). Follow it with pc_shell_status, stop it with pc_shell_kill.\n" + PcTools.Json(s));
            }
            try
            {
                PcWait(run);
            }
            finally
            {
                string ignored;
                _relayProcs.TryRemove(p, out ignored);
                PcRelease(env, false);
            }
            if (_relayEnded.ContainsKey(key))
                return PcTools.Fail("cancelled", "The site job has ended; the run was stopped.");
            string text = PcRunText(run, maxOut);
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["command"] = command;
                text = PcGuardPost(key, chatId, shell == "bash" ? "Bash" : "PowerShell", ti, text, cwd);
            }
            return PcTools.Ok(text);
        }

        private void PcWait(PcRun run)
        {
            PcExec.Wait(run, PcFamilyKill);
        }

        private void PcKillRun(PcRun run)
        {
            PcExec.Kill(run, PcFamilyKill);
        }

        // The run's tree by its pid file; when it is the member's last run, every process of that user (setsid and
        // double-forked leftovers would outlive the timeout otherwise - review B-04).
        private void PcFamilyKill(PcRun run)
        {
            int exit;
            string err;
            bool last;
            lock (_pcQuotaLock)
            {
                int n;
                last = !_pcActive.TryGetValue(run.env, out n) || n <= 1;
            }
            PcFamilyExec(run.box, PcTools.FamilyKillScript(run.box, run.id) + (last ? "kill -KILL -1 2>/dev/null; true\n" : ""), null, 30, out exit, out err);
        }

        private readonly object _pcQuotaLock = new object();
        private readonly Dictionary<string, int> _pcActive = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pcActiveBg = new Dictionary<string, int>(StringComparer.Ordinal);

        // Reserves a run slot of the environment; null = granted, else why not.
        private string PcReserve(string env, bool background, PcBox box)
        {
            int maxBg = box == null ? PcTools.MaxBackgroundPerEnv : PcQuota.maxBackground;
            int maxAll = box == null ? Int32.MaxValue : PcQuota.maxBackground + 2;
            lock (_pcQuotaLock)
            {
                int all, bg;
                _pcActive.TryGetValue(env, out all);
                _pcActiveBg.TryGetValue(env, out bg);
                if (background && bg >= maxBg)
                    return "Already " + bg + " background runs of this environment; wait for them or stop one (pc_shell_kill).";
                if (all >= maxAll)
                    return "Already " + all + " runs of this environment at once (limit " + maxAll + "); wait for them.";
                _pcActive[env] = all + 1;
                if (background)
                    _pcActiveBg[env] = bg + 1;
                return null;
            }
        }

        private void PcRelease(string env, bool background)
        {
            lock (_pcQuotaLock)
            {
                int n;
                if (_pcActive.TryGetValue(env, out n))
                    _pcActive[env] = Math.Max(0, n - 1);
                if (background && _pcActiveBg.TryGetValue(env, out n))
                    _pcActiveBg[env] = Math.Max(0, n - 1);
            }
        }

        private static string PcShownPath(PcBox box, string path)
        {
            return PcExec.ShownPath(box, path);
        }

        private static string PcRunText(PcRun run, int maxOut)
        {
            return PcExec.Text(run, maxOut);
        }

        internal static void PcKillTree(Process p)
        {
            KillProcessTreeFallback(p);
        }

        // A run belongs to the caller when it is of the same environment and the same chat (or the same job).
        private static bool PcOwns(PcRun r, string env, string chatId, string key)
        {
            if (r == null || r.env != env)
                return false;
            if (!String.IsNullOrEmpty(chatId) && !String.IsNullOrEmpty(r.chatId))
                return r.chatId == chatId;
            return r.jobKey == key;
        }

        private Dictionary<string, object> PcShellStatus(string env, string chatId, string key, Dictionary<string, object> args)
        {
            string id = PcTools.Str(args, "id");
            int tail = (int)PcTools.Int(args, "tail", 4000, 0, PcTools.MaxOutputChars);
            if (String.IsNullOrEmpty(id))
            {
                List<object> list = new List<object>();
                foreach (PcRun r in _pcRuns.Values)
                    if (PcOwns(r, env, chatId, key))
                    {
                        Dictionary<string, object> s = new Dictionary<string, object>();
                        s["id"] = r.id;
                        s["state"] = r.done ? "exited" : "running";
                        if (r.done) s["exitCode"] = r.exitCode;
                        s["startedUtc"] = r.startedUtc.ToString("o", CultureInfo.InvariantCulture);
                        s["command"] = r.command;
                        list.Add(s);
                    }
                Dictionary<string, object> all = new Dictionary<string, object>();
                all["runs"] = list;
                return PcTools.Ok(JsonUtil.Serialize(all));
            }
            PcRun run;
            if (!_pcRuns.TryGetValue(id, out run) || !PcOwns(run, env, chatId, key))
                return PcTools.Fail("not_found", "No background run " + id + " of this chat.");
            StringBuilder sb = new StringBuilder();
            sb.Append(run.done ? "exited" : "running").Append(" · ").Append(run.id).Append('\n');
            sb.Append(PcRunText(run, tail).Replace("exit code (running)", "exit code -"));
            if (run.box == null)
                sb.Append("logs: ").Append(run.outLog).Append(" , ").Append(run.errLog).Append('\n');
            return PcTools.Ok(sb.ToString());
        }

        private Dictionary<string, object> PcShellKill(string env, string chatId, string key, Dictionary<string, object> args)
        {
            string id = PcTools.Str(args, "id");
            PcRun run;
            if (String.IsNullOrEmpty(id) || !_pcRuns.TryGetValue(id, out run) || !PcOwns(run, env, chatId, key))
                return PcTools.Fail("not_found", "No background run " + id + " of this chat.");
            if (run.done)
                return PcTools.Ok("Run " + id + " had already exited with code " + run.exitCode + ".");
            run.killReason = "pc_shell_kill";
            PcKillRun(run);
            run.exited.WaitOne(20000);
            return PcTools.Ok("Run " + id + " stopped" + (run.done ? " (exit code " + run.exitCode + ")." : "; it is still exiting."));
        }

        // ------------------------------------------------------------ file primitives (owner: .NET, family: inside WSL)

        // Runs a short script in the sandbox as the member's user; returns stdout bytes (null when it could not run).
        private byte[] PcFamilyExec(PcBox box, string script, byte[] stdin, int timeoutSec, out int exitCode, out string stderr)
        {
            string inFile = null;
            try
            {
                inFile = PcExec.StdinFile(Path.Combine(PcRoot, "in"), stdin);
                return PcExec.Capture(PcExec.WithStdin(PcExec.SystemExe("wsl.exe"), PcTools.WslArguments(box, script), inFile), timeoutSec, out exitCode, out stderr);
            }
            finally
            {
                PcExec.DeleteQuietly(inFile);
            }
        }

        // Resolved path inside the home (realpath on the Linux side), or a refusal.
        private string PcFamilyResolve(PcBox box, string path, out Dictionary<string, object> refusal)
        {
            refusal = null;
            if (String.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.IndexOf('\n') >= 0)
            {
                refusal = PcTools.Fail("bad_args", "'path' is required.");
                return null;
            }
            int exit;
            string err;
            byte[] o = PcFamilyExec(box, PcTools.FamilyPrelude(box) + "aiwb_in " + PcTools.ShQuote(path), null, 60, out exit, out err);
            string r = o == null ? null : Encoding.UTF8.GetString(o).TrimEnd('\n');
            if (o == null)
            {
                refusal = PcTools.Fail("sandbox_error", "The sandbox did not answer: " + PushCut(err, 300));
                return null;
            }
            if (exit != 0 || !PcTools.InHome(r, box.Home))
            {
                refusal = PcTools.Fail("outside_home", "Only files under " + box.Home + " are available.");
                return null;
            }
            return r;
        }

        // Whole file or a byte range; size = file size; null + refusal when it cannot be read.
        private byte[] PcReadBytes(PcBox box, string path, long offset, long length, out long size, out Dictionary<string, object> refusal)
        {
            size = 0;
            refusal = null;
            if (box == null)
            {
                if (!File.Exists(path))
                {
                    refusal = PcTools.Fail("not_found", (Directory.Exists(path) ? "That is a folder (use pc_list): " : "No such file: ") + path);
                    return null;
                }
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    size = fs.Length;
                    long n = Math.Max(0, Math.Min(length, size - offset));
                    byte[] buf = new byte[n];
                    if (n > 0)
                    {
                        fs.Seek(offset, SeekOrigin.Begin);
                        int got = 0;
                        while (got < n)
                        {
                            int k = fs.Read(buf, got, (int)(n - got));
                            if (k <= 0) break;
                            got += k;
                        }
                        if (got < n) Array.Resize(ref buf, got);
                    }
                    return buf;
                }
            }
            string q = PcTools.ShQuote(path);
            string script = "[ -f " + q + " ] || { [ -d " + q + " ] && echo 'That is a folder (use pc_list)' >&2 || echo 'No such file' >&2; exit 4; }\n" +
                            "stat -c %s -- " + q + "\n" +
                            "tail -c +" + (offset + 1).ToString(CultureInfo.InvariantCulture) + " -- " + q + " | head -c " + length.ToString(CultureInfo.InvariantCulture) + " | base64 -w0\n";
            int exit;
            string err;
            byte[] o = PcFamilyExec(box, script, null, 120, out exit, out err);
            if (o == null || exit != 0)
            {
                refusal = PcTools.Fail(exit == 4 ? "not_found" : "sandbox_error", (PushCut(err, 300) + " " + path).Trim());
                return null;
            }
            string text = Encoding.ASCII.GetString(o);
            int nl = text.IndexOf('\n');
            size = Int64.Parse(text.Substring(0, nl).Trim(), CultureInfo.InvariantCulture);
            return Convert.FromBase64String(text.Substring(nl + 1).Trim());
        }

        private Dictionary<string, object> PcWriteBytes(PcBox box, string path, byte[] data, bool append)
        {
            if (box == null)
            {
                string dir = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using (FileStream fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                    fs.Write(data, 0, data.Length);
                return null;
            }
            string q = PcTools.ShQuote(path);
            string script = "mkdir -p -- \"$(dirname -- " + q + ")\" && cat " + (append ? ">> " : "> ") + q;
            int exit;
            string err;
            byte[] o = PcFamilyExec(box, script, data, 300, out exit, out err);
            if (o == null || exit != 0)
                return PcTools.Fail("write_failed", (PushCut(err, 300) + " " + path).Trim());
            return null;
        }

        private string PcResolve(PcBox box, string path, out Dictionary<string, object> refusal)
        {
            refusal = null;
            if (box != null)
                return PcFamilyResolve(box, path, out refusal);
            string why;
            string p = PcTools.OwnerPath(path, out why);
            if (p == null)
                refusal = PcTools.Fail("bad_args", why);
            return p;
        }

        // ------------------------------------------------------------ pc_read / pc_write / pc_upload / pc_edit

        private Dictionary<string, object> PcRead(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            bool b64 = String.Equals(PcTools.Str(args, "encoding"), "base64", StringComparison.OrdinalIgnoreCase);
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Read", ti, null);
                if (held != null)
                    return held;
            }
            long size;
            if (b64)
            {
                long off = PcTools.Int(args, "offset", 0, 0, Int64.MaxValue);
                long len = PcTools.Int(args, "limit", PcTools.MaxReadBytes, 1, PcTools.MaxReadBytes);
                byte[] data = PcReadBytes(box, path, off, len, out size, out refusal);
                if (data == null)
                    return refusal;
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["path"] = path;
                r["size"] = size;
                r["offset"] = off;
                r["length"] = data.Length;
                r["eof"] = off + data.Length >= size;
                r["data_base64"] = Convert.ToBase64String(data);
                return PcTools.Ok(PcTools.Json(r));
            }
            byte[] all = PcReadBytes(box, path, 0, PcTools.MaxTextFileBytes, out size, out refusal);
            if (all == null)
                return refusal;
            if (size > PcTools.MaxTextFileBytes)
                return PcTools.Fail("too_large", "The file has " + size + " bytes; read it with pc_search, or as base64 in ranges.");
            if (Array.IndexOf(all, (byte)0) >= 0 && Array.IndexOf(all, (byte)0) < 8000)
                return PcTools.Fail("binary_file", "The file looks binary; read it with encoding base64.");
            string text = new UTF8Encoding(false).GetString(all).TrimStart('\uFEFF');
            long total;
            bool more;
            string body = PcTools.NumberLines(text, PcTools.Int(args, "offset", 1, 1, Int64.MaxValue), PcTools.Int(args, "limit", 2000, 1, 100000), out total, out more);
            string head = path + " · " + total + " lines · " + size + " bytes" + (more ? " · more lines follow (offset/limit)" : "") + "\n";
            string res = head + (body.Length == 0 ? "(no lines in this range)\n" : body);
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                res = PcGuardPost(key, chatId, "Read", ti, res, null);
            }
            return PcTools.Ok(res);
        }

        private Dictionary<string, object> PcWrite(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js, bool upload)
        {
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            string text = upload ? null : PcTools.Str(args, "content");
            string b64 = PcTools.Str(args, upload ? "data_base64" : "content_base64");
            if (text == null && b64 == null)
                return PcTools.Fail("bad_args", upload ? "'data_base64' is required (local_path is read by the site relay; this relay version does not support it)." : "'content' or 'content_base64' is required.");
            byte[] data;
            try
            {
                data = text != null ? new UTF8Encoding(false).GetBytes(text) : Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                return PcTools.Fail("bad_args", "Invalid base64.");
            }
            bool append = PcTools.Bool(args, "append");
            if (box == null)
            {
                if (Directory.Exists(path))
                    return PcTools.Fail("bad_args", path + " is a folder.");
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                ti["content"] = text != null ? (text.Length > 4000 ? text.Substring(0, 4000) : text) : "(binary " + data.Length + " bytes)";
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Write", ti, null);
                if (held != null)
                    return held;
            }
            Dictionary<string, object> fail = PcWriteBytes(box, path, data, append);
            if (fail != null)
                return fail;
            long size = box == null ? new FileInfo(path).Length : -1;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["path"] = path;
            r["written"] = data.Length;
            if (size >= 0) r["size"] = size;
            if (upload && !append)
                r["sha256"] = PcTools.Sha256Hex(data);
            Log("PC tools: " + (box == null ? "owner" : box.env) + (upload ? " pc_upload " : " pc_write ") + path + " (" + data.Length + " bytes)");
            if (!upload)
                return PcTools.Ok((append ? "Appended" : "Written") + ": " + PcTools.Json(r));
            // relay-mcp.php read the chunk from the site job's file (local_path) and says where the next one starts
            Dictionary<string, object> site = args.ContainsKey("site") ? args["site"] as Dictionary<string, object> : null;
            if (site != null)
                r["site"] = site;
            string more = site != null && !PcTools.Bool(site, "eof") ? "\nMore to send: call pc_upload again with the same path and local_path and offset=" + Convert.ToString(site.ContainsKey("next") ? site["next"] : 0, CultureInfo.InvariantCulture) + "." : "";
            return PcTools.Ok(PcTools.Json(r) + more);
        }

        private Dictionary<string, object> PcEdit(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            string oldS = PcTools.Str(args, "old_string");
            string newS = PcTools.Str(args, "new_string");
            if (oldS == null || newS == null)
                return PcTools.Fail("bad_args", "'old_string' and 'new_string' are required.");
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                ti["old_string"] = oldS.Length > 2000 ? oldS.Substring(0, 2000) : oldS;
                ti["new_string"] = newS.Length > 2000 ? newS.Substring(0, 2000) : newS;
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Edit", ti, null);
                if (held != null)
                    return held;
            }
            long size;
            byte[] all = PcReadBytes(box, path, 0, PcTools.MaxTextFileBytes, out size, out refusal);
            if (all == null)
                return refusal;
            if (size > PcTools.MaxTextFileBytes)
                return PcTools.Fail("too_large", "The file is too large to edit here.");
            bool bom = all.Length >= 3 && all[0] == 0xEF && all[1] == 0xBB && all[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(all, bom ? 3 : 0, all.Length - (bom ? 3 : 0));
            int count;
            string why;
            string edited = PcTools.Replace(text, oldS, newS, PcTools.Bool(args, "replace_all"), out count, out why);
            if (edited == null)
                return PcTools.Fail("edit_failed", why);
            byte[] body = new UTF8Encoding(false).GetBytes(edited);
            byte[] data = body;
            if (bom)
            {
                data = new byte[body.Length + 3];
                data[0] = 0xEF; data[1] = 0xBB; data[2] = 0xBF;
                Buffer.BlockCopy(body, 0, data, 3, body.Length);
            }
            Dictionary<string, object> fail = PcWriteBytes(box, path, data, false);
            if (fail != null)
                return fail;
            Log("PC tools: " + (box == null ? "owner" : box.env) + " pc_edit " + path + " (" + count + " replacement(s))");
            return PcTools.Ok("Edited " + path + ": " + count + " replacement(s).");
        }

        // ------------------------------------------------------------ pc_list / pc_search / pc_download

        private Dictionary<string, object> PcList(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Read", ti, null);
                if (held != null)
                    return held;
            }
            int depth = (int)PcTools.Int(args, "depth", 1, 1, 5);
            int max = (int)PcTools.Int(args, "max_entries", 500, 1, 5000);
            StringBuilder sb = new StringBuilder();
            int n = 0;
            if (box == null)
            {
                if (!Directory.Exists(path))
                    return PcTools.Fail("not_found", File.Exists(path) ? path + " is a file (use pc_read)." : "No such folder: " + path);
                Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
                q.Enqueue(new KeyValuePair<string, int>(path, 1));
                while (q.Count > 0 && n < max)
                {
                    KeyValuePair<string, int> cur = q.Dequeue();
                    IEnumerable<FileSystemInfo> items;
                    try { items = new DirectoryInfo(cur.Key).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
                    catch (Exception ex) { sb.Append("! ").Append(cur.Key).Append(": ").Append(ex.Message).Append('\n'); continue; }
                    foreach (FileSystemInfo i in items)
                    {
                        if (n >= max) break;
                        bool dir = (i.Attributes & FileAttributes.Directory) != 0;
                        bool link = (i.Attributes & FileAttributes.ReparsePoint) != 0;
                        sb.Append(dir ? "d " : "f ").Append((dir ? "-" : ((FileInfo)i).Length.ToString(CultureInfo.InvariantCulture)).PadLeft(12))
                          .Append(' ').Append(i.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(' ').Append(i.FullName)
                          .Append(dir ? "\\" : "").Append(link ? " (link)" : "").Append('\n');
                        n++;
                        if (dir && !link && cur.Value < depth)
                            q.Enqueue(new KeyValuePair<string, int>(i.FullName, cur.Value + 1));
                    }
                }
            }
            else
            {
                string script = "[ -d " + PcTools.ShQuote(path) + " ] || { echo 'No such folder' >&2; exit 4; }\n" +
                                "find " + PcTools.ShQuote(path) + " -mindepth 1 -maxdepth " + depth + " -printf '%y %12s %TY-%Tm-%Td %TH:%TM %p\\n' 2>/dev/null | sort -k5 | head -n " + max;
                int exit;
                string err;
                byte[] o = PcFamilyExec(box, script, null, 120, out exit, out err);
                if (o == null || exit != 0)
                    return PcTools.Fail(exit == 4 ? "not_found" : "sandbox_error", (PushCut(err, 300) + " " + path).Trim());
                string text = Encoding.UTF8.GetString(o);
                n = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                sb.Append(text);
            }
            if (n == 0)
                sb.Append("(empty)\n");
            else if (n >= max)
                sb.Append("... (first ").Append(max).Append(" entries; raise max_entries or list a subfolder)\n");
            return PcTools.Ok(sb.ToString());
        }

        private string PcRipgrep()
        {
            string conf = D.pcRipgrep;
            if (!String.IsNullOrEmpty(conf) && File.Exists(conf))
                return conf;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim().Trim('"'), "rg.exe");
                    if (dir.Trim().Length > 0 && File.Exists(p))
                        return p;
                }
                catch { }
            }
            return null;
        }

        private Dictionary<string, object> PcSearch(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            string pattern = PcTools.Str(args, "pattern");
            if (String.IsNullOrEmpty(pattern))
                return PcTools.Fail("bad_args", "'pattern' is required.");
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Read", ti, null);
                if (held != null)
                    return held;
            }
            string glob = PcTools.Str(args, "glob");
            bool icase = PcTools.Bool(args, "ignore_case");
            bool filesOnly = PcTools.Bool(args, "files_only");
            int max = (int)PcTools.Int(args, "max_results", 300, 1, 3000);
            byte[] o;
            string err;
            int exit;
            if (box == null)
            {
                string rg = PcRipgrep();
                List<string> a = new List<string>();
                string exe;
                if (rg != null)
                {
                    exe = rg;
                    a.Add("--line-number"); a.Add("--no-heading"); a.Add("--color=never"); a.Add("--max-columns=400");
                    if (icase) a.Add("-i");
                    if (filesOnly) a.Add("-l");
                    if (!String.IsNullOrEmpty(glob)) { a.Add("--glob"); a.Add(glob); }
                    a.Add("-e"); a.Add(pattern); a.Add("--"); a.Add(path);
                }
                else
                {
                    string bash = PcGitBash();
                    string grep = bash == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bash)), @"usr\bin\grep.exe");
                    if (grep == null || !File.Exists(grep))
                        return PcTools.Fail("unavailable", "Neither ripgrep nor Git's grep is installed on the home PC.");
                    exe = grep;
                    a.Add("-rnIE"); a.Add("--color=never");
                    if (icase) a.Add("-i");
                    if (filesOnly) a.Add("-l");
                    if (!String.IsNullOrEmpty(glob)) a.Add("--include=" + glob);
                    a.Add("--exclude-dir=.git"); a.Add("--exclude-dir=node_modules");
                    a.Add("-e"); a.Add(pattern); a.Add("--"); a.Add(path);
                }
                StringBuilder line = new StringBuilder();
                foreach (string s in a)
                    line.Append(line.Length > 0 ? " " : "").Append(QuoteWinArg(s));
                o = PcRunCapture(exe, line.ToString(), 300, out exit, out err);
                if (o == null)
                    return PcTools.Fail("search_failed", err);
            }
            else
            {
                string q = PcTools.ShQuote(pattern);
                string p = PcTools.ShQuote(path);
                string script = "if command -v rg >/dev/null; then rg --line-number --no-heading --color=never --max-columns=400 " + (icase ? "-i " : "") + (filesOnly ? "-l " : "") +
                                (String.IsNullOrEmpty(glob) ? "" : "--glob " + PcTools.ShQuote(glob) + " ") + "-e " + q + " -- " + p + "; " +
                                "else grep -rnIE --color=never " + (icase ? "-i " : "") + (filesOnly ? "-l " : "") + (String.IsNullOrEmpty(glob) ? "" : "--include=" + PcTools.ShQuote(glob) + " ") +
                                "--exclude-dir=.git -e " + q + " -- " + p + "; fi";
                o = PcFamilyExec(box, script, null, 300, out exit, out err);
                if (o == null)
                    return PcTools.Fail("sandbox_error", PushCut(err, 300));
            }
            string text = Encoding.UTF8.GetString(o);
            if (exit == 1 && text.Trim().Length == 0)
                return PcTools.Ok("no matches");
            if (exit > 1 && text.Trim().Length == 0)
                return PcTools.Fail("search_failed", PushCut(err, 500));
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (string l in lines)
            {
                if (l.Length == 0) continue;
                if (n >= max) { sb.Append("... (first ").Append(max).Append(" lines of ").Append(lines.Length).Append("; narrow the search)\n"); break; }
                sb.Append(l).Append('\n');
                n++;
            }
            string res = PcTools.Cut(sb.ToString(), PcTools.MaxOutputChars, null);
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["command"] = "grep -rn " + pattern + " " + path;
                res = PcGuardPost(key, chatId, "Bash", ti, res, null);
            }
            return PcTools.Ok(res);
        }

        private byte[] PcRunCapture(string exe, string arguments, int timeoutSec, out int exitCode, out string stderr)
        {
            return PcExec.Capture(new ProcessStartInfo(exe, arguments), timeoutSec, out exitCode, out stderr);
        }

        private Dictionary<string, object> PcDownload(string key, string chatId, Dictionary<string, object> args, PcBox box, JobDeviceState js)
        {
            Dictionary<string, object> refusal;
            string path = PcResolve(box, PcTools.Str(args, "path"), out refusal);
            if (path == null)
                return refusal;
            if (box == null)
            {
                Dictionary<string, object> ti = new Dictionary<string, object>();
                ti["file_path"] = path;
                Dictionary<string, object> held = PcGuardPre(key, chatId, js, "Read", ti, null);
                if (held != null)
                    return held;
            }
            long off = PcTools.Int(args, "offset", 0, 0, Int64.MaxValue);
            long len = PcTools.Int(args, "length", PcTools.MaxReadBytes, 1, PcTools.MaxReadBytes);
            long size;
            byte[] data = PcReadBytes(box, path, off, len, out size, out refusal);
            if (data == null)
                return refusal;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["path"] = path;
            r["size"] = size;
            r["offset"] = off;
            r["length"] = data.Length;
            r["eof"] = off + data.Length >= size;
            r["sha256"] = PcTools.Sha256Hex(data);     // of this chunk
            r["data_base64"] = Convert.ToBase64String(data);
            Dictionary<string, object> site = args.ContainsKey("site") ? args["site"] as Dictionary<string, object> : null;
            if (site != null)
                r["site"] = site;     // relay-mcp.php writes the chunk into the site job's file and drops data_base64
            return PcTools.Ok(PcTools.Json(r));
        }
    }
}
