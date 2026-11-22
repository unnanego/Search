"""Phase 6 end to end: the launcher, Capture, pinned rows and live folders, and Ctrl+T finding open tabs, in the dev
build's own test worlds, through the bench.

    python test/phase6.py

- The launcher: off until turned on, its key given back to the page while Search is in front, taken while it isn't;
  the real key raising it over another app, open pages first, Escape keeping what was typed, Enter going to the tab
  that has it and bringing Search forward; a key Windows keeps refused.
- Capture: an area, an element and the whole page, each a PNG in the world's Downloads, the layer never in it; Esc
  taking nothing.
- Pinned rows: a pin shown as a row, a folder made with it, folded, the pin moved out and back, the folder deleted;
  a live folder made from a feed the test serves, its posts opening in a new tab; all of it back after a restart.
- Ctrl+T finds your open tabs: off, the field offers as it always did; on, the tab that matches is picked as you type,
  Enter goes to it and the blank tab goes; Up lets go of it.
"""
import ctypes, struct, sys, time, zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App, _send, _key, _KEYUP, user32
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

PAGES = {f"/{name}": f"<title>{name.title()} page</title><p>{name}</p>" for name in ("one", "two", "three", "four")}
PAGES["/box"] = "<title>Box</title><body style='margin:0;background:#fff'><div id=box style='width:300px;height:200px;background:#f00'></div><div style='height:3000px;background:#00f'></div></body>"
RSS = """<?xml version="1.0"?><rss version="2.0"><channel><title>Test Feed</title><link>/</link>
<item><title>First post</title><link>/one</link></item><item><title>Second post</title><link>/two</link></item></channel></rss>"""
PAGES["/feed.xml"] = ("application/rss+xml", RSS.encode())


def png(path):
    """Width, height and the colour at a point, read straight from the file (8-bit RGB or RGBA, no interlacing)."""
    data = Path(path).read_bytes()
    width, height, depth, kind = struct.unpack(">IIBB", data[16:26])
    at, idat = 8, b""
    while at < len(data):
        size, = struct.unpack(">I", data[at:at + 4]); tag = data[at + 4:at + 8]
        if tag == b"IDAT": idat += data[at + 8:at + 8 + size]
        at += 12 + size
    raw = zlib.decompress(idat); stride = {2: 3, 6: 4}[kind]; rows, previous = [], bytearray(width * stride)
    for y in range(height):
        f, line = raw[y * (width * stride + 1)], bytearray(raw[y * (width * stride + 1) + 1:(y + 1) * (width * stride + 1)])
        for i in range(len(line)):
            a = line[i - stride] if i >= stride else 0; b = previous[i]; c = previous[i - stride] if i >= stride else 0
            if f == 1: line[i] = (line[i] + a) & 255
            elif f == 2: line[i] = (line[i] + b) & 255
            elif f == 3: line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4: p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c); line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); previous = line
    return width, height, lambda x, y: tuple(rows[y][x * stride:x * stride + 3])


def press_global(*vks):
    _send(*[_key(v) for v in vks], *[_key(v, flags=_KEYUP) for v in reversed(vks)])
    time.sleep(0.8)


