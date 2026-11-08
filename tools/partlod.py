#!/usr/bin/env python3
"""A part that came off a car, simulated here, while the car is another player's copy far from this player (the collider
LOD switches a far car copy's parts' colliders off: Entities.LodKeepsPartsOff). The playtest of 2026-11-06: the host's
rear wheel came off in a crash, the host died, the car went to the client, the host respawned over 50 m from it — and
the wheel fell through the desert floor. Two players:
  A  a wheel off the HOST's car lies beside it; the car goes to the joiner; the host walks 70 m off: the wheel stays on
     the ground on the host (the host simulates it)
  B  the host 70 m from the JOINER's car (its parts' colliders off here); a wheel comes off it on the joiner; the host
     takes the wheel (its own, simulated here) and it stays on the ground
--old runs with LodKeepsPartsOff off (before). --bot: one game, the other player a player bot in it (`mp pbot`) that
gets in the car; A only (a bot can't make a part come off), for when the machine has no room for a second game.
  tools/partlod.py [--no-build] [--relay iad|off] [--old] [--bot] [--car Car06Full]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def until(fn, timeout=15, step=0.5):
    """fn's first truthy value (roadkit.until only says whether there was one)"""
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
RELAY, CAR = arg("--relay", "off"), arg("--car", "Car06Full")
OLD, BOT = "--old" in sys.argv, "--bot" in sys.argv
T = "TLDRevamp.Net.Entities"; AWAY = 70

V = []
def verdict(name, ok, detail=""):
    V.append((name, bool(ok))); print(f"  {'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def ent(s, net): return ents(s).get(net)
def pos(s, net):
    e = ent(s, net)
    return e["pos"] if e else None

def spawn(s, name):
    before = set(ents(s))
    print(f"  spawn {name}:", s(f"devspawn {name}"), flush=True)
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and e["car"] and not e["proxy"] and e["part"] < 0), None), 20)

def wheel_off(s, car):
    """a wheel off `car` on machine s (its owner): the new object's net"""
    parts = until(lambda: s(f"mp attachedparts {car}")["parts"], 15) or []
    w = next((p for p in parts if "Felni" in p["name"] or p["name"].startswith("Wheel")), None)
    if not w: print("  no wheel among the car's parts:", [p["name"] for p in parts], flush=True); return None
    name = w["name"].split("(")[0]
    before = set(ents(s))
    print("  wheel off:", s(f"mp falloff {car} {w['i']}"), flush=True)
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and name in e["name"]), None), 15)

def watch(s, net, secs):
    """the lowest height the object reaches on s in `secs`, and its height at the start"""
    y0 = pos(s, net)[1]; low = y0
    t = time.time()
    while time.time() - t < secs:
        p = pos(s, net)
        if p: low = min(low, p[1])
        time.sleep(0.5)
    return y0, low

