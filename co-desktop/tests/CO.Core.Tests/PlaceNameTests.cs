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
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { @"C:\Users\X\OneDrive - Contoso", @"C:\Users\X\Amentum (CMS)\EEX - Documents", @"G:\" };
            Assert.Equal("OneDrive", PlaceNames.SyncedKind(@"C:\Users\X\OneDrive - Contoso\Actuators", roots));
            Assert.Equal("Library", PlaceNames.SyncedKind(@"C:\Users\X\Amentum (CMS)\EEX - Documents", roots));
            Assert.Equal("Google Drive", PlaceNames.SyncedKind(@"G:\My Drive\AI-Workbench", roots));
            Assert.Null(PlaceNames.SyncedKind(@"C:\Boring", roots));
        }
        else
        {
            // macOS File Provider folders (the Mac check runs these tests on the Mac; Linux runs them as its proxy)
            const string cs = "/Users/x/Library/CloudStorage/";
            var roots = new[] { cs + "OneDrive-Contoso", cs + "SharePoint-EEX - Documents", cs + "GoogleDrive-x@example.org/My Drive" };
            Assert.Equal("OneDrive", PlaceNames.SyncedKind(cs + "OneDrive-Contoso/Actuators", roots));
            Assert.Equal("Library", PlaceNames.SyncedKind(cs + "SharePoint-EEX - Documents", roots));
            Assert.Equal("Google Drive", PlaceNames.SyncedKind(cs + "GoogleDrive-x@example.org/My Drive/AI-Workbench", roots));
            Assert.Null(PlaceNames.SyncedKind("/Users/x/Boring", roots));
        }
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
