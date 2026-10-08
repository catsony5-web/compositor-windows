using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Compositor.Windows;

// Properties panel, while a selection exists: recommended material swatches for it.
// A swatch turns the selection into an editable material layer in one undo step, on the
// same path as the AI connection's define_region (source: selection) + apply_material.
// The layer just made stays the target, so another swatch swaps its material instead of
// stacking a second fill on the same area.
public sealed partial class MainWindow
{
    sealed record SelectionMaterialCache(Document Document, Guid Revision, Selection Selection, MaterialSuggestion Suggestion);
    // AddedAsset: the library entry this flow registered for the layer's current material, if any.
    sealed record SelectionMaterialTarget(Document Document, Selection Selection, Guid LayerId, Guid? AddedAsset);
    internal sealed record SelectionMaterialChoice(MaterialAsset Asset, string Name, bool Preset);
    // The palette's two kinds of swatches: material images (presets and the document's own) and line hatch patterns.
    internal enum MaterialPaletteTab { Images, Patterns }
    // Remembered for the session; a target layer that already holds a pattern opens on patterns.
    internal MaterialPaletteTab materialPaletteTab = MaterialPaletteTab.Images;
    SelectionMaterialCache? selectionMaterialCache;
    SelectionMaterialTarget? selectionMaterialTarget;
    internal StackPanel? selectionMaterialPanel;
    static readonly Dictionary<MaterialKind, MaterialAsset> presetMaterials = [];
    static readonly ConditionalWeakTable<Raster, BitmapSource> materialThumbnails = new();

    static MaterialAsset PresetMaterial(MaterialKind kind)
    {
        lock (presetMaterials)
        {
            if (!presetMaterials.TryGetValue(kind, out var asset)) presetMaterials[kind] = asset = MaterialPresets.Create(kind);
            return asset;
        }
    }