# The launcher, Capture and Ctrl+T, in one world.
with App("phase6", PAGES) as app:
    ask, until = app.ask, app.until
    def tabs(): return app.tabs()
    def go(path, new=True):
        app.keys("ctrl", "t" if new else "l")
        ask({"do": "type", "text": "http://" + app.base + path, "into": "field"})
        app.keys("enter")
        until(lambda: any(t["title"] == path[1:].title() + " page" or t["title"] == "Box" for t in tabs()), 8)
    go("/one", new=False); go("/two"); go("/three")
    check("three tabs open", [t["title"] for t in tabs()] == ["One page", "Two page", "Three page"], [t["title"] for t in tabs()])

    # The launcher.
    s = ask({"do": "launcher"})
    check("launcher: off until turned on", not s["on"] and not s["registered"] and not s["wants"], s)
    s = ask({"do": "launcher", "on": True, "key": "Alt+Tab"})
    check("launcher: a key Windows keeps is refused, and not asked for", "used by Windows" in s["refusal"] and not s["registered"], s)
    s = ask({"do": "launcher", "key": "Ctrl+Alt+Shift+F9"})
    app.front(); s = ask({"do": "launcher", "refresh": True})
    check("launcher: the key is the page's while Search is in front", s["searchInFront"] and not s["registered"] and not s["wants"], s)
    user32.ShowWindow(app.window(), 6); time.sleep(1)
    s = ask({"do": "launcher"})
    check("launcher: taken while Search isn't in front", s["registered"] and s["wants"] and s["refusal"] == "", s)
    press_global(0x11, 0x12, 0x10, 0x78)
    s = ask({"do": "launcher"})
    check("launcher: the real key raises it, in front", s["up"] and s["shown"] and s["searchInFront"], s)
    s = ask({"do": "launcher", "type": "two"})
    check("launcher: the open page first", s["offers"] and s["offers"][0]["open"] and s["offers"][0]["url"].endswith("/two"), s["offers"])
    s = ask({"do": "launcher", "escape": True})
    check("launcher: Escape puts it away", not s["up"] and not s["shown"], s)
    press_global(0x11, 0x12, 0x10, 0x78)
    s = ask({"do": "launcher"})
    check("launcher: what was typed waits for the next time", s["up"] and s["typed"] == "two", s)
    s = ask({"do": "launcher", "pick": 0, "submit": True}); time.sleep(1)
    p = ask({"do": "probe"}); s = ask({"do": "launcher"})
    check("launcher: Enter goes to the tab, Search in front, the key given back",
          not s["up"] and p["windowTitle"] == "Two page" and s["searchInFront"] and not s["registered"], (s, p.get("windowTitle")))
    check("launcher: nothing opened twice", len(tabs()) == 3, [t["title"] for t in tabs()])
    s = ask({"do": "launcher", "on": False})
    check("launcher: off, nothing kept", not s["on"] and not s["registered"], s)

    # Capture.
    go("/box"); time.sleep(0.5)
    check("capture: the page to take is in front", ask({"do": "probe"})["windowTitle"] == "Box", ask({"do": "probe"}).get("windowTitle"))
    s = ask({"do": "capture", "start": True})
    check("capture: nothing while off", not s["live"] and not s["on"], s)
    s = ask({"do": "capture", "on": True, "start": True})
    check("capture: the layer is up", s["live"] and s["layer"], s)
    s = ask({"do": "capture", "area": [10, 10, 120, 80], "wait": 6})
    w, h, at = png(s["path"]) if s.get("path") else (0, 0, None)
    check("capture: an area, as a PNG in Downloads", (w, h) == (120, 80) and Path(s["path"]).parent.name == "Downloads", s)
    check("capture: the layer isn't in it", at and at(60, 40)[0] > 200 and at(60, 40)[2] < 60, at and at(60, 40))
    ask({"do": "capture", "start": True}); s = ask({"do": "capture", "element": [150, 100], "wait": 6})
    check("capture: an element, its own size", s.get("path") and png(s["path"])[:2] == (300, 200), s)
    ask({"do": "capture", "start": True}); s = ask({"do": "capture", "page": True, "wait": 10})
    check("capture: the whole page, past what's on screen", s.get("path") and png(s["path"])[1] >= 3200 and not s["cropped"], s)
    taken = s.get("path")
    ask({"do": "capture", "start": True}); s = ask({"do": "capture", "escape": True, "wait": 3})
    check("capture: Esc takes nothing", not s["live"] and not s["layer"] and s["path"] == taken, s)
    check("capture: says where it went", s["notice"] == "Copied and saved to Downloads", s["notice"])

    # Ctrl+T finds your open tabs: off, the field is as it was (no row picked, a search first for words).
    app.keys("ctrl", "t"); time.sleep(0.3)
    ask({"do": "type", "text": "thr", "into": "field"}); time.sleep(0.4)
    p = ask({"do": "probe"})
    check("ctrl+t, off: nothing picked as you type", p["offerPicked"] == -1 and p["offers"] and p["offers"][0]["kind"] == "search", (p.get("offerPicked"), p.get("offers")))
    app.keys("esc"); ask({"do": "press", "keys": "ctrl+w"})

