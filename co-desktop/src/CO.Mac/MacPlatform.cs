// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CO.Core.Auth;
using CO.Core.Crypto;
using CO.Core.Host;
using CO.Core.Runtime;

namespace CO.Mac;

public static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DataDir => Path.Combine(Home, "Library", "Application Support", "CO");
    public static string LogDir => Path.Combine(Home, "Library", "Logs", "CO");
    public static string LogFile => Path.Combine(LogDir, "resident.log");
    public static string AppDir => AppContext.BaseDirectory.TrimEnd('/');
    public static string Binary => Environment.ProcessPath ?? Path.Combine(AppDir, "co-resident");
    public const string Label = "org.freedom-workbench.co.resident";
    public static string LaunchAgent => Path.Combine(Home, "Library", "LaunchAgents", Label + ".plist");
    public static string WorkFolder => Path.Combine(Home, "CO");   // not under ~/Documents: no privacy (TCC) prompt
}

public static class Log
{
    private static readonly object Sync = new();

    public static void Write(string line)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 5_000_000) File.Move(Paths.LogFile, Paths.LogFile + ".1", true);
                File.AppendAllText(Paths.LogFile, $"{DateTime.UtcNow:O} {line}\n");
            }
            catch (IOException) { }
        }
    }
}

public static class Proc
{
    // Runs a program with arguments (no shell); stdin text optional. Returns exit code and output.
    public static (int code, string stdout, string stderr) Run(string file, IEnumerable<string> args, string? stdin = null, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, o.Result, "timeout"); }
        return (p.ExitCode, o.Result, e.Result);
    }
}

// Secrets in the login Keychain through /usr/bin/security; values go through stdin (security -i), never argv.
public sealed class KeychainStore : ISecretStore
{
    public const string Service = "org.freedom-workbench.co.resident";

    public string? Read(string target)
    {
        var (code, so, se) = Proc.Run("/usr/bin/security", new[] { "find-generic-password", "-s", Service, "-a", target, "-w" });
        if (code == 0) return so.TrimEnd('\n');
        if (code == 44) return null;   // errSecItemNotFound
        // Locked or refused Keychain, timeout: never report "missing" (that would make new keys over a paired identity).
        throw new InvalidOperationException($"Keychain read failed ({code}): {se.Trim()}");
    }

    public void Write(string target, string secret)
    {
        if (secret.IndexOfAny(new[] { '"', '\\', '\n', '\r' }) >= 0 || target.IndexOfAny(new[] { '"', '\\', '\n' }) >= 0)
            throw new ArgumentException("Secret contains characters the Keychain helper does not pass.");
        var (code, _, se) = Proc.Run("/usr/bin/security", new[] { "-i" },
            $"add-generic-password -U -s \"{Service}\" -a \"{target}\" -w \"{secret}\"\n");
        if (code != 0) throw new InvalidOperationException("Keychain write failed: " + se.Trim());
        if (Read(target) != secret) throw new InvalidOperationException("Keychain write could not be read back.");
    }

    public void Delete(string target)
    {
        var (code, _, se) = Proc.Run("/usr/bin/security", new[] { "delete-generic-password", "-s", Service, "-a", target });
        if (code != 0 && code != 44) throw new InvalidOperationException($"Keychain delete failed ({code}): {se.Trim()}");
    }
}

// Device keys (P-256 result signing, RSA 2048 service account) kept as PKCS#8 in the Keychain.
// Secure Enclave needs a signed app with entitlements (R4); until then the keys are software keys.
public sealed class MacKeyStore : IDeviceKeyStore
{
    private const string SigningName = "device-signing", ServiceAccountName = "device-sa";
    private readonly EcdsaSigner _signer;
    private readonly RSA _rsa;

    // allowCreate: only before the device exists (no device.json); later a missing key is an error, not a new identity.
    public MacKeyStore(ISecretStore store, bool allowCreate)
    {
        string? e = store.Read(SigningName), r = store.Read(ServiceAccountName);
        if ((e == null || r == null) && !allowCreate)
            throw new InvalidOperationException("The device keys are missing from the Keychain. Reinstall: co-resident uninstall --purge, then the install command from CO.");
        var ec = ECDsa.Create();
        if (e != null) ec.ImportPkcs8PrivateKey(Convert.FromBase64String(e), out _);
        else { ec.GenerateKey(ECCurve.NamedCurves.nistP256); store.Write(SigningName, Convert.ToBase64String(ec.ExportPkcs8PrivateKey())); }
        RSA rsa;
        if (r != null) { rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(r), out _); }
        else { rsa = RSA.Create(2048); store.Write(ServiceAccountName, Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())); }
        _signer = new EcdsaSigner(ec);
        _rsa = rsa;
    }

    public ISigner SigningKey() => _signer;
    public RSA ServiceAccountKey() => _rsa;
    public bool HardwareBacked => false;

    public static void DeleteKeys(ISecretStore store)
    {
        store.Delete(SigningName);
        store.Delete(ServiceAccountName);
    }
}

