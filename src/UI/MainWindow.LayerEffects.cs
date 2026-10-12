using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 레이어 효과 in the properties panel: the effects of the active layer from the top (painted last) to
// the bottom, each with an on/off switch and its settings; + 효과 추가 and a ☰ menu (copy, paste, clear).
// Slider ticks change the effect for a live preview; one undo step is recorded when the change settles,
// like the material sliders. Also the 레이어 › 레이어 효과 menu, the quick action 획 추가 and the
// layer-row menu entries.
public sealed partial class MainWindow
{
    sealed record EffectPreviewState(Document Document, Document Before, string Label, Guid LayerId, LayerEffect[] Effects);
    EffectPreviewState? effectPreview;
    DispatcherTimer? effectPreviewTimer;
    // Copied effects (☰ › 효과 복사), pasted onto any layer of any open document.
    internal LayerEffect[]? copiedEffects;
    // The effect whose settings are open: one at a time, kept across panel rebuilds.
    Guid expandedEffectLayer; int expandedEffect = -1;
    // Self-tests answer the color picker instead of showing it.
    internal Func<Color, string, Color?>? effectColorHandler;

    static string EffectGlyph(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Theme.Glyphs.StrokeEffect, LayerEffectKind.DropShadow => Theme.Glyphs.Shadow, LayerEffectKind.OuterGlow => Theme.Glyphs.Glow,
        LayerEffectKind.InnerGlow => Theme.Glyphs.InnerGlow, LayerEffectKind.ColorOverlay => Theme.Glyphs.ColorFill, _ => Theme.Glyphs.GradientMap
    };

    static string EffectTip(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => "레이어 가장자리를 따라 테두리를 그립니다. 오린 사진의 흰 테두리, 강조한 글자에 씁니다.",
        LayerEffectKind.DropShadow => "레이어 아래에 부드러운 그림자를 드리웁니다. 레이어를 옮기면 함께 움직입니다.",
        LayerEffectKind.OuterGlow => "레이어 바깥으로 번지는 빛을 더합니다.",
        LayerEffectKind.InnerGlow => "레이어 안쪽 가장자리에서 번지는 빛을 더합니다.",
        LayerEffectKind.ColorOverlay => "레이어의 투명도를 그대로 두고 한 가지 색으로 다시 칠합니다. 오린 사진이나 아이콘의 색을 바꿀 때 씁니다.",
        _ => "레이어의 투명도를 그대로 두고 두 색의 그라데이션으로 다시 칠합니다."
    };

    // ---- Properties panel -------------------------------------------------------------------

