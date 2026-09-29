using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

/// <summary>How an adjustment preview shows the image before and after the adjustment.</summary>
public enum CompareMode { Result, Split, SideBySide, Toggle }

/// <summary>
/// Adjustment preview stage. Draws the "after" image alone, both images in one frame divided by a
/// draggable bar, both images side by side at the same scale, or one image that switches between them.
/// Before and after always share one fitted rectangle size, so the two states line up pixel for pixel.
/// </summary>
public sealed class BeforeAfterView : FrameworkElement
{
    /// <summary>Space between the stage edge and the image, matching the former preview margin.</summary>
    public const double Inset = 12;
    /// <summary>Space between the two side-by-side cells.</summary>
    public const double Gap = 12;
    const double HandleRadius = 13, BadgeInset = 8;
    internal const string BeforeText = "보정 전", AfterText = "보정 후", PendingText = "미리보기 계산 중…", BeforeFailedText = "보정 전 이미지를 만들지 못했습니다";

    BitmapSource? before, after;
    CompareMode mode;
    double divider = .5;
    bool showBefore, holdBefore, dragging, clickArmed, keyboardCue, beforeFailed;

    /// <summary>Raised when the viewer clicks the image (or presses Space/Enter) in <see cref="CompareMode.Toggle"/>.</summary>
    public event Action? ToggleRequested;

    public BeforeAfterView()
    {
        Focusable = true; FocusVisualStyle = null; ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(this, "보정 전후 미리보기");
    }

    public BitmapSource? Before { get => before; set { before = value; InvalidateVisual(); } }
    public BitmapSource? After { get => after; set { after = value; InvalidateVisual(); } }
    /// <summary>The "before" image could not be rendered; its cell says so instead of showing the calculating caption.</summary>
    public bool BeforeFailed { get => beforeFailed; set { if (beforeFailed == value) return; beforeFailed = value; InvalidateVisual(); } }

    public CompareMode Mode
    {
        get => mode;
        set
        {
            if (mode == value) return;
            mode = value; dragging = clickArmed = false; if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = value switch { CompareMode.Split => Cursors.SizeWE, CompareMode.Toggle => Cursors.Hand, _ => null };
            InvalidateVisual();
        }
    }

    /// <summary>Divider position across the image, 0 (all after) to 1 (all before). Non-finite values are ignored.</summary>
    public double Divider
    {
        get => divider;
        set { if (!double.IsFinite(value)) return; double next = Math.Clamp(value, 0, 1); if (next == divider) return; divider = next; InvalidateVisual(); }
    }

    /// <summary>Persistent single-image state: show the image without the adjustment.</summary>
    public bool ShowBefore { get => showBefore; set { if (showBefore == value) return; showBefore = value; InvalidateVisual(); } }

    /// <summary>Momentary single-image state while a hold key or button is pressed.</summary>
    public bool HoldBefore { get => holdBefore; set { if (holdBefore == value) return; holdBefore = value; InvalidateVisual(); } }

    /// <summary>Single-image modes (result and toggle) can show either state; the others always show both.</summary>
    public bool ShowsSingleImage => mode is CompareMode.Result or CompareMode.Toggle;
    public bool ShowsBoth => !ShowsSingleImage;

    /// <summary>True when the single image currently on screen is the "before" state.</summary>
    public bool DisplaysBefore => ShowsSingleImage && (showBefore || holdBefore);

    /// <summary>The state badge text in toggle mode (and in result mode while the before image is shown).</summary>
    internal string? StateLabel => mode == CompareMode.Toggle || DisplaysBefore ? (DisplaysBefore ? BeforeText : AfterText) : null;

    /// <summary>
    /// Side-by-side cells go top and bottom when that shows the image larger: a tall preview area, or a wide
    /// photo in a roughly square one. Ties stay left and right.
    /// </summary>
    internal static bool StackVertically(Size area, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return area.Height > area.Width;
        double across = Math.Min((area.Width - Gap) / 2 / imageWidth, area.Height / imageHeight);
        double stacked = Math.Min(area.Width / imageWidth, (area.Height - Gap) / 2 / imageHeight);
        return stacked > across + 1e-9;
    }

    PixelSize ImagePixels => after is { } a ? new(a.PixelWidth, a.PixelHeight) : before is { } b ? new(b.PixelWidth, b.PixelHeight) : new(0, 0);
    readonly record struct PixelSize(int Width, int Height);

