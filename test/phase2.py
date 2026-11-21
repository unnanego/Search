"""Phase 2 end to end: the daily driver's pieces, in the dev build's own test world, through the bench.

    python test/phase2.py

Shield (a real ad network's name, served by the test's own server, never reached; ad slots hidden; paused per site),
the history and downloads panels, a page's question and its answer, a site's notification (kept, never posted, in a
test run), private tabs in a browser of their own, and a link handed over by a second launch.
"""
import json, os, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

app = App("phase2", settings={"siteStyles": True, "littleLinks": False}, hosts=["doubleclick.net", "*.doubleclick.net"], files={"styles.json": json.dumps({"127.0.0.1": "#styled { color: rgb(255, 0, 0) !important; }"})})
port = app.server.server_port
app.pages.update({
    "/ads": f"<title>Ads</title><img id=tracker src='http://ad.doubleclick.net:{port}/img.gif'><img id=own src='/img.gif'><ins class=adsbygoogle id=slot>slot</ins><div class=ad id=plain>plain</div>",
    "/": "<title>Home page</title><p>home</p>",
    "/second": "<title>Second page</title>",
    "/file": "<title>Files</title><a id=get href='/download.bin' download='notes.bin'>get</a>",
    "/download.bin": "x" * 50000,
    "/ask": "<title>Ask</title><button id=ask onclick=\"Notification.requestPermission().then(p => document.title = 'answer ' + p)\">ask</button>"
            "<button id=send onclick=\"new Notification('Hello', { body: 'From the test' })\">send</button>",
    "/cookie": "<title>Cookie</title><script>document.cookie = 'seen=1; path=/'; document.title = 'set'</script>",
    "/read": "<title>Read</title><script>document.title = 'cookie ' + (document.cookie || 'none')</script>",
    "/signin": "<title>Sign in</title><form action='/welcome' method=get><input id=user name=user autocomplete=username>"
               "<input id=pass name=pass type=password autocomplete=current-password><button id=go>Sign in</button></form>",
    "/welcome": "<title>Welcome</title><p>signed in</p>",
    "/marked": "<title>Marked page</title>",
    "/promo": "<title>Promo</title><div id=promo class=promo-box style='width:300px;height:80px;background:#fc0'>Buy now</div><p id=styled>styled</p><p>keep</p>",
    "/article": "<title>A long read</title><header>Site menu · Shop · Subscribe</header><article><h1>A long read</h1>"
                + "".join(f"<p>Paragraph {i} of the article, with enough words in it to read like real writing about something worth reading at length.</p>" for i in range(30))
                + "</article><aside>Related nonsense</aside>",
})

