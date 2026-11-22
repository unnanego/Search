import sys, struct, json
raw = sys.stdin.buffer.read(4)
n = struct.unpack('<I', raw)[0] if len(raw) == 4 else 0
message = json.loads(sys.stdin.buffer.read(n)) if n else None
out = json.dumps({"echo": message, "args": sys.argv[1:]}).encode()
sys.stdout.buffer.write(struct.pack('<I', len(out)) + out)
sys.stdout.buffer.flush()
