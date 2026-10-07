using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Compositor.Windows;

// Properties of a material layer: what fills it, swapping to another material or hatch pattern,
// and its repeat size, vertical ratio, rotation and (for patterns) line weight and ink; a gradient
// screentone adds its direction and the density at each end (and, for the stipple, another random
// arrangement), black poché only its colors. Slider ticks change only the fill description for a
// live preview; the pixels are re-rendered and one undo step is recorded when the change settles.
public sealed partial class MainWindow
{
    sealed record MaterialPreviewState(Document Document, Document Before, string Label, IReadOnlyDictionary<Guid, MaterialFill> Fills);
    MaterialPreviewState? materialPreview;
    DispatcherTimer? materialPreviewTimer;

    void AddMaterialProperties(Layer layer)
    {
        if (layer.Material is not { } fill) return;
        bool locked = IsLockedWithParents(layer), pattern = LinePatterns.IsPattern(fill.Asset), builtIn = HatchPatterns.TryGet(fill.Asset, out var hatch);
        // Black poché has no repeat or line weight; a gradient screentone's density comes from its gradient, not the line weight.
        bool solid = builtIn && hatch == HatchPattern.SolidBlack, gradient = builtIn && HatchPatterns.IsGradient(hatch), screentone = builtIn && HatchPatterns.IsScreentone(hatch);
        var boundDocument = doc; long version = inspectorVersion;
        bool Current() => ReferenceEquals(doc, boundDocument) && inspectorVersion == version && doc.ActiveId == layer.Id && !IsLockedWithParents(layer);
        var controls = new List<UIElement>();
        T Control<T>(T element) where T : UIElement { controls.Add(element); return element; }

        properties.Children.Add(Theme.Section("재질과 패턴"));
        var identity = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
        var swatch = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        if (pattern)
        {
            // The pattern in its ink over its background color, on white paper (a gradient with its own ramp).
            swatch.Background = Brushes.White;
            var layers = new Grid();
            if (MaterialRenderer.Background(fill) is { } tint) layers.Children.Add(new System.Windows.Shapes.Rectangle { Fill = tint, RadiusX = 5, RadiusY = 5 });
            layers.Children.Add(new System.Windows.Shapes.Rectangle { Fill = PatternSwatchBrush(fill.Asset, fill.Ink, gradient ? ToneGradient.Of(fill) : null), RadiusX = 5, RadiusY = 5 });
            swatch.Child = layers;
        }
        else { var texture = new ImageBrush(MaterialThumbnail(fill.Asset)) { Stretch = Stretch.UniformToFill }; texture.Freeze(); swatch.Background = texture; }
        DockPanel.SetDock(swatch, Dock.Left); identity.Children.Add(swatch);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string display = builtIn ? HatchPatterns.Name(hatch) : PresetDisplayName(fill.Asset);
        var title = Theme.Label(display, Theme.BodySize); title.FontWeight = FontWeights.SemiBold;
        if (!HatchPatterns.IsBuiltInMaterial(fill.Asset)) Loc.Keep(title);
        titles.Children.Add(title);
        var tone = ToneGradient.Of(fill);
        var repeat = Theme.Label(solid ? "스크린톤 · 잉크 색으로 채움" : gradient ? $"스크린톤 · 농도 {tone.Start * 100:0}% → {tone.End * 100:0}%"
            : screentone ? $"스크린톤 · 반복 {fill.TileWidth:0}×{fill.TileHeight:0}px" : builtIn ? $"해치 패턴 · 반복 {fill.TileWidth:0}×{fill.TileHeight:0}px"
            : pattern ? $"내 패턴 · 반복 {fill.TileWidth:0}×{fill.TileHeight:0}px" : $"재질 이미지 · 반복 {fill.TileWidth:0}×{fill.TileHeight:0}px", Theme.CaptionSize, Theme.Muted);
        titles.Children.Add(repeat); identity.Children.Add(titles);
        properties.Children.Add(identity);

        // The selection section above already offers swap tiles for this same layer.
        if (SelectionMaterialLayer()?.Id != layer.Id)
        {
            properties.Children.Add(Theme.Section("다른 재질로 바꾸기", foldedByDefault: true));
            var images = SelectionMaterialChoices(new MaterialSuggestion(SurfaceHint.General, null, SelectionMaterials.Order(SurfaceHint.General)));
            var palette = MaterialPalette(pattern ? MaterialPaletteTab.Patterns : materialPaletteTab, fill.Asset.Id, true,
                (asset, name) => { if (Current()) SwapLayerMaterial(asset, name); }, images, SelectionMaterials.PatternOrder(SurfaceHint.General),
                () => { if (Current()) ChooseLayerMaterialImage(); }, "내 재질 이미지를 문서에 등록하고 이 레이어의 재질로 바꿉니다.");
            palette.Margin = new Thickness(0, 0, 0, 4); palette.IsEnabled = !locked;
            properties.Children.Add(palette);
        }

        properties.Children.Add(Theme.Section(solid ? "패턴 색" : "패턴 크기와 방향"));
        double baseTile = MaterialEditing.DefaultTile(doc.Width, doc.Height, fill.Asset);
        ParameterSlider Slider(string label, double min, double max, double value, double reset, string? tip, string history, Func<MaterialFill, double, MaterialFill> change, bool logarithmic = false)
        {
            var slider = new ParameterSlider(label, min, max, value, reset, logarithmic: logarithmic) { ToolTip = tip, IsEnabled = !locked };
            slider.Changed += v => { if (Current()) PreviewMaterialEdit(history, f => change(f, v)); };
            properties.Children.Add(Control(slider)); return slider;
        }
        if (!solid)
        {
            Slider("크기 %", 10, 1000, fill.TileWidth / baseTile * 100, 100, "100%는 새 재질의 기본 크기입니다. 무늬 간격과 길이가 함께 바뀝니다.", "재질 크기",
                (f, v) => MaterialEditing.Sized(f, baseTile * v / 100, MaterialEditing.Stretch(f)), logarithmic: true);
            Slider("세로 비율 %", 25, 400, MaterialEditing.Stretch(fill) * 100, 100, "100%는 원래 비율입니다. 해치 패턴은 무늬 모양을 유지한 채 세로 간격만, 재질 이미지는 이미지를 세로로 늘입니다.", "재질 비율",
                (f, v) => MaterialEditing.Sized(f, f.TileWidth, v / 100), logarithmic: true);
            Slider("회전 °", -180, 180, NormalizeAngle(fill.Angle), 0, null, "재질 회전", (f, v) => f with { Angle = v });
        }
        if (pattern)
        {
            if (!solid && !gradient)
                Slider("선 굵기 %", 25, 400, fill.LineWeight * 100, 100, screentone ? "스크린톤의 점과 선 굵기를 바꿉니다. 굵을수록 농도가 진해집니다." : "해치 패턴의 선과 점 굵기를 바꿉니다.",
                    "패턴 선 굵기", (f, v) => f with { LineWeight = v / 100 });
            uint ink = fill.Ink == 0 ? HatchPatterns.DefaultInk : fill.Ink;
            var row = new DockPanel { Margin = new Thickness(2, 2, 2, 8) };
            var reset = Theme.IconButton(Theme.Glyphs.Revert, () => Guard(() => { if (Current()) EditMaterial("패턴 잉크 색", f => f with { Ink = 0 }); }), "잉크 색 기본값", 26, 14);
            reset.IsEnabled = !locked && fill.Ink != 0; reset.Margin = new Thickness(4, 0, 0, 0); DockPanel.SetDock(reset, Dock.Right); row.Children.Add(Control(reset));
            var chip = PropertyRows.ColorChip(VectorShapes.Color(ink), $"#{ink & 0xFFFFFF:X6}", () =>
            {
                if (!Current()) return;
                var dialog = new ColorPickerDialog(this, VectorShapes.Color(ink), "잉크 색");
                if (dialog.ShowDialog() == true && Current()) EditMaterial("패턴 잉크 색", f => f with { Ink = VectorShapes.Argb(dialog.SelectedColor) | 0xFF000000 });
            }, "잉크 색 변경");
            chip.MinWidth = 120; chip.IsEnabled = !locked; DockPanel.SetDock(chip, Dock.Right); row.Children.Add(Control(chip));
            var caption = Theme.Label("잉크 색", Theme.BodySize, Theme.Muted); caption.Margin = new Thickness(1, 2, 8, 2); row.Children.Add(caption);
            properties.Children.Add(row);
            AddPatternBackgroundRow(fill, locked, Current, Control<UIElement>);
        }
        if (gradient)
        {
            // The density runs across the region: its direction and the density at each end.
            properties.Children.Add(Theme.Section("그라데이션"));
            Slider("방향 °", -180, 180, NormalizeAngle(tone.Angle), ToneGradient.Default.Angle, "농도가 바뀌는 방향입니다. 0°는 왼쪽에서 오른쪽, 90°는 위에서 아래입니다.", "그라데이션 방향",
                (f, v) => f with { Gradient = ToneGradient.Of(f) with { Angle = v } });
            Slider("시작 농도 %", 0, 100, tone.Start * 100, ToneGradient.Default.Start * 100, "영역이 시작하는 가장자리의 잉크 농도입니다.", "그라데이션 시작 농도",
                (f, v) => f with { Gradient = ToneGradient.Of(f) with { Start = v / 100 } });
            Slider("끝 농도 %", 0, 100, tone.End * 100, ToneGradient.Default.End * 100, "영역이 끝나는 가장자리의 잉크 농도입니다.", "그라데이션 끝 농도",
                (f, v) => f with { Gradient = ToneGradient.Of(f) with { End = v / 100 } });
            if (hatch == HatchPattern.StippleGradient)
                properties.Children.Add(Control(Theme.ActionRow("점 배치 바꾸기", () => Guard(() =>
                {
                    if (Current()) EditMaterial("점 배치 바꾸기", f => f with { Gradient = ToneGradient.Of(f) with { Seed = (ToneGradient.Of(f).Seed + 1) % (ToneGradient.MaxSeed + 1) } });
                }), "같은 농도로 점의 무작위 배치만 바꿉니다.", Theme.Glyphs.Grain)));
        }
        var defaults = Theme.ActionRow("기본값으로 되돌리기", () => Guard(() =>
        {
            if (Current()) EditMaterial("재질 기본값", f => MaterialEditing.Sized(f, baseTile, 1) with { Angle = 0, LineWeight = 1, Ink = 0, Gradient = gradient ? null : f.Gradient });
        }), solid ? "기본 잉크 색으로 되돌립니다." : gradient ? "크기 100%, 세로 비율 100%, 회전 0°, 기본 잉크 색과 그라데이션 기본값으로 되돌립니다."
            : pattern ? "크기 100%, 세로 비율 100%, 회전 0°, 선 굵기 100%, 기본 잉크 색으로 되돌립니다." : "크기 100%, 세로 비율 100%, 회전 0°로 되돌립니다.", Theme.Glyphs.Revert);
        defaults.IsEnabled = !locked; properties.Children.Add(Control(defaults));
        if (locked) foreach (var control in controls) control.IsEnabled = false;
    }

