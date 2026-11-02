// Reliable messages Steam refused: nothing lost, nothing overtakes, a connection too far behind is reported.
using System;
using System.Collections.Generic;
using TLDRevamp.Net;

internal static class SendOutboxTest
{
    private static int Main()
    {
        int fails = 0, checks = 0;
        void check(bool ok, string what) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
        byte[] M(byte b, int len = 4) { var m = new byte[len]; m[0] = b; return m; }

        var o = new SendOutbox<int>(100);
        check(!o.Waits(1) && o.Connections == 0, "empty at the start");
        check(o.Add(1, M(10), 4) && o.Add(1, M(11), 4) && o.Add(2, M(20), 4), "takes messages");
        check(o.Waits(1) && o.Waits(2) && !o.Waits(3) && o.BytesWaiting(1) == 8, "per connection");

        var got = new List<(int c, byte b)>();
        int room = 0;
        SendOutbox<int>.Result Send(int c, byte[] m)
        {
            if (room <= 0) return SendOutbox<int>.Result.Full;
            room--; got.Add((c, m[0])); return SendOutbox<int>.Result.Sent;
        }
        o.Flush(Send);
        check(got.Count == 0 && o.Waits(1) && o.Waits(2), "Steam still full: all wait");
        o.Add(1, M(12), 4);
        room = 1; o.Flush(Send);
        check(got.Count == 1 && got[0] == (1, 10) && o.Waits(1) && o.Waits(2), "one place: the first in, the rest wait");
        room = 10; o.Flush(Send);
        check(got.Count == 4 && got[1] == (1, 11) && got[2] == (1, 12) && got[3] == (2, 20), "the rest, in order per connection");
        check(!o.Waits(1) && !o.Waits(2) && o.Connections == 0 && o.BytesWaiting(1) == 0, "nothing left, and Waits is false so sends go direct again");
        check(o.Queued == 4 && o.Flushed == 4 && o.Dropped == 0, "counts");

        // the buffer is a copy: the shared writer goes on to the next message
        var buf = M(30); o.Add(1, buf, 4); buf[0] = 99; got.Clear(); o.Flush(Send);
        check(got.Count == 1 && got[0].b == 30, "keeps a copy");

        // only the length written goes in, the writer's buffer is larger
        o.Add(1, new byte[50], 3); int seen = 0;
        o.Flush((c, m) => { seen = m.Length; return SendOutbox<int>.Result.Sent; });
        check(seen == 3, "length of the message, not of the buffer");

        // a connection that can't take it: dropped, the others go on, not stuck on the head
        o.Add(1, M(40), 4); o.Add(1, M(41), 4); o.Add(2, M(50), 4);
        got.Clear();
        o.Flush((c, m) => { if (c == 1) return SendOutbox<int>.Result.Failed; got.Add((c, m[0])); return SendOutbox<int>.Result.Sent; });
        check(got.Count == 1 && got[0] == (2, 50) && !o.Waits(1) && o.Dropped == 2, "failed messages don't block");

        // the cap
        var cap = new SendOutbox<int>(10);
        check(cap.Add(1, M(1, 6), 6), "under the cap");
        check(!cap.Add(1, M(2, 6), 6), "over the cap: the caller ends the connection");
        check(cap.BytesWaiting(1) == 6, "not added");
        check(cap.Add(2, M(3, 6), 6), "another connection has its own");

        cap.Forget(1);
        check(!cap.Waits(1) && cap.Waits(2) && cap.Dropped == 1, "a closed connection's queue goes");
        cap.Clear();
        check(cap.Connections == 0, "cleared");

        Console.WriteLine($"SendOutboxTest: {checks - fails}/{checks} checks passed");
        return fails == 0 ? 0 : 1;
    }
}
