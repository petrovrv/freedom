// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CO.Core.Connectors;
using CO.Core.Policy;
using CO.Core.Tools;
using static CO.Mac.MacMailRules;

namespace CO.Mac;

// Pure rules of the Gmail tools of this Mac (mail_status, mail_search, mail_read, mail_attachment, mail_draft, mail_label;
// MacMail.cs): the IMAP reader and tokenizer (literals {n}), command quoting, modified UTF-7 labels, a small MIME parser
// (multipart, base64, quoted-printable, charsets, RFC 2047/2231), HTML to text, addresses, the draft message, the
// connector switch (MailSettings) and the IMAP conversation over a stream (ImapClient; TLS is added by MacMail.cs).
// No network or process calls here, so CO.Core.Tests compiles this file too (tests/CO.Core.Tests/MacMailTests.cs).
// Nothing in here ever sends mail: drafts are APPENDed to the Drafts mailbox by MacMail.cs; there is no SMTP.
public static class MacMailRules
{
    public const string SecretName = "gmail-imap";              // Keychain service aiwb/gmail-imap, account = the Gmail address
    public const string Host = "imap.gmail.com";
    public const int Port = 993;
    public const int MaxSearch = 50, DefaultSearch = 20;
    public const int SnippetFetchBytes = 16_384, SnippetChars = 200;
    public const int MaxQueryChars = 1000, MaxSubjectChars = 900, MaxBodyChars = 200_000, MaxRecipients = 50;
    public const long MaxAttachBytes = 25L << 20;               // Gmail's limit for a message's attachments
    public const long MaxMessageBytes = 60L << 20;              // largest message mail_read / mail_attachment fetch

    static MacMailRules() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // windows-1252, koi8-r, iso-2022-jp …

    private static readonly Regex EmailRe = new(
        @"\A[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+\z",
        RegexOptions.CultureInvariant);

    public static bool IsEmail(string s) => s.Length <= 254 && EmailRe.IsMatch(s);

    // Google shows app passwords as "abcd efgh ijkl mnop"; IMAP wants the 16 letters without the spaces.
    public static string NormalizeAppPassword(string stored)
    {
        string s = stored.TrimEnd('\r', '\n');
        return Regex.IsMatch(s, @"\A[A-Za-z]{4}(?: ?[A-Za-z]{4}){3}\z") ? s.Replace(" ", "") : s;
    }

