"""Phase 4 end to end: Chrome extensions, in the dev build's own test world, through the bench.

    python test/phase4.py

Extensions written here, never downloaded: two loaded from folders, one "from the Chrome Web Store", which this
script's own server plays (test/makecrx.cs signs the package). Popups and what they see, the side panel, a press with
no popup, where an extension runs, turning one off, reload, the store's "Add to Search", a forged package refused, an
update that keeps what the extension saved, removing, a restart, a space with sign-ins of its own, and private tabs.
"""
import json, os, re, shutil, subprocess, sys, tempfile, time
from pathlib import Path
from urllib.parse import parse_qs, unquote

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App, ROOT
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

work = Path(tempfile.mkdtemp(prefix="search-phase4-"))
def write(root, files):
    for name, text in files.items():
        (root / name).parent.mkdir(parents=True, exist_ok=True)
        (root / name).write_text(text, "utf-8")
    return root

PAGES = {f"/{n}": f"<title>{n.title()} page</title><p>{n}</p>" for n in ("one", "two", "three")}
PAGES["/clicked"] = "<title>Clicked</title><p>clicked</p>"
store = {"version": "1.0", "id": None, "forged": None}

def crx(version):
    src = write(work / f"stored-{version}", {
        "manifest.json": json.dumps({"manifest_version": 3, "name": "Stored", "version": version, "permissions": ["storage"],
                                     "background": {"service_worker": "worker.js"},
                                     "content_scripts": [{"matches": ["<all_urls>"], "js": ["content.js"], "run_at": "document_end"}]}),
        "content.js": f"document.documentElement.dataset.stored = '{version}';",
        "worker.js": "chrome.runtime.onMessage.addListener((m, s, reply) => { (async () => { if (m.q === 'set') { await chrome.storage.local.set({ kept: m.v }); reply('set'); } else if (m.q === 'get') reply((await chrome.storage.local.get('kept')).kept ?? null); })(); return true; });",
        "page.html": "<!doctype html><meta charset=utf-8><title>Stored page</title>",
    })
    out = work / f"stored-{version}.crx"
    made = subprocess.run(["dotnet", "run", str(ROOT / "test/makecrx.cs"), "--", str(src), str(out), str(work / "stored.pem")], capture_output=True, text=True, cwd=ROOT)
    if made.returncode: raise SystemExit("makecrx failed:\n" + made.stdout + made.stderr)
    return made.stdout.strip().splitlines()[-1], out.read_bytes()

print("making the store's packages…")
store["id"], store["1.0"] = crx("1.0")
_, store["1.1"] = crx("1.1")
# Another extension's package, offered under Stored's id: its key doesn't hash to that id.
shutil.copy(work / "stored.pem", work / "stored-real.pem"); (work / "stored.pem").unlink()
_, forged = crx("6.6")
shutil.copy(work / "stored-real.pem", work / "stored.pem")

def endpoint(query):
    q = parse_qs(query)
    x = parse_qs(unquote(q.get("x", [""])[0]))
    asked = x.get("id", [""])[0]
    if q.get("response") == ["updatecheck"]:
        status = "ok" if store["version"] != x.get("v", [""])[0] else "noupdate"
        xml = f"<?xml version='1.0' encoding='UTF-8'?><gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'><app appid='{asked}' status='ok'><updatecheck status='{status}' version='{store['version']}'/></app></gupdate>"
        return ("text/xml", xml.encode())
    if asked == store["forged"]: return ("application/x-chrome-extension", forged)
    return ("application/x-chrome-extension", store[store["version"]]) if asked == store["id"] else ("text/plain", b"")
