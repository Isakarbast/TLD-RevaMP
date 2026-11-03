using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;

namespace TLDRevamp.Net
{
    /// Every machine in a session generates the world itself (terrain, roads, buildings, scenery) from the host's seed,
    /// map and terrain mode; nothing of it is sent. For that to give everyone the same world:
    ///  - the game must be the same: its code (Plugin.GameBuild) and the data files the world is made from (GameData).
    ///    Before, a different game build was only announced ("the missing diner", 2026-10-09/10);
    ///  - mods that patch the world generators must be the same (GenPatches): RevaMP's own are the same by BuildCheck;
    ///  - RevaMP's fixes that make generation independent of the order an area was reached in (SceneryTileSync,
    ///    TerrainSync, ObjGenFast that they run on, and main road only's MainRoadOnlyRoads) are on for everyone while
    ///    in a session, whatever the player set (SessionBegins/SessionEnds). A host whose world was generated with
    ///    them off has everything around it checked again (SceneryTileSync.RefreshAll).
    /// The host refuses a joiner that differs (WorldCheckRules.Refusal). A/B (bridge `set`): Refuse, Force.
    public static class WorldCheck
    {
        public static bool Refuse = true, Force = true;
        public static long Refused, Forced, Refreshes;

        /// The world's data files, hashed once per change (a few hundred MB: on a worker thread at start, cached by
        /// name, size and time in BepInEx/cache).
        private static Task<string> _data;
        public static string GameData
        {
            get
            {
                if (_data == null) Start();
                try { return _data.Wait(30000) ? _data.Result : "unknown"; }
                catch { return "unknown"; }
            }
        }

        public static void Start()
        {
            if (_data != null) return;
            string dir;
            try { dir = UnityEngine.Application.dataPath; } catch { dir = null; }
            _data = Task.Run(() => HashData(dir));
        }

