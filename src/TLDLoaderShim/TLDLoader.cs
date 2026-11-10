using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
#if TLD_UNITY
using UnityEngine;
#endif

// Enough of TLDLoader's API for TLDLoader mods to run under BepInEx on the beta (where TLDLoader itself doesn't work):
// the Mod base class and its lifecycle, its logger, the config and assets folders, and LoadAssets (asset bundles,
// textures, OBJ meshes). TLDRevamp's ModHost finds the mods, creates them and calls their lifecycle: DbLoad/dbLoad once
// the item database exists (mods with LoadInDB), OnMenuLoad in the menu, OnLoad once a game is loaded.
// Written from TLDLoader's public API as mods use it (the workshop's 205 mods, 2026-11-10), not from its code.
namespace TLDLoader
{
    public interface ILogger
    {
        void LogInfo(string msg);
        void LogWarning(string msg);
        void LogError(string msg);
    }

    /// The host's logger (BepInEx), handed over as three callbacks — TLDRevamp only reaches this assembly by reflection.
    public sealed class HostLogger : ILogger
    {
        private readonly Action<string> _info, _warn, _error;
        public HostLogger(Action<string> info, Action<string> warn, Action<string> error) { _info = info; _warn = warn; _error = error; }
        public void LogInfo(string msg) => _info?.Invoke(msg);
        public void LogWarning(string msg) => _warn?.Invoke(msg);
        public void LogError(string msg) => _error?.Invoke(msg);
    }

    public abstract class Mod
    {
        public virtual string ID => GetType().Name;
        public virtual string Name => ID;
        public virtual string Version => "";
        public virtual string Author => "";
        public virtual bool LoadInMenu => false;
        /// The mod changes or adds items in the item database: DbLoad/dbLoad run once it exists, before a world loads.
        public virtual bool LoadInDB => false;
        /// The mod has files in Assets/<ID> next to the mods (LoadAssets, GetModAssetsFolder).
        public virtual bool UseAssetsFolder => false;
        public virtual float ConfigScrollHeight => 0f;

        /// Set by the host before any lifecycle call.
        public ILogger Logger { get; set; }

        public virtual void DbLoad() { }
        public virtual void dbLoad() { }   // TLDLoader's older name: both are called
        public virtual void OnMenuLoad() { }
        public virtual void OnLoad() { }
        public virtual void OnUnload() { }
        public virtual void Config() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void OnGUI() { }
    }

    public static class ModLoader
    {
        /// Set by the host: BepInEx/config/TLDLoaderMods.
        public static string ConfigRoot = "";
        /// Set by the host: the folder the mods are loaded from (BepInEx/TLDLoaderMods); their assets are in its Assets/<ID>.
        public static string ModsFolder = "";

        public static string GetModConfigFolder(Mod mod)
        {
            string dir = Path.Combine(ConfigRoot, mod.ID);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetModAssetsFolder(Mod mod) => Path.Combine(Path.Combine(ModsFolder, "Assets"), mod.ID);
    }

#if TLD_UNITY
    /// A mod's own files from its assets folder (ModLoader.GetModAssetsFolder).
    public static class LoadAssets
    {
        private static string File(Mod mod, string fileName, string what)
        {
            string path = Path.Combine(ModLoader.GetModAssetsFolder(mod), fileName);
            if (!System.IO.File.Exists(path)) throw new FileNotFoundException(what + ": " + mod.ID + " has no file " + fileName + " in " + ModLoader.GetModAssetsFolder(mod), path);
            return path;
        }

        public static AssetBundle LoadBundle(Mod mod, string bundleName) =>
            AssetBundle.LoadFromMemory(System.IO.File.ReadAllBytes(File(mod, bundleName, "LoadBundle")));

        /// .dds (DXT1/DXT5), .tga (uncompressed 24/32-bit), .png and .jpg.
        public static Texture2D LoadTexture(Mod mod, string fileName, bool normalMap = false)
        {
            string path = File(mod, fileName, "LoadTexture");
            byte[] data = System.IO.File.ReadAllBytes(path);
            Texture2D t;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".dds": t = Dds(data); break;
                case ".tga": t = Tga(data); break;
                case ".png": case ".jpg": case ".jpeg":
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true, normalMap);
                    if (!ImageConversion.LoadImage(t, data)) throw new InvalidDataException("LoadTexture: " + fileName + " is not a readable image");
                    break;
                default: throw new NotSupportedException("LoadTexture: " + fileName + ": .dds, .tga, .png or .jpg");
            }
            t.name = Path.GetFileName(path);
            return t;
        }

