"""Phase 3 end to end: the Arc layer, in the dev build's own test world, through the bench.

    python test/phase3.py

Split View (side by side, the keys going from page to page, sizes and the pair coming back after a restart), and
the rest of Phase 3 as it lands.
"""
import json, os, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

PAGES = {f"/{name}": f"<title>{name.title()} page</title><p>{name}</p>" for name in ("one", "two", "three", "four", "five")}
PAGES["/cookie"] = "<title>Cookie</title><script>document.cookie = 'seen=1; path=/'; document.title = 'set'</script>"
PAGES["/read"] = "<title>Read</title><script>document.title = 'cookie ' + (document.cookie || 'none')</script>"
PAGES["/links"] = "<title>Links</title><p><a id=peek href='/five' style='display:inline-block;padding:20px'>peek at five</a></p>"
PAGES["/video"] = ("<title>Video</title><canvas width=320 height=180></canvas><video muted playsinline></video><script>"
                   "const c=document.querySelector('canvas'),g=c.getContext('2d');let f=0;(function d(){g.fillStyle=`hsl(${f++%360} 80% 50%)`;g.fillRect(0,0,320,180);requestAnimationFrame(d)})();"
                   "const v=document.querySelector('video');v.srcObject=c.captureStream(30);v.play()</script>")