    // SASL PLAIN initial response (RFC 4616) for "AUTHENTICATE PLAIN <b64>": the password never appears as an IMAP string.
    public static string PlainAuth(string user, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + user + "\0" + password));

    public static void NoLineBreaks(string? s, string what)
    {
        if (s != null && s.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ToolException("invalid_args", what + ": no line breaks.");
    }

    // ------------------------------------------------------------------ IMAP commands

    // An IMAP string for a command: a quoted string when it is short printable ASCII, else a literal (UTF-8 bytes).
    public static object Astring(string s)
    {
        if (s.IndexOf('\0') >= 0) throw new ToolException("invalid_args", "NUL in an IMAP string.");
        bool plain = s.Length <= 512 && s.All(c => c is >= ' ' and <= '~');
        return plain ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : Encoding.UTF8.GetBytes(s);
    }

    // UID SEARCH by a Gmail search (X-GM-RAW), as command parts (string text, byte[] literal).
    public static List<object> SearchCommand(string gmailQuery)
    {
        string q = (gmailQuery ?? "").Trim();
        if (q.Length == 0) throw new ToolException("invalid_args", "query: a Gmail search, e.g. 'from:school newer_than:7d' or 'in:inbox is:unread'.");
        if (q.Length > MaxQueryChars) throw new ToolException("invalid_args", $"query: at most {MaxQueryChars} characters.");
        NoLineBreaks(q, "query");
        var a = Astring(q);
        return a is string ? new List<object> { "UID SEARCH X-GM-RAW " + a } : new List<object> { "UID SEARCH CHARSET UTF-8 X-GM-RAW ", a };
    }

    public static string UidSet(IEnumerable<long> uids) => string.Join(",", uids.Select(u => u.ToString(CultureInfo.InvariantCulture)));

    // Modified UTF-7 of IMAP mailbox and Gmail label names (RFC 3501 5.1.3).
    public static string EncodeUtf7(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '&') { sb.Append("&-"); i++; continue; }
            if (c is >= ' ' and <= '~') { sb.Append(c); i++; continue; }
            int j = i;
            while (j < s.Length && !(s[j] is >= ' ' and <= '~')) j++;
            byte[] be = Encoding.BigEndianUnicode.GetBytes(s[i..j]);
            sb.Append('&').Append(Convert.ToBase64String(be).TrimEnd('=').Replace('/', ',')).Append('-');
            i = j;
        }
        return sb.ToString();
    }

    public static string DecodeUtf7(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '&') { sb.Append(s[i++]); continue; }
            int end = s.IndexOf('-', i + 1);
            if (end < 0) { sb.Append(s[i..]); break; }
            if (end == i + 1) { sb.Append('&'); i = end + 1; continue; }
            string b64 = s[(i + 1)..end].Replace(',', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try { sb.Append(Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(b64))); }
            catch (FormatException) { sb.Append(s[i..(end + 1)]); }
            i = end + 1;
        }
        return sb.ToString();
    }

    // A Gmail label for X-GM-LABELS STORE: system labels (\Inbox, \Starred, \Important) as atoms, others as strings.
    // Trash and Spam are refused: these tools never delete or report mail.
    public static object LabelArg(string label)
    {
        string l = (label ?? "").Trim();
        NoLineBreaks(l, "label");
        if (l.Length == 0 || l.Length > 225) throw new ToolException("invalid_args", "label: 1-225 characters.");
        string bare = l.TrimStart('\\').Replace("[Gmail]/", "", StringComparison.OrdinalIgnoreCase).Replace("[Google Mail]/", "", StringComparison.OrdinalIgnoreCase);
        if (bare.Equals("Trash", StringComparison.OrdinalIgnoreCase) || bare.Equals("Bin", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("Spam", StringComparison.OrdinalIgnoreCase) || bare.Equals("Junk", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("Draft", StringComparison.OrdinalIgnoreCase) || bare.Equals("Drafts", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("Sent", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("invalid_args", "label: Trash, Spam, Drafts and Sent cannot be set or removed with mail_label.");
        foreach (var sys in new[] { "Inbox", "Starred", "Important" })
            if (bare.Equals(sys, StringComparison.OrdinalIgnoreCase)) return "\\" + sys;
        return Astring(EncodeUtf7(l));
    }

    // ------------------------------------------------------------------ IMAP responses

    // Lines of an IMAP response joined with their literals, byte for byte (CRLF after each "{n}", the final CRLF removed).
    public sealed class Reader
    {
        public const int MaxLineBytes = 1 << 20;
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[16384];
        private int _pos, _len;
        public Reader(Stream s) => _s = s;

        private int Next()
        {
            if (_pos == _len)
            {
                _len = _s.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) { _len = 0; throw new IOException("The mail server closed the connection."); }
            }
            return _buf[_pos++];
        }

        public byte[] ReadResponse(long maxLiteral = MaxMessageBytes)
        {
            var ms = new MemoryStream();
            long literals = 0;
            while (true)
            {
                int lineStart = (int)ms.Length;
                int b;
                while ((b = Next()) != '\n')
                {
                    ms.WriteByte((byte)b);
                    if (ms.Length - lineStart > MaxLineBytes) throw new IOException("IMAP line too long.");
                }
                if (ms.Length > lineStart && ms.GetBuffer()[ms.Length - 1] == '\r') ms.SetLength(ms.Length - 1);
                long n = LiteralAtEnd(ms.GetBuffer(), lineStart, (int)ms.Length);
                if (n < 0) return ms.ToArray();
                literals += n;
                if (literals > maxLiteral) throw new IOException("IMAP literal too large.");
                ms.WriteByte((byte)'\r');
                ms.WriteByte((byte)'\n');
                for (long left = n; left > 0;)
                {
                    if (_pos == _len) { ms.WriteByte((byte)Next()); left--; continue; }   // Next refills the buffer
                    int take = (int)Math.Min(left, _len - _pos);
                    ms.Write(_buf, _pos, take);
                    _pos += take;
                    left -= take;
                }
            }
        }

        // "{123}" or "{123+}" at the end of a line: the literal size, else -1.
        public static long LiteralAtEnd(byte[] b, int start, int end)
        {
            if (end - start < 3 || b[end - 1] != '}') return -1;
            int i = end - 2;
            if (b[i] == '+') i--;
            int digitsEnd = i;
            while (i >= start && b[i] is >= (byte)'0' and <= (byte)'9') i--;
            if (i < start || b[i] != '{' || i == digitsEnd || digitsEnd - i > 12) return -1;
            return long.Parse(Encoding.ASCII.GetString(b, i + 1, digitsEnd - i), CultureInfo.InvariantCulture);
        }
    }

    public sealed class Node
    {
        public enum Kind { Atom, String, Nil, List }
        public Kind K { get; init; }
        public string Atom { get; init; } = "";
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public List<Node> Items { get; } = new();
        public string Text => K switch { Kind.String => Encoding.UTF8.GetString(Bytes), Kind.Atom => Atom, _ => "" };
        public override string ToString() => K == Kind.List ? "(" + string.Join(" ", Items) + ")" : K == Kind.Nil ? "NIL" : Text;
    }

    // Tokens of one response: atoms (with [section] and <partial> kept inside, e.g. BODY[HEADER.FIELDS (DATE)]<0>),
    // quoted strings, literals, NIL and parenthesized lists.
    public static List<Node> Tokens(byte[] d)
    {
        int i = 0;
        var top = new List<Node>();
        ParseInto(d, ref i, top, 0);
        return top;
    }

    private static void ParseInto(byte[] d, ref int i, List<Node> into, int depth)
    {
        if (depth > 40) throw new IOException("IMAP response nested too deeply.");
        while (i < d.Length)
        {
            byte c = d[i];
            if (c is (byte)' ' or (byte)'\r' or (byte)'\n') { i++; continue; }
            if (c == ')') { i++; return; }
            if (c == '(')
            {
                i++;
                var list = new Node { K = Node.Kind.List };
                ParseInto(d, ref i, list.Items, depth + 1);
                into.Add(list);
                continue;
            }
            if (c == '"')
            {
                i++;
                var ms = new MemoryStream();
                while (i < d.Length && d[i] != '"')
                {
                    if (d[i] == '\\' && i + 1 < d.Length) i++;
                    ms.WriteByte(d[i++]);
                }
                i++;
                into.Add(new Node { K = Node.Kind.String, Bytes = ms.ToArray() });
                continue;
            }
            if (c == '{')
            {
                int close = Array.IndexOf(d, (byte)'}', i);
                long n = close > 0 ? Reader.LiteralAtEnd(d, i, close + 1) : -1;
                if (n >= 0 && close + 2 < d.Length && d[close + 1] == '\r' && d[close + 2] == '\n')
                {
                    int startData = close + 3;
                    int len = (int)Math.Clamp(n, 0, d.Length - startData);   // a cut response gives what is there
                    into.Add(new Node { K = Node.Kind.String, Bytes = d.AsSpan(startData, len).ToArray() });
                    i = startData + len;
                    continue;
                }
                if (n >= 0 && close + 1 == d.Length) { into.Add(new Node { K = Node.Kind.String }); i = d.Length; continue; }   // "{0}" at the very end
            }
            int s = i;
            while (i < d.Length && d[i] is not ((byte)' ' or (byte)'(' or (byte)')' or (byte)'\r' or (byte)'\n'))
            {
                if (d[i] == '[')
                {
                    int close = Array.IndexOf(d, (byte)']', i);
                    i = close < 0 ? d.Length : close + 1;
                    continue;
                }
                i++;
            }
            string atom = Encoding.UTF8.GetString(d, s, i - s);
            into.Add(atom.Equals("NIL", StringComparison.OrdinalIgnoreCase) ? new Node { K = Node.Kind.Nil } : new Node { K = Node.Kind.Atom, Atom = atom });
        }
    }

    // The items of "* <n> FETCH (...)": keys upper-cased, BODY[...]<origin> without the origin.
    public static Dictionary<string, Node>? FetchItems(List<Node> resp)
    {
        if (resp.Count < 4 || resp[0].Atom != "*" || !resp[2].Atom.Equals("FETCH", StringComparison.OrdinalIgnoreCase) || resp[3].K != Node.Kind.List)
            return null;
        var d = new Dictionary<string, Node>(StringComparer.Ordinal);
        var items = resp[3].Items;
        for (int k = 0; k + 1 < items.Count; k += 2)
        {
            string key = items[k].Atom.ToUpperInvariant();
            int lt = key.LastIndexOf('<');
            if (lt > 0 && key.EndsWith('>')) key = key[..lt];
            d[key] = items[k + 1];
        }
        return d;
    }

    // UIDs of "* SEARCH 1 2 3" responses.
    public static List<long> SearchUids(IEnumerable<List<Node>> responses)
    {
        var uids = new List<long>();
        foreach (var r in responses)
            if (r.Count >= 2 && r[0].Atom == "*" && r[1].Atom.Equals("SEARCH", StringComparison.OrdinalIgnoreCase))
                foreach (var n in r.Skip(2))
                    if (long.TryParse(n.Atom, NumberStyles.None, CultureInfo.InvariantCulture, out long u) && u > 0) uids.Add(u);
        return uids;
    }

    // Mailboxes of "* LIST (\HasNoChildren \Drafts) "/" "[Gmail]/Drafts"": name (as sent, modified UTF-7) and flags.
    public static List<(string Name, HashSet<string> Flags)> ListMailboxes(IEnumerable<List<Node>> responses)
    {
        var list = new List<(string, HashSet<string>)>();
        foreach (var r in responses)
            if (r.Count >= 5 && r[0].Atom == "*" && r[1].Atom.Equals("LIST", StringComparison.OrdinalIgnoreCase) && r[2].K == Node.Kind.List)
                list.Add((r[4].Text, r[2].Items.Select(f => f.Atom.ToLowerInvariant()).ToHashSet()));
        return list;
    }

    public static string? SpecialUse(List<(string Name, HashSet<string> Flags)> boxes, string flag) =>
        boxes.FirstOrDefault(b => b.Flags.Contains(flag.ToLowerInvariant()) && !b.Flags.Contains("\\noselect")).Name;

    // "17-Jul-1996 02:44:25 -0700"
    public static DateTimeOffset? InternalDate(string s)
    {
        var m = Regex.Match(s.Trim(), @"\A(\d{1,2})-([A-Za-z]{3})-(\d{4}) (\d{2}):(\d{2}):(\d{2}) ([+-])(\d{2})(\d{2})\z");
        if (!m.Success) return null;
        int mon = Array.IndexOf(new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" }, m.Groups[2].Value.ToLowerInvariant()) + 1;
        if (mon == 0) return null;
        var off = new TimeSpan(int.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture), 0);
        if (m.Groups[7].Value == "-") off = -off;
        try
        {
            return new DateTimeOffset(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), mon, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture), off);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // Text after "<tag> NO" / "BAD" of a tagged response, for an error message (the server never echoes a password).
    public static string StatusText(byte[] tagged)
    {
        string s = Encoding.UTF8.GetString(tagged);
        var m = Regex.Match(s, @"\A\S+\s+(?:OK|NO|BAD)\s*(.*)\z", RegexOptions.Singleline);
        string t = m.Success ? m.Groups[1].Value : s;
        return t.Length > 300 ? t[..300] : t;
    }

    // ------------------------------------------------------------------ MIME

    public sealed class Part
    {
        public List<(string Name, string Value)> Headers { get; } = new();      // RFC 2047 words decoded (for display)
        public List<(string Name, string Value)> RawHeaders { get; } = new();   // as sent (address lists are parsed from these)
        public string Type { get; set; } = "text/plain";
        public Dictionary<string, string> TypeParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Disposition { get; set; } = "";
        public Dictionary<string, string> DispParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Encoding { get; set; } = "";
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public List<Part> Children { get; } = new();

        public string? Header(string name) => Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        public byte[] Decoded() => DecodeBody(Body, Encoding);
        public string? FileName => DispParams.TryGetValue("filename", out var f) && f.Length > 0 ? f : TypeParams.TryGetValue("name", out var n) && n.Length > 0 ? n : null;
    }

    public sealed record Attachment(int Index, string Name, string ContentType, long Size, Part Part);

    public sealed class Mail
    {
        public required Part Root { get; init; }
        public string From => Root.Header("From") ?? "";
        public string To => Root.Header("To") ?? "";
        public string Cc => Root.Header("Cc") ?? "";
        public string ReplyTo => Root.Header("Reply-To") ?? "";
        public string Subject => Root.Header("Subject") ?? "";
        public string Date => Root.Header("Date") ?? "";
        public string MessageId => (Root.Header("Message-ID") ?? "").Trim();
        public string InReplyTo => (Root.Header("In-Reply-To") ?? "").Trim();
        public string References => (Root.Header("References") ?? "").Trim();
        // An address header as sent: a display name such as =?UTF-8?B?…?= decoding to "Doe, Jane" stays one entry.
        public string RawHeader(string name) => Root.RawHeaders.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        public string Text { get; set; } = "";
        public bool TextFromHtml { get; set; }
        public List<Attachment> Attachments { get; } = new();
    }

    public static Mail ParseMail(byte[] raw)
    {
        var root = ParsePart(raw, 0);
        var mail = new Mail { Root = root };
        int index = 0;
        var (text, html) = Collect(root, mail, ref index, 0);
        mail.Text = NormalizeText(text);
        mail.TextFromHtml = html;
        return mail;
    }

    private static Part ParsePart(byte[] raw, int depth)
    {
        var p = new Part();
        int bodyStart = HeaderEnd(raw, out int headerLen);
        string head = DecodeHeaderBytes(raw.AsSpan(0, headerLen));
        foreach (var (name, value) in UnfoldHeaders(head)) p.Headers.Add((name, DecodeWords(value)));
        p.Body = bodyStart >= raw.Length ? Array.Empty<byte>() : raw.AsSpan(bodyStart).ToArray();
        // structured headers parsed from their raw (not RFC 2047 decoded) values
        var rawHeaders = UnfoldHeaders(head);
        p.RawHeaders.AddRange(rawHeaders);
        string ct = rawHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "text/plain";
        string cd = rawHeaders.FirstOrDefault(h => h.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        (p.Type, p.TypeParams) = ParseParams(ct);
        if (p.Type.Length == 0 || !p.Type.Contains('/')) p.Type = "text/plain";
        (p.Disposition, p.DispParams) = ParseParams(cd);
        p.Encoding = (rawHeaders.FirstOrDefault(h => h.Name.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase)).Value ?? "").Trim().ToLowerInvariant();
        if (p.Type.StartsWith("multipart/", StringComparison.Ordinal) && depth < 20
            && p.TypeParams.TryGetValue("boundary", out var boundary) && boundary.Length is > 0 and <= 200)
            foreach (var body in SplitMultipart(p.Body, boundary).Take(300))
                p.Children.Add(ParsePart(body, depth + 1));
        return p;
    }

    // Index of the body and the length of the header block (blank line CRLF CRLF or LF LF).
    private static int HeaderEnd(byte[] raw, out int headerLen)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            if (i == 0) { headerLen = 0; return 1; }   // body without headers
            if (i == 1 && raw[0] == '\r') { headerLen = 0; return 2; }
            if (i + 1 < raw.Length && raw[i + 1] == '\n') { headerLen = i; return i + 2; }
            if (i + 2 < raw.Length && raw[i + 1] == '\r' && raw[i + 2] == '\n') { headerLen = i; return i + 3; }
        }
        headerLen = raw.Length;
        return raw.Length;
    }

    // RFC 6532 headers may be raw UTF-8; otherwise bytes as Latin-1 (RFC 2047 words carry their own charset).
    private static string DecodeHeaderBytes(ReadOnlySpan<byte> b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(b); }
    }

    public static List<(string Name, string Value)> UnfoldHeaders(string head)
    {
        var list = new List<(string, string)>();
        string? name = null;
        var val = new StringBuilder();
        foreach (string raw in head.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                if (name != null) val.Append(' ').Append(raw.Trim());
                continue;
            }
            if (name != null) list.Add((name, val.ToString().Trim()));
            name = null;
            val.Clear();
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            name = raw[..colon].Trim();
            val.Append(raw[(colon + 1)..]);
        }
        if (name != null) list.Add((name, val.ToString().Trim()));
        return list;
    }

    // "type/sub; a=1; b="x y"; c*=UTF-8''%E2%82%AC; d*0="p"; d*1="q"" → ("type/sub", params) with RFC 2231 joined.
    public static (string Value, Dictionary<string, string> Params) ParseParams(string header)
    {
        var parts = SplitOutsideQuotes(header, ';');
        string value = parts.Count > 0 ? parts[0].Trim().ToLowerInvariant() : "";
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, SortedDictionary<int, (string V, bool Enc)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq <= 0) continue;
            string k = p[..eq].Trim();
            string v = p[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = Regex.Replace(v[1..^1], @"\\(.)", "$1");
            var m = Regex.Match(k, @"\A(.+?)\*(?:(\d{1,3})(\*)?)?\z");
            if (m.Success)
            {
                string bn = m.Groups[1].Value;
                bool enc = m.Groups[2].Success ? m.Groups[3].Success : true;
                int sec = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                if (!sections.TryGetValue(bn, out var s)) sections[bn] = s = new SortedDictionary<int, (string, bool)>();
                s[sec] = (v, enc);
            }
            else plain[k] = DecodeWords(v);   // some mailers put RFC 2047 words into name="…"
        }
        foreach (var (bn, secs) in sections)
        {
            Encoding cs = System.Text.Encoding.UTF8;
            var bytes = new List<byte>();
            bool first = true;
            foreach (var (_, (v, enc)) in secs)
            {
                string piece = v;
                if (enc && first)
                {
                    var m = Regex.Match(piece, @"\A([^']*)'[^']*'(.*)\z", RegexOptions.Singleline);
                    if (m.Success) { cs = GetEncoding(m.Groups[1].Value) ?? cs; piece = m.Groups[2].Value; }
                }
                first = false;
                bytes.AddRange(enc ? PercentDecode(piece) : System.Text.Encoding.UTF8.GetBytes(piece));
            }
            plain[bn] = cs.GetString(bytes.ToArray());
        }
        return (value, plain);
    }

    private static List<string> SplitOutsideQuotes(string s, char sep)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && q && i + 1 < s.Length) { sb.Append(c).Append(s[++i]); continue; }
            if (c == '"') q = !q;
            if (c == sep && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    private static byte[] PercentDecode(string s)
    {
        var o = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            { o.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue; }
            o.AddRange(System.Text.Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return o.ToArray();
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    // Bodies of the parts between "--boundary" lines; a missing closing line (a cut message) ends at the end.
    public static List<byte[]> SplitMultipart(byte[] body, string boundary)
    {
        byte[] delim = System.Text.Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<byte[]>();
        int partStart = -1, lineStart = 0;
        while (lineStart <= body.Length)
        {
            int nl = Array.IndexOf(body, (byte)'\n', lineStart);
            int lineEnd = nl < 0 ? body.Length : nl;
            int contentEnd = lineEnd > lineStart && body[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            if (contentEnd - lineStart >= delim.Length && body.AsSpan(lineStart, delim.Length).SequenceEqual(delim))
            {
                var rest = body.AsSpan(lineStart + delim.Length, contentEnd - lineStart - delim.Length);
                bool close = rest.Length >= 2 && rest[0] == '-' && rest[1] == '-';
                if (close || IsBlank(rest))
                {
                    if (partStart >= 0)
                    {
                        int end = lineStart;   // the CRLF before the delimiter belongs to it
                        if (end > partStart && body[end - 1] == '\n') end--;
                        if (end > partStart && body[end - 1] == '\r') end--;
                        parts.Add(body.AsSpan(partStart, Math.Max(0, end - partStart)).ToArray());
                    }
                    if (close) return parts;
                    partStart = nl < 0 ? body.Length : nl + 1;
                }
            }
            if (nl < 0) break;
            lineStart = nl + 1;
        }
        if (partStart >= 0 && partStart < body.Length) parts.Add(body.AsSpan(partStart).ToArray());
        return parts;
    }

    private static bool IsBlank(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s) if (b is not ((byte)' ' or (byte)'\t')) return false;
        return true;
    }

    public static byte[] DecodeBody(byte[] body, string transferEncoding) => transferEncoding switch
    {
        "base64" => Base64Tolerant(body),
        "quoted-printable" => QuotedPrintable(body),
        _ => body,
    };

    public static byte[] Base64Tolerant(byte[] body)
    {
        var sb = new StringBuilder(body.Length);
        foreach (byte b in body)
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/')
                sb.Append((char)b);
        int len = sb.Length - sb.Length % 4;
        if (sb.Length % 4 == 2) { sb.Append("=="); len = sb.Length; }
        else if (sb.Length % 4 == 3) { sb.Append('='); len = sb.Length; }
        try { return Convert.FromBase64String(sb.ToString(0, len)); }
        catch (FormatException) { return Array.Empty<byte>(); }
    }

    public static byte[] QuotedPrintable(byte[] body)
    {
        var o = new MemoryStream(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            byte b = body[i];
            if (b != '=') { o.WriteByte(b); continue; }
            if (i + 1 < body.Length && body[i + 1] == '\n') { i += 1; continue; }
            if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n') { i += 2; continue; }
            if (i + 2 < body.Length && IsHex((char)body[i + 1]) && IsHex((char)body[i + 2]))
            {
                o.WriteByte(Convert.ToByte(((char)body[i + 1]).ToString() + (char)body[i + 2], 16));
                i += 2;
                continue;
            }
            o.WriteByte(b);
        }
        return o.ToArray();
    }

    public static Encoding? GetEncoding(string? charset)
    {
        string c = (charset ?? "").Trim().Trim('"');
        if (c.Length == 0) return null;
        if (c.Equals("utf8", StringComparison.OrdinalIgnoreCase)) c = "utf-8";
        try { return System.Text.Encoding.GetEncoding(c); }
        catch (ArgumentException) { return null; }
    }

    // Text of a body in its charset; an undeclared or unknown charset is UTF-8 when valid, else Windows-1252/Latin-1.
    public static string DecodeText(byte[] b, string? charset)
    {
        var enc = GetEncoding(charset);
        if (enc != null && enc.CodePage != 20127) return enc.GetString(b);   // us-ascii is often mislabelled UTF-8
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return (GetEncoding("windows-1252") ?? System.Text.Encoding.Latin1).GetString(b); }
    }

    private static readonly Regex EncodedWord = new(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=", RegexOptions.CultureInvariant);

    // RFC 2047 encoded words of unstructured headers; whitespace between two encoded words is dropped.
    public static string DecodeWords(string s)
    {
        if (!s.Contains("=?", StringComparison.Ordinal)) return s;
        s = Regex.Replace(s, @"(\?=)\s+(=\?)", "$1$2");
        return EncodedWord.Replace(s, m =>
        {
            string cs = m.Groups[1].Value;
            int star = cs.IndexOf('*');
            if (star >= 0) cs = cs[..star];
            var enc = GetEncoding(cs) ?? System.Text.Encoding.UTF8;
            string data = m.Groups[3].Value;
            byte[] bytes = m.Groups[2].Value is "B" or "b"
                ? Base64Tolerant(System.Text.Encoding.ASCII.GetBytes(data))
                : QuotedPrintable(System.Text.Encoding.ASCII.GetBytes(data.Replace('_', ' ')));
            return enc.GetString(bytes);
        });
    }

    private static bool IsAttachment(Part p)
    {
        if (p.Disposition == "attachment") return true;
        if (p.FileName != null) return true;
        return p.Type is not ("text/plain" or "text/html");
    }

    // Text of the part (plain preferred over HTML in alternatives) and the attachments, numbered from 1 in order.
    private static (string Text, bool FromHtml) Collect(Part p, Mail mail, ref int index, int depth)
    {
        if (p.Type.StartsWith("multipart/", StringComparison.Ordinal) && p.Children.Count > 0)
        {
            var results = new List<(string Text, bool Html)>();
            foreach (var c in p.Children) results.Add(Collect(c, mail, ref index, depth + 1));
            if (p.Type == "multipart/alternative")
            {
                var plain = results.FirstOrDefault(r => !r.Html && r.Text.Trim().Length > 0);
                if (plain.Text != null) return plain;
                var any = results.LastOrDefault(r => r.Text.Trim().Length > 0);
                return any.Text != null ? any : ("", false);
            }
            var texts = results.Where(r => r.Text.Trim().Length > 0).ToList();
            return (string.Join("\n\n", texts.Select(r => r.Text.Trim())), texts.Count > 0 && texts.All(r => r.Html));
        }
        if (p.Type == "message/rfc822" || IsAttachment(p))
        {
            index++;
            string ext = p.Type switch
            {
                "message/rfc822" => ".eml", "text/calendar" => ".ics", "application/pdf" => ".pdf", "image/png" => ".png",
                "image/jpeg" => ".jpg", "image/gif" => ".gif", _ => "",
            };
            string name = p.FileName ?? (p.Type == "message/rfc822" ? "message-" + index + ".eml" : "attachment-" + index + ext);
            mail.Attachments.Add(new Attachment(index, name, p.Type, p.Decoded().LongLength, p));
            return ("", false);
        }
        string text = DecodeText(p.Decoded(), p.TypeParams.TryGetValue("charset", out var cs) ? cs : null);
        return p.Type == "text/html" ? (HtmlToText(text), true) : (text, false);
    }

    // ------------------------------------------------------------------ HTML and text

    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(2);

    public static string HtmlToText(string html)
    {
        if (html.Length > 2_000_000) html = html[..2_000_000];
        html = html.Replace('\u0001', ' ').Replace('\u0002', ' ');   // markers of link targets below
        try
        {
            string s = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline, RegexBudget);
            s = Regex.Replace(s, @"<(script|style|head|title|template)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexBudget);
            s = Regex.Replace(s, @"<a\b[^>]*?\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>(.*?)</a\s*>", m =>
            {
                string url = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                string inner = m.Groups[4].Value;
                string innerText = WebUtility.HtmlDecode(Regex.Replace(inner, "<[^>]+>", "", RegexOptions.None, RegexBudget)).Trim();
                bool show = Regex.IsMatch(url, @"\A(https?:|mailto:)", RegexOptions.IgnoreCase) && url.Length <= 2000
                            && !innerText.Equals(url, StringComparison.OrdinalIgnoreCase) && !("mailto:" + innerText).Equals(url, StringComparison.OrdinalIgnoreCase);
                return inner + (show ? " \u0001" + url.Replace("<", "%3C").Replace(">", "%3E") + "\u0002" : "");   // brackets after the tag strip
            }, RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexBudget);
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase, RegexBudget);
            s = Regex.Replace(s, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase, RegexBudget);
            s = Regex.Replace(s, @"</?(p|div|tr|table|ul|ol|h[1-6]|blockquote|section|article|header|footer)\b[^>]*>", "\n", RegexOptions.IgnoreCase, RegexBudget);
            s = Regex.Replace(s, @"</t[dh]\s*>", " \t", RegexOptions.IgnoreCase, RegexBudget);
            s = StripTags(s, "");
            s = WebUtility.HtmlDecode(s).Replace('\u0001', '<').Replace('\u0002', '>');
            return NormalizeText(s);
        }
        catch (RegexMatchTimeoutException)
        {
            return NormalizeText(WebUtility.HtmlDecode(StripTags(html, " ")));
        }
    }

    // Tags removed in one pass (no regex: many unclosed "<" in a crafted mail cannot make it slow); a "<" without a
    // closing ">" keeps the rest as text.
    public static string StripTags(string s, string replacement)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int lt = s.IndexOf('<', i);
            if (lt < 0) { sb.Append(s, i, s.Length - i); break; }
            int gt = s.IndexOf('>', lt + 1);
            if (gt < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, lt - i).Append(replacement);
            i = gt + 1;
        }
        return sb.ToString();
    }

    // Line ends to \n, no-break and zero-width characters to spaces/nothing, spaces collapsed, at most one blank line.
    public static string NormalizeText(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
        s = Regex.Replace(s, "[\u200B-\u200D\u2060\uFEFF\u034F\u00AD]", "");
        s = Regex.Replace(s, @"[ \t\f\v]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    public static string Snippet(string text, int max = SnippetChars)
    {
        string s = Regex.Replace(text, @"\s+", " ").Trim();
        if (s.Length <= max) return s;
        int cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut] + "…";
    }

    // A file name safe for a connected folder: no paths, control or reserved characters, at most 120 characters.
    public static string SafeFileName(string name, int index)
    {
        string n = (name ?? "").Replace('\\', '/').Split('/').Last();   // not Path.GetFileName: "a:b" is no drive here
        n = new string(n.Select(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray()).Trim().Trim('.').Trim();
        if (n.Length == 0) n = "attachment-" + index;
        if (n.Length > 120)
        {
            string ext = Path.GetExtension(n);
            if (ext.Length > 12) ext = "";
            n = n[..(120 - ext.Length)].TrimEnd() + ext;
        }
        return n;
    }

    // ------------------------------------------------------------------ addresses

    public sealed record Address(string Name, string Email);

    // "Ann <ann@example.org>, "Doe, John" <j@example.com>; bob@example.net" → addresses; bad entries are refused with the entry named.
    public static List<Address> ParseAddresses(string? list)
    {
        var o = new List<Address>();
        if (string.IsNullOrWhiteSpace(list)) return o;
        NoLineBreaks(list, "address");
        var items = new List<string>();
        var sb = new StringBuilder();
        bool q = false, angle = false;
        foreach (char c in list)
        {
            if (c == '"' && !angle) q = !q;
            else if (c == '<' && !q) angle = true;
            else if (c == '>' && !q) angle = false;
            if ((c == ',' || c == ';') && !q && !angle) { items.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        items.Add(sb.ToString());
        foreach (string raw in items.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var m = Regex.Match(raw, @"\A(.*?)\s*<\s*([^<>\s]+)\s*>\z", RegexOptions.Singleline);
            string name = m.Success ? m.Groups[1].Value.Trim() : "";
            string email = m.Success ? m.Groups[2].Value : raw;
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = Regex.Replace(name[1..^1], @"\\(.)", "$1");
            if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email[7..];
            if (!IsEmail(email)) throw new ToolException("invalid_args", $"Not an e-mail address: '{(raw.Length > 80 ? raw[..80] + "…" : raw)}'.");
            o.Add(new Address(DecodeWords(name), email));   // after splitting: an encoded name may hold a comma
        }
        if (o.Count > MaxRecipients) throw new ToolException("invalid_args", $"At most {MaxRecipients} recipients.");
        return o;
    }

    public static string FormatAddress(Address a)
    {
        if (a.Name.Length == 0) return a.Email;
        string name = !a.Name.All(c => c is >= ' ' and <= '~') ? EncodeWords(a.Name)
            : a.Name.IndexOfAny("()<>@,;:\\\".[]".ToCharArray()) < 0 ? a.Name
            : "\"" + a.Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return name + " <" + a.Email + ">";
    }

    // RFC 2047 B-words of UTF-8 for non-ASCII header text (at most 45 bytes per word, never half a character).
    public static string EncodeWords(string s)
    {
        if (s.All(c => c is >= ' ' and <= '~')) return s;
        var words = new List<string>();
        var cur = new List<byte>();
        var e = new System.Globalization.StringInfo(s);
        for (int i = 0; i < e.LengthInTextElements; i++)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(e.SubstringByTextElements(i, 1));
            if (cur.Count + b.Length > 45 && cur.Count > 0) { words.Add(Word(cur)); cur.Clear(); }
            cur.AddRange(b);
        }
        if (cur.Count > 0) words.Add(Word(cur));
        return string.Join("\r\n ", words);
        static string Word(List<byte> b) => "=?UTF-8?B?" + Convert.ToBase64String(b.ToArray()) + "?=";
    }

    // ------------------------------------------------------------------ drafts

    public sealed record DraftFile(string Name, string ContentType, byte[] Data);

    public sealed class Draft
    {
        public required Address From { get; init; }
        public List<Address> To { get; init; } = new();
        public List<Address> Cc { get; init; } = new();
        public List<Address> Bcc { get; init; } = new();
        public string Subject { get; init; } = "";
        public string Body { get; init; } = "";
        public string? InReplyTo { get; init; }
        public string? References { get; init; }
        public List<DraftFile> Files { get; init; } = new();
        public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
        public string MessageId { get; init; } = "";
        public string Boundary { get; init; } = "";
    }

    // Reply fields from the original: In-Reply-To, References (at most the last 20 ids), "Re: " subject, and recipients
    // (Reply-To, else From; for a reply to one's own mail, its To).
    public static (string InReplyTo, string References, string Subject, List<Address> To) ReplyFields(Mail orig, string account)
    {
        string id = orig.MessageId;
        if (!Regex.IsMatch(id, @"\A<[^<>\s]+>\z")) throw new ToolException("no_message_id", "The mail to reply to has no Message-ID; write a new mail instead (to, subject).");
        var refs = Regex.Matches(orig.References.Length > 0 ? orig.References : orig.InReplyTo, @"<[^<>\s]+>").Select(m => m.Value).ToList();
        refs.Add(id);
        string references = string.Join(" ", refs.Distinct().TakeLast(20));
        string subj = NormalizeText(orig.Subject).Replace('\n', ' ');
        if (!Regex.IsMatch(subj, @"\A(re|aw|sv|antw|vs)\s*:", RegexOptions.IgnoreCase)) subj = "Re: " + subj;
        List<Address> to;
        try
        {
            var from = ParseAddresses(orig.RawHeader("From"));
            to = from.Any(a => a.Email.Equals(account, StringComparison.OrdinalIgnoreCase)) ? ParseAddresses(orig.RawHeader("To"))
                : orig.RawHeader("Reply-To").Length > 0 ? ParseAddresses(orig.RawHeader("Reply-To")) : from;
        }
        catch (ToolException) { to = new List<Address>(); }
        return (id, references, subj, to);
    }

    // The quoted original below a reply body ("On <date>, <from> wrote:" and "> " lines, at most 20 000 characters).
    public static string Quote(Mail orig)
    {
        string t = orig.Text.Length > 20_000 ? orig.Text[..20_000] + "\n[…]" : orig.Text;
        return $"On {orig.Date}, {orig.From} wrote:\n" + string.Join("\n", t.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
    }

    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".csv" => "text/csv", ".html" or ".htm" => "text/html",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".heic" => "image/heic",
        ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip", ".ics" => "text/calendar", ".eml" => "message/rfc822", ".pages" => "application/vnd.apple.pages",
        _ => "application/octet-stream",
    };

    public static string RfcDate(DateTimeOffset d)
    {
        var inv = CultureInfo.InvariantCulture;
        var off = d.Offset;
        string sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return d.ToString("ddd, dd MMM yyyy HH:mm:ss ", inv) + sign + off.Hours.ToString("00", inv) + off.Minutes.ToString("00", inv);
    }

    // The draft as an RFC 5322 message: UTF-8 text in base64, attachments as base64 parts. Every header value was checked
    // for line breaks (no header injection); the subject and names are RFC 2047 encoded when not ASCII.
    public static byte[] BuildDraft(Draft d)
    {
        NoLineBreaks(d.Subject, "subject");
        NoLineBreaks(d.InReplyTo, "In-Reply-To");
        NoLineBreaks(d.References, "References");
        if (d.Subject.Length > MaxSubjectChars) throw new ToolException("invalid_args", $"subject: at most {MaxSubjectChars} characters.");
        if (d.Body.Length > MaxBodyChars) throw new ToolException("invalid_args", $"body: at most {MaxBodyChars} characters.");
        if (d.To.Count + d.Cc.Count + d.Bcc.Count > MaxRecipients) throw new ToolException("invalid_args", $"At most {MaxRecipients} recipients.");
        long files = d.Files.Sum(f => (long)f.Data.Length);
        if (files > MaxAttachBytes) throw new ToolException("too_large", $"Attachments are {files / 1048576.0:0.0} MB; Gmail takes at most 25 MB.");
        string boundary = d.Boundary.Length > 0 ? d.Boundary : "co-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        void H(string name, string value) { if (value.Length > 0) sb.Append(name).Append(": ").Append(value).Append("\r\n"); }
        H("Date", RfcDate(d.Date));
        H("From", FormatAddress(d.From));
        H("To", string.Join(",\r\n ", d.To.Select(FormatAddress)));
        H("Cc", string.Join(",\r\n ", d.Cc.Select(FormatAddress)));
        H("Bcc", string.Join(",\r\n ", d.Bcc.Select(FormatAddress)));
        H("Subject", EncodeWords(d.Subject));
        H("Message-ID", d.MessageId);
        H("In-Reply-To", d.InReplyTo ?? "");
        H("References", string.Join("\r\n ", Regex.Matches(d.References ?? "", @"<[^<>\s]+>").Select(m => m.Value)));
        H("MIME-Version", "1.0");
        string text = Base64Lines(System.Text.Encoding.UTF8.GetBytes(d.Body.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        if (d.Files.Count == 0)
        {
            H("Content-Type", "text/plain; charset=utf-8");
            H("Content-Transfer-Encoding", "base64");
            sb.Append("\r\n").Append(text);
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }
        H("Content-Type", $"multipart/mixed; boundary=\"{boundary}\"");
        sb.Append("\r\n--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(text);
        foreach (var f in d.Files)
        {
            string name = SafeFileName(f.Name, 1);
            sb.Append("\r\n--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: ").Append(f.ContentType).Append("; ").Append(FileParam("name", name)).Append("\r\n");
            sb.Append("Content-Disposition: attachment; ").Append(FileParam("filename", name)).Append("\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n").Append(Base64Lines(f.Data));
        }
        sb.Append("\r\n--").Append(boundary).Append("--\r\n");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string FileParam(string key, string name) =>
        name.All(c => c is >= ' ' and <= '~' and not '"' and not '\\')
            ? $"{key}=\"{name}\""
            : $"{key}*=UTF-8''" + string.Concat(System.Text.Encoding.UTF8.GetBytes(name).Select(b =>
                b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-' or (byte)'_'
                    ? ((char)b).ToString() : "%" + b.ToString("X2", CultureInfo.InvariantCulture)));

    private static string Base64Lines(byte[] data)
    {
        string b = Convert.ToBase64String(data);
        var sb = new StringBuilder(b.Length + b.Length / 76 * 2 + 2);
        for (int i = 0; i < b.Length; i += 76) sb.Append(b, i, Math.Min(76, b.Length - i)).Append("\r\n");
        return sb.ToString();
    }
}

// Rights of the Gmail connector in mail.json {"rights": "Read, Create"}; no file = Read + Create (the app password the
// person typed is the consent to reading and drafts), unreadable = off. Modes for the menu and the command line.
public sealed class MailSettings
{
    private readonly string _file;
    public MailSettings(string file) => _file = file;

    public const Rights Default = Rights.Read | Rights.Create;

    public Rights Rights
    {
        get
        {
            try
            {
                if (!File.Exists(_file)) return Default;
                string? r = JsonNode.Parse(File.ReadAllText(_file))?["rights"]?.GetValue<string>();
                return r == null ? Rights.None : ConnectorRegistry.Parse(r) & (Rights.Read | Rights.Create | Rights.Edit);
            }
            catch (Exception) { return Rights.None; }
        }
        set => File.WriteAllText(_file, new JsonObject { ["rights"] = value == Rights.None ? "None" : value.ToString() }.ToJsonString());
    }

    public static readonly string[] Modes = { "off", "read", "drafts", "labels" };

    public static Rights ModeRights(string mode) => mode switch
    {
        "read" => Rights.Read,
        "drafts" => Rights.Read | Rights.Create,
        "labels" => Rights.Read | Rights.Create | Rights.Edit,
        _ => Rights.None,
    };

    public string Mode
    {
        get
        {
            var r = Rights;
            return !r.HasFlag(Rights.Read) ? "off" : r.HasFlag(Rights.Edit) && r.HasFlag(Rights.Create) ? "labels" : r.HasFlag(Rights.Create) ? "drafts" : "read";
        }
        set => Rights = ModeRights(value);
    }

    public static string Describe(string mode) => mode switch
    {
        "read" => "read only (search and read mail)",
        "drafts" => "read, drafts and saved attachments (never sent)",
        "labels" => "read, drafts, attachments, labels / read marks / archive",
        _ => "off",
    };
}

// The IMAP protocol of the Gmail tools over any stream (TLS in MacMail.cs ImapSession, a scripted stream in the tests).
public class ImapClient
{
    public sealed class ImapError : Exception
    {
        public ImapError(string message, bool login = false) : base(message) => Login = login;
        public bool Login { get; }
    }

    private readonly Stream _io;
    private readonly MacMailRules.Reader _r;
    private int _tag;
    private (string? All, string? Drafts)? _boxes;

    public ImapClient(Stream io)
    {
        _io = io;
        _r = new MacMailRules.Reader(io);
    }

    // "* OK" greeting of the server.
    public void Greeting()
    {
        byte[] greet = _r.ReadResponse(0);
        if (!Encoding.ASCII.GetString(greet).StartsWith("* OK", StringComparison.OrdinalIgnoreCase))
            throw new ImapError("unexpected greeting");
    }

    private string NextTag() => "A" + (++_tag).ToString("000", System.Globalization.CultureInfo.InvariantCulture);

    private void Write(string s) => _io.Write(Encoding.UTF8.GetBytes(s));

    // Waits for "+ " (continuation); a tagged answer instead is the server's refusal.
    private void AwaitContinuation(string tag, List<List<Node>> untagged, bool login)
    {
        while (true)
        {
            byte[] r = _r.ReadResponse();
            if (r.Length > 0 && r[0] == '+') return;
            if (StartsWithTag(r, tag)) throw new ImapError(StatusText(r), login);
            untagged.Add(Tokens(r));
        }
    }

    private static bool StartsWithTag(byte[] r, string tag) =>
        r.Length > tag.Length && r[tag.Length] == ' ' && Encoding.ASCII.GetString(r, 0, tag.Length) == tag;

    // One command (text parts and byte[] literals); returns the untagged responses, throws on NO/BAD.
    public List<List<Node>> Command(IEnumerable<object> parts, long maxLiteral = MaxMessageBytes)
    {
        string tag = NextTag();
        var untagged = new List<List<Node>>();
        var line = new StringBuilder(tag).Append(' ');
        foreach (object p in parts)
        {
            if (p is string s) { line.Append(s); continue; }
            var lit = (byte[])p;
            line.Append('{').Append(lit.Length).Append("}\r\n");
            Write(line.ToString());
            line.Clear();
            _io.Flush();
            AwaitContinuation(tag, untagged, false);
            _io.Write(lit);
        }
        Write(line.Append("\r\n").ToString());
        _io.Flush();
        return Finish(tag, untagged, false, maxLiteral);
    }

    private List<List<Node>> Finish(string tag, List<List<Node>> untagged, bool login, long maxLiteral = MaxMessageBytes)
    {
        while (true)
        {
            byte[] r = _r.ReadResponse(maxLiteral);
            if (r.Length > 0 && r[0] == '+') continue;
            if (StartsWithTag(r, tag))
            {
                string t = Encoding.ASCII.GetString(r, tag.Length + 1, Math.Min(3, r.Length - tag.Length - 1));
                if (!t.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) throw new ImapError(StatusText(r), login);
                untagged.Add(Tokens(r));   // the tagged line last (APPENDUID and other response codes)
                return untagged;
            }
            untagged.Add(Tokens(r));
        }
    }

    // AUTHENTICATE PLAIN without an initial response (works with and without SASL-IR): the base64 line is sent only
    // after the server's "+". Nothing of it is logged.
    public void Login(string user, string password)
    {
        string tag = NextTag();
        var untagged = new List<List<Node>>();
        Write(tag + " AUTHENTICATE PLAIN\r\n");
        _io.Flush();
        AwaitContinuation(tag, untagged, true);
        Write(PlainAuth(user, password) + "\r\n");
        _io.Flush();
        Finish(tag, untagged, true);
    }

    public (string? All, string? Drafts) Mailboxes()
    {
        if (_boxes is { } b) return b;
        var boxes = ListMailboxes(Command(new object[] { "LIST \"\" \"*\"" }));
        _boxes = (SpecialUse(boxes, "\\All"), SpecialUse(boxes, "\\Drafts"));
        return _boxes.Value;
    }

    // All Mail (Gmail's whole mailbox, where X-GM-RAW searches like the web) or INBOX when All Mail is hidden from IMAP.
    public string SelectAll(bool write)
    {
        string box = Mailboxes().All ?? "INBOX";
        object name = Astring(box);
        string verb = write ? "SELECT " : "EXAMINE ";
        Command(name is string q ? new object[] { verb + q } : new object[] { verb, name });
        return box;
    }

    public List<long> Search(string gmailQuery) => SearchUids(Command(SearchCommand(gmailQuery)));

    public List<Dictionary<string, Node>> Fetch(IEnumerable<long> uids, string items, long maxLiteral = MaxMessageBytes) =>
        Command(new object[] { "UID FETCH " + UidSet(uids) + " (" + items + ")" }, maxLiteral)
            .Select(FetchItems).Where(d => d != null && d.ContainsKey("UID")).Select(d => d!).ToList();

    // The whole message (refused above MaxMessageBytes).
    public (Dictionary<string, Node> Meta, byte[] Raw) FetchMessage(long uid)
    {
        var meta = Fetch(new[] { uid }, "UID FLAGS INTERNALDATE RFC822.SIZE X-GM-LABELS X-GM-THRID").FirstOrDefault()
            ?? throw new ToolException("not_found", $"No mail with uid {uid} (uids come from mail_search; they belong to All Mail).");
        if (meta.TryGetValue("RFC822.SIZE", out var sz) && long.TryParse(sz.Text, out long size) && size > MaxMessageBytes)
            throw new ToolException("too_large", $"The mail is {size / 1048576} MB; at most {MaxMessageBytes / 1048576} MB.");
        var body = Fetch(new[] { uid }, "UID BODY.PEEK[]").FirstOrDefault(d => d.ContainsKey("BODY[]"));   // not an unsolicited FLAGS update
        if (body == null || !body.TryGetValue("BODY[]", out var raw)) throw new ToolException("not_found", $"Mail uid {uid} has no body.");
        return (meta, raw.Bytes);
    }

    // APPEND to Drafts with \Draft (never sent); returns the APPENDUID uid when the server gives one.
    public string AppendDraft(byte[] message)
    {
        string drafts = Mailboxes().Drafts
            ?? throw new ToolException("no_drafts", "Gmail shows no Drafts mailbox over IMAP (Gmail settings → Labels → Drafts: Show in IMAP).");
        var box = Astring(drafts);
        var parts = new List<object>();
        if (box is string q) parts.Add("APPEND " + q + " (\\Draft \\Seen) ");
        else { parts.Add("APPEND "); parts.Add(box); parts.Add(" (\\Draft \\Seen) "); }
        parts.Add(message);
        var resp = Command(parts);
        var tagged = resp.Last();
        string code = tagged.FirstOrDefault(n => n.Atom.StartsWith("[APPENDUID", StringComparison.OrdinalIgnoreCase))?.Atom ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(code, @"\[APPENDUID \d+ (\d+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }
}
