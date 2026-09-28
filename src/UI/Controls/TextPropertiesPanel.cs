using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class TextPropertiesPanel : StackPanel
{
    readonly TextSpec original;
    readonly Func<TextSpec, string?> commit;
    readonly TextBox editor, size, leading, tracking;
    readonly ComboBox family, style;
    readonly TextBlock message;
    readonly Button colorButton;
    readonly List<Button> alignmentButtons = [];
    TextAlignment alignment;
    Color color;
    bool composing;
    public event Action? EditingStarted;
    public Action CommitPending => () => TryApply();

    public TextPropertiesPanel(TextSpec spec, Func<TextSpec, string?> commit, Func<Color, Color?> pickColor, Action<string> alignLayer)
    {
        original = spec; this.commit = commit; alignment = spec.Alignment; color = DocumentFeatures.Color(spec.ColorArgb);
        Margin = new Thickness(2, 4, 2, 12);
        Children.Add(Theme.Section("문자 · 단락"));
        AddLabel("텍스트 내용");
        editor = new TextBox { Text = spec.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 88, MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = Theme.HeadingSize, Padding = new Thickness(8), Margin = new Thickness(0, 3, 0, 8),
            ToolTip = "Enter 줄바꿈 · Ctrl+Enter 적용" };
        AutomationProperties.SetName(editor, "텍스트 내용"); Children.Add(editor);
        AddLabel("글꼴");
        family = new ComboBox { IsEditable = true, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray(), Text = spec.FontFamily,
            MinHeight = Theme.ControlHeight, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(family, "텍스트 글꼴"); Children.Add(family);
        style = new ComboBox { ItemsSource = new[] { "보통", "굵게", "기울임", "굵게 기울임" }, SelectedIndex = (spec.Bold ? 1 : 0) + (spec.Italic ? 2 : 0),
            MinHeight = Theme.ControlHeight, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(style, "글꼴 스타일"); Children.Add(style);
        size = Number(spec.FontSize, "글자 크기 px"); leading = Number(spec.LineHeight, "줄 간격 px · 0은 자동"); tracking = Number(spec.Tracking, "자간 · 1/1000 em");
        leading.ToolTip = "줄 간격 px · 0 = 자동 · Enter 적용";
        tracking.ToolTip = "자간 · 1/1000 em · 0 = 기본 · Enter 적용";
        Children.Add(PropertyRows.Pair("크기 · px", size, "줄 간격 · px", leading, new Thickness(0, 2, 0, 8)));
        colorButton = PropertyRows.ColorChip(color, "글자 색상", () => { if (pickColor(color) is { } selected) { color = selected; UpdateColor(); ClearError(); } }, "글자 색상");
        Children.Add(PropertyRows.Pair("자간 · 1/1000 em", tracking, "색상", colorButton, new Thickness(0, 0, 0, 4)));
        AddLabel("단락 정렬");
        var alignments = new UniformGrid { Columns = 3, Margin = new Thickness(0, 3, 0, 9) };
        // Whole sentences per alignment so each language can order the words itself.
        foreach (var (label, value, glyph) in new[] { ("단락 왼쪽 정렬", TextAlignment.Left, Theme.Glyphs.TextLeft), ("단락 가운데 정렬", TextAlignment.Center, Theme.Glyphs.TextCenter), ("단락 오른쪽 정렬", TextAlignment.Right, Theme.Glyphs.TextRight) })
        {
            var button = Theme.Button("", () => { alignment = value; UpdateAlignment(); ClearError(); }, label);
            button.Content = Theme.Glyph(glyph, 18, Theme.Text); AutomationProperties.SetName(button, label);
            button.MinHeight = 34; button.Padding = new Thickness(4); button.Margin = new Thickness(1); button.Tag = value; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            alignmentButtons.Add(button); alignments.Children.Add(button);
        }
        Children.Add(alignments); UpdateAlignment();
        var apply = Theme.Styled(Theme.Button("텍스트 적용", () => TryApply(), "내용과 문자 서식을 한 번에 적용 · Ctrl+Enter"), "PrimaryButton"); apply.MinHeight = 34;
        apply.Margin = new Thickness(0, 0, 0, 4); Children.Add(apply);
        message = Theme.Label("", Theme.CaptionSize, Theme.Muted); message.TextWrapping = TextWrapping.Wrap; message.Visibility = Visibility.Collapsed; Children.Add(message);
        AddLabel("캔버스에 정렬");
        var position = QuickActions.IconStrip(MainWindow.CanvasAlignments.Select(a => (a.Glyph, $"텍스트 레이어 {a.Name}", (Action)(() => { if (TryApply()) alignLayer(a.Direction); }))), out _);
        position.Margin = new Thickness(0, 4, 0, 0); Children.Add(position);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => EditingStarted?.Invoke()));
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => ClearError()));
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            bool inContent = ReferenceEquals(source, editor) || editor.IsAncestorOf(source);
            bool inField = inContent || new FrameworkElement[] { size, leading, tracking }.Any(field => ReferenceEquals(source, field) || field.IsAncestorOf(source)) || source is TextBox && family.IsAncestorOf(source);
            if ((inField || e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control)) && ShouldApplyKey(e.Key, e.KeyboardDevice.Modifiers, inContent, family.IsDropDownOpen || style.IsDropDownOpen, composing, e.ImeProcessedKey))
            { TryApply(); e.Handled = true; }
        };
        AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, _) => composing = true));
        AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, _) => composing = true));
        AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => composing = false))), true);
    }

    internal static bool ShouldApplyKey(Key key, ModifierKeys modifiers, bool inContent, bool dropdownOpen, bool compositionActive, Key imeProcessedKey = Key.None)
    {
        if (compositionActive || key == Key.ImeProcessed || imeProcessedKey != Key.None || dropdownOpen || key != Key.Enter) return false;
        return modifiers.HasFlag(ModifierKeys.Control) || !inContent && !modifiers.HasFlag(ModifierKeys.Shift);
    }

    public void FocusContent() { editor.Focus(); editor.SelectAll(); editor.BringIntoView(); }
    public bool TryApply()
    {
        if (composing) return false;
        try
        {
            var next = original with { Content = editor.Text, FontFamily = family.Text.Trim(), FontSize = Parse(size, 1, 1024, "글자 크기"),
                LineHeight = Parse(leading, 0, 8192, "줄 간격"), Tracking = Parse(tracking, -200, 2000, "자간"),
                Bold = (style.SelectedIndex & 1) != 0, Italic = (style.SelectedIndex & 2) != 0, Alignment = alignment,
                ColorArgb = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B) };
            next.Validate();
            string? error = commit(next); if (error != null) { SetError(error); return false; }
            ClearError();
            return true;
        }
        catch (Exception error) when (error is FormatException or System.IO.InvalidDataException or ArgumentException)
        { SetError(error.Message); return false; }
    }
    static double Parse(TextBox box, double min, double max, string name)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < min || value > max)
            throw new FormatException($"{name}: {min}~{max} 사이의 숫자를 입력하세요.");
        return value;
    }
    void SetError(string error) { message.Text = error; message.Foreground = Theme.Danger; message.Visibility = Visibility.Visible; }
    void ClearError() { if (message == null) return; message.Text = ""; message.Visibility = Visibility.Collapsed; }
    void UpdateColor() => PropertyRows.SetChipColor(colorButton, color, "글자 색상");
    void UpdateAlignment() { foreach (var button in alignmentButtons) { button.Background = Equals(button.Tag, alignment) ? Theme.Selected : Theme.Surface; button.BorderBrush = Equals(button.Tag, alignment) ? Theme.Accent : Theme.Line; } }
    void AddLabel(string label) { var text = PropertyRows.Caption(label); text.Margin = new Thickness(0, 10, 0, 3); Children.Add(text); }
    static TextBox Number(double value, string name)
    {
        var box = PropertyRows.NumberBox(value, name); box.ToolTip = name + " · Enter 적용"; return box;
    }

    internal TextBox EditorForTesting => editor;
    internal TextBox SizeForTesting => size;
    internal TextBox LeadingForTesting => leading;
    internal TextBox TrackingForTesting => tracking;
    internal string ValidationForTesting => message.Text;
}
