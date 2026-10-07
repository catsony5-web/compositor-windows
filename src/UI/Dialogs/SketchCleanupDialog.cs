using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Compositor.Windows;

// What the dialog hands back: the sheet's corners in photo pixels (null: keep the whole photo), the
// line settings, and whether a white layer goes under the lines.
public sealed record SketchChoice(Point[]? Corners, SketchOptions Options, bool WhiteBackground);

// 스케치 사진 정리: the photo with the found sheet and four draggable corners on the left, the cleaned
// line art on the right, the line settings below. The sheet is found on the full photo off the UI thread
// when the dialog opens; previews are made from a reduced copy (PreviewSide) after a short pause, each
// newer request cancelling the older, and only the line step is redone when a line setting changes.
// Accepting returns the settings; the editor processes the photo at full resolution.
public sealed class SketchCleanupDialog : Window
{
    public const int DisplaySide = 1400, PreviewSide = 1000;
    internal static readonly string[] CornerNames = ["왼쪽 위", "오른쪽 위", "오른쪽 아래", "왼쪽 아래"];

    readonly Raster photo, display;
    readonly double displayScale;
    readonly Image photoImage = new() { Stretch = Stretch.Fill }, resultImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly Grid photoHost = new();
    // Handles on the photo's edge reach into the card's padding.
    readonly Canvas overlay = new() { ClipToBounds = false };
    readonly System.Windows.Shapes.Path shade = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
    readonly Polygon outline = new() { Stroke = Theme.Accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
    readonly Thumb[] handles = new Thumb[4];
    readonly CheckBox keepWhole, whiteBackground;
    readonly ParameterSlider threshold, speck, boldness;
    readonly SegmentedChoice<SketchLineColor> lineColor;
    readonly Border swatch = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock detectNote = DialogShell.Note(""), info = DialogShell.Note(""), error = DialogShell.ErrorText();
    readonly Button accept;
    Point[] corners;
    Rect imageRect;
    SheetDetection? detection;
    SketchSheet? prepared;
    CancellationTokenSource? work, detecting;
    long generation;
    bool closed, cornersEdited, thresholdAuto = true, speckAuto = true, syncing;
    uint customColor = new SketchOptions().CustomColor;

    public SketchChoice? Result { get; private set; }
    internal Point[] Corners => corners.ToArray();
    internal SheetDetection? Detection => detection;
    internal Raster? PreviewLines { get; private set; }
    internal (int Width, int Height) ResultSize { get; private set; }
    internal string? Error => string.IsNullOrEmpty(error.Text) ? null : error.Text;
    internal string DetectionText => detectNote.Text;
    internal IReadOnlyList<Thumb> Handles => handles;
    internal double ThresholdPercent => threshold.Value;
    internal double SpeckSize => speck.Value;
    internal SketchSheet? PreparedSheet => prepared;

    public SketchCleanupDialog(Window? owner, Raster photo, string photoName)
    {
        ArgumentNullException.ThrowIfNull(photo);
        this.photo = photo;
        display = PreviewScaling.Fit(photo, DisplaySide);
        displayScale = display.Width / (double)photo.Width;
        corners = SketchCleanup.ImageCorners(photo.Width, photo.Height);
        DialogShell.Prepare(this, owner, "스케치 사진 정리");
        Width = 1120; Height = 820; MinWidth = 760; MinHeight = 600;

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(DialogShell.Title("스케치 사진 정리"));
        var subtitle = DialogShell.Subtitle("종이에 그린 스케치를 찍은 사진에서 종이를 반듯하게 펴고 그림자와 종이 결을 지워 선만 남깁니다.");
        header.Children.Add(subtitle);
        var source = Loc.Keep(Theme.Label(photoName, Theme.CaptionSize, Theme.Subtle)); source.Margin = new Thickness(0, 4, 0, 0); source.TextTrimming = TextTrimming.CharacterEllipsis; source.TextWrapping = TextWrapping.NoWrap;
        header.Children.Add(source);
        root.Children.Add(header);

        // Photo (left) and result (right), equal columns.
        var views = new Grid(); Grid.SetRow(views, 1); root.Children.Add(views);
        views.ColumnDefinitions.Add(new ColumnDefinition()); views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); views.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); views.Children.Add(left);
        var leftCaption = DialogShell.FieldLabel("사진 · 종이의 네 모서리"); leftCaption.Margin = new Thickness(1, 0, 1, 6); DockPanel.SetDock(leftCaption, Dock.Top); left.Children.Add(leftCaption);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var find = DialogShell.Secondary("자동으로 찾기", () => DetectAsync(true)); find.ToolTip = "사진에서 종이의 네 모서리를 다시 찾습니다.";
        find.Margin = new Thickness(0, 0, 12, 4); tools.Children.Add(find);
        keepWhole = new CheckBox { Content = "원근 펴지 않기", Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4), ToolTip = "종이를 펴지 않고 사진 전체를 그대로 정리합니다. 스캔한 이미지처럼 이미 반듯할 때 고르세요." };
        keepWhole.Checked += (_, _) => FlattenChanged(); keepWhole.Unchecked += (_, _) => FlattenChanged();
        tools.Children.Add(keepWhole);
        var leftFooter = new StackPanel(); leftFooter.Children.Add(tools); detectNote.TextWrapping = TextWrapping.Wrap; leftFooter.Children.Add(detectNote);
        DockPanel.SetDock(leftFooter, Dock.Bottom); left.Children.Add(leftFooter);
        photoImage.Source = display.Bitmap();
        photoHost.Children.Add(photoImage); overlay.Children.Add(shade); overlay.Children.Add(outline);
        for (int k = 0; k < 4; k++) overlay.Children.Add(handles[k] = Handle(k));
        photoHost.Children.Add(overlay);
        var photoCard = DialogShell.Card(new Border { Padding = new Thickness(14), Child = photoHost });
        photoHost.SizeChanged += (_, _) => Arrange();
        left.Children.Add(photoCard);

        var right = new DockPanel(); Grid.SetColumn(right, 2); views.Children.Add(right);
        var rightCaption = DialogShell.FieldLabel("결과 · 선 그림"); rightCaption.Margin = new Thickness(1, 0, 1, 6); DockPanel.SetDock(rightCaption, Dock.Top); right.Children.Add(rightCaption);
        info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(1, 8, 1, 0); DockPanel.SetDock(info, Dock.Bottom); right.Children.Add(info);
        System.Windows.Automation.AutomationProperties.SetName(info, "미리보기 상태");
        right.Children.Add(DialogShell.Card(resultImage));

        // Line settings: three sliders side by side.
        var sliders = new Grid { Margin = new Thickness(0, 14, 0, 0) }; Grid.SetRow(sliders, 2); root.Children.Add(sliders);
        for (int c = 0; c < 5; c++) sliders.ColumnDefinitions.Add(c % 2 == 1 ? new ColumnDefinition { Width = new GridLength(16) } : new ColumnDefinition());
        threshold = new ParameterSlider("선 인식 기준 %", 1, 99, 35, 35)
        { ToolTip = "이 값보다 어두운 부분을 선으로 봅니다. 처음 값은 사진에서 자동으로 정했습니다. 흐린 선이 빠지면 낮추고, 종이 얼룩이 남으면 높이세요." };
        threshold.Changed += value => { if (syncing) return; thresholdAuto = Math.Abs(value - threshold.ResetValue) < .5; Schedule(false); };
        speck = new ParameterSlider("얼룩 정리 px", 0, 600, 16, 16, minimumStep: 1)
        { ToolTip = "이 픽셀 수보다 작은 점과 얼룩을 지웁니다. 0이면 지우지 않습니다. 처음 값은 결과 크기에 맞춰 정했습니다." };
        speck.Changed += value => { if (syncing) return; speckAuto = Math.Abs(value - speck.ResetValue) < .5; Schedule(false); };
        boldness = new ParameterSlider("선 진하게 %", 0, 100, 0, 0) { ToolTip = "흐린 선과 선의 가장자리를 더 진하게 합니다." };
        boldness.Changed += _ => { if (!syncing) Schedule(false); };
        foreach (var (slider, column) in new[] { (threshold, 0), (speck, 2), (boldness, 4) }) { Grid.SetColumn(slider, column); slider.Margin = new Thickness(0); sliders.Children.Add(slider); }

        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(options, 3); root.Children.Add(options);
        var colorCaption = Theme.Label("선 색", Theme.BodySize, Theme.Muted); colorCaption.Margin = new Thickness(1, 0, 10, 4); colorCaption.VerticalAlignment = VerticalAlignment.Center;
        options.Children.Add(colorCaption);
        lineColor = new SegmentedChoice<SketchLineColor>([(SketchLineColor.Original, "원래 펜 색"), (SketchLineColor.Black, "검은색"), (SketchLineColor.Custom, "고른 색")], SketchLineColor.Original)
        { MinWidth = 280, Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(lineColor, "선 색");
        lineColor.Buttons[0].ToolTip = "사진 속 펜과 연필의 색을 그대로 씁니다. 파란 펜은 파랗게, 여러 색으로 그린 스케치는 그 색대로 남습니다.";
        lineColor.Buttons[1].ToolTip = "모든 선을 검은색으로 만듭니다.";
        lineColor.Buttons[2].ToolTip = "오른쪽 색 고르기에서 정한 한 가지 색으로 모든 선을 칠합니다.";
        lineColor.Changed += _ => Schedule(false);
        options.Children.Add(lineColor);
        var pickContent = new StackPanel { Orientation = Orientation.Horizontal };
        pickContent.Children.Add(swatch); pickContent.Children.Add(new TextBlock { Text = "색 고르기…", VerticalAlignment = VerticalAlignment.Center });
        var pick = Theme.Button("", ChooseColor, "선에 칠할 색을 고릅니다.");
        pick.Content = pickContent; pick.MinHeight = Theme.ControlHeight; pick.Padding = new Thickness(8, 4, 12, 4); pick.Margin = new Thickness(0, 0, 24, 4); pick.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(pick, "선 색 고르기");
        options.Children.Add(pick);
        whiteBackground = new CheckBox { Content = "흰 바탕 넣기", IsChecked = true, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "선 아래에 흰 바탕 레이어를 함께 만듭니다. 끄면 선만 투명한 레이어로 만들어 다른 그림 위에 얹을 수 있습니다." };
        whiteBackground.Checked += (_, _) => Schedule(false); whiteBackground.Unchecked += (_, _) => Schedule(false);
        options.Children.Add(whiteBackground);
        UpdateSwatch();

        Grid.SetRow(error, 4); error.TextWrapping = TextWrapping.Wrap; root.Children.Add(error);
        var cancel = DialogShell.Secondary("취소", () => DialogResult = false); cancel.IsCancel = true;
        accept = DialogShell.Primary("정리하기", () => { if (Accept()) DialogResult = true; }); accept.IsDefault = true;
        accept.ToolTip = "원본 사진을 그대로 두고(숨김) 선 레이어와 흰 바탕을 새 그룹으로 만듭니다. 한 번에 실행 취소할 수 있습니다.";
        var footer = DialogShell.Footer(cancel, accept); footer.Margin = new Thickness(0, 14, 0, 0); Grid.SetRow(footer, 5); root.Children.Add(footer);

        UpdateResultSize(); SyncSpeck();
        detectNote.Text = "종이를 찾는 중…"; info.Text = "미리보기를 준비하는 중…";
        Loaded += (_, _) => DetectAsync(false);
        Closed += (_, _) => { closed = true; generation++; work?.Cancel(); detecting?.Cancel(); };
    }

    // ---- Corner handles -----------------------------------------------------------------------

    Thumb Handle(int index)
    {
        var template = new ControlTemplate(typeof(Thumb));
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(Shape.FillProperty, Theme.Accent); dot.SetValue(Shape.StrokeProperty, Theme.Panel); dot.SetValue(Shape.StrokeThicknessProperty, 2.0);
        template.VisualTree = dot;
        string corner = CornerNames[index], name = $"종이 {corner} 꼭짓점";
        var thumb = new Thumb { Width = 16, Height = 16, Template = template, Cursor = Cursors.SizeAll, Focusable = true, ToolTip = $"종이 {corner} 꼭짓점 · 끌어서 종이 모서리에 맞추세요 · 방향키로 미세 조절" };
        System.Windows.Automation.AutomationProperties.SetName(thumb, name);
        thumb.DragDelta += (_, e) => MoveCorner(index, new Vector(e.HorizontalChange, e.VerticalChange) / ScreenScale);
        thumb.KeyDown += (_, e) =>
        {
            var step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1) / ScreenScale;
            var move = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => (Vector?)null };
            if (move is { } delta) { MoveCorner(index, delta); e.Handled = true; }
        };
        return thumb;
    }

    // Screen units per photo pixel in the left preview.
    double ScreenScale => imageRect.Width > 0 ? imageRect.Width / photo.Width : 1;

    void MoveCorner(int index, Vector delta)
    {
        if (keepWhole.IsChecked == true || !double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        var p = corners[index] + delta;
        corners[index] = new Point(Math.Clamp(p.X, 0, photo.Width), Math.Clamp(p.Y, 0, photo.Height));
        cornersEdited = true;
        detectNote.Text = "꼭짓점을 직접 맞췄습니다. 자동으로 찾기를 누르면 다시 찾습니다.";
        Arrange(); UpdateResultSize(); Schedule(true);
    }

    internal void SetCorners(IReadOnlyList<Point> next)
    {
        corners = next.Select(p => new Point(Math.Clamp(p.X, 0, photo.Width), Math.Clamp(p.Y, 0, photo.Height))).ToArray();
        cornersEdited = true; Arrange(); UpdateResultSize(); Schedule(true);
    }

    // Places the photo uniformly in its card, then the outline, the shade outside it and the handles.
    void Arrange()
    {
        bool flatten = keepWhole.IsChecked != true;
        outline.Visibility = shade.Visibility = flatten ? Visibility.Visible : Visibility.Collapsed;
        foreach (var handle in handles) handle.Visibility = flatten ? Visibility.Visible : Visibility.Collapsed;
        double width = photoHost.ActualWidth, height = photoHost.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double scale = Math.Min(width / display.Width, height / display.Height);
        double w = display.Width * scale, h = display.Height * scale;
        imageRect = new Rect((width - w) / 2, (height - h) / 2, w, h);
        photoImage.Width = w; photoImage.Height = h; photoImage.HorizontalAlignment = HorizontalAlignment.Center; photoImage.VerticalAlignment = VerticalAlignment.Center;
        var points = corners.Select(p => new Point(imageRect.X + p.X * ScreenScale, imageRect.Y + p.Y * ScreenScale)).ToArray();
        outline.Points = new PointCollection(points);
        var quad = new StreamGeometry();
        using (var g = quad.Open()) { g.BeginFigure(points[0], true, true); g.PolyLineTo(points.Skip(1).ToArray(), true, true); }
        shade.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(imageRect), quad } };
        for (int k = 0; k < 4; k++) { Canvas.SetLeft(handles[k], points[k].X - handles[k].Width / 2); Canvas.SetTop(handles[k], points[k].Y - handles[k].Height / 2); }
    }

    void FlattenChanged()
    {
        Arrange(); UpdateResultSize();
        if (keepWhole.IsChecked == true) detectNote.Text = "사진 전체를 원근을 펴지 않고 정리합니다.";
        else ShowDetectionNote();
        Schedule(true);
    }

    internal void SetKeepWhole(bool whole) => keepWhole.IsChecked = whole;

    // ---- Detection ----------------------------------------------------------------------------

    async void DetectAsync(bool user)
    {
        if (closed) return;
        detecting?.Cancel(); var cts = detecting = new CancellationTokenSource();
        detectNote.Text = "종이를 찾는 중…";
        try
        {
            var found = await Task.Run(() => SketchCleanup.DetectSheet(photo, cts.Token), cts.Token);
            if (closed || !ReferenceEquals(detecting, cts)) return;
            // A corner the user moved while the sheet was being found stays where it was put.
            if (cornersEdited && !user) return;
            ApplyDetection(found);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { if (!closed) { detectNote.Text = e.Message; Schedule(true); } }
    }

    // Finds the sheet now on the calling thread (offscreen captures and self-tests).
    internal void DetectNow() => ApplyDetection(SketchCleanup.DetectSheet(photo));

    void ApplyDetection(SheetDetection found)
    {
        detection = found; corners = found.Corners.ToArray(); cornersEdited = false;
        if (found.Found && keepWhole.IsChecked == true) keepWhole.IsChecked = false;
        ShowDetectionNote(); Arrange(); UpdateResultSize(); Schedule(true);
    }

    void ShowDetectionNote()
    {
        if (detection == null || cornersEdited) return;
        detectNote.Text = detection.Found
            ? "종이를 찾았습니다. 모서리가 어긋나면 네 꼭짓점을 끌어 맞추세요."
            : "종이 가장자리를 찾지 못해 사진 전체를 씁니다. 꼭짓점을 끌어 종이 모서리에 맞추면 원근을 폅니다.";
    }

    // ---- Settings ------------------------------------------------------------------------------

    bool Flatten => keepWhole.IsChecked != true;

    void UpdateResultSize()
    {
        try { ResultSize = Flatten ? SketchCleanup.FlatSize(SketchCleanup.Corners(corners, photo.Width, photo.Height), photo.Width, photo.Height) : SketchCleanup.WholeSize(photo.Width, photo.Height); }
        catch (InvalidDataException) { }
        SyncSpeck();
    }

    // The automatic speck size follows the result size until the user sets one.
    void SyncSpeck()
    {
        int automatic = SketchCleanup.AutomaticSpeckSize(ResultSize.Width, ResultSize.Height);
        speck.ResetValue = automatic;
        if (!speckAuto) return;
        syncing = true;
        try { speck.SetValue(automatic); }
        finally { syncing = false; }
    }

    internal void SetThreshold(double percent) { threshold.SetValue(percent, true); }
    internal void SetSpeck(int pixels) { speck.SetValue(pixels, true); }
    internal void SetBoldness(double percent) { boldness.SetValue(percent, true); }
    internal void SetLineColor(SketchLineColor color, uint? custom = null)
    {
        if (custom is { } value) { customColor = value; UpdateSwatch(); }
        lineColor.Select(color); Schedule(false);
    }
    internal void SetWhiteBackground(bool on) => whiteBackground.IsChecked = on;

    void ChooseColor()
    {
        var picked = Dialogs.ColorPicker(this, VectorShapes.Color(customColor), "선 색");
        if (picked is not { } color) return;
        customColor = VectorShapes.Argb(color with { A = 255 }); UpdateSwatch();
        if (lineColor.Selected != SketchLineColor.Custom) lineColor.Select(SketchLineColor.Custom); else Schedule(false);
    }

    void UpdateSwatch() { var c = VectorShapes.Color(customColor); swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); }

    SketchOptions Options(double scale) => new()
    {
        Flatten = Flatten,
        Threshold = threshold.Value / 100,
        SpeckSize = (int)Math.Round(speck.Value * scale * scale),
        Boldness = boldness.Value / 100,
        LineColor = lineColor.Selected,
        CustomColor = customColor
    };

    // ---- Preview -------------------------------------------------------------------------------

    // The preview's share of the result's resolution (speck sizes scale with its square).
    double PreviewFit => Math.Min(1, PreviewSide / (double)Math.Max(1, Math.Max(ResultSize.Width, ResultSize.Height)));

    // The reduced photo flattened to the preview size, its lighting evened out.
    SketchSheet PrepareSheet(Point[] quad, bool flatten, (int Width, int Height) size, CancellationToken token)
    {
        double fit = Math.Min(1, PreviewSide / (double)Math.Max(size.Width, size.Height));
        int width = Math.Max(16, (int)Math.Round(size.Width * fit)), height = Math.Max(16, (int)Math.Round(size.Height * fit));
        var displayQuad = flatten ? quad.Select(p => new Point(p.X * displayScale, p.Y * displayScale)).ToArray() : SketchCleanup.ImageCorners(display.Width, display.Height);
        return SketchCleanup.Prepare(SketchCleanup.Flatten(display, displayQuad, width, height, token), token);
    }

    // The lines with the current settings, shown on white paper or, when they stay transparent, on the
    // canvas checkerboard.
    static (Raster Lines, BitmapSource Image) Render(SketchSheet sheet, SketchOptions options, bool white, CancellationToken token)
    {
        var lines = SketchCleanup.Extract(sheet, options, token);
        var shown = new Raster(lines.Width, lines.Height);
        for (int y = 0; y < lines.Height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < lines.Width; x++)
            {
                int i = (y * lines.Width + x) * 4; double a = lines.Data[i + 3] / 255.0;
                byte paper = white || (x / 10 + y / 10) % 2 == 1 ? (byte)255 : (byte)0xE2;
                for (int c = 0; c < 3; c++) shown.Data[i + c] = Imaging.Byte(lines.Data[i + c] * a + paper * (1 - a));
                shown.Data[i + 3] = 255;
            }
        }
        return (lines, shown.Bitmap());
    }

    async void Schedule(bool prepare)
    {
        // A new sheet is needed after the corners or the flattening changed, shown or not (RefreshNow).
        if (prepare) prepared = null;
        if (closed || !IsLoaded) return;
        long current = ++generation;
        work?.Cancel(); var cts = work = new CancellationTokenSource();
        try
        {
            await Task.Delay(prepare ? 140 : 70, cts.Token);
            if (current != generation || closed) return;
            Point[] quad;
            try { quad = SketchCleanup.Corners(corners, photo.Width, photo.Height); }
            catch (InvalidDataException e) { ShowError(e.Message); return; }
            info.Text = "미리보기를 만드는 중…";
            var sheet = prepared;
            if (sheet == null)
            {
                bool flatten = Flatten; var size = ResultSize;
                sheet = await Task.Run(() => PrepareSheet(quad, flatten, size, cts.Token), cts.Token);
                if (current != generation || closed) return;
                prepared = sheet; AdoptAutomaticThreshold(sheet);
            }
            var options = Options(PreviewFit); bool white = whiteBackground.IsChecked == true;
            var result = await Task.Run(() => Render(sheet, options, white, cts.Token), cts.Token);
            if (current != generation || closed) return;
            Show(result);
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException e) { if (current == generation && !closed) ShowError(e.Message); }
    }

    // Makes the preview now on the calling thread (offscreen captures and self-tests).
    internal void RefreshNow()
    {
        try
        {
            var quad = SketchCleanup.Corners(corners, photo.Width, photo.Height);
            prepared ??= PrepareSheet(quad, Flatten, ResultSize, CancellationToken.None);
            AdoptAutomaticThreshold(prepared);
            Show(Render(prepared, Options(PreviewFit), whiteBackground.IsChecked == true, CancellationToken.None));
        }
        catch (InvalidDataException e) { ShowError(e.Message); }
    }

    // Each sheet has its own automatic threshold; the slider follows it unless the user chose a value.
    void AdoptAutomaticThreshold(SketchSheet sheet)
    {
        double automatic = Math.Round(sheet.AutomaticThreshold * 100);
        threshold.ResetValue = automatic;
        if (!thresholdAuto) return;
        syncing = true;
        try { threshold.SetValue(automatic); }
        finally { syncing = false; }
    }

    void Show((Raster Lines, BitmapSource Image) result)
    {
        PreviewLines = result.Lines; resultImage.Source = result.Image;
        error.Text = ""; accept.IsEnabled = true;
        info.Text = $"결과 크기 {ResultSize.Width:N0} × {ResultSize.Height:N0} px · 원본 사진은 숨겨 둡니다.";
    }

    void ShowError(string message)
    {
        error.Text = message; accept.IsEnabled = false; info.Text = "";
        resultImage.Source = null; PreviewLines = null;
    }

    internal bool Accept()
    {
        foreach (var slider in new[] { threshold, speck, boldness }) if (!slider.TryCommit()) { error.Text = "표시된 범위 안의 숫자를 입력해 주세요."; return false; }
        Point[]? quad = null;
        if (Flatten)
        {
            try { quad = SketchCleanup.Corners(corners, photo.Width, photo.Height); }
            catch (InvalidDataException e) { ShowError(e.Message); return false; }
        }
        Result = new SketchChoice(quad, Options(1) with { Flatten = Flatten, SpeckSize = (int)Math.Round(speck.Value) }, whiteBackground.IsChecked == true);
        return true;
    }
}
