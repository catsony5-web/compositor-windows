using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed class TextEditorDialog : Window
{
    readonly TextBox editor;
    readonly ComboBox family;
    readonly TextBox size;
    readonly CheckBox bold;
    readonly CheckBox italic;
    readonly ComboBox align;
    readonly Button applyButton;
    readonly Button cancelButton;
    readonly Action<bool>? resultSink;
    Color color;
    bool imeCompositionActive;
    bool suppressNextEnter;

    public TextSpec Spec { get; private set; }

    public TextEditorDialog(Window owner, TextSpec initial) : this(owner, initial, null) { }

    // The sink is only used by the offscreen regression suite. The production
    // constructor keeps the normal Window.ShowDialog/DialogResult contract.
    internal TextEditorDialog(Window? owner, TextSpec initial, Action<bool>? resultSink)
    {
        this.resultSink = resultSink;
        Spec = initial; if (owner != null) Owner = owner;
        Title = "Morupixel · 텍스트"; Width = 760; Height = 570; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Panel; Foreground = Theme.Text;
        var grid = new Grid { Margin = new Thickness(22) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = grid;
        grid.Children.Add(Theme.Label("텍스트", 21));
        var controls = new WrapPanel { Margin = new Thickness(0, 14, 0, 14) }; Grid.SetRow(controls, 1); grid.Children.Add(controls);
        family = new ComboBox { ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray(), Text = initial.FontFamily, IsEditable = true, Width = 190, Margin = new Thickness(3), Padding = new Thickness(6) };
        size = new TextBox { Text = initial.FontSize.ToString(CultureInfo.InvariantCulture), Width = 64 };
        bold = new CheckBox { Content = "굵게", IsChecked = initial.Bold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
        italic = new CheckBox { Content = "기울임", IsChecked = initial.Italic, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
        align = new ComboBox { ItemsSource = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }, SelectedItem = initial.Alignment, Width = 90, Margin = new Thickness(3), Padding = new Thickness(6) };
        color = DocumentFeatures.Color(initial.ColorArgb);
        var colorButton = Theme.Button("색상", () => { var c = Dialogs.ColorPicker(this, color); if (c != null) color = c.Value; });
        foreach (var control in new UIElement[] { family, size, bold, italic, align, colorButton }) controls.Children.Add(control);
        editor = new TextBox { Text = initial.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = Math.Min(initial.FontSize, 72), Padding = new Thickness(16), Background = Theme.Brush("#747C88"), Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(initial.FontFamily) };
        Grid.SetRow(editor, 2); grid.Children.Add(editor);
        void Preview()
        {
            try { editor.FontFamily = new FontFamily(family.Text); editor.FontSize = Math.Clamp(double.Parse(size.Text, CultureInfo.InvariantCulture), 6, 120); editor.FontWeight = bold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal; editor.FontStyle = italic.IsChecked == true ? FontStyles.Italic : FontStyles.Normal; editor.TextAlignment = (TextAlignment)(align.SelectedItem ?? TextAlignment.Left); editor.Foreground = new SolidColorBrush(color); } catch { }
        }
        family.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(Preview); size.TextChanged += (_, _) => Preview(); bold.Click += (_, _) => Preview(); italic.Click += (_, _) => Preview(); align.SelectionChanged += (_, _) => Preview(); colorButton.Click += (_, _) => Preview();
        family.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Preview()));
        editor.PreviewKeyDown += EditorPreviewKeyDown;
        // TextInput is the committed end of a WPF IME composition. Keep the
        // guard through the rest of this input dispatch so the same Enter
        // cannot both commit Korean text and close this dialog.
        editor.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, _) => imeCompositionActive = true));
        editor.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, _) => imeCompositionActive = true));
        editor.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) =>
        {
            bool wasComposing = imeCompositionActive; imeCompositionActive = false;
            if (!wasComposing) return;
            suppressNextEnter = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => suppressNextEnter = false));
        }), true);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 3); grid.Children.Add(footer);
        applyButton = Theme.Button("텍스트 적용", ApplyChanges); applyButton.IsDefault = true; DockPanel.SetDock(applyButton, Dock.Right); footer.Children.Add(applyButton);
        cancelButton = Theme.Button("취소", CancelChanges); cancelButton.IsCancel = true; DockPanel.SetDock(cancelButton, Dock.Right); footer.Children.Add(cancelButton);
        editor.ToolTip = "Enter 적용 · Shift+Enter 줄바꿈 · Esc 취소";
        Loaded += (_, _) => { Preview(); editor.Focus(); editor.SelectAll(); };
    }

    internal TextBox EditorForTesting => editor;
    internal Button ApplyButtonForTesting => applyButton;
    internal Button CancelButtonForTesting => cancelButton;

    internal enum KeyAction { Ignore, InsertNewline, Apply, Cancel, ImePassThrough }

    internal static KeyAction ResolveKey(Key key, ModifierKeys modifiers, bool compositionActive, bool suppressEnter, Key imeProcessedKey = Key.None)
    {
        if (key == Key.ImeProcessed || imeProcessedKey != Key.None) return KeyAction.ImePassThrough;
        if (key == Key.Enter && (compositionActive || suppressEnter)) return KeyAction.ImePassThrough;
        if (key == Key.Escape) return compositionActive ? KeyAction.ImePassThrough : KeyAction.Cancel;
        if (key != Key.Enter) return KeyAction.Ignore;
        return (modifiers & ModifierKeys.Shift) != 0 ? KeyAction.InsertNewline : KeyAction.Apply;
    }

    void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (ResolveKey(key, e.KeyboardDevice.Modifiers, imeCompositionActive, suppressNextEnter, e.ImeProcessedKey))
        {
            case KeyAction.Apply: e.Handled = true; ApplyChanges(); break;
            case KeyAction.Cancel: e.Handled = true; CancelChanges(); break;
        }
    }

    void ApplyChanges()
    {
        try
        {
            Spec = Spec with { Content = editor.Text, FontFamily = family.Text, FontSize = Dialogs.Number(size.Text, 1, 1024), Bold = bold.IsChecked == true, Italic = italic.IsChecked == true, Alignment = (TextAlignment)(align.SelectedItem ?? TextAlignment.Left), ColorArgb = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B) };
            Spec.Validate(); Complete(true);
        }
        catch (Exception e) { MessageBox.Show(this, e.Message); }
    }

    void CancelChanges() => Complete(false);

    void Complete(bool result)
    {
        if (resultSink is { } sink) sink(result);
        else DialogResult = result;
    }
}
