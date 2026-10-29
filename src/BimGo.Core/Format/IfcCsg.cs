// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Solid boolean operations on triangle meshes for IFC import (openings cut from walls, clipping by half-spaces), with
    /// binary space partitioning trees (after Evan Wallace's csg.js, MIT; rewritten without recursion so deep trees can't
    /// overflow the stack). Inputs should be closed meshes; every output polygon keeps its source polygon's colour.
    /// </summary>
    internal static class IfcCsg
    {
        /// <summary>A convex polygon with its plane and colour.</summary>
        internal sealed class Polygon
        {
            public DVec3[] Vertices;
            public DVec3 Normal;
            public double W;
            public uint Colour;

            public Polygon(DVec3[] vertices, DVec3 normal, double w, uint colour)
            {
                Vertices = vertices;
                Normal = normal;
                W = w;
                Colour = colour;
            }

            public void Flip()
            {
                Array.Reverse(Vertices);
                Normal = -Normal;
                W = -W;
            }
        }

        /// <summary>The polygons of a mesh's triangles (degenerate ones dropped).</summary>
        public static List<Polygon> FromMesh(IfcMesh mesh, uint colourOverride = 0)
        {
            var list = new List<Polygon>(mesh.TriangleCount);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                DVec3 a = mesh.Points[t * 3], b = mesh.Points[t * 3 + 1], c = mesh.Points[t * 3 + 2];
                DVec3 n = DVec3.Cross(b - a, c - a);
                double length = n.Length;
                if (!(length > 1e-18)) { continue; }
                n /= length;
                list.Add(new Polygon(new[] { a, b, c }, n, DVec3.Dot(n, a), colourOverride != 0 ? colourOverride : mesh.Colours[t]));
            }
            return list;
        }

        /// <summary>Adds polygons to a mesh as fans (BSP output is convex).</summary>
        public static void ToMesh(List<Polygon> polygons, IfcMesh mesh)
        {
            foreach (Polygon p in polygons)
            {
                for (int i = 1; i + 1 < p.Vertices.Length; i++) { mesh.Add(p.Vertices[0], p.Vertices[i], p.Vertices[i + 1], p.Colour); }
            }
        }

        /// <summary>A − B.</summary>
        /// <param name="a">The solid to cut.</param>
        /// <param name="b">The solid removed (its faces inside A become A's new faces).</param>
        /// <param name="epsilon">Plane thickness (model units).</param>
        public static List<Polygon> Subtract(List<Polygon> a, List<Polygon> b, double epsilon)
        {
            var na = new Node(a, epsilon);
            var nb = new Node(b, epsilon);
            na.Invert();
            na.ClipTo(nb);
            nb.ClipTo(na);
            nb.Invert();
            nb.ClipTo(na);
            nb.Invert();
            na.Build(nb.AllPolygons());
            na.Invert();
            return na.AllPolygons();
        }

        /// <summary>A ∩ B.</summary>
        public static List<Polygon> Intersect(List<Polygon> a, List<Polygon> b, double epsilon)
        {
            var na = new Node(a, epsilon);
            var nb = new Node(b, epsilon);
            na.Invert();
            nb.ClipTo(na);
            nb.Invert();
            na.ClipTo(nb);
            nb.ClipTo(na);
            na.Build(nb.AllPolygons());
            na.Invert();
            return na.AllPolygons();
        }

        #region BSP tree

        private const int COPLANAR = 0, FRONT = 1, BACK = 2, SPANNING = 3;

        /// <summary>
        /// One node of a BSP tree: a splitting plane, the polygons lying in it, and the subtrees in front and behind.
        /// </summary>
        private sealed class Node
        {
            private readonly double _eps;
            private bool _hasPlane;
            private DVec3 _normal;
            private double _w;
            private Node _front, _back;
            private List<Polygon> _polygons = new();

            public Node(List<Polygon> polygons, double eps)
            {
                _eps = eps;
                if (polygons != null && polygons.Count > 0) { Build(polygons); }
            }

            private Node(double eps)
            {
                _eps = eps;
            }

            /// <summary>Every node of the tree (this one first).</summary>
            private List<Node> Nodes()
            {
                var nodes = new List<Node>();
                var stack = new Stack<Node>();
                stack.Push(this);
                while (stack.Count > 0)
                {
                    Node node = stack.Pop();
                    nodes.Add(node);
                    if (node._front != null) { stack.Push(node._front); }
                    if (node._back != null) { stack.Push(node._back); }
                }
                return nodes;
            }

            /// <summary>Turns the solid inside out.</summary>
            public void Invert()
            {
                foreach (Node node in Nodes())
                {
                    foreach (Polygon p in node._polygons) { p.Flip(); }
                    node._normal = -node._normal;
                    node._w = -node._w;
                    (node._front, node._back) = (node._back, node._front);
                }
            }

            /// <summary>The polygons of a list not inside this tree's solid.</summary>
            private List<Polygon> ClipPolygons(List<Polygon> polygons)
            {
                var result = new List<Polygon>();
                var stack = new Stack<(Node, List<Polygon>)>();
                stack.Push((this, polygons));
                while (stack.Count > 0)
                {
                    (Node node, List<Polygon> list) = stack.Pop();
                    if (list.Count == 0) { continue; }
                    if (!node._hasPlane)
                    {
                        result.AddRange(list);
                        continue;
                    }
                    var front = new List<Polygon>();
                    var back = new List<Polygon>();
                    foreach (Polygon p in list) { node.Split(p, front, back, front, back); }
                    if (node._front != null) { stack.Push((node._front, front)); }
                    else { result.AddRange(front); }
                    if (node._back != null) { stack.Push((node._back, back)); }
                }
                return result;
            }

            /// <summary>Removes every polygon of this tree that is inside the other tree's solid.</summary>
            public void ClipTo(Node other)
            {
                foreach (Node node in Nodes()) { node._polygons = other.ClipPolygons(node._polygons); }
            }

            /// <summary>Every polygon in the tree.</summary>
            public List<Polygon> AllPolygons()
            {
                var all = new List<Polygon>();
                foreach (Node node in Nodes()) { all.AddRange(node._polygons); }
                return all;
            }

            /// <summary>Adds polygons to the tree, splitting them by the planes on their way down.</summary>
            public void Build(List<Polygon> polygons)
            {
                var stack = new Stack<(Node, List<Polygon>)>();
                stack.Push((this, polygons));
                while (stack.Count > 0)
                {
                    (Node node, List<Polygon> list) = stack.Pop();
                    if (list.Count == 0) { continue; }
                    if (!node._hasPlane)
                    {
                        node._hasPlane = true;
                        node._normal = list[0].Normal;
                        node._w = list[0].W;
                    }
                    var front = new List<Polygon>();
                    var back = new List<Polygon>();
                    foreach (Polygon p in list) { node.Split(p, node._polygons, node._polygons, front, back); }
                    if (front.Count > 0)
                    {
                        node._front ??= new Node(_eps);
                        stack.Push((node._front, front));
                    }
                    if (back.Count > 0)
                    {
                        node._back ??= new Node(_eps);
                        stack.Push((node._back, back));
                    }
                }
            }

            /// <summary>
            /// Sorts a polygon by this node's plane: coplanar ones into the coplanar lists (by facing), the rest in front or
            /// behind, spanning ones split in two.
            /// </summary>
            private void Split(Polygon polygon, List<Polygon> coplanarFront, List<Polygon> coplanarBack, List<Polygon> front, List<Polygon> back)
            {
                DVec3[] v = polygon.Vertices;
                int polygonType = 0;
                Span<int> types = v.Length <= 32 ? stackalloc int[v.Length] : new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    double t = DVec3.Dot(_normal, v[i]) - _w;
                    int type = t < -_eps ? BACK : t > _eps ? FRONT : COPLANAR;
                    polygonType |= type;
                    types[i] = type;
                }

                switch (polygonType)
                {
                    case COPLANAR:
                        (DVec3.Dot(_normal, polygon.Normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
                        break;
                    case FRONT:
                        front.Add(polygon);
                        break;
                    case BACK:
                        back.Add(polygon);
                        break;
                    default:
                    {
                        var f = new List<DVec3>(v.Length + 1);
                        var b = new List<DVec3>(v.Length + 1);
                        for (int i = 0; i < v.Length; i++)
                        {
                            int j = (i + 1) % v.Length;
                            int ti = types[i], tj = types[j];
                            DVec3 vi = v[i], vj = v[j];
                            if (ti != BACK) { f.Add(vi); }
                            if (ti != FRONT) { b.Add(vi); }
                            if ((ti | tj) == SPANNING)
                            {
                                double t = (_w - DVec3.Dot(_normal, vi)) / DVec3.Dot(_normal, vj - vi);
                                DVec3 split = DVec3.Lerp(vi, vj, t);
                                f.Add(split);
                                b.Add(split);
                            }
                        }
                        if (f.Count >= 3) { front.Add(new Polygon(f.ToArray(), polygon.Normal, polygon.W, polygon.Colour)); }
                        if (b.Count >= 3) { back.Add(new Polygon(b.ToArray(), polygon.Normal, polygon.W, polygon.Colour)); }
                        break;
                    }
                }
            }
        }

        #endregion
    }
}
