// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a: headless UI of the .NET 8 Bridge on macOS/Linux (BRIDGE_HEADLESS, the net8.0 target).
//
// The shared sources use the WinForms subset below (tray icon and menu, a UI timer, message boxes, the
// ApplicationContext message loop). This namespace gives the same names without a desktop: Application.Run is a
// single-threaded work loop (UI timer ticks run on it one at a time, as on the WinForms UI thread), SIGTERM/SIGINT
// (launchd/systemd stop, Ctrl+C) end it like the tray's Exit, message boxes are logged and answer the safe button
// (No/Cancel/OK), the file dialog is cancelled, and the tray text/state goes to <state root>/tray.json for a menu bar
// host (CO Resident) to show.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AiWorkbenchBridge.HeadlessUi
{
    internal enum DialogResult { None = 0, OK = 1, Cancel = 2, Abort = 3, Retry = 4, Ignore = 5, Yes = 6, No = 7 }

    internal enum MessageBoxButtons { OK = 0, OKCancel = 1, AbortRetryIgnore = 2, YesNoCancel = 3, YesNo = 4, RetryCancel = 5 }

    internal enum MessageBoxIcon { None = 0, Error = 16, Question = 32, Warning = 48, Information = 64 }

    internal enum UnhandledExceptionMode { Automatic = 0, ThrowException = 1, CatchException = 2 }

    internal class ApplicationContext : IDisposable
    {
        public void ExitThread()
        {
            ExitThreadCore();
        }

        protected virtual void ExitThreadCore()
        {
            Application.Quit();
        }

        // SIGTERM/SIGINT: the tray's Exit without its question (BridgeApplicationContext overrides it).
        protected internal virtual void OnStopSignal()
        {
            ExitThread();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    internal static class Application
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static volatile bool _quit;
        private static int _loopThreadId;

        public static event ThreadExceptionEventHandler ThreadException;

        public static void SetUnhandledExceptionMode(UnhandledExceptionMode mode) { }
        public static void EnableVisualStyles() { }
        public static void SetCompatibleTextRenderingDefault(bool value) { }

        public static void Run(ApplicationContext context)
        {
            _loopThreadId = Environment.CurrentManagedThreadId;
            List<PosixSignalRegistration> signals = new List<PosixSignalRegistration>();
            foreach (PosixSignal s in new PosixSignal[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            {
                try
                {
                    signals.Add(PosixSignalRegistration.Create(s, delegate(PosixSignalContext c)
                    {
                        c.Cancel = true;   // the loop ends the process after a clean stop
                        BootstrapLog.Write("Signal " + c.Signal + ": stopping.");
                        Post(context.OnStopSignal);
                    }));
                }
                catch (PlatformNotSupportedException) { }
            }
            try
            {
                while (!_quit)
                {
                    Action a;
                    if (Queue.TryTake(out a, 500))
                        Invoke(a);
                }
            }
            finally
            {
                foreach (PosixSignalRegistration r in signals)
                    r.Dispose();
            }
        }

        // Before Run (startup waiting for Drive) the WinForms code pumps messages; here pending work runs on the
        // caller when it is the loop thread or no loop runs yet.
        public static void DoEvents()
        {
            if (_loopThreadId != 0 && _loopThreadId != Environment.CurrentManagedThreadId)
                return;
            Action a;
            while (Queue.TryTake(out a))
                Invoke(a);
        }

        public static void Post(Action action)
        {
            if (!_quit)
                Queue.Add(action);
        }

        internal static void Quit()
        {
            _quit = true;
        }

        private static void Invoke(Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                ThreadExceptionEventHandler h = ThreadException;
                if (h != null)
                    h(null, new ThreadExceptionEventArgs(ex));
                else
                    BootstrapLog.Write("UI loop exception: " + ex);
            }
        }
    }

    // WinForms Timer: ticks on the loop thread, never two at once, none queued twice.
    internal sealed class Timer : IDisposable
    {
        private System.Threading.Timer _timer;
        private int _pending;

        public int Interval { get; set; } = 100;
        public event EventHandler Tick;

        public void Start()
        {
            Stop();
            _timer = new System.Threading.Timer(delegate
            {
                if (Interlocked.Exchange(ref _pending, 1) == 1)
                    return;
                Application.Post(delegate
                {
                    Interlocked.Exchange(ref _pending, 0);
                    EventHandler h = Tick;
                    if (h != null)
                        h(this, EventArgs.Empty);
                });
            }, null, Interval, Interval);
        }

        public void Stop()
        {
            System.Threading.Timer t = _timer;
            _timer = null;
            if (t != null)
                t.Dispose();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal static class MessageBox
    {
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            DialogResult answer = SafeAnswer(buttons);
            BootstrapLog.Write("Message (" + icon + ", headless answer " + answer + "): " + caption + ": " + text);
            return answer;
        }

        internal static DialogResult SafeAnswer(MessageBoxButtons buttons)
        {
            switch (buttons)
            {
                case MessageBoxButtons.YesNo:
                case MessageBoxButtons.YesNoCancel:
                    return buttons == MessageBoxButtons.YesNo ? DialogResult.No : DialogResult.Cancel;
                case MessageBoxButtons.OK:
                    return DialogResult.OK;
                case MessageBoxButtons.AbortRetryIgnore:
                    return DialogResult.Abort;
                default:
                    return DialogResult.Cancel;
            }
        }
    }

    internal sealed class OpenFileDialog : IDisposable
    {
        public string Title { get; set; }
        public string Filter { get; set; }
        public string FileName { get; set; }

        public DialogResult ShowDialog()
        {
            BootstrapLog.Write("File dialog '" + Title + "' is not available headless; use --google-connect <client.json>.");
            return DialogResult.Cancel;
        }

        public void Dispose() { }
    }

    internal abstract class ToolStripItem
    {
        public string Text { get; set; }
        public bool Enabled { get; set; } = true;
    }

    internal sealed class ToolStripMenuItem : ToolStripItem
    {
        public ToolStripMenuItem(string text) { Text = text; }
        public event EventHandler Click;

        // A menu bar host may call this (not wired yet).
        internal void PerformClick()
        {
            EventHandler h = Click;
            if (h != null)
                h(this, EventArgs.Empty);
        }
    }

    internal sealed class ToolStripSeparator : ToolStripItem
    {
    }

    internal sealed class ContextMenuStrip
    {
        public List<ToolStripItem> Items { get; } = new List<ToolStripItem>();
    }

    // Tray icon: text and state go to tray.json (written on change, atomically).
    internal sealed class NotifyIcon : IDisposable
    {
        private string _text = "";
        private object _icon;
        private bool _visible;
        private string _written;

        public ContextMenuStrip ContextMenuStrip { get; set; }
#pragma warning disable 67
        public event EventHandler DoubleClick;
#pragma warning restore 67

        public string Text { get { return _text; } set { _text = value ?? ""; Publish(); } }
        public object Icon { get { return _icon; } set { _icon = value; Publish(); } }
        public bool Visible { get { return _visible; } set { _visible = value; Publish(); } }

        public static string StatusFile
        {
            get { return Path.Combine(BridgePlatform.Current.Paths.StateRoot, "tray.json"); }
        }

        private void Publish()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["visible"] = _visible;
            d["state"] = _icon == null ? "" : _icon.ToString();
            d["text"] = _text;
            List<string> items = new List<string>();
            if (ContextMenuStrip != null)
                foreach (ToolStripItem i in ContextMenuStrip.Items)
                    if (!(i is ToolStripSeparator) && !i.Enabled)
                        items.Add(i.Text);
            d["status"] = items;
            string json = JsonUtil.Serialize(d);
            if (json == _written)
                return;
            _written = json;
            try
            {
                string path = StatusFile;
                BridgePlatform.Current.Paths.EnsurePrivateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            _visible = false;
            Publish();
        }
    }
}

namespace AiWorkbenchBridge
{
    using AiWorkbenchBridge.HeadlessUi;

    internal sealed partial class BridgeApplicationContext
    {
        // Headless tray "icon": the state name (Idle, Busy, Paused, Problem) shown in tray.json.
        private object GetTrayIcon(TrayState state)
        {
            return state.ToString();
        }

        // SIGTERM/SIGINT: RequestExit without the "stop running jobs?" question (nobody can answer it).
        protected internal override void OnStopSignal()
        {
            if (_exiting)
                return;
            _exiting = true;
            foreach (KeyValuePair<string, WindowsJobObject> pair in _runningJobObjects)
            {
                try { pair.Value.Terminate(10); }
                catch { }
            }
            ExitThread();
        }
    }
}
