#!/usr/bin/env python3
"""The TV Browser's sound (src/Mods/TvBrowser/Sound.cs), one game: a TV spawned next to the player, switched on, its
keyboard taken, then
  A. a page's tone (WebAudio, 440 Hz, started by a click as a player would): it reaches the TV's buffer at the page's
     rate, and the TV's source plays it
  B. farther away it is quieter; beyond HearWithin (20 m) it is gone
  C. a real media file (an .ogg on Wikimedia Commons, played by a click): heard
  D. YouTube (--youtube): a video's sound, as a player would start it (YouTube may ask this network to sign in)
  E. switched off: the TV's sound source goes
Test games are muted (tools/testdisplay.py), and Unity then gives the TV's filter a carrier of 0 and skips some of its
blocks (a source at volume 0): the loudness checks (A's gaps and output, B) only run in a game with sound, and say so
otherwise. Sound.Fill itself is unit tested (tools/unit/TvBrowserCoreTest).
  TLD_TEST_MODS=TvBrowser tools/tvsound.py [--no-build] [--youtube]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "TvBrowser")
import sys, time, json, pathlib, urllib.parse
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(f, timeout, step=0.5):
    end = time.monotonic() + timeout
    while True:
        r = f()
        if r or time.monotonic() > end: return r
        time.sleep(step)

def esc(text): return urllib.parse.quote(text, safe="")

TONE = ("(()=>{window.__tone=1;addEventListener('click',()=>{if(window.__ctx)return;const c=new AudioContext();window.__ctx=c;"
        "const o=c.createOscillator(),g=c.createGain();o.frequency.value=440;g.gain.value=0.5;o.connect(g);g.connect(c.destination);o.start();},{once:true});return 'armed'})()")
CENTER = ("(function(s){const e=document.querySelector(s);if(!e)return null;const r=e.getBoundingClientRect();"
          "return [Math.round(r.x+r.width/2),Math.round(r.y+r.height/2)]})")

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.deploy_local()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge()
    def call(what, *args):
        try: return str(b("call TvBrowser.Probe." + " ".join([what, *map(str, args)]))["value"])
        except Exception as e: return "ERROR " + str(e)[:300]
    def state():
        try: return json.loads(call("State"))
        except Exception: return {}
    def js(expression, timeout=5):
        call("Eval", esc(expression))
        t0 = time.time()
        while time.time() - t0 < timeout:
            a = call("Answer")
            if a != "(waiting)": return a
            time.sleep(0.2)
        return "(no answer)"
    def out(n=6):
        vals = []
        for _ in range(n):
            vals.append(state().get("out", 0.0))
            time.sleep(0.25)
        return sum(vals) / max(1, len(vals))
    def loudness():
        """the loudest the TV's source puts out (its filter's output, before the master volume: test games are muted), with the TV and muted"""
        call("Quiet", False); time.sleep(0.6); on = out()
        call("Quiet", True); time.sleep(0.6); off = out()
        call("Quiet", False)
        return on, off
    def click_at(selector):
        r = js(CENTER + "(" + json.dumps(selector) + ")")
        try: x, y = json.loads(r)
        except Exception: return f"no {selector}: {r}"
        return call("Mouse", "click", x, y, 0, 1)

    b("set kaposztaleves.s.settings.god True")
    g = b("gpos"); x, z = float(g["gx"]), float(g["gz"])
    print(" spawn TV:", b("devspawn ujtv")); time.sleep(4)
    until(lambda: state().get("screen"), 30)
    print(" power:", call("Power", True))
    if not state().get("installed"):
        print(" accept:", call("Accept"))
        until(lambda: state().get("installed") or state().get("browser") == "Failed", 600, 2)
    print(" focus:", call("Focus")[:160])
    until(lambda: state().get("ready"), 90)
    print(" browser:", call("Browser_"))
    tv_dist = state().get("dist")

    # A. a tone
    call("Go", esc("https://example.com/"))
    until(lambda: (state().get("tabUrl") or "").startswith("https://example.com") and not state().get("loading"), 30)
    time.sleep(1)
    quiet_before = state().get("heard", 0)
    print(" tone:", js(TONE), click_at("body"))
    until(lambda: state().get("heard", 0) > 48000, 10)
    s0 = state(); time.sleep(8); s1 = state()
    print(f" source: playing {s1.get('playing')}, virtual {s1.get('virtual')}, listener volume {s1.get('listener')}, out {s1.get('out')}", flush=True)
    heard, played = s1["heard"] - s0["heard"], s1["played"] - s0["played"]
    verdict("A. the tone reaches the TV's buffer at about 48000 frames a second", quiet_before == 0 and 8 * 45000 < heard < 8 * 51000, f"before the click {quiet_before}, in 8 s {heard}")
    audible = (s1.get("listener") or 0) > 0
    verdict("A. the TV's source plays", s1.get("playing") and not s1.get("virtual") and s1["level"] > 0.4 and s1["underruns"] == s0["underruns"],
            f"playing {s1.get('playing')}, virtual {s1.get('virtual')}, level {s1['level']:.2f}, underruns {s1['underruns'] - s0['underruns']}")
    if not audible: print(" the game is muted (a test game): its output can't be measured, A's gaps and output and B are left out", flush=True)
    else:
        verdict("A. the TV plays it without gaps", 8 * 45000 < played < 8 * 51000 and s1["underruns"] - s0["underruns"] == 0 and s1["level"] > 0.4,
                f"played {played} in 8 s, underruns {s1['underruns'] - s0['underruns']}, level {s1['level']:.2f}, skipped {s1['skipped']}")
        on, off = loudness()
        verdict("A. the TV's source puts it out", on > 2 * off + 0.005, f"with {on:.4f}, muted {off:.4f}, TV {tv_dist:.1f} m away")

    # B. farther away
    if audible:
        b(f"tpground {x + 12} {z}"); time.sleep(3)
        on12, off12 = loudness()
        b(f"tpground {x + 23} {z}"); time.sleep(3)
        on23, off23 = loudness()
        verdict("B. quieter 12 m away", (on12 - off12) < (on - off) * 0.8 and on12 > off12, f"TV's share {on12 - off12:.4f} vs {on - off:.4f} near")
        verdict("B. not heard beyond 20 m", on23 - off23 < (on - off) * 0.05, f"with {on23:.4f}, muted {off23:.4f}")
        b(f"tpground {x} {z}"); time.sleep(3)

    # C. a real media file
    call("Focus")
    call("Go", esc("https://upload.wikimedia.org/wikipedia/commons/c/c8/Example.ogg"))
    until(lambda: "Example.ogg" in (state().get("tabUrl") or "") and not state().get("loading"), 30)
    time.sleep(2)
    print(" media play:", click_at("video,audio"))
    playing = js("(()=>{const m=document.querySelector('video,audio');return m?[m.paused,Math.round(m.currentTime*10)/10]:null})()")
    s0 = state(); time.sleep(3); s1 = state()
    playing2 = js("(()=>{const m=document.querySelector('video,audio');return m?[m.paused,Math.round(m.currentTime*10)/10,m.duration]:null})()")
    verdict("C. a media file's sound reaches the TV", s1["heard"] - s0["heard"] > 3 * 30000 and s1["level"] > 0.01,
            f"{s1['heard'] - s0['heard']} frames in 3 s, level {s1['level']:.3f}, media {playing} -> {playing2}")

    # D. YouTube
    if "--youtube" in sys.argv:
        call("Go", esc("https://www.youtube.com/watch?v=aqz-KE-bpKQ"))
        time.sleep(10)
        print(" consent:", js("(()=>{const b=[...document.querySelectorAll('button')].find(b=>/reject all/i.test(b.innerText||b.getAttribute('aria-label')||''));if(b){b.click();return 'rejected'}return 'none'})()"))
        time.sleep(5)
        print(" play:", click_at("#movie_player"))
        time.sleep(3)
        st = "(()=>{const v=document.querySelector('#movie_player video');if(!v)return 'no video';return [Math.round(v.currentTime),v.paused,v.muted,v.volume,(document.querySelector('.ytp-error')?.innerText||'').slice(0,60)].join(' ')})()"
        if "false" not in js(st):
            print(" play again:", click_at("#movie_player")); time.sleep(3)
        s0 = state(); time.sleep(5); s1 = state()
        print(" shot:", call("Shot", "tvsound_youtube"))
        verdict("D. YouTube's sound reaches the TV", s1["heard"] - s0["heard"] > 5 * 30000 and s1["level"] > 0.01,
                f"{s1['heard'] - s0['heard']} frames in 5 s, level {s1['level']:.3f}, video {js(st)}")

    # E. off
    call("Unfocus")
    print(" power:", call("Power", False))
    until(lambda: not state().get("sound"), 10)
    verdict("E. switched off: no sound source", not state().get("sound"), json.dumps({k: state().get(k) for k in ("on", "tab", "sound")}))
    game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
sys.exit(0 if VERDICTS and all(ok for _, ok in VERDICTS) else 1)
