using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// One editor action as it appears in the command palette. Execute runs the same path as
// its menu item, tool button or panel tab, so availability and undo behave identically.
internal sealed record EditorCommand(string Id, string Title, string Category, string Shortcut, Func<bool> IsAvailable, Action Execute);

// Ctrl+K search over menus, tools and panels. Matches titles, categories and shortcuts,
// and Korean initial consonants (ㅂㄹㅅ → 브러시 도구).
internal sealed class CommandPalette : Window
{
    const int MaxResults = 60;
    readonly IReadOnlyList<EditorCommand> commands;
    readonly IReadOnlyList<string> recent;
    readonly TextBlock hint;
    internal readonly TextBox Query = new();
    internal readonly ListBox Results = new();
    public EditorCommand? Chosen { get; private set; }
    internal IReadOnlyList<EditorCommand> Shown { get; private set; } = [];
    bool closing;

    public CommandPalette(Window? owner, IReadOnlyList<EditorCommand> commands, IReadOnlyList<string> recent)
    {
        this.commands = commands; this.recent = recent;
        DialogShell.Prepare(this, owner != null && owner.IsLoaded ? owner : null, "명령 찾기");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Width = 600; SizeToContent = SizeToContent.Height;
        if (Owner is { } parent)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var origin = parent.WindowState == WindowState.Maximized ? parent.PointToScreen(new Point()) : new Point(parent.Left, parent.Top);
            var dpi = VisualTreeHelper.GetDpi(parent);
            double left = parent.WindowState == WindowState.Maximized ? origin.X / dpi.DpiScaleX : origin.X, top = parent.WindowState == WindowState.Maximized ? origin.Y / dpi.DpiScaleY : origin.Y;
            Left = left + Math.Max(0, (parent.ActualWidth - Width) / 2); Top = top + 84;
        }

