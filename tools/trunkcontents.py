#!/usr/bin/env python3
"""What lies in a car's trunk when the car is shared (Entities.StoredContentsApart; the 2026-11-06 playtest: "things from
my trunk are STILL desynced, they dont appear for me when they take it from my trunk"). The host loads a save with things
stored in its car (TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default Playing: a bug bundle's
world/start), hosts, the joiner joins:
  A  the stored things: objects of their own, stored in the car, on both machines (--old: members of the car's group)
  B  the joiner takes one out and puts it down 3 m beside the car: on the host it leaves the trunk too, and lies where
     the joiner put it
  tools/trunkcontents.py [--no-build] [--old] [--car Car06P]"""
import sys, os, time, math, json, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both

SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "Playing")
SAVE = "trunkcontents"
OLD = "--old" in sys.argv
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06P"
T = "TLDRevamp.Net.Entities"

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

def ents(s): return {e["net"]: e for e in s("mp entities 2500")["entities"]}
def members(s, car):
    try: return s(f"mp entitems {car}")["items"]
    except RuntimeError: return []
def act(s, kind, net, item=-1, x=0, y=0, z=0):
    k = {"pickup": 4, "drop": 5}[kind]
    return s(f"repro act {k} {net} 0 {item} -1 {x} {y} {z} 0 0 0 0 0 0 0 0")

def stored_members(s, car): return [m for m in members(s, car) if m.get("loaded") and m.get("phys") and not m.get("attached")]
def stored_objects(s, car, ce):
    """objects of their own held by a container of `car` (`mp lockdiag`)"""
    out = []
    for n, e in ents(s).items():
        if e["car"] or e["part"] >= 0 or math.dist(e["pos"], ce["pos"]) > 6: continue
        try: lk = s(f"mp lockdiag {n}").get("lock")
        except RuntimeError: continue
        # the container by net on a copy; on the owner (no copy to name) by its name
        if isinstance(lk, dict) and (lk.get("containerNet") == car or lk.get("containerNet") == 0 and lk.get("container") == ce["name"]): out.append((n, e["name"]))
    return out

