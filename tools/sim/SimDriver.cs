using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Runs inside a machine's load context. Everything crosses as core types.
namespace TLDRevamp.Net
{
    public static class SimSide
    {
        public static List<string> Log = new List<string>();
        const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static void Init(ulong me, Func<uint, ulong> peer, Func<int> connect, Func<uint, byte[], int, int> send, Action<uint> accept, Action<uint> close)
        {
            BepInEx.Logging.ManualLogSource.Out = s => Log.Add(s);
            Plugin.Log = new BepInEx.Logging.ManualLogSource("sim");
            var go = new UnityEngine.GameObject("world");
            savedatascript.s = go.AddComponent<savedatascript>(); savedatascript.s.items = new Dictionary<uint, tosaveitemscript>(); savedatascript.s.data = new savedata();
            DataFromMenuScript.s = go.AddComponent<DataFromMenuScript>(); menuhandler.s = go.AddComponent<menuhandler>();
            mainscript.s = go.AddComponent<mainscript>(); mainscript.s.player = new UnityEngine.GameObject("player").AddComponent<fpscontroller>();
            Steamworks.SteamUser.Me = me; SteamCompat.PeerId = peer; SteamManager.Initialized = true;
            Steamworks.SteamNetworkingSockets.OnConnect = connect; Steamworks.SteamNetworkingSockets.OnSend = send;
            Steamworks.SteamNetworkingSockets.OnAccept = accept; Steamworks.SteamNetworkingSockets.OnClose = close; Steamworks.SteamNetworkingSockets.In.Clear();
        }
        public static void Arrive(uint c, byte[] b) => Steamworks.SteamNetworkingSockets.Arrive(c, b);
        public static void Status(uint c, int s, int o, bool l) => Steamworks.SteamNetworkingSockets.Status(c, s, o, l);
        public static void Tick(float t, int frame)
        {
            UnityEngine.Time.time = t; UnityEngine.Time.realtimeSinceStartup = t; UnityEngine.Time.unscaledTime = t; UnityEngine.Time.frameCount = frame; UnityEngine.Time.realtimeSinceStartupAsDouble = t; UnityEngine.Time.unscaledTimeAsDouble = t;
            UnityEngine.Time.deltaTime = 0.02f; UnityEngine.Time.fixedDeltaTime = 0.02f; UnityEngine.Time.unscaledDeltaTime = 0.02f;
            if (JoinLoading.Visible && Mp.Client != null && Mp.Client.MyId >= 0) JoinLoading.End("sim");
            Mp.Tick();
        }
        static object St(string typeField)
        {
            var parts = typeField.Split('.'); var t = typeof(Mp).Assembly.GetTypes().First(x => x.Name == parts[0]);
            return t.GetField(parts[1], Any);
        }
        public static string[] DrainLog() { var l = Log.ToArray(); Log.Clear(); return l; }
        static System.Collections.IDictionary Dict(string n) => (System.Collections.IDictionary)typeof(Entities).GetField(n, Any).GetValue(null);
        static tosaveitemscript _held;
        /// Rows: net, owner, epoch, proxy, placed here, held here. A server's rows: the same from its table.
        public static string Ents()
        {
            var o = new List<string>();
            foreach (System.Collections.DictionaryEntry d in Dict("ByNet"))
            {
                var e = (Entities.Ent)d.Value;
                var at = e.Root != null ? e.Root.transform.position : default;
                o.Add($"{d.Key},{e.OwnerId},{e.Epoch},{(e.Proxy ? 1 : 0)},{(e.Root != null ? 1 : 0)},{(e.Root != null && mainscript.s.player.pickedUp == e.Root.P ? 1 : 0)},{at.x:F2},{at.z:F2}");
            }
            return string.Join(";", o.OrderBy(x => x));
        }
        public static string Srv()
        {
            var o = new List<string>();
            foreach (System.Collections.DictionaryEntry d in Dict("Server"))
            {
                var t = d.Value.GetType();
                object G(string n) => t.GetField(n, Any) != null ? t.GetField(n, Any).GetValue(d.Value) : t.GetProperty(n, Any).GetValue(d.Value);
                o.Add($"{d.Key},{G("OwnerId")},{G("Epoch")},{G("Held")}");
            }
            return string.Join(";", o.OrderBy(x => x));
        }
        /// host, join, spawn x z, lose net, set Type.Field v, get Type.Field, grab net, drop
        public static string Do(string cmd)
        {
            var a = cmd.Split(' ');
            switch (a[0])
            {
                case "host": return Mp.Host(27070);
                case "join": return Mp.Join("127.0.0.1:27070");
                case "stop": return Mp.Stop();
                case "me": return Mp.Client != null ? Mp.Client.MyId.ToString() : "0";
                case "ready": return Mp.Server != null ? "1" : Mp.Client != null && Mp.Client.Ready ? "1" : "0";
                case "spawn":
                    {
                        var sd = savedatascript.s; uint k = sd.GetNewKey();
                        sd.SpawnItem(new save_item { id = k, prefabID = 7, transform = new posRotd(new UnityEngine.Vector3d(float.Parse(a[1]), 0, float.Parse(a[2])), default) });
                        return Entities.ShareItem(sd.items[k], null);
                    }
                case "spawnb":
                    {
                        var sd = savedatascript.s; uint k = sd.GetNewKey();
                        sd.SpawnItem(new save_item { id = k, prefabID = 8, transform = new posRotd(new UnityEngine.Vector3d(float.Parse(a[1]), 0, float.Parse(a[2])), default) });
                        return Entities.ShareItem(sd.items[k], null);
                    }
                case "break": return Entities.BreakEnt(uint.Parse(a[1]), 100000);
                case "breakstate": return Entities.BreakState(uint.Parse(a[1]));
                case "remove":
                    {
                        var e = (Entities.Ent)Dict("ByNet")[uint.Parse(a[1])];
                        if (e == null || e.Root == null || e.Proxy) return "no";
                        UnityEngine.Object.Destroy(e.Root.gameObject); return "removed";
                    }
                case "move":
                    {
                        var e = (Entities.Ent)Dict("ByNet")[uint.Parse(a[1])];
                        if (e == null || e.Root == null || e.Proxy) return "no";
                        e.Root.transform.position += new UnityEngine.Vector3(float.Parse(a[2]), 0, float.Parse(a[3])); return "moved";
                    }
                case "ghosts":
                    {
                        var known = new HashSet<tosaveitemscript>();
                        foreach (System.Collections.DictionaryEntry d in Dict("ByNet")) foreach (var it in ((Entities.Ent)d.Value).Items) if (it != null) known.Add(it);
                        return string.Join(",", savedatascript.s.items.Values.Where(i => i != null && !known.Contains(i)).Select(i => i.idInSave));
                    }
                case "switches":
                    {
                        // public static bools on by default that aren't state: only the mod's own net classes
                        var o = new List<string>();
                        foreach (var t in typeof(Mp).Assembly.GetTypes().Where(x => x.Namespace == "TLDRevamp.Net" && !x.IsGenericType && !x.Name.StartsWith("<") && !x.Name.StartsWith("Sim")))
                            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public))
                                if (f.FieldType == typeof(bool) && !f.IsLiteral && !f.IsInitOnly && (bool)f.GetValue(null)) o.Add(t.Name + "." + f.Name);
                        return string.Join(" ", o);
                    }
                case "joined": return Mp.Server != null || Mp.Client != null ? "1" : "0";
                case "lose": return Entities.TestLoseCopy(uint.Parse(a[1]));
                case "set":
                    {
                        var f = (FieldInfo)St(a[1]); f.SetValue(null, Convert.ChangeType(a[2], f.FieldType)); return "ok";
                    }
                case "get": return Convert.ToString(((FieldInfo)St(a[1])).GetValue(null));
                case "grab":
                    {
                        var e = (Entities.Ent)Dict("ByNet")[uint.Parse(a[1])]; var pl = mainscript.s.player;
                        if (e.Root == null || pl.pickedUp != null) return "no";
                        pl.Pickup(e.Root.P); return "held";
                    }
                case "drop": mainscript.s.player.Drop(); return "dropped";
            }
            return "unknown " + a[0];
        }
    }
}