    void AddLayerEffectsSection(Layer layer)
    {
        if (!LayerEffects.Supports(layer)) return;
        bool locked = IsLockedWithParents(layer);
        var boundDocument = doc; long version = inspectorVersion; var layerId = layer.Id;
        bool Current() => ReferenceEquals(doc, boundDocument) && inspectorVersion == version && doc.ActiveId == layerId && doc.Active is { } active && !IsLockedWithParents(active);
        properties.Children.Add(Theme.Section("레이어 효과"));
        var bar = new Grid { Margin = new Thickness(2, 0, 2, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button? add = null, more = null;
        add = QuickActions.Command(Theme.Glyphs.Plus, "효과 추가", () => Guard(() => { CommitFocusedInspectorField(); OpenEffectMenu(LayerEffectAddMenu(), add!); }),
            "획·그림자·외부 광선·내부 광선·색 오버레이·그라데이션 오버레이를 이 레이어에 더합니다.", "레이어 효과 추가");
        add.Margin = new Thickness(0); add.IsEnabled = !locked;
        more = Theme.IconButton(Theme.Glyphs.Menu, () => Guard(() => { CommitFocusedInspectorField(); OpenEffectMenu(LayerEffectMenu(), more!); }), "레이어 효과 메뉴 · 복사 · 붙여넣기 · 모두 지우기", 34, 16);
        more.Margin = new Thickness(6, 0, 0, 0); more.VerticalAlignment = VerticalAlignment.Stretch; Grid.SetColumn(more, 1);
        bar.Children.Add(add); bar.Children.Add(more);
        properties.Children.Add(bar);
        var effects = layer.Effects ?? [];
        if (effects.Length == 0)
        {
            var hint = Density.Mark(Theme.Label("획·광선·그림자·색을 레이어에 더합니다. 효과는 레이어를 옮기거나 고쳐도 따라가고 언제든 바꿀 수 있습니다.", Theme.CaptionSize, Theme.Muted), DensityRole.Description);
            hint.Margin = new Thickness(2, 0, 2, 8); properties.Children.Add(hint);
        }
        // The list reads like the layer list: the effect painted last (on top) first.
        for (int index = effects.Length - 1; index >= 0; index--) properties.Children.Add(EffectRow(layer, index, effects[index], effects.Length, locked, Current));
        // A shadow layer made from this layer (그림자 추가…) belongs to its look too.
        foreach (var shadow in doc.Layers.Where(l => l.Shadow is { } spec && spec.Sources.Contains(layerId)).ToArray())
        {
            var shadowId = shadow.Id;
            properties.Children.Add(Theme.ActionRow("연결된 그림자 레이어 선택", () => Guard(() => SelectLayer(shadowId)),
                "이 레이어가 드리운 그림자 레이어를 선택합니다. 그 레이어에서 그림자를 편집하거나 원본에 맞춰 다시 만듭니다.", Theme.Glyphs.Shadow));
        }
    }

    FrameworkElement EffectRow(Layer layer, int index, LayerEffect effect, int count, bool locked, Func<bool> current)
    {
        bool expanded = expandedEffectLayer == layer.Id && expandedEffect == index;
        string name = LayerEffects.Name(effect.Kind);
        var card = new Border
        {
            BorderBrush = expanded ? Theme.Stroke : Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Theme.Compact ? 3 : 8),
            Margin = new Thickness(2, 0, 2, 6), Padding = new Thickness(6, 2, 2, expanded ? 6 : 2), Background = expanded ? Theme.Input : Brushes.Transparent
        };
        var stack = new StackPanel(); card.Child = stack;
        var header = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var toggle = new CheckBox { IsChecked = effect.Enabled, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !locked, Margin = new Thickness(0, 0, 2, 0), ToolTip = "효과 켜기 · 끄기" };
        AutomationProperties.SetName(toggle, $"{name} 켜기");
        toggle.Click += (_, _) => { if (current()) Guard(() => EditEffect(toggle.IsChecked == true ? "레이어 효과 켜기" : "레이어 효과 끄기", index, e => e with { Enabled = toggle.IsChecked == true })); };
        header.Children.Add(toggle);
        // The name and its summary open the settings.
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Theme.Glyph(EffectGlyph(effect.Kind), 16, effect.Enabled ? Theme.Text : Theme.Subtle); icon.Margin = new Thickness(2, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 4, 3) };
        var caption = Theme.Label(name, Theme.BodySize, effect.Enabled ? Theme.Text : Theme.Muted); caption.Margin = new Thickness(0); caption.FontWeight = FontWeights.SemiBold;
        var summary = Theme.Label(LayerEffects.Summary(effect), Theme.CaptionSize, Theme.Muted); summary.Margin = new Thickness(0, 1, 0, 0);
        summary.TextWrapping = TextWrapping.NoWrap; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        labels.Children.Add(caption); labels.Children.Add(summary);
        var content = new DockPanel(); DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon); content.Children.Add(labels);
        var open = new Button { Content = content, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = $"{EffectTip(effect.Kind)}\n{(expanded ? "설정 닫기" : "설정 열기")}", Focusable = true };
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border)); surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        surface.AppendChild(presenter); template.VisualTree = surface; open.Template = template;
        AutomationProperties.SetName(open, $"{name} 설정");
        void Expand() { CommitFocusedInspectorField(); CommitEffectPreview(true); if (expanded) expandedEffect = -1; else { expandedEffectLayer = layer.Id; expandedEffect = index; } BuildProperties(); }
        open.Click += (_, _) => Guard(Expand);
        Grid.SetColumn(open, 1); header.Children.Add(open);
        var chevron = Theme.IconButton(expanded ? Theme.Glyphs.ChevronDown : Theme.Glyphs.ChevronRight, () => Guard(Expand), expanded ? $"{name} 설정 닫기" : $"{name} 설정 열기", 26, 14);
        chevron.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(chevron, 2); header.Children.Add(chevron);
        Button? actions = null;
        actions = Theme.IconButton(Theme.Glyphs.More, () => Guard(() =>
        {
            var menu = new ContextMenu();
            void Item(string label, bool enabled, Action run) { var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Click += (_, _) => { if (current()) Guard(run); }; menu.Items.Add(item); }
            Item("위로 (앞으로)", index < count - 1, () => MoveLayerEffect(index, 1));
            Item("아래로 (뒤로)", index > 0, () => MoveLayerEffect(index, -1));
            Item("복제", count < LayerEffect.MaxCount, () => EditEffects("레이어 효과 복제", list => [.. list![..(index + 1)], list[index], .. list[(index + 1)..]], index + 1));
            menu.Items.Add(new Separator());
            Item("효과 삭제", true, () => RemoveLayerEffect(index));
            OpenEffectMenu(menu, actions!);
        }), $"{name} · 순서 바꾸기 · 복제 · 삭제", 26, 14);
        actions.IsEnabled = !locked; actions.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(actions, 3); header.Children.Add(actions);
        stack.Children.Add(header);
        if (expanded)
        {
            var settings = new StackPanel { Margin = new Thickness(0, 6, 4, 0), IsEnabled = !locked };
            EffectControls(settings, index, effect, current, summary);
            stack.Children.Add(settings);
        }
        return card;
    }

    void EffectControls(StackPanel panel, int index, LayerEffect effect, Func<bool> current, TextBlock summary)
    {
        void Slider(string label, double min, double max, double value, double reset, string history, Func<LayerEffect, double, LayerEffect> change, string? tip = null, bool logarithmic = false)
        {
            var slider = new ParameterSlider(label, min, max, Math.Clamp(value, min, max), reset, logarithmic: logarithmic) { ToolTip = tip };
            slider.Changed += v => { if (!current()) return; PreviewEffectEdit(history, index, e => change(e, v)); summary.Text = LayerEffects.Summary(change(effect, v)); };
            panel.Children.Add(slider);
        }
        void ColorRow(string caption, uint argb, string history, Func<LayerEffect, uint, LayerEffect> change)
        {
            var chip = PropertyRows.ColorChip(DocumentFeatures.Color(argb), $"#{argb & 0xFFFFFF:X6}", () =>
            {
                if (!current() || PickEffectColor(DocumentFeatures.Color(argb), caption) is not { } picked || !current()) return;
                Guard(() => EditEffect(history, index, e => change(e, VectorShapes.Argb(picked) | 0xFF000000)));
            }, $"{caption} 변경");
            panel.Children.Add(PropertyRows.Field(caption, chip, new Thickness(0, 0, 0, 8)));
        }
        void Choice<T>(string caption, (T Value, string Caption)[] options, T value, string history, Func<LayerEffect, T, LayerEffect> change)
        {
            var choice = new SegmentedChoice<T>(options, value);
            choice.Changed += v => { if (current()) Guard(() => EditEffect(history, index, e => change(e, v))); };
            panel.Children.Add(PropertyRows.Field(caption, choice, new Thickness(0, 0, 0, 8)));
        }
        void Blend()
        {
            var blends = LayerEffect.BlendsFor(effect.Kind);
            if (blends.Length > 1) Choice("혼합", blends.Select(b => (b, BlendLabel(b))).ToArray(), effect.Blend, "레이어 효과 혼합", (e, v) => e with { Blend = v });
        }
        void Opacity() => Slider("불투명도 %", 0, 100, effect.Opacity * 100, LayerEffect.Default(effect.Kind).Opacity * 100, "레이어 효과 불투명도", (e, v) => e with { Opacity = v / 100 });
        var defaults = LayerEffect.Default(effect.Kind);
        switch (effect.Kind)
        {
            case LayerEffectKind.Stroke:
                ColorRow("획 색", effect.ColorArgb, "획 색", (e, c) => e with { ColorArgb = c });
                Slider("크기 px", .5, LayerEffect.MaxSize, effect.Size, defaults.Size, "획 크기", (e, v) => e with { Size = v }, "획의 두께입니다. 레이어의 크기를 바꿔도 이 두께를 유지합니다.", true);
                Choice("위치", [(StrokePosition.Outside, "바깥쪽"), (StrokePosition.Center, "가운데"), (StrokePosition.Inside, "안쪽")], effect.Position, "획 위치", (e, v) => e with { Position = v });
                Opacity();
                var rounded = new CheckBox { Content = "둥근 모서리", IsChecked = effect.Rounded, Foreground = Theme.Text, Margin = new Thickness(1, 0, 0, 8), ToolTip = "끄면 모서리를 둥글리지 않고 각지게 둡니다." };
                rounded.Click += (_, _) => { if (current()) Guard(() => EditEffect("획 모서리", index, e => e with { Rounded = rounded.IsChecked == true })); };
                panel.Children.Add(rounded);
                break;
            case LayerEffectKind.DropShadow:
                ColorRow("그림자 색", effect.ColorArgb, "그림자 색", (e, c) => e with { ColorArgb = c });
                Opacity();
                Slider("거리 px", 0, 300, effect.Distance, defaults.Distance, "그림자 거리", (e, v) => e with { Distance = v }, "레이어에서 그림자까지의 거리입니다.");
                Slider("각도 °", -180, 180, NormalizeAngle(effect.Angle), defaults.Angle, "그림자 각도", (e, v) => e with { Angle = v }, "그림자가 떨어지는 방향입니다. 0°는 오른쪽, 90°는 아래쪽입니다.");
                Slider("부드러움 px", 0, 150, effect.Size, defaults.Size, "그림자 부드러움", (e, v) => e with { Size = v }, "그림자 가장자리가 퍼지는 거리입니다.");
                Slider("퍼짐 %", 0, 100, effect.Spread * 100, defaults.Spread * 100, "그림자 퍼짐", (e, v) => e with { Spread = v / 100 }, "부드러움 가운데 진하게 남는 부분의 비율입니다.");
                Blend();
                break;
            case LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow:
                ColorRow("빛 색", effect.ColorArgb, "광선 색", (e, c) => e with { ColorArgb = c });
                Opacity();
                Slider("크기 px", 1, LayerEffect.MaxSize, Math.Max(1, effect.Size), defaults.Size, "광선 크기", (e, v) => e with { Size = v }, effect.Kind == LayerEffectKind.OuterGlow ? "빛이 레이어 바깥으로 번지는 거리입니다." : "빛이 가장자리에서 안쪽으로 번지는 거리입니다.", true);
                Slider("퍼짐 %", 0, 100, effect.Spread * 100, defaults.Spread * 100, "광선 퍼짐", (e, v) => e with { Spread = v / 100 }, "크기 가운데 진하게 남는 부분의 비율입니다.");
                Blend();
                break;
            case LayerEffectKind.ColorOverlay:
                ColorRow("덮을 색", effect.ColorArgb, "오버레이 색", (e, c) => e with { ColorArgb = c });
                Opacity(); Blend();
                break;
            default:
                ColorRow("시작 색", effect.ColorArgb, "그라데이션 시작 색", (e, c) => e with { ColorArgb = c });
                ColorRow("끝 색", effect.EndColorArgb, "그라데이션 끝 색", (e, c) => e with { EndColorArgb = c });
                Slider("방향 °", -180, 180, NormalizeAngle(effect.Angle), defaults.Angle, "그라데이션 방향", (e, v) => e with { Angle = v }, "색이 바뀌는 방향입니다. 0°는 왼쪽에서 오른쪽, 90°는 위에서 아래입니다.");
                Opacity(); Blend();
                break;
        }
    }

    Color? PickEffectColor(Color initial, string label)
    {
        if (effectColorHandler != null) return effectColorHandler(initial, label);
        var dialog = new ColorPickerDialog(this, initial, label);
        return dialog.ShowDialog() == true ? dialog.SelectedColor : null;
    }

    void OpenEffectMenu(ContextMenu menu, UIElement target)
    {
        if (headlessTesting) return;
        menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    internal ContextMenu LayerEffectAddMenu()
    {
        var menu = new ContextMenu();
        foreach (var kind in LayerEffects.MenuOrder)
        {
            var item = new MenuItem { Header = LayerEffects.Name(kind), Icon = Theme.Glyph(EffectGlyph(kind), 16, Theme.Muted), ToolTip = EffectTip(kind) };
            var chosen = kind; item.Click += (_, _) => Guard(() => AddLayerEffect(chosen));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        // The cast shadow with plan and ground projections stays its own layer (그림자 추가…).
        var shadow = new MenuItem { Header = "그림자 레이어 만들기…", Icon = Theme.Glyph(Theme.Glyphs.Shadow, 16, Theme.Muted), ToolTip = "배치도의 평면 그림자나 바닥에 드리운 그림자를 따로 고칠 수 있는 레이어로 만듭니다." };
        shadow.Click += (_, _) => Guard(AddShadow);
        menu.Items.Add(shadow);
        return menu;
    }

    internal ContextMenu LayerEffectMenu()
    {
        var menu = new ContextMenu(); bool any = doc.Active?.Effects != null;
        void Item(string label, bool enabled, Action run) { var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Click += (_, _) => Guard(run); menu.Items.Add(item); }
        Item("효과 복사", any, CopyLayerEffects);
        Item("효과 붙여넣기", copiedEffects != null, PasteLayerEffects);
        menu.Items.Add(new Separator());
        Item("효과 모두 지우기", any, ClearLayerEffects);
        return menu;
    }

    void AddLayerEffectMenuItems(MenuItem layerMenu)
    {
        var effects = DocumentControl(new MenuItem { Header = "레이어 효과", Foreground = Theme.Text });
        foreach (var (label, action) in new (string, Action)[] { ("획 추가", AddStrokeEffect), ("레이어 효과 복사", CopyLayerEffects), ("레이어 효과 붙여넣기", PasteLayerEffects), ("레이어 효과 지우기", ClearLayerEffects) })
        {
            var item = DocumentControl(new MenuItem { Header = label, Foreground = Theme.Text });
            item.Click += (_, _) => { if (HasDocument) Guard(action); };
            effects.Items.Add(item);
        }
        layerMenu.Items.Add(effects);
    }

    // ---- Edits ------------------------------------------------------------------------------

    bool CanEditEffects(out Layer layer)
    {
        layer = doc.Active!;
        if (!HasDocument || doc.Active is not { } active) { status.Text = "레이어를 먼저 선택하세요."; return false; }
        layer = active;
        if (!LayerEffects.Supports(active)) { status.Text = "그룹과 조정 레이어에는 레이어 효과를 둘 수 없습니다. 이미지·텍스트·도형 레이어를 선택하세요."; return false; }
        if (IsLockedWithParents(active)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return false; }
        return true;
    }

    // One undo step on the active layer's list; `expand` opens that effect's settings afterwards.
    void EditEffects(string label, Func<LayerEffect[]?, LayerEffect[]?> change, int expand = -2)
    {
        CommitEffectPreview(true);
        if (!CanEditEffects(out var layer)) return;
        var id = layer.Id;
        Edit(label, () =>
        {
            var target = doc.Layers.Single(l => l.Id == id);
            target.Effects = LayerEffects.Normalize(change(target.Effects));
            if (expand != -2) { expandedEffectLayer = id; expandedEffect = expand; }
        });
    }

    void EditEffect(string label, int index, Func<LayerEffect, LayerEffect> change) =>
        EditEffects(label, list => list != null && index < list.Length ? LayerEffects.Replace(list, index, change(list[index])) : list);

    internal void AddLayerEffect(LayerEffectKind kind, LayerEffect? effect = null)
    {
        CommitFocusedInspectorField();
        if (!CanEditEffects(out var layer)) return;
        if ((layer.Effects?.Length ?? 0) >= LayerEffect.MaxCount) { status.Text = $"레이어 효과는 레이어마다 {LayerEffect.MaxCount}개까지 둘 수 있습니다."; return; }
        var added = effect ?? LayerEffect.Default(kind);
        var next = LayerEffects.Add(layer.Effects, added);
        EditEffects($"{LayerEffects.Name(kind)} 추가", _ => next, Array.IndexOf(next, added));
        status.Text = $"{LayerEffects.Name(kind)} 효과를 더했습니다 · 속성 패널의 레이어 효과에서 조절합니다.";
    }

    /// <summary>획 추가: a white outside stroke sized to the layer, for cut-outs, titles and icons.</summary>
    internal void AddStrokeEffect()
    {
        if (!HasDocument || doc.Active is not { } layer) { status.Text = "획을 더할 이미지·텍스트·도형 레이어를 선택하세요."; return; }
        double size = Math.Clamp(Math.Round(Math.Max(layer.Pixels.Width * Math.Abs(layer.Scale * layer.ScaleX), layer.Pixels.Height * Math.Abs(layer.Scale * layer.ScaleY)) * .012), 3, 40);
        AddLayerEffect(LayerEffectKind.Stroke, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = size });
    }

    void RemoveLayerEffect(int index)
    {
        EditEffects("레이어 효과 삭제", list => list == null || index >= list.Length ? list : LayerEffects.Remove(list, index), -1);
    }

    // The open effect stays open where it moves to.
    void MoveLayerEffect(int index, int delta)
    {
        int next = index + delta, open = expandedEffect == index ? next : expandedEffect == next ? index : -2;
        EditEffects("레이어 효과 순서", list => list == null || index >= list.Length ? list : LayerEffects.Move(list, index, delta), open);
    }

    internal void CopyLayerEffects()
    {
        if (!HasDocument || doc.Active?.Effects is not { } effects) { status.Text = "복사할 레이어 효과가 없습니다."; return; }
        copiedEffects = effects; status.Text = $"레이어 효과 {effects.Length}개를 복사했습니다 · 다른 레이어를 고르고 붙여넣으세요.";
    }

    // Pastes onto every selected layer that can hold effects (their own effects are replaced).
    internal void PasteLayerEffects()
    {
        CommitEffectPreview(true); CommitFocusedInspectorField();
        if (!HasDocument || copiedEffects is not { } effects) { status.Text = "먼저 레이어 효과를 복사하세요."; return; }
        var ids = ExportSelectionIds().ToHashSet();
        var targets = doc.Layers.Where(l => ids.Contains(l.Id) && LayerEffects.Supports(l)).ToArray();
        if (targets.Length == 0) { status.Text = "효과를 붙여넣을 이미지·텍스트·도형 레이어를 선택하세요."; return; }
        if (targets.Any(IsLockedWithParents)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        var targetIds = targets.Select(l => l.Id).ToHashSet();
        Edit("레이어 효과 붙여넣기", () => { foreach (var layer in doc.Layers.Where(l => targetIds.Contains(l.Id))) layer.Effects = effects; });
        status.Text = $"레이어 {targets.Length}개에 효과를 붙여넣었습니다.";
    }

    internal void ClearLayerEffects()
    {
        if (!HasDocument || doc.Active?.Effects == null) { status.Text = "지울 레이어 효과가 없습니다."; return; }
        EditEffects("레이어 효과 지우기", _ => null, -1);
    }

    // ---- Live preview -----------------------------------------------------------------------

    // Every tick recomputes from the effect before the gesture (no compounding) and renders it.
    internal void PreviewEffectEdit(string label, int index, Func<LayerEffect, LayerEffect> change)
    {
        if (!HasDocument || doc.Active is not { } layer || IsLockedWithParents(layer)) return;
        // Another setting of the panel: the previous change becomes its own step; the panel stays.
        if (effectPreview is { } pending && (!ReferenceEquals(pending.Document, doc) || pending.Label != label || pending.LayerId != layer.Id)) CommitEffectPreview(true, rebuildInspector: false);
        if (effectPreview == null)
        {
            if (layer.Effects is not { } list || index >= list.Length) return;
            effectPreview = new(doc, doc.Snapshot(), label, layer.Id, list);
        }
        var before = effectPreview.Effects;
        LayerEffect next;
        try { next = change(before[index]); next.Validate(); }
        catch (InvalidDataException error) { status.Text = error.Message; return; }
        layer.Effects = LayerEffects.Replace(before, index, next);
        RenderGesture();
        effectPreviewTimer ??= CreateEffectPreviewTimer();
        effectPreviewTimer.Stop(); effectPreviewTimer.Start();
        pendingInspectorCommit = CommitEffectPreview;
    }

    DispatcherTimer CreateEffectPreviewTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => CommitEffectPreview();
        return timer;
    }

    void CommitEffectPreview() => CommitEffectPreview(false);

    // One undo step for the whole drag. While the slider is still held the commit waits (the panel is not
    // rebuilt under the pointer); a forced commit from another setting keeps the panel.
    internal void CommitEffectPreview(bool force, bool rebuildInspector = true)
    {
        effectPreviewTimer?.Stop();
        if (effectPreview is not { } preview) return;
        if (!force && InspectorHoldsPointer()) { effectPreviewTimer?.Start(); pendingInspectorCommit = CommitEffectPreview; return; }
        effectPreview = null;
        if (pendingInspectorCommit is { } commit && commit.Target == this && commit.Method.Name == nameof(CommitEffectPreview)) pendingInspectorCommit = null;
        if (!ReferenceEquals(preview.Document, doc))
        {
            // The document changed under the preview: put the effects back rather than leave an unrecorded edit.
            if (preview.Document.Layers.FirstOrDefault(l => l.Id == preview.LayerId) is { } layer) layer.Effects = preview.Effects;
            return;
        }
        try { doc.Validate(); history.Commit(preview.Label, preview.Before, doc); Refresh(rebuildProperties: rebuildInspector); }
        catch (Exception error) { doc = preview.Before; Refresh(); if (headlessTesting) throw; MessageDialog.Show(this, error.Message); }
    }
}
