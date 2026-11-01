using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// DetachSync, written and read in one place. The server's release of a trailer wrote its rotation the short way while
        /// the reader takes four floats: the read ran off the end and the message applied nowhere.
        internal static void WriteDetachSync(NetWriter w, uint net, uint epoch, Vector3d pos, Quaternion rot)
        {
            w.U8(DetachSync); w.U32(net); w.U32(epoch);
            w.F64(pos.x); w.F64(pos.y); w.F64(pos.z); w.F32(rot.x); w.F32(rot.y); w.F32(rot.z); w.F32(rot.w);
        }

        internal static bool ReadDetachSync(NetReader r, out uint net, out Vector3d pos, out Quaternion rot)
        {
            net = r.U32(); r.U32();
            pos = new Vector3d(r.F64(), r.F64(), r.F64()); rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            return !r.Bad;
        }
    }
}
