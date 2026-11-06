#!/usr/bin/env python3
"""A thing glued on with a body of its own (the game's ball joint: attachablescript.parentAtAttach off) on a car, and
another player's machine (Entities.CopyJoints, the 2026-11-06 playtest: an Attachable07 on the client's roof held the
host's copy of that car 5-16 m off it, "car tweaking", "heavy positional desync"), two players:
  A  the joiner glues the thing on its own car, parked beside the host's car (so the host's copy of it is a physical
     body): on the host the thing hangs on its joint, a copy, its joint free
  B  the joiner drives its car off and stops: the host's copy follows it and rests where the car rests
  C  the joiner glues a second thing on the HOST's car: the host drives it, and it drives as without the thing
--old runs with CopyJointsFree off (the joints stay locked on copies, as before).
  tools/copyjoint.py [--no-build] [--relay iad|off] [--old] [--car Car06Full] [--item Attachable07]"""
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
RELAY, CAR, ITEM = arg("--relay", "off"), arg("--car", "Car06Full"), arg("--item", "Attachable07")
OLD = "--old" in sys.argv

V = []
def verdict(name, ok, detail=""):
    V.append((name, bool(ok))); print(f"  {'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def pos(s, net):
    e = ents(s).get(net)
    return e["pos"] if e else None
def gap(a, b, net):
    pa, pb = pos(a, net), pos(b, net)
    return math.dist(pa, pb) if pa and pb else float("inf")

def spawn(s, name, car):
    before = set(ents(s))
    print(f"  spawn {name}:", s(f"devspawn {name}"), flush=True)
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and bool(e["car"]) == car and not e["proxy"] and e["part"] < 0), None), 20)

def joints(s): return {j["net"]: j for j in s("mp copyjoints")["jointed"]}

def glue_jointed(s, car):
    """a new ITEM glued on `car` on a joint of its own: the glue hits a door frame (a sunroof) and parents it there,
    so other spots are tried with new items"""
    for dx in (0.0, 0.55, -0.55, 0.8, -0.8):
        it = spawn(s, ITEM, False)
        if not it: continue
        print(f"  glue at {dx:+.2f} m:", s(f"mp glue {it} {car} {dx}"), flush=True)
        if until(lambda: it in joints(s), 5): return it
        print("    parented there, not on a joint", flush=True)
    return None

