// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// A double-precision 2D point or vector (IFC profiles and plan outlines).
    /// </summary>
    internal readonly struct DVec2
    {
        public readonly double X, Y;

        public DVec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static DVec2 operator +(DVec2 a, DVec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static DVec2 operator -(DVec2 a, DVec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static DVec2 operator *(DVec2 a, double s) => new(a.X * s, a.Y * s);

        /// <summary>The 2D cross product (z of the 3D one).</summary>
        public static double Cross(DVec2 a, DVec2 b) => a.X * b.Y - a.Y * b.X;

        public double Length => Math.Sqrt(X * X + Y * Y);

        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>
    /// A double-precision 3D point or vector. IFC coordinates can be large (survey positions in millimetres), so geometry
    /// stays in doubles until the scene's origin is taken off.
    /// </summary>
    internal readonly struct DVec3
    {
        public readonly double X, Y, Z;

        public DVec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly DVec3 Zero = new(0, 0, 0);
        public static readonly DVec3 UnitX = new(1, 0, 0);
        public static readonly DVec3 UnitY = new(0, 1, 0);
        public static readonly DVec3 UnitZ = new(0, 0, 1);

        public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVec3 operator -(DVec3 a) => new(-a.X, -a.Y, -a.Z);
        public static DVec3 operator *(DVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static DVec3 operator /(DVec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(DVec3 a, DVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static DVec3 Cross(DVec3 a, DVec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static DVec3 Lerp(DVec3 a, DVec3 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        public static DVec3 Min(DVec3 a, DVec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

        public static DVec3 Max(DVec3 a, DVec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        public double LengthSquared => X * X + Y * Y + Z * Z;

        public double Length => Math.Sqrt(LengthSquared);

        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

        /// <summary>The unit vector, or the fallback when this one has (almost) no length.</summary>
        public DVec3 Normalized(DVec3 fallback)
        {
            double length = Length;
            return length > 1e-12 && double.IsFinite(length) ? this / length : fallback;
        }

        /// <summary>Some unit vector at right angles to this (unit) one.</summary>
        public DVec3 AnyPerpendicular()
        {
            DVec3 other = Math.Abs(X) < 0.9 ? UnitX : UnitY;
            return Cross(this, other).Normalized(UnitZ);
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>
    /// An affine transform: p' = T + X·p.x + Y·p.y + Z·p.z. The axes may be scaled or mirrored (IFC mapped items).
    /// </summary>
    internal readonly struct DXform
    {
        public readonly DVec3 X, Y, Z, T;

        public DXform(DVec3 x, DVec3 y, DVec3 z, DVec3 t)
        {
            X = x;
            Y = y;
            Z = z;
            T = t;
        }

        public static readonly DXform Identity = new(DVec3.UnitX, DVec3.UnitY, DVec3.UnitZ, DVec3.Zero);

        /// <summary>Applies the transform to a point.</summary>
        public DVec3 Apply(DVec3 p) => new(
            T.X + X.X * p.X + Y.X * p.Y + Z.X * p.Z,
            T.Y + X.Y * p.X + Y.Y * p.Y + Z.Y * p.Z,
            T.Z + X.Z * p.X + Y.Z * p.Y + Z.Z * p.Z);

        /// <summary>Applies the transform to a direction (no translation).</summary>
        public DVec3 ApplyVector(DVec3 v) => new(
            X.X * v.X + Y.X * v.Y + Z.X * v.Z,
            X.Y * v.X + Y.Y * v.Y + Z.Y * v.Z,
            X.Z * v.X + Y.Z * v.Y + Z.Z * v.Z);

        /// <summary>The composition: <paramref name="outer"/> applied after <paramref name="inner"/>.</summary>
        public static DXform Multiply(in DXform outer, in DXform inner) => new(
            outer.ApplyVector(inner.X), outer.ApplyVector(inner.Y), outer.ApplyVector(inner.Z), outer.Apply(inner.T));

        /// <summary>The determinant of the axes (negative when mirrored).</summary>
        public double Determinant => DVec3.Dot(X, DVec3.Cross(Y, Z));

        /// <summary>True when the transform mirrors (triangle winding must flip).</summary>
        public bool IsMirrored => Determinant < 0;

        /// <summary>
        /// A right-handed frame from an origin, a Z direction and a reference X direction (IFC's Axis2Placement3D rule:
        /// X is the reference projected at right angles to Z).
        /// </summary>
        public static DXform Frame(DVec3 origin, DVec3 z, DVec3 refX)
        {
            DVec3 zAxis = z.Normalized(DVec3.UnitZ);
            DVec3 x = refX - zAxis * DVec3.Dot(refX, zAxis);
            DVec3 xAxis = x.LengthSquared > 1e-18 ? x.Normalized(DVec3.UnitX) : zAxis.AnyPerpendicular();
            DVec3 yAxis = DVec3.Cross(zAxis, xAxis);
            return new DXform(xAxis, yAxis, zAxis, origin);
        }
    }
}