static class SimMain
{
    sealed class M
    {
        public int Id;
        public Action<ulong, Func<uint, ulong>, Func<int>, Func<uint, byte[], int, int>, Action<uint>, Action<uint>> Init; public Action<uint, byte[]> Arrive; public Action<uint, int, int, bool> Status;
        public Func<string, string> Do; public Func<string> Ents, Srv; public Action<float, int> Tick; public Func<string[]> DrainLog;
        public static M Load(string path, int i)
        {
            var t = new AssemblyLoadContext("machine" + i + "-" + Guid.NewGuid(), true).LoadFromAssemblyPath(path).GetType("TLDRevamp.Net.SimSide");
            D Dl<D>(string n) where D : Delegate => (D)Delegate.CreateDelegate(typeof(D), t.GetMethod(n));
            return new M { Id = i, Init = Dl<Action<ulong, Func<uint, ulong>, Func<int>, Func<uint, byte[], int, int>, Action<uint>, Action<uint>>>("Init"), Arrive = Dl<Action<uint, byte[]>>("Arrive"), Status = Dl<Action<uint, int, int, bool>>("Status"),
                Do = Dl<Func<string, string>>("Do"), Ents = Dl<Func<string>>("Ents"), Srv = Dl<Func<string>>("Srv"), Tick = Dl<Action<float, int>>("Tick"), DrainLog = Dl<Func<string[]>>("DrainLog") };
        }
    }
    const int Connecting = 1, Connected = 3, OK = 1, LimitExceeded = 25;

