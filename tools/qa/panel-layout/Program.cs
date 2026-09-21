using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Windows;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly List<CaptureReport> reports = [];
    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    [STAThread]
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "artifacts/qa/panel-layout");
        bool closeDialogOnly = args.Contains("--close-dialog-only", StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        try
        {
            Run(output, closeDialogOnly);
            File.WriteAllText(Path.Combine(output, "layout-report.json"), JsonSerializer.Serialize(reports, jsonOptions));
            var failures = reports.SelectMany(r => r.Issues.Select(i => (r.File, Issue: i))).ToArray();
            foreach (var count in failures.GroupBy(f => f.Issue.Kind))
                Console.WriteLine($"{count.Key}: {count.Count()} occurrences");
            foreach (var (file, issue) in failures.DistinctBy(f => (f.Issue.Kind, f.Issue.Text, f.Issue.Detail)).Take(20))
                Console.WriteLine($"{issue.Kind}: {file} | {issue.Text} | {issue.Detail}");
            int abbreviated = reports.SelectMany(r => r.Actions).Count(a => a.HasLiteralEllipsis);
            Console.WriteLine($"Captured {reports.Count} actual panel layouts; {failures.Length} glyph/action-label issues; {abbreviated} abbreviated action labels. Output: {output}");
            return failures.Length == 0 ? 0 : 2;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Application.Current?.Shutdown(); }
    }

    static void Run(string output, bool closeDialogOnly)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Theme.Apply(app);
        ValidateDetector(output);
        if (closeDialogOnly) { CaptureSaveChanges(output); return; }
        var window = new MainWindow(null);
        Field("headlessTesting").SetValue(window, true);
        window.RenderPreview(Path.Combine(output, "startup.png"));
        Invoke(window, "OpenLearningSample");
        var doc = (Document)Field("doc").GetValue(window)!;
        foreach (var layer in doc.Layers) layer.Locked = false;
        var image = doc.Layers.First(l => l.Kind == LayerKind.Raster);
        image.Name = "원본 사진 · 한글 가로획 ㅡ와 긴 레이어 이름도 읽을 수 있는지 확인합니다";
        var text = doc.Layers.First(l => l.Kind == LayerKind.Text);
        text.Name = "편집 가능한 긴 한글 제목 · 글자 크기와 줄 간격을 조절하는 텍스트";
        DocumentFeatures.UpdateText(text, text.Text! with { Content = "한글 가로획 ㅡ 확인\n텍스트와 이미지 함께 편집", FontFamily = "Malgun Gothic", FontSize = 36 });
        var shape = VectorShapes.Create(new ShapeSpec { Width = 320, Height = 140, CornerRadius = 20, StrokeEnabled = true, StrokeWidth = 2 }, 60, 60);
        shape.Name = "둥근 사각형 · 채우기와 선 두께를 조절하는 긴 도형 레이어"; doc.Add(shape);
        for (int i = 0; i < 8; i++)
            doc.Add(new Layer { Name = $"{i + 1:00} · 긴 한글 보조 이미지 레이어 · 이름이 잘려 보이지 않는지 확인", Pixels = new Raster(16, 16) });
        doc.ActiveId = image.Id;
        window.RenderPreview(Path.Combine(output, "initial-editor.png"));
        var root = (Grid)window.Content;
        var body = root.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 3);
        var right = body.Children.OfType<FrameworkElement>().Single(e => Grid.GetColumn(e) == 3);

        var cases = new[]
        {
            new PanelCase("workspace", 0, image), new PanelCase("image-properties", 1, image),
            new PanelCase("text-properties", 1, text), new PanelCase("shape-properties", 1, shape),
            new PanelCase("colors", 2, image), new PanelCase("brushes", 3, image),
            new PanelCase("layers", -1, image)
        };
        foreach (bool design in new[] { false, true })
        {
            Invoke(window, "SetWorkspaceMode", design);
            string mode = design ? "design" : "photo";
            foreach (var scenario in cases)
            {
                doc.ActiveId = scenario.Layer.Id;
                Invoke(window, "BuildProperties"); Invoke(window, "BuildLayers");
                if (scenario.Page >= 0) Invoke(window, "ShowStudioPage", scenario.Page, false);
                var pane = scenario.Page < 0 ? (FrameworkElement)Field("layersPane").GetValue(window)! : (FrameworkElement)((Array)Field("studioPanes").GetValue(window)!).GetValue(scenario.Page)!;
                foreach (double scale in new[] { 1d, 1.5 })
                {
                    // Change the actual root's DPI, not only PNG metadata: WPF must
                    // measure hinted glyphs and round layout at the tested scale.
                    SetDpi(root, scale);
                    foreach (int width in new[] { 324, 396 })
                    {
                        body.ColumnDefinitions[^1].Width = new GridLength(width);
                        ((ScrollViewer)Field("studioScroll").GetValue(window)!).Height = 390;
                        Layout(root, 1480, 980);
                        ScrollTo(pane, false); Layout(root, 1480, 980);
                        Capture(right, output, $"{mode}-{scenario.Name}-docked-{width}-{scale:0.0}x-top", scale, root);
                        if (ScrollTo(pane, true))
                        {
                            Layout(root, 1480, 980);
                            Capture(right, output, $"{mode}-{scenario.Name}-docked-{width}-{scale:0.0}x-bottom", scale, root);
                        }
                    }
                }

                // Remove only the existing live pane, and put it in an offscreen
                // parent. No Window.Show, focus, mouse or keyboard APIs are used.
                Invoke(window, "RemovePane", pane);
                var host = new Border { Background = Theme.Header, Child = pane };
                try
                {
                    foreach (double scale in new[] { 1d, 1.5 })
                    {
                        SetDpi(host, scale);
                        foreach (int width in new[] { 324, 390 })
                        {
                            Layout(host, width, 620); ScrollTo(pane, false); Layout(host, width, 620);
                            Capture(host, output, $"{mode}-{scenario.Name}-floating-{width}-{scale:0.0}x-top", scale);
                            if (ScrollTo(pane, true))
                            {
                                Layout(host, width, 620);
                                Capture(host, output, $"{mode}-{scenario.Name}-floating-{width}-{scale:0.0}x-bottom", scale);
                            }
                        }
                    }
                    // Add selected difficult controls at200% without repeating
                    // the whole width/mode matrix or generating hundreds more PNGs.
                    bool highDpiCase = design
                        ? scenario.Name is "text-properties" or "shape-properties" or "colors" or "layers"
                        : scenario.Name is "workspace" or "brushes";
                    if (highDpiCase)
                    {
                        SetDpi(host, 2);
                        Layout(host, 324, 620); ScrollTo(pane, false); Layout(host, 324, 620);
                        Capture(host, output, $"{mode}-{scenario.Name}-floating-324-2.0x-top", 2);
                        if (ScrollTo(pane, true))
                        {
                            Layout(host, 324, 620);
                            Capture(host, output, $"{mode}-{scenario.Name}-floating-324-2.0x-bottom", 2);
                        }
                    }
                }
                finally
                {
                    host.Child = null;
                    if (scenario.Page < 0) ((Border)Field("layersSlot").GetValue(window)!).Child = pane;
                    else Invoke(window, "ShowStudioPage", scenario.Page, false);
                }
            }
        }
        CaptureCustomBrush(window, output);
        CapturePhotoDevelop(doc, (Raster)Field("composite").GetValue(window)!, output);
        CaptureQuickAdjustments(output);
        CaptureLegacyAdjustments(doc, (Raster)Field("composite").GetValue(window)!, output);
        CaptureSaveChanges(output);
    }

    static void CaptureSaveChanges(string output)
    {
        var scenarios = new[]
        {
            (Name: "short", Document: "제목 없음"),
            (Name: "long-korean", Document: "2026년 가을 브랜드 디자인 최종 수정본 · 한글 가로획 ㅡ와 긴 문서 이름 · 인쇄용 포스터와 웹 배너 이미지 레이어를 함께 정리한 작업 파일 · 검토 의견 반영 완료.moruproj")
        };
        foreach (var scenario in scenarios)
        {
            var dialog = new SaveChangesDialog(null, scenario.Document);
            var content = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            // The opaque host includes the dialog content's outer margin; native
            // caption metrics and rendering are outside this offscreen capture.
            var host = new Border { Background = dialog.Background, Child = content };
            try
            {
                foreach (double scale in new[] { 1d, 1.5, 2 })
                    foreach (double width in new[] { dialog.Width, dialog.MinWidth }.Distinct())
                    {
                        SetDpi(host, scale);
                        host.Measure(new Size(width, double.PositiveInfinity));
                        // SaveChangesDialog sizes to its content, so measure the
                        // actual client controls instead of imposing an empty fixed-height card.
                        double height = Math.Ceiling(Math.Max(dialog.MinHeight, host.DesiredSize.Height));
                        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
                            throw new InvalidOperationException("Save confirmation must provide measurable dialog dimensions.");
                        Layout(host, width, height);
                        Capture(host, output, $"save-changes-{scenario.Name}-{width:0}-{scale:0.0}x", scale);
                        var report = reports[^1];
                        foreach (string action in new[] { "취소", "저장하지 않고 닫기", "저장 후 닫기" })
                        {
                            var captured = report.Actions.FirstOrDefault(item => item.Label == action);
                            if (captured == null || !captured.VisibleInCapture || !captured.Enabled)
                                report.Issues.Add(new Issue("close-action-not-visible", action, "All three complete close-confirmation choices must be enabled and visible."));
                        }
                        var documentLabel = Descendants(host).OfType<TextBlock>().FirstOrDefault(block => block.Text.Contains(scenario.Document, StringComparison.Ordinal));
                        if (documentLabel == null || !VisibleArea(documentLabel, host) || documentLabel.TextTrimming != TextTrimming.None
                            || !new Rect(host.RenderSize).Contains(documentLabel.TransformToAncestor(host).TransformBounds(new Rect(documentLabel.RenderSize))))
                            report.Issues.Add(new Issue("close-document-not-visible", scenario.Document, "The entire document-name field must be present inside the measured dialog and must not use text trimming."));
                    }
            }
            finally { host.Child = null; dialog.Close(); }
        }
        File.WriteAllText(Path.Combine(output, "save-changes-capture-scope.txt"),
            "SaveChangesDialog content and its full background were rendered offscreen at measured auto heights, default/minimum widths, and 100%, 150%, 200% DPI.\n" +
            "No native window was shown. Windows title-bar/non-client rendering and desktop interaction are outside these captures.\n");
    }

    static void CaptureCustomBrush(MainWindow window, string output)
    {
        // A generated in-memory alpha mask exercises the real custom-tip controls
        // without loading or persisting anything in the user's brush library.
        var mask = new byte[48 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 48; x++)
            {
                double radius = Math.Pow((x - 23.5) / 22, 2) + Math.Pow((y - 15.5) / 13, 2);
                mask[y * 48 + x] = (byte)Math.Round(Math.Clamp((1 - radius) * 3, 0, 1) * 255);
            }
        var tip = BrushTip.FromAlpha("내가 만든 수채화 나뭇잎 브러시 · 긴 한글 이미지 이름과 가로획 ㅡ 확인", 48, 32, mask);
        ((List<BrushTip>)Field("customBrushTips").GetValue(window)!).Add(tip);
        Field("brushAngle").SetValue(window, -37.5);
        Field("brushSpacing").SetValue(window, .67);
        Invoke(window, "ShowStudioPage", 3, false);
        ((StackPanel[])Field("studioContents").GetValue(window)!)[3].Children.Clear();
        Invoke(window, "BuildStudioPage", 3);
        Invoke(window, "SelectBrushTip", tip);
        var pane = (FrameworkElement)((Array)Field("studioPanes").GetValue(window)!).GetValue(3)!;
        Invoke(window, "RemovePane", pane);
        var host = new Border { Background = Theme.Header, Child = pane };
        try
        {
            foreach (double scale in new[] { 1d, 1.5 })
            {
                SetDpi(host, scale);
                Layout(host, 324, 620); ScrollTo(pane, false); Layout(host, 324, 620);
                Capture(host, output, $"brush-custom-long-name-floating-324-{scale:0.0}x-top", scale);
                if (!ScrollTo(pane, true)) throw new InvalidOperationException("Custom brush QA must exercise the lower rotation and spacing controls.");
                Layout(host, 324, 620);
                Capture(host, output, $"brush-custom-long-name-floating-324-{scale:0.0}x-bottom", scale);
            }
        }
        finally { host.Child = null; Invoke(window, "ShowStudioPage", 3, false); }
    }

    static void CapturePhotoDevelop(Document document, Raster source, string output)
    {
        var settings = new PhotoDevelopSpec
        {
            Exposure = .45, Contrast = 12, Highlights = -35, Shadows = 22,
            Whites = 8, Blacks = -8, Temperature = 10, Tint = -5,
            Vibrance = 18, Saturation = 2, Texture = 8, Clarity = 6, Dehaze = 4
        };
        var dialog = new AdjustmentDialog(null, document, new AdjustmentSpec { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = settings });
        var preview = PhotoDevelop.Apply(source, settings);
        (typeof(AdjustmentDialog).GetMethod("SetDesignPreview", Private) ?? throw new MissingMethodException("SetDesignPreview"))
            .Invoke(dialog, [preview]);
        ((TextBlock)(typeof(AdjustmentDialog).GetField("info", Private) ?? throw new MissingFieldException("info")).GetValue(dialog)!).Text
            = $"{document.Width} × {document.Height} px · 조정 미리보기";
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        var host = new Border { Background = Theme.Header, Child = content };
        try
        {
            foreach (double scale in new[] { 1d, 1.5 })
                foreach (var size in new[] { new Size(1040, 760), new Size(860, 580) })
                {
                    SetDpi(host, scale);
                    Layout(host, size.Width, size.Height); ScrollTo(content, false); Layout(host, size.Width, size.Height);
                    Capture(host, output, $"photo-develop-{size.Width:0}x{size.Height:0}-{scale:0.0}x-top", scale);
                    if (!ScrollTo(content, true)) throw new InvalidOperationException("Photo development QA must exercise both ends of the actual adjustment controls.");
                    Layout(host, size.Width, size.Height);
                    Capture(host, output, $"photo-develop-{size.Width:0}x{size.Height:0}-{scale:0.0}x-bottom", scale);
                }
        }
        finally { host.Child = null; dialog.Close(); }
    }

    static void CaptureQuickAdjustments(string output)
    {
        var create = typeof(MainWindow).GetMethod("CreateQuickAdjustmentDialog", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("CreateQuickAdjustmentDialog");
        foreach (string kind in new[] { "exposure", "levels", "saturation", "blur" })
        {
            // Use the real menu factory so bounds, labels, validation and field
            // count cannot silently diverge between the product and QA fixtures.
            var dialog = (Window)create.Invoke(null, [null, kind])!;
            var content = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            var host = new Border { Background = Theme.Header, Child = content };
            try
            {
                foreach (double scale in new[] { 1d, 1.5 })
                    foreach (int width in new[] { 400, 480 })
                    {
                        SetDpi(host, scale);
                        double height = DialogCaptureHeight(dialog, host, width);
                        Layout(host, width, height); ScrollTo(content, false); Layout(host, width, height);
                        Capture(host, output, $"quick-{kind}-{width}-{scale:0.0}x", scale);
                        if (ScrollTo(content, true))
                        {
                            Layout(host, width, height);
                            Capture(host, output, $"quick-{kind}-{width}-{scale:0.0}x-bottom", scale);
                        }
                    }
                // The short controls have the same narrow layout when a coarser
                // increment is selected; capture the displayed choice at high DPI.
                SetStepFive(content);
                SetDpi(host, 1.5);
                double steppedHeight = DialogCaptureHeight(dialog, host, 400);
                Layout(host, 400, steppedHeight); ScrollTo(content, false); Layout(host, 400, steppedHeight);
                Capture(host, output, $"quick-{kind}-400-1.5x-step5", 1.5);
                if (ScrollTo(content, true))
                {
                    Layout(host, 400, steppedHeight);
                    Capture(host, output, $"quick-{kind}-400-1.5x-step5-bottom", 1.5);
                }
            }
            finally { host.Child = null; dialog.Close(); }
        }
    }

    static double DialogCaptureHeight(Window dialog, FrameworkElement host, int width)
    {
        host.Measure(new Size(width, double.PositiveInfinity));
        // Auto-sized dialogs report NaN for Height. Measure the live content at
        // the tested width, then retain a bounded viewport that exercises scrolling.
        double height = double.IsFinite(dialog.Height) ? dialog.Height : Math.Ceiling(host.DesiredSize.Height);
        if (!double.IsFinite(height)) throw new InvalidOperationException("Quick adjustment content did not produce a finite desired height.");
        return Math.Max(dialog.MinHeight, Math.Clamp(height, 300, 760));
    }

    static void CaptureLegacyAdjustments(Document document, Raster source, string output)
    {
        var scenarios = new[]
        {
            (Name: "levels", Spec: new AdjustmentSpec { Kind = AdjustmentKind.Levels, Black = 14, White = 244, Gamma = .85 }),
            (Name: "hue-saturation", Spec: new AdjustmentSpec { Kind = AdjustmentKind.HueSaturation, Hue = -24, Saturation = 20, Lightness = -12 }),
            (Name: "exposure", Spec: new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = -.35, Offset = -.12, ExposureGamma = .85 })
        };
        foreach (var scenario in scenarios)
        {
            var dialog = new AdjustmentDialog(null, document, scenario.Spec);
            (typeof(AdjustmentDialog).GetMethod("SetDesignPreview", Private) ?? throw new MissingMethodException("SetDesignPreview"))
                .Invoke(dialog, [DocumentFeatures.ApplyAdjustment(source, scenario.Spec)]);
            var content = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            var host = new Border { Background = Theme.Header, Child = content };
            try
            {
                foreach (double scale in new[] { 1d, 1.5 })
                {
                    SetDpi(host, scale);
                    Layout(host, 860, 580); ScrollTo(content, false); Layout(host, 860, 580);
                    Capture(host, output, $"adjustment-{scenario.Name}-860x580-{scale:0.0}x-top", scale);
                    if (ScrollTo(content, true))
                    {
                        Layout(host, 860, 580);
                        Capture(host, output, $"adjustment-{scenario.Name}-860x580-{scale:0.0}x-bottom", scale);
                    }
                }
            }
            finally { host.Child = null; dialog.Close(); }
        }
    }

    static void SetStepFive(FrameworkElement content)
    {
        var stepSelectors = Descendants(content).OfType<ParameterSlider>()
            .SelectMany(Descendants).OfType<ComboBox>().ToArray();
        if (stepSelectors.Length == 0) throw new InvalidOperationException("Quick adjustment QA expected actual parameter step selectors.");
        foreach (var selector in stepSelectors)
        {
            // Select the semantic value through the real ComboBox event path.
            var five = selector.Items.OfType<ComboBoxItem>().Single(item => item.Tag is double step && step == 5);
            selector.SelectedItem = five;
            if (selector.SelectedItem is not ComboBoxItem { Tag: double selected } || selected != 5)
                throw new InvalidOperationException("Parameter step selector did not select five units.");
        }
    }

    static bool ScrollTo(DependencyObject root, bool bottom)
    {
        bool changed = false;
        foreach (var scroll in Descendants(root).OfType<ScrollViewer>())
        {
            // Do not scroll the multiline content editor or editable font field.
            if (Ancestor<TextBox>(scroll) != null) continue;
            if (scroll.ScrollableHeight > .5) changed = true;
            if (bottom) scroll.ScrollToBottom(); else scroll.ScrollToTop();
        }
        return changed;
    }

    static void Layout(FrameworkElement target, double width, double height)
    { target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); target.UpdateLayout(); }

    static void SetDpi(Visual element, double scale)
    {
        // Unshown Window content can be a separate visual root even though it
        // has a logical Window parent. Start at the actual content tree.
        while (VisualTreeHelper.GetParent(element) is Visual parent) element = parent;
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
    }

    static void Capture(FrameworkElement target, string output, string name, double scale, FrameworkElement? captureRoot = null)
    {
        if (Math.Abs(VisualTreeHelper.GetDpi(target).DpiScaleX - scale) > .001)
            throw new InvalidOperationException("Capture DPI did not propagate to the actual control tree: " + name);
        // Render the actual visual root before cropping a docked child. Child
        // renders/VisualBrushes may retain ancestor offsets or layout clips and
        // silently produce transparent pixels after wrappers are introduced.
        // Root rendering also preserves the real parent viewport clips.
        captureRoot ??= target;
        var whole = new RenderTargetBitmap((int)Math.Ceiling(captureRoot.ActualWidth * scale), (int)Math.Ceiling(captureRoot.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        whole.Render(captureRoot);
        BitmapSource bitmap = whole;
        if (!ReferenceEquals(captureRoot, target))
        {
            Rect bounds = target.TransformToAncestor(captureRoot).TransformBounds(new Rect(target.RenderSize));
            int x = Math.Max(0, (int)Math.Round(bounds.Left * scale)), y = Math.Max(0, (int)Math.Round(bounds.Top * scale));
            int width = Math.Min(whole.PixelWidth - x, (int)Math.Round(bounds.Width * scale));
            int height = Math.Min(whole.PixelHeight - y, (int)Math.Round(bounds.Height * scale));
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Panel has no area inside the captured editor: " + name);
            bitmap = new CroppedBitmap(whole, new Int32Rect(x, y, width, height));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string file = name + ".png";
        using (var stream = File.Create(Path.Combine(output, file))) encoder.Save(stream);
        var report = new CaptureReport(file, target.ActualWidth, target.ActualHeight, scale, [], [], []);
        report.Image = InspectPixels(bitmap);
        if (report.Image.UnexpectedBlank)
            report.Issues.Add(new Issue("blank-capture", file, $"Opaque coverage={report.Image.VisibleCoverage:P1}, luminance range={report.Image.LuminanceRange:0.##}, distinct colors={report.Image.DistinctColorsUpTo64}; expected a rendered panel with text and controls."));
        MeasureGlyphs(target, report);
        InspectActionLabels(target, report);
        reports.Add(report);
    }

    static PixelEvidence InspectPixels(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        int visible = 0; double darkest = 255, lightest = 0;
        var colors = new HashSet<uint>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] <= 16) continue;
            visible++;
            double luminance = .0722 * pixels[i] + .7152 * pixels[i + 1] + .2126 * pixels[i + 2];
            darkest = Math.Min(darkest, luminance); lightest = Math.Max(lightest, luminance);
            if (colors.Count < 64) colors.Add((uint)(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16 | pixels[i + 3] << 24));
        }
        double coverage = visible / (double)(bitmap.PixelWidth * bitmap.PixelHeight);
        double range = visible == 0 ? 0 : lightest - darkest;
        return new PixelEvidence(coverage, range, colors.Count, coverage < .25 || range < 25 || colors.Count < 8);
    }

    static void MeasureGlyphs(FrameworkElement root, CaptureReport report)
    {
        foreach (var scroll in Descendants(root).OfType<ScrollViewer>().Where(s => Ancestor<TextBox>(s) == null && s.ScrollableHeight > .5))
            report.Scrolling.Add($"{scroll.GetType().Name}: viewport {scroll.ViewportHeight:0.##}, extent {scroll.ExtentHeight:0.##}, offset {scroll.VerticalOffset:0.##}");

        foreach (var element in Descendants(root).OfType<FrameworkElement>().Where(e => e is TextBlock or TextBox))
        {
            if (!VisibilityChain(element) || element.ActualWidth < .1 || element.ActualHeight < .1) continue;
            string text = element is TextBlock tb ? tb.Text : ((TextBox)element).Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var ink = GlyphInk(element);
            if (ink.IsEmpty) continue;
            // Only the control's own content area is checked. Ink crossing an
            // outer panel ScrollContentPresenter is ordinary viewport scrolling.
            FrameworkElement area = element;
            bool allowHorizontalScroll = false, allowVerticalScroll = false;
            if (element is TextBox box)
            {
                area = Descendants(box).OfType<ScrollContentPresenter>().FirstOrDefault() ?? (FrameworkElement)box;
                allowHorizontalScroll = box.TextWrapping == TextWrapping.NoWrap;
                allowVerticalScroll = box.AcceptsReturn;
            }
            var contentClip = VisualTreeHelper.GetClip(area);
            Rect availableLocal = contentClip?.Bounds ?? new Rect(area.RenderSize);
            Rect available = area == element ? availableLocal : area.TransformToAncestor(element).TransformBounds(availableLocal);
            const double tolerance = .9; // antialiasing and one physical pixel rounding
            bool vertical = !allowVerticalScroll && (ink.Top < available.Top - tolerance || ink.Bottom > available.Bottom + tolerance);
            bool horizontal = !allowHorizontalScroll && (ink.Left < available.Left - tolerance || ink.Right > available.Right + tolerance);
            if (element is TextBlock { TextTrimming: not TextTrimming.None }) horizontal = false;
            // A TextBlock can legally paint an overhanging hinted glyph outside
            // its arrange rectangle. This is not clipping unless an actual
            // visual/content clip cuts that ink. TextBox is measured separately
            // against its scrolling viewport, which is an actual content clip.
            if (element is TextBox && contentClip != null && (vertical || horizontal))
                report.Issues.Add(new Issue("field-content-clip", text, $"{element.GetType().Name}, ink {Format(ink)}, content {Format(available)}, vertical={vertical}, horizontal={horizontal}"));

            // A vertically centered presenter can paint all glyphs outside its
            // too-small allocation unless a parent clips it. Check real clips
            // up to the containing button/field, excluding outer panel scrolling.
            Rect visibleInk = ink;
            for (DependencyObject? parent = element; parent is Visual visual; parent = VisualTreeHelper.GetParent(parent))
            {
                bool outerScroll = visual is ScrollContentPresenter && Ancestor<TextBox>(visual) == null;
                var clip = VisualTreeHelper.GetClip(visual);
                if (clip == null && visual is UIElement { ClipToBounds: true } bounded) clip = new RectangleGeometry(new Rect(bounded.RenderSize));
                if (clip != null)
                {
                    var localInk = element == visual ? visibleInk : element.TransformToAncestor(visual).TransformBounds(visibleInk);
                    var bounds = clip.Bounds;
                    if (outerScroll)
                    {
                        // Ordinary scrolling removes ink from all later ancestor
                        // checks, not just this presenter's own clip. WPF can add
                        // an additional layout clip to its enclosing Grid at DPI
                        // boundaries; only the still-visible ink can be cut there.
                        double top = Math.Max(localInk.Top, bounds.Top);
                        double bottom = Math.Min(localInk.Bottom, bounds.Bottom);
                        if (bottom <= top) break;
                        localInk = new Rect(localInk.Left, top, localInk.Width, bottom - top);
                        visibleInk = element == visual ? localInk : visual.TransformToDescendant(element).TransformBounds(localInk);
                    }
                    bool clippedY = !allowVerticalScroll && !outerScroll && (localInk.Top < bounds.Top - tolerance || localInk.Bottom > bounds.Bottom + tolerance);
                    bool clippedX = !allowHorizontalScroll && (localInk.Left < bounds.Left - tolerance || localInk.Right > bounds.Right + tolerance);
                    if (clippedX || clippedY)
                        report.Issues.Add(new Issue("glyph-clip", text, $"{visual.GetType().Name} clip {Format(bounds)}, ink {Format(localInk)}"));
                }
                if (ReferenceEquals(parent, root)) break;
            }
        }
    }

    static void ValidateDetector(string output)
    {
        var blankBitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        if (!InspectPixels(blankBitmap).UnexpectedBlank) throw new InvalidOperationException("Capture guard failed to detect a transparent image.");
        var flatSurface = new DrawingVisual(); using (var context = flatSurface.RenderOpen()) context.DrawRectangle(Theme.Panel, null, new Rect(0, 0, 32, 32));
        blankBitmap.Render(flatSurface);
        if (!InspectPixels(blankBitmap).UnexpectedBlank) throw new InvalidOperationException("Capture guard failed to detect a featureless background.");
        var positive = new TextBlock { Text = "한글 ㅡ descender g", FontSize = 22, Width = 140, Height = 8, ClipToBounds = true };
        Layout(positive, 140, 8);
        var clipped = new CaptureReport("calibration-clipped", 140, 8, 1, [], [], []);
        var host = new Border { Child = positive }; Layout(host, 140, 8); MeasureGlyphs(host, clipped);
        if (!clipped.Issues.Any(i => i.Kind == "glyph-clip")) throw new InvalidOperationException("Detector failed to find a deliberately clipped glyph.");

        var panel = new StackPanel();
        foreach (var label in new[] { "Y #BCD9FA", "한글 가로획 ㅡ", "아래쪽으로 스크롤하는 레이블", "More labels" }) panel.Children.Add(Theme.Label(label));
        var normal = new ScrollViewer { Content = panel, Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Layout(normal, 170, 42);
        var expected = new CaptureReport("calibration-scroll", 170, 42, 1, [], [], []); MeasureGlyphs(normal, expected);
        if (expected.Issues.Count != 0) throw new InvalidOperationException("Detector mistook normal glyph overhang or outer vertical scrolling for clipping.");

        var clippedParent = new Grid { ClipToBounds = true };
        clippedParent.Children.Add(normal); SetDpi(clippedParent, 1.5); Layout(clippedParent, 170, 42);
        var nestedTop = new CaptureReport("calibration-nested-scroll-top", 170, 42, 1.5, [], [], []); MeasureGlyphs(clippedParent, nestedTop);
        normal.ScrollToBottom(); Layout(clippedParent, 170, 42);
        var nestedBottom = new CaptureReport("calibration-nested-scroll-bottom", 170, 42, 1.5, [], [], []); MeasureGlyphs(clippedParent, nestedBottom);
        if (nestedTop.Issues.Count != 0 || nestedBottom.Issues.Count != 0)
            throw new InvalidOperationException("Detector mistook scrolled ink outside an ancestor Grid for clipping.");

        var actions = new StackPanel();
        foreach (var label in new[] { "수정…", "수정...", "⋯", "레이어 이름 변경" }) actions.Children.Add(Theme.Button(label, () => { }));
        Layout(actions, 220, 180);
        var actionReport = new CaptureReport("calibration-action-labels", 220, 180, 1, [], [], []); InspectActionLabels(actions, actionReport);
        if (actionReport.Issues.Count(i => i.Kind == "abbreviated-action-label") != 2 || actionReport.Actions.Count != 4)
            throw new InvalidOperationException("Action-label detector must flag literal ellipses while allowing the overflow-menu glyph and complete labels.");
        File.WriteAllText(Path.Combine(output, "detector-calibration.json"), JsonSerializer.Serialize(new[] { clipped, expected, nestedTop, nestedBottom, actionReport }, jsonOptions));
    }

    static void InspectActionLabels(FrameworkElement root, CaptureReport report)
    {
        foreach (var button in Descendants(root).OfType<Button>())
        {
            if (!VisibilityChain(button) || button.ActualWidth <= 0 || button.ActualHeight <= 0) continue;
            // A layer's document name is user content, not a UI command label.
            // Footer layer actions remain in scope because they are outside rows.
            if (Ancestor<LayerRow>(button) != null) continue;
            string label = button.Content as string ?? string.Join(" · ", Descendants(button).OfType<TextBlock>()
                .Where(VisibilityChain).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
            string automation = AutomationProperties.GetName(button);
            bool abbreviated = label.Contains('…') || label.Contains("...", StringComparison.Ordinal);
            bool visible = VisibleArea(button, root);
            report.Actions.Add(new ActionLabelReport(label, automation, button.ToolTip as string ?? "", visible, button.IsEnabled, abbreviated));
            if (abbreviated)
                report.Issues.Add(new Issue("abbreviated-action-label", label, $"Literal ellipsis in a panel command; visible in this capture={visible}, automation name={automation}"));
        }
    }

    static bool VisibleArea(FrameworkElement element, FrameworkElement root)
    {
        Rect visible = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        visible.Intersect(new Rect(root.RenderSize));
        for (DependencyObject? parent = element; parent is Visual visual; parent = VisualTreeHelper.GetParent(parent))
        {
            if (VisualTreeHelper.GetClip(visual) is { } clip)
            {
                Rect bounds = ReferenceEquals(visual, root) ? clip.Bounds : visual.TransformToAncestor(root).TransformBounds(clip.Bounds);
                visible.Intersect(bounds);
            }
            if (ReferenceEquals(parent, root)) break;
        }
        return !visible.IsEmpty && visible.Width > .5 && visible.Height > .5;
    }

    static Rect GlyphInk(FrameworkElement owner)
    {
        Rect result = Rect.Empty;
        foreach (var visual in new DependencyObject[] { owner }.Concat(Descendants(owner)).OfType<Visual>())
        {
            if (VisualTreeHelper.GetDrawing(visual) is not { } drawing) continue;
            foreach (Rect local in DrawingInk(drawing, Matrix.Identity))
                result.Union(ReferenceEquals(visual, owner) ? local : visual.TransformToAncestor(owner).TransformBounds(local));
        }
        return result;
    }

    static IEnumerable<Rect> DrawingInk(Drawing drawing, Matrix transform)
    {
        if (drawing is GlyphRunDrawing glyph && !glyph.Bounds.IsEmpty) yield return Rect.Transform(glyph.Bounds, transform);
        if (drawing is not DrawingGroup group) yield break;
        var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
        foreach (var child in group.Children)
            foreach (var bounds in DrawingInk(child, next)) yield return bounds;
    }

    static string Format(Rect value) => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.##},{value.Y:0.##},{value.Width:0.##},{value.Height:0.##}");
    static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }
    static bool VisibilityChain(DependencyObject item)
    {
        for (DependencyObject? current = item; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
        {
            var child = VisualTreeHelper.GetChild(item, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, Private) ?? throw new MissingFieldException(name);
    static void Invoke(MainWindow window, string name, params object?[] arguments) => (typeof(MainWindow).GetMethod(name, Private) ?? throw new MissingMethodException(name)).Invoke(window, arguments);
    record PanelCase(string Name, int Page, Layer Layer);
    record Issue(string Kind, string Text, string Detail);
    record ActionLabelReport(string Label, string AutomationName, string ToolTip, bool VisibleInCapture, bool Enabled, bool HasLiteralEllipsis);
    record PixelEvidence(double VisibleCoverage, double LuminanceRange, int DistinctColorsUpTo64, bool UnexpectedBlank);
    record CaptureReport(string File, double Width, double Height, double DpiScale, List<Issue> Issues, List<string> Scrolling, List<ActionLabelReport> Actions)
    { public PixelEvidence? Image { get; set; } }
}
