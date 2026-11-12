// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CO.Desktop;

// co.ico is embedded twice: as the Win32 application icon (Explorer, taskbar) and as a resource for WPF and the tray.
public static class AppIcon
{
    private static Stream Open() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream("CO.Desktop.co.ico")
        ?? throw new InvalidOperationException("Embedded icon missing.");

    public static System.Drawing.Icon Tray()
    {
        using var s = Open();
        return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    public static ImageSource Window()
    {
        using var s = Open();
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }
}
