using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// A small deterministic floor plan with the structure of a CAD object import: a locked paper
// shape, CAD-layer folders (walls, partitions with door openings, doors, columns, a stair,
// furniture, dimensions) holding tight vector objects, all wrapped in one drawing folder.
// Design style checks and offscreen captures use it; rooms are closed by walls and doors.
public static class SyntheticPlan
{
    public static Document Create(int width = 2400, int height = 1600, string name = "평면 예시")
    {
        var doc = new Document { Name = name, Width = width, Height = height };
        double s = Math.Min(width / 2400d, height / 1600d);
        var paper = VectorShapes.Create(new ShapeSpec { Width = width, Height = height, FillArgb = 0xFFFFFFFF }); paper.Name = "도면 배경"; paper.Locked = true; doc.Layers.Add(paper);
        var surface = new Raster(width, height);
        Layer Folder(string source)
        {
            var folder = new Layer { Name = source, SourceLayerName = source, Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = surface };
            doc.Layers.Add(folder); return folder;
        }
        int count = 0;
        void Add(Layer folder, string kind, Color color, double stroke, params Point[][] lines)
        {
            var points = lines.SelectMany(l => l).Select(p => new Point(p.X * s, p.Y * s)).ToArray();
            double pad = stroke + 2;
            double left = Math.Max(0, Math.Floor(points.Min(p => p.X) - pad)), top = Math.Max(0, Math.Floor(points.Min(p => p.Y) - pad));
            int w = Math.Max(1, (int)Math.Ceiling(Math.Min(width, points.Max(p => p.X) + pad) - left)), h = Math.Max(1, (int)Math.Ceiling(Math.Min(height, points.Max(p => p.Y) + pad) - top));
            var path = new StreamGeometry();
            using (var context = path.Open())
                foreach (var line in lines)
                {
                    bool closed = line.Length > 2 && line[0] == line[^1];
                    var local = line.Select(p => new Point(p.X * s - left, p.Y * s - top)).ToArray();
                    context.BeginFigure(local[0], false, closed); context.PolyLineTo(local.Skip(1).Take(closed ? local.Length - 2 : local.Length - 1).ToArray(), true, false);
                }
            path.Freeze();
            var vector = VectorContent.FromPaths(w, h, [new VectorPrimitive(path, color, false, stroke * Math.Max(.5, s))]);
            var preview = Imaging.Draw(w, h, dc => dc.DrawDrawing(vector.Drawing));
            doc.Layers.Add(new Layer { Name = $"{kind} {++count}", Kind = LayerKind.Vector, Vector = vector, Pixels = preview, X = left, Y = top, ParentId = folder.Id });
        }
        static Point[] Box(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0)];
        static Point[] Arc(Point center, double radius, double from, double to, int steps = 18) =>
            Enumerable.Range(0, steps + 1).Select(i => { double a = (from + (to - from) * i / steps) * Math.PI / 180; return new Point(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius); }).ToArray();
        var ink = Color.FromRgb(0x14, 0x16, 0x1A); var door = Color.FromRgb(0x33, 0x38, 0x40); var furniture = Color.FromRgb(0x9A, 0xA0, 0xA8); var dims = Color.FromRgb(0x7A, 0x80, 0x8A);