    internal MaterialSuggestion SelectionMaterialSuggestion()
    {
        var region = selection!;
        if (selectionMaterialCache is { } cache && ReferenceEquals(cache.Document, doc) && cache.Revision == doc.Revision && ReferenceEquals(cache.Selection, region))
            return cache.Suggestion;
        MaterialSuggestion suggestion;
        try { suggestion = SelectionMaterials.Suggest(doc, region); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        { suggestion = new(SurfaceHint.General, null, SelectionMaterials.Order(SurfaceHint.General)); }
        selectionMaterialCache = new(doc, doc.Revision, region, suggestion);
        return suggestion;
    }

    // Built-in swatches in recommended order. The document's own images join them: one
    // whose name spells a material (오크 마루 → wood) sits before that swatch, others lead.
    internal IReadOnlyList<SelectionMaterialChoice> SelectionMaterialChoices(MaterialSuggestion suggestion)
    {
        var presets = suggestion.Order.Select(kind => new SelectionMaterialChoice(PresetMaterial(kind), DrawingCleanup.MaterialName(kind), true)).ToArray();
        var presetIds = presets.Select(choice => choice.Asset.Id).ToHashSet();
        IReadOnlyList<MaterialAsset> assets;
        try { assets = MaterialEditing.Assets(doc); } catch (InvalidDataException) { assets = []; }
        var custom = assets.Where(asset => !presetIds.Contains(asset.Id) && !asset.Source.StartsWith("morupixel:preset/", StringComparison.Ordinal)
                && !asset.Source.StartsWith(HatchPatterns.SourcePrefix, StringComparison.Ordinal) && !LinePatterns.IsCustom(asset))
            .Select(asset => (Asset: asset, Rank: SelectionMaterials.Named(asset.Name) is { } kind ? Array.IndexOf(suggestion.Order.ToArray(), kind) : -1)).ToArray();
        var result = custom.Where(c => c.Rank < 0).Select(c => new SelectionMaterialChoice(c.Asset, c.Asset.Name, false)).ToList();
        for (int i = 0; i < presets.Length; i++)
        {
            result.AddRange(custom.Where(c => c.Rank == i).Select(c => new SelectionMaterialChoice(c.Asset, c.Asset.Name, false)));
            result.Add(presets[i]);
        }
        return result;
    }

    // The layer made from this selection, while it is still the active layer.
    Layer? SelectionMaterialLayer() =>
        selectionMaterialTarget is { } target && ReferenceEquals(target.Document, doc) && ReferenceEquals(target.Selection, selection) && doc.ActiveId == target.LayerId
        && doc.Active is { Material: not null } layer && !IsLockedWithParents(layer) ? layer : null;

    void AddSelectionMaterials()
    {
        selectionMaterialPanel = null;
        if (!HasDocument || selection == null || tool == Tool.Artboard) return;
        var suggestion = SelectionMaterialSuggestion();
        var target = SelectionMaterialLayer();
        var panel = selectionMaterialPanel = new StackPanel();
        var header = Theme.Section("선택 영역 재질", properties.Children.Count > 0);
        header.ToolTip = "선택 영역을 재질 이미지로 채운 레이어로 만듭니다. 원본 이미지와 경계는 그대로 보관됩니다.";
        panel.Children.Add(header);
        string lead = suggestion.Surface switch
        {
            SurfaceHint.Wall => "벽체에 어울리는 재질부터 보여 줍니다.",
            SurfaceHint.Floor => "바닥에 어울리는 재질부터 보여 줍니다.",
            SurfaceHint.Ground => "외부 바닥에 어울리는 재질부터 보여 줍니다.",
            _ => "선택 영역에 채울 재질을 고르세요."
        };
        var caption = Density.Mark(Theme.Label(lead, Theme.CaptionSize, Theme.Muted), DensityRole.Description); caption.Margin = new Thickness(2, 0, 2, 2);
        panel.Children.Add(caption);
        if (suggestion.LayerName is { } source)
        {
            var reference = new WrapPanel { Margin = new Thickness(2, 0, 2, 2) };
            var label = Theme.Label("기준 레이어", Theme.CaptionSize, Theme.Subtle); label.Margin = new Thickness(0, 0, 6, 0);
            var name = Loc.Keep(Theme.Label(source, Theme.CaptionSize, Theme.Muted)); name.Margin = new Thickness(0); name.ToolTip = source;
            reference.Children.Add(label); reference.Children.Add(name); panel.Children.Add(reference);
        }
        var hint = Theme.Label(target != null ? "다른 재질을 누르면 방금 만든 재질 레이어를 바꿉니다."
            : SelectionMaterials.IsDrawing(doc) ? "누르면 선택 영역 모양의 재질 레이어를 도면 선 아래에 만듭니다." : "누르면 선택 영역 모양의 재질 레이어를 만듭니다.",
            Theme.CaptionSize, Theme.Subtle);
        hint.Margin = new Thickness(2, 0, 2, 6); panel.Children.Add(Density.Mark(hint, DensityRole.Description));
        var current = target?.Material?.Asset.Id;
        var tab = LinePatterns.IsPattern(target?.Material?.Asset) ? MaterialPaletteTab.Patterns : materialPaletteTab;
        panel.Children.Add(MaterialPalette(tab, current, target != null, (asset, name) => ApplySelectionMaterial(asset, name),
            SelectionMaterialChoices(suggestion), SelectionMaterials.PatternOrder(suggestion.Surface),
            () => ChooseSelectionMaterialImage(), "내 재질 이미지를 문서에 등록하고 선택 영역에 채웁니다."));
        properties.Children.Add(panel);
    }

    internal static SelectionMaterialChoice PatternChoice(HatchPattern pattern) => new(HatchPatternRenderer.Create(pattern), HatchPatterns.Name(pattern), true);

    // Tabs [재질 이미지 | 해치 패턴] above the swatch grid. Shared by the selection section and the
    // material layer's properties; switching tabs only replaces the grid below.
    internal FrameworkElement MaterialPalette(MaterialPaletteTab tab, Guid? currentAssetId, bool swapping, Action<MaterialAsset, string> apply,
        IReadOnlyList<SelectionMaterialChoice> imageChoices, IReadOnlyList<HatchPattern> patternOrder, Action? addImage = null, string? addImageTip = null)
    {
        var palette = new StackPanel();
        var tabs = new SegmentedChoice<MaterialPaletteTab>([(MaterialPaletteTab.Images, "재질 이미지"), (MaterialPaletteTab.Patterns, "해치 패턴")], tab) { Margin = new Thickness(2, 0, 2, 8) };
        foreach (var button in tabs.Buttons)
        {
            button.MinWidth = 0; button.Padding = new Thickness(10, 5, 10, 5);
            if (button.Content is string caption) button.Content = new KeepWordsTextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        }
        palette.Children.Add(tabs);
        var body = new StackPanel(); palette.Children.Add(body);
        void Show(MaterialPaletteTab shown)
        {
            body.Children.Clear();
            if (shown == MaterialPaletteTab.Patterns) { AddPatternTab(body, patternOrder, currentAssetId, swapping, apply, () => Show(MaterialPaletteTab.Patterns)); return; }
            body.Children.Add(QuickActions.Grid(4, imageChoices.Select(choice => MaterialTile(choice, choice.Asset.Id == currentAssetId, swapping, apply)), 76));
            if (addImage != null)
                body.Children.Add(Theme.ActionRow("이미지로 재질 추가…", () => Guard(() => { CommitFocusedInspectorField(); addImage(); }), addImageTip, Theme.Glyphs.Image));
        }
        tabs.Changed += shown => { materialPaletteTab = shown; Show(shown); };
        Show(tab);
        return palette;
    }

    Button MaterialTile(SelectionMaterialChoice choice, bool current, bool swapping, Action<MaterialAsset, string>? apply = null)
    {
        apply ??= (asset, name) => ApplySelectionMaterial(asset, name);
        var button = Theme.Button("", () => Guard(() => apply(choice.Asset, choice.Name)),
            swapping ? "방금 만든 재질 레이어를 이 재질로 바꾸기" : "선택 영역에 이 재질로 레이어 만들기");
        button.Tag = choice;
        var content = new StackPanel();
        Border swatch;
        if (LinePatterns.IsPattern(choice.Asset))
        {
            // Patterns show on paper (white, also in the dark theme), repeated at a fixed scale so they stay crisp and seamless.
            swatch = new Border { Height = 46, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = current ? Theme.Accent : Theme.Stroke, Background = Brushes.White,
                Child = new System.Windows.Shapes.Rectangle { Fill = PatternSwatchBrush(choice.Asset), RadiusX = 4, RadiusY = 4 } };
        }
        else
        {
            // The swatch stretches with its column; the texture fills it without distortion.
            var texture = new ImageBrush(MaterialThumbnail(choice.Asset)) { Stretch = Stretch.UniformToFill }; texture.Freeze();
            swatch = new Border { Height = 46, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = current ? Theme.Accent : Theme.Stroke, Background = texture };
        }
        content.Children.Add(swatch);
        // Two-line names (점 그라데이션) wrap between words.
        var label = new KeepWordsTextBlock
        {
            Text = choice.Name, FontSize = Theme.CaptionSize, Foreground = current ? Theme.Text : Theme.Muted, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal
        };
        if (!choice.Preset) Loc.Keep(label);
        content.Children.Add(label);
        button.Content = content; button.Padding = new Thickness(4, 4, 4, 6); button.Margin = new Thickness(3);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top;
        if (current) { button.Background = Theme.Selected; button.BorderBrush = Theme.Accent; button.BorderThickness = new Thickness(1); }
        AutomationProperties.SetName(button, choice.Name);
        return button;
    }

    static BitmapSource MaterialThumbnail(MaterialAsset asset) => HatchPatterns.TryGet(asset, out var pattern)
            ? HatchPatterns.IsGradient(pattern) ? ToneGradientRenderer.Swatch(pattern, 96, 96) : HatchPatternRenderer.Swatch(pattern, 96)
        : LinePatterns.IsCustom(asset) ? LinePatternRenderer.Swatch(asset, 96) : materialThumbnails.GetValue(asset.Pixels, pixels => pixels.Thumbnail(96));

    // A pattern swatch brush on 48 DIP repeats drawn at 96 px: on white paper for the palette, or with
    // `ink` as a clear tile to lay over a fill's background. A user's pattern whose lines are thin for
    // its size repeats larger (RepeatScale), as it does on the canvas, and keeps its own proportions.
    // A gradient screentone does not repeat: one preview of its ramp fills the swatch (light to dark in
    // the palette; a fill's own `gradient` and ink in its properties).
    internal static ImageBrush PatternSwatchBrush(MaterialAsset asset, uint? ink = null, ToneGradient? gradient = null)
    {
        ImageBrush brush;
        if (HatchPatterns.TryGet(asset, out var pattern) && HatchPatterns.IsGradient(pattern))
        {
            var ramp = gradient == null ? ToneGradientRenderer.Swatch(pattern, 192, 96, ink ?? 0) : ToneGradientRenderer.Swatch(pattern, 96, 96, ink ?? 0, gradient);
            brush = new ImageBrush(ramp) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.RelativeToBoundingBox, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.UniformToFill };
            brush.Freeze(); return brush;
        }
        if (HatchPatterns.TryGet(asset, out pattern))
            brush = new ImageBrush(ink is { } color ? HatchPatternRenderer.Tile(pattern, 96, 96, HatchPatternRenderer.SwatchPen(pattern, 1.6), color) : HatchPatternRenderer.Swatch(pattern, 96)) { Viewport = new Rect(0, 0, 48, 48) };
        else
        {
            int px = (int)Math.Clamp(Math.Round(96 * LinePatternRenderer.RepeatScale(asset)), 96, 256);
            BitmapSource swatch = ink is { } color ? LinePatternRenderer.Tile(asset, px, Math.Clamp((int)Math.Round(px * MaterialEditing.Aspect(asset)), 1, 1024), 1, color)
                : LinePatternRenderer.Swatch(asset, px);
            brush = new ImageBrush(swatch) { Viewport = new Rect(0, 0, px / 2d, swatch.PixelHeight / 2d) };
        }
        brush.TileMode = TileMode.Tile; brush.ViewportUnits = BrushMappingMode.Absolute; brush.Stretch = Stretch.Fill; brush.Freeze();
        return brush;
    }