        /// The files a Unity player is built from: scenes (level*), managers and asset bundles with their streamed data.
        /// Not news, configs or logs, which change without the game changing.
        internal static IEnumerable<string> DataFiles(string dataDir)
        {
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dataDir))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith("level", StringComparison.Ordinal) || n.StartsWith("globalgamemanagers", StringComparison.Ordinal) ||
                    n.StartsWith("sharedassets", StringComparison.Ordinal) || n.StartsWith("resources.", StringComparison.Ordinal))
                    files.Add(f);
            }
            string code = Path.Combine(Path.Combine(dataDir, "Managed"), "Assembly-CSharp.dll");
            if (File.Exists(code)) files.Add(code);
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static string HashData(string dataDir)
        {
            try
            {
                if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) return "unknown";
                var files = DataFiles(dataDir).ToList();
                var stamp = new StringBuilder();
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    stamp.Append(Path.GetFileName(f)).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
                string cache = null;
                try { cache = Path.Combine(BepInEx.Paths.CachePath, "tldrevamp-gamedata.txt"); } catch { }
                if (cache != null && File.Exists(cache))
                {
                    var lines = File.ReadAllText(cache);
                    int cut = lines.LastIndexOf("hash ", StringComparison.Ordinal);
                    if (cut > 0 && lines.Substring(0, cut) == stamp.ToString()) return lines.Substring(cut + 5).Trim();
                }
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                string hash;
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[1 << 20];
                    foreach (var f in files)
                    {
                        var name = Encoding.UTF8.GetBytes(Path.GetFileName(f) + "\n");
                        sha.TransformBlock(name, 0, name.Length, null, 0);
                        using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                        {
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    hash = BitConverter.ToString(sha.Hash).Replace("-", "").Substring(0, 12).ToLowerInvariant();
                }
                Plugin.Log.LogInfo($"world check: game data {hash} ({files.Count} files, {t0.ElapsedMilliseconds} ms)");
                if (cache != null) try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllText(cache, stamp + "hash " + hash); } catch { }
                return hash;
            }
            catch (Exception e) { Plugin.Log.LogWarning("world check: game data not hashed: " + e.Message); return "unknown"; }
        }

        /// The world generators' types. A patch on any of their methods (or a nested type's) can change the world.
        private static readonly HashSet<string> GenTypes = new HashSet<string>
        {
            "roadGenScript", "poiGenScript", "objGenScript", "terrainGenScript", "terrainGenSettings", "mapSettingScript",
            "NoiseS3D", "Noises", "randomterrain", "terrainscript",
        };

        private static bool IsGen(Type t)
        {
            for (; t != null; t = t.DeclaringType) if (GenTypes.Contains(t.Name)) return true;
            return false;
        }

        /// Other mods' Harmony patches on the world generators: "owner|Type.Method|kind", sorted.
        public static string GenPatches()
        {
            var lines = new List<string>();
            try
            {
                foreach (var m in Harmony.GetAllPatchedMethods())
                {
                    if (m == null || !IsGen(m.DeclaringType)) continue;
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    string where = m.DeclaringType.Name + "." + m.Name;
                    void Add(IEnumerable<Patch> ps, string kind)
                    {
                        foreach (var p in ps) if (!WorldCheckRules.Own(p.owner)) lines.Add(p.owner + "|" + where + "|" + kind);
                    }
                    Add(info.Prefixes, "prefix"); Add(info.Postfixes, "postfix"); Add(info.Transpilers, "transpiler"); Add(info.Finalizers, "finalizer");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("world check: patches not listed: " + e.Message); }
            return WorldCheckRules.Join(lines);
        }

        /// Hello: what this machine generates the world from.
        public static void Write(NetWriter w) => WorldCheckRules.Write(w, GameData, GenPatches());

        /// The host: why this joiner is refused, or null (and null with Refuse off: it is announced at Ready instead).
        public static string Check(string joinerBuild, string joinerData, string joinerPatches, bool comparePatches)
        {
            string mine = GenPatches();
            if (!comparePatches) joinerPatches = mine;   // the API mods differ: the host offers them first (OfferMods), the patches are compared on the next Hello
            string why = WorldCheckRules.Refusal(Plugin.GameBuild, joinerBuild, GameData, joinerData, mine, joinerPatches);
            if (why == null) return null;
            Plugin.Log.LogWarning($"[anomaly] world differs: game {joinerBuild}/{Plugin.GameBuild}, data {joinerData}/{GameData}, generator patches '{joinerPatches.Replace('\n', ';')}' / '{GenPatches().Replace('\n', ';')}'" + (Refuse ? " - refused" : " - let in (Refuse off)"));
            if (!Refuse) return null;
            Refused++;
            return why;
        }

        // ---- the order-independence fixes, on for the session

        private static bool _forced, _scenery, _terrain, _objGen, _mainRoad, _waitLinks, _lineAhead;
        public static bool Forcing => _forced;

        /// A session starts (Mp.Host; a joiner from Welcome, before the host's world loads).
        public static void SessionBegins(bool host)
        {
            if (!Force || _forced) return;
            _scenery = Fixes.SceneryTileSync.Enabled; _terrain = Fixes.TerrainSync.Enabled; _objGen = Fixes.ObjGenFast.Enabled;
            _mainRoad = WorldGen.MainRoadOnlyRoads.Enabled; _waitLinks = WorldGen.MainRoadOnlyRoads.PoisWaitForLinks; _lineAhead = WorldGen.MainRoadOnlyRoads.LineAhead;
            bool wasOff = !_scenery || !_terrain || !_objGen;
            Fixes.SceneryTileSync.Enabled = Fixes.TerrainSync.Enabled = Fixes.ObjGenFast.Enabled = true;
            WorldGen.MainRoadOnlyRoads.Enabled = WorldGen.MainRoadOnlyRoads.PoisWaitForLinks = WorldGen.MainRoadOnlyRoads.LineAhead = true;
            _forced = true;
            if (wasOff || !_mainRoad || !_waitLinks || !_lineAhead)
            {
                Forced++;
                Plugin.Log.LogInfo($"world check: generation fixes on for the session (scenery {_scenery}, terrain {_terrain}, scenery generation {_objGen}, main road {_mainRoad}/{_waitLinks}/{_lineAhead})");
            }
            // the host's world exists already and was generated with them off: check everything generated against what is there now
            if (host && wasOff)
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map != null) { Fixes.SceneryTileSync.RefreshAll(map); Refreshes++; }
            }
        }

        /// The session is over (Mp.Stop): the player's own settings again.
        public static void SessionEnds()
        {
            if (!_forced) return;
            _forced = false;
            Fixes.SceneryTileSync.Enabled = _scenery; Fixes.TerrainSync.Enabled = _terrain; Fixes.ObjGenFast.Enabled = _objGen;
            WorldGen.MainRoadOnlyRoads.Enabled = _mainRoad; WorldGen.MainRoadOnlyRoads.PoisWaitForLinks = _waitLinks; WorldGen.MainRoadOnlyRoads.LineAhead = _lineAhead;
        }

        /// The settings rows: in a session they stay on, and what the player picks counts after the session.
        public static void SetScenery(bool on)
        {
            if (_forced) { _scenery = _terrain = on; return; }
            Fixes.SceneryTileSync.Enabled = Fixes.TerrainSync.Enabled = on;
        }

        public static void SetObjGen(bool on)
        {
            if (_forced) { _objGen = on; return; }
            Fixes.ObjGenFast.Enabled = on;
        }

        /// Bridge `worldcheck`.
        public static string Status() =>
            "{\"gameBuild\":" + Json.Str(Plugin.GameBuild) + ",\"gameData\":" + Json.Str(GameData) + ",\"genPatches\":" + Json.Str(GenPatches()) +
            ",\"refuse\":" + (Refuse ? "true" : "false") + ",\"force\":" + (Force ? "true" : "false") + ",\"forcing\":" + (_forced ? "true" : "false") +
            ",\"refused\":" + Refused + ",\"forced\":" + Forced + ",\"refreshes\":" + Refreshes +
            ",\"scenery\":" + B(Fixes.SceneryTileSync.Enabled) + ",\"terrain\":" + B(Fixes.TerrainSync.Enabled) + ",\"objGen\":" + B(Fixes.ObjGenFast.Enabled) +
            ",\"mainRoad\":[" + B(WorldGen.MainRoadOnlyRoads.Enabled) + "," + B(WorldGen.MainRoadOnlyRoads.PoisWaitForLinks) + "," + B(WorldGen.MainRoadOnlyRoads.LineAhead) + "]}";

        private static string B(bool b) => b ? "true" : "false";
    }
}