    static double NormalizeAngle(double angle)
    {
        double a = angle % 360; if (a <= -180) a += 360; else if (a > 180) a -= 360;
        return a;
    }

    // Korean name of a built-in preset ("morupixel:preset/wood" → 목재 마루); other assets keep their own name.
    static string PresetDisplayName(MaterialAsset asset) =>
        asset.Source.StartsWith("morupixel:preset/", StringComparison.Ordinal) && Enum.TryParse<MaterialKind>(asset.Source["morupixel:preset/".Length..], true, out var kind)
            ? DrawingCleanup.MaterialName(kind) : asset.Name;

    // Material layers the edit applies to: the active layer (or its source-layer bundle), skipping
    // layers without a material and locked ones.
    Layer[] MaterialTargets() => EditingLayerBundle().Where(l => l.Material != null && !IsLockedWithParents(l)).ToArray();

    // Live preview of a slider: every tick recomputes from the fill before the gesture (no compounding).
    internal void PreviewMaterialEdit(string label, Func<MaterialFill, MaterialFill> change)
    {
        if (!HasDocument) return;
        // Another slider of this panel: record the previous change as its own step, but keep the panel,
        // so the slider now under the pointer stays bound for the rest of its drag.
        if (materialPreview is { } pending && (!ReferenceEquals(pending.Document, doc) || pending.Label != label)) CommitMaterialPreview(true, rebuildInspector: false);
        if (materialPreview == null)
        {
            var targets = MaterialTargets();
            if (targets.Length == 0) return;
            materialPreview = new(doc, doc.Snapshot(), label, targets.ToDictionary(l => l.Id, l => l.Material!));
        }
        foreach (var (id, before) in materialPreview.Fills)
        {
            if (doc.Layers.FirstOrDefault(l => l.Id == id) is not { } layer) continue;
            MaterialFill next;
            try { next = change(before); MaterialEditing.ValidateFill(next, layer.Pixels); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { status.Text = error.Message; continue; }
            layer.Material = next;
        }
        RenderGesture();
        materialPreviewTimer ??= CreateMaterialPreviewTimer();
        materialPreviewTimer.Stop(); materialPreviewTimer.Start();
        pendingInspectorCommit = CommitMaterialPreview;
    }

    DispatcherTimer CreateMaterialPreviewTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => CommitMaterialPreview();
        return timer;
    }

