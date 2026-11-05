using System;
using System.IO;

namespace TvBrowser
{
    /// bzip2 decompression, read as a stream: CEF's builds come as .tar.bz2, and neither .NET Standard nor the game's
    /// Mono can read that. One or more bzip2 streams back to back (as pbzip2 writes them); every block's checksum and
    /// every stream's are checked, so a damaged file stops with an error instead of unpacking wrong bytes.
    internal sealed class Bzip2Stream : Stream
    {
        private readonly Stream _in;
        private readonly byte[] _buf = new byte[1 << 16];
        private int _bufPos, _bufLen;
        private ulong _bits; private int _bitCount;
        /// Compressed bytes read so far (for progress).
        public long CompressedRead;

        private int _blockMax;
        private int[] _tt;
        private bool _inBlock, _end;
        // the block being written out: what's left of the Burrows-Wheeler transform, and the run-length state
        private int _left, _tPos, _last = -1, _same, _repeat;
        private uint _crc, _blockCrc, _streamCrc;

        private static readonly uint[] CrcTable = MakeCrcTable();

        public Bzip2Stream(Stream input)
        {
            _in = input;
            if (!StreamHeader()) throw new InvalidDataException("not bzip2 data");
        }

        // ------------------------------------------------------------------ bits

        private bool More()
        {
            _bufLen = _in.Read(_buf, 0, _buf.Length); _bufPos = 0;
            if (_bufLen <= 0) { _bufLen = 0; return false; }
            CompressedRead += _bufLen;
            return true;
        }

        /// The next n bits (n <= 24), most significant first.
        private int Bits(int n)
        {
            while (_bitCount < n)
            {
                if (_bufPos == _bufLen && !More()) throw new EndOfStreamException("the bzip2 data ends early");
                _bits = (_bits << 8) | _buf[_bufPos++]; _bitCount += 8;
            }
            _bitCount -= n;
            return (int)(_bits >> _bitCount) & ((1 << n) - 1);
        }

        private uint Bits32() => ((uint)Bits(16) << 16) | (uint)Bits(16);

        // ------------------------------------------------------------------ streams and blocks

        /// A stream's "BZh1".."BZh9", on a byte boundary. False at the end of the input.
        private bool StreamHeader()
        {
            _bitCount -= _bitCount % 8;
            if (_bitCount == 0 && _bufPos == _bufLen && !More()) return false;
            if (Bits(8) != 'B') { if (_tt != null) return false; throw new InvalidDataException("not bzip2 data"); }
            if (Bits(8) != 'Z' || Bits(8) != 'h') throw new InvalidDataException("not bzip2 data");
            int level = Bits(8) - '0';
            if (level < 1 || level > 9) throw new InvalidDataException("not bzip2 data");
            _blockMax = level * 100000;
            if (_tt == null || _tt.Length < _blockMax) _tt = new int[_blockMax];
            _streamCrc = 0;
            return true;
        }

        private static InvalidDataException Bad(string what) => new InvalidDataException("damaged bzip2 data (" + what + ")");

        /// Reads the next block into _tt, ready to write out. False at the end of the last stream.
        private bool NextBlock()
        {
            while (true)
            {
                long magic = ((long)Bits(24) << 24) | (uint)Bits(24);
                if (magic == 0x314159265359L) break;
                if (magic != 0x177245385090L) throw Bad("block header");
                if (Bits32() != _streamCrc) throw Bad("stream checksum");
                if (!StreamHeader()) return false;
            }
            _blockCrc = Bits32();
            if (Bits(1) != 0) throw new InvalidDataException("randomised bzip2 blocks (bzip2 0.9.0) aren't supported");
            int origPtr = Bits(24);

            // the bytes the block uses
            var seqToUnseq = new byte[256];
            int inUse = 0, used16 = Bits(16);
            for (int i = 0; i < 16; i++)
            {
                if ((used16 & (0x8000 >> i)) == 0) continue;
                int used = Bits(16);
                for (int j = 0; j < 16; j++) if ((used & (0x8000 >> j)) != 0) seqToUnseq[inUse++] = (byte)(i * 16 + j);
            }
            if (inUse == 0) throw Bad("no symbols");
            int alphaSize = inUse + 2;

            // which Huffman table each group of 50 symbols uses
            int nGroups = Bits(3), nSelectors = Bits(15);
            if (nGroups < 2 || nGroups > 6 || nSelectors < 1) throw Bad("tables");
            var mtfGroups = new byte[] { 0, 1, 2, 3, 4, 5 };
            var selectors = new byte[nSelectors];
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0;
                while (Bits(1) != 0) if (++j >= nGroups) throw Bad("selector");
                byte v = mtfGroups[j];
                for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
                mtfGroups[0] = selectors[i] = v;
            }

