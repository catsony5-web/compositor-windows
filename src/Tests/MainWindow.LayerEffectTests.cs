using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 레이어 효과 in the editor: the properties section (rows, switches, add, settings with live preview and one
// undo step), copy and paste, the quick action and menus, the layer-row badge, both screen densities,
// the move preview with effects, and set_layer_effects / get_layer over the AI connection.
public sealed partial class MainWindow
{
    internal static void RunLayerEffectUiTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        void Window(string name, Action<MainWindow> action) => test("layer effects UI: " + name, () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document); w.history.MarkSaved(w.doc);
                w.StopRenderingForShutdown(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        static Document Board()
        {
            var document = new Document { Width = 400, Height = 300, Name = "효과 보드" };
            document.Add(new Layer { Name = "배경", Pixels = Raster.Solid(400, 300, Colors.LightGray) });
            document.Add(new Layer { Name = "오린 사진", Pixels = Raster.Solid(160, 120, Colors.SteelBlue), X = 40, Y = 40 });
            document.Add(new Layer { Name = "다른 사진", Pixels = Raster.Solid(80, 60, Colors.Orange), X = 260, Y = 180 });
            return document;
        }
        static void Select(MainWindow w, string name)
        {
            var layer = w.doc.Layers.Single(l => l.Name == name);
            w.doc.ActiveId = layer.Id; w.selectedLayers.Clear(); w.selectedLayers.Add(layer.Id); w.BuildProperties();
        }
        static string[] Switches(MainWindow w) => Descendants(w.properties).OfType<CheckBox>().Select(c => AutomationProperties.GetName(c)).Where(n => n.EndsWith(" 켜기")).ToArray();

        Window("the section lists effects top first; adding, switching off and removing are one undo step each", w =>
        {
            w.AddTab(Board(), null); Select(w, "오린 사진");
            Check(w.properties.Children.OfType<SectionHeader>().Any(h => h.Key == "레이어 효과") &&
                Descendants(w.properties).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "레이어 효과 추가"), "The properties panel lacks the effects section");
            w.AddLayerEffect(LayerEffectKind.Stroke);
            Check(w.doc.Active!.Effects is [{ Kind: LayerEffectKind.Stroke }] && w.history.UndoLabel == "획 추가", "Adding a stroke was not one step");
            Check(Descendants(w.properties).OfType<ParameterSlider>().Any(), "A new effect must open its settings");
            w.AddLayerEffect(LayerEffectKind.DropShadow);
            Check(w.doc.Active!.Effects!.Select(e => e.Kind).SequenceEqual([LayerEffectKind.DropShadow, LayerEffectKind.Stroke]), "The shadow did not go behind the stroke");
            Check(Switches(w).SequenceEqual(["획 켜기", "그림자 켜기"]), "Rows must list the top effect first: " + string.Join(", ", Switches(w)));
            var shadowSwitch = Descendants(w.properties).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "그림자 켜기");
            shadowSwitch.IsChecked = false; Click(shadowSwitch);
            Check(!w.doc.Active!.Effects![0].Enabled && w.history.UndoLabel == "레이어 효과 끄기", "Switching off was not one step");
            w.Undo(); Check(w.doc.Active!.Effects![0].Enabled, "Undo did not switch the shadow back on");
            w.RemoveLayerEffect(1);
            Check(w.doc.Active!.Effects is [{ Kind: LayerEffectKind.DropShadow }] && w.history.UndoLabel == "레이어 효과 삭제", "Removing was not one step");
            w.ClearLayerEffects(); Check(w.doc.Active!.Effects == null && w.history.UndoLabel == "레이어 효과 지우기", "Clearing failed");
            w.Undo(); w.Undo(); Check(w.doc.Active!.Effects!.Length == 2, "Undo did not restore the effects");
        });

        Window("dragging a slider previews live and records one undo step", w =>
        {
            w.AddTab(Board(), null); Select(w, "오린 사진");
            w.AddLayerEffect(LayerEffectKind.Stroke); int steps = w.history.UndoLabels.Count;
            var size = Descendants(w.properties).OfType<ParameterSlider>().Single(s => Descendants(s).OfType<TextBox>().Any(t => AutomationProperties.GetName(t) == "크기 px"));
            foreach (double value in new[] { 12d, 16, 20 }) size.SetValue(value, true);
            Check(w.doc.Active!.Effects![0].Size == 20 && w.history.UndoLabels.Count == steps, "The preview must change the effect without recording steps");
            w.CommitEffectPreview(true);
            Check(w.history.UndoLabels.Count == steps + 1 && w.history.UndoLabel == "획 크기", "The drag was not recorded as one step");
            w.Undo(); Check(w.doc.Active!.Effects![0].Size == LayerEffect.Default(LayerEffectKind.Stroke).Size, "Undo did not go back to the size before the drag");
            // A color from the picker and the position choice are one step each.
            w.Redo(); w.effectColorHandler = (_, _) => Colors.Crimson; w.BuildProperties();
            Click(Descendants(w.properties).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "획 색 변경"));
            Check(w.doc.Active!.Effects![0].ColorArgb == 0xFFDC143C && w.history.UndoLabel == "획 색", "The stroke color was not set");
            var position = Descendants(w.properties).OfType<SegmentedChoice<StrokePosition>>().Single();
            position.Select(StrokePosition.Inside);
            Check(w.doc.Active!.Effects![0].Position == StrokePosition.Inside && w.history.UndoLabel == "획 위치", "The stroke position was not set");
        });

        Window("copy and paste effects between layers; the row shows a badge and an effect thumbnail", w =>
        {
            w.AddTab(Board(), null); Select(w, "오린 사진");
            w.AddStrokeEffect(); w.AddLayerEffect(LayerEffectKind.DropShadow);
            var source = w.doc.Active!.Effects!;
            Check(source.Any(e => e is { Kind: LayerEffectKind.Stroke, Position: StrokePosition.Outside, ColorArgb: 0xFFFFFFFF } && e.Size == Math.Clamp(Math.Round(160 * .012), 3, 40)), "The quick stroke was not a white outside stroke sized to the layer");
            w.CopyLayerEffects(); Select(w, "다른 사진"); w.PasteLayerEffects();
            Check(LayerEffects.Same(w.doc.Active!.Effects, source) && w.history.UndoLabel == "레이어 효과 붙여넣기", "Pasting did not copy the effects in one step");
            var layer = w.doc.Active!;
            var row = new LayerRow(layer, false, () => { }, _ => { }, () => { });
            Check(Descendants(row).OfType<FrameworkElement>().Any(e => AutomationProperties.GetName(e) == "레이어 효과") &&
                Descendants(row).OfType<TextBlock>().Any(t => t.Text.EndsWith("레이어 효과")), "The row has no effect badge");
            var thumbnail = Descendants(row).OfType<Image>().First(i => i.Source is System.Windows.Media.Imaging.BitmapSource).Source as System.Windows.Media.Imaging.BitmapSource;
            var plain = layer.Pixels.Thumbnail();
            Check(thumbnail != null && (thumbnail.PixelWidth != plain.PixelWidth || thumbnail.PixelHeight != plain.PixelHeight), "The thumbnail does not include the effects' reach");
            // Groups and adjustment layers cannot hold effects; locked layers refuse edits.
            var group = DocumentFeatures.CreateGroup(w.doc, "그룹"); w.doc.Add(group); Select(w, "그룹");
            w.AddStrokeEffect();
            Check(group.Effects == null && w.status.Text.Contains("그룹") && !w.properties.Children.OfType<SectionHeader>().Any(h => h.Key == "레이어 효과"), "A group accepted effects");
            Select(w, "다른 사진"); w.doc.Active!.Locked = true; int count = w.history.UndoLabels.Count;
            w.AddLayerEffect(LayerEffectKind.OuterGlow);
            Check(w.history.UndoLabels.Count == count && w.status.Text.Contains("잠긴"), "A locked layer accepted an effect");
        });

        Window("menus, command search, ribbon and both screen densities offer the effects", w =>
        {
            w.AddTab(Board(), null); Select(w, "오린 사진");
            var commands = w.BuildCommandRegistry();
            Check(commands.Any(c => c.Id == "menu:레이어/레이어 효과/획 추가") && commands.Any(c => c.Id == "menu:레이어/레이어 효과/레이어 효과 붙여넣기"), "The effect commands are missing from the menu and search");
            Check(RibbonGlyph("획 추가") == Theme.Glyphs.StrokeEffect && RibbonGlyph("레이어 효과 복사") == Theme.Glyphs.Effects, "The ribbon lacks the effect icons");
            Check(w.LayerEffectAddMenu().Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).SequenceEqual(["획", "그림자", "외부 광선", "내부 광선", "색 오버레이", "그라데이션 오버레이", "그림자 레이어 만들기…"]), "The add menu lists the wrong kinds");
            w.AddLayerEffect(LayerEffectKind.GradientOverlay);
            w.SetScreenStyle(true);
            try
            {
                w.BuildProperties();
                Check(Switches(w).SequenceEqual(["그라데이션 오버레이 켜기"]) && Descendants(w.properties).OfType<ParameterSlider>().Count() >= 2, "The compact screen lacks the effect rows");
            }
            finally { w.SetScreenStyle(false); }
        });

        test("layer effects UI: the move preview draws the layer with its effects like the compositor, or falls back", () =>
        {
            var document = new Document { Width = 120, Height = 90 };
            document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(120, 90, Colors.White), Locked = true });
            var mover = new Layer { Name = "오린 사진", Pixels = Raster.Solid(30, 20, Colors.SteelBlue), X = 20, Y = 20,
                Effects = [LayerEffect.Default(LayerEffectKind.DropShadow) with { Distance = 6 }, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 4, ColorArgb = 0xFFFFFFFF }] };
            document.Add(mover);
            document.Add(new Layer { Name = "위", Pixels = Raster.Solid(10, 40, Colors.Lime), X = 70, Y = 10 });
            document.ActiveId = mover.Id;
            Check(CanPreviewLayerMove(document, mover, 1), "A layer with normal effects must keep the fast move preview");
            var (below, above) = CreateLayerMovePreviewStacks(document, mover.Id);
            var (pixels, toLocal) = LayerEffectRenderer.Flatten(mover);
            mover.X = 47; mover.Y = 33;
            var composed = Imaging.Render(below);
            var placement = toLocal; placement.Append(mover.Matrix);
            Imaging.Composite(composed, new Layer { Pixels = pixels }, default, placement);
            Imaging.Composite(composed, new Layer { Pixels = Imaging.Render(above) });
            var expected = Imaging.Render(document);
            int worst = 0; for (int i = 0; i < expected.Data.Length; i++) worst = Math.Max(worst, Math.Abs(expected.Data[i] - composed.Data[i]));
            Check(worst <= 3, $"The moving image with its effects differs from the full render by {worst}");
            var footprint = MoveFootprint(document, mover);
            Check(footprint.X <= 47 - 4 && footprint.X + footprint.Width >= 47 + 30 + 6, $"The move footprint misses the effects: {footprint}");
            mover.Effects = [mover.Effects![0] with { Blend = BlendMode.Multiply }, mover.Effects[1]];
            Check(!CanPreviewLayerMove(document, mover, 1), "A multiplying shadow reads the layers below and must fall back");
            mover.Effects = [mover.Effects[0] with { Blend = BlendMode.Normal }, mover.Effects[1]]; mover.Opacity = .5;
            Check(!CanPreviewLayerMove(document, mover, 1), "A translucent layer with a shadow must fall back");
            mover.Effects = [mover.Effects[1]];
            Check(CanPreviewLayerMove(document, mover, 1), "A translucent layer with a stroke only keeps the fast preview");
            mover.Opacity = 1; document.Layers[2].Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow) with { Blend = BlendMode.Screen }];
            Check(!CanPreviewLayerMove(document, mover, 1), "A screening glow above the mover reads it and must fall back");
        });

        Window("set_layer_effects sets, reports, clears, batches and rejects", w =>
        {
            static JsonObject Await(Task<JsonObject> task) => WaitOnDispatcher(() => task);
            JsonObject Call(string command, JsonObject arguments) => Await(w.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments }));
            static JsonObject Success(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == true, "Automation failed: " + response.ToJsonString()); return response["result"]!.AsObject(); }
            void Failure(JsonObject response, string code) => Check(response["ok"]?.GetValue<bool>() == false && response["error"]?["code"]?.GetValue<string>() == code, $"Expected {code}: {response.ToJsonString()}");
            JsonObject Write(params (string Key, JsonNode? Value)[] values)
            {
                var arguments = new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["expectedRevision"] = w.doc.Revision.ToString(), ["includeLayers"] = false };
                foreach (var (key, value) in values) arguments[key] = value;
                return arguments;
            }
            w.AddTab(Board(), null);
            var photo = w.doc.Layers.Single(l => l.Name == "오린 사진"); string id = photo.Id.ToString();
            var result = Success(Call("set_layer_effects", Write(("layerId", id), ("effects", new JsonArray(
                new JsonObject { ["kind"] = "drop_shadow", ["distance"] = 12, ["angle"] = 60, ["blend"] = "Multiply" },
                new JsonObject { ["kind"] = "stroke", ["size"] = 6, ["color"] = "#FFFFFF", ["position"] = "outside" },
                new JsonObject { ["kind"] = "color_overlay", ["color"] = "#80FF0000", ["enabled"] = false })))));
            photo = w.doc.Layers.Single(l => l.Name == "오린 사진");
            Check(photo.Effects is [{ Kind: LayerEffectKind.DropShadow, Distance: 12, Angle: 60, Blend: BlendMode.Multiply }, { Kind: LayerEffectKind.Stroke, Size: 6, ColorArgb: 0xFFFFFFFF }, { Kind: LayerEffectKind.ColorOverlay, Enabled: false, ColorArgb: 0x80FF0000 }]
                && w.history.UndoLabel == "AI · set_layer_effects" && result["effects"]!.AsArray().Count == 3, "set_layer_effects did not set the list");
            var described = Success(Call("get_layer", new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["layerId"] = id }))["layer"]!;
            Check(described["effects"]!.AsArray()[1]!["position"]!.GetValue<string>() == "outside" && described["effects"]![0]!["opacity"]!.GetValue<double>() == LayerEffect.Default(LayerEffectKind.DropShadow).Opacity
                && described["editing"]!["effects"]!.GetValue<bool>(), "get_layer does not report the effects with their defaults");
            // What get_layer reports is accepted back unchanged.
            Success(Call("set_layer_effects", Write(("layerId", id), ("effects", described["effects"]!.DeepClone()))));
            Check(LayerEffects.Same(w.doc.Layers.Single(l => l.Id == photo.Id).Effects, photo.Effects), "The reported effects did not round-trip");
            var batch = Success(Call("apply_batch", Write(("operationId", Guid.NewGuid().ToString()), ("steps", new JsonArray(
                new JsonObject { ["command"] = "add_text", ["ref"] = "title", ["arguments"] = new JsonObject { ["text"] = "제목", ["x"] = 20, ["y"] = 200, ["fontSize"] = 30 } },
                new JsonObject { ["command"] = "set_layer_effects", ["arguments"] = new JsonObject { ["layerId"] = "@title", ["effects"] = new JsonArray(new JsonObject { ["kind"] = "outer_glow", ["size"] = 10, ["blend"] = "Screen" }) } })))));
            Check(batch["undoSteps"]!.GetValue<int>() == 1 && w.doc.Layers.Single(l => l.Kind == LayerKind.Text).Effects is [{ Kind: LayerEffectKind.OuterGlow, Blend: BlendMode.Screen }], "The batch did not add glowing text");
            Success(Call("set_layer_effects", Write(("layerId", id), ("effects", new JsonArray()))));
            Check(w.doc.Layers.Single(l => l.Id == photo.Id).Effects == null, "An empty list did not clear the effects");
            var before = w.doc.Snapshot();
            void Rejected(string code, JsonArray effects, string? layerId = null) { Failure(Call("set_layer_effects", Write(("layerId", layerId ?? id), ("effects", effects))), code); Check(SameDocument(before, w.doc), "A rejected request changed the document"); }
            Rejected("invalid_arguments", new JsonArray(new JsonObject { ["kind"] = "bevel" }));
            Rejected("invalid_arguments", new JsonArray(new JsonObject { ["kind"] = "stroke", ["distance"] = 4 }));
            Rejected("invalid_arguments", new JsonArray(new JsonObject { ["kind"] = "outer_glow", ["blend"] = "Multiply" }));
            Rejected("invalid_arguments", new JsonArray(new JsonObject { ["kind"] = "stroke", ["size"] = 0.1 }));
            Rejected("invalid_arguments", new JsonArray(Enumerable.Range(0, LayerEffect.MaxCount + 1).Select(_ => (JsonNode?)new JsonObject { ["kind"] = "stroke" }).ToArray()));
            var group = DocumentFeatures.CreateGroup(w.doc, "묶음"); w.doc.Add(group); before = w.doc.Snapshot();
            Rejected("wrong_layer_kind", new JsonArray(new JsonObject { ["kind"] = "stroke" }), group.Id.ToString());
            var capabilities = Success(Call("get_capabilities", new JsonObject()));
            Check(capabilities["layerEffects"]!["kinds"]!.AsArray().Count == 6 && capabilities["batch"]!["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "set_layer_effects"), "Capabilities do not describe set_layer_effects");
        });
    }
}