    /// <summary>Rectangles the image occupies for a stage of <paramref name="size"/>: one rectangle, or two for side by side.</summary>
    internal Rect[] ImageRects(Size size)
    {
        var pixels = ImagePixels;
        if (pixels.Width <= 0 || pixels.Height <= 0) return [];
        var area = new Rect(Inset, Inset, Math.Max(0, size.Width - 2 * Inset), Math.Max(0, size.Height - 2 * Inset));
        if (mode != CompareMode.SideBySide) return [Fit(pixels, area)];
        bool vertical = StackVertically(area.Size, pixels.Width, pixels.Height);
        var first = vertical ? new Rect(area.X, area.Y, area.Width, Math.Max(0, (area.Height - Gap) / 2)) : new Rect(area.X, area.Y, Math.Max(0, (area.Width - Gap) / 2), area.Height);
        var second = vertical ? first with { Y = first.Bottom + Gap } : first with { X = first.Right + Gap };
        return [Fit(pixels, first), Fit(pixels, second)];
    }

    // Uniform fit rounded to whole units; equal cells therefore give identical sizes for both states.
    static Rect Fit(PixelSize pixels, Rect cell)
    {
        double scale = Math.Min(cell.Width / pixels.Width, cell.Height / pixels.Height);
        if (!double.IsFinite(scale) || scale <= 0) return new Rect(cell.X, cell.Y, 0, 0);
        double width = Math.Max(1, Math.Round(pixels.Width * scale)), height = Math.Max(1, Math.Round(pixels.Height * scale));
        return new Rect(Math.Round(cell.X + (cell.Width - width) / 2), Math.Round(cell.Y + (cell.Height - height) / 2), width, height);
    }

    /// <summary>X coordinate of the divider for an image rectangle.</summary>
    internal double DividerX(Rect image) => image.X + Math.Round(divider * image.Width);

    /// <summary>Moves the divider to the point's horizontal position over the image (mouse drag path).</summary>
    internal void MoveDividerTo(Point point)
    {
        var rects = ImageRects(RenderSize);
        if (rects.Length == 0 || rects[0].Width <= 0) return;
        Divider = (point.X - rects[0].X) / rects[0].Width;
    }

    /// <summary>Keyboard handling shared by real key events and self-tests. Returns true when the key was used.</summary>
    internal bool HandleKey(Key key, bool large)
    {
        if (mode == CompareMode.Split)
        {
            double step = large ? .1 : .01;
            switch (key)
            {
                case Key.Left: Divider = Math.Round((divider - step) * 1000) / 1000; return true;
                case Key.Right: Divider = Math.Round((divider + step) * 1000) / 1000; return true;
                case Key.Home: Divider = 0; return true;
                case Key.End: Divider = 1; return true;
            }
        }
        if (mode == CompareMode.Toggle && key is Key.Space or Key.Enter) { ToggleRequested?.Invoke(); return true; }
        return false;
    }