    void CommitMaterialPreview() => CommitMaterialPreview(false);

    // A press held inside the properties panel (a slider thumb) postpones the commit; a press anywhere
    // else (the canvas) does not, so a pending change is never recorded after a later gesture.
    bool InspectorHoldsPointer() => Mouse.LeftButton == MouseButtonState.Pressed && Mouse.Captured is Visual held && (ReferenceEquals(held, properties) || properties.IsAncestorOf(held));

    // A canvas gesture starts: record a pending material change first, so its undo step comes before the gesture's.
    void SettleMaterialPreview() { if (materialPreview != null) CommitMaterialPreview(true); }

    // Re-renders the pixels once and records one undo step. While the slider is still held the
    // commit waits, so the inspector is not rebuilt under the pointer; a forced commit from another
    // slider of the panel keeps the panel (rebuildInspector false).
    void CommitMaterialPreview(bool force, bool rebuildInspector = true)
    {
        materialPreviewTimer?.Stop();
        if (materialPreview is not { } preview) return;
        if (!force && InspectorHoldsPointer()) { materialPreviewTimer?.Start(); pendingInspectorCommit = CommitMaterialPreview; return; }
        materialPreview = null;
        if (pendingInspectorCommit is { } commit && commit.Target == this && commit.Method.Name == nameof(CommitMaterialPreview)) pendingInspectorCommit = null;
        if (!ReferenceEquals(preview.Document, doc))
        {
            // The document changed under the preview: put its fills back rather than leave an unrecorded edit.
            foreach (var (id, before) in preview.Fills) if (preview.Document.Layers.FirstOrDefault(l => l.Id == id) is { } layer) layer.Material = before;
            return;
        }
        try
        {
            foreach (var id in preview.Fills.Keys)
                if (doc.Layers.FirstOrDefault(l => l.Id == id) is { Material: { } fill } layer) layer.Pixels = MaterialRenderer.Render(fill);
            doc.Validate(); history.Commit(preview.Label, preview.Before, doc); Refresh(rebuildProperties: rebuildInspector);
        }
        catch (Exception error) { doc = preview.Before; Refresh(); if (headlessTesting) throw; MessageDialog.Show(this, error.Message); }
    }

