// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace CO.Mac;

// How the previous resident process ended, from the "last ..." lines of 'launchctl print' (pure, tested in CO.Core.Tests).
public static class MacRestartRules
{
    private static readonly Regex ExitCode = new(@"^last exit code = (-?\d+)");

    // "last exit code = 1" or "last terminating signal = Abort trap: 6" after a crash; "last exit code = 0" or
    // "(never exited)" after a planned exit (setup, sign-in, update swap, 'folders add') or a fresh bootstrap.
    public static bool Crashed(IEnumerable<string> launchdLines)
    {
        foreach (string raw in launchdLines)
        {
            string l = raw.Trim();
            if (l.StartsWith("last terminating signal", StringComparison.Ordinal)) return true;
            var m = ExitCode.Match(l);
            if (m.Success && m.Groups[1].Value != "0") return true;
        }
        return false;
    }
}
