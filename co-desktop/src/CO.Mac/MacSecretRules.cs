// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using CO.Core.Tools;

namespace CO.Mac;

// Pure rules of the Mac secret tools (secret_prompt, secret_list, secret_delete; MacSecrets.cs): names, the dialog
// text and its osascript arguments, the `security -i` line, parsing of the dialog answer and of dump-keychain.
// No process calls here, so CO.Core.Tests compiles this file too (tests/CO.Core.Tests/MacSecretTests.cs).
//
// Items are generic passwords in the login Keychain with service "aiwb/<name>" and account "<account>", the same
// names the tool library uses with Python keyring (on macOS keyring reads the login Keychain), so a tool run on the
// Mac reads keyring.get_password("aiwb/<name>", "<account>").
public static class MacSecretRules
{
    public const string Prefix = "aiwb/";
    // security -i reads a line into 4096 bytes (MAX_LINE_LEN in security.c); -X doubles the UTF-8 bytes, names take ~310.
    public const int MaxValueChars = 1000, MaxValueBytes = 1500, MaxLineChars = 4000;
    public const int MaxLabelChars = 300;
    public const int DialogTimeoutSec = 600;

    private static readonly Regex NameRe = new(@"\A[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex AccountRe = new(@"\A[A-Za-z0-9][A-Za-z0-9._@+-]{0,127}\z", RegexOptions.CultureInvariant);

    // "aiwb/<name>"; refuses bad names and names that point at the resident's own items (reserved = its Keychain
    // service and LaunchAgent labels), so no call can read, overwrite or delete them.
    public static string Service(string? name, IEnumerable<string> reserved)
    {
        name = (name ?? "").Trim();
        if (name.StartsWith(Prefix, StringComparison.Ordinal)) name = name[Prefix.Length..];
        if (!NameRe.IsMatch(name) || name.Contains(".."))
            throw new ToolException("invalid_args", "name: 1-64 characters a-z 0-9 . _ - (starting with a letter or digit), e.g. gmail-imap; the item is aiwb/<name>.");
        foreach (string r in reserved)
            if (r.Length > 0 && name.Contains(r, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("invalid_args", "name: reserved for CO Resident's own Keychain items.");
        return Prefix + name;
    }

    public static string Account(string? account)
    {
        account = (account ?? "").Trim();
        if (account.Length == 0) return "default";
        if (!AccountRe.IsMatch(account))
            throw new ToolException("invalid_args", "account: 1-128 characters A-Z a-z 0-9 . _ @ + - (a login or e-mail), default \"default\".");
        return account;
    }

    // Agent-supplied text shown to the person: one line, no control characters, capped.
    public static string CleanLabel(string? label)
    {
        var sb = new StringBuilder();
        foreach (char c in label ?? "")
            sb.Append(char.IsControl(c) || c is '\u2028' or '\u2029' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c);
        string s = Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        if (s.Length <= MaxLabelChars) return s;
        int cut = char.IsHighSurrogate(s[MaxLabelChars - 1]) ? MaxLabelChars - 1 : MaxLabelChars;   // never half a surrogate pair
        return s[..cut] + "…";
    }

    public static string DialogTitle(string service) => "CO Resident — secret for " + service;

    public static string DialogText(string service, string account, string label, bool exists, bool tainted, int timeoutSec = DialogTimeoutSec)
    {
        var sb = new StringBuilder();
        sb.Append("CO Resident: an AI Workbench agent asks you to type a password or key on this Mac.\n\n");
        if (label.Length > 0) sb.Append("The agent says: ").Append(label).Append("\n\n");
        sb.Append("It is saved only in your login Keychain on this Mac:\n  service: ").Append(service)
          .Append("\n  account: ").Append(account).Append('\n');
        if (exists) sb.Append("A value is already saved there; Save replaces it.\n");
        sb.Append("\nThe agent never sees the value; only tools that run on this Mac can read it.\n");
        sb.Append("Do not type your Mac or Apple ID password unless you really mean to.");
        if (tainted) sb.Append("\n\n⚠ The agent has read untrusted content (web, mail) in this task: Cancel if you did not expect this request.");
        sb.Append($"\n\nNo answer in {timeoutSec / 60} min = Cancel.");
        return sb.ToString();
    }

    // osascript arguments: the script lines are constant (only the integer timeout is in them); text and title are
    // argv items, so nothing an agent writes can become AppleScript. The answer is printed as S<value>, C or T.
    public static List<string> DialogArgs(string text, string title, int timeoutSec = DialogTimeoutSec) => new()
    {
        "-e", "on run argv",
        "-e", "try",
        "-e", "tell me to activate",
        "-e", "end try",
        "-e", $"with timeout of {timeoutSec + 120} seconds",
        "-e", "try",
        "-e", $"set r to display dialog (item 1 of argv) with title (item 2 of argv) default answer \"\" with hidden answer buttons {{\"Cancel\", \"Save\"}} default button \"Save\" cancel button \"Cancel\" giving up after {timeoutSec} with icon caution",
        "-e", "on error number -128",
        "-e", "return \"C\"",
        "-e", "end try",
        "-e", "end timeout",
        "-e", "if gave up of r then return \"T\"",
        "-e", "return \"S\" & (text returned of r)",
        "-e", "end run",
        text, title,
    };

    public enum Answer { Saved, Cancelled, TimedOut, Empty, TooLong, Error }

    // The value only for Saved (edges trimmed, like the Windows secret-prompt).
    public static (Answer Kind, string? Value) ParseDialog(int exitCode, string stdout)
    {
        if (exitCode != 0) return (Answer.Error, null);
        string s = stdout.EndsWith('\n') ? stdout[..^1] : stdout;
        if (s == "C") return (Answer.Cancelled, null);
        if (s == "T") return (Answer.TimedOut, null);
        if (!s.StartsWith('S')) return (Answer.Error, null);
        string v = s[1..].Trim();
        if (v.Length == 0) return (Answer.Empty, null);
        if (v.Length > MaxValueChars || Encoding.UTF8.GetByteCount(v) > MaxValueBytes) return (Answer.TooLong, null);
        return (Answer.Saved, v);
    }

    // One line for `security -i` (stdin, never argv). Plain printable ASCII without quotes or backslash goes as
    // -w "..."; anything else as -X <hex of the UTF-8 bytes>, so the security tokenizer never sees a quote or escape.
    public static string AddCommand(string service, string account, string value)
    {
        if (!service.StartsWith(Prefix, StringComparison.Ordinal) || !NameRe.IsMatch(service[Prefix.Length..]) || !AccountRe.IsMatch(account))
            throw new ArgumentException("bad service or account");
        if (value.Length == 0 || value.Length > MaxValueChars || Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
            throw new ArgumentException("bad value length");
        bool plain = value.All(c => c is >= ' ' and <= '~' and not '"' and not '\'' and not '\\');
        string data = plain ? $"-w \"{value}\"" : "-X " + Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
        string line = $"add-generic-password -U -s \"{service}\" -a \"{account}\" -l \"{service}\" {data}\n";
        if (line.Length > MaxLineChars) throw new ArgumentException("line too long for security -i");
        return line;
    }

    private static readonly Regex AttrRe = new(@"^\s*""(svce|acct|mdat)""<[a-z]+>=(?:0x[0-9A-Fa-f]+\s+)?""(.*)""\s*$", RegexOptions.CultureInvariant);

    // Generic passwords "aiwb/*" of `security dump-keychain` (attributes only, the command never prints values).
    public static List<(string Service, string Account)> ParseDump(string dump)
    {
        var found = new SortedSet<(string, string)>();
        string? cls = null, svc = null, acct = null;
        void Flush()
        {
            if (cls == "genp" && svc != null && acct != null && svc.StartsWith(Prefix, StringComparison.Ordinal)
                && NameRe.IsMatch(svc[Prefix.Length..]) && AccountRe.IsMatch(acct))
                found.Add((svc, acct));
            cls = svc = acct = null;
        }
        foreach (string raw in dump.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("keychain:", StringComparison.Ordinal)) { Flush(); continue; }
            if (line.StartsWith("class:", StringComparison.Ordinal)) { cls = line[6..].Trim().Trim('"'); continue; }
            var m = AttrRe.Match(line);
            if (!m.Success) continue;
            if (m.Groups[1].Value == "svce") svc = m.Groups[2].Value;
            else if (m.Groups[1].Value == "acct") acct = m.Groups[2].Value;
        }
        Flush();
        return found.ToList();
    }

    // "mdat" of `security find-generic-password` (attributes only): changes when the item is written.
    public static string? ModDate(string attributes)
    {
        foreach (string raw in attributes.Split('\n'))
        {
            var m = AttrRe.Match(raw.TrimEnd('\r'));
            if (m.Success && m.Groups[1].Value == "mdat") return m.Groups[2].Value;
        }
        return null;
    }
}
