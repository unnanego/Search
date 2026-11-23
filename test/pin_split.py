"""A split pinned as a split (the Mac's Tests/pin_split.py), in the dev build's own test world, through the bench only.

    python test/pin_split.py

Pin on one page of a split pins every page, as rows naming one pair in pins.json; another window shows them as one
split, and so does a restart; Show as Square is refused; Unpin lets both out, still a split; closing one closes both.
No real keys are sent, so it runs whatever window is in front.
"""
import json, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from apptest import App
from winbench import folder

failures = []
def check(name, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""))
    if not ok: failures.append(name)

PAGES = {f"/{n}": f"<title>{n.title()} page</title><p>{n}</p>" for n in ("one", "two", "three")}

def pairs():
    """Every pin pins.json holds, as (title, pair)."""
    found = []
    def walk(node):
        if isinstance(node, dict):
            if "letter" in node and "home" in node: found.append((node.get("title"), node.get("pair")))
            for v in node.values(): walk(v)
        elif isinstance(node, list):
            for v in node: walk(v)
    file = Path(folder("pinsplit"), "pins.json")
    if file.exists(): walk(json.loads(file.read_text("utf-8")))
    return found

with App("pinsplit", PAGES, settings={"pinRows": True, "pinFolders": True}) as app:
    ask, until = app.ask, app.until
    def tabs(window=None): return [t for t in ask({"do": "tabs", **({"window": window} if window is not None else {})})["tabs"] if not t["bench"]]
    def go(path):
        ask({"do": "press", "keys": "ctrl+t"}); ask({"do": "type", "text": "http://" + app.base + path, "into": "field"}); ask({"do": "press", "keys": "enter"})
        until(lambda: any(t["title"] == path[1:].title() + " page" and not t["loading"] for t in tabs()), 8)
    def by(title, window=None): return next(t for t in tabs(window) if t["title"] == title)
    go("/one"); go("/two"); go("/three")
    one, two, three = by("One page")["id"], by("Two page")["id"], by("Three page")["id"]

    ask({"do": "split", "id": one, "with": two})
    check("two pages side by side", by("One page")["split"] and by("One page")["split"] == by("Two page")["split"], [(t["title"], t["split"]) for t in tabs()])
    ask({"do": "pin", "id": two})
    a, b = by("One page"), by("Two page")
    check("pin on one page pins both", a["pinned"] and b["pinned"] and not by("Three page")["pinned"], [(t["title"], t["pinned"]) for t in tabs()])
    check("as rows, still one split", a["listed"] and b["listed"] and a["pinnedSplit"] and b["pinnedSplit"] and a["split"] == b["split"], (a, b))
    r = ask({"do": "pinrows"})
    check("drawn among the pinned rows", [l["tab"] for l in r["lines"] if l["kind"] == "pin"] == [one, two] and r["squares"] == [], r)
    check("pins.json: both name one pair", until(lambda: len(pairs()) == 2 and pairs()[0][1] and pairs()[0][1] == pairs()[1][1], 5), pairs())

    ask({"do": "pinrows", "listed": {"tab": one, "on": False}})
    check("no Show as Square for a pinned split", by("One page")["listed"] and by("One page")["pinnedSplit"], by("One page"))
    ask({"do": "pinrows", "newfolder": {"name": "Work", "pin": one}})
    r = ask({"do": "pinrows"})
    check("nor a folder", not any(l["kind"] == "folder" for l in r["lines"]), r["lines"])

    ask({"do": "newwindow"})
    check("another window: one split of the two pins", until(lambda: (lambda t: len(t) >= 2 and all(x["pinnedSplit"] for x in t[:2]) and t[0]["split"] == t[1]["split"])([x for x in tabs(1) if x["pinned"]]), 8), tabs(1))
    ask({"do": "closewindow", "window": 1}); time.sleep(0.5)

    app.quit(); app.launch()
    check("restart: still a pinned split", until(lambda: (lambda a, b: a["pinnedSplit"] and b["pinnedSplit"] and a["split"] == b["split"])(by("One page"), by("Two page")), 8), tabs())

    ask({"do": "pin", "id": by("One page")["id"], "off": True})
    a, b = by("One page"), by("Two page")
    check("unpin lets both out", not a["pinned"] and not b["pinned"], (a, b))
    check("still a split among the tabs", a["split"] and a["split"] == b["split"] and not a["pinnedSplit"], (a, b))
    titles = [t["title"] for t in tabs() if t["url"]]
    check("at the head of the tabs, in order", titles[:2] == ["One page", "Two page"], titles)
    check("pins.json: no pins left", until(lambda: pairs() == [], 5), pairs())

    ask({"do": "pin", "id": by("One page")["id"]})
    ask({"do": "select", "id": by("One page")["id"]}); time.sleep(0.3)
    ask({"do": "press", "keys": "ctrl+w"}); time.sleep(0.5)
    titles = [t["title"] for t in tabs()]
    check("ctrl+w on a pinned split closes both", "One page" not in titles and "Two page" not in titles and "Three page" in titles, titles)

print(f"\n{'all passed' if not failures else str(len(failures)) + ' failed: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
