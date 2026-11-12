// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace CO.Core.Doctor;

// S3: the doctor asks the local CLI agent (claude or codex of this computer's subscription) directly, without the Site
// and without Bridge. The agent only reads (Read/Grep/Glob for Claude, a read-only sandbox for Codex) in the Bridge
// state folder; repairs stay with the doctor's buttons.
public static class DoctorAi
{
    public static string Prompt(DoctorReport report, BridgeLayout l, string question)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the doctor of AI Workbench Bridge on this Windows computer, started by CO Desktop.");
        sb.AppendLine("Bridge is a tray agent that takes jobs from the CO site database and Google Drive and runs Claude Code / Codex CLI.");
        sb.AppendLine("Its launcher (AIWorkbenchBridge.exe) starts versions\\<v>\\BridgeAgent.exe, rolls back a version that dies in 120 s,");
        sb.AppendLine("and restarts Bridge for an update through drain.request (Bridge exits with code 3 when no job runs).");
        sb.AppendLine($"Files: state folder {l.StateRoot} (config.json, logs\\<date>.log, logs\\launcher.log, drain.request, supervisor.json);");
        sb.AppendLine($"launcher {l.ProgramsDir} (state.json, versions\\); host transport {l.HostDir} (owner.json = heartbeat, inbox\\, claimed\\).");
        sb.AppendLine("Rules: only read files; never print secrets from config.json or elsewhere; do not stop or start processes.");
        sb.AppendLine("The doctor can apply these repairs on the owner's button: start_launcher, restart (graceful drain),");
        sb.AppendLine("rollback (previous version), cancel_stuck (cancel markers), hard_restart (kills Bridge and its running jobs).");
        sb.AppendLine("Answer in Russian, short: what is wrong, the likely cause with evidence (log lines), and what to do.");
        sb.AppendLine("If one of the repairs helps, end with a line 'REPAIR: <id>'.");
        sb.AppendLine();
        sb.AppendLine(report.ToText());
        sb.AppendLine("Last lines of the Bridge log:");
        string? log = null;
        try
        {
            log = new DirectoryInfo(l.LogsDir).EnumerateFiles("20*.log").OrderByDescending(f => f.Name).FirstOrDefault()?.FullName;
        }
        catch (Exception) { }
        if (log != null) foreach (string line in BridgeDoctor.LastLines(log, 60)) sb.AppendLine(BridgeDoctor.Cut(line, 400));
        sb.AppendLine("Last lines of launcher.log:");
        foreach (string line in BridgeDoctor.LastLines(l.LauncherLog, 15)) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("Question of the owner: " + (string.IsNullOrWhiteSpace(question) ? "Is Bridge healthy? If not, why and how to fix it?" : question.Trim()));
        return sb.ToString();
    }

    public static (string Exe, string Args) Command(string agent, string exe, string workDir, string lastMessageFile) => agent switch
    {
        "claude" => (exe, "-p --output-format text --permission-mode default --allowedTools Read,Grep,Glob"),
        "codex" => (exe, $"exec --sandbox read-only --skip-git-repo-check --color never -C \"{workDir}\" --output-last-message \"{lastMessageFile}\" -"),
        _ => throw new ArgumentException("unknown agent " + agent),
    };

    // Runs the CLI with the prompt on stdin; returns its answer. Kills only the process tree it started itself.
    public static async Task<string> AskAsync(string agent, string exe, string prompt, BridgeLayout l, TimeSpan timeout, CancellationToken ct)
    {
        string last = Path.Combine(Path.GetTempPath(), "co-doctor-" + Guid.NewGuid().ToString("N") + ".txt");
        var (file, args) = Command(agent, exe, l.StateRoot, last);
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = Directory.Exists(l.StateRoot) ? l.StateRoot : Path.GetTempPath(),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException($"{agent} did not answer in {(int)timeout.TotalMinutes} min");
        }
        string output = await stdout.ConfigureAwait(false);
        string err = await stderr.ConfigureAwait(false);
        try
        {
            if (File.Exists(last))
            {
                output = await File.ReadAllTextAsync(last, ct).ConfigureAwait(false);
                File.Delete(last);
            }
        }
        catch (IOException) { }
        if (p.ExitCode != 0 && output.Trim().Length == 0)
            throw new InvalidOperationException($"{agent} exited with code {p.ExitCode}: {BridgeDoctor.Cut(err.Trim(), 400)}");
        return output.Trim();
    }

    // "REPAIR: <id>" lines of an answer that name a known repair.
    public static List<string> Repairs(string answer) =>
        answer.Split('\n').Select(x => x.Trim())
            .Where(x => x.StartsWith("REPAIR:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[7..].Trim().Trim('`', '*', '.').ToLowerInvariant())
            .Where(x => x is DoctorFix.StartLauncher or DoctorFix.Restart or DoctorFix.Rollback or DoctorFix.CancelStuck or DoctorFix.HardRestart)
            .Distinct().ToList();
}
