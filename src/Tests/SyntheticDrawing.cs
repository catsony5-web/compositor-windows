using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// A deterministic stand-in for a large imported site plan. It uses the same
// node kinds as the CAD object importer: a locked paper shape, Multiply material
// layers, source-layer folders that share one transparent page surface, and
// tight retained-vector objects with small pixel previews, all wrapped in one
// drawing folder. Generation draws previews directly instead of through WPF so
// tests can build tens of thousands of objects quickly.
public static class SyntheticDrawing
{
    public static Document Create(int groups = 50, int objectsPerGroup = 380, int width = 2400, int height = 1707, int materials = 4, int seed = 7)
    {
        var random = new Random(seed);
        var doc = new Document { Name = "합성 도면", Width = width, Height = height };
        var paper = VectorShapes.Create(new ShapeSpec { Width = width, Height = height, FillArgb = 0xFFFFFFFF });
        paper.Name = "도면 배경"; paper.Locked = true; doc.Layers.Add(paper);
        var surface = new Raster(width, height);
        for (int g = 0; g < groups; g++)
        {
            var folder = new Layer { Name = $"도면층 {g + 1}", SourceLayerName = $"A-{g + 1:00}", Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = surface };
            doc.Layers.Add(folder);
            var color = Color.FromRgb((byte)random.Next(0, 160), (byte)random.Next(0, 160), (byte)random.Next(0, 160));
            for (int i = 0; i < objectsPerGroup; i++)
            {
                var item = Object(random, width, height, color, i); item.ParentId = folder.Id; doc.Layers.Add(item);
            }
        }
        for (int m = 0; m < materials; m++)
        {
            var asset = MaterialPresets.Create((MaterialKind)(m % Enum.GetValues<MaterialKind>().Length));
            if (!doc.Materials.Any(a => a.Id == asset.Id)) doc.Materials.Add(asset);
            double size = Math.Min(360, Math.Min(width, height) / 2.5);
            double x = 20 + random.NextDouble() * (width - size - 40), y = 20 + random.NextDouble() * (height - size - 40);
            var polygon = MaterialEditing.Polygon([[new(x, y), new(x + size, y + size * .05), new(x + size * .92, y + size * .84), new(x + size * .05, y + size * .95)]]);
            var region = MaterialEditing.Region(doc, $"해치 {m + 1}", polygon, "polygon"); doc.MaterialRegions.Add(region);
            var layer = MaterialEditing.Apply(doc, asset.Id, region.Id, 120, 120 * asset.Pixels.Height / Math.Max(1, asset.Pixels.Width));
            layer.Name = "재질 · " + region.Name; doc.Layers.Insert(1 + m, layer);
        }
        doc.ActiveId = doc.Layers[^1].Id;
        DrawingLayers.Wrap(doc);
        return doc;
    }

    static Layer Object(Random random, int width, int height, Color color, int index)
    {
        // Lines dominate site plans; closed outlines and circles add filled bounds.
        var points = new List<Point>(); bool closed; string name;
        double cx = 30 + random.NextDouble() * (width - 60), cy = 30 + random.NextDouble() * (height - 60);
        int kind = index % 20;
        if (kind < 12)
        {
            double length = 4 + random.NextDouble() * 150, angle = random.NextDouble() * Math.Tau;
            points.Add(new(cx, cy)); points.Add(new(Math.Clamp(cx + Math.Cos(angle) * length, 3, width - 3), Math.Clamp(cy + Math.Sin(angle) * length, 3, height - 3)));
            closed = false; name = "선";
        }
        else if (kind < 17)
        {
            double w = 6 + random.NextDouble() * 80, h = 6 + random.NextDouble() * 80;
            double left = Math.Min(cx, width - w - 3), top = Math.Min(cy, height - h - 3);
            points.AddRange([new(left, top), new(left + w, top), new(left + w, top + h), new(left, top + h)]);
            closed = true; name = "닫힌 폴리라인";
        }
        else
        {
            double r = 3 + random.NextDouble() * 36; cx = Math.Clamp(cx, r + 3, width - r - 3); cy = Math.Clamp(cy, r + 3, height - r - 3);
            for (int i = 0; i < 24; i++) points.Add(new(cx + Math.Cos(i * Math.Tau / 24) * r, cy + Math.Sin(i * Math.Tau / 24) * r));
            closed = true; name = "원";
        }
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y), maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 2), y0 = Math.Max(0, (int)Math.Floor(minY) - 2);
        int pixelWidth = Math.Max(1, Math.Min(width, (int)Math.Ceiling(maxX) + 2) - x0), pixelHeight = Math.Max(1, Math.Min(height, (int)Math.Ceiling(maxY) + 2) - y0);
        var local = points.Select(p => new Point(p.X - x0, p.Y - y0)).ToArray();
        var path = new StreamGeometry();
        using (var context = path.Open()) { context.BeginFigure(local[0], false, closed); context.PolyLineTo(local.Skip(1).ToArray(), true, false); }
        path.Freeze();
        var vector = VectorContent.FromPaths(pixelWidth, pixelHeight, [new VectorPrimitive(path, color, false, 1)]);
        var preview = new Raster(pixelWidth, pixelHeight);
        for (int i = 0; i < local.Length - (closed ? 0 : 1); i++) Plot(preview, local[i], local[(i + 1) % local.Length], color);
        return new Layer { Name = $"{name} {index + 1}", Kind = LayerKind.Vector, Category = LayerCategory.Automatic, Vector = vector, Pixels = preview, X = x0, Y = y0 };
    }

    static void Plot(Raster raster, Point a, Point b, Color color)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        for (int s = 0; s <= steps; s++)
        {
            int x = (int)(a.X + (b.X - a.X) * s / steps), y = (int)(a.Y + (b.Y - a.Y) * s / steps);
            if (x < 0 || y < 0 || x >= raster.Width || y >= raster.Height) continue;
            int i = (y * raster.Width + x) * 4;
            raster.Data[i] = color.B; raster.Data[i + 1] = color.G; raster.Data[i + 2] = color.R; raster.Data[i + 3] = 255;
        }
    }
}