        var root = new DockPanel();
        Content = new Border { Background = Theme.Panel, BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = root };
        var search = new Grid { Margin = new Thickness(14, 8, 14, 8) };
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); search.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Theme.Glyph(Theme.Glyphs.Search, 18, Theme.Muted); icon.VerticalAlignment = VerticalAlignment.Center; search.Children.Add(icon);
        Query.Background = Brushes.Transparent; Query.BorderThickness = new Thickness(0); Query.FontSize = 15; Query.Padding = new Thickness(6, 4, 6, 4); Query.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(Query, "명령 검색");
        var placeholder = new TextBlock { Text = "명령·도구·패널 검색  (예: 레이어 복제, ㅂㄹㅅ, ctrl+j)", Foreground = Theme.Subtle, FontSize = 15, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        Grid.SetColumn(Query, 1); Grid.SetColumn(placeholder, 1); search.Children.Add(Query); search.Children.Add(placeholder);
        DockPanel.SetDock(search, Dock.Top); root.Children.Add(search);
        var divider = new Border { Height = 1, Background = Theme.Line }; DockPanel.SetDock(divider, Dock.Top); root.Children.Add(divider);
        hint = new TextBlock { Text = "↑↓ 이동   Enter 실행   Esc 닫기", Foreground = Theme.Subtle, FontSize = Theme.CaptionSize, Margin = new Thickness(16, 7, 16, 9) };
        var footer = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = hint };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Results.Background = Brushes.Transparent; Results.BorderThickness = new Thickness(0); Results.MaxHeight = 400; Results.Padding = new Thickness(0, 6, 0, 6);
        Results.Focusable = false; Results.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaletteItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(Results, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(Results, "검색 결과");
        root.Children.Add(Results);

        Query.TextChanged += (_, _) => { placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
        Query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up) { Move(e.Key == Key.Down ? 1 : -1); e.Handled = true; }
            else if (e.Key == Key.Enter) { Choose(Results.SelectedItem is ListBoxItem { Tag: EditorCommand selected } ? selected : null); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Results.MouseLeftButtonUp += (_, _) => Choose(Results.SelectedItem is ListBoxItem { Tag: EditorCommand selected } ? selected : null);
        Deactivated += (_, _) => { if (!closing) Close(); };
        Closing += (_, _) => closing = true;
        Loaded += (_, _) => Query.Focus();
        Refresh();
    }

    internal void SetQuery(string text) => Query.Text = text;

    void Refresh()
    {
        Shown = Filter(commands, Query.Text, recent).Take(MaxResults).ToArray();
        Results.Items.Clear();
        bool browsing = Normalize(Query.Text).Length == 0;
        foreach (var command in Shown) Results.Items.Add(new ListBoxItem { Content = Row(command, browsing && recent.Contains(command.Id)), Tag = command, Opacity = command.IsAvailable() ? 1 : .45 });
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
        hint.Text = Shown.Count == 0 ? "일치하는 명령이 없습니다" : "↑↓ 이동   Enter 실행   Esc 닫기";
    }

    void Move(int delta)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = (Math.Max(0, Results.SelectedIndex) + delta + Results.Items.Count) % Results.Items.Count;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    internal bool Choose(EditorCommand? command)
    {
        if (command == null) return false;
        if (!command.IsAvailable()) { hint.Text = "문서를 연 뒤 사용할 수 있는 명령입니다"; return false; }
        Chosen = command;
        if (IsLoaded) Close();
        return true;
    }

    static FrameworkElement Row(EditorCommand command, bool recentlyUsed)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = command.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        var category = new TextBlock { FontSize = Theme.CaptionSize, Foreground = Theme.Subtle, TextTrimming = TextTrimming.CharacterEllipsis };
        if (recentlyUsed) category.Inlines.Add(new System.Windows.Documents.Run("최근 사용  ·  ") { Foreground = Theme.Accent });
        category.Inlines.Add(new System.Windows.Documents.Run(command.Category));
        labels.Children.Add(category);
        row.Children.Add(labels);
        if (command.Shortcut.Length > 0)
        {
            var key = new Border
            {
                Background = Theme.Input, BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = command.Shortcut, FontSize = Theme.CaptionSize, Foreground = Theme.Muted }
            };
            Grid.SetColumn(key, 1); row.Children.Add(key);
        }
        return row;
    }

    // Empty query: recently run commands first, then registry order. Otherwise ranked matches.
    internal static IReadOnlyList<EditorCommand> Filter(IReadOnlyList<EditorCommand> commands, string query, IReadOnlyList<string> recent)
    {
        string q = Normalize(query);
        if (q.Length == 0)
        {
            var rank = recent.Select((id, index) => (id, index)).DistinctBy(r => r.id).ToDictionary(r => r.id, r => r.index);
            return commands.OrderBy(c => rank.GetValueOrDefault(c.Id, int.MaxValue)).ToArray();
        }
        bool initials = q.All(IsInitialConsonant);
        return commands.Select((command, index) => (command, index, score: Score(command, q, initials) + (recent.Contains(command.Id) ? 5 : 0)))
            .Where(item => item.score > 5).OrderByDescending(item => item.score).ThenBy(item => item.index).Select(item => item.command).ToArray();
    }

    static int Score(EditorCommand command, string q, bool initials)
    {
        string title = Normalize(command.Title);
        if (title.StartsWith(q, StringComparison.Ordinal)) return 100;
        if (title.Contains(q, StringComparison.Ordinal)) return 80;
        if (initials)
        {
            string letters = Initials(title);
            if (letters.StartsWith(q, StringComparison.Ordinal)) return 70;
            if (letters.Contains(q, StringComparison.Ordinal)) return 60;
        }
        if (Normalize(command.Category + command.Title).Contains(q, StringComparison.Ordinal)) return 50;
        if (command.Shortcut.Length > 0 && Normalize(command.Shortcut).Contains(q, StringComparison.Ordinal)) return 40;
        return 0;
    }

    // Lowercase without spaces or ellipses so "레이어복제", "ctrl+j" and "CTRL + J" match.
    internal static string Normalize(string text) => new(text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '…' && c != '·').ToArray());

    static readonly char[] Leading = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ".ToCharArray();
    internal static string Initials(string text) => new(text.Select(c => c is >= '가' and <= '힣' ? Leading[(c - '가') / 588] : c).ToArray());
    static bool IsInitialConsonant(char c) => c is >= 'ㄱ' and <= 'ㅎ';
}