    /// <summary>Click on the image in toggle mode (mouse path, also used by self-tests).</summary>
    internal void Click() { if (mode == CompareMode.Toggle) ToggleRequested?.Invoke(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (mode == CompareMode.Split) { dragging = true; CaptureMouse(); MoveDividerTo(e.GetPosition(this)); e.Handled = true; }
        else if (mode == CompareMode.Toggle) { clickArmed = true; e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && e.LeftButton == MouseButtonState.Pressed) MoveDividerTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging) { dragging = false; ReleaseMouseCapture(); e.Handled = true; }
        if (clickArmed) { clickArmed = false; Click(); e.Handled = true; }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragging = false; }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); clickArmed = false; }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Like WPF focus visuals, the toggle view's outline appears only when focus arrives from the keyboard.
        keyboardCue = InputManager.Current.MostRecentInputDevice is KeyboardDevice; InvalidateVisual();
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); keyboardCue = false; InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent surface makes the whole stage respond to the mouse, not only the painted image.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var rects = ImageRects(RenderSize);
        if (rects.Length == 0 || rects[0].Width <= 0 || rects[0].Height <= 0) return;
        switch (mode)
        {
            case CompareMode.Split:
            {
                var image = rects[0]; double x = DividerX(image);
                DrawClipped(dc, before, image, new Rect(image.X, image.Y, x - image.X, image.Height), true);
                DrawClipped(dc, after, image, new Rect(x, image.Y, image.Right - x, image.Height), false);
                DrawDivider(dc, image, x);
                var left = Badge(BeforeText); var right = Badge(AfterText);
                if (x - image.X >= left.Width + 3 * BadgeInset) DrawBadge(dc, left, new Point(image.X + BadgeInset, image.Y + BadgeInset));
                if (image.Right - x >= right.Width + 3 * BadgeInset) DrawBadge(dc, right, new Point(image.Right - BadgeInset - right.Width - 16, image.Y + BadgeInset));
                break;
            }
            case CompareMode.SideBySide:
                DrawImage(dc, before, rects[0], true); DrawImage(dc, after, rects[1], false);
                DrawBadge(dc, Badge(BeforeText), new Point(rects[0].X + BadgeInset, rects[0].Y + BadgeInset));
                DrawBadge(dc, Badge(AfterText), new Point(rects[1].X + BadgeInset, rects[1].Y + BadgeInset));
                break;
            default:
                DrawImage(dc, DisplaysBefore ? before : after, rects[0], DisplaysBefore);
                if (StateLabel is { } label) DrawBadge(dc, Badge(label), new Point(rects[0].X + BadgeInset, rects[0].Y + BadgeInset));
                if (mode == CompareMode.Toggle && keyboardCue && IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(Theme.Accent, 2), Rect.Inflate(rects[0], 3, 3), 4, 4);
                break;
        }
    }

    // The caption for a state that has no image: still rendering, or a "before" render that failed.
    string MissingText(bool isBefore) => isBefore && beforeFailed ? BeforeFailedText : PendingText;

    void DrawImage(DrawingContext dc, BitmapSource? image, Rect rect, bool isBefore)
    {
        if (image != null) { dc.DrawImage(image, rect); return; }
        // The other state is still rendering (or failed); say so instead of showing a blank cell.
        var text = Text(MissingText(isBefore), Theme.Muted);
        if (text.Width <= rect.Width) dc.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
    }

    void DrawClipped(DrawingContext dc, BitmapSource? image, Rect imageRect, Rect clip, bool isBefore)
    {
        if (clip.Width <= 0) return;
        dc.PushClip(new RectangleGeometry(clip));
        if (image != null) dc.DrawImage(image, imageRect);
        else
        {
            var text = Text(MissingText(isBefore), Theme.Muted);
            if (text.Width + 2 * BadgeInset <= clip.Width) dc.DrawText(text, new Point(clip.X + (clip.Width - text.Width) / 2, clip.Y + (clip.Height - text.Height) / 2));
        }
        dc.Pop();
    }

    void DrawDivider(DrawingContext dc, Rect image, double x)
    {
        dc.PushOpacity(.55); dc.DrawRectangle(Theme.Canvas, null, new Rect(x - 2, image.Y, 4, image.Height)); dc.Pop();
        dc.DrawRectangle(Theme.Text, null, new Rect(x - 1, image.Y, 2, image.Height));
        var center = new Point(x, image.Y + image.Height / 2);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Theme.Accent, 2), center, HandleRadius + 3, HandleRadius + 3);
        dc.DrawEllipse(Theme.Surface, new Pen(Theme.Text, 1.5), center, HandleRadius, HandleRadius);
        var pen = new Pen(Theme.Text, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var arrows = new StreamGeometry();
        using (var g = arrows.Open())
        {
            g.BeginFigure(new Point(center.X - 3, center.Y - 4), false, false); g.LineTo(new Point(center.X - 7, center.Y), true, false); g.LineTo(new Point(center.X - 3, center.Y + 4), true, false);
            g.BeginFigure(new Point(center.X + 3, center.Y - 4), false, false); g.LineTo(new Point(center.X + 7, center.Y), true, false); g.LineTo(new Point(center.X + 3, center.Y + 4), true, false);
        }
        arrows.Freeze(); dc.DrawGeometry(null, pen, arrows);
    }

    FormattedText Text(string text, Brush brush) =>
        new(Loc.T(text), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Theme.CaptionSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = TextAlignment.Left };

    FormattedText Badge(string text) => Text(text, Theme.Text);

    // Small chip (radius 5) on a translucent panel so it reads on bright and dark photos alike.
    static void DrawBadge(DrawingContext dc, FormattedText text, Point at)
    {
        var chip = new Rect(at.X, at.Y, text.Width + 16, text.Height + 6);
        dc.PushOpacity(.86); dc.DrawRoundedRectangle(Theme.Panel, new Pen(Theme.Stroke, 1), chip, 5, 5); dc.Pop();
        dc.DrawText(text, new Point(at.X + 8, at.Y + 3));
    }
}

