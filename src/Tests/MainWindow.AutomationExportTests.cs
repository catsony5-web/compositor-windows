using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

// export_document (PDF, .psd, .ai through the export dialog's writer) and hatch patterns inserted
// and edited over the AI connection, including one-step inline boundaries and batch references.
public sealed partial class MainWindow
{
    internal static void RunAutomationExportTests(Action<string, Action> test, string directory)
    {
        string files = Path.Combine(Path.GetFullPath(directory), "automation-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing string in automation result: " + key);
        static T Await<T>(Task<T> task)
        {
            if (!task.IsCompleted)
            {
                var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(60) };
                timeout.Tick += (_, _) => frame.Continue = false;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                timeout.Start();
                try { Dispatcher.PushFrame(frame); } finally { timeout.Stop(); }
                if (!task.IsCompleted) throw new TimeoutException("Automation command did not complete.");
            }
            return task.GetAwaiter().GetResult();
        }
        static JsonObject Call(MainWindow window, string command, JsonObject arguments)
            => Await(window.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments.DeepClone() }));
        static JsonObject Success(JsonObject response)
        {
            Check(response["ok"]?.GetValue<bool>() == true, "Automation command failed: " + response.ToJsonString());
            return response["result"]!.AsObject();
        }
        static JsonObject Failure(JsonObject response, string code)
        {
            Check(response["ok"]?.GetValue<bool>() == false, "Invalid command was accepted: " + response.ToJsonString());
            var error = response["error"]!.AsObject();
            Check(Text(error, "code") == code && !string.IsNullOrWhiteSpace(Text(error, "suggestedAction")), $"Expected {code}, received {response.ToJsonString()}");
            return error;
        }
        static JsonObject Write(MainWindow window, params (string Key, JsonNode? Value)[] values)
        {
            var arguments = new JsonObject { ["documentId"] = window.tabs[window.activeTab].Id.ToString(), ["expectedRevision"] = window.doc.Revision.ToString(), ["includeLayers"] = false };
            foreach (var (key, value) in values) arguments[key] = value;
            return arguments;
        }
        static JsonArray Rect(double x, double y, double w, double h) => new(new JsonObject { ["x"] = x, ["y"] = y }, new JsonObject { ["x"] = x + w, ["y"] = y },
            new JsonObject { ["x"] = x + w, ["y"] = y + h }, new JsonObject { ["x"] = x, ["y"] = y + h });
        static JsonObject Step(string command, JsonObject arguments, string? reference = null)
        { var step = new JsonObject { ["command"] = command, ["arguments"] = arguments }; if (reference != null) step["ref"] = reference; return step; }
        void Case(string name, Action<MainWindow> action) => test("automation export: " + name, () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try { action(window); }
            finally
            {
                window.renderCts?.Cancel(); window.jobCts?.Cancel();
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                window.history.MarkSaved(window.doc);
                window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        // A background photo, a group of two layers, a hidden nested group, text and a hidden shape.
        static Document Scene()
        {
            var doc = new Document { Width = 80, Height = 60, Dpi = 150, Name = "AI 내보내기" };
            doc.Add(new Layer { Name = "배경", Pixels = Raster.Solid(80, 60, Color.FromRgb(240, 236, 220)) });
            var group = DocumentFeatures.CreateGroup(doc, "건물"); doc.Add(group);
            doc.Add(new Layer { Name = "벽", Pixels = Raster.Solid(20, 10, Colors.IndianRed), X = 5, Y = 5, Blend = BlendMode.Multiply, ParentId = group.Id });
            doc.Add(new Layer { Name = "창", Pixels = Raster.Solid(10, 10, Colors.RoyalBlue), X = 30, Y = 20, Opacity = .5, ParentId = group.Id });
            var detail = DocumentFeatures.CreateGroup(doc, "세부"); detail.Visible = false; detail.ParentId = group.Id; doc.Add(detail);
            doc.Add(new Layer { Name = "문", Pixels = Raster.Solid(6, 6, Colors.SeaGreen), X = 50, Y = 40, ParentId = detail.Id });
            var title = DocumentFeatures.CreateText(new TextSpec { Content = "평면", FontFamily = "Malgun Gothic", FontSize = 14, ColorArgb = 0xFF202020 }, 40, 6);
            title.Name = "제목"; doc.Add(title);
            var shape = VectorShapes.Create(new ShapeSpec { Width = 12, Height = 8, FillArgb = 0xFF3060C0 }); shape.Name = "숨김 도형"; shape.X = 60; shape.Y = 44; shape.Visible = false; doc.Add(shape);
            doc.Validate(); return doc;
        }
        static int Images(string pdfPath)
        {
            using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            var objects = pdf.Pages[0].Resources.Elements.GetDictionary("/XObject");
            return objects?.Elements.Values.Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).Count(d => d?.Elements.GetName("/Subtype") == "/Image") ?? 0;
        }
        static int Pages(string pdfPath) { using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import); return pdf.PageCount; }

        Case("every PDF, .psd and .ai choice is written by the export dialog's writer without editing the document", window =>
        {
            window.AddTab(Scene(), null);
            var revision = window.doc.Revision; bool canUndo = window.history.CanUndo;
            var expectedUnits = PdfLayerExport.Build(window.doc, true).Units.Count; var psdPlan = PsdLayerExport.Build(window.doc);
            foreach (var (format, layers, extension, choice) in new[]
            {
                ("pdf", "keep", ".pdf", CompatibilityExportFormat.PdfLayers), ("pdf", "flatten", ".pdf", CompatibilityExportFormat.PdfSingle),
                ("psd", "keep", ".psd", CompatibilityExportFormat.PsdLayers), ("psd", "flatten", ".psd", CompatibilityExportFormat.PsdSingle),
                ("ai", "keep", ".ai", CompatibilityExportFormat.AiLayers)
            })
            {
                string path = Path.Combine(files, $"scene-{format}-{layers}{extension}");
                var result = Success(Call(window, "export_document", Write(window, ("path", path), ("format", format), ("layers", layers))));
                Check(File.Exists(path) && result["bytes"]!.GetValue<long>() == new FileInfo(path).Length && Text(result, "path") == path, $"{format}/{layers}: file or byte count missing");
                Check(Text(result, "format") == format && Text(result, "layers") == layers && result["notes"]!.AsArray().Count > 0, $"{format}/{layers}: result lacks its description");
                // The dialog's own write of the same snapshot, for comparison.
                string dialog = Path.Combine(files, $"dialog-{format}-{layers}{extension}");
                ProjectStore.AtomicWrite(dialog, s => CompatibilityExport.Write(window.doc.Snapshot(), choice, s, CompatibilityExport.SuggestVectors(window.doc)));
                if (format == "psd")
                {
                    Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(dialog)), $".psd {layers} differs from the dialog's file");
                    var back = PsdCompatibility.Read(path, true).Document;
                    if (layers == "keep")
                    {
                        Check(result["psdLayerCount"]!.GetValue<int>() == psdPlan.Layers && result["psdGroupCount"]!.GetValue<int>() == psdPlan.Folders, "PSD counts differ from the plan");
                        Check(back.Layers.Count == psdPlan.Layers + psdPlan.Folders && back.Layers.Count(l => l.Kind == LayerKind.Group) == psdPlan.Folders,
                            $"Layered .psd read back {back.Layers.Count} layers, expected {psdPlan.Layers}+{psdPlan.Folders}");
                        Check(back.Layers.Any(l => l.Name == "건물" && l.Kind == LayerKind.Group) && back.Layers.Any(l => l.Name == "제목"), "Layer names were lost");
                    }
                    else Check(back.Layers.Count == 1 && result["psdLayerCount"]!.GetValue<int>() == 1 && result["pageCount"]!.GetValue<int>() == 0, "A flattened .psd must hold one layer");
                }
                else
                {
                    int groups = PdfLayerImport.Count(path);
                    Check(groups == PdfLayerImport.Count(dialog) && Pages(path) == 1 && result["pageCount"]!.GetValue<int>() == 1, $"{format}/{layers}: {groups} PDF layers vs dialog {PdfLayerImport.Count(dialog)}");
                    Check(groups == (layers == "keep" ? expectedUnits : 0) && result["pdfLayerCount"]!.GetValue<int>() == groups, $"{format}/{layers}: unexpected PDF layer count {groups}");
                    Check(Images(path) == Images(dialog), $"{format}/{layers}: different image parts than the dialog");
                    if (format == "ai") Check(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5) == "%PDF-", "The .ai file must start with its PDF part");
                    if (layers == "flatten") Check(result["vectors"]!.GetValue<bool>() == CompatibilityExport.SuggestVectors(window.doc), "The flat PDF must default to the dialog's vector choice");
                }
            }
            Check(expectedUnits == 4, $"The scene should make 4 PDF layers (one per top-level layer or group), made {expectedUnits}");
            string image = Path.Combine(files, "scene-flat-image.pdf");
            Success(Call(window, "export_document", Write(window, ("path", image), ("format", "pdf"), ("layers", "flatten"), ("vectors", false))));
            Check(Images(image) == 1 && PdfLayerImport.Count(image) == 0, "vectors=false must place the page as one image");
            Check(window.doc.Revision == revision && window.history.CanUndo == canUndo, "Export changed the document or its history");
            Check(!Directory.EnumerateFiles(files, ".morupixel-*").Any(), "A staging file was left behind");
        });

        Case("one artboard exports at its own size and the result renders like the canvas", window =>
        {
            var doc = new Document { Width = 120, Height = 60, Name = "대지" };
            doc.Add(new Layer { Name = "배경", Pixels = Raster.Solid(120, 60, Colors.White) });
            doc.Add(new Layer { Name = "빨강", Pixels = Raster.Solid(30, 30, Colors.Red), X = 70, Y = 10 });
            window.AddTab(doc, null);
            var board = Success(Call(window, "add_artboard", Write(window, ("name", "오른쪽"), ("x", 60), ("y", 0), ("width", 60), ("height", 60))));
            string id = Text(board, "artboardId"), path = Path.Combine(files, "board.psd");
            var result = Success(Call(window, "export_document", Write(window, ("path", path), ("format", "psd"), ("layers", "flatten"), ("artboardId", id))));
            var back = PsdCompatibility.Read(path, true).Document;
            Check(back.Width == 60 && back.Height == 60 && result["width"]!.GetValue<int>() == 60 && Text(result, "artboardId") == id, "Artboard size was not exported");
            var pixels = Imaging.Render(back); int at = (20 * 60 + 20) * 4;
            Check(pixels.Data[at + 2] > 200 && pixels.Data[at + 1] < 40, "The artboard content moved: red should sit at (20,20)");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "nope.pdf")), ("format", "pdf"), ("artboardId", Guid.NewGuid().ToString()))), "artboard_not_found");
        });

        Case("paths, formats, overwrite, revision and limits are checked before any file is written", window =>
        {
            window.AddTab(Scene(), null);
            string existing = Path.Combine(files, "existing.psd"); File.WriteAllBytes(existing, [1, 2, 3]);
            Failure(Call(window, "export_document", Write(window, ("path", existing), ("format", "psd"))), "file_exists");
            Check(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 1, 2, 3 }), "A refused export replaced the file");
            Success(Call(window, "export_document", Write(window, ("path", existing), ("format", "psd"), ("overwrite", true))));
            Check(new FileInfo(existing).Length > 3, "overwrite=true did not replace the file");
            Failure(Call(window, "export_document", Write(window, ("path", "relative.pdf"), ("format", "pdf"))), "invalid_arguments");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "wrong.png")), ("format", "psd"))), "unsupported_format");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "wrong.pdf")), ("format", "ai"))), "unsupported_format");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "missing", "a.pdf")), ("format", "pdf"))), "file_not_found");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "flat.ai")), ("format", "ai"), ("layers", "flatten"))), "invalid_arguments");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "v.psd")), ("format", "psd"), ("vectors", true))), "invalid_arguments");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "v.pdf")), ("format", "pdf"), ("vectors", true))), "invalid_arguments");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "x.svg")), ("format", "svg"))), "invalid_arguments");
            Failure(Call(window, "export_document", Write(window, ("path", Path.Combine(files, "x.pdf")))), "invalid_arguments");
            var stale = Write(window, ("path", Path.Combine(files, "stale.pdf")), ("format", "pdf")); stale["expectedRevision"] = Guid.NewGuid().ToString();
            Failure(Call(window, "export_document", stale), "stale_revision");
            var batch = Write(window, ("operationId", Guid.NewGuid().ToString()), ("steps", new JsonArray(Step("export_document", new JsonObject { ["path"] = Path.Combine(files, "batch.pdf"), ["format"] = "pdf" }))));
            Failure(Call(window, "apply_batch", batch), "invalid_arguments");
            // More top-level layers than PDF layers allow: the dialog's explanation, and nothing written.
            var many = new Document { Width = 16, Height = 16, Name = "많음" };
            for (int i = 0; i < PdfLayerExport.MaxLayers + 1; i++) many.Add(VectorShapes.Create(new ShapeSpec { Width = 2, Height = 2 }));
            window.AddTab(many, null);
            string limited = Path.Combine(files, "many.pdf");
            var error = Failure(Call(window, "export_document", Write(window, ("path", limited), ("format", "pdf"))), "export_limit");
            Check(Text(error, "message") == CompatibilityExport.Describe(many, CompatibilityExportFormat.PdfLayers).Problem, "The limit message differs from the dialog's");
            Success(Call(window, "export_document", Write(window, ("path", limited), ("format", "pdf"), ("layers", "flatten"))));
            foreach (string name in new[] { "relative.pdf", "wrong.png", "wrong.pdf", "flat.ai", "v.psd", "v.pdf", "x.svg", "x.pdf", "stale.pdf", "batch.pdf" })
                Check(!File.Exists(Path.Combine(files, name)), name + " was written by a refused request");
            Check(!Directory.EnumerateFiles(files, ".morupixel-*").Any(), "A staging file was left behind");
            var capabilities = Success(Call(window, "get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 8 && capabilities["documentExport"]!["layers"]!["ai"]!.AsArray().Count == 1 &&
                capabilities["formats"]!["exportDocument"]!.AsArray().Count == 3 && !capabilities["unsupportedViaMcp"]!.AsArray().Any(v => v!.GetValue<string>().Contains("pdf")),
                "Capabilities do not describe export_document: " + capabilities["documentExport"]?.ToJsonString());
        });

        Case("a hatch pattern is inserted in one call with size, ratio, rotation, line weight, ink and opacity, then edited", window =>
        {
            Success(Call(window, "new_document", new JsonObject { ["name"] = "해치", ["width"] = 120, ["height"] = 80, ["background"] = "#FFFFFF" }));
            var plain = Imaging.Render(window.doc);
            var brick = HatchPatternRenderer.Create(HatchPattern.Brick);
            double baseTile = MaterialEditing.DefaultTile(120, 80, brick);
            var created = Success(Call(window, "apply_material", Write(window, ("patternId", "brick"), ("points", Rect(10, 10, 60, 50)), ("regionName", "벽면"),
                ("scale", .5), ("verticalRatio", 2), ("angle", 30), ("lineWeight", 1.5), ("ink", "#FF3366AA"), ("opacity", .8), ("name", "벽돌"))));
            var id = Guid.Parse(Text(created, "layerId")); Layer Mapped() => window.doc.Layers.Single(l => l.Id == id);
            var region = window.doc.MaterialRegions.Single();
            Check(Text(created, "regionId") == region.Id.ToString() && region.Name == "벽면" && region.Source == "polygon", "The inline boundary was not stored as a region template");
            var fill = Mapped().Material!;
            Check(HatchPatterns.TryGet(fill.Asset, out var kind) && kind == HatchPattern.Brick && window.doc.Materials.Any(m => m.Id == brick.Id), "patternId did not register and apply brick");
            Check(Math.Abs(fill.TileWidth - baseTile * .5) < 1e-9 && Math.Abs(MaterialEditing.Stretch(fill) - 2) < 1e-9 && fill.Angle == 30 && fill.LineWeight == 1.5 && fill.Ink == 0xFF3366AA,
                $"Pattern parameters were not applied: {fill.TileWidth}/{fill.TileHeight}/{fill.Angle}");
            Check(Mapped().Opacity == .8 && Mapped().Name == "벽돌" && Mapped().Blend == BlendMode.Multiply && window.history.CanUndo, "Layer properties were not applied");
            // The pattern draws ink inside the boundary and nothing outside it.
            var rendered = Imaging.Render(window.doc); int inside = 0, outside = 0;
            for (int y = 0; y < 80; y++) for (int x = 0; x < 120; x++)
            {
                int i = (y * 120 + x) * 4; bool changed = Math.Abs(rendered.Data[i] - plain.Data[i]) + Math.Abs(rendered.Data[i + 1] - plain.Data[i + 1]) + Math.Abs(rendered.Data[i + 2] - plain.Data[i + 2]) > 30;
                if (x > 11 && x < 69 && y > 11 && y < 59) inside += changed ? 1 : 0; else if (x < 9 || x > 71 || y < 9 || y > 61) outside += changed ? 1 : 0;
            }
            Check(inside > 200 && outside == 0, $"Pattern pixels inside {inside}, outside {outside}");
            var detail = Success(Call(window, "get_layer", new JsonObject { ["documentId"] = Text(created, "documentId"), ["layerId"] = id.ToString() }))["layer"]!.AsObject();
            var material = detail["material"]!.AsObject();
            Check(Text(material, "patternId") == "brick" && Math.Abs(material["scale"]!.GetValue<double>() - .5) < 1e-6 && Math.Abs(material["verticalRatio"]!.GetValue<double>() - 2) < 1e-6
                && material["angle"]!.GetValue<double>() == 30 && Text(material, "ink") == "#FF3366AA" && material["lineWeight"]!.GetValue<double>() == 1.5 && detail["opacity"]!.GetValue<double>() == .8,
                "get_layer does not round-trip the pattern: " + material.ToJsonString());
            var bounds = fill.Boundary.Geometry.Bounds; var before = Mapped().Pixels;
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("patternId", "sand"), ("scale", 1), ("verticalRatio", 1), ("angle", -15),
                ("lineWeight", .8), ("ink", "default"), ("opacity", .5), ("blend", "Normal"))));
            fill = Mapped().Material!;
            Check(HatchPatterns.TryGet(fill.Asset, out kind) && kind == HatchPattern.Sand && Math.Abs(fill.TileWidth - MaterialEditing.DefaultTile(120, 80, fill.Asset)) < 1e-9 &&
                Math.Abs(MaterialEditing.Stretch(fill) - 1) < 1e-9 && fill.Angle == -15 && fill.LineWeight == .8 && fill.Ink == 0 && Mapped().Opacity == .5 && Mapped().Blend == BlendMode.Normal,
                "update_material did not change every pattern property");
            Check(fill.Boundary.Geometry.Bounds == bounds && !ReferenceEquals(Mapped().Pixels, before), "The boundary changed or the pixels were not redrawn");
            double width = fill.TileWidth;
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("verticalRatio", .5))));
            Check(Mapped().Material!.TileWidth == width && Math.Abs(MaterialEditing.Stretch(Mapped().Material!) - .5) < 1e-9, "verticalRatio alone must keep the repeat width");
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("scale", 2))));
            Check(Math.Abs(MaterialEditing.Stretch(Mapped().Material!) - .5) < 1e-9 && Math.Abs(Mapped().Material!.TileWidth - width * 2) < 1e-9, "scale alone must keep the vertical ratio");
            Success(Call(window, "undo", Write(window))); Success(Call(window, "undo", Write(window))); Success(Call(window, "undo", Write(window)));
            Check(HatchPatterns.TryGet(Mapped().Material!.Asset, out kind) && kind == HatchPattern.Brick && Mapped().Opacity == .8, "One undo must restore one pattern edit");
            Success(Call(window, "undo", Write(window)));
            Check(!window.doc.Layers.Any(l => l.Id == id) && window.doc.MaterialRegions.Count == 0, "Undo must remove the layer and its inline region in one step");
        });

        Case("a closed shape becomes the boundary and invalid pattern requests change nothing", window =>
        {
            Success(Call(window, "new_document", new JsonObject { ["name"] = "경계", ["width"] = 100, ["height"] = 80, ["background"] = "#FFFFFF" }));
            var shape = Success(Call(window, "add_shape", Write(window, ("shape", "ellipse"), ("width", 50), ("height", 40), ("x", 20), ("y", 20), ("fill", "transparent"))));
            string shapeId = Text(shape, "layerId");
            var created = Success(Call(window, "apply_material", Write(window, ("patternId", "dots"), ("boundaryLayerId", shapeId))));
            var region = window.doc.MaterialRegions.Single(); var layer = window.doc.Layers.Single(l => l.Id.ToString() == Text(created, "layerId"));
            Check(region.Source == "closed_layer" && region.SourceLayerId.ToString() == shapeId && region.Name == "영역 1" && layer.Material!.Boundary.Geometry.FillContains(new Point(25, 20)) && !layer.Material.Boundary.Geometry.FillContains(new Point(1, 1)),
                "The closed shape was not used as the boundary");
            Check(Math.Abs(layer.Material!.TileWidth - MaterialEditing.DefaultTile(100, 80, layer.Material.Asset)) < 1e-9, "Omitted size must use the default repeat");
            var before = window.doc.Snapshot();
            string regionId = region.Id.ToString(), sand = HatchPatterns.StableId(HatchPattern.Sand).ToString();
            foreach (var args in new[]
            {
                Write(window, ("patternId", "sand"), ("materialId", sand), ("regionId", regionId)),
                Write(window, ("regionId", regionId)),
                Write(window, ("patternId", "sand")),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("points", Rect(1, 1, 10, 10))),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("holes", new JsonArray(Rect(2, 2, 2, 2)))),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("scale", 1), ("tileWidth", 20)),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("verticalRatio", 1), ("tileHeight", 20)),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("scale", 20)),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("verticalRatio", .1)),
                Write(window, ("patternId", "marble"), ("regionId", regionId)),
                Write(window, ("patternId", "sand"), ("regionId", regionId), ("regionName", "새 이름")),
                Write(window, ("patternId", "sand"), ("points", new JsonArray(new JsonObject { ["x"] = 1, ["y"] = 1 })))
            })
                Failure(Call(window, "apply_material", args), "invalid_arguments");
            Failure(Call(window, "apply_material", Write(window, ("patternId", "sand"), ("boundaryLayerId", Guid.NewGuid().ToString()))), "layer_not_found");
            Failure(Call(window, "apply_material", Write(window, ("patternId", "sand"), ("points", Rect(60, 60, 80, 80)))), "invalid_arguments");
            Failure(Call(window, "update_material", Write(window, ("layerId", layer.Id.ToString()), ("patternId", "sand"), ("materialId", sand))), "invalid_arguments");
            Failure(Call(window, "update_material", Write(window, ("layerId", shapeId), ("patternId", "sand"))), "wrong_layer_kind");
            Check(SameDocument(window.doc, before), "A refused pattern request changed the document");
            // Legacy calls keep working: explicit pixels and an existing region.
            var legacy = Success(Call(window, "apply_material", Write(window, ("materialId", sand), ("regionId", regionId), ("tileWidth", 24), ("tileHeight", 12))));
            var mapped = window.doc.Layers.Single(l => l.Id.ToString() == Text(legacy, "layerId")).Material!;
            Check(mapped.TileWidth == 24 && mapped.TileHeight == 12 && window.doc.MaterialRegions.Count == 1 && Text(legacy, "regionId") == regionId, "A legacy apply_material call changed meaning");
        });

        Case("a batch draws a room, fills it with a pattern by reference and tunes it as one undo step", window =>
        {
            Success(Call(window, "new_document", new JsonObject { ["name"] = "묶음 해치", ["width"] = 100, ["height"] = 80, ["background"] = "#FFFFFF" }));
            var before = window.doc.Snapshot();
            var steps = new JsonArray(
                Step("add_shape", new JsonObject { ["shape"] = "rectangle", ["width"] = 60, ["height"] = 40, ["x"] = 10, ["y"] = 10, ["fill"] = "transparent", ["stroke"] = "#202020" }, "room"),
                Step("apply_material", new JsonObject { ["patternId"] = "grass-sparse", ["boundaryLayerId"] = "@room", ["scale"] = .8, ["regionName"] = "마당" }, "lawn"),
                Step("update_material", new JsonObject { ["layerId"] = "@lawn", ["angle"] = 45, ["ink"] = "#FF2E7D32", ["verticalRatio"] = 1.5 }),
                Step("set_layer", new JsonObject { ["layerId"] = "@lawn", ["opacity"] = .7 }));
            var batch = Write(window, ("operationId", Guid.NewGuid().ToString()), ("label", "잔디 넣기"), ("dryRun", true), ("steps", steps));
            var dry = Success(Call(window, "apply_batch", batch));
            Check(dry["wouldChange"]!.GetValue<bool>() && SameDocument(window.doc, before) && window.doc.MaterialRegions.Count == 0 && dry["steps"]![1]!["regionId"] == null, "Dry run changed the document or leaked IDs");
            batch["dryRun"] = false;
            var applied = Success(Call(window, "apply_batch", batch));
            var lawnId = applied["steps"]![1]!["layerId"]!.GetValue<string>(); var lawn = window.doc.Layers.Single(l => l.Id.ToString() == lawnId);
            var region = window.doc.MaterialRegions.Single();
            Check(applied["undoSteps"]!.GetValue<int>() == 1 && applied["steps"]![1]!["regionId"]!.GetValue<string>() == region.Id.ToString() && region.Name == "마당" &&
                region.SourceLayerId.ToString() == applied["steps"]![0]!["layerId"]!.GetValue<string>(), "The batch did not report or store the inline region");
            Check(HatchPatterns.TryGet(lawn.Material!.Asset, out var kind) && kind == HatchPattern.GrassSparse && lawn.Material.Angle == 45 && lawn.Material.Ink == 0xFF2E7D32 &&
                Math.Abs(MaterialEditing.Stretch(lawn.Material) - 1.5) < 1e-9 && Math.Abs(lawn.Material.TileWidth - MaterialEditing.DefaultTile(100, 80, lawn.Material.Asset) * .8) < 1e-9 && lawn.Opacity == .7,
                "Referenced pattern edits were not applied");
            Success(Call(window, "undo", Write(window)));
            Check(SameDocument(window.doc, before), "One undo must revert the whole pattern batch");
        });
    }
}
