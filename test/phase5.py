"""Phase 5 end to end: the ecosystem, in the dev build's own test worlds, through the bench.

    python test/phase5.py

- Several windows: each with its tabs, the pins the same in all of them, the oldest holding the session files, the
  others in windows.json, all of them back after a restart, Ctrl+Shift+T bringing a closed one back.
- Profiles: a window of each, its own sign-ins, history and bookmarks, back after a restart, removed with its windows.
- Import: a Chrome laid out in the world's own Import folder, its bookmarks, history and passwords brought in, and a
  passwords file.
- Sync: two test worlds sharing one folder, as two computers would, each taking what the other made.
- A coding agent: the real search-mcp, asking, allowed, reading, typing and clicking in a tab of its own, then stopped.
"""
import base64, ctypes, ctypes.wintypes as w, json, os, shutil, sqlite3, subprocess, sys, tempfile, threading, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

PAGES = {f"/{name}": f"<title>{name.title()} page</title><p>{name}</p>" for name in ("one", "two", "three", "four")}
PAGES["/cookie"] = "<title>Cookie</title><script>document.cookie = 'seen=1; path=/'; document.title = 'set'</script>"
PAGES["/read"] = "<title>Read</title><script>document.title = 'cookie ' + (document.cookie || 'none')</script>"


class _Blob(ctypes.Structure): _fields_ = [("cbData", w.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]
def dpapi(data):
    """Bytes sealed for this user, as Chrome seals its key (CryptProtectData)."""
    blob_in, blob_out = _Blob(len(data), ctypes.cast(ctypes.create_string_buffer(data, len(data)), ctypes.POINTER(ctypes.c_char))), _Blob()
    if not ctypes.windll.crypt32.CryptProtectData(ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)): raise OSError("CryptProtectData")
    try: return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally: ctypes.windll.kernel32.LocalFree(blob_out.pbData)


class Drive:
    """The bench for one app, any of its windows ("window": its place, oldest first)."""
    def __init__(self, app):
        self.app, self.until, self.base = app, app.until, app.base
        self.data = Path(folder(app.world))
    def ask(self, request, window=None): return self.app.ask(dict(request, window=window) if window is not None else request)
    def press(self, keys, window=None): return self.ask({"do": "press", "keys": keys}, window)
    def probe(self, window=None): return self.ask({"do": "probe"}, window)
    def tabs(self, window=None): return [t for t in self.ask({"do": "tabs"}, window)["tabs"] if not t["bench"]]
    def go(self, path, window=None, new=False):
        self.press("ctrl+t" if new else "ctrl+l", window)
        self.ask({"do": "type", "text": path if path.startswith("http") else f"{self.base}{path}", "into": "field"}, window)
        self.press("enter", window)
    def titled(self, title, window=None): return self.until(lambda: any(t["title"] == title for t in self.tabs(window)))
    def read(self, name): return json.loads((self.data / name).read_text("utf-8"))
    def exists(self, name): return (self.data / name).exists()


