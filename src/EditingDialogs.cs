using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed class TextEditorDialog : Window
{
    public TextSpec Spec { get; private set; }
    public TextEditorDialog(Window owner, TextSpec initial)
    {
        Spec = initial; Owner = owner; Title = "Morupixel · 텍스트"; Width = 760; Height = 570; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Panel; Foreground = Theme.Text;
        var grid = new Grid { Margin = new Thickness(22) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = grid;
        grid.Children.Add(Theme.Label("글자를 편집하고, 레이어로 보존하세요.", 21));
        var controls = new WrapPanel { Margin = new Thickness(0, 14, 0, 14) }; Grid.SetRow(controls, 1); grid.Children.Add(controls);
        var family = new ComboBox { ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray(), Text = initial.FontFamily, IsEditable = true, Width = 190, Margin = new Thickness(3), Padding = new Thickness(6) };
        var size = new TextBox { Text = initial.FontSize.ToString(CultureInfo.InvariantCulture), Width = 64 };
        var bold = new CheckBox { Content = "굵게", IsChecked = initial.Bold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
        var italic = new CheckBox { Content = "기울임", IsChecked = initial.Italic, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
        var align = new ComboBox { ItemsSource = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }, SelectedItem = initial.Alignment, Width = 90, Margin = new Thickness(3), Padding = new Thickness(6) };
        Color color = DocumentFeatures.Color(initial.ColorArgb);
        var colorButton = Theme.Button("색상", () => { var c = Dialogs.ColorPicker(this, color); if (c != null) color = c.Value; });
        foreach (var control in new UIElement[] { family, size, bold, italic, align, colorButton }) controls.Children.Add(control);
        var editor = new TextBox { Text = initial.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = Math.Min(initial.FontSize, 72), Padding = new Thickness(16), Background = Theme.Brush("#747C88"), Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(initial.FontFamily) };
        Grid.SetRow(editor, 2); grid.Children.Add(editor);
        void Preview()
        {
            try { editor.FontFamily = new FontFamily(family.Text); editor.FontSize = Math.Clamp(double.Parse(size.Text, CultureInfo.InvariantCulture), 6, 120); editor.FontWeight = bold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal; editor.FontStyle = italic.IsChecked == true ? FontStyles.Italic : FontStyles.Normal; editor.TextAlignment = (TextAlignment)(align.SelectedItem ?? TextAlignment.Left); editor.Foreground = new SolidColorBrush(color); } catch { }
        }
        family.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(Preview); size.TextChanged += (_, _) => Preview(); bold.Click += (_, _) => Preview(); italic.Click += (_, _) => Preview(); align.SelectionChanged += (_, _) => Preview(); colorButton.Click += (_, _) => Preview();
        family.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Preview()));
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 3); grid.Children.Add(footer);
        var apply = Theme.Button("텍스트 적용", () =>
        {
            try { Spec = new TextSpec { Content = editor.Text, FontFamily = family.Text, FontSize = Dialogs.Number(size.Text, 1, 1024), Bold = bold.IsChecked == true, Italic = italic.IsChecked == true, Alignment = (TextAlignment)(align.SelectedItem ?? TextAlignment.Left), ColorArgb = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B) }; Spec.Validate(); DialogResult = true; }
            catch (Exception e) { MessageBox.Show(this, e.Message); }
        }); DockPanel.SetDock(apply, Dock.Right); footer.Children.Add(apply);
        var cancel = Theme.Button("취소", () => DialogResult = false); cancel.IsCancel = true; DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        footer.Children.Add(Theme.Label("줄바꿈 지원 · 저장 후에도 내용과 서식 수정 가능", 11, Theme.Muted));
        Loaded += (_, _) => { Preview(); editor.Focus(); editor.SelectAll(); };
    }
}

