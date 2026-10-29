# I-A build notes: IFC import

**Built and tested here**: the .NET 8 SDK (Ubuntu package) was available this round, so BimGo.Core and its tests were
compiled and run (`dotnet test tests/BimGo.Core.Tests`: all pass except `SunStudyTests.Studies_WriteListReadDelete`,
which fails on Linux only because ':' is a legal file name character there; unchanged by this round). BimGo.App was
compile-checked with a scratch project against the `Microsoft.WindowsDesktop.App.Ref` reference assemblies (Ubuntu's SDK
has no Windows Desktop SDK); it was **not run** (no Windows / GPU). Nothing in BimGo.Revit changed.

## Files
- Core `Format/IfcStepFile.cs`: ISO 10303-21 reader (`StepFile`, `StepRef`, `StepEnum`, `StepTyped`, `StepDerived`).
  One pass indexes instances (number → type, argument offset; `/* */` comments and quoted strings with `''` respected;
  complex instances skipped); arguments parsed on demand, not cached; header FILE_SCHEMA / FILE_NAME / FILE_DESCRIPTION.
  String escapes `\X2\…\X0\`, `\X4\…\X0\`, `\X\hh`, `\S\c`, `\\`, `\Px\`; raw bytes as UTF-8, else Latin-1. `.ifczip`
  reads its first `.ifc` entry. Progress / cancellation every MB.
- `Format/IfcMath.cs`: `DVec2`, `DVec3`, `DXform` (affine, may be scaled / mirrored; `Frame` = Axis2Placement3D rule).
- `Format/IfcPolygons.cs`: 2D triangulation (outline + holes; Eberly's hole bridging with the reflex-vertex visibility
  check; earcut-style ear test that only lets non-convex vertices block; stuck → clip the flattest corner; fan above 4,000
  vertices) and planar 3D faces (Newell normal, project dropping the largest component, winding kept; fast paths for
  triangles and convex quads).
- `Format/IfcCsg.cs`: BSP CSG after csg.js (MIT; acknowledged in THIRD-PARTY-NOTICES), iterative build / clip so deep
  trees can't overflow the stack; split polygons keep their parent's plane; colours carried per polygon; Subtract,
  Intersect.
- `Format/IfcGeometry.cs`: `IfcMesh` (triangles + per-triangle colour, 0 = inherit; `Append` through a transform flips
  for mirrors; `OrientOutwardFrom`; `RepairOrientation`) and `IfcGeometry` (placements cached, Axis2Placement 2D / 3D /
  Axis1, transformation operators 2D / 3D uniform / non-uniform per the IFC base-axis rules, body representation choice,
  every item type listed in DEVELOPING → *IFC import*, profiles, curves, booleans, `SubtractInPlace` for openings).
- `Format/IfcImporter.cs`: `IfcImporter.Import` (never throws; errors in plain words), `IfcImportSummary` (+ `Notice`),
  and the run: units (SI prefixes, conversion-based FOOT / INCH / DEGREE…), styles, material colours, relationships
  (containment, aggregates / nests, voids, fills, types, properties, materials), product discovery, origin, per-product
  geometry → scene buffers (40° crease smoothing, vertex merge, opaque then transparent), parameters, entourage slots,
  levels, rooms, grids, site.
- `Format/FileModels.cs`: `FileKinds.IFC_IMPORT` (the in-memory document's kind; saved files are `save`).
- App `Shell/OpenTarget.cs` (`OPEN_FILTER`, `CanOpen`), `AppShell.cs` (`LoadFile` → `ImportIfc` with the progress screen;
  start notice; queue / inbox accept IFC), `HomeScreen.cs` (Open dialog filter and title, drop, wording: "OPEN .BIMGO OR
  IFC…", Revit LT hint), `AppInstance.cs` (inbox accepts IFC), `RecentFiles.cs` (IFC entries: "IFC · imported when
  opened").
- Tests `IfcImportTests.cs` (8): a hand-written Revit-style IFC (mm, storey at 3 m) checks the opening cut (volume
  2.0 m³), half-space and polygon-bounded clipping volumes, a brep with a reversed face, a mirrored mapped item, a
  tessellated set (all positive volumes), world positions, styles (red opaque wall, 60 % transparent door), names /
  "Family: Type" / level / host / GUIDs, property values with units, planting + entourage slot, level elevation, the room
  (number, decoded name, area from the quantity, contains, parameters), grids, site (lat / long, time zone, true north
  −30°), a `.bimgo` round trip, `.ifczip`, refusals, extensions, the notice.

## Checked against real exports (not committed: third-party files)
From `youshengCode/IfcSampleFiles` and `buildingSMART/Sample-Test-Files`, imported, written to `.bimgo`, read back and
rendered with a throwaway software rasteriser (scratch, not in the repo):

| File | Exporter | Result |
|---|---|---|
| Ifc2x3_Duplex_Architecture | Revit 2011, IFC2x3 CV | 215 elements, 26 k tris, 4 levels, 21 rooms, 39 openings cut, 0.6 s |
| Ifc2x3_Duplex_Mechanical | Revit 2011, IFC2x3 CV | 487 elements (pipes, ducts, equipment), 37 rooms |
| Ifc4_Revit_ARC | Revit 2021, IFC4 RV | 446 elements, 362 k tris, 6 levels; RPC trees → Planting (16), people / car → Entourage (5) |
| Ifc4_Revit_STR | Revit 2021, IFC4 RV | 453 elements (3,680 rebar left out), framing, columns |
| Ifc4_SampleHouse | (IFC4) | 58 elements, curved roof smooth, 4 rooms |
| buildingSMART Building-* (2x3, 4, 4.3), wall-with-opening, tessellation-with-colours | various | all import, nothing unsupported |

Every element's triangles enclose a positive volume after the orientation repair (Revit 2011's surface models are wound
face by face). Zero unsupported items across these files.

## Decisions worth knowing
- **ElementId = IFC instance number** (unique in the file); Revit's id is shown as the *Revit ElementId* parameter (from
  `Tag`) so a Revit LT user can Select by ID. Model key `ifc:<project GlobalId>`: push can never match a Revit model.
- **Movable**: FFE / services only (as the Revit rule, roughly); everything else says why not.
- **Aggregates**: drawn through their parts when the parts have geometry (curtain walls, stairs, roofs), else as
  themselves.
- **Reinforcement** left out (Revit extraction has no rebar category; hidden in concrete; heavy).
- **Spaces** never drawn: they become rooms. Flat spaces (Revit areas) are skipped.
- **Booleans** skipped (solid kept, counted) above 20,000 triangles in either operand, to bound import time.
- **No new package, no new folder** in src (files in `Format/`).

## To verify (Gavin)
See DEVELOPING → Open build. Also worth a look: a large real project (import time and memory), an imperial (feet) export,
IFC from Revit LT itself, walls under sloped roofs (polygon-bounded half-spaces from CV 2.0), and whether the Open
button's longer label fits at 100 % and 150 % scaling.
