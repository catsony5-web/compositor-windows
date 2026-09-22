using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class MaterialMappingTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Invalid material data was accepted."); }
        Document Document()
        {
            var doc = new Document { Width = 40, Height = 32 };
            doc.Materials.Add(new MaterialAsset(Guid.NewGuid(), "Test brick", Raster.Solid(8, 8, Colors.Red), "Synthetic fixture", true));
            return doc;
        }
        MaterialRegion Region(Document doc, bool hole = false)
        {
            List<Point[]> contours = [[new(2, 2), new(30, 2), new(30, 24), new(2, 24)]];
            if (hole) contours.Add([new(10, 10), new(18, 10), new(18, 18), new(10, 18)]);
            var region = MaterialEditing.Region(doc, "Floor", MaterialEditing.Polygon(contours), "polygon"); doc.MaterialRegions.Add(region); return region;
        }
        Layer Map(Document doc, MaterialRegion region)
        {
            var layer = MaterialEditing.Apply(doc, doc.Materials[0].Id, region.Id, 8, 8);
            layer.Blend = BlendMode.Normal; doc.Add(layer); return layer;
        }
        byte Alpha(Raster image, int x, int y) => image.Data[(y * image.Width + x) * 4 + 3];

        test("Material boundary retains holes and leaves surrounding pixels transparent", () =>
        {
            var doc = Document(); var region = Region(doc, true); var layer = Map(doc, region);
            var rendered = Imaging.Render(doc);
            Check(Alpha(rendered, 4, 4) == 255 && Alpha(rendered, 12, 12) == 0 && Alpha(rendered, 0, 0) == 0, "Material escaped the region or filled its hole.");
            Check(region.Path.Geometry.FillContains(new Point(4, 4)) && !region.Path.Geometry.FillContains(new Point(12, 12)), "Region topology changed.");
            Check(layer.X == 2 && layer.Y == 2 && layer.Material!.Boundary.Geometry.Bounds.Left == 0, "Document-to-local placement is wrong.");
        });
        test("Material repeat size and rotation use the original texture independently of cached pixels", () =>
        {
            var doc = Document(); var pixels = Raster.Solid(8, 8, Colors.Red);
            for (int y = 0; y < 8; y++) for (int x = 4; x < 8; x++) { int i = (y * 8 + x) * 4; pixels.Data[i] = 255; pixels.Data[i + 2] = 0; }
            doc.Materials[0] = doc.Materials[0] with { Pixels = pixels };
            var layer = Map(doc, Region(doc)); var originalBytes = pixels.Data.ToArray();
            var first = Imaging.Render(doc);
            Check(first.Data[(4 * 40 + 4) * 4 + 2] > 220 && first.Data[(4 * 40 + 8) * 4] > 220, "Texture did not repeat at the requested scale.");
            layer.Pixels = Raster.Solid(layer.Pixels.Width, layer.Pixels.Height, Colors.Lime);
            var zoom = DesignRenderer.Render(doc, new Rect(2, 2, 8, 8), 128, 128);
            Check(zoom.Data[(32 * 128 + 32) * 4 + 2] > 220 && zoom.Data[(32 * 128 + 32) * 4 + 1] < 20, "Zoom used the stale pixel cache.");
            layer.Material = layer.Material! with { Angle = 90, OffsetX = 1 };
            layer.Pixels = MaterialRenderer.Render(layer.Material);
            Check(!Imaging.Render(doc).Data.AsSpan().SequenceEqual(first.Data) && pixels.Data.AsSpan().SequenceEqual(originalBytes), "Pattern editing changed the source texture or ignored rotation.");
        });
        test("Material pattern respects parent transforms opacity masks and vector boundary at output", () =>
        {
            var doc = Document(); var layer = Map(doc, Region(doc));
            var group = DocumentFeatures.CreateGroup(doc); group.X = 3; group.Y = 4; doc.Add(group); layer.ParentId = group.Id;
            layer.Opacity = .5; layer.Mask = Enumerable.Repeat((byte)255, layer.Pixels.Width * layer.Pixels.Height).ToArray();
            for (int y = 0; y < layer.Pixels.Height; y++) for (int x = 0; x < 8; x++) layer.Mask[y * layer.Pixels.Width + x] = 0;
            var image = Imaging.Render(doc);
            Check(Alpha(image, 7, 8) == 0 && Alpha(image, 20, 12) is >= 127 and <= 128, "Parent transform, opacity or mask was lost.");
        });
        test("Material project embeds original assets regions and editable mapping across reopening", () =>
        {
            var doc = Document(); var region = Region(doc, true); var layer = Map(doc, region);
            layer.Material = layer.Material! with { TileWidth = 5.5, Angle = 17, OffsetY = 2 };
            layer.Pixels = MaterialRenderer.Render(layer.Material);
            var original = Imaging.Render(doc); string path = Path.Combine(directory, "material-roundtrip.moruproj");
            ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            Check(loaded.Materials.Count == 1 && loaded.MaterialRegions.Count == 1 && loaded.Active!.Material != null, "Material data disappeared on reopen.");
            Check(loaded.Active!.Material!.Asset.Id == doc.Materials[0].Id && loaded.Active.Material.TileWidth == 5.5 &&
                loaded.Active.Material.Boundary == layer.Material.Boundary, "Editable pattern definition changed.");
            Check(Imaging.Render(loaded).Data.AsSpan().SequenceEqual(original.Data), "Saved material appearance changed.");
            using var zip = ZipFile.OpenRead(path); using var input = zip.GetEntry("document.json")!.Open();
            Check(JsonNode.Parse(input)!["Version"]!.GetValue<int>() == 6 && zip.GetEntry("materials/0.png") != null, "Original asset was not embedded.");
        });
        test("Material layer copies carry their source into documents without a library", () =>
        {
            var source = Document(); var layer = Map(source, Region(source));
            var target = new Document { Width = 40, Height = 32 }; target.Add(layer.Snapshot()); target.Validate();
            Check(MaterialEditing.Assets(target).Count == 1 && target.MaterialRegions.Count == 0, "Copied mapping lost its source.");
            string path = Path.Combine(directory, "material-copy.moruproj"); ProjectStore.Save(target, path);
            Check(ProjectStore.Load(path).Active!.Material!.Asset.Pixels.Data.AsSpan().SequenceEqual(source.Materials[0].Pixels.Data), "Copied material did not survive saving.");
        });
        test("Material library mapping and rasterization participate in undo without mutating source buffers", () =>
        {
            var doc = new Document { Width = 40, Height = 32 }; var history = new History(); history.Reset(doc);
            var before = doc.Snapshot(); doc.Materials.Add(new MaterialAsset(Guid.NewGuid(), "Texture", Raster.Solid(2, 2, Colors.Red)));
            history.Commit("Register", before, doc); doc = history.Undo(doc); Check(doc.Materials.Count == 0, "Registration was not undone.");
            doc = history.Redo(doc); var region = Region(doc); var layer = Map(doc, region);
            history.Reset(doc); before = doc.Snapshot(); var source = layer.Material!.Asset;
            DocumentFeatures.Rasterize(layer); history.Commit("Rasterize", before, doc);
            Check(layer.Material == null && layer.Kind == LayerKind.Raster, "Raster painting retained a stale material definition.");
            doc = history.Undo(doc); Check(ReferenceEquals(doc.Active!.Material!.Asset, source), "Undo replaced or mutated the original texture.");
        });
        test("Material source boundaries reject open CAD paths and retain transformed closed geometry", () =>
        {
            var doc = Document();
            var shape = VectorShapes.Create(new ShapeSpec { Width = 10, Height = 6 }, 4, 5); shape.Rotation = 30; doc.Add(shape);
            var boundary = MaterialEditing.ClosedLayer(doc, shape.Id); var center = shape.Document(new Point(5, 3));
            Check(boundary.FillContains(center), "Closed shape transform was dropped.");
            var stream = new StreamGeometry(); using (var dc = stream.Open()) { dc.BeginFigure(new Point(1, 1), false, false); dc.LineTo(new Point(8, 8), true, false); }
            stream.Freeze();
            var vector = new Layer { Kind = LayerKind.Vector, Pixels = new Raster(10, 10), Vector = VectorContent.FromPaths(10, 10, [new VectorPrimitive(stream, Colors.Black, false, 1)]) };
            doc.Add(vector); Reject(() => MaterialEditing.ClosedLayer(doc, vector.Id));
            var closed = MaterialEditing.Polygon([[new(1, 1), new(8, 1), new(8, 8), new(1, 8)]]);
            vector.Vector = VectorContent.FromPaths(10, 10, [new VectorPrimitive(closed, Colors.Black, false, 1)]);
            vector.X = 20; var region = MaterialEditing.Region(doc, "Closed CAD", MaterialEditing.ClosedLayer(doc, vector.Id), "closed_layer", vector.Id);
            Check(region.Path.Geometry.FillContains(new Point(24, 4)) && !region.Path.Geometry.FillContains(new Point(4, 4)), "Closed CAD placement was lost.");
        });
        test("Material selection regions preserve ellipse and precision contour holes", () =>
        {
            var doc = Document(); var selection = new Selection(new Rect(4, 4, 20, 20), true);
            var region = MaterialEditing.Region(doc, "Ellipse", SelectionContours.Create(selection), "selection");
            Check(region.Path.Geometry.FillContains(new Point(14, 14)) && !region.Path.Geometry.FillContains(new Point(5, 5)), "Selection was replaced by its bounding box.");
            var mask = Enumerable.Repeat((byte)255, 16 * 16).ToArray();
            for (int y = 5; y < 10; y++) for (int x = 5; x < 10; x++) mask[y * 16 + x] = 0;
            var selected = new Selection(new Rect(0, 0, 16, 16)) { CanvasWidth = 16, CanvasHeight = 16, Coverage = mask };
            var geometry = SelectionContours.Create(selected);
            Check(!geometry.FillContains(new Point(7, 7)) && geometry.FillContains(new Point(2, 2)), "Selection hole was lost.");
        });
        test("Material artboard expansion moves region templates with their mapped layers", () =>
        {
            var doc = Document(); var region = Region(doc); var layer = Map(doc, region);
            var shifted = ArtboardEditing.Set(doc, new Artboard(Guid.Empty, "Left sheet", -10, 0, 10, 20), true);
            Check(shifted.Offset.X == 10 && doc.MaterialRegions[0].Path.Geometry.Bounds.X == 12 &&
                doc.Layers.Single(l => l.Id == layer.Id).X == 12, "Template and mapping diverged during artboard growth.");
            var another = MaterialEditing.Apply(doc, doc.Materials[0].Id, region.Id, 8, 8);
            Check(another.X == 12, "Reapplying a moved template used stale coordinates.");
        });
        test("Material limits malformed boundaries and duplicate IDs reject before persistence", () =>
        {
            var doc = Document(); Reject(() => MaterialEditing.Region(doc, "Open", new PathGeometry([new PathFigure(new Point(0, 0), [new LineSegment(new Point(20, 0), true), new LineSegment(new Point(10, 10), true)], false)]), "polygon"));
            Reject(() => MaterialEditing.ValidateSize(8192, 8192));
            var region = Region(doc); var layer = Map(doc, region);
            Reject(() => MaterialEditing.ValidateFill(layer.Material! with { TileWidth = 0 }, layer.Pixels));
            Reject(() => MaterialEditing.ValidateFill(layer.Material! with { Angle = double.NaN }, layer.Pixels));
            doc.Materials.Add(doc.Materials[0]); Reject(doc.Validate); doc.Materials.RemoveAt(1);
            doc.MaterialRegions.Add(region); Reject(doc.Validate); doc.MaterialRegions.RemoveAt(1);
            doc.MaterialRegions.Add(null!); Reject(doc.Validate); doc.MaterialRegions.RemoveAt(1);
            doc.MaterialRegions[0] = region with { Path = region.Path.Translate(-50, 0) };
            Reject(() => MaterialEditing.Apply(doc, doc.Materials[0].Id, region.Id, 8, 8));
        });
        test("Material project loader rejects missing sources invalid references and downgraded metadata", () =>
        {
            var doc = Document(); Map(doc, Region(doc)); string source = Path.Combine(directory, "material-valid.moruproj"); ProjectStore.Save(doc, source);
            int index = 0;
            void Corrupt(Action<ZipArchive, JsonObject> edit)
            {
                string output = Path.Combine(directory, "material-invalid-" + index++ + ".moruproj"); File.Copy(source, output, true);
                using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
                {
                    var entry = zip.GetEntry("document.json")!; JsonObject manifest;
                    using (var input = entry.Open()) manifest = JsonNode.Parse(input)!.AsObject();
                    entry.Delete(); edit(zip, manifest);
                    using var target = zip.CreateEntry("document.json").Open(); JsonSerializer.Serialize(target, manifest);
                }
                Reject(() => ProjectStore.Load(output));
            }
            Corrupt((zip, _) => zip.GetEntry("materials/0.png")!.Delete());
            Corrupt((_, manifest) => manifest["Version"] = 5);
            Corrupt((_, manifest) => manifest["Layers"]![0]!["Material"]!["MaterialId"] = Guid.NewGuid().ToString());
            Corrupt((_, manifest) => manifest["Materials"]![0]!["Width"] = 8193);
            Corrupt((_, manifest) => manifest["MaterialRegions"]![0]!["Path"]!["Data"] = "M0,0 L10,0 L10,10");
        });
    }
}