if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*")): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
L = Agent()
ensure_both("--no-build" not in sys.argv)
copied, h = [], None
with game.ProtectedSession():
    try:
        for f in SRC.glob(f"TLDsave*_{SRC_NAME}.*"):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        L("!seeded")
        h = game.loadsave(SAVE)
        for s in (h, L):
            s("set TLDRevamp.TestSafety.BlockAutoSave True"); s("bg on"); s("set UnityEngine.QualitySettings.vSyncCount 0"); s("set kaposztaleves.s.settings.god True")
            s(f"set {T}.StoredContentsApart {not OLD}")   # before the share: the host shares its world as it hosts
        if not join_both(h, L): sys.exit("THE JOINER NEVER JOINED")
        if OLD:
            for s in (h, L): s("set TLDRevamp.ReproPlayback.Active True")
        cars = [n for n, e in ents(h).items() if e["car"] and not e["dormant"] and not e["proxy"]]
        if not cars: sys.exit("no car of the host's loaded")
        named = [n for n in cars if ents(h)[n]["name"].startswith(CAR)]
        car = named[0] if named else max(cars, key=lambda n: len(stored_members(h, n)) + len(stored_objects(h, n, ents(h)[n])))
        ce = ents(h)[car]
        L(f"tpground {ce['pos'][0] - 3} {ce['pos'][2]}")
        until(lambda: car in ents(L) and not ents(L)[car]["dormant"], 20); time.sleep(6)
        sm = stored_members(h, car)
        so = stored_objects(h, car, ce) if OLD else until(lambda: stored_objects(h, car, ce), 20) or []
        if not OLD and not so:   # what is there instead
            for n, e in ents(h).items():
                if not e["car"] and e["part"] < 0 and math.dist(e["pos"], ce["pos"]) < 6:
                    print("   near:", n, e["name"], json.dumps(h(f"mp lockdiag {n}").get("lock"))[:200], flush=True)
            print("   counters:", h(f"get {T}.StoredContentsShared"), h("mp entities")["count"] if "count" in h("mp entities") else "", flush=True)
        print(f"  the car: net {car} {ce['name']}; stored members {[(m['i'], m['name']) for m in sm]}; stored objects {so}", flush=True)
        if OLD: verdict("A (OLD): STORED THINGS ARE MEMBERS OF THE CAR", sm, f"{len(sm)} members")
        else: verdict("A: STORED THINGS ARE OBJECTS OF THEIR OWN, NONE A MEMBER", so and not sm, f"{len(so)} objects, {len(sm)} members")
        if OLD and sm: m_ = next((m for m in sm if "OilCan" in m["name"]), sm[0]); pick, item, name = car, m_["i"], m_["name"]
        elif so: pick, item, name = next((x for x in so if "OilCan" in x[1]), so[0])[0], -1, next((x for x in so if "OilCan" in x[1]), so[0])[1]
        else: sys.exit("nothing stored in the car")
        lj = until(lambda: stored_objects(L, car, ents(L)[car]) if not OLD else stored_members(L, car), 15)
        print(f"  the joiner sees stored: {lj}", flush=True)

        # B
        def where(side):
            """the thing's place in the car's frame on that machine (a member: entitems; an object: its position)"""
            if OLD:
                m = next((m for m in members(side, car) if m["i"] == item), None)
                return m["lp"] if m and m.get("loaded") else None
            e, c = ents(side).get(pick), ents(side).get(car)
            return [a - b for a, b in zip(e["pos"], c["pos"])] if e and c else None
        h0, j0 = where(h), where(L)
        if OLD:   # a member has no net id to hold by: the game's pickup as a recording replays it
            print(f"  joiner takes {name} (net {pick} item {item}):", act(L, "pickup", pick, item), flush=True); time.sleep(2)
            print("  joiner lets go:", act(L, "drop", pick, item), flush=True)
        else:     # in the hand (held out in front of the joiner, 3 m from the car), let go
            print(f"  joiner takes {name} (net {pick}):", L(f"mp holdnet {pick}"), flush=True)
            for _ in range(4):
                time.sleep(0.5)
                print("    holding:", L("get mainscript.s.player.pickedUp.name").get("value"), "owner here", ents(L)[pick]["owner"], flush=True)
            st = L("mp status"); print("    claims refused:", st.get("claimsRefused"), st.get("refusedWhy"), flush=True)
            try: print("  joiner lets go:", L("dropheld"), flush=True)
            except RuntimeError as ex: print("  joiner lets go:", str(ex)[:120], flush=True)
        time.sleep(8)
        h1, j1 = where(h), where(L)
        mh, mj = (math.dist(h0, h1) if h0 and h1 else -1), (math.dist(j0, j1) if j0 and j1 else -1)
        print(f"  moved from its place in the trunk: host {mh:.2f} m, joiner {mj:.2f} m", flush=True)
        verdict("B: THE JOINER TOOK IT OUT", mj > 1.0, f"{mj:.2f} m")
        verdict("B: ON THE HOST IT LEFT THE TRUNK TOO", mh > 1.0, f"{mh:.2f} m")
        verdict("B: WHERE THE JOINER PUT IT", h1 and j1 and math.dist(h1, j1) < 0.5, f"{math.dist(h1, j1) if h1 and j1 else -1:.2f} m apart")
        verdict("B: THE CAR STAYED THE HOST'S", ents(h)[car]["owner"] == 0, f"owner {ents(h)[car]['owner']}")
        L("mp stop"); h("mp stop")
    finally:
        for s in (h, L) if h else (L,):
            try: s(f"set {T}.StoredContentsApart True"); s("set TLDRevamp.ReproPlayback.Active False")
            except Exception: pass
        for f in copied:
            try: f.unlink()
            except OSError: pass
        L.close()
print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
sys.exit(0 if V and all(ok for _, ok in V) else 1)
