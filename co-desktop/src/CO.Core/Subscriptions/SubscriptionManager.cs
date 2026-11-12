// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CO.Core.Browser;

namespace CO.Core.Subscriptions;

// One sign-in of a CLI in progress: the CLI process waits for the browser side (and for claude, the code).
public sealed class LoginSession
{
    public required string Cli { get; init; }
    public string State { get; internal set; } = "starting";   // starting | waiting | done | failed | cancelled
    public string Url { get; internal set; } = "";
    public string UserCode { get; internal set; } = "";         // codex device code the page asks for
    public string Email { get; internal set; } = "";            // account of the subscription (typed on the sign-in page)
    public string Owner { get; init; } = "local";               // who started it: "local" (the window) or the agent's run id
    // R2 (review S4-01): Message goes to Brief (other runs), the log and the window, so it never holds the device code or a
    // sign-in link - the one who started the sign-in gets those from Url/UserCode (Describe).
    private string _message = "";
    public string Message { get => _message; internal set => _message = Scrub(value ?? ""); }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; internal set; }
    public bool Ended => EndedUtc != null;
    internal Process? Proc;
    internal readonly StringBuilder Output = new();
    internal readonly CancellationTokenSource Cts = new();
    internal bool CodeSent;

    // Without the link and the code: for anybody but the one who started the sign-in.
    public string Brief() => $"{Cli} sign-in: {State}" + (Message.Length > 0 ? " — " + Scrub(Message) : "");

    // Links, the device code (with or without its dash) and anything shaped like a device code become placeholders.
    public string Scrub(string text)
    {
        string t = Regex.Replace(text, @"https?://\S+", "<link>");
        string code = UserCode;
        if (code.Length > 0)
            t = t.Replace(code, "<code>", StringComparison.OrdinalIgnoreCase).Replace(code.Replace("-", ""), "<code>", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(t, @"(?<![A-Za-z0-9])[A-Z0-9]{4}-[A-Z0-9]{4,6}(?![A-Za-z0-9])", "<code>");
    }

    public string Describe() => $"{Cli} sign-in: {State}" + (Message.Length > 0 ? " — " + Message : "")
        + (Url.Length > 0 && !Ended ? "\nPage: " + Url : "") + (UserCode.Length > 0 && !Ended ? "\nCode for the page: " + UserCode : "");
}

// S4: install, sign-in, check and registration of the agent CLIs on this computer. Shared by CO Desktop (Windows) and
// CO Resident (Mac); the window, the tray/menu and the device tool cli_subs are thin layers over it.
public sealed class SubscriptionManager
{
    public static readonly TimeSpan LoginLimit = TimeSpan.FromMinutes(15);
    private readonly CliShell _sh;
    private readonly HttpClient _http;
    private readonly Dictionary<string, CliStatus> _last = new();
    private readonly Dictionary<string, LoginSession> _logins = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _loginGates = CliCatalog.All.ToDictionary(c => c.Id, _ => new SemaphoreSlim(1, 1));

    public SubscriptionManager(HttpClient http, CliShell? shell = null)
    {
        _http = http;
        _sh = shell ?? new CliShell();
    }

    public CliShell Shell => _sh;
    public Action<string>? Log { get; set; }
    public event Action? Changed;

    // The CO browser tab where a sign-in page opens (the browser profile where the user is signed in to the sites).
    // Null: no browser here; the page link is shown instead.
    // The argument is the CLI id (one tab per CLI). Returns null when the browser is off on this computer.
    public Func<string, (IBrowser Tab, Func<Task> Close)?>? LoginTab { get; set; }
    // The user's mailbox for the provider's sign-in mail (Gmail in the CO browser); null: the person opens the mail.
    public IMailCodes? Mail { get; set; }

    // Registration of the subscriptions on the server (SubsRegistry.PutAsync); null: not reported.
    public Func<IReadOnlyList<CliStatus>, CancellationToken, Task<string>>? Report { get; set; }
    public string LastReport { get; private set; } = "";

    public IReadOnlyList<CliStatus> Last { get { lock (_last) return CliCatalog.All.Select(c => _last.GetValueOrDefault(c.Id)).OfType<CliStatus>().ToList(); } }

