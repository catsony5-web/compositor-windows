using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class ThemeFontTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Windows UI font resolves Korean syllables and horizontal vowel", () =>
        {
            var face = new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            const string sample = "ㅡ으그글브러시한M";
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawText(new FormattedText(sample, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, Theme.BodySize, Brushes.White, 1), new Point());
            IEnumerable<GlyphRun> Runs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph.GlyphRun] : drawing is DrawingGroup group ? group.Children.SelectMany(Runs) : [];
            var runs = Runs(visual.Drawing).ToArray();
            // Composite families intentionally resolve Korean and Latin to different faces.
            if (runs.Length == 0 || runs.Any(r => r.GlyphIndices.Contains((ushort)0)) || sample.Any(c => !runs.Any(r => r.GlyphTypeface.CharacterToGlyphMap.ContainsKey(c))))
                throw new Exception("Korean UI fallback glyphs are missing");
        });
        test("horizontal Korean vowel remains visible at 100 125 and 150 percent scaling", () =>
        {
            foreach (double scale in new[] { 1d, 1.25, 1.5 }) foreach (double size in new[] { 12d, 13d, 14d })
            {
                var text = Theme.Label("ㅡ", size, Brushes.White); text.UseLayoutRounding = true;
                text.Measure(new Size(60, 40)); text.Arrange(new Rect(0, 0, 60, 40)); text.UpdateLayout();
                int w = (int)(60 * scale), h = (int)(40 * scale);
                var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(text);
                var bytes = new byte[w * h * 4]; bitmap.CopyPixels(bytes, w * 4, 0);
                int longest = 0;
                for (int y = 0; y < h; y++) { int run = 0; for (int x = 0; x < w; x++) { run = bytes[(y * w + x) * 4 + 3] > 50 ? run + 1 : 0; longest = Math.Max(longest, run); } }
                if (longest < size * scale * .4) throw new Exception($"Horizontal stroke disappeared at {size}px / {scale:P0}: {longest}px");
            }
        });
    }
}
