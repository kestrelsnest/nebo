"""Against camera.lua (port 10399): camera logins (NEBO Camera) next to a controller.
Checks: no camera password -> refused; wrong password -> refused; a camera gets camera_* and
never a coalition picture, a controller never camera_*; camera_set_focus ids/hz reach nebo.camera()
(hz clamped to 20, more than 4 ids ignored) and the focus feed arrives at ~20 Hz without 100 ms
gaps; a cockpit at the camera's address closes it and refuses a new camera login, while the
controller (allow_flying_controllers-like) stays.
usage: python camera_test.py <dir with nebo.dll>   (runs camera.lua itself)"""
import hashlib, hmac, json, os, socket, subprocess, sys, time

LUAE = r"E:\DCS World\bin\luae.exe"
here = os.path.dirname(os.path.abspath(__file__))
server = subprocess.Popen([LUAE, os.path.join(here, "camera.lua"), sys.argv[1]], stdout=subprocess.PIPE, text=True)
t0 = time.time()
time.sleep(1.0)
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + (" | " + detail if detail else ""))


def until(t):
    time.sleep(max(0.0, t - (time.time() - t0)))


class Lines:
    """Line reader over a socket that survives timeouts (socket.makefile does not)."""
    def __init__(self, s):
        self.s, self.buf = s, b""

    def readline(self, timeout=5.0):
        end = time.time() + timeout
        while b"\n" not in self.buf:
            left = end - time.time()
            if left <= 0:
                raise TimeoutError
            self.s.settimeout(left)
            try:
                chunk = self.s.recv(65536)
            except (socket.timeout, TimeoutError):
                raise TimeoutError
            if not chunk:
                return ""
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return line.decode() + "\n"


def login(password, camera=True, side=None):
    """Returns (socket, reader, last reply)."""
    s = socket.create_connection(("127.0.0.1", 10399), timeout=5)
    f = Lines(s)
    send = lambda o: s.sendall((json.dumps(o) + "\n").encode())
    hello = {"type": "hello", "client": "test", "version": 2}
    if camera:
        hello["camera"] = True
    send(hello)
    h = json.loads(f.readline())
    if h.get("type") != "hello":
        s.close()
        return None, None, h
    if camera and "discord" in h:
        s.close()
        return None, None, h
    proof = hmac.new(password.encode(), h["nonce"].encode(), hashlib.sha256).hexdigest()
    send({"type": "proof", "proof": proof})
    a = json.loads(f.readline())
    if a.get("type") != "auth_ok":
        s.close()
        return None, None, a
    want = side or (a["sides"][0] if camera else "blue")
    send({"type": "side", "coalition": want})
    ok = json.loads(f.readline())
    if ok.get("type") != "side_ok":
        s.close()
        return None, None, ok
    return s, f, a


def read_for(s, f, seconds):
    """[(arrival, message)] for seconds (or until the server closes)."""
    out, end = [], time.time() + seconds
    while time.time() < end:
        try:
            line = f.readline(max(0.01, end - time.time()))
        except (socket.timeout, TimeoutError):
            break
        if not line:
            break
        out.append((time.time(), json.loads(line)))
    return out


# 1. No camera password yet (0-3 s): refused at once.
_, _, r = login("campw")
check("no camera password -> refused", r.get("type") == "auth_fail", json.dumps(r))

until(3.8)
# 2. Wrong camera password; the controller password is not a camera password either.
_, _, r = login("wrong")
check("wrong camera password -> refused", r.get("reason") == "password", json.dumps(r))
_, _, r = login("ctrlpw")
check("controller password as camera -> refused", r.get("reason") == "password", json.dumps(r))
# 3. The camera password is not a controller password.
_, _, r = login("campw", camera=False)
check("camera password as controller -> refused", r.get("type") == "auth_fail", json.dumps(r))

# 4. Camera and controller.
cam, camf, a = login("campw")
check("camera login", cam is not None and a.get("sides") == ["camera"], json.dumps(a))
_, _, r = login("campw", side="blue")
check("camera picking blue -> refused", r.get("reason") == "side", json.dumps(r))
ctl, ctlf, a = login("ctrlpw", camera=False)
check("controller login", ctl is not None, json.dumps(a))

got = read_for(cam, camf, 2.5)
types = {m["type"] for _, m in got}
check("camera gets camera_picture", "camera_picture" in types, str(types))
check("camera never gets a coalition picture", "picture" not in types, str(types))
got = read_for(ctl, ctlf, 1.5)
types = {m["type"] for _, m in got}
check("controller gets its picture", "picture" in types, str(types))
check("controller never gets camera_*", not any(t.startswith("camera") for t in types), str(types))

# 5. Focus: 2 ids at 50 Hz asked -> clamped to 20.
cam.sendall(b'{"type":"camera_set_focus","ids":[16777473,1000000007],"hz":50}\n')
read_for(cam, camf, 0.5)
got = read_for(cam, camf, 2.0)
focus = [(t, m) for t, m in got if m["type"] == "camera_focus"]
gaps = [b[0] - a[0] for a, b in zip(focus, focus[1:])]
check("focus feed ~20 Hz", 30 <= len(focus) <= 48, f"{len(focus)} in 2 s")
check("focus ids echoed", bool(focus) and focus[-1][1]["ids"] == "16777473,1000000007", focus[-1][1]["ids"] if focus else "")
check("no long focus gaps (5 ms wake)", bool(gaps) and max(gaps) < 0.09, f"max gap {max(gaps) * 1000:.0f} ms" if gaps else "")
# 6. Five ids: ignored (the last good focus stays).
cam.sendall(b'{"type":"camera_set_focus","ids":[1,2,3,4,5],"hz":10}\n')
got = read_for(cam, camf, 0.6)
focus = [m for _, m in got if m["type"] == "camera_focus"]
check("5 ids ignored", bool(focus) and focus[-1]["ids"] == "16777473,1000000007", focus[-1]["ids"] if focus else "")
# 7. Empty ids stop the feed.
cam.sendall(b'{"type":"camera_set_focus","ids":[],"hz":10}\n')
read_for(cam, camf, 0.4)
got = read_for(cam, camf, 1.2)
check("empty ids stop the feed", not any(m["type"] == "camera_focus" for _, m in got))

# 8. Cockpit at 127.0.0.1 from 14 s: camera closed, controller kept, new camera refused.
until(13.0)
read_for(ctl, ctlf, 0.1)
got = read_for(cam, camf, 2.5)
closed = [m for _, m in got if m["type"] == "closed"]
check("camera closed when its address takes a cockpit", bool(closed) and closed[0].get("reason") == "flying", json.dumps(closed))
got = read_for(ctl, ctlf, 1.5)
check("controller kept (flying list empty)", any(m["type"] == "picture" for _, m in got))
_, _, r = login("campw")
check("camera login from a cockpit -> refused", r.get("reason") == "flying", json.dumps(r))
ctl.close()

out = server.communicate(timeout=30)[0]
print("--- camera.lua")
print("\n".join(l for l in out.splitlines() if "camera" in l or l.startswith(("nebo ", "start", "stopped"))))
check("nebo.camera() saw the focus (clamped hz)", "[16777473,1000000007] 20" in out)
check("nebo.camera() count back to 0", "camera: 0 1 [] 0" in out)
print("ALL PASS" if all(results) else f"{results.count(False)} FAILED")
sys.exit(0 if all(results) else 1)