with app:
    ask, until, base, tabs, probe = app.ask, app.until, app.base, app.tabs, app.probe
    press = lambda keys: ask({"do": "press", "keys": keys})
    type_in = lambda text, into="field": ask({"do": "type", "text": text, "into": into})
    def go(path, new=False):
        press("ctrl+t" if new else "ctrl+l"); type_in(f"{base}{path}"); press("enter")
    world = lambda tid, e: ask({"do": "world", "id": tid, "expression": e}).get("value")

    # Shield
    tid = ask({"do": "open", "url": f"{base}/ads"})["id"]; ask({"do": "wait", "id": tid}); time.sleep(0.5)
    check("shield: the ad network is never reached", "ad.doubleclick.net" not in app.hits, app.hits)
    check("shield: the page's own image loads", world(tid, "document.getElementById('own').naturalWidth > 0") is True)
    check("shield: an ad slot is hidden", world(tid, "getComputedStyle(document.getElementById('slot')).display") == "none")
    check("shield: a plain .ad stays (site rules only)", world(tid, "getComputedStyle(document.getElementById('plain')).display") == "block")
    ask({"do": "close", "id": "all"})

    # History panel
    go("/"); until(lambda: tabs()[0]["title"] == "Home page")
    go("/second", new=True); until(lambda: tabs()[1]["title"] == "Second page")
    press("ctrl+h")
    p = probe()
    check("history panel opens", p["panel"] == "history", p["panel"])
    # Visiting a deep page also credits the site's front door, as on the Mac, so both are recent.
    check("history lists where you've been", {r["title"] for r in p["panelRows"]} >= {"Second page", "Home page"}, p["panelRows"])
    type_in("home", into="panel")
    check("history searches", until(lambda: [r["title"] for r in probe()["panelRows"]] == ["Home page"]), probe()["panelRows"])
    press("esc")
    check("esc puts the panel away", until(lambda: probe()["panel"] == "none"))

    # Downloads
    go("/file", new=True); until(lambda: tabs()[-1]["title"] == "Files")
    ask({"do": "click", "id": tabs()[-1]["id"], "selector": "#get"})
    check("a download opens the downloads panel", until(lambda: probe()["panel"] == "downloads"), probe()["panel"])
    check("and finishes", until(lambda: any("notes.bin" in r["title"] and "B ·" in r["detail"] for r in probe()["panelRows"])), probe()["panelRows"])
    check("into the test world, not your Downloads", (Path(folder(app.world)) / "Downloads" / "notes.bin").exists())
    press("esc")

    # A page's question, and a notification
    go("/ask"); until(lambda: tabs()[-1]["title"] == "Ask")
    ask({"do": "click", "id": tabs()[-1]["id"], "selector": "#ask"})
    check("the page asks, over its own page", until(lambda: (probe()["asking"] or "").endswith("would like to send you notifications.")), probe()["asking"])
    ask({"do": "answer", "allow": True})
    check("the answer reaches the page", until(lambda: tabs()[-1]["title"] == "answer granted"), tabs()[-1]["title"])
    ask({"do": "click", "id": tabs()[-1]["id"], "selector": "#send"})
    check("a notification is kept, not posted, in a test run", until(lambda: any(n["title"] == "Hello" for n in probe()["notifications"])), probe()["notifications"])

    # Private tabs live apart
    go("/cookie"); until(lambda: tabs()[-1]["title"] == "set")
    press("ctrl+shift+n"); type_in(f"{base}/read"); press("enter")
    check("a private tab doesn't see your cookies", until(lambda: tabs()[-1]["private"] and tabs()[-1]["title"] == "cookie none"), tabs()[-1])
    press("ctrl+t"); type_in(f"{base}/read"); press("enter")
    check("an ordinary tab does", until(lambda: tabs()[-1]["title"] == "cookie seen=1"), tabs()[-1])

    # Bookmarks
    go("/marked"); until(lambda: tabs()[-1]["title"] == "Marked page")
    press("ctrl+d")
    check("ctrl+d bookmarks the page", until(lambda: probe()["bookmarks"] == 1), probe()["bookmarks"])
    press("ctrl+shift+o")
    check("the bookmarks panel lists it", until(lambda: [r["title"] for r in probe()["panelRows"]] == ["Marked page"]), probe()["panelRows"])
    # Somewhere else in that tab, so the bookmark isn't also an open tab (which the field offers first).
    go("/"); until(lambda: tabs()[-1]["title"] == "Home page")
    press("ctrl+l"); type_in("marked")
    check("the field offers it", any(o.startswith("Bookmark") for o in probe()["offering"]), probe()["offering"])
    press("esc"); go("/marked"); until(lambda: tabs()[-1]["title"] == "Marked page"); press("ctrl+d")
    check("ctrl+d again lets it go", until(lambda: probe()["bookmarks"] == 0), probe()["bookmarks"])
    check("bookmarks.json in the Mac's shape", isinstance(json.loads((Path(folder(app.world)) / "bookmarks.json").read_text("utf-8")), list))

    # Passwords: offered once a sign-in has worked, kept encrypted, offered under the box, filled on your choice
    go("/signin"); until(lambda: tabs()[-1]["title"] == "Sign in")
    sid = tabs()[-1]["id"]
    world(sid, "document.getElementById('user').value = 'alice@example.com'; document.getElementById('pass').value = 'correct horse'; 1")
    ask({"do": "click", "id": sid, "selector": "#go"})
    check("a sign-in that worked is offered for keeping", until(lambda: "alice@example.com" in (probe()["keeping"] or "")), probe()["keeping"])
    ask({"do": "keep", "answer": "save"})
    vault = Path(folder(app.world)) / "passwords.dat"
    check("the vault is written, never in the clear", until(lambda: vault.exists()) and b"correct horse" not in vault.read_bytes() and b"alice" not in vault.read_bytes())
    go("/signin"); until(lambda: tabs()[-1]["title"] == "Sign in")
    ask({"do": "click", "id": tabs()[-1]["id"], "selector": "#user"})
    check("your account is offered under the box", until(lambda: probe()["logins"] == ["alice@example.com"]), (probe()["logins"], probe()["fieldNote"]))
    check("nothing is filled on its own", world(tabs()[-1]["id"], "document.getElementById('pass').value") == "")
    ask({"do": "pick", "index": 0})
    check("filled on your choice", until(lambda: world(tabs()[-1]["id"], "document.getElementById('pass').value") == "correct horse"))

    # Hiding something, for good on this site (the picker takes only real clicks: sent as input through DevTools)
    go("/promo"); until(lambda: tabs()[-1]["title"] == "Promo")
    pid = tabs()[-1]["id"]
    check("your own style for the site is on the page", world(pid, "getComputedStyle(document.getElementById('styled')).color") == "rgb(255, 0, 0)")
    press("ctrl+shift+h"); time.sleep(0.5)
    box = world(pid, "(() => { const r = document.getElementById('promo').getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; })()")
    for kind in ("mouseMoved", "mousePressed", "mouseReleased"):
        ask({"do": "cdp", "id": pid, "method": "Input.dispatchMouseEvent", "params": {"type": kind, "x": box[0], "y": box[1], "button": "left", "clickCount": 1}})
    check("a click takes it off", until(lambda: world(pid, "getComputedStyle(document.getElementById('promo')).display") == "none"))
    hidden = Path(folder(app.world)) / "hidden.json"
    check("and it's kept for the site", until(lambda: hidden.exists() and "127.0.0.1" in json.loads(hidden.read_text("utf-8"))), hidden.read_text("utf-8") if hidden.exists() else "no file")
    press("esc"); press("ctrl+r"); time.sleep(1.5)
    check("still gone after a reload", world(pid, "getComputedStyle(document.getElementById('promo')).display") == "none")
    press("ctrl+shift+u")
    check("the hidden panel lists it", until(lambda: probe()["panel"] == "hidden" and len(probe()["panelRows"]) == 1), probe()["panelRows"])
    ask({"do": "panelaction", "index": 0})
    check("and puts it back", until(lambda: world(pid, "getComputedStyle(document.getElementById('promo')).display") == "block"))
    press("esc")

    # Reading mode
    go("/article"); until(lambda: tabs()[-1]["title"] == "A long read")
    rid = tabs()[-1]["id"]
    press("f9")
    check("f9 reads the article", until(lambda: world(rid, "!!document.getElementById('office-reader')") is True))
    check("without what was arranged around it", world(rid, "document.body.innerText.includes('Related nonsense')") is False)
    press("f9")
    check("f9 again, the page as it was", until(lambda: world(rid, "!!document.querySelector('aside')") is True))

    # A link from another app: a second launch hands it over and leaves
    count = len(tabs())
    second = subprocess.run([str(app.exe), f"http://{base}/second?from=elsewhere"], env=dict(os.environ, SEARCH_PROBE=app.world), timeout=30)
    check("a second launch leaves at once", second.returncode == 0, second.returncode)
    check("its link opens in the running window", until(lambda: len(tabs()) == count + 1 and "from=elsewhere" in (tabs()[-1]["url"] or "")), tabs()[-1:])

    # Downloads are remembered across a restart
    app.quit(); app.launch()
    press("ctrl+j")
    check("an earlier download is still listed after a restart", until(lambda: any("notes.bin" in r["title"] for r in probe()["panelRows"])), probe()["panelRows"])
    app.quit()

print(f"\n{'all passed' if not failures else f'{len(failures)} failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
