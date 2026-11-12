// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using System.Xml;
using CO.Core.Tools;

namespace CO.Core.Files;

public static class TextExtractor
{
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    public static string Extract(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new ToolException("not_found", "File not found.");
        if (fi.Length > MaxSourceBytes) throw new ToolException("too_large", $"File is {fi.Length} bytes; limit {MaxSourceBytes}.");

        switch (fi.Extension.ToLowerInvariant())
        {
            case ".pdf": return Pdf(path);
            case ".docx": return OfficeXml(path, n => n == "word/document.xml", "p");
            case ".pptx": return OfficeXml(path, n => n.StartsWith("ppt/slides/slide") && n.EndsWith(".xml"), "p");
            case ".xlsx": return Xlsx(path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        int probe = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < probe; i++)
            if (bytes[i] == 0) throw new ToolException("binary", "Binary file; no text extractor for this type.");
        return Decode(bytes);
    }

    public static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string Pdf(string path)
    {
        var sb = new StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            sb.Append("--- page ").Append(page.Number).AppendLine(" ---");
            sb.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
        }
        return sb.ToString();
    }

    // Paragraph text of WordprocessingML / DrawingML: concatenate <t> nodes, newline per <p>.
    private static string OfficeXml(string path, Func<string, bool> partFilter, string paragraph)
    {
        var sb = new StringBuilder();
        using var zip = ZipFile.OpenRead(path);
        var parts = zip.Entries.Where(e => partFilter(e.FullName))
            .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (parts.Count() > 1) sb.Append("--- ").Append(part.FullName).AppendLine(" ---");
            using var s = part.Open();
            using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t") sb.Append(r.ReadElementContentAsString());
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == paragraph) sb.AppendLine();
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "tab") sb.Append('\t');
            }
        }
        return sb.ToString();
    }

    private static string Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ss = zip.GetEntry("xl/sharedStrings.xml");
        if (ss != null)
        {
            using var r = XmlReader.Create(ss.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var cur = new StringBuilder();
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") cur.Clear();
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t") cur.Append(r.ReadElementContentAsString());
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") shared.Add(cur.ToString());
            }
        }

        var sb = new StringBuilder();
        foreach (var sheet in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
                     .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal))
        {
            sb.Append("--- ").Append(sheet.FullName).AppendLine(" ---");
            using var r = XmlReader.Create(sheet.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            string? cellRef = null, type = null;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                {
                    cellRef = r.GetAttribute("r");
                    type = r.GetAttribute("t");
                }
                else if (r.NodeType == XmlNodeType.Element && (r.LocalName == "v" || (r.LocalName == "t" && type == "inlineStr")))
                {
                    string v = r.ReadElementContentAsString();
                    if (type == "s" && int.TryParse(v, out int idx) && idx < shared.Count) v = shared[idx];
                    sb.Append(cellRef).Append('\t').AppendLine(v);
                }
            }
        }
        return sb.ToString();
    }
}

public static class LineDiff
{
    // Unified diff for the confirmation window. LCS on lines; above the cell limit only a summary.
    public static string Unified(string before, string after, string label, int context = 3, long maxCells = 4_000_000)
    {
        string[] a = SplitLines(before), b = SplitLines(after);
        if ((long)a.Length * b.Length > maxCells)
            return $"--- {label}\n+++ {label}\n(diff too large: {a.Length} → {b.Length} lines)\n";

        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<(char op, string text)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) ops.Add(('-', a[x++]));
            else ops.Add(('+', b[y++]));
        }
        while (x < n) ops.Add(('-', a[x++]));
        while (y < m) ops.Add(('+', b[y++]));

        var sb = new StringBuilder().Append("--- ").AppendLine(label).Append("+++ ").AppendLine(label);
        var keep = new bool[ops.Count];
        for (int k = 0; k < ops.Count; k++)
            if (ops[k].op != ' ')
                for (int c = Math.Max(0, k - context); c <= Math.Min(ops.Count - 1, k + context); c++) keep[c] = true;
        bool gap = false;
        for (int k = 0; k < ops.Count; k++)
        {
            if (!keep[k]) { gap = true; continue; }
            if (gap) { sb.AppendLine("@@"); gap = false; }
            sb.Append(ops[k].op).AppendLine(ops[k].text);
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string s) => s.Length == 0 ? Array.Empty<string>() : s.Replace("\r\n", "\n").Split('\n');
}
