using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// A rotation on the wire: the three smallest of its four parts, 7 bytes.
        private const float RotScale = 32767f / 0.70710678f;

        private static void WriteRot(NetWriter w, Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, ww = q.w;
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y), az = Mathf.Abs(z), aw = Mathf.Abs(ww);
            int big = 3; float m = aw;
            if (ax > m) { big = 0; m = ax; }
            if (ay > m) { big = 1; m = ay; }
            if (az > m) { big = 2; }
            float s = (big == 0 ? x : big == 1 ? y : big == 2 ? z : ww) < 0f ? -1f : 1f;   // q and -q are the same rotation
            w.U8((byte)big);
            if (big != 0) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(x * s * RotScale), -32767, 32767));
            if (big != 1) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(y * s * RotScale), -32767, 32767));
            if (big != 2) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(z * s * RotScale), -32767, 32767));
            if (big != 3) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(ww * s * RotScale), -32767, 32767));
        }

        private static Quaternion ReadRot(NetReader r)
        {
            int big = r.U8() & 3;
            float a = (short)r.U16() / RotScale, b = (short)r.U16() / RotScale, c = (short)r.U16() / RotScale;
            float d = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (big)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }
    }
}