with App("phase5", PAGES) as app:
    d = Drive(app)
    ask, press, probe, tabs, go, titled, read, until = d.ask, d.press, d.probe, d.tabs, d.go, d.titled, d.read, d.until

    # Several windows
    go("/one"); titled("One page")
    one = next(t for t in tabs() if t["title"] == "One page")
    ask({"do": "pin", "id": one["id"]})
    check("one window to start with, holding the session files", probe()["window"] == 0 and probe()["owner"] and len(probe()["windows"]) == 1, probe()["windows"])
    ask({"do": "newwindow"}, 0)
    check("ctrl+n's window opens", until(lambda: len(probe(0)["windows"]) == 2), probe(0)["windows"])
    second = probe(1)
    check("beside the first, in its space, not holding the files", second.get("window") == 1 and not second.get("owner") and second.get("space") == probe(0)["space"], second)
    check("with the pins every window has", until(lambda: any(t["pinned"] and t["url"] and t["url"].endswith("/one") for t in tabs(1))), tabs(1))
    check("and an empty tab of its own in front", any(t["active"] and not t["pinned"] and not t["url"] for t in tabs(1)), tabs(1))
    go("/two", window=1); titled("Two page", 1)
    check("its own tabs", not any(t["title"] == "Two page" for t in tabs(0)), [t["title"] for t in tabs(0)])
    two = next(t for t in tabs(1) if t["title"] == "Two page")
    ask({"do": "pin", "id": two["id"]}, 1)
    check("a pin made in one window is in the other", until(lambda: sum(t["pinned"] for t in tabs(0)) == 2), [(t["title"], t["pinned"]) for t in tabs(0)])
    ask({"do": "pin", "id": two["id"], "off": True}, 1)
    check("and unpinned there, gone from the other", until(lambda: sum(t["pinned"] for t in tabs(0)) == 1), [(t["title"], t["pinned"]) for t in tabs(0)])
    go("/three", window=1, new=True); titled("Three page", 1)

    app.quit()
    windows, session = read("windows.json"), read("session.json")
    check("windows.json: the first window's place, then the second whole", len(windows) == 2 and not windows[0].get("rows") and any(e["url"].endswith("/three") for row in windows[1]["rows"].values() for e in row["tabs"]), windows)
    check("the session files are the first window's", any(e["url"].endswith("/one") for e in session["tabs"]) and not any(e["url"].endswith("/three") for e in session["tabs"]), session["tabs"])
    check("pins.json has the pin", any(p["letter"] for row in read("pins.json").values() for p in row), read("pins.json"))

    app.launch()
    check("both windows come back", until(lambda: len(probe()["windows"]) == 2), probe()["windows"])
    check("each with its own tabs", until(lambda: any(t["url"] and t["url"].endswith("/three") for t in tabs(1))) and not any(t["url"] and t["url"].endswith("/three") for t in tabs(0)), (tabs(0), tabs(1)))

    ask({"do": "closewindow"}, 0)
    check("closing the first leaves the other", until(lambda: len(probe()["windows"]) == 1), probe()["windows"])
    check("which holds the session files now", probe(0)["owner"] and until(lambda: any(e["url"].endswith("/three") for e in read("session.json")["tabs"])), read("session.json")["tabs"])
    press("ctrl+shift+t", 0)
    check("ctrl+shift+t brings the closed window back", until(lambda: len(probe(0)["windows"]) == 2), probe(0)["windows"])
    check("with its pin", until(lambda: any(t["pinned"] for t in tabs(1))), tabs(1))
    ask({"do": "closewindow"}, 1)
    until(lambda: len(probe(0)["windows"]) == 1)

    # Profiles
    check("nothing about profiles shows with one", " — " not in probe(0)["windowTitle"] and not d.exists("profiles.json"), probe(0)["windowTitle"])
    go("/cookie", 0, new=True); titled("set", 0)
    made = ask({"do": "addprofile", "name": "Work"}, 0)
    work, w = made.get("profile"), made.get("window")
    check("a new profile opens a window of its own", w == 1 and until(lambda: probe(1)["profile"] == "Work"), made)
    check("which says whose it is, and so does the other", until(lambda: probe(1)["windowTitle"].endswith("— Work") and probe(0)["windowTitle"].endswith("— Personal")), (probe(0)["windowTitle"], probe(1)["windowTitle"]))
    check("with none of the other profile's pins", not any(t["pinned"] for t in tabs(1)), tabs(1))
    go("/read", 1); titled("cookie none", 1)
    check("nor its sign-ins", any(t["title"] == "cookie none" for t in tabs(1)), [t["title"] for t in tabs(1)])
    go("/four", 1, new=True); titled("Four page", 1)
    press("ctrl+d", 1)
    check("its bookmarks are its own", until(lambda: probe(1)["bookmarks"] == 1) and probe(0)["bookmarks"] == 0, (probe(0)["bookmarks"], probe(1)["bookmarks"]))
    ask({"do": "newwindow"}, 1)
    check("ctrl+n in it opens another of the same profile", until(lambda: len(probe(0)["windows"]) == 3) and probe(2)["profile"] == "Work" and not probe(2)["owner"], probe(2))
    ask({"do": "closewindow"}, 2)
    until(lambda: len(probe(0)["windows"]) == 2)

    app.quit()
    profiles = read("profiles.json")
    check("profiles.json in the Mac's shape", [p["name"] for p in profiles] == ["Personal", "Work"] and all({"id", "name", "home"} <= set(p) for p in profiles), profiles)
    check("its history is its own file", d.exists(f"history-{work}.json") and "/four" in (d.data / f"history-{work}.json").read_text("utf-8") and "/four" not in (d.data / "history.json").read_text("utf-8"))
    check("and its bookmarks", d.exists(f"bookmarks-{work}.json") and "four" in (d.data / f"bookmarks-{work}.json").read_text("utf-8"))
    home = d.data / f"session-{profiles[1]['home']}.json"
    check("each profile's oldest window holds its files", home.exists() and any(e["url"].endswith("/four") for e in read(home.name)["tabs"]), (sorted(p.name for p in d.data.glob("session*")), profiles, read("windows.json")))

    app.launch()
    check("both profiles' windows come back", until(lambda: len(probe()["windows"]) == 2) and sorted(probe(i)["profile"] for i in (0, 1)) == ["Personal", "Work"], probe()["windows"])
    wi = 0 if probe(0)["profile"] == "Work" else 1
    check("the work window with its tabs", until(lambda: any(t["url"] and t["url"].endswith("/four") for t in tabs(wi))), tabs(wi))
    ask({"do": "removeprofile", "profile": work}, 0)
    check("removing a profile closes its windows", until(lambda: len(probe(0)["windows"]) == 1) and probe(0)["profile"] == "Personal", probe(0)["windows"])
    check("and forgets it", [p["name"] for p in ask({"do": "profiles"})["profiles"]] == ["Personal"] and until(lambda: not d.exists(f"history-{work}.json")), ask({"do": "profiles"}))
    check("one profile left: the title is the page's again", until(lambda: " — " not in probe(0)["windowTitle"]), probe(0)["windowTitle"])

    # Bringing things in from another browser: a Chrome laid out in the test world's own Import folder (a test run
    # never reads this PC's real browsers), its key wrapped by DPAPI as Chrome wraps it.
    chrome = d.data / "Import" / "Local" / "Google" / "Chrome" / "User Data"
    (chrome / "Default").mkdir(parents=True)
    (chrome / "Local State").write_text(json.dumps({"os_crypt": {"encrypted_key": base64.b64encode(b"DPAPI" + dpapi(os.urandom(32))).decode()},
                                                    "profile": {"info_cache": {"Default": {"name": "Person 1"}}, "last_used": "Default"}}), "utf-8")
    (chrome / "Default" / "Bookmarks").write_text(json.dumps({"roots": {"bookmark_bar": {"type": "folder", "children": [
        {"type": "url", "name": "News", "url": "https://news.example/"},
        {"type": "folder", "name": "Work", "children": [{"type": "url", "name": "Tracker", "url": "https://tracker.example/"}]}]}}}), "utf-8")
    db = sqlite3.connect(chrome / "Default" / "History")
    db.execute("CREATE TABLE urls (id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER, last_visit_time INTEGER, hidden INTEGER)")
    now = int((time.time() + 11644473600) * 1_000_000)
    db.executemany("INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES (?, ?, ?, ?, 0)", [("https://news.example/", "News", 12, now), ("https://weather.example/", "Weather", 3, now)])
    db.commit(); db.close()
    db = sqlite3.connect(chrome / "Default" / "Login Data")
    db.execute("CREATE TABLE logins (origin_url VARCHAR NOT NULL, username_value VARCHAR, password_value BLOB, blacklisted_by_user INTEGER NOT NULL, date_last_used INTEGER)")
    db.executemany("INSERT INTO logins VALUES (?, ?, ?, 0, ?)", [("https://www.news.example/login", "ann", dpapi(b"hunter2"), now), ("https://bound.example/", "cat", b"v20" + os.urandom(40), now)])
    db.commit(); db.close()
    sources = ask({"do": "importsources"})["sources"]
    found = next((s for s in sources if s["name"] == "Chrome"), None)
    check("Chrome is found, and counted from its files alone", found and found["bookmarks"] == 2 and found["places"] == 2 and found["passwords"] == 2 and found["appBound"] == 1, sources)
    before = probe(0)["bookmarks"]
    brought = ask({"do": "import", "source": "Chrome", "what": ["bookmarks", "history", "passwords"]})
    check("its bookmarks come in", until(lambda: probe(0)["bookmarks"] == before + 2), (brought, probe(0)["bookmarks"]))
    check("its passwords too, but for the one Chrome keeps to itself, which says how to export it", brought.get("accounts") == 1 and "Export" in brought.get("said", ""), brought)
    again = ask({"do": "import", "source": "Chrome", "what": ["bookmarks"]})
    check("bringing it in again brings nothing twice", probe(0)["bookmarks"] == before + 2 and "already here" in again.get("said", ""), again)
    csv = d.data / "Import" / "passwords.csv"
    csv.write_text("name,url,username,password,note\nShop,https://shop.example/,cat,s3cret,\n", "utf-8")
    took = ask({"do": "csv", "file": str(csv)})
    check("an exported passwords file comes in", took.get("accounts") == 2, took)
    app.quit()
    check("its history is in this profile's", "news.example" in (d.data / "history.json").read_text("utf-8") and "weather.example" in (d.data / "history.json").read_text("utf-8"))

