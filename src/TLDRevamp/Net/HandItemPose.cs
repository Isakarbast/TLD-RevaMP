using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class HandItemPose
    {
        internal const float BlendS = 0.12f;
        internal static void Relative(Transform hand, Vector3 pos, Quaternion rot, out Vector3 lp, out Quaternion lq)
        { lp = hand.InverseTransformPoint(pos); lq = Quaternion.Inverse(hand.rotation) * rot; }
        internal static void World(Transform hand, Vector3 lp, Quaternion lq, out Vector3 pos, out Quaternion rot)
        { pos = hand.TransformPoint(lp); rot = hand.rotation * lq; }
        /// From the interpolated pose towards the hand pose, by at most maxM metres and maxDeg degrees.
        internal static void Pull(Vector3d from, Quaternion fromRot, Vector3d to, Quaternion toRot, float maxM, float maxDeg, out Vector3d pos, out Quaternion rot)
        {
            var d = to - from; double m = Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            pos = m <= maxM || m <= 0 ? to : from + d * (maxM / m);
            float a = Quaternion.Angle(fromRot, toRot);
            rot = a <= maxDeg ? toRot : Quaternion.Slerp(fromRot, toRot, maxDeg / a);
        }
    }

    /// Preserve the last drawn pose on a source switch, then remove its error against the moving destination.
    /// Global positions keep a floating-origin shift from looking like a new grip or a world-sized glide.
    internal sealed class HandItemGlide
    {
        internal bool HasShown;
        internal object Source;
        internal Vector3d Shown;
        internal Quaternion Rotation;
        private Vector3d error;
        private Quaternion turn = Quaternion.identity;
        private double since = double.NegativeInfinity;
        internal bool Blending(double now) => now - since < HandItemPose.BlendS;
        internal void Seed(Vector3d pos, Quaternion rot) { HasShown = true; Shown = pos; Rotation = rot; }
        internal void Reset() { HasShown = false; Source = null; since = double.NegativeInfinity; error = default; turn = Quaternion.identity; }
        internal void Apply(object source, double now, Vector3d pos, Quaternion rot, out Vector3d shown, out Quaternion rotation)
        {
            if (!ReferenceEquals(Source, source))
            {
                error = HasShown ? Shown - pos : default;
                turn = HasShown ? Rotation * Quaternion.Inverse(rot) : Quaternion.identity;
                since = now; Source = source;
            }
            float k = (float)Math.Max(0, Math.Min(1, (now - since) / HandItemPose.BlendS));
            shown = pos + error * (1 - k);
            rotation = Quaternion.Slerp(turn, Quaternion.identity, k) * rot;
            Seed(shown, rotation);
        }
    }
}
