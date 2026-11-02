// By hand: what the race needs to behave and not only exist. The generator leaves out whatever this file declares.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace UnityEngine
{
    public partial struct Mathf
    {
        public static float Abs(float f) => Math.Abs(f); public static int Abs(int v) => Math.Abs(v);
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < 1e-5f;
        public static float Asin(float f) => (float)Math.Asin(f); public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f); public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
        public static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v; public static float Clamp01(float v) => Clamp(v, 0, 1);
        public static float Cos(float f) => (float)Math.Cos(f); public static float Exp(float p) => (float)Math.Exp(p);
        public static float InverseLerp(float a, float b, float v) => a == b ? 0 : Clamp01((v - a) / (b - a));
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t); public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float LerpAngle(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Max(float a, float b) => a > b ? a : b; public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b; public static int Min(int a, int b) => a < b ? a : b;
        public static float MoveTowards(float c, float t, float d) => Math.Abs(t - c) <= d ? t : c + Math.Sign(t - c) * d;
        public static float Pow(float f, float p) => (float)Math.Pow(f, p); public static float Repeat(float t, float l) => t - (float)Math.Floor(t / l) * l;
        public static float Round(float f) => (float)Math.Round(f); public static int RoundToInt(float f) => (int)Math.Round(f);
        public static float Sign(float f) => f >= 0 ? 1 : -1; public static float Sin(float f) => (float)Math.Sin(f); public static float Sqrt(float f) => (float)Math.Sqrt(f); public static float Tan(float f) => (float)Math.Tan(f);
    }
    public partial struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float Angle(Vector3 a, Vector3 b) { float d = Mathf.Sqrt(a.sqrMagnitude * b.sqrMagnitude); return d < 1e-15f ? 0 : (float)Math.Acos(Mathf.Clamp(Dot(a, b) / d, -1, 1)) * Mathf.Rad2Deg; }
        public static Vector3 ClampMagnitude(Vector3 v, float m) => v.sqrMagnitude > m * m ? v.normalized * m : v;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude; public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public bool Equals(Vector3 o) => x == o.x && y == o.y && z == o.z; public override bool Equals(object o) => o is Vector3 v && Equals(v); public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return a + (b - a) * t; }
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        public void Normalize() { this = normalized; }
        public static Vector3 Project(Vector3 v, Vector3 n) { float s = Dot(n, n); return s < 1e-15f ? zero : n * (Dot(v, n) / s); } public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) => v - Project(v, n);
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        public static float SignedAngle(Vector3 a, Vector3 b, Vector3 ax) => Angle(a, b) * Math.Sign(Dot(ax, Cross(a, b)));
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})"; public string ToString(string f) => ToString();
        public static Vector3 back => new Vector3(0, 0, -1); public static Vector3 down => new Vector3(0, -1, 0); public static Vector3 forward => new Vector3(0, 0, 1); public static Vector3 left => new Vector3(-1, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1); public static Vector3 right => new Vector3(1, 0, 0); public static Vector3 up => new Vector3(0, 1, 0); public static Vector3 zero => default;
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z); public float sqrMagnitude => x * x + y * y + z * z; public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z); public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d); public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-10f; public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public float this[int i] { get => i == 0 ? x : i == 1 ? y : z; set { if (i == 0) x = value; else if (i == 1) y = value; else z = value; } }
    }
    public partial struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public static float Angle(Quaternion a, Quaternion b) { float d = Math.Min(Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w), 1f); return d > 0.999999f ? 0 : (float)Math.Acos(d) * 2f * Mathf.Rad2Deg; }
        public static Quaternion AngleAxis(float angle, Vector3 ax) { ax = ax.normalized; float h = angle * Mathf.Deg2Rad / 2, s = Mathf.Sin(h); return new Quaternion(ax.x * s, ax.y * s, ax.z * s, Mathf.Cos(h)); }
        public static Quaternion Euler(float x, float y, float z) => AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);
        public static Quaternion Euler(Vector3 e) => Euler(e.x, e.y, e.z);
        public static Quaternion FromToRotation(Vector3 a, Vector3 b) { var c = Vector3.Cross(a, b); return c.sqrMagnitude < 1e-10f ? identity : AngleAxis(Vector3.Angle(a, b), c); }
        public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
        public static Quaternion Lerp(Quaternion a, Quaternion b, float t) => Slerp(a, b, t);
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) { t = Mathf.Clamp01(t); float s = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0 ? -1 : 1; var q = new Quaternion(a.x + (s * b.x - a.x) * t, a.y + (s * b.y - a.y) * t, a.z + (s * b.z - a.z) * t, a.w + (s * b.w - a.w) * t); float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m); }
        public static Quaternion LookRotation(Vector3 f, Vector3 up) => FromToRotation(Vector3.forward, f); public static Quaternion LookRotation(Vector3 f) => LookRotation(f, Vector3.up);
        public void ToAngleAxis(out float angle, out Vector3 axis) { angle = 2 * (float)Math.Acos(Mathf.Clamp(w, -1, 1)) * Mathf.Rad2Deg; float s = Mathf.Sqrt(1 - w * w); axis = s < 1e-4f ? Vector3.right : new Vector3(x / s, y / s, z / s); }
        public Vector3 eulerAngles { get => default; set => this = Euler(value); }
        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z, a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        public static Vector3 operator *(Quaternion q, Vector3 v) { var u = new Vector3(q.x, q.y, q.z); var t = Vector3.Cross(u, v) * 2; return v + t * q.w + Vector3.Cross(u, t); }
    }

    public partial class Object
    {
        internal bool _dead; static int _ids; int _id = ++_ids;
        internal static readonly List<Object> All = new List<Object>();
        public Object() { All.Add(this); }
        public int GetInstanceID() => _id; public string name { get; set; }
        public static bool operator ==(Object a, Object b) { bool an = ReferenceEquals(a, null) || a._dead, bn = ReferenceEquals(b, null) || b._dead; return an && bn || !an && !bn && ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object o) => ReferenceEquals(this, o) || o == null && _dead; public override int GetHashCode() => _id;
        public static void Destroy(Object o) { if (ReferenceEquals(o, null)) return; o.Die(); }
        public static void Destroy(Object o, float t) => Destroy(o); public static void DestroyImmediate(Object o) => Destroy(o);
        internal virtual void Die() { if (_dead) return; _dead = true; All.Remove(this); }
        public static T[] FindObjectsOfType<T>() where T : Object => All.OfType<T>().ToArray(); public static T[] FindObjectsOfType<T>(bool i) where T : Object => FindObjectsOfType<T>();
        public static T FindObjectOfType<T>() where T : Object => All.OfType<T>().FirstOrDefault(); public static T FindObjectOfType<T>(bool i) where T : Object => FindObjectOfType<T>();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode m) where T : Object => FindObjectsOfType<T>(); public static Object[] FindObjectsOfType(Type t) => All.Where(t.IsInstanceOfType).ToArray();
    }
    public partial class Component
    {
        public new string name { get => gameObject?.name; set { if (gameObject != null) gameObject.name = value; } } public GameObject gameObject { get; set; } public Transform transform => this is Transform t ? t : gameObject?.transform;
        internal override void Die() { base.Die(); gameObject?.Parts.Remove(this); }
        public T GetComponent<T>() => gameObject.GetComponent<T>(); public T GetComponentInChildren<T>() => gameObject.GetComponentInChildren<T>(); public T GetComponentInChildren<T>(bool i) => gameObject.GetComponentInChildren<T>(i);
        public T GetComponentInParent<T>() => gameObject.GetComponentInParent<T>(); public T GetComponentInParent<T>(bool i) => gameObject.GetComponentInParent<T>();
        public T[] GetComponents<T>() => gameObject.GetComponents<T>(); public T[] GetComponentsInChildren<T>() => gameObject.GetComponentsInChildren<T>(); public T[] GetComponentsInChildren<T>(bool includeInactive) => gameObject.GetComponentsInChildren<T>(includeInactive);
        public void GetComponentsInChildren<T>(bool i, List<T> r) { r.Clear(); r.AddRange(gameObject.GetComponentsInChildren<T>(i)); }
        public bool CompareTag(string tag) => false;
    }
    public partial class GameObject
    {
        internal readonly List<Component> Parts = new List<Component>(); public Transform transform { get; private set; } public bool activeSelf { get; private set; } = true;
        public GameObject() { transform = new Transform { gameObject = this }; Parts.Add(transform); }
        public GameObject(string n) : this() { name = n; }
        public GameObject(string n, params Type[] c) : this() { name = n; }
        internal override void Die() { if (_dead) return; base.Die(); foreach (var c in Parts.ToArray()) c.Die(); if (transform != null) foreach (var ch in transform.Kids.ToArray()) ch.gameObject.Die(); }
        public T AddComponent<T>() where T : Component { var c = (T)Activator.CreateInstance(typeof(T)); c.gameObject = this; Parts.Add(c); return c; }
        public T GetComponent<T>() => Parts.OfType<T>().FirstOrDefault(); public T[] GetComponents<T>() => Parts.OfType<T>().ToArray();
        public T[] GetComponentsInChildren<T>() => GetComponentsInChildren<T>(false);
        public T[] GetComponentsInChildren<T>(bool includeInactive) { var r = new List<T>(); void W(GameObject g) { if (!includeInactive && !g.activeInHierarchy) return; r.AddRange(g.Parts.OfType<T>()); foreach (var k in g.transform.Kids) W(k.gameObject); } W(this); return r.ToArray(); }
        public T GetComponentInChildren<T>() => GetComponentsInChildren<T>(false).FirstOrDefault(); public T GetComponentInChildren<T>(bool i) => GetComponentsInChildren<T>(i).FirstOrDefault();
        public Component GetComponentInChildren(Type t, bool i) => GetComponentsInChildren<Component>(i).FirstOrDefault(t.IsInstanceOfType);
        public T GetComponentInParent<T>() { for (var t = transform; t != null; t = t.parent) { var c = t.gameObject.GetComponent<T>(); if (c != null) return c; } return default; }
        public void SetActive(bool v) { activeSelf = v; }
        public static GameObject CreatePrimitive(PrimitiveType t) => new GameObject("primitive");
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
    }
    public partial class Transform
    {
        internal readonly List<Transform> Kids = new List<Transform>(); Transform _p;
        public Vector3 localPosition { get; set; } public Quaternion localRotation { get; set; } = Quaternion.identity; public Vector3 localScale { get; set; } = Vector3.one;
        public Transform parent { get => _p == null ? null : _p; set => SetParent(value, true); }
        public void SetParent(Transform p) => SetParent(p, true);
        public void SetParent(Transform parent, bool worldPositionStays) { var p = parent; var stay = worldPositionStays; var wp = position; var wr = rotation; if (!ReferenceEquals(_p, null)) _p.Kids.Remove(this); _p = p; if (!ReferenceEquals(p, null)) p.Kids.Add(this); if (stay) { position = wp; rotation = wr; } }
        public Transform root { get { var t = this; while (t.parent != null) t = t.parent; return t; } }
        public Vector3 position { get => parent == null ? localPosition : parent.TransformPoint(localPosition); set => localPosition = parent == null ? value : parent.InverseTransformPoint(value); }
        public Quaternion rotation { get => parent == null ? localRotation : parent.rotation * localRotation; set => localRotation = parent == null ? value : Quaternion.Inverse(parent.rotation) * value; }
        public Vector3 TransformPoint(Vector3 v) => position + rotation * Vector3.Scale(v, localScale); public Vector3 TransformDirection(Vector3 v) => rotation * v;
        public Vector3 InverseTransformPoint(Vector3 v) { var l = Quaternion.Inverse(rotation) * (v - position); return new Vector3(l.x / localScale.x, l.y / localScale.y, l.z / localScale.z); }
        public Vector3 InverseTransformDirection(Vector3 v) => Quaternion.Inverse(rotation) * v; public Vector3 InverseTransformVector(Vector3 v) => Quaternion.Inverse(rotation) * v;
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
        public int childCount => Kids.Count; public Transform GetChild(int i) => Kids[i]; public Transform Find(string n) => Kids.FirstOrDefault(k => k.gameObject.name == n);
        public bool IsChildOf(Transform p) { for (var t = this; t != null; t = t.parent) if (t == p) return true; return false; }
        public Vector3 forward { get => rotation * Vector3.forward; set => rotation = Quaternion.LookRotation(value); } public Vector3 up { get => rotation * Vector3.up; set { } } public Vector3 right { get => rotation * Vector3.right; set { } }
    }
}