# Sync: two worlds, as two computers, sharing one folder.
shared = Path(tempfile.mkdtemp(prefix="search-sync-"))
try:
    with App("phase5", PAGES) as app:
        a = Drive(app)
        state = a.ask({"do": "sync", "folder": str(shared)})
        check("sync on with a folder", state.get("on") and state.get("folder") == str(shared), state)
        a.go("/one"); a.titled("One page")
        a.press("ctrl+d")
        a.until(lambda: a.probe()["bookmarks"] == 1)
        a.ask({"do": "addspace", "name": "Trips"})
        ran = a.ask({"do": "sync", "now": True})
        check("a pass writes this computer's files", ran.get("ran") and not ran.get("problem"), ran)
        mine = list((shared / "Search Sync").rglob("*.json"))
        check("one file per scope, named for this computer", {p.parent.name for p in mine} >= {"app"} and any(p.parent.name.startswith("profile-") for p in mine) and len({p.name for p in mine}) == 1, [str(p.relative_to(shared)) for p in mine])
        app.quit()
        with App("phase5b", PAGES) as other:
            b = Drive(other)
            b.ask({"do": "sync", "folder": str(shared)})
            ran = b.ask({"do": "sync", "now": True})
            check("the other computer takes what the first made", ran.get("ran") and b.until(lambda: b.probe()["bookmarks"] == 1) and "Trips" in b.probe()["spaces"], (ran, b.probe()["bookmarks"], b.probe()["spaces"]))
            check("and knows who it was", b.until(lambda: len(b.ask({"do": "sync"})["peers"]) == 1), b.ask({"do": "sync"}))
            b.go("/two"); b.titled("Two page")
            b.press("ctrl+d")
            b.until(lambda: b.probe()["bookmarks"] == 2)
            b.ask({"do": "sync", "now": True})
        app.launch()
        a.ask({"do": "sync", "now": True})
        check("and the first takes back what the second added", a.until(lambda: a.probe()["bookmarks"] == 2), a.probe()["bookmarks"])
