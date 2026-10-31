# M-A build notes: materials for IFC models

**Built and tested here**: BimGo.Core and its tests (`dotnet test tests/BimGo.Core.Tests`: all pass except
`SunStudyTests.Studies_WriteListReadDelete`, Linux only, as before). BimGo.App compile-checked against the Windows
Desktop reference assemblies; every shader was extracted from the build and compiled and linked (vertex + fragment
pairs) with glslangValidator 15. **Not run** (no Windows / GPU here).

## Files
- Core `Format/IfcImporter.cs`: surface tokens (colour + material name) in place of colours in the geometry; a
  material table, per-vertex indices and surface coordinates (`SurfaceCoordinate`). `Scene/MaterialData.cs`:
  `IFC_SCHEMA`, `IFC_TEXTURE_SIZE`; `SceneMaterial.Grass`, `Bump`, `BumpFromTexture`, `BumpStrength`, `Cutout`,
  `CutoutFromTexture`, `CutoutThreshold`, `HasBump`, `HasCutout`, `Images()`; `With` keeps bump and cutout images.
- Core `Format/MaterialMatches.cs` (new): `MaterialMatch`, `MaterialMatchDocument`, `MaterialMatcher` (`Apply`,
  `ApplyLook`, `Overrides`, `OriginalOf`), `MaterialLibrary` (read / write / put / merge / apply). `BimGoDocument`:
  `MaterialMatches`, `MatchImages`, `MaterialBaseVertices`; writer / reader: `material-matches.json`,
  `material-base.bin`, bump and cutout images. `ModelUpdate.WithNewModel` re-applies matches. `GrassField.Build`
  takes the drawn materials and `Grass`.
- App `Rendering/MaterialTextures.cs` (8 texels, bump / cutout layers and counts, alpha kept), `Shaders.cs`
  (`MATERIAL_SAMPLE_GLSL`, `materialCutout`, `materialBump`, `bumpNormal`; the depth pre-pass, AO geometry and shadow
  depth shaders cut holes; `SHADOW_VS` passes material and surface coordinate), `SceneRenderer` (`CutoutsWanted`,
  sampler units for the extra programs, `ApplyCutouts`, `RefreshMaterialTable`, per-vertex indices re-uploaded when
  overrides change them), `ReflectionProbes.UseMaterials`.
- App `Game/GameSession.Materials.cs` (new: the panel, pickers, eyedropper, library), `Game/MaterialImages.cs` (new:
  encoding, Autodesk library discovery, thumbnailer); hooks in `GameSession.cs` (initialise, Esc, the click),
  `Menu.cs` (MATERIALS in place of TEXTURES), `PanelShell.cs`, `Document.cs` (what Save writes), `Textures.cs`,
  `Grass.cs`, `Sun.cs` (shadow key), `Render.cs` (cutouts wanted), `Viewer.cs` (a matched viewer file opens Realistic).
- Tests: `IfcImportTests.Import_Materials_TableVerticesAndSurfaceCoordinates`; `MaterialMatchTests` (7: by name,
  water / grass / cutout, overrides, the .bimgo round trip and the original back, the library, cleaning, update from
  IFC).

## Decisions worth knowing
- **The material name is the style's name**: Revit names the IFC surface style after the material (as IfcOpenShell
  notes), so styled geometry gets the material's name even when no IfcMaterial reaches it.
- **The file holds the look applied**, not only the matches: viewer copies, older BimGo and anything reading
  materials.json see it; the matches and the model's own table ride along to edit or re-apply.
- **Images are copied into the file** (BimGo's textures, the Autodesk library's, your own), never referenced by path:
  the viewer copy needs nothing else.
- **Bump without tangents** (screen-space derivatives, forward differences along the image's own steps): works with
  any surface coordinates, including the Revit export's parametric ones.
- **Cutouts in every depth pass**, or the depth pre-pass's EQUAL test would leave black holes.
- **MATERIALS replaces TEXTURES for IFC models only**: TEXTURES edits the table in place, MATERIALS re-applies from
  the model's own; mixing them would lose edits.