with App("phase3", PAGES, hosts=["example.com"]) as app:
    ask, until, base, tabs, probe = app.ask, app.until, app.base, app.tabs, app.probe
    press = lambda keys: ask({"do": "press", "keys": keys})
    type_in = lambda text, into="field": ask({"do": "type", "text": text, "into": into})
    def go(path, new=False):
        press("ctrl+t" if new else "ctrl+l"); type_in(f"{base}{path}"); press("enter")
    titled = lambda title: until(lambda: any(t["title"] == title for t in tabs()))

    # Split View
    go("/one"); titled("One page")
    press("ctrl+\\")
    check("ctrl+\\ splits the page, the new one waiting for an address", until(lambda: len(probe()["panes"]) == 2) and probe()["field"], probe()["panes"])
    type_in(f"{base}/two"); press("enter"); titled("Two page")
    p = probe()["panes"]
    check("two pages side by side, sharing the room", len(p) == 2 and abs(p[0]["w"] - p[1]["w"]) < 2 and p[1]["x"] > p[0]["x"] + p[0]["w"], p)
    check("the new page has the keys", probe()["selected"] == p[1]["id"])
    press("ctrl+alt+left")
    check("ctrl+alt+left gives them to the other", until(lambda: probe()["selected"] == p[0]["id"]))
    press("ctrl+\\"); type_in(f"{base}/three"); press("enter"); titled("Three page")
    check("a third page joins the split", until(lambda: len(probe()["panes"]) == 3), probe()["panes"])
    go("/four", new=True); titled("Four page")
    check("another tab shows alone", until(lambda: len(probe()["panes"]) == 1), probe()["panes"])
    press("ctrl+1")
    check("going back to the split shows all of it", until(lambda: len(probe()["panes"]) == 3), probe()["panes"])
    press("ctrl+w")
    check("closing a page gives the others its room", until(lambda: len(probe()["panes"]) == 2), probe()["panes"])

    # Pages side by side come back side by side
    before = [x["id"] for x in probe()["panes"]]
    urls = {t["id"]: t["url"] for t in tabs()}
    if os.environ.get("SEARCH_DEBUG"): print("panes", before, "tabs", [(t["id"], t["url"], t["title"]) for t in tabs()])
    app.quit()
    shape = json.loads((Path(folder(app.world)) / "session.json").read_text("utf-8"))
    check("the split is written in the Mac's shape", len(shape.get("splits", [])) == 1 and len(shape["splits"][0]["tabs"]) == 2 and "sizes" in shape["splits"][0], shape.get("splits"))
    app.launch()
    split_urls = sorted(urls[i] for i in before)
    press("ctrl+1")
    check("and comes back after a restart", until(lambda: sorted(next(t["url"] for t in tabs() if t["id"] == x["id"]) for x in probe()["panes"]) == split_urls), (probe()["panes"], split_urls))
    press("ctrl+shift+\\")
    check("ctrl+shift+\\ separates them", until(lambda: len(probe()["panes"]) == 1), probe()["panes"])

    world = lambda tid, e: ask({"do": "world", "id": tid, "expression": e}).get("value")
    def real_click(tid, selector, shift=False):
        box = world(tid, f"(() => {{ const r = document.querySelector({json.dumps(selector)}).getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; }})()")
        for kind in ("mouseMoved", "mousePressed", "mouseReleased"):
            ask({"do": "cdp", "id": tid, "method": "Input.dispatchMouseEvent", "params": {"type": kind, "x": box[0], "y": box[1], "button": "left", "clickCount": 1, "modifiers": 8 if shift else 0}})

    # Peek
    go("/links", new=True); titled("Links")
    lid = probe()["selected"]
    real_click(lid, "#peek", shift=True)
    check("shift+click peeks at the link", until(lambda: (probe()["peek"] or {}).get("title") == "Five page"), probe()["peek"])
    check("the page underneath stays", next(t for t in tabs() if t["id"] == lid)["title"] == "Links")
    press("esc")
    check("esc puts the peek away", until(lambda: probe()["peek"] is None))
    real_click(lid, "#peek", shift=True); until(lambda: (probe()["peek"] or {}).get("title") == "Five page")
    count = len(tabs())
    app.ask({"do": "keeppeek"})
    check("open as tab keeps it, beside, loaded as it is", until(lambda: len(tabs()) == count + 1 and probe()["selected"] == next(t["id"] for t in tabs() if t["title"] == "Five page")), tabs())

    # Ctrl+K
    press("ctrl+k"); type_in("four", into="switcher")
    check("ctrl+k lists the open tabs by name", until(lambda: probe()["switcher"] == ["Four page"]), probe()["switcher"])
    press("enter")
    check("enter goes there", until(lambda: probe()["switcher"] is None and next(t for t in tabs() if t["id"] == probe()["selected"])["title"] == "Four page"))

    # The floating video
    go("/video"); titled("Video"); time.sleep(1)
    vid = probe()["selected"]
    press("ctrl+shift+p")
    check("ctrl+shift+p floats the video", until(lambda: world(vid, "!!document.pictureInPictureElement") is True))
    press("ctrl+shift+p")
    check("and again puts it back", until(lambda: world(vid, "!!document.pictureInPictureElement") is False))

    # The folded column
    press("ctrl+s")
    check("ctrl+s folds the column", until(lambda: probe()["folded"] is True))
    ask({"do": "edge"})
    check("a push against the edge slides it out", probe()["slidOut"] is True)
    press("ctrl+1")
    check("picking a tab slides it back", until(lambda: probe()["slidOut"] is False))
    press("ctrl+s")

    # A link from another app, in a small window
    second = __import__("subprocess").run([str(app.exe), f"http://{base}/three?little"], env=dict(os.environ, SEARCH_PROBE=app.world), timeout=30)
    check("a link from another app opens in a small window", until(lambda: any("little" in (l["url"] or "") for l in probe()["littles"])), probe()["littles"])
    count = len(tabs())
    ask({"do": "keeplittle"})
    check("open in search makes it a tab of the window", until(lambda: probe()["littles"] == [] and len(tabs()) == count + 1), (probe()["littles"], len(tabs())))

    # Pins and groups
    go("/one", new=True); titled("One page")
    one = probe()["selected"]
    ask({"do": "pin", "id": one})
    check("a pinned tab goes to the pins", until(lambda: one in probe()["pins"] and one not in probe()["column"]), (probe()["pins"], probe()["column"]))
    check("wearing a letter", next(t for t in tabs() if t["id"] == one)["letter"] not in (None, ""))
    go("/two", new=True); titled("Two page"); two = probe()["selected"]
    go("/three", new=True); titled("Three page"); three = probe()["selected"]
    ask({"do": "group", "id": two, "name": "Reading"}); ask({"do": "group", "id": three, "name": "Reading"})
    col = probe()["column"]
    check("a group heads its tabs in the column", "group Reading" in col and col.index("group Reading") == col.index(two) - 1 and col.index(three) == col.index(two) + 1, col)
    ask({"do": "fold", "id": two})
    check("folded, its tabs are out of the column", until(lambda: "group Reading (folded)" in probe()["column"] and two not in probe()["column"]), probe()["column"])
    ask({"do": "fold", "id": two})

    # Spaces
    ask({"do": "spaces", "on": True})
    go("/cookie", new=True); titled("set")
    made = ask({"do": "addspace", "name": "Work", "own": True})
    check("a space with sign-ins of its own has a profile of its own", made.get("store") and probe()["space"] == "Work" and probe()["store"] == made["store"], (made, probe()["space"], probe()["store"]))
    check("it starts with a tab of its own", len(tabs()) == 1, tabs())
    go("/read"); titled("cookie none")
    check("and doesn't see the first space's cookies", any(t["title"] == "cookie none" for t in tabs()))
    work_tabs = len(tabs())
    ask({"do": "space", "number": 1})
    check("alt+1 goes back to the first space", until(lambda: probe()["space"] != "Work"), probe()["space"])
    check("its tabs are there, their pages kept as they were", until(lambda: any(t["title"] == "set" and not t["asleep"] for t in tabs())), tabs())
    press("alt+2")
    check("alt+2 to Work, its tab there", until(lambda: probe()["space"] == "Work" and len(tabs()) == work_tabs), (probe()["space"], tabs()))

    # Links routed to their space
    ask({"do": "space", "number": 1}); until(lambda: probe()["space"] != "Work")
    ask({"do": "route", "site": "example.com", "name": "Work"})
    port = app.server.server_port
    __import__("subprocess").run([str(app.exe), f"http://example.com:{port}/one?routed"], env=dict(os.environ, SEARCH_PROBE=app.world), timeout=30)
    check("a routed link from another app opens in its space", until(lambda: probe()["space"] == "Work" and any("routed" in (t["url"] or "") for t in tabs())), (probe()["space"], tabs()))

    # Spaces come back after a restart
    app.quit()
    notes = Path(folder(app.world)) / "notes.json"
    if os.environ.get("SEARCH_DEBUG"): print("notes:", notes.read_text("utf-8") if notes.exists() else "no notes.json")
    check("spaces.json in the Mac's shape", [s["name"] for s in json.loads((Path(folder(app.world)) / "spaces.json").read_text("utf-8"))][-1] == "Work")
    first = json.loads((Path(folder(app.world)) / "session.json").read_text("utf-8"))
    check("pins and groups are written in the Mac's shape", any(e.get("pin") and e.get("pinID") for e in first["tabs"]) and first.get("groups") and any(e.get("groupID") == first["groups"][0]["id"] for e in first["tabs"]), first)
    app.launch()
    check("the space on screen comes back, with its tabs", until(lambda: probe()["space"] == "Work" and any("routed" in (t["url"] or "") for t in tabs())), (probe()["space"], tabs()))

    ask({"do": "space", "number": 1})
    check("pins and groups come back with their space", until(lambda: len(probe()["pins"]) == 1 and any(c.startswith("group Reading") for c in probe()["column"])), (probe()["pins"], probe()["column"]))