namespace BepInEx.Logging
{
    public partial class ManualLogSource
    {
        public static Action<string> Out;
        public ManualLogSource(string n) { }
        public void LogInfo(object o) { Out?.Invoke("info: " + o); } public void LogWarning(object o) { Out?.Invoke("warn: " + o); } public void LogError(object o) { Out?.Invoke("ERROR: " + o); } public void LogFatal(object o) { Out?.Invoke("FATAL: " + o); } public void LogDebug(object o) { } public void LogMessage(object o) { Out?.Invoke("msg: " + o); }
    }
}

namespace Steamworks
{
    public static partial class SteamUser { public static ulong Me; public static CSteamID GetSteamID() { CSteamID c = default; c.m_SteamID = Me; return c; } }
    public partial struct SteamNetworkingIPAddr { public bool ParseString(string a) => true; }
    public partial class Callback<T>
    {
        public static DispatchDelegate Last;
        public static Callback<T> Create(DispatchDelegate d) { Last += d; return new Callback<T>(); }
    }
    public partial struct SteamNetworkingMessage_t { public static void Release(IntPtr p) { Marshal.FreeHGlobal(Marshal.ReadIntPtr(p, (int)Marshal.OffsetOf<SteamNetworkingMessage_t>("m_pData"))); Marshal.FreeHGlobal(p); } }
    /// The fake network's end on this machine. The driver holds the other end, through core types only: machines are in different load contexts.
    public static partial class SteamNetworkingSockets
    {
        public static Func<int> OnConnect; public static Func<uint, byte[], int, int> OnSend; public static Action<uint> OnAccept, OnClose;
        public static readonly Dictionary<uint, Queue<byte[]>> In = new Dictionary<uint, Queue<byte[]>>();
        public static void Arrive(uint conn, byte[] b) { if (!In.TryGetValue(conn, out var q)) In[conn] = q = new Queue<byte[]>(); q.Enqueue(b); }
        public static void Status(uint conn, int state, int old, bool viaListen)
        {
            var s = new SteamNetConnectionStatusChangedCallback_t(); s.m_hConn.m_HSteamNetConnection = conn; s.m_eOldState = (ESteamNetworkingConnectionState)old; s.m_info.m_eState = (ESteamNetworkingConnectionState)state;
            if (viaListen) s.m_info.m_hListenSocket.m_HSteamListenSocket = 1;
            Callback<SteamNetConnectionStatusChangedCallback_t>.Last?.Invoke(s);
        }
        public static HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr a, int n, SteamNetworkingConfigValue_t[] o) { HSteamListenSocket l = default; l.m_HSteamListenSocket = 1; return l; }
        public static HSteamNetConnection ConnectByIPAddress(ref SteamNetworkingIPAddr a, int n, SteamNetworkingConfigValue_t[] o) { HSteamNetConnection c = default; c.m_HSteamNetConnection = (uint)OnConnect(); return c; }
        public static EResult AcceptConnection(HSteamNetConnection c) { OnAccept?.Invoke(c.m_HSteamNetConnection); return EResult.k_EResultOK; }
        public static bool CloseConnection(HSteamNetConnection c, int r, string d, bool l) { OnClose?.Invoke(c.m_HSteamNetConnection); return true; }
        public static bool CloseListenSocket(HSteamListenSocket s) => true;
        public static EResult SendMessageToConnection(HSteamNetConnection c, IntPtr p, uint n, int flags, out long num)
        {
            num = 0; var b = new byte[n]; Marshal.Copy(p, b, 0, (int)n);
            return (EResult)OnSend(c.m_HSteamNetConnection, b, flags);
        }
        public static int ReceiveMessagesOnConnection(HSteamNetConnection c, IntPtr[] msgs, int max)
        {
            if (!In.TryGetValue(c.m_HSteamNetConnection, out var q)) return 0;
            int k = 0;
            for (; k < max && q.Count > 0; k++)
            {
                var b = q.Dequeue(); var data = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, data, b.Length);
                var m = Marshal.AllocHGlobal(Marshal.SizeOf<SteamNetworkingMessage_t>());
                Marshal.WriteInt32(m, (int)Marshal.OffsetOf<SteamNetworkingMessage_t>("m_cbSize"), b.Length); Marshal.WriteIntPtr(m, (int)Marshal.OffsetOf<SteamNetworkingMessage_t>("m_pData"), data);
                msgs[k] = m;
            }
            return k;
        }
    }
}

