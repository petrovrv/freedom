// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Mac;

// 0.5.2: secrets of agent tools on this Mac, in its own login Keychain (generic passwords "aiwb/<name>", account
// "<account>"), never sent to the home PC. secret_prompt asks the person at the Mac in a native dialog with a hidden
// field; the value goes from osascript's stdout straight to `security -i` stdin and is never logged, returned,
// put into argv or into the audit (arguments hold only name, account and the agent's note). Rules: MacSecretRules.cs.
public static class MacSecretStore
{
    private const string Security = "/usr/bin/security";

    // The resident's own items (Keychain service, LaunchAgent labels): never read, written or deleted by these tools.
    public static IEnumerable<string> Reserved => new[] { KeychainStore.Service, Paths.Label, MacAppInstall.Label, "co.resident", "co-resident" };

    public static string LoginKeychain => Path.Combine(Paths.Home, "Library", "Keychains", "login.keychain-db");

    // Attributes of the item (no -w: the value is not printed), null when missing.
    public static string? Attributes(string service, string account)
    {
        var (code, so, se) = Proc.Run(Security, new[] { "find-generic-password", "-s", service, "-a", account }, timeoutMs: 20_000);
        if (code == 0) return so + "\n" + se;   // security prints the attributes on stdout and the keychain line on stderr in some versions
        if (code == 44) return null;           // errSecItemNotFound
        throw new ToolException("keychain_failed", $"Keychain lookup failed (security exit {code}); is the login Keychain locked?");
    }

    // Writes the value through stdin; checks that the item exists afterwards and, for an update, that it changed.
    // Errors never carry the output of security (it could echo the line).
    public static void Write(string service, string account, string value)
    {
        string? before = Attributes(service, account);
        string line = MacSecretRules.AddCommand(service, account, value);
        var (code, _, _) = Proc.Run(Security, new[] { "-i" }, line, timeoutMs: 30_000);
        line = "";
        if (code != 0) throw new ToolException("keychain_failed", $"Keychain write failed (security exit {code}).");
        string? after = Attributes(service, account);
        if (after == null) throw new ToolException("keychain_failed", "Keychain write did not create the item.");
        if (before != null && MacSecretRules.ModDate(before) is { } m0 && MacSecretRules.ModDate(after) == m0)
            throw new ToolException("keychain_failed", "Keychain write did not change the existing item (an item made by another app may need 'Allow' at the Mac; delete it with secret_delete and ask again).");
    }

    public static bool Delete(string service, string account)
    {
        var (code, _, _) = Proc.Run(Security, new[] { "delete-generic-password", "-s", service, "-a", account }, timeoutMs: 20_000);
        if (code == 0) return true;
        if (code == 44) return false;
        throw new ToolException("keychain_failed", $"Keychain delete failed (security exit {code}).");
    }

    public static List<(string Service, string Account)> List()
    {
        var (code, so, _) = Proc.Run(Security, new[] { "dump-keychain", LoginKeychain }, timeoutMs: 30_000);
        if (code != 0) throw new ToolException("keychain_failed", $"Keychain listing failed (security exit {code}).");
        return MacSecretRules.ParseDump(so);
    }
}

// The person's answers to secret dialogs of this process (status only, never values).
internal static class SecretPrompts
{
    public sealed class Prompt
    {
        public required string Service { get; init; }
        public required string Account { get; init; }
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        private volatile string _status = "waiting", _detail = "";
        private long _endedTicks;
        public DateTime? EndedUtc { get => Interlocked.Read(ref _endedTicks) is var t && t > 0 ? new DateTime(t, DateTimeKind.Utc) : null; set => Interlocked.Exchange(ref _endedTicks, value?.Ticks ?? 0); }
        public string Status { get => _status; set => _status = value; }   // waiting | saved | cancelled | timeout | empty | too_long | error
        public string Detail { get => _detail; set => _detail = value; }
        public Task Done { get; set; } = Task.CompletedTask;
    }

