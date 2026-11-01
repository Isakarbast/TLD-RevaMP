#!/usr/bin/env python3
"""Try to break items on two machines (playtest 2026-10-28/29: "items don't physlock and don't sync when I pick them up",
"some items never physlock until the save is loaded again"). Every action goes the game's own way as the player
(repro act: detach, pickup, drop, throw, seat; MountAnything's glue press), in the cars of BOTH players: a world save with
glued things on the car (TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default autosave1), both
copies with the same mods. After every step the two machines are compared; what still differs after the settle time
is a finding, with the steps that led to it.
  An object both have: the same owner, exactly one real one, the same number of parts on, the same parent, the same
  place at rest, the same physics-lock container. A member of a car: the same on and off on both. An item at rest whose
  centre is in a storage area that can store it and that nobody holds: held by a container (on both machines).
  Nothing refused (part-off), no exceptions.
Scripted runs of what the reports did, then a random monkey (--seed, --steps).
  tools/itemchaos.py [--no-build] [--relay off|iad] [--seed 1] [--steps 60] [--only NAME[,NAME]] [--no-monkey] [--oldglue]"""
import sys, os, time, math, json, random, re, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both, DESKTOP_IP
from roadkit import until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "off")
if RELAY == "off": RELAY = None
SEED, STEPS = arg("--seed", 1, int), arg("--steps", 60, int)
ONLY = [x for x in arg("--only", "").split(",") if x]
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "autosave1")
SAVE = "itemchaos"
T = "TLDRevamp.Net.Entities"
SETTLE = 5.0        # after a step
PERSIST = 12.0      # a difference must still be there this much later
LOOSE = {"can": 60, "lamp": 66, "sponge": 119, "jerry": 72}     # itemdatabase ids, what gets spawned to play with

KIND = dict(pose=1, seat_in=2, seat_out=3, pickup=4, drop=5, throw=6, inv_switch=7, inv_store=9, inv_unstore=10, usable=13, attach=15, detach=16)
HISTORY, FINDINGS = [], []
def note(s):
    HISTORY.append(s); print(f"  [{len(HISTORY):3}] {s}", flush=True)
def finding(tag, detail):
    FINDINGS.append({"tag": tag, "detail": detail, "steps": HISTORY[-8:]})
    print(f"FINDING  {tag}: {detail}", flush=True)

class Player:
    def __init__(self, s, label):
        self.s, self.label = s, label
    def __call__(self, cmd): return self.s(cmd)
    def act(self, kind, net=0, other=0, item=-1, slot=-1, x=0, y=0, z=0, a=0):
        r = self.s(f"repro act {KIND[kind]} {net} {other} {item} {slot} {x} {y} {z} {a} 0 0 0 0 0 0 0")
        note(f"{self.label} {kind} net {net} item {item} slot {slot} -> {str(r)[:70]}")
        return r
    def try_act(self, *a, **k):
        try: return self.act(*a, **k)
        except RuntimeError as ex:
            note(f"{self.label} {a[0]} refused: {str(ex)[:60]}...{str(ex)[-90:]}"); return None
    def ents(self): return {e["net"]: e for e in self.s("mp entities 2500")["entities"]}
    def me(self): return self.s("mp entities")["me"]
    def members(self, car):
        try: return self.s(f"mp entitems {car}")["items"]
        except RuntimeError: return []
    def pose_on(self, net):
        try: return self.s(f"mp attachpose {net}")
        except RuntimeError: return None
    def slots(self, car):
        try: return {(x["i"], x["s"]): x for x in self.s(f"mp carslots {car}")["slots"]}
        except RuntimeError: return {}
    def lock(self, net):
        try: return self.s(f"mount whynot {net}")
        except RuntimeError: return None
    def held(self):
        try: return self.s("get mainscript.s.player.pickedUp.name").get("value")
        except RuntimeError: return None
    def seated(self):
        try: return str(self.s("get mainscript.s.player.Bsitting").get("value")) == "True"
        except RuntimeError: return False

def qrot(q, v):
    x, y, z, w = q
    t = (2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0]))
    return (v[0] + w * t[0] + (y * t[2] - z * t[1]), v[1] + w * t[1] + (z * t[0] - x * t[2]), v[2] + w * t[2] + (x * t[1] - y * t[0]))

