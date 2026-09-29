using System.IO;
using System.Text;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PsdSharp;

namespace Compositor.Windows;

/// <summary>Layered .psd, layered PDF / .ai and single-page PDF exports, read back independently.</summary>
public static class LayeredExportTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "layered-export"); Directory.CreateDirectory(root);
        string PathFor(string name) => Path.Combine(root, name);
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static CompatibilityResult Read(string path, CompatibilityOptions options) => Task.Run(() => CompatibilityImport.ReadAsync(path, options)).GetAwaiter().GetResult();
        // Mean premultiplied channel difference. PDF page sizes may round up by one pixel on import.
        static double Difference(Raster a, Raster b, bool exactSize = true)
        {
            Check(!exactSize || a.Width == b.Width && a.Height == b.Height, $"Size changed: {a.Width}×{a.Height} vs {b.Width}×{b.Height}");
            Check(Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1, "Page size changed");
            int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height); double total = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = (y * a.Width + x) * 4, j = (y * b.Width + x) * 4;
                for (int c = 0; c < 4; c++) total += Math.Abs(a.Data[i + c] * (c == 3 ? 1 : a.Data[i + 3] / 255d) - b.Data[j + c] * (c == 3 ? 1 : b.Data[j + 3] / 255d));
            }
            return total / (4d * w * h);
        }
        // The independent parser reads the short legacy name; the Unicode name is in the "luni" block.
        static string Unicode(PsdSharp.Layer layer)
        {
            var block = layer.TaggedBlocks?.Blocks.FirstOrDefault(b => b.Key.Key == "luni");
            if (block?.RawData is not { Length: >= 4 } raw) return layer.Name;
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(raw);
            return Encoding.BigEndianUnicode.GetString(raw, 4, Math.Min(length * 2, raw.Length - 4));
        }
        static Raster Solid(int w, int h, Color color) => Raster.Solid(w, h, color);

        // Photo background, a group with a multiply layer, a masked half-transparent layer and a
        // hidden nested group, then text and a hidden shape at the top.
        Document Scene()
        {
            var doc = new Document { Width = 80, Height = 60, Dpi = 150, Name = "레이어 보존 문서" };
            doc.Add(new Layer { Name = "배경 사진", Pixels = Solid(80, 60, Color.FromRgb(240, 236, 220)) });
            var building = DocumentFeatures.CreateGroup(doc, "건물 그룹"); building.Opacity = .8; doc.Add(building);
            doc.Add(new Layer { Name = "벽체", Pixels = Solid(20, 10, Colors.IndianRed), X = 5, Y = 5, Blend = BlendMode.Multiply, ParentId = building.Id });
            var mask = new byte[10 * 10]; for (int y = 0; y < 10; y++) for (int x = 0; x < 5; x++) mask[y * 10 + x] = 255;
            doc.Add(new Layer { Name = "창문 가나다", Pixels = Solid(10, 10, Colors.RoyalBlue), X = 30, Y = 20, Opacity = .5, Mask = mask, ParentId = building.Id });
            var detail = DocumentFeatures.CreateGroup(doc, "세부 (숨김)"); detail.Visible = false; detail.ParentId = building.Id; doc.Add(detail);
            doc.Add(new Layer { Name = "문", Pixels = Solid(6, 6, Colors.SeaGreen), X = 50, Y = 40, ParentId = detail.Id });
            doc.Add(new Layer { Name = "빈 레이어", Pixels = new Raster(5, 5), X = 3, Y = 3 });
            var title = DocumentFeatures.CreateText(new TextSpec { Content = "평면도", FontFamily = "Malgun Gothic", FontSize = 16, ColorArgb = 0xFF202020 }, 40, 8);
            title.Name = "제목 텍스트"; doc.Add(title);
            var shape = VectorShapes.Create(new ShapeSpec { Width = 12, Height = 8, FillArgb = 0xFF3060C0 }); shape.Name = "숨긴 도형"; shape.X = 60; shape.Y = 44; shape.Visible = false; doc.Add(shape);
            doc.Validate(); return doc;
        }
        string[] Names(Document doc) => doc.Layers.Select(l => l.Name).ToArray();

        test("layered .psd keeps names, order, groups, opacity, blend, visibility and masks", () =>
        {
            var doc = Scene(); string path = PathFor("scene.psd");
            ProjectStore.AtomicWrite(path, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PsdLayers, s));
            var back = PsdCompatibility.Read(path, true).Document;
            string[] expected = ["배경 사진", "벽체", "창문 가나다", "문", "세부 (숨김)", "건물 그룹", "빈 레이어", "제목 텍스트", "숨긴 도형"];
            Check(Names(back).SequenceEqual(expected), "Layer order or names changed: " + string.Join(", ", Names(back)));
            Layer Named(string name) => back.Layers.Single(l => l.Name == name);
            var building = Named("건물 그룹"); var detail = Named("세부 (숨김)");
            Check(building.Kind == LayerKind.Group && building.ParentId == null && Math.Abs(building.Opacity - .8) < .01, "The group folder lost its opacity");
            Check(detail.Kind == LayerKind.Group && detail.ParentId == building.Id && !detail.Visible, "The nested hidden group changed");
            Check(Named("벽체").ParentId == building.Id && Named("창문 가나다").ParentId == building.Id && Named("문").ParentId == detail.Id, "Children left their groups");
            Check(Named("벽체").Blend == BlendMode.Multiply, "Blend mode lost");
            var window = Named("창문 가나다");
            Check(Math.Abs(window.Opacity - .5) < .01 && window.Mask != null && window.Pixels.Width == 10 && window.X == 30 && window.Y == 20, "Masked layer bounds or opacity changed");
            Check(window.Mask![0] == 255 && window.Mask[9] == 0 && window.Mask[5 * 10 + 4] == 255 && window.Mask[5 * 10 + 5] == 0, "Layer mask values changed");
            Check(Named("벽체").Pixels.Width == 20 && Named("벽체").Pixels.Height == 10 && Named("벽체").X == 5, "Layers must be cropped to their content, not the canvas");
            Check(Named("빈 레이어").Pixels.Width == 1 && !Named("숨긴 도형").Visible && Named("숨긴 도형").Pixels.Width == 12, "Empty or hidden layers were dropped");
            Check(Named("제목 텍스트").Kind == LayerKind.Raster, "Text should arrive as a pixel layer");
            double difference = Difference(DesignRenderer.RenderOutput(doc), DesignRenderer.RenderOutput(back));
            Check(difference < .6, $"Composite differs after the round trip ({difference:0.###})");
            ProjectStore.Save(back, PathFor("scene-back.moruproj"));
            Check(Names(ProjectStore.Load(PathFor("scene-back.moruproj"))).SequenceEqual(expected), "Imported groups must save as a project");
        });

        test("layered .psd is read by an independent parser with the same tree", () =>
        {
            var doc = Scene(); string path = PathFor("scene-independent.psd");
            ProjectStore.AtomicWrite(path, s => PsdCompatibility.Write(doc, s, true));
            using var input = File.OpenRead(path); var psd = PsdFile.Open(input);
            Check(psd.Header.WidthInPixels == 80 && psd.Header.HeightInPixels == 60 && psd.Header.NumberOfChannels == 4, "Header changed");
            var roots = psd.RootLayers.ToArray();
            string Name(PsdSharp.Layer l) => Unicode(l);
            var rootNames = roots.Select(Name).ToArray();
            Check(rootNames.Contains("건물 그룹") && rootNames.Contains("배경 사진") && rootNames.Contains("제목 텍스트"), "Root layers: " + string.Join(", ", rootNames));
            var group = roots.Single(l => Name(l) == "건물 그룹");
            var inside = group.Children.Select(Name).ToArray();
            Check(inside.Length == 3 && inside.Contains("벽체") && inside.Contains("창문 가나다") && inside.Contains("세부 (숨김)"), "Group children: " + string.Join(", ", inside));
            var nested = group.Children.Single(l => Name(l) == "세부 (숨김)"); var wall = group.Children.Single(l => Name(l) == "벽체");
            // PsdSharp 1.2 reports the visibility bit itself (set = hidden in the format and in
            // other readers), so compare hidden layers with a visible one instead of a fixed value.
            Check(nested.IsVisible != wall.IsVisible && Unicode(nested.Children.Single()) == "문", $"Nested hidden group changed: children={string.Join(", ", nested.Children.Select(Unicode))}");
            Check(group.Opacity == Imaging.Byte(.8 * 255) && group.BlendMode.Key is "pass" or "norm", "Group opacity or mode changed");
            Check(wall.BlendMode.Key == "mul " && wall.Bounds.Width == 20 && wall.Bounds.Height == 10 && wall.Bounds.TopLeft.X == 5, "Blend or bounds changed");
            var masked = group.Children.Single(l => Name(l) == "창문 가나다");
            // The mask rectangle covers the visible left half (PsdSharp counts the right edge inclusively).
            var maskData = masked.LayerMaskData;
            Check(masked.Opacity == Imaging.Byte(.5 * 255) && maskData != null && maskData.Bounds.TopLeft.X == 30 && maskData.Bounds.TopLeft.Y == 20 && maskData.Bounds.Width is 5 or 6, "Mask data missing");
            Check(roots.Single(l => Name(l) == "숨긴 도형").IsVisible != roots.Single(l => Name(l) == "배경 사진").IsVisible, "Hidden layer became visible");
            Check(psd.ImageData != null && psd.ImageData.Width == 80, "Composite image missing");
        });

        test("layered .psd stores clipping, adjustments as results and keeps the look", () =>
        {
            var doc = new Document { Width = 40, Height = 30, Name = "클리핑" };
            var photo = new Raster(40, 30); for (int y = 0; y < 30; y++) for (int x = 0; x < 40; x++) { int i = (y * 40 + x) * 4; photo.Data[i] = (byte)(x * 6); photo.Data[i + 1] = (byte)(y * 8); photo.Data[i + 2] = 200; photo.Data[i + 3] = 255; }
            doc.Add(new Layer { Name = "사진", Pixels = photo });
            doc.Add(new Layer { Name = "기준", Pixels = Solid(16, 12, Colors.White), X = 4, Y = 4 });
            doc.Add(new Layer { Name = "클리핑 색", Pixels = Solid(40, 30, Color.FromArgb(160, 255, 80, 0)), Clipped = true });
            var invert = DocumentFeatures.CreateAdjustment(doc, new AdjustmentSpec { Kind = AdjustmentKind.Levels, Black = 20, White = 200, Gamma = 1.4 }, "레벨 보정"); invert.Opacity = .6; doc.Add(invert);
            string path = PathFor("clip-adjust.psd"); ProjectStore.AtomicWrite(path, s => PsdCompatibility.Write(doc, s, true));
            var back = PsdCompatibility.Read(path, true).Document;
            Check(Names(back).SequenceEqual(["사진", "기준", "클리핑 색", "레벨 보정"]), "Order changed: " + string.Join(", ", Names(back)));
            var clip = back.Layers.Single(l => l.Name == "클리핑 색");
            Check(clip.Clipped && clip.Pixels.Width == 40, "Clipping lost");
            var adjusted = back.Layers.Single(l => l.Name == "레벨 보정");
            Check(adjusted.Kind == LayerKind.Raster && Math.Abs(adjusted.Opacity - .6) < .01 && adjusted.Mask == null, "Adjustment result lost its opacity");
            double difference = Difference(DesignRenderer.RenderOutput(doc), DesignRenderer.RenderOutput(back));
            Check(difference < .6, $"Adjusted composite differs ({difference:0.###})");
            using var input = File.OpenRead(path); var psd = PsdFile.Open(input);
            Check(psd.RootLayers.Single(l => Unicode(l) == "클리핑 색").Clipping, "Independent parser did not see the clipping");
        });

        test("layered .psd merges deep groups past 128 layers and explains it", () =>
        {
            var doc = new Document { Width = 64, Height = 64, Name = "많은 객체" };
            var drawing = DocumentFeatures.CreateGroup(doc, "도면"); doc.Add(drawing);
            var walls = DocumentFeatures.CreateGroup(doc, "벽"); walls.ParentId = drawing.Id; doc.Add(walls);
            for (int i = 0; i < 140; i++) { var line = VectorShapes.Create(new ShapeSpec { Width = 4, Height = 2, FillArgb = 0xFF000000 }); line.Name = $"선 {i}"; line.X = i % 60; line.Y = i / 3; line.ParentId = walls.Id; doc.Add(line); }
            doc.Add(new Layer { Name = "위 사진", Pixels = Solid(8, 8, Colors.Orange) });
            var plan = PsdLayerExport.Build(doc);
            Check(plan.Layers == 2 && plan.Folders == 1 && plan.Collapsed == 1, $"Unexpected merge: {plan.Layers} layers, {plan.Folders} folders, {plan.Collapsed} merged");
            var report = CompatibilityExport.Describe(doc, CompatibilityExportFormat.PsdLayers);
            Check(report.Problem == null && report.Lines.Any(l => l.Contains("각각 한 장의 레이어로 합칩니다")), "The merge was not explained");
            string path = PathFor("merged-groups.psd"); ProjectStore.AtomicWrite(path, s => PsdCompatibility.Write(doc, s, true));
            var back = PsdCompatibility.Read(path, true).Document;
            Check(Names(back).SequenceEqual(["벽", "도면", "위 사진"]) && back.Layers[0].Kind == LayerKind.Raster && back.Layers[0].ParentId == back.Layers[1].Id, "Merged group structure changed");
            Check(Difference(DesignRenderer.RenderOutput(doc), DesignRenderer.RenderOutput(back)) < .6, "Merged drawing changed its look");
            var flat = new Document { Width = 8, Height = 8 };
            for (int i = 0; i < Document.MaxLayers + 1; i++) flat.Add(VectorShapes.Create(new ShapeSpec { Width = 2, Height = 2 }));
            var refused = CompatibilityExport.Describe(flat, CompatibilityExportFormat.PsdLayers);
            Check(refused.Problem != null && refused.Problem.Contains("128") && refused.Problem.Contains(".psd · 한 장으로 합치기"), "Too many flat layers must name the limit and the alternative");
            Check(CompatibilityExport.Describe(flat, CompatibilityExportFormat.PsdSingle).Problem == null, "A single-image .psd must stay available");
        });

        test("PSD reader follows the stored bottom-first layer order of external files", () =>
        {
            string path = PathFor("2layers.psd");
            using (var source = typeof(LayeredExportTests).Assembly.GetManifestResourceStream("Morupixel.Compatibility.2layers.psd")!) using (var output = File.Create(path)) source.CopyTo(output);
            var layered = PsdCompatibility.Read(path, true).Document; var merged = PsdCompatibility.Read(path, false).Document;
            // The first record is the background, so it must be the lowest layer here too.
            Check(layered.Layers[0].Pixels.Width == layered.Width && layered.Layers[0].Pixels.Height == layered.Height, "The background record is no longer at the bottom");
            double difference = Difference(Imaging.Render(layered), Imaging.Render(merged));
            Check(difference < 3, $"Layered import differs from the file's own composite ({difference:0.###})");
        });

        test("PackBits rows decode back to the original bytes", () =>
        {
            var random = new Random(7);
            foreach (int length in new[] { 1, 2, 3, 127, 128, 129, 300, 1000 })
            {
                var row = new byte[length]; for (int i = 0; i < length; i++) row[i] = (byte)(random.Next(4) == 0 ? random.Next(256) : i / 37);
                var packed = new byte[length + length / 128 + 4]; int n = PsdLayerExport.Pack(row, packed);
                var decoded = new List<byte>();
                for (int p = 0; p < n;)
                {
                    int header = unchecked((sbyte)packed[p++]);
                    if (header >= 0) { decoded.AddRange(packed.AsSpan(p, header + 1).ToArray()); p += header + 1; }
                    else if (header != -128) { decoded.AddRange(Enumerable.Repeat(packed[p++], 1 - header)); }
                }
                Check(decoded.SequenceEqual(row) && n <= length + (length + 127) / 128, $"PackBits failed for {length} bytes");
            }
        });

        test("layered PDF makes one Unicode PDF layer per top-level layer with visibility", () =>
        {
            var doc = Scene(); string path = PathFor("scene-layers.pdf");
            ProjectStore.AtomicWrite(path, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PdfLayers, s));
            Check(PdfLayerImport.Count(path) == 5, $"Expected 5 PDF layers, found {PdfLayerImport.Count(path)}");
            using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import))
            {
                var optional = pdf.Internals.Catalog.Elements.GetDictionary("/OCProperties")!;
                var names = optional.Elements.GetArray("/OCGs")!.Elements.Select(e => ((PdfSharp.Pdf.Advanced.PdfReference)e).Value).OfType<PdfDictionary>().Select(d => d.Elements.GetString("/Name")).ToArray();
                Check(names.SequenceEqual(["배경 사진", "건물 그룹", "빈 레이어", "제목 텍스트", "숨긴 도형"]), "PDF layer names: " + string.Join(", ", names));
                var config = optional.Elements.GetDictionary("/D")!;
                Check(config.Elements.GetArray("/OFF")!.Elements.Count == 1 && config.Elements.GetArray("/Order")!.Elements.Count == 5, "Visibility or order list missing");
            }
            var layered = Read(path, new(Dpi: 150, PreservePdfLayers: true)).Document;
            Check(layered.Layers.Any(l => l.Name == "건물 그룹") && layered.Layers.Any(l => l.Name == "제목 텍스트"), "Importer lost layer names: " + string.Join(", ", Names(layered)));
            var hidden = layered.Layers.Single(l => l.Name == "숨긴 도형");
            Check(!hidden.Visible && hidden.Pixels.Data.Where((_, i) => i % 4 == 3).Any(a => a > 0), "A hidden PDF layer must keep its content");
            var flat = Read(path, new(Dpi: 150, PreservePdfLayers: false)).Document;
            double difference = Difference(DesignRenderer.RenderOutput(doc), Imaging.Render(flat), exactSize: false);
            Check(difference < 6, $"Layered PDF looks different ({difference:0.###})");
        });

        test(".ai export is a PDF-compatible file that reopens with its layers", () =>
        {
            var doc = Scene(); string path = PathFor("scene.ai");
            ProjectStore.AtomicWrite(path, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.AiLayers, s));
            Check(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5) == "%PDF-", "The .ai file must start with its PDF part");
            var info = Task.Run(() => PdfCompatibility.InspectAsync(path)).GetAwaiter().GetResult();
            Check(info.Pages == 1 && info.Layers == 5, $"AI layers {info.Layers}");
            var back = Read(path, new(Dpi: 150, PreservePdfLayers: true)).Document;
            Check(back.Layers.Count(l => l.Name != "PDF 용지") >= 5 && back.Layers.Any(l => l.Name == "건물 그룹"), "AI layers did not reopen: " + string.Join(", ", Names(back)));
        });

        test("single-page PDF has no PDF layers and keeps text as vectors", () =>
        {
            var doc = new Document { Width = 120, Height = 60, Name = "벡터 한 장" };
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "도면 제목", FontFamily = "Malgun Gothic", FontSize = 20, ColorArgb = 0xFF101010 }, 4, 4); doc.Add(text);
            doc.Add(VectorShapes.Create(new ShapeSpec { Width = 30, Height = 20, FillArgb = 0xFF2050A0 }));
            foreach (bool vectors in new[] { true, false })
            {
                string path = PathFor($"single-{vectors}.pdf");
                ProjectStore.AtomicWrite(path, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PdfSingle, s, vectors));
                Check(PdfLayerImport.Count(path) == 0, "A single-page PDF must not contain PDF layers");
                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                var xobjects = pdf.Pages[0].Resources.Elements.GetDictionary("/XObject");
                int images = xobjects?.Elements.Values.Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).Count(d => d?.Elements.GetName("/Subtype") == "/Image") ?? 0;
                Check(vectors ? images == 0 : images == 1, $"Vector={vectors}: {images} images");
            }
            var plan = PdfLayerExport.Build(doc, false); Check(plan.VectorParts == 2 && plan.ImageParts == 0, "Text and shape must stay vectors");
            var faded = doc.Snapshot(); faded.Layers[1].Opacity = .5;
            plan = PdfLayerExport.Build(faded, true); Check(plan.VectorParts == 1 && plan.ImageParts == 1, "Only the translucent shape becomes an image");
            string blended = PathFor("blend.pdf"); var multiply = doc.Snapshot(); multiply.Layers[1].Blend = BlendMode.Multiply;
            ProjectStore.AtomicWrite(blended, s => CompatibilityExport.Write(multiply, CompatibilityExportFormat.PdfLayers, s));
            Check(Encoding.Latin1.GetString(File.ReadAllBytes(blended)).Contains("/BM/Multiply") || Encoding.Latin1.GetString(File.ReadAllBytes(blended)).Contains("/BM /Multiply"), "Blend mode must be written as a PDF blend state");
        });

        test("layered PDF opens a wrapped drawing file into its drawing layers", () =>
        {
            var doc = new Document { Width = 100, Height = 80, Name = "평면" };
            var file = DocumentFeatures.CreateGroup(doc, "평면.dxf"); file.Category = LayerCategory.Drawing; doc.Add(file);
            foreach (var name in new[] { "A-WALL", "A-DOOR", "치수" })
            {
                var group = DocumentFeatures.CreateGroup(doc, name); group.Category = LayerCategory.Drawing; group.ParentId = file.Id; doc.Add(group);
                var line = VectorShapes.Create(new ShapeSpec { Width = 20, Height = 3, FillArgb = 0xFF000000 }); line.ParentId = group.Id; line.X = 10; line.Y = 10 + doc.Layers.Count * 5; doc.Add(line);
            }
            var plan = PdfLayerExport.Build(doc, true);
            Check(plan.Expanded && plan.Units.Select(u => u.Source.Name).SequenceEqual(["A-WALL", "A-DOOR", "치수"]) && plan.ImageParts == 0, "Drawing layers did not become PDF layers");
            string path = PathFor("drawing-layers.pdf"); ProjectStore.AtomicWrite(path, s => PdfLayerExport.Write(doc, s, true));
            Check(PdfLayerImport.Count(path) == 3, "Expected one PDF layer per drawing layer");
            var many = new Document { Width = 8, Height = 8 };
            for (int i = 0; i < PdfLayerExport.MaxLayers + 1; i++) many.Add(VectorShapes.Create(new ShapeSpec { Width = 2, Height = 2 }));
            var report = CompatibilityExport.Describe(many, CompatibilityExportFormat.PdfLayers);
            Check(report.Problem != null && report.Problem.Contains("127") && report.Problem.Contains("PDF · 한 장으로 합치기"), "Too many PDF layers must be explained");
            Check(CompatibilityExport.Describe(many, CompatibilityExportFormat.PdfSingle).Problem == null, "The single-page PDF must remain available");
        });

        test("export choices are clearly named and their extension and filter follow the choice", () =>
        {
            var titles = CompatibilityExport.Choices.Select(c => c.Title).ToArray();
            Check(titles.SequenceEqual(["PDF · 한 장으로 합치기 (인쇄·공유용)", "PDF · 레이어 나누기 (레이어별 켜고 끄기)", ".psd · 레이어 유지", ".psd · 한 장으로 합치기", ".ai · 레이어 유지 (PDF 호환)"]), "Choice names changed: " + string.Join(" | ", titles));
            Check(CompatibilityExport.Choices.All(c => c.Description.Length > 10 && !c.Description.Contains('\n')), "Each choice needs a one-line explanation");
            Check(CompatibilityExport.Filter(CompatibilityExportFormat.PsdLayers) == "PSD 이미지|*.psd" && CompatibilityExport.Filter(CompatibilityExportFormat.AiLayers) == "AI 파일 (PDF 호환)|*.ai"
                && CompatibilityExport.Filter(CompatibilityExportFormat.PdfLayers) == "PDF 문서|*.pdf", "Save filters do not follow the choice");
            Check(CompatibilityExport.FileName("도면: 1/2", CompatibilityExportFormat.AiLayers) == "도면_ 1_2.ai" && CompatibilityExport.FileName("평면", CompatibilityExportFormat.PsdSingle) == "평면.psd"
                && CompatibilityExport.FileName("  ", CompatibilityExportFormat.PdfSingle) == "Morupixel.pdf", "Default file names do not follow the choice");
            string[] banned = ["Photoshop", "포토샵", "Illustrator", "일러스트레이터", "Adobe", "어도비", "Acrobat", "AutoCAD"];
            var doc = Scene();
            var words = CompatibilityExport.Choices.SelectMany(c => new[] { c.Title, c.Description, c.FileType })
                .Concat(Enum.GetValues<CompatibilityExportFormat>().SelectMany(f => CompatibilityExport.Describe(doc, f).Lines)).ToArray();
            var found = words.Where(w => banned.Any(b => w.Contains(b, StringComparison.OrdinalIgnoreCase))).ToArray();
            Check(found.Length == 0, "Product names in export text: " + string.Join(" | ", found));
        });

        test("layer renders above the screen tile size stitch into the same image", () =>
        {
            // 4097 px squares need four tiles, including one-pixel edge tiles.
            var doc = new Document { Width = 4097, Height = 4097, Name = "타일" };
            var ramp = new Raster(64, 64); for (int i = 0; i < ramp.Data.Length; i += 4) { ramp.Data[i] = (byte)(i / 4 % 64 * 4); ramp.Data[i + 1] = (byte)(i / 256 * 4); ramp.Data[i + 2] = 90; ramp.Data[i + 3] = 255; }
            doc.Add(new Layer { Name = "경사", Pixels = ramp, Scale = 20, ScaleX = 3.3, ScaleY = 3.3, Rotation = 3 });
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "타일", FontSize = 120, ColorArgb = 0xFFFFFFFF }, 4000, 4000); doc.Add(text);
            var stitched = LayerExportRender.Region(doc, LayerExportRender.Canvas(doc), CancellationToken.None);
            var whole = DesignRenderer.RenderOutput(doc);
            Check(stitched.Width == whole.Width && stitched.Data.AsSpan().SequenceEqual(whole.Data), "Tiled rendering differs from a whole-canvas render");
        });

        test("layered exports reject oversize documents before writing", () =>
        {
            var wide = new Document { Width = 30_001, Height = 1 }; wide.Add(new Layer { Pixels = new Raster(1, 1) });
            var report = CompatibilityExport.Describe(wide, CompatibilityExportFormat.PsdLayers);
            Check(report.Problem != null && report.Problem.Contains("30,000px"), "Oversize PSD must be explained");
            using var output = new MemoryStream();
            bool rejected = false; try { PsdLayerExport.Write(wide, output); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && output.Length == 0, "Oversize PSD wrote output");
        });
    }
}
