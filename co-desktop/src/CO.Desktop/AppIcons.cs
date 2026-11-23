// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CO.Desktop;

// Freedom F14e.2 (SPEC §3.6a п. 9): the icon of an app of the launcher on this computer - its glyph from the module list
// (ui.page icon, an emoji or a letter) white on a rounded square of a colour chosen by the app, so each app window has
// its own button on the taskbar, in Alt+Tab, in the jump list and in the tray menu. The .ico is kept in app-icons\ of the
// data folder, where the taskbar finds it for a pinned app window (relaunch icon) and the jump list for its tasks.
internal static class AppIcons
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x0E, 0x9F, 0x6E), Color.FromRgb(0xD9, 0x46, 0x0F),
        Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0x0F, 0x76, 0x6E),
        Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0x47, 0x55, 0x69),
    };
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
    private static readonly Dictionary<string, (string Path, BitmapFrame Image)> Made = new(StringComparer.Ordinal);

    public static string Folder => Path.Combine(DesktopSettings.DataDir, "app-icons");

    /// <summary>The .ico file and the window icon of an app ("module/page"); made once per glyph and run.</summary>
    public static (string Path, BitmapFrame Image) For(string key, string glyph)
    {
        string id = key + "\n" + glyph;
        if (Made.TryGetValue(id, out var done)) return done;
        byte[] ico = Ico(glyph, key);
        string path = Path.Combine(Folder, key.Replace('/', '.') + ".ico");
        try
        {
            Directory.CreateDirectory(Folder);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(ico))
            {
                File.WriteAllBytes(path + ".tmp", ico);
                File.Move(path + ".tmp", path, true);
            }
        }
        catch (IOException ex) { Log.Write("app icon " + key + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Write("app icon " + key + ": " + ex.Message); }
        var decoder = new IconBitmapDecoder(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var r = (path, decoder.Frames.OrderByDescending(f => f.PixelWidth).First());
        Made[id] = r;
        return r;
    }

    /// <summary>A small picture for a WinForms menu (the tray menu).</summary>
    public static System.Drawing.Bitmap MenuImage(string key, string glyph)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(glyph, key, 32)));
        var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var full = new System.Drawing.Bitmap(ms);
        return new System.Drawing.Bitmap(full, 16, 16);
    }

    internal static BitmapSource Render(string glyph, string seed, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new SolidColorBrush(Palette[Hash(seed) % (uint)Palette.Length]);
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            var face = new Typeface(new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new FormattedText(glyph.Length > 0 ? glyph : "▫", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face,
                size * 0.62, Brushes.White, 1.0);
            if (text.Width > size * 0.9) text.SetFontSize(size * 0.62 * size * 0.9 / text.Width);   // a longer text fits in
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // a .ico of PNG images (Windows Vista and later read them for every size)
    private static byte[] Ico(string glyph, string seed)
    {
        var pngs = Sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(glyph, seed, s)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)Sizes.Length);
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            byte d = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
            w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        w.Flush();
        return o.ToArray();
    }

    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
