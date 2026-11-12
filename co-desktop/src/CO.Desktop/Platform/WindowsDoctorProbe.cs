// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CO.Core.Doctor;

namespace CO.Desktop.Platform;

// S3: Windows side of the doctor — processes, Defender detections, the logon task of Bridge.
public sealed class WindowsDoctorProbe : IDoctorProbe
{
    private readonly HttpClient _http;

    public WindowsDoctorProbe(HttpClient http) => _http = http;

    public IReadOnlyList<ProcInfo> Processes(string name)
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                string path = "";
                DateTime start = default;
                try
                {
                    path = p.MainModule?.FileName ?? "";
                    start = p.StartTime.ToUniversalTime();
                }
                catch (Exception) { /* another user's process or access denied: path unknown, never ours */ }
                list.Add(new ProcInfo(p.Id, path, start));
            }
        }
        return list;
    }

    // Get-MpThreatDetection works without elevation; the doctor reads it through Windows PowerShell.
    public IReadOnlyList<ThreatInfo> Detections()
    {
        const string script = "$ErrorActionPreference='Stop'; @(Get-MpThreatDetection | ForEach-Object { [pscustomobject]@{ utc = $_.InitialDetectionTime.ToUniversalTime().ToString('o'); res = ($_.Resources -join ';') } }) | ConvertTo-Json -Compress";
        string output = Run(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"", 30, out int code, out string err);
        if (code != 0) throw new InvalidOperationException(err.Trim().Length > 0 ? err.Trim() : "powershell exit " + code);
        var list = new List<ThreatInfo>();
        output = output.Trim();
        if (output.Length == 0) return list;
        using var doc = JsonDocument.Parse(output);
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : new[] { doc.RootElement };
        foreach (var e in items)
        {
            string utc = e.TryGetProperty("utc", out var u) ? u.GetString() ?? "" : "";
            string res = e.TryGetProperty("res", out var r) ? r.GetString() ?? "" : "";
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
                list.Add(new ThreatInfo(t, res));
        }
        return list;
    }

    public string RunTask(string name)
    {
        Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Run /TN \"" + name + "\"", 30, out int code, out string err);
        if (code != 0) throw new InvalidOperationException("schtasks exit " + code + " " + err.Trim());
        return "task " + name + " started";
    }

    public void Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();

    public void Kill(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: false);
        p.WaitForExit(10000);
    }

    public async Task<bool> SiteUpAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        return (int)resp.StatusCode < 500;
    }

    public string? FindOnPath(string exe)
    {
        if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        // CLI installers put themselves here; CO Desktop may have started before PATH was updated.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (string d in dirs)
        {
            try
            {
                string full = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(full)) return full;
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full + ".exe")) return full + ".exe";
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string Run(string file, string args, int seconds, out int code, out string err)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(seconds * 1000))
        {
            try { p.Kill(); } catch (Exception) { }
            throw new TimeoutException(Path.GetFileName(file) + " did not finish in " + seconds + " s");
        }
        code = p.ExitCode;
        err = e.Result;
        return o.Result;
    }
}
