// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Polygon triangulation for IFC import: a 2D outline with holes (ear clipping, holes bridged into the outline first),
    /// and planar 3D faces (projected onto their best plane).
    /// </summary>
    internal static class IfcPolygons
    {
        /// <summary>Above this many vertices a polygon is fanned instead of ear clipped (bounded time; very rare).</summary>
        private const int MAX_EAR_CLIP = 4000;

        #region 2D

        /// <summary>
        /// Triangulates a 2D polygon: the first loop is the outline, the rest are holes (either winding). Triangles come out
        /// counter-clockwise, as index triples into <paramref name="points"/> (which gets the cleaned vertices).
        /// </summary>
        /// <returns>False when nothing usable remained.</returns>
        public static bool Triangulate(IReadOnlyList<DVec2[]> loops, List<DVec2> points, List<int> triangles)
        {
            points.Clear();
            triangles.Clear();
            if (loops == null || loops.Count == 0) { return false; }

            // Size-relative tolerance for duplicate points
            double size = 0;
            foreach (DVec2[] loop in loops)
            {
                if (loop == null) { continue; }
                foreach (DVec2 p in loop) { size = Math.Max(size, Math.Max(Math.Abs(p.X), Math.Abs(p.Y))); }
            }
            double eps = Math.Max(size * 1e-10, 1e-12);

            List<int> outline = AddLoop(loops[0], points, eps, wantCounterClockwise: true);
            if (outline == null) { return false; }

            // Holes, rightmost first, each bridged into the outline
            var holes = new List<List<int>>();
            for (int h = 1; h < loops.Count; h++)
            {
                List<int> hole = AddLoop(loops[h], points, eps, wantCounterClockwise: false);
                if (hole != null) { holes.Add(hole); }
            }
            holes.Sort((a, b) => MaxX(points, b).CompareTo(MaxX(points, a)));
            foreach (List<int> hole in holes) { Bridge(outline, hole, points); }

            if (outline.Count > MAX_EAR_CLIP)
            {
                for (int i = 1; i + 1 < outline.Count; i++) { triangles.Add(outline[0]); triangles.Add(outline[i]); triangles.Add(outline[i + 1]); }
                return triangles.Count > 0;
            }
            EarClip(outline, points, triangles);
            return triangles.Count > 0;
        }

        /// <summary>The signed area of a loop (positive when counter-clockwise).</summary>
        public static double SignedArea(IReadOnlyList<DVec2> loop)
        {
            double area = 0;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++) { area += DVec2.Cross(loop[j], loop[i]); }
            return area * 0.5;
        }

        /// <summary>
        /// Adds a loop's points (consecutive duplicates and the closing repeat dropped) with the wanted winding.
        /// </summary>
        /// <returns>The loop as indices, or null when fewer than three distinct points or no area remain.</returns>
        private static List<int> AddLoop(DVec2[] loop, List<DVec2> points, double eps, bool wantCounterClockwise)
        {
            if (loop == null || loop.Length < 3) { return null; }
            var clean = new List<DVec2>(loop.Length);
            foreach (DVec2 p in loop)
            {
                if (!p.IsFinite) { continue; }
                if (clean.Count > 0 && Near(clean[^1], p, eps)) { continue; }
                clean.Add(p);
            }
            while (clean.Count > 1 && Near(clean[0], clean[^1], eps)) { clean.RemoveAt(clean.Count - 1); }
            if (clean.Count < 3) { return null; }

            double area = SignedArea(clean);
            if (Math.Abs(area) <= eps * eps) { return null; }
            if ((area > 0) != wantCounterClockwise) { clean.Reverse(); }

            var indices = new List<int>(clean.Count);
            foreach (DVec2 p in clean)
            {
                indices.Add(points.Count);
                points.Add(p);
            }
            return indices;
        }

        private static bool Near(DVec2 a, DVec2 b, double eps) => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;

        private static double MaxX(List<DVec2> points, List<int> loop)
        {
            double max = double.MinValue;
            foreach (int i in loop) { max = Math.Max(max, points[i].X); }
            return max;
        }

        /// <summary>
        /// Joins a (clockwise) hole into the (counter-clockwise) outline with a two-way bridge from the hole's rightmost
        /// vertex to a vertex of the outline it can see (Eberly's method).
        /// </summary>
        private static void Bridge(List<int> outline, List<int> hole, List<DVec2> points)
        {
            // The hole's rightmost vertex
            int hm = 0;
            for (int i = 1; i < hole.Count; i++)
            {
                if (points[hole[i]].X > points[hole[hm]].X) { hm = i; }
            }
            DVec2 m = points[hole[hm]];

            // The nearest outline edge hit by a ray from M towards +X
            int best = -1;
            double bestX = double.MaxValue;
            for (int i = 0; i < outline.Count; i++)
            {
                DVec2 a = points[outline[i]];
                DVec2 b = points[outline[(i + 1) % outline.Count]];
                if ((a.Y > m.Y) == (b.Y > m.Y) && a.Y != m.Y && b.Y != m.Y) { continue; }
                if (Math.Abs(b.Y - a.Y) < 1e-300)
                {
                    if (a.Y != m.Y) { continue; }
                    double xEdge = Math.Min(a.X, b.X);
                    if (xEdge >= m.X && xEdge < bestX) { bestX = xEdge; best = a.X <= b.X ? i : (i + 1) % outline.Count; }
                    continue;
                }
                double t = (m.Y - a.Y) / (b.Y - a.Y);
                if (t < 0 || t > 1) { continue; }
                double x = a.X + t * (b.X - a.X);
                if (x < m.X || x >= bestX) { continue; }
                bestX = x;
                best = a.X > b.X ? i : (i + 1) % outline.Count; // the edge's endpoint with the larger X
            }

            int join;
            if (best < 0)
            {
                // Numerical trouble: the nearest outline vertex
                join = 0;
                double bestDistance = double.MaxValue;
                for (int i = 0; i < outline.Count; i++)
                {
                    double distance = (points[outline[i]] - m).Length;
                    if (distance < bestDistance) { bestDistance = distance; join = i; }
                }
            }
            else
            {
                // A reflex outline vertex inside the triangle (M, hit, P) blocks the view: take the one at the smallest angle
                join = best;
                DVec2 p = points[outline[best]];
                var hit = new DVec2(bestX, m.Y);
                double bestCos = -2;
                double bestDistance = double.MaxValue;
                for (int i = 0; i < outline.Count; i++)
                {
                    if (i == best) { continue; }
                    DVec2 v = points[outline[i]];
                    if (v.X < m.X) { continue; }
                    DVec2 prev = points[outline[(i + outline.Count - 1) % outline.Count]];
                    DVec2 next = points[outline[(i + 1) % outline.Count]];
                    if (DVec2.Cross(v - prev, next - v) > 0) { continue; } // convex: can't block
                    if (!InTriangleInclusive(m, hit, p, v) && !InTriangleInclusive(m, p, hit, v)) { continue; }
                    DVec2 d = v - m;
                    double length = d.Length;
                    if (length <= 0) { continue; }
                    double cos = d.X / length;
                    if (cos > bestCos + 1e-12 || (Math.Abs(cos - bestCos) <= 1e-12 && length < bestDistance))
                    {
                        bestCos = cos;
                        bestDistance = length;
                        join = i;
                    }
                }
            }

            // outline[..join] + hole from hm round to hm + outline[join..]
            var spliced = new List<int>(outline.Count + hole.Count + 2);
            for (int i = 0; i <= join; i++) { spliced.Add(outline[i]); }
            for (int k = 0; k <= hole.Count; k++) { spliced.Add(hole[(hm + k) % hole.Count]); }
            for (int i = join; i < outline.Count; i++) { spliced.Add(outline[i]); }
            outline.Clear();
            outline.AddRange(spliced);
        }

        /// <summary>
        /// Ear clipping of a counter-clockwise polygon given as indices (bridges may repeat a vertex). An ear is a convex
        /// corner with no non-convex vertex inside it (vertices at the ear's corners don't count). When no ear is found
        /// (degenerate input) the flattest corner is clipped, so it always ends.
        /// </summary>
        private static void EarClip(List<int> polygon, List<DVec2> points, List<int> triangles)
        {
            int n = polygon.Count;
            if (n < 3) { return; }
            int[] prev = new int[n];
            int[] next = new int[n];
            for (int i = 0; i < n; i++)
            {
                prev[i] = (i + n - 1) % n;
                next[i] = (i + 1) % n;
            }

            int remaining = n;
            int current = 0;
            int sinceLastEar = 0;
            while (remaining > 3)
            {
                int p = prev[current], q = next[current];
                DVec2 a = points[polygon[p]], b = points[polygon[current]], c = points[polygon[q]];
                double turn = DVec2.Cross(b - a, c - b);

                bool ear = turn > 0 && !AnyInside(polygon, points, next, current, p, q, a, b, c);
                if (ear || sinceLastEar > remaining)
                {
                    if (!ear)
                    {
                        // Stuck: clip the flattest corner (drops collinear points; slivers at worst)
                        int flattest = current;
                        double smallest = double.MaxValue;
                        int k = current;
                        for (int step = 0; step < remaining; step++, k = next[k])
                        {
                            double t = Math.Abs(DVec2.Cross(points[polygon[k]] - points[polygon[prev[k]]], points[polygon[next[k]]] - points[polygon[k]]));
                            if (t < smallest) { smallest = t; flattest = k; }
                        }
                        current = flattest;
                        p = prev[current];
                        q = next[current];
                        a = points[polygon[p]];
                        b = points[polygon[current]];
                        c = points[polygon[q]];
                        turn = DVec2.Cross(b - a, c - b);
                    }
                    if (turn > 0)
                    {
                        triangles.Add(polygon[p]);
                        triangles.Add(polygon[current]);
                        triangles.Add(polygon[q]);
                    }
                    next[p] = q;
                    prev[q] = p;
                    remaining--;
                    current = q;
                    sinceLastEar = 0;
                    continue;
                }
                current = q;
                sinceLastEar++;
            }

            int last = current;
            DVec2 la = points[polygon[prev[last]]], lb = points[polygon[last]], lc = points[polygon[next[last]]];
            if (DVec2.Cross(lb - la, lc - lb) > 0)
            {
                triangles.Add(polygon[prev[last]]);
                triangles.Add(polygon[last]);
                triangles.Add(polygon[next[last]]);
            }
        }

        private static bool AnyInside(List<int> polygon, List<DVec2> points, int[] next, int current, int p, int q, DVec2 a, DVec2 b, DVec2 c)
        {
            for (int k = next[q]; k != p; k = next[k])
            {
                DVec2 v = points[polygon[k]];
                if (Same(v, a) || Same(v, b) || Same(v, c)) { continue; }
                if (!InTriangleInclusive(a, b, c, v)) { continue; }
                return true;
            }
            return false;
        }

        private static bool Same(DVec2 a, DVec2 b) => a.X == b.X && a.Y == b.Y;

        private static bool InTriangleInclusive(DVec2 a, DVec2 b, DVec2 c, DVec2 p)
        {
            double d1 = DVec2.Cross(b - a, p - a);
            double d2 = DVec2.Cross(c - b, p - b);
            double d3 = DVec2.Cross(a - c, p - c);
            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(negative && positive);
        }

        #endregion

        #region 3D faces

        /// <summary>
        /// The Newell normal of a 3D loop (not normalised; its length is twice the loop's area).
        /// </summary>
        public static DVec3 Newell(IReadOnlyList<DVec3> loop)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                DVec3 a = loop[j], b = loop[i];
                x += (a.Y - b.Y) * (a.Z + b.Z);
                y += (a.Z - b.Z) * (a.X + b.X);
                z += (a.X - b.X) * (a.Y + b.Y);
            }
            return new DVec3(x, y, z);
        }

        /// <summary>
        /// Triangulates a planar 3D face (first loop the outer bound, the rest holes) and calls <paramref name="emit"/> per
        /// triangle, wound to face along the outer loop's normal.
        /// </summary>
        public static void TriangulateFace(IReadOnlyList<IReadOnlyList<DVec3>> loops, Action<DVec3, DVec3, DVec3> emit)
        {
            if (loops == null || loops.Count == 0 || loops[0] == null || loops[0].Count < 3) { return; }
            IReadOnlyList<DVec3> outer = loops[0];

            // Fast paths: a lone triangle, a lone convex quad
            if (loops.Count == 1 && outer.Count == 3)
            {
                emit(outer[0], outer[1], outer[2]);
                return;
            }
            DVec3 normal = Newell(outer);
            if (normal.LengthSquared <= 0) { return; }
            if (loops.Count == 1 && outer.Count == 4 && IsConvexQuad(outer, normal))
            {
                emit(outer[0], outer[1], outer[2]);
                emit(outer[0], outer[2], outer[3]);
                return;
            }

            // Project onto the plane that drops the normal's largest component, keeping the winding
            int drop = Math.Abs(normal.X) > Math.Abs(normal.Y) ? (Math.Abs(normal.X) > Math.Abs(normal.Z) ? 0 : 2) : (Math.Abs(normal.Y) > Math.Abs(normal.Z) ? 1 : 2);
            bool flip = (drop == 0 ? normal.X : drop == 1 ? normal.Y : normal.Z) < 0;

            var flat = new List<DVec2[]>(loops.Count);
            var lookup = new Dictionary<(double, double), DVec3>();
            foreach (IReadOnlyList<DVec3> loop in loops)
            {
                if (loop == null || loop.Count < 3) { continue; }
                var projected = new DVec2[loop.Count];
                for (int i = 0; i < loop.Count; i++)
                {
                    projected[i] = Project(loop[i], drop, flip);
                    lookup[(projected[i].X, projected[i].Y)] = loop[i];
                }
                flat.Add(projected);
            }

            var points = new List<DVec2>();
            var triangles = new List<int>();
            if (!Triangulate(flat, points, triangles)) { return; }
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                DVec3 a = Lift(points[triangles[t]], lookup, outer[0], normal, drop, flip);
                DVec3 b = Lift(points[triangles[t + 1]], lookup, outer[0], normal, drop, flip);
                DVec3 c = Lift(points[triangles[t + 2]], lookup, outer[0], normal, drop, flip);
                emit(a, b, c);
            }
        }

        private static bool IsConvexQuad(IReadOnlyList<DVec3> q, DVec3 normal)
        {
            for (int i = 0; i < 4; i++)
            {
                DVec3 a = q[i], b = q[(i + 1) % 4], c = q[(i + 2) % 4];
                if (DVec3.Dot(DVec3.Cross(b - a, c - b), normal) <= 0) { return false; }
            }
            return true;
        }

        private static DVec2 Project(DVec3 p, int drop, bool flip)
        {
            DVec2 v = drop switch
            {
                0 => new DVec2(p.Y, p.Z),
                1 => new DVec2(p.Z, p.X),
                _ => new DVec2(p.X, p.Y)
            };
            return flip ? new DVec2(v.Y, v.X) : v;
        }

        /// <summary>The 3D point a projected point came from (or, failing that, the point on the face's plane).</summary>
        private static DVec3 Lift(DVec2 p, Dictionary<(double, double), DVec3> lookup, DVec3 origin, DVec3 normal, int drop, bool flip)
        {
            if (lookup.TryGetValue((p.X, p.Y), out DVec3 original)) { return original; }
            double u = flip ? p.Y : p.X, v = flip ? p.X : p.Y;
            return drop switch
            {
                0 => new DVec3(origin.X - (normal.Y * (u - origin.Y) + normal.Z * (v - origin.Z)) / normal.X, u, v),
                1 => new DVec3(v, origin.Y - (normal.Z * (u - origin.Z) + normal.X * (v - origin.X)) / normal.Y, u),
                _ => new DVec3(u, v, origin.Z - (normal.X * (u - origin.X) + normal.Y * (v - origin.Y)) / normal.Z)
            };
        }

        #endregion
    }
}
