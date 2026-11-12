// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Core.Pairing;

// Owner's choice (07.11): computers of listed members are paired without the 6-digit code. The proof of the
// account is Google's: the device folder CO-Device-<id> must be owned by the member's Google account, the one
// set for the environment in the owner's settings. A request that claims another account, a folder owned or
// created by someone else, or an environment outside the list waits for the code as before.
public static class AutoPair
{
    // Null when the request may be paired automatically, otherwise the reason it waits for the code.
    public static string? Refusal(PairingRequest r, IReadOnlyDictionary<string, string> members,
        IEnumerable<string> autoEnvironments, string ownerEmail, string folderName, IReadOnlyCollection<string> folderOwners)
    {
        if (!autoEnvironments.Contains(r.EnvironmentId, StringComparer.Ordinal)) return "environment not in the auto-pair list";
        if (!members.TryGetValue(r.EnvironmentId, out string? expected) || expected.Length == 0) return "no account set for the environment";
        if (expected.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase)) return "owner environment";
        if (!expected.Equals(r.Account, StringComparison.OrdinalIgnoreCase)) return "account differs from the environment's account";
        if (folderName != "CO-Device-" + r.DeviceId) return "device folder name differs";
        if (folderOwners.Count != 1 || !folderOwners.First().Equals(expected, StringComparison.OrdinalIgnoreCase))
            return "device folder is not owned by " + expected;
        return null;
    }
}
