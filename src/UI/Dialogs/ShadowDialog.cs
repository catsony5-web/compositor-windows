using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Shadow settings with a live preview of the whole document. The source coverage is rendered once
/// per preview scale; parameter changes only recast the shadow and recomposite, and each new request
/// cancels the previous one.
/// </summary>
public sealed class ShadowDialog : Window
{
    public const double PreviewLongSide = 1100;
    // North-up drawings: morning sun from the east-southeast, noon from the south, afternoon from the west-southwest.
    internal static readonly (string Label, double DropAngle, double Angle, double Elevation)[] SunPresets =
        [("오전", 135, 210, 30), ("정오", 90, 270, 62), ("오후", 45, 330, 30)];

    public ShadowSpec Spec { get; private set; }
    readonly Document original;
    readonly Guid? editingId;
    readonly double scale;
    readonly int previewWidth, previewHeight;
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(14) };
    readonly TextBlock info = DialogShell.Note(""), styleNote = DialogShell.Note(""), projectionNote = DialogShell.Note("");
    readonly SemaphoreSlim renderGate = new(1, 1);
    readonly Dictionary<bool, ShadowSilhouette> silhouettes = [];
    readonly List<Action> synchronize = [];
    readonly List<ParameterSlider> sliders = [];
    readonly ParameterSlider distance, height, elevation, softness, outlineWidth;
    readonly CheckBox fillClosed, outlineOnly;
    readonly Border swatch = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    CancellationTokenSource? previewCts;
    long version;
    bool closed, synchronizing;

    internal SegmentedChoice<ShadowStyle> StyleChoice { get; }
    internal SegmentedChoice<ShadowProjection> ProjectionChoice { get; }
    internal IReadOnlyList<ParameterSlider> Sliders => sliders;
    internal Raster? PreviewRaster { get; private set; }
    internal string Info => info.Text;

    public ShadowDialog(Window? owner, Document document, ShadowSpec initial, Guid? editingId = null)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(initial);
        initial.Validate(); Spec = initial; original = document.Snapshot(); this.editingId = editingId;
        scale = Math.Min(1, PreviewLongSide / Math.Max(original.Width, original.Height));
        previewWidth = Math.Max(1, (int)Math.Round(original.Width * scale)); previewHeight = Math.Max(1, (int)Math.Round(original.Height * scale));
        string caption = editingId == null ? "그림자 추가" : "그림자 편집";
        DialogShell.Prepare(this, owner, caption);
        Width = 1080; Height = 800; MinWidth = 840; MinHeight = 560;

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(376) });
        var card = DialogShell.Card(preview); card.Margin = new Thickness(0, 0, 18, 0); root.Children.Add(card);
        var side = new Grid(); side.RowDefinitions.Add(new RowDefinition()); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(side, 1); root.Children.Add(side);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        side.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        panel.Children.Add(DialogShell.Title(caption));
        var names = initial.Sources.Select(id => original.Layers.Find(l => l.Id == id)?.Name).OfType<string>().ToArray();
        if (names.Length > 0)
        {
            var subtitle = Loc.Keep(DialogShell.Subtitle(string.Join(", ", names.Take(3)) + (names.Length > 3 ? $" +{names.Length - 3}" : "")));
            subtitle.TextWrapping = TextWrapping.Wrap; panel.Children.Add(subtitle);
        }

        panel.Children.Add(DialogShell.FieldLabel("그림자 종류"));
        StyleChoice = new SegmentedChoice<ShadowStyle>([(ShadowStyle.Realistic, "사실적인 그림자"), (ShadowStyle.Shape, "그림자 형태")], initial.Style);
        StyleChoice.Changed += style => { Spec = Spec with { Style = style }; UpdateOptions(); Schedule(); };
        panel.Children.Add(StyleChoice); panel.Children.Add(styleNote);

        panel.Children.Add(DialogShell.FieldLabel("드리우는 방식"));
        ProjectionChoice = new SegmentedChoice<ShadowProjection>([(ShadowProjection.Drop, "띄운 그림자"), (ShadowProjection.Plan, "평면 그림자"), (ShadowProjection.Ground, "바닥에 드리우기")], initial.Projection);
        ProjectionChoice.Changed += projection =>
        {
            // Keep a direction the user chose; replace only the previous projection's default.
            double angle = Spec.Angle == ShadowSpec.Default(Spec.Projection).Angle ? ShadowSpec.Default(projection).Angle : Spec.Angle;
            Spec = Spec with { Projection = projection, Angle = angle }; SyncControls(); UpdateOptions(); Schedule();
        };
        panel.Children.Add(ProjectionChoice); panel.Children.Add(projectionNote);

        panel.Children.Add(DialogShell.FieldLabel("태양 위치"));
        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(-2, 0, -2, 4) };
        foreach (var preset in SunPresets)
        {
            var button = Theme.Button(preset.Label, () => ApplyPreset(preset.Label), "북쪽이 위인 도면 기준 · 방향과 태양 높이를 바꿉니다.");
            button.MinHeight = Theme.ControlHeight; button.Margin = new Thickness(2); presets.Children.Add(button);
        }
        panel.Children.Add(presets);

        double longest = Math.Clamp(Math.Max(original.Width, original.Height), 200, ShadowSpec.MaxLength);
        ParameterSlider Slider(string label, double min, double max, Func<ShadowSpec, double> get, Func<ShadowSpec, double, ShadowSpec> set, string tip)
        {
            var control = new ParameterSlider(label, min, Math.Max(max, get(initial)), get(initial), get(ShadowSpec.Default(initial.Projection, initial.Style))) { ToolTip = tip };
            control.Changed += value => { if (synchronizing) return; Spec = set(Spec, value); Schedule(); };
            synchronize.Add(() => control.SetValue(get(Spec)));
            sliders.Add(control); panel.Children.Add(control); return control;
        }
        Slider("방향 °", 0, 360, s => (s.Angle % 360 + 360) % 360, (s, v) => s with { Angle = v }, "그림자가 떨어지는 방향 · 0° 오른쪽, 90° 아래, 180° 왼쪽, 270° 위");
        distance = Slider("거리 px", 0, Math.Min(longest, 2000), s => s.Distance, (s, v) => s with { Distance = v }, "물체에서 그림자까지의 거리");
        height = Slider("높이 px", 0, longest, s => s.Height, (s, v) => s with { Height = v }, "도면에서 건물·물체의 높이(도면 픽셀) · 그림자 길이 = 높이 ÷ tan(태양 높이)");
        elevation = Slider("태양 높이 °", ShadowSpec.MinElevation, ShadowSpec.MaxElevation, s => s.Elevation, (s, v) => s with { Elevation = v }, "태양이 낮을수록 그림자가 길어집니다.");
        softness = Slider("부드러움 px", 0, 200, s => s.Softness, (s, v) => s with { Softness = v }, "그림자 끝의 흐림 · 물체에 닿은 곳은 선명하게 유지됩니다.");
        Slider("불투명도 %", 0, 100, s => s.Opacity * 100, (s, v) => s with { Opacity = v / 100 }, "그림자의 진하기");

        var colorContent = new StackPanel { Orientation = Orientation.Horizontal };
        colorContent.Children.Add(swatch); colorContent.Children.Add(new TextBlock { Text = "그림자 색상 선택…", VerticalAlignment = VerticalAlignment.Center });
        var color = Theme.Button("", ChooseColor, "그림자 색상 · 곱하기 혼합으로 아래 색과 섞입니다.");
        color.Content = colorContent; color.HorizontalAlignment = HorizontalAlignment.Left; color.HorizontalContentAlignment = HorizontalAlignment.Left;
        color.MinHeight = Theme.ControlHeight; color.Padding = new Thickness(8, 4, 12, 4); color.Margin = new Thickness(0, 4, 0, 8);
        System.Windows.Automation.AutomationProperties.SetName(color, "그림자 색상 선택");
        panel.Children.Add(color);

        fillClosed = Option("닫힌 윤곽 안쪽 채우기", initial.FillClosed, "선으로만 그린 건물 윤곽도 채워진 면으로 보고 그림자를 만듭니다.", on => Spec = Spec with { FillClosed = on });
        panel.Children.Add(fillClosed);
        outlineOnly = Option("윤곽선만 그리기", initial.OutlineOnly, "그림자 모양의 테두리만 선으로 표시합니다.", on => { Spec = Spec with { OutlineOnly = on }; UpdateOptions(); });
        panel.Children.Add(outlineOnly);
        outlineWidth = Slider("윤곽선 두께 px", .5, 20, s => s.OutlineWidth, (s, v) => s with { OutlineWidth = v }, "그림자 윤곽선의 두께");

        info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(1, 10, 1, 4);
        System.Windows.Automation.AutomationProperties.SetName(info, "미리보기 상태"); panel.Children.Add(info);

        var cancel = DialogShell.Secondary("취소", Close); cancel.IsCancel = true;
        var apply = DialogShell.Primary(editingId == null ? "그림자 만들기" : "적용", () => { if (TryCommit()) DialogResult = true; }); apply.IsDefault = true;
        var footer = DialogShell.Footer(cancel, apply); footer.Margin = new Thickness(0, 14, 8, 0); Grid.SetRow(footer, 1); side.Children.Add(footer);

        UpdateColor(); UpdateOptions();
        Loaded += (_, _) => Schedule();
        Closed += (_, _) => { closed = true; version++; previewCts?.Cancel(); };
    }

    CheckBox Option(string label, bool value, string tip, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = Theme.Text, Margin = new Thickness(1, 6, 1, 4), ToolTip = tip };
        void Changed() { if (synchronizing) return; set(box.IsChecked == true); Schedule(); }
        box.Checked += (_, _) => Changed(); box.Unchecked += (_, _) => Changed();
        return box;
    }

    internal void ApplyPreset(string label)
    {
        var preset = SunPresets.Single(p => p.Label == label);
        Spec = Spec.Projection == ShadowProjection.Drop ? Spec with { Angle = preset.DropAngle } : Spec with { Angle = preset.Angle, Elevation = preset.Elevation };
        SyncControls(); Schedule();
    }

    void ChooseColor()
    {
        var picked = Dialogs.ColorPicker(this, DocumentFeatures.Color(Spec.ColorArgb), "그림자 색상");
        if (picked is not { } c) return;
        Spec = Spec with { ColorArgb = VectorShapes.Argb(c) }; UpdateColor(); Schedule();
    }

    void UpdateColor()
    {
        var c = DocumentFeatures.Color(Spec.ColorArgb);
        swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }

    void UpdateOptions()
    {
        bool realistic = Spec.Style == ShadowStyle.Realistic;
        styleNote.Text = realistic ? "물체에 닿은 곳은 선명하고, 멀어질수록 부드럽게 퍼집니다." : "그림자가 드리우는 모양을 또렷한 면이나 윤곽선으로 보여 줍니다. 일조 검토와 모양 다듬기에 알맞습니다.";
        projectionNote.Text = Spec.Projection switch
        {
            ShadowProjection.Plan => "배치도·평면의 건물을 높이만큼 늘여 바닥에 드리웁니다.",
            ShadowProjection.Ground => "서 있는 사람·나무의 그림자를 발밑에서 바닥으로 눕힙니다.",
            _ => "보드 위 사진·오려낸 사람·나무를 살짝 띄운 듯이 그림자를 둡니다."
        };
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        distance.Visibility = Show(Spec.Projection == ShadowProjection.Drop);
        height.Visibility = Show(Spec.Projection == ShadowProjection.Plan);
        elevation.Visibility = Show(Spec.Projection != ShadowProjection.Drop);
        softness.Visibility = Show(realistic);
        outlineOnly.Visibility = Show(!realistic);
        outlineWidth.Visibility = Show(!realistic && Spec.OutlineOnly);
    }

    void SyncControls()
    {
        synchronizing = true;
        try { foreach (var sync in synchronize) sync(); fillClosed.IsChecked = Spec.FillClosed; outlineOnly.IsChecked = Spec.OutlineOnly; }
        finally { synchronizing = false; }
    }

    /// <summary>Commits typed numbers; false keeps the dialog open with the invalid field marked.</summary>
    internal bool TryCommit()
    {
        bool valid = true;
        foreach (var slider in sliders.Where(s => s.IsVisible || s.Visibility == Visibility.Visible)) valid &= slider.TryCommit();
        if (!valid) { info.Text = "표시된 범위 안의 숫자를 입력해 주세요."; return false; }
        try { Spec.Validate(); return true; }
        catch (Exception e) { info.Text = e.Message; return false; }
    }

    async void Schedule()
    {
        if (closed || !IsLoaded) return;
        long generation = ++version; var spec = Spec;
        previewCts?.Cancel(); var cts = previewCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(90, cts.Token);
            if (generation != version || closed) return;
            info.Text = "미리보기 계산 중…";
            await renderGate.WaitAsync(cts.Token);
            (Raster Image, Raster Display) result;
            try { result = await CompatibilityImport.OnSta(() => RenderPreview(spec, cts.Token), cts.Token); }
            finally { renderGate.Release(); }
            if (generation != version || closed) return;
            Show(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == version && !closed) info.Text = e.Message; }
        finally { if (ReferenceEquals(previewCts, cts)) previewCts = null; cts.Dispose(); }
    }

    /// <summary>Renders the current settings synchronously (offscreen captures and self-tests).</summary>
    internal void RefreshPreviewNow() => Show(RenderPreview(Spec, CancellationToken.None));

    void Show((Raster Image, Raster Display) result)
    {
        PreviewRaster = result.Image; preview.Source = result.Display.Bitmap();
        info.Text = $"미리보기 · {previewWidth:N0} × {previewHeight:N0} px";
    }

    (Raster Image, Raster Display) RenderPreview(ShadowSpec spec, CancellationToken token)
    {
        bool separate = spec.Projection == ShadowProjection.Ground;
        ShadowSilhouette? silhouette;
        lock (silhouettes) silhouettes.TryGetValue(separate, out silhouette);
        if (silhouette == null)
        {
            silhouette = ShadowRenderer.Silhouette(original, spec.Sources, scale, separate, token);
            lock (silhouettes) silhouettes[separate] = silhouette;
        }
        var alpha = ShadowRenderer.Cast(silhouette, spec, token);
        var document = PreviewDocument(original, editingId, spec, alpha, silhouette);
        var image = DesignRenderer.Render(document, new Rect(0, 0, original.Width, original.Height), previewWidth, previewHeight, token);
        return (image, OverChecker(image));
    }

    /// <summary>The document with the previewed shadow in place of (or below the sources as) the shadow layer.</summary>
    internal static Document PreviewDocument(Document original, Guid? editingId, ShadowSpec spec, AlphaMap? alpha, ShadowSilhouette silhouette)
    {
        var document = original.Snapshot();
        Layer layer;
        if (editingId is { } id && document.Layers.Find(l => l.Id == id) is { } existing)
        {
            layer = existing;
            if (layer.ParentId != silhouette.ParentId)
            {
                document.Layers.Remove(layer); layer.ParentId = silhouette.ParentId; layer.Clipped = false;
                document.Layers.Insert(ShadowRenderer.InsertionIndex(document, spec.Sources), layer);
            }
        }
        else
        {
            layer = new Layer { Name = Loc.T("그림자"), Blend = BlendMode.Multiply, ParentId = silhouette.ParentId, Pixels = new Raster(1, 1) };
            document.Layers.Insert(ShadowRenderer.InsertionIndex(document, spec.Sources), layer);
        }
        if (ShadowRenderer.Colorize(alpha, spec) is { } colored)
        {
            // Preview pixels are at the preview scale; the layer transform maps them back to full size.
            double inverse = 1 / silhouette.Scale, common = Math.Min(20, inverse);
            layer.Pixels = colored.Pixels; layer.Mask = null; layer.Warp = null; layer.Rotation = 0; layer.FlipX = layer.FlipY = false;
            layer.Scale = common; layer.ScaleX = layer.ScaleY = inverse / common;
            layer.X = colored.X * inverse; layer.Y = colored.Y * inverse; layer.Visible = true;
        }
        else layer.Visible = false;
        return document;
    }

    // The same light checkerboard as the canvas, so dark shadows stay visible on transparent documents.
    static Raster OverChecker(Raster image)
    {
        var output = new Raster(image.Width, image.Height);
        Parallel.For(0, image.Height, y =>
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4; double a = image.Data[i + 3] / 255.0;
                bool light = (x / 10 + y / 10) % 2 == 1;
                byte r = light ? (byte)255 : (byte)0xE2, g = light ? (byte)255 : (byte)0xE4, b = light ? (byte)255 : (byte)0xE8;
                output.Data[i] = Imaging.Byte(image.Data[i] * a + b * (1 - a)); output.Data[i + 1] = Imaging.Byte(image.Data[i + 1] * a + g * (1 - a));
                output.Data[i + 2] = Imaging.Byte(image.Data[i + 2] * a + r * (1 - a)); output.Data[i + 3] = 255;
            }
        });
        return output;
    }
}
