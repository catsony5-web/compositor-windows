using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

// Lines, curves, arrows, dashes and callouts (ShapeKind.Line / .Callout): geometry, fitting, editing,
// picking, project round trip and export parity.
public static class DiagramShapeTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "diagram-shapes"); Directory.CreateDirectory(root);
        static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
        static bool Near(double a, double b, double tolerance = .01) => Math.Abs(a - b) <= tolerance;
        static ShapeSpec Line(params Point[] points) => new()
        {
            Kind = ShapeKind.Line, Points = new ShapePoints(points), StrokeEnabled = true, StrokeArgb = 0xFF101820, StrokeWidth = 2, FillEnabled = false, Width = 1, Height = 1
        };
        static ShapeSpec Callout(Point anchor, Point label, string text = "Lobby") => new()
        {
            Kind = ShapeKind.Callout, Points = new ShapePoints([anchor, DiagramEditing.DefaultElbow(anchor, label), label]), StrokeEnabled = true, StrokeArgb = 0xFF101820,
            StrokeWidth = 1.5, StartMark = LineMark.Dot, FillEnabled = false, Width = 1, Height = 1,
            Label = new TextSpec { Content = text, FontFamily = "Segoe UI", FontSize = 16, ColorArgb = 0xFF101820 }
        };
        static Document Board(params Layer[] layers)
        {
            var doc = new Document { Width = 320, Height = 220, Name = "diagram" };
            doc.Add(new Layer { Name = "paper", Pixels = Raster.Solid(320, 220, Colors.White) });
            foreach (var layer in layers) doc.Add(layer);
            return doc;
        }
        static double Difference(Raster a, Raster b)
        {
            double sum = 0; for (int i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);
            return sum / a.Data.Length;
        }
        // Runs of dark pixels along one row: (start, length) for each dash.
        static List<(int Start, int Length)> Runs(Raster image, int y, int from, int to)
        {
            var runs = new List<(int, int)>(); int start = -1;
            for (int x = from; x < to; x++)
            {
                bool ink = image.Data[(y * image.Width + x) * 4 + 3] > 127;
                if (ink && start < 0) start = x;
                if (!ink && start >= 0) { runs.Add((start, x - start)); start = -1; }
            }
            if (start >= 0) runs.Add((start, to - start));
            return runs;
        }

        test("diagram: dash patterns follow stroke width, spacing and line ends", () =>
        {
            var dashed = Line(new(0, 0), new(200, 0)) with { Dash = StrokeDash.Dashed, Cap = StrokeCap.Flat };
            Check(ShapeGeometry.VisibleDashLengths(dashed)!.SequenceEqual([10d, 6d]) && ShapeGeometry.DashLengths(dashed)!.SequenceEqual([10d, 6d]), "Flat dashed lengths");
            var round = dashed with { Cap = StrokeCap.Round };
            Check(ShapeGeometry.DashLengths(round)!.SequenceEqual([8d, 8d]), "Round ends must shorten the pen dash by one width and widen the gap");
            var dotted = round with { Dash = StrokeDash.Dotted };
            Check(ShapeGeometry.VisibleDashLengths(dotted)!.SequenceEqual([2d, 4d]) && ShapeGeometry.DashLengths(dotted)!.SequenceEqual([0d, 6d]), "Round dots are zero-length dashes");
            var dashDot = round with { Dash = StrokeDash.DashDot, DashScale = 2 };
            Check(ShapeGeometry.VisibleDashLengths(dashDot)!.SequenceEqual([40d, 12d, 2d, 12d]), "Dash-dot pattern or its spacing scale is wrong");
            Check(ShapeGeometry.DashLengths(round with { Dash = StrokeDash.Solid }) == null, "A solid stroke has no dash pattern");
            // Measured on the rendered line: 10 px dashes and 6 px gaps for both flat and round ends.
            foreach (var cap in new[] { StrokeCap.Flat, StrokeCap.Round })
            {
                var layer = VectorShapes.Create(Line(new(20, 20), new(220, 20)) with { Dash = StrokeDash.Dashed, Cap = cap });
                var local = layer.Shape!.Points![0]; int y = (int)local.Y;
                var runs = Runs(layer.Pixels, y, 0, layer.Pixels.Width);
                var inner = runs.Skip(1).Take(runs.Count - 2).ToArray();
                Check(inner.Length >= 8, $"{cap}: expected many dashes, found {runs.Count}");
                Check(inner.All(r => Math.Abs(r.Length - 10) <= 1), $"{cap}: dash lengths {string.Join(",", inner.Select(r => r.Length))}");
                for (int i = 1; i < inner.Length; i++) Check(Math.Abs(inner[i].Start - inner[i - 1].Start - 16) <= 1, $"{cap}: dash period is not 16 px");
            }
        });

        test("diagram: arrowheads sit on the end points and point along the line", () =>
        {
            var spec = Line(new(20, 50), new(120, 50)) with { StartMark = LineMark.Arrow, EndMark = LineMark.Arrow, MarkSize = 12 };
            var marks = ShapeGeometry.Marks(spec);
            Check(marks.Length == 2 && marks.All(m => m.Filled), "Two filled arrowheads expected");
            var start = marks[0].Geometry.Bounds; var end = marks[1].Geometry.Bounds;
            Check(Near(end.Right, 120, .01) && Near(end.Left, 108, .01) && Near((end.Top + end.Bottom) / 2, 50, .01), $"End arrow bounds {end}");
            Check(Near(start.Left, 20, .01) && Near(start.Right, 32, .01), $"Start arrow must point back at the first point: {start}");
            var visible = ShapeGeometry.VisiblePath(spec);
            Check(Near(visible[0].Start.X, 29, .05) && Near(visible[^1].End.X, 111, .05), "The stroke must end inside the arrowheads (0.75 × length)");
            // A diagonal: the head's centroid lies behind the tip along the line.
            var diagonal = Line(new(10, 10), new(70, 90)) with { EndMark = LineMark.Arrow, MarkSize = 20 };
            var head = (PathGeometry)PathGeometry.CreateFromGeometry(ShapeGeometry.Marks(diagonal)[0].Geometry);
            var corners = new List<Point> { head.Figures[0].StartPoint }; foreach (var s in head.Figures[0].Segments.OfType<LineSegment>()) corners.Add(s.Point);
            var tip = corners.OrderByDescending(p => p.Y).First(); var centroid = new Point(corners.Average(p => p.X), corners.Average(p => p.Y));
            var along = tip - centroid; along.Normalize(); var direction = new Vector(60, 80); direction.Normalize();
            Check(Near(tip.X, 70, .01) && Near(tip.Y, 90, .01) && Vector.Multiply(along, direction) > .999, "Diagonal arrow tip or orientation is wrong");
            // Dots and rings are centred on the end; bars cross it; an open arrow stays a stroke.
            var other = Line(new(20, 50), new(120, 50)) with { StartMark = LineMark.Dot, EndMark = LineMark.Bar };
            var otherMarks = ShapeGeometry.Marks(other);
            Check(Near(otherMarks[0].Geometry.Bounds.X + otherMarks[0].Geometry.Bounds.Width / 2, 20) && otherMarks[0].Filled, "Dot must be centred on the start");
            Check(!otherMarks[1].Filled && Near(otherMarks[1].Geometry.Bounds.X, 120) && otherMarks[1].Geometry.Bounds.Height > 8, "Bar must cross the end");
            Check(!ShapeGeometry.Marks(other with { EndMark = LineMark.OpenArrow })[1].Filled, "Open arrow is a stroke");
            // Curve: the end arrow follows the visible stroke's last direction.
            var curve = Line(new(10, 100), new(60, 20), new(140, 60)) with { Smooth = true, EndMark = LineMark.Arrow, MarkSize = 14 };
            var curveHead = ShapeGeometry.Marks(curve)[0].Geometry.Bounds;
            Check(curveHead.Contains(new Point(139.5, 60)) || Near(curveHead.Right, 140, .5), "Curve arrow must end on the last point");
        });

        test("diagram: a curve passes through every point with smooth joins", () =>
        {
            var points = new[] { new Point(10, 80), new Point(50, 10), new Point(90, 70), new Point(200, 40), new Point(205, 120) };
            var segments = ShapeGeometry.Centerline(points, true, false);
            Check(segments.Length == 4, "One cubic per span");
            for (int i = 0; i < segments.Length; i++)
                Check(segments[i].Start == points[i] && segments[i].End == points[i + 1] && !segments[i].Straight, $"Segment {i} does not run between its points");
            for (int i = 1; i < segments.Length; i++)
            {
                var into = points[i] - segments[i - 1].Control2; var outof = segments[i].Control1 - points[i];
                into.Normalize(); outof.Normalize();
                Check(Vector.Multiply(into, outof) > .9999, $"The curve has a corner at point {i}");
            }
            var closed = ShapeGeometry.Centerline(points, true, true);
            Check(closed.Length == 5 && closed[^1].End == points[0], "A closed curve returns to its first point");
            var straight = ShapeGeometry.Centerline(points, false, false);
            Check(straight.All(s => s.Straight), "Straight mode makes line segments");
            // Very uneven spacing stays inside a sensible box (centripetal: no loops or overshoot spikes).
            var uneven = ShapeGeometry.Centerline([new(0, 0), new(1, 0), new(200, 0), new(201, 50)], true, false);
            for (double t = 0; t <= 1; t += .05) Check(uneven[1].At(t).Y > -40 && uneven[1].At(t).Y < 60, "Uneven points make a loop or a spike");
            // Inserting a point keeps the curve through all points.
            var spec = Line(points) with { Smooth = true };
            var near = ShapeGeometry.Nearest(spec, new Point(150, 47))!.Value;
            Check(near.InsertIndex == 3 && near.Distance < 12, $"Nearest span {near.InsertIndex} at {near.Distance:0.##}");
            var inserted = DiagramEditing.InsertPoint(spec, near.InsertIndex, near.At);
            Check(inserted.Points!.Count == 6 && inserted.Points[3] == near.At, "Inserted point is not stored in order");
        });

        test("diagram: fitting keeps every point on the canvas through edits and transforms", () =>
        {
            var points = new[] { new Point(100, 100), new Point(180, 60), new Point(260, 140) };
            var layer = VectorShapes.Create(Line(points) with { EndMark = LineMark.Arrow });
            for (int i = 0; i < points.Length; i++) { var at = layer.Document(layer.Shape!.Points![i]); Check(Near(at.X, points[i].X, 1e-9) && Near(at.Y, points[i].Y, 1e-9), "Created point moved"); }
            var refit = ShapeGeometry.Fit(layer.Shape!);
            Check(refit.Shift == default && refit.Spec == layer.Shape, "Fitting a fitted spec must change nothing");
            var bounds = ShapeGeometry.ContentBounds(layer.Shape!);
            Check(bounds.Left >= 1 && bounds.Top >= 1 && bounds.Right <= layer.Pixels.Width - 1 && bounds.Bottom <= layer.Pixels.Height - 1, "Content must sit inside the surface");
            layer.Rotation = 33; layer.Scale = 1.4; layer.FlipX = true;
            var before = layer.Shape!.Points!.Select(layer.Document).ToArray();
            var moved = DiagramEditing.MovePoint(layer.Shape!, 2, layer.Local(new Point(400, 30)));
            VectorShapes.Update(layer, moved with { StrokeWidth = 9, MarkSize = 40 });
            var after = layer.Shape!.Points!.Select(layer.Document).ToArray();
            Check((after[0] - before[0]).Length < 1e-6 && (after[1] - before[1]).Length < 1e-6 && (after[2] - new Point(400, 30)).Length < 1e-6, "Unedited points moved or the edited point missed its target");
            Check(layer.Pixels.Width == layer.Shape.Width && layer.Pixels.Height == layer.Shape.Height, "Pixel cache does not match the fitted surface");
            Board(layer).Validate();
            Check(DiagramEditing.Reverse(layer.Shape).Points![0] == layer.Shape.Points![^1] && DiagramEditing.Reverse(layer.Shape).StartMark == LineMark.Arrow, "Reversing must swap points and marks");
            Check(Throws(() => DiagramEditing.DeletePoint(Line(new(0, 0), new(5, 5)), 0)), "A line must keep two points");
            Check(Throws(() => VectorShapes.Create(Line(new(0, 0), new(70_000, 10)))), "A line beyond the surface limit must be refused");
            Check(Throws(() => new ShapeSpec { Kind = ShapeKind.Line, Points = new([new Point(0, 0)]) }.Validate()), "A one-point line must be invalid");
            Check(Throws(() => new ShapeSpec { Points = new([new Point(0, 0), new Point(1, 1)]) }.Validate()), "A rectangle with points must be invalid");
            static bool Throws(Action action) { try { action(); return false; } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return true; } }
        });

        test("diagram: callout label sits beside the leader end and the bend stays level", () =>
        {
            var anchor = new Point(60, 150); var label = new Point(160, 60);
            var layer = VectorShapes.Create(Callout(anchor, label));
            var spec = layer.Shape!; var rect = ShapeGeometry.LabelRect(spec); var end = spec.Points![2];
            double pad = ShapeGeometry.Padding(spec.Label!);
            Check(rect.Left > end.X && Near(rect.Left - end.X, pad, .01) && Near((rect.Top + rect.Bottom) / 2, end.Y, .01), $"Label {rect} is not beside the leader end {end}");
            var leftward = VectorShapes.Create(Callout(new Point(260, 150), new Point(160, 60)));
            var leftRect = ShapeGeometry.LabelRect(leftward.Shape!);
            Check(Near(leftward.Shape!.Points![2].X - leftRect.Right, pad, .01), "A leader coming from the right must put the label on the left");
            Check(spec.Points[1] == new Point(spec.Points[0].X, spec.Points[2].Y), "The default bend is straight above the target, level with the label");
            // Moving the label end drags the bend's height along; moving the bend drags the label's.
            var movedLabel = DiagramEditing.MovePoint(spec, DiagramEditing.LabelPoint, new Point(200, 30));
            Check(movedLabel.Points![1].Y == 30 && movedLabel.Points[1].X == spec.Points[1].X, "The bend must follow the label's height");
            var movedElbow = DiagramEditing.MovePoint(spec, DiagramEditing.Elbow, new Point(90, 40));
            Check(movedElbow.Points![2].Y == 40 && movedElbow.Points[2].X == spec.Points[2].X, "The label must follow the bend's height");
            var straight = spec with { Leader = CalloutLeader.Straight };
            Check(DiagramEditing.MovePoint(straight, DiagramEditing.LabelPoint, new Point(1, 2)).Points![1] == straight.Points![1], "A straight leader has no shoulder to keep level");
            Check(ShapeGeometry.FullPath(straight).Length == 1 && ShapeGeometry.FullPath(spec).Length == 2, "Leader segments");
            // Hit tests: the label, the leader and dash gaps count; empty space does not.
            Check(ShapeGeometry.Hit(spec, new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)), "The label text must be hittable");
            Check(ShapeGeometry.Hit(spec, spec.Points[0] + new Vector(0, -30), 1), "The leader must be hittable");
            Check(!ShapeGeometry.Hit(spec, new Point(spec.Points[0].X + 40, spec.Points[0].Y)), "Empty space is not part of the callout");
            var boxed = VectorShapes.Create(Callout(anchor, label) with { FillEnabled = true, FillArgb = 0xFFFFE680 });
            Check(boxed.Pixels.Width > layer.Pixels.Width && ShapeGeometry.Ink(boxed.Shape!).Bounds.Width > 0, "The label box must widen the callout");
            // The label uses the text engine: an outline widens the text surface.
            var outlined = VectorShapes.Create(Callout(anchor, label) with { Label = spec.Label! with { Outline = true, OutlineWidth = 6 } });
            Check(ShapeGeometry.LabelRect(outlined.Shape!).Width >= rect.Width && outlined.Pixels.Height >= layer.Pixels.Height + 6, "Label outline was ignored");
        });

        test("diagram: picking and selection treat dash gaps as part of the line", () =>
        {
            var line = VectorShapes.Create(Line(new(40, 40), new(240, 40)) with { Dash = StrokeDash.Dashed, DashScale = 3, StrokeWidth = 2, Cap = StrokeCap.Flat });
            var doc = Board(line);
            var gapRuns = Runs(line.Pixels, (int)line.Shape!.Points![0].Y, 0, line.Pixels.Width);
            var gapLocal = new Point(gapRuns[1].Start + gapRuns[1].Length + 4, line.Shape.Points[0].Y);
            var gap = line.Document(gapLocal);
            Check(line.Pixels.Data[((int)gapLocal.Y * line.Pixels.Width + (int)gapLocal.X) * 4 + 3] == 0, "Probe is not in a dash gap");
            Check(LayerPicking.Pick(doc, gap)?.Id == line.Id, "A click in a dash gap must pick the line");
            Check(LayerPicking.Pick(doc, new Point(140, 70))?.Id != line.Id, "A click away from the line picked it");
            Check(ObjectSelection.Find(doc, new Rect(30, 30, 220, 20), false).Contains(line.Id), "A marquee around the line must select it");
            var closed = VectorShapes.Create(Line(new(60, 100), new(160, 100), new(160, 180), new(60, 180)) with { Closed = true, FillEnabled = true, FillArgb = 0xFF80C0FF });
            doc = Board(closed);
            Check(LayerPicking.Pick(doc, new Point(110, 140))?.Id == closed.Id, "Inside a filled closed line must pick it");
            var outline = MaterialEditing.ClosedLayer(doc, closed.Id);
            Check(Near(outline.Bounds.Left, 60, .5) && Near(outline.Bounds.Bottom, 180, .5), $"Closed line boundary {outline.Bounds}");
        });

        test("diagram: project round trip keeps lines, curves, dashes and callouts", () =>
        {
            var curve = VectorShapes.Create(Line(new(20, 160), new(90, 40), new(200, 120)) with { Smooth = true, Dash = StrokeDash.DashDot, DashScale = 1.5, Cap = StrokeCap.Square, StartMark = LineMark.Dot, EndMark = LineMark.OpenArrow, MarkSize = 15 });
            curve.Rotation = 12;
            var callout = VectorShapes.Create(Callout(new(240, 180), new(150, 90), "주출입구\nMain entry") with { Leader = CalloutLeader.Elbow, FillEnabled = true, EndMark = LineMark.Ring });
            var dashedBox = VectorShapes.Create(new ShapeSpec { Width = 80, Height = 50, StrokeEnabled = true, StrokeWidth = 3, Dash = StrokeDash.Dashed, FillEnabled = false }, 200, 20);
            var doc = Board(curve, callout, dashedBox);
            string file = Path.Combine(root, "diagram.moruproj"); ProjectStore.Save(doc, file);
            var read = ProjectStore.Load(file);
            for (int i = 1; i < 4; i++) Check(read.Layers[i].Shape == doc.Layers[i].Shape && read.Layers[i].X == doc.Layers[i].X && read.Layers[i].Rotation == doc.Layers[i].Rotation, $"Layer {i} changed in the project");
            Check(DesignRenderer.RenderOutput(read).Data.SequenceEqual(DesignRenderer.RenderOutput(doc).Data), "Reopened board renders differently");
            // Old projects: a rectangle without the new fields reads with a solid outline and no points.
            var legacy = JsonSerializer.Deserialize<ShapeSpec>("{\"Kind\":0,\"Width\":10,\"Height\":8,\"FillArgb\":4294901760,\"StrokeArgb\":4278190080,\"FillEnabled\":true,\"StrokeEnabled\":true,\"StrokeWidth\":2,\"CornerRadius\":0}")!;
            legacy.Validate();
            Check(legacy.Dash == StrokeDash.Solid && legacy.Points == null && legacy.DashScale == 1 && legacy.MarkSize == 12 && !ShapeGeometry.UsesDrawing(legacy), "Legacy shape fields");
            string json = JsonSerializer.Serialize(new ShapeSpec());
            Check(!json.Contains("Points") && !json.Contains("Label") && !json.Contains("HasPoints"), "Rectangles must not write line fields: " + json);
            Check(JsonSerializer.Serialize(curve.Shape).Contains("\"Points\":[["), "Points are stored as [x, y] pairs");
            var history = new History(); history.Reset(doc); var before = doc.Snapshot(); var original = doc.Layers[1].Shape;
            VectorShapes.Update(doc.Layers[1], DiagramEditing.MovePoint(doc.Layers[1].Shape!, 1, new Point(100, 30)));
            Check(doc.Layers[1].Shape != original, "The point edit changed nothing");
            history.Commit("점", before, doc); doc = history.Undo(doc);
            Check(doc.Layers[1].Shape == original, "Undo did not restore the line");
        });

        test("diagram: exports keep vector strokes in PDF and .ai and match the canvas in PNG and .psd", () =>
        {
            var arrow = VectorShapes.Create(Line(new(30, 170), new(110, 60), new(250, 90)) with { Smooth = true, Dash = StrokeDash.Dashed, EndMark = LineMark.Arrow, StartMark = LineMark.Dot, StrokeWidth = 2.5 });
            var callout = VectorShapes.Create(Callout(new(270, 190), new(190, 140), "Atrium"));
            var doc = Board(arrow, callout);
            var output = DesignRenderer.RenderOutput(doc);
            // Vector PDF: only the paper is an image; the page content has a dash operator, round caps and curves.
            using (var encoded = new MemoryStream())
            {
                VectorPdfExport.Write(doc, encoded); encoded.Position = 0;
                using var pdf = PdfReader.Open(encoded, PdfDocumentOpenMode.Import);
                var page = pdf.Pages[0];
                var xobjects = page.Resources.Elements.GetDictionary("/XObject");
                int images = xobjects?.Elements.Values.Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).Count(d => d?.Elements.GetName("/Subtype") == "/Image") ?? 0;
                Check(images == 1, $"Only the paper may be an image; the diagram must stay vector ({images} images)");
                string content = System.Text.Encoding.ASCII.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);
                Check(System.Text.RegularExpressions.Regex.IsMatch(content, @"\[[0-9. ]+\]\s*-?[0-9.]+\s+d"), "The PDF stroke lost its dash pattern");
                Check(content.Contains("1 J"), "The PDF stroke lost its round ends");
                Check(System.Text.RegularExpressions.Regex.Matches(content, @"\bc\b").Count >= 4, "The curve must stay Bézier segments in the PDF");
            }
            foreach (var format in new[] { CompatibilityExportFormat.PdfLayers, CompatibilityExportFormat.AiLayers })
            {
                string path = Path.Combine(root, "diagram" + (format == CompatibilityExportFormat.AiLayers ? ".ai" : ".pdf"));
                ProjectStore.AtomicWrite(path, s => CompatibilityExport.Write(doc, format, s));
                var reopened = Imaging.Render(Task.Run(() => CompatibilityImport.ReadAsync(path, new(Dpi: 96, PreservePdfLayers: false))).GetAwaiter().GetResult().Document);
                Check(Difference(reopened, output) < 4, $"{format} looks different ({Difference(reopened, output):0.###})");
            }
            string psd = Path.Combine(root, "diagram.psd");
            ProjectStore.AtomicWrite(psd, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PsdLayers, s));
            var back = PsdCompatibility.Read(psd, true).Document;
            Check(Difference(DesignRenderer.RenderOutput(back), output) < .8, "The layered .psd moved or lost the diagram");
            string png = Path.Combine(root, "diagram.png"); ProjectStore.Export(doc, png);
            Check(Difference(Raster.Load(png), output) < .01, "PNG export differs from the output render");
            // Crisp at any zoom: a 2× render equals the same drawing made twice as large.
            var doubled = new Document { Width = 640, Height = 440, Name = "2x" };
            doubled.Add(new Layer { Name = "paper", Pixels = Raster.Solid(640, 440, Colors.White) });
            var big = arrow.Shape! with { Points = new ShapePoints(arrow.Shape!.Points!.Select(p => new Point(p.X * 2, p.Y * 2))), StrokeWidth = 5, MarkSize = arrow.Shape.MarkSize * 2 };
            doubled.Add(VectorShapes.Create(big, arrow.X * 2, arrow.Y * 2));
            var lineOnly = Board(arrow);
            var scaled = DesignRenderer.RenderScaled(lineOnly, new Rect(0, 0, 320, 220), 640, 440);
            double difference = Difference(scaled, DesignRenderer.RenderOutput(doubled));
            Check(difference < 1.5, $"A 2× render is not the drawing at twice the size ({difference:0.###})");
        });
    }
}
