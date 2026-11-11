"""Against push.lua (port 10399): a controller pushes a whole cartridge, a pilot fetches it."""
import hashlib, hmac, json, os, socket, sys, time

PORT = 10399
password = os.environ['NEBO_TEST_PASSWORD']
cartridge_path = sys.argv[1]
cartridge = json.load(open(cartridge_path, encoding='utf-8'))
ok_all = True


def check(name, cond, info=''):
    global ok_all
    ok_all = ok_all and cond
    print('%-52s %s  %s' % (name, 'PASS' if cond else 'FAIL', info))


def lines(sock):
    f = sock.makefile('r', encoding='utf-8')
    return f


def pilot(request):
    s = socket.create_connection(('127.0.0.1', PORT), timeout=10)
    s.sendall((json.dumps(request) + '\n').encode())
    data = b''
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        data += chunk
    s.close()
    return json.loads(data.decode('utf-8').split('\n')[0])


# Controller: login 2 with the password, blue.
s = socket.create_connection(('127.0.0.1', PORT), timeout=10)
f = lines(s)
send = lambda o: s.sendall((json.dumps(o) + '\n').encode())
send({'type': 'hello', 'client': 'test', 'version': 2})
nonce = json.loads(f.readline())['nonce']
send({'type': 'proof', 'proof': hmac.new(password.encode(), nonce.encode(), hashlib.sha256).hexdigest()})
check('controller login', json.loads(f.readline())['type'] == 'auth_ok')
send({'type': 'side', 'coalition': 'blue'})
f.readline()

# Push: recipients before the cartridge (it has "id"/"name" keys of its own).
line = json.dumps({'type': 'command', 'id': 7, 'name': 'dtc_push', 'units': ['1001', '2002'], 'cartridge': cartridge})
t = time.time()
s.sendall((line + '\n').encode())
while True:
    m = json.loads(f.readline())
    if m.get('type') == 'command_result' and m.get('id') == 7:
        break
check('push of a %d KB line accepted' % (len(line) // 1024), m.get('ok') is True, '%s (%.2f s)' % (m.get('value') or m.get('error'), time.time() - t))

# Pilot asks: the cartridge comes back unchanged.
r = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 0})
check('player 1001 gets the push', r.get('type') == 'dtc_push', 'version %s from %s' % (r.get('version'), r.get('from')))
check('cartridge identical to what was pushed', r.get('cartridge') == cartridge)
time.sleep(3.1)
r2 = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': r.get('version', 0)})
check('asking again with that version: nothing new', r2.get('type') == 'dtc_none', json.dumps(r2))
time.sleep(3.1)
check('other player from that address refused', pilot({'type': 'dtc_request', 'version': 2, 'player': '9999', 'have': 0}).get('reason') == 'not_on_a_side')
check('asking within 3 s refused', pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 0}).get('reason') == 'too_soon')
time.sleep(3.1)
check('an old (0.12) hook is told to update', pilot({'type': 'dtc_request'}).get('reason') == 'update')


def push(cmd_id, c):
    s.sendall((json.dumps({'type': 'command', 'id': cmd_id, 'name': 'dtc_push', 'units': ['1001'], 'cartridge': c}) + '\n').encode())
    while True:
        m = json.loads(f.readline())
        if m.get('type') == 'command_result' and m.get('id') == cmd_id:
            return m


# 0.14: one cartridge per aircraft type. A second jet's cartridge does not replace the first: a pilot
# still choosing a spawn gets both, the older first, then the next when asking with its version.
viper = {'data': {'MPD': {}}, 'name': 'viper test', 'type': 'F-16C_50'}
check('second jet pushed', push(10, viper).get('ok') is True)
time.sleep(3.1)
r3 = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 0})
check('from scratch: the older cartridge (first jet) first', r3.get('cartridge') == cartridge, 'version %s' % r3.get('version'))
time.sleep(3.1)
r4 = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': r3.get('version', 0)})
check('asking with it: the second jet next', r4.get('cartridge') == viper, 'version %s' % r4.get('version'))
time.sleep(3.1)
check('then nothing new', pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': r4.get('version', 0)}).get('type') == 'dtc_none')
# The first jet again: replaces its own, not the second's.
cartridge2 = dict(cartridge, name='second push')
push(11, cartridge2)
time.sleep(3.1)
r5 = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': r4.get('version', 0)})
check('a new push of the first jet replaces only that one', r5.get('cartridge', {}).get('name') == 'second push', 'version %s' % r5.get('version'))
time.sleep(3.1)
r6 = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 0})
check('the second jet kept beside it', r6.get('cartridge') == viper)
time.sleep(3.1)

# Designations ride on the ask: checked and rebuilt field by field before they reach Lua.
bad = pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 1000,
             'designation': {'lat': -5.0, 'lon': 41.6, 'track': 'AREA'}})
check('a designation does not change the answer', bad.get('type') == 'dtc_none', json.dumps(bad))
time.sleep(3.1)
pilot({'type': 'dtc_request', 'version': 2, 'player': '1001', 'have': 1000,
       'designation': {'lat': 42.567367, 'lon': 41.628833, 'alt_ft': 51.0, 'track': 'INR POINT"},"x":{"',
                       'range_nm': 20.0, 'had_major_m': 6482.0, 'had_minor_m': 1228.3, 'had_pgm': 2.0,
                       'had_brg': 999.0, 'evil': '<script>'}})
got_d = None
deadline = time.time() + 5
while time.time() < deadline and got_d is None:
    m = json.loads(f.readline())
    if m.get('type') == 'designation_test':
        got_d = m
# The first one to arrive is the good one: the negative latitude (the pod page has no hemisphere) was refused.
check('designation reaches Lua for player 1001 (blue)', got_d is not None and got_d['player'] == '1001')
d = (got_d or {}).get('d', {})
check('position and pod data kept', d.get('lat') == 42.567367 and d.get('lon') == 41.628833 and d.get('alt_ft') == 51 and d.get('range_nm') == 20.0, json.dumps(d))
check('HTS ellipse and grade kept', d.get('had_major_m') == 6482 and d.get('had_minor_m') == 1228 and d.get('had_pgm') == 2)
check('out-of-range bearing dropped, unknown field dropped', 'had_brg' not in d and 'evil' not in d and 'x' not in d)
check('track text cleaned (capitals, digits, spaces)', d.get('track') == 'INR POINT', repr(d.get('track')))
time.sleep(3.1)

# A command without units, and one with a bad unit id, are refused by the DLL.
send({'type': 'command', 'id': 8, 'name': 'dtc_push', 'units': [], 'cartridge': {'type': 'x', 'data': {}}})
send({'type': 'command', 'id': 9, 'name': 'dtc_push', 'units': ['1; drop'], 'cartridge': {'type': 'x', 'data': {}}})
got = {}
while len(got) < 2:
    m = json.loads(f.readline())
    if m.get('type') == 'command_result':
        got[m['id']] = m
check('push without recipients refused', got[8]['ok'] is False, got[8].get('error'))
check('push to a malformed unit id refused', got[9]['ok'] is False, got[9].get('error'))
s.close()
print('ALL PASS' if ok_all else 'SOME FAILED')
