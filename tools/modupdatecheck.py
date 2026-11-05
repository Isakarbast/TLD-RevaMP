#!/usr/bin/env python3
"""The start-up mod update check (Net/ModsScreen.StartupCheck), one game, against the real catalog on GitHub:
Road End's previous release is planted as if the mod browser had installed it, then
  A. with the bridge on, the game doesn't check by itself
  B. as at start (the bridge guard off for a moment, the once-per-run flag reset), the game checks by itself, opens the
     Mods screen and asks to update Road End to the catalog's version
  C. No goes back to the main menu and changes nothing
  D. asked again, Yes downloads it: the new file is the approved one, the old one is set aside, the screen says to restart
The copy's managed mods (and their enable states) are put back as they were afterwards.
  tools/modupdatecheck.py [--no-build]"""
import os
os.environ["TLD_TEST_MODS"] = ""   # no hand-installed example mods: Road End must be the managed copy
import sys, time, json, io, zipfile, hashlib, pathlib, shutil, tempfile, urllib.request
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parent.parent
MOD = "tldrevamp.roadend"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(f, timeout, step=1.0):
    end = time.monotonic() + timeout
    while True:
        r = f()
        if r or time.monotonic() > end: return r
        time.sleep(step)

catalog = json.loads((ROOT / "packaging/mods.json").read_text())
versions = next(m for m in catalog["mods"] if m["id"] == MOD)["versions"]
new, old = versions[0], versions[1]
approved = {l.split()[0]: l.split()[1:] for l in (ROOT / "packaging/approved-mods.txt").read_text().splitlines() if l and not l.startswith("#")}
data = urllib.request.urlopen(old["url"], timeout=60).read()
dll = zipfile.ZipFile(io.BytesIO(data)).read(old["file"])
assert hashlib.sha256(dll).hexdigest() == old["sha256"] and approved.get(old["sha256"]) == [MOD, old["version"]], "old release does not match"

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
managed = tldenv.GAME_DIR / "BepInEx/plugins/TLDRevampMods"
states = tldenv.GAME_DIR / "BepInEx/disabled-mods"
# Yes updates every managed mod with an update, not only the planted one: keep both folders as they were
saved = pathlib.Path(tempfile.mkdtemp(prefix="modupdatecheck-"))
for d in (managed, states):
    if d.exists(): shutil.copytree(d, saved / d.name, symlinks=True)
def ours(): return sorted(p.name for p in managed.glob(MOD + "-*")) if managed.exists() else []
def restore():
    for d in (managed, states):
        if d.exists(): shutil.rmtree(d)
        if (saved / d.name).exists(): shutil.copytree(saved / d.name, d, symlinks=True)
    shutil.rmtree(saved)
managed.mkdir(parents=True, exist_ok=True)
for p in managed.glob(MOD + "-*"): p.unlink()
(managed / f"{MOD}-{old['version']}.dll").write_bytes(dll)
print("planted:", ours(), flush=True)

game.launch()
b = Bridge()
try:
    time.sleep(6)
    s = b("mods updates")
    verdict("A. no check by itself with the bridge on", not s["open"] and not s["catalog"], json.dumps(s))
    want = f"Road End {old['version']} to {new['version']}"
    def ask(start):
        start()
        s = until(lambda: (lambda r: r if r.get("question") else None)(b("mods updates")), 90)
        q = (s or {}).get("question") or ""
        return bool(s and s["open"] and want in q and q.endswith("Update now?")), q
    def as_at_start():
        b("set TLDRevamp.Plugin.BridgeEnabled False"); b("set TLDRevamp.Net.ModsScreen._checked False")
    try: ok, q = ask(as_at_start)
    finally: b("set TLDRevamp.Plugin.BridgeEnabled True")
    verdict("B. at start, the game checks by itself and asks to update Road End", ok, repr(q))
    try: print("  screenshot:", b("screenshot"), flush=True)
    except Exception as e: print("  screenshot failed:", e, flush=True)
    b("mods no")
    menu = until(lambda: b("get menuhandler.s.Menu.activeSelf").get("value") in (True, "True"), 10)
    verdict("C. No goes back to the main menu and changes nothing", menu and not b("mods updates")["open"] and ours() == [f"{MOD}-{old['version']}.dll"], str(ours()))
    ok, q = ask(lambda: b("mods check"))
    if not ok: print("  asked again:", repr(q), flush=True)
    b("mods yes")
    s = until(lambda: (lambda r: r if not r["busy"] and ("Updated" in r["message"] or "Not updated" in r["message"]) else None)(b("mods updates")), 120)
    msg = (s or {}).get("message", "")
    files = ours()
    newfile = managed / f"{MOD}-{new['version']}.dll"
    ok_hash = newfile.exists() and hashlib.sha256(newfile.read_bytes()).hexdigest() == new["sha256"]
    verdict("D. asked again, Yes installs the approved new version and says to restart",
            ok_hash and msg.startswith("Updated ") and f"Road End {new['version']}" in msg and "Restart the game" in msg and f"{MOD}-{old['version']}.dll" not in files,
            f"{msg!r} files={files}")
    try: print("  screenshot:", b("screenshot"), flush=True)
    except Exception as e: print("  screenshot failed:", e, flush=True)
finally:
    try: game.quit_game()
    finally: restore()
print("Road End files after restoring:", ours(), flush=True)
print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
sys.exit(0 if VERDICTS and all(ok for _, ok in VERDICTS) else 1)
