"""Against terrain.lua (port 10398): how the terrain grid reaches each kind of client (DLL 0.17.3).
Checks: an old client (no terrain_z) gets the plain grid; a client with "terrain_z":true gets it
compressed with a hash, decoding to the same heights and surfaces as the cache file; one whose
"terrain_have" lists that hash gets a one-line terrain_cached instead; every client gets the
theatre and its first picture before the terrain.
usage: python terrain_test.py <dir with nebo.dll> [cache file]   (runs terrain.lua itself)"""
import base64, ctypes, hashlib, hmac, json, os, socket, struct, subprocess, sys, time

LUAE = r"E:\DCS World\bin\luae.exe"
here = os.path.dirname(os.path.abspath(__file__))
cache = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.environ["USERPROFILE"], r"Saved Games\DCS.server\NEBO\terrain_Caucasus_500.bin")
head = open(cache, "rb").read(104)  # magic 8, x0 z0 step 24, nx nz 8, id 64
x0, z0, step, nx, nz = struct.unpack("<dddii", head[8:40])
tid = head[40:104].split(b"\0")[0].decode()
body = open(cache, "rb").read()[104:]
server = subprocess.Popen([LUAE, os.path.join(here, "terrain.lua"), sys.argv[1], cache, tid, str(x0), str(z0), str(step), str(nx), str(nz)],
                          stdout=subprocess.PIPE, text=True)
time.sleep(1.5)
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + (" | " + detail if detail else ""))


class Lines:
    def __init__(self, s):
        self.s, self.buf = s, b""

    def readline(self, timeout):
        end = time.time() + timeout
        while b"\n" not in self.buf:
            left = end - time.time()
            if left <= 0:
                raise TimeoutError
            self.s.settimeout(left)
            try:
                chunk = self.s.recv(1 << 20)
            except (socket.timeout, TimeoutError):
                raise TimeoutError
            if not chunk:
                return ""
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return line.decode()


def session(extra):
    """Logs in with these hello fields; returns [(type, message, bytes)] for 6 s."""
    s = socket.create_connection(("127.0.0.1", 10398), timeout=5)
    f = Lines(s)
    send = lambda o: s.sendall((json.dumps(o) + "\n").encode())
    send({"type": "hello", "client": "test", "version": 2, **extra})
    h = json.loads(f.readline(5))
    send({"type": "proof", "proof": hmac.new(b"ctrlpw", h["nonce"].encode(), hashlib.sha256).hexdigest()})
    json.loads(f.readline(5))
    send({"type": "side", "coalition": "blue"})
    json.loads(f.readline(5))
    out, end = [], time.time() + 6
    while time.time() < end:
        try:
            line = f.readline(max(0.01, end - time.time()))
        except TimeoutError:
            break
        if not line:
            break
        m = json.loads(line)
        out.append((m["type"], m, len(line)))
    s.close()
    return out


def order_ok(msgs):
    kinds = [k for k, _, _ in msgs]
    t = next(i for i, k in enumerate(kinds) if k in ("terrain", "terrain_cached"))
    return "theatre" in kinds[:t] and "picture" in kinds[:t], " ".join(kinds[: t + 1])


def decompress(data, size):
    cab = ctypes.WinDLL("cabinet")
    h = ctypes.c_void_p()
    cab.CreateDecompressor(4, None, ctypes.byref(h))
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t()
    ok = cab.Decompress(h, data, ctypes.c_size_t(len(data)), buf, ctypes.c_size_t(size), ctypes.byref(got))
    cab.CloseDecompressor(h)
    return buf.raw[: got.value] if ok else None


old = session({})
terrain = [m for k, m, _ in old if k == "terrain"]
check("old client gets the plain grid", len(terrain) == 1 and "heights" in terrain[0] and "enc" not in terrain[0])
plain_bytes = next((n for k, _, n in old if k == "terrain"), 0)
ok, seq = order_ok(old)
check("old client: theatre and picture before the terrain", ok, seq)

new = session({"terrain_z": True, "terrain_have": []})
z = [(m, n) for k, m, n in new if k == "terrain"]
check("new client gets the compressed grid", len(z) == 1 and z[0][0].get("enc") == "xpress_huff" and len(z[0][0].get("hash", "")) == 16)
if z:
    m, z_bytes = z[0]
    raw = decompress(base64.b64decode(m["data"]), m["size"])
    check("compressed grid decodes to the cache file's heights and surfaces", raw == body, f"{len(raw or b'')} of {len(body)} bytes")
    check("compressed is far smaller", z_bytes * 5 < plain_bytes, f"{plain_bytes // 1024} KB -> {z_bytes // 1024} KB")
    ok, seq = order_ok(new)
    check("new client: theatre and picture before the terrain", ok, seq)

    kept = session({"terrain_z": True, "terrain_have": ["0000000000000000", m["hash"]]})
    kinds = [k for k, _, _ in kept]
    cached = [mm for k, mm, _ in kept if k == "terrain_cached"]
    check("a client keeping the grid gets terrain_cached only", "terrain" not in kinds and len(cached) == 1 and cached[0]["hash"] == m["hash"],
          " ".join(kinds[:6]))

    other = session({"terrain_z": True, "terrain_have": ["0000000000000000"]})
    check("a client keeping another grid gets the compressed one", any(k == "terrain" and mm.get("enc") for k, mm, _ in other))

server.wait(timeout=30)
print(f"{sum(results)}/{len(results)} passed")
sys.exit(0 if all(results) else 1)