    sealed class World
    {
        public M[] Ms; public float T = 10; public int Frame = 1; public Random Rng; public float MaxDelay; public int RefusePct; public long Msgs, Refused, Lost; public int LossPct; public bool Watch; readonly HashSet<uint> _closed = new HashSet<uint>();
        readonly SortedList<(float, long), Action> _due = new SortedList<(float, long), Action>(); long _n;
        readonly Dictionary<uint, (M to, uint conn)> _links = new Dictionary<uint, (M, uint)>(); readonly Dictionary<uint, float> _linkDue = new Dictionary<uint, float>();
        readonly Dictionary<uint, ulong> _peer = new Dictionary<uint, ulong>(); uint _next = 1;
        void At(float t, Action a) => _due.Add((t, _n++), a);
        public World(string path, int machines, int seed, float maxDelay, M[] reuse = null, float startT = 10)
        {
            Rng = new Random(seed); MaxDelay = maxDelay; T = startT;
            Ms = reuse ?? Enumerable.Range(0, machines).Select(i => M.Load(path, i)).ToArray();
            if (reuse != null) foreach (var m in Ms) m.Do("stop");
            foreach (var m in Ms)
            {
                var me = m;
                m.Init(76561190000000000UL + (ulong)m.Id, c => _peer.TryGetValue(c, out var p) ? p : 0UL,
                    () =>
                    {
                        uint c = _next++, s = _next++;
                        _links[c] = (Ms[0], s); _links[s] = (me, c); _peer[s] = 76561190000000000UL + (ulong)me.Id; _peer[c] = 76561190000000000UL;
                        At(T, () => Ms[0].Status(s, Connecting, 0, true));
                        return (int)c;
                    },
                    (c, b, flags) =>
                    {
                        if (_closed.Contains(c)) return 3;   // no connection
                        bool reliable = (flags & 8) != 0;
                        if (reliable && RefusePct > 0 && Rng.Next(100) < RefusePct) { Refused++; return LimitExceeded; }
                        Msgs++;
                        var l = _links[c];
                        if (!reliable)
                        {
                            if (LossPct > 0 && Rng.Next(100) < LossPct) { Lost++; return OK; }
                            At(T + (float)Rng.NextDouble() * MaxDelay, () => { if (!_closed.Contains(l.conn)) l.to.Arrive(l.conn, b); });   // any order
                            return OK;
                        }
                        if (Watch && (b[0] == 24 || b[0] == 25 || b[0] == 80 && false)) { int tl = T.GetHashCode(); Console.WriteLine($"{T:F3}   m{me.Id} sends {(b[0] == 24 ? "Claim net " + BitConverter.ToUInt32(b, 1) + " why " + b[5] : "Owner " + string.Join(" ", b.Skip(1)))}"); }
                        _linkDue.TryGetValue(c, out float last);
                        float due = Math.Max(last, T + (float)Rng.NextDouble() * MaxDelay); _linkDue[c] = due;   // a reliable link: delayed, in order
                        At(due, () => { if (!_closed.Contains(l.conn)) l.to.Arrive(l.conn, b); });
                        return OK;
                    },
                    c =>
                    {
                        var l = _links[c];
                        At(T, () => { me.Status(c, Connected, Connecting, false); l.to.Status(l.conn, Connected, Connecting, false); });
                    },
                    c =>
                    {
                        if (!_links.TryGetValue(c, out var l) || !_closed.Add(c)) return;
                        _closed.Add(l.conn);
                        At(T + MaxDelay, () => l.to.Status(l.conn, 4, Connected, false));   // closed by peer
                    });
            }
        }
        public void Step()
        {
            T += 0.02f; Frame++;
            while (_due.Count > 0 && _due.Keys[0].Item1 <= T) { var a = _due.Values[0]; _due.RemoveAt(0); a(); }
            foreach (var m in Ms) m.Tick(T, Frame);
        }
        public void Run(float s) { for (float end = T + s; T < end;) Step(); }
        public bool Join()
        {
            Ms[0].Do("host");
            foreach (var m in Ms.Skip(1)) m.Do("join");
            for (int i = 0; i < 4000; i++) { Step(); if (Ms.All(m => m.Do("ready") == "1")) return true; }
            return false;
        }
        /// Null if every machine agrees with the server's table.
        public string Disagree()
        {
            var srv = Ms[0].Srv().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(',')).ToDictionary(x => x[0]);
            foreach (var m in Ms)
            {
                string me = m.Do("me");
                var rows = m.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(',')).ToDictionary(x => x[0]);
                foreach (var n in srv.Keys) if (!rows.ContainsKey(n)) return $"m{m.Id} has no net {n}";
                foreach (var r in rows.Values)
                {
                    if (!srv.TryGetValue(r[0], out var s)) return $"m{m.Id} has net {r[0]}, the server doesn't";
                    if (r[1] != s[1]) return $"m{m.Id}: net {r[0]} owner {r[1]}, the server says {s[1]}";
                    if (r[2] != s[2]) return $"m{m.Id}: net {r[0]} epoch {r[2]}, the server says {s[2]}";
                    if ((r[3] == "1") != (s[1] != me)) return $"m{m.Id}: net {r[0]} proxy {r[3]} with owner {s[1]}";
                    if (r[5] == "1" && s[1] != me) return $"m{m.Id} holds net {r[0]}, owner {s[1]}";
                }
            }
            var rows0 = Ms.ToDictionary(m => m.Do("me"), m => m.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(',')).ToDictionary(x => x[0]));
            foreach (var kv in rows0) foreach (var r in kv.Value.Values)
            {
                if (r[4] != "1") return $"player {kv.Key}: net {r[0]} not placed";
                if (!rows0.TryGetValue(r[1], out var ownerRows) || !ownerRows.TryGetValue(r[0], out var o)) continue;
                double dx = double.Parse(r[6]) - double.Parse(o[6]), dz = double.Parse(r[7]) - double.Parse(o[7]);
                if (dx * dx + dz * dz > 1) return $"player {kv.Key}: net {r[0]} {Math.Sqrt(dx * dx + dz * dz):F1} m from its owner's";
            }
            foreach (var m in Ms) { var g = m.Do("ghosts"); if (g != "") return $"m{m.Id}: items in no shared object: {g.Split(',').Length}"; }
            return null;
        }
    }

    static string Path0; static List<string> PerSeed;
    static uint[] Nets(M m) => m.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(r => uint.Parse(r.Split(',')[0])).ToArray();

    /// Not in the same instant as the leave: the host hasn't heard of it yet and refuses ("already in this game").
    static bool Rejoin(World w, M m)
    {
        w.Run(w.MaxDelay * 2 + 0.5f);
        m.Do("join"); for (int k = 0; k < 4000 && m.Do("ready") != "1"; k++) w.Step();
        return m.Do("ready") == "1";
    }

    /// Each switch that's on by default, off, for some seeds: which ones does this test notice? Part k of n, so it can share cores.
    static void Sweep(int part, int parts, int seeds)
    {
        var names = new List<string>();
        var probe = new World(Path0, 3, 0, 0.02f);
        names.AddRange(probe.Ms[0].Do("switches").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Console.WriteLine($"{names.Count} switches on by default; this is part {part} of {parts}");
        M[] kept = probe.Ms; float t = probe.T + 1;
        int Batch(string flip, out string kinds)
        {
            if (flip != null) foreach (var m in kept) m.Do($"set {flip} False");
            int bad = 0; var ks = new Dictionary<string, int>(); var rx = new System.Text.RegularExpressions.Regex(@"\d+(\.\d+)?");
            for (int sd = 0; sd < seeds; sd++)
            {
                string wrong;
                try { wrong = Chaos(kept, sd, out var w, t); t = w.T + 1; foreach (var m in w.Ms) m.DrainLog(); }
                catch (Exception e) { wrong = "threw " + (e.InnerException ?? e).GetType().Name; t += 100; }
                if (wrong != null) { bad++; string k = rx.Replace(wrong, "#"); ks.TryGetValue(k, out int c); ks[k] = c + 1; }
            }
            if (flip != null) foreach (var m in kept) m.Do($"set {flip} True");
            kinds = string.Join("; ", ks.OrderByDescending(k => k.Value).Take(3).Select(k => k.Value + " x " + k.Key));
            return bad;
        }
        Console.WriteLine($"baseline: {Batch(null, out var b0)} wrong of {seeds}: {b0}");
        for (int i = part; i < names.Count; i += parts)
        {
            int bad = Batch(names[i], out var kinds);
            Console.WriteLine($"{names[i]} off: {bad} wrong{(bad > 0 ? ": " + kinds : "")}"); Console.Out.Flush();
        }
    }

    /// Everything at once, on several items: grabs, drops, moves, spawns, removals, copies the game destroys, a player leaving and
    /// coming back, refused sends, lost unreliable messages. At the end everyone lets go and waits; every machine must agree.
    static string Chaos(M[] kept, int seed, out World world, float start, bool trace = false)
    {
        float delay = new[] { 0.02f, 0.1f, 0.3f }[seed % 3];
        var w = world = new World(Path0, 3, seed, delay, kept, start); var rng = w.Rng; w.LossPct = new[] { 0, 5, 20 }[seed / 3 % 3];
        if (!w.Join()) return "no join";
        w.Run(1); w.Ms[0].Do("spawn 5 5"); w.Ms[1].Do("spawn 8 5"); w.Run(2);
        int spawned = 2;
        void Log(string s) { if (trace) Console.WriteLine($"{w.T:F3} {s}"); }
        for (int a = 0; a < 25; a++)
        {
            var i = rng.Next(3); var m = w.Ms[i];
            if (i > 0 && m.Do("ready") != "1" ) { if (m.Do("joined") == "0") { Log($"m{i} joins again"); Rejoin(w, m); Log($"m{i} back"); } continue; }
            var nets = Nets(m); uint net = nets.Length > 0 ? nets[rng.Next(nets.Length)] : 0;
            int d = rng.Next(100); string r = "";
            if (d < 25 && net != 0) { r = m.Do($"grab {net}"); Log($"m{i} grab {net}: {r}"); }
            else if (d < 40) { m.Do("drop"); Log($"m{i} drop"); }
            else if (d < 55 && net != 0) { r = m.Do($"move {net} {rng.Next(-5, 6)} {rng.Next(-5, 6)}"); Log($"m{i} move {net}: {r}"); }
            else if (d < 67 && spawned < 8) { spawned++; r = m.Do($"spawn {rng.Next(20)} {rng.Next(20)}"); Log($"m{i} spawn: {r}"); }
            else if (d < 75 && net != 0) { r = m.Do($"remove {net}"); Log($"m{i} remove {net}: {r}"); }
            else if (d < 83 && net != 0) { r = m.Do($"lose {net}"); Log($"m{i} lose {net}: {r}"); }
            else if (d < 92 && i > 0) { Log($"m{i} leaves"); m.Do("drop"); m.Do("stop"); }
            else { w.RefusePct = 50; Log("refusing"); w.Run(0.5f); w.RefusePct = 0; Log("refusing ends"); }
            w.Run((float)rng.NextDouble() * delay * 2);
        }
        w.RefusePct = 0; w.LossPct = 0;
        foreach (var m in w.Ms) m.Do("drop");
        foreach (var m in w.Ms.Skip(1)) if (m.Do("joined") == "0") { Log($"m{m.Id} joins for the end"); if (!Rejoin(w, m)) return $"m{m.Id} could not join again"; }
        w.Run(45);
        return w.Disagree();
    }
    static void ChaosRun(int seeds, bool reuse)
    {
        M[] kept = null; float t = 10; int bad = 0; var kinds = new Dictionary<string, List<int>>(); var errs = new Dictionary<string, int>();
        var rx = new System.Text.RegularExpressions.Regex(@"\d+(\.\d+)?"); var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int seed = 0; seed < seeds; seed++)
        {
            string wrong = Chaos(kept, seed, out var w, t);
            if (reuse) { kept = w.Ms; t = w.T + 1; }
            if (wrong != null) { bad++; string k = rx.Replace(wrong, "#"); if (!kinds.TryGetValue(k, out var l)) kinds[k] = l = new List<int>(); l.Add(seed); }
            foreach (var m in w.Ms) foreach (var g in m.DrainLog().Where(x => x.StartsWith("ERROR")).Select(x => { var ls = x.Split('\n'); var at = ls.FirstOrDefault(y => y.Contains(" at ") && y.Contains(".cs:")); return rx.Replace(ls[0], "#")[..Math.Min(60, rx.Replace(ls[0], "#").Length)] + " | " + (at != null ? at.Trim()[..Math.Min(100, at.Trim().Length)] : ""); }))
            { errs.TryGetValue(g, out int c); errs[g] = c + 1; }
        }
        Console.WriteLine($"chaos{(reuse ? " (reused)" : " (fresh)")}: {seeds} seeds in {clock.ElapsedMilliseconds} ms, {bad} wrong");
        foreach (var k in kinds.OrderByDescending(k => k.Value.Count)) Console.WriteLine($"   {k.Value.Count} x {k.Key}   seeds {string.Join(",", k.Value.Take(6))}");
        Console.WriteLine($"errors in the logs: {errs.Values.Sum()}");
        foreach (var g in errs.OrderByDescending(g => g.Value).Take(12)) Console.WriteLine($"   {g.Value} x {g.Key}");
    }
    static int Main(string[] args)
    {
        Path0 = Assembly.GetExecutingAssembly().Location;
        string which = args.Length > 0 ? args[0] : "all";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (which == "all" || which == "refused") { Refused(false); Refused(true); }
        if (which == "all" || which == "lost") { Lost(false); Lost(true); }
        if (which == "all" || which == "race") { Race(60); Race(60, true); }
        if (which == "all" || which == "late") { Late(false); Late(true); }
        if (which == "all" || which == "hostgrab") { HostGrab(false); HostGrab(true); }
        if (which == "reuse")
        {
            PerSeed = new List<string>(); Race(30); var fresh = PerSeed;
            PerSeed = new List<string>(); Race(30, true); var kept = PerSeed;
            int same = 0; for (int i = 0; i < fresh.Count; i++) if (fresh[i] == kept[i]) same++; else if (same + 5 > i) Console.WriteLine($" fresh {fresh[i]}\n kept  {kept[i]}");
            Console.WriteLine($"{same} of {fresh.Count} seeds the same");
        }
        if (which == "chaos") ChaosRun(args.Length > 1 ? int.Parse(args[1]) : 100, args.Length <= 2 || args[2] != "fresh");
        if (which == "chaos1") { var wrong = Chaos(null, int.Parse(args[1]), out var cw, 10, true); Console.WriteLine(wrong ?? "agree"); foreach (var m in cw.Ms) Console.WriteLine($"m{m.Id} me {m.Do("me")}: {m.Ents()}"); Console.WriteLine($"srv {cw.Ms[0].Srv()}"); Show(cw, 8); }
        if (which == "chaosat")   // a reused batch repeats as a whole: replay it up to one seed, and watch that one
        {
            int upto = int.Parse(args[1]); M[] kept = null; float t = 10;
            for (int sd = 0; sd <= upto; sd++)
            {
                var wrong = Chaos(kept, sd, out var cw, t, sd == upto); kept = cw.Ms; t = cw.T + 1;
                if (sd == upto) { Console.WriteLine(wrong ?? "agree"); foreach (var m in cw.Ms) Console.WriteLine($"m{m.Id} me {m.Do("me")}: {m.Ents()}"); Console.WriteLine($"srv {cw.Ms[0].Srv()}"); foreach (var m in cw.Ms) foreach (var l in m.DrainLog().Where(x => x.Contains("copy lost") || x.Contains("refetch") || x.Contains("no copy") || x.Contains("anomaly") || x.Contains("repair") || x.Contains("census"))) Console.WriteLine($"  m{m.Id} {l[..Math.Min(150, l.Length)]}"); }
                else foreach (var m in cw.Ms) m.DrainLog();
            }
        }
        if (which == "sweep") Sweep(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));
        if (which == "trace") { Trace(int.Parse(args[1])); }
        Console.WriteLine($"{clock.ElapsedMilliseconds} ms");
        return 0;
    }
    static long StableHash(string s) { long h = 17; foreach (char c in s) h = h * 131 + c; return h; }
    static void Show(World w, int n = 6) { foreach (var m in w.Ms) foreach (var g in m.DrainLog().Where(l => l.Contains("ERROR") || l.Contains("anomaly")).GroupBy(x => x.Length > 170 ? x.Substring(0, 170) : x).OrderByDescending(g => g.Count()).Take(n)) { Console.WriteLine($"   m{m.Id} {g.Count()} x {g.Key.Split('\n')[0]}"); foreach (var st in g.First().Split('\n').Where(l => l.Contains(" at ")).Take(3)) Console.WriteLine("        " + st.Trim()); } }

    /// Half the sends refused for 2 s while both spawn. Overnight's A/B: messages lost off, in order on.
    static void Refused(bool on)
    {
        var w = new World(Path0, 2, 1, 0.05f);
        foreach (var m in w.Ms) m.Do($"set SteamTransport.QueueRefusedSends {on}");
        Console.WriteLine($"refused sends, queue {on}: joined {w.Join()}");
        w.Run(2);
        w.RefusePct = 50;
        for (int i = 0; i < 10; i++) { w.Ms[i % 2].Do($"spawn {i * 3} 5"); w.Run(0.2f); }
        w.RefusePct = 0;
        w.Run(8);
        Console.WriteLine($"   {w.Refused} sends refused, then: {w.Disagree() ?? "all agree"}; server has {w.Ms[0].Srv().Split(';', StringSplitOptions.RemoveEmptyEntries).Length} of 10, m1 has {w.Ms[1].Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Length}");
        Show(w);
    }

    /// The host grabs what a player holds. Refused at once, inside the pickup: found here, seen in the game with the host beside them.
    static void HostGrab(bool on)
    {
        var w = new World(Path0, 2, 1, 0.05f);
        foreach (var m in w.Ms) m.Do($"set Entities.HostGrabLetGo {on}");
        w.Join(); w.Run(1); w.Ms[0].Do("spawn 5 5"); w.Run(2);
        w.Ms[1].Do("grab 1"); w.Run(2);
        w.Ms[0].Do("grab 1");
        string Hold(M m) => m.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Any(r => r.Split(',')[5] == "1") ? "holds" : "empty";
        string at0 = Hold(w.Ms[0]); w.Run(0.1f); string at1 = Hold(w.Ms[0]); w.Run(0.3f); string at4 = Hold(w.Ms[0]); w.Run(0.3f); string at7 = Hold(w.Ms[0]);
        Console.WriteLine($"the host grabs what a player holds, let go {on}: host's hand at once {at0}, 0.1 s {at1}, 0.4 s {at4}, 0.7 s {at7}; the player {Hold(w.Ms[1])}; {w.Disagree() ?? "all agree"}; let go {w.Ms[0].Do("get Entities.HostGrabsLetGo")}, copies in hands claimed {w.Ms[0].Do("get Entities.HandCopiesClaimed")}");
        Show(w);
    }

    /// A break with the joiner not in the game. This morning's A/B on a rabbit: alive on the joiner off, dead on.
    static void Late(bool on)
    {
        var w = new World(Path0, 2, 1, 0.05f);
        foreach (var m in w.Ms) m.Do($"set Entities.BreaksKept {on}");
        w.Ms[0].Do("host"); w.Run(1);
        Console.WriteLine($"a break before the joiner comes, kept {on}: {w.Ms[0].Do("spawnb 5 5")}; {w.Ms[0].Do("break 1")}");
        w.Ms[1].Do("join"); for (int i = 0; i < 4000 && w.Ms[1].Do("ready") != "1"; i++) w.Step();
        w.Run(5);
        string st = w.Ms[1].Do("breakstate 1");
        Console.WriteLine($"   host {w.Ms[0].Do("breakstate 1")[..28]}; joiner {st[..Math.Min(28, st.Length)]}");
        Show(w);
    }

    /// The game destroys a copy twice within 30 s. Overnight's A/B: gone for the session off, back after 30 s on.
    static void Lost(bool on)
    {
        var w = new World(Path0, 2, 1, 0.05f);
        foreach (var m in w.Ms) m.Do($"set Entities.RefetchLater {on}");
        Console.WriteLine($"copy lost twice, refetch later {on}: joined {w.Join()}");
        w.Run(2); w.Ms[0].Do("spawn 5 5"); w.Run(2);
        bool Has() => w.Ms[1].Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Any(r => r.StartsWith("1,") && r.Split(',')[4] == "1");
        Console.WriteLine($"   first: {w.Ms[1].Do("lose 1")}");
        float t0 = w.T; while (!Has() && w.T - t0 < 10) w.Step();
        Console.WriteLine($"   back after {w.T - t0:F1} s, has {Has()}; lost again");
        w.Ms[1].Do("lose 1");
        t0 = w.T; w.Run(1); while (!Has() && w.T - t0 < 45) w.Step();
        Console.WriteLine($"   after {w.T - t0:F1} s: {(Has() ? "back" : "STILL gone")}");
        Show(w);
    }

    static void Trace(int seed)
    {
        float delay = new[] { 0.02f, 0.2f, 1f }[seed % 3];
        var w = new World(Path0, 3, seed, delay); w.Watch = true;
        w.Join(); w.Run(1); w.Ms[0].Do("spawn 5 5"); w.Run(2);
        void Row(string what) => Console.WriteLine($"{w.T:F3} {what,-16} srv [{w.Ms[0].Srv()}] m0 [{w.Ms[0].Ents()}] m1 [{w.Ms[1].Ents()}] m2 [{w.Ms[2].Ents()}]");
        Console.WriteLine($"delay {delay}"); Row("start");
        for (int a = 0; a < 12; a++)
        {
            int i = w.Rng.Next(3); var who = w.Ms[i];
            bool holds = who.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Any(r => r.Split(',')[5] == "1");
            if (holds) { if (w.Rng.Next(100) < 40) { who.Do("drop"); Row($"m{i} drops"); } } else { who.Do("grab 1"); Row($"m{i} grabs"); }
            w.Run((float)w.Rng.NextDouble() * delay * 1.5f);
        }
        w.Run(delay * 4 + 3); Row("end");
        Console.WriteLine(w.Disagree() ?? "agree");
        Show(w);
    }

    /// Everyone grabs and drops one item at random, with the replies late. Many seeds in a row, each from a fresh join.
    static void Race(int seeds, bool reuse = false)
    {
        M[] kept = null; long sig = 0; float clockNow = 10;
        int bad = 0; string first = null; var kinds = new Dictionary<string, int>(); var clock = System.Diagnostics.Stopwatch.StartNew(); long steps = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"\d+");
        for (int seed = 0; seed < seeds; seed++)
        {
            float delay = new[] { 0.02f, 0.2f, 1f }[seed % 3];
            var w = new World(Path0, 3, seed, delay, kept, clockNow); if (reuse) kept = w.Ms;
            if (!w.Join()) { Console.WriteLine("no join"); Show(w); return; }
            w.Run(1); w.Ms[0].Do("spawn 5 5"); w.Run(2);
            for (int a = 0; a < 12; a++)
            {
                var who = w.Ms[w.Rng.Next(3)];
                bool holds = who.Ents().Split(';', StringSplitOptions.RemoveEmptyEntries).Any(r => r.Split(',')[5] == "1");
                if (holds) { if (w.Rng.Next(100) < 40) who.Do("drop"); } else who.Do("grab 1");
                w.Run((float)w.Rng.NextDouble() * delay * 1.5f);
            }
            w.Run(delay * 4 + 3);
            var wrong = w.Disagree();
            foreach (var m in w.Ms) sig = sig * 31 + StableHash(m.Ents());
            if (PerSeed != null) PerSeed.Add($"{seed}: {string.Join(" | ", w.Ms.Select(m => m.Ents()))} srv {w.Ms[0].Srv()}");
            if (wrong != null) { bad++; string k = rx.Replace(wrong, "#"); kinds.TryGetValue(k, out int c); kinds[k] = c + 1; if (first == null) first = $"seed {seed}: {wrong}"; }
            if (seed == 0) Show(w);
            foreach (var m in w.Ms) m.DrainLog();
            if (reuse) clockNow = w.T + 1;
        }
        Console.WriteLine($"race{(reuse ? " (machines reused)" : " (fresh machines)")}: {seeds} seeds in {clock.ElapsedMilliseconds} ms, {bad} wrong, results hash {sig}{(first != null ? "; first " + first : "")}");
        foreach (var k in kinds) Console.WriteLine($"   {k.Value} x {k.Key}");
    }
}
