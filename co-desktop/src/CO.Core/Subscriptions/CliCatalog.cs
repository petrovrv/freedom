// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CO.Core.Subscriptions;

// S4: the agent CLIs a member can connect to their own subscription from CO Desktop / CO Resident.
public sealed record CliInfo(
    string Id,            // claude | codex | agy
    string Title,
    string Exe,           // file name without extension
    string Provider,      // whose account signs in
    bool CanLogin);       // CO runs the sign-in itself (agy: only status, sign-in by running it once)

// What CO found out about one CLI on this computer.
public sealed class CliStatus
{
    public required string Id { get; init; }
    public bool Installed { get; set; }
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public bool? LoggedIn { get; set; }          // null: unknown
    public string Account { get; set; } = "";    // e-mail of the signed-in account, when the CLI tells it
    public string Plan { get; set; } = "";       // max, pro, plus, team, …
    public string Method { get; set; } = "";     // claude.ai, ChatGPT, API key, …
    public string Error { get; set; } = "";
    public DateTime CheckedUtc { get; set; }

    public string Line()
    {
        var c = CliCatalog.Get(Id);
        if (!Installed) return $"{c.Title}: not installed";
        string who = LoggedIn switch
        {
            true => "signed in" + (Account.Length > 0 ? " as " + Account : "") + (Plan.Length > 0 ? $" ({Plan})" : Method.Length > 0 ? $" ({Method})" : ""),
            false => "not signed in",
            _ => "sign-in unknown",
        };
        return $"{c.Title} {Version}: {who}" + (Error.Length > 0 ? " — " + Error : "");
    }
}

public static class CliCatalog
{
    public static readonly IReadOnlyList<CliInfo> All = new[]
    {
        new CliInfo("claude", "Claude Code", "claude", "Anthropic (claude.ai)", true),
        new CliInfo("codex", "Codex", "codex", "OpenAI (ChatGPT)", true),
        new CliInfo("agy", "Antigravity", "agy", "Google", false),
    };

    public static CliInfo Get(string id) =>
        All.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException("unknown CLI '" + id + "' (claude, codex, agy)");

    public static bool IsKnown(string id) => All.Any(c => c.Id == id);

    // ---------------------------------------------------------------- parsing of the CLIs' own answers

    // "2.1.310 (Claude Code)", "codex-cli 0.157.0", "1.4.2"
    public static string ParseVersion(string output)
    {
        var m = Regex.Match(output, @"\d+\.\d+(\.\d+)?([-.][0-9A-Za-z.]+)?");
        return m.Success ? m.Value : "";
    }

    // claude auth status --json: {"loggedIn":true,"authMethod":"claude.ai","email":"…","subscriptionType":"max",…}
    public static void ParseClaudeStatus(string output, CliStatus st)
    {
        int i = output.IndexOf('{');
        if (i < 0) { st.LoggedIn = null; st.Error = Cut(output.Trim(), 160); return; }
        try
        {
            using var doc = JsonDocument.Parse(output[i..]);
            var r = doc.RootElement;
            st.LoggedIn = r.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            st.Account = Str(r, "email");
            st.Plan = Str(r, "subscriptionType");
            st.Method = Str(r, "authMethod");
        }
        catch (JsonException) { st.LoggedIn = null; st.Error = "unreadable auth status"; }
    }

