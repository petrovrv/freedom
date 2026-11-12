// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Files;
using CO.Core.Policy;
using Xunit;

namespace CO.Core.Tests;

public class FolderRightsTests
{
    [Fact]
    public void OldWritableBecomesReadCreateEdit()
    {
        string file = Path.Combine(Path.GetTempPath(), "co-folders-" + Guid.NewGuid().ToString("N") + ".json");
        string dir = Directory.CreateTempSubdirectory("co-old").FullName;
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new[]
            {
                new { Id = "rw", Path = dir, Writable = true, Synced = false, Project = false },
                new { Id = "ro", Path = dir + "x", Writable = false, Synced = false, Project = false },
            }));
            var reg = new FolderRegistry(file);
            Assert.Equal(Rights.Read | Rights.Create | Rights.Edit, reg.Find("rw")!.Granted);
            Assert.Equal(Rights.Read, reg.Find("ro")!.Granted);
        }
        finally { File.Delete(file); Directory.Delete(dir, true); }
    }

    [Fact]
    public void DamagedEntriesDoNotStopTheStart()
    {
        string file = Path.Combine(Path.GetTempPath(), "co-folders-" + Guid.NewGuid().ToString("N") + ".json");
        string dir = Directory.CreateTempSubdirectory("co-dmg").FullName;
        try
        {
            // PowerShell 5.1 wraps an array into {"value": [...], "Count": n} when it is re-serialized carelessly.
            File.WriteAllText(file, "[{\"value\":[],\"Count\":0},{\"Id\":\"ok\",\"Path\":" + JsonSerializer.Serialize(dir) + ",\"Rules\":null}]");
            var reg = new FolderRegistry(file);
            Assert.Equal("ok", Assert.Single(reg.All).Id);
            reg.Add(Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName, "sub", Rights.Read, project: false);

            File.WriteAllText(file, "{ not json");
            Assert.Empty(new FolderRegistry(file).All);
            Assert.True(File.Exists(file + ".bad"));
        }
        finally { File.Delete(file); File.Delete(file + ".bad"); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task BoxesAreChangedInPlace()
    {
        using var f = new Fixture();
        Assert.Equal("read_only", (await f.Run("fs_write", new { path = "inbox/a.txt", content = "x" })).Error!.Code);
        f.Folders.SetRights("inbox", null, Rights.Read | Rights.Create);
        Assert.True((await f.Run("fs_write", new { path = "inbox/a.txt", content = "x" })).Ok);
        // Create without Edit: a new file yes, overwriting no.
        var over = await f.Run("fs_write", new { path = "inbox/a.txt", content = "y", overwrite = true });
        Assert.Equal("not_permitted", over.Error!.Code);
        Assert.Contains("edit", over.Error.Message);
    }

    [Fact]
    public async Task SubfolderBecomesARuleAndTheDeepestRuleWins()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Mail, "Drafts", "Old"));
        var parent = f.Folders.Add(Path.Combine(f.Mail, "Drafts"), "drafts", Rights.Read, project: false);
        Assert.Equal("inbox", parent.Id);                        // no second path to the same files
        Assert.Null(f.Folders.Find("drafts"));
        f.Folders.SetRights("inbox", "Drafts", Rights.Read | Rights.Create | Rights.Edit);
        f.Folders.Add(Path.Combine(f.Mail, "Drafts", "Old"), "old", Rights.Read, project: false);
        f.Folders.SetRights("inbox", "Drafts/Old", Rights.Read);

        Assert.True((await f.Run("fs_write", new { path = "inbox/Drafts/n.txt", content = "x" })).Ok);
        Assert.Equal("read_only", (await f.Run("fs_write", new { path = "inbox/Drafts/Old/n.txt", content = "x" })).Error!.Code);
        Assert.Equal("read_only", (await f.Run("fs_write", new { path = "inbox/n.txt", content = "x" })).Error!.Code);

        var list = await f.Run("fs_list", new { path = "" });
        Assert.Contains("inbox/Drafts", list.Content);
    }

    [Fact]
    public async Task UnreadableRuleHidesItsFiles()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Work, "hr"));
        File.WriteAllText(Path.Combine(f.Work, "hr", "salary.txt"), "secret budget");
        File.WriteAllText(Path.Combine(f.Work, "plan.txt"), "budget plan");
        f.Folders.Add(Path.Combine(f.Work, "hr"), "hr", Rights.Read, project: false);
        f.Folders.SetRights("work", "hr", Rights.None);
        Assert.Equal("not_permitted", (await f.Run("fs_read", new { path = "work/hr/salary.txt" })).Error!.Code);
        var s = await f.Run("fs_search", new { path = "work", query = "budget" });
        Assert.Contains("plan.txt", s.Content);
        Assert.DoesNotContain("salary", s.Content);
    }

    [Fact]
    public void NewParentTakesInConnectedChildren()
    {
        string root = Directory.CreateTempSubdirectory("co-parent").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            var reg = new FolderRegistry();
            reg.Add(Path.Combine(root, "a"), "a", Rights.Read | Rights.Create, project: false);
            reg.Add(Path.Combine(root, "b"), "b", Rights.Read, project: false);
            reg.Add(root, "root", Rights.Read, project: false);
            var r = Assert.Single(reg.All);
            Assert.Equal("root", r.Id);
            var rule = Assert.Single(r.Rules);                    // b had the parent's rights: dropped
            Assert.Equal("a", rule.Path);
            Assert.Equal(Rights.Read | Rights.Create, r.RightsAt("a/x.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TickedDeleteWorksOnManagedDevices()
    {
        using var f = new Fixture(managed: true);
        File.WriteAllText(Path.Combine(f.Work, "old.txt"), "x");
        f.Folders.SetRights("work", null, Rights.All);
        var r = await f.Run("fs_delete", new { paths = new[] { "work/old.txt" } });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Empty(f.Approvals.Seen);
        Assert.False(File.Exists(Path.Combine(f.Work, "old.txt")));
    }
}