        var walls = Folder("A-WALL");
        Add(walls, "외벽", ink, 2.4, Box(300, 240, 2100, 1360), Box(324, 264, 2076, 1336));
        Add(walls, "칸막이", ink, 1.6, Box(893, 264, 907, 560), Box(893, 650, 907, 1336));
        Add(walls, "칸막이", ink, 1.6, Box(1493, 264, 1507, 420), Box(1493, 510, 1507, 793));
        Add(walls, "칸막이", ink, 1.6, Box(324, 793, 560, 807), Box(650, 793, 893, 807), Box(907, 793, 1150, 807), Box(1240, 793, 1750, 807), Box(1840, 793, 2076, 807));
        var doors = Folder("A-DOOR");
        // Leaf and swing close each opening on one side.
        Add(doors, "문", door, 1.2, [new(907, 560), new(997, 560)], Arc(new(907, 560), 90, 0, 90));
        Add(doors, "문", door, 1.2, [new(1507, 420), new(1597, 420)], Arc(new(1507, 420), 90, 0, 90));
        Add(doors, "문", door, 1.2, [new(650, 807), new(650, 897)], Arc(new(650, 807), 90, 90, 180));
        Add(doors, "문", door, 1.2, [new(1240, 807), new(1240, 897)], Arc(new(1240, 807), 90, 90, 180));
        Add(doors, "문", door, 1.2, [new(1840, 793), new(1840, 703)], Arc(new(1840, 793), 90, 270, 180));
        var columns = Folder("A-COLS");
        foreach (double x in new[] { 1200d, 1500, 1800 }) Add(columns, "기둥", ink, 1.6, Box(x - 18, 1052, x + 18, 1088));
        var stair = Folder("A-STRS");
        Add(stair, "계단", ink, 1.2, [.. new[] { Box(1640, 330, 1800, 700) }.Concat(Enumerable.Range(1, 12).Select(i => new Point[] { new(1640, 330 + i * 28.5), new(1800, 330 + i * 28.5) }))]);
        var furnishing = Folder("A-FURN");
        Add(furnishing, "가구", furniture, 1, Box(420, 360, 760, 560), Box(470, 610, 710, 700));
        Add(furnishing, "가구", furniture, 1, Box(420, 1000, 640, 1260));
        Add(furnishing, "가구", furniture, 1, Box(1050, 330, 1350, 470));
        Add(furnishing, "가구", furniture, 1, Box(1100, 1180, 1450, 1290));
        var annotation = Folder("A-ANNO-DIMS");
        Add(annotation, "치수", dims, 1, [new(300, 1430), new(2100, 1430)], [new(300, 1410), new(300, 1450)], [new(2100, 1410), new(2100, 1450)], [new(1200, 1410), new(1200, 1450)]);
        DrawingLayers.Wrap(doc);
        doc.ActiveId = doc.Layers[0].Id;
        return doc;
    }
}

// A photo-like image without any file: sky, a low sun, hills, ground and a dark subject (a vase
// with flowers) in front, with grain. The subject mask is known, so checks can stand in for AI.
public static class SyntheticPhoto
{
    public static Document Create(int width = 1600, int height = 1000, string name = "sea window study")
    {
        var doc = new Document { Name = name, Width = width, Height = height };
        doc.Add(new Layer { Name = "사진", Pixels = Render(width, height) });
        return doc;
    }

    public static bool InSubject(double u, double v) =>
        (u - .5) * (u - .5) / (.11 * .11) + (v - .7) * (v - .7) / (.2 * .2) <= 1 || (u - .5) * (u - .5) / (.17 * .17) + (v - .38) * (v - .38) / (.15 * .15) <= 1;

    /// <summary>The subject coverage the checks use instead of the AI model.</summary>
    public static byte[] SubjectMask(Raster image, CancellationToken token = default)
    {
        var mask = new byte[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            mask[y * image.Width + x] = InSubject((x + .5) / image.Width, (y + .5) / image.Height) ? (byte)255 : (byte)0;
        return mask;
    }

    public static Raster Render(int width, int height)
    {
        var raster = new Raster(width, height);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double u = (x + .5) / width, v = (y + .5) / height, r, g, b;
                double horizon = .58 + .05 * Math.Sin(u * 7.1) + .03 * Math.Sin(u * 17.3 + 1.2);
                if (v < horizon)
                {
                    double t = v / horizon; r = .55 + .35 * t; g = .72 + .2 * t; b = .95 - .05 * t;
                    double sun = Math.Exp(-((u - .78) * (u - .78) + (v - .22) * (v - .22)) * 160); r += sun * .4; g += sun * .35; b += sun * .2;
                    double cloud = Math.Max(0, Math.Sin(u * 9 + v * 3) * Math.Sin(v * 21 + u * 2) - .55); r += cloud * .4; g += cloud * .4; b += cloud * .35;
                }
                else { double t = (v - horizon) / (1 - horizon); r = .32 - .12 * t; g = .45 - .15 * t; b = .3 - .12 * t; }
                if (InSubject(u, v))
                {
                    bool vase = (u - .5) * (u - .5) / (.11 * .11) + (v - .7) * (v - .7) / (.2 * .2) <= 1;
                    double shade = .5 + .5 * Math.Cos((u - .44) * 9);
                    if (vase) { r = .15 + .35 * shade; g = .2 + .3 * shade; b = .32 + .3 * shade; }
                    else { double petal = .5 + .5 * Math.Sin(u * 90) * Math.Sin(v * 80); r = .82 + .15 * petal; g = .78 + .1 * petal; b = .7 * (1 - .3 * petal); }
                }
                double n = (StyleTextures.Hash(x, y, 4242) - .5) * .06;
                int i = (y * width + x) * 4;
                raster.Data[i] = Imaging.Byte((b + n) * 255); raster.Data[i + 1] = Imaging.Byte((g + n) * 255); raster.Data[i + 2] = Imaging.Byte((r + n) * 255); raster.Data[i + 3] = 255;
            }
        });
        return raster;
    }
}