PAGES["/crx"] = endpoint
PAGES[f"/detail/stored/{store['id']}"] = "<title>Stored – Chrome Web Store</title><main><section><h1>Stored</h1><button disabled>Add to Chrome</button></section></main>"
# The store as WebView2 sees it, taking it for Chrome: its own button, live, calling the store's private install API.
PAGES[f"/detail/live/{store['id']}"] = ("<title>Stored – Chrome Web Store</title><main><section><h1>Stored</h1><button id=theirs>Установить</button></section></main>"
    "<script>document.getElementById('theirs').onclick = () => chrome.webstorePrivate.beginInstallWithManifest3({ id: location.pathname.split('/').pop(), manifest: '{}' }).then(() => document.title = 'installed natively', e => document.title = 'refused')</script>")

with App("phase4", PAGES, hosts=["probe.test", "a.test", "b.test", "store.test"],
         env=lambda app: {"SEARCH_CRX_ENDPOINT": f"http://{app.base}/crx", "SEARCH_STORE_HOSTS": "store.test"}) as app:
    ask, until, tabs = app.ask, app.until, app.tabs
    port = app.server.server_port
    site = lambda host, path: f"http://{host}:{port}{path}"
    press = lambda keys: ask({"do": "press", "keys": keys})
    type_in = lambda text, into="field": ask({"do": "type", "text": text, "into": into})
    def go(url, new=False):
        press("ctrl+t" if new else "ctrl+l"); type_in(url); press("enter")
        until(lambda: any(t["url"] == url and not t["loading"] and not t["asleep"] for t in tabs()))
    exts = lambda: {e["name"]: e for e in ask({"do": "extensions"})["extensions"]}
    def dom(tab, expression):
        r = ask({"do": "cdp", "id": tab, "method": "Runtime.evaluate", "params": {"expression": expression, "returnByValue": True, "awaitPromise": True}})
        return r.get("value", {}).get("result", {}).get("value")
    def bench(url):
        opened = ask({"do": "open", "url": url}); ask({"do": "wait", "id": opened["id"]}); time.sleep(0.4)
        return opened["id"]
    popup = lambda expression=None: ask({"do": "extpopup", **({"eval": expression} if expression else {})})
    panel = lambda **kw: ask({"do": "extpanel", **kw})

    check("no extensions, no buttons", not ask({"do": "extensions"})["bar"])

    probe = write(work / "probe", {
        "manifest.json": json.dumps({"manifest_version": 3, "name": "Probe", "version": "1.0", "permissions": ["storage", "tabs", "sidePanel"], "host_permissions": ["<all_urls>"],
                                     "background": {"service_worker": "worker.js"}, "action": {"default_popup": "popup.html", "default_title": "Probe"},
                                     "side_panel": {"default_path": "panel.html"}, "options_page": "page.html",
                                     "content_scripts": [{"matches": ["<all_urls>"], "js": ["content.js"], "run_at": "document_end"}]}),
        "content.js": "document.documentElement.dataset.probe = 'v1';",
        "worker.js": """chrome.runtime.onMessage.addListener((m, s, reply) => { (async () => {
            if (m.q === 'front') { const [t] = await chrome.tabs.query({ active: true, currentWindow: true }); reply(t ? t.url : 'none'); }
            else if (m.q === 'set') { await chrome.storage.local.set({ kept: m.v }); reply('set'); }
            else if (m.q === 'get') reply((await chrome.storage.local.get('kept')).kept ?? null);
            })(); return m && ['front', 'set', 'get'].includes(m.q); });
            chrome.runtime.onMessage.addListener(m => { if (m && m.__search) self.leaked = true; });
            chrome.runtime.onMessage.addListener((m, s, reply) => { if (m && m.q === 'leaked') { reply(!!self.leaked); } });""",
        "popup.html": "<!doctype html><meta charset=utf-8><title>Probe popup</title><body style='margin:0;width:320px;height:180px'>Probe popup<script src=popup.js></script></body>",
        "popup.js": "chrome.tabs.query({active:true,currentWindow:true}).then(t => document.body.dataset.front = t[0] ? t[0].url : 'none'); chrome.runtime.sendMessage({q:'front'}).then(u => document.body.dataset.worker = u);",
        "panel.html": "<!doctype html><meta charset=utf-8><title>Probe panel</title><body>Probe panel</body>",
        "page.html": "<!doctype html><meta charset=utf-8><title>Probe options</title><body>Options</body>",
    })
    clicker = write(work / "clicker", {
        "manifest.json": json.dumps({"manifest_version": 3, "name": "Clicker", "version": "1.0", "background": {"service_worker": "worker.js"}, "action": {"default_title": "Clicker"}}),
        "worker.js": f"chrome.action.onClicked.addListener(tab => chrome.tabs.create({{ url: 'http://probe.test:{port}/clicked?from=' + (tab ? tab.id : 'none') }}));",
    })

    # From a folder
    r = ask({"do": "extinstall", "folder": str(probe)})
    check("a folder loads as an extension", "Probe" in exts() and exts()["Probe"]["enabled"] and not exts()["Probe"]["fromStore"], r)
    check("with one installed, the puzzle shows", ask({"do": "extensions"})["bar"])
    page = bench(site("a.test", "/one"))
    check("its content script runs in pages", dom(page, "document.documentElement.dataset.probe") == "v1")

    # The popup, and what it sees
    go(site("a.test", "/two"))
    front = site("a.test", "/two")
    answer = ask({"do": "extpress", "ext": exts()["Probe"]["id"]})
    check("a press opens its popup", (answer.get("answer") or {}).get("popup", "").endswith("/popup.html") and until(lambda: popup()["shown"]), answer)
    check("the popup is sized to its page", until(lambda: abs(popup()["width"] - 322) < 4 and abs(popup()["height"] - 182) < 4), popup())
    if os.environ.get("SEARCH_DEBUG"): print("debug", popup("window.__searchReady().then(f=>JSON.stringify(f))").get("value"), popup("chrome.windows.getAll({populate:true}).then(ws=>ws.map(w=>[w.id,w.tabs.map(t=>t.url)]))").get("value"), [(t["id"], t["url"], t["active"]) for t in tabs()])
    check("in the popup, the current window's tab is the page in front", until(lambda: popup("document.body.dataset.front")["value"] == front), popup("document.body.dataset.front"))
    check("its worker, asked from the popup, sees the page in front too", until(lambda: popup("document.body.dataset.worker")["value"] == front), popup("document.body.dataset.worker"))
    check("Search's own messages never reach the extension's listeners", popup("chrome.runtime.sendMessage({q:'leaked'})")["value"] is False)
    popup("window.close()")
    check("window.close() puts the popup away", until(lambda: not popup()["open"]))
    ask({"do": "extpress", "ext": exts()["Probe"]["id"]}); until(lambda: popup()["shown"])
    ask({"do": "extpress", "ext": exts()["Probe"]["id"]})
    check("pressing its button again puts it away", until(lambda: not popup()["open"]))

    # The side panel
    panel(open=exts()["Probe"]["id"])
    check("its side panel opens beside the page", until(lambda: (panel()["url"] or "").endswith("/panel.html") and panel()["width"] > 200), panel())
    active = "chrome.tabs.query({active:true,currentWindow:true}).then(t => t[0] ? t[0].url : 'none')"
    check("the panel's current tab is the page in front", until(lambda: panel(eval=active)["value"] == front), panel(eval=active))
    go(site("b.test", "/three"), new=True)
    check("going to another tab, the panel follows it", until(lambda: panel(eval=active)["value"] == site("b.test", "/three")), panel(eval=active))
    panel(close=True)
    check("and it closes", not panel()["open"])

    # Pinned
    ask({"do": "extpin", "ext": exts()["Probe"]["id"]})
    check("pinned, its button sits beside the puzzle", until(lambda: exts()["Probe"]["pinned"] and exts()["Probe"]["button"]))

    # A press with no popup
    ask({"do": "extinstall", "folder": str(clicker)})
    before = len(tabs())
    answer = ask({"do": "extpress", "ext": exts()["Clicker"]["id"]})
    check("with no popup, a press goes to its onClicked", (answer.get("answer") or {}).get("clicked") is True, answer)
    # Its tab is the one in front; without "tabs" it can't read its address (Chrome lends that on a click, through activeTab).
    check("which is given the tab in front", until(lambda: any(re.search(r"/clicked\?from=\d+$", t["url"] or "") for t in tabs())), [t["url"] for t in tabs()])

    # Where it runs
    pid = exts()["Probe"]["id"]
    ask({"do": "extsites", "ext": pid, "hosts": ["a.test"]})
    on_a, on_b = bench(site("a.test", "/one")), bench(site("b.test", "/one"))
    check("told to run only on a site, it runs there", dom(on_a, "document.documentElement.dataset.probe") == "v1")
    check("and nowhere else", dom(on_b, "document.documentElement.dataset.probe") is None)
    ask({"do": "extsites", "ext": pid, "hosts": []})
    check("let back onto every site, it runs everywhere again", dom(bench(site("b.test", "/one")), "document.documentElement.dataset.probe") == "v1")

    # Off and on
    ask({"do": "extenable", "ext": pid, "off": True})
    check("turned off, it runs nowhere", dom(bench(site("a.test", "/one")), "document.documentElement.dataset.probe") is None)
    ask({"do": "extenable", "ext": pid})
    check("turned on, it's back", until(lambda: dom(bench(site("a.test", "/one")), "document.documentElement.dataset.probe") == "v1"))

    # Reload keeps what it saved
    ask({"do": "extpress", "ext": pid}); until(lambda: popup()["shown"])
    popup("chrome.runtime.sendMessage({q:'set', v:'kept'})")
    popup("window.close()"); until(lambda: not popup()["open"])
    (probe / "content.js").write_text("document.documentElement.dataset.probe = 'v2';", "utf-8")
    ask({"do": "extreload", "ext": pid})
    check("reload brings in the folder's edits", until(lambda: dom(bench(site("a.test", "/two")), "document.documentElement.dataset.probe") == "v2"))
    ask({"do": "extpress", "ext": pid}); until(lambda: popup()["shown"])
    check("and keeps what it saved", until(lambda: popup("chrome.runtime.sendMessage({q:'get'})")["value"] == "kept"), popup("chrome.runtime.sendMessage({q:'get'})"))
    popup("window.close()")

    # The Chrome Web Store as it is in WebView2: it takes Search for Chrome and its own button is live
    go(site("store.test", f"/detail/live/{store['id']}"), new=True)
    live = next(t["id"] for t in tabs() if "/detail/live/" in (t["url"] or ""))
    dom(live, "document.getElementById('theirs').click()")
    check("a script pressing the store's own button asks nothing", ask({"do": "extconfirm", "yes": False}).get("error") and dom(live, "document.title") != "installed natively")
    ask({"do": "trustedclick", "id": live, "selector": "#theirs"})
    check("pressing the store's own button asks Search, not the browser", until(lambda: "error" not in ask({"do": "extconfirm", "yes": False}), seconds=20) and dom(live, "document.title") != "installed natively")
    check("and saying no adds nothing", until(lambda: not ask({"do": "extensions"})["busy"]) and "Stored" not in exts(), exts().keys())

    # The Chrome Web Store
    go(site("store.test", f"/detail/stored/{store['id']}"), new=True)
    shop = next(t["id"] for t in tabs() if "/detail/stored/" in (t["url"] or ""))
    check("on the store, its button becomes “Add to Search”", until(lambda: dom(shop, "(document.querySelector('button[data-search=add]')||{}).textContent") == "Add to Search"))
    check("and the store's own one is put away", dom(shop, "getComputedStyle(document.querySelector('button[data-search=theirs]')).display") == "none")
    ask({"do": "trustedclick", "id": shop, "selector": "button[data-search=add]"})
    check("pressing it asks first", until(lambda: "error" not in ask({"do": "extconfirm", "yes": True}), seconds=20))
    check("and adds the extension from the store", until(lambda: "Stored" in exts() and exts()["Stored"]["fromStore"] and exts()["Stored"]["id"] == store["id"], seconds=20), exts().keys())
    check("the store's button says it's added", until(lambda: dom(shop, "(document.querySelector('button[data-search=add]')||{}).textContent") == "Added to Search"))
    check("its content script runs", until(lambda: dom(bench(site("a.test", "/three")), "document.documentElement.dataset.stored") == "1.0"))
    check("a script's click on the button does nothing", (dom(shop, "document.querySelector('button[data-search=add]').disabled=false, document.querySelector('button[data-search=add]').click(), 1") == 1) and ask({"do": "extconfirm", "yes": False}).get("error"))

    # A forged package
    store["forged"] = "a" * 32
    r = ask({"do": "extinstall", "store": "a" * 32})
    check("a package whose key isn't its id's is refused", "signature" in (r.get("notice") or "") and "a" * 32 not in [e["id"] for e in exts().values()], r)

    # Updates keep what it saved
    stored = store["id"]
    keep = bench(f"chrome-extension://{stored}/page.html")
    check("an extension's own page opens in a tab", dom(keep, "chrome.runtime.sendMessage({q:'set', v:'before update'})") == "set")
    store["version"] = "1.1"
    r = ask({"do": "extupdate"})
    check("a newer version on the store is installed", until(lambda: exts()["Stored"]["version"] == "1.1"), r)
    check("its new files run", until(lambda: dom(bench(site("a.test", "/one")), "document.documentElement.dataset.stored") == "1.1"))
    keep = bench(f"chrome-extension://{stored}/page.html")
    check("and what it saved is still there", until(lambda: dom(keep, "chrome.runtime.sendMessage({q:'get'})") == "before update"), dom(keep, "chrome.runtime.sendMessage({q:'get'})"))

    # Removing
    ask({"do": "extremove", "ext": pid})
    check("removed, it's gone from the list", "Probe" not in exts())
    check("and from pages", dom(bench(site("a.test", "/one")), "document.documentElement.dataset.probe") is None)
    check("and its copy from disk", not (Path(folder(app.world)) / "Extensions" / pid).exists())

    # A restart
    app.quit(); app.launch()
    check("after a restart the list is the same", set(exts()) == {"Clicker", "Stored"}, exts().keys())
    check("and the extensions run", until(lambda: dom(bench(site("a.test", "/one")), "document.documentElement.dataset.stored") == "1.1", seconds=15))
    listed = json.loads((Path(folder(app.world)) / "Extensions" / "installed.json").read_text("utf-8"))
    check("kept in the Mac's installed.json shape", all({"id", "name", "version", "enabled", "fromStore", "permissions"} <= set(e) for e in listed), listed)

    # A space with sign-ins of its own is a profile of its own; its pages get the extensions too
    ask({"do": "spaces", "on": True}); ask({"do": "addspace", "name": "Own", "own": True})
    go(site("a.test", "/two"))
    own = next(t["id"] for t in tabs() if t["url"] == site("a.test", "/two"))
    check("a space with its own sign-ins has the extensions too", until(lambda: dom(own, "document.documentElement.dataset.stored") == "1.1", seconds=15))

    # Private tabs have none
    private = ask({"do": "open", "url": site("a.test", "/three"), "private": True})
    ask({"do": "wait", "id": private["id"]}); time.sleep(0.5)
    check("private tabs run no extensions", dom(private["id"], "document.documentElement.dataset.stored") is None)

    log = Path(folder(app.world)) / "extensions.log"
    if failures and log.exists(): print("--- extensions.log ---\n" + log.read_text("utf-8", errors="replace")[:4000])
shutil.rmtree(work, ignore_errors=True)
print(f"\n{'all passed' if not failures else str(len(failures)) + ' failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
