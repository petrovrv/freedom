#!/usr/bin/env python3
"""secret-prompt: prompt for a secret in a small Tkinter window and store it in keyring.

The secret is never written to stdout, stderr, logs, or files.

Exit codes:
  0  saved (or --check: present)
  1  keyring / UI error
  2  cancelled
  3  timeout
  4  --check: missing
  5  refused: a household member's job (own_device_first; the keyring of this PC is the owner's)
"""
from __future__ import annotations

import argparse
import sys

try:
    import keyring
except ImportError as _exc:  # keyring is a hard requirement
    print("error: " + type(_exc).__name__, file=sys.stderr)
    sys.exit(1)

sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
from aiwb_secret import MemberRefused, get_secret, guard, set_secret  # noqa: E402  (long values are stored in chunks)

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_CANCEL = 2
EXIT_TIMEOUT = 3
EXIT_MISSING = 4
EXIT_REFUSED = 5

MASK = "•"  # bullet character used to mask input
DEFAULT_TITLE = "AI Workbench: save secret"
OVERWRITE_NOTE = "A value is already stored; saving replaces it."


class KeyringVerifyError(Exception):
    """Raised when the stored value does not match what was written."""


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="secret-prompt.py",
        description="Prompt for a secret in an always-on-top window and store it in keyring.",
        epilog="Exit codes: 0 saved/present, 1 error, 2 cancelled, 3 timeout, 4 missing (--check).",
    )
    parser.add_argument("--service", required=True, help="keyring service name, e.g. aiwb/co-mysql")
    parser.add_argument("--account", default="default", help="keyring account name (default: %(default)s)")
    parser.add_argument("--title", default=DEFAULT_TITLE, help="window title (default: %(default)r)")
    parser.add_argument("--label", default=None,
                        help="text above the field (default: 'Enter the secret for <service> / <account>')")
    parser.add_argument("--confirm", action="store_true", help="ask twice and require a match")
    parser.add_argument("--timeout", type=int, default=600,
                        help="seconds before the window closes with exit code 3 (default: %(default)s)")
    parser.add_argument("--overwrite", action="store_true",
                        help="do not show the 'already stored' note when a value exists")
    parser.add_argument("--check", action="store_true",
                        help="no window: print 'present' or 'missing' and exit 0/4")
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be a positive integer")
    if args.label is None:
        args.label = f"Enter the secret for {args.service} / {args.account}"
    return args


def enable_dpi_awareness() -> None:
    """Avoid blurry Tk rendering on high-DPI Windows displays."""
    if sys.platform != "win32":
        return
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass


def has_stored_value(service: str, account: str) -> bool:
    value = get_secret(service, account)
    present = value is not None
    del value
    return present