    private static readonly object Sync = new();
    private static Prompt? _open;
    private static readonly List<Prompt> Recent = new();

    public static bool DialogOpen { get { lock (Sync) return _open != null; } }

    public static Prompt? Open { get { lock (Sync) return _open; } }

    public static List<Prompt> History { get { lock (Sync) return Recent.ToList(); } }

    // Starts the dialog in the background (one at a time); returns the open prompt for the same item, or null when
    // a dialog for another item is open.
    public static Prompt? Start(string service, string account, string label, bool tainted)
    {
        lock (Sync)
        {
            if (_open != null)
                return _open.Service == service && _open.Account == account ? _open : null;
            // A repeated call right after a real answer reports that answer instead of opening a second dialog
            // (failures such as an empty field or a locked Keychain may be asked again at once).
            if (Recent.LastOrDefault(r => r.Service == service && r.Account == account) is { EndedUtc: { } end } last
                && last.Status is "saved" or "cancelled" or "timeout" && DateTime.UtcNow - end < TimeSpan.FromSeconds(30))
                return last;
            // Against a loop of dialogs (an agent steered by a web page): three Cancels in 10 min stop new ones.
            if (Recent.Count(r => r.Status == "cancelled" && r.EndedUtc is { } e && DateTime.UtcNow - e < TimeSpan.FromMinutes(10)) >= 3)
                throw new ToolException("declined", "The person cancelled three secret dialogs in the last 10 minutes; no new dialog for now. Ask in the chat first.");
            var p = new Prompt { Service = service, Account = account };
            _open = p;
            Recent.Add(p);
            if (Recent.Count > 20) Recent.RemoveAt(0);
            p.Done = Task.Run(() => Run(p, label, tainted));
            return p;
        }
    }

    private static void Run(Prompt p, string label, bool tainted)
    {
        try
        {
            bool exists = MacSecretStore.Attributes(p.Service, p.Account) != null;
            Log.Write($"secret_prompt asked: {p.Service} account={p.Account}");
            Dialog.Notify("An agent asks you to type a secret for " + p.Service, "CO");
            string text = MacSecretRules.DialogText(p.Service, p.Account, label, exists, tainted);
            var (code, so, _) = Proc.Run("/usr/bin/osascript", MacSecretRules.DialogArgs(text, MacSecretRules.DialogTitle(p.Service)),
                timeoutMs: (MacSecretRules.DialogTimeoutSec + 60) * 1000);
            var (kind, value) = MacSecretRules.ParseDialog(code, so);
            so = "";
            switch (kind)
            {
                case MacSecretRules.Answer.Saved:
                    try { MacSecretStore.Write(p.Service, p.Account, value!); p.Status = "saved"; p.Detail = $"{value!.Length} characters"; }
                    catch (ToolException ex) { p.Status = "error"; p.Detail = ex.Message; }
                    value = null;
                    break;
                case MacSecretRules.Answer.Cancelled: p.Status = "cancelled"; break;
                case MacSecretRules.Answer.TimedOut: p.Status = "timeout"; p.Detail = $"no answer in {MacSecretRules.DialogTimeoutSec / 60} min"; break;
                case MacSecretRules.Answer.Empty: p.Status = "empty"; p.Detail = "Save was pressed with an empty field; nothing saved"; break;
                case MacSecretRules.Answer.TooLong: p.Status = "too_long"; p.Detail = $"longer than {MacSecretRules.MaxValueChars} characters or {MacSecretRules.MaxValueBytes} UTF-8 bytes; nothing saved"; break;
                default: p.Status = "error"; p.Detail = code == -1 ? "the dialog did not close in time" : $"the dialog failed (osascript exit {code})"; break;
            }
        }
        catch (Exception ex) { p.Status = "error"; p.Detail = ex is ToolException ? ex.Message : "unexpected " + ex.GetType().Name; }
        finally
        {
            p.EndedUtc = DateTime.UtcNow;
            lock (Sync) if (_open == p) _open = null;
            Log.Write($"secret_prompt answer: {p.Service} account={p.Account} -> {p.Status}");
        }
    }

