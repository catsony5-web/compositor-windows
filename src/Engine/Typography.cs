using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

internal static class Typography
{
    // Adjust the advances of WPF's already shaped glyph clusters, retaining font
    // fallback, ligatures, combining marks, emoji sequences, and bidi ordering.
    public static Raster RenderTracked(TextSpec spec)
    {
        var layout = LayoutTracked(spec); return Imaging.Draw(layout.Width, layout.Height, dc => dc.DrawDrawing(layout.Drawing));
    }
    internal static (DrawingGroup Drawing, int Width, int Height) LayoutTracked(TextSpec spec)
    {
        var face = Face(spec);
        var brush = new SolidColorBrush(DocumentFeatures.Color(spec.ColorArgb));
        var lines = new List<(Drawing Drawing, double Width, double Y)>();
        double y = 0, maxWidth = 1, bottom = 1;
        foreach (string content in Paragraphs(spec.Content))
        {
            var line = Shape(content, spec, face, brush, 0);
            lines.Add((line.Drawing, line.Width, y)); maxWidth = Math.Max(maxWidth, line.Width); bottom = y + line.Height;
            Raster.ValidateSize((int)Math.Ceiling(maxWidth + 8), (int)Math.Ceiling(bottom + 8));
            y += spec.LineHeight > 0 ? spec.LineHeight : line.Height;
        }
        var result = new DrawingGroup();
        using (var dc = result.Open()) foreach (var line in lines)
        {
            double x = spec.Alignment == TextAlignment.Right ? maxWidth - line.Width : spec.Alignment == TextAlignment.Center ? (maxWidth - line.Width) / 2 : 0;
            dc.PushTransform(new TranslateTransform(x, line.Y)); dc.DrawDrawing(line.Drawing); dc.Pop();
        }
        return Place(result, new Rect(0, 0, maxWidth, bottom));
    }