class SecretDialog:
    """Topmost window collecting and storing a secret."""

    def __init__(self, args: argparse.Namespace, already_stored: bool) -> None:
        import tkinter as tk
        from tkinter import ttk

        self.args = args
        self.outcome = EXIT_CANCEL
        self.saved_len = 0
        self.error_name = ""
        self._done = False
        self._timer: str | None = None

        self.root = root = tk.Tk()
        root.title(args.title)
        root.resizable(False, False)

        self.var1 = tk.StringVar(master=root)
        self.var2 = tk.StringVar(master=root) if args.confirm else None
        self.show_var = tk.BooleanVar(master=root, value=False)

        frame = ttk.Frame(root, padding=12)
        frame.grid(row=0, column=0, sticky="nsew")
        frame.columnconfigure(0, weight=1)

        row = 0
        ttk.Label(frame, text=args.label, wraplength=380, justify="left").grid(
            row=row, column=0, columnspan=2, sticky="w", pady=(0, 6))
        row += 1

        if already_stored:
            ttk.Label(frame, text=OVERWRITE_NOTE, foreground="#b36b00", wraplength=380).grid(
                row=row, column=0, columnspan=2, sticky="w", pady=(0, 6))
            row += 1

        self.entry1 = ttk.Entry(frame, textvariable=self.var1, show=MASK, width=44)
        self.entry1.grid(row=row, column=0, columnspan=2, sticky="ew")
        row += 1

        self.entry2 = None
        if self.var2 is not None:
            ttk.Label(frame, text="Confirm:").grid(row=row, column=0, columnspan=2, sticky="w", pady=(6, 0))
            row += 1
            self.entry2 = ttk.Entry(frame, textvariable=self.var2, show=MASK, width=44)
            self.entry2.grid(row=row, column=0, columnspan=2, sticky="ew")
            row += 1

        self._last_entry = None
        for entry in (self.entry1, self.entry2):
            if entry is not None:
                self._install_paste(entry)

        ttk.Checkbutton(frame, text="Show", variable=self.show_var, command=self._toggle_show).grid(
            row=row, column=0, sticky="w", pady=(6, 0))
        ttk.Button(frame, text="Paste", takefocus=False,
                   command=lambda: self._paste_into(self._last_entry or self.entry1)).grid(
            row=row, column=1, sticky="e", pady=(6, 0))
        row += 1

        self.msg = ttk.Label(frame, text="", foreground="red", wraplength=380)
        self.msg.grid(row=row, column=0, columnspan=2, sticky="w", pady=(4, 0))
        row += 1

        buttons = ttk.Frame(frame)
        buttons.grid(row=row, column=0, columnspan=2, sticky="e", pady=(8, 0))
        ttk.Button(buttons, text="Save", default="active", command=self.on_save).grid(row=0, column=0, padx=(0, 6))
        ttk.Button(buttons, text="Cancel", command=self.on_cancel).grid(row=0, column=1)

        root.bind("<Return>", lambda _e: self.on_save())
        root.bind("<KP_Enter>", lambda _e: self.on_save())
        root.bind("<Escape>", lambda _e: self.on_cancel())
        root.protocol("WM_DELETE_WINDOW", self.on_cancel)

        self._center()
        self._raise()
        root.after(200, self._raise)
        self._timer = root.after(args.timeout * 1000, self.on_timeout)

    # --- Paste support (works in any keyboard layout) -----------------------

    _VK_A = 65
    _VK_C = 67
    _VK_V = 86
    _VK_X = 88
    _ALT_MASK_WIN = 0x20000  # AltGr on Windows is reported as Ctrl+Alt

    def _install_paste(self, entry) -> None:
        import tkinter as tk

        entry.bind("<Control-KeyPress>", lambda e, w=entry: self._on_ctrl_key(e, w))
        entry.bind("<Shift-Insert>", lambda e, w=entry: self._on_paste_event(w))
        entry.bind("<<Paste>>", lambda e, w=entry: self._on_paste_event(w))
        entry.bind("<<Copy>>", lambda e: "break")
        entry.bind("<<Cut>>", lambda e: "break")
        entry.bind("<FocusIn>", lambda e, w=entry: self._set_last_entry(w), add="+")

        menu = tk.Menu(entry, tearoff=0)
        menu.add_command(label="Paste", command=lambda w=entry: self._paste_into(w))
        menu.add_command(label="Clear", command=lambda w=entry: self._clear_entry(w))
        entry.bind("<Button-3>", lambda e, w=entry, m=menu: self._show_context_menu(e, w, m))

    def _on_ctrl_key(self, event, entry) -> str | None:
        import tkinter as tk

        if event.state & self._ALT_MASK_WIN:
            return None
        if event.keycode == self._VK_V:
            self._paste_into(entry)
            return "break"
        if event.keycode == self._VK_A:
            entry.select_range(0, tk.END)
            entry.icursor(tk.END)
            return "break"
        if event.keycode in (self._VK_C, self._VK_X):
            return "break"
        return None

    def _on_paste_event(self, entry) -> str:
        self._paste_into(entry)
        return "break"

    def _paste_into(self, entry) -> None:
        import tkinter as tk

        try:
            text = self.root.clipboard_get()
        except tk.TclError:
            self._show_error("Clipboard is empty or not text.")
            return
        text = text.strip()
        if not text:
            del text
            self._show_error("Clipboard is empty or not text.")
            return
        if entry.selection_present():
            entry.delete(tk.SEL_FIRST, tk.SEL_LAST)
        entry.insert(tk.INSERT, text)
        del text
        self.msg.configure(text="")
        entry.focus_set()

    def _clear_entry(self, entry) -> None:
        import tkinter as tk

        entry.delete(0, tk.END)
        entry.focus_set()

    def _show_context_menu(self, event, entry, menu) -> str:
        entry.focus_set()
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()
        return "break"

    def _set_last_entry(self, entry) -> None:
        self._last_entry = entry

    # --- UI helpers ---------------------------------------------------------

    def _center(self) -> None:
        self.root.update_idletasks()
        w, h = self.root.winfo_reqwidth(), self.root.winfo_reqheight()
        x = (self.root.winfo_screenwidth() - w) // 2
        y = (self.root.winfo_screenheight() - h) // 3
        self.root.geometry(f"+{max(x, 0)}+{max(y, 0)}")

    def _raise(self) -> None:
        if self._done:
            return
        try:
            self.root.attributes("-topmost", True)
            self.root.lift()
            self.root.focus_force()
            self.entry1.focus_set()
        except Exception:
            pass

    def _toggle_show(self) -> None:
        show = "" if self.show_var.get() else MASK
        self.entry1.configure(show=show)
        if self.entry2 is not None:
            self.entry2.configure(show=show)

    def _show_error(self, text: str) -> None:
        self.msg.configure(text=text)
        self.entry1.focus_set()

    def _validate(self, secret: str) -> str | None:
        if secret == "":
            return "The secret must not be empty."
        if secret != secret.strip():
            return "Leading or trailing whitespace is not allowed."
        if self.var2 is not None:
            second = self.var2.get().strip()
            match = second == secret
            del second
            if not match:
                return "The two entries do not match."
        return None

    def _clear_vars(self) -> None:
        self.var1.set("")
        if self.var2 is not None:
            self.var2.set("")

    def _finish(self) -> None:
        if self._done:
            return
        self._done = True
        if self._timer is not None:
            try:
                self.root.after_cancel(self._timer)
            except Exception:
                pass
        self._clear_vars()
        self.root.destroy()

    # --- Actions ------------------------------------------------------------

    def on_save(self) -> None:
        if self._done:
            return
        secret = self.var1.get().strip()
        problem = self._validate(secret)
        if problem:
            del secret
            self._show_error(problem)
            return
        try:
            set_secret(self.args.service, self.args.account, secret)
            stored = get_secret(self.args.service, self.args.account)
            ok = stored == secret
            del stored
            if not ok:
                raise KeyringVerifyError()
        except Exception as exc:  # report type name only, never the value
            self.error_name = type(exc).__name__
            self.outcome = EXIT_ERROR
        else:
            self.saved_len = len(secret)
            self.outcome = EXIT_OK
        finally:
            del secret
        self._finish()

    def on_cancel(self) -> None:
        if self._done:
            return
        self.outcome = EXIT_CANCEL
        self._finish()

    def on_timeout(self) -> None:
        if self._done:
            return
        self._timer = None
        self.outcome = EXIT_TIMEOUT
        self._finish()

    def run(self) -> int:
        self.root.mainloop()
        return self.outcome


