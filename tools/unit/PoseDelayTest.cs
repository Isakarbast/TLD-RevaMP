using System;
using TLDRevamp.Net;
using UnityEngine;

/// The copy's display delay: gaps of a nearly still object are no send interval (a barrel let go after being held still).
static class PoseDelayTest
{
    static float Run(bool movingOnly, out float afterMoving)
    {
        PoseInterpolator.GapsMovingOnly = PoseInterpolator.TypicalInterval = PoseInterpolator.IgnorePauses = movingOnly;
        var ip = new PoseInterpolator(); double t = 0; var pos = new Vector3d(0, 1, 0);
        // held nearly still: a state each time it crept 1 cm, every 0.2-0.45 s, for 6 s
        double[] gaps = { .2, .35, .45, .3, .25, .4, .45, .2, .3, .35, .4, .25, .3, .45, .35, .4, .2, .3 };
        foreach (var g in gaps) { t += g; pos = pos + new Vector3d(.01, 0, 0); ip.Add(new PoseInterpolator.Sample { T = t, Pos = pos, Rot = Quaternion.identity, Vel = new Vector3(.03f, 0, 0) }, t + .01); }
        float atDrop = ip.DelayMs;
        // let go: falling, a state every 50 ms
        for (int i = 0; i < 6; i++) { t += .05; pos = pos + new Vector3d(0, -.15 * (i + 1), 0); ip.Add(new PoseInterpolator.Sample { T = t, Pos = pos, Rot = Quaternion.identity, Vel = new Vector3(0, -3f * (i + 1), 0) }, t + .01); }
        afterMoving = ip.DelayMs;
        PoseInterpolator.GapsMovingOnly = PoseInterpolator.TypicalInterval = PoseInterpolator.IgnorePauses = true;
        return atDrop;
    }