    // One immediate undo step for discrete changes (swap, ink, defaults).
    void EditMaterial(string label, Func<MaterialFill, MaterialFill> change, MaterialAsset? register = null, string? displayName = null)
    {
        CancelGesture();
        var ids = MaterialTargets().Select(l => l.Id).ToArray();
        if (ids.Length == 0) return;
        Edit(label, () =>
        {
            if (register != null && !doc.Materials.Any(m => m.Id == register.Id) && !MaterialEditing.Assets(doc).Any(a => a.Id == register.Id)) doc.Materials.Add(register);
            foreach (var id in ids)
            {
                var layer = doc.Layers.Single(l => l.Id == id); var before = layer.Material!; var next = change(before);
                MaterialEditing.ValidateFill(next, layer.Pixels);
                layer.Pixels = MaterialRenderer.Render(next); layer.Material = next;
                // An automatic name follows the new material; a name the user gave stays.
                if (displayName != null && IsAutomaticMaterialName(layer.Name, before.Asset)) layer.Name = MaterialLayerName(next.Asset, displayName);
            }
        });
    }

    // Names made by the app (selection swatches, CAD cleanup, apply_material) follow a swap; a name the user typed stays.
    static bool IsAutomaticMaterialName(string name, MaterialAsset asset)
    {
        string own = HatchPatterns.TryGet(asset, out var pattern) ? HatchPatterns.Name(pattern) : LinePatterns.IsCustom(asset) ? asset.Name : PresetDisplayName(asset);
        return name.StartsWith(MaterialLayerName(asset, own), StringComparison.Ordinal) || name.StartsWith("재료 · ", StringComparison.Ordinal);
    }