# On, in a world of its own.
with App("phase6b", PAGES, settings={"findsOpenTabs": True, "pinRows": True, "pinFolders": True}) as app:
    ask, until = app.ask, app.until
    def tabs(): return app.tabs()
    def go(path, new=True):
        app.keys("ctrl", "t" if new else "l")
        ask({"do": "type", "text": "http://" + app.base + path, "into": "field"})
        app.keys("enter")
        until(lambda: any(t["title"] == path[1:].title() + " page" for t in tabs()), 8)
    go("/one", new=False); go("/two"); go("/three")
    app.keys("ctrl", "t"); time.sleep(0.3)
    ask({"do": "type", "text": "two", "into": "field"}); time.sleep(0.4)
    count = len(tabs())
    app.keys("enter"); time.sleep(1)
    p = ask({"do": "probe"})
    check("ctrl+t, on: the open tab is picked as you type, Enter goes to it", p["windowTitle"] == "Two page", p.get("windowTitle"))
    check("ctrl+t, on: the blank tab goes", len(tabs()) == count - 1, [t["title"] for t in tabs()])
    app.keys("ctrl", "t"); time.sleep(0.3)
    ask({"do": "type", "text": "127.0.0.1/on", "into": "field"}); time.sleep(0.4)
    p = ask({"do": "probe"})
    check("ctrl+t, on: the tab's address finished in grey", p["offerPicked"] == 0 and p["fieldText"] == "127.0.0.1/one", (p.get("offerPicked"), p.get("fieldText")))
    app.keys("up"); time.sleep(0.2)
    p = ask({"do": "probe"})
    check("ctrl+t, on: Up lets go of it, and of its ending", p["offerPicked"] == -1 and p["fieldText"] == "127.0.0.1/on", (p.get("offerPicked"), p.get("fieldText")))
    app.keys("enter"); time.sleep(1.5)
    check("ctrl+t, on: Enter then opens what was typed", ask({"do": "probe"})["windowTitle"] != "One page" and len(tabs()) == count, len(tabs()))
    ask({"do": "press", "keys": "ctrl+w"}); time.sleep(0.5)

    # Pinned rows and folders.
    one, two = [t["id"] for t in tabs() if t["title"] in ("One page", "Two page")][:2]
    ask({"do": "pin", "id": one}); ask({"do": "pin", "id": two})
    r = ask({"do": "pinrows"})
    check("pins: squares by default", set(r["squares"]) >= {one, two} and r["lines"] == [], r)
    r = ask({"do": "pinrows", "listed": {"tab": one, "on": True}})
    check("pins: Show as Row", one not in r["squares"] and [l["tab"] for l in r["lines"]] == [one], r)
    ask({"do": "pinrows", "listed": {"tab": two, "on": True}})
    r = ask({"do": "pinrows", "newfolder": {"name": "Work", "pin": one}})
    kinds = [(l["kind"], l["title"], l["depth"]) for l in r["lines"]]
    check("folders: one made with the pin in it", kinds == [("folder", "Work", 0), ("pin", "One page", 1), ("pin", "Two page", 0)], kinds)
    work = r["lines"][0]["id"]
    ask({"do": "select", "id": two})
    r = ask({"do": "pinrows", "fold": work})
    check("folders: folded, its pin out of sight", [l["kind"] for l in r["lines"]] == ["folder", "pin"] and r["lines"][0]["collapsed"], r["lines"])
    ask({"do": "select", "id": one}); r = ask({"do": "pinrows"})
    check("folders: the tab in front shows even in a folded folder", any(l["tab"] == one and l["selected"] for l in r["lines"]), r["lines"])
    r = ask({"do": "pinrows", "fold": work, "movepin": {"tab": two, "folder": work}})
    check("folders: a pin moved in", [l["depth"] for l in r["lines"] if l["kind"] == "pin"] == [1, 1], r["lines"])
    r = ask({"do": "pinrows", "movepin": {"tab": two, "folder": None}})
    check("folders: and out again", [(l["tab"], l["depth"]) for l in r["lines"] if l["kind"] == "pin"][-1] == (two, 0), r["lines"])

    # Live folders.
    r = ask({"do": "pinrows", "livefolder": {"url": "http://" + app.base + "/feed.xml"}})
    live = [l for l in r["lines"] if l["live"]]
    posts = [l["title"] for l in r["lines"] if l["kind"] == "post"]
    check("live folder: made from the feed, named by it", live and live[0]["title"] == "Test Feed", r["lines"])
    check("live folder: its posts", posts == ["First post", "Second post"], posts)
    check("live folder: its heading names the site", live and live[0]["tip"] == "127.0.0.1", live and live[0]["tip"])
    count = len(tabs())
    ask({"do": "pinrows", "post": {"folder": live[0]["id"], "index": 1}}); time.sleep(1.5)
    check("live folder: a post opens in a new tab, never a pin", len(tabs()) == count + 1 and ask({"do": "probe"})["windowTitle"] == "Two page", [t["title"] for t in tabs()])
    r = ask({"do": "pinrows", "refresh": live[0]["id"]})
    check("live folder: refreshed, the same posts", [l["title"] for l in r["lines"] if l["kind"] == "post"] == posts, r["lines"])
    data = Path(folder(app.world))
    time.sleep(1.5)
    app.quit(); app.launch()
    until(lambda: ask({"do": "pinrows"})["lines"], 10)
    r = ask({"do": "pinrows"})
    kinds = [(l["kind"], l["title"], l["depth"]) for l in r["lines"]]
    check("restart: folders, rows and posts come back", ("folder", "Work", 0) in kinds and ("pin", "One page", 1) in kinds and ("post", "First post", 1) in kinds, kinds)
    r = ask({"do": "pinrows", "deletefolder": work})
    check("folders: deleted, its pin goes up, nothing closed", ("folder", "Work", 0) not in [(l["kind"], l["title"], l["depth"]) for l in r["lines"]]
          and sum(1 for l in r["lines"] if l["kind"] == "pin" and l["depth"] == 0) == 2, r["lines"])
    r = ask({"do": "pinrows", "listed": {"tab": [l["tab"] for l in r["lines"] if l["kind"] == "pin"][0], "on": False}})
    check("pins: Show as Square", len(r["squares"]) >= 1, r)

print(f"\n{'all passed' if not failures else str(len(failures)) + ' failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