def run_check(args: argparse.Namespace) -> int:
    try:
        present = has_stored_value(args.service, args.account)
    except Exception as exc:
        print("error: " + type(exc).__name__, file=sys.stderr)
        return EXIT_ERROR
    print("present" if present else "missing")
    return EXIT_OK if present else EXIT_MISSING


def run_prompt(args: argparse.Namespace) -> int:
    already_stored = False
    if not args.overwrite:
        try:
            already_stored = has_stored_value(args.service, args.account)
        except Exception as exc:
            print("error: " + type(exc).__name__, file=sys.stderr)
            return EXIT_ERROR

    enable_dpi_awareness()
    try:
        dialog = SecretDialog(args, already_stored)
        code = dialog.run()
    except Exception as exc:  # e.g. TclError when no display is available
        print("error: " + type(exc).__name__, file=sys.stderr)
        return EXIT_ERROR

    if code == EXIT_OK:
        print(f"saved {args.service} / {args.account} ({dialog.saved_len} chars)")
    elif code == EXIT_ERROR:
        print("error: " + dialog.error_name, file=sys.stderr)
    elif code == EXIT_TIMEOUT:
        print("timeout")
    else:
        print("cancelled")
    return code


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        guard()   # 0.4.0: no window, no keyring for a household member's job
    except MemberRefused as exc:
        print(str(exc), file=sys.stderr)
        return EXIT_REFUSED
    return run_check(args) if args.check else run_prompt(args)


if __name__ == "__main__":
    sys.exit(main())
