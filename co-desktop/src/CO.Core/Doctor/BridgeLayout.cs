// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace CO.Core.Doctor;

// S3 (11.11): where Bridge lives on this computer, read from the launcher and Bridge files only (no Site, no Bridge).
//   <ProgramsDir>\AIWorkbenchBridge.exe, launcher.json, state.json, versions\<v>\BridgeAgent.exe
//   <StateRoot>\config.json, logs\<date>.log, logs\launcher.log, drain.request
//   <DriveRoot>\transport\hosts\<HostId>\owner.json, inbox\, claimed\, cancel\
public sealed class BridgeLayout
{
    public required string ProgramsDir { get; init; }
    public required string StateRoot { get; init; }
    public string ConfigPath => Path.Combine(StateRoot, "config.json");
    public string HostId { get; init; } = "home-main";
    public string DriveRoot { get; init; } = "";
    public string DbUrl { get; init; } = "";
    public string PushTarget { get; init; } = "aiwb/co-push";
    public int MaxProcessMinutes { get; init; } = 240;
    public string ClaudeExe { get; init; } = "";
    public string CodexExe { get; init; } = "";

    public string LauncherExe => Path.Combine(ProgramsDir, "AIWorkbenchBridge.exe");
    public string LauncherStatePath => Path.Combine(ProgramsDir, "state.json");
    public string VersionsDir => Path.Combine(ProgramsDir, "versions");
    public string BridgeExe(string version) => Path.Combine(VersionsDir, version, "BridgeAgent.exe");
    public string LogsDir => Path.Combine(StateRoot, "logs");
    public string LauncherLog => Path.Combine(LogsDir, "launcher.log");
    public string DrainPath => Path.Combine(StateRoot, "drain.request");
    public string HostDir => Path.Combine(DriveRoot, "transport", "hosts", HostId);
    public string OwnerJson => Path.Combine(HostDir, "owner.json");

    public const string AutostartTask = "AIWB-BridgeAutostart";

    public static string DefaultProgramsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AIWorkbenchBridge");

    // null when Bridge (its launcher) is not installed on this computer: the doctor is off.
    public static BridgeLayout? Load(string? programsDir = null)
    {
        programsDir ??= DefaultProgramsDir;
        if (!File.Exists(Path.Combine(programsDir, "launcher.json"))) return null;
        string stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkbenchBridge");
        string configPath = "";
        try
        {
            using var l = Parse(Path.Combine(programsDir, "launcher.json"));
            if (Str(l.RootElement, "stateRoot") is { Length: > 0 } sr) stateRoot = Environment.ExpandEnvironmentVariables(sr);
            if (Str(l.RootElement, "configPath") is { Length: > 0 } cp) configPath = Environment.ExpandEnvironmentVariables(cp);
        }
        catch (Exception) { /* defaults */ }
        if (configPath.Length == 0) configPath = Path.Combine(stateRoot, "config.json");

        string host = "home-main", drive = "", db = "", push = "aiwb/co-push", claude = "", codex = "";
        int maxMin = 240;
        try
        {
            using var c = Parse(configPath);
            var r = c.RootElement;
            host = Str(r, "hostId") is { Length: > 0 } h ? h : host;
            drive = Environment.ExpandEnvironmentVariables(Str(r, "driveRoot") ?? "");
            if (r.TryGetProperty("dbQueue", out var q) && q.ValueKind == JsonValueKind.Object) db = Str(q, "url") ?? "";
            if (r.TryGetProperty("push", out var p) && p.ValueKind == JsonValueKind.Object && Str(p, "credentialTarget") is { Length: > 0 } t) push = t;
            if (r.TryGetProperty("maxProcessTimeoutMinutes", out var m) && m.TryGetInt32(out int mm) && mm > 0) maxMin = mm;
            if (r.TryGetProperty("agents", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                if (a.TryGetProperty("claude", out var ca) && ca.ValueKind == JsonValueKind.Object) claude = Str(ca, "executable") ?? "";
                if (a.TryGetProperty("codex", out var xa) && xa.ValueKind == JsonValueKind.Object) codex = Str(xa, "executable") ?? "";
            }
        }
        catch (Exception) { /* config unreadable: the check reports it */ }
        return new BridgeLayout
        {
            ProgramsDir = programsDir, StateRoot = stateRoot, HostId = host, DriveRoot = drive, DbUrl = db, PushTarget = push,
            MaxProcessMinutes = maxMin, ClaudeExe = claude, CodexExe = codex,
        };
    }

    internal static JsonDocument Parse(string path) =>
        JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
