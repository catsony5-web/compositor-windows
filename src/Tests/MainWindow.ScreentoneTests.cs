using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// The 스크린톤 group in the pattern palette, the gradient and black poché rows of a material layer's
// properties (one undo step per change), and screentones over the AI connection.
public sealed partial class MainWindow
{
    internal static void RunScreentoneTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static SegmentedChoice<MaterialPaletteTab> Tabs(Panel panel) => Descendants(panel).OfType<SegmentedChoice<MaterialPaletteTab>>().Single();
        static string[] Groups(Panel panel) => Descendants(panel).OfType<TextBlock>().Select(t => t.Text).Where(t => t is "즐겨찾기" or "기본 패턴" or "스크린톤" or "내 패턴").ToArray();
        static string? Caption(ParameterSlider slider) => slider.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().LastOrDefault()?.Text;
        static ParameterSlider? Slider(MainWindow w, string label) => w.properties.Children.OfType<ParameterSlider>().FirstOrDefault(s => Caption(s) == label);
        static string[] Sections(MainWindow w) => w.properties.Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
        static bool Row(MainWindow w, string caption) => w.properties.Children.OfType<DockPanel>().Any(d => d.Children.OfType<TextBlock>().Any(t => t.Text == caption));
        MainWindow Open(HatchPattern pattern, out Layer layer)
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.AddTab(Plan(), null);
            w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
            layer = w.ApplySelectionMaterial(HatchPatternRenderer.Create(pattern), HatchPatterns.Name(pattern))!;
            w.selection = null; w.Refresh(false);
            return w;
        }

        test("pattern palette: 기본 패턴, 스크린톤 and 내 패턴 groups in order, with gradient previews", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Plan(), null);
                w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
                var panel = w.selectionMaterialPanel!; Tabs(panel).Select(MaterialPaletteTab.Patterns);
                Check(Groups(panel).SequenceEqual(new[] { "기본 패턴", "스크린톤" }), "The built-in groups are missing or out of order: " + string.Join(", ", Groups(panel)));
                var names = Tiles(panel).Select(AutomationProperties.GetName).ToArray();
                var order = SelectionMaterials.PatternOrder(w.SelectionMaterialSuggestion().Surface);
                Check(names.SequenceEqual(order.Where(p => !HatchPatterns.IsScreentone(p)).Concat(order.Where(HatchPatterns.IsScreentone)).Select(HatchPatterns.Name)),
                    "Tiles are not the line patterns, then the screentones: " + string.Join(", ", names));
                Check(names.Skip(19).SequenceEqual(HatchPatterns.All.Where(HatchPatterns.IsScreentone).Select(HatchPatterns.Name)), "The screentones are not in catalog order");
                var ramp = Tiles(panel).Single(t => AutomationProperties.GetName(t) == "점 그라데이션");
                Check(ramp.Content is StackPanel { Children: [Border { Background: SolidColorBrush { Color: var paper }, Child: System.Windows.Shapes.Rectangle { Fill: ImageBrush { TileMode: TileMode.Tile, ViewportUnits: BrushMappingMode.RelativeToBoundingBox } } }, ..] } && paper == Colors.White,
                    "The gradient swatch is not one preview of its ramp on paper");
                // The ramp runs light to dark: more ink at the right of the preview.
                var preview = Raster.FromBitmap(ToneGradientRenderer.Swatch(HatchPattern.StippleGradient, 192, 96));
                double Ink(int x0, int x1) { double sum = 0; for (int y = 0; y < 96; y++) for (int x = x0; x < x1; x++) sum += preview.Data[(y * 192 + x) * 4 + 3]; return sum / (96 * (x1 - x0) * 255d); }
                Check(Ink(0, 40) < .25 && Ink(152, 192) > .75, $"The stipple preview does not run light to dark ({Ink(0, 40):P0} → {Ink(152, 192):P0})");
                // Favorites and 내 패턴 keep their places around the built-in groups.
                w.ToggleFavoritePattern(HatchPatternRenderer.Create(HatchPattern.DotScreen30), "점 스크린 30%");
                var tile = new Raster(32, 32); for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) if ((x + y) % 8 < 2) tile.Data[(y * 32 + x) * 4 + 3] = 255;
                w.linePatternLibrary = [LinePatterns.Create("빗살 무늬", tile)];
                w.Refresh(false); panel = w.selectionMaterialPanel!; Tabs(panel).Select(MaterialPaletteTab.Patterns);
                Check(Groups(panel).SequenceEqual(new[] { "즐겨찾기", "기본 패턴", "스크린톤", "내 패턴" }), "Groups with favorites and 내 패턴 are out of order: " + string.Join(", ", Groups(panel)));
                names = Tiles(panel).Select(AutomationProperties.GetName).ToArray();
                Check(names[0] == "점 스크린 30%" && names.Count(n => n == "점 스크린 30%") == 1 && names[^1] == "빗살 무늬", "A starred screentone did not move to the favorites");
                // A screentone fills the selection like any pattern.
                int layers = w.doc.Layers.Count;
                Click(Tiles(panel).First(t => AutomationProperties.GetName(t) == "검정 채움"));
                var poche = w.doc.Layers.Single(l => l.Kind == LayerKind.Material);
                Check(w.doc.Layers.Count == layers + 1 && poche.Name == "패턴 · 검정 채움" && HatchPatterns.TryGet(poche.Material!.Asset, out var solid) && solid == HatchPattern.SolidBlack
                    && w.history.UndoLabel == "선택 영역 재질" && Tabs(w.selectionMaterialPanel!).Selected == MaterialPaletteTab.Patterns, "The black poché swatch did not make one pattern layer");
            }
            finally { w.materialPaletteTab = MaterialPaletteTab.Images; w.patternFavorites = []; w.StopRenderingForShutdown(); }
        });

        test("gradient properties: direction and density sliders only for gradient fills, one undo step each", () =>
        {
            var w = Open(HatchPattern.DotGradient, out var layer);
            try
            {
                Check(Sections(w).Contains("그라데이션") && Slider(w, "방향 °") != null && Slider(w, "시작 농도 %") != null && Slider(w, "끝 농도 %") != null, "Gradient rows are missing: " + string.Join(", ", Sections(w)));
                Check(Slider(w, "크기 %") != null && Slider(w, "회전 °") != null && Slider(w, "선 굵기 %") == null && Row(w, "잉크 색") && Row(w, "바탕색"), "The gradient fill shows the wrong pattern rows");
                Check(Descendants(w.properties).OfType<TextBlock>().Any(t => t.Text == "스크린톤 · 농도 10% → 90%"), "The gradient caption does not show its densities");
                Check(!Descendants(w.properties).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "점 배치 바꾸기"), "The dot gradient offers a stipple arrangement");
                var pixels = layer.Pixels;
                Slider(w, "시작 농도 %")!.SetValue(30, true); Slider(w, "시작 농도 %")!.SetValue(40, true);
                var live = w.doc.Layers.Single(l => l.Id == layer.Id);
                Check(live.Material!.Gradient == ToneGradient.Default with { Start = .4 } && ReferenceEquals(live.Pixels, pixels), "The preview did not change only the fill description");
                w.CommitFocusedInspectorField();
                Check(w.history.UndoLabel == "그라데이션 시작 농도" && !ReferenceEquals(w.doc.Layers.Single(l => l.Id == layer.Id).Pixels, pixels), "The start density was not one committed step");
                Slider(w, "끝 농도 %")!.SetValue(60, true); Slider(w, "방향 °")!.SetValue(0, true);
                Check(w.history.UndoLabel == "그라데이션 끝 농도", "Switching sliders did not record the end density");
                w.CommitFocusedInspectorField();
                var fill = w.doc.Layers.Single(l => l.Id == layer.Id).Material!;
                Check(w.history.UndoLabel == "그라데이션 방향" && fill.Gradient == new ToneGradient(0, .4, .6), "The direction was not committed: " + fill.Gradient);
                // More ink at the right edge than the left now (40% → 60%, left to right).
                var drawn = w.doc.Layers.Single(l => l.Id == layer.Id).Pixels;
                double Ink(int x0) { double sum = 0; for (int y = 0; y < drawn.Height; y++) for (int x = x0; x < x0 + 16; x++) sum += drawn.Data[(y * drawn.Width + x) * 4 + 3]; return sum / (drawn.Height * 16 * 255d); }
                Check(Ink(drawn.Width - 16) > Ink(0) + .1, $"The layer pixels do not follow the new direction ({Ink(0):P0} → {Ink(drawn.Width - 16):P0})");
                w.Undo(); Check(w.doc.Layers.Single(l => l.Id == layer.Id).Material!.Gradient == ToneGradient.Default with { Start = .4, End = .6 }, "Undo did not step back the direction alone");
                // Defaults restore the gradient too; swapping keeps it for a swap back.
                Click(w.properties.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "기본값으로 되돌리기"));
                Check(w.history.UndoLabel == "재질 기본값" && w.doc.Active!.Material!.Gradient == null, "Defaults kept the gradient");
                w.EditMaterial("그라데이션 끝 농도", f => f with { Gradient = new ToneGradient(45, .2, .8) });
                w.SwapLayerMaterial(HatchPatternRenderer.Create(HatchPattern.DotScreen30), "점 스크린 30%");
                Check(!Sections(w).Contains("그라데이션") && Slider(w, "선 굵기 %") != null && Descendants(w.properties).OfType<TextBlock>().Any(t => t.Text.StartsWith("스크린톤 · 반복 ", StringComparison.Ordinal))
                    && w.doc.Active!.Material!.Gradient == new ToneGradient(45, .2, .8) && w.doc.Active.Name == "패턴 · 점 스크린 30%", "The uniform screen shows gradient rows or lost the kept gradient");
                w.SwapLayerMaterial(HatchPatternRenderer.Create(HatchPattern.StippleGradient), "점묘 그라데이션");
                Check(Math.Abs(Slider(w, "시작 농도 %")!.Value - 20) < 1e-6 && w.doc.Active!.Material!.Gradient == new ToneGradient(45, .2, .8), "Swapping back did not restore the gradient");
                // The stipple's arrangement changes in one step at the same density.
                var arrange = Descendants(w.properties).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "점 배치 바꾸기");
                Click(arrange);
                Check(w.history.UndoLabel == "점 배치 바꾸기" && w.doc.Active!.Material!.Gradient == new ToneGradient(45, .2, .8, 1), "The arrangement did not change in one step");
                w.Undo(); Check(w.doc.Active!.Material!.Gradient!.Seed == 0, "Undo did not restore the arrangement");
                // A locked layer keeps its gradient rows disabled.
                w.EditLayer("잠금", l => l.Locked = true); w.BuildProperties();
                Check(w.properties.Children.OfType<ParameterSlider>().All(s => !s.IsEnabled) && !Descendants(w.properties).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "점 배치 바꾸기").IsEnabled,
                    "A locked gradient layer kept editable controls");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("black poché properties show its colors only; screentone captions and line weight", () =>
        {
            var w = Open(HatchPattern.SolidBlack, out var layer);
            try
            {
                Check(Sections(w).Contains("패턴 색") && !Sections(w).Contains("패턴 크기와 방향") && !w.properties.Children.OfType<ParameterSlider>().Any() && Row(w, "잉크 색") && Row(w, "바탕색"),
                    "Black poché shows repeat or line weight rows: " + string.Join(", ", Sections(w)));
                Check(Descendants(w.properties).OfType<TextBlock>().Any(t => t.Text == "스크린톤 · 잉크 색으로 채움"), "The poché caption is missing");
                w.EditMaterial("패턴 잉크 색", f => f with { Ink = 0xFF203A80 });
                var pixels = w.doc.Layers.Single(l => l.Id == layer.Id).Pixels; int center = ((pixels.Height / 2) * pixels.Width + pixels.Width / 2) * 4;
                Check(pixels.Data[center + 3] == 255 && pixels.Data[center] == 0x80 && pixels.Data[center + 2] == 0x20, "The poché is not filled with its ink");
            }
            finally { w.StopRenderingForShutdown(); }
            var screen = Open(HatchPattern.LineScreen35, out _);
            try
            {
                Check(Slider(screen, "선 굵기 %") is { } weight && ((string)weight.ToolTip).StartsWith("스크린톤의", StringComparison.Ordinal) && !Sections(screen).Contains("그라데이션"),
                    "A uniform screentone lacks its line weight row");
            }
            finally { screen.StopRenderingForShutdown(); }
        });

        RunScreentoneAutomationTests(test, directory);
    }

    static void RunScreentoneAutomationTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? throw new InvalidOperationException("Missing string in automation result: " + key);
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
        static void Failure(JsonObject response, string code)
        {
            Check(response["ok"]?.GetValue<bool>() == false, "Invalid command was accepted: " + response.ToJsonString());
            Check(response["error"]!["code"]!.GetValue<string>() == code, $"Expected {code}, received {response.ToJsonString()}");
        }
        static JsonObject Write(MainWindow window, params (string Key, JsonNode? Value)[] values)
        {
            var arguments = new JsonObject { ["documentId"] = window.tabs[window.activeTab].Id.ToString(), ["expectedRevision"] = window.doc.Revision.ToString(), ["includeLayers"] = false };
            foreach (var (key, value) in values) arguments[key] = value;
            return arguments;
        }
        static JsonArray Rect(double x, double y, double w, double h) => new(new JsonObject { ["x"] = x, ["y"] = y }, new JsonObject { ["x"] = x + w, ["y"] = y },
            new JsonObject { ["x"] = x + w, ["y"] = y + h }, new JsonObject { ["x"] = x, ["y"] = y + h });

        test("automation: screentones are listed with group and coverage, applied by patternId and tuned with gradient parameters", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var listed = Success(Call(window, "query_patterns", new JsonObject()));
                var entries = listed["patterns"]!.AsArray().OfType<JsonObject>().ToArray();
                Check(listed["count"]!.GetValue<int>() == 32 && entries.Count(e => Text(e, "group") == "screentone") == 13 && entries.Take(19).All(e => Text(e, "group") == "basic"),
                    "query_patterns does not list the screentone group after the basic patterns");
                var dots = entries.Single(e => Text(e, "patternId") == "dot-screen-30"); var ramp = entries.Single(e => Text(e, "patternId") == "stipple-gradient");
                Check(dots["coverage"]!.GetValue<double>() == .3 && !dots["gradient"]!.GetValue<bool>() && ramp["coverage"] == null && ramp["gradient"]!.GetValue<bool>() && Text(ramp, "name") == "점묘 그라데이션",
                    "Screentone entries lack coverage or gradient: " + ramp.ToJsonString());
                Check(Success(Call(window, "query_patterns", new JsonObject { ["nameContains"] = "스크린" }))["count"]!.GetValue<int>() == 10, "The name filter does not find the screens");
                var capabilities = Success(Call(window, "get_capabilities", new JsonObject()));
                var patterns = capabilities["materials"]!["patterns"]!;
                Check(capabilities["contractVersion"]!.GetValue<int>() == AutomationCatalog.ContractVersion && patterns["count"]!.GetValue<int>() == 32 && patterns["screentones"]!["count"]!.GetValue<int>() == 13
                    && patterns["parameters"]!.AsArray().Any(p => p!.GetValue<string>() == "gradientStart"), "Capabilities do not describe screentones");
                Success(Call(window, "new_document", new JsonObject { ["name"] = "스크린톤", ["width"] = 200, ["height"] = 120, ["background"] = "#FFFFFF" }));
                // A uniform screen by its key (digits in the key), and a gradient with its parameters.
                var screen = Success(Call(window, "apply_material", Write(window, ("patternId", "dot-screen-30"), ("points", Rect(10, 10, 80, 60)), ("lineWeight", 1.2))));
                var screenFill = window.doc.Layers.Single(l => l.Id.ToString() == Text(screen, "layerId")).Material!;
                Check(HatchPatterns.TryGet(screenFill.Asset, out var kind) && kind == HatchPattern.DotScreen30 && screenFill.LineWeight == 1.2 && screenFill.Gradient == null, "dot-screen-30 was not applied");
                var created = Success(Call(window, "apply_material", Write(window, ("patternId", "dot-gradient"), ("points", Rect(100, 10, 90, 100)), ("regionName", "로비"),
                    ("gradientAngle", 0), ("gradientStart", .05), ("gradientEnd", .95))));
                var id = Guid.Parse(Text(created, "layerId")); Layer Mapped() => window.doc.Layers.Single(l => l.Id == id);
                Check(Mapped().Material!.Gradient == new ToneGradient(0, .05, .95) && Mapped().Blend == BlendMode.Multiply, "The gradient parameters were not applied");
                var detail = Success(Call(window, "get_layer", new JsonObject { ["documentId"] = Text(created, "documentId"), ["layerId"] = id.ToString() }))["layer"]!["material"]!.AsObject();
                Check(Text(detail, "patternId") == "dot-gradient" && Text(detail, "patternGroup") == "screentone" && detail["gradient"]!["end"]!.GetValue<double>() == .95
                    && detail["gradient"]!["angle"]!.GetValue<double>() == 0, "get_layer does not report the gradient: " + detail.ToJsonString());
                var pixels = Mapped().Pixels; double Ink(int x0) { double sum = 0; for (int y = 0; y < pixels.Height; y++) for (int x = x0; x < x0 + 10; x++) sum += pixels.Data[(y * pixels.Width + x) * 4 + 3]; return sum / (pixels.Height * 10 * 255d); }
                Check(Ink(pixels.Width - 10) > Ink(0) + .6, $"The applied gradient does not run left to right ({Ink(0):P0} → {Ink(pixels.Width - 10):P0})");
                // update_material changes only what it names; a swap to the stipple keeps the gradient.
                Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("gradientEnd", .5))));
                Check(Mapped().Material!.Gradient == new ToneGradient(0, .05, .5), "gradientEnd alone did not update");
                Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("patternId", "stipple-gradient"), ("gradientSeed", 7))));
                Check(HatchPatterns.TryGet(Mapped().Material!.Asset, out kind) && kind == HatchPattern.StippleGradient && Mapped().Material!.Gradient == new ToneGradient(0, .05, .5, 7), "The swap lost the gradient or ignored the seed");
                // Gradient parameters belong to gradient fills only, and are range-checked.
                var before = window.doc.Snapshot();
                Failure(Call(window, "apply_material", Write(window, ("patternId", "dot-screen-30"), ("points", Rect(1, 1, 9, 9)), ("gradientStart", .2))), "invalid_arguments");
                Failure(Call(window, "apply_material", Write(window, ("materialId", HatchPatterns.StableId(HatchPattern.Brick).ToString()), ("points", Rect(1, 1, 9, 9)), ("gradientEnd", .2))), "invalid_arguments");
                Failure(Call(window, "update_material", Write(window, ("layerId", Text(screen, "layerId")), ("gradientAngle", 30))), "invalid_arguments");
                Failure(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("gradientStart", 2))), "invalid_arguments");
                Failure(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("gradientSeed", -1))), "invalid_arguments");
                Failure(Call(window, "apply_material", Write(window, ("patternId", "dot-screen-99"), ("points", Rect(1, 1, 9, 9)))), "invalid_arguments");
                Check(window.doc.Layers.Count == before.Layers.Count && Mapped().Material!.Gradient == new ToneGradient(0, .05, .5, 7), "A refused request changed the document");
                // In a batch, a gradient step is atomic with the others.
                var batch = new JsonObject
                {
                    ["documentId"] = window.tabs[window.activeTab].Id.ToString(), ["expectedRevision"] = window.doc.Revision.ToString(), ["operationId"] = Guid.NewGuid().ToString(), ["includeLayers"] = false,
                    ["steps"] = new JsonArray(
                        new JsonObject { ["command"] = "apply_material", ["ref"] = "wall", ["arguments"] = new JsonObject { ["patternId"] = "solid-black", ["points"] = Rect(10, 80, 80, 30), ["ink"] = "#FF101010" } },
                        new JsonObject { ["command"] = "update_material", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["gradientAngle"] = 90, ["gradientStart"] = .9, ["gradientEnd"] = .1 } })
                };
                Success(Call(window, "apply_batch", batch));
                Check(window.doc.Layers.Any(l => l.Material is { } f && HatchPatterns.TryGet(f.Asset, out var p) && p == HatchPattern.SolidBlack && f.Ink == 0xFF101010)
                    && Mapped().Material!.Gradient == new ToneGradient(90, .9, .1, 7) && window.history.UndoLabel?.Length > 0, "The batch did not apply black poché and the reversed gradient");
                AutomationCatalog.Validate("apply_material", new JsonObject { ["documentId"] = Guid.NewGuid().ToString(), ["expectedRevision"] = Guid.NewGuid().ToString(), ["patternId"] = "line-screen-50", ["regionId"] = Guid.NewGuid().ToString() });
            }
            finally
            {
                window.renderCts?.Cancel(); window.jobCts?.Cancel();
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                window.history.MarkSaved(window.doc); window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }
}
