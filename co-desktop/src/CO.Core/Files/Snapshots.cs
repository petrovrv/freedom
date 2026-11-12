// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Protocol;

namespace CO.Core.Files;

// TZ §10: a copy of every file is taken before a W/D action; a Run or one step can be undone
// byte for byte. Layout: <root>/<runId>/journal.jsonl + <runId>/<step>-<n>.bin

public sealed class SnapshotEntry
{
    public int Step { get; set; }
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "file";     // file | dir
    public bool Existed { get; set; }
    public string? Blob { get; set; }              // file name inside the run folder
    public string? Sha256 { get; set; }
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

public sealed class SnapshotStore
{
    private readonly string _root;
    private readonly object _sync = new();
    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);

    public SnapshotStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public int BeginStep(string runId)
    {
        lock (_sync)
        {
            var j = Journal(runId);
            return j.Count == 0 ? 1 : j.Max(e => e.Step) + 1;
        }
    }

    // Throws when the copy cannot be made; the caller must then refuse the action (TZ §10.1).
    public void CaptureFile(string runId, int step, string path)
    {
        lock (_sync)
        {
            string dir = RunDir(runId);
            Directory.CreateDirectory(dir);
            var e = new SnapshotEntry { Step = step, Path = path, Kind = "file", Existed = File.Exists(path) };
            if (e.Existed)
            {
                long len = new FileInfo(path).Length;
                if (TotalBytes() + len > MaxBytes)
                {
                    Prune(len);
                    if (TotalBytes() + len > MaxBytes)
                        throw new IOException("Snapshot store is full; the action is refused.");
                }
                e.Blob = $"{step}-{Guid.NewGuid():N}.bin";
                string blob = System.IO.Path.Combine(dir, e.Blob);
                File.Copy(path, blob);
                e.Sha256 = Wire.Sha256Hex(File.ReadAllBytes(blob));
            }
            Append(runId, e);
        }
    }

    public void CaptureDir(string runId, int step, string path)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(RunDir(runId));
            Append(runId, new SnapshotEntry { Step = step, Path = path, Kind = "dir", Existed = Directory.Exists(path) });
        }
    }

    public IReadOnlyList<SnapshotEntry> Journal(string runId)
    {
        string f = System.IO.Path.Combine(RunDir(runId), "journal.jsonl");
        if (!File.Exists(f)) return Array.Empty<SnapshotEntry>();
        return File.ReadAllLines(f).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<SnapshotEntry>(l)!).ToList();
    }

    public int RevertRun(string runId)
    {
        lock (_sync)
        {
            var steps = Journal(runId).Select(e => e.Step).Distinct().OrderByDescending(s => s).ToList();
            foreach (int s in steps) RevertStepLocked(runId, s);
            return steps.Count;
        }
    }

    public void RevertStep(string runId, int step)
    {
        lock (_sync) RevertStepLocked(runId, step);
    }

    private void RevertStepLocked(string runId, int step)
    {
        // Within a step, restore in reverse capture order; the earliest capture of a path wins.
        var entries = Journal(runId).Where(e => e.Step == step).Reverse().ToList();
        foreach (var e in entries)
        {
            if (e.Kind == "dir")
            {
                if (!e.Existed && Directory.Exists(e.Path) && !Directory.EnumerateFileSystemEntries(e.Path).Any())
                    Directory.Delete(e.Path);
                continue;
            }
            if (e.Existed)
            {
                string blob = System.IO.Path.Combine(RunDir(runId), e.Blob!);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(e.Path)!);
                File.Copy(blob, e.Path, true);
                string sha = Wire.Sha256Hex(File.ReadAllBytes(e.Path));
                if (sha != e.Sha256) throw new IOException($"Restore of {e.Path} does not match snapshot hash.");
            }
            else if (File.Exists(e.Path))
            {
                // The file did not exist before this step: keep the agent's version aside, then remove it.
                string aside = System.IO.Path.Combine(RunDir(runId), $"reverted-{step}-{Guid.NewGuid():N}.bin");
                File.Move(e.Path, aside);
            }
        }
    }

    public void Prune(long needBytes = 0)
    {
        lock (_sync)
        {
            if (!Directory.Exists(_root)) return;
            var runs = new DirectoryInfo(_root).GetDirectories().OrderBy(d => d.LastWriteTimeUtc).ToList();
            foreach (var d in runs.Where(d => DateTime.UtcNow - d.LastWriteTimeUtc > MaxAge).ToList())
            {
                d.Delete(true);
                runs.Remove(d);
            }
            foreach (var d in runs)
            {
                if (TotalBytes() + needBytes <= MaxBytes) break;
                d.Delete(true);
            }
        }
    }

    private long TotalBytes() => !Directory.Exists(_root) ? 0
        : new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private string RunDir(string runId)
    {
        foreach (char c in runId)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("Bad runId.");
        return System.IO.Path.Combine(_root, runId);
    }

    private void Append(string runId, SnapshotEntry e) =>
        File.AppendAllText(System.IO.Path.Combine(RunDir(runId), "journal.jsonl"), JsonSerializer.Serialize(e) + "\n");
}
