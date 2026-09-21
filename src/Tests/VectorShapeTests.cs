using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class VectorShapeTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Assert(bool ok, string detail) { if (!ok) throw new Exception(detail); }
        static Document DocumentWith(Layer layer, int width = 96, int height = 80)
        {
            var doc = new Document { Width = width, Height = height }; doc.Add(layer); return doc;
        }
        static Point[] DocumentCorners(Layer layer) => new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }.Select(layer.Document).ToArray();
        static void Invalid(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Invalid shape specification accepted");
        }

        test("retained rounded shape scales as geometry instead of an enlarged pixel cache", () =>
        {
            var spec = new ShapeSpec { Width = 8, Height = 6, CornerRadius = 2, StrokeEnabled = true, StrokeWidth = 1, FillArgb = 0xFFEFBC62, StrokeArgb = 0xFF156B93 };
            var small = VectorShapes.Create(spec, 7, 9); small.Scale = 4;
            var large = VectorShapes.Create(spec with { Width = 32, Height = 24, CornerRadius = 8, StrokeWidth = 4 }, 7, 9);
            var scaled = Imaging.Render(DocumentWith(small));
            Assert(scaled.Data.SequenceEqual(Imaging.Render(DocumentWith(large)).Data), "Scaling the retained geometry differs from drawing the equivalent larger path");
            var cacheOnly = small.Snapshot(); cacheOnly.Kind = LayerKind.Raster; cacheOnly.Shape = null;
            Assert(!scaled.Data.SequenceEqual(Imaging.Render(DocumentWith(cacheOnly)).Data), "Retained shape appears to be rendered from the low-resolution pixel cache");
        });
        test("retained ellipse supports nonuniform scale with correct bounds and interior", () =>
        {
            var spec = new ShapeSpec { Kind = ShapeKind.Ellipse, Width = 8, Height = 6, FillArgb = 0xFFDA4878 };
            var small = VectorShapes.Create(spec, 4, 5); small.Scale = 3; small.ScaleX = 2;
            var reference = VectorShapes.Create(spec with { Width = 48, Height = 18 }, 4, 5);
            var scaled = Imaging.Render(DocumentWith(small));
            Assert(scaled.Data.SequenceEqual(Imaging.Render(DocumentWith(reference)).Data), "Nonuniform ellipse geometry is incorrect");
            Assert(scaled.Data[(14 * 96 + 28) * 4 + 3] == 255 && scaled.Data[(5 * 96 + 4) * 4 + 3] == 0, "Ellipse interior or corner bounds are incorrect");
        });
        test("shape fill and translucent stroke compose before mask and layer opacity", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Width = 10, Height = 10, FillArgb = 0x80FF0000, StrokeEnabled = true, StrokeWidth = 2, StrokeArgb = 0x800000FF });
            shape.Mask = Enumerable.Repeat((byte)128, 100).ToArray(); shape.Opacity = .5;
            var output = Imaging.Render(DocumentWith(shape, 10, 10));
            int edge = (4 * 10) * 4, center = (5 * 10 + 5) * 4;
            Assert(Math.Abs(output.Data[edge] - 170) <= 1 && Math.Abs(output.Data[edge + 2] - 85) <= 1 && Math.Abs(output.Data[edge + 3] - 48) <= 1, "Stroke must overlay fill before applying opacity and mask once");
            Assert(output.Data[center + 2] == 255 && Math.Abs(output.Data[center + 3] - 32) <= 1, "Interior fill opacity or straight-alpha color was changed");
        });
        test("shape property edits resize masks without mutating undo buffers or transform properties", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Width = 2, Height = 2 }, 11, 13);
            shape.Mask = [0, 64, 128, 255]; shape.Scale = 1.5; shape.ScaleX = .8; shape.ScaleY = 1.2; shape.Rotation = 37; shape.FlipX = true;
            var before = shape.Snapshot(); var originalMask = shape.Mask.ToArray(); var originalPixels = shape.Pixels.Data.ToArray();
            VectorShapes.Update(shape, shape.Shape! with { Width = 4, Height = 4, FillArgb = 0xFF984CC4 });
            Assert(shape.Pixels.Width == 4 && shape.Pixels.Height == 4 && shape.Mask!.Length == 16, "Cache and mask dimensions do not follow shape size");
            Assert(shape.Mask![0] == 0 && shape.Mask[3] == 64 && shape.Mask[12] == 128 && shape.Mask[15] == 255, "Mask normalized coverage was not preserved");
            Assert(before.Mask!.SequenceEqual(originalMask) && before.Pixels.Data.SequenceEqual(originalPixels) && !ReferenceEquals(shape.Mask, before.Mask) && !ReferenceEquals(shape.Pixels, before.Pixels), "Shape edit changed published history buffers");
            Assert(shape.X == before.X && shape.Y == before.Y && shape.Scale == before.Scale && shape.ScaleX == before.ScaleX && shape.ScaleY == before.ScaleY && shape.Rotation == before.Rotation && shape.FlipX == before.FlipX, "Editing shape properties unexpectedly changed its transform");
            DocumentWith(shape).Validate();
        });
        test("resizing a warped rotated shape preserves all document-space warp corners", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Width = 12, Height = 9 }, 25, 18);
            shape.Scale = 1.6; shape.ScaleX = .7; shape.ScaleY = 1.3; shape.Rotation = 31; shape.FlipX = true;
            shape.Warp = new WarpQuad(new Point(-2, 1), new Point(14, -1), new Point(11, 12), new Point(0, 8));
            var corners = DocumentCorners(shape); var beforeWarp = shape.Warp;
            VectorShapes.Update(shape, shape.Shape! with { Width = 20, Height = 16, CornerRadius = 2 });
            var changed = DocumentCorners(shape);
            Assert(corners.Zip(changed).All(pair => (pair.First - pair.Second).Length < 1e-7), "Shape resize moved the established destination quadrilateral");
            Assert(beforeWarp != shape.Warp, "Warp local coordinates were not adjusted for the changed transform center");
            DocumentWith(shape).Validate();
        });
        test("shape rasterization clears retained metadata preserves appearance and can be undone", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Width = 18, Height = 12, CornerRadius = 4, StrokeEnabled = true, StrokeWidth = 2 }, 3, 4);
            shape.Mask = Enumerable.Range(0, 216).Select(i => (byte)(i % 256)).ToArray(); shape.Opacity = .65;
            var doc = DocumentWith(shape); var before = doc.Snapshot(); var image = Imaging.Render(doc); var history = new History(); history.Reset(doc);
            DocumentFeatures.Rasterize(shape); doc.Validate();
            Assert(shape.Kind == LayerKind.Raster && shape.Shape == null, "Rasterized shape retained editable geometry metadata");
            var rasterized = Imaging.Render(doc);
            Assert(image.Data.Zip(rasterized.Data).All(pair => Math.Abs(pair.First - pair.Second) <= 1), "Native-scale rasterization changed the rendered result beyond 8-bit cache rounding");
            history.Commit("도형 래스터화", before, doc); doc = history.Undo(doc);
            Assert(doc.Active!.Kind == LayerKind.Shape && doc.Active.Shape == before.Active!.Shape && Imaging.Render(doc).Data.SequenceEqual(image.Data), "Undo did not restore retained shape geometry");
        });
        test("shape project round trip retains editable geometry mask transforms and render", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Kind = ShapeKind.Ellipse, Width = 18, Height = 12, FillArgb = 0xAADC957E, StrokeEnabled = true, StrokeArgb = 0xD0447691, StrokeWidth = 1.25 }, 18, 16);
            shape.Scale = 1.7; shape.ScaleX = .9; shape.Rotation = -15; shape.FlipY = true; shape.Opacity = .8;
            shape.Mask = Enumerable.Repeat((byte)192, 216).ToArray(); shape.Warp = new WarpQuad(new Point(0, 1), new Point(18, 0), new Point(20, 12), new Point(1, 11));
            var doc = DocumentWith(shape); doc.Dpi = 150; string path = Path.Combine(directory, "vector-shape-roundtrip.moruproj");
            Directory.CreateDirectory(directory); ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            Assert(loaded.Active!.Kind == LayerKind.Shape && loaded.Active.Shape == shape.Shape && loaded.Active.Warp == shape.Warp && loaded.Active.Mask!.SequenceEqual(shape.Mask) && loaded.Dpi == 150, "Saved shape lost editable specification or metadata");
            Assert(Imaging.Render(doc).Data.SequenceEqual(Imaging.Render(loaded).Data), "Shape render changed during save/load");
            VectorShapes.Update(loaded.Active, loaded.Active.Shape! with { FillArgb = 0xFF47A082 });
            Assert(loaded.Active.Shape!.FillArgb == 0xFF47A082 && shape.Shape!.FillArgb == 0xAADC957E, "Loaded shape was not independently editable");
        });
        test("invalid shape dimensions and nonfinite style values are rejected before edits", () =>
        {
            var shape = VectorShapes.Create(new ShapeSpec { Width = 12, Height = 8 }); var original = shape.Snapshot();
            foreach (var spec in new[] { shape.Shape! with { Width = 0 }, shape.Shape! with { Kind = (ShapeKind)99 }, shape.Shape! with { StrokeWidth = double.NaN }, shape.Shape! with { CornerRadius = double.PositiveInfinity } })
                Invalid(() => VectorShapes.Update(shape, spec));
            Assert(shape.Shape == original.Shape && ReferenceEquals(shape.Pixels, original.Pixels), "Rejected shape edits changed geometry or pixels");
            VectorShapes.Update(shape, shape.Shape!);
            Assert(ReferenceEquals(shape.Pixels, original.Pixels), "A no-op shape edit replaced its pixel cache");
        });
    }
}
