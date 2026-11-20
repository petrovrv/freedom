// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using CO.Core.Policy;
using CO.Core.Tools;
using CO.Mac;
using static CO.Mac.MacMailRules;

namespace CO.Core.Tests;

// CO Resident 0.6.0 Gmail tools: the pure rules (src/CO.Mac/MacMailRules.cs, compiled into this project). No mailbox,
// no network: IMAP transcripts and MIME messages are written here.
public class MacMailTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    // A stream that gives one byte per Read, so the reader's buffer refills inside lines and literals.
    private sealed class Trickle : MemoryStream
    {
        public Trickle(byte[] b) : base(b) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }

    private const string FetchTranscript =
        "* 3 FETCH (UID 4512 FLAGS (\\Seen) INTERNALDATE \"17-Jul-2026 02:44:25 -0700\" X-GM-LABELS (\"\\\\Inbox\" \"\\\\Important\" Sch&APY-le) BODY[]<0> {11}\r\n" +
        "Hi\r\nthere\r\n)\r\n" +   // the literal holds two CRLFs; the ")" after it closes the FETCH
        "A004 OK Success\r\n";

    [Fact]
    public void Reader_joins_literals_and_tokens_read_fetch_items()
    {
        foreach (var stream in new Stream[] { new MemoryStream(B(FetchTranscript)), new Trickle(B(FetchTranscript)) })
        {
            var r = new Reader(stream);
            var first = Tokens(r.ReadResponse());
            var items = FetchItems(first)!;
            Assert.Equal("4512", items["UID"].Text);
            Assert.Equal("Hi\r\nthere\r\n", Encoding.UTF8.GetString(items["BODY[]"].Bytes));
            Assert.Equal(new[] { "Inbox", "Important", "Schöle" }, items["X-GM-LABELS"].Items.Select(n => DecodeUtf7(n.Text).TrimStart('\\')));
            Assert.Equal(new DateTimeOffset(2026, 7, 17, 2, 44, 25, TimeSpan.FromHours(-7)), InternalDate(items["INTERNALDATE"].Text));
            Assert.Contains(items["FLAGS"].Items, n => n.Atom == "\\Seen");
            Assert.Equal("A004 OK Success", Encoding.ASCII.GetString(r.ReadResponse()));
        }
    }

    [Fact]
    public void Reader_refuses_oversized_literal_and_closed_connection()
    {
        var r = new Reader(new MemoryStream(B("* 1 FETCH (BODY[] {999999}\r\nabc")));
        Assert.Throws<IOException>(() => r.ReadResponse(maxLiteral: 1000));
        var closed = new Reader(new MemoryStream(B("* OK partial")));
        Assert.Throws<IOException>(() => closed.ReadResponse());
    }

    [Fact]
    public void Literal_marker_only_at_line_end()
    {
        Assert.Equal(12, Reader.LiteralAtEnd(B("x {12}"), 0, 6));
        Assert.Equal(5, Reader.LiteralAtEnd(B("x {5+}"), 0, 6));
        Assert.Equal(-1, Reader.LiteralAtEnd(B("x {}"), 0, 4));
        Assert.Equal(-1, Reader.LiteralAtEnd(B("x {1a}"), 0, 6));
        Assert.Equal(-1, Reader.LiteralAtEnd(B("x {12} y"), 0, 8));
    }

    [Fact]
    public void Tokens_handle_quotes_nil_sections_and_nesting()
    {
        var t = Tokens(B("* 1 FETCH (UID 7 BODY[HEADER.FIELDS (DATE FROM)] \"a \\\"q\\\" \\\\b\" X NIL (1 (2 3)))"));
        var items = FetchItems(t)!;
        Assert.True(items.ContainsKey("BODY[HEADER.FIELDS (DATE FROM)]"));
        Assert.Equal("a \"q\" \\b", items["BODY[HEADER.FIELDS (DATE FROM)]"].Text);
        Assert.Equal(Node.Kind.Nil, items["X"].K);
        Assert.Equal("(2 3)", t[3].Items.Last().Items.Last().ToString());
    }

    [Fact]
    public void Search_and_list_responses()
    {
        var resp = new[] { Tokens(B("* SEARCH 3 17 250")), Tokens(B("* SEARCH")), Tokens(B("A1 OK SEARCH completed")) };
        Assert.Equal(new long[] { 3, 17, 250 }, SearchUids(resp));
        var boxes = ListMailboxes(new[]
        {
            Tokens(B("* LIST (\\HasNoChildren) \"/\" \"INBOX\"")),
            Tokens(B("* LIST (\\HasChildren \\Noselect) \"/\" \"[Gmail]\"")),
            Tokens(B("* LIST (\\All \\HasNoChildren) \"/\" \"[Gmail]/Alle Nachrichten\"")),
            Tokens(B("* LIST (\\Drafts \\HasNoChildren) \"/\" \"[Gmail]/Entw&APw-rfe\"")),
        });
        Assert.Equal("[Gmail]/Alle Nachrichten", SpecialUse(boxes, "\\All"));
        Assert.Equal("[Gmail]/Entw&APw-rfe", SpecialUse(boxes, "\\Drafts"));
        Assert.Equal("[Gmail]/Entwürfe", DecodeUtf7(SpecialUse(boxes, "\\Drafts")!));
        Assert.Null(SpecialUse(boxes, "\\Junk"));
    }

    [Fact]
    public void Search_command_quotes_ascii_and_sends_other_text_as_literal()
    {
        Assert.Equal(new object[] { "UID SEARCH X-GM-RAW \"from:a@b.org \\\"x y\\\"\"" }, SearchCommand("from:a@b.org \"x y\""));
        var lit = SearchCommand("subject:Привет");
        Assert.Equal("UID SEARCH CHARSET UTF-8 X-GM-RAW ", lit[0]);
        Assert.Equal(B("subject:Привет"), (byte[])lit[1]);
        Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => SearchCommand("in:inbox\r\nA2 LOGOUT")).Code);
        Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => SearchCommand("  ")).Code);
        Assert.Throws<ToolException>(() => SearchCommand(new string('a', MaxQueryChars + 1)));
    }

    [Theory]
    [InlineData("Entwürfe", "Entw&APw-rfe")]
    [InlineData("A&B", "A&-B")]
    [InlineData("日本語", "&ZeVnLIqe-")]
    [InlineData("plain", "plain")]
    public void Modified_utf7_round_trip(string text, string encoded)
    {
        Assert.Equal(encoded, EncodeUtf7(text));
        Assert.Equal(text, DecodeUtf7(encoded));
        Assert.Equal("Школа/Класс 5", DecodeUtf7(EncodeUtf7("Школа/Класс 5")));
    }

    [Fact]
    public void Labels_map_system_names_and_refuse_trash_and_spam()
    {
        Assert.Equal("\\Inbox", LabelArg("inbox"));
        Assert.Equal("\\Starred", LabelArg("\\Starred"));
        Assert.Equal("\"Sch&APY-le\"", LabelArg("Schöle"));
        foreach (var bad in new[] { "Trash", "\\Trash", "[Gmail]/Spam", "Bin", "Drafts", "", "a\nb" })
            Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => LabelArg(bad)).Code);
    }

    [Theory]
    [InlineData("abcd efgh ijkl mnop\n", "abcdefghijklmnop")]
    [InlineData("abcdefghijklmnop", "abcdefghijklmnop")]
    [InlineData("abcd efgh ijkl mnop", "abcdefghijklmnop")]
    [InlineData("pass word with spaces", "pass word with spaces")]
    public void App_password_spaces_removed_only_for_google_format(string stored, string expected) =>
        Assert.Equal(expected, NormalizeAppPassword(stored));

    [Fact]
    public void Plain_auth_is_base64_of_nul_user_nul_password()
    {
        string b = PlainAuth("ann@gmail.com", "abcdefghijklmnop");
        Assert.Equal("\0ann@gmail.com\0abcdefghijklmnop", Encoding.UTF8.GetString(Convert.FromBase64String(b)));
        Assert.DoesNotContain("abcdefghijklmnop", b);
    }

    [Fact]
    public void Status_text_of_tagged_refusal()
    {
        Assert.Equal("[AUTHENTICATIONFAILED] Invalid credentials (Failure)", StatusText(B("A001 NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)")));
        Assert.Equal("Could not parse command", StatusText(B("A002 BAD Could not parse command")));
    }

    private const string Alternative =
        "From: =?UTF-8?B?0JDQvdC90LA=?= <anna@example.org>\r\n" +
        "To: ann@gmail.com\r\n" +
        "Subject: =?iso-8859-1?Q?Caf=E9?= =?iso-8859-1?Q?_menu?=\r\n" +
        "Message-ID: <m1@example.org>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n" +
        "preamble\r\n" +
        "--outer\r\n" +
        "Content-Type: multipart/alternative;\r\n boundary=inner\r\n\r\n" +
        "--inner\r\n" +
        "Content-Type: text/plain; charset=iso-8859-1\r\n" +
        "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
        "Bonjour, le caf=E9 est pr=\r\n" +
        "=EAt.\r\n" +
        "--inner\r\n" +
        "Content-Type: text/html; charset=utf-8\r\n\r\n" +
        "<p>Bonjour HTML</p>\r\n" +
        "--inner--\r\n" +
        "--outer\r\n" +
        "Content-Type: application/pdf\r\n" +
        "Content-Disposition: attachment; filename*=UTF-8''men%C3%BC%20Juni.pdf\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" +
        "JVBERi0x\r\nLjQK\r\n" +
        "--outer\r\n" +
        "Content-Type: image/png; name=\"logo.png\"\r\n" +
        "Content-Disposition: inline\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" +
        "iVBORw==\r\n" +
        "--outer--\r\n" +
        "epilogue\r\n";

    [Fact]
    public void Mime_prefers_plain_text_and_lists_attachments()
    {
        var m = ParseMail(Encoding.Latin1.GetBytes(Alternative));
        Assert.Equal("Анна <anna@example.org>", m.From);
        Assert.Equal("Café menu", m.Subject);
        Assert.Equal("<m1@example.org>", m.MessageId);
        Assert.Equal("Bonjour, le café est prêt.", m.Text);
        Assert.False(m.TextFromHtml);
        Assert.Equal(2, m.Attachments.Count);
        Assert.Equal((1, "menü Juni.pdf", "application/pdf"), (m.Attachments[0].Index, m.Attachments[0].Name, m.Attachments[0].ContentType));
        Assert.Equal("%PDF-1.4\n", Encoding.ASCII.GetString(m.Attachments[0].Part.Decoded()));
        Assert.Equal(9, m.Attachments[0].Size);
        Assert.Equal("logo.png", m.Attachments[1].Name);
    }

    [Fact]
    public void Mime_html_only_becomes_text_with_links()
    {
        string raw = "Subject: x\nContent-Type: text/html; charset=windows-1252\nContent-Transfer-Encoding: quoted-printable\n\n" +
                     "<html><head><style>p{color:red}</style><title>T</title></head><body><script>alert(1)</script>" +
                     "<p>Hello&nbsp;<b>Ann</b> =80 5</p><a href=3D\"https://school.example/plan?a=3D1&amp;b=3D2\">the plan</a>" +
                     "<br><a href=3D\"https://x.example\">https://x.example</a><ul><li>one</li><li>two</li></ul><!-- hidden --></body></html>";
        var m = ParseMail(Encoding.ASCII.GetBytes(raw));
        Assert.True(m.TextFromHtml);
        Assert.Equal("Hello Ann € 5\nthe plan <https://school.example/plan?a=1&b=2>\nhttps://x.example\n\n• one\n• two", m.Text);
        Assert.DoesNotContain("alert", m.Text);
        Assert.DoesNotContain("hidden", m.Text);
    }

    [Fact]
    public void Crafted_html_with_unclosed_tags_is_fast()
    {
        Assert.Equal("a b", StripTags("a<x>b", " ").Replace("  ", " "));
        Assert.Equal("a<b c", StripTags("a<b c", ""));
        string evil = string.Concat(Enumerable.Repeat("<a href=\"x\" <p <b ", 40_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HtmlToText(evil);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
    }

    [Fact]
    public void Mime_cut_message_and_unknown_charset_still_give_text()
    {
        string raw = "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain; charset=x-unknown\r\n\r\nPrivé tekst die halverwege afbreekt";
        var m = ParseMail(B(raw));
        Assert.Equal("Privé tekst die halverwege afbreekt", m.Text);
        var noHeaders = ParseMail(B("\r\nonly body"));
        Assert.Equal("only body", noHeaders.Text);
    }

    [Fact]
    public void Rfc2231_continuations_and_encoded_words_in_names()
    {
        var (v, p) = ParseParams("attachment; filename*0*=UTF-8''%D0%BF%D0%BB; filename*1*=%D0%B0%D0%BD.pdf; size=10");
        Assert.Equal("attachment", v);
        Assert.Equal("план.pdf", p["filename"]);
        Assert.Equal("10", p["size"]);
        var (_, q) = ParseParams("application/pdf; name=\"=?UTF-8?B?0L/Qu9Cw0L0ucGRm?=\"");
        Assert.Equal("план.pdf", q["name"]);
        var (_, r) = ParseParams("text/plain; name*0=\"long \"; name*1=\"name.txt\"");
        Assert.Equal("long name.txt", r["name"]);
    }

    [Fact]
    public void Base64_and_quoted_printable_are_tolerant()
    {
        Assert.Equal("hello", Encoding.ASCII.GetString(Base64Tolerant(B("aGVs\r\nbG8"))));
        Assert.Equal("hel", Encoding.ASCII.GetString(Base64Tolerant(B("aGVs!!"))));
        Assert.Equal("a=b c=Zz", Encoding.ASCII.GetString(QuotedPrintable(B("a=3Db =\r\nc=Zz"))));
    }

    [Fact]
    public void Addresses_parse_names_with_commas_and_refuse_bad_ones()
    {
        var list = ParseAddresses("\"Doe, John\" <john@example.org>; ann@example.org, Анна <anna@example.ru>");
        Assert.Equal(new[] { "john@example.org", "ann@example.org", "anna@example.ru" }, list.Select(a => a.Email));
        Assert.Equal("Doe, John", list[0].Name);
        Assert.Equal("\"Doe, John\" <john@example.org>", FormatAddress(list[0]));
        Assert.StartsWith("=?UTF-8?B?", FormatAddress(list[2]));
        Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => ParseAddresses("not an address")).Code);
        Assert.Equal("invalid_args", Assert.Throws<ToolException>(() => ParseAddresses("a@b.org\r\nBcc: evil@x.org")).Code);
        Assert.Empty(ParseAddresses("  "));
    }

    [Fact]
    public void Draft_round_trips_through_the_parser()
    {
        byte[] pdf = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        var d = new Draft
        {
            From = new Address("", "ann@gmail.com"),
            To = ParseAddresses("Mme Dupont <dupont@school.example>"),
            Cc = ParseAddresses("papa@example.org"),
            Subject = "Absence de Léa — 21/11",
            Body = "Bonjour,\nLéa sera absente.\n\nhttps://ir.example/link stays as written\nAnn",
            InReplyTo = "<m1@school.example>",
            References = "<m0@school.example> <m1@school.example>",
            Files = new() { new DraftFile("certificat médical.pdf", ContentTypeFor("x.PDF"), pdf) },
            Date = new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.FromHours(1)),
            MessageId = "<co-1@gmail.com>",
            Boundary = "co-test",
        };
        byte[] msg = BuildDraft(d);
        string text = Encoding.ASCII.GetString(msg);
        Assert.All(msg, b => Assert.True(b < 128, "draft is 7-bit"));
        Assert.Contains("Date: Fri, 20 Nov 2026 09:05:00 +0100\r\n", text);
        Assert.Contains("In-Reply-To: <m1@school.example>\r\n", text);
        Assert.Contains("References: <m0@school.example>\r\n <m1@school.example>\r\n", text);
        Assert.DoesNotContain("\n.\r\n", text);
        var m = ParseMail(msg);
        Assert.Equal(d.Subject, m.Subject);
        Assert.Equal("Mme Dupont <dupont@school.example>", m.To);
        Assert.Equal(NormalizeText(d.Body), m.Text);
        Assert.Single(m.Attachments);
        Assert.Equal("certificat médical.pdf", m.Attachments[0].Name);
        Assert.Equal("application/pdf", m.Attachments[0].ContentType);
        Assert.Equal(pdf, m.Attachments[0].Part.Decoded());
    }

    [Fact]
    public void Draft_refuses_header_injection_and_too_large_files()
    {
        var from = new Address("", "ann@gmail.com");
        Assert.Throws<ToolException>(() => BuildDraft(new Draft { From = from, Subject = "Hi\r\nBcc: x@y.org" }));
        Assert.Throws<ToolException>(() => BuildDraft(new Draft { From = from, InReplyTo = "<a@b>\nX: y" }));
        Assert.Equal("too_large", Assert.Throws<ToolException>(() => BuildDraft(new Draft
        {
            From = from, Subject = "big", Files = new() { new DraftFile("a.bin", "application/octet-stream", new byte[MaxAttachBytes + 1]) },
        })).Code);
        string plain = Encoding.ASCII.GetString(BuildDraft(new Draft { From = from, To = ParseAddresses("a@b.org"), Subject = "x", Body = "only text" }));
        Assert.Contains("Content-Type: text/plain; charset=utf-8\r\n", plain);
        Assert.DoesNotContain("multipart", plain);
    }

    [Fact]
    public void Reply_fields_thread_the_draft()
    {
        var orig = ParseMail(B("From: Teacher <t@school.example>\r\nReply-To: office@school.example\r\nTo: ann@gmail.com\r\n" +
                               "Subject: Trip on Friday\r\nMessage-ID: <m2@school.example>\r\nReferences: <m0@school.example> <m1@school.example>\r\n\r\nDetails\r\nline 2"));
        var r = ReplyFields(orig, "ann@gmail.com");
        Assert.Equal("<m2@school.example>", r.InReplyTo);
        Assert.Equal("<m0@school.example> <m1@school.example> <m2@school.example>", r.References);
        Assert.Equal("Re: Trip on Friday", r.Subject);
        Assert.Equal("office@school.example", Assert.Single(r.To).Email);
        Assert.EndsWith("wrote:\n> Details\n> line 2", Quote(orig));

        var own = ParseMail(B("From: ann@gmail.com\r\nTo: Teacher <t@school.example>\r\nSubject: RE: Trip\r\nMessage-ID: <m3@gmail.com>\r\n\r\nx"));
        var r2 = ReplyFields(own, "ANN@gmail.com");
        Assert.Equal("RE: Trip", r2.Subject);
        Assert.Equal("t@school.example", Assert.Single(r2.To).Email);
        Assert.Equal("<m3@gmail.com>", r2.References);

        var encoded = ParseMail(B("From: =?UTF-8?B?RG9lLCBKYW5l?= <jane@school.example>\r\nSubject: x\r\nMessage-ID: <m4@school.example>\r\n\r\ny"));
        Assert.Equal("Doe, Jane <jane@school.example>", encoded.From);
        var r3 = ReplyFields(encoded, "ann@gmail.com");
        Assert.Equal(("Doe, Jane", "jane@school.example"), (Assert.Single(r3.To).Name, r3.To[0].Email));

        var noId = ParseMail(B("From: a@b.org\r\nSubject: x\r\n\r\ny"));
        Assert.Equal("no_message_id", Assert.Throws<ToolException>(() => ReplyFields(noId, "ann@gmail.com")).Code);
    }

    [Theory]
    [InlineData("../../etc/passwd", 1, "passwd")]
    [InlineData("C:\\Users\\x\\report.pdf", 1, "report.pdf")]
    [InlineData("a:b?.pdf", 1, "a_b_.pdf")]
    [InlineData("..", 3, "attachment-3")]
    [InlineData("", 2, "attachment-2")]
    [InlineData("bad\u0001name.txt", 1, "bad_name.txt")]
    public void Attachment_file_names_are_safe(string name, int index, string expected) =>
        Assert.Equal(expected, SafeFileName(name, index));

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        string n = SafeFileName(new string('x', 300) + ".pdf", 1);
        Assert.Equal(120, n.Length);
        Assert.EndsWith(".pdf", n);
    }

    [Fact]
    public void Mail_settings_default_modes_and_broken_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "co-mail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new MailSettings(Path.Combine(dir, "mail.json"));
            Assert.Equal(Rights.Read | Rights.Create, s.Rights);
            Assert.Equal("drafts", s.Mode);
            foreach (string mode in MailSettings.Modes)
            {
                s.Mode = mode;
                Assert.Equal(mode, new MailSettings(Path.Combine(dir, "mail.json")).Mode);
            }
            s.Mode = "off";
            Assert.Equal(Rights.None, s.Rights);
            s.Rights = Rights.All;
            Assert.Equal(Rights.Read | Rights.Create | Rights.Edit, s.Rights);   // Delete is never a box of Gmail
            File.WriteAllText(Path.Combine(dir, "mail.json"), "{ broken");
            Assert.Equal("off", s.Mode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Snippet_and_email_checks()
    {
        Assert.Equal("a b c", Snippet(" a\n\n b\tc "));
        Assert.EndsWith("…", Snippet(new string('x', 500)));
        Assert.True(IsEmail("ann.p+school@gmail.com"));
        Assert.False(IsEmail("ann@gmail"));
        Assert.False(IsEmail("a b@gmail.com"));
    }
}