/// <summary>Button that reports while it is held down (mouse or Space), for "press to see before" controls.</summary>
public sealed class HoldButton : Button
{
    public event Action<bool>? HeldChanged;
    // Implicit styles match the exact type, so borrow the standard button look explicitly.
    public HoldButton() => SetResourceReference(StyleProperty, typeof(Button));
    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); HeldChanged?.Invoke(IsPressed); }
    /// <summary>Self-test hook: the same notification the pressed state raises.</summary>
    internal void SimulateHold(bool held) => HeldChanged?.Invoke(held);
}

/// <summary>Compare mode row and toggle-mode buttons shared by the adjustment dialogs.</summary>
public static class CompareControls
{
    internal static readonly (CompareMode Mode, string Caption, string Glyph, string Tip)[] Modes =
    [
        (CompareMode.Result, "결과", Theme.Glyphs.Image, "보정 결과만 봅니다."),
        (CompareMode.Split, "좌우 분할", Theme.Glyphs.CompareSplit, "한 화면을 가운데 막대로 나눠 왼쪽에 보정 전, 오른쪽에 보정 후를 봅니다. 막대를 끌거나 미리보기를 누른 뒤 ←/→ 키로 옮깁니다."),
        (CompareMode.SideBySide, "나란히", Theme.Glyphs.CompareSideBySide, "보정 전과 보정 후를 같은 배율로 나란히 봅니다. 이미지가 더 크게 보이는 쪽으로 좌우나 위아래에 놓습니다."),
        (CompareMode.Toggle, "전후 전환", Theme.Glyphs.CompareToggle, "한 이미지에서 보정 전과 보정 후를 바꿔 봅니다. 이미지를 클릭하면 바뀌고, \\(₩) 키나 ‘누르는 동안 보정 전’ 버튼을 누르고 있는 동안 보정 전을 봅니다."),
    ];

    /// <summary>Segmented mode row with a 16px icon beside each caption; the icon brightens with the selection.</summary>
    public static SegmentedChoice<CompareMode> ModeChoice(CompareMode initial)
    {
        var choice = new SegmentedChoice<CompareMode>(Modes.Select(m => (m.Mode, m.Caption)), initial);
        var buttons = choice.Buttons; var icons = new List<(CompareMode Mode, FrameworkElement On, FrameworkElement Off)>();
        for (int i = 0; i < Modes.Length; i++)
        {
            var (value, caption, glyph, tip) = Modes[i];
            var on = Theme.Glyph(glyph, 16, Theme.Text); var off = Theme.Glyph(glyph, 16, Theme.Muted);
            buttons[i].Content = IconLabel(on, off, caption); buttons[i].ToolTip = tip;
            AutomationProperties.SetHelpText(buttons[i], tip);
            icons.Add((value, on, off));
        }
        void Paint(CompareMode selected) { foreach (var (value, on, off) in icons) { on.Visibility = value == selected ? Visibility.Visible : Visibility.Collapsed; off.Visibility = value == selected ? Visibility.Collapsed : Visibility.Visible; } }
        choice.Changed += Paint; Paint(initial);
        return choice;
    }

    /// <summary>Icon + wrapping caption content for compact buttons.</summary>
    internal static FrameworkElement IconLabel(FrameworkElement icon, FrameworkElement? alternate, string caption)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        icon.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(icon);
        if (alternate != null) { alternate.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(alternate); }
        var text = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        return grid;
    }

    /// <summary>Compact icon button with a visible caption.</summary>
    internal static T Command<T>(T button, string glyph, string caption, string tooltip) where T : Button
    {
        button.Content = IconLabel(Theme.Glyph(glyph, 16, Theme.Muted), null, caption);
        button.ToolTip = tooltip; button.MinHeight = Theme.CompactHeight; button.Padding = new Thickness(8, 3, 10, 3); button.Margin = new Thickness(8, 0, 0, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, caption); AutomationProperties.SetHelpText(button, tooltip);
        return button;
    }

    /// <summary>The hold key: \ on US/Korean (₩) layouts and the extra ISO/Japanese backslash key.</summary>
    public static bool IsHoldKey(Key key) => key is Key.Oem5 or Key.Oem102;
}