namespace TLDRevamp.Net
{
    /// In place of the real SteamCompat: that one reaches into Steamworks by reflection.
    public static class SteamCompat
    {
        public struct QuickStatus { public int State, Ping; public float QualityLocal, QualityRemote, OutPacketsPerSec, OutBytesPerSec, InPacketsPerSec, InBytesPerSec; public int SendRateBytesPerSecond, PendingUnreliable, PendingReliable, SentUnackedReliable; public long QueueTimeUsec; }
        public static void Ensure() { }
        public static QuickStatus Status(Steamworks.HSteamNetConnection c) => new QuickStatus { State = 3 };
        public static string Pops() => ""; public static string Route(Steamworks.HSteamNetConnection c) => "sim";
        public const int IdentitySteamId = 16; public static string Info = "sim"; public static bool Applied = true;
        public static Func<uint, ulong> PeerId; public static ulong RemoteSteamId(Steamworks.HSteamNetConnection c, out int type) { type = IdentitySteamId; return PeerId != null ? PeerId(c.m_HSteamNetConnection) : 0; }
    }
}


namespace UnityEngine
{
public partial struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public Vector3d(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public override bool Equals(object o) => o is Vector3d v && v.x == x && v.y == y && v.z == z; public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode(); public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    public double sqrMagnitude => x * x + y * y + z * z; public double magnitude => Math.Sqrt(sqrMagnitude); public static Vector3d zero => default;
    public static Vector3d operator +(Vector3d a, UnityEngine.Vector3 b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z); public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z); public static Vector3d operator *(Vector3d a, double d) => new Vector3d(a.x * d, a.y * d, a.z * d);
    public static explicit operator UnityEngine.Vector3(Vector3d v) => new UnityEngine.Vector3((float)v.x, (float)v.y, (float)v.z);
}
}
public partial struct vector3 { public float x, y, z; public vector3(UnityEngine.Vector3 v) { x = v.x; y = v.y; z = v.z; } public UnityEngine.Vector3 Load() => new UnityEngine.Vector3(x, y, z); }
public partial struct posRotd { public UnityEngine.Vector3d pos; public vector3 rot; public posRotd(UnityEngine.Vector3d p, UnityEngine.Vector3 r) { pos = p; rot = new vector3(r); } }
public partial class itemDataClass
{
    public itemDataClass() { foreach (var f in GetType().GetFields()) if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)) f.SetValue(this, Activator.CreateInstance(f.FieldType)); }
}
public partial class mainscript
{
    public static UnityEngine.Vector3 UnityPosFromGlobal(UnityEngine.Vector3d p) => (UnityEngine.Vector3)p;
    public static UnityEngine.Vector3d GlobalFromUnityPos(UnityEngine.Vector3 p) => new UnityEngine.Vector3d(p.x, p.y, p.z);
}
public partial class savedatascript
{
    uint _key = 1000;
    public uint GetNewKey() => ++_key;
    public static void SaveToDictionary(tosaveitemscript t, itemDataClass d)
    {
        d.items.Add(new save_item { id = t.idInSave, prefabID = t.id, transform = new posRotd(mainscript.GlobalFromUnityPos(t.transform.position), default) });
    }
    public UnityEngine.GameObject SpawnItem(save_item v)
    {
        var go = new UnityEngine.GameObject("item" + v.prefabID);
        var it = go.AddComponent<tosaveitemscript>(); it.id = v.prefabID; it.idInSave = v.id;
        foreach (var f in it.GetType().GetFields()) if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)) f.SetValue(it, Activator.CreateInstance(f.FieldType));
        go.AddComponent<UnityEngine.Rigidbody>(); it.RB = go.GetComponent<UnityEngine.Rigidbody>();
        var p = go.AddComponent<pickupable>(); p.tosave = it; p.RB = it.RB; it.P = p;
        if (v.prefabID == 8) go.AddComponent<breakablescript>();
        go.transform.position = (UnityEngine.Vector3)v.transform.pos;
        items[v.id] = it; it.loaded = true; it.started = true;
        return go;
    }
    public tosaveitemscript InitAfterSpawn(UnityEngine.GameObject go, uint key) => items[key];
}

