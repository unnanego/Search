"""Phase 1 end to end: the dev build in a test world of its own, driven through the bench.

    python test/phase1.py            # everything through the bench's test-only press/type
    python test/phase1.py --keys     # and a short run of real key presses while a page has the keys

Starts src/Search.App's build with SEARCH_PROBE=phase1 (its own folder, settings and WebView2 data, wiped first and
after), serves a few pages from 127.0.0.1, and checks the bench verbs; the field (typing, completion from history,
Enter, searching); the tab keys; find; the layout keys; and the session coming back after a restart. `--keys` also
sends real key presses through Windows to check that a page with the keyboard still lets the window's keys through.
Those take the foreground for a moment; each press first checks the window is in front, so nothing lands elsewhere.
"""
import json, os, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App
from winbench import folder

PAGES = {
    "/": "<title>Home page</title><h1>Home</h1><p>apple banana apple cherry apple</p>",
    "/form": "<title>Form</title><button id=go onclick=\"document.querySelector('#out').textContent='clicked'\">Go</button><p id=out>waiting</p>",
    "/second": "<title>Second page</title><p>second</p>",
    "/search": "<title>Results</title><p>results</p>",
}
# Searches go to the test's own server: a search engine asked the same thing every run soon answers with a captcha.
LOCAL_SEARCH = lambda app: {"engine": "custom", "customEngine": f"http://{app.base}/search?q=%s"}
failures = []

def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

