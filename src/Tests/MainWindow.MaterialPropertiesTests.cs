using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// Hatch pattern tab in the material palette and the material layer's properties (size, ratio,
// rotation, line weight, ink) with live preview and one undo step per change.
public sealed partial class MainWindow
{
    internal static void RunMaterialPropertiesTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string[] Sections(MainWindow w) => w.properties.Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
        static string? Caption(ParameterSlider slider) => slider.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().LastOrDefault()?.Text;
        static ParameterSlider? Slider(MainWindow w, string label) => w.properties.Children.OfType<ParameterSlider>().FirstOrDefault(s => Caption(s) == label);
        static SegmentedChoice<MaterialPaletteTab> Tabs(Panel panel) => Descendants(panel).OfType<SegmentedChoice<MaterialPaletteTab>>().Single();
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        MainWindow Open(out Layer layer, HatchPattern? pattern = HatchPattern.GrassSparse)
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.AddTab(Plan(), null);
            w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
            layer = pattern is { } p ? w.ApplySelectionMaterial(HatchPatternRenderer.Create(p), HatchPatterns.Name(p))!
                : w.ApplySelectionMaterial(PresetMaterial(MaterialKind.Wood), DrawingCleanup.MaterialName(MaterialKind.Wood))!;
            w.selection = null; w.Refresh(false);
            return w;
        }

        test("material palette: the hatch pattern tab offers every pattern on paper and applies and swaps them", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Plan(), null);
                w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
                var panel = w.selectionMaterialPanel!;
                Check(Tabs(panel).Selected == MaterialPaletteTab.Images && Tiles(panel).Length == SelectionMaterials.Presets.Length, "The palette does not open on material images");
                Tabs(panel).Select(MaterialPaletteTab.Patterns);
                var tiles = Tiles(panel); var surface = w.SelectionMaterialSuggestion().Surface;
                Check(tiles.Select(AutomationProperties.GetName).SequenceEqual(SelectionMaterials.PatternOrder(surface).Select(HatchPatterns.Name)), "Pattern tiles are missing or out of order: " + string.Join(", ", tiles.Select(AutomationProperties.GetName)));
                Check(tiles.All(t => double.IsNaN(t.Width) && t.Content is StackPanel { Children.Count: 2 } c && c.Children[0] is Border { Background: SolidColorBrush { Color: var paper }, Child: System.Windows.Shapes.Rectangle { Fill: ImageBrush { TileMode: TileMode.Tile } } } && paper == Colors.White),
                    "A pattern swatch is not a tiled pattern on white paper");
                Check(Descendants(panel).OfType<TextBlock>().Any(t => t.Text == "도면용 선 패턴입니다. 바탕이 투명해 아래 색과 선이 그대로 보입니다."), "The pattern note is missing");
                int layers = w.doc.Layers.Count;
                Click(tiles.First(t => AutomationProperties.GetName(t) == "잔디"));
                var layer = w.doc.Layers.Single(l => l.Kind == LayerKind.Material); var fill = layer.Material!;
                Check(w.doc.Layers.Count == layers + 1 && layer.Name == "패턴 · 잔디" && layer.Blend == BlendMode.Multiply && ReferenceEquals(fill.Asset, HatchPatternRenderer.Create(HatchPattern.GrassSparse))
                    && w.doc.Materials.Any(m => m.Id == fill.Asset.Id) && w.history.UndoLabel == "선택 영역 재질", "The lawn swatch did not make one named pattern layer");
                panel = w.selectionMaterialPanel!;
                Check(Tabs(panel).Selected == MaterialPaletteTab.Patterns, "A pattern layer did not reopen the pattern tab");
                Click(Tiles(panel).First(t => AutomationProperties.GetName(t) == "모래"));
                var swapped = w.doc.Layers.Single(l => l.Kind == LayerKind.Material).Material!;
                Check(HatchPatterns.TryGet(swapped.Asset, out var sand) && sand == HatchPattern.Sand && swapped.TileWidth == fill.TileWidth && swapped.TileHeight == fill.TileHeight
                    && w.history.UndoLabel == "선택 영역 재질 바꾸기" && w.doc.Active!.Name == "패턴 · 모래", "The sand swatch did not swap the pattern keeping its size");
                Check(Tiles(w.selectionMaterialPanel!).Single(t => t.Background == Theme.Selected) is { } current && AutomationProperties.GetName(current) == "모래", "The applied pattern is not marked");
            }
            finally { w.materialPaletteTab = MaterialPaletteTab.Images; w.StopRenderingForShutdown(); }
        });

        test("material properties: pattern and image layers show their sections and pattern-only rows", () =>
        {
            var w = Open(out var layer);
            try
            {
                var sections = Sections(w);
                Check(sections.Contains("재질과 패턴") && sections.Contains("다른 재질로 바꾸기") && sections.Contains("패턴 크기와 방향"), "Material sections are missing: " + string.Join(", ", sections));
                Check(w.properties.Children.OfType<SectionHeader>().Single(h => h.Key == "다른 재질로 바꾸기").Folded, "The swap section is not folded by default");
                Check(Slider(w, "크기 %") != null && Slider(w, "세로 비율 %") != null && Slider(w, "회전 °") != null && Slider(w, "선 굵기 %") != null, "Pattern sliders are missing");
                Check(w.properties.Children.OfType<DockPanel>().Any(d => d.Children.OfType<TextBlock>().Any(t => t.Text == "잉크 색")), "The ink row is missing");
                Check(Descendants(w.properties).OfType<TextBlock>().Any(t => t.Text == "해치 패턴 레이어"), "The kind caption does not name a hatch pattern layer");
                // While the selection section offers swap tiles for this layer, the properties omit their copy.
                w.selection = new Selection(new Rect(20, 20, 80, 120)); w.selectionMaterialTarget = new(w.doc, w.selection, layer.Id, null); w.Refresh(false);
                Check(!Sections(w).Contains("다른 재질로 바꾸기"), "The swap palette is shown twice");
            }
            finally { w.StopRenderingForShutdown(); }
            var image = Open(out _, null);
            try
            {
                Check(Sections(image).Contains("패턴 크기와 방향") && Slider(image, "크기 %") != null && Slider(image, "선 굵기 %") == null
                    && !image.properties.Children.OfType<DockPanel>().Any(d => d.Children.OfType<TextBlock>().Any(t => t.Text == "잉크 색")), "An image layer shows pattern-only rows");
            }
            finally { image.StopRenderingForShutdown(); }
        });

        test("material properties: slider ticks preview live and commit as one undo step", () =>
        {
            var w = Open(out var layer);
            try
            {
                var original = layer.Material!; var pixels = layer.Pixels; string label = w.history.UndoLabel!;
                double tile = MaterialEditing.DefaultTile(w.doc.Width, w.doc.Height);
                var size = Slider(w, "크기 %")!;
                size.SetValue(150, true); size.SetValue(180, true); size.SetValue(200, true);
                var live = w.doc.Layers.Single(l => l.Id == layer.Id);
                Check(Math.Abs(live.Material!.TileWidth - tile * 2) < 1e-9 && Math.Abs(MaterialEditing.Stretch(live.Material) - 1) < 1e-9 && w.history.UndoLabel == label && ReferenceEquals(live.Pixels, pixels),
                    "The preview did not change only the fill description");
                w.CommitFocusedInspectorField();
                var committed = w.doc.Layers.Single(l => l.Id == layer.Id);
                Check(w.history.UndoLabel == "재질 크기" && !ReferenceEquals(committed.Pixels, pixels) && Math.Abs(committed.Material!.TileWidth - tile * 2) < 1e-9, "The size change was not one committed step");
                w.Undo();
                Check(w.history.UndoLabel == label && w.doc.Layers.Single(l => l.Id == layer.Id).Material == original, "Undo did not restore the original fill");
                // A pending change becomes its own step before another edit.
                Slider(w, "세로 비율 %")!.SetValue(200, true);
                w.EditLayer("혼합 모드", l => l.Blend = BlendMode.Normal);
                Check(w.history.UndoLabel == "혼합 모드", "The blend change was not recorded");
                w.Undo();
                Check(w.history.UndoLabel == "재질 비율" && Math.Abs(MaterialEditing.Stretch(w.doc.Layers.Single(l => l.Id == layer.Id).Material!) - 2) < 1e-9, "The pending ratio was not flushed as its own step");
                Slider(w, "선 굵기 %")!.SetValue(250, true); Slider(w, "회전 °")!.SetValue(30, true);
                Check(w.history.UndoLabel == "패턴 선 굵기" && w.doc.Layers.Single(l => l.Id == layer.Id).Material!.LineWeight == 2.5, "Switching sliders did not commit the previous one");
                w.CommitFocusedInspectorField();
                Check(w.history.UndoLabel == "재질 회전" && w.doc.Layers.Single(l => l.Id == layer.Id).Material!.Angle == 30, "Rotation was not committed");
                // Defaults: size, ratio, rotation, line weight and ink in one step.
                w.EditMaterial("패턴 잉크 색", f => f with { Ink = 0xFF3366AA });
                Click(w.properties.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "기본값으로 되돌리기"));
                var reset = w.doc.Layers.Single(l => l.Id == layer.Id).Material!;
                Check(w.history.UndoLabel == "재질 기본값" && Math.Abs(reset.TileWidth - tile) < 1e-9 && Math.Abs(MaterialEditing.Stretch(reset) - 1) < 1e-9 && reset.Angle == 0 && reset.LineWeight == 1 && reset.Ink == 0,
                    "Defaults did not reset every setting: " + reset);
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("material properties: bundles edit only material layers, locks disable controls and swaps are single steps", () =>
        {
            var w = Open(out var layer);
            try
            {
                var raster = new Layer { Name = "사진", Pixels = Raster.Solid(20, 20, Colors.Gray) }; w.doc.Add(raster);
                w.sourceLayerSelection = [layer.Id, raster.Id]; w.selectedLayers.Clear(); w.selectedLayers.Add(layer.Id); w.selectedLayers.Add(raster.Id); w.doc.ActiveId = layer.Id;
                w.PreviewMaterialEdit("재질 크기", f => MaterialEditing.Sized(f, f.TileWidth * 3, 1));
                w.CommitFocusedInspectorField();
                Check(w.history.UndoLabel == "재질 크기" && w.doc.Layers.Single(l => l.Id == raster.Id).Material == null, "The bundle edit failed or touched the raster layer");
                w.sourceLayerSelection = null; w.selectedLayers.Clear(); w.selectedLayers.Add(layer.Id);
                w.EditLayer("잠금", l => l.Locked = true); w.BuildProperties();
                var sliders = w.properties.Children.OfType<ParameterSlider>().ToArray();
                Check(sliders.Length >= 4 && sliders.All(s => !s.IsEnabled) && !w.properties.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "기본값으로 되돌리기").IsEnabled,
                    "A locked material layer kept editable controls");
                w.Edit("잠금", () => w.doc.Layers.Single(l => l.Id == layer.Id).Locked = false);
                w.SwapLayerMaterial(PresetMaterial(MaterialKind.Wood), DrawingCleanup.MaterialName(MaterialKind.Wood));
                Check(w.history.UndoLabel == "재질 바꾸기" && w.doc.Active!.Material!.Asset.Source == "morupixel:preset/wood" && w.doc.Active.Name == "재질 · 목재 마루", "Pattern → image swap failed");
                w.SwapLayerMaterial(HatchPatternRenderer.Create(HatchPattern.Brick), HatchPatterns.Name(HatchPattern.Brick));
                Check(w.history.UndoLabel == "재질 바꾸기" && HatchPatterns.TryGet(w.doc.Active!.Material!.Asset, out var brick) && brick == HatchPattern.Brick && w.doc.Active.Name == "패턴 · 벽돌", "Image → pattern swap failed");
                w.Undo(); Check(w.doc.Active!.Material!.Asset.Source == "morupixel:preset/wood", "Undo did not restore the image material");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("material properties and palette are translated in English", () =>
        {
            string previous = Loc.Language;
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                Loc.Use("en");
                w.AddTab(Plan(), null);
                w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
                var layer = w.ApplySelectionMaterial(HatchPatternRenderer.Create(HatchPattern.GrassSparse), "잔디")!;
                Check(layer.Name == "Pattern · Lawn", "The English pattern layer name is " + layer.Name);
                foreach (var text in new[] { "재질 이미지", "해치 패턴", "크기 %", "세로 비율 %", "선 굵기 %", "잉크 색", "재질과 패턴", "패턴 크기와 방향", "다른 재질로 바꾸기" })
                    Check(Loc.T(text) != text && !Loc.T(text).Any(c => c is >= '가' and <= '힣'), "Not translated: " + text);
            }
            finally { Loc.Use(previous); w.StopRenderingForShutdown(); }
        });
    }
}