# Tabs you aren't using go to sleep (waits made seconds long for the test)
os.environ["SEARCH_IDLE_SECONDS"] = "3"
with App("phase3sleep", PAGES) as app:
    ask, until, base, tabs, probe = app.ask, app.until, app.base, app.tabs, app.probe
    press = lambda keys: ask({"do": "press", "keys": keys})
    for path in ("/one", "/two", "/three"):
        press("ctrl+t"); ask({"do": "type", "text": f"{base}{path}"}); press("enter")
        until(lambda: any(t["url"] and t["url"].endswith(path) and t["title"].endswith("page") for t in tabs()))
    press("ctrl+2")  # the first tab is the empty one the window starts with
    check("a tab left alone goes to sleep", until(lambda: sum(t["asleep"] for t in tabs() if t["url"]) >= 2, 20), [(t["title"], t["asleep"]) for t in tabs()])
    check("the tab in front stays awake", until(lambda: not next(t for t in tabs() if t["active"])["asleep"], 5) and all(not t["asleep"] for t in tabs() if t["active"]), [(t["title"], t["active"], t["asleep"], t["url"]) for t in tabs()])
    press("ctrl+3")
    check("and wakes where it was when you go to it", until(lambda: not next(t for t in tabs() if t["active"])["asleep"] and next(t for t in tabs() if t["active"])["title"] == "Two page"), tabs())

# Tabs left alone close themselves, into the archive
with App("phase3close", PAGES, files={"notes.json": json.dumps({"tabs.close.after": "12", "tabs.sleep": "false"})}) as app:
    ask, until, base, tabs, probe = app.ask, app.until, app.base, app.tabs, app.probe
    press = lambda keys: ask({"do": "press", "keys": keys})
    for path in ("/one", "/two"):
        press("ctrl+t"); ask({"do": "type", "text": f"{base}{path}"}); press("enter")
        until(lambda: any(t["url"] and t["url"].endswith(path) for t in tabs()))
    press("ctrl+2")
    check("a tab left alone closes by itself", until(lambda: not any(t["title"] == "Two page" for t in tabs()), 20), tabs())
    archive = Path(folder(app.world)) / "archive.json"
    check("and waits in the archive", until(lambda: archive.exists() and "two" in archive.read_text("utf-8")), archive.read_text("utf-8") if archive.exists() else "no archive.json")
del os.environ["SEARCH_IDLE_SECONDS"]

print(f"\n{'all passed' if not failures else f'{len(failures)} failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