// Native dialogs through osascript (works from a LaunchAgent in the user's session, no UI framework).
public static class Dialog
{
    // Returns the pressed button, or null on timeout/cancel.
    public const int MaxText = 1800;

    public static string? Ask(string text, string title, string[] buttons, string defaultButton, int timeoutSec, bool caution = true)
    {
        if (text.Length > MaxText) text = text[..MaxText] + "…";
        var args = new List<string>
        {
            "-e", "on run argv",
            "-e", "set btns to {}",
            "-e", "repeat with i from 4 to count of argv",
            "-e", "set end of btns to item i of argv",
            "-e", "end repeat",
            "-e", "try",
            "-e", "tell me to activate",
            "-e", "end try",
            "-e", "set r to display dialog (item 1 of argv) with title (item 2 of argv) buttons btns default button (item 3 of argv) giving up after " + timeoutSec + (caution ? " with icon caution" : ""),
            "-e", "if gave up of r then return \"\"",
            "-e", "return button returned of r",
            "-e", "end run",
            text, title, defaultButton,
        };
        args.AddRange(buttons);
        var (code, so, _) = Proc.Run("/usr/bin/osascript", args, timeoutMs: (timeoutSec + 15) * 1000);
        string b = so.Trim();
        return code == 0 && b.Length > 0 ? b : null;
    }

    public static void Notify(string text, string title = "CO Resident")
    {
        try
        {
            Proc.Run("/usr/bin/osascript", new[]
            {
                "-e", "on run argv", "-e", "display notification (item 1 of argv) with title (item 2 of argv)", "-e", "end run", text, title,
            });
        }
        catch (Exception ex) { Log.Write("notification failed: " + ex.Message); }
    }

    public static void Open(string url) => Proc.Run("/usr/bin/open", new[] { url });
}

// Confirmations of agent actions, one dialog at a time; no answer within 2 minutes = declined.
public sealed class MacApprovalService : IApprovalService
{
    private readonly SemaphoreSlim _one = new(1, 1);
    public const int TimeoutSec = 120;

    public async Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            bool shell = request.Plan.Tool == "shell_run";
            bool release = request.Kind == ApprovalKind.ReleaseResult;
            var sb = new StringBuilder();
            sb.Append(shell ? "An AI Workbench agent wants to run a command on this Mac:\n\n"
                : release ? "An AI Workbench agent wants to receive this result:\n\n"
                : "An AI Workbench agent asks for permission:\n\n");
            sb.Append(request.Plan.Summary);
            if (!shell && request.Decision.Reason.Length > 0) sb.Append("\n\n").Append(request.Decision.Reason);
            if (request.Tainted) sb.Append("\n\n⚠ The agent has read untrusted content (web, mail) in this task.");
            if (request.ResultPreview is { Length: > 0 } pv) sb.Append("\n\n").Append(pv.Length > 600 ? pv[..600] + "…" : pv);
            sb.Append($"\n\nNo answer in {TimeoutSec / 60} min = Decline.");
            string allow = shell ? "Run" : "Allow once";
            string[] buttons = !shell && request.Decision.Grantable ? new[] { "Decline", "Allow for this task", allow } : new[] { "Decline", allow };
            Log.Write($"approval asked: {request.Call.Tool} run={request.Call.RunId}");
            string? b = await Task.Run(() => Dialog.Ask(sb.ToString(), "CO Resident — " + request.Call.Tool, buttons, "Decline", TimeoutSec), ct);
            var choice = b == allow ? ApprovalChoice.Allow : b == "Allow for this task" ? ApprovalChoice.AllowForTask : ApprovalChoice.Deny;
            Log.Write($"approval answer: {request.Call.Tool} run={request.Call.RunId} -> {choice}{(b == null ? " (timeout)" : "")}");
            return new ApprovalAnswer(choice, b == null ? "No answer at the computer within 2 minutes." : null);
        }
        finally { _one.Release(); }
    }
}
