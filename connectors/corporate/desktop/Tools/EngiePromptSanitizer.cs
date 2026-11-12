// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CO.Corporate.Tools;

// 0.7.12: cleans an engie_chat prompt before it goes to the ENGIE gateway. The gateway's firewall drops requests
// (browser fetch: "Failed to fetch") that contain loopback URLs with a scheme; unusual symbols are suspected too.
// Loopback URLs become "localhost port N [path /p]"; runes outside the safe set become \uXXXX text.
// strict (used for the single retry after fetch_failed) keeps only printable ASCII and letters/marks of any script.
public static class EngiePromptSanitizer
{
    // Text sent, loopback URLs rewritten, runes escaped.
    public sealed record Result(string Text, int Urls, int Chars);

    // Matches loopback URLs such as http://localhost:8080/path or https://[::1]/x.
    // The negative lookahead after the host prevents matches like "localhostx" or "127.0.0.1x".
    private static readonly Regex LocalUrl = new(
        @"https?://" +
        @"(?<host>localhost|127(?:\.\d{1,3}){3}|0\.0\.0\.0|\[::1\])" +
        @"(?![\w.\-])" +
        @"(?::(?<port>\d+))?" +
        @"(?<path>/[^\s""'`)\]}<>]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Result Sanitize(string? text, bool strict = false)
    {
        if (text is null) return new Result("", 0, 0);

        // Fast path: printable ASCII (plus tab/LF/CR) with no URL scheme separator.
        if (IsPlainAscii(text) && !text.Contains("://", StringComparison.Ordinal))
            return new Result(text, 0, 0);

        // Step 1: replace loopback URLs with a neutral description.
        int urls = 0;
        string replaced = LocalUrl.Replace(text, m =>
        {
            urls++;
            Group port = m.Groups["port"], path = m.Groups["path"];
            var sb = new StringBuilder("localhost");
            if (port.Success) sb.Append(" port ").Append(port.Value);
            // A lone "/" carries no information, so only longer paths are kept.
            if (path.Success && path.Value.Length > 1) sb.Append(" path ").Append(path.Value);
            return sb.ToString();
        });

        // Step 2: escape every rune that Keep rejects as \uXXXX per UTF-16 unit.
        int chars = 0;
        var output = new StringBuilder(replaced.Length);
        Span<char> buffer = stackalloc char[2];
        foreach (Rune rune in replaced.EnumerateRunes())
        {
            int written = rune.EncodeToUtf16(buffer);
            if (Keep(rune, strict))
            {
                output.Append(buffer[..written]);
                continue;
            }
            chars++;
            for (int i = 0; i < written; i++)
                output.Append('\\').Append('u').Append(((int)buffer[i]).ToString("X4", CultureInfo.InvariantCulture));
        }
        return new Result(output.ToString(), urls, chars);
    }

    private static bool Keep(Rune r, bool strict)
    {
        int v = r.Value;
        if (v == '\t' || v == '\n' || v == '\r' || (v >= 32 && v <= 126)) return true;
        if (Rune.IsLetter(r)) return true;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark ||
            cat == UnicodeCategory.EnclosingMark)
            return true;
        if (strict) return false;
        if (v >= 160 && v <= 255) return true;
        if (Rune.IsNumber(r) || Rune.IsPunctuation(r) || cat == UnicodeCategory.SpaceSeparator) return true;
        return (v >= 8208 && v <= 8231)    // dashes, quotes, bullet, ellipsis
            || (v >= 8240 && v <= 8250)    // per mille, primes, single guillemets
            || (v >= 8352 && v <= 8383)    // currency signs
            || v == 8470 || v == 8482;     // numero, trade mark
    }

    // True when every char is tab, LF, CR, or printable ASCII.
    private static bool IsPlainAscii(string text)
    {
        foreach (char c in text)
            if (!(c is '\t' or '\n' or '\r' || (c >= 32 && c <= 126))) return false;
        return true;
    }
}
