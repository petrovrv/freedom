// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using CO.Core.Protocol;

namespace CO.Core.Host;

public sealed class AuditRecord
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("utc")] public string Utc { get; set; } = "";
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("argsSha256")] public string ArgsSha256 { get; set; } = "";
    [JsonPropertyName("decision")] public string Decision { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("resultSha256")] public string ResultSha256 { get; set; } = "";
    [JsonPropertyName("tainted")] public bool Tainted { get; set; }
    [JsonPropertyName("prevHash")] public string PrevHash { get; set; } = "";
}

// TZ §9.5.2: append-only JSONL; each line carries the SHA-256 of the previous line.
public sealed class AuditLog
{
    private readonly string _file;
    private readonly object _sync = new();
    private string _lastHash = "";
    private long _seq;

    public AuditLog(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        if (File.Exists(file))
        {
            string? last = File.ReadLines(file).LastOrDefault(l => l.Length > 0);
            if (last != null)
            {
                _lastHash = Wire.Sha256Hex(last);
                _seq = JsonSerializer.Deserialize<AuditRecord>(last)!.Seq;
            }
        }
    }

    public event Action<string>? LineAppended;

    public void Append(AuditRecord r)
    {
        lock (_sync)
        {
            r.Seq = ++_seq;
            r.Utc = Wire.Now(DateTime.UtcNow);
            r.PrevHash = _lastHash;
            string line = JsonSerializer.Serialize(r, Wire.Json);
            using (var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs))
                w.Write(line + "\n");
            _lastHash = Wire.Sha256Hex(line);
            LineAppended?.Invoke(line);
        }
    }

    // Returns the 1-based line number of the first break, or 0 when the chain is intact.
    public static long Verify(string file)
    {
        string prev = "";
        long n = 0;
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            n++;
            var r = JsonSerializer.Deserialize<AuditRecord>(line)!;
            if (r.PrevHash != prev) return n;
            prev = Wire.Sha256Hex(line);
        }
        return 0;
    }
}
