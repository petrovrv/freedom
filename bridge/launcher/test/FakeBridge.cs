// SPDX-License-Identifier: AGPL-3.0-or-later
// Stand-in for BridgeAgent.exe in launcher tests. Version = name of its folder. Markers in the config folder:
//   crash-<v>  exit 1 after 2 s;  invalid-<v>  --validate-config fails;  stop  exit 0 (user Exit).
// drain.request -> exit 3, like Bridge >= 1.1.7 with no job running.
using System;
using System.IO;
using System.Threading;

internal static class FakeBridge
{
    private static int Main(string[] args)
    {
        string v = Path.GetFileName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
        int i = Array.IndexOf(args, "--config");
        string dir = Path.GetDirectoryName(args[i + 1]);
        string log = Path.Combine(dir, "fake.log");
        if (Array.IndexOf(args, "--validate-config") >= 0)
        {
            bool ok = !File.Exists(Path.Combine(dir, "invalid-" + v));
            File.WriteAllText(Path.Combine(dir, "config-check.txt"), ok ? "OK" : "ERROR: test");
            return ok ? 0 : 1;
        }
        File.AppendAllText(log, "start " + v + Environment.NewLine);
        DateTime t0 = DateTime.UtcNow;
        while (true)
        {
            Thread.Sleep(500);
            if (File.Exists(Path.Combine(dir, "crash-" + v)) && (DateTime.UtcNow - t0).TotalSeconds > 2) { File.AppendAllText(log, "crash " + v + Environment.NewLine); return 1; }
            if (File.Exists(Path.Combine(dir, "drain.request"))) { File.AppendAllText(log, "drained " + v + Environment.NewLine); return 3; }
            if (File.Exists(Path.Combine(dir, "stop"))) { File.AppendAllText(log, "stop " + v + Environment.NewLine); return 0; }
        }
    }
}
