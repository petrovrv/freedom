// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CO.Mac;

// Minimal Objective-C runtime bridge for the CO window (R4): AppKit and WebKit through objc_msgSend, with no UI
// framework and no Xcode, so the window builds on Windows like the rest of the resident. arm64 only: objc_msgSend is
// called with the exact signature of each method (no _stret variants), BOOL is one byte.
internal static unsafe class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Lib)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] private static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Lib)] private static extern IntPtr objc_getProtocol(string name);
    [DllImport(Lib)] private static extern byte class_addProtocol(IntPtr cls, IntPtr protocol);

    private static readonly IntPtr MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(Lib), "objc_msgSend");
    private static readonly ConcurrentDictionary<string, IntPtr> Sels = new();
    private static readonly ConcurrentDictionary<string, IntPtr> Classes = new();

    public static void LoadFrameworks()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");
    }

    public static IntPtr Sel(string name) => Sels.GetOrAdd(name, sel_registerName);
    public static IntPtr Cls(string name) => Classes.GetOrAdd(name, n =>
    {
        IntPtr c = objc_getClass(n);
        return c != IntPtr.Zero ? c : throw new InvalidOperationException("Objective-C class not found: " + n);
    });

    // A subclass of NSObject whose methods are C# functions marked [UnmanagedCallersOnly].
    public static IntPtr DefineClass(string name, string[] protocols, params (string sel, IntPtr imp, string types)[] methods)
    {
        IntPtr existing = objc_getClass(name);
        if (existing != IntPtr.Zero) return existing;
        IntPtr cls = objc_allocateClassPair(Cls("NSObject"), name, 0);
        foreach (var p in protocols)
            if (objc_getProtocol(p) is var proto && proto != IntPtr.Zero) class_addProtocol(cls, proto);   // conformsToProtocol: checks
        foreach (var (sel, imp, types) in methods)
            if (class_addMethod(cls, Sel(sel), imp, types) == 0) throw new InvalidOperationException("class_addMethod failed: " + sel);
        objc_registerClassPair(cls);
        return cls;
    }

    // ------------------------------------------------------------------ objc_msgSend with fixed signatures

    public static IntPtr Send(IntPtr o, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(o, Sel(sel));
    public static IntPtr Send(IntPtr o, string sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(o, Sel(sel), a);
    public static IntPtr Send(IntPtr o, string sel, IntPtr a, IntPtr b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(o, Sel(sel), a, b);
    public static IntPtr Send(IntPtr o, string sel, IntPtr a, IntPtr b, IntPtr c) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(o, Sel(sel), a, b, c);
    public static IntPtr SendN(IntPtr o, string sel, nint a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nint, IntPtr>)MsgSend)(o, Sel(sel), a);
    public static nint SendRetN(IntPtr o, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nint>)MsgSend)(o, Sel(sel));
    public static void SendBool(IntPtr o, string sel, bool v) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)MsgSend)(o, Sel(sel), v ? (byte)1 : (byte)0);
    public static bool SendRetBool(IntPtr o, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSend)(o, Sel(sel)) != 0;
    public static bool RespondsTo(IntPtr o, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(o, Sel("respondsToSelector:"), Sel(sel)) != 0;
    public static IntPtr SendD(IntPtr o, string sel, double v) =>
        ((delegate* unmanaged<IntPtr, IntPtr, double, IntPtr>)MsgSend)(o, Sel(sel), v);
    public static bool IsKind(IntPtr o, IntPtr cls) =>
        o != IntPtr.Zero && ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(o, Sel("isKindOfClass:"), cls) != 0;
    // -[NSObject performSelector:withObject:afterDelay:] with delay 0: on the next turn of the run loop.
    public static void Later(IntPtr o, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, double, void>)MsgSend)(o, Sel("performSelector:withObject:afterDelay:"), Sel(sel), IntPtr.Zero, 0.0);
    public static IntPtr SendRect(IntPtr o, string sel, CGRect r) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CGRect, IntPtr>)MsgSend)(o, Sel(sel), r);
    public static IntPtr SendRect(IntPtr o, string sel, CGRect r, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CGRect, IntPtr, IntPtr>)MsgSend)(o, Sel(sel), r, a);
    // -[NSWindow initWithContentRect:styleMask:backing:defer:]
    public static IntPtr InitWindow(IntPtr o, CGRect r, nuint style) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CGRect, nuint, nuint, byte, IntPtr>)MsgSend)(o, Sel("initWithContentRect:styleMask:backing:defer:"), r, style, 2, 0);
    // -[WKUserScript initWithSource:injectionTime:forMainFrameOnly:]
    public static IntPtr InitUserScript(IntPtr o, IntPtr source, nint time, bool mainOnly) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint, byte, IntPtr>)MsgSend)(o, Sel("initWithSource:injectionTime:forMainFrameOnly:"), source, time, mainOnly ? (byte)1 : (byte)0);
    // +[NSTimer scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:]
    public static IntPtr Timer(double seconds, IntPtr target, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, double, IntPtr, IntPtr, IntPtr, byte, IntPtr>)MsgSend)(
            Cls("NSTimer"), Sel("scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:"), seconds, target, Sel(sel), IntPtr.Zero, 1);

    public static IntPtr New(string cls) => Send(Send(Cls(cls), "alloc"), "init");

    // ------------------------------------------------------------------ Foundation values

    public static IntPtr Str(string s)
    {
        IntPtr p = Marshal.StringToCoTaskMemUTF8(s);
        try { return Send(Cls("NSString"), "stringWithUTF8String:", p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? FromStr(IntPtr ns) => ns == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(ns, "UTF8String"));

    public static IntPtr Url(string s) => Send(Cls("NSURL"), "URLWithString:", Str(s));
    public static IntPtr FileUrl(string path) => Send(Cls("NSURL"), "fileURLWithPath:", Str(path));
    public static IntPtr Yes => Send(Cls("NSNumber"), "numberWithBool:", (IntPtr)1);

    // ------------------------------------------------------------------ blocks handed to us by AppKit/WebKit

    // Block layout: isa, flags (int), reserved (int), invoke, descriptor; the first argument of invoke is the block.
    private static IntPtr Invoke(IntPtr block) => *(IntPtr*)((byte*)block + 16);
    public static void CallBlock(IntPtr block) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, void>)Invoke(block))(block); }
    public static void CallBlockN(IntPtr block, nint v) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, nint, void>)Invoke(block))(block, v); }
    public static void CallBlock(IntPtr block, IntPtr obj) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, IntPtr, void>)Invoke(block))(block, obj); }
    public static void CallBlockBool(IntPtr block, bool v) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, byte, void>)Invoke(block))(block, v ? (byte)1 : (byte)0); }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X, Y, W, H;
    public CGRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
}
