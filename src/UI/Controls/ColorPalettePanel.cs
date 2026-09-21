using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Foreground-based tone swatches, a modeless HSV picker and color harmonies.</summary>
public sealed class ColorPalettePanel : StackPanel
{
    readonly SaturationValuePad pad = new();
    readonly Slider hue;
    readonly TextBlock values = Theme.Label("", 11, Theme.Muted);
    readonly UniformGrid recommendations = new() { Columns = 3 };
    readonly List<Button> harmonyButtons = [];
    readonly UniformGrid shades = new() { Columns = ColorShadePalette.Columns, Rows = ColorShadePalette.Rows, Margin = new Thickness(2, 3, 2, 3) };
    readonly TextBlock shadeBasis = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly List<Button> shadeButtons = [];
    Color[] shadeColors = [];
    Color shadeBaseColor;
    int selectedShade = ColorShadePalette.BaseIndex;
    bool syncing;
    double currentHue = 210;
    Color selectedColor = Colors.Transparent;
    ColorHarmonyKind harmonyKind;

    public event Action<Color>? ColorChanged;
    public Color SelectedColor => selectedColor;
    internal Color ShadeBaseColor => shadeBaseColor;

    public ColorPalettePanel()
    {
        Margin = new Thickness(0, 10, 0, 2);
        BuildShadePalette();
        Children.Add(Theme.Section("채도 · 명도"));
        Children.Add(pad);
        hue = new Slider
        {
            Minimum = 0, Maximum = 359.99, SmallChange = 1, LargeChange = 15,
            IsMoveToPointEnabled = true, Height = 26, Margin = new Thickness(7, 3, 7, 0),
            Background = HueGradient(), ToolTip = "색조 · 좌우 방향키로 1°씩 조절"
        };
        if (TryFindResource("SpectrumSlider") is Style style) hue.Style = style;
        AutomationProperties.SetName(hue, "팔레트 색조");
        Children.Add(hue);
        values.TextWrapping = TextWrapping.Wrap;
        Children.Add(values);
        Children.Add(Theme.Section("추천 색상"));
        var modes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 1, 0, 5) };
        foreach (var (label, kind, help) in new[]
        {
            ("보색", ColorHarmonyKind.Complementary, "반대 색조와 부드러운 톤으로 대비를 만듭니다"),
            ("유사색", ColorHarmonyKind.Analogous, "색상환에서 가까운 세 색을 조합합니다"),
            ("삼각색", ColorHarmonyKind.Triadic, "120° 간격의 세 색을 조합합니다")
        })
        {
            var button = Theme.Button(label, () => SetHarmony(kind), help);
            button.Padding = new Thickness(3, 5, 3, 5); button.FontSize = Theme.BodySize;
            harmonyButtons.Add(button); modes.Children.Add(button);
        }
        recommendations.ToolTip = "추천색을 누르면 전경색으로 사용합니다";
        Children.Add(modes); Children.Add(recommendations);
        hue.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            currentHue = hue.Value;
            pad.SetHsv(currentHue, pad.Saturation, pad.Value);
            PublishColor();
        };
        pad.Changed += (_, _) => PublishColor();
        SetColor(Color.FromRgb(188, 217, 250));
    }

    /// <summary>Synchronizes from the foreground without raising ColorChanged.</summary>
    public void SetColor(Color color) => SetColor(color, true);

    void SetColor(Color color, bool updateShadeBase)
    {
        // The owner often echoes our change immediately. Preserve latent hue/saturation
        // at black and avoid quantizing the pointer position to 8-bit RGB on that echo.
        if (selectedColor == color) return;
        selectedColor = color;
        var hsv = ColorValues.ToHsv(color);
        if (hsv.S > .001) currentHue = hsv.H;
        syncing = true;
        hue.Value = currentHue;
        pad.SetHsv(currentHue, hsv.S, hsv.V);
        syncing = false;
        UpdateReadout(); UpdateRecommendations();
        if (updateShadeBase) SetShadeBase(color);
    }

    void PublishColor()
    {
        var next = ColorValues.FromHsv(currentHue, pad.Saturation, pad.Value, selectedColor.A);
        bool changed = next != selectedColor;
        selectedColor = next;
        UpdateReadout(); UpdateRecommendations();
        SetShadeBase(next);
        if (changed) ColorChanged?.Invoke(next);
    }

    void BuildShadePalette()
    {
        Children.Add(Theme.Section("선택 색상 톤"));
        Children.Add(shadeBasis);
        AutomationProperties.SetName(shades, "선택 색상 톤 그리드");
        for (int i = 0; i < ColorShadePalette.Columns * ColorShadePalette.Rows; i++)
        {
            int index = i;
            var button = Theme.Button("", () => SelectShade(index));
            button.Padding = new Thickness(1); button.Margin = new Thickness(0);
            button.MinWidth = 0; button.MinHeight = 0; button.Height = 29;
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(1);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Stretch;
            button.Content = new Border { CornerRadius = new CornerRadius(1) };
            // Ordinary button Tab/Enter/Space navigation also works without special input capture.
            shadeButtons.Add(button); shades.Children.Add(button);
        }
        Children.Add(shades);
        var reset = Theme.Button("현재 색을 기준으로", () => SetShadeBase(selectedColor), "현재 전경색으로 톤 팔레트의 기준색을 다시 설정합니다");
        reset.MinHeight = 32; reset.Padding = new Thickness(5, 5, 5, 5);
        Children.Add(reset);
        shades.SizeChanged += (_, e) =>
        {
            double height = Math.Clamp(e.NewSize.Width / ColorShadePalette.Columns, 20, 34);
            foreach (var button in shadeButtons) button.Height = height;
        };
    }

    void SetShadeBase(Color color)
    {
        shadeBaseColor = color;
        shadeColors = ColorShadePalette.Create(color);
        shadeBasis.Text = $"기준 #{color.R:X2}{color.G:X2}{color.B:X2}";
        shades.ToolTip = "위: 밝게 · 가운데: 기준색 · 아래: 어둡게\n" +
            (color.R == color.G && color.G == color.B ? "무채색 단계" : "좌우로 인접 색조");
        selectedShade = ColorShadePalette.BaseIndex;
        for (int i = 0; i < shadeColors.Length; i++)
        {
            var swatch = shadeColors[i];
            var tile = (Border)shadeButtons[i].Content;
            tile.Background = new SolidColorBrush(Color.FromRgb(swatch.R, swatch.G, swatch.B));
            int row = i / ColorShadePalette.Columns, column = i % ColorShadePalette.Columns;
            string tone = row < ColorShadePalette.Rows / 2 ? "밝은 톤" : row > ColorShadePalette.Rows / 2 ? "어두운 톤" : "기준 톤";
            string label = $"#{swatch.R:X2}{swatch.G:X2}{swatch.B:X2} · {tone} · {row + 1}행 {column + 1}열";
            if (i == ColorShadePalette.BaseIndex) label += " · 원래 기준색";
            shadeButtons[i].ToolTip = label;
            shadeButtons[i].Tag = swatch;
            AutomationProperties.SetName(shadeButtons[i], label);
        }
        UpdateShadeSelection();
    }

    void SelectShade(int index)
    {
        Color color = shadeColors[index];
        bool changed = color != selectedColor;
        // Keep the original grid in place while trying its tones. The owner's immediate
        // SetColor echo is silent, and an external foreground change establishes a new grid.
        SetColor(color, false);
        selectedShade = index;
        UpdateShadeSelection();
        if (changed) ColorChanged?.Invoke(color);
    }

    void UpdateShadeSelection()
    {
        for (int i = 0; i < shadeButtons.Count; i++)
        {
            shadeButtons[i].BorderBrush = i == selectedShade ? Theme.Text : Brushes.Transparent;
            AutomationProperties.SetItemStatus(shadeButtons[i], i == selectedShade ? "선택된 전경색" : "");
        }
    }

    void UpdateReadout() => values.Text = $"H {currentHue:0}°  ·  S {pad.Saturation * 100:0}%  ·  V {pad.Value * 100:0}%     #{selectedColor.R:X2}{selectedColor.G:X2}{selectedColor.B:X2}";

    void SetHarmony(ColorHarmonyKind kind) { harmonyKind = kind; UpdateRecommendations(); }

    void UpdateRecommendations()
    {
        for (int i = 0; i < harmonyButtons.Count; i++)
        {
            bool active = i == (int)harmonyKind;
            harmonyButtons[i].Background = active ? Theme.Selected : Theme.Surface;
            harmonyButtons[i].BorderBrush = active ? Theme.Accent : Theme.Line;
        }
        recommendations.Children.Clear();
        foreach (var color in ColorHarmony.Create(selectedColor, harmonyKind, currentHue))
        {
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var content = new StackPanel();
            content.Children.Add(new Border { Height = 30, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), Margin = new Thickness(0, 0, 0, 2) });
            var label = Theme.Label(hex, 11); label.TextAlignment = TextAlignment.Center;
            content.Children.Add(label);
            var button = Theme.Button("", () =>
            {
                bool changed = color != selectedColor;
                SetColor(color);
                if (changed) ColorChanged?.Invoke(color);
            }, hex + " · 추천 전경색");
            button.Content = content; button.Padding = new Thickness(3);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button, "추천색 " + hex);
            recommendations.Children.Add(button);
        }
    }

    static LinearGradientBrush HueGradient()
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (int i = 0; i <= 6; i++) gradient.GradientStops.Add(new GradientStop(ColorValues.FromHsv(i * 60, 1, 1), i / 6d));
        gradient.Freeze(); return gradient;
    }
}