// The IMAP conversation of ImapClient against a scripted server (all answers queued up front; what the client wrote is checked).
public class MacImapClientTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private sealed class Script : Stream
    {
        private readonly MemoryStream _in;
        public MemoryStream Out { get; } = new();
        public Script(string server) => _in = new MemoryStream(B(server));
        public string Written => Encoding.UTF8.GetString(Out.ToArray());
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Out.Write(buffer, offset, count);
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void Login_list_examine_search_and_append_draft()
    {
        byte[] draft = B("Subject: x\r\n\r\nbody\r\n");
        var io = new Script(
            "* OK Gimap ready for requests\r\n" +
            "+ \r\n" +
            "* CAPABILITY IMAP4rev1 X-GM-EXT-1\r\n" +
            "A001 OK ann@gmail.com authenticated (Success)\r\n" +
            "* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n" +
            "* LIST (\\All \\HasNoChildren) \"/\" \"[Gmail]/All Mail\"\r\n" +
            "* LIST (\\Drafts \\HasNoChildren) \"/\" \"[Gmail]/Drafts\"\r\n" +
            "A002 OK Success\r\n" +
            "* 5 EXISTS\r\n" +
            "A003 OK [READ-ONLY] All Mail selected. (Success)\r\n" +
            "+ go ahead\r\n" +
            "* SEARCH 12 10\r\n" +
            "A004 OK SEARCH completed (Success)\r\n" +
            "+ go ahead\r\n" +
            "A005 OK [APPENDUID 7 99] (Success)\r\n");
        var c = new ImapClient(io);
        c.Greeting();
        c.Login("ann@gmail.com", "abcdefghijklmnop");
        Assert.Equal("[Gmail]/All Mail", c.SelectAll(write: false));
        Assert.Equal(new long[] { 12, 10 }, c.Search("subject:Привет"));
        Assert.Equal("99", c.AppendDraft(draft));
        int q = Encoding.UTF8.GetByteCount("subject:Привет");
        Assert.Equal(
            "A001 AUTHENTICATE PLAIN\r\n" + PlainAuth("ann@gmail.com", "abcdefghijklmnop") + "\r\n" +
            "A002 LIST \"\" \"*\"\r\n" +
            "A003 EXAMINE \"[Gmail]/All Mail\"\r\n" +
            $"A004 UID SEARCH CHARSET UTF-8 X-GM-RAW {{{q}}}\r\nsubject:Привет\r\n" +
            $"A005 APPEND \"[Gmail]/Drafts\" (\\Draft \\Seen) {{{draft.Length}}}\r\nSubject: x\r\n\r\nbody\r\n\r\n",
            io.Written);
        Assert.DoesNotContain("abcdefghijklmnop", io.Written);
    }

    [Theory]
    [InlineData("* OK\r\n+ \r\nA001 NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)\r\n")]
    [InlineData("* OK\r\nA001 NO [ALERT] Application-specific password required\r\n")]
    public void Refused_sign_in_is_a_login_error(string server)
    {
        var c = new ImapClient(new Script(server));
        c.Greeting();
        var ex = Assert.Throws<ImapClient.ImapError>(() => c.Login("ann@gmail.com", "wrongwrongwrongw"));
        Assert.True(ex.Login);
        Assert.DoesNotContain("wrongwrongwrongw", ex.Message);
    }

    [Fact]
    public void Literal_refused_before_continuation_and_bad_greeting()
    {
        var c = new ImapClient(new Script("* OK\r\nA001 NO [CANNOT] too long\r\n"));
        c.Greeting();
        var ex = Assert.Throws<ImapClient.ImapError>(() => c.Search("Ü"));
        Assert.False(ex.Login);
        Assert.Throws<ImapClient.ImapError>(() => new ImapClient(new Script("* BYE go away\r\n")).Greeting());
    }

    [Fact]
    public void Fetch_message_skips_unsolicited_updates_and_refuses_huge_mail()
    {
        string mail = "Subject: Hi\r\n\r\nHello";
        var io = new Script(
            "* OK\r\n" +
            "* 3 FETCH (UID 10 FLAGS (\\Seen) INTERNALDATE \"20-Nov-2026 09:00:00 +0100\" RFC822.SIZE 22 X-GM-LABELS (\"\\Inbox\") X-GM-THRID 123)\r\n" +
            "A001 OK Success\r\n" +
            "* 3 FETCH (UID 10 FLAGS (\\Seen))\r\n" +
            $"* 3 FETCH (UID 10 BODY[] {{{mail.Length}}}\r\n{mail})\r\n" +
            "A002 OK Success\r\n" +
            "* 4 FETCH (UID 11 RFC822.SIZE 999999999)\r\n" +
            "A003 OK Success\r\n");
        var c = new ImapClient(io);
        c.Greeting();
        var (meta, raw) = c.FetchMessage(10);
        Assert.Equal("123", meta["X-GM-THRID"].Text);
        Assert.Equal(mail, Encoding.UTF8.GetString(raw));
        Assert.Equal("too_large", Assert.Throws<ToolException>(() => c.FetchMessage(11)).Code);
        Assert.Contains("A001 UID FETCH 10 (UID FLAGS INTERNALDATE RFC822.SIZE X-GM-LABELS X-GM-THRID)\r\nA002 UID FETCH 10 (UID BODY.PEEK[])\r\n", io.Written);
    }

    [Fact]
    public void Missing_drafts_mailbox_is_reported()
    {
        var c = new ImapClient(new Script("* OK\r\n* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\nA001 OK Success\r\n"));
        c.Greeting();
        Assert.Equal("no_drafts", Assert.Throws<ToolException>(() => c.AppendDraft(B("x"))).Code);
    }
}
