// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;

namespace CO.Desktop.Platform;

// Freedom F14e.2: an app window of CO Desktop is its own app for the Windows shell - an AppUserModelID per app gives it
// its own taskbar button (not grouped under CO), and the relaunch properties make a pinned button start CO Desktop with
// --app <module>/<page>. The properties are cleared when the window goes (the shell keeps them otherwise).
internal static class WindowAppId
{
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly PropertyKey PKEY_RelaunchCommand = new(AppUserModelFmtId, 2);
    private static readonly PropertyKey PKEY_RelaunchIconResource = new(AppUserModelFmtId, 3);
    private static readonly PropertyKey PKEY_RelaunchDisplayNameResource = new(AppUserModelFmtId, 4);
    private static readonly PropertyKey PKEY_AppUserModel_ID = new(AppUserModelFmtId, 5);
    private const ushort VT_EMPTY = 0;
    private const ushort VT_LPWSTR = 31;

    /// <summary>AppUserModelID of an app window: at most 128 characters, no spaces.</summary>
    public static string For(string module, string page) => ("Freedom.CO.App." + module + "." + page) is var s && s.Length <= 128 ? s : s[..128];

    public static void Set(IntPtr hwnd, string appId, string relaunchCommand, string displayName, string iconPath)
    {
        try
        {
            Apply(hwnd, store =>
            {
                // the relaunch properties first, the id last (the shell reads them when the id is set)
                if (iconPath.Length > 0) SetString(store, PKEY_RelaunchIconResource, iconPath + ",0");
                SetString(store, PKEY_RelaunchCommand, relaunchCommand);
                SetString(store, PKEY_RelaunchDisplayNameResource, displayName);
                SetString(store, PKEY_AppUserModel_ID, appId);
            });
        }
        catch (Exception ex) { Log.Write("app window id: " + ex.Message); }
    }

    public static void Clear(IntPtr hwnd)
    {
        try
        {
            Apply(hwnd, store =>
            {
                foreach (var k in new[] { PKEY_AppUserModel_ID, PKEY_RelaunchCommand, PKEY_RelaunchDisplayNameResource, PKEY_RelaunchIconResource })
                {
                    var key = k;
                    var pv = new PropVariant { vt = VT_EMPTY };
                    store.SetValue(ref key, ref pv);
                }
            });
        }
        catch (Exception ex) { Log.Write("app window id clear: " + ex.Message); }
    }

    private static void Apply(IntPtr hwnd, Action<IPropertyStore> set)
    {
        if (hwnd == IntPtr.Zero) return;
        var iid = IID_IPropertyStore;
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var obj);
        if (hr < 0 || obj is null) Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
        try
        {
            var store = (IPropertyStore)obj!;
            set(store);
            Check(store.Commit());
        }
        finally { Marshal.ReleaseComObject(obj!); }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            var k = key;
            Check(store.SetValue(ref k, ref pv));
        }
        finally { PropVariantClear(ref pv); }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