with App("phase1", PAGES, settings=LOCAL_SEARCH) as app:
    ask, until, base = app.ask, app.until, app.base
    press = lambda keys: ask({"do": "press", "keys": keys})
    type_in = lambda text, into="field": ask({"do": "type", "text": text, "into": into})
    tabs, probe = app.tabs, app.probe
    def trace(label):
        if not os.environ.get("SEARCH_TRACE_BINDING"): return
        live = [t for t in tabs() if not t["asleep"]]
        if not live: print("trace", label, "no live tab"); return
        t0 = live[0]; before = t0["heard"]
        ask({"do": "world", "id": t0["id"], "expression": "__searchSend({kind: \"ping\"}), 1"})
        print("trace", label, t0["id"], "heard" if until(lambda: next(t for t in tabs() if t["id"] == t0["id"])["heard"] > before, 3) else "LOST")

    # The bench
    tid = ask({"do": "open", "url": f"{base}/form"})["id"]
    waited = ask({"do": "wait", "id": tid})
    check("bench open + wait", waited.get("title") == "Form" and not waited.get("timeout"), waited)
    check("bench tab not selected, not in the row", not waited.get("active") and len(tabs()) == 1, waited)
    check("bench text", "waiting" in ask({"do": "text", "id": tid}).get("text", ""))
    check("bench click", ask({"do": "click", "id": tid, "selector": "#go"}).get("ok") is True)
    check("bench click took effect", "clicked" in ask({"do": "text", "id": tid}).get("text", ""))
    shot = ask({"do": "shot", "id": tid, "path": str(Path(os.environ["TEMP"]) / "phase1-shot.png")})
    check("bench shot", shot.get("width", 0) > 100 and Path(shot.get("path", "")).exists(), shot)
    check("bench shot scaled", ask({"do": "shot", "id": tid, "width": 400, "path": str(Path(os.environ["TEMP"]) / "phase1-small.png")}).get("width") == 400)
    check("bench close all", ask({"do": "close", "id": "all"}).get("closed") == 1)
    check("bench unknown tab is an error", "error" in ask({"do": "text", "id": "ffffffff"}))

    trace("The field")
    # The field
    press("ctrl+l"); type_in(f"{base}/"); press("enter")
    check("address typed and gone to", until(lambda: tabs()[0]["url"] == f"http://{base}/"), tabs())
    check("title arrives", until(lambda: tabs()[0]["title"] == "Home page"))
    trace("after first page")
    press("ctrl+t")
    check("ctrl+t: new tab, field has the keys", len(tabs()) == 2 and probe()["field"], probe())
    type_in("127.0.0.1:")
    p = probe()
    check("completion from history", p["fieldText"] == base, p["fieldText"])
    check("history offered", any(o.startswith("Visited") for o in p["offering"]), p["offering"])
    type_in("x")
    check("typing over the completion keeps what was typed", probe()["fieldText"] == "127.0.0.1:x", probe()["fieldText"])
    press("esc"); press("ctrl+l"); type_in(f"{base}/second"); press("enter")
    check("second tab loads", until(lambda: tabs()[1]["title"] == "Second page"), tabs())
    press("ctrl+t"); type_in("some words here")
    check("words offer a search first", probe()["offering"][:1] == ["Search some words here"], probe()["offering"])
    press("enter")
    check("words are searched", until(lambda: f"{base}/search?q=some" in (tabs()[2]["url"] or "")), tabs()[2:])
    press("ctrl+l"); type_in("second")
    offers = probe()["offering"]
    rows = [i for i, o in enumerate(offers) if o.startswith("Open")]
    check("an open tab is offered", bool(rows), offers)
    for _ in range((rows or [0])[0] + 1): press("down")
    press("enter")
    check("and switched to", until(lambda: probe()["selected"] == tabs()[1]["id"]) and len(tabs()) == 3, (probe()["selected"], tabs()))

    trace("Tabs by key")
    # Tabs by key
    press("ctrl+3"); press("ctrl+w")
    check("ctrl+w closes", until(lambda: len(tabs()) == 2), tabs())
    press("ctrl+shift+t")
    check("ctrl+shift+t reopens", until(lambda: len(tabs()) == 3), tabs())
    press("ctrl+1")
    check("ctrl+1", until(lambda: probe()["selected"] == tabs()[0]["id"]))
    press("ctrl+tab")
    check("ctrl+tab", until(lambda: probe()["selected"] == tabs()[1]["id"]))
    press("ctrl+shift+tab")
    check("ctrl+shift+tab", until(lambda: probe()["selected"] == tabs()[0]["id"]))
    press("ctrl+9")
    check("ctrl+9 is the last", until(lambda: probe()["selected"] == tabs()[-1]["id"]))
    press("ctrl+shift+k")
    check("ctrl+shift+k duplicates", until(lambda: len(tabs()) == 4 and tabs()[3]["url"] == tabs()[2]["url"]), tabs())
    press("ctrl+w")

    trace("Find")
    # Find
    press("ctrl+1")
    until(lambda: not tabs()[0]["loading"])
    press("ctrl+f"); type_in("apple", into="find")
    check("find counts from the first", until(lambda: probe()["findCount"] == "1 of 3"), probe()["findCount"])
    pressed = press("enter")
    check("enter finds the next", until(lambda: probe()["findCount"] == "2 of 3"), (pressed, probe()["findCount"], probe()["findRaw"], probe()["focused"], probe()["selected"], tabs()[0]["id"]))
    press("shift+enter")
    check("shift+enter the one before", until(lambda: probe()["findCount"] == "1 of 3"), probe()["findCount"])
    type_in("x", into="find")
    check("no match", until(lambda: probe()["findCount"] == "None"), probe()["findCount"])
    press("esc")
    check("esc closes find", until(lambda: not probe()["find"]))

    trace("Layout")
    # Layout
    press("ctrl+shift+s")
    check("tabs across the top", until(lambda: probe()["sidebar"] is False))
    press("ctrl+shift+s"); press("ctrl+s")
    check("column folded", until(lambda: probe()["folded"] is True))
    press("ctrl+s")
    check("column back", until(lambda: probe()["folded"] is False))

    trace("Private tab")
    # Private tab
    press("ctrl+shift+n"); type_in(f"{base}/second"); press("enter")
    check("private tab", until(lambda: tabs()[-1]["private"] and tabs()[-1]["title"] == "Second page"), tabs()[-1:])

    trace("Real keys")
    # Real keys, through Windows, while a page has the keyboard
    if "--keys" in sys.argv:
        # As a person would: the window to the front, then a click into the page, so the page has the keyboard.
        press("ctrl+1"); until(lambda: probe()["selected"] == tabs()[0]["id"]); app.click()
        count = len(tabs())
        app.keys("ctrl", "t")
        ok = until(lambda: len(tabs()) == count + 1)
        check("os keys: ctrl+t from a page", ok, (tabs(), probe()["focused"]))
        app.keys("ctrl", "w")
        check("os keys: ctrl+w from the field", until(lambda: len(tabs()) == count), tabs())
        app.keys("ctrl", "1"); app.click(); app.keys("ctrl", "tab")
        check("os keys: ctrl+tab from a page", until(lambda: probe()["selected"] == tabs()[1]["id"]), probe()["selected"])
        app.click(); app.keys("ctrl", "shift", "s")
        check("os keys: ctrl+shift+s from a page", until(lambda: probe()["sidebar"] is False))
        app.keys("ctrl", "shift", "s")
        app.click(); app.keys("ctrl", "f")
        check("os keys: ctrl+f from a page", until(lambda: probe()["find"]))
        app.keys("esc")

    # The session, across a restart
    before = [t["url"] for t in tabs() if not t["private"]]
    app.quit()
    session = Path(folder(app.world)) / "session.json"
    check("session written", session.exists())
    shape = json.loads(session.read_text("utf-8")) if session.exists() else {}
    check("session in the Mac's shape", isinstance(shape.get("tabs"), list) and "active" in shape and all({"url", "title", "muted"} <= set(e) for e in shape["tabs"]), shape)
    check("private tab not written", not any("second" in e["url"] and e is shape["tabs"][-1] and len(shape["tabs"]) > len(before) for e in shape.get("tabs", [])))
    history = Path(folder(app.world)) / "history.json"
    check("history written in the Mac's shape", history.exists() and all({"url", "key", "title", "count", "last"} <= set(v) for v in json.loads(history.read_text("utf-8"))))
    app.launch()
    after = [t["url"] for t in tabs()]
    check("tabs come back", after == before, f"{before} → {after}")
    check("only the tab in front loads", all(t["asleep"] for t in tabs() if not t["active"]), tabs())

print(f"\n{'all passed' if not failures else f'{len(failures)} failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