public sealed class AdjustmentDialog : Window
{
    public AdjustmentSpec Spec { get; private set; }
    readonly Document original;
    readonly Guid? editingId;
    readonly byte[]? selectionMask;
    readonly SemaphoreSlim renderGate = new(1, 1);
    readonly List<Action> synchronizeControls = [];
    CancellationTokenSource? previewCts;
    int selectedChannel;
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly CheckBox enabled = new() { Content = "미리보기", IsChecked = true, Foreground = Theme.Text, Margin = new Thickness(8) };
    readonly TextBlock info = Theme.Label("", 11, Theme.Muted);
    long version;
    bool closed, synchronizing;
    public AdjustmentDialog(Window owner, Document document, AdjustmentSpec initial, Guid? editingId = null, Selection? selection = null)
    {
        Spec = initial; original = document.Snapshot(); this.editingId = editingId;
        info.TextWrapping = TextWrapping.Wrap;
        selectionMask = editingId == null && selection != null ? SelectionTools.Mask(selection, document.Width, document.Height) : null;
        Owner = owner; Title = "Morupixel · " + initial.Kind; Width = 1000; Height = 660; MinWidth = 860; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Panel; Foreground = Theme.Text;
        var grid = new Grid { Margin = new Thickness(18) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(292) });
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); Content = grid;
        grid.Children.Add(new Border { Background = Theme.Brush("#11171C"), Child = preview });
        var controls = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        var controlScroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(controlScroll, 1); grid.Children.Add(controlScroll);
        controls.Children.Add(Theme.Label(initial.Kind.ToString(), 24)); controls.Children.Add(Theme.Label("원본 픽셀을 보존하는 조정 레이어", 11, Theme.Muted));
        if (initial.Kind is AdjustmentKind.Levels or AdjustmentKind.Curves)
        {
            var channel = new ComboBox { ItemsSource = new[] { "RGB 전체", "빨강", "초록", "파랑" }, SelectedIndex = 0, Margin = new Thickness(3, 12, 3, 8), Padding = new Thickness(6) };
            channel.SelectionChanged += (_, _) => { selectedChannel = channel.SelectedIndex; SyncControls(); Schedule(); };
            controls.Children.Add(channel);
        }
        void Slider(string label, double min, double max, Func<AdjustmentSpec, double> get, Action<double> set)
        {
            double value = get(Spec);
            var text = Theme.Label(label + "  " + value.ToString("0.##"), 12); controls.Children.Add(text);
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(3, 2, 3, 10), SmallChange = (max - min) / 100 };
            synchronizeControls.Add(() => { slider.Value = get(Spec); text.Text = label + "  " + get(Spec).ToString("0.##"); });
            slider.ValueChanged += (_, _) => { if (synchronizing) return; set(slider.Value); SyncControls(); Schedule(); }; controls.Children.Add(slider);
        }
        switch (initial.Kind)
        {
            case AdjustmentKind.Levels:
                Slider("입력 검정", 0, 254, s => ChannelLevels(s).Black, n => SetChannelLevels(ChannelLevels(Spec) with { Black = Math.Min(n, ChannelLevels(Spec).White - 1) }));
                Slider("입력 흰색", 1, 255, s => ChannelLevels(s).White, n => SetChannelLevels(ChannelLevels(Spec) with { White = Math.Max(n, ChannelLevels(Spec).Black + 1) }));
                Slider("감마", .1, 9.99, s => ChannelLevels(s).Gamma, n => SetChannelLevels(ChannelLevels(Spec) with { Gamma = n }));
                Slider("출력 검정", 0, 255, s => ChannelLevels(s).OutputBlack, n => SetChannelLevels(ChannelLevels(Spec) with { OutputBlack = n }));
                Slider("출력 흰색", 0, 255, s => ChannelLevels(s).OutputWhite, n => SetChannelLevels(ChannelLevels(Spec) with { OutputWhite = n }));
                controls.Children.Add(Theme.Button("자동 레벨", AutoLevels)); break;
            case AdjustmentKind.Curves:
                var curve = new CurveEditor(initial.Curve); curve.Changed += points => { SetChannelCurve(points); Schedule(); };
                synchronizeControls.Add(() => curve.SetPoints(ChannelCurve(Spec)));
                controls.Children.Add(curve); controls.Children.Add(Theme.Label("클릭: 점 추가 · 드래그: 조절\n우클릭: 중간 점 삭제", 11, Theme.Muted));
                controls.Children.Add(Theme.Button("선형으로 초기화", () => { curve.Reset(); })); break;
            case AdjustmentKind.HueSaturation:
                Slider("색조", -360, 360, s => s.Hue, n => Spec = Spec with { Hue = n }); Slider("채도", -100, 100, s => s.Saturation, n => Spec = Spec with { Saturation = n }); Slider("명도", -100, 100, s => s.Lightness, n => Spec = Spec with { Lightness = n }); break;
            case AdjustmentKind.Exposure:
                Slider("노출 EV", -20, 20, s => s.Exposure, n => Spec = Spec with { Exposure = n }); Slider("오프셋", -.5, .5, s => s.Offset, n => Spec = Spec with { Offset = n }); Slider("감마", .01, 9.99, s => s.ExposureGamma, n => Spec = Spec with { ExposureGamma = n }); break;
            case AdjustmentKind.Grain:
                Slider("양", 0, 1, s => s.Amount, n => Spec = Spec with { Amount = n }); Slider("크기", .5, 20, s => s.GrainSize, n => Spec = Spec with { GrainSize = n }); Slider("거칠기", 0, 1, s => s.GrainRoughness, n => Spec = Spec with { GrainRoughness = n }); break;
            case AdjustmentKind.GradientMap:
                void ColorButton(string label, bool dark)
                {
                    var button = Theme.Button(label, () => { var color = Dialogs.ColorPicker(this, DocumentFeatures.Color(dark ? Spec.DarkColor : Spec.LightColor)); if (color == null) return; var c = color.Value; uint value = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B); Spec = dark ? Spec with { DarkColor = value } : Spec with { LightColor = value }; Schedule(); }); controls.Children.Add(button);
                }
                ColorButton("어두운 영역 색상", true); ColorButton("밝은 영역 색상", false); break;
        }
        controls.Children.Add(enabled); controls.Children.Add(info); enabled.Click += (_, _) => Schedule();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); grid.Children.Add(footer);
        var cancel = Theme.Button("취소", () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        footer.Children.Add(Theme.Button("조정 적용", () => { try { Spec.Validate(); DialogResult = true; } catch (Exception e) { MessageBox.Show(this, e.Message); } }));
        Closed += (_, _) => { closed = true; version++; previewCts?.Cancel(); }; Loaded += (_, _) => Schedule();
    }
    LevelsRange ChannelLevels(AdjustmentSpec spec) => selectedChannel switch
    {
        1 => spec.RedLevels, 2 => spec.GreenLevels, 3 => spec.BlueLevels,
        _ => new LevelsRange { Black = spec.Black, White = spec.White, Gamma = spec.Gamma, OutputBlack = spec.OutputBlack, OutputWhite = spec.OutputWhite }
    };
    void SetChannelLevels(LevelsRange value) => Spec = selectedChannel switch
    {
        1 => Spec with { RedLevels = value }, 2 => Spec with { GreenLevels = value }, 3 => Spec with { BlueLevels = value },
        _ => Spec with { Black = value.Black, White = value.White, Gamma = value.Gamma, OutputBlack = value.OutputBlack, OutputWhite = value.OutputWhite }
    };
    CurvePoint[] ChannelCurve(AdjustmentSpec spec) => selectedChannel switch { 1 => spec.RedCurve, 2 => spec.GreenCurve, 3 => spec.BlueCurve, _ => spec.Curve };
    void SetChannelCurve(CurvePoint[] value) => Spec = selectedChannel switch { 1 => Spec with { RedCurve = value }, 2 => Spec with { GreenCurve = value }, 3 => Spec with { BlueCurve = value }, _ => Spec with { Curve = value } };
    void SyncControls()
    {
        synchronizing = true;
        try { foreach (var sync in synchronizeControls) sync(); }
        finally { synchronizing = false; }
    }
    internal static Document PreviewDocument(Document original, Guid? editingId, AdjustmentSpec spec, bool show, byte[]? selectionMask)
    {
        var document = original.Snapshot();
        if (editingId is { } id)
        {
            var layer = document.Layers.Single(l => l.Id == id);
            if (show) layer.Adjustment = spec;
            else layer.Visible = false;
        }
        else if (show)
        {
            var layer = DocumentFeatures.CreateAdjustment(document, spec); layer.Mask = selectionMask; document.Add(layer);
        }
        return document;
    }
    async Task<Raster> Render(Document document, CancellationToken token)
    {
        await renderGate.WaitAsync(token);
        try { return await Task.Run(() => Imaging.Render(document, token), token); }
        finally { renderGate.Release(); }
    }
    async void Schedule()
    {
        if (closed) return;
        long generation = ++version; var spec = Spec; bool show = enabled.IsChecked == true;
        previewCts?.Cancel(); var cts = previewCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token); if (generation != version || closed) return;
            info.Text = "미리보기 계산 중…";
            var d = PreviewDocument(original, editingId, spec, show, selectionMask);
            var result = await Render(d, cts.Token);
            if (generation != version || closed) return;
            preview.Source = result.Bitmap(); info.Text = $"{d.Width} × {d.Height} px · " + (show ? "조정 미리보기" : "이 조정 없이 보기");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == version && !closed) info.Text = e.Message; }
        finally { if (ReferenceEquals(previewCts, cts)) previewCts = null; cts.Dispose(); }
    }
    async void AutoLevels()
    {
        if (closed) return;
        long generation = ++version; var spec = Spec; int channel = selectedChannel;
        previewCts?.Cancel(); var cts = previewCts = new CancellationTokenSource();
        try
        {
            info.Text = "조정 전 이미지의 자동 레벨 계산 중…";
            var image = await Render(PreviewDocument(original, editingId, spec, false, selectionMask), cts.Token);
            var levels = await Task.Run(() =>
            {
                var bins = new double[256]; double count = 0;
                for (int i = 0; i < image.Data.Length; i += 4)
                {
                    if ((i & 65535) == 0) cts.Token.ThrowIfCancellationRequested();
                    double weight = image.Data[i + 3] / 255.0 * (selectionMask == null ? 1 : selectionMask[i / 4] / 255.0);
                    int value = channel switch { 1 => image.Data[i + 2], 2 => image.Data[i + 1], 3 => image.Data[i], _ => (image.Data[i] + image.Data[i + 1] + image.Data[i + 2]) / 3 };
                    bins[value] += weight; count += weight;
                }
                if (count <= 0) return (Black: 0, White: 255);
                int low = 0, high = 255; double sum = 0;
                while (low < 254 && sum + bins[low] < count * .005) sum += bins[low++]; sum = 0;
                while (high > low + 1 && sum + bins[high] < count * .005) sum += bins[high--];
                return (Black: low, White: high);
            }, cts.Token);
            if (generation != version || closed || channel != selectedChannel) return;
            SetChannelLevels(ChannelLevels(Spec) with { Black = levels.Black, White = levels.White, Gamma = 1 }); SyncControls(); Schedule();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == version && !closed) info.Text = e.Message; }
        finally { if (ReferenceEquals(previewCts, cts)) previewCts = null; cts.Dispose(); }
    }
}

