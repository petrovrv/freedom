// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Xunit.Runners;

namespace CO.MacCheck;

// Runs CO.Core.Tests in this process and prints one JSON summary line; with "--out FILE" it also writes it there
// atomically (the mac_check tool of the resident starts this detached and reads FILE).
// Exit code: 0 all passed, 1 failures, 2 the runner itself failed.
public static class Program
{
    private const int MaxFailures = 60;
    private const int MaxMessage = 400;

    public static int Main(string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "CO.Core.Tests.dll");
        var failures = new List<object>();
        int total = 0, failed = 0, skipped = 0;
        decimal seconds = 0;
        string? error = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            using var runner = AssemblyRunner.WithoutAppDomain(dll);
            runner.OnTestFailed = i =>
            {
                lock (failures)
                    if (failures.Count < MaxFailures)
                        failures.Add(new { test = i.TestDisplayName, message = Cut(i.ExceptionType + ": " + i.ExceptionMessage) });
            };
            runner.OnErrorMessage = i => error = Cut(i.ExceptionType + ": " + i.ExceptionMessage);
            runner.OnExecutionComplete = i =>
            {
                total = i.TotalTests;
                failed = i.TestsFailed;
                skipped = i.TestsSkipped;
                seconds = i.ExecutionTime;
                done.Set();
            };
            runner.Start(parallel: false);
            done.Wait();
            // Dispose throws while the runner is still winding down.
            while (runner.Status != AssemblyRunnerStatus.Idle) Thread.Sleep(100);
        }
        catch (Exception ex)
        {
            Emit(args, JsonSerializer.Serialize(new { ok = false, error = Cut(ex.GetType().Name + ": " + ex.Message) }));
            return 2;
        }
        Emit(args, JsonSerializer.Serialize(new
        {
            ok = failed == 0 && error == null,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            total, passed = total - failed - skipped, failed, skipped, seconds = Math.Round(seconds, 1),
            error, failures,
        }));
        return failed == 0 && error == null ? 0 : 1;
    }

    private static void Emit(string[] args, string json)
    {
        Console.WriteLine(json);
        int i = Array.IndexOf(args, "--out");
        if (i < 0 || i + 1 >= args.Length) return;
        File.WriteAllText(args[i + 1] + ".tmp", json);
        File.Move(args[i + 1] + ".tmp", args[i + 1], overwrite: true);
    }

    private static string Cut(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= MaxMessage ? s : s[..MaxMessage] + "…";
    }
}