            // the tables
            var limit = new int[nGroups][]; var bases = new int[nGroups][]; var perm = new int[nGroups][]; var minLens = new int[nGroups];
            var len = new int[alphaSize];
            for (int t = 0; t < nGroups; t++)
            {
                int curr = Bits(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > 20) throw Bad("code length");
                        if (Bits(1) == 0) break;
                        curr += Bits(1) == 0 ? 1 : -1;
                    }
                    len[i] = curr;
                }
                MakeTable(len, alphaSize, out limit[t], out bases[t], out perm[t], out minLens[t]);
            }

            // the symbols: move-to-front indexes, with runs of the front byte as RUNA/RUNB digits
            var mtf = new byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            var counts = new int[256];
            var tt = _tt;
            int n = 0, groupNo = -1, groupPos = 0, run = 0, runBit = 1, eob = inUse + 1, gMin = 0;
            int[] gLimit = null, gBase = null, gPerm = null;
            while (true)
            {
                if (groupPos == 0)
                {
                    if (++groupNo >= nSelectors) throw Bad("too many symbols");
                    groupPos = 50;
                    int g = selectors[groupNo];
                    gLimit = limit[g]; gBase = bases[g]; gPerm = perm[g]; gMin = minLens[g];
                }
                groupPos--;
                int zn = gMin, zvec = Bits(zn);
                while (zvec > gLimit[zn])
                {
                    if (++zn > 20) throw Bad("code");
                    zvec = (zvec << 1) | Bits(1);
                }
                int idx = zvec - gBase[zn];
                if (idx < 0 || idx >= alphaSize) throw Bad("code");
                int sym = gPerm[idx];

                if (sym <= 1)
                {
                    if (runBit > _blockMax) throw Bad("run");
                    run += (sym + 1) * runBit; runBit <<= 1;
                    continue;
                }
                if (run > 0)
                {
                    if (run > _blockMax - n) throw Bad("block too long");
                    byte b = seqToUnseq[mtf[0]];
                    counts[b] += run;
                    for (; run > 0; run--) tt[n++] = b;
                    runBit = 1;
                }
                if (sym == eob) break;
                if (n >= _blockMax) throw Bad("block too long");
                int pos = sym - 1;
                byte v = mtf[pos];
                for (; pos > 0; pos--) mtf[pos] = mtf[pos - 1];
                mtf[0] = v;
                byte c = seqToUnseq[v];
                counts[c]++; tt[n++] = c;
            }
            if (origPtr >= n) throw Bad("origin");

            // the inverse Burrows-Wheeler transform's links, in the upper bits of each entry
            var cftab = new int[256];
            for (int i = 0, sum = 0; i < 256; i++) { cftab[i] = sum; sum += counts[i]; }
            for (int i = 0; i < n; i++) { int c = tt[i] & 0xff; tt[cftab[c]++] |= i << 8; }
            _tPos = tt[origPtr] >> 8;
            _left = n; _last = -1; _same = 0; _repeat = 0; _crc = 0xffffffff;
            return true;
        }

        /// A Huffman table as bzip2's decoder keeps it: per code length, the largest code (limit), the offset into
        /// perm (base), and the symbols in code order (perm).
        private static void MakeTable(int[] len, int alphaSize, out int[] limit, out int[] bases, out int[] perm, out int minLen)
        {
            limit = new int[23]; bases = new int[23]; perm = new int[alphaSize];
            minLen = 32; int maxLen = 0;
            for (int i = 0; i < alphaSize; i++) { if (len[i] > maxLen) maxLen = len[i]; if (len[i] < minLen) minLen = len[i]; }
            int pp = 0;
            for (int i = minLen; i <= maxLen; i++)
                for (int j = 0; j < alphaSize; j++) if (len[j] == i) perm[pp++] = j;
            for (int i = 0; i < alphaSize; i++) bases[len[i] + 1]++;
            for (int i = 1; i < 23; i++) bases[i] += bases[i - 1];
            int vec = 0;
            for (int i = minLen; i <= maxLen; i++) { vec += bases[i + 1] - bases[i]; limit[i] = vec - 1; vec <<= 1; }
            for (int i = minLen + 1; i <= maxLen; i++) bases[i] = ((limit[i - 1] + 1) << 1) - bases[i];
            // lengths past the longest code never match (their limit stays below every code)
            for (int i = maxLen + 1; i < 23; i++) limit[i] = int.MaxValue;
        }

        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04c11db7 : c << 1;
                t[i] = c;
            }
            return t;
        }

        // ------------------------------------------------------------------ Stream

        public override int Read(byte[] buffer, int offset, int count)
        {
            int start = offset, end = offset + count;
            int[] tt = _tt;
            uint crc = _crc;
            int left = _left, tPos = _tPos, last = _last, same = _same, repeat = _repeat;
            while (offset < end && !_end)
            {
                if (repeat > 0)
                {
                    int k = Math.Min(repeat, end - offset);
                    for (int i = 0; i < k; i++) { buffer[offset++] = (byte)last; crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ (uint)last]; }
                    repeat -= k;
                    continue;
                }
                if (left == 0)
                {
                    if (_inBlock)
                    {
                        if (~crc != _blockCrc) throw Bad("block checksum");
                        _streamCrc = ((_streamCrc << 1) | (_streamCrc >> 31)) ^ _blockCrc;
                    }
                    _inBlock = NextBlock();
                    if (!_inBlock) { _end = true; break; }
                    tt = _tt; crc = _crc; left = _left; tPos = _tPos; last = _last; same = _same; repeat = _repeat;
                    continue;
                }
                tPos = tt[tPos]; int ch = tPos & 0xff; tPos >>= 8; left--;
                // run-length: after four of the same byte, the next is how many more of it follow
                if (same == 4) { repeat = ch; same = 0; continue; }
                if (ch == last) same++; else { last = ch; same = 1; }
                buffer[offset++] = (byte)ch;
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ (uint)ch];
            }
            _crc = crc; _left = left; _tPos = tPos; _last = last; _same = same; _repeat = repeat;
            return offset - start;
        }

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