public sealed class CurveEditor : FrameworkElement
{
    readonly List<CurvePoint> points;
    byte[] curveDisplay = [];
    int active = -1;
    public event Action<CurvePoint[]>? Changed;
    public CurveEditor(CurvePoint[] initial)
    {
        points = initial.ToList(); RebuildCurve(); Height = 252; Margin = new Thickness(4, 15, 4, 12); Cursor = Cursors.Cross;
        MouseDown += (_, e) =>
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var p = e.GetPosition(this); var unit = new CurvePoint(Math.Clamp(p.X / ActualWidth, 0, 1), Math.Clamp(1 - p.Y / ActualHeight, 0, 1));
            active = points.FindIndex(q => Math.Abs(q.X - unit.X) * ActualWidth < 10 && Math.Abs(q.Y - unit.Y) * ActualHeight < 10);
            if (e.ChangedButton == MouseButton.Right) { if (active > 0 && active < points.Count - 1) { points.RemoveAt(active); Update(); } active = -1; return; }
            if (e.ChangedButton != MouseButton.Left) return;
            if (active < 0 && points.Count < 64 && unit.X > .01 && unit.X < .99 && points.All(q => Math.Abs(q.X - unit.X) > .004)) { points.Add(unit); points.Sort((a,b) => a.X.CompareTo(b.X)); active = points.IndexOf(unit); }
            CaptureMouse(); Update();
        };
        MouseMove += (_, e) =>
        {
            if (active < 0 || e.LeftButton != MouseButtonState.Pressed) return; var p = e.GetPosition(this);
            double gap = active == 0 || active == points.Count - 1 ? .002 : Math.Min(.002, (points[active + 1].X - points[active - 1].X) / 3);
            double x = active == 0 ? 0 : active == points.Count - 1 ? 1 : Math.Clamp(p.X / ActualWidth, points[active - 1].X + gap, points[active + 1].X - gap);
            points[active] = new CurvePoint(x, Math.Clamp(1 - p.Y / ActualHeight, 0, 1)); Update();
        };
        MouseUp += (_, _) => { active = -1; ReleaseMouseCapture(); };
    }
    public void Reset() { points.Clear(); points.Add(new(0,0)); points.Add(new(1,1)); Update(); }
    public void SetPoints(CurvePoint[] value) { points.Clear(); points.AddRange(value); active = -1; RebuildCurve(); InvalidateVisual(); }
    void RebuildCurve()
    {
        var ramp = new Raster(256, 1);
        for (int x = 0; x < 256; x++) { ramp.Data[x * 4] = ramp.Data[x * 4 + 1] = ramp.Data[x * 4 + 2] = (byte)x; ramp.Data[x * 4 + 3] = 255; }
        var mapped = DocumentFeatures.ApplyAdjustment(ramp, new AdjustmentSpec { Kind = AdjustmentKind.Curves, Curve = points.ToArray() });
        curveDisplay = Enumerable.Range(0, 256).Select(x => mapped.Data[x * 4]).ToArray();
    }
    void Update() { RebuildCurve(); InvalidateVisual(); Changed?.Invoke(points.ToArray()); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Brush("#151B22"), new Pen(Theme.Line, 1), new Rect(RenderSize));
        for (int i = 1; i < 4; i++) { dc.DrawLine(new Pen(Theme.Line, 1), new Point(ActualWidth * i / 4,0),new Point(ActualWidth * i / 4,ActualHeight)); dc.DrawLine(new Pen(Theme.Line,1),new Point(0,ActualHeight*i/4),new Point(ActualWidth,ActualHeight*i/4)); }
        dc.DrawLine(new Pen(Theme.Brush("#46505B"),1),new Point(0,ActualHeight),new Point(ActualWidth,0));
        Point Screen(CurvePoint p) => new(p.X * ActualWidth,(1-p.Y)*ActualHeight);
        var curvePen = new Pen(Theme.Accent, 2);
        for (int i = 1; i < curveDisplay.Length; i++) dc.DrawLine(curvePen, new Point((i - 1) * ActualWidth / 255, (1 - curveDisplay[i - 1] / 255d) * ActualHeight), new Point(i * ActualWidth / 255, (1 - curveDisplay[i] / 255d) * ActualHeight));
        foreach (var p in points) dc.DrawEllipse(Theme.Accent,new Pen(Theme.Panel,1),Screen(p),5,5);
    }
}

