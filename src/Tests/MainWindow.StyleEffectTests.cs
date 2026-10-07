using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunStyleEffectTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is not DependencyObject node) continue;
                if (node is T match) yield return match;
                foreach (var nested in Find<T>(node)) yield return nested;
            }
        }
        static Document Sample()
        {
            var pixels = new Raster(96, 64);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 96; x++) { int i = (y * 96 + x) * 4; pixels.Data[i] = (byte)(x * 2); pixels.Data[i + 1] = (byte)(y * 3); pixels.Data[i + 2] = (byte)(x + y); pixels.Data[i + 3] = 255; }
            var doc = new Document { Width = 96, Height = 64, Name = "효과 확인" }; doc.Add(new Layer { Name = "사진", Pixels = pixels }); return doc;
        }
        string[] labels = ["한계값…", "망점 (하프톤)…", "종이·인쇄 질감…", "빛 번짐…"];

        test("style effects are in the adjustment menu, ribbon, command palette and photo panel", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(NewDocumentDialog.CreateDocument("효과", "64", "48", 1), null);
                var registry = window.BuildCommandRegistry();
                foreach (var label in labels)
                {
                    Check(registry.Any(c => c.Id == "menu:레이어/새 조정 레이어/" + label), "Menu command missing: " + label);
                    Check(RibbonGlyph(label) != Theme.Glyphs.More, "Ribbon icon missing: " + label);
                }
                var adjustments = window.mainMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "레이어")).Items.OfType<MenuItem>().Single(m => Equals(m.Header, "새 조정 레이어"));
                var headers = adjustments.Items.OfType<MenuItem>().Select(m => m.Header as string).ToArray();
                Check(headers.SkipWhile(h => h != "그레인…").Skip(1).Take(5).SequenceEqual([.. labels, "선택 조정 레이어 편집…"]), "Menu order changed: " + string.Join(", ", headers));
                var panel = new StackPanel(); window.BuildPhotoActions(panel);
                var keys = panel.Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
                Check(Array.IndexOf(keys, "스타일 효과") == Array.IndexOf(keys, "조정 레이어") + 1, "The style effect section does not follow the adjustment layers: " + string.Join(", ", keys));
                var tiles = Find<Button>(panel).Where(b => AutomationProperties.GetName(b) is "한계값" or "망점" or "종이·인쇄 질감" or "빛 번짐").ToArray();
                Check(tiles.Length == 4 && tiles.All(t => t.ToolTip is string { Length: > 10 } && t.Content is not string), "Style effect tiles are missing icons or tooltips");
                foreach (var glyph in new[] { Theme.Glyphs.Threshold, Theme.Glyphs.Halftone, Theme.Glyphs.PaperTexture, Theme.Glyphs.Glow })
                    foreach (var layer in glyph.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var bounds = System.Windows.Media.Geometry.Parse(layer.TrimStart('~', '*')).Bounds;
                        Check(bounds.Left >= 2.5 && bounds.Top >= 2.5 && bounds.Right <= 21.5 && bounds.Bottom <= 21.5, "Glyph leaves the 24-unit grid: " + layer);
                    }
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("style effect dialogs restore, edit and reset every setting with an actual-size view", () =>
        {
            var doc = Sample();
            var specs = new AdjustmentSpec[]
            {
                new() { Kind = AdjustmentKind.Threshold, Threshold = new() { Level = 90, Smoothness = 12, KeepAlpha = false } },
                new() { Kind = AdjustmentKind.Halftone, Halftone = new() { CellSize = 14, Angle = -30, Shape = HalftoneShape.Square, InkArgb = 0x00000000 } },
                new() { Kind = AdjustmentKind.PaperTexture, Paper = new() { Seed = 77, Scale = 3, Toner = .4, Edges = .7, EdgeWidth = .2 } },
                new() { Kind = AdjustmentKind.Glow, Glow = new() { Threshold = .5, Radius = 120, Intensity = 2.5, TintArgb = 0x80FF8000 } }
            };
            foreach (var spec in specs)
            {
                var dialog = new AdjustmentDialog(null, doc, spec);
                try
                {
                    var sliders = Find<ParameterSlider>(dialog).ToDictionary(s => AutomationProperties.GetName(Find<Slider>(s).Single()));
                    var toggles = Find<CheckBox>(dialog).Where(c => !ReferenceEquals(c, dialog.PreviewToggle)).ToDictionary(c => (string)c.Content);
                    switch (spec.Kind)
                    {
                        case AdjustmentKind.Threshold:
                            Check(sliders["검정·흰색 경계"].Value == 90 && sliders["부드러운 가장자리"].Value == 12 && toggles["원래 투명도 유지"].IsChecked == false, "Threshold settings were not restored");
                            sliders["검정·흰색 경계"].SetValue(140, true);
                            toggles["원래 투명도 유지"].IsChecked = true; toggles["원래 투명도 유지"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Check(dialog.Spec.Threshold == new ThresholdSpec { Level = 140, Smoothness = 12, KeepAlpha = true }, "Threshold edits were lost: " + dialog.Spec.Threshold);
                            break;
                        case AdjustmentKind.Halftone:
                            var shapes = Find<SegmentedChoice<HalftoneShape>>(dialog).Single();
                            Check(shapes.Selected == HalftoneShape.Square && sliders["망점 간격 (px)"].Value == 14 && sliders["각도 (°)"].Value == 150, "Halftone settings were not restored");
                            Check(toggles["원래 색으로 망점 찍기"].IsChecked == true && toggles["망점 사이에 아래 이미지 보이기"].IsChecked == false, "Halftone color modes were not restored");
                            shapes.Select(HalftoneShape.Line); sliders["망점 간격 (px)"].SetValue(6, true);
                            toggles["원래 색으로 망점 찍기"].IsChecked = false; toggles["원래 색으로 망점 찍기"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Check(dialog.Spec.Halftone.Shape == HalftoneShape.Line && dialog.Spec.Halftone.CellSize == 6 && dialog.Spec.Halftone.InkArgb == 0xFF000000, "Halftone edits were lost: " + dialog.Spec.Halftone);
                            break;
                        case AdjustmentKind.PaperTexture:
                            Check(sliders["무늬 번호"].Value == 77 && sliders["질감 크기 (px)"].Value == 3 && Math.Abs(sliders["토너 반점 (%)"].Value - 40) < 1e-9 && Math.Abs(sliders["가장자리 폭 (%)"].Value - 20) < 1e-9, "Paper settings were not restored");
                            sliders["복사 줄무늬 (%)"].SetValue(55, true); sliders["섬유 (%)"].SetValue(0, true);
                            Check(Math.Abs(dialog.Spec.Paper.Streaks - .55) < 1e-9 && dialog.Spec.Paper.Fibers == 0 && dialog.Spec.Paper.Seed == 77, "Paper edits were lost: " + dialog.Spec.Paper);
                            Find<Button>(dialog).Single(b => Equals(b.Content, "다른 무늬")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Check(dialog.Spec.Paper.Seed != 77 && sliders["무늬 번호"].Value == dialog.Spec.Paper.Seed, "Another pattern did not change the seed and its slider");
                            break;
                        case AdjustmentKind.Glow:
                            Check(sliders["빛으로 볼 밝기 (%)"].Value == 50 && sliders["번짐 반경 (px)"].Value == 120 && sliders["세기"].Value == 2.5 && Math.Abs(sliders["빛 색 농도 (%)"].Value - 128 / 2.55) < .01, "Glow settings were not restored");
                            sliders["세기"].SetValue(0, true); sliders["빛 색 농도 (%)"].SetValue(0, true);
                            Check(dialog.Spec.Glow.Intensity == 0 && (dialog.Spec.Glow.TintArgb >> 24) == 0 && (dialog.Spec.Glow.TintArgb & 0xFFFFFF) == 0xFF8000, "Glow edits were lost: " + dialog.Spec.Glow);
                            break;
                    }
                    dialog.Spec.Validate();
                    Check(Find<ParameterSlider>(dialog).All(s => s.ToolTip is string { Length: > 8 }), spec.Kind + " sliders lack explanations");
                    dialog.RenderDetailNow();
                    Check(dialog.DetailPreview is { PixelWidth: 96, PixelHeight: 64 }, spec.Kind + " actual-size view does not show the small document at 1:1");
                    Find<Button>(dialog).Single(b => Equals(b.Content, "기본값으로 되돌리기")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var defaults = new AdjustmentSpec();
                    Check(DocumentFeatures.SameAdjustment(dialog.Spec, defaults with { Kind = spec.Kind }), spec.Kind + " reset left changed values");
                    Check(dialog.Title.EndsWith(AdjustmentDialog.StyleEffectTitle(spec.Kind), StringComparison.Ordinal), "Dialog title is not the effect's name");
                }
                finally { dialog.Close(); }
            }
            // A large document previews at the stage resolution and keeps one document pixel per screen pixel in the detail.
            var large = new Document { Width = 3000, Height = 1800 }; large.Add(new Layer { Pixels = Raster.Solid(3000, 1800, System.Windows.Media.Colors.Gray) });
            var big = new AdjustmentDialog(null, large, new AdjustmentSpec { Kind = AdjustmentKind.Halftone });
            try
            {
                var preview = big.Renderer(AdjustmentDialog.PreviewDocument(large, null, big.Spec, true, null), default);
                Check(preview.Width == AdjustmentDialog.PreviewMaxSide && preview.Height == 1536, $"Large preview rendered at {preview.Width}×{preview.Height}");
                big.MoveDetail(new Point(-50, 99999)); big.RenderDetailNow();
                Check(big.DetailCenter == new Point(0, 1800) && big.DetailPreview is { PixelHeight: > 100 }, "The actual-size view left the document or did not render");
            }
            finally { big.Close(); }
        });

        test("automation adds style effect layers with their settings and rejects wrong parameters", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                JsonObject Call(string command, JsonObject arguments)
                {
                    var task = window.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments }, default);
                    if (!task.IsCompleted)
                    {
                        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
                        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)), TaskScheduler.Default);
                        Dispatcher.PushFrame(frame);
                    }
                    return task.GetAwaiter().GetResult();
                }
                JsonObject Write(params (string Key, JsonNode? Value)[] values)
                {
                    var state = Call("get_state", new JsonObject { ["includeLayers"] = false })["result"]!.AsObject();
                    var current = state["documents"]!.AsArray().OfType<JsonObject>().Single(d => d["documentId"]!.GetValue<string>() == state["activeDocumentId"]!.GetValue<string>());
                    var args = new JsonObject { ["documentId"] = current["documentId"]!.GetValue<string>(), ["expectedRevision"] = current["revision"]!.GetValue<string>() };
                    foreach (var (key, value) in values) args[key] = value;
                    return args;
                }
                Check(Call("new_document", new JsonObject { ["name"] = "효과 자동화", ["width"] = 80, ["height"] = 60, ["dpi"] = 96, ["background"] = "#203040" })["ok"]!.GetValue<bool>(), "Document creation failed");
                var added = new List<Guid>();
                foreach (var (kind, args) in new (string, (string, JsonNode?)[])[]
                {
                    ("threshold", [("level", 100), ("smoothness", 3), ("keepAlpha", false)]),
                    ("halftone", [("cellSize", 12), ("angle", 15), ("dotShape", "square"), ("ink", "#FF1B1464"), ("paper", "transparent")]),
                    ("paper_texture", [("seed", 5), ("textureSize", 2.5), ("toner", .5), ("edges", .4), ("edgeColor", "#FFFFFF"), ("name", "복사 질감")]),
                    ("glow", [("radius", 40), ("intensity", 2), ("glowColor", "#80FFC080")])
                })
                {
                    var response = Call("add_adjustment", Write([("kind", kind), .. args]));
                    Check(response["ok"]!.GetValue<bool>(), kind + " failed: " + response.ToJsonString());
                    added.Add(Guid.Parse(response["result"]!["layerId"]!.GetValue<string>()));
                }
                var layers = added.Select(id => window.doc.Layers.Single(l => l.Id == id)).ToArray();
                Check(layers[0].Adjustment!.Threshold == new ThresholdSpec { Level = 100, Smoothness = 3, KeepAlpha = false } && layers[0].Name == "한계값", "Threshold settings differ");
                Check(layers[1].Adjustment!.Halftone == new HalftoneSpec { CellSize = 12, Angle = 15, Shape = HalftoneShape.Square, InkArgb = 0xFF1B1464, PaperArgb = 0x00FFFFFF }, "Halftone settings differ: " + layers[1].Adjustment!.Halftone);
                Check(layers[2].Adjustment!.Paper == new PaperTextureSpec { Seed = 5, Scale = 2.5, Toner = .5, Edges = .4, EdgeArgb = 0xFFFFFFFF } && layers[2].Name == "복사 질감", "Paper settings differ");
                Check(layers[3].Adjustment!.Glow == new GlowSpec { Radius = 40, Intensity = 2, TintArgb = 0x80FFC080 } && layers[3].Name == "빛 번짐", "Glow settings differ");
                var detail = Call("get_layer", new JsonObject { ["documentId"] = Write()["documentId"]!.DeepClone(), ["layerId"] = added[3].ToString() });
                Check(detail.ToJsonString().Contains("\"Radius\":40", StringComparison.Ordinal), "get_layer does not report glow settings: " + detail.ToJsonString());
                var before = window.doc.Snapshot(); int count = window.doc.Layers.Count;
                foreach (var bad in new (string, JsonNode?)[][]
                {
                    [("kind", "halftone"), ("radius", 10)], [("kind", "halftone"), ("cellSize", 1)], [("kind", "glow"), ("intensity", 9)],
                    [("kind", "paper_texture"), ("edgeColor", "burnt")], [("kind", "threshold"), ("level", 300)], [("kind", "halftone"), ("dotShape", "star")]
                })
                {
                    var response = Call("add_adjustment", Write(bad));
                    Check(response["ok"]!.GetValue<bool>() == false && response["error"]!["code"]!.GetValue<string>().Length > 0, "Accepted " + response.ToJsonString());
                }
                Check(window.doc.Layers.Count == count && Imaging.Render(window.doc).Data.SequenceEqual(Imaging.Render(before).Data), "A rejected call changed the document");
                Check(Call("undo", Write())["ok"]!.GetValue<bool>() && !window.doc.Layers.Any(l => l.Id == added[3]) && window.doc.Layers.Any(l => l.Id == added[2]), "Undo did not remove exactly the last effect");
                Check(Call("redo", Write())["ok"]!.GetValue<bool>() && window.doc.Layers.Any(l => l.Id == added[3]), "Redo did not restore the effect");
            }
            finally
            {
                window.history.MarkSaved(window.doc); foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                window.StopRenderingForShutdown(); window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }
}
