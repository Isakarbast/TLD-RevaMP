#!/usr/bin/env python3
"""The game's VR switch (mainscript.SetVR(false): the settings refresh calls it outside VR too) puts a turnable usable's
collider back on its own layer, also on the thing in the player's right hand (Fixes.HandLayersAfterVr; the 2026-11-06
playtest: a lamp in the hand, the settings applied, then the host's car stopped dead at 60 km/h three times, each
against its own kinematic collider). On the host's game, seated in its car with a lamp taken in the right hand:
  round old (the fix off) and new: after SetVR(false), is every collider of the lamp still on the hand's layer?
  --drive: then drives the car on the road at 80 km/h for 20 s: does it get past 65 km/h without a crash?
  tools/handlayer.py [--no-build] [--item ujlamp] [--drive]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
ITEM, CAR = arg("--item", "ujlamp"), arg("--car", "Car06Full")
DRIVE = "--drive" in sys.argv

V = []
def verdict(name, ok, detail=""):
    V.append((name, bool(ok))); print(f"  {'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def spawn(s, name, car):
    before = set(ents(s))
    print(f"  spawn {name}:", s(f"devspawn {name}"), flush=True)
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and bool(e["car"]) == car and not e["proxy"] and e["part"] < 0), None), 20)

def off_hand(r):
    """the colliders of the thing in the right hand that are not on the hand's layer"""
    hand = r.get("inHand")
    if not isinstance(hand, dict): return None
    return [(c["name"], c["layer"]) for c in hand["colliders"] if c["layer"] != r["rhLayer"]]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    if not join_both(h, L): sys.exit("THE SECOND PLAYER NEVER JOINED")   # shared objects: the test reaches them by net id
    try:
        h("set kaposztaleves.s.settings.god True"); h("set TLDRevamp.TestSafety.BlockAutoSave True")
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)
        car = spawn(h, CAR, True)
        time.sleep(3)
        def take_lamp():   # standing: spawned while seated it isn't shared as a loose thing
            lamp = spawn(h, ITEM, False)
            print("  hold:", h(f"mp holdnet {lamp}"), flush=True); time.sleep(1)
            print("  right hand:", h("call mainscript.s.player.RightHandGrip"), flush=True); time.sleep(1.5)
        take_lamp()
        r = h("drivein")
        if r.get("stepped"): time.sleep(0.4); r = h("drivein")
        print("  in:", r, flush=True); time.sleep(1); h("fuelup")
        for fix in (False, True):
            h(f"set TLDRevamp.Fixes.HandLayersAfterVr.Enabled {fix}")
            before = h("handlayers")
            print("  before:", json.dumps(before)[:400], flush=True)
            h("call mainscript.s.SetVR False"); time.sleep(1)
            after = h("handlayers")
            print("  after: ", json.dumps(after)[:400], flush=True)
            b, a = off_hand(before), off_hand(after)
            name = "NEW" if fix else "OLD"
            verdict(f"{name}: THE LAMP IS IN THE RIGHT HAND, ALL ON THE HAND'S LAYER", b == [], f"off the hand's layer: {b}")
            if fix: verdict("NEW: AFTER THE VR SWITCH STILL ALL ON THE HAND'S LAYER", a == [], f"off: {a}")
            else: print(f"  OLD: after the VR switch off the hand's layer: {a}", flush=True)
            if not fix:   # as when it's taken in hand again (the playtest's host did): the game's hand layer back
                print("  back on the hand's layer:", h(f"call mainscript.s.player.inHandP.changeLayer {after['rhLayer']}"), flush=True)
        if DRIVE:
            h("autodrive on 80"); top = 0.0; t = time.time()
            while time.time() - t < 20:
                time.sleep(0.5); top = max(top, abs(float(h("get mainscript.s.player.Car.speed").get("value") or 0)))
            h("autodrive off"); h("brakecar")
            verdict("DRIVE: PAST 65 KM/H", top > 65, f"top {top:.0f} km/h")
    finally:
        try: h("set TLDRevamp.Fixes.HandLayersAfterVr.Enabled True"); L("mp stop"); h("mp stop")
        except Exception: pass
L.close()
print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
sys.exit(0 if V and all(ok for _, ok in V) else 1)