public static class ExportDialog
{
    public static void Show(Window owner, Document doc)
    {
        var window = new Window { Owner = owner, Title = "Morupixel · 내보내기", Width = 920, Height = 630, MinWidth = 760, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Panel, Foreground = Theme.Text };
        var grid = new Grid { Margin = new Thickness(20) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); window.Content = grid;
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8) }; grid.Children.Add(new Border { Background = Theme.Brush("#11171C"), Child = image });
        var side = new StackPanel { Margin = new Thickness(18, 0, 0, 0) }; Grid.SetColumn(side,1); grid.Children.Add(side);
        side.Children.Add(Theme.Label("내보내기",24)); side.Children.Add(Theme.Label($"{doc.Width} × {doc.Height} px",12,Theme.Muted));
        var format = new ComboBox { ItemsSource = new[] { ".png", ".jpg", ".tiff" }, SelectedIndex = 0, Padding = new Thickness(8), Margin = new Thickness(3,15,3,8) }; side.Children.Add(format);
        var qualityLabel = Theme.Label("JPEG 품질 95"); side.Children.Add(qualityLabel);
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = 95, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(5) }; side.Children.Add(quality);
        var size = Theme.Label("미리보기 준비…",11,Theme.Muted); side.Children.Add(size);
        int generation = 0; bool closed = false, saving = false;
        var snapshot = doc.Snapshot();
        var lifetime = new CancellationTokenSource();
        var render = Task.Run(() => Imaging.Render(snapshot, lifetime.Token), lifetime.Token);
        var encodeGate = new SemaphoreSlim(1, 1);
        CancellationTokenSource? pending = null;
        async void Update()
        {
            if (closed || saving) return;
            int id = ++generation; string ext = format.SelectedItem?.ToString() ?? ".png"; int q = (int)quality.Value;
            qualityLabel.Text = "JPEG 품질 " + q; quality.IsEnabled = ext == ".jpg";
            pending?.Cancel(); var cts = pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                await Task.Delay(120, cts.Token); size.Text = "미리보기 계산 중…";
                var raster = await render.WaitAsync(cts.Token);
                await encodeGate.WaitAsync(cts.Token);
                ExportPreview result;
                try
                {
                    result = await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(); ImportExport.Write(raster, stream, ext, q);
                        cts.Token.ThrowIfCancellationRequested(); long bytes = stream.Length; stream.Position = 0;
                        var decoded = Raster.Load(stream); double scale = Math.Min(1, 720d / Math.Max(decoded.Width, decoded.Height));
                        var previewRaster = scale == 1 ? decoded : ImportExport.Resize(decoded, Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale)));
                        cts.Token.ThrowIfCancellationRequested(); return new ExportPreview(previewRaster, bytes);
                    }, cts.Token);
                }
                finally { encodeGate.Release(); }
                if (id == generation && !closed) { image.Source = result.Image.Bitmap(); size.Text = $"예상 파일 크기 {result.ByteCount / 1024.0:0.#} KB"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (id == generation && !closed) size.Text = e.Message; }
            finally { if (ReferenceEquals(pending, cts)) pending = null; cts.Dispose(); }
        }
        quality.ValueChanged += (_,_) => Update(); format.SelectionChanged += (_,_) => Update();
        async void Save()
        {
            if (saving || closed) return;
            string ext = format.SelectedItem?.ToString() ?? ".png";
            var picker = new SaveFileDialog { FileName = doc.Name + ext, DefaultExt = ext, Filter = ext.TrimStart('.').ToUpperInvariant() + " 이미지|*" + ext };
            if (picker.ShowDialog(window) != true) return;
            int q = (int)quality.Value; saving = true; pending?.Cancel(); generation++; window.IsEnabled = false;
            try
            {
                var raster = await render;
                await encodeGate.WaitAsync(lifetime.Token);
                try { await Task.Run(() => ProjectStore.AtomicWrite(picker.FileName, stream => ImportExport.Write(raster, stream, ext, q)), lifetime.Token); }
                finally { encodeGate.Release(); }
                window.Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!closed) MessageBox.Show(window, e.Message, "내보내기 실패"); }
            finally { saving = false; if (!closed) { window.IsEnabled = true; Update(); } }
        }
        side.Children.Add(Theme.Button("파일로 저장…", Save));
        var cancel=Theme.Button("닫기",window.Close); cancel.IsCancel=true; side.Children.Add(cancel);
        window.Closed += (_, _) => { closed = true; generation++; lifetime.Cancel(); pending?.Cancel(); }; window.Loaded += (_, _) => Update(); window.ShowDialog();
    }
}
