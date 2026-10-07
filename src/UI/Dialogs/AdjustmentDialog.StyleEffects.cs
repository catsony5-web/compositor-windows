using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Controls of the design-style adjustment layers (한계값, 망점, 종이·인쇄 질감, 빛 번짐) and their
// actual-size view: a fitted preview cannot show halftone dots or paper grain, so a crop at one
// document pixel per screen pixel follows every setting; drag it to look at another spot.
public sealed partial class AdjustmentDialog
{
    internal const double DetailHeight = 176;
    Image? detailImage;
    Border? detailFrame;
    TextBlock? detailCaption;
    Point detailCenter;
    CancellationTokenSource? detailCts;
    long detailVersion;
    int detailPending;
    internal BitmapSource? DetailPreview => detailImage?.Source as BitmapSource;
    internal Point DetailCenter => detailCenter;
    internal bool DetailPending => detailPending > 0;

    internal static string StyleEffectTitle(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.Threshold => "한계값", AdjustmentKind.Halftone => "망점 (하프톤)", AdjustmentKind.PaperTexture => "종이·인쇄 질감", _ => "빛 번짐"
    };

    static string Hex(uint argb) => (argb >> 24) == 255 ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";

    void AddStyleEffectControls(StackPanel controls, AdjustmentKind kind)
    {
        // Large documents preview at the stage's resolution: the effects are drawn for that scale
        // (screens, grain and radii stay in document pixels), so nothing is lost by not drawing every pixel.
        if (Math.Max(original.Width, original.Height) > PreviewMaxSide) Renderer = RenderFitted;
        controls.Children.Add(BuildDetail());
        void Slider(string label, double min, double max, Func<AdjustmentSpec, double> get, Action<double> set, string tip, double minimumStep = 0, bool logarithmic = false)
        {
            var control = new ParameterSlider(label, min, max, get(Spec), get(new AdjustmentSpec { Kind = kind }), showStepControls: !logarithmic, minimumStep: minimumStep, logarithmic: logarithmic) { ToolTip = tip };
            parameterControls.Add(control);
            synchronizeControls.Add(() => { if (!preservePendingInputs || !control.HasPendingInput) control.SetValue(get(Spec)); });
            control.Changed += value => { if (synchronizing) return; set(value); SyncControls(preservePending: true); Schedule(); };
            controls.Children.Add(control);
        }
        CheckBox Toggle(string label, Func<AdjustmentSpec, bool> get, Func<bool, AdjustmentSpec> set, string tip)
        {
            var box = new CheckBox { Content = label, IsChecked = get(Spec), Foreground = Theme.Text, Margin = new Thickness(3, 4, 3, 8), ToolTip = tip };
            AutomationProperties.SetName(box, label);
            synchronizeControls.Add(() => box.IsChecked = get(Spec));
            box.Click += (_, _) => { Spec = set(box.IsChecked == true); SyncControls(); Schedule(); };
            controls.Children.Add(box); return box;
        }
        void ColorRow(string label, Func<AdjustmentSpec, uint> get, Func<uint, AdjustmentSpec> set, string tip)
        {
            Button? chip = null;
            chip = PropertyRows.ColorChip(DocumentFeatures.Color(get(Spec)), Hex(get(Spec)), () =>
            {
                var picked = Dialogs.ColorPicker(this, DocumentFeatures.Color(get(Spec)), label);
                if (picked is not { } c) return;
                Spec = set((uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B)); SyncControls(); Schedule();
            }, label);
            chip.ToolTip = tip;
            synchronizeControls.Add(() => PropertyRows.SetChipColor(chip, DocumentFeatures.Color(get(Spec)), Hex(get(Spec))));
            controls.Children.Add(PropertyRows.Field(label, chip));
        }
        static uint Opaque(uint argb, bool opaque) => opaque ? argb | 0xFF000000 : argb & 0x00FFFFFF;
        switch (kind)
        {
            case AdjustmentKind.Threshold:
                Slider("검정·흰색 경계", 0, 255, s => s.Threshold.Level, n => Spec = Spec with { Threshold = Spec.Threshold with { Level = n } },
                    "이 밝기보다 어두운 곳은 검정, 같거나 밝은 곳은 흰색이 됩니다.");
                Slider("부드러운 가장자리", 0, 64, s => s.Threshold.Smoothness, n => Spec = Spec with { Threshold = Spec.Threshold with { Smoothness = n } },
                    "경계 근처의 밝기를 회색 단계로 남겨 가장자리를 매끄럽게 합니다. 0이면 순수한 흑백입니다.");
                Toggle("원래 투명도 유지", s => s.Threshold.KeepAlpha, on => Spec with { Threshold = Spec.Threshold with { KeepAlpha = on } },
                    "끄면 반투명한 가장자리도 50%를 기준으로 완전히 보이거나 숨겨 비트맵처럼 딱딱해집니다.");
                break;
            case AdjustmentKind.Halftone:
                controls.Children.Add(DialogShell.FieldLabel("망점 모양"));
                var shapes = new SegmentedChoice<HalftoneShape>([(HalftoneShape.Round, "원형"), (HalftoneShape.Line, "선"), (HalftoneShape.Square, "정사각형")], Spec.Halftone.Shape) { Margin = new Thickness(2, 0, 2, 10) };
                AutomationProperties.SetName(shapes, "망점 모양");
                shapes.Changed += shape => { if (synchronizing) return; Spec = Spec with { Halftone = Spec.Halftone with { Shape = shape } }; Schedule(); };
                synchronizeControls.Add(() => shapes.Select(Spec.Halftone.Shape));
                controls.Children.Add(shapes);
                Slider("망점 간격 (px)", 2, 256, s => s.Halftone.CellSize, n => Spec = Spec with { Halftone = Spec.Halftone with { CellSize = n } },
                    "망점 한 칸의 크기(문서 픽셀)입니다. 화면 배율이나 내보내기 배율과 관계없이 같은 크기로 인쇄됩니다.", logarithmic: true);
                // Screens repeat every 180° (dots also every 90°): any stored angle shows as its 0–180° equivalent.
                Slider("각도 (°)", 0, 180, s => (s.Halftone.Angle % 180 + 180) % 180, n => Spec = Spec with { Halftone = Spec.Halftone with { Angle = n } },
                    "망점 줄의 기울기입니다. 신문 인쇄의 흑백 망점은 보통 45°입니다.");
                ColorRow("잉크 색", s => s.Halftone.InkArgb, value => Spec with { Halftone = Spec.Halftone with { InkArgb = value } }, "망점을 찍는 색입니다.");
                Toggle("원래 색으로 망점 찍기", s => (s.Halftone.InkArgb >> 24) == 0, on => Spec with { Halftone = Spec.Halftone with { InkArgb = Opaque(Spec.Halftone.InkArgb, !on) } },
                    "켜면 망점이 잉크 색 대신 아래 이미지의 색을 띱니다.");
                ColorRow("종이 색", s => s.Halftone.PaperArgb, value => Spec with { Halftone = Spec.Halftone with { PaperArgb = value } }, "망점 사이를 채우는 색입니다.");
                Toggle("망점 사이에 아래 이미지 보이기", s => (s.Halftone.PaperArgb >> 24) == 0, on => Spec with { Halftone = Spec.Halftone with { PaperArgb = Opaque(Spec.Halftone.PaperArgb, !on) } },
                    "켜면 종이 색 대신 아래 이미지가 망점 사이에 그대로 보입니다.");
                break;
            case AdjustmentKind.PaperTexture:
                controls.Children.Add(Theme.Section("종이"));
                ColorRow("종이 색", s => s.Paper.TintArgb, value => Spec with { Paper = Spec.Paper with { TintArgb = value } }, "흰 부분이 이 색 종이에 인쇄한 것처럼 바뀝니다. 검정은 그대로입니다.");
                Slider("종이 색 농도 (%)", 0, 100, s => s.Paper.Tint * 100, n => Spec = Spec with { Paper = Spec.Paper with { Tint = n / 100 } }, "종이 색을 얼마나 입힐지 정합니다.");
                Slider("종이 결 (%)", 0, 100, s => s.Paper.Grain * 100, n => Spec = Spec with { Paper = Spec.Paper with { Grain = n / 100 } }, "종이의 고운 결과 구름 같은 얼룩을 더합니다.");
                Slider("섬유 (%)", 0, 100, s => s.Paper.Fibers * 100, n => Spec = Spec with { Paper = Spec.Paper with { Fibers = n / 100 } }, "종이 속 가는 섬유 가닥을 보이게 합니다.");
                Slider("질감 크기 (px)", .5, 32, s => s.Paper.Scale, n => Spec = Spec with { Paper = Spec.Paper with { Scale = n } },
                    "가장 고운 결의 크기(문서 픽셀)입니다. 섬유와 반점도 함께 커집니다.", logarithmic: true);
                controls.Children.Add(Theme.Section("복사·인쇄"));
                Slider("토너 반점 (%)", 0, 100, s => s.Paper.Toner * 100, n => Spec = Spec with { Paper = Spec.Paper with { Toner = n / 100 } }, "복사기 토너 얼룩처럼 밝은 곳에 검은 반점, 진한 곳에 빠진 자국을 더합니다.");
                Slider("복사 줄무늬 (%)", 0, 100, s => s.Paper.Streaks * 100, n => Spec = Spec with { Paper = Spec.Paper with { Streaks = n / 100 } }, "복사할 때 생기는 세로 줄무늬를 더합니다.");
                controls.Children.Add(Theme.Section("가장자리"));
                Slider("거친 가장자리 (%)", 0, 100, s => s.Paper.Edges * 100, n => Spec = Spec with { Paper = Spec.Paper with { Edges = n / 100 } }, "불규칙하게 타거나 바랜 종이 가장자리를 만듭니다.");
                Slider("가장자리 폭 (%)", 1, 50, s => s.Paper.EdgeWidth * 100, n => Spec = Spec with { Paper = Spec.Paper with { EdgeWidth = n / 100 } }, "짧은 변 길이에 대한 가장자리 효과의 폭입니다.");
                ColorRow("가장자리 색", s => s.Paper.EdgeArgb, value => Spec with { Paper = Spec.Paper with { EdgeArgb = value } }, "어두운 색은 탄 가장자리, 흰색은 인쇄되지 않은 여백처럼 보입니다.");
                controls.Children.Add(Theme.Section("무늬"));
                Slider("무늬 번호", 0, 9999, s => Math.Clamp(s.Paper.Seed, 0, 9999), n => Spec = Spec with { Paper = Spec.Paper with { Seed = (int)Math.Round(n) } },
                    "같은 번호는 언제나 같은 무늬를 만듭니다. 번호를 바꾸면 다른 무늬가 나옵니다.", minimumStep: 1);
                var shuffle = Theme.Button("다른 무늬", () => { Spec = Spec with { Paper = Spec.Paper with { Seed = (Spec.Paper.Seed + 7919) % 10000 } }; SyncControls(); Schedule(); }, "무늬 번호를 바꿔 다른 섬유·반점 배치를 만듭니다.");
                shuffle.HorizontalAlignment = HorizontalAlignment.Left; shuffle.Margin = new Thickness(2, 0, 2, 8); controls.Children.Add(shuffle);
                break;
            case AdjustmentKind.Glow:
                Slider("빛으로 볼 밝기 (%)", 0, 100, s => s.Glow.Threshold * 100, n => Spec = Spec with { Glow = Spec.Glow with { Threshold = n / 100 } },
                    "가장 강한 색 채널이 이 밝기를 넘는 곳만 빛을 냅니다. 낮추면 더 많은 곳이 번집니다.");
                Slider("번짐 반경 (px)", 1, 1000, s => s.Glow.Radius, n => Spec = Spec with { Glow = Spec.Glow with { Radius = n } },
                    "빛이 퍼지는 거리(문서 픽셀)입니다.", logarithmic: true);
                Slider("세기", 0, 4, s => s.Glow.Intensity, n => Spec = Spec with { Glow = Spec.Glow with { Intensity = n } },
                    "번지는 빛의 양입니다. 0이면 이미지가 바뀌지 않습니다.");
                ColorRow("빛 색", s => s.Glow.TintArgb | 0xFF000000, value => Spec with { Glow = Spec.Glow with { TintArgb = value & 0x00FFFFFF | Spec.Glow.TintArgb & 0xFF000000 } },
                    "번지는 빛에 입힐 색입니다.");
                Slider("빛 색 농도 (%)", 0, 100, s => (s.Glow.TintArgb >> 24) / 2.55, n => Spec = Spec with { Glow = Spec.Glow with { TintArgb = (uint)Math.Round(n * 2.55) << 24 | Spec.Glow.TintArgb & 0xFFFFFF } },
                    "0이면 각 조명의 원래 색으로 번집니다.");
                break;
        }
        var reset = Theme.Button("기본값으로 되돌리기", () =>
        {
            var defaults = new AdjustmentSpec();
            Spec = Spec with { Threshold = defaults.Threshold, Halftone = defaults.Halftone, Paper = defaults.Paper, Glow = defaults.Glow }; SyncControls(); Schedule();
        }, "이 효과의 모든 값을 처음 값으로 되돌립니다.");
        reset.HorizontalAlignment = HorizontalAlignment.Left; reset.Margin = new Thickness(2, 6, 2, 4);
        controls.Children.Add(reset);
        Closed += (_, _) => { detailVersion++; detailCts?.Cancel(); };
    }

