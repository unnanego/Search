"""Driving the dev build from a test: launch it in a test world of its own, serve pages, press keys, ask the bench.

    from apptest import App
    with App("phase1", pages={"/": "<title>Home</title>"}) as app:
        app.keys("ctrl", "l"); app.type("example.com"); app.keys("enter")
        app.probe(); app.tabs()

The world's folder (%LOCALAPPDATA%\\Search (<world>)) is wiped before and after. Keys go only to the app's own
window: every press first checks it is in front and stops the test if it isn't, so nothing typed lands in another
app. Text is typed as Unicode characters, so the keyboard layout doesn't matter.
"""
import ctypes, ctypes.wintypes as w, http.server, os, shutil, subprocess, threading, time
from pathlib import Path
from winbench import ask, folder

ROOT = Path(__file__).resolve().parent.parent
user32 = ctypes.WinDLL("user32", use_last_error=True)

class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", w.WORD), ("wScan", w.WORD), ("dwFlags", w.DWORD), ("time", w.DWORD), ("dwExtraInfo", ctypes.c_size_t)]
class _INPUT(ctypes.Structure):
    class _U(ctypes.Union): _fields_ = [("ki", _KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
    _anonymous_ = ("u",)
    _fields_ = [("type", w.DWORD), ("u", _U)]
_KEYUP, _UNICODE = 0x0002, 0x0004

def _send(*events):
    array = (_INPUT * len(events))(*events)
    user32.SendInput(len(events), array, ctypes.sizeof(_INPUT))

def _key(vk=0, scan=0, flags=0):
    # A real keyboard sends a scan code with every key; pages read KeyboardEvent.code from it.
    if vk and not scan: scan = user32.MapVirtualKeyW(vk, 0)
    if vk in (0x25, 0x26, 0x27, 0x28, 0x21, 0x22, 0x2D, 0x2E): flags |= 0x0001  # extended keys
    return _INPUT(type=1, ki=_KEYBDINPUT(vk, scan, flags, 0, 0))

VK = {"ctrl": 0x11, "shift": 0x10, "alt": 0x12, "enter": 0x0D, "tab": 0x09, "esc": 0x1B, "f3": 0x72, "f5": 0x74, "backspace": 0x08,
      "down": 0x28, "up": 0x26, ",": 0xBC, "[": 0xDB, "]": 0xDD}


class App:
    def __init__(self, world, pages=None, settings=None, hosts=None, files=None, env=None):
        self.world, self.pages, self.settings, self.process = world, pages or {}, settings, None
        # Files to put in the world's folder before the first launch (name → text, or a function of the app).
        self.files = files or {}
        self.hits = {}
        # Real hostnames the test wants served by its own server (e.g. an ad network, to see it blocked).
        self.hosts = hosts or []
        # More of the app's environment for this test (a function of the app, or a dict).
        self.env = env or {}
        app = self
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                host = (self.headers.get("Host") or "").split(":")[0]
                app.hits[host] = app.hits.get(host, 0) + 1
                if self.path.startswith("/img"):
                    gif = bytes.fromhex("47494638396101000100800000ffffff00000021f90401000000002c00000000010001000002024401003b")
                    self.send_response(200); self.send_header("Content-Type", "image/gif"); self.send_header("Content-Length", str(len(gif))); self.end_headers(); self.wfile.write(gif); return
                # A page is its HTML, or (type, bytes) for anything else, or a function of the query that returns one.
                path, _, query = self.path.partition("?")
                page = app.pages.get(path, "<title>404</title>not found")
                if callable(page): page = page(query)
                kind, body = page if isinstance(page, tuple) else ("text/html; charset=utf-8", page.encode())
                self.send_response(200); self.send_header("Content-Type", kind); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            def log_message(self, *a): pass
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"127.0.0.1:{self.server.server_port}"
        self.exe = max((ROOT / "src/Search.App/bin").rglob("Search.exe"), key=lambda p: p.stat().st_mtime)

    def __enter__(self):
        shutil.rmtree(folder(self.world), ignore_errors=True)
        os.makedirs(folder(self.world), exist_ok=True)
        for name, text in self.files.items():
            Path(folder(self.world), name).write_text(text(self) if callable(text) else text, "utf-8")
        if self.settings is not None:
            settings = self.settings(self) if callable(self.settings) else self.settings
            Path(folder(self.world), "settings.json").write_text(__import__("json").dumps(settings), "utf-8")
        self.launch()
        return self

    def __exit__(self, *_):
        self.quit()
        self.server.shutdown()
        crash = Path(folder(self.world), "crash.log")
        if crash.exists(): print(f"--- crash.log of {self.world} ---\n" + crash.read_text("utf-8", errors="replace")[:4000])
        shutil.rmtree(folder(self.world), ignore_errors=True)

    def launch(self):
        env = dict(os.environ, SEARCH_PROBE=self.world, SEARCH_BENCH="1", **(self.env(self) if callable(self.env) else self.env))
        if self.hosts: env["SEARCH_WEBVIEW_ARGS"] = "--host-resolver-rules=\"" + ", ".join(f"MAP {h} 127.0.0.1" for h in self.hosts) + "\""
        self.process = subprocess.Popen([str(self.exe)], env=env)
        for _ in range(150):
            try:
                if "error" not in self.ask({"do": "probe"}): return
            except (ConnectionError, OSError): pass
            if self.process.poll() is not None: raise SystemExit(f"the app exited ({self.process.returncode:#x}); see {folder(self.world)}\\crash.log")
            time.sleep(0.2)
        raise SystemExit("the app never answered the bench")

    def quit(self):
        if not self.process or self.process.poll() is not None: return
        if h := self.window(): user32.PostMessageW(h, 0x0010, 0, 0)  # WM_CLOSE
        try: self.process.wait(15)
        except subprocess.TimeoutExpired: self.process.kill()

    def ask(self, request): return ask(request, self.world)
    def probe(self): return self.ask({"do": "probe"})
    def tabs(self): return [t for t in self.ask({"do": "tabs"})["tabs"] if not t["bench"]]

    def window(self):
        found = []
        @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        def each(h, _):
            pid = w.DWORD(); user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
            if pid.value == self.process.pid and user32.IsWindowVisible(h):
                cls = ctypes.create_unicode_buffer(256); user32.GetClassNameW(h, cls, 256)
                if cls.value == "WinUIDesktopWin32WindowClass": found.append(h)
            return True
        user32.EnumWindows(each, 0)
        return found[0] if found else None

    def ours(self, pid):
        """The app or one of its child processes: a page with the keyboard puts a WebView2 window in front."""
        class Entry(ctypes.Structure):
            _fields_ = [("size", w.DWORD), ("usage", w.DWORD), ("pid", w.DWORD), ("heap", ctypes.c_size_t), ("module", w.DWORD), ("threads", w.DWORD),
                        ("parent", w.DWORD), ("priority", ctypes.c_long), ("flags", w.DWORD), ("exe", ctypes.c_wchar * 260)]
        kernel = ctypes.WinDLL("kernel32"); kernel.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
        snap = kernel.CreateToolhelp32Snapshot(2, 0); parents = {}
        entry = Entry(); entry.size = ctypes.sizeof(Entry)
        ok = kernel.Process32FirstW(ctypes.c_void_p(snap), ctypes.byref(entry))
        while ok: parents[entry.pid] = entry.parent; ok = kernel.Process32NextW(ctypes.c_void_p(snap), ctypes.byref(entry))
        kernel.CloseHandle(ctypes.c_void_p(snap))
        for _ in range(8):
            if pid == self.process.pid: return True
            pid = parents.get(pid)
            if not pid: return False
        return False

    def foreground_is_ours(self):
        pid = w.DWORD(); user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), ctypes.byref(pid))
        return self.ours(pid.value)

    def front(self):
        """Puts the window in front, and refuses to go on if it isn't: a key meant for it must never land elsewhere."""
        h = self.window()
        for attempt in range(10):
            if h and (user32.GetForegroundWindow() == h or self.foreground_is_ours()): return
            user32.ShowWindow(h, 9)
            # An Alt tap lets a background process take the foreground, but in the app it starts access-key mode and
            # eats the next keys, so only when another process holds the foreground, and Esc after it.
            if attempt and not self.foreground_is_ours(): _send(_key(0x12), _key(0x12, flags=_KEYUP))
            user32.SetForegroundWindow(h)
            time.sleep(0.3)
            if user32.GetForegroundWindow() == h and attempt: _send(_key(0x1B), _key(0x1B, flags=_KEYUP)); time.sleep(0.1)
        raise SystemExit("could not bring the Search window to the front; stopped before sending any keys")

    def click(self, x=0.6, y=0.5):
        """A real left click at a point of the window, as fractions of its width and height (default: in the page)."""
        self.front()
        rect = w.RECT(); user32.GetWindowRect(self.window(), ctypes.byref(rect))
        px, py = int(rect.left + (rect.right - rect.left) * x), int(rect.top + (rect.bottom - rect.top) * y)
        user32.SetCursorPos(px, py)
        class MOUSEINPUT(ctypes.Structure):
            _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("data", w.DWORD), ("flags", w.DWORD), ("time", w.DWORD), ("extra", ctypes.c_size_t)]
        class MINPUT(ctypes.Structure):
            class _U(ctypes.Union): _fields_ = [("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]
            _anonymous_ = ("u",); _fields_ = [("type", w.DWORD), ("u", _U)]
        down, up = MINPUT(type=0, mi=MOUSEINPUT(0, 0, 0, 0x0002, 0, 0)), MINPUT(type=0, mi=MOUSEINPUT(0, 0, 0, 0x0004, 0, 0))
        array = (MINPUT * 2)(down, up); user32.SendInput(2, array, ctypes.sizeof(MINPUT))
        time.sleep(0.4)

    def keys(self, *combo, settle=0.5):
        """keys("ctrl", "t"): pressed in order, released in reverse."""
        self.front()
        codes = [VK.get(k) or (ord(k.upper()) if len(k) == 1 else 0) for k in combo]
        _send(*[_key(c) for c in codes], *[_key(c, flags=_KEYUP) for c in reversed(codes)])
        time.sleep(settle)

    def type(self, text, settle=0.6):
        self.front()
        for ch in text:
            _send(_key(scan=ord(ch), flags=_UNICODE), _key(scan=ord(ch), flags=_UNICODE | _KEYUP))
            time.sleep(0.03)
        time.sleep(settle)

    @staticmethod
    def until(condition, seconds=10):
        end = time.time() + seconds
        while time.time() < end:
            try:
                if condition(): return True
            except (KeyError, IndexError, TypeError): pass
            time.sleep(0.2)
        return False
