// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Corporate.SharePoint;
using Xunit;

namespace CO.Corporate.Tests;

public class SharePointNameTests
{
    [Theory]
    [InlineData("https://engie.sharepoint.com/sites/BelgianNewBuild/Shared Documents/Specs", "BelgianNewBuild › Specs", "Specs")]
    [InlineData("https://engie.sharepoint.com/sites/BelgianNewBuild/Shared Documents", "BelgianNewBuild", "BelgianNewBuild")]
    [InlineData("https://amentum.sharepoint.com/teams/EEX/Gedeelde documenten/A/B/C", "EEX › A › … › C", "C")]
    [InlineData("https://corp-my.sharepoint.com/personal/anna_smith_corp_example/Documents/Notes", "OneDrive › Notes", "Notes")]
    public void SharePointRowsAreNamedBySiteAndFolder(string url, string title, string idSource)
    {
        Assert.Equal(title, SharePointNames.Title(url));
        Assert.Equal(idSource, SharePointNames.IdSource(url));
    }
}