    // Layer name for a material: built-in swatches in the display language, a user's image by its own name.
    static string MaterialLayerName(MaterialAsset asset, string displayName) => HatchPatterns.TryGet(asset, out _)
        ? Loc.T("패턴 · ") + Loc.T(displayName)
        : LinePatterns.IsCustom(asset) ? Loc.T("패턴 · ") + displayName
        : Loc.T("재질 · ") + (HatchPatterns.IsBuiltInMaterial(asset) ? Loc.T(displayName) : displayName);

    void ChooseSelectionMaterialImage()
    {
        if (!HasDocument || selection == null) { status.Text = "먼저 재질을 채울 영역을 선택하세요."; return; }
        var open = new OpenFileDialog { Title = "선택 영역에 채울 재질 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        ApplySelectionMaterialImage(open.FileName);
    }

    // Registers the user's image (as register_material does) and fills the selection with it.
    internal Layer? ApplySelectionMaterialImage(string path)
    {
        var pixels = MaterialTextures.Load(path);
        string name = Path.GetFileNameWithoutExtension(path), source = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = "재질 이미지";
        // The same image picked again reuses its library entry instead of adding a copy.
        var same = MaterialEditing.Assets(doc).FirstOrDefault(a => a.Name == name && a.Source == source && !a.Tileable &&
            a.Pixels.Width == pixels.Width && a.Pixels.Height == pixels.Height && a.Pixels.Data.AsSpan().SequenceEqual(pixels.Data));
        return ApplySelectionMaterial(same ?? new MaterialAsset(Guid.NewGuid(), name, pixels, source, false), name);
    }

    internal Layer? ApplySelectionMaterial(MaterialAsset asset, string displayName)
    {
        CommitFocusedInspectorField();
        if (!HasDocument || selection is not { } region) { status.Text = "먼저 재질을 채울 영역을 선택하세요."; return null; }
        var library = MaterialEditing.Assets(doc);
        // The document's own copy of this ID wins (a renamed library pattern keeps the name the document knows).
        asset = library.FirstOrDefault(a => a.Id == asset.Id) ?? asset;
        bool known = library.Any(a => a.Id == asset.Id);
        if (!known && library.Count >= MaterialEditing.MaxAssets) { status.Text = "재료 라이브러리가 가득 찼습니다. 쓰지 않는 재질 레이어를 정리하세요."; return null; }
        // Built-in swatches are named in the display language; the user's own image keeps its name.
        string layerName = MaterialLayerName(asset, displayName);
        if (SelectionMaterialLayer() is { Material: { } fill } existing)
        {
            if (fill.Asset.Id == asset.Id) { status.Text = "이미 이 재질이 적용되어 있습니다."; return existing; }
            Guid id = existing.Id; var target = selectionMaterialTarget!; bool swapped = false;
            Edit("선택 영역 재질 바꾸기", () =>
            {
                if (!known) doc.Materials.Add(asset);
                var layer = doc.Layers.Single(l => l.Id == id);
                // Size, the user's vertical ratio, direction, ink and line weight carry over.
                var replacement = MaterialEditing.Swap(fill, asset);
                MaterialEditing.ValidateFill(replacement, layer.Pixels);
                layer.Pixels = MaterialRenderer.Render(replacement); layer.Material = replacement; layer.Name = layerName;
                // An entry this flow registered for the material swapped away leaves with it once no layer uses it.
                if (target.AddedAsset == fill.Asset.Id && !doc.Layers.Any(l => l.Material?.Asset.Id == fill.Asset.Id))
                    doc.Materials.RemoveAll(m => m.Id == fill.Asset.Id);
                swapped = true;
            });
            if (swapped && ReferenceEquals(target.Document, doc)) selectionMaterialTarget = target with { AddedAsset = known ? null : asset.Id };
            status.Text = $"재질을 바꿨습니다: {displayName}";
            return doc.Layers.FirstOrDefault(l => l.Id == id);
        }
        // Regions whose material layers were deleted do not count: their slots are reused below.
        if (UsedMaterialRegions().Count >= MaterialEditing.MaxRegions) { status.Text = "적용 영역은 최대 128개까지 보관합니다. 쓰지 않는 재질 레이어를 정리하세요."; return null; }
        Geometry boundary;
        try { boundary = SelectionMaterials.Boundary(region, doc.Width, doc.Height); }
        catch (InvalidDataException error) { status.Text = error.Message; return null; }
        Layer? created = null; bool added = false;
        Edit("선택 영역 재질", () =>
        {
            if (!known && !doc.Materials.Any(m => m.Id == asset.Id)) { doc.Materials.Add(asset); added = true; }
            if (doc.MaterialRegions.Count >= MaterialEditing.MaxRegions)
            {
                // Release just enough regions no layer uses, oldest first.
                var used = UsedMaterialRegions();
                var spare = doc.MaterialRegions.Where(r => !used.Contains(r.Id)).Take(doc.MaterialRegions.Count - MaterialEditing.MaxRegions + 1).Select(r => r.Id).ToHashSet();
                doc.MaterialRegions.RemoveAll(r => spare.Contains(r.Id));
            }
            var area = MaterialEditing.Region(doc, "선택 영역", boundary, "selection");
            doc.MaterialRegions.Add(area);
            double tile = MaterialEditing.DefaultTile(doc.Width, doc.Height, asset);
            var layer = MaterialEditing.Apply(doc, asset.Id, area.Id, tile, tile * MaterialEditing.Aspect(asset));
            layer.Name = layerName;
            var place = SelectionMaterials.Placement(doc, boundary.Bounds);
            if (place == null) layer.Category = LayerCategory.Photo;
            doc.Add(layer);
            if (place is { } found)
            {
                doc.Layers.Remove(layer);
                var spot = SelectionMaterials.Localize(doc, layer, found); layer.ParentId = spot.Parent;
                doc.Layers.Insert(Math.Clamp(spot.Index, 0, doc.Layers.Count), layer);
            }
            doc.ActiveId = layer.Id; selectedLayers.Clear(); selectedLayers.Add(layer.Id); sourceLayerSelection = null; maskEditing = false;
            created = layer;
        });
        if (created == null || !doc.Layers.Contains(created)) return null;
        selectionMaterialTarget = new(doc, region, created.Id, added ? asset.Id : null);
        BuildProperties();
        status.Text = $"재질 레이어를 만들었습니다: {displayName}";
        return created;
    }

    HashSet<Guid> UsedMaterialRegions() => doc.Layers.Select(l => l.Material?.SourceRegionId).OfType<Guid>().ToHashSet();

    // After a wand pick on a drawing, bring the properties tab forward so the swatches show.
    void RevealSelectionMaterials()
    {
        if (!HasDocument || selection == null || studioPage == 1 || studioPanes.Length < 2 || studioPanes[1].Location != "right" || !SelectionMaterials.IsDrawing(doc)) return;
        ShowStudioPage(1, false);
    }

    // Offscreen review (--render-studio-previews): a wand pick in a room of the sample plan,
    // the swatches it offers at two panel widths, then the plan after the first swatch.
    void RenderSelectionMaterialPreviews(string directory, Action<FrameworkElement, string, int, int> capturePane, Action<string, int, int> captureWindow)
    {
        string plan = Path.Combine(directory, "평면 예시.dxf");
        if (!File.Exists(plan)) return;
        var result = CompatibilityImport.ReadAsync(plan, new(CadLongEdge: 900, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: new CadCleanup()))
            .GetAwaiter().GetResult();
        AddTab(result.Document, null); SetTool(Tool.MagicWand);
        var picked = PrecisionWand.Select(doc, new Point(doc.Width * .86, doc.Height * .22), wandTolerance, true, true, false, 1);
        selection = picked with { Contour = SelectionContours.Create(picked) };
        Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); ShowStudioPage(1);
        captureWindow("selection-materials-window", 1480, 920);
        capturePane(studioPanes[1], "selection-materials", 360, 900);
        capturePane(studioPanes[1], "selection-materials-narrow", 300, 900);
        var first = SelectionMaterialChoices(SelectionMaterialSuggestion())[0];
        ApplySelectionMaterial(first.Asset, first.Name);
        composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        captureWindow("selection-materials-applied", 1480, 920);
        capturePane(studioPanes[1], "selection-materials-swap", 360, 900);
    }
}
