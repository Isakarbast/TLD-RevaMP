using System;
using System.Globalization;
using System.IO;

namespace TLDRevamp
{
    internal enum ReproKind { Bind, Pose, SeatEnter, SeatLeave, Pickup, Drop, Throw, InventorySwitch, InventorySwap,
        InventoryStore, InventoryUnstore, RightGrip, RightRelease, Usable, Ignition, Attach, Detach, PhysLock, PhysUnlock,
        Hitch, Unhitch, Command, Mark, EntityPose, PoiUsable, Network, Drive }
    internal struct ReproRow
    {
        public double T, St, X, Y, Z, A, B, C, D, E, F, G, H;
        public ReproKind Kind;
        public uint Net, Other;
        public int Item, Slot;
        /// The car's response in the spare columns f, g, h (ReproCarState).
        internal ReproRow WithCar(ReproCarState.State s) { F = ReproCarState.Pack(s); G = s.Rpm; H = s.Speed; return this; }
        internal bool Valid => WellFormed && !Unresolved;
        internal bool WellFormed => Kind >= ReproKind.Bind && Kind <= ReproKind.Drive && T >= 0 && Finite(T) && Finite(St) &&
            Finite(X) && Finite(Y) && Finite(Z) && Finite(A) && Finite(B) && Finite(C) && Finite(D) && Finite(E) && Finite(F) && Finite(G) && Finite(H) &&
            Item >= -1 && Slot >= -1 && (Kind != ReproKind.Bind || A >= -1 && A == Math.Truncate(A));
        internal bool Unresolved => NeedsNet && Net == 0 || Kind == ReproKind.Bind && A == -1 ||
            (Kind == ReproKind.Hitch || Kind == ReproKind.Unhitch || Kind == ReproKind.PhysLock) && Other == 0;
        private bool NeedsNet => Kind == ReproKind.Bind || Kind == ReproKind.SeatEnter || Kind == ReproKind.SeatLeave || Kind == ReproKind.Pickup || Kind == ReproKind.Drop ||
            Kind == ReproKind.Throw || Kind == ReproKind.Usable || Kind == ReproKind.Ignition || Kind == ReproKind.Attach || Kind == ReproKind.Detach ||
            Kind == ReproKind.PhysLock || Kind == ReproKind.PhysUnlock || Kind == ReproKind.Hitch || Kind == ReproKind.Unhitch || Kind == ReproKind.EntityPose || Kind == ReproKind.Drive;
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        internal void Write(TextWriter w)
        {
            w.Write('['); Number(w, T); w.Write(','); Number(w, St); w.Write(','); w.Write((int)Kind);
            w.Write(','); w.Write(Net.ToString(CultureInfo.InvariantCulture)); w.Write(','); w.Write(Other.ToString(CultureInfo.InvariantCulture));
            w.Write(','); w.Write(Item.ToString(CultureInfo.InvariantCulture)); w.Write(','); w.Write(Slot.ToString(CultureInfo.InvariantCulture));
            w.Write(','); Number(w, X); w.Write(','); Number(w, Y); w.Write(','); Number(w, Z); w.Write(','); Number(w, A);
            w.Write(','); Number(w, B); w.Write(','); Number(w, C); w.Write(','); Number(w, D);
            w.Write(','); Number(w, E); w.Write(','); Number(w, F); w.Write(','); Number(w, G); w.Write(','); Number(w, H); w.Write(']');
        }
        private static void Number(TextWriter w, double n) => w.Write(n.ToString("R", CultureInfo.InvariantCulture));
    }
    /// The last rows after ReproData is full, for reading: what the player did just before a bug late in a long session. A real
    /// session of 73 minutes filled the 60,000 rows in 28. Replay needs the unbroken beginning, so this is another file, never
    /// complete. Its array exists only once the recording is full.
    internal sealed class ReproTail
    {
        internal const int Capacity = 30000;
        private readonly int _capacity;
        private ReproRow[] _rows;
        private int _next;
        internal int Count { get; private set; }
        internal long Seen { get; private set; }
        internal ReproTail(int capacity = Capacity) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _capacity = capacity; }
        internal void Add(ReproRow row)
        {
            if (!row.WellFormed || row.Unresolved) return;
            if (_rows == null) _rows = new ReproRow[_capacity];
            _rows[_next] = row; _next = (_next + 1) % _capacity; Seen++;
            if (Count < _capacity) Count++;
        }
        /// In order, oldest first, as a recording that can't be replayed (complete:false).
        internal ReproData Snapshot()
        {
            if (Count == 0) return null;
            var data = new ReproData(Count);
            for (int i = 0; i < Count; i++) data.Add(_rows[(_next - Count + i + _capacity) % _capacity]);
            data.Truncated();
            return data;
        }
    }

    /// A recording ends at the cap instead of overwriting the beginning of a repro. Only typed numbers reach disk.
    internal sealed class ReproData
    {
        internal const int Format = 1, Capacity = 60000;
        private readonly ReproRow[] _rows;
        internal int Count { get; private set; }
        internal int Dropped { get; private set; }
        internal int Skipped { get; private set; }
        internal void Skip() => Skipped++;
        internal void Truncated() => Dropped++;
        internal ReproData(int capacity = Capacity) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _rows = new ReproRow[capacity]; }
        /// What has been recorded so far, as its own object: the recorder keeps going while a report writes this one.
        internal ReproData Copy()
        {
            var copy = new ReproData(Math.Max(1, Count));
            Array.Copy(_rows, copy._rows, Count);
            copy.Count = Count; copy.Dropped = Dropped; copy.Skipped = Skipped;
            return copy;
        }
        internal double Seconds => Count > 0 ? _rows[Count - 1].T : 0;
        internal double FirstT => Count > 0 ? _rows[0].T : 0;
        internal bool Add(ReproRow row)
        {
            if (!row.WellFormed || Count == _rows.Length || Count > 0 && row.T < _rows[Count - 1].T) { Dropped++; return false; }
            if (row.Unresolved) { Skip(); return false; }
            _rows[Count++] = row; return true;
        }
        internal void Write(TextWriter w, int protocol)
        {
            w.Write("{\"format\":1,\"protocol\":" + protocol.ToString(CultureInfo.InvariantCulture) + ",\"sampleHz\":10,\"complete\":" + (Dropped == 0 ? "true" : "false") +
                ",\"dropped\":" + Dropped.ToString(CultureInfo.InvariantCulture) + ",\"skipped\":" + Skipped.ToString(CultureInfo.InvariantCulture) + ",\"cols\":[\"t\",\"st\",\"kind\",\"net\",\"other\",\"item\",\"slot\",\"x\",\"y\",\"z\",\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\"],\"rows\":[");
            for (int i = 0; i < Count; i++) { if (i > 0) w.Write(','); _rows[i].Write(w); }
            w.Write("]}\n");
        }
        // Only the command verb is retained. Arguments (including names, passwords and mark notes) never enter rows.
        internal static int Command(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '/') return -1;
            int end = text.IndexOf(' '); if (end < 0) end = text.Length;
            string verb = text.Substring(0, end).ToLowerInvariant();
            switch (verb)
            {
                case "/tpa":
                    string target = text.Substring(end).Trim();
                    return target.Equals("on", StringComparison.OrdinalIgnoreCase) || target.Equals("off", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                case "/y": case "/tpaccept": return 2; case "/n": case "/tpdeny": return 3; case "/mark": return 4;
                default: return 0;
            }
        }
    }
}