def bot_run():
    """A with a player bot as the other player, in the host's one game (the game on this build: tools/build.sh first)"""
    from bench import Bridge
    with game.ProtectedSession():
        game.launch(); game.newgame(20261108); h = Bridge()
        try:
            h(f"set {T}.LodKeepsPartsOff {not OLD}")
            h("set kaposztaleves.s.settings.god True"); h("set TLDRevamp.TestSafety.BlockAutoSave True")
            print(f"  {'OLD (LodKeepsPartsOff off)' if OLD else 'CURRENT BUILD'}", flush=True)
            print("  host:", h("mp host 27070"), flush=True); time.sleep(4); h("mp policy everyone")
            print("  bot:", h("mp pbot add b1"), flush=True)
            if not until(lambda: h("mp pbot 1 status")["ready"], 40): sys.exit("THE BOT NEVER GOT READY")
            bid = h("mp pbot 1 status")["id"]
            # 200 m from the new world's starter car (the host's, simulated here): within 12 m of it a car copy is physical,
            # and the LOD leaves a physical copy alone
            hp = h("gpos"); h(f"tpground {hp['gx'] + 200} {hp['gz']}"); time.sleep(3)
            hc = spawn(h, CAR)
            if not hc: sys.exit("the host's car was not spawned")
            time.sleep(4)
            wa = wheel_off(h, hc)
            if not wa: sys.exit("A: no wheel came off the host's car")
            time.sleep(4)
            cp = pos(h, hc); wp = pos(h, wa)
            print(f"  A: wheel {wa} at {wp} (proxy {ent(h, wa)['proxy']}), car {hc} at {cp}", flush=True)
            h(f"mp pbot 1 tp {cp[0] - 3} {cp[2] - 3}"); time.sleep(1)
            print("  A: the bot gets in:", h(f"mp pbot 1 enter {hc}").get("doing"), flush=True)
            took = until(lambda: (ent(h, hc) or {}).get("proxy") is True and ent(h, hc)["owner"] == bid, 30)
            verdict("A: THE CAR IS THE BOT'S, A COPY ON THE HOST", took, f"{ent(h, hc)}"[:160] + f" bot {h('mp pbot 1 status').get('last')}")
            verdict("A: THE WHEEL IS STILL THE HOST'S", (ent(h, wa) or {}).get("proxy") is False, f"owner {(ent(h, wa) or {}).get('owner')}")
            h(f"tpground {cp[0] - AWAY} {cp[2]}"); time.sleep(2)
            off = until(lambda: (ent(h, hc) or {}).get("partsColOff") is True, 10)
            ce, me = ent(h, hc) or {}, h("gpos")
            why = {k: ce.get(k) for k in ("physical", "byBody", "partsColOff", "proxy", "pos")} | {"host": [me["gx"], me["gy"], me["gz"]]}
            verdict(f"A: {AWAY} M OFF, THE HOST'S COPY OF THE CAR HAS ITS PARTS' COLLIDERS OFF", off, json.dumps(why))
            g = pos(h, wa)[1]
            print("  A: lift the wheel:", h(f"mp moveitem {wa} 0.3 0"), flush=True)
            y0, low = watch(h, wa, 10)
            verdict("A: THE WHEEL STAYS ON THE GROUND ON THE HOST", g - low < 1.0, f"on the ground at y {g:.2f}, lifted to {y0:.2f}, lowest {low:.2f} in 10 s")
            print(f"  host: kept {h(f'get {T}.LodPartsOffKept')['value']}, parts' colliders off {h(f'get {T}.PartCollidersOff')['value']}", flush=True)
        finally:
            try: h(f"set {T}.LodKeepsPartsOff True"); h("mp stop")
            except Exception: pass
            game.quit_game()
    print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
    sys.exit(0 if V and all(ok for _, ok in V) else 1)