    public static string Describe(Prompt p) => p.Status switch
    {
        "waiting" => $"waiting: the dialog for {p.Service} (account {p.Account}) is open on the Mac since {p.StartedUtc:HH:mm:ss} UTC; " +
                     $"it closes by itself after {MacSecretRules.DialogTimeoutSec / 60} min. Poll with secret_list name=<name> until it says saved, cancelled or timeout; do not call secret_prompt again meanwhile.",
        "saved" => $"saved: {p.Service} account {p.Account} ({p.Detail}) in the login Keychain of this Mac. Tools on this Mac read it with " +
                   $"python keyring.get_password(\"{p.Service}\", \"{p.Account}\") (or, for plain ASCII values, `security find-generic-password -s {p.Service} -a {p.Account} -w`).",
        "cancelled" => $"cancelled: the person pressed Cancel for {p.Service} (account {p.Account}); nothing saved.",
        "timeout" => $"timeout: nobody answered the dialog for {p.Service} (account {p.Account}) in {MacSecretRules.DialogTimeoutSec / 60} min; nothing saved.",
        _ => $"{p.Status}: {p.Service} (account {p.Account}): {p.Detail}",
    };
}

// Ask the person at the Mac for a secret and keep it in this Mac's Keychain; returns only the status.
internal sealed class SecretPromptTool : IDeviceTool
{
    public const int MaxWaitSec = 90;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "secret_prompt", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Ask the person at this Mac to type a password, app password or token in a native dialog with a hidden field " +
                      "(title 'CO Resident — secret for aiwb/<name>'). On Save it is stored in the login Keychain of THIS Mac as a generic " +
                      "password, service aiwb/<name>, account <account> (default \"default\"), the names the tool library uses with Python " +
                      "keyring; nothing goes to the home PC. The value is never returned, logged or shown to the agent: the result is only " +
                      "saved / cancelled / timeout / waiting. The dialog stays open up to 10 min; this call waits up to wait_sec (default and " +
                      "max 90 s) and otherwise returns 'waiting' — then poll with secret_list. Tools that run on this Mac read the value with " +
                      "keyring.get_password(\"aiwb/<name>\", \"<account>\") (macOS may ask 'Allow access' once, the person clicks Always Allow) " +
                      "or, for plain ASCII values, `security find-generic-password -s aiwb/<name> -a <account> -w` (other values print as hex). " +
                      "Use it on a Mac instead of the home PC secret-prompt window. After 3 Cancels in 10 min no new dialog opens.",
        InputSchema = Schema.Obj(
            ("name", "string", "item name, a-z 0-9 . _ - (the Keychain service is aiwb/<name>), e.g. gmail-imap", true),
            ("account", "string", "login or e-mail the secret belongs to (default \"default\")", false),
            ("label", "string", "one line for the person: what to type and where to find it, e.g. 'Gmail app password (myaccount.google.com → App passwords)'", false),
            ("wait_sec", "integer", "how long this call waits for the answer, 0-90 (default 90)", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env)
    {
        string service = MacSecretRules.Service(Args.OptStr(args, "name"), MacSecretStore.Reserved);
        string account = MacSecretRules.Account(Args.OptStr(args, "account"));
        return new() { Tool = "secret_prompt", Class = RiskClass.W, Summary = $"ask the person for a secret, saved as {service} ({account}) in this Mac's Keychain" };
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string service = MacSecretRules.Service(Args.OptStr(args, "name"), MacSecretStore.Reserved);
        string account = MacSecretRules.Account(Args.OptStr(args, "account"));
        string label = MacSecretRules.CleanLabel(Args.OptStr(args, "label"));
        var p = SecretPrompts.Start(service, account, label, env.Tainted);
        if (p == null)
        {
            var o = SecretPrompts.Open;
            throw new ToolException("busy", $"Another secret dialog is open on the Mac ({o?.Service} account {o?.Account}); wait for it (secret_list) and ask again.");
        }
        int callMs = env.Call?.TimeoutMs ?? 120_000;
        int waitMs = (int)Math.Clamp(Math.Min(Args.OptLong(args, "wait_sec", MaxWaitSec) * 1000, callMs - 20_000), 0, MaxWaitSec * 1000);
        try { await p.Done.WaitAsync(TimeSpan.FromMilliseconds(waitMs), ct); }
        catch (TimeoutException) { }
        return new ToolOutput { Content = SecretPrompts.Describe(p) };
    }
}

// Names of the aiwb/* secrets in this Mac's login Keychain and the state of recent dialogs; never values.
internal sealed class SecretListTool : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "secret_list", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "List the aiwb/<name> secrets (service and account, never values) in the login Keychain of this Mac, and the " +
                      "state of the last 20 secret_prompt dialogs since the resident started (waiting / saved / cancelled / timeout). Optional name filters " +
                      "one item. Read-only, no confirmation.",
        InputSchema = Schema.Obj(("name", "string", "only this item (aiwb/<name>)", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) => new() { Tool = "secret_list", Class = RiskClass.R, Summary = "list aiwb/* Keychain items (names only)" };

    public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string? only = Args.OptStr(args, "name") is { Length: > 0 } n ? MacSecretRules.Service(n, MacSecretStore.Reserved) : null;
        var sb = new StringBuilder();
        var items = MacSecretStore.List().Where(i => only == null || i.Service == only).ToList();
        sb.Append(items.Count == 0 ? "No aiwb/* secrets in the login Keychain of this Mac" + (only != null ? " for " + only : "") + ".\n"
            : "Secrets in the login Keychain of this Mac (service  account):\n");
        foreach (var (s, a) in items) sb.Append("  ").Append(s).Append("  ").Append(a).Append('\n');
        var prompts = SecretPrompts.History.Where(p => only == null || p.Service == only).Reverse().ToList();
        if (prompts.Count > 0)
        {
            sb.Append("\nsecret_prompt dialogs since the resident started (newest first):\n");
            foreach (var p in prompts) sb.Append("  ").Append(SecretPrompts.Describe(p)).Append('\n');
        }
        return Task.FromResult(new ToolOutput { Content = sb.ToString() });
    }
}

