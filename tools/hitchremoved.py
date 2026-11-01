#!/usr/bin/env python3
"""A trailer's tow car removed: the host's record lets the trailer go (ReleaseHitch), and every copy must come off too.
The host hitches a trailer to a car, then removes the car. Its DetachSync must apply on both machines: the trailer free
and still there, in one place (the old message read wrong everywhere and the trailer flew off, far apart).
  tools/hitchremoved.py [--no-build]   (two players: run2.sh / TLD_SECOND)"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both
from roadkit import clear_road, car_ready, until

T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def roots(s): return {n for n, e in ents(s).items() if e["part"] < 0}
def counter(s, name): return s(f"get {T}.{name}").get("value")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        IFA = next(i for i, n in h("itemids Bus02UtanfutoFull")["items"] if n.lower() == "bus02utanfutofull")
        rp = h("roadpoint"); F0 = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F0[0], F0[1]); yaw = r0["yaw"]
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        x, z = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        if not car_ready(h, x, z, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        h("driveout"); time.sleep(2)
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        time.sleep(4)

        def spawn_trailer(px, pz):
            before = roots(h)
            for k in range(10):   # the ground there may still be generating (after the join)
                try: h(f"spawncarg {IFA} 0 {px} {pz} {yaw}"); break
                except RuntimeError as e:
                    if "no ground" not in str(e) or k == 9: raise
                    h(f"tpground {px + 4} {pz}"); time.sleep(3)
            until(lambda: roots(h) - before, 15); time.sleep(2)
            es = ents(h); new = [es[n] for n in roots(h) - before]
            return min(new, key=lambda e: math.dist((e["pos"][0], e["pos"][2]), (px, pz)))["net"] if new else None

        def state(p, tr):
            e = ents(p).get(tr)
            if e is None: return "gone"
            try: st = p(f"mount hitchstate {tr}")
            except RuntimeError as ex: st = {"error": str(ex)[-20:]}
            return {"attached": st.get("attached"), "owner": e["owner"], "pos": [round(v, 1) for v in e["pos"]], "error": st.get("error")}

        def round_(remover, label, gx, gz):   # only the car's owner: a copy's holder doesn't remove it
            print(f"\n--- {label} removes the tow car", flush=True)
            pos = h("mp players")["me"]
            car = min((e for e in ents(h).values() if e["part"] < 0 and e["car"] and "Utanfuto" not in e.get("name", "")), key=lambda e: math.dist(e["pos"], pos))["net"]
            probe = spawn_trailer(gx + R[0] * 15, gz + R[1] * 15)
            gp = h(f"mount gap {car} {probe}"); ahead, ball = gp["ahead"], gp["ball"]
            tr = spawn_trailer(ball[0] - F[0] * (ahead + 0.7), ball[2] - F[1] * (ahead + 0.7))
            print("  car", car, "trailer", tr, "gap", h(f"mount gap {car} {tr}").get("dist"), flush=True)
            L(f"tpground {ball[0] + R[0] * 8} {ball[2] + R[1] * 8}"); time.sleep(4)
            h(f"tpground {ball[0] - F[0] * 2 + R[0] * 1.5} {ball[2] - F[1] * 2 + R[1] * 1.5}"); time.sleep(3)
            h(f"mount pickgrip {tr} stand"); time.sleep(1.5); h(f"mount pickgrip {tr}"); time.sleep(1.5)
            print("  attach:", str(h("mount playermount 3"))[:90], flush=True)
            on = until(lambda: h(f"mount hitchstate {tr}").get("attached") and L(f"mount hitchstate {tr}").get("attached"), 10)
            verdict(f"{label}: the trailer hitched on both machines", bool(on))
            if not on: return
            time.sleep(2)
            a0 = {"host": counter(h, "DetachSyncsApplied"), "joiner": counter(L, "DetachSyncsApplied")}
            print(f"  {label} removes the car:", str(remover(f"deleteshare {car}"))[:90], flush=True)
            for k in range(6):
                time.sleep(1); print(f"  {k + 1} s: host {state(h, tr)} | joiner {state(L, tr)} | car here: host {car in ents(h)}, joiner {car in ents(L)}", flush=True)
            a1 = {"host": counter(h, "DetachSyncsApplied"), "joiner": counter(L, "DetachSyncsApplied")}
            print(f"  detach messages applied: {a0} -> {a1}", flush=True)
            hs, ls = state(h, tr), state(L, tr)
            verdict(f"{label}: the trailer exists on both machines or on neither", (hs == "gone") == (ls == "gone"), f"host {hs}, joiner {ls}")
            if hs != "gone" and ls != "gone":
                verdict(f"{label}: it is free on both", hs["attached"] is False and ls["attached"] is False)
                d = math.dist(ents(h)[tr]["pos"], ents(L)[tr]["pos"])
                verdict(f"{label}: it is in one place", d < 1.0, f"{d:.2f} m apart")

        round_(h, "the host", x, z)
    finally:
        for s_ in (h, L):
            try: s_("set TLDRevamp.TestSafety.BlockAutoSave False")
            except Exception: pass
print("\n".join(f"{'PASS' if ok else 'FAIL'}  {n}" for n, ok in VERDICTS), flush=True)
sys.exit(0 if VERDICTS and all(ok for _, ok in VERDICTS) else 1)
