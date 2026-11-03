using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp.Fixes
{
    /// Road pieces drawn from their baked shape. A road piece (roadscript) is a skinned mesh bent along the road by its
    /// bones: Place sets the bones once and BakeCollider bakes the bent shape (SkinnedMeshRenderer.BakeMesh) for its
    /// MeshCollider; nothing moves the bones afterwards. Yet the SkinnedMeshRenderer, with updateWhenOffscreen on, was
    /// skinned again every frame, on screen or not: 139 road pieces (1529 bones) around the player, SkinnedMeshRenderer-
    /// Manager.FinalizeUpdate ~21% of FinishFrameRendering's main-thread time (sampled stacks, 2026-11-03).
    /// Now the baked mesh the collider already uses is drawn by a MeshRenderer on a child at the road's own place, with
    /// the skinned renderer's materials and render settings, and the skinned renderer is switched off. Same shape, same
    /// materials, same place: the picture stays the same (checked by screenshot diff); only the per-frame skinning goes.
    /// Kept in step: a later BakeCollider (a Place again) re-points the child; the grass setting's material swap
    /// (GrassMaterialRemoveScript.Upd on the skinned renderer) is copied. A road under an LODGroup is left as it was.
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.BakedRoads.Enabled (off: new road pieces skin as before).
    public static class BakedRoads
    {
        public static bool Enabled = true;
        public static long Baked, Rebaked, SkippedLod, MaterialSyncs;
        private const string ChildName = "TLDRevamp baked road";

        [HarmonyPatch(typeof(roadscript), nameof(roadscript.BakeCollider))]
        private static class AfterBake
        {
            [HarmonyPostfix]
            private static void Postfix(roadscript __instance)
            {
                if (Enabled) Use(__instance);
            }
        }

        [HarmonyPatch(typeof(GrassMaterialRemoveScript), nameof(GrassMaterialRemoveScript.Upd))]
        private static class GrassMaterials
        {
            [HarmonyPostfix]
            private static void Postfix(GrassMaterialRemoveScript __instance)
            {
                if (!(__instance.R is SkinnedMeshRenderer smr) || !smr) return;
                var child = smr.transform.Find(ChildName);
                var mr = child ? child.GetComponent<MeshRenderer>() : null;
                if (!mr) return;
                mr.sharedMaterials = smr.sharedMaterials;
                MaterialSyncs++;
            }
        }

        private static void Use(roadscript rs)
        {
            var smr = rs.skinnedMeshRenderer;
            var mesh = rs.bakedMesh;
            if (!smr || !mesh) return;
            if (smr.GetComponentInParent<LODGroup>()) { SkippedLod++; return; }
            var t = smr.transform;
            var child = t.Find(ChildName);
            MeshRenderer mr;
            if (child)
            {
                child.GetComponent<MeshFilter>().sharedMesh = mesh;
                mr = child.GetComponent<MeshRenderer>();
                Rebaked++;
            }
            else
            {
                var go = new GameObject(ChildName) { layer = smr.gameObject.layer };
                child = go.transform;
                child.SetParent(t, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                mr = go.AddComponent<MeshRenderer>();
                Baked++;
            }
            // BakeMesh(mesh) bakes in the renderer's space at scale 1: the child undoes the renderer's scale
            Vector3 s = t.lossyScale;
            child.localScale = new Vector3(s.x != 0 ? 1f / s.x : 1f, s.y != 0 ? 1f / s.y : 1f, s.z != 0 ? 1f / s.z : 1f);
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
            mr.sharedMaterials = smr.sharedMaterials;
            mr.shadowCastingMode = smr.shadowCastingMode;
            mr.receiveShadows = smr.receiveShadows;
            mr.lightProbeUsage = smr.lightProbeUsage;
            mr.reflectionProbeUsage = smr.reflectionProbeUsage;
            mr.probeAnchor = smr.probeAnchor;   // none: both sample light probes at the centre of the same bounds
            mr.motionVectorGenerationMode = smr.motionVectorGenerationMode;
            mr.allowOcclusionWhenDynamic = smr.allowOcclusionWhenDynamic;
            mr.renderingLayerMask = smr.renderingLayerMask;
            mr.sortingLayerID = smr.sortingLayerID;
            mr.sortingOrder = smr.sortingOrder;
            mr.enabled = true;
            smr.enabled = false;
        }

        /// Test (FrameProbe -frameprobe-roadswap): every road piece already in the world drawn from its baked mesh now.
        public static int ApplyAll()
        {
            int n = 0;
            foreach (var rs in Object.FindObjectsOfType<roadscript>()) { Use(rs); n++; }
            return n;
        }

        internal static string Stats() =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"baked\":" + Baked + ",\"rebaked\":" + Rebaked + ",\"skippedLod\":" + SkippedLod + ",\"materialSyncs\":" + MaterialSyncs + "}";
    }
}