    Raster RenderFitted(Document document, CancellationToken token)
    {
        double scale = Math.Min(1, PreviewMaxSide / (double)Math.Max(document.Width, document.Height));
        int width = Math.Max(1, (int)Math.Round(document.Width * scale)), height = Math.Max(1, (int)Math.Round(document.Height * scale));
        return DesignRenderer.RenderScaled(document, new Rect(0, 0, document.Width, document.Height), width, height, token);
    }

    FrameworkElement BuildDetail()
    {
        detailCenter = new Point(original.Width / 2.0, original.Height / 2.0);
        detailImage = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(detailImage, BitmapScalingMode.NearestNeighbor);
        detailFrame = new Border
        {
            Height = DetailHeight, CornerRadius = new CornerRadius(8), Background = Theme.Stage, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = detailImage, Cursor = Cursors.SizeAll, Margin = new Thickness(2, 0, 2, 4),
            ToolTip = "효과를 실제 크기(문서 1픽셀 = 화면 1픽셀)로 보여 줍니다. 끌어서 다른 곳을 봅니다."
        };
        AutomationProperties.SetName(detailFrame, "실제 크기 미리보기");
        Point? grabbed = null;
        detailFrame.MouseLeftButtonDown += (_, e) => { grabbed = e.GetPosition(detailFrame); detailFrame.CaptureMouse(); e.Handled = true; };
        detailFrame.MouseMove += (_, e) =>
        {
            if (grabbed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(detailFrame); grabbed = now;
            MoveDetail(detailCenter - (now - start) / DetailScale());
        };
        detailFrame.MouseLeftButtonUp += (_, _) => { grabbed = null; detailFrame.ReleaseMouseCapture(); };
        detailFrame.SizeChanged += (_, e) => { if (e.WidthChanged && e.PreviousSize.Width > 0) ScheduleDetail(Spec); };
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        detailCaption = Theme.Label("실제 크기 · 끌어서 위치 이동", Theme.CaptionSize, Theme.Muted); detailCaption.Margin = new Thickness(3, 0, 3, 4);
        panel.Children.Add(detailCaption); panel.Children.Add(detailFrame);
        return panel;
    }

    double DetailScale() => detailFrame is { } frame && PresentationSource.FromVisual(frame) is { CompositionTarget: { } target } ? target.TransformToDevice.M11 : 1;

    internal void MoveDetail(Point center)
    {
        detailCenter = new Point(Math.Clamp(center.X, 0, original.Width), Math.Clamp(center.Y, 0, original.Height));
        ScheduleDetail(Spec, TimeSpan.FromMilliseconds(16));
    }

    // The detail area in document pixels and its size in device pixels; null before the frame has a size.
    (Rect Area, int Width, int Height) DetailRequest()
    {
        double dpi = DetailScale();
        double frameWidth = detailFrame is { ActualWidth: > 8 } frame ? frame.ActualWidth - 2 : 300, frameHeight = DetailHeight - 2;
        int width = Math.Max(1, (int)Math.Round(frameWidth * dpi)), height = Math.Max(1, (int)Math.Round(frameHeight * dpi));
        // One document pixel per device pixel, kept inside the document when it is larger than the frame.
        double areaWidth = Math.Min(width, original.Width), areaHeight = Math.Min(height, original.Height);
        double left = Math.Clamp(detailCenter.X - areaWidth / 2, 0, original.Width - areaWidth), top = Math.Clamp(detailCenter.Y - areaHeight / 2, 0, original.Height - areaHeight);
        return (new Rect(left, top, areaWidth, areaHeight), (int)Math.Round(areaWidth), (int)Math.Round(areaHeight));
    }

    /// <summary>Renders the actual-size view at once (offscreen captures and checks).</summary>
    internal void RenderDetailNow()
    {
        if (detailImage == null) return;
        var (area, width, height) = DetailRequest();
        var document = PreviewDocument(original, editingId, Spec, true, selectionMask);
        ShowDetail(DesignRenderer.Render(document, area, width, height));
    }

    void ShowDetail(Raster raster)
    {
        if (detailImage == null) return;
        var bitmap = raster.Bitmap(96 * DetailScale());
        detailImage.Source = bitmap;
    }

    void ScheduleDetail(AdjustmentSpec spec) => ScheduleDetail(spec, TimeSpan.FromMilliseconds(60));

    async void ScheduleDetail(AdjustmentSpec spec, TimeSpan delay)
    {
        if (detailImage == null || closed) return;
        long generation = ++detailVersion; detailCts?.Cancel(); var cts = detailCts = new CancellationTokenSource(); detailPending++;
        try
        {
            await Task.Delay(delay, cts.Token);
            // Like the main preview, the view only updates from the window's own thread.
            if (generation != detailVersion || closed || !Dispatcher.CheckAccess()) return;
            var (area, width, height) = DetailRequest();
            var document = PreviewDocument(original, editingId, spec, true, selectionMask);
            var raster = await CompatibilityImport.OnSta(() => DesignRenderer.Render(document, area, width, height, cts.Token), cts.Token);
            if (generation == detailVersion && !closed && Dispatcher.CheckAccess()) ShowDetail(raster);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == detailVersion && !closed && detailCaption != null && Dispatcher.CheckAccess()) detailCaption.Text = Loc.Format("실제 크기 미리보기를 만들지 못했습니다: {0}", e.Message); }
        finally { detailPending--; if (ReferenceEquals(detailCts, cts)) detailCts = null; cts.Dispose(); }
    }
}