def drive(s, kmh, secs, label):
    """the player in its car (beside it): engine on, drives `secs` at `kmh`, stops; returns the car's path length"""
    r = s("drivein")
    if r.get("stepped"): time.sleep(0.4); r = s("drivein")   # stepped beside the seat first
    print("  in:", r, flush=True); time.sleep(1); s("fuelup")
    net = None
    start = s("gpos"); p0 = (start["gx"], start["gy"], start["gz"])
    s(f"autodrive on {kmh}")
    path, last, top = 0.0, p0, 0.0
    t = time.time()
    while time.time() - t < secs:
        time.sleep(0.5)
        g = s("gpos"); p = (g["gx"], g["gy"], g["gz"])
        path += math.dist(p, last); last = p
        top = max(top, abs(float(s("get mainscript.s.player.Car.speed").get("value") or 0)))
    s("autodrive off"); s("brakecar"); s("handbrake on")
    print(f"  {label}: {path:.1f} m in {secs} s, top {top:.0f} km/h", flush=True)
    time.sleep(2); s("driveout"); time.sleep(1)
    return path, top

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("THE SECOND PLAYER NEVER JOINED")
    try:
        for s in (h, L):
            s(f"set TLDRevamp.Net.Entities.CopyJointsFree {not OLD}")
            s("set kaposztaleves.s.settings.god True"); s("set TLDRevamp.TestSafety.BlockAutoSave True")
        h("mp policy everyone")
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); L(f"tpground {hp['gx'] + 15} {hp['gz'] + 7}"); time.sleep(4)
        hc = spawn(h, CAR, True); jc = spawn(L, CAR, True)
        if not hc or not jc: sys.exit(f"cars not spawned: host {hc} joiner {jc}")
        if not until(lambda: hc in ents(L) and jc in ents(h), 30): sys.exit("the cars never reached the other machine")
        time.sleep(5)
        print(f"  cars: host's {hc} at {pos(h, hc)}, joiner's {jc} at {pos(L, jc)}", flush=True)

        # A
        jp = pos(L, jc); L(f"tpground {jp[0] + 2.5} {jp[2]}"); time.sleep(2)
        it = glue_jointed(L, jc)
        ok = until(lambda: it in joints(L) and it in joints(h), 15)
        jl, jh = joints(L).get(it, {}), joints(h).get(it, {})
        print("  joiner:", jl, "\n  host:  ", jh, flush=True)
        verdict("A: THE THING HANGS ON ITS JOINT ON BOTH MACHINES", ok and jl.get("onNet") == jc and jh.get("onNet") == jc, f"joiner on {jl.get('onNet')}, host on {jh.get('onNet')}")
        phys = h("mp physical")
        print("  host's physical copies:", json.dumps(phys)[:300], flush=True)
        time.sleep(4)
        jh = joints(h).get(it, {})
        verdict("A: ON THE HOST A COPY, ITS JOINT " + ("LOCKED (OLD)" if OLD else "FREE"),
                jh.get("proxy") is True and jh.get("free") is (not OLD), f"{jh}")
        rest0 = gap(h, L, jc)
        print(f"  joiner's car parked: host's copy {rest0:.2f} m off", flush=True)

        # B
        worst = 0.0
        L(f"tpground {jp[0] - 1.5} {jp[2]}"); time.sleep(2)
        path, _ = drive(L, 8, 5, "joiner drives")
        for _ in range(16):
            worst = max(worst, gap(h, L, jc)); time.sleep(0.5)
        rest = gap(h, L, jc)
        verdict("B: THE JOINER'S CAR MOVED", path > 3, f"{path:.1f} m")
        verdict("B: THE HOST'S COPY RESTS WHERE THE CAR RESTS", rest < 0.5, f"{rest:.2f} m off 8 s after the stop (worst {worst:.2f} m while it settled)")

        # C
        hp2 = pos(h, hc); L(f"tpground {hp2[0] + 2.5} {hp2[2] + 2}"); time.sleep(2)
        it2 = glue_jointed(L, hc)
        # the thing goes to the car's owner with the glue; the joiner takes it back (a thing of one player on another's
        # car: how the playtest's rims ended up, by a hand-off)
        ok = until(lambda: joints(h).get(it2, {}).get("onNet") == hc, 15)
        print("  joiner claims it back:", L(f"mp claim {it2}"), flush=True)
        ok = ok and until(lambda: joints(h).get(it2, {}).get("proxy") is True, 10)
        jh2 = joints(h).get(it2, {})
        print("  host:", jh2, flush=True)
        verdict("C: THE JOINER'S THING HANGS ON THE HOST'S CAR, A COPY THERE", ok and jh2.get("onNet") == hc and jh2.get("proxy") is True, f"{jh2}")
        # the host steps in on the side away from the joiner's car (drivein takes the nearest free driver's seat)
        jp2 = pos(h, jc); away = [hp2[0] - jp2[0], hp2[2] - jp2[2]]; n_ = math.hypot(*away) or 1
        L(f"tpground {hp2[0] + 12} {hp2[2] + 12}"); h(f"tpground {hp2[0] + away[0] / n_ * 2.5} {hp2[2] + away[1] / n_ * 2.5}"); time.sleep(2)
        c0 = pos(h, hc)
        path2, top2 = drive(h, 20, 8, "host drives its car")
        print(f"  the host's car moved {math.dist(c0, pos(h, hc)):.1f} m", flush=True)
        verdict("C: THE HOST'S CAR DRIVES", path2 > 20 and top2 > 12, f"{path2:.1f} m, top {top2:.0f} km/h")
        time.sleep(4)
        at = L(f"mp attachpose {it2}"); ah = h(f"mp attachpose {it2}")
        verdict("C: THE THING STAYED ON THE CAR ON BOTH MACHINES", at.get("attached") is True and ah.get("attached") is True, f"joiner {at}, host {ah}")
        print("  host joints:", h("mp copyjoints"), flush=True)
    finally:
        for s in (h, L):
            try: s(f"set TLDRevamp.Net.Entities.CopyJointsFree True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
L.close()
print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
sys.exit(0 if V and all(ok for _, ok in V) else 1)
