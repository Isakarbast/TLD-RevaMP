namespace TLDRevamp.Net
{
    /// A world position in six bytes for the census: each axis in tenths of a metre, wrapped at 6.5 km. The two machines'
    /// values are never that far apart unless something is badly wrong, and a difference is what is being asked about.
    internal static class PlaceDigest
    {
        public const int Bytes = 6;

        public static ushort Of(double metres) => (ushort)((long)System.Math.Round(metres * 10.0) & 0xFFFF);

        private static double Axis(ushort a, ushort b) => (short)(ushort)(a - b) * 0.1;

        public static double Apart(ushort ax, ushort ay, ushort az, ushort bx, ushort by, ushort bz)
        {
            double x = Axis(ax, bx), y = Axis(ay, by), z = Axis(az, bz);
            return System.Math.Sqrt(x * x + y * y + z * z);
        }
    }
}
