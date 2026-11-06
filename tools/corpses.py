#!/usr/bin/env python3
"""Players' corpses stay and look like their player (Entities.Death DeathCorpseStays, Entities.Corpses), two players:
  A. the host dies: after the respawn its corpse is still there on both machines, in the host's outfit (a random one
     set first, so it can't match the joiner's by chance), drawn whole and solid, its limbs in the same place
  B. the joiner dies: the same for the joiner's corpse; both corpses on both machines
  C. the joiner grabs the host's corpse by a limb: it takes the body, and the host's copy follows it
  D. the host dies again: three corpses, the first two still where they were
  E. the joiner leaves and joins again: it gets all three, dressed and placed the same
--old runs with the switches off (DeathCorpseStays, CorpseWhole, CorpseOutfitSaved): the corpses go at the respawn, as before.
  tools/corpses.py [--no-build] [--relay iad|off] [--old]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
RELAY = arg("--relay", "off")
OLD = "--old" in sys.argv

V = []
def verdict(name, ok, detail=""):
    V.append((name, bool(ok))); print(f"  {'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def dist(a, b): return sum((x - y) ** 2 for x, y in zip(a, b)) ** .5

def corpses(side):
    # keyed by the network id: the save id is each machine's own counter (they differ between machines)
    try: return {c["net"]: c for c in side("mp corpses")["corpses"]}
    except Exception as e: print("  corpses:", str(e)[:200], flush=True); return {}

def look(o): return (o["character"], o["on"], o["colors"]) if isinstance(o, dict) else None

def respawn_count(side): return side("get TLDRevamp.Net.SessionRules.Respawns")["value"]

def net_of(side, sid, timeout=8):
    """the network id of this machine's corpse with this save id (its share is answered a moment after the death)"""
    end = time.time() + timeout
    while sid and time.time() < end:
        net = next((n for n, c in corpses(side).items() if c["saveId"] == sid), 0)
        if net: return net
        time.sleep(0.5)
    return 0

def die(side, name, vel="6 9 0"):
    r0 = respawn_count(side)
    f = side(f"mp deathfling {vel}")
    print(f"  {name} dies:", json.dumps(f)[:300], flush=True)
    ok = f.get("died") is True
    sid = f.get("corpseSaveId") or 0
    net = net_of(side, sid)   # read before the respawn: with --old the respawn takes the corpse away
    back = until(lambda: respawn_count(side) > r0, 40, 0.5)
    side("set kaposztaleves.s.settings.god True")
    time.sleep(4)   # the body settles; the last state reaches the other machine
    return ok and back, net

