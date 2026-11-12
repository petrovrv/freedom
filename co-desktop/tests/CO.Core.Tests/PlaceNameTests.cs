// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Files;
using CO.Core.Policy;
using Xunit;

namespace CO.Core.Tests;

public class PlaceNameTests
{
    [Fact]
    public void SyncedFoldersSayWhatTheyBelongTo()
    {
        var roots = new[] { @"C:\Users\X\OneDrive - Contoso", @"C:\Users\X\Amentum (CMS)\EEX - Documents", @"G:\" };
        Assert.Equal("OneDrive", PlaceNames.SyncedKind(@"C:\Users\X\OneDrive - Contoso\Actuators", roots));
        Assert.Equal("Library", PlaceNames.SyncedKind(@"C:\Users\X\Amentum (CMS)\EEX - Documents", roots));
        Assert.Equal("Google Drive", PlaceNames.SyncedKind(@"G:\My Drive\AI-Workbench", roots));
        Assert.Null(PlaceNames.SyncedKind(@"C:\Boring", roots));
        Assert.Equal("Amentum (CMS) › EEX - Documents", PlaceNames.Tail(@"C:\Users\X\Amentum (CMS)\EEX - Documents"));
    }

    [Fact]
    public async Task RenamedFolderAnswersToTheNewName()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Work, "a.txt"), "x");
        f.Folders.Rename("work", "proj-neo");
        Assert.True((await f.Run("fs_read", new { path = "proj-neo/a.txt" })).Ok);
        Assert.Equal("outside_scope", (await f.Run("fs_read", new { path = "work/a.txt" })).Error!.Code);
        Assert.Throws<ArgumentException>(() => f.Folders.Rename("proj-neo", "inbox"));       // taken
        Assert.Throws<ArgumentException>(() => f.Folders.Rename("proj-neo", "Bad Name!"));
    }
}