public sealed class SaturationValuePad : FrameworkElement
{
    const double Inset = 7;
    double hue;
    public double Saturation { get; private set; }
    public double Value { get; private set; }
    public event Action<double, double>? Changed;

    public SaturationValuePad()
    {
        Height = 150; Focusable = true; Cursor = Cursors.Cross;
        ToolTip = "좌우: 채도 · 위아래: 명도\n드래그 또는 방향키 · Shift: 10%씩 조절";
        AutomationProperties.SetName(this, "채도와 명도 팔레트");
        AutomationProperties.SetHelpText(this, "좌우 방향키로 채도, 위아래 방향키로 명도를 조절합니다. Shift를 누르면 10%씩 조절합니다.");
        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { Pick(e.GetPosition(this)); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } };
        LostKeyboardFocus += (_, _) => InvalidateVisual();
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        KeyDown += (_, e) => { if (AdjustByKey(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true; };
    }

    public void SetHsv(double h, double s, double v)
    {
        hue = double.IsFinite(h) ? (h % 360 + 360) % 360 : 0;
        Saturation = double.IsFinite(s) ? Math.Clamp(s, 0, 1) : 0;
        Value = double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        InvalidateVisual();
    }

    Rect ColorRect => new(Inset, Inset, Math.Max(1, ActualWidth - Inset * 2), Math.Max(1, ActualHeight - Inset * 2));

    void Pick(Point position)
    {
        var rect = ColorRect;
        Change((position.X - rect.Left) / rect.Width, 1 - (position.Y - rect.Top) / rect.Height);
    }

    void Change(double s, double v)
    {
        double beforeS = Saturation, beforeV = Value;
        SetHsv(hue, s, v);
        if (beforeS != Saturation || beforeV != Value) Changed?.Invoke(Saturation, Value);
    }

    internal bool AdjustByKey(Key key, bool largeStep = false)
    {
        double step = largeStep ? .1 : .01;
        switch (key)
        {
            case Key.Left: Change(Saturation - step, Value); break;
            case Key.Right: Change(Saturation + step, Value); break;
            case Key.Down: Change(Saturation, Value - step); break;
            case Key.Up: Change(Saturation, Value + step); break;
            case Key.Home: Change(0, Value); break;
            case Key.End: Change(1, Value); break;
            default: return false;
        }
        return true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PalettePadAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        var rect = ColorRect;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.PushClip(new RectangleGeometry(rect, 7, 7));
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, ColorValues.FromHsv(hue, 1, 1), 0), null, rect);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), null, rect);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(IsKeyboardFocused ? Theme.Accent : Theme.Line, IsKeyboardFocused ? 2 : 1), rect, 7, 7);
        var point = new Point(rect.Left + Saturation * rect.Width, rect.Top + (1 - Value) * rect.Height);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3.5), point, 4.5, 4.5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.8), point, 4.5, 4.5);
    }

    sealed class PalettePadAutomationPeer(SaturationValuePad owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SaturationValuePad);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetItemStatusCore() => $"채도 {owner.Saturation * 100:0}%, 명도 {owner.Value * 100:0}%";
    }
}