    public LoginSession? Login(string id) { lock (_logins) return _logins.GetValueOrDefault(id); }

    private void Note(string m) { Log?.Invoke("subscriptions: " + m); }
    private void Fire() { try { Changed?.Invoke(); } catch (Exception) { } }

    // ---------------------------------------------------------------- status

    public async Task<CliStatus> StatusAsync(string id, CancellationToken ct)
    {
        var c = CliCatalog.Get(id);
        var st = new CliStatus { Id = id, CheckedUtc = DateTime.UtcNow };
        string? exe = _sh.Find(c.Exe);
        if (exe != null)
        {
            st.Installed = true;
            st.Path = exe;
            try
            {
                var (code, so, se) = await _sh.RunAsync(exe, new[] { "--version" }, TimeSpan.FromSeconds(30), ct);
                st.Version = CliCatalog.ParseVersion(so + " " + se);
                if (code != 0 && st.Version.Length == 0) st.Error = CliCatalog.Cut((se + so).Trim(), 160);
                switch (id)
                {
                    case "claude":
                    {
                        var (_, o, e) = await _sh.RunAsync(exe, new[] { "auth", "status", "--json" }, TimeSpan.FromSeconds(30), ct);
                        CliCatalog.ParseClaudeStatus(o.Length > 0 ? o : e, st);
                        break;
                    }
                    case "codex":
                    {
                        var (cc, o, e) = await _sh.RunAsync(exe, new[] { "login", "status" }, TimeSpan.FromSeconds(30), ct);
                        CliCatalog.ParseCodexStatus(o + "\n" + e, cc, st);
                        string auth = Path.Combine(_sh.ExtraEnv.GetValueOrDefault("CODEX_HOME") is { Length: > 0 } h0 ? h0
                            : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(_sh.Home, ".codex"), "auth.json");
                        if (st.LoggedIn == true && File.Exists(auth)) CliCatalog.ParseCodexAuthFile(await File.ReadAllTextAsync(auth, ct), st);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { st.Error = CliCatalog.Cut(ex.Message, 160); }
        }
        lock (_last) _last[id] = st;
        Fire();
        return st;
    }

    public async Task<IReadOnlyList<CliStatus>> StatusAllAsync(CancellationToken ct)
    {
        var list = new List<CliStatus>();
        foreach (var c in CliCatalog.All) list.Add(await StatusAsync(c.Id, ct));
        return list;
    }

    // Status of all CLIs, then the registration on the server (startup, every few hours, after a sign-in).
    public async Task<string> RefreshAndReportAsync(CancellationToken ct)
    {
        var all = await StatusAllAsync(ct);
        if (Report == null) return "";
        try { LastReport = await Report(all, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { LastReport = "not registered: " + CliCatalog.Cut(ex.Message, 200); }
        Note(LastReport);
        Fire();
        return LastReport;
    }

    // ---------------------------------------------------------------- install

    public async Task<string> InstallAsync(string id, CancellationToken ct)
    {
        var c = CliCatalog.Get(id);
        if (!await _installGate.WaitAsync(0, ct)) throw new InvalidOperationException("another installation is running");
        try
        {
            var before = await StatusAsync(id, ct);
            if (before.Installed) return $"{c.Title} {before.Version} is already installed ({before.Path})";
            string how;
            switch (id)
            {
                case "claude":
                    // the official native installer (no Node.js needed), into ~/.local/bin
                    if (_sh.Windows)
                    {
                        string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                        await Must(ps, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "irm https://claude.ai/install.ps1 | iex" }, ct);
                    }
                    else await Must("/bin/bash", new[] { "-c", "curl -fsSL https://claude.ai/install.sh | bash" }, ct);
                    how = "official installer";
                    break;
                case "codex":
                    if (_sh.Find("npm") is string npm)
                    {
                        await Must(npm, new[] { "install", "-g", "@openai/codex" }, ct);
                        how = "npm";
                    }
                    else
                    {
                        await InstallCodexReleaseAsync(ct);
                        how = "GitHub release";
                    }
                    break;
                default:
                    throw new InvalidOperationException($"{c.Title} is installed from its own site; CO only checks it.");
            }
            var after = await StatusAsync(id, ct);
            if (!after.Installed) throw new InvalidOperationException($"{c.Title} was installed ({how}) but is not found on PATH");
            Note($"{id} {after.Version} installed ({how})");
            return $"{c.Title} {after.Version} installed ({how}): {after.Path}";
        }
        finally { _installGate.Release(); }
    }

    private async Task Must(string file, string[] args, CancellationToken ct)
    {
        var (code, so, se) = await _sh.RunAsync(file, args, TimeSpan.FromMinutes(10), ct);
        if (code != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} exit {code}: {CliCatalog.Cut((se + "\n" + so).Trim(), 400)}");
    }

    // Codex without npm: the binary of the latest release on GitHub, checked against the sha256 digest GitHub publishes.
    private async Task InstallCodexReleaseAsync(CancellationToken ct)
    {
        string asset = _sh.Windows ? "codex-x86_64-pc-windows-msvc.exe"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "codex-aarch64-apple-darwin.tar.gz" : "codex-x86_64-apple-darwin.tar.gz";
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/openai/codex/releases/latest");
        req.Headers.UserAgent.ParseAdd("CO-Desktop");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        JsonElement? a = null;
        foreach (var x in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (x.GetProperty("name").GetString() == asset) a = x;
        if (a is not { } el) throw new InvalidOperationException("the latest codex release has no " + asset);
        string url = el.GetProperty("browser_download_url").GetString()!;
        if (!url.StartsWith("https://github.com/openai/codex/releases/download/", StringComparison.Ordinal))
            throw new InvalidOperationException("unexpected download address " + url);
        string digest = el.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        byte[] bytes = await _http.GetByteArrayAsync(url, ct);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidOperationException("GitHub gives no sha256 for " + asset + "; install codex with npm instead");
        if (digest[7..] != sha) throw new InvalidOperationException("codex download does not match its sha256");
        Directory.CreateDirectory(_sh.OwnBinDir);
        string target = Path.Combine(_sh.OwnBinDir, _sh.Windows ? "codex.exe" : "codex");
        if (_sh.Windows)
        {
            await File.WriteAllBytesAsync(target + ".new", bytes, ct);
            File.Move(target + ".new", target, true);
            return;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "co-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmp, asset), bytes, ct);
            await Must("/usr/bin/tar", new[] { "-xzf", Path.Combine(tmp, asset), "-C", tmp }, ct);
            string bin = Directory.GetFiles(tmp).FirstOrDefault(f => Path.GetFileName(f).StartsWith("codex-", StringComparison.Ordinal) && !f.EndsWith(".tar.gz", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("no codex binary in " + asset);
            File.Move(bin, target, true);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
    }

    // ---------------------------------------------------------------- sign-in

    // Starts the CLI's own sign-in and returns once the page link is known. The CLI waits (up to 15 min) for the page:
    // codex polls by itself; claude waits for the code of the callback page (SubmitCode, or the pilot passes it).
    // email: the subscription's account (default: the account this CLI was signed in with last time).
    public async Task<LoginSession> StartLoginAsync(string id, CancellationToken ct, bool pilot = true, string? email = null, string owner = "local")
    {
        var c = CliCatalog.Get(id);
        var gate = _loginGates[id];
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(60), ct)) throw new InvalidOperationException("another sign-in of " + id + " is starting");
        try { return await StartLoginLockedAsync(c, ct, pilot, email, owner); }
        finally { gate.Release(); }
    }

    private async Task<LoginSession> StartLoginLockedAsync(CliInfo c, CancellationToken ct, bool pilot, string? email, string owner)
    {
        string id = c.Id;
        email = (email ?? "").Trim();
        if (email.Length == 0) { lock (_last) email = _last.GetValueOrDefault(id)?.Account ?? ""; }
        if (email.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s""'<>]{1,64}@[A-Za-z0-9.-]{1,190}\.[A-Za-z]{2,}$"))
            throw new ArgumentException("not an e-mail address: " + email);
        if (!c.CanLogin) throw new InvalidOperationException($"{c.Title}: sign in by running '{c.Exe}' once in a terminal; CO only checks it.");
        string exe = _sh.Find(c.Exe) ?? throw new InvalidOperationException($"{c.Title} is not installed (install it first)");
        Cancel(id);
        var s = new LoginSession { Cli = id, Email = email, Owner = owner };
        string[] args = id == "claude" ? new[] { "auth", "login", "--claudeai" } : new[] { "login", "--device-auth" };
        // BROWSER: the CLI must not open the user's default browser; the page opens in the CO browser instead.
        var psi = _sh.Start(exe, args, env: new Dictionary<string, string> { ["BROWSER"] = "none" });
        s.Proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        lock (_logins) _logins[id] = s;
        void Read(StreamReader r) => _ = Task.Run(async () =>
        {
            char[] buf = new char[2048];
            int n;
            try
            {
                while ((n = await r.ReadAsync(buf)) > 0)
                    lock (s.Output) if (s.Output.Length < 64_000) s.Output.Append(buf, 0, n);
            }
            catch (Exception) { }
        });
        Read(s.Proc.StandardOutput);
        Read(s.Proc.StandardError);
        _ = Task.Run(() => WatchAsync(s));
        Note($"{id} sign-in started (pid {s.Proc.Id})");

        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until && !s.Ended)
        {
            if (ct.IsCancellationRequested) { Cancel(id); ct.ThrowIfCancellationRequested(); }
            string text;
            lock (s.Output) text = s.Output.ToString();
            s.Url = (id == "claude" ? CliCatalog.FindUrl(text, "oauth/authorize") : CliCatalog.FindUrl(text, "auth.openai.com")) ?? "";
            if (id == "codex") s.UserCode = CliCatalog.FindDeviceCode(text) ?? "";
            if (s.Url.Length > 0 && (id != "codex" || s.UserCode.Length > 0)) break;
            await Task.Delay(300, CancellationToken.None);
        }
        if (s.Url.Length == 0 && !s.Ended)
        {
            Cancel(id);
            string tail;
            lock (s.Output) tail = CliCatalog.Cut(CliCatalog.StripAnsi(s.Output.ToString()).Trim(), 400);
            throw new InvalidOperationException($"{c.Title} printed no sign-in link: {s.Scrub(tail)}");
        }
        if (!s.Ended)
        {
            s.State = "waiting";
            s.Message = id == "claude" ? "waiting for the authorization in the browser" : "waiting for the code on the page";
        }
        Fire();
        if (pilot && LoginTab?.Invoke(id) is { } tab) _ = Task.Run(() => PilotAsync(s, tab));
        return s;
    }

    // claude: the code from the callback page (the page link, "code#state" or the bare code).
    public string SubmitCode(string id, string code)
    {
        var s = Login(id) ?? throw new InvalidOperationException("no sign-in of " + id + " is running");
        if (id != "claude") throw new InvalidOperationException(id + " needs no code: it finishes by itself once the page accepted its device code");
        if (s.Ended || s.Proc == null) throw new InvalidOperationException("the sign-in has ended: " + s.State);
        string line = CliCatalog.ClaudeCode(code, s.Url);
        lock (s)
        {
            if (s.CodeSent) return "the code was already passed to claude";
            s.Proc.StandardInput.WriteLine(line);
            s.Proc.StandardInput.Flush();
            s.CodeSent = true;
        }
        s.Message = "code passed to claude, checking";
        Note("claude sign-in code passed");
        Fire();
        return "code passed to claude";
    }

    public bool Cancel(string id)
    {
        LoginSession? s;
        lock (_logins) s = _logins.GetValueOrDefault(id);
        if (s == null || s.Ended) return false;
        s.State = "cancelled";
        s.Cts.Cancel();
        try { s.Proc?.Kill(entireProcessTree: true); } catch (Exception) { }
        return true;
    }

    private async Task WatchAsync(LoginSession s)
    {
        var p = s.Proc!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
        cts.CancelAfter(LoginLimit);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            if (s.State != "cancelled") { s.State = "failed"; s.Message = "no sign-in in 15 min"; }
        }
        try { await Task.Delay(300); } catch (Exception) { }
        if (s.State is "starting" or "waiting")
        {
            var st = await StatusAsync(s.Cli, CancellationToken.None);
            if (st.LoggedIn == true)
            {
                s.State = "done";
                s.Message = "signed in" + (st.Account.Length > 0 ? " as " + st.Account : "") + (st.Plan.Length > 0 ? $" ({st.Plan})" : "");
            }
            else
            {
                string tail;
                lock (s.Output) tail = CliCatalog.StripAnsi(s.Output.ToString()).Trim();
                s.State = "failed";
                s.Message = $"exit {SafeExit(p)}: " + CliCatalog.Cut(tail.Length > 300 ? tail[^300..] : tail, 300);
            }
        }
        s.EndedUtc = DateTime.UtcNow;
        Note($"{s.Cli} sign-in {s.State}: {s.Message}");
        Fire();
        if (s.State == "done")
        {
            try { await RefreshAndReportAsync(CancellationToken.None); } catch (Exception ex) { Note("report after sign-in: " + ex.Message); }
        }
        p.Dispose();
    }

    private static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch (Exception) { return "?"; } }

    // Opens the page in the CO browser and does the safe clicks itself (LoginPilot); the person or an agent at the
    // device finishes a page that asks to sign in to the provider first.
    private async Task PilotAsync(LoginSession s, (IBrowser Tab, Func<Task> Close) t)
    {
        try
        {
            await LoginPilot.RunAsync(t.Tab, s, code => SubmitCode(s.Cli, code), m => { s.Message = m; Note(s.Cli + ": " + s.Message); Fire(); }, s.Cts.Token, Mail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { s.Message = "browser: " + CliCatalog.Cut(ex.Message, 200); Note(s.Cli + " pilot: " + s.Scrub(ex.Message)); Fire(); }
        // the tab stays while the sign-in waits for the person; closed once it ended well
        for (int i = 0; i < 900 && !s.Ended; i++) { try { await Task.Delay(1000, s.Cts.Token); } catch (OperationCanceledException) { break; } }
        if (s.State == "done") { try { await t.Close(); } catch (Exception) { } }
    }

    // ---------------------------------------------------------------- check

    // A one-word request through the subscription: proves the sign-in works end to end.
    public async Task<string> VerifyAsync(string id, CancellationToken ct)
    {
        var c = CliCatalog.Get(id);
        var st = await StatusAsync(id, ct);
        if (!st.Installed) throw new InvalidOperationException(c.Title + " is not installed");
        const string prompt = "Reply with the single word OK.";
        string[] args = id switch
        {
            "claude" => new[] { "-p", "--output-format", "text", "--max-turns", "1" },
            "codex" => new[] { "exec", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never", "-" },
            _ => new[] { "-p", prompt },
        };
        var sw = Stopwatch.StartNew();
        string tmp = Path.Combine(Path.GetTempPath(), "co-subs-check");
        Directory.CreateDirectory(tmp);
        var (code, so, se) = await _sh.RunAsync(st.Path, args, TimeSpan.FromMinutes(3), ct, stdin: id == "agy" ? null : prompt, workDir: tmp);
        string answer = CliCatalog.StripAnsi(so).Trim();
        bool ok = code == 0 && answer.Contains("OK", StringComparison.OrdinalIgnoreCase);
        string r = ok
            ? $"{c.Title} answered in {sw.Elapsed.TotalSeconds:0} s: {CliCatalog.Cut(answer.Split('\n').Last().Trim(), 60)}"
            : $"{c.Title} check failed (exit {code}): {CliCatalog.Cut((se + "\n" + answer).Trim(), 300)}";
        Note(r);
        if (!ok) throw new InvalidOperationException(r);
        return r;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var c in CliCatalog.All)
        {
            var st = Last.FirstOrDefault(x => x.Id == c.Id);
            sb.AppendLine(st?.Line() ?? c.Title + ": not checked yet");
            if (Login(c.Id) is { } s && (!s.Ended || s.EndedUtc > DateTime.UtcNow.AddMinutes(-30))) sb.AppendLine("  " + s.Describe().Replace("\n", "\n  "));
        }
        if (LastReport.Length > 0) sb.AppendLine("Server: " + LastReport);
        return sb.ToString().TrimEnd();
    }
}
