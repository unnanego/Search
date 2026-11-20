"""The bench client for Windows: the same verbs and answers as ./bench, over the app's named pipe.

The Mac's bench is an AF_UNIX socket file; on Windows it is \\\\.\\pipe\\<store folder name>\\bench (see
BenchServer.cs for why). A pipe opens like a file, so this needs nothing beyond the standard library. Everything
above the transport is ./bench's: one JSON line in, one out.

    python test/winbench.py --test open https://example.com     # prints the tab's id
    python test/winbench.py --test wait 2e7e7e89
    python test/winbench.py --test text 2e7e7e89
    python test/winbench.py --test shot 2e7e7e89 out.png [width]
    python test/winbench.py --test click 2e7e7e89 "button.go"
    python test/winbench.py --test probe
    python test/winbench.py --test close all

`ask(request, world)` is importable for tests.
"""
import json, os, sys, time


def folder(world=None):
    base = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
    return os.path.join(base, f"Search ({world})" if world else "Search")


def pipe(world=None):
    return "\\\\.\\pipe\\" + (f"Search ({world})" if world else "Search") + "\\bench"


def ask(request, world=None):
    name = pipe(world)
    # Every instance can be busy for a moment between callers; the pipe being absent is a real "not listening".
    for attempt in range(50):
        try:
            handle = open(name, "r+b", buffering=0)
            break
        except FileNotFoundError:
            raise ConnectionError(f"no Search listening at {name} (Settings › Let a script drive Search)")
        except OSError:
            time.sleep(0.05)
    else:
        raise ConnectionError(f"{name} stayed busy")
    with handle:
        handle.write((json.dumps(request) + "\n").encode("utf-8"))
        chunks = []
        while True:
            try: chunk = handle.read(65536)
            except (BrokenPipeError, OSError): break
            if not chunk: break
            chunks.append(chunk)
            if b"\n" in chunk: break
    text = b"".join(chunks).decode("utf-8").split("\n", 1)[0]
    return json.loads(text) if text else {"error": "no answer"}


def main(argv):
    world = None
    while argv and argv[0].startswith("--"):
        flag = argv.pop(0)
        if flag == "--test": world = "test"
        elif flag == "--world": world = argv.pop(0).lower()
    if not argv: print(__doc__); return 2
    verb, rest = argv[0], argv[1:]
    request = {"do": verb}
    if verb == "open":
        request["url"] = rest[0]
        if rest[1:2] == ["private"]: request["private"] = True
    elif verb == "wait":
        request["id"] = rest[0]
        if rest[1:]: request["seconds"] = float(rest[1])
    elif verb == "shot":
        request["id"] = rest[0]
        if rest[1:]: request["path"] = os.path.abspath(rest[1])
        if rest[2:]: request["width"] = float(rest[2])
    elif verb == "click":
        request["id"], request["selector"] = rest[0], rest[1]
    elif verb in ("text", "close"):
        request["id"] = rest[0]
    answer = ask(request, world)
    if "error" in answer: print("error:", answer["error"], file=sys.stderr); return 1
    if verb == "open": print(answer["id"])
    elif verb == "text":
        print(answer["text"])
        if answer.get("truncated"): print("[… truncated]", file=sys.stderr)
    elif verb == "shot": print(answer["path"])
    else: print(json.dumps(answer, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
