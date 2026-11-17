// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace CO.Mac;

// 08.11: 0.2.0 and 0.1.1 died with SIGABRT 1-4 s after "serving tool calls" for minutes after an install, and nothing
// reached resident.log (no managed exception). The reason is in macOS's crash report (.ips) and in resident.out; both are
// summarised into resident.log so that log_read finds them without shell_run.
public static class MacCrash
{
    private static string SeenFile => Path.Combine(Paths.DataDir, "crash-seen.txt");

    // Newest co-resident crash reports, newest first (user folder; the system one is usually not readable).
    public static IEnumerable<FileInfo> Reports()
    {
        var dirs = new[] { Path.Combine(Paths.Home, "Library", "Logs", "DiagnosticReports"), "/Library/Logs/DiagnosticReports" };
        var all = new List<FileInfo>();
        foreach (string d in dirs)
        {
            try
            {
                if (Directory.Exists(d))
                    all.AddRange(new DirectoryInfo(d).EnumerateFiles("co-resident*").Where(f => f.Extension is ".ips" or ".crash"));
            }
            catch (Exception) { }
        }
        return all.OrderByDescending(f => f.LastWriteTimeUtc);
    }

    // The part of an .ips report that names the cause: exception, termination reason, abort message, crashed thread frames.
    public static string Summarize(FileInfo f, int frames = 30)
    {
        var sb = new StringBuilder();
        sb.Append($"crash report {f.Name} ({f.LastWriteTimeUtc:O})");
        try
        {
            string text = File.ReadAllText(f.FullName);
            int nl = text.IndexOf('\n');
            if (nl < 0) return sb.Append(": ").Append(Cut(text, 2000)).ToString();
            using var doc = JsonDocument.Parse(text[(nl + 1)..]);
            var r = doc.RootElement;
            if (r.TryGetProperty("procLaunch", out var pl)) sb.Append($"; launched {pl}");
            if (r.TryGetProperty("captureTime", out var ct)) sb.Append($"; captured {ct}");
            if (r.TryGetProperty("exception", out var ex)) sb.Append("\n  exception: ").Append(ex.GetRawText());
            if (r.TryGetProperty("termination", out var te)) sb.Append("\n  termination: ").Append(Cut(te.GetRawText(), 1500));
            if (r.TryGetProperty("asi", out var asi)) sb.Append("\n  asi: ").Append(Cut(asi.GetRawText(), 1500));
            if (r.TryGetProperty("ktriageinfo", out var kt)) sb.Append("\n  ktriage: ").Append(Cut(kt.GetRawText(), 500));
            var images = new List<string>();
            if (r.TryGetProperty("usedImages", out var ui) && ui.ValueKind == JsonValueKind.Array)
                foreach (var im in ui.EnumerateArray())
                    images.Add(im.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?");
            int faulting = r.TryGetProperty("faultingThread", out var ft) && ft.TryGetInt32(out int fi) ? fi : -1;
            if (r.TryGetProperty("threads", out var th) && th.ValueKind == JsonValueKind.Array && faulting >= 0 && faulting < th.GetArrayLength())
            {
                var t = th[faulting];
                sb.Append($"\n  crashed thread {faulting}");
                if (t.TryGetProperty("name", out var tn)) sb.Append($" '{tn.GetString()}'");
                if (t.TryGetProperty("queue", out var tq)) sb.Append($" queue {tq.GetString()}");
                if (t.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var frame in fr.EnumerateArray())
                    {
                        if (i++ >= frames) { sb.Append("\n    …"); break; }
                        string img = frame.TryGetProperty("imageIndex", out var ii) && ii.TryGetInt32(out int ix) && ix >= 0 && ix < images.Count ? images[ix] : "?";
                        string sym = frame.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                        string off = frame.TryGetProperty("imageOffset", out var io) ? io.GetRawText() : "";
                        sb.Append($"\n    {i - 1,2} {img} {(sym.Length > 0 ? sym : "+" + off)}");
                    }
                }
            }
        }
        catch (Exception e) { sb.Append(" (not parsed: ").Append(e.Message).Append(')'); }
        return sb.ToString();
    }

    // Last lines of resident.out (the .NET runtime writes unhandled exceptions and "Stack overflow" there).
    public static string OutTail(int lines = 40)
    {
        string file = Path.Combine(Paths.LogDir, "resident.out");
        try { return File.Exists(file) ? string.Join("\n", File.ReadLines(file).TakeLast(lines)) : ""; }
        catch (Exception e) { return "(resident.out not readable: " + e.Message + ")"; }
    }

    // Writes crash reports newer than the last one written before, and the resident.out tail if there was a new one.
    // Returns the reports newer than sinceUtc, newest first. waitSec: setup waits for ReportCrash, which writes the report a
    // few seconds after the process died. ignoreSeen (setup): the resident that launchd restarted meanwhile may already have
    // logged the report and moved crash-seen.txt past it; setup still gets it back (for the Terminal) but does not log it twice.
    public static List<FileInfo> LogNew(DateTime sinceUtc, int waitSec = 0, int max = 2, bool ignoreSeen = false)
    {
        var until = DateTime.UtcNow.AddSeconds(waitSec);
        List<FileInfo> fresh;
        DateTime seen;
        while (true)
        {
            // Only reports newer than the newest one already written (its write time is kept in crash-seen.txt).
            seen = Seen();
            var from = ignoreSeen || seen <= sinceUtc ? sinceUtc : seen;
            fresh = Reports().Where(f => f.LastWriteTimeUtc > from).Take(max).ToList();
            if (fresh.Count > 0 || DateTime.UtcNow >= until) break;
            Thread.Sleep(2000);
        }
        var unlogged = fresh.Where(f => f.LastWriteTimeUtc > seen).ToList();
        if (unlogged.Count == 0) return fresh;
        foreach (var f in unlogged) Log.Write(Summarize(f));
        string tail = OutTail();
        if (tail.Length > 0) Log.Write("resident.out (last lines):\n" + tail);
        // Only forward: setup and the resident may both get here for the same report.
        try { if (unlogged[0].LastWriteTimeUtc > Seen()) File.WriteAllText(SeenFile, unlogged[0].LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception) { }
        return fresh;
    }

    private static DateTime Seen()
    {
        try { if (File.Exists(SeenFile) && long.TryParse(File.ReadAllText(SeenFile).Trim(), out long t)) return new DateTime(t, DateTimeKind.Utc); }
        catch (Exception) { }
        return DateTime.MinValue;
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
