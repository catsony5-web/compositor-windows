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
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(8), Margin = new Thickness(0, 3, 0, 8),
            ToolTip = "Enter 줄바꿈 · Ctrl+Enter 적용" };
        AutomationProperties.SetName(editor, "텍스트 내용"); Children.Add(editor);
        AddLabel("글꼴");
        family = new ComboBox { IsEditable = true, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray(), Text = spec.FontFamily,
            MinHeight = 38, Margin = new Thickness(0, 3, 0, 5), Padding = new Thickness(7, 4, 7, 4) };
        AutomationProperties.SetName(family, "텍스트 글꼴"); Children.Add(family);
        style = new ComboBox { ItemsSource = new[] { "보통", "굵게", "기울임", "굵게 기울임" }, SelectedIndex = (spec.Bold ? 1 : 0) + (spec.Italic ? 2 : 0),
            MinHeight = 38, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(7, 4, 7, 4) };
        AutomationProperties.SetName(style, "글꼴 스타일"); Children.Add(style);
        size = Number(spec.FontSize, "글자 크기 px"); leading = Number(spec.LineHeight, "줄 간격 px · 0은 자동"); tracking = Number(spec.Tracking, "자간 · 1/1000 em");
        leading.ToolTip = "줄 간격 px · 0 = 자동 · Enter 적용";
        tracking.ToolTip = "자간 · 1/1000 em · 0 = 기본 · Enter 적용";
        Children.Add(Pair("크기 · px", size, "줄 간격 · px", leading));
        var lower = new UniformGrid { Columns = 2, Margin = new Thickness(0, 5, 0, 8) };
        lower.Children.Add(Field("자간 · 1/1000 em", tracking));
        colorButton = Theme.Button("글자 색상", () => { if (pickColor(color) is { } selected) { color = selected; UpdateColor(); ClearError(); } });
        colorButton.MinHeight = 34; colorButton.Margin = new Thickness(4, 3, 0, 0); colorButton.Padding = new Thickness(7, 4, 7, 4);
        lower.Children.Add(Field("색상", colorButton)); Children.Add(lower); UpdateColor();
        AddLabel("단락 정렬");
        var alignments = new UniformGrid { Columns = 3, Margin = new Thickness(0, 3, 0, 9) };
        foreach (var (label, value) in new[] { ("왼쪽", TextAlignment.Left), ("가운데", TextAlignment.Center), ("오른쪽", TextAlignment.Right) })
        {
            var button = Theme.Button(label, () => { alignment = value; UpdateAlignment(); ClearError(); }, "단락 " + label + " 정렬");
            button.MinHeight = 34; button.Padding = new Thickness(4); button.Margin = new Thickness(1); button.Tag = value;
            alignmentButtons.Add(button); alignments.Children.Add(button);
        }
        Children.Add(alignments); UpdateAlignment();
        var apply = Theme.Button("텍스트 적용", () => TryApply(), "내용과 문자 서식을 한 번에 적용 · Ctrl+Enter"); apply.MinHeight = 36;
        apply.Background = Theme.Primary; apply.Margin = new Thickness(0, 0, 0, 4); Children.Add(apply);
        message = Theme.Label("", 11, Theme.Muted); message.TextWrapping = TextWrapping.Wrap; message.Visibility = Visibility.Collapsed; Children.Add(message);
        AddLabel("캔버스에 정렬");
        var position = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, command) in new[] { ("왼쪽", "left"), ("가로 중앙", "center"), ("오른쪽", "right"), ("위쪽", "top"), ("세로 중앙", "middle"), ("아래쪽", "bottom") })
        {
            var button = Theme.Button(label, () => { if (TryApply()) alignLayer(command); }, "텍스트 레이어를 캔버스 " + label + "에 정렬");
            button.Padding = new Thickness(3, 5, 3, 5); button.MinHeight = 34; button.Margin = new Thickness(1); position.Children.Add(button);
        }
        Children.Add(position);
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
    void SetError(string error) { message.Text = error; message.Foreground = Theme.Brush("#F2AAAA"); message.Visibility = Visibility.Visible; }
    void ClearError() { if (message == null) return; message.Text = ""; message.Visibility = Visibility.Collapsed; }
    void UpdateColor()
    {
        var contents = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        contents.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(color), BorderBrush = Theme.Muted, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) });
        contents.Children.Add(Theme.Label("글자 색상", 12)); colorButton.Content = contents;
    }
    void UpdateAlignment() { foreach (var button in alignmentButtons) { button.Background = Equals(button.Tag, alignment) ? Theme.Selected : Theme.Surface; button.BorderBrush = Equals(button.Tag, alignment) ? Theme.Accent : Theme.Line; } }
    void AddLabel(string label) { var text = Theme.Label(label, Theme.CaptionSize, Theme.Muted); text.Margin = new Thickness(0, 10, 0, 3); Children.Add(text); }
    static TextBox Number(double value, string name)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture), MinHeight = 34, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 3, 4, 0), HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(box, name); box.ToolTip = name + " · Enter 적용"; return box;
    }
    static StackPanel Field(string label, UIElement element) { var stack = new StackPanel(); var caption = Theme.Label(label, 11, Theme.Muted); caption.TextWrapping = TextWrapping.Wrap; stack.Children.Add(caption); stack.Children.Add(element); return stack; }
    static UniformGrid Pair(string a, UIElement left, string b, UIElement right) { var grid = new UniformGrid { Columns = 2 }; grid.Children.Add(Field(a, left)); grid.Children.Add(Field(b, right)); return grid; }

    internal TextBox EditorForTesting => editor;
    internal TextBox SizeForTesting => size;
    internal TextBox LeadingForTesting => leading;
    internal TextBox TrackingForTesting => tracking;
    internal string ValidationForTesting => message.Text;
}
