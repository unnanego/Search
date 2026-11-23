"""Pins and folders carried in the column (the Mac's Tests/pin_drag.py), in the dev build's own test world.

    python test/pin_drag.py          # through the bench: where each drop goes
    python test/pin_drag.py --mouse  # and a real drag with the mouse, which needs the window in front

The bench lets go at a line and a zone as a drag does, through the same code the drop calls; --mouse also drags a row
into a folder and a square across the squares with real mouse input.
"""
import ctypes, ctypes.wintypes as w, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

PAGES = {f"/{n}": f"<title>{n.title()} page</title><p>{n}</p>" for n in ("one", "two", "three", "four", "five", "six")}
user32 = ctypes.windll.user32
user32.WindowFromPoint.argtypes = [w.POINT]; user32.WindowFromPoint.restype = w.HWND; user32.GetAncestor.restype = w.HWND

with App("pindrag", PAGES, settings={"pinRows": True, "pinFolders": True}) as app:
    ask, until = app.ask, app.until
    def tabs(): return [t for t in ask({"do": "tabs"})["tabs"] if not t["bench"]]
    def go(path):
        ask({"do": "press", "keys": "ctrl+t"}); ask({"do": "type", "text": "http://" + app.base + path, "into": "field"}); ask({"do": "press", "keys": "enter"})
        until(lambda: any(t["title"] == path[1:].title() + " page" and not t["loading"] for t in tabs()), 8)
    for n in ("one", "two", "three", "four", "five", "six"): go("/" + n)
    ids = {t["title"].split()[0].lower(): t["id"] for t in tabs() if t["url"]}
    title = {v: k for k, v in ids.items()}
    for n in ("one", "two", "three", "four"):
        ask({"do": "pin", "id": ids[n]}); ask({"do": "pinrows", "listed": {"tab": ids[n], "on": True}})
    r = ask({"do": "pinrows", "newfolder": {"name": "Work", "pin": ids["one"]}})
    work = next(l["id"] for l in r["lines"] if l["kind"] == "folder")
    def shape(r=None):
        r = r or ask({"do": "pinrows"})
        return [("F:" + l["title"] if l["kind"] == "folder" else l["title"].split()[0].lower(), l["depth"]) for l in r["lines"]]
    def at(name, r=None):
        r = r or ask({"do": "pinrows"})
        return next(i for i, l in enumerate(r["lines"]) if (l["kind"] == "folder" and l["title"] == name) or (l["kind"] == "pin" and l["title"].split()[0].lower() == name))
    def drop(zone, on, pin=None, folder=None): return ask({"do": "pinrows", "drop": {"pin": ids.get(pin), "folder": folder, "on": at(on), "zone": zone}})
    check("the start: Work holding one, then two three four", shape() == [("F:Work", 0), ("one", 1), ("two", 0), ("three", 0), ("four", 0)], shape())

    r = drop("into", "Work", pin="two")
    check("a row let go in the middle of a folder goes in, at its end", shape(r) == [("F:Work", 0), ("one", 1), ("two", 1), ("three", 0), ("four", 0)] and r["dropped"] == "Move to Work", (shape(r), r["dropped"]))
    r = drop("before", "two", pin="four")
    check("before a row in a folder: in it, just there", shape(r) == [("F:Work", 0), ("one", 1), ("four", 1), ("two", 1), ("three", 0)], shape(r))
    r = drop("after", "three", pin="one")
    check("after a row at the top: out of the folder, after it", shape(r) == [("F:Work", 0), ("four", 1), ("two", 1), ("three", 0), ("one", 0)] and r["dropped"] == "Move here", (shape(r), r["dropped"]))
    r = drop("before", "Work", pin="two")
    check("before a heading: the top of its level's pins", shape(r) == [("F:Work", 0), ("four", 1), ("two", 0), ("three", 0), ("one", 0)], shape(r))
    r = drop("after", "Work", pin="one")
    check("after an open heading: first in it", shape(r) == [("F:Work", 0), ("one", 1), ("four", 1), ("two", 0), ("three", 0)], shape(r))
    r = drop("after", "one", pin="one")
    check("over itself: nothing", r["dropped"] is None and shape(r)[1] == ("one", 1), (shape(r), r["dropped"]))

    r = ask({"do": "pinrows", "newfolder": {"name": "Play"}})
    play = next(l["id"] for l in r["lines"] if l["kind"] == "folder" and l["title"] == "Play")
    r = drop("into", "Work", folder=play)
    check("a folder let go in the middle of another goes in", ("F:Play", 1) in shape(r) and shape(r)[0] == ("F:Work", 0), shape(r))
    r = drop("before", "Work", folder=play)
    check("a folder before another's heading: before it, at its level", shape(r)[0] == ("F:Play", 0) and ("F:Work", 0) in shape(r), shape(r))
    r = drop("into", "Play", folder=work)
    check("a folder into another, with what it holds", [s for s in shape(r) if s[0] in ("F:Work", "one", "four")] == [("F:Work", 1), ("one", 2), ("four", 2)], shape(r))
    r = drop("into", "Work", folder=play)
    check("never inside itself", r["dropped"] is None, shape(r))

    # A split pinned whole: carried whole, never into a folder.
    ask({"do": "split", "id": ids["five"], "with": ids["six"]}); ask({"do": "pin", "id": ids["five"]})
    r = drop("into", "Work", pin="five")
    check("a pinned split isn't let into a folder", r["dropped"] is None and ("five", 0) in shape(r), shape(r))
    r = drop("before", "two", pin="six")
    s = [n for n, _ in shape(r)]
    check("carried by one page, it goes whole, in its order", s.index("five") + 1 == s.index("six") and s.index("six") + 1 == s.index("two"), s)

    # Squares, and between squares and rows.
    ask({"do": "pinrows", "listed": {"tab": ids["three"], "on": False}}); ask({"do": "pinrows", "listed": {"tab": ids["two"], "on": False}})
    r = ask({"do": "pinrows"})
    check("two squares", [title[s] for s in r["squares"]] == ["three", "two"], r["squares"])
    r = ask({"do": "pinrows", "squares": [ids["two"], ids["three"]]})
    check("squares put in another order", [title[s] for s in r["squares"]] == ["two", "three"] and [t["title"] for t in tabs()][:2] == ["Two page", "Three page"], (r["squares"], [t["title"] for t in tabs()][:3]))
    r = drop("after", "Work", pin="three")
    # Work is in Play by now, so what it holds is two levels in.
    check("a square let go among the rows becomes one, there", [title[s] for s in r["squares"]] == ["two"] and shape(r)[2] == ("three", 2), (r["squares"], shape(r)))

    before = shape()
    app.quit(); app.launch()
    check("restart: all of it as it was", until(lambda: shape() == before, 8), (shape(), before))

    if "--mouse" in sys.argv:
        # Real drags, by the pointer: a row onto a folder's heading, and a square across the other.
        class MI(ctypes.Structure): _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("data", w.DWORD), ("flags", w.DWORD), ("time", w.DWORD), ("extra", ctypes.c_size_t)]
        class INP(ctypes.Structure):
            class _U(ctypes.Union): _fields_ = [("mi", MI), ("pad", ctypes.c_byte * 32)]
            _anonymous_ = ("u",); _fields_ = [("type", w.DWORD), ("u", _U)]
        def mouse(flags, x=0, y=0): i = INP(type=0, mi=MI(x, y, 0, flags, 0, 0)); user32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INP))
        # Moves as a real mouse sends them (absolute, over the whole desktop), so the page sees pointer moves.
        vx, vy, vw, vh = (user32.GetSystemMetrics(n) for n in (76, 77, 78, 79))
        def move(x, y): mouse(0x0001 | 0x8000 | 0x4000, (x - vx) * 65535 // (vw - 1), (y - vy) * 65535 // (vh - 1))
        def drag(a, b):
            print("drag", a, "to", b, "over", hex(user32.GetAncestor(user32.WindowFromPoint(w.POINT(*a)), 2) or 0), "ours", hex(h))
            move(*a); time.sleep(0.3); mouse(0x0002); time.sleep(0.3)
            for k in range(1, 31): move(a[0] + (b[0] - a[0]) * k // 30, a[1] + (b[1] - a[1]) * k // 30); time.sleep(0.03)
            time.sleep(0.6); mouse(0x0004); time.sleep(1.0)
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
        h = app.window(); user32.ShowWindow(h, 9); user32.SetWindowPos(h, 0, 100, 100, 1200, 800, 0x0004); time.sleep(0.6); app.front()
        def screen(rect, fx=0.5, fy=0.5):
            p = w.POINT(int(rect[0] + rect[2] * fx), int(rect[1] + rect[3] * fy)); user32.ClientToScreen(h, ctypes.byref(p)); return p.x, p.y
        def rect_of(name):
            r = ask({"do": "pinrows", "rects": True})
            return next(l["rect"] for l in r["lines"] if (l["kind"] == "folder" and l["title"] == name) or (l["kind"] == "pin" and l["title"].split()[0].lower() == name))
        drag(screen(rect_of("one")), screen(rect_of("Play")))
        check("mouse: a row dragged onto a folder's middle goes in", ("one", 1) in shape(), shape())
        four = next(l["tab"] for l in ask({"do": "pinrows"})["lines"] if l["kind"] == "pin" and l["title"].startswith("Four"))
        ask({"do": "pinrows", "listed": {"tab": four, "on": False}})
        r = ask({"do": "pinrows", "rects": True})
        names = lambda: [t["title"].split()[0].lower() for t in tabs() if t["url"]][:2]
        first = names()
        drag(screen(r["squareRects"][1]), screen(r["squareRects"][0], fx=0.15))
        check("mouse: a square dragged before the other", names() == first[::-1], (first, names()))

print(f"\n{'all passed' if not failures else str(len(failures)) + ' failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