finally:
    shutil.rmtree(shared, ignore_errors=True)

# A coding agent: the real search-mcp, run as an agent runs it, speaking JSON-RPC on its stdio to this test.
AGENT_PAGES = dict(PAGES)
AGENT_PAGES["/form"] = ("<title>Form</title><label>Query <input id=q></label> <button onclick=\"document.title='clicked '+q.value\">Go</button>"
                        "<label>Secret <input type=password id=p value=hunter2></label>")
with App("phase5c", AGENT_PAGES, settings={"agents": True}) as app:
    d = Drive(app)
    d.go("/one"); d.titled("One page")
    mcp = subprocess.Popen([sys.executable, str(Path(__file__).parent.parent / "search-mcp"), "--world", app.world], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
    replies, ids = {}, iter(range(1, 1000))
    def reader():
        for line in mcp.stdout:
            try: message = json.loads(line)
            except ValueError: continue
            replies[message.get("id")] = message
    threading.Thread(target=reader, daemon=True).start()
    def send(method, params=None):
        n = next(ids)
        mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": n, "method": method, "params": params or {}}) + "\n"); mcp.stdin.flush()
        return n
    def answer(n, seconds=30):
        d.until(lambda: n in replies, seconds)
        message = replies.get(n) or {}
        text = ((message.get("result") or {}).get("content") or [{}])[0].get("text", "{}")
        try: return json.loads(text), (message.get("result") or {}).get("isError", False)
        except ValueError: return text, False
    def tool(name, **args): return answer(send("tools/call", {"name": name, "arguments": args}))
    answer(send("initialize", {"protocolVersion": "2025-06-18", "clientInfo": {"name": "claude-code"}, "capabilities": {}}))
    opening = send("tools/call", {"name": "open_tab", "arguments": {"url": f"http://{d.base}/form"}})
    check("the first call asks in the window, and waits", d.until(lambda: d.ask({"do": "agents"}).get("asking")) and opening not in replies, d.ask({"do": "agents"}))
    d.press("enter")
    check("a key never answers it", d.ask({"do": "agents"}).get("asking") and opening not in replies)
    time.sleep(1)  # an answer in the question's first half second is taken for a click meant for something else
    d.ask({"do": "agents", "answer": "allow"})
    opened, bad = answer(opening)
    page = opened.get("page") if isinstance(opened, dict) else None
    check("allowed, it opens a tab of its own", not bad and page and d.until(lambda: d.ask({"do": "agents"}).get("agentTabs") == 1), opened)
    check("behind yours: the one in front stays yours", any(t["active"] and t["title"] == "One page" for t in d.tabs()), d.tabs())
    check("and the window says who is using Search, with Stop", d.until(lambda: (d.ask({"do": "agents"}).get("line") or "").startswith("Claude Code is using Search")), d.ask({"do": "agents"}))
    listed, _ = tool("list_tabs")
    check("it sees only its own tabs", isinstance(listed, dict) and len(listed.get("tabs", [])) == 1, listed)
    read, _ = tool("read_page", page=page)
    nodes = read.get("nodes") or read.get("elements") or [] if isinstance(read, dict) else []
    flat = json.dumps(read)
    check("it reads the page, roles and names", "Go" in flat and "Query" in flat, flat[:400])
    check("but never a password's value", "hunter2" not in flat)
    field = next((n for n in nodes if "Query" in (n.get("name") or "")), None)
    button = next((n for n in nodes if n.get("role") == "button"), None)
    typed, bad = tool("type", element=(field or {}).get("element") or (field or {}).get("id"), text="cats")
    check("it types into a field, checked against what the field then holds", not bad, typed)
    clicked, bad = tool("click", element=(button or {}).get("element") or (button or {}).get("id"))
    check("and clicks with a real click", not bad and d.until(lambda: "clicked cats" in json.dumps(tool("list_tabs")[0])), (clicked, tool("list_tabs")[0]))
    refused, bad = tool("navigate", page=page, url="file:///C:/Windows/win.ini")
    check("an address that isn't http or https is refused", bad and "refused" in json.dumps(refused), refused)
    d.ask({"do": "agents", "stop": True})
    check("Stop ends it", d.until(lambda: not d.ask({"do": "agents"}).get("connected")) and d.until(lambda: not d.ask({"do": "agents"}).get("line")), d.ask({"do": "agents"}))
    mcp.stdin.close(); mcp.terminate()

print("\nall passed" if not failures else f"\n{len(failures)} failed: " + ", ".join(failures))
sys.exit(1 if failures else 0)