def same(label, sid, owner_side, owner_name, other, other_name, want_look, want_owner_proxy=False):
    """one corpse on both machines: there, dressed as its player, drawn whole, solid, in the same place"""
    a, b = corpses(owner_side).get(sid), corpses(other).get(sid)
    verdict(f"{label}: ON BOTH MACHINES AFTER THE RESPAWN", a is not None and b is not None,
            f"{owner_name} {'yes' if a else 'no'}, {other_name} {'yes' if b else 'no'}")
    if not a or not b: return None
    verdict(f"{label}: DRESSED AS ITS PLAYER", look(a["outfit"]) == want_look and look(b["outfit"]) == want_look,
            f"player {want_look}\n      {owner_name} {look(a['outfit'])}\n      {other_name} {look(b['outfit'])}")
    # a character draws its own renderers only (the other character's are off), so the counts are compared
    verdict(f"{label}: DRAWN WHOLE", a["renderers"][0] > 0 and a["renderers"] == b["renderers"] and b["cut"] == 0,
            f"renderers {owner_name} {a['renderers']} {a.get('drawn')} {other_name} {b['renderers']} {b.get('drawn')}, first-person cut {owner_name} {a['cut']} {other_name} {b['cut']}")
    verdict(f"{label}: SOLID", a["colliders"][0] > 0 and b["colliders"][0] > 0, f"colliders {owner_name} {a['colliders']} {other_name} {b['colliders']}")
    gap = dist(a["centre"], b["centre"])
    verdict(f"{label}: SAME PLACE", gap < 0.3, f"limb centres {gap:.2f} m apart")
    return a, b

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("THE SECOND PLAYER NEVER JOINED")
    try:
        # set either way: a game kept from an --old run still has them off
        for s in (h, L):
            for k in ("DeathCorpseStays", "CorpseWhole", "CorpseOutfitSaved"): s(f"set TLDRevamp.Net.Entities.{k} {not OLD}")
        h("mp policy everyone")    # the joiner's death below is a developer command
        time.sleep(1)
        hp = h("gpos")
        L(f"tpground {hp['gx'] + 6} {hp['gz'] + 4}"); time.sleep(3)
        host_look, joiner_look = look(h("mp outfit random")), look(L("mp outfit random"))
        print("  outfits: host", host_look, "\n           joiner", joiner_look, flush=True)
        time.sleep(2)

        # A
        ok, sa = die(h, "host")
        verdict("A: THE HOST DIED AND RESPAWNED", ok and sa, f"corpse net id {sa}")
        if OLD:
            gone = sa and sa not in corpses(h) and sa not in corpses(L)
            verdict("OLD: THE CORPSE WENT AT THE RESPAWN", gone, f"net {sa}: host {sorted(corpses(h))}, joiner {sorted(corpses(L))}")
        else:
            ra = same("A HOST'S CORPSE", sa, h, "host", L, "joiner", host_look)

            # B
            ok, sb = die(L, "joiner", "-6 9 3")
            verdict("B: THE JOINER DIED AND RESPAWNED", ok and sb, f"corpse net id {sb}")
            same("B JOINER'S CORPSE", sb, L, "joiner", h, "host", joiner_look)
            verdict("B: BOTH CORPSES ON BOTH MACHINES", {sa, sb} <= set(corpses(h)) and {sa, sb} <= set(corpses(L)),
                    f"host {sorted(corpses(h))}, joiner {sorted(corpses(L))}")

            # C
            if ra:
                c = corpses(h)[sa]
                before = h(f"mp ragdoll {c['net']}")["limbs"]
                lb = next(l for l in before if l != "null")
                L(f"tpground {lb[0] + 1.2} {lb[2] + 1.2}"); time.sleep(3)   # as tools/ragdolltest.py: within reach of a limb
                print("  joiner at", L("gpos"), "limb at", lb[:3], flush=True)
                g = L(f"mp ragdoll {c['net']} hold"); print("  joiner grabs:", g, flush=True)
                time.sleep(3)
                a, b = corpses(L).get(sa), corpses(h).get(sa)
                verdict("C: GRABBING A LIMB TAKES THE BODY", a and b and not a["proxy"] and b["proxy"],
                        f"joiner owns {a and not a['proxy']}, host copy {b and b['proxy']}")
                lj, lh = L(f"mp ragdoll {c['net']}")["limbs"], h(f"mp ragdoll {c['net']}")["limbs"]
                lifted = max(dist(x[:3], y[:3]) for x, y in zip(before, lh) if x != "null" and y != "null")
                worst = max(dist(x[:3], y[:3]) for x, y in zip(lj, lh) if x != "null" and y != "null")
                verdict("C: THE HOST'S COPY FOLLOWS IT", lifted > 0.2 and worst < 0.5,
                        f"held limb moved {lifted:.2f} m on the host's screen, worst limb {worst:.2f} m apart (held: moving)")
                try: print("  joiner drops:", L("mp drop"), flush=True)
                except Exception as e: print("  joiner drops:", str(e)[:120], flush=True)
                time.sleep(5)

            # D
            before = corpses(h)
            ok, sd = die(h, "host", "4 9 -6")
            verdict("D: THE HOST DIED AGAIN", ok and sd and sd not in before, f"corpse net id {sd}")
            hc, lc = corpses(h), corpses(L)
            verdict("D: THREE CORPSES ON BOTH MACHINES", {sa, sb, sd} <= set(hc) and {sa, sb, sd} <= set(lc), f"host {sorted(hc)}, joiner {sorted(lc)}")
            stay = [round(dist(before[s]["centre"], hc[s]["centre"]), 2) for s in (sa, sb) if s in before and s in hc]
            verdict("D: THE EARLIER CORPSES STAYED PUT", len(stay) == 2 and max(stay) < 0.3, f"moved {stay} m")
            same("D HOST'S SECOND CORPSE", sd, h, "host", L, "joiner", host_look)

            # E
            L("mp stop"); time.sleep(5)
            if not join_both(h, L, rehost=False): verdict("E: THE JOINER CAME BACK", False)
            else:
                time.sleep(8)
                hc, lc = corpses(h), corpses(L)
                verdict("E: ALL THREE AFTER JOINING AGAIN", {sa, sb, sd} <= set(lc), f"joiner {sorted(lc)}")
                for s, who, want in ((sa, "host", host_look), (sb, "joiner", joiner_look), (sd, "host", host_look)):
                    if s in hc and s in lc:
                        gap = dist(hc[s]["centre"], lc[s]["centre"])
                        verdict(f"E: {who.upper()}'S CORPSE {s} DRESSED AND PLACED THE SAME",
                                look(lc[s]["outfit"]) == want and lc[s]["cut"] == 0 and gap < 0.3,
                                f"{look(lc[s]['outfit'])}, cut {lc[s]['cut']}, {gap:.2f} m apart")
        print("  host corpses:", h("mp corpses"), flush=True)
    finally:
        for s in (h, L):
            try: s("set kaposztaleves.s.settings.god True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
L.close()
print(f"{sum(ok for _, ok in V)}/{len(V)} passed", flush=True)
sys.exit(0 if V and all(ok for _, ok in V) else 1)