    // Paragraph box (자동 줄바꿈 폭): lines wrap between words to fit spec.BoxWidth. Korean keeps
    // whole words, as it wraps between words; CJK ideographs and kana may break between characters;
    // a word wider than the box breaks inside. Every line is shaped on its own, so tracking and
    // justification change cluster advances exactly as in LayoutTracked. Justified lines reach the
    // box edge; the last line of each paragraph keeps the start alignment.
    internal static (DrawingGroup Drawing, int Width, int Height) LayoutBox(TextSpec spec)
    {
        var face = Face(spec);
        var brush = new SolidColorBrush(DocumentFeatures.Color(spec.ColorArgb));
        double box = spec.BoxWidth;
        double Measure(string text) => spec.Tracking == 0
            ? new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, spec.FontSize, brush, 1).WidthIncludingTrailingWhitespace
            : Shape(text, spec, face, brush, 0).Width;
        var lines = new List<(Drawing Drawing, double X, double Y)>();
        double y = 0, bottom = 1;
        foreach (string paragraph in Paragraphs(spec.Content))
        {
            var broken = Wrap(paragraph, box, Measure);
            for (int index = 0; index < broken.Count; index++)
            {
                bool justify = spec.Alignment == TextAlignment.Justify && index < broken.Count - 1;
                var line = Shape(broken[index], spec, face, brush, justify ? box : 0);
                double x = spec.Alignment == TextAlignment.Right ? box - line.Width : spec.Alignment == TextAlignment.Center ? (box - line.Width) / 2 : 0;
                lines.Add((line.Drawing, x, y)); bottom = y + line.Height;
                Raster.ValidateSize((int)Math.Ceiling(box + 8), (int)Math.Ceiling(bottom + 8));
                y += spec.LineHeight > 0 ? spec.LineHeight : line.Height;
            }
        }
        var result = new DrawingGroup();
        using (var dc = result.Open()) foreach (var line in lines) { dc.PushTransform(new TranslateTransform(line.X, line.Y)); dc.DrawDrawing(line.Drawing); dc.Pop(); }
        return Place(result, new Rect(0, 0, box, bottom));
    }

    static Typeface Face(TextSpec spec) => new(new FontFamily(spec.FontFamily), spec.Italic ? FontStyles.Italic : FontStyles.Normal,
        spec.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    static string[] Paragraphs(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // The text box united with the ink, inside a 4 px margin on every side.
    static (DrawingGroup Drawing, int Width, int Height) Place(DrawingGroup content, Rect box)
    {
        var bounds = box; if (!content.Bounds.IsEmpty) bounds.Union(content.Bounds);
        int widthPixels = Math.Max(1, (int)Math.Ceiling(bounds.Width + 8)), heightPixels = Math.Max(1, (int)Math.Ceiling(bounds.Height + 8));
        Raster.ValidateSize(widthPixels, heightPixels);
        var placed = new DrawingGroup { Transform = new TranslateTransform(4 - bounds.Left, 4 - bounds.Top) }; placed.Children.Add(content); placed.Freeze();
        return (placed, widthPixels, heightPixels);
    }

    // One shaped line. Tracking is added after every glyph cluster but the last visual one. When
    // `justifyTo` is wider than the line, the difference goes to its word spaces, or between the
    // clusters of a CJK line without spaces; a single Latin word is left as it is.
    internal static (Drawing Drawing, double Width, double Height) Shape(string content, TextSpec spec, Typeface face, Brush brush, double justifyTo)
    {
        var text = new FormattedText(content.Length == 0 ? " " : content, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, face, spec.FontSize, brush, 1);
        var original = new DrawingGroup(); using (var dc = original.Open()) dc.DrawText(text, new Point());
        double natural = text.WidthIncludingTrailingWhitespace;
        if (spec.Tracking == 0 && justifyTo <= natural + .01) return (original, Math.Max(1, natural), text.Height);
        var runs = Glyphs(original).OrderBy(g => Left(g.GlyphRun)).ToArray();
        var advances = new double[runs.Length][]; var extras = new double[runs.Length];
        // Every cluster as (run, cluster start glyph, glyph that carries its advance), in run order.
        var clusters = new List<(int Run, int Start, int Glyph, bool LastVisual)>();
        for (int index = 0; index < runs.Length; index++)
        {
            var source = runs[index].GlyphRun; var widths = advances[index] = source.AdvanceWidths.ToArray();
            var starts = source.ClusterMap is { Count: > 0 } map ? map.Distinct().Order().Select(n => (int)n).ToArray() : Enumerable.Range(0, widths.Length).ToArray();
            bool rtl = (source.BidiLevel & 1) != 0;
            for (int cluster = 0; cluster < starts.Length; cluster++)
            {
                int last = (cluster + 1 < starts.Length ? starts[cluster + 1] : widths.Length) - 1;
                while (last > starts[cluster] && widths[last] == 0) last--;
                // The last visual cluster has no trailing letter space.
                clusters.Add((index, starts[cluster], last, index == runs.Length - 1 && cluster == (rtl ? 0 : starts.Length - 1)));
            }
        }
        if (spec.Tracking != 0)
            foreach (var (run, _, glyph, lastVisual) in clusters)
            {
                if (lastVisual) continue;
                double next = Math.Max(0, advances[run][glyph] + spec.Tracking * spec.FontSize / 1000);
                extras[run] += next - advances[run][glyph]; advances[run][glyph] = next;
            }
        double Width() { double offset = 0; foreach (double extra in extras) offset += extra; return natural + offset; }
        double width = Width();
        if (justifyTo > width + .01)
        {
            var spaces = runs.Select(r => SpaceClusters(r.GlyphRun)).ToArray();
            var gaps = clusters.Where(c => !c.LastVisual && spaces[c.Run].Contains(c.Start)).ToArray();
            if (gaps.Length == 0 && HasCjk(content)) gaps = clusters.Where(c => !c.LastVisual).ToArray();
            if (gaps.Length > 0)
            {
                double share = (justifyTo - width) / gaps.Length;
                foreach (var (run, _, glyph, _) in gaps) { advances[run][glyph] += share; extras[run] += share; }
                width = Width();
            }
        }
        var drawing = new DrawingGroup(); double shift = 0;
        using (var dc = drawing.Open())
        {
            for (int index = 0; index < runs.Length; index++)
            {
                var source = runs[index].GlyphRun;
                var origin = source.BaselineOrigin; origin.X += shift + ((source.BidiLevel & 1) != 0 ? extras[index] : 0);
                var run = new GlyphRun(source.GlyphTypeface, source.BidiLevel, source.IsSideways, source.FontRenderingEmSize,
                    source.PixelsPerDip, source.GlyphIndices, origin, advances[index], source.GlyphOffsets, source.Characters,
                    source.DeviceFontName, source.ClusterMap, source.CaretStops, source.Language);
                dc.DrawGlyphRun(runs[index].ForegroundBrush, run); shift += extras[index];
            }
        }
        return (drawing, Math.Max(1, width), text.Height);
    }

    // Cluster start glyphs whose characters are all word spaces.
    static HashSet<int> SpaceClusters(GlyphRun run)
    {
        var all = new Dictionary<int, bool>();
        if (run.Characters is { Count: > 0 } characters)
            for (int i = 0; i < characters.Count; i++)
            {
                int start = run.ClusterMap is { Count: > 0 } map ? map[i] : i;
                bool space = IsBreakSpace(characters[i]);
                all[start] = all.TryGetValue(start, out bool before) ? before && space : space;
            }
        return all.Where(p => p.Value).Select(p => p.Key).ToHashSet();
    }

    // Line-breaking spaces; no-break spaces keep words together.
    static bool IsBreakSpace(char c) => char.IsWhiteSpace(c) && c is not (' ' or ' ' or ' ');
    static int CodeAt(string text, int index) => char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text, index) : text[index];
    static bool IsHangul(int code) => code is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;
    // Ideographs and kana: lines of them may break between characters.
    static bool IsIdeographic(int code) => code is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0xFF66 and <= 0xFF9F or >= 0x20000 and <= 0x3FFFF;
    static bool HasCjk(string text)
    {
        for (int i = 0; i < text.Length; i++) { int code = CodeAt(text, i); if (IsIdeographic(code) || IsHangul(code)) return true; }
        return false;
    }
    // Kinsoku: no line starts with closing punctuation or small kana, and none ends with an opening bracket.
    const string Closing = "、。，．・：；？！ー）」』】〕〉》〙〛｝］〟ゝゞヽヾ々〻ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ’”,.!?;:)]}…‥%％";
    const string Opening = "（「『【〔〈《〘〚｛［〝‘“([{";

    // Pieces of a paragraph that end at a break opportunity: after spaces, after a hyphen or dash
    // inside a word, and on both sides of an ideograph or kana. Hangul words stay whole.
    internal static List<string> Tokens(string paragraph)
    {
        var tokens = new List<string>(); var current = new StringBuilder(); string? previous = null;
        var elements = StringInfo.GetTextElementEnumerator(paragraph);
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            if (previous != null && Breaks(previous, element, current.Length > previous.Length)) { tokens.Add(current.ToString()); current.Clear(); }
            current.Append(element); previous = element;
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;

        static bool Breaks(string before, string after, bool inWord)
        {
            if (after.Length == 1 && IsBreakSpace(after[0])) return false;
            if (before.Length == 1 && IsBreakSpace(before[0])) return true;
            if (Closing.Contains(after[0]) || Opening.Contains(before[^1])) return false;
            if (before is "-" or "‐" or "–" or "—") return inWord;
            return IsIdeographic(CodeAt(before, 0)) || IsIdeographic(CodeAt(after, 0));
        }
    }

    // Greedy filling: each line takes as many pieces as fit `width`; a piece wider than the
    // whole line is split between characters (at least one per line). Spaces at a line end are dropped.
    internal static List<string> Wrap(string paragraph, double width, Func<string, double> measure)
    {
        static string Trimmed(string line) { int end = line.Length; while (end > 0 && IsBreakSpace(line[end - 1])) end--; return line[..end]; }
        bool Fits(string line) => measure(Trimmed(line)) <= width + .01;
        var lines = new List<string>(); string current = "";
        foreach (var token in Tokens(paragraph))
        {
            if (current.Length == 0 || Fits(current + token)) current += token;
            else if (Trimmed(current).Length == 0) current = token; // leading spaces that leave no room are dropped, not a blank line
            else { lines.Add(Trimmed(current)); current = token; }
            while (!Fits(current))
            {
                var starts = StringInfo.ParseCombiningCharacters(Trimmed(current));
                if (starts.Length < 2) break;
                int low = 1, high = starts.Length - 1;
                while (low < high) { int middle = (low + high + 1) / 2; if (Fits(current[..starts[middle]])) low = middle; else high = middle - 1; }
                lines.Add(current[..starts[low]]); current = current[starts[low]..];
            }
        }
        lines.Add(Trimmed(current));
        return lines;
    }

    static double Left(GlyphRun run) => run.BaselineOrigin.X - ((run.BidiLevel & 1) != 0 ? run.AdvanceWidths.Sum() : 0);
    static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        else if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var item in Glyphs(child)) yield return item;
    }
}
