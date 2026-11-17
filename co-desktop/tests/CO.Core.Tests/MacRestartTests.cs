// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Mac;

namespace CO.Core.Tests;

// CO Resident safe start (review 13.11): only crashes count, planned restarts do not (src/CO.Mac/MacRestartRules.cs).
public class MacRestartTests
{
    [Theory]
    [InlineData("last exit code = 1")]
    [InlineData("last exit code = -1")]
    [InlineData("last terminating signal = Abort trap: 6")]
    [InlineData("  last terminating signal = Killed: 9")]
    public void Crash_lines_count(string line) => Assert.True(MacRestartRules.Crashed(new[] { "runs = 4", line }));

    [Theory]
    [InlineData("last exit code = 0")]
    [InlineData("last exit code = (never exited)")]
    [InlineData("runs = 1")]
    public void Planned_exits_do_not_count(string line) => Assert.False(MacRestartRules.Crashed(new[] { line }));

    [Fact]
    public void No_launchd_info_is_not_a_crash() => Assert.False(MacRestartRules.Crashed(Array.Empty<string>()));
}