if BOT: bot_run()

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("THE SECOND PLAYER NEVER JOINED")
    try:
        for s in (h, L):
            s(f"set {T}.LodKeepsPartsOff {not OLD}")
            s("set kaposztaleves.s.settings.god True"); s("set TLDRevamp.TestSafety.BlockAutoSave True")
        h("mp policy everyone")
        lid = L("mp entities")["me"]
        print(f"  {'OLD (LodKeepsPartsOff off)' if OLD else 'CURRENT BUILD'}", flush=True)
        hp = h("gpos")
        # 200 m from the new world's starter car (the host's): within 12 m of it a car copy is physical, and the LOD
        # leaves a physical copy alone
        h(f"tpground {hp['gx'] + 200} {hp['gz']}"); L(f"tpground {hp['gx'] + 200} {hp['gz'] + 7}"); time.sleep(4)

        # A
        hc = spawn(h, CAR)
        if not hc or not until(lambda: hc in ents(L), 30): sys.exit(f"the host's car was not shared: {hc}")
        time.sleep(4)
        wa = wheel_off(h, hc)
        if not wa: sys.exit("A: no wheel came off the host's car")
        until(lambda: wa in ents(L), 15); time.sleep(4)
        print(f"  A: wheel {wa} on the host at {pos(h, wa)} (proxy {ent(h, wa)['proxy']}), car {hc} at {pos(h, hc)}", flush=True)
        cp = pos(h, hc); L(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(2)
        print("  A: the joiner takes the car:", L(f"mp claim {hc}"), flush=True)
        took = until(lambda: (ent(h, hc) or {}).get("proxy") is True and ent(h, hc)["owner"] == lid, 15)
        verdict("A: THE CAR IS THE JOINER'S, A COPY ON THE HOST", took, f"{ent(h, hc)}"[:200])
        verdict("A: THE WHEEL IS STILL THE HOST'S", (ent(h, wa) or {}).get("proxy") is False, f"owner {(ent(h, wa) or {}).get('owner')}")
        wp = pos(h, wa)
        h(f"tpground {cp[0] - AWAY} {cp[2]}"); time.sleep(2)
        off = until(lambda: (ent(h, hc) or {}).get("partsColOff") is True, 10)
        verdict(f"A: {AWAY} M OFF, THE HOST'S COPY OF THE CAR HAS ITS PARTS' COLLIDERS OFF", off, "")
        # the wheel lies still (asleep): lifted 1 m (moveitem), as a wheel just thrown off a crashing car is moving
        g = pos(h, wa)[1]
        print("  A: lift the wheel:", h(f"mp moveitem {wa} 0.3 0"), flush=True)
        y0, low = watch(h, wa, 10)
        L(f"tpground {wp[0] + 3} {wp[2] + 3}"); time.sleep(3)
        jy = (pos(L, wa) or [0, float('nan')])[1]
        verdict("A: THE WHEEL STAYS ON THE GROUND ON THE HOST", g - low < 1.0, f"on the ground at y {g:.2f}, lifted to {y0:.2f}, lowest {low:.2f} in 10 s; the joiner's copy at y {jy:.2f}")

        # B
        jc = spawn(L, CAR)
        if not jc or not until(lambda: jc in ents(h), 30): sys.exit(f"the joiner's car was not shared: {jc}")
        time.sleep(4)
        jp = pos(L, jc)
        h(f"tpground {jp[0] - AWAY} {jp[2]}"); time.sleep(2)
        off = until(lambda: (ent(h, jc) or {}).get("partsColOff") is True, 10)
        verdict(f"B: {AWAY} M OFF, THE HOST'S COPY OF THE JOINER'S CAR HAS ITS PARTS' COLLIDERS OFF", off, "")
        wb = wheel_off(L, jc)
        if not wb: sys.exit("B: no wheel came off the joiner's car")
        until(lambda: wb in ents(h), 15); time.sleep(4)
        print(f"  B: wheel {wb}: the host takes it:", h(f"mp claim {wb}"), flush=True)
        mine = until(lambda: (ent(h, wb) or {}).get("proxy") is False, 10)
        verdict("B: THE WHEEL IS THE HOST'S", mine, f"{ent(h, wb)}"[:160])
        g = pos(h, wb)[1]
        print("  B: lift the wheel:", h(f"mp moveitem {wb} 0.3 0"), flush=True)
        y0, low = watch(h, wb, 10)
        verdict("B: THE WHEEL STAYS ON THE GROUND ON THE HOST", g - low < 1.0, f"on the ground at y {g:.2f}, lifted to {y0:.2f}, lowest {low:.2f} in 10 s")
        for s, who in ((h, "host"), (L, "joiner")):
            print(f"  {who}: kept {s(f'get {T}.LodPartsOffKept')['value']}, parts' colliders off {s(f'get {T}.PartCollidersOff')['value']} on {s(f'get {T}.PartCollidersOn')['value']}", flush=True)
    finally:
        for s in (h, L):
            try: s(f"set {T}.LodKeepsPartsOff True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
L.close()
print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
sys.exit(0 if V and all(ok for _, ok in V) else 1)
