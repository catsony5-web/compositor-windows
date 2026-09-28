using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Compositor.Windows;

// Status bar: tool on the left; document size, object count, RGB/CMYK view and the
// zoom control on the right. Viewing controls live here so the title bar and tool
// options stay about editing.
public sealed partial class MainWindow
{
    internal static readonly double[] ZoomSteps = [.0625, .125, .25, 1 / 3d, .5, 2 / 3d, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64];
    static readonly (string Label, double Zoom)[] ZoomChoices = [("화면에 맞춤", 0), ("25%", .25), ("50%", .5), ("100%", 1), ("200%", 2), ("400%", 4), ("800%", 8)];
    readonly TextBlock objectCount = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    ComboBox? zoomBox;
    Button? rgbView, cmykView;
    bool updatingZoomBox;
    FrameworkElement? viewControls;

    FrameworkElement BuildStatusBar()
    {
        var bar = new DockPanel { Background = Theme.Header, LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        documentInfo.VerticalAlignment = VerticalAlignment.Center; documentInfo.Margin = new Thickness(8, 0, 12, 0); right.Children.Add(documentInfo);
        objectCount.VerticalAlignment = VerticalAlignment.Center; objectCount.Margin = new Thickness(0, 0, 10, 0); right.Children.Add(objectCount);
        // View controls appear with a document; the start screen keeps the bar quiet.
        var view = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        view.Children.Add(StatusDivider());
        view.Children.Add(DocumentControl(BuildProofToggle()));
        view.Children.Add(StatusDivider());
        view.Children.Add(DocumentControl(BuildZoomControl()));
        viewControls = view; right.Children.Add(view);
        DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right);
        status.Margin = new Thickness(18, 0, 8, 0); status.Foreground = Theme.Muted; status.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(status);
        return bar;
    }

    static Border StatusDivider() => new() { Width = 1, Height = 14, Background = Theme.Line, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    // RGB is the editing space; CMYK previews print color without changing pixels.
    FrameworkElement BuildProofToggle()
    {
        var track = new StackPanel { Orientation = Orientation.Horizontal };
        Button Segment(string label, string name, bool proof)
        {
            var button = Theme.Styled(Theme.Button(label, () => Guard(() => { try { SetProof(proof); } finally { UpdateProofButtons(); } }), name), "GhostButton");
            button.MinHeight = 20; button.Height = 20; button.Padding = new Thickness(8, 0, 8, 0); button.Margin = new Thickness(0); button.FontSize = Theme.CaptionSize;
            AutomationProperties.SetName(button, name); track.Children.Add(button); return button;
        }
        rgbView = Segment("RGB", "RGB 편집 화면 보기", false);
        cmykView = Segment("CMYK", "CMYK 인쇄색 미리보기", true);
        UpdateProofButtons();
        return new Border { Background = Theme.Input, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = track };
    }

    FrameworkElement BuildZoomControl()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Icon(string glyph, Action run, string tip)
        {
            var button = Theme.IconButton(glyph, () => { if (HasDocument) Guard(run); }, tip, 22, 13);
            panel.Children.Add(button); return button;
        }
        Icon("M5 12H19", () => StepZoom(-1), "축소 · Ctrl+-");
        zoomBox = new ComboBox { IsEditable = true, Width = 78, MinHeight = 20, Height = 22, Padding = new Thickness(6, 0, 4, 0), FontSize = Theme.CaptionSize, Margin = new Thickness(2, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "배율 · 숫자를 입력하고 Enter" };
        foreach (var (label, _) in ZoomChoices) zoomBox.Items.Add(label);
        AutomationProperties.SetName(zoomBox, "화면 배율");
        zoomBox.SelectionChanged += (_, _) =>
        {
            if (updatingZoomBox || zoomBox.SelectedIndex < 0 || !HasDocument) return;
            var (_, zoom) = ZoomChoices[zoomBox.SelectedIndex];
            Guard(() => { if (zoom == 0) FitView(); else SetZoom(zoom, resetPan: zoom == 1); });
        };
        zoomBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitZoomText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { UpdateZoomBox(); canvas.Focus(); e.Handled = true; }
        };
        zoomBox.LostKeyboardFocus += (_, e) => { if (!zoomBox.IsKeyboardFocusWithin) UpdateZoomBox(); };
        panel.Children.Add(zoomBox);
        Icon(Theme.Glyphs.Plus, () => StepZoom(1), "확대 · Ctrl++");
        Icon(Theme.Glyphs.Fit, FitView, "화면에 맞춤 · Ctrl+0");
        return panel;
    }

    // Typed values accept "150", "150%" or "1.5x"; anything else restores the current zoom.
    internal bool CommitZoomText()
    {
        if (zoomBox == null || !HasDocument) return false;
        string text = zoomBox.Text.Trim().TrimEnd('%').Trim();
        double? zoom = text.EndsWith('x') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) ? factor
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ? percent / 100 : null;
        if (zoom is not { } value || !double.IsFinite(value) || value <= 0) { UpdateZoomBox(); status.Text = "배율은 1~6400% 사이 숫자로 입력하세요."; return false; }
        SetZoom(value); canvas.Focus(); return true;
    }

    double MaxZoom => canvas.DesignMode ? 64 : 16;

    internal void SetZoom(double zoom, bool resetPan = false)
    {
        if (!HasDocument) return;
        zoom = Math.Clamp(zoom, .01, MaxZoom);
        if (resetPan) { canvas.Zoom = zoom; canvas.Pan = new(); canvas.InvalidateVisual(); }
        else canvas.ZoomAt(zoom / canvas.Zoom, new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        UpdateStatus();
    }

    internal void StepZoom(int direction)
    {
        double current = canvas.Zoom;
        double next = direction > 0 ? ZoomSteps.FirstOrDefault(step => step > current * 1.001, MaxZoom) : ZoomSteps.LastOrDefault(step => step < current / 1.001, ZoomSteps[0]);
        SetZoom(next);
    }

    internal void FitView() { if (!HasDocument) return; canvas.Fit(); canvas.InvalidateVisual(); UpdateStatus(); }

    void ActualSize() => SetZoom(1, resetPan: true);

    void UpdateZoomBox()
    {
        if (zoomBox == null) return;
        updatingZoomBox = true;
        try { zoomBox.SelectedIndex = -1; zoomBox.Text = HasDocument ? $"{canvas.Zoom * 100:0.#}%" : ""; }
        finally { updatingZoomBox = false; }
    }

    void UpdateProofButtons()
    {
        foreach (var (button, on) in new[] { (rgbView, !cmykProof), (cmykView, cmykProof) })
        {
            if (button == null) continue;
            button.Background = on ? Theme.Surface : System.Windows.Media.Brushes.Transparent;
            button.Foreground = on ? Theme.Text : Theme.Muted;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (cmykView != null) cmykView.ToolTip = "RGB 원본을 유지하며 인쇄색 확인 · " + (proofProfile ?? "Windows 기본 CMYK 프로필") + " · Ctrl+Shift+Y\nCMYK 파일 저장: 파일 → 인쇄용 CMYK 내보내기";
    }
}