// Delete one aiwb/* secret; confirmed at the Mac for every call.
internal sealed class SecretDeleteTool : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "secret_delete", Class = RiskClass.D, Untrusted = UntrustedMode.Never,
        Description = "Delete the secret aiwb/<name> (account <account>, default \"default\") from the login Keychain of this Mac. " +
                      "The person at the Mac confirms every call. Only aiwb/* items; CO Resident's own items are refused.",
        InputSchema = Schema.Obj(
            ("name", "string", "item name (aiwb/<name>)", true),
            ("account", "string", "account (default \"default\")", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env)
    {
        string service = MacSecretRules.Service(Args.OptStr(args, "name"), MacSecretStore.Reserved);
        string account = MacSecretRules.Account(Args.OptStr(args, "account"));
        return new() { Tool = "secret_delete", Class = RiskClass.D, ConfirmEveryCall = true, Summary = $"delete the secret {service} (account {account}) from this Mac's Keychain" };
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string service = MacSecretRules.Service(Args.OptStr(args, "name"), MacSecretStore.Reserved);
        string account = MacSecretRules.Account(Args.OptStr(args, "account"));
        bool done = MacSecretStore.Delete(service, account);
        Log.Write($"secret_delete: {service} account={account} -> {(done ? "deleted" : "missing")}");
        return Task.FromResult(new ToolOutput { Content = done ? $"deleted: {service} account {account}" : $"missing: no {service} with account {account} on this Mac" });
    }
}