        private static Texture2D Dds(byte[] d)
        {
            // DDS: "DDS " magic, a 124-byte header; the pixel format's FourCC at byte 84, height at 12, width at 16, data after 128
            if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || BitConverter.ToInt32(d, 4) != 124) throw new InvalidDataException("LoadTexture: not a DDS file");
            string fourCc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            var format = fourCc == "DXT1" ? TextureFormat.DXT1 : fourCc == "DXT5" ? TextureFormat.DXT5 : throw new NotSupportedException("LoadTexture: DDS " + fourCc + " (DXT1 or DXT5 only)");
            int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(d, 28));
            var t = new Texture2D(w, h, format, mips > 1);
            var raw = new byte[d.Length - 128];
            Buffer.BlockCopy(d, 128, raw, 0, raw.Length);
            t.LoadRawTextureData(raw);
            t.Apply(false);
            return t;
        }

        private static Texture2D Tga(byte[] d)
        {
            // TGA: an 18-byte header (image type at 2, width at 12, height at 14, bits per pixel at 16, descriptor at 17),
            // then the image id and pixels (BGR or BGRA), bottom-up unless descriptor bit 5 says top-down
            if (d.Length < 18 || d[2] != 2) throw new NotSupportedException("LoadTexture: TGA must be uncompressed true colour");
            int w = BitConverter.ToUInt16(d, 12), h = BitConverter.ToUInt16(d, 14), bpp = d[16] / 8, at = 18 + d[0];
            if (bpp != 3 && bpp != 4) throw new NotSupportedException("LoadTexture: TGA must be 24 or 32 bit");
            bool topDown = (d[17] & 0x20) != 0;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++, at += bpp)
                    px[(topDown ? h - 1 - y : y) * w + x] = new Color32(d[at + 2], d[at + 1], d[at], bpp == 4 ? d[at + 3] : (byte)255);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// A game object with the .obj file's mesh (MeshFilter, MeshRenderer, optionally a collider and a rigidbody).
        public static GameObject LoadObj(Mod mod, string fileName, string name, bool collider = true, bool rigidbody = false)
        {
            var mesh = LoadOBJMesh(mod, fileName);
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().mesh = mesh;
            go.AddComponent<MeshRenderer>();
            if (rigidbody) go.AddComponent<Rigidbody>();
            if (collider) { var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = mesh; mc.convex = rigidbody; }
            return go;
        }

        /// A Wavefront .obj's first object: positions, texture coordinates, normals; polygons fanned into triangles.
        public static Mesh LoadOBJMesh(Mod mod, string fileName)
        {
            string path = File(mod, fileName, "LoadOBJ");
            if (Path.GetExtension(path).ToLowerInvariant() != ".obj") throw new NotSupportedException("LoadOBJ: .obj files only");
            var mesh = ObjMesh.Parse(System.IO.File.ReadAllLines(path));
            mesh.name = Path.GetFileNameWithoutExtension(path);
            return mesh;
        }
    }

    /// The .obj reader behind LoadOBJMesh (its own class so the unit test runs it without the game).
    public static class ObjMesh
    {
        public static Mesh Parse(IEnumerable<string> lines)
        {
            Build(lines, out var verts, out var uvs, out var normals, out var tris);
            var m = new Mesh();
            if (verts.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            if (uvs.Count == verts.Count) m.SetUVs(0, uvs);
            if (normals.Count == verts.Count) m.SetNormals(normals); else m.RecalculateNormals();
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// One output vertex per distinct "v/vt/vn" corner; faces with more than three corners become a fan.
        public static void Build(IEnumerable<string> lines, out List<Vector3> verts, out List<Vector2> uvs, out List<Vector3> normals, out List<int> tris)
        {
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
            verts = new List<Vector3>(); uvs = new List<Vector2>(); normals = new List<Vector3>(); tris = new List<int>();
            var corners = new Dictionary<string, int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    // .obj is right-handed, Unity left-handed: mirror x (and reverse the winding below)
                    case "v": v.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "vt": vt.Add(new Vector2(float.Parse(p[1], ci), p.Length > 2 ? float.Parse(p[2], ci) : 0f)); break;
                    case "vn": vn.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "f":
                        var face = new List<int>();
                        for (int i = 1; i < p.Length; i++)
                        {
                            if (!corners.TryGetValue(p[i], out int idx))
                            {
                                var s = p[i].Split('/');
                                verts.Add(v[Index(s[0], v.Count)]);
                                if (s.Length > 1 && s[1].Length > 0) uvs.Add(vt[Index(s[1], vt.Count)]);
                                if (s.Length > 2 && s[2].Length > 0) normals.Add(vn[Index(s[2], vn.Count)]);
                                idx = verts.Count - 1;
                                corners[p[i]] = idx;
                            }
                            face.Add(idx);
                        }
                        for (int i = 1; i + 1 < face.Count; i++) { tris.Add(face[0]); tris.Add(face[i + 1]); tris.Add(face[i]); }
                        break;
                }
            }
        }

        private static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;   // 1-based; negative counts back from the latest
        }
    }
#endif
}
