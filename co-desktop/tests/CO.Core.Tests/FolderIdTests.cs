// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Files;
using CO.Core.Tools;
using Xunit;

namespace CO.Core.Tests;

public class FolderIdTests
{
    [Theory]
    [InlineData(null, @"C:\Data\Amentum Library", "amentum-libr")]
    [InlineData("  My Docs!! ", @"C:\x", "my-docs")]
    [InlineData("SP", @"C:\x", "sp")]
    [InlineData(null, @"C:\Документы", "dokumenty")]
    [InlineData("???", @"C:\x", "folder")]
    public void MakesValidIds(string? typed, string path, string expected)
    {
        var reg = new FolderRegistry();
        string id = reg.MakeId(typed, path.Replace('\\', Path.DirectorySeparatorChar));
        Assert.Equal(expected, id);
        Assert.True(ToolNames.IsValidShort(id.Replace("-", "")));
    }

    [Fact]
    public void MakesUniqueIds()
    {
        using var f = new Fixture();
        string id = f.Folders.MakeId("work", f.Work);
        Assert.Equal("work2", id);
        string longId = f.Folders.MakeId("abcdefghijkl", f.Work);
        Directory.CreateDirectory(Path.Combine(f.Root, "l"));
        f.Folders.Add(Path.Combine(f.Root, "l"), longId, false, false, false);
        Assert.Equal("abcdefghijk2", f.Folders.MakeId("abcdefghijkl", f.Work));
    }
}