    internal void SwapLayerMaterial(MaterialAsset asset, string displayName)
    {
        CommitFocusedInspectorField();
        if (!HasDocument || doc.Active is not { Material: { } fill }) return;
        if (fill.Asset.Id == asset.Id) { status.Text = "이미 이 재질이 적용되어 있습니다."; return; }
        var library = MaterialEditing.Assets(doc);
        // The document's own copy of this ID wins (a renamed library pattern keeps the name the document knows).
        asset = library.FirstOrDefault(a => a.Id == asset.Id) ?? asset;
        if (!library.Any(a => a.Id == asset.Id) && library.Count >= MaterialEditing.MaxAssets) { status.Text = "재료 라이브러리가 가득 찼습니다. 쓰지 않는 재질 레이어를 정리하세요."; return; }
        EditMaterial("재질 바꾸기", f => MaterialEditing.Swap(f, asset), asset, displayName);
        status.Text = $"재질을 바꿨습니다: {displayName}";
    }

    void ChooseLayerMaterialImage()
    {
        var open = new OpenFileDialog { Title = "레이어에 채울 재질 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        SwapLayerMaterialImage(open.FileName);
    }

    // Registers the user's image (reusing an identical entry) and swaps it into the active material layer.
    internal void SwapLayerMaterialImage(string path)
    {
        var pixels = MaterialTextures.Load(path);
        string name = Path.GetFileNameWithoutExtension(path), source = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = "재질 이미지";
        var same = MaterialEditing.Assets(doc).FirstOrDefault(a => a.Name == name && a.Source == source && !a.Tileable &&
            a.Pixels.Width == pixels.Width && a.Pixels.Height == pixels.Height && a.Pixels.Data.AsSpan().SequenceEqual(pixels.Data));
        SwapLayerMaterial(same ?? new MaterialAsset(Guid.NewGuid(), name, pixels, source, false), name);
    }
}
