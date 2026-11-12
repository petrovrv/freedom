// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;

namespace CO.Desktop.Platform;

// 0.8.4: CO browser windows get their own taskbar group and icon (co-browser.ico), so they differ from the user's Edge.
// 0.9.5: Edge rewrites the window's AppUserModelID (MSEdge.browser.<profile>) later, e.g. on profile updates;
// every poll reads it back and applies ours again when it differs.
public static class BrowserWindowBadge
{
    private const string AppId = "AIWorkbench.CO.Browser"; // AppUserModelID to apply
    private const string DisplayName = "CO browser (agent)"; // Relaunch display name
    private const string TargetClass = "Chrome_WidgetWin_1"; // Chromium top-level window class

    private const ushort VT_LPWSTR = 31; // PROPVARIANT string type
    private const uint IMAGE_ICON = 1; // LoadImage icon type
    private const uint LR_LOADFROMFILE = 0x10; // Load from file path
    private const int SM_CXICON = 11; // Large icon metric
    private const int SM_CXSMICON = 49; // Small icon metric
    private const uint WM_SETICON = 0x80; // Set window icon message
    private const int ICON_SMALL = 0; // Small icon slot
    private const int ICON_BIG = 1; // Big icon slot
    private const uint SMTO_ABORTIFHUNG = 2; // Don't wait on hung windows
    private const uint SendTimeoutMs = 1000; // SendMessageTimeout timeout

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); // IPropertyStore IID
    private static readonly Guid AppUserModelFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); // PKEY_AppUserModel fmtid

    private static readonly PropertyKey PKEY_AppUserModel_ID = new(AppUserModelFmtId, 5); // AppUserModel ID
    private static readonly PropertyKey PKEY_RelaunchCommand = new(AppUserModelFmtId, 2); // Relaunch command (required with the name)
    private static readonly PropertyKey PKEY_RelaunchIconResource = new(AppUserModelFmtId, 3); // Relaunch icon
    private static readonly PropertyKey PKEY_RelaunchDisplayNameResource = new(AppUserModelFmtId, 4); // Relaunch name

    public static async Task RunAsync(Func<CancellationToken, Task<int>> browserPid, string iconPath, string relaunchCommand, CancellationToken ct)
    {
        var iconsLoaded = false; // Icons load once flag
        IntPtr bigIcon = IntPtr.Zero, smallIcon = IntPtr.Zero; // Cached icon handles

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pid = await browserPid(ct).ConfigureAwait(false); // Resolve current browser pid
                if (pid > 0)
                {
                    if (!iconsLoaded)
                    {
                        iconsLoaded = true; // Only attempt once
                        bigIcon = LoadIcon(iconPath, GetSystemMetrics(SM_CXICON)); // Large icon
                        smallIcon = LoadIcon(iconPath, GetSystemMetrics(SM_CXSMICON)); // Small icon
                    }

                    foreach (var hwnd in FindWindows((uint)pid))
                    {
                        try
                        {
                            if (HasOurAppId(hwnd)) continue; // Still ours; Edge has not reset it
                            ApplyProperties(hwnd, iconPath, relaunchCommand); // Set AppUserModel properties
                            ApplyIcons(hwnd, bigIcon, smallIcon); // Set window icons
                        }
                        catch
                        {
                            // Swallow per-window errors
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // Quiet cancellation
            }
            catch
            {
                // Swallow per-iteration errors
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); // Poll interval
            }
            catch (OperationCanceledException)
            {
                return; // Quiet cancellation
            }
        }
    }

    private static IntPtr LoadIcon(string path, int size)
    {
        try
        {
            return LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE); // Load icon from file
        }
        catch
        {
            return IntPtr.Zero; // Treat failure as no icon
        }
    }

    private static List<IntPtr> FindWindows(uint pid)
    {
        var result = new List<IntPtr>(); // Matching windows
        var className = new StringBuilder(256); // Class name buffer

        EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true; // Visible only
                GetWindowThreadProcessId(hwnd, out var wpid);
                if (wpid != pid) return true; // Same process only
                className.Clear();
                if (GetClassName(hwnd, className, className.Capacity) == 0) return true; // Class lookup failed
                if (!string.Equals(className.ToString(), TargetClass, StringComparison.Ordinal)) return true; // Class filter
                if (GetWindowTextLength(hwnd) <= 0) return true; // Non-empty title only
                result.Add(hwnd);
            }
            catch
            {
                // Ignore per-window enumeration errors
            }
            return true; // Continue enumeration
        };

        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback); // Keep delegate alive during enumeration
        return result;
    }

    private static void ApplyProperties(IntPtr hwnd, string iconPath, string relaunchCommand)
    {
        var iid = IID_IPropertyStore; // Local copy for ref
        var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var obj);
        if (hr < 0 || obj is null) Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005)); // Fail on error

        try
        {
            var store = (IPropertyStore)obj!; // Cast to interface
            SetString(store, PKEY_RelaunchCommand, relaunchCommand); // Relaunch command
            SetString(store, PKEY_RelaunchDisplayNameResource, DisplayName); // Relaunch name
            SetString(store, PKEY_RelaunchIconResource, iconPath + ",0"); // Relaunch icon
            SetString(store, PKEY_AppUserModel_ID, AppId); // App ID last, after the relaunch properties
            Check(store.Commit()); // Persist changes
        }
        finally
        {
            Marshal.ReleaseComObject(obj!); // Release COM object
        }
    }

    private static bool HasOurAppId(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore; // Local copy for ref
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var obj) < 0 || obj is null) return false; // Unknown: apply
        try
        {
            var key = PKEY_AppUserModel_ID; // Local copy for ref
            if (((IPropertyStore)obj).GetValue(ref key, out var pv) < 0) return false;
            try
            {
                return pv.vt == VT_LPWSTR && Marshal.PtrToStringUni(pv.p) == AppId; // Current AppUserModelID
            }
            finally
            {
                PropVariantClear(ref pv); // Free the returned copy
            }
        }
        finally
        {
            Marshal.ReleaseComObject(obj); // Release COM object
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(value) }; // Build LPWSTR variant
        try
        {
            var k = key; // Local copy for ref
            Check(store.SetValue(ref k, ref pv)); // Store copies the value
        }
        finally
        {
            PropVariantClear(ref pv); // Free CoTaskMem string
        }
    }

    private static void ApplyIcons(IntPtr hwnd, IntPtr bigIcon, IntPtr smallIcon)
    {
        if (bigIcon != IntPtr.Zero)
            SendMessageTimeout(hwnd, WM_SETICON, (IntPtr)ICON_BIG, bigIcon, SMTO_ABORTIFHUNG, SendTimeoutMs, out _); // Big icon
        if (smallIcon != IntPtr.Zero)
            SendMessageTimeout(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, smallIcon, SMTO_ABORTIFHUNG, SendTimeoutMs, out _); // Small icon
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr); // Throw on failed HRESULT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid; // Format id
        public uint pid; // Property id

        public PropertyKey(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt; // Variant type
        public ushort r1; // Reserved
        public ushort r2; // Reserved
        public ushort r3; // Reserved
        public IntPtr p; // Value pointer
        public IntPtr p2; // Padding to native size
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps); // Property count
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey); // Key at index
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv); // Read value
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv); // Write value
        [PreserveSig] int Commit(); // Save changes
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam); // EnumWindows callback

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
