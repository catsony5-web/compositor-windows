using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

internal static class Typography
{
    // Adjust the advances of WPF's already shaped glyph clusters, retaining font
    // fallback, ligatures, combining marks, emoji sequences, and bidi ordering.
    public static Raster RenderTracked(TextSpec spec)
    {
        var face = new Typeface(new FontFamily(spec.FontFamily), spec.Italic ? FontStyles.Italic : FontStyles.Normal,
            spec.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush(DocumentFeatures.Color(spec.ColorArgb));
        var lines = new List<(Drawing Drawing, double Width, double Y)>();
        double y = 0, maxWidth = 1, bottom = 1;
        foreach (string content in spec.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = new FormattedText(content.Length == 0 ? " " : content, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, spec.FontSize, brush, 1);
            var original = new DrawingGroup(); using (var dc = original.Open()) dc.DrawText(text, new Point());
            var runs = Glyphs(original).OrderBy(g => Left(g.GlyphRun)).ToArray();
            var drawing = new DrawingGroup(); double offset = 0;
            using (var dc = drawing.Open())
            {
                for (int index = 0; index < runs.Length; index++)
                {
                    var source = runs[index].GlyphRun; var advances = source.AdvanceWidths.ToArray();
                    var starts = source.ClusterMap is { Count: > 0 } map ? map.Distinct().Order().Select(n => (int)n).ToArray() : Enumerable.Range(0, advances.Length).ToArray();
                    double extra = 0;
                    for (int cluster = 0; cluster < starts.Length; cluster++)
                    {
                        // The last visual cluster has no trailing letter space.
                        bool rtl = (source.BidiLevel & 1) != 0;
                        if (index == runs.Length - 1 && cluster == (rtl ? 0 : starts.Length - 1)) continue;
                        int last = (cluster + 1 < starts.Length ? starts[cluster + 1] : advances.Length) - 1;
                        while (last > starts[cluster] && advances[last] == 0) last--;
                        double next = Math.Max(0, advances[last] + spec.Tracking * spec.FontSize / 1000);
                        extra += next - advances[last]; advances[last] = next;
                    }
                    var origin = source.BaselineOrigin; origin.X += offset + ((source.BidiLevel & 1) != 0 ? extra : 0);
                    var run = new GlyphRun(source.GlyphTypeface, source.BidiLevel, source.IsSideways, source.FontRenderingEmSize,
                        source.PixelsPerDip, source.GlyphIndices, origin, advances, source.GlyphOffsets, source.Characters,
                        source.DeviceFontName, source.ClusterMap, source.CaretStops, source.Language);
                    dc.DrawGlyphRun(runs[index].ForegroundBrush, run); offset += extra;
                }
            }
            double width = Math.Max(1, text.WidthIncludingTrailingWhitespace + offset);
            lines.Add((drawing, width, y)); maxWidth = Math.Max(maxWidth, width); bottom = y + text.Height;
            Raster.ValidateSize((int)Math.Ceiling(maxWidth + 8), (int)Math.Ceiling(bottom + 8));
            y += spec.LineHeight > 0 ? spec.LineHeight : text.Height;
        }
        var result = new DrawingGroup();
        using (var dc = result.Open()) foreach (var line in lines)
        {
            double x = spec.Alignment == TextAlignment.Right ? maxWidth - line.Width : spec.Alignment == TextAlignment.Center ? (maxWidth - line.Width) / 2 : 0;
            dc.PushTransform(new TranslateTransform(x, line.Y)); dc.DrawDrawing(line.Drawing); dc.Pop();
        }
        var bounds = new Rect(0, 0, maxWidth, bottom); if (!result.Bounds.IsEmpty) bounds.Union(result.Bounds);
        int widthPixels = Math.Max(1, (int)Math.Ceiling(bounds.Width + 8)), heightPixels = Math.Max(1, (int)Math.Ceiling(bounds.Height + 8));
        Raster.ValidateSize(widthPixels, heightPixels);
        return Imaging.Draw(widthPixels, heightPixels, dc => { dc.PushTransform(new TranslateTransform(4 - bounds.Left, 4 - bounds.Top)); dc.DrawDrawing(result); dc.Pop(); });
    }

    static double Left(GlyphRun run) => run.BaselineOrigin.X - ((run.BidiLevel & 1) != 0 ? run.AdvanceWidths.Sum() : 0);
    static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        else if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var item in Glyphs(child)) yield return item;
    }
}
