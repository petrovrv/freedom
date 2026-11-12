// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Pairing;
using Xunit;

namespace CO.Core.Tests;

public class AutoPairTests
{
    private static readonly Dictionary<string, string> Members = new()
    {
        ["owner"] = "owner@example.org", ["sam"] = "sam@example.org", ["alex"] = "alex@example.org",
    };
    private static readonly string[] Auto = { "sam", "alex" };

    private static PairingRequest Req(string env, string account) =>
        new() { DeviceId = "d-0011223344556677", EnvironmentId = env, Account = account, DeviceFolderId = "F1" };

    private static string? Check(PairingRequest r, string folder, params string[] owners) =>
        AutoPair.Refusal(r, Members, Auto, "owner@example.org", folder, owners);

    [Fact]
    public void MemberDeviceInOwnFolderPairsWithoutCode()
    {
        Assert.Null(Check(Req("sam", "sam@example.org"), "CO-Device-d-0011223344556677", "Sam@Example.org"));
        Assert.Null(Check(Req("alex", "alex@example.org"), "CO-Device-d-0011223344556677", "alex@example.org"));
    }

    [Fact]
    public void FolderOfAnotherAccountWaitsForCode()
    {
        // Anyone can write account=sam@example.org into the request; only Google's folder owner counts.
        Assert.NotNull(Check(Req("sam", "sam@example.org"), "CO-Device-d-0011223344556677", "stranger@gmail.com"));
        Assert.NotNull(Check(Req("sam", "sam@example.org"), "CO-Device-d-0011223344556677"));
        Assert.NotNull(Check(Req("sam", "sam@example.org"), "CO-Device-d-0011223344556677", "sam@example.org", "x@y.z"));
    }

    [Fact]
    public void WrongAccountEnvironmentOrFolderWaitsForCode()
    {
        Assert.NotNull(Check(Req("sam", "alex@example.org"), "CO-Device-d-0011223344556677", "alex@example.org"));   // account of another env
        Assert.NotNull(Check(Req("owner", "owner@example.org"), "CO-Device-d-0011223344556677", "owner@example.org"));     // not in the list
        Assert.NotNull(Check(Req("guest", "sam@example.org"), "CO-Device-d-0011223344556677", "sam@example.org"));   // unknown env
        Assert.NotNull(Check(Req("sam", "sam@example.org"), "CO-Device-d-ffffffffffffffff", "sam@example.org"));   // other folder
        Assert.NotNull(AutoPair.Refusal(Req("sam", "sam@example.org"), Members, Array.Empty<string>(), "owner@example.org",
            "CO-Device-d-0011223344556677", new[] { "sam@example.org" }));                                       // list switched off
    }
}