    internal static void Run(Action<bool, string> check)
    {
        float oldDrop = Run(false, out float oldFall);
        float newDrop = Run(true, out float newFall);
        check(oldDrop > 400f, "before: an item held nearly still had a display delay of " + oldDrop.ToString("F0") + " ms when let go");
        check(newDrop < 80f && newFall < 80f, "now: the delay stays at one send interval + margin (" + newDrop.ToString("F0") + " / " + newFall.ToString("F0") + " ms)");
        // lifted in steps: 20 Hz while moving, still for 0.15-0.35 s in between (it sends nothing then), then let go
        foreach (bool ignore in new[] { false, true })
        {
            PoseInterpolator.IgnorePauses = PoseInterpolator.TypicalInterval = ignore;
            var lift = new PoseInterpolator(); double lt = 0; var lpos = new Vector3d(0, 0, 0);
            double[] pauses = { .3, .15, .35, .25, .3 };
            foreach (var pause in pauses)
            {
                for (int i = 0; i < 4; i++) { lt += .05; lpos = lpos + new Vector3d(0, .1, 0); lift.Add(new PoseInterpolator.Sample { T = lt, Pos = lpos, Rot = Quaternion.identity, Vel = new Vector3(0, 2, 0) }, lt + .01); }
                lt += pause;
            }
            for (int i = 0; i < 3; i++) { lt += .05; lpos = lpos + new Vector3d(0, -.2, 0); lift.Add(new PoseInterpolator.Sample { T = lt, Pos = lpos, Rot = Quaternion.identity, Vel = new Vector3(0, -4, 0) }, lt + .01); }
            if (ignore) check(lift.DelayMs < 90f, "a pause before moving again is no send interval: let go after lifting in steps, " + lift.DelayMs.ToString("F0") + " ms behind");
            else check(lift.DelayMs > 300f, "before: the pauses made it " + lift.DelayMs.ToString("F0") + " ms");
        }
        PoseInterpolator.IgnorePauses = PoseInterpolator.TypicalInterval = true;
        // 20 Hz from a 63 FPS sender (40 and 60 ms in turn): the typical gap, 50 ms, is the interval
        var even = new PoseInterpolator(); double et = 0;
        for (int i = 0; i < 20; i++) { et += i % 2 == 0 ? .04 : .06; even.Add(new PoseInterpolator.Sample { T = et, Pos = new Vector3d(et * 3, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(3, 0, 0) }, et + .01); }
        check(Math.Abs(even.Interval - .05f) < .002f, "40/60 ms gaps: interval " + (even.Interval * 1000).ToString("F0") + " ms");
        // a held item at 40 Hz: half the buffer
        var fast = new PoseInterpolator(); double ft = 0;
        for (int i = 0; i < 20; i++) { ft += .025; fast.Add(new PoseInterpolator.Sample { T = ft, Pos = new Vector3d(ft * 3, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(3, 0, 0) }, ft + .01); }
        check(fast.DelayMs < 45f, "40 Hz sender: " + fast.DelayMs.ToString("F0") + " ms behind");
        // a bump wears off within about a second (20 states), not five
        var bump = new PoseInterpolator(); double bt = 0;
        for (int i = 0; i < 20; i++) { bt += .05; bump.Add(new PoseInterpolator.Sample { T = bt, Pos = new Vector3d(bt * 3, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(3, 0, 0) }, bt + .01 + (i == 10 ? .2 : 0)); }
        for (int i = 0; i < 20; i++) { bt += .05; bump.Add(new PoseInterpolator.Sample { T = bt, Pos = new Vector3d(bt * 3, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(3, 0, 0) }, bt + .01); }
        check(bump.DelayMs < 90f, "a 200 ms late state is forgotten within a second: " + bump.DelayMs.ToString("F0") + " ms");
        // let go: the display runs fast to its own delay behind the newest state, then on at real time
        var go = new PoseInterpolator(); double gt = 0, now = 0; PoseInterpolator.RecoverShare = 0f;   // the catch-up alone
        // 72 states; four 250 ms late early on: once they leave the 60-state jitter window, the delay is still on its way down
        for (int i = 0; i < 72; i++) { gt += .05; go.Add(new PoseInterpolator.Sample { T = gt, Pos = new Vector3d(0, 1, 0), Rot = Quaternion.identity, Vel = Vector3.zero }, gt + .01 + (i >= 5 && i <= 8 ? .25 : 0)); }
        now = gt + .01;
        for (int f = 0; f < 10; f++) { now += .016; go.Evaluate(now, .016f, out _, out _); }
        double lagBefore = (now - .01) - go.RenderTime;
        go.CatchUp(now);
        for (int f = 0; f < 10; f++) { now += .016; go.Evaluate(now, .016f, out _, out _); }
        double shownLag = (now - .01) - go.RenderTime;   // owner time now (latency .01) minus the time shown
        PoseInterpolator.RecoverShare = .15f;
        check(lagBefore > .15 && shownLag < .1, "let go: " + (lagBefore * 1000).ToString("F0") + " ms behind its owner, after 0.16 s of catching up " + (shownLag * 1000).ToString("F0") + " ms");
        // a far car thinned to 4 Hz while driving: that gap is the interval, the delay covers it
        var car = new PoseInterpolator(); double t = 0;
        for (int i = 0; i < 12; i++) { t += .25; car.Add(new PoseInterpolator.Sample { T = t, Pos = new Vector3d(t * 15, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(15, 0, 0) }, t + .02); }
        check(car.DelayMs >= 250f, "a car thinned to 4 Hz still runs a quarter second behind (" + car.DelayMs.ToString("F0") + " ms)");
        // and one that moves without sending its velocity (distance alone)
        var slide = new PoseInterpolator(); t = 0;
        for (int i = 0; i < 12; i++) { t += .2; slide.Add(new PoseInterpolator.Sample { T = t, Pos = new Vector3d(t * 2, 0, 0), Rot = Quaternion.identity, Vel = Vector3.zero }, t + .01); }
        check(slide.DelayMs >= 200f, "gaps of an object that covered 2 m/s count without a sent velocity (" + slide.DelayMs.ToString("F0") + " ms)");
    }
}