# ---- what must be the same
def differences(h, L, tracked, cars):
    """every difference between the machines now, as {key: text}"""
    eh, el = h.ents(), L.ents()
    out = {}
    for n in set(tracked) | set(cars):
        a, b = eh.get(n), el.get(n)
        if a is None and b is None: continue
        if a is None or b is None:
            near = a or b
            if near.get("dormant") or not near.get("name"): continue    # far away: the game has it streamed out, on one machine
            out[f"exists {n}"] = f"{near['name']} only on the {'host' if a else 'joiner'} at {near['pos']}"
            continue
        if a.get("dormant") or b.get("dormant"): continue
        name = a["name"]
        if a["owner"] != b["owner"]: out[f"owner {n}"] = f"{name}: host says {a['owner']}, joiner says {b['owner']}"
        if a["proxy"] == b["proxy"]: out[f"real {n}"] = f"{name}: copy on both or real on both ({a['proxy']})"
        if a["items"] != b["items"]: out[f"members {n}"] = f"{name}: {a['items']} members on the host, {b['items']} on the joiner"
        if a["attached"] != b["attached"]: out[f"on {n}"] = f"{name}: {a['attached']} attached on the host, {b['attached']} on the joiner"
        if a["parent"] != b["parent"]: out[f"parent {n}"] = f"{name}: parent {a['parent']} on the host, {b['parent']} on the joiner"
        held = a.get("held") or b.get("held")
        if held and n in tracked:
            for who, x in (("host", h), ("joiner", L)):
                l = x.lock(n)
                if l and l.get("physlock"): out[f"held-lock {n} {who}"] = f"{name}: in a hand, and held by {l['physlock']} on the {who} (owner {a['owner']})"
        if not held and not a.get("stored") and not b.get("stored"):
            d = math.dist(a["pos"], b["pos"])
            moving = max(abs(x) for x in (a.get("vel") or [0])) > 0.3 or max(abs(x) for x in (b.get("vel") or [0])) > 0.3
            if d > 0.6 and not moving: out[f"place {n}"] = f"{name} net {n} owner {a['owner']}: {d:.2f} m apart at rest ({a['pos']} / {b['pos']})"
        if n in tracked and not held and not a["car"] and not a.get("stored") and not b.get("stored"):
            la, lb = h.lock(n), L.lock(n)
            if la and lb:
                if la.get("physlock") != lb.get("physlock"):
                    out[f"lock {n}"] = f"{name}: container '{la.get('physlock')}' on the host, '{lb.get('physlock')}' on the joiner (owner {a['owner']})"
                for who, l, e in (("host", la, a), ("joiner", lb, b)):
                    if not l.get("physlock") and not a["attached"] and not l.get("dontMount"):
                        ar = [x["name"] for x in l.get("areas", []) if x["centreInBox"] and x["canStore"] and not x["manual"] and not x["noLock"]]
                        if ar: out[f"never {n} {who}"] = f"{name}: in {ar[0]} on the {who} and not held (owner {a['owner']}, copy {e['proxy']}, in scan {[x['inScan'] for x in l['areas'] if x['centreInBox']][:1]})"
    for n in tracked:
        a, b = eh.get(n), el.get(n)
        if not a or not b or a.get("dormant") or b.get("dormant"): continue
        pa, pb = h.pose_on(n), L.pose_on(n)
        if pa and pb and pa.get("attached") != pb.get("attached"): out[f"part-on {n}"] = f"{a['name']}: bolted on {pa.get('attached')} on the host, {pb.get('attached')} on the joiner"
        elif pa and pb and pa.get("attached"):
            if pa.get("on") != pb.get("on"): out[f"part-parent {n}"] = f"{a['name']}: on {pa.get('on')} on the host, {pb.get('on')} on the joiner"
            elif math.dist(pa["pos"], pb["pos"]) > 0.15: out[f"part-place {n}"] = f"{a['name']}: {math.dist(pa['pos'], pb['pos']):.2f} m apart on {pa.get('on')} ({pa['pos']} / {pb['pos']})"
    for car in cars:
        ma, mb = h.members(car), L.members(car)
        sa, sb = h.slots(car), L.slots(car)
        for k in sa.keys() & sb.keys():
            pa, pb = sa[k]["parts"], sb[k]["parts"]
            if sorted(pa) != sorted(pb): out[f"slot {car}/{k[0]}/{k[1]}"] = f"member {k[0]} slot {k[1]}: {pa or 'empty'} on the host, {pb or 'empty'} on the joiner"
            for who, l, st in (("host", pa, sa[k]), ("joiner", pb, sb[k])):
                if len(l) > 1 and not st.get("many") and f"{car}/{k}" not in DOUBLED_AT_START: out[f"double {car}/{k[0]}/{k[1]} {who}"] = f"member {k[0]} slot {k[1]}: {l} in one slot on the {who}"
        for x, y in zip(ma, mb):
            if not x.get("loaded") or not y.get("loaded"):
                if x.get("loaded") != y.get("loaded") and not x.get("stored") and not y.get("stored"): out[f"member-absent {car}/{x['i']}"] = f"member {x['i']}: {x.get('name') or y.get('name')} loaded on the {'host' if x.get('loaded') else 'joiner'} only"
                continue
            if x.get("name") != y.get("name"): out[f"member {car}/{x['i']}"] = f"member {x['i']}: {x.get('name')} on the host, {y.get('name')} on the joiner"
            elif x.get("attached") != y.get("attached"): out[f"member-on {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): attached {x['attached']} on the host, {y['attached']} on the joiner"
            elif x.get("on") != y.get("on") or x.get("onSlot") != y.get("onSlot"): out[f"member-slot {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): on member {x.get('on')} slot {x.get('onSlot')} on the host, member {y.get('on')} slot {y.get('onSlot')} on the joiner"
            # in the car's frame: a tyre on a wheel 170 m off the car is 3 m apart for a car turned 1 degree between the copies
            elif x.get("attached") and x.get("lp") and y.get("lp") and math.hypot(*x["lp"]) < 30 and math.dist(x["lp"], y["lp"]) > 0.15: out[f"member-place {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): {math.dist(x['lp'], y['lp']):.2f} m apart in the car's frame ({x['lp']} / {y['lp']})"
            elif x.get("phys") != y.get("phys"): out[f"member-lock {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): container '{x.get('phys')}' on the host, '{y.get('phys')}' on the joiner"
    return out

DOUBLED_AT_START = set()   # a save that already has two doors in a slot: not this run's doing
_counts = {}
def check(h, L, tracked, cars, tag, settle=SETTLE):
    time.sleep(settle)
    first = differences(h, L, tracked, cars)
    if first:
        time.sleep(PERSIST)
        again = differences(h, L, tracked, cars)
        for k, v in again.items():
            if k in first: finding(tag + " / " + k.split()[0], v)
    for who, s in (("host", h), ("joiner", L)):
        e = s("errors")
        n = e.get("errors", 0) + e.get("exceptions", 0)
        if n > _counts.get(who, 0):
            finding(tag + " / errors", f"{who}: {n - _counts.get(who, 0)} new: {str(e.get('kinds'))[:300]}")
            _counts[who] = n
    for who, s in (("host", h),):
        r = s(f"get {T}.PartOffRejected").get("value", 0)
        if r > _counts.get("partoff", 0):
            finding(tag + " / part-off", f"{r - _counts.get('partoff', 0)} refused: {s(f'get {T}.PartRejects').get('value')}")
            _counts["partoff"] = r

# ---- the world
def spawn(p, what):
    before = set(p.ents()); me = p.me()
    p(f"devspawn {LOOSE.get(what, what)}")
    got = []
    until(lambda: got.extend(n for n, e in p.ents().items() if n not in before and not e["car"] and e.get("name") and not e.get("dormant")) or got, 10)
    if not got: raise RuntimeError(f"{what} never appeared")
    note(f"{p.label} spawned {what}: net {got[0]}")
    return got[0]

def glued(p, car):
    return [m for m in p.members(car) if m.get("loaded") and m.get("attached") and not m.get("slot")]
def bolted(p, car):
    return [m for m in p.members(car) if m.get("loaded") and m.get("attached") and m.get("slot")]

def beside(p, car_e, off=(2.6, 0.0, 0.0)):
    v = qrot(car_e["rot"], off)
    p(f"tpground {car_e['pos'][0] + v[0]} {car_e['pos'][2] + v[2]}"); time.sleep(1.5)

def bed(h, car):
    a = h(f"mount areas {car}")["areas"]
    t = next((x for x in a if x["type"] == "trunk"), None) or next((x for x in a if x["type"] == "passengerRoom"), None)
    return t

def car_frame(c, pos):
    q = c["rot"]; inv = (-q[0], -q[1], -q[2], q[3])
    return qrot(inv, [pos[i] - c["pos"][i] for i in range(3)])

def into_car(p, car, net, ents_of):
    """let what the player holds go over the car's bed, as a player does: standing beside the bed, facing it, the thing held
    out over it (seated: inside the cab, in front of the seat)"""
    if not p.seated():
        t = bed(p, car); c = p.ents()[car]
        if t is None: note(f"{car}: no bed"); return
        v = qrot(c["rot"], (t["local"][0] + 1.25, 0, t["local"][2])); r = qrot(c["rot"], (1, 0, 0))
        p(f"tpground {c['pos'][0] + v[0]} {c['pos'][2] + v[2]}"); time.sleep(0.5)
        g = p("gpos")
        p.act("pose", x=g["gx"], y=g["gy"], z=g["gz"], a=math.degrees(math.atan2(-r[0], -r[2])))
        time.sleep(0.5)
    try: note(f"{p.label} holds {net} out: {str(p(f'mp holdnet {net}'))[:50]}")
    except RuntimeError as ex: note(f"{p.label} holdnet {net}: {str(ex)[:80]}")
    time.sleep(0.8)
    es = p.ents(); c = es[car]; e = es.get(net)
    if e: note(f"  {net} in the car's frame {[round(x, 2) for x in car_frame(c, e['pos'])]}")
    p.try_act("drop", net=net, item=0)
    if "--trace" in sys.argv:
        for w in (0.2, 0.4, 0.8, 1.5, 3):
            time.sleep(w); es = p.ents(); e2 = es.get(net)
            if e2: print(f"     trace +{w}: in the car's frame {[round(x, 2) for x in car_frame(es[car], e2['pos'])]}, owner {e2['owner']}, copy {e2['proxy']}, the car moved {math.dist(es[car]['pos'], c['pos']):.2f} m", flush=True)
    time.sleep(1.2)

def take_car(p, car):
    for _ in range(4):
        p(f"mp sitdriver {car}"); time.sleep(2)
        if p.seated(): break
    time.sleep(2)
    try: p("driveout")
    except RuntimeError: pass
    note(f"{p.label} sat in the driver's seat of {car} and got out"); time.sleep(3)

# ---- scripted: what the reports did
def run_glue_off(h, L, A, car, tracked, cars, tag, drop_in):
    """A takes a glued thing off car `car` the way a player does: detach, in the hand, let go. Report 191428: the same
    member could be taken off four times in a row: it was still on, for the owner and for the game."""
    g = glued(A, car)
    if not g: note(f"{tag}: nothing glued on car {car}"); return None
    m = g[0]; i = m["i"]; name = m["name"]
    e = A.ents()[car]; beside(A, e); time.sleep(1)
    known = set(A.ents())
    A.try_act("detach", net=car, item=i)
    A.try_act("pickup", net=car, item=i)
    time.sleep(1.5)
    held = A.held()
    new = [(n, x["name"]) for n, x in A.ents().items() if n not in known and x.get("name")]
    note(f"{tag}: {A.label} took off {name} (member {i}): in the hand {held}; new objects {new}")
    part = next((n for n, x in new if x == name), None)
    if held is None: finding(tag + " / hand", f"{name}: not in {A.label}'s hand after the detach")
    if part is None: finding(tag + " / split", f"{name}: no object of its own after the detach (still a member of {car})")
    else: tracked.add(part)
    time.sleep(1)
    if drop_in and part: into_car(A, car, part, None)
    else: A.try_act("drop", net=part or car, item=0 if part else i)
    check(h, L, tracked, cars, tag)
    again = next((x for x in A.members(car) if x["i"] == i), None)
    if again and again.get("attached"): finding(tag + " / still on", f"{name}: still attached on {A.label} after it was taken off and let go")
    return part

def doorcase(h, L, car, m, tracked, cars):
    i = m["i"]; name = m["name"]; own = [None]
    def snap(tag):
        def row(p):
            sl = p.slots(car).get((m["on"], m["onSlot"]), {}).get("parts")
            e = p.ents().get(own[0]) if own[0] else None
            return f"{sl} / {'loose or held' if e and not e['parent'] else ''}{' dormant' if e and e.get('dormant') else ''}"
        print(f"     {tag}: slot on the host {row(h)}, on the joiner {row(L)}", flush=True)
    def off(A):
        beside(A, A.ents()[car])
        if own[0]: A.try_act("detach", net=own[0], item=0)
        else:
            known = set(A.ents()); A.try_act("detach", net=car, item=i); time.sleep(3)
            new = [k for k, v in A.ents().items() if k not in known and v.get("name") == name]
            if new: own[0] = new[0]; tracked.add(new[0])
            else: finding(f"door-chaos {A.label}", f"{name}: no object of its own after the detach")
        time.sleep(3)
    def on(A):
        beside(A, A.ents()[car])
        A.try_act("attach", net=own[0], other=car, item=m["on"] + 1, slot=m["onSlot"], a=2); time.sleep(4)
    def far(P, go, home={}):
        if go:
            g = P("gpos"); home[P.label] = (g["gx"], g["gz"]); P(f"tpground {g['gx'] + 3000} {g['gz']}"); note(f"{P.label} goes 3 km away"); time.sleep(12)
        else:
            P(f"tpground {home[P.label][0]} {home[P.label][1]}"); note(f"{P.label} comes back"); time.sleep(14)
    def round_(tag, *steps):
        print(f"\n  -- {tag}", flush=True)
        for st in steps:
            st()
            snap(st.__name__ if hasattr(st, "__name__") else "")
        check(h, L, tracked, cars, f"door-chaos {tag}")
    def S(label, fn):
        fn.__name__ = label; return fn
    hoff, hon = S("host off", lambda: off(h)), S("host on", lambda: on(h))
    loff, lon = S("joiner off", lambda: off(L)), S("joiner on", lambda: on(L))
    round_("the host takes it off, the owner bolts it", hoff, lon)
    round_("the owner takes it off, the host bolts it", loff, hon)
    lfar, lback = S("joiner far", lambda: far(L, True)), S("joiner back", lambda: far(L, False))
    hfar, hback = S("host far", lambda: far(h, True)), S("host back", lambda: far(h, False))
    round_("off and on with the owner 3 km away", lfar, hoff, hon, lback)
    round_("off with the owner 3 km away, stays off", lfar, hoff, lback)
    round_("on again with the owner 3 km away", lfar, hon, lback)
    round_("the owner takes it off with the host 3 km away", hfar, loff, hback)
    round_("the owner bolts it with the host 3 km away", hfar, lon, hback)
    rejoin = S("joiner rejoined", lambda: rejoin_joiner(h, L))
    round_("off, the owner leaves and joins again", hoff, rejoin)
    round_("on, the owner leaves and joins again", hon, rejoin)
    rm = next((x for x in L.members(car) if x["i"] == i), {})
    round_("it falls off on the owner (crash)", S("falls", lambda: L.s(f"mp falloff {own[0] and car} {i}")), S("wait", lambda: time.sleep(3)), hon)
    round_("the game drops it on the copy only", S("falls on the copy", lambda: h.s(f"mp falloff {car} {i}")), S("wait", lambda: time.sleep(3)))
    def flipto(P):
        beside(P, P.ents()[car]); take_car(P, car)
    round_("off, the car changes hands, the old owner bolts it", loff, S("car to host", lambda: flipto(h)), lon, S("car to joiner", lambda: flipto(L)))
    def both():
        beside(h, h.ents()[car]); beside(L, L.ents()[car])
        h.try_act("attach", net=own[0], other=car, item=m["on"] + 1, slot=m["onSlot"], a=2); L.try_act("attach", net=own[0], other=car, item=m["on"] + 1, slot=m["onSlot"], a=2); time.sleep(4)
    round_("both bolt it in the same moment", loff, S("both on", both))

def rejoin_joiner(h, L):
    L("mp stop"); note("joiner left"); time.sleep(6)
    L(f"mp joinid {h('net me')['steamId']}") if RELAY else L(f"mp join {DESKTOP_IP}:27070")
    until(lambda: L("mp status")["client"]["ready"], 120); note("joiner back"); time.sleep(10)
    L("set TLDRevamp.ReproPlayback.Active True"); L("set kaposztaleves.s.settings.god True")

def run_pick_replace(h, L, A, car, net, tracked, cars, tag, seated=None):
    """A takes an item out of a car and puts it back, as many times as the player tries"""
    if seated is not None: A.try_act("seat_in", net=car, slot=seated); time.sleep(1)
    else: beside(A, A.ents()[car])
    for k in range(3):
        A.try_act("pickup", net=net, item=0); time.sleep(0.7 if k else 1.5)
        into_car(A, car, net, None)
        check(h, L, tracked, cars, f"{tag} round {k + 1}")
        la, lb = h.lock(net), L.lock(net)
        note(f"{tag} round {k + 1}: container host '{la and la.get('physlock')}', joiner '{lb and lb.get('physlock')}', owner {h.ents().get(net, {}).get('owner')}")
        if not seated and la and not la.get("physlock"): finding(tag + " / not in", f"net {net} put in the bed, in no container on the host; areas with its centre: {[x['name'] for x in la.get('areas', []) if x['centreInBox']]}")
    if seated is not None: A.try_act("seat_out", net=car, slot=seated); time.sleep(1)

def scripted(h, L, car, tracked, cars):
    def want(n): return not ONLY or n in ONLY
    hid, lid = h.me(), L.me()
    if want("own-glue"):
        print("\n=== the car is the host's: the host takes a glued thing off", flush=True)
        run_glue_off(h, L, h, car, tracked, cars, "own-glue", False)
    print("\n=== the joiner takes the car", flush=True)
    beside(L, L.ents()[car]); take_car(L, car)
    check(h, L, tracked, cars, "car changes hands")
    if want("other-glue"):
        print("\n=== the joiner's car: the host takes a glued thing off (report 191428)", flush=True)
        for k in range(2):
            run_glue_off(h, L, h, car, tracked, cars, f"other-glue {k + 1}", k == 1)
    if want("owner-glue"):
        print("\n=== the joiner's car: the joiner takes a glued thing off, the host watches", flush=True)
        beside(h, h.ents()[car], (5, 0, 0))
        run_glue_off(h, L, L, car, tracked, cars, "owner-glue", False)
    if want("other-can"):
        print("\n=== the joiner's car: the host puts a can in, takes it out, puts it back (report 211758)", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "can"); tracked.add(n)
        into_car(h, car, n, None); check(h, L, tracked, cars, "other-can in")
        run_pick_replace(h, L, h, car, n, tracked, cars, "other-can")
    if want("other-can-seat"):
        print("\n=== the same, the host sitting in the joiner's car", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "lamp"); tracked.add(n)
        into_car(h, car, n, None); check(h, L, tracked, cars, "seated lamp in")
        run_pick_replace(h, L, h, car, n, tracked, cars, "other-lamp-seated", seated=1)
    if want("out-of-hand"):
        print("\n=== locked in the joiner's car on both, then the host takes it: gone from the container on both?", flush=True)
        for rnd in range(3):
            beside(L, L.ents()[car]); n = spawn(L, "can"); tracked.add(n)
            into_car(L, car, n, None); check(h, L, tracked, cars, f"out-of-hand {rnd + 1} in")
            def locks(): return (h.lock(n) or {}).get("physlock"), (L.lock(n) or {}).get("physlock")
            for w in range(8):
                if all(locks()): break
                es = L.ents(); e2 = es.get(n); lk = L.lock(n) or {}
                print(f"     not locked yet: {locks()}, in the car's frame {[round(x, 2) for x in car_frame(es[car], e2['pos'])] if e2 else None}, areas {[(x['name'], x['centreInBox'], x['canStore'], x['inScan']) for x in lk.get('areas', [])][:4]}, dontMount {lk.get('dontMount')}", flush=True)
                time.sleep(1)
            if not all(locks()):
                finding(f"out-of-hand {rnd + 1}", f"never locked before the grab: {locks()}: nothing tested this round")
                h.try_act("drop", net=n, item=0); continue
            note(f"before: locks {locks()}")
            beside(h, h.ents()[car]); h(f"mp holdnet {n}")
            for w in range(10):
                time.sleep(0.5); eh, el = h.ents().get(n), L.ents().get(n)
                print(f"     +{(w + 1) * 0.5}: host holds {h.held()}, lock host {(h.lock(n) or {}).get('physlock')!r} joiner {(L.lock(n) or {}).get('physlock')!r}, owner {eh and eh['owner']}, apart {math.dist(eh['pos'], el['pos']) if eh and el else None}", flush=True)
            check(h, L, tracked, cars, f"out-of-hand {rnd + 1} held")
            h.try_act("drop", net=n, item=0); time.sleep(2)
    for x in (h, L):    # the games are kept between runs: said every time
        for sw in ("SlotReplaces", "StaleOwnerForwards", "StaleDetachDirect", "AttachReqBecomesSync", "WornLateOk", "EditsComeBack", "CensusRepairs", "CarriedPartsShared", "RemoveFarMembers", "TankAddEdits", "CarriedLeavesLock"): x(f"set TLDRevamp.Net.Entities.{sw} {'--oldslot' not in sys.argv}")
    for k, x in h.slots(car).items():
        if "--slots" in sys.argv and x["parts"]: print(f"     member {k[0]} slot {k[1]}: {x['parts']}", flush=True)
        if len(x["parts"]) > 1 and not x.get("many"):
            DOUBLED_AT_START.add(f"{car}/{k}"); print(f"the save already has {x['parts']} in member {k[0]} slot {k[1]}", flush=True)
    if want("barrel-out"):
        print("\n=== the host puts things in the joiner's bed and takes them out again: the copy must leave the container", flush=True)
        beside(h, h.ents()[car])
        def sample(n, tag):
            for w in range(6):
                time.sleep(0.5); lj = L.lock(n) or {}; lh = h.lock(n) or {}; ej = L.ents().get(n, {}); eh = h.ents().get(n, {})
                print(f"     {tag} +{(w + 1) * 0.5}: host holds {h.held()}, lock host {lh.get('physlock')!r} joiner {lj.get('physlock')!r}, owner {eh.get('owner')}, joiner row held {ej.get('held')} proxy {ej.get('proxy')}, facts {json.dumps(lj.get('mod'))[:300]}, apart {math.dist(eh['pos'], ej['pos']) if eh and ej else None}", flush=True)
        g = next((m for m in glued(h, car) if "BARREL" in m["name"]), None)
        items = []
        if g:
            known = set(h.ents()); h.act("detach", net=car, item=g["i"]); time.sleep(4)
            nw = [k for k, v in h.ents().items() if k not in known and "BARREL" in v["name"]]
            if nw: items.append(nw[0])
        else: note("no glued barrel in this save")
        n = spawn(h, "can"); items.append(n)
        for n in items:
            tracked.add(n)
            for rnd in range(3):
                for wait_s in (0, 25):
                    beside(h, h.ents()[car])
                    into_car(h, car, n, None); time.sleep(4 + wait_s)
                    note(f"{n} round {rnd + 1}, {wait_s} s in the bed: locks host {(h.lock(n) or {}).get('physlock')!r} joiner {(L.lock(n) or {}).get('physlock')!r}, owner {h.ents()[n]['owner']}")
                    beside(h, h.ents()[car]); h(f"mp holdnet {n}")
                    sample(n, f"{n} r{rnd + 1}")
                    check(h, L, tracked, cars, f"barrel-out {n} round {rnd + 1} wait {wait_s}")
                    h("dropheld"); time.sleep(2)
    if want("barrel-seat"):
        print("\n=== the same with the joiner sitting in the car (random seed 5): the copy must leave the container", flush=True)
        n = spawn(h, "can"); tracked.add(n)
        for seat in (4, 1):
            for rnd in range(2):
                beside(h, h.ents()[car]); into_car(h, car, n, None); time.sleep(4)
                note(f"seat {seat}: {str(L.try_act('seat_in', net=car, slot=seat))[:40]}"); time.sleep(3)
                note(f"before: locks host {(h.lock(n) or {}).get('physlock')!r} joiner {(L.lock(n) or {}).get('physlock')!r}, owner {h.ents()[n]['owner']}")
                beside(h, h.ents()[n], (1.2, 0, 0)) if rnd else beside(h, h.ents()[car]); h(f"mp holdnet {n}"); time.sleep(3)
                lj = L.lock(n) or {}
                note(f"held 3 s: host {h.held()}, joiner lock {lj.get('physlock')!r}, facts {json.dumps(lj.get('mod'))[:400]}")
                check(h, L, tracked, cars, f"barrel-seat {seat} round {rnd + 1}")
                h("dropheld"); L.try_act("seat_out", net=car, slot=seat); time.sleep(2)
    def doorpair():
        d = {m["onSlot"]: m for m in bolted(h, car) if "Door" in m["name"] and m.get("on") == 0}
        k = sorted(d)
        return (d[k[0]], d[k[1]]) if len(k) > 1 else (None, None)
    def slot_row(s_, tag):
        ha, la = h.slots(car).get((0, s_), {}).get("parts"), L.slots(car).get((0, s_), {}).get("parts")
        print(f"     {tag}: slot {s_}: host {ha}, joiner {la}", flush=True)
        return ha, la
    if want("door-top"):
        print("\n=== the door is off on one screen only and that player bolts another one on: two in one slot?", flush=True)
        for who, A, B in (("the car's owner", L, h),) if arg("--who", "owner") == "owner" else (("the other player", h, L),):
            d2, d1 = doorpair()     # d1 goes off on A's screen alone, A bolts d2 into its slot
            if d1 is None: note("not two doors left"); break
            print(f"  -- on the screen of {who}: {d1['name']} missing, {d2['name']} goes into slot {d1['onSlot']}", flush=True)
            beside(A, A.ents()[car]); time.sleep(1)
            print("     local off:", A.s(f"mp localoff {car} {d1['i']}"), flush=True); time.sleep(2)
            slot_row(d1["onSlot"], "one screen empty")
            known = set(A.ents()); A.act("detach", net=car, item=d2["i"]); time.sleep(3)
            new = [k for k, v in A.ents().items() if k not in known and v.get("name") == d2["name"]]
            if not new: finding("door-top", f"{d2['name']}: no object of its own"); continue
            tracked.add(new[0])
            A.try_act("attach", net=new[0], other=car, item=d1["on"] + 1, slot=d1["onSlot"], a=2); time.sleep(5)
            ha, la = slot_row(d1["onSlot"], "after the bolt")
            for w, l in (("host", ha), ("joiner", la)):
                if l and len(l) > 1: finding(f"door-top {who}", f"two doors in one slot on the {w}: {l}")
            if ha != la: finding(f"door-top {who}", f"host {ha}, joiner {la}")
            print("     replaced:", h("get TLDRevamp.Net.Entities.SlotPartsReplaced").get("value"), L("get TLDRevamp.Net.Entities.SlotPartsReplaced").get("value"), flush=True)
            am, bm = {x["i"]: x for x in h.members(car)}, {x["i"]: x for x in L.members(car)}
            print(f"     {d1['name']}: attached host {am[d1['i']].get('attached')} joiner {bm[d1['i']].get('attached')}, lp host {am[d1['i']].get('lp')} joiner {bm[d1['i']].get('lp')}", flush=True)
            check(h, L, tracked, cars, f"door-top {who}")
            print(f"     end: {slot_row(d1['onSlot'], 'end')}", flush=True)
            time.sleep(1)
    if want("door-stale"):
        print("\n=== the car changes hands, its old owner doesn't know yet and loses a door: on one screen only? (a slow connection)", flush=True)
        keep, d1 = doorpair()
        ms = int(arg("--lag", "400"))
        L(f"mp lag {ms}"); time.sleep(2)
        for label, m in () if "--bolt-only" in sys.argv else (("member of the car", d1), ("door bolted back on (its own object)", keep)):
            print(f"  -- {label}: {m['name']}", flush=True)
            beside(L, L.ents()[car]); take_car(L, car)
            if "own object" in label:
                known = set(L.ents()); L.act("detach", net=car, item=m["i"]); time.sleep(3)
                new = [k for k, v in L.ents().items() if k not in known and v.get("name") == m["name"]]
                if new:
                    tracked.add(new[0]); L.try_act("attach", net=new[0], other=car, item=1, slot=m["onSlot"], a=2); time.sleep(4)
            slot_row(m["onSlot"], "before")
            if "--far" in sys.argv:     # the audit: a detach the reach check of a player's request would turn down (thrown clear of a crash)
                h("set TLDRevamp.Net.Entities.EditRangeM 0.1")
            print("     host claims:", h.s(f"mp claim {car}"), flush=True)
            time.sleep(0.05)
            print("     joiner loses it:", str(L.s(f"mp falloff {car} {m['i']}"))[:120], flush=True)
            for w in (0.3, 1, 3, 8, 12):
                time.sleep(w); slot_row(m["onSlot"], f"+{w}")
            if "--far" in sys.argv: h("set TLDRevamp.Net.Entities.EditRangeM 15")
            print("     owner log:", h(f"mp ownerlog {car}")["log"][-3:], flush=True)
            ha, la = slot_row(m["onSlot"], "after 24 s")
            if ha != la: finding(f"door-stale {label}", f"{m['name']}: {ha or 'empty'} on the host, {la or 'empty'} on the joiner, for good")
            check(h, L, tracked, cars, f"door-stale {label}")
        print("  -- the same for bolting: the door is loose, the host's claim on it is on its way, the joiner bolts it", flush=True)
        m = keep
        beside(L, L.ents()[car]); take_car(L, car)
        if "--bolt-only" in sys.argv:
            known = set(L.ents()); L.act("detach", net=car, item=m["i"]); time.sleep(3)
            n = next((k for k, v in L.ents().items() if k not in known and v.get("name") == m["name"]), None)
            if n: tracked.add(n)
        else: n = next((k for k, v in L.ents().items() if v.get("name") == m["name"]), None)
        pos = L.pose_on(n) if n else None
        if n is None: note("no door of its own to bolt")
        else:
            if pos and pos.get("attached"):
                L.try_act("detach", net=n, item=0); time.sleep(3)
            if h.ents()[n]["owner"] != L.me():
                L.s(f"mp claim {n}"); time.sleep(3)
            beside(L, L.ents()[car]); beside(h, h.ents()[car], (-2.6, 0, 0))
            slot_row(m["onSlot"], "before")
            print("     host claims the door:", h.s(f"mp claim {n}"), flush=True)
            time.sleep(0.05)
            L.try_act("attach", net=n, other=car, item=m["on"] + 1, slot=m["onSlot"], a=2)
            for w in (0.3, 1, 3, 8, 12):
                time.sleep(w); slot_row(m["onSlot"], f"+{w}")
            print("     owner log:", h(f"mp ownerlog {n}")["log"][-3:], flush=True)
            ha, la = slot_row(m["onSlot"], "after 24 s")
            if ha != la: finding("door-stale bolt", f"{m['name']}: {ha or 'empty'} on the host, {la or 'empty'} on the joiner, for good")
            check(h, L, tracked, cars, "door-stale bolt")
        L("mp lag 0")
    T_ = "TLDRevamp.Net.Entities"
    def litres(p, n):
        try: return {k: round(float(v), 2) for k, v in re.findall(r"([a-zA-Z_]+):(\d+\.\d+)", json.dumps(p.s(f"mp fluids {n}")))}
        except RuntimeError as ex: return str(ex)[:60]
    def barrel_of():
        m = next((m for m in L.members(car) if m.get("attached") and "BARREL" in m.get("name", "")), None)
        if m is None: note("no barrel on the car"); return None
        beside(L, L.ents()[car]); known = set(L.ents()); L.act("detach", net=car, item=m["i"]); time.sleep(4)
        n = next((k for k, v in L.ents().items() if k not in known and "BARREL" in v.get("name", "")), None)
        if n: tracked.add(n); L.s(f"mp fill {n} 0 gas 10"); time.sleep(3)
        return n
    if want("aud-near-pour") or want("aud-far-edit"):
        print("\n=== the host pours into a barrel the joiner owns (barrel fluids): beside it, and with its owner 3 km away", flush=True)
        n = barrel_of()
        if n: print("     barrel clear of the car:", str(L.s(f"mp moveitem {n} 6 0"))[:90], flush=True); time.sleep(4)
        for far_ in (False, True) if n else ():
            if (far_ and not want("aud-far-edit")) or (not far_ and not want("aud-near-pour")): continue
            if far_:
                g = L("gpos"); home = (g["gx"], g["gz"]); L(f"tpground {g['gx'] + 3000} {g['gz']}"); note("joiner 3 km away"); time.sleep(12)
            beside(h, h.ents()[n], (2, 0, 0)); can = spawn(h, "jerry"); tracked.add(can); time.sleep(2)
            if h.ents()[can]["proxy"]: h.s(f"mp claim {can}"); time.sleep(2)
            h.s(f"mp fill {can} 0 gas 8"); time.sleep(2)
            a0 = litres(h, n); c0 = litres(h, can)
            print(f"     before: barrel host {a0}, joiner {litres(L, n) if not far_ else 'far'}, can {c0}, owner {h.ents()[n]['owner']}", flush=True)
            print("     fill as a player (can in hand, left mouse):", str(h.s(f"mp holdfill {can} {n} 0"))[:120], flush=True); time.sleep(10)
            st = h.s("mp holdfill status"); print("     status:", str(st)[:240], flush=True)
            print("     apart:", round(math.dist(h.ents()[n]["pos"], L.ents()[n]["pos"]), 2) if not far_ else "far", "| BARRELs:", h.s("mp namecount BARREL 0"), flush=True)
            h.s("mp holdfill stop"); time.sleep(3)
            if h.held(): h("dropheld")
            time.sleep(1)
            if far_:
                L(f"tpground {home[0]} {home[1]}"); note("joiner back"); time.sleep(16)
            ha, la, c1 = litres(h, n), litres(L, n), litres(h, can)
            print(f"     after: barrel host {ha}, joiner {la}, can {c1}, owner {h.ents()[n]['owner']}", flush=True)
            if ha != la and (not isinstance(ha, dict) or not isinstance(la, dict) or sum(ha.values()) - sum(la.values()) > 0.2 or sum(la.values()) - sum(ha.values()) > 0.2):
                finding("aud-far-edit" if far_ else "aud-near-pour", f"barrel {ha} on the host, {la} on the joiner")
            if isinstance(ha, dict) and isinstance(a0, dict) and isinstance(c0, dict) and isinstance(c1, dict):
                gain, loss = sum(ha.values()) - sum(a0.values()), sum(c0.values()) - sum(c1.values())
                print(f"     barrel gained {gain:.2f}, can lost {loss:.2f}", flush=True)
                if gain > loss + 0.5: finding("aud-pour-count", f"barrel gained {gain:.2f} L, the can lost {loss:.2f} L")
    if want("aud-fight"):
        print("\n=== both players change usables of one item within a second: the same on both after?", flush=True)
        beside(h, h.ents()[car]); beside(L, L.ents()[car], (-2.6, 0, 0)); time.sleep(1)
        rows = h.s(f"mp usables {car}")["usables"]
        by = {}
        for u in rows: by.setdefault(u["item"], []).append(u)
        pick = [(i, us) for i, us in by.items() if len(us) >= 2][:3] + [(i, us) for i, us in by.items() if len(us) == 1][:2]
        def vals(p): return {(u["item"], u["usable"]): u["value"] for u in p.s(f"mp usables {car}")["usables"]}
        for i, us in pick:
            a, b = us[0], us[-1]
            for rnd, (x1, x2) in enumerate(((0.3, 0.7), (0.8, 0.2))):
                h.try_act("usable", net=car, item=i, slot=a["usable"], x=x1); time.sleep(0.4)
                L.try_act("usable", net=car, item=i, slot=b["usable"], x=x2); time.sleep(10)
                va, vb = vals(h), vals(L)
                bad = {k: (va[k], vb[k]) for k in va if k in vb and k[0] == i and any(abs(float(x) - float(y)) > 0.01 for x, y in zip(va[k], vb[k]))}
                print(f"     item {i} {a['itemName']} usables {a['usable']}/{b['usable']} round {rnd + 1}: {'same' if not bad else bad}"[:260], flush=True)
                if bad: finding(f"aud-fight {a['itemName']} {rnd + 1}", f"different after 10 s: {str(bad)[:160]}")
    if want("aud-wheel-far"):
        print("\n=== a tyre unbolted from a wheel, beside its car and 3 km from it: the same on both?", flush=True)
        def tyres(p): return p.s("mp namecount TireGumi 0")
        if "--oldslot" in sys.argv: h(f"set {T_}.CarriedPartsShared false"); L(f"set {T_}.CarriedPartsShared false")
        for dist, both in ((0, True), (100, True), (3000, True), (3000, False)):
            if ("--far-only" in sys.argv and dist < 500) or ("--far-stay" in sys.argv and both) or ("--near-100" in sys.argv and dist != 100): continue
            print(f"     --- {dist} m, the host {'beside the wheel' if both else 'stays at the car'}", flush=True)
            beside(L, L.ents()[car]); h(f"tpground {L('gpos')['gx'] + 6} {L('gpos')['gz']}"); time.sleep(3)
            if L.ents()[car]["proxy"]: print("     joiner claims the car:", str(L.s(f"mp claim {car}"))[:60], flush=True); time.sleep(3)
            w = next((m for m in bolted(L, car) if "Felni" in m["name"] and m.get("on") == 0), None)
            if w is None: note("INVALID: no wheel left on the car (they came off in the teleport's fall)"); break
            known = set(L.ents()); L.act("detach", net=car, item=w["i"]); time.sleep(3)
            wn = next((k for k, v in L.ents().items() if k not in known and "Felni" in v.get("name", "")), None); tracked.add(wn)
            if L.ents()[wn]["proxy"]: L.s(f"mp claim {wn}"); time.sleep(2)
            print(f"     wheel {wn}, {dist} m from the car:", str(L.s(f"mp moveitem {wn} {dist + 5} 0"))[:60], flush=True); time.sleep(2)
            g = L("gpos"); L(f"tpground {g['gx'] + dist + 3} {g['gz']}")
            if both: h(f"tpground {g['gx'] + dist + 5} {g['gz'] + 3}")
            time.sleep(14 if dist else 4)
            e = L.ents().get(wn); print(f"     wheel owner {e and e['owner']}, car dormant here {L.ents()[car].get('dormant')}", flush=True)
            if dist > 500:
                # the ground there was not generated when it moved: it fell. Put it back beside the player, on the ground.
                if L.ents()[wn]["proxy"]: L.s(f"mp claim {wn}"); time.sleep(2)
                g2 = L("gpos"); wp = L.ents()[wn]["pos"]
                print("     wheel fetched:", str(L.s(f"mp moveitem {wn} {g2['gx'] + 2 - wp[0]:.1f} {g2['gz'] - wp[2]:.1f} ground"))[:60], flush=True); time.sleep(4)
            ctrs = "PartsReported CarriedOffSent CarriedOffApplied AutoDetachesIgnored SuppressedFallOff DetachReqsApplied PartOffRejected".split()
            def cnt(p): return [p(f"get {T_}.{c}").get("value") for c in ctrs]
            print(f"     owner of the car {L.ents()[car]['owner']}, before {ctrs}: joiner {cnt(L)}, host {cnt(h)}", flush=True)
            near0 = lambda rows: sorted((r["on"], r["root"][:14]) for r in rows["items"] if r["d"] < 25)
            print(f"     before: host {near0(tyres(h))}, joiner {near0(tyres(L))}", flush=True)
            if L.ents()[wn]["proxy"]: print("     joiner claims the wheel:", L.s(f"mp claim {wn}"), flush=True); time.sleep(3)
            try: print("     joiner unbolts the tyre:", L.s("mp gameoff TireGumi 0"), flush=True)
            except RuntimeError as ex: note(f"could not unbolt: {ex}")
            time.sleep(6)
            print(f"     after: joiner {cnt(L)}, host {cnt(h)}, rejects: host {h(f'get {T_}.PartRejects').get('value')}, joiner {L(f'get {T_}.PartRejects').get('value')}", flush=True)
            if not both: h(f"tpground {g['gx'] + dist + 5} {g['gz'] + 3}"); time.sleep(16)
            th, tl = tyres(h), tyres(L)
            near = lambda rows: sorted((r["on"], r["root"][:14]) for r in rows["items"] if r["d"] < 25)
            print(f"     host {near(th)}\n     joiner {near(tl)}\n     refused {h('get ' + T_ + '.PartRejects').get('value')}", flush=True)
            if near(th) != near(tl): finding(f"aud-wheel-far {dist}", f"the tyre: host {near(th)}, joiner {near(tl)}")
            L(f"tpground {g['gx']} {g['gz']}"); h(f"tpground {g['gx'] + 3} {g['gz'] + 3}"); time.sleep(16 if dist else 4)
            check(h, L, tracked, cars, f"aud-wheel-far {dist}")
    if want("soak"):
        print("\n=== normal driving, nothing wrong: the census must leave it alone", flush=True)
        beside(L, L.ents()[car]); time.sleep(1); print("     drivein:", str(L("drivein"))[:80], flush=True); time.sleep(2)
        h.try_act("seat_in", net=car, slot=1); time.sleep(1)
        for c_ in ("fuelup", "handbrake off"):
            try: print(f"     {c_}:", str(L(c_))[:90], flush=True)
            except RuntimeError as ex: note(f"{c_}: {str(ex)[:60]}")
        for secs, speed in ((60, 12), (60, 25), (40, 6)):
            print(f"     joiner drives at {speed} m/s for {secs} s: {str(L(f'autodrive on {speed}'))[:70]}", flush=True)
            time.sleep(secs)
            a_, b_ = h.s("mp anomalies"), L.s("mp anomalies")
            print(f"     host asks {a_.get('censusAsks')}, differ {a_.get('censusDiffers')}, repairs {a_.get('repairsAsked')}; joiner asks {b_.get('censusAsks')}, differ {b_.get('censusDiffers')}, repairs {b_.get('repairsAsked')}, car at {[round(x) for x in h.ents()[car]['pos']]}", flush=True)
        try: L("autodrive off")
        except RuntimeError: pass
        time.sleep(6); h.try_act("seat_out", net=car, slot=1); L("driveout"); time.sleep(20)
        check(h, L, tracked, cars, "soak")
    if want("aud-edge"):
        print("\n=== a door bolted on with the other player at the edge of range: part loaded, car not?", flush=True)
        beside(L, L.ents()[car]); time.sleep(1)
        if L.ents()[car]["proxy"]: L.s(f"mp claim {car}"); time.sleep(3)
        door = next((m for m in bolted(L, car) if "Door" in m["name"] and m.get("on") == 0), None)
        known = set(L.ents()); L.act("detach", net=car, item=door["i"]); time.sleep(3)
        n = next((k for k in L.ents() if k not in known), None); tracked.add(n)
        cp = L.ents()[car]["pos"]; band = None
        for d in range(200, 800, 20):
            h(f"tpground {cp[0] + d} {cp[2]}"); time.sleep(2.5)
            eh = h.ents(); c_, p_ = eh.get(car, {}), eh.get(n, {})
            cl, pl_ = not c_.get("dormant"), not p_.get("dormant")
            print(f"     {d} m: car loaded {cl}, door loaded {pl_}", flush=True)
            if pl_ and not cl: band = d; break
            if not cl and not pl_: break
        print(f"     the band: {band}", flush=True)
        print("     joiner bolts the door back:", L.try_act("attach", net=n, other=car, item=1, slot=door["onSlot"], a=2), flush=True); time.sleep(4)
        print("     host's attach fails:", str(h(f"get {T_}.AttachFails").get("value"))[-80:], flush=True)
        h(f"tpground {cp[0] + 6} {cp[2]}"); time.sleep(25)
        rh, rl = h.s(f"mp repairrows {n} 0")["rows"], L.s(f"mp repairrows {n} 0")["rows"]
        print(f"     back: self host {[r for r in rh if r.startswith('self')]}, joiner {[r for r in rl if r.startswith('self')]}", flush=True)
        check(h, L, tracked, cars, "aud-edge")
    if want("aud-far-quick"):
        print("\n=== the owner has just left, the object not yet handed over: the other player pours into it", flush=True)
        n = barrel_of()
        if n:
            print("     clear of the car:", str(L.s(f"mp moveitem {n} 6 0"))[:60], flush=True); time.sleep(4)
            beside(h, h.ents()[n], (2, 0, 0)); can = spawn(h, "jerry"); tracked.add(can); time.sleep(2)
            if h.ents()[can]["proxy"]: h.s(f"mp claim {can}"); time.sleep(2)
            h.s(f"mp fill {can} 0 gas 8"); time.sleep(2)
            print("     fill:", str(h.s(f"mp holdfill {can} {n} 0"))[:80], flush=True); time.sleep(1)
            a0 = litres(h, n); c0 = litres(h, can)
            g = L("gpos"); L(f"tpground {g['gx'] + 3000} {g['gz']}"); note("joiner 3 km away, the pour going on"); time.sleep(8)
            own = h.ents()[n]["owner"]; h.s("mp holdfill stop"); time.sleep(2)
            if h.held(): h("dropheld")
            L(f"tpground {g['gx']} {g['gz']}"); note("joiner back"); time.sleep(25)
            ha, la, c1 = litres(h, n), litres(L, n), litres(h, can)
            print(f"     owner during the pour {own}; barrel {a0} -> host {ha}, joiner {la}; can {c0} -> {c1}", flush=True)
            if ha != la: finding("aud-far-quick", f"barrel {ha} on the host, {la} on the joiner")
    if want("aud-two-pour"):
        print("\n=== both players pour into one barrel at once, each from their own can: does it add up?", flush=True)
        n = barrel_of()
        if n:
            print("     clear of the car:", str(L.s(f"mp moveitem {n} 6 0"))[:60], flush=True); time.sleep(4)
            beside(h, h.ents()[n], (2, 0, 0)); beside(L, L.ents()[n], (-2, 0, 0))
            def can_of(p):
                c = spawn(p, "jerry"); tracked.add(c); time.sleep(2)
                if p.ents()[c]["proxy"]: p.s(f"mp claim {c}"); time.sleep(2)
                p.s(f"mp fill {c} 0 gas 15"); time.sleep(2)
                return c
            ch, cl = can_of(h), can_of(L)
            # a step back, each from a side where the view lands on the cap: over the barrel one's can is in the way of the
            # other's view (u1), and from some sides the off-centre cap is behind the rim. The view off the cap drinks from the can.
            def spot(p, c, turns):
                for t in turns:
                    p.s(f"mp holdfill {c} {n} 0 {t} 1.1"); time.sleep(0.6)
                    d = p.s("mp holdfill status").get("lookDist", 9); p.s("mp holdfill stop")
                    if d <= 0.25: return t
                note(f"INVALID aud-two-pour: {p.label} found no side with the cap in view"); return turns[0]
            th = spot(h, ch, [0, 45, 315, 90, 270]); tl = spot(L, cl, [180, 135, 225, 90, 270, 45, 315, 0])
            time.sleep(1.5)
            a0, h0, l0 = litres(h, n), litres(h, ch), litres(L, cl)
            print(f"     before: barrel {a0}, cans {h0} {l0}, owner {h.ents()[n]['owner']}, sides {th} {tl}", flush=True)
            print("     host pours:", str(h.s(f"mp holdfill {ch} {n} 0 {th} 1.1"))[:70], flush=True)
            print("     joiner pours:", str(L.s(f"mp holdfill {cl} {n} 0 {tl} 1.1"))[:70], flush=True)
            time.sleep(6)
            print(f"     mid pour: host {litres(h, ch)}, joiner {litres(L, cl)}", flush=True)
            time.sleep(6)
            print(f"     pouring: host {str(h.s('mp holdfill status'))[:110]}; joiner {str(L.s('mp holdfill status'))[:110]}", flush=True)
            h.s("mp holdfill stop"); L.s("mp holdfill stop"); time.sleep(2)
            if h.held(): h("dropheld")
            if L.held(): L("dropheld")
            time.sleep(25)
            ha, la, h1, l1 = litres(h, n), litres(L, n), litres(h, ch), litres(L, cl)
            gain, loss = sum(ha.values()) - sum(a0.values()), sum(h0.values()) - sum(h1.values()) + sum(l0.values()) - sum(l1.values())
            print(f"     after: barrel host {ha}, joiner {la}; cans {h1} {l1}; barrel gained {gain:.2f}, the cans lost {loss:.2f}, repairs {h.s('mp anomalies').get('repairsAsked')}/{L.s('mp anomalies').get('repairsAsked')}", flush=True)
            if sum(h0.values()) - sum(h1.values()) < 2 or sum(l0.values()) - sum(l1.values()) < 2: note("INVALID aud-two-pour: one player hardly poured")
            if ha != la and abs(sum(ha.values()) - sum(la.values())) > 0.5: finding("aud-two-pour", f"barrel {ha} on the host, {la} on the joiner")
            if abs(gain - loss) > 1: finding("aud-two-pour / count", f"the barrel gained {gain:.2f} L, the cans lost {loss:.2f} L")
    if want("aud-pocket-grab"):
        print("\n=== the other player grabs a can that is in someone's pocket", flush=True)
        def row(p, n):
            e = p.ents().get(n)
            return {k: e.get(k) for k in ("owner", "proxy", "held", "stored", "pos", "vel")} if e else None
        beside(h, h.ents()[car], (3, 0, 0)); beside(L, L.ents()[car], (-3, 0, 0))
        c = spawn(h, "can"); tracked.add(c); time.sleep(2)
        if h.ents()[c]["proxy"]: h.s(f"mp claim {c}"); time.sleep(2)
        print("     host holds it:", str(h.s(f"mp holdnet {c}"))[:60], flush=True); time.sleep(1)
        h.try_act("inv_store", slot=0); time.sleep(3)
        print(f"     in the pocket: host {row(h, c)}", flush=True); print(f"                    joiner {row(L, c)}", flush=True)
        print(f"     joiner's copy: {str(L.s(f'mp itemcheck {c}'))[:260]}", flush=True)
        print("     joiner grabs it:", str(L.s(f"mp holdnet {c}"))[:60], flush=True); time.sleep(4)
        print(f"     host {row(h, c)}", flush=True); print(f"     joiner {row(L, c)}, holds {L.held()}", flush=True)
        L("dropheld"); time.sleep(3)
        for w in range(4):
            print(f"     +{w * 8}: host {row(h, c)}", flush=True); print(f"          joiner {row(L, c)}", flush=True); time.sleep(8)
        check(h, L, tracked, cars, "aud-pocket-grab")
    if want("aud-part-hand"):
        print("\n=== a part put in the other player's car from a seat, taken back in hand, both get out", flush=True)
        take_car(L, car); time.sleep(2)
        beside(h, h.ents()[car]); print("     host sits:", h.try_act("seat_in", net=car, slot=1), flush=True); time.sleep(1)
        known = set(h.ents())
        m = next((m for m in glued(L, car) + bolted(L, car) if "Light" in m["name"] or "Lamp" in m["name"]), None) or bolted(L, car)[0]
        print("     part:", m["i"], m["name"], flush=True)
        h.act("detach", net=car, item=m["i"]); time.sleep(3)
        n = next((k for k in h.ents() if k not in known), None); tracked.add(n)
        def locks(): return (h.lock(n) or {}).get("physlock"), (L.lock(n) or {}).get("physlock")
        def show(tag):
            a, b = h.ents().get(n, {}), L.ents().get(n, {})
            print(f"     {tag}: locks {locks()}, held {a.get('held')} {b.get('held')}, stored {a.get('stored')} {b.get('stored')}, owner {a.get('owner')}, host holds {h.held()}, seated {h.seated()} {L.seated()}, apart {math.dist(a['pos'], b['pos']) if a and b else None}", flush=True)
        print("     host holds it:", str(h.s(f"mp holdnet {n}"))[:60], flush=True); time.sleep(1)
        into_car(h, car, n, None); time.sleep(4); show("in the car")
        print("     host takes it:", str(h.s(f"mp holdnet {n}"))[:60], flush=True)
        for w in range(4): time.sleep(1.5); show(f"+{(w + 1) * 1.5}")
        print("     host gets out:", h.try_act("seat_out", net=car, slot=1), flush=True)
        for w in range(4): time.sleep(2); show(f"out +{(w + 1) * 2}")
        check(h, L, tracked, cars, "aud-part-hand held")
        if h.held(): h("dropheld")
        for w in range(3): time.sleep(3); show(f"let go +{(w + 1) * 3}")
        check(h, L, tracked, cars, "aud-part-hand let go")
        print("     host gets out, hands empty:", h.try_act("seat_out", net=car, slot=1), flush=True); time.sleep(2); show("out")
        if h.seated(): h("driveout"); time.sleep(2); show("driveout")
    if want("aud-tank-fight"):
        print("\n=== the owner changes a tank's level while the other player pours into it", flush=True)
        n = barrel_of()
        if n:
            print("     clear of the car:", str(L.s(f"mp moveitem {n} 6 0"))[:60], flush=True); time.sleep(4)
            beside(h, h.ents()[n], (2, 0, 0)); beside(L, L.ents()[n], (-2, 0, 0)); can = spawn(h, "jerry"); tracked.add(can); time.sleep(2)
            if h.ents()[can]["proxy"]: h.s(f"mp claim {can}"); time.sleep(2)
            h.s(f"mp fill {can} 0 gas 8"); time.sleep(2)
            print("     fill:", str(h.s(f"mp holdfill {can} {n} 0"))[:80], flush=True)
            for k in range(3):
                time.sleep(1.5); print("     owner sets it:", str(L.s(f"mp fill {n} 0 gas {30 + 10 * k}"))[:60], flush=True)
            h.s("mp holdfill stop"); time.sleep(2)
            if h.held(): h("dropheld")
            time.sleep(25)
            ha, la = litres(h, n), litres(L, n)
            print(f"     barrel host {ha}, joiner {la}, repairs {h.s('mp anomalies').get('repairsAsked')}, resyncs held {h(f'get {T_}.ResyncHeld').get('value')}", flush=True)
            if ha != la and abs(sum(ha.values()) - sum(la.values())) > 0.5: finding("aud-tank-fight", f"barrel {ha} on the host, {la} on the joiner")
    if want("aud-attach-held"):
        print("\n=== a player bolts a part its owner is holding: refused, undone on both?", flush=True)
        beside(L, L.ents()[car]); beside(h, h.ents()[car], (-2.6, 0, 0)); time.sleep(1)
        if L.ents()[car]["proxy"]: L.s(f"mp claim {car}"); time.sleep(3)
        door = next((m for m in bolted(L, car) if "Door" in m["name"] and m.get("on") == 0), None)
        known = set(L.ents()); L.act("detach", net=car, item=door["i"]); time.sleep(3)
        n = next((k for k in L.ents() if k not in known), None); tracked.add(n)
        if L.ents()[n]["proxy"]: L.s(f"mp claim {n}"); time.sleep(2)
        print("     owner holds it:", str(L.s(f"mp holdnet {n}"))[:60], flush=True); time.sleep(2)
        print("     host bolts it:", h.try_act("attach", net=n, other=car, item=1, slot=door["onSlot"], a=2), flush=True); time.sleep(5)
        print(f"     attach requests refused {L(f'get {T_}.AttachReqsRefused').get('value')}; self host {[r for r in h.s(f'mp repairrows {n} 0')['rows'] if r.startswith('self')]}, joiner {[r for r in L.s(f'mp repairrows {n} 0')['rows'] if r.startswith('self')]}", flush=True)
        L("dropheld"); time.sleep(20)
        print(f"     after: self host {[r for r in h.s(f'mp repairrows {n} 0')['rows'] if r.startswith('self')]}, joiner {[r for r in L.s(f'mp repairrows {n} 0')['rows'] if r.startswith('self')]}", flush=True)
        check(h, L, tracked, cars, "aud-attach-held")
    if want("repair"):
        print("\n=== one machine made to differ, no message at all: does the census bring it back?", flush=True)
        beside(L, L.ents()[car]); beside(h, h.ents()[car], (-2.6, 0, 0)); time.sleep(2)
        if L.ents()[car]["proxy"]: L.s(f"mp claim {car}"); time.sleep(3)
        def rows(p, n):
            try: return dict(x.split("=", 1) for x in p.s(f"mp repairrows {n} 0")["rows"])
            except RuntimeError as ex: return {"error": str(ex)[:60]}
        def differ(n):
            a, b = rows(h, n), rows(L, n)
            return {k: (a.get(k), b.get(k)) for k in set(a) | set(b) if a.get(k) != b.get(k)}
        def heal(n, tag, asker=h, wait=45, ask=True):
            before = differ(n); print(f"     {tag}: made to differ: {str(before)[:200]}", flush=True)
            if not before: note(f"INVALID {tag}: no difference made"); return
            if ask: asker.s(f"mp censusnow {n} 0")
            t0 = time.time()
            while time.time() - t0 < wait and differ(n): time.sleep(1)
            left = differ(n)
            print(f"     {tag}: {'healed in %.0f s' % (time.time() - t0) if not left else 'STILL DIFFERENT after %d s: %s' % (wait, str(left)[:200])}", flush=True)
            if left: finding(f"repair {tag}", str(left)[:200])
        plain = [m for m in bolted(L, car) if m.get("on") == 0 and not any(x in m["name"] for x in ("Felni", "Gumi"))]
        print("     bolted members:", [(m["i"], m["name"]) for m in plain][:8], "| rows on the car:", len(rows(h, car)), "| differences at the start:", str(differ(car))[:200], flush=True)
        a, b, d = plain[0], plain[1], plain[2]
        print("     the copy loses a part:", h.s(f"mp localoff {car} {a['i']} 0"), flush=True); heal(car, "part off on the copy")
        print("     the owner loses a part:", L.s(f"mp localoff {car} {b['i']} 0"), flush=True); heal(car, "part off on the owner")
        st = next((m for m in glued(L, car) + bolted(L, car) if m["i"] != a["i"] and m["i"] != b["i"]), None)
        print("     a state changed on the copy:", h.s(f"mp localstate {car} 0 0"), flush=True); heal(car, "state on the copy")
        known = set(L.ents()); L.act("detach", net=car, item=d["i"]); time.sleep(3)
        n = next((k for k in L.ents() if k not in known), None); tracked.add(n)
        print("     own object", n, "bolted back:", L.try_act("attach", net=n, other=car, item=1, slot=d["onSlot"], a=2), flush=True); time.sleep(4)
        print("     rows:", rows(h, n).get("self"), rows(L, n).get("self"), flush=True)
        print("     the copy's own object falls off:", h.s(f"mp localoff {n} 0 0"), flush=True); heal(n, "own object off on the copy")
        print("     the owner's own object falls off:", L.s(f"mp localoff {n} 0 0"), flush=True); heal(n, "own object off on the owner")
        print("     no asking, its turn only: the copy loses a part:", h.s(f"mp localoff {car} {plain[3]['i']} 0"), flush=True); heal(car, "part off, by its turn", wait=120, ask=False)
        print(f"     repairs: host {str(h.s(f'mp repairrows {car} 0'))[-70:]}, joiner {str(L.s(f'mp repairrows {car} 0'))[-70:]}", flush=True)
        print("     anomalies host:", str(h.s("mp anomalies").get("lines"))[-300:], flush=True)
        check(h, L, tracked, cars, "repair")
    if want("aud-place"):
        print("\n=== a copy that missed an item's last states: does the census put it back where the owner has it?", flush=True)
        beside(h, h.ents()[car]); beside(L, L.ents()[car], (-2.6, 0, 0)); time.sleep(2)
        n = spawn(L, "jerry"); tracked.add(n); time.sleep(3)
        if n is None: note("INVALID aud-place: no loose item")
        else:
            if L.ents()[n]["proxy"]: L.s(f"mp claim {n}"); time.sleep(2)
            print("     item", n, L.ents()[n]["name"], "owner", L.ents()[n]["owner"], "| before:", h.s("mp places"), flush=True)
            for rnd, on in enumerate((False, True)):
                h.s(f"set {T}.CensusPlaces {on}"); L.s(f"set {T}.CensusPlaces {on}")
                h.s(f"mp dropstates {n}")
                print(f"     census positions {on}: the owner moves it, the copy hears none of it:", str(L.s(f"mp moveitem {n} {8 + 6 * rnd} 0"))[:70], flush=True); time.sleep(6)
                h.s("mp dropstates 0")
                gap = lambda: math.dist(h.ents()[n]["pos"], L.ents()[n]["pos"])
                d0 = gap(); print(f"     apart {d0:.2f} m", flush=True)
                if d0 < 2: note(f"INVALID aud-place {on}: no gap made ({d0:.2f} m)"); continue
                h.s(f"mp censusnow {n} 0")
                t0 = time.time()
                while time.time() - t0 < 40 and gap() > 0.6: time.sleep(1)
                d1 = gap()
                print(f"     census positions {on}: {'healed in %.0f s' % (time.time() - t0) if d1 <= 0.6 else 'STILL %.2f m apart after 40 s' % d1}", flush=True)
                if on and d1 > 0.6: finding("aud-place", f"still {d1:.2f} m apart")
                if not on and d1 <= 0.6: note("INVALID aud-place: healed with the check off, a gap this way doesn't last")
            print("     places: host", h.s("mp places"), "joiner", L.s("mp places"), flush=True)
            print("     anomalies host:", str(h.s("mp anomalies").get("lines"))[-400:], flush=True)
            check(h, L, tracked, cars, "aud-place")
    if want("aud-hold"):
        print("\n=== taking an item out of a car the other player owns: whose is it, whose hand, where is the copy?", flush=True)
        def row(n):
            out = []
            for p in (h, L):
                try: m = p.s(f"mp lockdiag {n}")
                except RuntimeError as ex: out.append(f"{p.label} no {str(ex)[-30:]}"); continue
                out.append(f"{p.label}: owner {m.get('owner')} proxy {m.get('proxy')} held {m.get('held')} stored {m.get('stored')} kin {m.get('kinematic')} body {m.get('movedByBody')} lock {(m.get('lock') or {}).get('container')} hands {p.held()}")
            try: gap = math.dist(h.ents()[n]["pos"], L.ents()[n]["pos"])
            except KeyError: gap = -1
            return f"{gap:5.2f} m | " + " | ".join(out)
        def watch(n, secs, label):
            for k in range(secs):
                print(f"     {label} {k:2d}s {row(n)}", flush=True); time.sleep(1)
        beside(L, L.ents()[car], (-2.6, 0, 0)); beside(h, h.ents()[car]); time.sleep(2)
        tanks = [m for m in glued(h, car) if m["name"].startswith("Bus02Tank")]
        if tanks:
            before = set(h.ents()); h.try_act("detach", net=car, item=tanks[0]["i"]); time.sleep(4)
            new = [x for x, e in h.ents().items() if x not in before and "Tank" in str(e.get("name"))]
            if new:
                n = new[0]; tracked.add(n); time.sleep(3)
                def right():
                    try: return h("get mainscript.s.player.inHandP.name").get("value")
                    except RuntimeError: return None
                print("  -- the host's inventory is in use (two items stored in one slot), then the host takes the joiner's tank", flush=True)
                beside(h, h.ents()[car]); time.sleep(1)
                for k in range(2):
                    x = spawn(h, "lamp"); tracked.add(x); time.sleep(2); print("     host holds", str(h(f"mp holdnet {x}"))[:50], flush=True); time.sleep(0.5)
                    h.try_act("inv_store", slot=0); time.sleep(1); print(f"     stored: hands {h.held()}, right hand {right()}", flush=True)
                if h.held(): h("dropheld"); time.sleep(1)
                print(f"     now: hands {h.held()}, right hand {right()}", flush=True)
                into_car(L, car, n, None); time.sleep(3); L.try_act("seat_in", net=car, slot=4); time.sleep(5); L("driveout"); time.sleep(3)
                print("     before", row(n), flush=True)
                beside(h, h.ents()[n], (1.2, 0, 0)); time.sleep(1)
                print("     host takes it:", str(h.s(f"mp holdnet {n}"))[:60], f"right hand {right()}", flush=True)
                watch(n, 8, "held")
                print(f"     hands {h.held()}, right hand {right()}", flush=True)
                h(f"mp lookat {car}"); time.sleep(0.5); h("set MountAnything.Mounting.TestPress True"); time.sleep(1)
                print("     glue press:", h("get MountAnything.Mounting.LastMount").get("value"), f"hands {h.held()}, right hand {right()}", flush=True)
                watch(n, 6, "glued")
                if h.held(): h("dropheld")
                watch(n, 8, "after")
                print("  -- both grab it in the same moment, three times", flush=True)
                for k in range(3):
                    if h.ents()[n].get("attached") or L.ents()[n].get("attached"): note("the tank is on the car now"); break
                    beside(h, h.ents()[n], (1.2, 0, 0)); beside(L, L.ents()[n], (-1.2, 0, 0)); time.sleep(1)
                    for x in (h, L) if k % 2 == 0 else (L, h):
                        try: x(f"mp holdnet {n}")
                        except RuntimeError: pass
                    watch(n, 5, f"both{k}")
                    for x in (h, L):
                        if x.held(): x("dropheld")
                    time.sleep(5); print("     let go", row(n), flush=True)
                    d = math.dist(h.ents()[n]["pos"], L.ents()[n]["pos"])
                    if d > 0.6: finding("aud-hold", f"both grab {k}: {d:.2f} m apart after both let go")
            else: note("INVALID aud-hold: the tank came off as no object of its own")
        else: note("INVALID aud-hold: no Bus02Tank on the car")
        print("     places: host", h.s("mp places"), "joiner", L.s("mp places"), flush=True)
        print("     anomalies host:", str(h.s("mp anomalies").get("lines"))[-500:], flush=True)
        print("     refused:", h.s("mp entities").get("refusedWhy"), flush=True)
        check(h, L, tracked, cars, "aud-hold")
    if want("aud-handcopy"):
        print("\n=== a copy in a player's hands with no claim sent (forced): stuck? does the guard take it?", flush=True)
        beside(h, h.ents()[car]); beside(L, L.ents()[car], (-2.6, 0, 0)); time.sleep(2)
        n = spawn(L, "jerry"); tracked.add(n); time.sleep(3)
        if L.ents()[n]["proxy"]: L.s(f"mp claim {n}"); time.sleep(2)
        def hrow():
            m = h.s(f"mp lockdiag {n}"); o = L.s(f"mp lockdiag {n}")
            return f"{math.dist(h.ents()[n]['pos'], L.ents()[n]['pos']):5.2f} m | host: owner {m.get('owner')} proxy {m.get('proxy')} kin {m.get('kinematic')} body {m.get('movedByBody')} hands {h.held()} | joiner: owner {o.get('owner')} proxy {o.get('proxy')} held {o.get('held')} kin {o.get('kinematic')}"
        for on, secs in ((False, 45), (True, 8)):
            h.s(f"set {T}.HandCopyClaims {on}"); beside(h, h.ents()[n], (2.5, 0, 0)); time.sleep(1)
            print(f"     guard {on}: before", hrow(), flush=True)
            print("     host takes it, no claim:", str(h.s(f"mp handcopy {n}"))[:60], flush=True)
            for k in range(secs):
                if k < 4 or k % 10 == 0: print(f"     {k:2d}s {hrow()}", flush=True)
                time.sleep(1)
            print(f"     guard {on}: {hrow()}", flush=True); print("     places:", h.s("mp places"), flush=True)
            if on and h.ents()[n]["proxy"]: finding("aud-handcopy", "still a copy in the host's hands with the guard on")
            if not on and not h.ents()[n]["proxy"]: note("INVALID aud-handcopy: the hold made no copy in the hands")
            if h.held(): h("dropheld")
            time.sleep(6); d = math.dist(h.ents()[n]["pos"], L.ents()[n]["pos"])
            print(f"     let go: {hrow()}", flush=True)
            if d > 0.6: finding("aud-handcopy", f"guard {on}: {d:.2f} m apart after letting go")
            L.s(f"mp claim {n}"); time.sleep(3)
        print("     anomalies host:", str(h.s("mp anomalies").get("lines"))[-600:], flush=True)
        check(h, L, tracked, cars, "aud-handcopy")
    if want("aud-place-cost"):
        print("\n=== what the position check costs: both players by the car, nothing else going on, a minute each way", flush=True)
        beside(h, h.ents()[car]); beside(L, L.ents()[car], (-2.6, 0, 0)); time.sleep(5)
        for on in (False, True, False, True):
            h.s(f"set {T}.CensusPlaces {on}"); L.s(f"set {T}.CensusPlaces {on}")
            h("net meter reset"); L("net meter reset"); time.sleep(60)
            for who, p in (("host", h), ("joiner", L)):
                m = p("net meter")
                ask = next((t for t in m["types"] if t["type"] == 65), {})
                print(f"     positions {on}, {who}: {m['seconds']:.0f} s, all traffic out {m['outBytesPerSec']} B/s in {m['inBytesPerSec']} B/s; census asks out {ask.get('outMsgs')} msgs {ask.get('outBytes')} B, in {ask.get('inMsgs')} msgs {ask.get('inBytes')} B", flush=True)
        print("     places: host", h.s("mp places"), "joiner", L.s("mp places"), flush=True)
    if want("aud-share"):
        print("\n=== an item spawned and deleted while the message about it is still on its way: left behind anywhere?", flush=True)
        beside(h, h.ents()[car]); beside(L, L.ents()[car], (-2.6, 0, 0))
        for who, o, lag in ((h, L, 1000), (L, h, 1000), (h, L, 0)):
            o(f"mp lag {lag}"); time.sleep(1)
            c0 = o.s("mp namecount benzineskanna 0")["count"]; before = set(who.ents())
            who(f"devspawn {LOOSE['jerry']}"); got = []
            until(lambda: got.extend(k for k, v in who.ents().items() if k not in before and "benzineskanna" in v.get("name", "")) or got, 5)
            print(f"     {who.label} deletes {got}:", str(who.s(f"deleteshare {got[0]}"))[:80] if got else "no item", flush=True)
            o("mp lag 0"); time.sleep(6)
            c1 = o.s("mp namecount benzineskanna 0")["count"]; left = got and got[0] in o.ents()
            print(f"     cans on {o.label}: {c0} before, {c1} after, entity left {left}", flush=True)
            if c1 != c0 or left: finding("aud-share", f"{o.label} keeps a can {who.label} deleted ({c0} -> {c1}, entity left {left})")
    if want("aud-ghost"):
        print("\n=== a car deleted while the other player is 3 km away: do its parts come back when they return?", flush=True)
        if "--oldslot" in sys.argv: L(f"set {T_}.RemoveFarMembers false")
        names = ("Car06PFRDoor", "Car06P(Clone)", "BARREL")
        def counts(p): return {k: p.s(f"mp namecount {k} 0")["count"] for k in names}
        beside(h, h.ents()[car]); time.sleep(2)
        if h.ents()[car]["proxy"]: h.s(f"mp claim {car}"); time.sleep(3)
        print(f"     before: host {counts(h)}, joiner {counts(L)}, entities {len(h.ents())}/{len(L.ents())}", flush=True)
        g = L("gpos"); L(f"tpground {g['gx'] + 3000} {g['gz']}"); note("joiner 3 km away"); time.sleep(14)
        print(f"     far: joiner {counts(L)}", flush=True)
        print("     host deletes the car:", str(h.s(f"deleteshare {car}"))[:90], flush=True); time.sleep(5)
        L(f"tpground {g['gx']} {g['gz']}"); note("joiner back"); time.sleep(20)
        ch, cl = counts(h), counts(L); print(f"     after: host {ch}, joiner {cl}, entities {len(h.ents())}/{len(L.ents())}", flush=True)
        if ch != cl: finding("aud-ghost", f"after the car was deleted: host {ch}, joiner {cl}")
        tracked.clear(); cars = []
    if want("aud-break-far"):
        print("\n=== a window broken with the other player 3 km away", flush=True)
        beside(h, h.ents()[car]); time.sleep(1)
        g = L("gpos"); L(f"tpground {g['gx'] + 3000} {g['gz']}"); note("joiner 3 km away"); time.sleep(12)
        print("     host breaks:", str(h.s(f"mp breakent {car}"))[:160], flush=True); time.sleep(3)
        L(f"tpground {g['gx']} {g['gz']}"); note("joiner back"); time.sleep(16)
        def br(p):
            try: return json.dumps(p.s("mp breakables 40"))
            except RuntimeError as ex: return str(ex)
        bh, bl = br(h), br(L); print(f"     host {bh[:240]}\n     joiner {bl[:240]}", flush=True)
        if bh.count("true") != bl.count("true") or bh.count("false") != bl.count("false"): finding("aud-break-far", f"breakables differ: {bh.count('true')}/{bl.count('true')} true")
    if want("worn-stale"):
        print("\n=== a player puts a hat on while the other player's claim on it is on its way: on one head, loose for the other?", flush=True)
        hat = next(w for w in h("mp wearables")["wearables"] if w[2] in ("hat", "cap"))
        L(f"mp lag {arg('--lag', '400')}"); time.sleep(2)
        for rnd in range(4):
            before = set(L.ents()); beside(L, L.ents()[car], (4, 0, 2 * rnd)); L(f"devspawn {hat[0]}")
            def newhat(): return next((k for k, v in L.ents().items() if k not in before and v.get("name", "").startswith(hat[1])), None)
            until(newhat, 10); n = newhat(); tracked.add(n); time.sleep(3)
            beside(h, h.ents()[n], (1.2, 0, 0)); time.sleep(1)
            if h.ents()[n]["owner"] != L.me(): L.s(f"mp claim {n}"); time.sleep(2)
            if rnd == 0: print("     no claim this round: what a hat looks like when nothing goes wrong", flush=True)
            else: print("     host claims it:", h.s(f"mp claim {n}"), flush=True)
            time.sleep(0.05)
            print("     joiner puts it on:", str(L.s(f"mp wear {n}"))[:90], flush=True)
            time.sleep(6)
            g = L("gpos"); L(f"tpground {g['gx'] + 8} {g['gz'] + 3}"); time.sleep(4)
            eh, el = h.ents()[n], L.ents()[n]; me = L("gpos")
            apart = math.dist(eh["pos"], el["pos"])
            def wrow(p):
                j = p.s("mp worn"); rows = next((v for v in j.values() if isinstance(v, list) and v and isinstance(v[0], dict) and "net" in v[0]), [])
                r = next((x for x in rows if x["net"] == n), {})
                return {k: r.get(k) for k in ("proxy", "localSlot", "wornBy", "wearing", "rides", "headDist", "onBody")}
            wh, wl = wrow(h), wrow(L)
            print(f"     host {wh}\n     joiner {wl}", flush=True)
            if wl.get("localSlot", -1) >= 0 and wl.get("proxy"): finding(f"worn-stale {rnd + 1} / slot", "in the joiner's wear slot, the hat itself is the host's and lies elsewhere")
            if wl.get("localSlot", -1) >= 0 and float(wl.get("headDist") or 0) > 1: finding(f"worn-stale {rnd + 1} / head", f"on the joiner's head in the game, {wl['headDist']} m from it")
            print(f"     owner {eh['owner']}/{el['owner']}, copy {eh['proxy']}/{el['proxy']}, hat apart {apart:.2f} m, took back {h('get ' + T_ + '.WornTakenBack').get('value')}, log {h(f'mp ownerlog {n}')['log'][-2:]}", flush=True)
            if apart > 1: finding(f"worn-stale {rnd + 1}", f"on the joiner's head, {apart:.1f} m away on the host's screen (owner {eh['owner']})")
            check(h, L, tracked, cars, f"worn-stale {rnd + 1}")
            tracked.discard(n)
            try: L.s(f"mp takeoff {n} drop")
            except RuntimeError as ex: print("     takeoff:", str(ex)[:80], flush=True)
            time.sleep(2)
        L("mp lag 0")
    if want("edit-stale"):
        print("\n=== a part comes with a car the player just took, the player edits it before hearing that it's theirs", flush=True)
        door = next((m for m in bolted(h, car) if "Door" in m["name"]), None)
        beside(h, h.ents()[car]); take_car(h, car)
        known = set(h.ents()); h.act("detach", net=car, item=door["i"]); time.sleep(3)
        dn = next((k for k, v in h.ents().items() if k not in known and v.get("name") == door["name"]), None)
        h.try_act("attach", net=dn, other=car, item=door["on"] + 1, slot=door["onSlot"], a=2); time.sleep(4)
        tracked.add(dn)
        L(f"mp lag {arg('--lag', '400')}"); time.sleep(2)
        for rnd in range(3):
            beside(h, h.ents()[car]); take_car(h, car)
            beside(L, L.ents()[car]); time.sleep(1)
            r0 = (h(f"get {T_}.EditsRejected").get("value"), h(f"get {T_}.EditsReturned").get("value"), L(f"get {T_}.EditsApplied").get("value"))
            print("     joiner claims the car:", L.s(f"mp claim {car}"), flush=True)
            time.sleep(0.03)
            print("     joiner edits the door:", str(L.s(f"mp forgeedit {dn} ok"))[:100], flush=True)
            time.sleep(3)
            r1 = (h(f"get {T_}.EditsRejected").get("value"), h(f"get {T_}.EditsReturned").get("value"), L(f"get {T_}.EditsApplied").get("value"))
            print(f"     refused {r0[0]} -> {r1[0]}, returned {r0[1]} -> {r1[1]}, applied on the joiner {r0[2]} -> {r1[2]}, door owner {h.ents()[dn]['owner']}", flush=True)
            if r1[0] != r0[0]: finding(f"edit-stale {rnd + 1}", "the joiner's edit was refused and lost")
            check(h, L, tracked, cars, f"edit-stale {rnd + 1}")
        L("mp lag 0")
    if want("door-chaos"):
        print("\n=== one door of the joiner's car, off and on in every odd situation: a door on one screen only, or two in a slot?", flush=True)
        dm = next((m for m in bolted(h, car) if "FLDoor" in m["name"]), None) or next((m for m in bolted(h, car) if "door" in m["name"].lower()), None)
        doorcase(h, L, car, dm, tracked, cars)
    if want("doors"):
        print("\n=== doors on the joiner's car, off, on again, swapped: by the player who doesn't own the car, then by its owner (report 211707)", flush=True)
        for m in h.members(car):
            if m.get("slot"):
                o = next((x for x in L.members(car) if x["i"] == m["i"]), {})
                print(f"     {m['i']:3} {m['name']:30} on {m.get('on')}/{m.get('onSlot')} joiner on {o.get('on')}/{o.get('onSlot')} lp {m.get('lp')}", flush=True)
        def off(A, m):
            known = set(A.ents()); A.act("detach", net=car, item=m["i"]); time.sleep(3)
            new = [k for k, v in A.ents().items() if k not in known and v.get("name") == m["name"]]
            if not new: finding(f"doors {m['name']}", f"{A.label}: no object of its own after the detach"); return None
            tracked.add(new[0]); return new[0]
        def on(A, net, onitem, slot):
            A.try_act("attach", net=net, other=car, item=onitem + 1, slot=slot, a=2); time.sleep(3)
        for A in (h, L):
            beside(A, A.ents()[car])
            doors = [m for m in bolted(A, car) if "door" in m["name"].lower() and m.get("on", -1) >= 0]
            note(f"{A.label}: doors {[(m['i'], m['name'], m['on'], m['onSlot']) for m in doors]}")
            for m in doors:
                n = off(A, m)
                if n is None: continue
                check(h, L, tracked, cars, f"doors {A.label} {m['name']} off")
                on(A, n, m["on"], m["onSlot"])
                check(h, L, tracked, cars, f"doors {A.label} {m['name']} back on")
                ph, pl = h.pose_on(n), L.pose_on(n)
                note(f"{m['name']}: back on: host {ph and ph.get('attached')}, joiner {pl and pl.get('attached')}")
            doors = [m for m in bolted(A, car) if "door" in m["name"].lower() and m.get("on", -1) >= 0]
            pairs = [(x, y) for x in doors for y in doors if x["i"] < y["i"] and x["name"] == y["name"]]
            note(f"{A.label}: same doors to swap: {[(x['i'], y['i']) for x, y in pairs]}")
            for x, y in pairs[:2]:
                nx, ny = off(A, x), off(A, y)
                if nx is None or ny is None: continue
                check(h, L, tracked, cars, f"doors {A.label} swap off")
                on(A, nx, y["on"], y["onSlot"]); on(A, ny, x["on"], x["onSlot"])
                check(h, L, tracked, cars, f"doors {A.label} swapped")
    if want("member-out"):
        print("\n=== the owner takes a glued thing off, it falls into the bed and locks there, the other player takes it", flush=True)
        beside(L, L.ents()[car])
        for name in ("Jizzonit", "sponge", "Car03Tank"):
            m = next((x for x in glued(L, car) if name in x["name"]), None)
            if m is None: note(f"no glued {name}"); continue
            before = set(L.ents()); L.act("detach", net=car, item=m["i"]); time.sleep(4)
            new = [k for k, v in L.ents().items() if k not in before and name in v["name"]]
            if not new: finding(f"member-out {name}", "no object of its own after the detach"); continue
            n = new[0]; tracked.add(n)
            fl = L.lock(n) or {}; fh = h.lock(n) or {}
            note(f"{name}: net {n}, lock joiner {fl.get('physlock')!r} host {fh.get('physlock')!r}, member row {[x.get('phys') for x in L.members(car) if x['i'] == m['i']]}")
            check(h, L, tracked, cars, f"member-out {name} off")
            beside(h, h.ents()[n], (1.2, 0, 0)); h(f"mp holdnet {n}"); time.sleep(3)
            note(f"host holds {h.held()}, lock joiner {(L.lock(n) or {}).get('physlock')!r} host {(h.lock(n) or {}).get('physlock')!r}, member row joiner {[x.get('phys') for x in L.members(car) if x['i'] == m['i']]} host {[x.get('phys') for x in h.members(car) if x['i'] == m['i']]}")
            check(h, L, tracked, cars, f"member-out {name} held")
            h.try_act("drop", net=n, item=0); time.sleep(2)
            check(h, L, tracked, cars, f"member-out {name} dropped")
    if want("found"):
        print("\n=== found in the world, never touched: the joiner's item put in the joiner's car by the host", flush=True)
        beside(L, L.ents()[car], (4, 0, 4)); n = spawn(L, "sponge"); tracked.add(n); time.sleep(3)
        beside(h, h.ents()[n], (1.2, 0, 0))
        h.try_act("pickup", net=n, item=0); time.sleep(1); into_car(h, car, n, None)
        check(h, L, tracked, cars, "found item")
    if want("flip"):
        print("\n=== the car changes hands with things in it, and back", flush=True)
        beside(h, h.ents()[car]); take_car(h, car); check(h, L, tracked, cars, "flip to host")
        beside(L, L.ents()[car]); take_car(L, car); check(h, L, tracked, cars, "flip to joiner")
    if want("drive"):
        print("\n=== the joiner drives with the things in the car, the host rides along and takes one out on the way", flush=True)
        beside(L, L.ents()[car]); L(f"mp sitdriver {car}"); time.sleep(2)
        h.try_act("seat_in", net=car, slot=1); time.sleep(1)
        note(f"joiner drives: {str(L('autodrive on 8'))[:60]}"); time.sleep(10)
        check(h, L, tracked, cars, "driving", 0.5)
        c = next((n for n in sorted(tracked) if h.ents().get(n) and not h.ents()[n].get("stored") and not h.ents()[n]["attached"] and h.lock(n) and h.lock(n).get("physlock")), None)
        if c:
            h.try_act("pickup", net=c, item=0); time.sleep(3); h.try_act("drop", net=c, item=0); time.sleep(5)
        g = glued(h, car)
        if g: h.try_act("detach", net=car, item=g[-1]["i"]); time.sleep(4)
        try: L("autodrive off")
        except RuntimeError: pass
        time.sleep(6); h.try_act("seat_out", net=car, slot=1); L("driveout"); time.sleep(3)
        check(h, L, tracked, cars, "after the drive")
    if want("rejoin"):
        print("\n=== the joiner leaves and joins again (the owner of the car goes away)", flush=True)
        L("mp stop"); note("joiner left"); time.sleep(6)
        L(f"mp joinid {h('net me')['steamId']}") if RELAY else L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 120); note("joiner back"); time.sleep(10)
        L("set TLDRevamp.ReproPlayback.Active True"); L("set kaposztaleves.s.settings.god True")
        check(h, L, tracked, cars, "after the rejoin")
    if want("both-grab"):
        print("\n=== both players grab the same thing in the same moment, five times", flush=True)
        n = spawn(L, "sponge"); tracked.add(n); time.sleep(3)
        for k in range(5):
            e = h.ents()[n]
            h(f"tpground {e['pos'][0] + 1.2} {e['pos'][2]}"); L(f"tpground {e['pos'][0] - 1.2} {e['pos'][2]}"); time.sleep(2)
            first, second = (h, L) if k % 2 else (L, h)
            note(f"grab {k + 1}: {first.label} {str(first(f'mp holdnet {n}'))[:40]}, {second.label} {str(second(f'mp holdnet {n}'))[:40]}")
            time.sleep(3)
            hh, lh = h.held(), L.held()
            own = h.ents()[n]["owner"]
            note(f"grab {k + 1}: 3 s later host holds {hh}, joiner holds {lh}, owner {own}")
            if hh and lh: finding(f"both-grab {k + 1}", f"in both hands 3 s later, owner {own}")
            if not hh and not lh: finding(f"both-grab {k + 1}", f"in nobody's hand, owner {own}")
            for p_ in (h, L):
                if p_.held():
                    try: p_("dropheld")
                    except RuntimeError: pass
            check(h, L, tracked, cars, f"both-grab {k + 1}")
            if h.held() or L.held(): finding(f"both-grab {k + 1}", f"still held after letting go: host {h.held()}, joiner {L.held()}")
    if want("glue-race"):
        print("\n=== take a glued can off another player's rim, with the hand a varying moment after the detach (one 'still on' in 15 before)", flush=True)
        cx = h.ents()[car]; off = qrot(cx["rot"], (7, 0, 0))
        L(f"tpground {cx['pos'][0] + off[0]} {cx['pos'][2] + off[2]}"); time.sleep(1.5)
        rim = spawn(L, 43); tracked.add(rim); time.sleep(4)
        gaps = [0, 0.03, 0.1, 0.25, 0.5, 1.0] * 3
        for k, gap in enumerate(gaps):
            tag = f"glue-race {k + 1} (gap {gap} s)"
            e = L.ents().get(rim)
            if not e: note("the rim is gone"); break
            L(f"tpground {e['pos'][0] + 1.2} {e['pos'][2]}"); h(f"tpground {e['pos'][0] - 1.2} {e['pos'][2]}"); time.sleep(1.2)
            try: can = spawn(L, "can")
            except RuntimeError as ex: note(str(ex)); continue
            before = L("get MountAnything.Mounting.Mounts").get("value"); mounted = False
            for a_ in range(8):
                e = L.ents().get(rim); ang = a_ * math.pi / 4
                L(f"tpground {e['pos'][0] + 1.2 * math.cos(ang)} {e['pos'][2] + 1.2 * math.sin(ang)}"); time.sleep(0.8)
                L(f"mp holdnet {can}"); time.sleep(0.3); L(f"mp lookat {rim}"); time.sleep(0.4)
                L("set MountAnything.Mounting.TestPress True"); time.sleep(1.2)
                if L("get MountAnything.Mounting.Mounts").get("value") != before: mounted = True; break
                L("set MountAnything.Mounting.TestPress False")
            if not mounted:
                note(f"{tag}: not mounted")
                try: L("dropheld")
                except RuntimeError: pass
                continue
            time.sleep(2.5)
            h.s(f"repro act 16 {can} 0 0 -1 0 0 0 0 0 0 0 0 0 0 0")
            if gap: time.sleep(gap)
            try: h.s(f"repro act 4 {can} 0 0 -1 0 0 0 0 0 0 0 0 0 0 0")
            except RuntimeError as ex: note(f"{tag}: pickup {str(ex)[:60]}")
            time.sleep(1.5)
            try: h.s(f"repro act 5 {can} 0 0 -1 0 0 0 0 0 0 0 0 0 0 0")
            except RuntimeError as ex: note(f"{tag}: drop {str(ex)[:60]}")
            time.sleep(4)
            a, b = h.ents().get(can), L.ents().get(can)
            note(f"{tag}: host attached {a and a['attached']}, joiner attached {b and b['attached']}, owner {a and a['owner']}/{b and b['owner']}")
            if a and b and (a["attached"] or b["attached"] or a["owner"] != b["owner"]):
                finding(tag, f"host attached {a['attached']} owner {a['owner']}, joiner attached {b['attached']} owner {b['owner']}; owner log {h(f'mp ownerlog {can}')['log'][-6:]}")
    if want("glue-loose"):
        print("\n=== glued onto another loose thing (report 211758: the can's parent was a rim on one machine, nothing on the other)", flush=True)
        cx = h.ents()[car]
        for k, (gluer, rim_owner, where) in enumerate(((L, L, "ground"), (L, L, "ground"), (L, L, "ground"), (L, L, "ground"), (h, h, "ground"), (h, h, "ground"), (h, L, "ground"), (L, h, "ground"), (h, L, "bed"), (L, L, "bed"))):
            tag = f"glue-loose {k + 1} ({gluer.label} glues on {rim_owner.label}'s rim, {where})"
            print(f"  --- {tag}", flush=True)
            cx = h.ents()[car]; off = qrot(cx["rot"], (6 + k * 1.5, 0, 0))
            rim_owner(f"tpground {cx['pos'][0] + off[0]} {cx['pos'][2] + off[2]}"); time.sleep(1.5)
            try: rim = spawn(rim_owner, 43); tracked.add(rim)
            except RuntimeError as ex: note(str(ex)); continue
            if where == "bed": into_car(rim_owner, car, rim, None)
            time.sleep(5)
            e = gluer.ents().get(rim)
            if not e: finding(tag, f"the rim {rim} never reached {gluer.label}"); continue
            gluer(f"tpground {e['pos'][0] + 1.2} {e['pos'][2]}"); time.sleep(1.5)
            try: can = spawn(gluer, "can"); tracked.add(can)
            except RuntimeError as ex: note(str(ex)); continue
            before = gluer("get MountAnything.Mounting.Mounts").get("value"); mounted = False; look = None
            for a_ in range(8):
                e = gluer.ents().get(rim)
                if not e: break
                ang = a_ * math.pi / 4; rr = 1.1 + 0.15 * (a_ % 3)
                gluer(f"tpground {e['pos'][0] + rr * math.cos(ang)} {e['pos'][2] + rr * math.sin(ang)}"); time.sleep(1)
                gluer(f"mp holdnet {can}"); time.sleep(0.4); look = gluer(f"mp lookat {rim}"); time.sleep(0.5)
                gluer("set MountAnything.Mounting.TestPress True"); time.sleep(1.5)
                if gluer("get MountAnything.Mounting.Mounts").get("value") != before: mounted = True; break
                gluer("set MountAnything.Mounting.TestPress False")
            note(f"{gluer.label} glue press (look {look}): {gluer('get MountAnything.Mounting.LastMount').get('value') if mounted else 'nothing mounted'}")
            if not mounted:
                try: gluer("dropheld")
                except RuntimeError: pass
                continue
            check(h, L, tracked, cars, tag + " on")
            a, b = h.ents().get(can), L.ents().get(can)
            note(f"  the can: host attached {a and a['attached']} parent {a and a['parent']}, joiner attached {b and b['attached']} parent {b and b['parent']}")
            if a and b and (a["attached"] != 1 or b["attached"] != 1): finding(tag + " / not on", f"host attached {a['attached']}, joiner attached {b['attached']}")
            # the other player takes it off, as they would
            other = L if gluer is h else h
            other.try_act("detach", net=can, item=0); time.sleep(0.5); other.try_act("pickup", net=can, item=0); time.sleep(1.5); other.try_act("drop", net=can, item=0)
            check(h, L, tracked, cars, tag + " off")
            a, b = h.ents().get(can), L.ents().get(can)
            note(f"  off: host attached {a and a['attached']}, joiner attached {b and b['attached']}, owner {a and a['owner']}")
            if a and b and (a["attached"] or b["attached"]):
                finding(tag + " / not off", f"host attached {a['attached']}, joiner attached {b['attached']}")
                print("     owner log:", h(f"mp ownerlog {can}")["log"][-8:], flush=True)
                print("     detach requests:", h("mp entities")["attach"], "|", L("mp entities")["attach"], flush=True)
                # as the player: try again, twice
                for tr in range(2):
                    other.try_act("detach", net=can, item=0); time.sleep(0.3); other.try_act("pickup", net=can, item=0); time.sleep(1.5); other.try_act("drop", net=can, item=0); time.sleep(3)
                    a, b = h.ents().get(can), L.ents().get(can)
                    note(f"  try {tr + 2}: host attached {a and a['attached']}, joiner attached {b and b['attached']}, owner {a and a['owner']}")
            for rr in (rim, can): tracked.discard(rr) if False else None
    if want("mount"):
        print("\n=== the host glues an item on the joiner's car, takes it off again", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "sponge"); tracked.add(n)
        h(f"mp holdnet {n}"); h(f"mp lookat {car}"); time.sleep(1)
        h("set MountAnything.Mounting.TestPress True"); time.sleep(4)
        note(f"host glue press: {h('get MountAnything.Mounting.LastMount').get('value')}")
        check(h, L, tracked, cars, "host glued on the joiner's car")

# ---- the monkey
def monkey(h, L, car, tracked, cars, seed, steps):
    rng = random.Random(seed)
    print(f"\n=== the monkey, seed {seed}, {steps} steps", flush=True)
    players = (h, L)
    def loose(p): return [n for n in tracked if n in p.ents() and not p.ents()[n].get("dormant") and not p.ents()[n].get("held")]
    for step in range(steps):
        p = rng.choice(players)
        e = p.ents()
        if car not in e: note("car gone"); break
        heldname = p.held()
        ops = ["pickup", "pickup", "spawn", "walk", "seat", "away", "both", "detach", "drive"] if heldname is None else ["drop", "drop_car", "drop_car", "throw", "store", "glue"]
        if p.seated(): ops = ["seat_out", "pickup", "drop_car", "drop_car"] if heldname else ["seat_out", "pickup", "detach"]
        op = rng.choice(ops)
        try:
            if op == "spawn" and len(tracked) < 14:
                beside(p, e[car], (rng.uniform(1.5, 4), 0, rng.uniform(-3, 3))); tracked.add(spawn(p, rng.choice(list(LOOSE))))
            elif op == "pickup":
                c = loose(p)
                if c:
                    n = rng.choice(c)
                    if not p.seated(): beside(p, e[n], (1.2, 0, 0))
                    if p.held(): p("dropheld"); time.sleep(0.3)   # a second pick-up with full hands is no player's: the first item stays half in hand (lock, place findings)
                    try: note(f"{p.label} takes {n}: {str(p(f'mp holdnet {n}'))[:50]}")
                    except RuntimeError as ex: note(f"{p.label} holdnet {n}: {str(ex)[:80]}")
            elif op == "drop" and heldname: p("dropheld"); note(f"{p.label} let go")
            elif op == "drop_car" and heldname:
                me = p.me()
                ms = [n for n in tracked if e.get(n, {}).get("held") and e[n]["owner"] == me]
                if ms: into_car(p, car, ms[0], None)
            elif op == "throw" and heldname:
                g = p("gpos"); note(f"{p.label} throws: {str(p(f'mp throwpoint {e[car]['pos'][0]} {e[car]['pos'][2]} 0.8'))[:60]}")
            elif op == "store" and heldname: p.try_act("inv_store", slot=rng.randrange(0, 3))
            elif op == "glue" and heldname:
                p(f"mp lookat {car}"); time.sleep(0.5); p("set MountAnything.Mounting.TestPress True"); note(f"{p.label} glues: {p('get MountAnything.Mounting.LastMount').get('value')}")
            elif op == "walk":
                c = loose(p)
                if c:
                    n = rng.choice(c); beside(p, e[n], (2.5, 0, 0)); note(f"{p.label} walks into {n}: {p(f'mp walkat {n} 3')}"); time.sleep(3.5)
            elif op == "seat":
                p.try_act("seat_in", net=car, slot=rng.choice([0, 1, 4]))
            elif op == "seat_out":
                try: p("driveout"); note(f"{p.label} got out")
                except RuntimeError as ex: note(f"{p.label} driveout: {str(ex)[:60]}")
            elif op == "away":
                g = p("gpos"); note(f"{p.label} walks 700 m away and back"); p(f"tpground {g['gx'] + 700} {g['gz']}"); time.sleep(15); beside(p, p.ents()[car] if car in p.ents() else e[car], (4, 0, 0))
            elif op == "both":
                c = [n for n in loose(h) if n in loose(L)]
                if c:
                    n = rng.choice(c); beside(h, h.ents()[n], (1.2, 0, 0)); beside(L, L.ents()[n], (-1.2, 0, 0))
                    for x in (h, L):
                        try: x(f"mp holdnet {n}")
                        except RuntimeError: pass
                    time.sleep(3)
                    note(f"both grabbed {n}, 3 s later: host holds {h.held()}, joiner holds {L.held()}")
                    if h.held() and L.held() and h.held() == L.held(): finding("both hold", f"net {n}: in both players' hands 3 s later")
            elif op == "drive":
                beside(p, e[car]); p(f"mp sitdriver {car}"); time.sleep(2); note(f"{p.label} drives: {str(p('autodrive on 6'))[:50]}"); time.sleep(6)
                try: p("autodrive off")
                except RuntimeError: pass
                time.sleep(3); p("driveout"); note(f"{p.label} parked and got out")
            elif op == "detach":
                m = glued(p, car) or bolted(p, car)
                if m:
                    x = rng.choice(m); 
                    if not p.seated(): beside(p, e[car])
                    known = set(e)
                    p.try_act("detach", net=car, item=x["i"]); time.sleep(0.5)
                    for n, y in p.ents().items():
                        if n not in known and not y["car"] and y.get("name"): tracked.add(n)
        except RuntimeError as ex:
            note(f"{p.label} {op}: {str(ex)[:120]}")
        check(h, L, tracked, cars, f"monkey {step + 1}", 4.0)

# ---- run
if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*")): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
Lag = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    try:
        for f in SRC.glob(f"TLDsave*_{SRC_NAME}.*"):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        Lag("!seeded")
        hb = game.loadsave(SAVE)
        for s_ in (hb, Lag):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0"); s_("set kaposztaleves.s.settings.god True")
        if RELAY: print("relay:", hb(f"net relay {RELAY}"), Lag(f"net relay {RELAY}"), flush=True)
        if not join_both(hb, Lag): sys.exit("JOINER NEVER JOINED")
        h, L = Player(hb, "host"), Player(Lag, "joiner")
        for s_ in (h, L):
            s_(f"set TLDRevamp.ReproPlayback.Active True"); s_("set TLDRevamp.TestSafety.BlockAutoSave True")
            s_("set UnityEngine.Application.runInBackground True")
        if "--oldglue" in sys.argv: h(f"set {T}.GluedPartOff False")
        time.sleep(8)
        g = h("gpos")
        cs = sorted((math.hypot(e["pos"][0] - g["gx"], e["pos"][2] - g["gz"]), n) for n, e in h.ents().items() if e["car"] and not e.get("dormant"))
        if not cs: sys.exit("no car near the host")
        car = cs[0][1]
        ce = h.ents()[car]
        print(f"the car: net {car} {ce['name']}, owner {ce['owner']}, {len(glued(h, car))} glued, {len(bolted(h, car))} bolted: {[m['name'] for m in glued(h, car)][:10]}", flush=True)
        beside(h, ce); L(f"tpground {ce['pos'][0] - 5} {ce['pos'][2]}"); time.sleep(10)
        tracked, cars = set(), {car}
        for who, s_ in (("host", h), ("joiner", L)):    # a game kept from the last run brings its old errors along
            e_ = s_("errors"); _counts[who] = e_.get("errors", 0) + e_.get("exceptions", 0)
        _counts["partoff"] = h(f"get {T}.PartOffRejected").get("value", 0)
        check(h, L, tracked, cars, "as they join")
        scripted(h, L, car, tracked, cars)
        if "--no-monkey" not in sys.argv: monkey(h, L, car, tracked, cars, SEED, STEPS)
        for who, s_ in (("host", h), ("joiner", L)):
            a_ = s_.s("mp anomalies")
            print(f"census {who}: asks {a_.get('censusAsks')}, differ {a_.get('censusDiffers')}, repairs asked {a_.get('repairsAsked')}, healed {a_.get('repairsHealed')}, failed {a_.get('repairsFailed')}, run {a_.get('repairsRun')}", flush=True)
            print(f"places {who}: {s_('mp places')}", flush=True)
            for l_ in a_.get("lines", []):
                if "repair" in l_ or "parts differ" in l_: print("    ", l_[:300], flush=True)
        L("mp stop"); h("mp stop")
    finally:
        for f in copied:
            try: f.unlink()
            except OSError: pass
        Lag.close()
print("\nFINDINGS", len(FINDINGS))
seen = {}
for f in FINDINGS:
    k = f["tag"].split(" / ")[-1] + " " + f["detail"].split(":")[0]
    seen.setdefault(k, 0); seen[k] += 1
    print(f"  {f['tag']}: {f['detail']}")
pathlib.Path(os.environ.get("TLD_CHAOS_OUT", "/dev/null")).write_text(json.dumps({"findings": FINDINGS, "history": HISTORY}, indent=1))
