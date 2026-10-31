using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// IFC import (Revit LT's way in): a small hand-written IFC as Revit writes it, checked for geometry (opening cut,
    /// half-space and polygon-bounded clipping, a mirrored mapped item, a tessellated set, a brep wound inconsistently),
    /// styles, names, levels, hosts, property sets, a room, grids, the site, entourage slots, and a .bimgo round trip.
    /// </summary>
    [TestClass]
    public sealed class IfcImportTests
    {
        /// <summary>
        /// The test model, in millimetres, on a storey at 3 m: tokens {G} become fresh GUIDs, in order.
        /// </summary>
        private const string MODEL = @"ISO-10303-21;
HEADER;
/* a comment before the header records */
FILE_DESCRIPTION(('ViewDefinition [CoordinationView_V2.0]'),'2;1');
FILE_NAME('test.ifc','2026-10-29T00:00:00',(''),(''),'BimGo tests','BimGo tests','Autodesk Revit 2025 (ENU)');
FILE_SCHEMA(('IFC2X3'));
ENDSEC;
DATA;
#1=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#2=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);
#3=IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.);
#4=IFCUNITASSIGNMENT((#1,#2,#3));
#5=IFCCARTESIANPOINT((0.,0.,0.));
#6=IFCDIRECTION((0.,0.,1.));
#7=IFCDIRECTION((1.,0.,0.));
#8=IFCAXIS2PLACEMENT3D(#5,$,$);
#9=IFCDIRECTION((-0.5,0.8660254037844387));
#10=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#8,#9);
#11=IFCPROJECT('{G}',$,'P1',$,$,'Test Project',$,(#10),#4);
#12=IFCLOCALPLACEMENT($,#8);
#13=IFCSITE('{G}',$,'Site',$,$,#12,$,$,.ELEMENT.,(-33,-52,0,0),(151,12,0,0),0.,$,$);
#14=IFCLOCALPLACEMENT(#12,#8);
#15=IFCBUILDING('{G}',$,'Building',$,$,#14,$,$,.ELEMENT.,$,$,$);
#16=IFCCARTESIANPOINT((0.,0.,3000.));
#17=IFCAXIS2PLACEMENT3D(#16,$,$);
#18=IFCLOCALPLACEMENT(#14,#17);
#19=IFCBUILDINGSTOREY('{G}',$,'Level 2',$,$,#18,$,$,.ELEMENT.,3000.);
#20=IFCRELAGGREGATES('{G}',$,$,$,#11,(#13));
#21=IFCRELAGGREGATES('{G}',$,$,$,#13,(#15));
#22=IFCRELAGGREGATES('{G}',$,$,$,#15,(#19));
#30=IFCLOCALPLACEMENT(#18,#8);
#31=IFCCARTESIANPOINT((2000.,100.));
#32=IFCAXIS2PLACEMENT2D(#31,$);
#33=IFCRECTANGLEPROFILEDEF(.AREA.,$,#32,4000.,200.);
#34=IFCEXTRUDEDAREASOLID(#33,#8,#6,3000.);
#35=IFCSHAPEREPRESENTATION(#10,'Body','SweptSolid',(#34));
#36=IFCPRODUCTDEFINITIONSHAPE($,$,(#35));
#37=IFCWALLSTANDARDCASE('{G}',$,'Basic Wall:Generic - 200mm:1001',$,'Basic Wall:Generic - 200mm',#30,#36,'1001');
#38=IFCCOLOURRGB($,1.,0.,0.);
#39=IFCSURFACESTYLERENDERING(#38,0.,$,$,$,$,$,$,.FLAT.);
#40=IFCSURFACESTYLE('Red',.BOTH.,(#39));
#41=IFCPRESENTATIONSTYLEASSIGNMENT((#40));
#42=IFCSTYLEDITEM(#34,(#41),$);
#50=IFCCARTESIANPOINT((1500.,-50.,0.));
#51=IFCDIRECTION((0.,1.,0.));
#52=IFCAXIS2PLACEMENT3D(#50,#51,#7);
#53=IFCCARTESIANPOINT((0.,-1000.));
#54=IFCAXIS2PLACEMENT2D(#53,$);
#55=IFCRECTANGLEPROFILEDEF(.AREA.,$,#54,1000.,2000.);
#56=IFCEXTRUDEDAREASOLID(#55,#52,#6,300.);
#57=IFCSHAPEREPRESENTATION(#10,'Body','SweptSolid',(#56));
#58=IFCPRODUCTDEFINITIONSHAPE($,$,(#57));
#59=IFCLOCALPLACEMENT(#30,#8);
#60=IFCOPENINGELEMENT('{G}',$,'Opening',$,$,#59,#58,$);
#61=IFCRELVOIDSELEMENT('{G}',$,$,$,#37,#60);
#70=IFCCARTESIANPOINT((1000.,75.,0.));
#71=IFCCARTESIANPOINT((2000.,75.,0.));
#72=IFCCARTESIANPOINT((2000.,125.,0.));
#73=IFCCARTESIANPOINT((1000.,125.,0.));
#74=IFCCARTESIANPOINT((1000.,75.,2000.));
#75=IFCCARTESIANPOINT((2000.,75.,2000.));
#76=IFCCARTESIANPOINT((2000.,125.,2000.));
#77=IFCCARTESIANPOINT((1000.,125.,2000.));
#78=IFCPOLYLOOP((#70,#73,#72,#71));
#79=IFCPOLYLOOP((#74,#75,#76,#77));
#80=IFCPOLYLOOP((#70,#71,#75,#74));
#81=IFCPOLYLOOP((#71,#72,#76,#75));
#82=IFCPOLYLOOP((#72,#73,#77,#76));
#83=IFCPOLYLOOP((#70,#74,#77,#73));
#84=IFCFACEOUTERBOUND(#78,.T.);
#85=IFCFACEOUTERBOUND(#79,.F.);
#86=IFCFACEOUTERBOUND(#80,.T.);
#87=IFCFACEOUTERBOUND(#81,.T.);
#88=IFCFACEOUTERBOUND(#82,.T.);
#89=IFCFACEOUTERBOUND(#83,.T.);
#90=IFCFACE((#84));
#91=IFCFACE((#85));
#92=IFCFACE((#86));
#93=IFCFACE((#87));
#94=IFCFACE((#88));
#95=IFCFACE((#89));
#96=IFCCLOSEDSHELL((#90,#91,#92,#93,#94,#95));
#97=IFCFACETEDBREP(#96);
#98=IFCSHAPEREPRESENTATION(#10,'Body','Brep',(#97));
#99=IFCPRODUCTDEFINITIONSHAPE($,$,(#98));
#100=IFCDOOR('{G}',$,'Single-Flush:1000 x 2000:1002',$,'Single-Flush:1000 x 2000',#30,#99,'1002',2000.,1000.);
#101=IFCRELFILLSELEMENT('{G}',$,$,$,#60,#100);
#102=IFCCOLOURRGB($,0.5,0.7,0.9);
#103=IFCSURFACESTYLERENDERING(#102,0.6,$,$,$,$,$,$,.FLAT.);
#104=IFCSURFACESTYLE('Glass',.BOTH.,(#103));
#105=IFCPRESENTATIONSTYLEASSIGNMENT((#104));
#106=IFCSTYLEDITEM(#97,(#105),$);
#120=IFCCARTESIANPOINT((0.,0.));
#121=IFCAXIS2PLACEMENT2D(#120,$);
#122=IFCRECTANGLEPROFILEDEF(.AREA.,$,#121,500.,500.);
#123=IFCEXTRUDEDAREASOLID(#122,#8,#6,800.);
#124=IFCSHAPEREPRESENTATION(#10,'Body','SweptSolid',(#123));
#125=IFCREPRESENTATIONMAP(#8,#124);
#126=IFCCARTESIANPOINT((3000.,2000.,0.));
#127=IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,#126,1.,$);
#128=IFCMAPPEDITEM(#125,#127);
#129=IFCSHAPEREPRESENTATION(#10,'Body','MappedRepresentation',(#128));
#130=IFCPRODUCTDEFINITIONSHAPE($,$,(#129));
#131=IFCFURNISHINGELEMENT('{G}',$,'Desk:Desk:1003',$,'Desk:Desk',#30,#130,'1003');
#132=IFCDIRECTION((-1.,0.,0.));
#133=IFCDIRECTION((0.,1.,0.));
#134=IFCCARTESIANPOINT((3000.,3000.,0.));
#135=IFCCARTESIANTRANSFORMATIONOPERATOR3D(#132,#133,#134,1.,#6);
#136=IFCMAPPEDITEM(#125,#135);
#137=IFCSHAPEREPRESENTATION(#10,'Body','MappedRepresentation',(#136));
#138=IFCPRODUCTDEFINITIONSHAPE($,$,(#137));
#139=IFCFURNISHINGELEMENT('{G}',$,'Desk:Desk:1004',$,'Desk:Desk',#30,#138,'1004');
#140=IFCCARTESIANPOINT((100.,2250.));
#141=IFCAXIS2PLACEMENT2D(#140,$);
#142=IFCRECTANGLEPROFILEDEF(.AREA.,$,#141,200.,3500.);
#143=IFCEXTRUDEDAREASOLID(#142,#8,#6,3000.);
#144=IFCCARTESIANPOINT((0.,0.,2000.));
#145=IFCAXIS2PLACEMENT3D(#144,#6,#7);
#146=IFCPLANE(#145);
#147=IFCHALFSPACESOLID(#146,.F.);
#148=IFCBOOLEANCLIPPINGRESULT(.DIFFERENCE.,#143,#147);
#149=IFCSHAPEREPRESENTATION(#10,'Body','Clipping',(#148));
#150=IFCPRODUCTDEFINITIONSHAPE($,$,(#149));
#151=IFCWALL('{G}',$,'Basic Wall:Generic - 200mm:1005',$,'Basic Wall:Generic - 200mm',#30,#150,'1005');
#160=IFCCARTESIANPOINT((4100.,1000.));
#161=IFCAXIS2PLACEMENT2D(#160,$);
#162=IFCRECTANGLEPROFILEDEF(.AREA.,$,#161,200.,2000.);
#163=IFCEXTRUDEDAREASOLID(#162,#8,#6,3000.);
#164=IFCCARTESIANPOINT((0.,0.,1000.));
#165=IFCAXIS2PLACEMENT3D(#164,#6,#7);
#166=IFCPLANE(#165);
#167=IFCCARTESIANPOINT((3900.,-100.));
#168=IFCCARTESIANPOINT((4300.,-100.));
#169=IFCCARTESIANPOINT((4300.,1000.));
#170=IFCCARTESIANPOINT((3900.,1000.));
#171=IFCPOLYLINE((#167,#168,#169,#170,#167));
#172=IFCPOLYGONALBOUNDEDHALFSPACE(#166,.F.,#8,#171);
#173=IFCBOOLEANCLIPPINGRESULT(.DIFFERENCE.,#163,#172);
#174=IFCSHAPEREPRESENTATION(#10,'Body','Clipping',(#173));
#175=IFCPRODUCTDEFINITIONSHAPE($,$,(#174));
#176=IFCWALL('{G}',$,'Basic Wall:Generic - 200mm:1006',$,'Basic Wall:Generic - 200mm',#30,#175,'1006');
#180=IFCCARTESIANPOINTLIST3D(((5000.,0.,0.),(6000.,0.,0.),(5000.,1000.,0.),(5000.,0.,1000.)));
#181=IFCTRIANGULATEDFACESET(#180,$,.T.,((1,3,2),(1,2,4),(2,3,4),(3,1,4)),$);
#182=IFCSHAPEREPRESENTATION(#10,'Body','Tessellation',(#181));
#183=IFCPRODUCTDEFINITIONSHAPE($,$,(#182));
#184=IFCBUILDINGELEMENTPROXY('{G}',$,'RPC Tree - Deciduous:Red Oak:1007',$,'RPC Tree - Deciduous:Red Oak',#30,#183,'1007',$);
#190=IFCCARTESIANPOINT((2000.,1500.));
#191=IFCAXIS2PLACEMENT2D(#190,$);
#192=IFCRECTANGLEPROFILEDEF(.AREA.,$,#191,4000.,3000.);
#193=IFCEXTRUDEDAREASOLID(#192,#8,#6,2700.);
#194=IFCSHAPEREPRESENTATION(#10,'Body','SweptSolid',(#193));
#195=IFCPRODUCTDEFINITIONSHAPE($,$,(#194));
#196=IFCSPACE('{G}',$,'201',$,$,#30,#195,'Caf\X2\00E9\X0\ ''North''',.ELEMENT.,.INTERNAL.,$);
#197=IFCRELAGGREGATES('{G}',$,$,$,#19,(#196));
#198=IFCPROPERTYSINGLEVALUE('Department',$,IFCLABEL('Admin'),$);
#199=IFCPROPERTYSET('{G}',$,'Pset_SpaceCommon',$,(#198));
#200=IFCRELDEFINESBYPROPERTIES('{G}',$,$,$,(#196),#199);
#201=IFCQUANTITYAREA('NetFloorArea',$,$,12.);
#202=IFCELEMENTQUANTITY('{G}',$,'Qto_SpaceBaseQuantities',$,$,(#201));
#203=IFCRELDEFINESBYPROPERTIES('{G}',$,$,$,(#196),#202);
#210=IFCPROPERTYSINGLEVALUE('FireRating',$,IFCLABEL('60 min'),$);
#211=IFCPROPERTYSINGLEVALUE('Width',$,IFCLENGTHMEASURE(200.),$);
#212=IFCPROPERTYSINGLEVALUE('IsExternal',$,IFCBOOLEAN(.T.),$);
#213=IFCPROPERTYSET('{G}',$,'Pset_WallCommon',$,(#210,#211,#212));
#214=IFCRELDEFINESBYPROPERTIES('{G}',$,$,$,(#37),#213);
#220=IFCCARTESIANPOINT((-1000.,0.));
#221=IFCCARTESIANPOINT((5000.,0.));
#222=IFCCARTESIANPOINT((0.,-1000.));
#223=IFCCARTESIANPOINT((0.,5000.));
#224=IFCPOLYLINE((#220,#221));
#225=IFCPOLYLINE((#222,#223));
#226=IFCGRIDAXIS('A',#224,.T.);
#227=IFCGRIDAXIS('1',#225,.T.);
#228=IFCGRID('{G}',$,$,$,$,#30,$,(#226),(#227),$);
#230=IFCRELCONTAINEDINSPATIALSTRUCTURE('{G}',$,$,$,(#37,#100,#131,#139,#151,#176,#184,#228),#19);
#240=IFCMATERIAL('Concrete - Cast-in-Place');
#241=IFCRELASSOCIATESMATERIAL('{G}',$,$,$,(#151),#240);
ENDSEC;
END-ISO-10303-21;
";

        #region Helpers

        /// <summary>Writes the model (fresh GUIDs) and returns its path and the GUIDs in order.</summary>
        private static string WriteModel(TempFolder folder, out List<string> guids, string name = "Test House.ifc")
        {
            var used = new List<string>();
            string text = Regex.Replace(MODEL, @"\{G\}", _ =>
            {
                string guid = IfcGuid.Encode(Guid.NewGuid());
                used.Add(guid);
                return guid;
            });
            guids = used;
            string path = folder.File(name);
            File.WriteAllText(path, text, Encoding.ASCII);
            return path;
        }

        private static BimGoDocument Import(string path, out IfcImportSummary summary)
        {
            BimGoDocument document = IfcImporter.Import(path, new LaunchSettings(), out string error, out summary);
            Assert.IsNotNull(document, error);
            return document;
        }

        private static ElementRecord ByTag(SceneData scene, string revitId)
        {
            for (int i = 0; i < scene.Elements.Length; i++)
            {
                if (Parameter(scene, i, "Revit ElementId") == revitId) { return scene.Elements[i]; }
            }
            Assert.Fail($"No element with Revit id {revitId}.");
            return null;
        }

        private static string Parameter(SceneData scene, int element, string name)
        {
            int[] row = scene.Parameters.Rows[element];
            if (row == null) { return null; }
            for (int k = 0; k + 1 < row.Length; k += 2)
            {
                if (scene.Parameters.Names[row[k]] == name) { return scene.Parameters.Values[row[k + 1]]; }
            }
            return null;
        }

        /// <summary>The signed volume (m³) enclosed by an element's triangles: positive when they face out.</summary>
        private static double Volume(SceneData scene, ElementRecord e)
        {
            double volume = 0;
            void Range(int start, int count)
            {
                for (int i = 0; i < count; i += 3)
                {
                    Vector3 a = scene.Vertices[scene.Indices[start + i]].Position;
                    Vector3 b = scene.Vertices[scene.Indices[start + i + 1]].Position;
                    Vector3 c = scene.Vertices[scene.Indices[start + i + 2]].Position;
                    volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                }
            }
            Range(e.OpaqueStart, e.OpaqueCount);
            Range(e.TransparentStart, e.TransparentCount);
            return volume;
        }

        #endregion

        [TestMethod]
        public void Import_Geometry_OpeningsClippingMappedTessellated()
        {
            using var folder = new TempFolder();
            BimGoDocument document = Import(WriteModel(folder, out _), out IfcImportSummary summary);
            SceneData scene = document.Scene;

            // Seven drawn elements: two cut / clipped walls and a plain one, a door, two desks, a proxy (no opening, space or grid)
            Assert.AreEqual(7, scene.Elements.Length);
            Assert.AreEqual(1, summary.OpeningsCut);
            Assert.AreEqual(0, summary.Unsupported.Count);

            // 4 × 0.2 × 3 m wall less a 1 × 0.2 × 2 m opening
            Assert.AreEqual(2.0, Volume(scene, ByTag(scene, "1001")), 1e-3);
            // Clipped at 2 m by a half-space; clipped at 1 m over half its length by a polygon-bounded half-space
            Assert.AreEqual(0.2 * 3.5 * 2.0, Volume(scene, ByTag(scene, "1005")), 1e-3);
            Assert.AreEqual(0.2 * 2 * 3 - 0.2 * 1 * 2, Volume(scene, ByTag(scene, "1006")), 1e-3);
            // The door's brep has a reversed face (repaired), the second desk is mirrored: all face out
            Assert.AreEqual(1 * 0.05 * 2, Volume(scene, ByTag(scene, "1002")), 1e-4);
            Assert.AreEqual(0.5 * 0.5 * 0.8, Volume(scene, ByTag(scene, "1003")), 1e-4);
            Assert.AreEqual(0.5 * 0.5 * 0.8, Volume(scene, ByTag(scene, "1004")), 1e-4);
            Assert.AreEqual(1.0 / 6.0, Volume(scene, ByTag(scene, "1007")), 1e-4);

            // World positions: the storey's 3 m placement and the file's millimetres
            ElementRecord desk = ByTag(scene, "1003");
            Vector3 deskMin = desk.Bounds.Min + scene.OriginOffset;
            Assert.AreEqual(2.75f, deskMin.X, 1e-3f);
            Assert.AreEqual(1.75f, deskMin.Y, 1e-3f);
            Assert.AreEqual(3.0f, deskMin.Z, 1e-3f);
            Assert.AreEqual(0f, (desk.Bounds.Size - new Vector3(0.5f, 0.5f, 0.8f)).Length(), 1e-3f);
        }

        [TestMethod]
        public void Import_Materials_TableVerticesAndSurfaceCoordinates()
        {
            using var folder = new TempFolder();
            BimGoDocument document = Import(WriteModel(folder, out _), out _);
            SceneData scene = document.Scene;
            MaterialData materials = scene.Materials;

            // One material per name: styles named after their material (Revit's way), else the element's IFC material,
            // else the category; every vertex indexed, with a surface coordinate
            Assert.IsFalse(materials.IsEmpty);
            Assert.AreEqual(scene.Vertices.Length, materials.VertexMaterial.Length);
            Assert.AreEqual(scene.Vertices.Length, materials.VertexUv.Length);
            string[] names = materials.Materials.Select(m => m.Name).ToArray();
            CollectionAssert.Contains(names, "Red");
            CollectionAssert.Contains(names, "Glass");
            CollectionAssert.Contains(names, "Concrete - Cast-in-Place");
            CollectionAssert.Contains(names, "Walls (no material)");
            Assert.AreEqual(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsTrue(materials.Materials.All(m => m.Schema == MaterialData.IFC_SCHEMA));

            SceneMaterial red = materials.Materials.First(m => m.Name == "Red");
            Assert.AreEqual(1f, red.Colour.X, 1e-3f);
            Assert.AreEqual(0f, red.Colour.Y, 1e-3f);
            Assert.IsFalse(red.SeeThrough);
            Assert.IsTrue(materials.Materials.First(m => m.Name == "Glass").SeeThrough);

            // The red wall's vertices are "Red"; the concrete wall's are "Concrete - Cast-in-Place"
            ushort redIndex = (ushort)Array.IndexOf(names, "Red"), concrete = (ushort)Array.IndexOf(names, "Concrete - Cast-in-Place");
            ElementRecord wall = ByTag(scene, "1001");
            uint first = scene.Indices[wall.OpaqueStart];
            Assert.AreEqual(redIndex, materials.VertexMaterial[first]);
            ElementRecord concreteWall = ByTag(scene, "1005");
            Assert.AreEqual(concrete, materials.VertexMaterial[scene.Indices[concreteWall.OpaqueStart]]);

            // Surface coordinates: plan X / Y on the wall's top, along the face and up on its sides (metres)
            for (int i = 0; i < wall.OpaqueCount; i++)
            {
                uint v = scene.Indices[wall.OpaqueStart + i];
                SceneVertex vertex = scene.Vertices[v];
                Vector2 uv = materials.VertexUv[v];
                if (vertex.Normal.Z > 0.9f)
                {
                    Assert.AreEqual(vertex.Position.X, uv.X, 1e-4f);
                    Assert.AreEqual(vertex.Position.Y, uv.Y, 1e-4f);
                }
                else if (MathF.Abs(vertex.Normal.Y) > 0.9f)
                {
                    Assert.AreEqual(MathF.Abs(vertex.Position.X), MathF.Abs(uv.X), 1e-4f); // along the wall
                    Assert.AreEqual(vertex.Position.Z, uv.Y, 1e-4f); // up
                }
            }

            // Kept through a .bimgo
            string path = folder.File("materials.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            CollectionAssert.AreEqual(names, read.Scene.Materials.Materials.Select(m => m.Name).ToArray());
            CollectionAssert.AreEqual(materials.VertexMaterial, read.Scene.Materials.VertexMaterial);
        }

        [TestMethod]
        public void Import_Metadata_NamesStylesHostsProperties()
        {
            using var folder = new TempFolder();
            BimGoDocument document = Import(WriteModel(folder, out List<string> guids), out IfcImportSummary summary);
            SceneData scene = document.Scene;

            Assert.AreEqual("Autodesk Revit 2025 (ENU)", summary.Application);
            Assert.AreEqual("CoordinationView_V2.0", summary.ViewDefinition);
            Assert.AreEqual("IFC2X3", summary.Schema);
            Assert.IsNull(document.Path, "an import is unsaved");
            Assert.AreEqual(FileKinds.IFC_IMPORT, document.Kind);
            Assert.AreEqual("Test House", scene.ModelTitle);
            StringAssert.StartsWith(scene.Provenance.ModelKey, "ifc:");

            ElementRecord wall = ByTag(scene, "1001");
            int wallIndex = Array.IndexOf(scene.Elements, wall);
            Assert.AreEqual(CategoryCatalog.Find("walls").Index, wall.CategoryIndex);
            Assert.AreEqual("Generic - 200mm", wall.Name);
            Assert.AreEqual("Basic Wall: Generic - 200mm", wall.FamilyType);
            Assert.AreEqual("Level 2", wall.LevelName);
            Assert.AreEqual(guids[7], wall.IfcGuid);
            Assert.AreEqual(guids[7], wall.UniqueId);
            Assert.IsFalse(wall.Movable);
            Assert.AreEqual(0xFF0000FFu, scene.Vertices[scene.Indices[wall.OpaqueStart]].Colour, "red, opaque");
            Assert.AreEqual("IfcWallStandardCase", Parameter(scene, wallIndex, "IFC class"));
            Assert.AreEqual("60 min", Parameter(scene, wallIndex, "FireRating"));
            Assert.AreEqual("200 mm", Parameter(scene, wallIndex, "Width"));
            Assert.AreEqual("Yes", Parameter(scene, wallIndex, "IsExternal"));

            // The door: hosted by the wall (through its opening), see-through glass style
            ElementRecord door = ByTag(scene, "1002");
            Assert.AreEqual(CategoryCatalog.Find(CategoryCatalog.KEY_DOORS).Index, door.CategoryIndex);
            Assert.AreEqual(wall.ElementId, door.HostId);
            Assert.AreEqual(0, door.OpaqueCount);
            Assert.IsTrue(door.TransparentCount > 0);
            Assert.AreEqual(102u, scene.Vertices[scene.Indices[door.TransparentStart]].Colour >> 24);

            // Furniture moves; the RPC proxy is Planting with an entourage slot for billboards
            Assert.IsTrue(ByTag(scene, "1003").Movable);
            ElementRecord tree = ByTag(scene, "1007");
            Assert.AreEqual(CategoryCatalog.Find("planting").Index, tree.CategoryIndex);
            Assert.AreEqual(1, scene.EntourageSlots.Length);
            EntourageSlot slot = scene.EntourageSlots[0];
            Assert.AreEqual(EntourageSlots.PLANTING, slot.Category);
            Assert.AreEqual("RPC Tree - Deciduous", slot.Family);
            Assert.AreEqual("Red Oak", slot.Type);
            Assert.AreEqual(Array.IndexOf(scene.Elements, tree), slot.Element);
            Assert.AreEqual(1f, slot.Height, 1e-3f);
            Assert.IsTrue(slot.Clean(scene.Elements.Length));
            Assert.IsTrue(scene.CategoryLoaded[tree.CategoryIndex]);
            Assert.AreEqual(3, scene.CategoryElementCounts[wall.CategoryIndex]);
        }

        [TestMethod]
        public void Import_LevelsRoomsGridsSite()
        {
            using var folder = new TempFolder();
            SceneData scene = Import(WriteModel(folder, out _), out IfcImportSummary summary).Scene;

            Assert.AreEqual(1, scene.Levels.Length);
            Assert.AreEqual("Level 2", scene.Levels[0].Name);
            Assert.AreEqual(3.0f, scene.Levels[0].Elevation + scene.OriginOffset.Z, 1e-4f);

            // The space: number = Name, name = LongName (escapes decoded), area from its quantity, outline from its body
            Assert.AreEqual(1, scene.Rooms.Length);
            RoomInfo room = scene.Rooms[0];
            Assert.AreEqual("201", room.Number);
            Assert.AreEqual("Café 'North'", room.Name);
            Assert.AreEqual("Level 2", room.LevelName);
            Assert.AreEqual(12f, room.Area, 1e-4f);
            Assert.AreEqual(2.7f, room.TopZ - room.BottomZ, 1e-4f);
            Vector2 middle = new Vector2(2f, 1.5f) - new Vector2(scene.OriginOffset.X, scene.OriginOffset.Y);
            Assert.IsTrue(room.Contains(middle));
            Assert.IsFalse(room.Contains(middle + new Vector2(3f, 0f)));
            Assert.IsTrue(room.TryGetParameter("Department", out RoomParameter department));
            Assert.AreEqual("Admin", department.Value);
            Assert.IsTrue(room.TryGetParameter("Area", out RoomParameter area));
            Assert.AreEqual(12.0, area.Number, 1e-6);
            Assert.AreEqual(1, summary.Rooms);

            CollectionAssert.AreEquivalent(new[] { "A", "1" }, scene.Grids.Select(g => g.Name).ToArray());
            GridLine a = scene.Grids.First(g => g.Name == "A");
            Assert.AreEqual(-1f, a.Start.X + scene.OriginOffset.X, 1e-4f);
            Assert.AreEqual(5f, a.End.X + scene.OriginOffset.X, 1e-4f);

            Assert.IsTrue(scene.Site.HasLocation);
            Assert.AreEqual(-33.8667, scene.Site.Latitude, 1e-3);
            Assert.AreEqual(151.2, scene.Site.Longitude, 1e-3);
            Assert.AreEqual(10.0, scene.Site.TimeZone, 1e-9);
            Assert.AreEqual(-30.0, scene.Site.TrueNorthAngle * 180.0 / Math.PI, 1e-3);
        }

        [TestMethod]
        public void Import_SavesAndReadsBackAsBimGo()
        {
            using var folder = new TempFolder();
            BimGoDocument document = Import(WriteModel(folder, out _), out _);
            string path = folder.File("Test House.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, new WriterInfo("BimGo", "1.0.0"), FileKinds.SAVE, out string error), error);

            BimGoDocument back = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(back, error);
            Assert.AreEqual(document.Scene.Elements.Length, back.Scene.Elements.Length);
            Assert.AreEqual(document.Scene.TriangleCount, back.Scene.TriangleCount);
            CollectionAssert.AreEqual(document.Scene.Elements.Select(e => e.IfcGuid).ToArray(), back.Scene.Elements.Select(e => e.IfcGuid).ToArray());
            Assert.AreEqual(1, back.Scene.Rooms.Length);
            Assert.AreEqual(2, back.Scene.Grids.Length);
            Assert.AreEqual(1, back.Scene.EntourageSlots.Length);
            Assert.AreEqual(document.Scene.Parameters.Names.Length, back.Scene.Parameters.Names.Length);
        }

        [TestMethod]
        public void Import_IfcZip()
        {
            using var folder = new TempFolder();
            string ifc = WriteModel(folder, out _);
            string zip = folder.File("Test House.ifczip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(ifc, "Test House.ifc");
            }
            Assert.IsTrue(IfcImporter.HasExtension(zip));
            Assert.AreEqual(7, Import(zip, out _).Scene.Elements.Length);
        }

        [TestMethod]
        public void Import_RefusesWhatIsNotIfc()
        {
            using var folder = new TempFolder();
            string text = folder.File("notes.ifc");
            File.WriteAllText(text, "just some text");
            Assert.IsNull(IfcImporter.Import(text, new LaunchSettings(), out string error, out IfcImportSummary summary));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsNull(summary);

            // A STEP file with no geometry BimGo can show
            string empty = folder.File("empty.ifc");
            File.WriteAllText(empty, "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n#1=IFCPROJECT('0YvctVUKr0kugbFTf53O9L',$,'P',$,$,$,$,$,$);\nENDSEC;\nEND-ISO-10303-21;\n");
            Assert.IsNull(IfcImporter.Import(empty, new LaunchSettings(), out error, out _));
            StringAssert.Contains(error, "geometry");

            Assert.IsNull(IfcImporter.Import(folder.File("missing.ifc"), new LaunchSettings(), out error, out _));
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }

        [TestMethod]
        public void HasExtension_IfcAndIfcZipOnly()
        {
            Assert.IsTrue(IfcImporter.HasExtension(@"C:\Models\House.ifc"));
            Assert.IsTrue(IfcImporter.HasExtension("House.IFC"));
            Assert.IsTrue(IfcImporter.HasExtension("House.ifczip"));
            Assert.IsFalse(IfcImporter.HasExtension("House.ifcxml"));
            Assert.IsFalse(IfcImporter.HasExtension("House.bimgo"));
            Assert.IsFalse(IfcImporter.HasExtension(null));
        }

        [TestMethod]
        public void Notice_SaysWhatCameInAndToSave()
        {
            var summary = new IfcImportSummary { Elements = 1234, Storeys = 1, Rooms = 2 };
            StringAssert.Contains(summary.Notice(), "1,234 elements, 1 level, 2 rooms");
            StringAssert.Contains(summary.Notice(), "Save as .bimgo");
            Assert.IsFalse(summary.Notice().Contains("left out"));
            summary.Unsupported["IFCADVANCEDFACE (curved)"] = 3;
            StringAssert.Contains(summary.Notice(), "left out");
        }
    }
}
