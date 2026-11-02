using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    /// Reliable messages Steam refused because its send buffer is full (512 KB by default): they wait here, in order, and
    /// everything reliable sent to that connection behind them waits too. Dropped, the sender's state had already moved on
    /// ("sent") and nothing sent them again. No Steam types: tools/unit/SendOutboxTest.
    internal sealed class SendOutbox<TConn>
    {
        public enum Result { Sent, Full, Failed }
        private sealed class Waiting { public readonly Queue<byte[]> Q = new Queue<byte[]>(); public long Bytes; }

        public readonly long MaxBytesPerConnection;
        public long Queued, Flushed, Dropped;
        public long MaxBytesSeen;
        private readonly Dictionary<TConn, Waiting> _by = new Dictionary<TConn, Waiting>();
        private readonly List<TConn> _conns = new List<TConn>();

        public SendOutbox(long maxBytesPerConnection) { MaxBytesPerConnection = maxBytesPerConnection; }

        public int Connections => _by.Count;
        public bool Waits(TConn c) => _by.Count != 0 && _by.ContainsKey(c);
        public long BytesWaiting(TConn c) => _by.TryGetValue(c, out var w) ? w.Bytes : 0;

        /// False when this connection has fallen further behind than the cap: the caller ends it.
        public bool Add(TConn c, byte[] buf, int len)
        {
            if (!_by.TryGetValue(c, out var w)) _by[c] = w = new Waiting();
            if (w.Bytes + len > MaxBytesPerConnection) return false;
            var copy = new byte[len]; Buffer.BlockCopy(buf, 0, copy, 0, len);
            w.Q.Enqueue(copy); w.Bytes += len; Queued++;
            if (w.Bytes > MaxBytesSeen) MaxBytesSeen = w.Bytes;
            return true;
        }

        /// Once a frame, in order, until Steam is full again. A failure other than full means the connection can't take it.
        public void Flush(Func<TConn, byte[], Result> send)
        {
            if (_by.Count == 0) return;
            _conns.Clear(); _conns.AddRange(_by.Keys);
            foreach (var c in _conns)
            {
                var w = _by[c];
                while (w.Q.Count > 0)
                {
                    var r = send(c, w.Q.Peek());
                    if (r == Result.Full) break;
                    var m = w.Q.Dequeue(); w.Bytes -= m.Length;
                    if (r == Result.Sent) Flushed++; else Dropped++;
                }
                if (w.Q.Count == 0) _by.Remove(c);
            }
        }

        public void Forget(TConn c)
        {
            if (_by.TryGetValue(c, out var w)) { Dropped += w.Q.Count; _by.Remove(c); }
        }
        public void Clear() { foreach (var w in _by.Values) Dropped += w.Q.Count; _by.Clear(); }
    }
}
