using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

/// <summary>Poster text: letter outlines, paragraph boxes with justification, their exports and project round trip.</summary>
public static class TextPosterTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "text-poster"); Directory.CreateDirectory(root);
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        // A bold capital I: one plain vertical stem, so every edge is a straight line.
        static TextSpec Stem() => new() { Content = "I", FontFamily = "Segoe UI", FontSize = 120, Bold = true, ColorArgb = 0xFFFFFFFF };
        static (byte B, byte G, byte R, byte A) Pixel(Raster raster, int x, int y) { int i = (y * raster.Width + x) * 4; return (raster.Data[i], raster.Data[i + 1], raster.Data[i + 2], raster.Data[i + 3]); }
        static bool White((byte B, byte G, byte R, byte A) p) => p.A > 245 && p.R > 235 && p.G > 235 && p.B > 235;
        static bool Black((byte B, byte G, byte R, byte A) p) => p.A > 245 && p.R < 20 && p.G < 20 && p.B < 20;
        // The opaque run of the stem on the middle row of a plain render.
        static (int Left, int Right, int Row) StemRun(Raster raster)
        {
            int y = raster.Height / 2, left = -1, right = -1;
            for (int x = 0; x < raster.Width; x++) if (raster.Data[(y * raster.Width + x) * 4 + 3] > 200) { if (left < 0) left = x; right = x; }
            Check(left > 0 && right - left > 12, $"The test stem is missing or too thin: {left}..{right}");
            return (left, right, y);
        }
        static double Difference(Raster a, Raster b)
        {
            Check(Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1, $"Size changed: {a.Width}×{a.Height} vs {b.Width}×{b.Height}");
            int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height); double total = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = (y * a.Width + x) * 4, j = (y * b.Width + x) * 4;
                for (int c = 0; c < 4; c++) total += Math.Abs(a.Data[i + c] * (c == 3 ? 1 : a.Data[i + 3] / 255d) - b.Data[j + c] * (c == 3 ? 1 : b.Data[j + 3] / 255d));
            }
            return total / (4d * w * h);
        }
        // An outlined poster title above a coloured backdrop, plus a justified paragraph.
        static Document Poster(TextSpec title)
        {
            var doc = new Document { Width = 360, Height = 220, Dpi = 96, Name = "포스터 글자" };
            doc.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(360, 220, Color.FromRgb(40, 110, 200)) });
            doc.Add(DocumentFeatures.CreateText(title, 20, 10));
            doc.Add(DocumentFeatures.CreateText(new TextSpec { Content = "Small text blocks set in a narrow column wrap between words and reach both edges.", FontFamily = "Segoe UI",
                FontSize = 12, ColorArgb = 0xFF101010, BoxWidth = 150, Alignment = TextAlignment.Justify }, 190, 150));
            doc.Validate(); return doc;
        }
        static TextSpec Title() => new() { Content = "POSTER", FontFamily = "Segoe UI", FontSize = 72, Bold = true, ColorArgb = 0xFFFFFFFF,
            Outline = true, OutlineWidth = 5, OutlineArgb = 0xFF000000 };

        test("text outline: outside paints a ring around the letters and keeps the fill", () =>
        {
            var plain = DocumentFeatures.RenderText(Stem()); var (left, right, row) = StemRun(plain);
            const int width = 6;
            var outlined = DocumentFeatures.RenderText(Stem() with { Outline = true, OutlineWidth = width, OutlineArgb = 0xFF000000 });
            Check(outlined.Width >= plain.Width + 2 * width && outlined.Height >= plain.Height + 2 * width, "The surface did not grow by the outline on every side");
            int y = row + width, a = left + width, b = right + width;
            Check(Enumerable.Range(a + 2, b - a - 3).All(x => White(Pixel(outlined, x, y))), "The fill inside the letter changed");
            Check(Enumerable.Range(a - width + 2, width - 3).All(x => Black(Pixel(outlined, x, y))) && Enumerable.Range(b + 2, width - 3).All(x => Black(Pixel(outlined, x, y))),
                "The outline does not cover the band outside both edges");
            Check(Pixel(outlined, a - width - 3, y).A < 20 && Pixel(outlined, b + width + 3, y).A < 20, "The outline reaches beyond its width");
        });

        test("text outline: center straddles the edges and covers the fill's border", () =>
        {
            var plain = DocumentFeatures.RenderText(Stem()); var (left, right, row) = StemRun(plain);
            const int width = 8, half = width / 2;
            var outlined = DocumentFeatures.RenderText(Stem() with { Outline = true, OutlineWidth = width, OutlineArgb = 0xFF000000, OutlinePosition = TextOutlinePosition.Center });
            int y = row + half, a = left + half, b = right + half;
            Check(Black(Pixel(outlined, a - half + 1, y)) && Black(Pixel(outlined, a + half - 2, y)) && Black(Pixel(outlined, b - half + 2, y)) && Black(Pixel(outlined, b + half - 1, y)),
                "A centred outline must cover half its width inside and half outside each edge");
            Check(White(Pixel(outlined, (a + b) / 2, y)), "The middle of the letter lost its fill");
            Check(Pixel(outlined, a - half - 3, y).A < 20, "A centred outline reaches beyond half its width");
        });

        test("text outline: outline only draws hollow letters, never the fill", () =>
        {
            var plain = DocumentFeatures.RenderText(Stem()); var (left, right, row) = StemRun(plain);
            foreach (var position in new[] { TextOutlinePosition.Outside, TextOutlinePosition.Center })
            {
                var spec = Stem() with { Outline = true, OutlineOnly = true, OutlineWidth = 4, OutlineArgb = 0xFFFF2020, OutlinePosition = position };
                var hollow = DocumentFeatures.RenderText(spec); int reach = (int)spec.OutlineExtent;
                int y = row + reach, middle = (left + right) / 2 + reach;
                Check(hollow.Data[(y * hollow.Width + middle) * 4 + 3] < 10, $"{position}: the hollow letter is filled");
                var edge = Pixel(hollow, (position == TextOutlinePosition.Outside ? left - 2 : left) + reach, y);
                Check(edge.A > 200 && edge.R > 200 && edge.G < 80, $"{position}: the outline is missing at the edge");
                int white = 0; for (int i = 0; i < hollow.Data.Length; i += 4) if (hollow.Data[i] > 200 && hollow.Data[i + 1] > 200 && hollow.Data[i + 3] > 200) white++;
                Check(white == 0, $"{position}: {white} fill-coloured pixels remain");
            }
            var noOutline = DocumentFeatures.RenderText(Stem() with { OutlineOnly = true });
            Check(noOutline.Data.SequenceEqual(plain.Data), "Outline only without an outline must keep the plain filled letters");
            Check(DocumentFeatures.RenderText(Stem() with { OutlineWidth = 9, OutlineArgb = 0xFF00FF00, OutlinePosition = TextOutlinePosition.Center }).Data.SequenceEqual(plain.Data),
                "Outline settings without the outline switched on changed the letters");
        });

        test("text outline: a translucent fill shows no outline colour through it", () =>
        {
            var plain = DocumentFeatures.RenderText(Stem()); var (left, right, row) = StemRun(plain);
            var translucent = DocumentFeatures.RenderText(Stem() with { ColorArgb = 0x80FFFFFF, Outline = true, OutlineWidth = 6, OutlineArgb = 0xFF000000 });
            var inside = Pixel(translucent, (left + right) / 2 + 6, row + 6);
            Check(inside.A is > 110 and < 145 && inside.R > 235, $"The outside outline showed through the translucent fill: {inside}");
        });

        test("text outline: canvas, layer raster and every export path draw the same outline", () =>
        {
            var doc = Poster(Title());
            var output = DesignRenderer.RenderOutput(doc);
            var screen = DesignRenderer.Render(doc, new Rect(0, 0, doc.Width, doc.Height), doc.Width, doc.Height);
            Check(Difference(screen, output) < .01, "The canvas and the export render differ");
            // The layer's own raster backs thumbnails, move previews and perspective rendering.
            Check(Difference(Imaging.Render(doc), output) < .6, $"The text layer raster differs from the retained outline ({Difference(Imaging.Render(doc), output):0.###})");
            string png = Path.Combine(root, "outline.png"); ProjectStore.Export(doc, png);
            Check(Raster.Load(png).Data.SequenceEqual(output.Data), "PNG export changed the outline");
            var scaled = new ExportSettings(Scale: 2).Render(doc); double resampled = Difference(ImportExport.Resize(scaled, doc.Width, doc.Height), output);
            // Resampling the 2× render back to 1× softens every edge a little; the outline pixel is checked below.
            Check(scaled.Width == doc.Width * 2 && resampled < 4, $"A 2× export drew a different outline ({resampled:0.###})");
            string psd = Path.Combine(root, "outline.psd");
            ProjectStore.AtomicWrite(psd, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PsdLayers, s));
            var back = PsdCompatibility.Read(psd, true).Document;
            Check(Difference(DesignRenderer.RenderOutput(back), output) < .6, "The layered .psd lost or moved the outline");
            string pdf = Path.Combine(root, "outline.pdf");
            ProjectStore.AtomicWrite(pdf, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PdfSingle, s, true));
            using (var file = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                var xobjects = file.Pages[0].Resources.Elements.GetDictionary("/XObject");
                int images = xobjects?.Elements.Values.Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).Count(d => d?.Elements.GetName("/Subtype") == "/Image") ?? 0;
                Check(images == 1, $"Only the photo may be an image; the outlined title must stay vector ({images} images)");
            }
            var reopened = Imaging.Render(Task.Run(() => CompatibilityImport.ReadAsync(pdf, new(Dpi: 96, PreservePdfLayers: false))).GetAwaiter().GetResult().Document);
            Check(Difference(reopened, output) < 6, $"The vector PDF looks different ({Difference(reopened, output):0.###})");
            // The outline itself, not only the average: a pixel on the black ring left of the first letter.
            var title = doc.Layers[1]; var ring = Ring(title);
            var expected = PixelAt(output, ring.X, ring.Y); var drawn = PixelAt(reopened, ring.X, ring.Y); var sharp = PixelAt(scaled, ring.X * 2 + 1, ring.Y * 2 + 1);
            Check(expected.R < 40 && drawn.R < 70 && Math.Abs(expected.R - drawn.R) < 50, $"The PDF outline is missing at ({ring.X},{ring.Y}): canvas {expected}, PDF {drawn}");
            Check(sharp.R < 40 && sharp.A > 240, $"The 2× export lost the outline at ({ring.X * 2 + 1},{ring.Y * 2 + 1}): {sharp}");
        });

        test("text outline: hollow title keeps its look in PDF, .psd and CMYK previews", () =>
        {
            var doc = Poster(Title() with { OutlineOnly = true, OutlinePosition = TextOutlinePosition.Center, OutlineWidth = 3 });
            var output = DesignRenderer.RenderOutput(doc);
            string pdf = Path.Combine(root, "hollow.pdf");
            ProjectStore.AtomicWrite(pdf, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PdfLayers, s));
            var reopened = Imaging.Render(Task.Run(() => CompatibilityImport.ReadAsync(pdf, new(Dpi: 96, PreservePdfLayers: false))).GetAwaiter().GetResult().Document);
            Check(Difference(reopened, output) < 6, $"The layered PDF hollow title looks different ({Difference(reopened, output):0.###})");
            string psd = Path.Combine(root, "hollow.psd");
            ProjectStore.AtomicWrite(psd, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PsdSingle, s));
            Check(Difference(DesignRenderer.RenderOutput(PsdCompatibility.Read(psd, false).Document), output) < .6, "The flattened .psd changed the hollow title");
        });

        test("text outline: switching it keeps the letters in place, also rotated and scaled", () =>
        {
            foreach (var (rotation, scale) in new[] { (0d, 1d), (23d, 1.4) })
            {
                var layer = DocumentFeatures.CreateText(Stem(), 30, 40); layer.Rotation = rotation; layer.Scale = scale;
                var glyph = new Point(layer.Pixels.Width / 2.0, layer.Pixels.Height / 2.0); var before = layer.Document(glyph);
                DocumentFeatures.UpdateText(layer, layer.Text! with { Outline = true, OutlineWidth = 10 });
                Check((layer.Document(glyph + new Vector(10, 10)) - before).Length < 1e-6, $"An outside outline moved the letters ({rotation}°)");
                DocumentFeatures.UpdateText(layer, layer.Text! with { OutlinePosition = TextOutlinePosition.Center });
                Check((layer.Document(glyph + new Vector(5, 5)) - before).Length < 1e-6, $"Moving the outline to the centre moved the letters ({rotation}°)");
                DocumentFeatures.UpdateText(layer, layer.Text! with { Outline = false });
                Check((layer.Document(glyph) - before).Length < 1e-6 && layer.Pixels.Width == DocumentFeatures.RenderText(Stem()).Width, $"Removing the outline did not restore the layer ({rotation}°)");
            }
            // A mask moves with the letters when only the outline changes: its edge stays on the same glyph column.
            var masked = DocumentFeatures.CreateText(Stem(), 10, 10); int w = masked.Pixels.Width, h = masked.Pixels.Height, edge = w / 2;
            masked.Mask = new byte[w * h]; for (int y = 0; y < h; y++) for (int x = edge; x < w; x++) masked.Mask[y * w + x] = 255;
            DocumentFeatures.UpdateText(masked, masked.Text! with { Outline = true, OutlineWidth = 10 });
            int row = (h / 2 + 10) * masked.Pixels.Width;
            Check(masked.Mask.Length == masked.Pixels.Width * masked.Pixels.Height && masked.Mask[row + edge + 10 - 2] == 0 && masked.Mask[row + edge + 10 + 1] == 255 && masked.Mask[row + 3] == 0,
                "The mask did not move with the letters when the outline was added");
            DocumentFeatures.UpdateText(masked, masked.Text! with { Content = "II" });
            Check(masked.Mask.Length == masked.Pixels.Width * masked.Pixels.Height, "A content change lost the mask");
        });

        test("text outline and paragraph box: project round trip and older files", () =>
        {
            var spec = Title() with { OutlinePosition = TextOutlinePosition.Center, OutlineOnly = true, OutlineArgb = 0xCC102030, BoxWidth = 260, Alignment = TextAlignment.Justify };
            var doc = Poster(spec); string path = Path.Combine(root, "poster.moruproj");
            ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            Check(loaded.Layers[1].Text == spec && loaded.Layers[2].Text == doc.Layers[2].Text, "The project lost outline or paragraph settings");
            Check(DesignRenderer.RenderOutput(loaded).Data.SequenceEqual(DesignRenderer.RenderOutput(doc).Data), "A reopened project draws differently");
            // A file written before these settings existed opens with plain, unwrapped text.
            string old = Path.Combine(root, "older.moruproj"); File.Copy(path, old, true);
            using (var zip = ZipFile.Open(old, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("document.json")!; JsonObject manifest;
                using (var stream = entry.Open()) manifest = JsonNode.Parse(stream)!.AsObject();
                foreach (var layer in manifest["Layers"]!.AsArray().OfType<JsonObject>())
                    if (layer["Text"] is JsonObject text) foreach (var key in new[] { "Outline", "OutlineWidth", "OutlineArgb", "OutlinePosition", "OutlineOnly", "BoxWidth" }) text.Remove(key);
                entry.Delete();
                using var writer = zip.CreateEntry("document.json").Open(); JsonSerializer.Serialize(writer, manifest);
            }
            var legacy = ProjectStore.Load(old).Layers[1].Text!;
            Check(!legacy.Outline && !legacy.OutlineOnly && legacy.BoxWidth == 0 && legacy.OutlineWidth == 4 && legacy == spec with { Outline = false, OutlineOnly = false, BoxWidth = 0, OutlineWidth = 4, OutlineArgb = 0xFF000000, OutlinePosition = TextOutlinePosition.Outside },
                "An older project did not read as plain text");
            // .comp has no outline or paragraph box: such text keeps its look as pixels, with a warning.
            string package = Path.Combine(root, "poster-" + Guid.NewGuid().ToString("N") + ".comp");
            var warnings = CompositorPackage.Export(doc, package); var imported = CompositorPackage.Import(package).Document;
            Check(warnings.Count(w => w.Contains("글자 외곽선·자동 줄바꿈 폭·양쪽 정렬")) == 2 && imported.Layers[1].Kind == LayerKind.Raster && imported.Layers[1].Pixels.Data.SequenceEqual(doc.Layers[1].Pixels.Data),
                ".comp export did not keep the outlined title as pixels with a warning");
            var json = JsonSerializer.SerializeToNode(spec)!.AsObject();
            Check(!json.ContainsKey("DrawsFill") && !json.ContainsKey("OutlineExtent"), "Derived values were written into the project");
            Check(JsonSerializer.Deserialize<TextSpec>("{\"Content\":\"A\",\"FontSize\":20}") is { Outline: false, BoxWidth: 0, OutlineWidth: 4 }, "Missing values do not default to no outline");
            foreach (var invalid in new[] { spec with { OutlineWidth = 0 }, spec with { OutlineWidth = double.NaN }, spec with { OutlinePosition = (TextOutlinePosition)7 }, spec with { BoxWidth = .5 }, spec with { BoxWidth = -3 }, spec with { BoxWidth = 40_000 } })
            {
                bool rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "An invalid outline or box value was accepted");
            }
        });

        test("paragraph box: Korean wraps between words and CJK between characters", () =>
        {
            const string korean = "모루픽셀은 사진과 도면을 함께 다루는 무료 편집기입니다";
            var face = new Typeface("Malgun Gothic"); double Width(string s) => new FormattedText(s.Length == 0 ? " " : s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 20, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
            var lines = Typography.Wrap(korean, 150, Width);
            Check(lines.Count >= 3 && string.Join(" ", lines) == korean, "Korean lines split inside a word: " + string.Join(" | ", lines));
            Check(lines.All(line => Width(line) <= 150.01), "A Korean line is wider than the box");
            var japanese = Typography.Wrap("日本語の文章は文字の間で折り返します。", 90, Width);
            Check(japanese.Count >= 3 && string.Concat(japanese) == "日本語の文章は文字の間で折り返します。" && japanese.All(l => !l.StartsWith('。')) && japanese.All(line => Width(line) <= 90.01),
                "Japanese did not break between characters: " + string.Join(" | ", japanese));
            var tokens = Typography.Tokens("state-of-the-art (a -5) 漢字。");
            Check(tokens.SequenceEqual(["state-", "of-", "the-", "art ", "(a ", "-5) ", "漢", "字。"]), "Break opportunities: " + string.Join(" | ", tokens));
            var word = Typography.Wrap("Supercalifragilistic", 60, Width);
            Check(word.Count >= 3 && string.Concat(word) == "Supercalifragilistic" && word.All(line => line.Length == 1 || Width(line) <= 60.01), "A word wider than the box did not break inside");
            Check(Typography.Wrap("", 50, Width).SequenceEqual([""]) && Typography.Wrap("a   ", 50, Width).SequenceEqual(["a"]), "Empty and space-only paragraphs");
            var indented = Typography.Wrap("      wide words", 26, Width);
            Check(indented.Count >= 3 && indented.All(line => line.Length > 0) && string.Concat(indented).Replace(" ", "") == "widewords", "Leading spaces that do not fit made a blank line: " + string.Join(" | ", indented));
        });

        test("paragraph box: lines fit the box, justify reaches both edges and keeps the last line", () =>
        {
            const string copy = "Small text blocks in a narrow column wrap between words.\nA second paragraph starts here and wraps too.";
            const double box = 180, step = 22;
            var basic = new TextSpec { Content = copy, FontFamily = "Segoe UI", FontSize = 14, LineHeight = step, ColorArgb = 0xFF000000, BoxWidth = box };
            var face = new Typeface("Segoe UI"); double Width(string s) => new FormattedText(s.Length == 0 ? " " : s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 14, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
            var paragraphs = copy.Split('\n').Select(p => Typography.Wrap(p, box, Width)).ToArray();
            int count = paragraphs.Sum(p => p.Count); var ends = new HashSet<int> { paragraphs[0].Count - 1, count - 1 };
            var unwrapped = DocumentFeatures.RenderText(basic with { BoxWidth = 0 });
            var left = DocumentFeatures.RenderText(basic); var justified = DocumentFeatures.RenderText(basic with { Alignment = TextAlignment.Justify });
            // The surface is the box plus the 4 px margins, and a little more where a glyph's ink overhangs it.
            Check(count >= 5 && left.Width is >= 188 and <= 190 && justified.Width is >= 188 and <= 190 && left.Height >= 8 + step * (count - 1) && unwrapped.Height < left.Height / 2,
                $"The box did not set the width or wrap the lines: {left.Width}×{left.Height}, justified {justified.Width}, {count} lines");
            // The ink of line i lies in its own row band (fixed line height).
            (int Left, int Right) Ink(Raster r, int line)
            {
                int right = -1, leftEdge = r.Width;
                for (int y = 4 + (int)(step * line) + 1; y < 4 + (int)(step * (line + 1)) - 1 && y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++) if (r.Data[(y * r.Width + x) * 4 + 3] > 60) { right = Math.Max(right, x); leftEdge = Math.Min(leftEdge, x); }
                return (leftEdge, right);
            }
            var ragged = Enumerable.Range(0, count).Select(i => Ink(left, i)).ToArray(); var even = Enumerable.Range(0, count).Select(i => Ink(justified, i)).ToArray();
            int edge = even.Where((_, i) => !ends.Contains(i)).Max(l => l.Right);
            Check(Math.Abs(edge - (4 + box)) <= 2, $"Justified lines end at {edge}, not at the box edge {4 + box}");
            for (int i = 0; i < count; i++)
            {
                Check(ragged[i].Right >= 0 && ragged[i].Right <= edge + 1 && even[i].Left == ragged[i].Left, $"Line {i} is missing, wider than the box or moved: {ragged[i]} / {even[i]}");
                if (ends.Contains(i)) Check(even[i].Right == ragged[i].Right, $"Line {i}: the last line of a paragraph was justified");
                else Check(even[i].Right >= edge - 2 && even[i].Right > ragged[i].Right, $"Line {i}: a justified line ends at {even[i].Right}, not at the box edge {edge}");
            }
            var right = DocumentFeatures.RenderText(basic with { Alignment = TextAlignment.Right });
            Check(Enumerable.Range(0, count).All(i => Ink(right, i).Right >= edge - 2), "Right alignment does not use the box edge");
            // Tracking and Korean in a box: every line still fits, and justified lines reach the edge.
            var korean = basic with { Content = "모루픽셀은 사진과 도면을 함께 다루는 무료 편집기입니다 모루픽셀은 사진과 도면을 함께", FontFamily = "Malgun Gothic", Tracking = 120, Alignment = TextAlignment.Justify };
            var tracked = DocumentFeatures.RenderText(korean); int trackedCount = (int)Math.Round((tracked.Height - 8 - (DocumentFeatures.RenderText(korean with { Content = "모" }).Height - 8)) / step) + 1;
            var trackedLines = Enumerable.Range(0, trackedCount).Select(i => Ink(tracked, i)).ToArray();
            Check(trackedCount >= 3 && trackedLines.All(l => l.Right >= 0 && l.Right <= 4 + box + 2) && trackedLines.SkipLast(1).All(l => l.Right >= 4 + box - 4),
                "Tracked justified Korean lines do not fit the box: " + string.Join(", ", trackedLines));
            // An outline around a box keeps the box and grows the surface by the outline.
            var boxed = DocumentFeatures.RenderText(basic with { Outline = true, OutlineWidth = 3 });
            Check(boxed.Width == left.Width + 6 && boxed.Height == left.Height + 6, "An outline changed the paragraph layout");
        });
    }

    // A point on the outside ring left of the first letter of a title layer, in document pixels.
    static Point Ring(Layer title)
    {
        var plain = DocumentFeatures.RenderText(title.Text! with { Outline = false });
        int row = plain.Height / 2, left = -1;
        for (int x = 0; x < plain.Width && left < 0; x++) if (plain.Data[(row * plain.Width + x) * 4 + 3] > 200) left = x;
        double reach = title.Text!.OutlineExtent;
        var local = new Point(left + reach - Math.Max(1, reach / 2), row + reach);
        var point = title.Document(local); return new Point(Math.Floor(point.X), Math.Floor(point.Y));
    }
    static (byte B, byte G, byte R, byte A) PixelAt(Raster raster, double x, double y) { int i = ((int)y * raster.Width + (int)x) * 4; return (raster.Data[i], raster.Data[i + 1], raster.Data[i + 2], raster.Data[i + 3]); }
}
