using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Players' corpses (the game's PlayerRagdol) once the death is over: they stay (Entities.Death, DeathCorpseStays)
    /// and look like the player who died. The dying machine's corpse copies its player's outfit (fpscontroller.SpawnRagdol
    /// → CopyFrom), and the corpse's record carries it (save_outfit, CorpseOutfitSaved), so every other machine's copy is
    /// dressed the same.
    public static partial class Entities
    {
        /// A/B: an outfit's record holds the outfit. The game's own save_outfit.Save keeps only the id (its MP sends the
        /// outfit in a message of its own, syncScript.SendOutfit), so a copy made from the record - every other machine's
        /// copy of a corpse, a late joiner's, a corpse in a loaded save - wore no outfit at all: character 0, every
        /// segment off, black (two copies, 2026-11-06). The record's fields are the game's own (selectedCharacter,
        /// segments), so the bytes have the same shape and an older peer reads them too. Off: as the game does.
        public static bool CorpseOutfitSaved = true;
        public static long OutfitsSaved, OutfitLoadsRefused;

        [HarmonyLib.HarmonyPatch(typeof(save_outfit), nameof(save_outfit.Save), new[] { typeof(playermodeloutfitscript), typeof(uint) })]
        private static class OutfitIntoRecord
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(save_outfit __instance, playermodeloutfitscript _script)
            {
                if (!CorpseOutfitSaved || _script == null || _script.characters == null) return;
                int sel = _script.selectedCharacter;
                if (sel < 0 || sel >= _script.characters.Length || _script.characters[sel] == null) return;
                var segs = _script.characters[sel].ch.segments;
                __instance.selectedCharacter = sel;
                __instance.segments = new save_outfitsegment[segs.Length];
                for (int i = 0; i < segs.Length; i++)
                    __instance.segments[i] = new save_outfitsegment { enabled = segs[i].BEnabled, r = segs[i].Ccolor.r, g = segs[i].Ccolor.g, b = segs[i].Ccolor.b };
                OutfitsSaved++;
            }
        }

        /// A record's outfit names a character this body has (it comes from another machine, or an old save): the game's
        /// Load indexes the characters with it unchecked.
        [HarmonyLib.HarmonyPatch(typeof(save_outfit), nameof(save_outfit.Load), new[] { typeof(playermodeloutfitscript) })]
        private static class OutfitRecordChecked
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(save_outfit __instance, playermodeloutfitscript _script)
            {
                if (_script == null || _script.characters == null) return false;
                int sel = __instance.selectedCharacter;
                if (sel >= 0 && sel < _script.characters.Length && _script.characters[sel] != null) return true;
                OutfitLoadsRefused++;
                return false;
            }
        }

        /// A/B: a corpse loaded from a record (another machine's copy, a late joiner's, one in a loaded save) is drawn
        /// whole. The game's save_outfit.Load ends in Refresh → SetDistance, which gives the model the first-person cut
        /// (_Dist: what is within that distance of the camera is not drawn, so the player doesn't see the inside of their
        /// own head); only the local player's own body and its ready corpse should have it. And it keeps its eyes: they
        /// are the game's bulging eyes (bulgingeyesScript, the zoom gag), whose script switches its eyes off on a body
        /// that is neither the local player nor a remote player. The dying machine's corpse was made under its player,
        /// so it keeps them; a copy lost them (two copies, 2026-11-06: the copy drew 16 of the owner's 18, no EyeL/EyeR).
        /// Off: as the game does.
        public static bool CorpseWhole = true;
        public static long CorpseCutsUndone;

        [HarmonyLib.HarmonyPatch(typeof(save_outfit), nameof(save_outfit.Load), new[] { typeof(playermodeloutfitscript) })]
        private static class CorpseLoadedWhole
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(playermodeloutfitscript _script)
            {
                if (!CorpseWhole || _script == null || OwnModel(_script)) return;
                try { _script.ReSetDistance(); EyesKept(_script); CorpseCutsUndone++; }
                catch (System.Exception e) { Plugin.Log.LogWarning("corpse outfit: " + e.Message); }
            }
        }

        /// A body's eyes at rest and kept (the dying machine's corpse has them so: its script was stopped by RagdolReset's
        /// DisableControl at their resting size).
        private static void EyesKept(playermodeloutfitscript o)
        {
            if (o.GetComponent<tosaveitemscript>() == null) return;
            foreach (var eyes in o.GetComponentsInChildren<bulgingeyesScript>(true))
            {
                eyes.current = 0f; eyes.disabled = false; eyes.Refresh();
                eyes.disabled = true; eyes.enabled = false;
                eyes.gameObject.SetActive(true);   // which eyes are drawn is the outfit's (Refresh: the chosen character's)
            }
        }

        /// The local player's own model or the corpse it keeps ready for its next death (both are cut on purpose).
        private static bool OwnModel(playermodeloutfitscript o)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return false;
            return o == pl.outfit || pl.spawnedRagdol != null && o.gameObject == pl.spawnedRagdol && o.transform.IsChildOf(pl.transform);
        }

        /// Diagnostics (bridge `mp corpses`): every shared player corpse here, with what a test compares between machines:
        /// its outfit (the character, each segment on/off and its colour), whether it is drawn (renderers on, the
        /// largest first-person cut), whether it is solid (colliders on), and where its limbs are.
        public static string CorpseList()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.Root.name.IndexOf("PlayerRagdol", System.StringComparison.Ordinal) < 0) continue;
                var go = e.Root.gameObject;
                var o = go.GetComponent<playermodeloutfitscript>();
                string outfit = "null";
                float cut = 0f;
                if (o != null)
                {
                    o.GetData(out int sel, out bool[] en, out Color[] col);
                    var sb = new System.Text.StringBuilder();
                    sb.Append("{\"character\":").Append(sel).Append(",\"on\":\"");
                    foreach (bool b in en) sb.Append(b ? '1' : '0');
                    sb.Append("\",\"colors\":\"");
                    foreach (var c in col) sb.Append(ColorUtility.ToHtmlStringRGB(c)).Append(' ');
                    sb.Append("\",\"hash\":").Append(PlayerLook.OutfitHash(sel, en, col)).Append('}');
                    outfit = sb.ToString();
                    foreach (var ch in o.characters)
                        if (ch != null)
                            foreach (var r in ch.ch.renderers)
                                if (r != null)
                                    foreach (var m in r.sharedMaterials)
                                        if (m != null && m.HasProperty("_Dist")) cut = Mathf.Max(cut, m.GetFloat("_Dist"));
                }
                int rOn = 0, rAll = 0, cOn = 0, cAll = 0;
                var drawn = new List<string>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { rAll++; if (r.enabled && r.gameObject.activeInHierarchy) { rOn++; drawn.Add(Json.Str(r.name)); } }
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) { cAll++; if (c.enabled && c.gameObject.activeInHierarchy) cOn++; }
                var ls = Limbs(e);
                Vector3d mid = mainscript.GlobalFromUnityPos(e.Root.transform.position);
                if (ls != null)
                {
                    var sum = Vector3.zero; int n = 0;
                    foreach (var l in ls) if (l != null) { sum += l.position; n++; }
                    if (n > 0) mid = mainscript.GlobalFromUnityPos(sum / n);
                }
                rows.Add("{\"net\":" + e.NetId + ",\"saveId\":" + e.RootId + ",\"owner\":" + e.OwnerId + ",\"proxy\":" + (e.Proxy ? "true" : "false") +
                         ",\"outfit\":" + outfit + ",\"cut\":" + cut.ToString("F3", ic) + ",\"renderers\":[" + rOn + "," + rAll + "],\"drawn\":[" + string.Join(",", drawn) + "],\"colliders\":[" + cOn + "," + cAll + "]" +
                         ",\"limbs\":" + (ls != null ? ls.Length : 0) + ",\"centre\":[" + mid.x.ToString("F3", ic) + "," + mid.y.ToString("F3", ic) + "," + mid.z.ToString("F3", ic) + "]}");
            }
            rows.Sort(System.StringComparer.Ordinal);
            return "{\"count\":" + rows.Count + ",\"kept\":" + DeathCorpsesKept + ",\"released\":" + DeathCorpsesReleased + ",\"cutsUndone\":" + CorpseCutsUndone + ",\"outfitsSaved\":" + OutfitsSaved + ",\"outfitLoadsRefused\":" + OutfitLoadsRefused +
                   ",\"corpses\":[" + string.Join(",", rows) + "]}";
        }

        /// Test hook (bridge `mp outfit [random]`): the local player's outfit, or a new random one first (the game's own
        /// SetRandom, so the test's dead player looks unlike the default one on the other machine).
        public static string OutfitTest(bool random)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.outfit == null) return "{\"error\":\"no player\"}";
            if (random) { pl.outfit.SetRandom(_FullRandom: true); pl.outfit.Refresh(fromMultiplayer: false); }
            pl.outfit.GetData(out int sel, out bool[] en, out Color[] col);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"character\":").Append(sel).Append(",\"on\":\"");
            foreach (bool b in en) sb.Append(b ? '1' : '0');
            sb.Append("\",\"colors\":\"");
            foreach (var c in col) sb.Append(ColorUtility.ToHtmlStringRGB(c)).Append(' ');
            sb.Append("\",\"hash\":").Append(PlayerLook.OutfitHash(sel, en, col)).Append('}');
            return sb.ToString();
        }
    }
}
