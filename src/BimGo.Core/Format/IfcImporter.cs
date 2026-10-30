using System.Globalization;
using System.Numerics;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// What an IFC import found and left out (for the start notice and the log).
    /// </summary>
    public sealed class IfcImportSummary
    {
        /// <summary>The schema (e.g. "IFC2X3", "IFC4").</summary>
        public string Schema { get; set; } = string.Empty;

        /// <summary>The application that wrote the file (e.g. "Autodesk Revit 2025 (ENU)"), or empty.</summary>
        public string Application { get; set; } = string.Empty;

        /// <summary>The view definition named in the header (e.g. "CoordinationView_V2.0"), or empty.</summary>
        public string ViewDefinition { get; set; } = string.Empty;

        /// <summary>Elements with geometry brought in.</summary>
        public int Elements { get; set; }

        /// <summary>Triangles brought in.</summary>
        public int Triangles { get; set; }

        /// <summary>Building storeys (levels).</summary>
        public int Storeys { get; set; }

        /// <summary>Spaces brought in as rooms.</summary>
        public int Rooms { get; set; }

        /// <summary>Grid lines.</summary>
        public int Grids { get; set; }

        /// <summary>Openings cut from their host.</summary>
        public int OpeningsCut { get; set; }

        /// <summary>Products that had a 3D body BimGo couldn't turn into triangles.</summary>
        public int EmptyElements { get; set; }

        /// <summary>Cuts (openings, clipping) skipped because the solid was too large.</summary>
        public int SkippedCuts { get; set; }

        /// <summary>Geometry types left out, with counts.</summary>
        public Dictionary<string, int> Unsupported { get; } = new(StringComparer.Ordinal);

        /// <summary>How long the import took.</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// One line for the walkthrough's start notice: what came in, what didn't, and the reminder to save.
        /// </summary>
        public string Notice()
        {
            string text = $"IFC imported: {Elements:N0} elements, {Storeys} level{(Storeys == 1 ? "" : "s")}, {Rooms} room{(Rooms == 1 ? "" : "s")}.";
            int skipped = Unsupported.Values.Sum();
            if (skipped > 0 || EmptyElements > 0)
            {
                text += $" Some geometry was left out ({skipped + EmptyElements:N0} item{(skipped + EmptyElements == 1 ? "" : "s")}; see the log).";
            }
            return text + " Save as .bimgo to keep comments, bookmarks and edits.";
        }
    }

    /// <summary>
    /// Imports an IFC file (IFC2X3 / IFC4 / IFC4X3, .ifc or .ifczip) as a new, unsaved BimGo document. Written with Revit's
    /// IFC exporter in mind, which is how Revit LT (no add-ins) gets a model into BimGo: elements keep their IFC GUID, name,
    /// type, level and property sets (shown by Scan, usable in search sets and colour profiles); storeys become levels,
    /// spaces become rooms, grids become grids, the site's latitude / longitude and true north drive the sun. Openings are
    /// cut from their hosts, clipped walls are clipped. Elements are read-only in spirit (nothing can be pushed back to an
    /// IFC), but every walkthrough tool works and the edits are kept in the saved .bimgo's journal. Never throws.
    /// </summary>
    public static class IfcImporter
    {
        #region Constants

        /// <summary>The extensions the importer reads.</summary>
        public static readonly string[] EXTENSIONS = { ".ifc", ".ifczip" };

        /// <summary>Default colour when an element has no style or material (light grey, as the Revit extractor).</summary>
        private const uint DEFAULT_COLOUR = 0xFFC8C8C8;

        /// <summary>Alpha for glass that has no style (≈ 55 % transparent, as the Revit extractor's keyword glass).</summary>
        private const byte GLASS_ALPHA = 115;

        /// <summary>Normals of neighbouring triangles closer than this angle are smoothed (curved surfaces).</summary>
        private static readonly float CREASE_COS = MathF.Cos(40f * MathF.PI / 180f);

        #endregion

        /// <summary>True when a path is an IFC file the importer reads (.ifc, .ifczip).</summary>
        public static bool HasExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) { return false; }
            string extension = Path.GetExtension(path);
            return EXTENSIONS.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Imports an IFC file.
        /// </summary>
        /// <param name="path">The .ifc / .ifczip file.</param>
        /// <param name="settings">The launch settings handed to the scene.</param>
        /// <param name="error">A short reason on failure, else null.</param>
        /// <param name="summary">What was imported and left out (null on failure).</param>
        /// <param name="progress">Optional progress / cancellation (the caller starts the stage).</param>
        /// <returns>A new document with no path (save it as .bimgo), or null.</returns>
        public static BimGoDocument Import(string path, LaunchSettings settings, out string error, out IfcImportSummary summary, Utilities.OperationProgress progress = null)
        {
            error = null;
            summary = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                progress?.Begin("Reading the IFC file", 0.0, 0.25);
                StepFile file = StepFile.Read(path, progress);
                progress?.ThrowIfCancelled();
                if (!file.Schema.StartsWith("IFC", StringComparison.Ordinal) && file.OfType("IFCPROJECT").Count == 0)
                {
                    throw new InvalidDataException("This STEP file is not an IFC model.");
                }

                var import = new ImportRun(file, path, settings ?? LaunchSettings.LoadOrDefault(), progress);
                BimGoDocument document = import.Run();
                import.Summary.Duration = clock.Elapsed;
                summary = import.Summary;
                Utilities.Log_Utils.Write($"IFC import of {path}: {summary.Elements} elements, {summary.Triangles:N0} triangles, {summary.Storeys} storeys, "
                    + $"{summary.Rooms} rooms, {summary.Grids} grids, {summary.OpeningsCut} openings cut, {summary.EmptyElements} empty, {summary.SkippedCuts} cuts skipped "
                    + $"({summary.Schema}, {summary.Application}, {summary.ViewDefinition}) in {clock.Elapsed.TotalSeconds:0.0} s"
                    + (summary.Unsupported.Count > 0 ? "; left out: " + string.Join(", ", summary.Unsupported.Select(p => $"{p.Key} × {p.Value}")) : string.Empty));
                return document;
            }
            catch (OperationCanceledException)
            {
                error = "Cancelled.";
                return null;
            }
            catch (Exception ex)
            {
                error = ex switch
                {
                    FileNotFoundException => "The file was not found.",
                    DirectoryNotFoundException => "The folder was not found.",
                    UnauthorizedAccessException => "Access to the file was denied.",
                    IOException io => io.Message,
                    InvalidDataException bad => bad.Message,
                    OutOfMemoryException => "The model is too large to import on this PC.",
                    _ => ex.Message
                };
                Utilities.Log_Utils.Write($"IFC import of {path} failed: {ex}");
                return null;
            }
        }

        #region Categories

        /// <summary>
        /// IFC classes BimGo knows: the class name as written in IFC (for Scan) and the catalog category. Unknown element
        /// classes become generic models.
        /// </summary>
        private static readonly Dictionary<string, (string Name, string Category)> CLASSES = BuildClasses();

        private static Dictionary<string, (string, string)> BuildClasses()
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            void Add(string category, params string[] names)
            {
                foreach (string name in names) { map[name.ToUpperInvariant()] = (name, category); }
            }
            Add("walls", "IfcWall", "IfcWallStandardCase", "IfcWallElementedCase", "IfcCurtainWall");
            Add("floors", "IfcSlab", "IfcSlabStandardCase", "IfcSlabElementedCase");
            Add("roofs", "IfcRoof");
            Add("ceilings", "IfcCovering");
            Add("curtainpanels", "IfcPlate", "IfcPlateStandardCase");
            Add("mullions", "IfcMember", "IfcMemberStandardCase");
            Add(CategoryCatalog.KEY_DOORS, "IfcDoor", "IfcDoorStandardCase");
            Add("windows", "IfcWindow", "IfcWindowStandardCase");
            Add(CategoryCatalog.KEY_STAIRS, "IfcStair", "IfcStairFlight");
            Add("ramps", "IfcRamp", "IfcRampFlight");
            Add("columns", "IfcColumn", "IfcColumnStandardCase");
            Add("framing", "IfcBeam", "IfcBeamStandardCase");
            Add("foundations", "IfcFooting", "IfcPile", "IfcDeepFoundation", "IfcCaissonFoundation");
            Add("topo", "IfcSite", "IfcGeographicElement", "IfcEarthworksFill", "IfcEarthworksCut");
            Add("furniture", "IfcFurnishingElement", "IfcFurniture");
            Add("furnsys", "IfcSystemFurnitureElement");
            Add(CategoryCatalog.KEY_GENERIC, "IfcBuildingElementProxy", "IfcBuildingElementPart", "IfcElementAssembly", "IfcChimney", "IfcShadingDevice",
                "IfcDiscreteAccessory", "IfcFastener", "IfcMechanicalFastener", "IfcElementComponent", "IfcProxy", "IfcCivilElement",
                "IfcBuiltElement");
            Add("specialty", "IfcTransportElement");
            Add("railings", "IfcRailing");
            Add("signage", "IfcSign", "IfcSignal");
            Add("plumbing", "IfcSanitaryTerminal", "IfcWasteTerminal", "IfcStackTerminal");
            Add("lightfixtures", "IfcLightFixture", "IfcLamp");
            Add("elecfixtures", "IfcOutlet", "IfcSwitchingDevice", "IfcJunctionBox");
            Add("elecequipment", "IfcElectricAppliance", "IfcElectricDistributionBoard", "IfcElectricDistributionPoint", "IfcTransformer",
                "IfcElectricGenerator", "IfcElectricMotor", "IfcProtectiveDevice", "IfcElectricFlowStorageDevice", "IfcAudioVisualAppliance");
            Add("mechequipment", "IfcEnergyConversionDevice", "IfcFlowMovingDevice", "IfcFlowStorageDevice", "IfcFlowTreatmentDevice", "IfcFlowController",
                "IfcUnitaryEquipment", "IfcBoiler", "IfcChiller", "IfcCoil", "IfcCompressor", "IfcCondenser", "IfcCoolingTower", "IfcFan", "IfcPump",
                "IfcAirToAirHeatRecovery", "IfcHeatExchanger", "IfcHumidifier", "IfcSpaceHeater", "IfcTank", "IfcValve", "IfcDamper", "IfcFilter",
                "IfcInterceptor", "IfcMedicalDevice", "IfcBurner", "IfcEngine", "IfcEvaporativeCooler", "IfcEvaporator", "IfcTubeBundle", "IfcFlowMeter");
            Add("airterminals", "IfcAirTerminal", "IfcAirTerminalBox");
            Add("sprinklers", "IfcFireSuppressionTerminal");
            Add("security", "IfcAlarm", "IfcSensor", "IfcActuator", "IfcController", "IfcUnitaryControlElement", "IfcDistributionControlElement");
            Add("comms", "IfcCommunicationsAppliance");
            Add("ducts", "IfcDuctSegment", "IfcDuctFitting", "IfcDuctSilencer");
            Add("pipes", "IfcPipeSegment", "IfcPipeFitting");
            Add("cabletrays", "IfcCableCarrierSegment", "IfcCableCarrierFitting");
            Add("conduits", "IfcCableSegment", "IfcCableFitting");
            Add(CategoryCatalog.KEY_GENERIC, "IfcFlowTerminal", "IfcFlowSegment", "IfcFlowFitting", "IfcDistributionElement", "IfcDistributionFlowElement",
                "IfcDistributionChamberElement");
            return map;
        }

        /// <summary>
        /// IFC2X3 type objects that say what a generic distribution element is (IfcFlowTerminal + IfcLightFixtureType…).
        /// </summary>
        private static readonly Dictionary<string, string> TYPE_CATEGORIES = new(StringComparer.Ordinal)
        {
            ["IFCLIGHTFIXTURETYPE"] = "lightfixtures", ["IFCLAMPTYPE"] = "lightfixtures",
            ["IFCSANITARYTERMINALTYPE"] = "plumbing", ["IFCWASTETERMINALTYPE"] = "plumbing",
            ["IFCAIRTERMINALTYPE"] = "airterminals", ["IFCAIRTERMINALBOXTYPE"] = "airterminals",
            ["IFCFIRESUPPRESSIONTERMINALTYPE"] = "sprinklers",
            ["IFCOUTLETTYPE"] = "elecfixtures", ["IFCSWITCHINGDEVICETYPE"] = "elecfixtures", ["IFCJUNCTIONBOXTYPE"] = "elecfixtures",
            ["IFCELECTRICAPPLIANCETYPE"] = "elecequipment", ["IFCELECTRICDISTRIBUTIONBOARDTYPE"] = "elecequipment",
            ["IFCDUCTSEGMENTTYPE"] = "ducts", ["IFCDUCTFITTINGTYPE"] = "ducts", ["IFCDUCTSILENCERTYPE"] = "ducts",
            ["IFCPIPESEGMENTTYPE"] = "pipes", ["IFCPIPEFITTINGTYPE"] = "pipes",
            ["IFCCABLECARRIERSEGMENTTYPE"] = "cabletrays", ["IFCCABLECARRIERFITTINGTYPE"] = "cabletrays",
            ["IFCCABLESEGMENTTYPE"] = "conduits", ["IFCCABLEFITTINGTYPE"] = "conduits",
            ["IFCSENSORTYPE"] = "security", ["IFCALARMTYPE"] = "firealarm",
            ["IFCCOMMUNICATIONSAPPLIANCETYPE"] = "comms",
            ["IFCFURNITURETYPE"] = "furniture", ["IFCFURNISHINGELEMENTTYPE"] = "furniture", ["IFCSYSTEMFURNITUREELEMENTTYPE"] = "furnsys"
        };

        /// <summary>Words in a proxy's name that make it Planting (Revit's RPC trees and shrubs come through as proxies).</summary>
        private static readonly string[] PLANT_WORDS = { "tree", "shrub", "palm", "bush", "hedge", "planting", "plant:", "plant -", "flower bed", "grass clump" };

        /// <summary>Product classes that are never drawn (spatial structure, openings, annotation, ports…).</summary>
        private static readonly HashSet<string> NOT_DRAWN = new(StringComparer.Ordinal)
        {
            "IFCSPACE", "IFCOPENINGELEMENT", "IFCOPENINGSTANDARDCASE", "IFCVOIDINGFEATURE", "IFCANNOTATION", "IFCGRID", "IFCBUILDING",
            "IFCBUILDINGSTOREY", "IFCDISTRIBUTIONPORT", "IFCPORT", "IFCVIRTUALELEMENT", "IFCEXTERNALSPATIALELEMENT", "IFCSPATIALZONE",
            "IFCALIGNMENT", "IFCFACILITY", "IFCFACILITYPART", "IFCBRIDGE", "IFCROAD", "IFCRAILWAY", "IFCMARINEFACILITY", "IFCREFERENT",
            "IFCLINEARPOSITIONINGELEMENT", "IFCSTRUCTURALCURVEMEMBER", "IFCSTRUCTURALSURFACEMEMBER", "IFCSTRUCTURALPOINTCONNECTION",
            "IFCSTRUCTURALCURVECONNECTION", "IFCSTRUCTURALSURFACECONNECTION", "IFCSTRUCTURALPOINTACTION", "IFCSTRUCTURALLINEARACTION",
            "IFCSTRUCTURALPLANARACTION", "IFCSTRUCTURALPOINTREACTION", "IFCSTRUCTURALCURVEREACTION", "IFCSTRUCTURALSURFACEREACTION",
            "IFCSTRUCTURALCURVEMEMBERVARYING", "IFCSTRUCTURALSURFACEMEMBERVARYING", "IFCSTRUCTURALCURVEACTION", "IFCSTRUCTURALSURFACEACTION",
            "IFCPROJECTIONELEMENT", "IFCSURFACEFEATURE", "IFCGEOTECHNICALSTRATUM", "IFCBOREHOLE", "IFCGEOMODEL", "IFCGEOSLICE",
            // Reinforcement: hidden in the concrete and heavy (Revit extraction has no rebar category either)
            "IFCREINFORCINGBAR", "IFCREINFORCINGMESH", "IFCTENDON", "IFCTENDONANCHOR", "IFCTENDONCONDUIT", "IFCREINFORCINGELEMENT"
        };

        #endregion

        /// <summary>
        /// One import run: the file, its units and relationships, and the scene being built.
        /// </summary>
        private sealed class ImportRun
        {
            #region Fields

            private readonly StepFile _file;
            private readonly string _path;
            private readonly LaunchSettings _settings;
            private readonly Utilities.OperationProgress _progress;
            public readonly IfcImportSummary Summary = new();

            // Units: metres / radians / m² / m³ per file unit, and display symbols
            private double _length = 1.0, _angle = 1.0, _area = 1.0, _volume = 1.0;
            private string _lengthSymbol = "m", _areaSymbol = "m²", _volumeSymbol = "m³";

            private IfcGeometry _geometry;

            // The geometry carries a surface token per triangle instead of a colour (1-based into _surfaces): the colour
            // and the material name it was styled with (null: the element's material), resolved when the element's
            // triangles go into the scene. Materials round: the names become the material table.
            private readonly List<(uint Colour, string Material)> _surfaces = new();
            private readonly Dictionary<(uint, string), uint> _surfaceTokens = new();
            private readonly Dictionary<int, uint> _itemColours = new();      // styled item → surface token
            private readonly Dictionary<int, uint> _materialColours = new();
            private readonly Dictionary<int, int> _materialOf = new();         // object (element or type) → IfcRelAssociatesMaterial's material select
            private readonly Dictionary<int, int> _container = new();          // element → spatial structure element
            private readonly Dictionary<int, int> _parent = new();             // part → aggregate (IfcRelAggregates / IfcRelNests)
            private readonly Dictionary<int, List<int>> _children = new();     // aggregate → parts
            private readonly Dictionary<int, List<int>> _openings = new();     // host → openings
            private readonly Dictionary<int, int> _openingHost = new();        // opening → host
            private readonly Dictionary<int, int> _filledOpening = new();      // door / window → the opening it fills
            private readonly Dictionary<int, int> _typeOf = new();             // element → type object
            private readonly Dictionary<int, List<int>> _propertySets = new(); // object → property definitions

            // Output
            private readonly List<SceneVertex> _vertices = new(1 << 16);
            private readonly List<ushort> _vertexMaterial = new(1 << 16);       // materials round: per vertex, as the Revit export
            private readonly List<Vector2> _vertexUv = new(1 << 16);
            private readonly List<SceneMaterial> _materials = new();
            private readonly Dictionary<string, ushort> _materialIndex = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<uint> _indices = new(1 << 17);
            private readonly List<ElementRecord> _elements = new();
            private readonly Dictionary<int, long> _elementIdOf = new();       // IFC instance → ElementRecord.ElementId (= the instance number)
            private readonly ParameterTableBuilder _parameters = new();
            private readonly List<EntourageSlot> _entourageSlots = new();
            private Vector3 _originMetres;
            private DVec3 _origin;

            #endregion

            public ImportRun(StepFile file, string path, LaunchSettings settings, Utilities.OperationProgress progress)
            {
                _file = file;
                _path = path;
                _settings = settings;
                _progress = progress;
                Summary.Schema = file.Schema;
                Summary.Application = !string.IsNullOrWhiteSpace(file.OriginatingSystem) ? file.OriginatingSystem
                    : StepFile.Text(StepFile.Arg(file.Args(file.OfType("IFCAPPLICATION").FirstOrDefault()), 2))?.Trim() ?? string.Empty;
                int view = file.Description.IndexOf("ViewDefinition", StringComparison.OrdinalIgnoreCase);
                if (view >= 0)
                {
                    int open = file.Description.IndexOf('[', view), close = open >= 0 ? file.Description.IndexOf(']', open) : -1;
                    if (open >= 0 && close > open) { Summary.ViewDefinition = file.Description.Substring(open + 1, close - open - 1).Trim(); }
                }
            }

            /// <summary>Builds the document.</summary>
            public BimGoDocument Run()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _progress?.Begin("Reading units, styles and relationships", 0.25, 0.3);
                ReadUnits();
                ReadStyles();
                ReadRelationships();
                _geometry = new IfcGeometry(_file, _length, _angle, _itemColours);
                _progress?.ThrowIfCancelled();

                List<int> products = FindProducts();
                ChooseOrigin(products);

                _progress?.Begin("Building the geometry", 0.3, 0.92);
                int done = 0;
                foreach (int product in products)
                {
                    if ((++done & 15) == 0)
                    {
                        _progress?.Step(done, products.Count);
                        _progress?.ThrowIfCancelled();
                    }
                    try { AddProduct(product); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Utilities.Log_Utils.Write($"IFC #{product} ({_file.TypeOf(product)}) skipped: {ex.Message}"); }
                }
                if (_elements.Count == 0) { throw new InvalidDataException("The IFC file has no 3D geometry BimGo can show."); }

                _progress?.Begin("Levels, rooms and grids", 0.92, 1.0);
                LevelInfo[] levels = ReadLevels();
                RoomInfo[] rooms = ReadRooms();
                GridLine[] grids = ReadGrids();
                SiteInfo site = ReadSite();
                _progress?.Step(1.0);

                foreach ((string type, int count) in _geometry.Unsupported) { Summary.Unsupported[type] = count; }
                Summary.SkippedCuts = _geometry.SkippedBooleans;
                Summary.Storeys = levels.Length;
                Summary.Rooms = rooms.Length;
                Summary.Grids = grids.Length;

                int categories = CategoryCatalog.All.Count;
                bool[] loaded = new bool[categories];
                int[] counts = new int[categories];
                Aabb bounds = Aabb.Empty;
                foreach (ElementRecord record in _elements)
                {
                    loaded[record.CategoryIndex] = true;
                    counts[record.CategoryIndex]++;
                    bounds.Include(record.Bounds);
                }

                string title = Path.GetFileNameWithoutExtension(_path);
                var scene = new SceneData
                {
                    Vertices = _vertices.ToArray(),
                    Materials = _materials.Count == 0 ? MaterialData.Empty : new MaterialData
                    {
                        Materials = _materials.ToArray(),
                        VertexMaterial = _vertexMaterial.ToArray(),
                        VertexUv = _vertexUv.ToArray(),
                        TextureMaxSize = MaterialData.IFC_TEXTURE_SIZE
                    },
                    Indices = _indices.ToArray(),
                    Elements = _elements.ToArray(),
                    Levels = levels,
                    Rooms = rooms,
                    Grids = grids,
                    EntourageSlots = _entourageSlots.ToArray(),
                    Spawn = null, // a random clear spot, as for any model without a saved view
                    Bounds = bounds,
                    OriginOffset = _originMetres,
                    ModelTitle = title,
                    Provenance = new ModelProvenance
                    {
                        ModelTitle = ProjectName() ?? title,
                        ModelPath = Path.GetFullPath(_path),
                        ModelKey = "ifc:" + (ProjectGuid() ?? title), // never matches a Revit model, so nothing can be pushed
                        RevitVersion = Summary.Application,
                        User = Environment.UserName,
                        Machine = Environment.MachineName,
                        ExtractedUtc = DateTime.UtcNow
                    },
                    Site = site,
                    Parameters = _parameters.Build(),
                    CategoryLoaded = loaded,
                    CategoryElementCounts = counts,
                    Settings = _settings,
                    ExtractionTime = clock.Elapsed
                };
                Summary.Elements = _elements.Count;
                Summary.Triangles = scene.TriangleCount;

                return new BimGoDocument
                {
                    Scene = scene,
                    CreatedUtc = DateTime.UtcNow,
                    Kind = FileKinds.IFC_IMPORT,
                    Path = null
                };
            }

            #region Units

            /// <summary>The project's length, angle, area and volume units (IfcProject.UnitsInContext).</summary>
            private void ReadUnits()
            {
                int project = _file.OfType("IFCPROJECT").FirstOrDefault();
                int assignment = StepFile.Ref(StepFile.Arg(_file.Args(project), 8));
                foreach (object u in StepFile.List(StepFile.Arg(_file.Args(assignment), 0)))
                {
                    int unit = StepFile.Ref(u);
                    string unitType = StepFile.Enum(StepFile.Arg(_file.Args(unit), 1));
                    double scale = UnitScale(unit, out string symbol);
                    if (!(scale > 0) || !double.IsFinite(scale)) { continue; }
                    switch (unitType)
                    {
                        case "LENGTHUNIT": _length = scale; _lengthSymbol = symbol; break;
                        case "PLANEANGLEUNIT": _angle = scale; break;
                        case "AREAUNIT": _area = scale; _areaSymbol = symbol; break;
                        case "VOLUMEUNIT": _volume = scale; _volumeSymbol = symbol; break;
                    }
                }
            }

            /// <summary>An IfcSIUnit's or IfcConversionBasedUnit's size in SI (metres, radians, m², m³), and a symbol.</summary>
            private double UnitScale(int unit, out string symbol)
            {
                symbol = string.Empty;
                object[] a = _file.Args(unit);
                switch (_file.TypeOf(unit))
                {
                    case "IFCSIUNIT":
                    {
                        string prefix = StepFile.Enum(StepFile.Arg(a, 2));
                        string name = StepFile.Enum(StepFile.Arg(a, 3));
                        double factor = prefix switch
                        {
                            "EXA" => 1e18, "PETA" => 1e15, "TERA" => 1e12, "GIGA" => 1e9, "MEGA" => 1e6, "KILO" => 1e3, "HECTO" => 1e2, "DECA" => 1e1,
                            "DECI" => 1e-1, "CENTI" => 1e-2, "MILLI" => 1e-3, "MICRO" => 1e-6, "NANO" => 1e-9, "PICO" => 1e-12,
                            _ => 1.0
                        };
                        string p = prefix switch { "KILO" => "k", "DECI" => "d", "CENTI" => "c", "MILLI" => "m", "MICRO" => "µ", _ => string.Empty };
                        switch (name)
                        {
                            case "METRE": symbol = p + "m"; return factor;
                            case "SQUARE_METRE": symbol = p + "m²"; return factor * factor;
                            case "CUBIC_METRE": symbol = p + "m³"; return factor * factor * factor;
                            case "RADIAN": symbol = "rad"; return factor;
                            default: symbol = string.Empty; return factor;
                        }
                    }
                    case "IFCCONVERSIONBASEDUNIT":
                    case "IFCCONVERSIONBASEDUNITWITHOFFSET":
                    {
                        string name = StepFile.Text(StepFile.Arg(a, 2))?.Trim().ToUpperInvariant() ?? string.Empty;
                        object[] measure = _file.Args(StepFile.Ref(StepFile.Arg(a, 3)));
                        double value = StepFile.Number(StepFile.Arg(measure, 0), 1.0);
                        double baseScale = UnitScale(StepFile.Ref(StepFile.Arg(measure, 1)), out _);
                        symbol = name switch
                        {
                            "FOOT" => "ft", "INCH" => "in", "YARD" => "yd", "MILE" => "mi",
                            "SQUARE FOOT" or "SQUARE_FOOT" => "ft²", "SQUARE INCH" or "SQUARE_INCH" => "in²",
                            "CUBIC FOOT" or "CUBIC_FOOT" => "ft³", "CUBIC INCH" or "CUBIC_INCH" => "in³",
                            "DEGREE" => "°",
                            _ => name.ToLowerInvariant()
                        };
                        return value * (baseScale > 0 ? baseScale : 1.0);
                    }
                    default:
                        return double.NaN;
                }
            }

            #endregion

            #region Styles and materials

            /// <summary>
            /// The colour of every styled representation item, and of every material with a styled representation.
            /// </summary>
            private void ReadStyles()
            {
                foreach (int styled in _file.OfType("IFCSTYLEDITEM"))
                {
                    object[] a = _file.Args(styled);
                    int item = StepFile.Ref(StepFile.Arg(a, 0));
                    if (item == 0) { continue; }
                    uint colour = StyleColour(StepFile.List(StepFile.Arg(a, 1)), 0, out string name);
                    if (colour != 0) { _itemColours[item] = Surface(colour, name); }
                }

                foreach (int definition in _file.OfType("IFCMATERIALDEFINITIONREPRESENTATION"))
                {
                    object[] a = _file.Args(definition);
                    int material = StepFile.Ref(StepFile.Arg(a, 3));
                    foreach (object r in StepFile.List(StepFile.Arg(a, 2)))
                    {
                        foreach (object item in StepFile.List(StepFile.Arg(_file.Args(StepFile.Ref(r)), 3)))
                        {
                            object[] styled = _file.Args(StepFile.Ref(item));
                            uint colour = StyleColour(StepFile.List(StepFile.Arg(styled, 1)), 0, out _);
                            if (colour != 0 && material != 0 && !_materialColours.ContainsKey(material)) { _materialColours[material] = colour; }
                        }
                    }
                }
            }

            /// <summary>
            /// The first surface colour found in a list of styles (presentation style assignments followed), and the surface
            /// style's name: Revit names it after the material, so it is the material's name (null when it has none).
            /// </summary>
            private uint StyleColour(object[] styles, int depth, out string name)
            {
                name = null;
                if (depth > 4) { return 0; }
                foreach (object s in styles)
                {
                    int style = StepFile.Ref(s);
                    object[] a = _file.Args(style);
                    switch (_file.TypeOf(style))
                    {
                        case "IFCPRESENTATIONSTYLEASSIGNMENT":
                        {
                            uint colour = StyleColour(StepFile.List(StepFile.Arg(a, 0)), depth + 1, out name);
                            if (colour != 0) { return colour; }
                            break;
                        }
                        case "IFCSURFACESTYLE":
                            foreach (object e in StepFile.List(StepFile.Arg(a, 2)))
                            {
                                int element = StepFile.Ref(e);
                                string type = _file.TypeOf(element);
                                if (type != "IFCSURFACESTYLERENDERING" && type != "IFCSURFACESTYLESHADING") { continue; }
                                object[] shading = _file.Args(element);
                                object[] rgb = _file.Args(StepFile.Ref(StepFile.Arg(shading, 0)));
                                if (rgb.Length < 4) { continue; }
                                double transparency = StepFile.Number(StepFile.Arg(shading, 1), 0);
                                if (!double.IsFinite(transparency)) { transparency = 0; }
                                string styleName = StepFile.Text(StepFile.Arg(a, 0))?.Trim();
                                name = string.IsNullOrEmpty(styleName) ? null : styleName;
                                return Pack(StepFile.Number(rgb[1], 0.8), StepFile.Number(rgb[2], 0.8), StepFile.Number(rgb[3], 0.8), transparency);
                            }
                            break;
                    }
                }
                return 0;
            }

            /// <summary>RGBA8 from 0–1 components and a transparency (alpha 64–255, as the Revit extractor).</summary>
            private static uint Pack(double r, double g, double b, double transparency)
            {
                static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round((double.IsFinite(v) ? v : 0.8) * 255.0), 0, 255);
                int alpha = Math.Clamp(255 - (int)Math.Round(Math.Clamp(transparency, 0, 1) * 255.0), 64, 255);
                return Byte(r) | ((uint)Byte(g) << 8) | ((uint)Byte(b) << 16) | ((uint)alpha << 24);
            }

            private static uint Rgb(byte r, byte g, byte b, byte a = 255) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

            /// <summary>The token for a colour and material name (made on first use; never 0).</summary>
            private uint Surface(uint colour, string material)
            {
                var key = (colour, material);
                if (_surfaceTokens.TryGetValue(key, out uint token)) { return token; }
                _surfaces.Add(key);
                token = (uint)_surfaces.Count;
                _surfaceTokens[key] = token;
                return token;
            }

            /// <summary>The name of an element's first material (else its type's), or null.</summary>
            private string MaterialNameOf(int element)
            {
                foreach (int material in MaterialsOf(element))
                {
                    string name = StepFile.Text(StepFile.Arg(_file.Args(material), 0))?.Trim();
                    if (!string.IsNullOrEmpty(name)) { return name; }
                }
                return null;
            }

            /// <summary>
            /// The material table's index for a name (added on first use with this colour: RGB display 0–1; see-through
            /// below 250 alpha), or <see cref="MaterialData.NONE"/> when the table is full.
            /// </summary>
            private ushort MaterialIndex(string name, uint colour)
            {
                if (_materialIndex.TryGetValue(name, out ushort index)) { return index; }
                if (_materials.Count >= MaterialData.MAX_MATERIALS) { return MaterialData.NONE; }
                index = (ushort)_materials.Count;
                _materials.Add(new SceneMaterial
                {
                    Name = name,
                    MaterialId = index + 1,
                    Schema = MaterialData.IFC_SCHEMA,
                    Colour = new Vector3((colour & 0xFF) / 255f, ((colour >> 8) & 0xFF) / 255f, ((colour >> 16) & 0xFF) / 255f),
                    TextureState = TextureState.None,
                    SeeThrough = (colour >> 24) < 250
                });
                _materialIndex[name] = index;
                return index;
            }

            /// <summary>
            /// A vertex's surface coordinate (m), world-anchored as the Revit export's planar faces: plan X / Y on floors and
            /// roofs (a normal within 45° of vertical), else U along the face horizontally and V up.
            /// </summary>
            private static Vector2 SurfaceCoordinate(Vector3 p, Vector3 normal)
            {
                if (MathF.Abs(normal.Z) >= 0.7071f) { return new Vector2(p.X, p.Y); }
                var across = new Vector2(-normal.Y, normal.X);
                float length = across.Length();
                across = length > 1e-6f ? across / length : Vector2.UnitX;
                return new Vector2(p.X * across.X + p.Y * across.Y, p.Z);
            }

            /// <summary>The materials associated with an element (else its type), first with a colour first.</summary>
            private List<int> MaterialsOf(int element)
            {
                var materials = new List<int>();
                if (!_materialOf.TryGetValue(element, out int select) && !(_typeOf.TryGetValue(element, out int type) && _materialOf.TryGetValue(type, out select)))
                {
                    return materials;
                }
                CollectMaterials(select, materials, 0);
                return materials;
            }

            private void CollectMaterials(int select, List<int> materials, int depth)
            {
                if (select == 0 || depth > 4) { return; }
                object[] a = _file.Args(select);
                switch (_file.TypeOf(select))
                {
                    case "IFCMATERIAL":
                        if (!materials.Contains(select)) { materials.Add(select); }
                        break;
                    case "IFCMATERIALLAYERSETUSAGE":
                    case "IFCMATERIALPROFILESETUSAGE":
                    case "IFCMATERIALPROFILESETUSAGETAPERING":
                        CollectMaterials(StepFile.Ref(StepFile.Arg(a, 0)), materials, depth + 1);
                        break;
                    case "IFCMATERIALLAYERSET":
                        foreach (object layer in StepFile.List(StepFile.Arg(a, 0))) { CollectMaterials(StepFile.Ref(StepFile.Arg(_file.Args(StepFile.Ref(layer)), 0)), materials, depth + 1); }
                        break;
                    case "IFCMATERIALLIST":
                        foreach (object m in StepFile.List(StepFile.Arg(a, 0))) { CollectMaterials(StepFile.Ref(m), materials, depth + 1); }
                        break;
                    case "IFCMATERIALCONSTITUENTSET":
                        foreach (object c in StepFile.List(StepFile.Arg(a, 2))) { CollectMaterials(StepFile.Ref(StepFile.Arg(_file.Args(StepFile.Ref(c)), 2)), materials, depth + 1); }
                        break;
                    case "IFCMATERIALPROFILESET":
                        foreach (object p in StepFile.List(StepFile.Arg(a, 2))) { CollectMaterials(StepFile.Ref(StepFile.Arg(_file.Args(StepFile.Ref(p)), 2)), materials, depth + 1); }
                        break;
                    case "IFCMATERIALLAYER":
                    case "IFCMATERIALLAYERWITHOFFSETS":
                        CollectMaterials(StepFile.Ref(StepFile.Arg(a, 0)), materials, depth + 1);
                        break;
                }
            }

            /// <summary>
            /// The surface token an element's unstyled geometry gets: its material's colour and name, else a colour for its
            /// category (the material name left to the element's).
            /// </summary>
            private uint FallbackColour(int element, string category, string name) => Surface(FallbackRgba(element, category, name, out string material), material);

            private uint FallbackRgba(int element, string category, string name, out string material)
            {
                material = null;
                foreach (int m in MaterialsOf(element))
                {
                    if (!_materialColours.TryGetValue(m, out uint colour)) { continue; }
                    string own = StepFile.Text(StepFile.Arg(_file.Args(m), 0))?.Trim();
                    material = string.IsNullOrEmpty(own) ? null : own;
                    return colour;
                }
                bool glassy = name != null && (name.Contains("glass", StringComparison.OrdinalIgnoreCase) || name.Contains("glaz", StringComparison.OrdinalIgnoreCase));
                return category switch
                {
                    "curtainpanels" => Rgb(170, 200, 220, GLASS_ALPHA),
                    "windows" when glassy => Rgb(170, 200, 220, GLASS_ALPHA),
                    "walls" => Rgb(228, 224, 216),
                    "floors" => Rgb(190, 188, 184),
                    "roofs" => Rgb(150, 112, 100),
                    "ceilings" => Rgb(240, 240, 238),
                    CategoryCatalog.KEY_DOORS => Rgb(165, 125, 85),
                    "mullions" or "railings" => Rgb(130, 132, 136),
                    "topo" => Rgb(118, 146, 92),
                    "furniture" or "casework" or "furnsys" => Rgb(176, 146, 112),
                    _ => DEFAULT_COLOUR
                };
            }

            #endregion

            #region Relationships

            private void ReadRelationships()
            {
                foreach (int rel in _file.OfType("IFCRELCONTAINEDINSPATIALSTRUCTURE"))
                {
                    object[] a = _file.Args(rel);
                    int structure = StepFile.Ref(StepFile.Arg(a, 5));
                    foreach (object e in StepFile.List(StepFile.Arg(a, 4))) { _container[StepFile.Ref(e)] = structure; }
                }
                foreach (string type in new[] { "IFCRELAGGREGATES", "IFCRELNESTS" })
                {
                    foreach (int rel in _file.OfType(type))
                    {
                        object[] a = _file.Args(rel);
                        int whole = StepFile.Ref(StepFile.Arg(a, 4));
                        foreach (object p in StepFile.List(StepFile.Arg(a, 5)))
                        {
                            int part = StepFile.Ref(p);
                            if (part == 0 || part == whole || _parent.ContainsKey(part)) { continue; }
                            _parent[part] = whole;
                            if (!_children.TryGetValue(whole, out List<int> parts)) { _children[whole] = parts = new List<int>(); }
                            parts.Add(part);
                        }
                    }
                }
                foreach (int rel in _file.OfType("IFCRELVOIDSELEMENT"))
                {
                    object[] a = _file.Args(rel);
                    int host = StepFile.Ref(StepFile.Arg(a, 4)), opening = StepFile.Ref(StepFile.Arg(a, 5));
                    if (host == 0 || opening == 0) { continue; }
                    if (!_openings.TryGetValue(host, out List<int> list)) { _openings[host] = list = new List<int>(); }
                    list.Add(opening);
                    _openingHost[opening] = host;
                }
                foreach (int rel in _file.OfType("IFCRELFILLSELEMENT"))
                {
                    object[] a = _file.Args(rel);
                    _filledOpening[StepFile.Ref(StepFile.Arg(a, 5))] = StepFile.Ref(StepFile.Arg(a, 4));
                }
                foreach (int rel in _file.OfType("IFCRELDEFINESBYTYPE"))
                {
                    object[] a = _file.Args(rel);
                    int type = StepFile.Ref(StepFile.Arg(a, 5));
                    foreach (object e in StepFile.List(StepFile.Arg(a, 4))) { _typeOf[StepFile.Ref(e)] = type; }
                }
                foreach (int rel in _file.OfType("IFCRELDEFINESBYPROPERTIES"))
                {
                    object[] a = _file.Args(rel);
                    int definition = StepFile.Ref(StepFile.Arg(a, 5));
                    if (definition == 0) { continue; }
                    foreach (object e in StepFile.List(StepFile.Arg(a, 4)))
                    {
                        int target = StepFile.Ref(e);
                        if (!_propertySets.TryGetValue(target, out List<int> list)) { _propertySets[target] = list = new List<int>(); }
                        list.Add(definition);
                    }
                }
                foreach (int rel in _file.OfType("IFCRELASSOCIATESMATERIAL"))
                {
                    object[] a = _file.Args(rel);
                    int material = StepFile.Ref(StepFile.Arg(a, 5));
                    foreach (object e in StepFile.List(StepFile.Arg(a, 4)))
                    {
                        int target = StepFile.Ref(e);
                        if (target != 0 && !_materialOf.ContainsKey(target)) { _materialOf[target] = material; }
                    }
                }
            }

            /// <summary>The storey an element is on (through its aggregate and spaces), or 0.</summary>
            private int StoreyOf(int element)
            {
                int current = element;
                for (int guard = 0; guard < 16 && current != 0; guard++)
                {
                    string type = _file.TypeOf(current);
                    if (type == "IFCBUILDINGSTOREY" && current != element) { return current; }
                    if (type == "IFCBUILDING" || type == "IFCSITE" || type == "IFCPROJECT") { return 0; }
                    if (_container.TryGetValue(current, out int structure)) { current = structure; }
                    else if (_parent.TryGetValue(current, out int whole)) { current = whole; }
                    else if (_openingHost.TryGetValue(current, out int host)) { current = host; }
                    else { return 0; }
                }
                return 0;
            }

            private string NameOf(int id) => StepFile.Text(StepFile.Arg(_file.Args(id), 2));

            #endregion

            #region Products

            /// <summary>
            /// Every product with a shape that BimGo draws: any entity type whose instances carry a GlobalId, an object placement
            /// and an IfcProductDefinitionShape (so classes BimGo doesn't list still come in, as generic models).
            /// </summary>
            private List<int> FindProducts()
            {
                var products = new List<int>();
                foreach ((string type, int _) in _file.TypeCounts())
                {
                    if (!type.StartsWith("IFC", StringComparison.Ordinal) || NOT_DRAWN.Contains(type) || type.StartsWith("IFCREL", StringComparison.Ordinal)) { continue; }
                    IReadOnlyList<int> ids = _file.OfType(type);
                    if (!IsProductType(ids)) { continue; }
                    foreach (int id in ids)
                    {
                        int shape = StepFile.Ref(StepFile.Arg(_file.Args(id), 6));
                        if (shape != 0 && _file.TypeOf(shape) == "IFCPRODUCTDEFINITIONSHAPE") { products.Add(id); }
                    }
                }
                products.Sort();
                return products;
            }

            /// <summary>True when a type's first instances look like products (GlobalId text, placement, product shape).</summary>
            private bool IsProductType(IReadOnlyList<int> ids)
            {
                for (int i = 0; i < ids.Count && i < 8; i++)
                {
                    object[] a = _file.Args(ids[i]);
                    if (a.Length < 7 || StepFile.Text(a[0]) == null) { return false; }
                    int placement = StepFile.Ref(a[5]);
                    int shape = StepFile.Ref(a[6]);
                    if (placement != 0 && !(_file.TypeOf(placement) ?? string.Empty).EndsWith("PLACEMENT", StringComparison.Ordinal)) { return false; }
                    if (shape != 0) { return _file.TypeOf(shape) == "IFCPRODUCTDEFINITIONSHAPE"; }
                }
                return false;
            }

            /// <summary>
            /// The scene origin: the median of the products' placement origins, rounded to the metre (as the Revit extractor
            /// takes the median element centre), so large site coordinates don't cost float precision.
            /// </summary>
            private void ChooseOrigin(List<int> products)
            {
                var xs = new List<double>(products.Count);
                var ys = new List<double>(products.Count);
                var zs = new List<double>(products.Count);
                IfcGeometry geometry = _geometry;
                foreach (int product in products)
                {
                    DVec3 p = geometry.Placement(StepFile.Ref(StepFile.Arg(_file.Args(product), 5))).T * _length;
                    if (!p.IsFinite) { continue; }
                    xs.Add(p.X);
                    ys.Add(p.Y);
                    zs.Add(p.Z);
                }
                static double Median(List<double> v)
                {
                    if (v.Count == 0) { return 0; }
                    v.Sort();
                    return Math.Round(v[v.Count / 2]);
                }
                _origin = new DVec3(Median(xs), Median(ys), Median(zs));
                _originMetres = new Vector3((float)_origin.X, (float)_origin.Y, (float)_origin.Z);
            }

            /// <summary>A world point (file units) as scene-local metres.</summary>
            private Vector3 ToScene(DVec3 p) => new((float)(p.X * _length - _origin.X), (float)(p.Y * _length - _origin.Y), (float)(p.Z * _length - _origin.Z));

            /// <summary>
            /// One product: its body (openings cut), category, names, level, host and properties.
            /// </summary>
            private void AddProduct(int product)
            {
                // An aggregate whose parts carry geometry is drawn through its parts (curtain walls, stairs, roofs)
                if (_children.TryGetValue(product, out List<int> parts) && parts.Any(HasShape)) { return; }

                object[] a = _file.Args(product);
                string type = _file.TypeOf(product);
                string guid = StepFile.Text(StepFile.Arg(a, 0)) ?? string.Empty;
                string ifcName = StepFile.Text(StepFile.Arg(a, 2));
                string objectType = StepFile.Text(StepFile.Arg(a, 4));
                string predefined = a.Length > 8 ? StepFile.Enum(a[8]) : null;
                string category = CategoryOf(product, type, predefined);
                CategoryDef def = CategoryCatalog.Find(category) ?? CategoryCatalog.Find(CategoryCatalog.KEY_GENERIC);

                DXform placement = _geometry.Placement(StepFile.Ref(StepFile.Arg(a, 5)));
                List<int> items = _geometry.BodyItems(StepFile.Ref(StepFile.Arg(a, 6)));
                if (items.Count == 0) { return; }

                uint fallback = FallbackColour(product, def.Key, ifcName);
                var mesh = new IfcMesh();
                foreach (int item in items) { _geometry.AddItem(item, placement, fallback, mesh); }
                if (mesh.TriangleCount == 0)
                {
                    Summary.EmptyElements++;
                    return;
                }
                CutOpenings(product, mesh);

                string elementMaterial = MaterialNameOf(product) ?? $"{def.Label} (no material)";
                if (!BuildGeometry(mesh, elementMaterial, out int opaqueStart, out int opaqueCount, out int transparentStart, out int transparentCount, out Aabb bounds))
                {
                    Summary.EmptyElements++;
                    return;
                }

                long elementId = product;
                _elementIdOf[product] = elementId;
                int storey = StoreyOf(product);
                bool movable = def.Group != CategoryGroup.System && def.Key != "topo";
                string displayClass = CLASSES.TryGetValue(type, out var known) ? known.Name : PrettyClass(type);

                var record = new ElementRecord
                {
                    ElementId = elementId,
                    UniqueId = guid,
                    IfcGuid = IfcGuid.IsValid(guid) ? guid : string.Empty,
                    HostId = HostOf(product),
                    Name = ElementName(ifcName, objectType, displayClass),
                    CategoryName = def.Label,
                    FamilyType = FamilyType(objectType, product),
                    LevelName = storey != 0 ? NameOf(storey) ?? "—" : "—",
                    CategoryIndex = def.Index,
                    OpaqueStart = opaqueStart,
                    OpaqueCount = opaqueCount,
                    TransparentStart = transparentStart,
                    TransparentCount = transparentCount,
                    Bounds = bounds,
                    Movable = movable,
                    MoveBlockReason = movable ? null : "Imported from IFC: only furniture, fixtures and equipment can be moved",
                    Pivot = ToScene(placement.T),
                    Phase = PhaseRole.Existing
                };
                if (def.Key == "planting" || def.Key == "entourage")
                {
                    // A slot like the Revit extractor's, so billboards can stand in for Revit's RPC planting and people
                    string familyType = FamilyType(objectType, product);
                    int split = familyType.IndexOf(": ", StringComparison.Ordinal);
                    _entourageSlots.Add(new EntourageSlot
                    {
                        UniqueId = guid,
                        ElementId = elementId,
                        Category = def.Key == "planting" ? EntourageSlots.PLANTING : EntourageSlots.ENTOURAGE,
                        Family = split > 0 ? familyType.Substring(0, split) : familyType,
                        Type = split > 0 ? familyType.Substring(split + 2) : string.Empty,
                        Base = new Vector3(bounds.Center.X, bounds.Center.Y, bounds.Min.Z),
                        Height = bounds.Size.Z,
                        Width = MathF.Max(bounds.Size.X, bounds.Size.Y),
                        Element = _elements.Count
                    });
                }
                _elements.Add(record);

                // Scan's parameters: identity first, then the property sets and quantities
                _parameters.Add("IFC class", displayClass);
                string tag = StepFile.Text(StepFile.Arg(a, 7));
                if (!string.IsNullOrWhiteSpace(tag) && long.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    _parameters.Add("Revit ElementId", tag); // Revit's exporter writes the element's id as the Tag
                }
                else if (!string.IsNullOrWhiteSpace(tag)) { _parameters.Add("Tag", tag); }
                if (!string.IsNullOrWhiteSpace(predefined) && predefined is not ("NOTDEFINED" or "USERDEFINED" or "ELEMENT" or "COMPLEX" or "PARTIAL"))
                {
                    _parameters.Add("Predefined type", predefined);
                }
                string description = StepFile.Text(StepFile.Arg(a, 3));
                if (!string.IsNullOrWhiteSpace(description)) { _parameters.Add("Description", description); }
                List<string> materialNames = MaterialsOf(product).Select(m => StepFile.Text(StepFile.Arg(_file.Args(m), 0))).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                if (materialNames.Count > 0) { _parameters.Add("Material", string.Join(", ", materialNames)); }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IFC class", "Revit ElementId", "Tag", "Predefined type", "Description", "Material" };
                foreach ((string name, string value, double _) in Properties(product))
                {
                    if (seen.Add(name)) { _parameters.Add(name, value); }
                }
                _parameters.EndElement();
            }

            /// <summary>True when an object has a product shape (geometry of its own).</summary>
            private bool HasShape(int id)
            {
                object[] a = _file.Args(id);
                return a.Length > 6 && _file.TypeOf(StepFile.Ref(a[6])) == "IFCPRODUCTDEFINITIONSHAPE";
            }

            /// <summary>The catalog key for a product, from its class, predefined type, aggregate and type object.</summary>
            private string CategoryOf(int product, string type, string predefined)
            {
                string parentType = _parent.TryGetValue(product, out int whole) ? _file.TypeOf(whole) : null;
                switch (type)
                {
                    case "IFCSLAB":
                    case "IFCSLABSTANDARDCASE":
                    case "IFCSLABELEMENTEDCASE":
                        return predefined == "ROOF" || parentType == "IFCROOF" ? "roofs" : "floors";
                    case "IFCCOVERING":
                        return predefined switch { "FLOORING" => "floors", "ROOFING" => "roofs", "CLADDING" => "walls", _ => "ceilings" };
                    case "IFCMEMBER":
                    case "IFCMEMBERSTANDARDCASE":
                        return predefined == "MULLION" || parentType == "IFCCURTAINWALL" ? "mullions" : "framing";
                    case "IFCPLATE":
                    case "IFCPLATESTANDARDCASE":
                        return predefined == "CURTAIN_PANEL" || parentType == "IFCCURTAINWALL" || parentType == null ? "curtainpanels" : "walls";
                    case "IFCGEOGRAPHICELEMENT":
                        return predefined == "TERRAIN" || predefined == null ? "topo" : "planting";
                    case "IFCBUILDINGELEMENTPROXY":
                    {
                        if (parentType == "IFCCURTAINWALL") { return "curtainpanels"; }
                        // Revit exports Planting and Entourage (RPC) as proxies: their names say what they are
                        string label = (StepFile.Text(StepFile.Arg(_file.Args(product), 4)) ?? StepFile.Text(StepFile.Arg(_file.Args(product), 2)) ?? string.Empty).ToLowerInvariant();
                        if (PLANT_WORDS.Any(label.Contains)) { return "planting"; }
                        if (label.Contains("rpc") || label.Contains("entourage")) { return "entourage"; }
                        break;
                    }
                    case "IFCRAILING":
                        return "railings";
                }
                if (parentType == "IFCSTAIR" && (type == "IFCSLAB" || type == "IFCMEMBER" || type == "IFCBUILDINGELEMENTPROXY")) { return CategoryCatalog.KEY_STAIRS; }
                if (parentType == "IFCRAILING") { return "railings"; }

                // IFC2X3's generic distribution classes say what they are through their type object
                if (_typeOf.TryGetValue(product, out int typeObject) && TYPE_CATEGORIES.TryGetValue(_file.TypeOf(typeObject) ?? string.Empty, out string byType))
                {
                    return byType;
                }
                return CLASSES.TryGetValue(type, out var known) ? known.Category : CategoryCatalog.KEY_GENERIC;
            }

            /// <summary>The host a door / window / opening sits in (its ElementRecord id), or 0.</summary>
            private long HostOf(int product)
            {
                if (_filledOpening.TryGetValue(product, out int opening) && _openingHost.TryGetValue(opening, out int host))
                {
                    return _elementIdOf.TryGetValue(host, out long id) ? id : host;
                }
                return 0;
            }

            /// <summary>
            /// Cuts the product's openings (IfcRelVoidsElement) out of its triangles, one opening at a time.
            /// </summary>
            private void CutOpenings(int product, IfcMesh mesh)
            {
                if (!_openings.TryGetValue(product, out List<int> openings)) { return; }
                mesh.TryGetBounds(out DVec3 min, out DVec3 max);
                foreach (int opening in openings)
                {
                    object[] a = _file.Args(opening);
                    DXform placement = _geometry.Placement(StepFile.Ref(StepFile.Arg(a, 5)));
                    var cutter = new IfcMesh();
                    foreach (int item in _geometry.BodyItems(StepFile.Ref(StepFile.Arg(a, 6)))) { _geometry.AddItem(item, placement, 0, cutter); }
                    if (!cutter.TryGetBounds(out DVec3 cmin, out DVec3 cmax)) { continue; }
                    if (cmin.X > max.X || cmax.X < min.X || cmin.Y > max.Y || cmax.Y < min.Y || cmin.Z > max.Z || cmax.Z < min.Z) { continue; }
                    if (_geometry.SubtractInPlace(mesh, cutter)) { Summary.OpeningsCut++; }
                    if (mesh.TriangleCount == 0) { return; }
                }
            }

            /// <summary>
            /// Scan's name: Revit's "Family:Type:12345" without the id (the type name), else the IFC name, else the class.
            /// </summary>
            private static string ElementName(string ifcName, string objectType, string displayClass)
            {
                string name = ifcName?.Trim();
                if (string.IsNullOrEmpty(name)) { name = objectType?.Trim(); }
                if (string.IsNullOrEmpty(name)) { return displayClass; }
                int colon = name.LastIndexOf(':');
                if (colon > 0 && colon < name.Length - 1 && name.Substring(colon + 1).All(char.IsDigit)) { name = name.Substring(0, colon); }
                colon = name.LastIndexOf(':');
                if (colon > 0 && colon < name.Length - 1) { name = name.Substring(colon + 1); }
                return name.Trim();
            }

            /// <summary>"Family: Type" from Revit's "Family:Type" object type, else the type object's name, else an em dash.</summary>
            private string FamilyType(string objectType, int product)
            {
                string text = objectType?.Trim();
                if (string.IsNullOrEmpty(text) && _typeOf.TryGetValue(product, out int type)) { text = NameOf(type)?.Trim(); }
                if (string.IsNullOrEmpty(text)) { return "—"; }
                int last = text.LastIndexOf(':');
                if (last > 0 && text.IndexOf(':') < last && last < text.Length - 1 && text.Substring(last + 1).All(char.IsDigit)) { text = text.Substring(0, last); }
                int colon = text.IndexOf(':');
                return colon > 0 && colon < text.Length - 1 ? $"{text.Substring(0, colon).Trim()}: {text.Substring(colon + 1).Trim()}" : text;
            }

            /// <summary>"IFCWALLTYPE"-style names made readable when the class isn't in the table ("IfcWalltype").</summary>
            private static string PrettyClass(string type) =>
                string.IsNullOrEmpty(type) ? "IfcProduct" : type.Length > 3 ? "Ifc" + char.ToUpperInvariant(type[3]) + type.Substring(4).ToLowerInvariant() : type;

            #endregion

            #region Properties

            /// <summary>
            /// An object's property sets and quantities (its own, then its type's), as display text and a number where the
            /// value is numeric (NaN otherwise).
            /// </summary>
            private List<(string Name, string Value, double Number)> Properties(int target)
            {
                var result = new List<(string, string, double)>();
                if (_propertySets.TryGetValue(target, out List<int> own))
                {
                    foreach (int definition in own) { AddPropertyDefinition(definition, result); }
                }
                if (_typeOf.TryGetValue(target, out int type))
                {
                    if (_propertySets.TryGetValue(type, out List<int> typeSets))
                    {
                        foreach (int definition in typeSets) { AddPropertyDefinition(definition, result); }
                    }
                    // IfcTypeObject.HasPropertySets
                    foreach (object definition in StepFile.List(StepFile.Arg(_file.Args(type), 5))) { AddPropertyDefinition(StepFile.Ref(definition), result); }
                }
                return result;
            }

            private void AddPropertyDefinition(int definition, List<(string, string, double)> result)
            {
                object[] a = _file.Args(definition);
                switch (_file.TypeOf(definition))
                {
                    case "IFCPROPERTYSET":
                        foreach (object p in StepFile.List(StepFile.Arg(a, 4)))
                        {
                            int property = StepFile.Ref(p);
                            object[] pa = _file.Args(property);
                            string name = StepFile.Text(StepFile.Arg(pa, 0))?.Trim();
                            if (string.IsNullOrEmpty(name)) { continue; }
                            string value;
                            double number = double.NaN;
                            switch (_file.TypeOf(property))
                            {
                                case "IFCPROPERTYSINGLEVALUE":
                                    value = FormatValue(StepFile.Arg(pa, 2), out number);
                                    break;
                                case "IFCPROPERTYENUMERATEDVALUE":
                                case "IFCPROPERTYLISTVALUE":
                                    value = string.Join(", ", StepFile.List(StepFile.Arg(pa, 2)).Select(v => FormatValue(v, out _)).Where(v => v.Length > 0));
                                    break;
                                case "IFCPROPERTYBOUNDEDVALUE":
                                {
                                    string upper = FormatValue(StepFile.Arg(pa, 2), out _), lower = FormatValue(StepFile.Arg(pa, 3), out _);
                                    value = lower.Length > 0 && upper.Length > 0 ? $"{lower} – {upper}" : lower + upper;
                                    break;
                                }
                                default:
                                    continue;
                            }
                            if (value.Length > 0) { result.Add((name, value, number)); }
                        }
                        break;
                    case "IFCELEMENTQUANTITY":
                        foreach (object q in StepFile.List(StepFile.Arg(a, 5)))
                        {
                            int quantity = StepFile.Ref(q);
                            object[] qa = _file.Args(quantity);
                            string name = StepFile.Text(StepFile.Arg(qa, 0))?.Trim();
                            double raw = StepFile.Number(StepFile.Arg(qa, 3));
                            if (string.IsNullOrEmpty(name) || !double.IsFinite(raw)) { continue; }
                            (string text, double number) = _file.TypeOf(quantity) switch
                            {
                                "IFCQUANTITYLENGTH" => (Measure(raw, _lengthSymbol), raw),
                                "IFCQUANTITYAREA" => (Measure(raw, _areaSymbol), raw),
                                "IFCQUANTITYVOLUME" => (Measure(raw, _volumeSymbol), raw),
                                "IFCQUANTITYWEIGHT" => (Measure(raw, "kg"), raw),
                                _ => (Number(raw), raw)
                            };
                            result.Add((name, text, number));
                        }
                        break;
                }
            }

            /// <summary>A property value as Scan shows it (units from the project), and its number (NaN when not numeric).</summary>
            private string FormatValue(object value, out double number)
            {
                number = double.NaN;
                switch (value)
                {
                    case null:
                        return string.Empty;
                    case StepTyped typed:
                    {
                        string text = StepFile.Text(typed.Value);
                        if (text != null) { return text.Trim(); }
                        string logical = StepFile.Enum(typed.Value);
                        if (logical != null) { return logical switch { "T" => "Yes", "F" => "No", "U" => "Unknown", _ => logical }; }
                        double raw = StepFile.Number(typed.Value);
                        if (!double.IsFinite(raw)) { return string.Empty; }
                        number = raw;
                        switch (typed.Type)
                        {
                            case "IFCLENGTHMEASURE":
                            case "IFCPOSITIVELENGTHMEASURE":
                            case "IFCNONNEGATIVELENGTHMEASURE":
                                return Measure(raw, _lengthSymbol);
                            case "IFCAREAMEASURE":
                                return Measure(raw, _areaSymbol);
                            case "IFCVOLUMEMEASURE":
                                return Measure(raw, _volumeSymbol);
                            case "IFCPLANEANGLEMEASURE":
                            case "IFCPOSITIVEPLANEANGLEMEASURE":
                                number = raw * _angle * 180.0 / Math.PI;
                                return Number(number) + "°";
                            case "IFCTHERMALTRANSMITTANCEMEASURE":
                                return Number(raw) + " W/(m²·K)";
                            case "IFCMASSMEASURE":
                                return Measure(raw, "kg");
                            default:
                                return Number(raw);
                        }
                    }
                    default:
                    {
                        string text = StepFile.Text(value);
                        if (text != null) { return text.Trim(); }
                        double raw = StepFile.Number(value);
                        if (!double.IsFinite(raw)) { return StepFile.Enum(value) ?? string.Empty; }
                        number = raw;
                        return Number(raw);
                    }
                }
            }

            private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

            private static string Measure(double value, string symbol) => string.IsNullOrEmpty(symbol) ? Number(value) : $"{Number(value)} {symbol}";

            #endregion

            #region Geometry to the scene

            /// <summary>
            /// Moves an element's triangles into the scene buffers: scene-local metres, normals smoothed between triangles
            /// closer than the crease angle, shared vertices merged, opaque triangles then transparent ones. Each triangle's
            /// surface token gives its colour and material (unnamed: <paramref name="elementMaterial"/>); each vertex gets
            /// the material's index and a surface coordinate.
            /// </summary>
            private bool BuildGeometry(IfcMesh mesh, string elementMaterial, out int opaqueStart, out int opaqueCount, out int transparentStart, out int transparentCount, out Aabb bounds)
            {
                opaqueStart = opaqueCount = transparentStart = transparentCount = 0;
                bounds = Aabb.Empty;
                int n = mesh.TriangleCount;
                if (n == 0) { return false; }

                // Corners in scene space, face normals, kept triangles
                var corners = new Vector3[n * 3];
                var faceNormals = new Vector3[n];
                var keep = new bool[n];
                int kept = 0;
                for (int t = 0; t < n; t++)
                {
                    Vector3 a = ToScene(mesh.Points[t * 3]), b = ToScene(mesh.Points[t * 3 + 1]), c = ToScene(mesh.Points[t * 3 + 2]);
                    corners[t * 3] = a;
                    corners[t * 3 + 1] = b;
                    corners[t * 3 + 2] = c;
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float doubleArea = cross.Length();
                    if (!(doubleArea > 1e-9f) || !float.IsFinite(doubleArea)) { continue; }
                    faceNormals[t] = cross / doubleArea;
                    keep[t] = true;
                    kept++;
                }
                if (kept == 0) { return false; }

                // Weld positions (0.1 mm) and list the triangles round each welded point
                var weldIndex = new Dictionary<(long, long, long), int>(n * 2);
                var weld = new int[n * 3];
                for (int i = 0; i < corners.Length; i++)
                {
                    if (!keep[i / 3]) { continue; }
                    Vector3 p = corners[i];
                    var key = ((long)MathF.Round(p.X * 10000f), (long)MathF.Round(p.Y * 10000f), (long)MathF.Round(p.Z * 10000f));
                    if (!weldIndex.TryGetValue(key, out int w)) { weldIndex[key] = w = weldIndex.Count; }
                    weld[i] = w;
                }
                var starts = new int[weldIndex.Count + 1];
                for (int i = 0; i < corners.Length; i++) { if (keep[i / 3]) { starts[weld[i] + 1]++; } }
                for (int w = 0; w < weldIndex.Count; w++) { starts[w + 1] += starts[w]; }
                var fill = (int[])starts.Clone();
                var around = new int[starts[^1]];
                for (int i = 0; i < corners.Length; i++) { if (keep[i / 3]) { around[fill[weld[i]]++] = i / 3; } }

                int vertexBase = _vertices.Count;
                var vertexIndex = new Dictionary<(int, int, int, int, uint), int>(kept * 2);
                var opaque = new List<uint>(kept * 3);
                var transparent = new List<uint>();
                for (int t = 0; t < n; t++)
                {
                    if (!keep[t]) { continue; }
                    uint token = mesh.Colours[t];
                    (uint colour, string named) = token != 0 && token <= _surfaces.Count ? _surfaces[(int)token - 1] : (DEFAULT_COLOUR, null);
                    ushort material = MaterialIndex(named ?? elementMaterial, colour);
                    List<uint> target = (colour >> 24) < 250 ? transparent : opaque;
                    Vector3 own = faceNormals[t];
                    for (int k = 0; k < 3; k++)
                    {
                        int corner = t * 3 + k;
                        int w = weld[corner];
                        Vector3 sum = Vector3.Zero;
                        for (int j = starts[w]; j < starts[w + 1]; j++)
                        {
                            Vector3 other = faceNormals[around[j]];
                            if (Vector3.Dot(own, other) >= CREASE_COS) { sum += other; }
                        }
                        Vector3 normal = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : own;
                        var key = (w, (int)MathF.Round(normal.X * 512f), (int)MathF.Round(normal.Y * 512f), (int)MathF.Round(normal.Z * 512f), token);
                        if (!vertexIndex.TryGetValue(key, out int index))
                        {
                            index = _vertices.Count - vertexBase;
                            vertexIndex[key] = index;
                            _vertices.Add(new SceneVertex(corners[corner], normal, colour));
                            _vertexMaterial.Add(material);
                            _vertexUv.Add(material == MaterialData.NONE ? Vector2.Zero : SurfaceCoordinate(corners[corner], normal));
                            bounds.Include(corners[corner]);
                        }
                        target.Add((uint)(vertexBase + index));
                    }
                }

                opaqueStart = _indices.Count;
                opaqueCount = opaque.Count;
                _indices.AddRange(opaque);
                transparentStart = _indices.Count;
                transparentCount = transparent.Count;
                _indices.AddRange(transparent);
                return opaqueCount + transparentCount > 0;
            }

            #endregion

            #region Levels, rooms, grids, site

            /// <summary>Building storeys as levels (elevation from the storey's placement), sorted.</summary>
            private LevelInfo[] ReadLevels()
            {
                var levels = new List<LevelInfo>();
                foreach (int storey in _file.OfType("IFCBUILDINGSTOREY"))
                {
                    object[] a = _file.Args(storey);
                    DXform placement = _geometry.Placement(StepFile.Ref(StepFile.Arg(a, 5)));
                    float elevation = ToScene(placement.T).Z;
                    if (StepFile.Ref(StepFile.Arg(a, 5)) == 0)
                    {
                        double attribute = StepFile.Number(StepFile.Arg(a, 9));
                        if (double.IsFinite(attribute)) { elevation = (float)(attribute * _length - _origin.Z); }
                    }
                    string name = StepFile.Text(StepFile.Arg(a, 2));
                    levels.Add(new LevelInfo(string.IsNullOrWhiteSpace(name) ? "Level" : name.Trim(), elevation));
                }
                return levels.OrderBy(l => l.Elevation).ToArray();
            }

            /// <summary>
            /// Spaces as rooms: the outline from the space body's downward faces at its bottom (boundary edges chained into
            /// loops), the vertical extent from its body, number = Name and name = LongName (as Revit writes rooms), the area
            /// from its quantities or its outline, and its properties.
            /// </summary>
            private RoomInfo[] ReadRooms()
            {
                var rooms = new List<RoomInfo>();
                foreach (int space in _file.OfType("IFCSPACE"))
                {
                    try
                    {
                        RoomInfo room = ReadRoom(space);
                        if (room != null) { rooms.Add(room); }
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"IFC space #{space} skipped: {ex.Message}");
                    }
                }
                return rooms.ToArray();
            }

            private RoomInfo ReadRoom(int space)
            {
                object[] a = _file.Args(space);
                DXform placement = _geometry.Placement(StepFile.Ref(StepFile.Arg(a, 5)));
                var mesh = new IfcMesh();
                foreach (int item in _geometry.BodyItems(StepFile.Ref(StepFile.Arg(a, 6)))) { _geometry.AddItem(item, placement, 0, mesh); }
                if (mesh.TriangleCount == 0) { return null; }

                var points = new Vector3[mesh.Points.Count];
                float bottom = float.MaxValue, top = float.MinValue;
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = ToScene(mesh.Points[i]);
                    bottom = MathF.Min(bottom, points[i].Z);
                    top = MathF.Max(top, points[i].Z);
                }

                if (top - bottom < 0.1f) { return null; } // an area (flat), not a room

                // Downward faces near the bottom, as plan triangles
                var floor = new List<(Vector2, Vector2, Vector2)>();
                float floorArea = 0;
                for (int t = 0; t < mesh.TriangleCount; t++)
                {
                    Vector3 p0 = points[t * 3], p1 = points[t * 3 + 1], p2 = points[t * 3 + 2];
                    Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                    float length = cross.Length();
                    if (!(length > 1e-9f) || cross.Z / length > -0.7f) { continue; }
                    if ((p0.Z + p1.Z + p2.Z) / 3f > bottom + 0.05f) { continue; }
                    floor.Add((new Vector2(p0.X, p0.Y), new Vector2(p1.X, p1.Y), new Vector2(p2.X, p2.Y)));
                    floorArea += MathF.Abs(cross.Z) * 0.5f;
                }
                if (floor.Count == 0) { return null; }
                Vector2[][] loops = BoundaryLoops(floor);

                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                foreach (Vector2[] loop in loops)
                {
                    foreach (Vector2 p in loop)
                    {
                        min = Vector2.Min(min, p);
                        max = Vector2.Max(max, p);
                    }
                }

                string number = StepFile.Text(StepFile.Arg(a, 2))?.Trim();
                string name = StepFile.Text(StepFile.Arg(a, 7))?.Trim();
                if (string.IsNullOrEmpty(name)) { (name, number) = (number, null); }
                int storey = StoreyOf(space);
                string level = storey != 0 ? NameOf(storey)?.Trim() : null;

                List<(string Name, string Value, double Number)> properties = Properties(space);
                float area = floorArea;
                foreach ((string pName, string _, double pNumber) in properties)
                {
                    if ((pName.Equals("NetFloorArea", StringComparison.OrdinalIgnoreCase) || pName.Equals("GrossFloorArea", StringComparison.OrdinalIgnoreCase))
                        && double.IsFinite(pNumber) && pNumber > 0)
                    {
                        area = (float)(pNumber * _area);
                        break;
                    }
                }

                // Revit's room parameters first (the overlay's defaults), then the space's own
                var parameters = new List<RoomParameter>
                {
                    new("Number", number ?? "—", RoomValues.ParseNumber(number ?? string.Empty)),
                    new("Name", name ?? "Room", double.NaN)
                };
                if (!string.IsNullOrEmpty(level)) { parameters.Add(new RoomParameter("Level", level, double.NaN)); }
                double areaInUnits = area / _area;
                parameters.Add(new RoomParameter("Area", Measure(areaInUnits, _areaSymbol), areaInUnits));
                var seen = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                foreach ((string pName, string pValue, double pNumber) in properties)
                {
                    if (seen.Add(pName)) { parameters.Add(new RoomParameter(pName, pValue, double.IsFinite(pNumber) ? pNumber : RoomValues.ParseNumber(pValue))); }
                }

                return new RoomInfo
                {
                    Number = string.IsNullOrEmpty(number) ? "—" : number,
                    Name = string.IsNullOrEmpty(name) ? "Room" : name,
                    Loops = loops,
                    Min = min,
                    Max = max,
                    BottomZ = bottom,
                    TopZ = top,
                    UniqueId = StepFile.Text(StepFile.Arg(a, 0)),
                    ElementId = space,
                    LevelName = level,
                    Area = area > 0 ? area : float.NaN,
                    Parameters = parameters.ToArray()
                };
            }

            /// <summary>
            /// The outline of a set of plan triangles: edges used by one triangle only, chained into loops. When the chain
            /// breaks (non-manifold input) the triangles themselves are the loops (the even-odd rule still holds).
            /// </summary>
            private static Vector2[][] BoundaryLoops(List<(Vector2 A, Vector2 B, Vector2 C)> triangles)
            {
                var index = new Dictionary<(long, long), int>();
                var positions = new List<Vector2>();
                int Weld(Vector2 p)
                {
                    var key = ((long)MathF.Round(p.X * 1000f), (long)MathF.Round(p.Y * 1000f));
                    if (!index.TryGetValue(key, out int i))
                    {
                        index[key] = i = positions.Count;
                        positions.Add(p);
                    }
                    return i;
                }

                var edgeUse = new Dictionary<(int, int), int>();
                var directed = new List<(int, int)>();
                foreach ((Vector2 a, Vector2 b, Vector2 c) in triangles)
                {
                    int ia = Weld(a), ib = Weld(b), ic = Weld(c);
                    foreach ((int from, int to) in new[] { (ia, ib), (ib, ic), (ic, ia) })
                    {
                        if (from == to) { continue; }
                        var undirected = (Math.Min(from, to), Math.Max(from, to));
                        edgeUse.TryGetValue(undirected, out int count);
                        edgeUse[undirected] = count + 1;
                        directed.Add((from, to));
                    }
                }

                var next = new Dictionary<int, List<int>>();
                int boundaryEdges = 0;
                foreach ((int from, int to) in directed)
                {
                    if (edgeUse[(Math.Min(from, to), Math.Max(from, to))] != 1) { continue; }
                    if (!next.TryGetValue(from, out List<int> list)) { next[from] = list = new List<int>(); }
                    list.Add(to);
                    boundaryEdges++;
                }

                var loops = new List<Vector2[]>();
                int used = 0;
                bool broken = false;
                foreach (int start in next.Keys.ToList())
                {
                    while (next.TryGetValue(start, out List<int> outgoing) && outgoing.Count > 0)
                    {
                        var loop = new List<Vector2>();
                        int current = start;
                        while (next.TryGetValue(current, out List<int> edges) && edges.Count > 0)
                        {
                            int to = edges[^1];
                            edges.RemoveAt(edges.Count - 1);
                            used++;
                            loop.Add(positions[current]);
                            current = to;
                            if (current == start) { break; }
                        }
                        if (current == start && loop.Count >= 3) { loops.Add(loop.ToArray()); }
                        else { broken = true; }
                    }
                }

                if (loops.Count == 0 || broken || used != boundaryEdges)
                {
                    return triangles.Select(t => new[] { t.A, t.B, t.C }).ToArray();
                }
                return loops.ToArray();
            }

            /// <summary>IfcGrid axes as grid lines (plan polylines through the grid's placement).</summary>
            private GridLine[] ReadGrids()
            {
                var grids = new List<GridLine>();
                foreach (int grid in _file.OfType("IFCGRID"))
                {
                    object[] a = _file.Args(grid);
                    DXform placement = _geometry.Placement(StepFile.Ref(StepFile.Arg(a, 5)));
                    for (int list = 7; list <= 9; list++)
                    {
                        foreach (object axisRef in StepFile.List(StepFile.Arg(a, list)))
                        {
                            object[] axis = _file.Args(StepFile.Ref(axisRef));
                            List<DVec3> curve = _geometry.Curve(StepFile.Ref(StepFile.Arg(axis, 1)));
                            if (curve.Count < 2) { continue; }
                            if (!StepFile.Bool(StepFile.Arg(axis, 2), true)) { curve.Reverse(); }
                            Vector2[] points = curve.Select(p => ToScene(placement.Apply(p))).Select(p => new Vector2(p.X, p.Y)).ToArray();
                            string tag = StepFile.Text(StepFile.Arg(axis, 0))?.Trim();
                            grids.Add(new GridLine
                            {
                                Name = string.IsNullOrEmpty(tag) ? "?" : tag,
                                Points = points,
                                Curved = points.Length > 2
                            });
                        }
                    }
                }
                return grids.ToArray();
            }

            /// <summary>The site's latitude / longitude (time zone guessed from the longitude), and true north from the model context.</summary>
            private SiteInfo ReadSite()
            {
                var site = new SiteInfo();
                int siteId = _file.OfType("IFCSITE").FirstOrDefault();
                if (siteId != 0)
                {
                    object[] a = _file.Args(siteId);
                    double latitude = CompoundAngle(StepFile.Arg(a, 9)), longitude = CompoundAngle(StepFile.Arg(a, 10));
                    if (double.IsFinite(latitude) && double.IsFinite(longitude) && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180 && (latitude != 0 || longitude != 0))
                    {
                        site.HasLocation = true;
                        site.Latitude = latitude;
                        site.Longitude = longitude;
                        site.TimeZone = Math.Clamp(Math.Round(longitude / 15.0), -12, 14);
                    }
                }

                foreach (int context in _file.OfType("IFCGEOMETRICREPRESENTATIONCONTEXT"))
                {
                    object[] a = _file.Args(context);
                    if (!string.Equals(StepFile.Text(StepFile.Arg(a, 1)), "Model", StringComparison.OrdinalIgnoreCase)) { continue; }
                    int north = StepFile.Ref(StepFile.Arg(a, 5));
                    if (north == 0) { continue; }
                    DVec3 d = _geometry.Direction(north, DVec3.UnitY);
                    // True north in model axes is (sin a, cos a) for an internal → true north angle a (SolarPosition.ToModel)
                    site.TrueNorthAngle = (float)Math.Atan2(d.X, d.Y);
                    break;
                }
                return site;
            }

            /// <summary>An IfcCompoundPlaneAngleMeasure (degrees, minutes, seconds, millionths) in decimal degrees, or NaN.</summary>
            private static double CompoundAngle(object value)
            {
                object[] parts = StepFile.List(value);
                if (parts.Length < 3) { return double.NaN; }
                double degrees = StepFile.Number(parts[0], 0), minutes = StepFile.Number(parts[1], 0), seconds = StepFile.Number(parts[2], 0);
                double millionths = parts.Length > 3 ? StepFile.Number(parts[3], 0) : 0;
                return degrees + minutes / 60.0 + (seconds + millionths / 1e6) / 3600.0;
            }

            private string ProjectName()
            {
                object[] a = _file.Args(_file.OfType("IFCPROJECT").FirstOrDefault());
                string longName = StepFile.Text(StepFile.Arg(a, 5)), name = StepFile.Text(StepFile.Arg(a, 2));
                return !string.IsNullOrWhiteSpace(longName) ? longName.Trim() : !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
            }

            private string ProjectGuid() => StepFile.Text(StepFile.Arg(_file.Args(_file.OfType("IFCPROJECT").FirstOrDefault()), 0));

            #endregion
        }
    }
}
