"""Against clients.lua (port 10399): controllers log in and out; nebo.clients() must follow.
usage: python clients_test.py <dir with nebo.dll>   (NEBO_TEST_PASSWORD set; runs clients.lua itself)"""
import hashlib, hmac, json, os, socket, subprocess, sys, time

LUAE = r"E:\DCS World\bin\luae.exe"
password = os.environ["NEBO_TEST_PASSWORD"]
here = os.path.dirname(os.path.abspath(__file__))
server = subprocess.Popen([LUAE, os.path.join(here, "clients.lua"), sys.argv[1]], stdout=subprocess.PIPE, text=True)
time.sleep(1.5)


def login(side, good=True):
    s = socket.create_connection(("127.0.0.1", 10399), timeout=10)
    f = s.makefile("r", encoding="utf-8")
    send = lambda o: s.sendall((json.dumps(o) + "\n").encode())
    send({"type": "hello", "client": "test", "version": 2})
    nonce = json.loads(f.readline())["nonce"]
    key = password if good else password + "x"
    send({"type": "proof", "proof": hmac.new(key.encode(), nonce.encode(), hashlib.sha256).hexdigest()})
    if json.loads(f.readline())["type"] != "auth_ok":
        s.close()
        return None
    send({"type": "side", "coalition": side})
    f.readline()
    return s


b1 = login("blue"); time.sleep(1)
b2 = login("blue"); time.sleep(1)
r1 = login("red"); time.sleep(1)
bad = login("red", good=False); time.sleep(1)  # refused: must not count
b1.close(); time.sleep(1)
b3 = login("blue"); time.sleep(1)               # one left, one joined: count same, logins up
for s in (b2, r1, b3): s.close()
out = server.communicate(timeout=30)[0]
lines = [l for l in out.splitlines() if l.startswith(("clients:", "after stop:"))]
print("\n".join(lines))
want = ["clients: 0 0 0 0", "clients: 1 0 1 0", "clients: 2 0 2 0", "clients: 2 1 2 1",
        "clients: 1 1 2 1", "clients: 2 1 3 1", "clients: 0 0 3 1", "after stop: 0 0"]
seen = [l for l in lines if l in want]
ok = all(w in lines for w in want) and bad is None
print("PASS" if ok else "FAIL", "| missing:", [w for w in want if w not in lines])
sys.exit(0 if ok else 1)