    // codex login status: "Logged in using ChatGPT" / "Logged in using an API key - sk-…" / "Not logged in"
    public static void ParseCodexStatus(string output, int exitCode, CliStatus st)
    {
        string t = output.Trim();
        if (t.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)) st.LoggedIn = false;
        else if (t.Contains("Logged in", StringComparison.OrdinalIgnoreCase))
        {
            st.LoggedIn = true;
            st.Method = t.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT" : t.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "API key" : "";
        }
        else st.LoggedIn = exitCode == 0 ? null : false;
    }

    // ~/.codex/auth.json: the id_token (a JWT) carries the account e-mail and the ChatGPT plan; tokens never leave here.
    public static void ParseCodexAuthFile(string json, CliStatus st)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return;
            string idToken = Str(tokens, "id_token");
            string[] parts = idToken.Split('.');
            if (parts.Length < 2) return;
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(b64));
            var c = claims.RootElement;
            if (st.Account.Length == 0) st.Account = Str(c, "email");
            if (c.TryGetProperty("https://api.openai.com/auth", out var a) && a.ValueKind == JsonValueKind.Object)
                st.Plan = Str(a, "chatgpt_plan_type");
        }
        catch (Exception e) when (e is JsonException or FormatException) { }
    }

    // The sign-in page link a CLI prints (ANSI colours removed).
    public static string? FindUrl(string text, string hostPart)
    {
        foreach (Match m in Regex.Matches(StripAnsi(text), @"https://[^\s""'<>\x1b]+"))
            if (m.Value.Contains(hostPart, StringComparison.OrdinalIgnoreCase)) return m.Value.TrimEnd('.', ',', ')');
        return null;
    }

    // codex --device-auth one-time code, e.g. "C69F-0EB6Q"
    public static string? FindDeviceCode(string text)
    {
        var m = Regex.Match(StripAnsi(text), @"(?m)^\s*([A-Z0-9]{4}-[A-Z0-9]{4,6})\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string StripAnsi(string s) => Regex.Replace(s, @"\x1b\[[0-9;?]*[A-Za-z]", "");

    // What to write to "claude auth login": "code#state". Accepts the callback URL, "code#state" or the bare code.
    public static string ClaudeCode(string input, string authUrl)
    {
        string s = input.Trim();
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            var q = Query(u.Query);
            if (!q.TryGetValue("code", out string? code) || code.Length == 0) throw new ArgumentException("the link has no code");
            return Safe(q.TryGetValue("state", out string? st) && st.Length > 0 ? code + "#" + st : WithState(code, authUrl));
        }
        if (s.Length == 0 || s.Length > 2000 || s.Any(char.IsWhiteSpace)) throw new ArgumentException("not a sign-in code");
        return Safe(s.Contains('#') ? s : WithState(s, authUrl));
    }

    // One line for the CLI's stdin: code and state characters only (a decoded %0A must never add a line).
    private static string Safe(string line) =>
        line.Length is > 0 and <= 2000 && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '#' or '-')
            ? line : throw new ArgumentException("not a sign-in code");

    // The state of a sign-in link ("" when none).
    public static string StateOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && Query(u.Query).TryGetValue("state", out string? st) ? st : "";

    private static string WithState(string code, string authUrl) =>
        Uri.TryCreate(authUrl, UriKind.Absolute, out var u) && Query(u.Query).TryGetValue("state", out string? st) && st.Length > 0
            ? code + "#" + st : code;

    public static Dictionary<string, string> Query(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            string k = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
            string v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            d.TryAdd(k, v);
        }
        return d;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

// Runs CLI commands with a PATH that also has the usual install folders (CO may start before PATH is updated, and a
// LaunchAgent on the Mac has a bare PATH).
public sealed class CliShell
{
    public bool Windows { get; }
    public string Home { get; }
    // Added to every CLI process (live tests: CLAUDE_CONFIG_DIR / CODEX_HOME of a throwaway sign-in).
    public Dictionary<string, string> ExtraEnv { get; } = new();
    private readonly List<string> _extra;

    public CliShell() : this(OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public CliShell(bool windows, string home)
    {
        Windows = windows;
        Home = home;
        _extra = windows
            ? new List<string>
            {
                System.IO.Path.Combine(home, ".local", "bin"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            }
            : new List<string>
            {
                System.IO.Path.Combine(home, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin",
                System.IO.Path.Combine(home, ".npm-global", "bin"), System.IO.Path.Combine(home, ".antigravity", "bin"),
                "/usr/bin", "/bin", "/usr/sbin", "/sbin",
            };
    }

    // Folder where CO puts a CLI it downloads itself (codex from its GitHub release).
    public string OwnBinDir => Windows
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin")
        : System.IO.Path.Combine(Home, ".local", "bin");

    public string PathVar()
    {
        char sep = Windows ? ';' : ':';
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string d in _extra) if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        return string.Join(sep, dirs);
    }

    public string? Find(string exe)
    {
        char sep = Windows ? ';' : ':';
        string[] names = Windows && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { exe + ".exe", exe + ".cmd" } : new[] { exe };
        foreach (string d in PathVar().Split(sep, StringSplitOptions.RemoveEmptyEntries))
            foreach (string n in names)
            {
                try
                {
                    string full = System.IO.Path.Combine(d.Trim().Trim('"'), n);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    public ProcessStartInfo Start(string file, IEnumerable<string> args, string? workDir = null, IDictionary<string, string>? env = null)
    {
        // npm shims on Windows are .cmd files: they run through cmd.exe with one command line of its own quoting
        bool cmd = Windows && file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(cmd ? System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe") : file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Home,
        };
        if (cmd) psi.Arguments = CmdLine(file, args);
        else foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = PathVar();
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CI"] = "";
        foreach (var kv in ExtraEnv) psi.Environment[kv.Key] = kv.Value;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    // cmd.exe /s strips the outer quotes of the whole line: /d /s /c ""<shim>" <args>". The shim path and the
    // arguments (fixed words of CO, never agent text) must not hold cmd metacharacters.
    public static string CmdLine(string file, IEnumerable<string> args)
    {
        static string Q(string a)
        {
            if (a.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n' }) >= 0)
                throw new ArgumentException("unsafe characters for cmd.exe: " + a);
            return a.Length > 0 && !a.Any(char.IsWhiteSpace) ? a : "\"" + a + "\"";
        }
        return "/d /s /c \"\"" + Q(file).Trim('"') + "\"" + string.Concat(args.Select(a => " " + Q(a))) + "\"";
    }

    public async Task<(int Code, string Out, string Err)> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? stdin = null, IDictionary<string, string>? env = null, string? workDir = null)
    {
        using var p = Process.Start(Start(file, args, workDir, env)) ?? throw new InvalidOperationException("could not start " + file);
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        if (stdin != null) await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{System.IO.Path.GetFileName(file)} did not finish in {(int)timeout.TotalSeconds} s");
        }
        return (p.ExitCode, await so.ConfigureAwait(false), await se.ConfigureAwait(false));
    }
}
