#!/usr/bin/env python3
"""Where a hosted session's frame goes, by the mod's own steps (the 2026-11-10 DevTest session: 16 ms frames on 11-06,
35 ms and a 120 ms hitch about once a second on 11-10, in a session only). The host loads a world save (TLD_PLAYSAVE_DIR
holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default autosave1), the joiner joins and stands beside the host. The
script timers time Entities.FixedTick, Mp.Tick and each step they call; then each newer system is switched off in turn
and the same is timed again.
  tools/perfmp.py [--no-build] [--secs 15] [--top 25]"""
import sys, os, time, shutil, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
SECS, TOP = arg("--secs", 15, float), arg("--top", 25, int)
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "autosave1")
SAVE = "perfmp"
T = "TLDRevamp.Net.Entities"

FIXED = ("HitchTick EditBackTick HitchPhysicsTelemetry TraceTick TelemetryPhysicsTick CrashHoldTick ServerCrashTick CarCargoTick "
         "RefreshOwnCars RefreshJointed ClaimContents ColliderLodTick CopyJointTick LinkedDelayTick TowTick CargoNearTick BodyNearTick "
         "ItemNearTick RabbitNearTick WatchTick BodyMotionTick StandInterpTick EnterPhysical ExitPhysical Servo").split()
TICK = ("CopyLodTick AnomalyTick PartWatchTick HealthWatchTick GrabRefusedTick RefetchLaterTick BreakWaitTick WorldBreakTick PoiCensusTick "
        "HostClockTick DriveClaimTick ShotgunTick PushTick RadioTick KeyTick KeyTestTick PhysLockTick PushGripTick StormTick ServerTimeTick "
        "ServerInterestTick ServerHandoffTick ServerRabbitTick LeaseTick LiveUsableTick EditTick CopyFluidTick ReshareTick BlastTick "
        "MemberGoneTick ShareQuotaTick AiTick RabbitTick WornTick GentleTick ClaimTraceTick ResyncFrameTick FlushFarRecords SprayTick "
        "RagdollTick PartWatchScan PartWatchLook ItemWatchScan ItemWatchLook HealthWatchCycle HealthWatchOne").split()
E = "TLDRevamp.Net.Entities"
TIMERS = [f"{E}.FixedTick", "Mp.Tick", f"{E}.Tick", f"{E}.LateSample", f"{E}+ItemDestroyed.Postfix"] + [f"{E}.{m}" for m in FIXED + TICK]
# (label, the switch, its value when off)
SWITCHES = [("HealthWatch", f"{T}.HealthWatch", "False"), ("PartWatch", f"{T}.PartWatch", "False"),
            ("ItemWatch", f"{T}.ItemWatch", "False"), ("CopyJointsFree", f"{T}.CopyJointsFree", "False"),
            ("LodKeepsPartsOff", f"{T}.LodKeepsPartsOff", "False"), ("HandItemDraw", f"{T}.HandItemDraw", "False")]

def measure(h, label):
    """SECS of whole 5 s windows (the session log resets the timers' window every 5 s; `timers show` reads it): the
    last reading before each reset is one window"""
    wins, last, t0 = [], None, time.time()
    while time.time() - t0 < SECS + 5.5:
        r = h("timers show 200")
        if last and r.get("frames", 0) < last.get("frames", 0):
            wins.append(last)
            if time.time() - t0 >= SECS: break
        last = r; time.sleep(0.4)
    frames = sum(w["frames"] for w in wins) or 1
    tot = {}
    for w in wins:
        for t in w.get("top", []):
            a = tot.setdefault(t["m"], [0.0, 0.0, 0.0])
            a[0] += t["msPerFrame"] * w["frames"]; a[1] += t["callsPerFrame"] * w["frames"]; a[2] = max(a[2], t["maxMs"])
    rows = sorted(((m, a[0] / frames, a[1] / frames, a[2]) for m, a in tot.items()), key=lambda x: -x[1])
    get = {m: (ms, c, mx) for m, ms, c, mx in rows}
    f, m_ = get.get("Entities.FixedTick", (0, 0, 0)), get.get("Mp.Tick", (0, 0, 0))
    print(f"\n== {label}: {len(wins)} windows, {frames} frames ({frames / max(1, 5 * len(wins)):.0f} fps); FixedTick {f[0]:.2f} ms/frame "
          f"({f[1]:.2f} calls, max {f[2]:.1f}), Mp.Tick {m_[0]:.2f} ms/frame (max {m_[2]:.1f})", flush=True)
    for m, ms, c, mx in rows[:TOP]:
        print(f"   {m:<40} {ms:8.3f} ms/frame  {c:6.2f} calls  max {mx:7.2f}", flush=True)
    return get

if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*")): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
L = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    h = None
    try:
        for f in SRC.glob(f"TLDsave*_{SRC_NAME}.*"):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        L("!seeded")
        h = game.loadsave(SAVE)
        for s_ in (h, L):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0")
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        hp = h("gpos"); L(f"tpground {hp['gx'] + 4} {hp['gz'] + 4}")
        time.sleep(20)
        print("  shared objects on the host:", len(h("mp entities 2000")["entities"]), flush=True)
        print("  timers:", h("timers on " + " ".join(TIMERS)), flush=True)
        time.sleep(3)
        measure(h, "NEW: LOOPS OVER PLACED OBJECTS")
        print("  placed:", h(f"get {T}.PlacedCount"), flush=True)
        h(f"set {T}.LoopPlacedOnly False")
        measure(h, "OLD: LOOPS OVER EVERY SHARED OBJECT")
        h(f"set {T}.LoopPlacedOnly True")
        measure(h, "NEW AGAIN")
    finally:
        if h is not None:
            try: h("timers off"); L("mp stop"); h("mp stop")
            except Exception: pass
        for p in copied:
            try: p.unlink()
            except OSError: pass
L.close()
