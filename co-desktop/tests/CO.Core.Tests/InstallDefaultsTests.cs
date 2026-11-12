// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core;
using Xunit;

namespace CO.Core.Tests;

// Freedom F1: the names of the install come from the install.json built in, never from constants.
public class InstallDefaultsTests
{
    [Fact]
    public void ValuesAreConsistent()
    {
        bool built = typeof(InstallDefaults).Assembly.GetManifestResourceNames().Contains("CO.Core.install.json");
        if (!built)
        {
            // CI and other machines without install.json: empty values, nothing invented
            Assert.Equal("", InstallDefaults.SiteUrl);
            Assert.Empty(InstallDefaults.Members);
            Assert.False(InstallDefaults.CorporateConnectors);   // corporate connectors are off unless install.json says so
            return;
        }
        Assert.StartsWith("https://", InstallDefaults.SiteUrl);
        Assert.False(InstallDefaults.SiteUrl.EndsWith('/'));
        Assert.NotEqual("", InstallDefaults.OwnerEnv);
        Assert.Equal(InstallDefaults.OwnerEmail, InstallDefaults.Members[InstallDefaults.OwnerEnv]);
        Assert.DoesNotContain(InstallDefaults.OwnerEnv, InstallDefaults.MemberEnvs);
        Assert.Equal(InstallDefaults.Members.Count - 1, InstallDefaults.MemberEnvs.Count);
    }
}
