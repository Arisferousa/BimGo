// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Triangles collected for one IFC product (or one representation map), in double precision and the file's length
    /// unit. One colour per triangle (RGBA8 as <see cref="Scene.SceneVertex.Colour"/>); 0 means "not styled here: take the
    /// colour of whatever uses it" (mapped items, boolean operands).
    /// </summary>
    internal sealed class IfcMesh
    {
        /// <summary>Three points per triangle.</summary>
        public readonly List<DVec3> Points = new();

        /// <summary>One colour per triangle.</summary>
        public readonly List<uint> Colours = new();

        /// <summary>Number of triangles.</summary>
        public int TriangleCount => Colours.Count;

        /// <summary>Adds a triangle (non-finite ones are dropped).</summary>
        public void Add(DVec3 a, DVec3 b, DVec3 c, uint colour)
        {
            if (!a.IsFinite || !b.IsFinite || !c.IsFinite) { return; }
            Points.Add(a);
            Points.Add(b);
            Points.Add(c);
            Colours.Add(colour);
        }

        /// <summary>Adds another mesh's triangles through a transform (winding flipped for mirroring transforms); unstyled
        /// triangles take <paramref name="inherit"/>.</summary>
        public void Append(IfcMesh other, in DXform xf, uint inherit)
        {
            bool flip = xf.IsMirrored;
            for (int t = 0; t < other.TriangleCount; t++)
            {
                DVec3 a = xf.Apply(other.Points[t * 3]), b = xf.Apply(other.Points[t * 3 + 1]), c = xf.Apply(other.Points[t * 3 + 2]);
                uint colour = other.Colours[t] != 0 ? other.Colours[t] : inherit;
                if (flip) { Add(a, c, b, colour); }
                else { Add(a, b, c, colour); }
            }
        }

        /// <summary>Adds another mesh's triangles as they are.</summary>
        public void Append(IfcMesh other)
        {
            Points.AddRange(other.Points);
            Colours.AddRange(other.Colours);
        }

        /// <summary>Reverses the winding of the triangles from <paramref name="start"/> on.</summary>
        public void FlipFrom(int start)
        {
            for (int t = start; t < TriangleCount; t++) { (Points[t * 3 + 1], Points[t * 3 + 2]) = (Points[t * 3 + 2], Points[t * 3 + 1]); }
        }

        /// <summary>The signed volume enclosed by the triangles from <paramref name="start"/> on (positive when they face out).</summary>
        public double SignedVolumeFrom(int start)
        {
            if (start >= TriangleCount) { return 0; }
            DVec3 o = Points[start * 3];
            double volume = 0;
            for (int t = start; t < TriangleCount; t++)
            {
                volume += DVec3.Dot(Points[t * 3] - o, DVec3.Cross(Points[t * 3 + 1] - o, Points[t * 3 + 2] - o));
            }
            return volume / 6.0;
        }

        /// <summary>Makes the triangles from <paramref name="start"/> on face outwards (a closed solid's).</summary>
        public void OrientOutwardFrom(int start)
        {
            if (SignedVolumeFrom(start) < 0) { FlipFrom(start); }
        }

        /// <summary>
        /// Makes the winding of the triangles from <paramref name="start"/> on consistent across shared edges (surface models
        /// written face by face often aren't), then turns each connected part outwards.
        /// </summary>
        /// <param name="start">The first triangle to repair.</param>
        /// <param name="tolerance">Points closer than this are the same point (file units).</param>
        public void RepairOrientation(int start, double tolerance)
        {
            int n = TriangleCount - start;
            if (n <= 0) { return; }
            double scale = 1.0 / Math.Max(tolerance, 1e-12);
            var ids = new Dictionary<(long, long, long), int>(n * 2);
            var vertex = new int[n * 3];
            for (int i = 0; i < n * 3; i++)
            {
                DVec3 p = Points[start * 3 + i];
                var key = ((long)Math.Round(p.X * scale), (long)Math.Round(p.Y * scale), (long)Math.Round(p.Z * scale));
                if (!ids.TryGetValue(key, out int id)) { ids[key] = id = ids.Count; }
                vertex[i] = id;
            }

            // Each undirected edge with the corners (triangle * 3 + edge) that use it
            var edges = new Dictionary<(int, int), List<int>>(n * 3);
            for (int i = 0; i < n * 3; i++)
            {
                int a = vertex[i], b = vertex[i - i % 3 + (i % 3 + 1) % 3];
                if (a == b) { continue; }
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!edges.TryGetValue(key, out List<int> list)) { edges[key] = list = new List<int>(2); }
                list.Add(i);
            }

            var flip = new bool[n];
            var visited = new bool[n];
            var queue = new Queue<int>();
            var component = new List<int>();
            for (int seed = 0; seed < n; seed++)
            {
                if (visited[seed]) { continue; }
                visited[seed] = true;
                queue.Enqueue(seed);
                component.Clear();
                bool closed = true;
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    component.Add(t);
                    for (int k = 0; k < 3; k++)
                    {
                        int corner = t * 3 + k;
                        int a = vertex[corner], b = vertex[t * 3 + (k + 1) % 3];
                        if (a == b) { continue; }
                        List<int> users = edges[(Math.Min(a, b), Math.Max(a, b))];
                        if (users.Count != 2)
                        {
                            closed = false;
                            continue;
                        }
                        int other = users[0] == corner ? users[1] : users[0];
                        int u = other / 3;
                        if (u == t) { continue; }
                        // Two consistently wound neighbours walk their shared edge in opposite directions
                        bool sameDirection = vertex[other] == a;
                        bool wantFlip = flip[t] ^ sameDirection;
                        if (!visited[u])
                        {
                            visited[u] = true;
                            flip[u] = wantFlip;
                            queue.Enqueue(u);
                        }
                    }
                }

                foreach (int t in component)
                {
                    if (flip[t]) { Swap(start + t); }
                }
                // Outwards: exact for closed parts; for open ones (a surface model with gaps) the volume about the part's
                // centre still says which side is out, and is about zero for flat pieces, which stay as they are
                DVec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
                foreach (int t in component)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        min = DVec3.Min(min, Points[(start + t) * 3 + k]);
                        max = DVec3.Max(max, Points[(start + t) * 3 + k]);
                    }
                }
                double volume = 0;
                DVec3 o = closed ? Points[(start + component[0]) * 3] : (min + max) * 0.5;
                foreach (int t in component)
                {
                    int i = (start + t) * 3;
                    volume += DVec3.Dot(Points[i] - o, DVec3.Cross(Points[i + 1] - o, Points[i + 2] - o));
                }
                double extent = (max - min).Length;
                if (volume < 0 && (closed || -volume > 1e-6 * extent * extent * extent))
                {
                    foreach (int t in component) { Swap(start + t); }
                }
            }
        }

        private void Swap(int t) => (Points[t * 3 + 1], Points[t * 3 + 2]) = (Points[t * 3 + 2], Points[t * 3 + 1]);

        /// <summary>The bounds of the points, or false when empty.</summary>
        public bool TryGetBounds(out DVec3 min, out DVec3 max)
        {
            min = new DVec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new DVec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (DVec3 p in Points)
            {
                min = DVec3.Min(min, p);
                max = DVec3.Max(max, p);
            }
            return Points.Count > 0;
        }

        /// <summary>The most used non-zero colour (0 when none).</summary>
        public uint DominantColour()
        {
            var counts = new Dictionary<uint, int>();
            uint best = 0;
            int bestCount = 0;
            foreach (uint c in Colours)
            {
                if (c == 0) { continue; }
                counts.TryGetValue(c, out int n);
                counts[c] = ++n;
                if (n > bestCount) { bestCount = n; best = c; }
            }
            return best;
        }
    }

    /// <summary>
    /// Turns IFC geometric representation items into triangles: swept solids (extrusions, revolutions, swept disks),
    /// faceted and tessellated boundary representations, mapped items (type geometry, cached per map), boolean results
    /// (half-space clipping and solid differences) and the profiles and curves they are built from. Everything stays in the
    /// file's length unit; <see cref="Placement"/> gives an object placement's transform.
    /// </summary>
    internal sealed class IfcGeometry
    {
        #region Fields

        /// <summary>Above this many triangles in the solid being cut, a boolean is skipped (the uncut solid is kept).</summary>
        public const int CSG_TRIANGLE_LIMIT = 20000;

        private readonly StepFile _file;
        private readonly double _unit;
        private readonly double _angleUnit;
        private readonly Dictionary<int, uint> _itemColours;
        private readonly Dictionary<int, IfcMesh> _mapCache = new();
        private readonly Dictionary<int, DXform> _placementCache = new();
        private readonly HashSet<int> _placementsInProgress = new();
        private readonly Dictionary<string, int> _unsupported = new(StringComparer.Ordinal);
        private int _depth;

        #endregion

        /// <summary>
        /// Creates the converter.
        /// </summary>
        /// <param name="file">The IFC file.</param>
        /// <param name="unit">Metres per file length unit.</param>
        /// <param name="angleUnit">Radians per file plane-angle unit.</param>
        /// <param name="itemColours">The colour of each styled representation item (IfcStyledItem).</param>
        public IfcGeometry(StepFile file, double unit, double angleUnit, Dictionary<int, uint> itemColours)
        {
            _file = file;
            _unit = unit > 0 && double.IsFinite(unit) ? unit : 1.0;
            _angleUnit = angleUnit > 0 && double.IsFinite(angleUnit) ? angleUnit : 1.0;
            _itemColours = itemColours ?? new Dictionary<int, uint>();
        }

        /// <summary>Representation items that couldn't be converted, by IFC type (for the import summary).</summary>
        public IReadOnlyDictionary<string, int> Unsupported => _unsupported;

        /// <summary>Booleans skipped because the solid was too big to cut.</summary>
        public int SkippedBooleans { get; private set; }

        /// <summary>A length in the file's unit from metres.</summary>
        private double FromMetres(double metres) => metres / _unit;

        /// <summary>The plane thickness used by the booleans (0.01 mm).</summary>
        public double CsgEpsilon => FromMetres(1e-5);

        private void NoteUnsupported(string type)
        {
            if (string.IsNullOrEmpty(type)) { return; }
            _unsupported.TryGetValue(type, out int n);
            _unsupported[type] = n + 1;
        }

        #region Points, directions, placements

        /// <summary>An IfcCartesianPoint (2D points get Z = 0).</summary>
        public DVec3 Point(int id)
        {
            object[] coords = StepFile.List(StepFile.Arg(_file.Args(id), 0));
            return Coords(coords);
        }

        private static DVec3 Coords(object[] coords)
        {
            double x = coords.Length > 0 ? StepFile.Number(coords[0], 0) : 0;
            double y = coords.Length > 1 ? StepFile.Number(coords[1], 0) : 0;
            double z = coords.Length > 2 ? StepFile.Number(coords[2], 0) : 0;
            return new DVec3(x, y, z);
        }

        /// <summary>An IfcDirection (2D directions get Z = 0), or the fallback when missing or zero.</summary>
        public DVec3 Direction(int id, DVec3 fallback)
        {
            if (id == 0) { return fallback; }
            DVec3 d = Coords(StepFile.List(StepFile.Arg(_file.Args(id), 0)));
            return d.LengthSquared > 1e-24 ? d : fallback;
        }

        /// <summary>An IfcAxis2Placement3D or 2D as a transform (identity when missing).</summary>
        public DXform Axis2Placement(int id)
        {
            if (id == 0) { return DXform.Identity; }
            object[] a = _file.Args(id);
            switch (_file.TypeOf(id))
            {
                case "IFCAXIS2PLACEMENT3D":
                {
                    DVec3 origin = Point(StepFile.Ref(StepFile.Arg(a, 0)));
                    DVec3 z = Direction(StepFile.Ref(StepFile.Arg(a, 1)), DVec3.UnitZ);
                    DVec3 x = Direction(StepFile.Ref(StepFile.Arg(a, 2)), DVec3.UnitX);
                    return DXform.Frame(origin, z, x);
                }
                case "IFCAXIS2PLACEMENT2D":
                {
                    DVec3 origin = Point(StepFile.Ref(StepFile.Arg(a, 0)));
                    DVec3 x = Direction(StepFile.Ref(StepFile.Arg(a, 1)), DVec3.UnitX);
                    x = new DVec3(x.X, x.Y, 0).Normalized(DVec3.UnitX);
                    return new DXform(x, new DVec3(-x.Y, x.X, 0), DVec3.UnitZ, new DVec3(origin.X, origin.Y, 0));
                }
                case "IFCAXIS1PLACEMENT":
                {
                    DVec3 origin = Point(StepFile.Ref(StepFile.Arg(a, 0)));
                    DVec3 z = Direction(StepFile.Ref(StepFile.Arg(a, 1)), DVec3.UnitZ);
                    return DXform.Frame(origin, z, DVec3.UnitX);
                }
                default:
                    return DXform.Identity;
            }
        }

        /// <summary>
        /// An object placement (IfcLocalPlacement, chained through PlacementRelTo) as a world transform, cached.
        /// Grid placements and anything unknown are the identity.
        /// </summary>
        public DXform Placement(int id)
        {
            if (id == 0) { return DXform.Identity; }
            if (_placementCache.TryGetValue(id, out DXform cached)) { return cached; }
            if (!_placementsInProgress.Add(id)) { return DXform.Identity; } // a cycle in a damaged file

            DXform result = DXform.Identity;
            if (_file.TypeOf(id) == "IFCLOCALPLACEMENT")
            {
                object[] a = _file.Args(id);
                DXform parent = Placement(StepFile.Ref(StepFile.Arg(a, 0)));
                DXform relative = Axis2Placement(StepFile.Ref(StepFile.Arg(a, 1)));
                result = DXform.Multiply(parent, relative);
            }
            _placementsInProgress.Remove(id);
            _placementCache[id] = result;
            return result;
        }

        /// <summary>
        /// An IfcCartesianTransformationOperator (2D / 3D, uniform or not) as a transform.
        /// </summary>
        public DXform TransformOperator(int id)
        {
            if (id == 0) { return DXform.Identity; }
            string type = _file.TypeOf(id);
            object[] a = _file.Args(id);
            DVec3 origin = Point(StepFile.Ref(StepFile.Arg(a, 2)));
            double scale = StepFile.Number(StepFile.Arg(a, 3), 1.0);
            if (!double.IsFinite(scale) || scale == 0) { scale = 1.0; }

            bool is3D = type != null && type.StartsWith("IFCCARTESIANTRANSFORMATIONOPERATOR3D", StringComparison.Ordinal);
            DVec3 axis1 = Direction(StepFile.Ref(StepFile.Arg(a, 0)), DVec3.UnitX);
            DVec3 axis2 = Direction(StepFile.Ref(StepFile.Arg(a, 1)), DVec3.UnitY);
            double scale2 = scale, scale3 = scale;

            if (is3D)
            {
                DVec3 axis3 = Direction(StepFile.Ref(StepFile.Arg(a, 4)), DVec3.UnitZ);
                if (type == "IFCCARTESIANTRANSFORMATIONOPERATOR3DNONUNIFORM")
                {
                    scale2 = StepFile.Number(StepFile.Arg(a, 5), scale);
                    scale3 = StepFile.Number(StepFile.Arg(a, 6), scale);
                }
                DVec3 z = axis3.Normalized(DVec3.UnitZ);
                DVec3 x = axis1 - z * DVec3.Dot(axis1, z);
                x = x.LengthSquared > 1e-18 ? x.Normalized(DVec3.UnitX) : z.AnyPerpendicular();
                DVec3 y = axis2 - z * DVec3.Dot(axis2, z) - x * DVec3.Dot(axis2, x);
                y = y.LengthSquared > 1e-18 ? y.Normalized(DVec3.UnitY) : DVec3.Cross(z, x);
                return new DXform(x * scale, y * (double.IsFinite(scale2) ? scale2 : scale), z * (double.IsFinite(scale3) ? scale3 : scale), origin);
            }
            else
            {
                if (type == "IFCCARTESIANTRANSFORMATIONOPERATOR2DNONUNIFORM") { scale2 = StepFile.Number(StepFile.Arg(a, 4), scale); }
                DVec3 x = new DVec3(axis1.X, axis1.Y, 0).Normalized(DVec3.UnitX);
                DVec3 yRef = new(axis2.X, axis2.Y, 0);
                DVec3 y = yRef - x * DVec3.Dot(yRef, x);
                y = y.LengthSquared > 1e-18 ? y.Normalized(DVec3.UnitY) : new DVec3(-x.Y, x.X, 0);
                return new DXform(x * scale, y * (double.IsFinite(scale2) ? scale2 : scale), DVec3.UnitZ, new DVec3(origin.X, origin.Y, 0));
            }
        }

        #endregion

        #region Representations

        /// <summary>
        /// The items of a product's 3D body: the IfcProductDefinitionShape's "Body" representation (else "Facetation",
        /// else the first solid-looking one). Empty when it has none.
        /// </summary>
        public List<int> BodyItems(int productShape)
        {
            var items = new List<int>();
            if (productShape == 0) { return items; }
            object[] reps = StepFile.List(StepFile.Arg(_file.Args(productShape), 2));

            int chosen = 0;
            int rank = int.MaxValue;
            foreach (object r in reps)
            {
                int rep = StepFile.Ref(r);
                if (rep == 0) { continue; }
                object[] a = _file.Args(rep);
                string identifier = StepFile.Text(StepFile.Arg(a, 1))?.Trim() ?? string.Empty;
                string kind = StepFile.Text(StepFile.Arg(a, 2))?.Trim() ?? string.Empty;
                int thisRank = identifier.Equals("Body", StringComparison.OrdinalIgnoreCase) ? 0
                    : identifier.Equals("Facetation", StringComparison.OrdinalIgnoreCase) ? 1
                    : IsSolidKind(kind) && !IsHelperIdentifier(identifier) ? 2
                    : int.MaxValue;
                if (thisRank < rank)
                {
                    rank = thisRank;
                    chosen = rep;
                }
            }
            if (chosen == 0) { return items; }
            foreach (object item in StepFile.List(StepFile.Arg(_file.Args(chosen), 3)))
            {
                int id = StepFile.Ref(item);
                if (id != 0) { items.Add(id); }
            }
            return items;
        }

        private static bool IsSolidKind(string kind) => kind switch
        {
            "SweptSolid" or "AdvancedSweptSolid" or "Brep" or "AdvancedBrep" or "CSG" or "Clipping" or "SurfaceModel"
                or "Tessellation" or "MappedRepresentation" or "SolidModel" => true,
            _ => false
        };

        private static bool IsHelperIdentifier(string identifier) => identifier switch
        {
            "Axis" or "FootPrint" or "Box" or "Annotation" or "Profile" or "Reference" or "Lighting" or "Clearance" => true,
            _ => false
        };

        /// <summary>
        /// Adds one representation item's triangles.
        /// </summary>
        /// <param name="item">The item (any IfcRepresentationItem).</param>
        /// <param name="xf">Item coordinates → output coordinates.</param>
        /// <param name="colour">The colour when the item has no style of its own (0: leave it to the caller).</param>
        /// <param name="output">Where the triangles go.</param>
        public void AddItem(int item, in DXform xf, uint colour, IfcMesh output)
        {
            if (item == 0) { return; }
            if (_itemColours.TryGetValue(item, out uint own)) { colour = own; }
            if (_depth > 64) { return; } // a cycle in a damaged file
            _depth++;
            try
            {
                string type = _file.TypeOf(item);
                object[] a = _file.Args(item);
                switch (type)
                {
                    case "IFCEXTRUDEDAREASOLID":
                    case "IFCEXTRUDEDAREASOLIDTAPERED":
                        AddExtrusion(a, xf, colour, output);
                        break;
                    case "IFCREVOLVEDAREASOLID":
                    case "IFCREVOLVEDAREASOLIDTAPERED":
                        AddRevolution(a, xf, colour, output);
                        break;
                    case "IFCSWEPTDISKSOLID":
                    case "IFCSWEPTDISKSOLIDPOLYGONAL":
                        AddSweptDisk(a, xf, colour, output);
                        break;
                    case "IFCFACETEDBREP":
                    case "IFCFACETEDBREPWITHVOIDS":
                    case "IFCADVANCEDBREP":
                    case "IFCADVANCEDBREPWITHVOIDS":
                        AddShell(StepFile.Ref(StepFile.Arg(a, 0)), xf, colour, output, closed: true);
                        break;
                    case "IFCCLOSEDSHELL":
                        AddShell(item, xf, colour, output, closed: true);
                        break;
                    case "IFCOPENSHELL":
                    case "IFCCONNECTEDFACESET":
                        AddShell(item, xf, colour, output, closed: false);
                        break;
                    case "IFCSHELLBASEDSURFACEMODEL":
                    case "IFCFACEBASEDSURFACEMODEL":
                        foreach (object shell in StepFile.List(StepFile.Arg(a, 0)))
                        {
                            int shellId = StepFile.Ref(shell);
                            AddShell(shellId, xf, colour, output, closed: _file.TypeOf(shellId) == "IFCCLOSEDSHELL");
                        }
                        break;
                    case "IFCTRIANGULATEDFACESET":
                    case "IFCTRIANGULATEDIRREGULARNETWORK":
                        AddTriangulatedFaceSet(a, xf, colour, output);
                        break;
                    case "IFCPOLYGONALFACESET":
                        AddPolygonalFaceSet(a, xf, colour, output);
                        break;
                    case "IFCMAPPEDITEM":
                        AddMappedItem(a, xf, colour, output);
                        break;
                    case "IFCBOOLEANRESULT":
                    case "IFCBOOLEANCLIPPINGRESULT":
                        AddBoolean(a, xf, colour, output);
                        break;
                    case "IFCCSGSOLID":
                        AddItem(StepFile.Ref(StepFile.Arg(a, 0)), xf, colour, output);
                        break;
                    case "IFCBLOCK":
                    {
                        DXform local = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(a, 0))));
                        AddBox(local, DVec3.Zero, new DVec3(StepFile.Number(StepFile.Arg(a, 1), 0), StepFile.Number(StepFile.Arg(a, 2), 0), StepFile.Number(StepFile.Arg(a, 3), 0)), colour, output);
                        break;
                    }
                    case "IFCBOUNDINGBOX":
                    {
                        DVec3 corner = Point(StepFile.Ref(StepFile.Arg(a, 0)));
                        AddBox(xf, corner, corner + new DVec3(StepFile.Number(StepFile.Arg(a, 1), 0), StepFile.Number(StepFile.Arg(a, 2), 0), StepFile.Number(StepFile.Arg(a, 3), 0)), colour, output);
                        break;
                    }
                    case "IFCRIGHTCIRCULARCYLINDER":
                    {
                        DXform local = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(a, 0))));
                        double height = StepFile.Number(StepFile.Arg(a, 1), 0), radius = StepFile.Number(StepFile.Arg(a, 2), 0);
                        if (height > 0 && radius > 0)
                        {
                            AddPrism(new List<Region> { new Region(Circle(radius, radius, DXform.Identity)) }, local, new DVec3(0, 0, height), colour, output);
                        }
                        break;
                    }
                    case "IFCSPHERE":
                    {
                        DXform local = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(a, 0))));
                        AddSphere(local, StepFile.Number(StepFile.Arg(a, 1), 0), colour, output);
                        break;
                    }
                    case "IFCGEOMETRICSET":
                    case "IFCGEOMETRICCURVESET":
                    case "IFCPOLYLINE":
                    case "IFCINDEXEDPOLYCURVE":
                    case "IFCCOMPOSITECURVE":
                    case "IFCTRIMMEDCURVE":
                    case "IFCCARTESIANPOINT":
                    case "IFCANNOTATIONFILLAREA":
                    case "IFCTEXTLITERAL":
                    case "IFCTEXTLITERALWITHEXTENT":
                    case "IFCCIRCLE":
                    case "IFCLINE":
                        break; // curves and annotation: nothing to walk on
                    default:
                        NoteUnsupported(type);
                        break;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"IFC #{item} ({_file.TypeOf(item)}) skipped: {ex.Message}");
                NoteUnsupported(_file.TypeOf(item));
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>
        /// A mapped item (a type's shared geometry placed by a transformation operator): the map's triangles are built once,
        /// in the map's own coordinates, then copied through each instance's transform.
        /// </summary>
        private void AddMappedItem(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            int map = StepFile.Ref(StepFile.Arg(a, 0));
            if (map == 0) { return; }
            if (!_mapCache.TryGetValue(map, out IfcMesh local))
            {
                local = new IfcMesh();
                _mapCache[map] = local; // set first: a map that (wrongly) contains itself stops here
                object[] m = _file.Args(map);
                DXform origin = Axis2Placement(StepFile.Ref(StepFile.Arg(m, 0)));
                int representation = StepFile.Ref(StepFile.Arg(m, 1));
                foreach (object item in StepFile.List(StepFile.Arg(_file.Args(representation), 3)))
                {
                    AddItem(StepFile.Ref(item), origin, 0, local);
                }
            }
            DXform target = TransformOperator(StepFile.Ref(StepFile.Arg(a, 1)));
            output.Append(local, DXform.Multiply(xf, target), colour);
        }

        #endregion

        #region Swept solids

        /// <summary>A closed planar region: an outline and its holes (profile coordinates).</summary>
        internal sealed class Region
        {
            public DVec2[] Outer;
            public readonly List<DVec2[]> Holes = new();

            /// <summary>True for an open curve (IfcArbitraryOpenProfileDef): swept into a surface, not a solid.</summary>
            public bool Open;

            public Region(DVec2[] outer)
            {
                Outer = outer;
            }
        }

        private void AddExtrusion(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            List<Region> regions = Profile(StepFile.Ref(StepFile.Arg(a, 0)));
            if (regions.Count == 0) { return; }
            DXform position = Axis2Placement(StepFile.Ref(StepFile.Arg(a, 1)));
            DVec3 direction = Direction(StepFile.Ref(StepFile.Arg(a, 2)), DVec3.UnitZ).Normalized(DVec3.UnitZ);
            double depth = StepFile.Number(StepFile.Arg(a, 3), 0);
            if (!(Math.Abs(depth) > 0) || !double.IsFinite(depth)) { return; }
            AddPrism(regions, DXform.Multiply(xf, position), direction * depth, colour, output);
        }

        /// <summary>
        /// Sweeps regions (in the local XY plane) along a vector: caps, sides, then oriented outwards.
        /// </summary>
        private void AddPrism(List<Region> regions, in DXform local, DVec3 offset, uint colour, IfcMesh output)
        {
            var points = new List<DVec2>();
            var triangles = new List<int>();
            foreach (Region region in regions)
            {
                int start = output.TriangleCount;
                if (region.Open)
                {
                    DVec2[] line = region.Outer;
                    for (int i = 0; i + 1 < line.Length; i++)
                    {
                        DVec3 a0 = local.Apply(new DVec3(line[i].X, line[i].Y, 0)), b0 = local.Apply(new DVec3(line[i + 1].X, line[i + 1].Y, 0));
                        DVec3 a1 = local.Apply(new DVec3(line[i].X, line[i].Y, 0) + offset), b1 = local.Apply(new DVec3(line[i + 1].X, line[i + 1].Y, 0) + offset);
                        output.Add(a0, b0, b1, colour);
                        output.Add(a0, b1, a1, colour);
                    }
                    continue;
                }

                var loops = new List<DVec2[]> { region.Outer };
                loops.AddRange(region.Holes);
                if (!IfcPolygons.Triangulate(loops, points, triangles)) { continue; }

                // Caps: bottom faces down the sweep, top faces along it
                for (int t = 0; t + 2 < triangles.Count; t += 3)
                {
                    DVec3 p0 = new(points[triangles[t]].X, points[triangles[t]].Y, 0);
                    DVec3 p1 = new(points[triangles[t + 1]].X, points[triangles[t + 1]].Y, 0);
                    DVec3 p2 = new(points[triangles[t + 2]].X, points[triangles[t + 2]].Y, 0);
                    output.Add(local.Apply(p0), local.Apply(p2), local.Apply(p1), colour);
                    output.Add(local.Apply(p0 + offset), local.Apply(p1 + offset), local.Apply(p2 + offset), colour);
                }

                // Sides: outline counter-clockwise, holes clockwise, so each quad faces out of the solid
                AddSides(Oriented(region.Outer, true), local, offset, colour, output);
                foreach (DVec2[] hole in region.Holes) { AddSides(Oriented(hole, false), local, offset, colour, output); }
                output.OrientOutwardFrom(start);
            }
        }

        private static void AddSides(DVec2[] loop, in DXform local, DVec3 offset, uint colour, IfcMesh output)
        {
            if (loop == null) { return; }
            for (int i = 0; i < loop.Length; i++)
            {
                DVec2 p = loop[i], q = loop[(i + 1) % loop.Length];
                if (p.X == q.X && p.Y == q.Y) { continue; }
                DVec3 a0 = local.Apply(new DVec3(p.X, p.Y, 0)), b0 = local.Apply(new DVec3(q.X, q.Y, 0));
                DVec3 a1 = local.Apply(new DVec3(p.X, p.Y, 0) + offset), b1 = local.Apply(new DVec3(q.X, q.Y, 0) + offset);
                output.Add(a0, b0, b1, colour);
                output.Add(a0, b1, a1, colour);
            }
        }

        /// <summary>A copy of a loop with the wanted winding (closing repeat dropped), or null when it has no area.</summary>
        private static DVec2[] Oriented(DVec2[] loop, bool counterClockwise)
        {
            if (loop == null || loop.Length < 3) { return null; }
            var list = new List<DVec2>(loop);
            if (list.Count > 1 && list[0].X == list[^1].X && list[0].Y == list[^1].Y) { list.RemoveAt(list.Count - 1); }
            if (list.Count < 3) { return null; }
            double area = IfcPolygons.SignedArea(list);
            if (area == 0) { return null; }
            if ((area > 0) != counterClockwise) { list.Reverse(); }
            return list.ToArray();
        }

        private void AddRevolution(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            List<Region> regions = Profile(StepFile.Ref(StepFile.Arg(a, 0)));
            if (regions.Count == 0) { return; }
            DXform local = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(a, 1))));
            object[] axis = _file.Args(StepFile.Ref(StepFile.Arg(a, 2)));
            DVec3 axisPoint = Point(StepFile.Ref(StepFile.Arg(axis, 0)));
            DVec3 axisDirection = Direction(StepFile.Ref(StepFile.Arg(axis, 1)), DVec3.UnitY).Normalized(DVec3.UnitY);
            double angle = StepFile.Number(StepFile.Arg(a, 3), 0) * _angleUnit;
            if (!(Math.Abs(angle) > 1e-9) || !double.IsFinite(angle)) { return; }
            angle = Math.Clamp(angle, -2 * Math.PI, 2 * Math.PI);
            bool full = Math.Abs(Math.Abs(angle) - 2 * Math.PI) < 1e-6;

            // Steps from the largest radius
            double radius = 0;
            foreach (Region region in regions)
            {
                foreach (DVec2 p in region.Outer)
                {
                    DVec3 v = new DVec3(p.X, p.Y, 0) - axisPoint;
                    radius = Math.Max(radius, (v - axisDirection * DVec3.Dot(v, axisDirection)).Length);
                }
            }
            int steps = Segments(radius, angle);

            DVec3 Rotate(DVec2 p, int step)
            {
                double theta = angle * step / steps;
                DVec3 v = new DVec3(p.X, p.Y, 0) - axisPoint;
                double c = Math.Cos(theta), s = Math.Sin(theta);
                DVec3 rotated = v * c + DVec3.Cross(axisDirection, v) * s + axisDirection * (DVec3.Dot(axisDirection, v) * (1 - c));
                return local.Apply(rotated + axisPoint);
            }

            var points = new List<DVec2>();
            var triangles = new List<int>();
            foreach (Region region in regions)
            {
                if (region.Open) { continue; }
                int start = output.TriangleCount;
                var loops = new List<DVec2[]> { Oriented(region.Outer, true) };
                foreach (DVec2[] hole in region.Holes) { loops.Add(Oriented(hole, false)); }
                loops.RemoveAll(l => l == null);
                if (loops.Count == 0) { continue; }

                foreach (DVec2[] loop in loops)
                {
                    for (int s = 0; s < steps; s++)
                    {
                        for (int i = 0; i < loop.Length; i++)
                        {
                            DVec2 p = loop[i], q = loop[(i + 1) % loop.Length];
                            output.Add(Rotate(p, s), Rotate(q, s), Rotate(q, s + 1), colour);
                            output.Add(Rotate(p, s), Rotate(q, s + 1), Rotate(p, s + 1), colour);
                        }
                    }
                }
                if (!full && IfcPolygons.Triangulate(loops, points, triangles))
                {
                    for (int t = 0; t + 2 < triangles.Count; t += 3)
                    {
                        output.Add(Rotate(points[triangles[t]], 0), Rotate(points[triangles[t + 2]], 0), Rotate(points[triangles[t + 1]], 0), colour);
                        output.Add(Rotate(points[triangles[t]], steps), Rotate(points[triangles[t + 1]], steps), Rotate(points[triangles[t + 2]], steps), colour);
                    }
                }
                output.OrientOutwardFrom(start);
            }
        }

        private void AddSweptDisk(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            List<DVec3> path = Curve(StepFile.Ref(StepFile.Arg(a, 0)));
            double radius = StepFile.Number(StepFile.Arg(a, 1), 0);
            if (path.Count < 2 || !(radius > 0)) { return; }
            for (int i = 0; i < path.Count; i++) { path[i] = xf.Apply(path[i]); }
            RemoveDuplicates(path, FromMetres(1e-6));
            if (path.Count < 2) { return; }

            int sides = Math.Clamp(Segments(radius, 2 * Math.PI), 8, 16);
            int start = output.TriangleCount;
            var rings = new List<DVec3[]>(path.Count);
            DVec3 normal = DVec3.Zero;
            for (int i = 0; i < path.Count; i++)
            {
                DVec3 tangent = (i == 0 ? path[1] - path[0] : i == path.Count - 1 ? path[i] - path[i - 1] : (path[i + 1] - path[i]).Normalized(DVec3.UnitZ) + (path[i] - path[i - 1]).Normalized(DVec3.UnitZ)).Normalized(DVec3.UnitZ);
                normal = i == 0 ? tangent.AnyPerpendicular() : (normal - tangent * DVec3.Dot(normal, tangent)).Normalized(tangent.AnyPerpendicular());
                DVec3 binormal = DVec3.Cross(tangent, normal);
                var ring = new DVec3[sides];
                for (int s = 0; s < sides; s++)
                {
                    double theta = 2 * Math.PI * s / sides;
                    ring[s] = path[i] + (normal * Math.Cos(theta) + binormal * Math.Sin(theta)) * radius;
                }
                rings.Add(ring);
            }
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int t = (s + 1) % sides;
                    output.Add(rings[i][s], rings[i][t], rings[i + 1][t], colour);
                    output.Add(rings[i][s], rings[i + 1][t], rings[i + 1][s], colour);
                }
            }
            for (int s = 1; s + 1 < sides; s++)
            {
                output.Add(rings[0][0], rings[0][s + 1], rings[0][s], colour);
                output.Add(rings[^1][0], rings[^1][s], rings[^1][s + 1], colour);
            }
            output.OrientOutwardFrom(start);
        }

        /// <summary>An axis-aligned box in local coordinates.</summary>
        private static void AddBox(in DXform local, DVec3 min, DVec3 max, uint colour, IfcMesh output)
        {
            if (!(max.X > min.X) || !(max.Y > min.Y) || !(max.Z > min.Z)) { return; }
            int start = output.TriangleCount;
            DVec3[] c = new DVec3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = local.Apply(new DVec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            }
            void Quad(int p, int q, int r, int s)
            {
                output.Add(c[p], c[q], c[r], colour);
                output.Add(c[p], c[r], c[s], colour);
            }
            Quad(0, 2, 3, 1); // bottom
            Quad(4, 5, 7, 6); // top
            Quad(0, 1, 5, 4); // front
            Quad(2, 6, 7, 3); // back
            Quad(0, 4, 6, 2); // left
            Quad(1, 3, 7, 5); // right
            output.OrientOutwardFrom(start);
        }

        private void AddSphere(in DXform local, double radius, uint colour, IfcMesh output)
        {
            if (!(radius > 0)) { return; }
            int segments = Math.Clamp(Segments(radius, 2 * Math.PI), 12, 48);
            int rings = Math.Max(6, segments / 2);
            int start = output.TriangleCount;
            DXform transform = local;
            DVec3 At(int ring, int seg)
            {
                double phi = Math.PI * ring / rings, theta = 2 * Math.PI * seg / segments;
                return transform.Apply(new DVec3(Math.Sin(phi) * Math.Cos(theta), Math.Sin(phi) * Math.Sin(theta), Math.Cos(phi)) * radius);
            }
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    DVec3 a = At(r, s), b = At(r + 1, s), c = At(r + 1, s + 1), d = At(r, s + 1);
                    if (r > 0) { output.Add(a, b, d, colour); }
                    if (r < rings - 1) { output.Add(b, c, d, colour); }
                }
            }
            output.OrientOutwardFrom(start);
        }

        #endregion

        #region Boundary representations

        /// <summary>
        /// Adds a shell's faces (IfcClosedShell, IfcOpenShell, IfcConnectedFaceSet). Closed shells are oriented outwards.
        /// </summary>
        private void AddShell(int shell, in DXform xf, uint colour, IfcMesh output, bool closed)
        {
            if (shell == 0) { return; }
            int start = output.TriangleCount;
            bool flip = xf.IsMirrored;
            DXform transform = xf;
            foreach (object f in StepFile.List(StepFile.Arg(_file.Args(shell), 0)))
            {
                int face = StepFile.Ref(f);
                if (face == 0) { continue; }
                uint faceColour = _itemColours.TryGetValue(face, out uint own) ? own : colour;
                List<List<DVec3>> loops = FaceLoops(face);
                if (loops == null) { continue; }
                IfcPolygons.TriangulateFace(loops, (p, q, r) =>
                {
                    if (flip) { output.Add(transform.Apply(p), transform.Apply(r), transform.Apply(q), faceColour); }
                    else { output.Add(transform.Apply(p), transform.Apply(q), transform.Apply(r), faceColour); }
                });
            }
            output.RepairOrientation(start, FromMetres(1e-5));
            if (closed) { output.OrientOutwardFrom(start); } // parts that aren't watertight still face out on the whole
        }

        /// <summary>
        /// A face's loops in face coordinates (outer first, each wound as the face's orientation says), or null when it
        /// can't be used (an advanced face on a curved surface).
        /// </summary>
        private List<List<DVec3>> FaceLoops(int face)
        {
            string type = _file.TypeOf(face);
            object[] a = _file.Args(face);
            bool sameSense = true;
            if (type == "IFCFACESURFACE" || type == "IFCADVANCEDFACE")
            {
                int surface = StepFile.Ref(StepFile.Arg(a, 1));
                if (_file.TypeOf(surface) != "IFCPLANE")
                {
                    NoteUnsupported("IFCADVANCEDFACE (curved)");
                    return null;
                }
                sameSense = StepFile.Bool(StepFile.Arg(a, 2), true);
            }
            else if (type != "IFCFACE")
            {
                return null;
            }

            List<DVec3> outer = null;
            var holes = new List<List<DVec3>>();
            foreach (object b in StepFile.List(StepFile.Arg(a, 0)))
            {
                int bound = StepFile.Ref(b);
                if (bound == 0) { continue; }
                object[] ba = _file.Args(bound);
                List<DVec3> loop = LoopPoints(StepFile.Ref(StepFile.Arg(ba, 0)));
                if (loop == null || loop.Count < 3) { continue; }
                if (!StepFile.Bool(StepFile.Arg(ba, 1), true)) { loop.Reverse(); }
                if (!sameSense) { loop.Reverse(); }
                if (outer == null && _file.TypeOf(bound) == "IFCFACEOUTERBOUND") { outer = loop; }
                else { holes.Add(loop); }
            }
            if (outer == null)
            {
                if (holes.Count == 0) { return null; }
                // No explicit outer bound: the largest loop is the outline
                int largest = 0;
                for (int i = 1; i < holes.Count; i++)
                {
                    if (IfcPolygons.Newell(holes[i]).LengthSquared > IfcPolygons.Newell(holes[largest]).LengthSquared) { largest = i; }
                }
                outer = holes[largest];
                holes.RemoveAt(largest);
            }
            var loops = new List<List<DVec3>>(1 + holes.Count) { outer };
            loops.AddRange(holes);
            return loops;
        }

        /// <summary>A loop's points: IfcPolyLoop, or IfcEdgeLoop (edges sampled along their curves).</summary>
        private List<DVec3> LoopPoints(int loop)
        {
            object[] a = _file.Args(loop);
            switch (_file.TypeOf(loop))
            {
                case "IFCPOLYLOOP":
                {
                    var points = new List<DVec3>();
                    foreach (object p in StepFile.List(StepFile.Arg(a, 0))) { points.Add(Point(StepFile.Ref(p))); }
                    return points;
                }
                case "IFCEDGELOOP":
                {
                    var points = new List<DVec3>();
                    foreach (object e in StepFile.List(StepFile.Arg(a, 0)))
                    {
                        int oriented = StepFile.Ref(e);
                        object[] oa = _file.Args(oriented);
                        int edge = _file.TypeOf(oriented) == "IFCORIENTEDEDGE" ? StepFile.Ref(StepFile.Arg(oa, 2)) : oriented;
                        bool forward = _file.TypeOf(oriented) != "IFCORIENTEDEDGE" || StepFile.Bool(StepFile.Arg(oa, 3), true);
                        List<DVec3> edgePoints = EdgePoints(edge);
                        if (edgePoints.Count == 0) { continue; }
                        if (!forward) { edgePoints.Reverse(); }
                        for (int i = 0; i < edgePoints.Count - 1; i++) { points.Add(edgePoints[i]); } // the end is the next edge's start
                    }
                    return points;
                }
                default:
                    return null;
            }
        }

        /// <summary>An edge's points from its start vertex to its end vertex (curved edges sampled).</summary>
        private List<DVec3> EdgePoints(int edge)
        {
            object[] a = _file.Args(edge);
            DVec3 start = VertexPoint(StepFile.Ref(StepFile.Arg(a, 0)));
            DVec3 end = VertexPoint(StepFile.Ref(StepFile.Arg(a, 1)));
            var points = new List<DVec3> { start };
            if (_file.TypeOf(edge) == "IFCEDGECURVE")
            {
                int curve = StepFile.Ref(StepFile.Arg(a, 2));
                bool sameSense = StepFile.Bool(StepFile.Arg(a, 3), true);
                string curveType = _file.TypeOf(curve);
                if (curveType != "IFCLINE" && curveType != "IFCPOLYLINE")
                {
                    List<DVec3> sampled = BetweenOnCurve(curve, sameSense ? start : end, sameSense ? end : start);
                    if (!sameSense) { sampled.Reverse(); }
                    points.AddRange(sampled);
                }
            }
            points.Add(end);
            return points;
        }

        private DVec3 VertexPoint(int vertex) => Point(StepFile.Ref(StepFile.Arg(_file.Args(vertex), 0)));

        /// <summary>Interior points of a curve between two of its points (circles / ellipses; other curves: none).</summary>
        private List<DVec3> BetweenOnCurve(int curve, DVec3 from, DVec3 to)
        {
            var points = new List<DVec3>();
            string type = _file.TypeOf(curve);
            if (type != "IFCCIRCLE" && type != "IFCELLIPSE") { return points; }
            object[] a = _file.Args(curve);
            DXform position = Axis2Placement(StepFile.Ref(StepFile.Arg(a, 0)));
            double ra = StepFile.Number(StepFile.Arg(a, 1), 0);
            double rb = type == "IFCELLIPSE" ? StepFile.Number(StepFile.Arg(a, 2), ra) : ra;
            if (!(ra > 0) || !(rb > 0)) { return points; }
            double t0 = ConicAngle(position, ra, rb, from), t1 = ConicAngle(position, ra, rb, to);
            while (t1 <= t0 + 1e-9) { t1 += 2 * Math.PI; }
            int n = Segments(Math.Max(ra, rb), t1 - t0);
            for (int i = 1; i < n; i++)
            {
                double t = t0 + (t1 - t0) * i / n;
                points.Add(position.Apply(new DVec3(ra * Math.Cos(t), rb * Math.Sin(t), 0)));
            }
            return points;
        }

        private void AddTriangulatedFaceSet(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            List<DVec3> coords = PointList(StepFile.Ref(StepFile.Arg(a, 0)));
            if (coords.Count == 0) { return; }
            bool closed = StepFile.Bool(StepFile.Arg(a, 2), false);
            object[] indices = StepFile.List(StepFile.Arg(a, 3));

            // IFC4 ADD2: PnIndex (a flat list) re-maps the coordinate indices; IFC4 ADD1's NormalIndex (a nested list) is ignored
            object[] pn = StepFile.List(StepFile.Arg(a, 4));
            int[] remap = pn.Length > 0 && pn[0] is not object[] ? pn.Select(v => (int)StepFile.Number(v, 0)).ToArray() : null;

            int start = output.TriangleCount;
            bool flip = xf.IsMirrored;
            foreach (object t in indices)
            {
                object[] tri = StepFile.List(t);
                if (tri.Length < 3) { continue; }
                if (!TryIndex(tri[0], remap, coords.Count, out int i0) || !TryIndex(tri[1], remap, coords.Count, out int i1) || !TryIndex(tri[2], remap, coords.Count, out int i2)) { continue; }
                DVec3 p0 = xf.Apply(coords[i0]), p1 = xf.Apply(coords[i1]), p2 = xf.Apply(coords[i2]);
                if (flip) { output.Add(p0, p2, p1, colour); }
                else { output.Add(p0, p1, p2, colour); }
            }
            if (closed) { output.OrientOutwardFrom(start); }
            else { output.RepairOrientation(start, FromMetres(1e-5)); }
        }

        private void AddPolygonalFaceSet(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            List<DVec3> coords = PointList(StepFile.Ref(StepFile.Arg(a, 0)));
            if (coords.Count == 0) { return; }
            bool closed = StepFile.Bool(StepFile.Arg(a, 1), false);
            object[] pn = StepFile.List(StepFile.Arg(a, 3));
            int[] remap = pn.Length > 0 ? pn.Select(v => (int)StepFile.Number(v, 0)).ToArray() : null;

            int start = output.TriangleCount;
            bool flip = xf.IsMirrored;
            DXform transform = xf;
            foreach (object f in StepFile.List(StepFile.Arg(a, 2)))
            {
                int face = StepFile.Ref(f);
                object[] fa = _file.Args(face);
                var loops = new List<IReadOnlyList<DVec3>>();
                List<DVec3> outer = IndexedLoop(StepFile.List(StepFile.Arg(fa, 0)), remap, coords);
                if (outer.Count < 3) { continue; }
                loops.Add(outer);
                if (_file.TypeOf(face) == "IFCINDEXEDPOLYGONALFACEWITHVOIDS")
                {
                    foreach (object inner in StepFile.List(StepFile.Arg(fa, 1)))
                    {
                        List<DVec3> hole = IndexedLoop(StepFile.List(inner), remap, coords);
                        if (hole.Count >= 3) { loops.Add(hole); }
                    }
                }
                IfcPolygons.TriangulateFace(loops, (p, q, r) =>
                {
                    if (flip) { output.Add(transform.Apply(p), transform.Apply(r), transform.Apply(q), colour); }
                    else { output.Add(transform.Apply(p), transform.Apply(q), transform.Apply(r), colour); }
                });
            }
            if (closed) { output.OrientOutwardFrom(start); }
            else { output.RepairOrientation(start, FromMetres(1e-5)); }
        }

        private static List<DVec3> IndexedLoop(object[] indices, int[] remap, List<DVec3> coords)
        {
            var loop = new List<DVec3>(indices.Length);
            foreach (object i in indices)
            {
                if (TryIndex(i, remap, coords.Count, out int index)) { loop.Add(coords[index]); }
            }
            return loop;
        }

        /// <summary>A 1-based index (through the optional PnIndex) as a 0-based coordinate index.</summary>
        private static bool TryIndex(object value, int[] remap, int count, out int index)
        {
            index = (int)StepFile.Number(value, 0) - 1;
            if (remap != null)
            {
                if (index < 0 || index >= remap.Length) { return false; }
                index = remap[index] - 1;
            }
            return index >= 0 && index < count;
        }

        /// <summary>An IfcCartesianPointList2D / 3D's points.</summary>
        private List<DVec3> PointList(int id)
        {
            var points = new List<DVec3>();
            foreach (object c in StepFile.List(StepFile.Arg(_file.Args(id), 0))) { points.Add(Coords(StepFile.List(c))); }
            return points;
        }

        #endregion

        #region Booleans

        /// <summary>
        /// IfcBooleanResult / IfcBooleanClippingResult: difference (solids, half-spaces, polygon-bounded half-spaces),
        /// intersection, union (the operands together).
        /// </summary>
        private void AddBoolean(object[] a, in DXform xf, uint colour, IfcMesh output)
        {
            string op = StepFile.Enum(StepFile.Arg(a, 0)) ?? "DIFFERENCE";
            int first = StepFile.Ref(StepFile.Arg(a, 1));
            int second = StepFile.Ref(StepFile.Arg(a, 2));

            var solid = new IfcMesh();
            AddItem(first, xf, colour, solid);
            if (solid.TriangleCount == 0) { return; }
            if (op == "UNION")
            {
                AddItem(second, xf, colour, solid);
                output.Append(solid);
                return;
            }

            solid.TryGetBounds(out DVec3 min, out DVec3 max);
            IfcMesh cutter = OperandSolid(second, xf, min, max);
            if (cutter == null || cutter.TriangleCount == 0)
            {
                if (op != "INTERSECTION") { output.Append(solid); }
                return;
            }
            cutter.TryGetBounds(out DVec3 cmin, out DVec3 cmax);
            bool overlap = cmin.X <= max.X && cmax.X >= min.X && cmin.Y <= max.Y && cmax.Y >= min.Y && cmin.Z <= max.Z && cmax.Z >= min.Z;
            if (!overlap)
            {
                if (op != "INTERSECTION") { output.Append(solid); }
                return;
            }
            if (solid.TriangleCount > CSG_TRIANGLE_LIMIT || cutter.TriangleCount > CSG_TRIANGLE_LIMIT)
            {
                SkippedBooleans++;
                output.Append(solid);
                return;
            }

            uint cutColour = solid.DominantColour();
            List<IfcCsg.Polygon> result = op == "INTERSECTION"
                ? IfcCsg.Intersect(IfcCsg.FromMesh(solid), IfcCsg.FromMesh(cutter, cutColour), CsgEpsilon)
                : IfcCsg.Subtract(IfcCsg.FromMesh(solid), IfcCsg.FromMesh(cutter, cutColour), CsgEpsilon);
            IfcCsg.ToMesh(result, output);
        }

        /// <summary>
        /// A boolean's second operand as a closed mesh: a solid as it is, a half-space as a large box on its material side
        /// (sized from the first operand's bounds), a polygon-bounded half-space as that box cut down to the boundary prism.
        /// </summary>
        private IfcMesh OperandSolid(int operand, in DXform xf, DVec3 min, DVec3 max)
        {
            string type = _file.TypeOf(operand);
            if (type != "IFCHALFSPACESOLID" && type != "IFCBOXEDHALFSPACE" && type != "IFCPOLYGONALBOUNDEDHALFSPACE")
            {
                var mesh = new IfcMesh();
                AddItem(operand, xf, 0, mesh);
                return mesh;
            }

            object[] a = _file.Args(operand);
            int surface = StepFile.Ref(StepFile.Arg(a, 0));
            if (_file.TypeOf(surface) != "IFCPLANE")
            {
                NoteUnsupported(type + " (non-planar)");
                return null;
            }
            bool agreement = StepFile.Bool(StepFile.Arg(a, 1), true);
            DXform plane = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(_file.Args(surface), 0))));
            DVec3 origin = plane.T;
            DVec3 normal = plane.Z.Normalized(DVec3.UnitZ);
            DVec3 u = (plane.X - normal * DVec3.Dot(plane.X, normal)).Normalized(normal.AnyPerpendicular());
            DVec3 v = DVec3.Cross(normal, u);

            // The material is on the side the normal points away from when the agreement flag is true
            DVec3 centre = (min + max) * 0.5;
            double size = (max - min).Length * 2 + (centre - origin).Length + FromMetres(1.0);
            DVec3 onPlane = centre - normal * DVec3.Dot(centre - origin, normal);
            double side = agreement ? -1 : 1;
            var frame = new DXform(u, v, normal * side, onPlane);
            var box = new IfcMesh();
            AddBox(frame, new DVec3(-size, -size, 0), new DVec3(size, size, size), 0, box);
            if (type != "IFCPOLYGONALBOUNDEDHALFSPACE") { return box; }

            // Bounded: only within the boundary polygon, swept along the position's Z
            DXform position = DXform.Multiply(xf, Axis2Placement(StepFile.Ref(StepFile.Arg(a, 2))));
            List<DVec3> boundary = Curve(StepFile.Ref(StepFile.Arg(a, 3)));
            if (boundary.Count < 3) { return box; }
            var outline = boundary.Select(p => new DVec2(p.X, p.Y)).ToArray();
            DVec3 zAxis = position.Z.Normalized(DVec3.UnitZ);
            double reach = size + (position.T - centre).Length;
            var prismBase = new DXform(position.X, position.Y, position.Z, position.T - zAxis * reach);
            var prism = new IfcMesh();
            AddPrism(new List<Region> { new Region(outline) }, prismBase, new DVec3(0, 0, 2 * reach / Math.Max(position.Z.Length, 1e-12)), 0, prism);
            if (prism.TriangleCount == 0) { return box; }
            var bounded = new IfcMesh();
            IfcCsg.ToMesh(IfcCsg.Intersect(IfcCsg.FromMesh(box), IfcCsg.FromMesh(prism), CsgEpsilon), bounded);
            return bounded;
        }

        /// <summary>
        /// Cuts a solid from a mesh in place (openings from their host). Returns false when skipped (too big).
        /// </summary>
        public bool SubtractInPlace(IfcMesh mesh, IfcMesh cutter)
        {
            if (mesh.TriangleCount == 0 || cutter.TriangleCount == 0) { return true; }
            if (mesh.TriangleCount > CSG_TRIANGLE_LIMIT || cutter.TriangleCount > CSG_TRIANGLE_LIMIT)
            {
                SkippedBooleans++;
                return false;
            }
            uint cutColour = mesh.DominantColour();
            List<IfcCsg.Polygon> result = IfcCsg.Subtract(IfcCsg.FromMesh(mesh), IfcCsg.FromMesh(cutter, cutColour), CsgEpsilon);
            mesh.Points.Clear();
            mesh.Colours.Clear();
            IfcCsg.ToMesh(result, mesh);
            return true;
        }

        #endregion

        #region Profiles

        /// <summary>
        /// A profile definition as closed regions in profile coordinates (its Position applied). Empty when unsupported.
        /// </summary>
        public List<Region> Profile(int id)
        {
            var regions = new List<Region>();
            if (id == 0) { return regions; }
            string type = _file.TypeOf(id);
            object[] a = _file.Args(id);
            double N(int index, double fallback = 0) => StepFile.Number(StepFile.Arg(a, index), fallback);
            DXform position = Axis2Placement(StepFile.Ref(StepFile.Arg(a, 2)));

            switch (type)
            {
                case "IFCARBITRARYCLOSEDPROFILEDEF":
                case "IFCARBITRARYPROFILEDEFWITHVOIDS":
                {
                    DVec2[] outer = Flat(Curve(StepFile.Ref(StepFile.Arg(a, 2))));
                    if (outer.Length < 3) { break; }
                    var region = new Region(outer);
                    if (type == "IFCARBITRARYPROFILEDEFWITHVOIDS")
                    {
                        foreach (object inner in StepFile.List(StepFile.Arg(a, 3)))
                        {
                            DVec2[] hole = Flat(Curve(StepFile.Ref(inner)));
                            if (hole.Length >= 3) { region.Holes.Add(hole); }
                        }
                    }
                    regions.Add(region);
                    return regions;
                }
                case "IFCARBITRARYOPENPROFILEDEF":
                {
                    DVec2[] line = Flat(Curve(StepFile.Ref(StepFile.Arg(a, 2))));
                    if (line.Length >= 2) { regions.Add(new Region(line) { Open = true }); }
                    return regions;
                }
                case "IFCRECTANGLEPROFILEDEF":
                    regions.Add(new Region(Rectangle(N(3), N(4), position)));
                    break;
                case "IFCROUNDEDRECTANGLEPROFILEDEF":
                    regions.Add(new Region(RoundedRectangle(N(3), N(4), N(5), position)));
                    break;
                case "IFCRECTANGLEHOLLOWPROFILEDEF":
                {
                    double x = N(3), y = N(4), t = N(5);
                    var region = new Region(Rectangle(x, y, position));
                    if (t > 0 && x > 2 * t && y > 2 * t) { region.Holes.Add(Rectangle(x - 2 * t, y - 2 * t, position)); }
                    regions.Add(region);
                    break;
                }
                case "IFCCIRCLEPROFILEDEF":
                    regions.Add(new Region(Circle(N(3), N(3), position)));
                    break;
                case "IFCCIRCLEHOLLOWPROFILEDEF":
                {
                    double r = N(3), t = N(4);
                    var region = new Region(Circle(r, r, position));
                    if (t > 0 && t < r) { region.Holes.Add(Circle(r - t, r - t, position)); }
                    regions.Add(region);
                    break;
                }
                case "IFCELLIPSEPROFILEDEF":
                    regions.Add(new Region(Circle(N(3), N(4), position)));
                    break;
                case "IFCISHAPEPROFILEDEF":
                {
                    double w = N(3), d = N(4), tw = N(5), tf = N(6);
                    regions.Add(new Region(Shape(position,
                        (-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, -d / 2 + tf), (tw / 2, -d / 2 + tf), (tw / 2, d / 2 - tf), (w / 2, d / 2 - tf),
                        (w / 2, d / 2), (-w / 2, d / 2), (-w / 2, d / 2 - tf), (-tw / 2, d / 2 - tf), (-tw / 2, -d / 2 + tf), (-w / 2, -d / 2 + tf))));
                    break;
                }
                case "IFCASYMMETRICISHAPEPROFILEDEF":
                {
                    double wb = N(3), d = N(4), tw = N(5), tfb = N(6), wt = N(8, wb), tft = N(9, tfb);
                    regions.Add(new Region(Shape(position,
                        (-wb / 2, -d / 2), (wb / 2, -d / 2), (wb / 2, -d / 2 + tfb), (tw / 2, -d / 2 + tfb), (tw / 2, d / 2 - tft), (wt / 2, d / 2 - tft),
                        (wt / 2, d / 2), (-wt / 2, d / 2), (-wt / 2, d / 2 - tft), (-tw / 2, d / 2 - tft), (-tw / 2, -d / 2 + tfb), (-wb / 2, -d / 2 + tfb))));
                    break;
                }
                case "IFCLSHAPEPROFILEDEF":
                {
                    double d = N(3), w = N(4, d), t = N(5);
                    if (!double.IsFinite(w) || w <= 0) { w = d; }
                    regions.Add(new Region(Shape(position,
                        (-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, -d / 2 + t), (-w / 2 + t, -d / 2 + t), (-w / 2 + t, d / 2), (-w / 2, d / 2))));
                    break;
                }
                case "IFCTSHAPEPROFILEDEF":
                {
                    double d = N(3), fw = N(4), tw = N(5), tf = N(6);
                    regions.Add(new Region(Shape(position,
                        (-tw / 2, -d / 2), (tw / 2, -d / 2), (tw / 2, d / 2 - tf), (fw / 2, d / 2 - tf), (fw / 2, d / 2), (-fw / 2, d / 2),
                        (-fw / 2, d / 2 - tf), (-tw / 2, d / 2 - tf))));
                    break;
                }
                case "IFCUSHAPEPROFILEDEF":
                {
                    double d = N(3), fw = N(4), tw = N(5), tf = N(6);
                    regions.Add(new Region(Shape(position,
                        (-fw / 2, -d / 2), (fw / 2, -d / 2), (fw / 2, -d / 2 + tf), (-fw / 2 + tw, -d / 2 + tf), (-fw / 2 + tw, d / 2 - tf),
                        (fw / 2, d / 2 - tf), (fw / 2, d / 2), (-fw / 2, d / 2))));
                    break;
                }
                case "IFCCSHAPEPROFILEDEF":
                {
                    double d = N(3), w = N(4), t = N(5), g = N(6);
                    if (!(g > t)) { g = t; }
                    regions.Add(new Region(Shape(position,
                        (-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, -d / 2 + g), (w / 2 - t, -d / 2 + g), (w / 2 - t, -d / 2 + t), (-w / 2 + t, -d / 2 + t),
                        (-w / 2 + t, d / 2 - t), (w / 2 - t, d / 2 - t), (w / 2 - t, d / 2 - g), (w / 2, d / 2 - g), (w / 2, d / 2), (-w / 2, d / 2))));
                    break;
                }
                case "IFCZSHAPEPROFILEDEF":
                {
                    double d = N(3), fw = N(4), tw = N(5), tf = N(6);
                    regions.Add(new Region(Shape(position,
                        (-fw + tw / 2, -d / 2), (tw / 2, -d / 2), (tw / 2, d / 2 - tf), (fw - tw / 2, d / 2 - tf), (fw - tw / 2, d / 2), (-tw / 2, d / 2),
                        (-tw / 2, -d / 2 + tf), (-fw + tw / 2, -d / 2 + tf))));
                    break;
                }
                case "IFCDERIVEDPROFILEDEF":
                case "IFCMIRROREDPROFILEDEF":
                {
                    List<Region> parent = Profile(StepFile.Ref(StepFile.Arg(a, 2)));
                    DXform op = TransformOperator(StepFile.Ref(StepFile.Arg(a, 3)));
                    foreach (Region region in parent)
                    {
                        var moved = new Region(Transform2(region.Outer, op)) { Open = region.Open };
                        foreach (DVec2[] hole in region.Holes) { moved.Holes.Add(Transform2(hole, op)); }
                        regions.Add(moved);
                    }
                    return regions;
                }
                case "IFCCOMPOSITEPROFILEDEF":
                    foreach (object p in StepFile.List(StepFile.Arg(a, 2))) { regions.AddRange(Profile(StepFile.Ref(p))); }
                    return regions;
                default:
                    NoteUnsupported(type);
                    return regions;
            }

            // Parameterised profiles: drop degenerate results (a zero dimension)
            regions.RemoveAll(r => r.Outer == null || r.Outer.Length < 3 || r.Outer.Any(p => !p.IsFinite) || Math.Abs(IfcPolygons.SignedArea(r.Outer)) <= 0);
            return regions;
        }

        private static DVec2[] Flat(List<DVec3> points) => points.Select(p => new DVec2(p.X, p.Y)).ToArray();

        private static DVec2[] Transform2(DVec2[] loop, in DXform xf)
        {
            var result = new DVec2[loop.Length];
            for (int i = 0; i < loop.Length; i++)
            {
                DVec3 p = xf.Apply(new DVec3(loop[i].X, loop[i].Y, 0));
                result[i] = new DVec2(p.X, p.Y);
            }
            return result;
        }

        private static DVec2[] Shape(in DXform position, params (double X, double Y)[] corners)
        {
            var result = new DVec2[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                DVec3 p = position.Apply(new DVec3(corners[i].X, corners[i].Y, 0));
                result[i] = new DVec2(p.X, p.Y);
            }
            return result;
        }

        private static DVec2[] Rectangle(double x, double y, in DXform position) =>
            Shape(position, (-x / 2, -y / 2), (x / 2, -y / 2), (x / 2, y / 2), (-x / 2, y / 2));

        private DVec2[] RoundedRectangle(double x, double y, double r, in DXform position)
        {
            r = Math.Min(Math.Max(r, 0), Math.Min(x, y) / 2);
            if (!(r > 0)) { return Rectangle(x, y, position); }
            int n = Math.Max(2, Segments(r, Math.PI / 2));
            var corners = new List<(double, double)>();
            (double cx, double cy, double start)[] centres = { (x / 2 - r, -y / 2 + r, -Math.PI / 2), (x / 2 - r, y / 2 - r, 0), (-x / 2 + r, y / 2 - r, Math.PI / 2), (-x / 2 + r, -y / 2 + r, Math.PI) };
            foreach ((double cx, double cy, double start) in centres)
            {
                for (int i = 0; i <= n; i++)
                {
                    double t = start + Math.PI / 2 * i / n;
                    corners.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
                }
            }
            return Shape(position, corners.ToArray());
        }

        private DVec2[] Circle(double ra, double rb, in DXform position)
        {
            if (!(ra > 0) || !(rb > 0)) { return Array.Empty<DVec2>(); }
            int n = Math.Max(12, Segments(Math.Max(ra, rb), 2 * Math.PI));
            var result = new DVec2[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                DVec3 p = position.Apply(new DVec3(ra * Math.Cos(t), rb * Math.Sin(t), 0));
                result[i] = new DVec2(p.X, p.Y);
            }
            return result;
        }

        #endregion

        #region Curves

        /// <summary>
        /// Segments for an arc of a radius (file units) and sweep (radians): chords within 2 mm of the arc, 12–96 per full
        /// turn.
        /// </summary>
        private int Segments(double radius, double sweep)
        {
            double tolerance = FromMetres(0.002);
            double step = radius > tolerance ? 2 * Math.Acos(Math.Clamp(1 - tolerance / radius, -1, 1)) : Math.PI / 4;
            step = Math.Clamp(step, 2 * Math.PI / 96, 2 * Math.PI / 12);
            return Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / step - 1e-9));
        }

        /// <summary>
        /// A curve's points (2D curves get Z = 0): polylines, indexed poly curves (lines and arcs), composite curves, trimmed
        /// curves on lines / circles / ellipses, whole circles / ellipses, and B-splines. Empty when unsupported.
        /// </summary>
        public List<DVec3> Curve(int id)
        {
            var points = new List<DVec3>();
            if (id == 0 || _depth > 64) { return points; }
            _depth++;
            try
            {
                string type = _file.TypeOf(id);
                object[] a = _file.Args(id);
                switch (type)
                {
                    case "IFCPOLYLINE":
                        foreach (object p in StepFile.List(StepFile.Arg(a, 0))) { points.Add(Point(StepFile.Ref(p))); }
                        break;
                    case "IFCINDEXEDPOLYCURVE":
                        IndexedPolyCurve(a, points);
                        break;
                    case "IFCCOMPOSITECURVE":
                    case "IFCCOMPOSITECURVEONSURFACE":
                        foreach (object s in StepFile.List(StepFile.Arg(a, 0)))
                        {
                            object[] segment = _file.Args(StepFile.Ref(s));
                            List<DVec3> part = Curve(StepFile.Ref(StepFile.Arg(segment, 2)));
                            if (!StepFile.Bool(StepFile.Arg(segment, 1), true)) { part.Reverse(); }
                            Join(points, part);
                        }
                        break;
                    case "IFCTRIMMEDCURVE":
                        TrimmedCurve(a, points);
                        break;
                    case "IFCCIRCLE":
                    case "IFCELLIPSE":
                    {
                        DXform position = Axis2Placement(StepFile.Ref(StepFile.Arg(a, 0)));
                        double ra = StepFile.Number(StepFile.Arg(a, 1), 0);
                        double rb = type == "IFCELLIPSE" ? StepFile.Number(StepFile.Arg(a, 2), ra) : ra;
                        if (ra > 0 && rb > 0)
                        {
                            int n = Math.Max(12, Segments(Math.Max(ra, rb), 2 * Math.PI));
                            for (int i = 0; i < n; i++)
                            {
                                double t = 2 * Math.PI * i / n;
                                points.Add(position.Apply(new DVec3(ra * Math.Cos(t), rb * Math.Sin(t), 0)));
                            }
                        }
                        break;
                    }
                    case "IFCBSPLINECURVEWITHKNOTS":
                    case "IFCRATIONALBSPLINECURVEWITHKNOTS":
                        BSpline(type, a, points);
                        break;
                    default:
                        NoteUnsupported(type);
                        break;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"IFC curve #{id} skipped: {ex.Message}");
            }
            finally
            {
                _depth--;
            }
            return points;
        }

        /// <summary>Appends a curve part, dropping its first point when it repeats the last one.</summary>
        private void Join(List<DVec3> points, List<DVec3> part)
        {
            double eps = FromMetres(1e-6);
            for (int i = 0; i < part.Count; i++)
            {
                if (i == 0 && points.Count > 0 && (points[^1] - part[0]).Length <= eps) { continue; }
                points.Add(part[i]);
            }
        }

        private static void RemoveDuplicates(List<DVec3> points, double eps)
        {
            for (int i = points.Count - 1; i > 0; i--)
            {
                if ((points[i] - points[i - 1]).Length <= eps) { points.RemoveAt(i); }
            }
        }

        private void IndexedPolyCurve(object[] a, List<DVec3> points)
        {
            List<DVec3> coords = PointList(StepFile.Ref(StepFile.Arg(a, 0)));
            object[] segments = StepFile.List(StepFile.Arg(a, 1));
            if (segments.Length == 0)
            {
                points.AddRange(coords);
                return;
            }
            foreach (object s in segments)
            {
                if (s is not StepTyped typed) { continue; }
                int[] indices = StepFile.List(typed.Value).Select(v => (int)StepFile.Number(v, 0) - 1).ToArray();
                if (indices.Any(i => i < 0 || i >= coords.Count)) { continue; }
                var part = new List<DVec3>();
                if (typed.Type == "IFCARCINDEX" && indices.Length == 3)
                {
                    ArcThrough(coords[indices[0]], coords[indices[1]], coords[indices[2]], part);
                }
                else
                {
                    foreach (int i in indices) { part.Add(coords[i]); }
                }
                Join(points, part);
            }
        }

        /// <summary>The arc from p0 through p1 to p2 (a straight line when they're in a row).</summary>
        private void ArcThrough(DVec3 p0, DVec3 p1, DVec3 p2, List<DVec3> output)
        {
            DVec3 u = p1 - p0, v = p2 - p0;
            DVec3 w = DVec3.Cross(u, v);
            double w2 = w.LengthSquared;
            if (w2 < 1e-24)
            {
                output.Add(p0);
                output.Add(p2);
                return;
            }
            // Circumcentre
            DVec3 centre = p0 + (DVec3.Cross(w, u) * v.LengthSquared + DVec3.Cross(v, w) * u.LengthSquared) / (2 * w2);
            DVec3 normal = w / Math.Sqrt(w2);
            DVec3 x = (p0 - centre).Normalized(DVec3.UnitX);
            DVec3 y = DVec3.Cross(normal, x);
            double radius = (p0 - centre).Length;
            double Angle(DVec3 p) => Math.Atan2(DVec3.Dot(p - centre, y), DVec3.Dot(p - centre, x));
            double sweep = Angle(p2);
            if (sweep <= 0) { sweep += 2 * Math.PI; }
            int n = Segments(radius, sweep);
            for (int i = 0; i <= n; i++)
            {
                double t = sweep * i / n;
                output.Add(centre + (x * Math.Cos(t) + y * Math.Sin(t)) * radius);
            }
        }

        private void TrimmedCurve(object[] a, List<DVec3> points)
        {
            int basis = StepFile.Ref(StepFile.Arg(a, 0));
            object[] trim1 = StepFile.List(StepFile.Arg(a, 1));
            object[] trim2 = StepFile.List(StepFile.Arg(a, 2));
            bool sense = StepFile.Bool(StepFile.Arg(a, 3), true);
            string basisType = _file.TypeOf(basis);
            object[] b = _file.Args(basis);

            bool hasPoint1 = TrimPoint(trim1, out DVec3 point1), hasPoint2 = TrimPoint(trim2, out DVec3 point2);
            bool hasParam1 = TrimParameter(trim1, out double param1), hasParam2 = TrimParameter(trim2, out double param2);

            switch (basisType)
            {
                case "IFCLINE":
                {
                    DVec3 origin = Point(StepFile.Ref(StepFile.Arg(b, 0)));
                    object[] vector = _file.Args(StepFile.Ref(StepFile.Arg(b, 1)));
                    DVec3 direction = Direction(StepFile.Ref(StepFile.Arg(vector, 0)), DVec3.UnitX).Normalized(DVec3.UnitX) * StepFile.Number(StepFile.Arg(vector, 1), 1.0);
                    DVec3 start = hasPoint1 ? point1 : origin + direction * (hasParam1 ? param1 : 0);
                    DVec3 end = hasPoint2 ? point2 : origin + direction * (hasParam2 ? param2 : 1);
                    points.Add(start);
                    points.Add(end);
                    return;
                }
                case "IFCCIRCLE":
                case "IFCELLIPSE":
                {
                    DXform position = Axis2Placement(StepFile.Ref(StepFile.Arg(b, 0)));
                    double ra = StepFile.Number(StepFile.Arg(b, 1), 0);
                    double rb = basisType == "IFCELLIPSE" ? StepFile.Number(StepFile.Arg(b, 2), ra) : ra;
                    if (!(ra > 0) || !(rb > 0)) { return; }

                    // Points on the curve when given (they don't depend on how the writer read the angle unit)
                    double t0 = hasPoint1 ? ConicAngle(position, ra, rb, point1) : hasParam1 ? param1 * _angleUnit : 0;
                    double t1 = hasPoint2 ? ConicAngle(position, ra, rb, point2) : hasParam2 ? param2 * _angleUnit : 2 * Math.PI;
                    if (sense) { while (t1 <= t0 + 1e-9) { t1 += 2 * Math.PI; } }
                    else { while (t1 >= t0 - 1e-9) { t1 -= 2 * Math.PI; } }
                    int n = Segments(Math.Max(ra, rb), t1 - t0);
                    for (int i = 0; i <= n; i++)
                    {
                        double t = t0 + (t1 - t0) * i / n;
                        points.Add(position.Apply(new DVec3(ra * Math.Cos(t), rb * Math.Sin(t), 0)));
                    }
                    if (hasPoint1) { points[0] = point1; }
                    if (hasPoint2) { points[^1] = point2; }
                    return;
                }
                default:
                {
                    // Other bases (polylines, splines): the whole curve, ends snapped to the trim points
                    List<DVec3> whole = Curve(basis);
                    if (whole.Count == 0) { return; }
                    if (!sense) { whole.Reverse(); }
                    points.AddRange(whole);
                    if (hasPoint1) { points[0] = point1; }
                    if (hasPoint2) { points[^1] = point2; }
                    return;
                }
            }
        }

        private bool TrimPoint(object[] trims, out DVec3 point)
        {
            foreach (object t in trims)
            {
                int id = StepFile.Ref(t);
                if (id != 0 && _file.TypeOf(id) == "IFCCARTESIANPOINT")
                {
                    point = Point(id);
                    return true;
                }
            }
            point = DVec3.Zero;
            return false;
        }

        private static bool TrimParameter(object[] trims, out double value)
        {
            foreach (object t in trims)
            {
                if (t is StepTyped typed && typed.Type == "IFCPARAMETERVALUE")
                {
                    value = StepFile.Number(typed.Value);
                    if (double.IsFinite(value)) { return true; }
                }
            }
            value = 0;
            return false;
        }

        /// <summary>The parameter angle of a point on a circle / ellipse.</summary>
        private static double ConicAngle(in DXform position, double ra, double rb, DVec3 p)
        {
            DVec3 d = p - position.T;
            double u = DVec3.Dot(d, position.X.Normalized(DVec3.UnitX)), v = DVec3.Dot(d, position.Y.Normalized(DVec3.UnitY));
            return Math.Atan2(v / rb, u / ra);
        }

        /// <summary>A B-spline (rational or not) sampled evenly in its parameter (de Boor).</summary>
        private void BSpline(string type, object[] a, List<DVec3> points)
        {
            int degree = (int)StepFile.Number(StepFile.Arg(a, 0), 1);
            DVec3[] control = StepFile.List(StepFile.Arg(a, 1)).Select(p => Point(StepFile.Ref(p))).ToArray();
            int[] multiplicities = StepFile.List(StepFile.Arg(a, 5)).Select(v => (int)StepFile.Number(v, 1)).ToArray();
            double[] knotValues = StepFile.List(StepFile.Arg(a, 6)).Select(v => StepFile.Number(v, 0)).ToArray();
            double[] weights = type == "IFCRATIONALBSPLINECURVEWITHKNOTS" ? StepFile.List(StepFile.Arg(a, 8)).Select(v => StepFile.Number(v, 1)).ToArray() : null;

            int n = control.Length;
            if (n < 2 || degree < 1)
            {
                points.AddRange(control);
                return;
            }
            var knots = new List<double>();
            for (int i = 0; i < knotValues.Length && i < multiplicities.Length; i++)
            {
                for (int m = 0; m < multiplicities[i]; m++) { knots.Add(knotValues[i]); }
            }
            if (knots.Count != n + degree + 1 || (weights != null && weights.Length != n))
            {
                points.AddRange(control); // inconsistent: the control polygon
                return;
            }

            double u0 = knots[degree], u1 = knots[n];
            int samples = Math.Clamp(n * 8, 16, 256);
            var d = new double[degree + 1, 4];
            for (int s = 0; s <= samples; s++)
            {
                double u = u0 + (u1 - u0) * s / samples;
                int k = degree;
                while (k < n - 1 && u >= knots[k + 1]) { k++; }
                for (int j = 0; j <= degree; j++)
                {
                    DVec3 p = control[k - degree + j];
                    double w = weights != null ? weights[k - degree + j] : 1.0;
                    d[j, 0] = p.X * w;
                    d[j, 1] = p.Y * w;
                    d[j, 2] = p.Z * w;
                    d[j, 3] = w;
                }
                for (int r = 1; r <= degree; r++)
                {
                    for (int j = degree; j >= r; j--)
                    {
                        double denominator = knots[j + 1 + k - r] - knots[j + k - degree];
                        double alpha = denominator > 0 ? (u - knots[j + k - degree]) / denominator : 0;
                        for (int c = 0; c < 4; c++) { d[j, c] = (1 - alpha) * d[j - 1, c] + alpha * d[j, c]; }
                    }
                }
                double weight = d[degree, 3] != 0 ? d[degree, 3] : 1.0;
                points.Add(new DVec3(d[degree, 0] / weight, d[degree, 1] / weight, d[degree, 2] / weight));
            }
        }

        #endregion
    }
}