public partial class fpscontroller
{
    public void Drop() { if (pickedUp != null) { pickedUp.pickedUp = false; pickedUp = null; } }
    public void Pickup(pickupable p, UnityEngine.Vector3 point) { Pickup(p); }
    public void Pickup(pickupable p) { p.Pickup(); pickedUp = p; }   // the game's order: the mod's prefix runs before the hand has it
}
public partial class pickupable { public void Pickup() { Harm.Fire(typeof(pickupable), "Pickup", this); pickedUp = true; } }
public partial class tosaveitemscript { internal override void Die() { Harm.Fire(typeof(tosaveitemscript), "OnDestroy", this); base.Die(); } }
public partial class breakablescript { public void Break() { Harm.Around(typeof(breakablescript), "Break", this, () => { destroyed = true; }); } }
/// Harmony's part: the mod's patches of a game method, run when the fake game's own method runs. Only for the methods above.
public static class Harm
{
    static System.Collections.Generic.Dictionary<string, List<System.Reflection.MethodInfo>> _p;
    public static void Fire(Type t, string m, object inst)
    {
        if (_p == null)
        {
            _p = new Dictionary<string, List<System.Reflection.MethodInfo>>();
            foreach (var c in typeof(TLDRevamp.Net.Mp).Assembly.GetTypes())
                foreach (var a in c.GetCustomAttributesData())
                {
                    if (a.AttributeType.Name != "HarmonyPatch" || a.ConstructorArguments.Count < 2 || !(a.ConstructorArguments[0].Value is Type pt) || !(a.ConstructorArguments[1].Value is string pm)) continue;
                    foreach (var me in c.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                        if (me.GetCustomAttributesData().Any(x => x.AttributeType.Name == "HarmonyPrefix" || x.AttributeType.Name == "HarmonyPostfix"))
                        { var k = pt.Name + "." + pm; if (!_p.TryGetValue(k, out var l)) _p[k] = l = new List<System.Reflection.MethodInfo>(); l.Add(me); }
                }
        }
        Around(t, m, inst, null);
    }
    public static void Around(Type t, string m, object inst, Action body)
    {
        if (_p == null) { Fire(typeof(Harm), "none", null); }
        _p.TryGetValue(t.Name + "." + m, out var ms); ms = ms ?? new List<System.Reflection.MethodInfo>();
        object state = null;
        object[] Args(System.Reflection.MethodInfo me) => me.GetParameters().Select(q => q.Name == "__instance" ? inst : q.Name == "__state" ? state : q.ParameterType.IsValueType ? Activator.CreateInstance(q.ParameterType) : null).ToArray();
        foreach (var me in ms.Where(x => x.GetCustomAttributesData().Any(y => y.AttributeType.Name == "HarmonyPrefix")))
        {
            var a = Args(me); me.Invoke(null, a);
            var pars = me.GetParameters(); for (int i = 0; i < pars.Length; i++) if (pars[i].Name == "__state") state = a[i];
        }
        body?.Invoke();
        foreach (var me in ms.Where(x => x.GetCustomAttributesData().Any(y => y.AttributeType.Name == "HarmonyPostfix"))) me.Invoke(null, Args(me));
    }
}

namespace HarmonyLib
{
    public static partial class AccessTools
    {
        /// A private field of the game: the fake game has none, so it lives beside the instance.
        public static FieldRef<T, F> FieldRefAccess<T, F>(string fieldName) where T : class
        {
            var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<F>>();
            return inst => ref table.GetOrCreateValue(inst).Value;
        }
    }
}
