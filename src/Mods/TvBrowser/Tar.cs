using System;
using System.IO;
using System.Text;

namespace TvBrowser
{
    /// Reads a tar archive from a stream (ustar, with GNU long names and pax paths): every regular file's name and
    /// contents, in order. Folders, links and the rest are passed over.
    internal static class Tar
    {
        /// Calls file(name, contents) for every regular file; whatever of the contents the callback doesn't read is
        /// skipped.
        public static void Read(Stream tar, Action<string, Stream> file)
        {
            var h = new byte[512];
            string longName = null;
            while (true)
            {
                if (!Fill(tar, h, 512)) throw new EndOfStreamException("the tar archive ends early");
                if (Array.TrueForAll(h, b => b == 0)) return;
                if (!ChecksumOk(h)) throw new InvalidDataException("damaged tar archive (a header's checksum)");
                long size = Number(h, 124, 12);
                char type = (char)h[156];
                string name = longName ?? HeaderName(h);
                longName = null;
                var body = new Part(tar, size);
                switch (type)
                {
                    case 'L': longName = Text(body).TrimEnd('\0'); break;                // GNU: the next entry's long name
                    case 'x': longName = PaxPath(Text(body)); break;                      // pax: the next entry's attributes
                    case '0': case '\0': case '7': file(name, body); break;
                }
                body.SkipRest();
                Skip(tar, (512 - size % 512) % 512);
            }
        }

        private static bool ChecksumOk(byte[] h)
        {
            long want = Number(h, 148, 8), sum = 0;
            for (int i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 : h[i];
            return sum == want;
        }

        /// An octal field, or base-256 when its first byte has the top bit set (sizes of 8 GB and more).
        private static long Number(byte[] h, int at, int len)
        {
            long v = 0;
            if ((h[at] & 0x80) != 0)
            {
                for (int i = 1; i < len; i++) v = (v << 8) | h[at + i];
                return v;
            }
            for (int i = at; i < at + len; i++)
            {
                byte c = h[i];
                if (c == 0 || c == ' ') { if (v != 0) break; continue; }
                if (c < '0' || c > '7') throw new InvalidDataException("damaged tar archive (a number)");
                v = v * 8 + (c - '0');
            }
            return v;
        }

        private static string HeaderName(byte[] h)
        {
            string name = Field(h, 0, 100);
            // ustar: a prefix for paths longer than 100 bytes
            if (Encoding.ASCII.GetString(h, 257, 5) == "ustar") { string prefix = Field(h, 345, 155); if (prefix.Length > 0) name = prefix + "/" + name; }
            return name;
        }

        private static string Field(byte[] h, int at, int len)
        {
            int end = Array.IndexOf(h, (byte)0, at, len);
            return Encoding.UTF8.GetString(h, at, (end < 0 ? at + len : end) - at);
        }

        private static string Text(Stream s)
        {
            using (var m = new MemoryStream()) { s.CopyTo(m); return Encoding.UTF8.GetString(m.ToArray()); }
        }

        /// The "path" of a pax header ("<length> path=<name>\n" records), or null.
        private static string PaxPath(string pax)
        {
            foreach (var record in pax.Split('\n'))
            {
                int sp = record.IndexOf(' ');
                if (sp > 0 && record.Substring(sp + 1).StartsWith("path=")) return record.Substring(sp + 6);
            }
            return null;
        }

        private static bool Fill(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
            return true;
        }

        private static void Skip(Stream s, long n)
        {
            var b = new byte[Math.Min(n, 1 << 16)];
            while (n > 0) { int r = s.Read(b, 0, (int)Math.Min(n, b.Length)); if (r <= 0) throw new EndOfStreamException("the tar archive ends early"); n -= r; }
        }

        /// One entry's contents: the next size bytes of the archive.
        private sealed class Part : Stream
        {
            private readonly Stream _s; private long _left;
            public Part(Stream s, long size) { _s = s; _left = size; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                int r = _s.Read(buffer, offset, (int)Math.Min(count, _left));
                if (r <= 0) throw new EndOfStreamException("the tar archive ends early");
                _left -= r;
                return r;
            }
            public void SkipRest() { if (_left > 0) Skip(_s, _left); _left = 0; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
