using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>HSV picker with HEX entry on top, then recent colors, swatches, a folded tone grid and harmonies.</summary>
public sealed class ColorPalettePanel : StackPanel
{
    readonly SaturationValuePad pad = new();
    readonly Slider hue;
    readonly TextBlock values = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly TextBox hex = new() { MinHeight = 30, MinWidth = 96, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center };
    readonly UniformGrid recent = new() { Columns = RecentLimit, Rows = 1, Margin = new Thickness(0, 0, 0, 2) };
    readonly TextBlock recentEmpty = Theme.Label("아직 사용한 색이 없습니다", Theme.CaptionSize, Theme.Muted);
    public const int RecentLimit = 9;
    static readonly List<Color> recentColors = [];
    internal static readonly string[] SwatchHexes = ["#FFFFFF", "#D8DCE2", "#88929F", "#4A515B", "#171A20", "#000000", "#EAE2D5", "#C5AE94", "#8C7061", "#F28792", "#DB5269", "#A12D4D", "#FFB774", "#F28446", "#B75132", "#F4D37A", "#D3AD4E", "#826C35", "#A5D9AF", "#50A98D", "#2A665E", "#AFDCF1", "#76A5E5", "#375C9D", "#CBB5EB", "#9D7BC8", "#624B86"];
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
        Margin = new Thickness(0, 4, 0, 2);
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
        values.TextWrapping = TextWrapping.Wrap; values.VerticalAlignment = VerticalAlignment.Center; values.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(hex, "HEX 색상값"); hex.ToolTip = "#RRGGBB 입력 후 Enter";
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } else if (e.Key == Key.Escape) { UpdateReadout(); e.Handled = true; } };
        hex.LostKeyboardFocus += (_, _) => CommitHex();
        var hexRow = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        var hexLabel = Theme.Label("HEX", Theme.CaptionSize, Theme.Muted); hexLabel.VerticalAlignment = VerticalAlignment.Center; hexLabel.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(hexLabel, Dock.Left); DockPanel.SetDock(hex, Dock.Left);
        hexRow.Children.Add(hexLabel); hexRow.Children.Add(hex); hexRow.Children.Add(values);
        Children.Add(hexRow);
        Children.Add(Theme.Section("최근 사용 색"));
        AutomationProperties.SetName(recent, "최근 사용 색");
        Children.Add(recent); Children.Add(recentEmpty); RebuildRecent();
        Children.Add(Theme.Section("색상 견본"));
        var swatches = new UniformGrid { Columns = 9 };
        AutomationProperties.SetName(swatches, "색상 견본");
        foreach (string code in SwatchHexes)
        {
            var color = (Color)ColorConverter.ConvertFromString(code);
            var b = Theme.Button("", () => Choose(color), code + " · 전경색 지정"); b.Background = new SolidColorBrush(color); b.BorderBrush = Theme.Line; b.Height = 24; b.MinHeight = 0; b.MinWidth = 0; b.Padding = new Thickness(0); b.Margin = new Thickness(2);
            AutomationProperties.SetName(b, $"견본 {code}"); swatches.Children.Add(b);
        }
        Children.Add(swatches);
        BuildShadePalette();
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
        // A drag or key run on the pad ends as one recent color, not one per step.
        pad.LostMouseCapture += (_, _) => Remember(selectedColor);
        pad.LostKeyboardFocus += (_, _) => Remember(selectedColor);
        hue.LostMouseCapture += (_, _) => Remember(selectedColor);
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
        var toneHeader = (SectionHeader)Theme.Section("선택 색상 톤", foldedByDefault: true);
        Children.Add(toneHeader);
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
        // Fold now rather than on the next dispatcher pass so the first frame is already compact.
        toneHeader.Apply();
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
        Remember(color);
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

    void UpdateReadout()
    {
        values.Text = $"H {currentHue:0}°  S {pad.Saturation * 100:0}%  V {pad.Value * 100:0}%";
        if (!hex.IsKeyboardFocused) hex.Text = Hex(selectedColor);
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Accepts #RGB, #RRGGBB or #AARRGGBB, with or without '#'.</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        string t = (text ?? "").Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(c => $"{c}{c}"));
        if (t.Length is not (6 or 8) || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint v)) return false;
        color = t.Length == 6 ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v) : Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    internal void CommitHex()
    {
        if (!TryParseHex(hex.Text, out var parsed)) { hex.Text = Hex(selectedColor); return; }
        if (hex.Text.Trim().TrimStart('#').Length != 8) parsed.A = selectedColor.A == 0 ? (byte)255 : selectedColor.A;
        Choose(parsed);
        hex.Text = Hex(selectedColor);
    }

    /// <summary>Discrete pick (swatch, recent, HEX, harmony): adopt, remember and publish once.</summary>
    void Choose(Color color)
    {
        bool changed = color != selectedColor;
        SetColor(color);
        Remember(color);
        if (changed) ColorChanged?.Invoke(color);
    }

    public static IReadOnlyList<Color> RecentColors => recentColors;

    /// <summary>Adds a color to the shared recent row (newest first, no duplicates).</summary>
    public void Remember(Color color) { if (RememberShared(color)) RebuildRecent(); }

    /// <summary>Records a color picked while no panel is open (eyedropper, color dialog).</summary>
    public static bool RememberShared(Color color)
    {
        if (color.A == 0 || (recentColors.Count > 0 && recentColors[0] == color)) return false;
        recentColors.Remove(color); recentColors.Insert(0, color);
        if (recentColors.Count > RecentLimit) recentColors.RemoveRange(RecentLimit, recentColors.Count - RecentLimit);
        return true;
    }

    internal static void ClearRecent() => recentColors.Clear();

    /// <summary>Restores the saved recent row (workspace layout), newest first.</summary>
    public static void SetRecent(IEnumerable<Color> colors)
    {
        recentColors.Clear();
        foreach (var c in colors) if (c.A != 0 && !recentColors.Contains(c) && recentColors.Count < RecentLimit) recentColors.Add(c);
    }

    public static Color ParseStored(string hex) => TryParseHex(hex, out var c) ? c : Colors.Transparent;

    public void RefreshRecent() => RebuildRecent();

    void RebuildRecent()
    {
        recent.Children.Clear();
        foreach (var color in recentColors)
        {
            var c = color; string code = Hex(c);
            var b = Theme.Button("", () => Choose(c), code + " · 최근 사용 색"); b.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); b.BorderBrush = Theme.Line; b.Height = 24; b.MinHeight = 0; b.MinWidth = 0; b.Padding = new Thickness(0); b.Margin = new Thickness(2);
            b.Tag = c; AutomationProperties.SetName(b, $"최근 {code}"); recent.Children.Add(b);
        }
        bool any = recentColors.Count > 0;
        recent.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        recentEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

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
            var label = Theme.Label(hex, Theme.CaptionSize); label.TextAlignment = TextAlignment.Center;
            content.Children.Add(label);
            var button = Theme.Button("", () => Choose(color), hex + " · 추천 전경색");
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
