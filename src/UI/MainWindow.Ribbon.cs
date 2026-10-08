using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Compositor.Windows;

// Optional ribbon: every top-level menu becomes a tab, its separator groups become
// titled panels of icon buttons that run the same menu items. ^ folds the ribbon to
// its tabs; 내 탭 holds commands the user adds from any button's context menu.
public sealed partial class MainWindow
{
    internal const string FavoritesTab = "내 탭";
    internal static readonly string[] DefaultFavorites =
    [
        "menu:파일/새 캔버스…", "menu:파일/열기…", "menu:파일/저장", "menu:파일/내보내기 미리보기…", "menu:편집/실행 취소", "menu:편집/다시 실행",
        "menu:레이어/레이어 복제", "menu:레이어/마스크 추가", "menu:필터/사진 현상…", "menu:보기/화면에 맞춤", "menu:보기/명령 찾기…"
    ];
    // Favorites saved under a command id that a later version renamed or moved.
    static readonly Dictionary<string, string> LegacyFavorites = new(StringComparer.Ordinal)
    {
        ["menu:파일/PDF / PSD 파일로 내보내기…"] = $"menu:파일/{CompatibilityExportMenu}/{CompatibilityExport.Choices[0].Title}…"
    };
    internal static List<string> MigrateFavorites(IEnumerable<string> saved) =>
        saved.Select(id => LegacyFavorites.TryGetValue(id, out var current) ? current : id).Distinct(StringComparer.Ordinal).ToList();
    internal bool ribbonMode, ribbonCollapsed;
    internal string ribbonTab = FavoritesTab;
    internal List<string> ribbonFavorites = [.. DefaultFavorites];
    StackPanel? ribbonTabs;
    Border? ribbonBody;
    FrameworkElement? menuHost;
    MenuItem? ribbonToggle;
    readonly Dictionary<string, MenuItem> ribbonItems = new(StringComparer.Ordinal);

    // Leaf menu items by the registry id format ("menu:상위/항목").
    void IndexMenuItems()
    {
        ribbonItems.Clear();
        void Walk(MenuItem item, string path)
        {
            string here = path.Length == 0 ? item.Header?.ToString() ?? "" : path + "/" + item.Header;
            var children = item.Items.OfType<MenuItem>().ToArray();
            if (children.Length == 0) ribbonItems[ "menu:" + here] = item;
            foreach (var child in children) Walk(child, here);
        }
        if (mainMenu != null) foreach (var top in mainMenu.Items.OfType<MenuItem>()) Walk(top, "");
    }

    FrameworkElement BuildRibbonTabs()
    {
        var bar = new DockPanel { VerticalAlignment = VerticalAlignment.Stretch };
        var collapse = Theme.IconButton(Theme.Glyphs.ChevronDown, () => SetRibbonCollapsed(!ribbonCollapsed), "리본 접기 · 탭만 보기", 24, 12);
        collapse.Name = "RibbonCollapse"; collapse.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(collapse, Dock.Right); bar.Children.Add(collapse);
        ribbonTabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(ribbonTabs);
        return bar;
    }

    FrameworkElement BuildRibbonBody()
    {
        ribbonBody = new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 1), Visibility = Visibility.Collapsed };
        return ribbonBody;
    }

    IEnumerable<string> RibbonTabNames() => new[] { FavoritesTab }.Concat(mainMenu?.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "") ?? []);

    void RebuildRibbon()
    {
        if (ribbonTabs == null || ribbonBody == null || mainMenu == null) return;
        if (ribbonItems.Count == 0) IndexMenuItems();
        ribbonTabs.Children.Clear();
        foreach (var name in RibbonTabNames())
        {
            string tab = name;
            var button = Theme.Styled(Theme.Button(tab, () => SelectRibbonTab(tab), tab + " 탭"), "PanelTab");
            button.FontSize = Theme.BodySize; button.MinHeight = 30; button.Padding = new Thickness(8, 2, 8, 2); button.Margin = new Thickness(0);
            bool on = tab == ribbonTab && !ribbonCollapsed;
            button.BorderBrush = on ? Theme.Accent : Brushes.Transparent; button.Foreground = on || tab == ribbonTab ? Theme.Text : Theme.Muted; button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetName(button, tab + " 탭");
            ribbonTabs.Children.Add(button);
        }
        var panels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 2) };
        foreach (var (title, items) in RibbonGroups(ribbonTab)) panels.Children.Add(RibbonGroup(title, items));
        if (ribbonTab == FavoritesTab && panels.Children.Count == 0)
            panels.Children.Add(new TextBlock { Text = "다른 탭의 버튼을 오른쪽 클릭해 ‘내 탭에 추가’를 고르세요.", Foreground = Theme.Muted, FontSize = Theme.CaptionSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 20, 8, 20) });
        ribbonBody.Child = new ScrollViewer { Content = panels, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ribbonBody.Visibility = ribbonMode && !ribbonCollapsed ? Visibility.Visible : Visibility.Collapsed;
        if (menuHost != null) menuHost.Visibility = ribbonMode ? Visibility.Collapsed : Visibility.Visible;
        if (ribbonTabs.Parent is FrameworkElement strip) strip.Visibility = ribbonMode ? Visibility.Visible : Visibility.Collapsed;
        if (ribbonTabs.Parent is DockPanel dock && dock.Children[0] is Button collapse)
        {
            collapse.Content = Theme.Glyph(ribbonCollapsed ? Theme.Glyphs.ChevronDown : "M6 15L12 9L18 15", 12, Theme.Muted);
            collapse.ToolTip = ribbonCollapsed ? "리본 펼치기" : "리본 접기 · 탭만 보기";
            AutomationProperties.SetName(collapse, collapse.ToolTip.ToString());
        }
        if (ribbonToggle != null) ribbonToggle.IsChecked = ribbonMode;
        updateSearchCompact?.Invoke();
    }

    // Groups follow the menu's separators; a submenu becomes its own group.
    internal IReadOnlyList<(string Title, MenuItem[] Items)> RibbonGroups(string tab)
    {
        if (tab == FavoritesTab)
        {
            var favorites = ribbonFavorites.Where(ribbonItems.ContainsKey).Select(id => ribbonItems[id]).ToArray();
            return favorites.Length == 0 ? [] : [("자주 쓰는 명령", favorites)];
        }
        var top = mainMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Header, tab));
        if (top == null) return [];
        var groups = new List<(string, MenuItem[])>(); var current = new List<MenuItem>();
        void Close() { if (current.Count > 0) { groups.Add((GroupTitle(tab, current[0]), current.ToArray())); current = []; } }
        foreach (var entry in top.Items)
        {
            if (entry is Separator) { Close(); continue; }
            if (entry is not MenuItem item) continue;
            var children = item.Items.OfType<MenuItem>().ToArray();
            if (children.Length > 0) { Close(); groups.Add((item.Header?.ToString() ?? "", children)); continue; }
            current.Add(item);
        }
        Close();
        return groups;
    }

    static string GroupTitle(string tab, MenuItem first)
    {
        string header = first.Header?.ToString() ?? "";
        (string Key, string Title)[] titles =
        [
            ("새 캔버스", "문서"), ("저장", "저장"), ("내보내기 미리보기", "내보내기"), ("Compositor", "호환 파일"), ("현재 문서 닫기", "닫기"),
            ("실행 취소", "기록"), ("합성 이미지 복사", "클립보드"), ("선택 픽셀 지우기", "채우기·지우기"), ("대지 편집", "캔버스"),
            ("레이어 복제", "레이어"), ("마스크 추가", "마스크"), ("선택 레이어 그룹화", "그룹·합성"), ("전체 선택", "선택"),
            ("레벨", "보정"), ("사진 현상", "필터"), ("화면에 맞춤", "화면"), ("가이드 추가", "가이드·격자"), ("명령 찾기", "도움·작업 공간"),
            ("샘플 작업", "배우기"), ("로컬 연결", "AI 연결"), ("선택 레이어 이미지로", "내보내기"), ("그림자", "그림자"), ("스케치 사진", "스케치")
        ];
        foreach (var (key, title) in titles) if (header.StartsWith(key, StringComparison.Ordinal)) return title;
        return tab;
    }

    FrameworkElement RibbonGroup(string title, MenuItem[] items)
    {
        var group = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };
        var caption = new TextBlock { Text = title, FontSize = 11, Foreground = Theme.Subtle, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 2, 4, 2) };
        DockPanel.SetDock(caption, Dock.Bottom); group.Children.Add(caption);
        Panel buttons;
        if (items.Length <= 3)
        {
            buttons = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items) buttons.Children.Add(RibbonButton(item, large: true));
        }
        else
        {
            // Small buttons stack three per column, like a dense ribbon panel.
            var columns = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < items.Length; i += 3)
            {
                var column = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
                foreach (var item in items.Skip(i).Take(3)) column.Children.Add(RibbonButton(item, large: false));
                columns.Children.Add(column);
            }
            buttons = columns;
        }
        group.Children.Add(buttons);
        return new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 4, 0), Margin = new Thickness(0, 0, 4, 0), Child = group };
    }

    Button RibbonButton(MenuItem item, bool large)
    {
        string label = item.Header?.ToString() ?? "", gesture = item.InputGestureText ?? "";
        var button = Theme.Styled(Theme.Button("", () => { if (item.IsEnabled) Guard(() => InvokeMenuItem(item)); }, gesture.Length > 0 ? $"{label} · {gesture}" : label), "GhostButton");
        string glyph = RibbonGlyph(label);
        if (large)
        {
            // Icons share one top line across the ribbon however many lines the label takes.
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            var icon = Theme.Glyph(glyph, 22, Theme.Text); icon.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(icon);
            content.Children.Add(LargeRibbonLabel(Loc.T(label).TrimEnd('…')));
            button.Content = content; button.MinWidth = 58; button.MinHeight = 66; button.Padding = new Thickness(6, 6, 6, 4);
            button.VerticalAlignment = VerticalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top;
        }
        else
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Theme.Glyph(glyph, 15, Theme.Muted); icon.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = Loc.T(label).TrimEnd('…'), FontSize = Theme.CaptionSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            button.Content = content; button.MinHeight = 22; button.Height = 22; button.Padding = new Thickness(6, 0, 8, 0); button.HorizontalContentAlignment = HorizontalAlignment.Left;
        }
        button.Margin = new Thickness(1);
        button.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = item });
        AutomationProperties.SetName(button, label);
        string? id = ribbonItems.FirstOrDefault(p => ReferenceEquals(p.Value, item)).Key;
        if (id != null)
        {
            var menu = new ContextMenu();
            bool favorite = ribbonFavorites.Contains(id);
            var toggle = new MenuItem { Header = favorite ? "내 탭에서 빼기" : "내 탭에 추가" };
            toggle.Click += (_, _) => { if (favorite) RemoveFavorite(id); else AddFavorite(id); };
            menu.Items.Add(toggle);
            if (favorite && ribbonTab == FavoritesTab)
            {
                var left = new MenuItem { Header = "앞으로 이동" }; left.Click += (_, _) => MoveFavorite(id, -1); menu.Items.Add(left);
                var right = new MenuItem { Header = "뒤로 이동" }; right.Click += (_, _) => MoveFavorite(id, 1); menu.Items.Add(right);
            }
            button.ContextMenu = menu;
        }
        return button;
    }

    const double LargeRibbonLabelWidth = 76, LargeRibbonLabelMaxWidth = 104;

    // Large button labels break only between words ("Compositor .comp" never splits inside ".comp").
    // Text without spaces (Japanese, Chinese) keeps the normal character wrapping.
    static TextBlock LargeRibbonLabel(string text)
    {
        var block = new TextBlock { FontSize = Theme.CaptionSize, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var lines = RibbonLabelLines(text, value => MeasureRibbonText(value, block.FontSize));
        if (lines == null) { block.Text = text; block.TextWrapping = TextWrapping.Wrap; block.MaxWidth = LargeRibbonLabelWidth; }
        else { block.Text = string.Join('\n', lines); block.TextWrapping = TextWrapping.NoWrap; }
        return block;
    }

    static double MeasureRibbonText(string text, double size) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.White, 1).WidthIncludingTrailingWhitespace;

    // One line when it fits, otherwise the most even two-line split, otherwise greedy lines of whole
    // words. Null means the text has no word breaks and should wrap by character.
    internal static string[]? RibbonLabelLines(string text, Func<string, double> measure)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [text];
        if (measure(string.Join(' ', words)) <= LargeRibbonLabelWidth) return [string.Join(' ', words)];
        if (words.Length == 1) return measure(words[0]) > LargeRibbonLabelMaxWidth ? null : words;
        string[]? best = null; double bestWidth = double.MaxValue;
        for (int split = 1; split < words.Length; split++)
        {
            string first = string.Join(' ', words[..split]), second = string.Join(' ', words[split..]);
            double width = Math.Max(measure(first), measure(second));
            if (width < bestWidth) { bestWidth = width; best = [first, second]; }
        }
        if (best != null && bestWidth <= LargeRibbonLabelMaxWidth) return best;
        double limit = Math.Max(LargeRibbonLabelMaxWidth, words.Max(measure));
        var lines = new List<string>(); string line = "";
        foreach (var word in words)
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && measure(next) > limit) { lines.Add(line); line = word; }
            else line = next;
        }
        lines.Add(line);
        return lines.ToArray();
    }

    // Icons by the start of the command name; unmatched commands share a neutral glyph.
    static string RibbonGlyph(string label)
    {
        (string Key, string Glyph)[] map =
        [
            ("새 캔버스", Theme.Glyphs.NewFile), ("열기", Theme.Glyphs.Open), ("레이어로 가져오기", Theme.Glyphs.Import), ("도면 가져오기 설정", Theme.Glyphs.Import), ("Compositor .comp 가져오기", Theme.Glyphs.Import),
            ("저장", Theme.Glyphs.Save), ("다른 이름으로 저장", Theme.Glyphs.Save), ("내보내기", Theme.Glyphs.Export), ("인쇄용 CMYK", Theme.Glyphs.Palette), ("PDF", Theme.Glyphs.Document),
            ("Compositor .comp 내보내기", Theme.Glyphs.Export), ("선택 레이어 이미지로", Theme.Glyphs.Export), ("현재 문서 닫기", Theme.Glyphs.Close),
            ("실행 취소", Theme.Glyphs.Undo), ("다시 실행", Theme.Glyphs.Redo), ("합성 이미지 복사", Theme.Glyphs.Duplicate), ("이미지 붙여넣기", Theme.Glyphs.Paste),
            ("선택 픽셀 지우기", Theme.Glyphs.Delete), ("전경색으로", ToolIcons.PathData(Tool.Bucket)), ("배경색으로", ToolIcons.PathData(Tool.Bucket)), ("버킷", ToolIcons.PathData(Tool.Bucket)),
            ("대지", ToolIcons.PathData(Tool.Artboard)), ("캔버스 크기", Theme.Glyphs.Fit), ("이미지 크기", Theme.Glyphs.Transform), ("선택 영역으로 자르기", ToolIcons.PathData(Tool.Crop)), ("스케치 사진", Theme.Glyphs.Sketch),
            ("레이어 복제", Theme.Glyphs.Duplicate), ("이름 변경", Theme.Glyphs.Rename), ("변형", Theme.Glyphs.Transform), ("가로 뒤집기", Theme.Glyphs.FlipHorizontal), ("세로 뒤집기", Theme.Glyphs.FlipVertical),
            ("마스크 추가", Theme.Glyphs.MaskAdd), ("마스크 반전", Theme.Glyphs.MaskInvert), ("마스크 제거", Theme.Glyphs.MaskRemove), ("마스크", Theme.Glyphs.Mask), ("모든 레이어 병합", Theme.Glyphs.GroupRemove), ("레이어 삭제", Theme.Glyphs.Delete), ("선택 레이어 그룹화", Theme.Glyphs.GroupAdd),
            ("그룹 해제", Theme.Glyphs.GroupRemove), ("그룹으로 이동", Theme.Glyphs.Folder), ("클리핑", Theme.Glyphs.Clip), ("아래 레이어와 병합", Theme.Glyphs.Backward),
            ("텍스트", Theme.Glyphs.Text), ("픽셀 레이어로", Theme.Glyphs.Image), ("다른 문서로", Theme.Glyphs.Duplicate),
            ("전체 선택", ToolIcons.PathData(Tool.RectangleSelect)), ("선택 해제", Theme.Glyphs.Close), ("선택 반전", Theme.Glyphs.Adjustment), ("페더", Theme.Glyphs.Sparkle),
            ("확장", Theme.Glyphs.Plus), ("축소", "M5 12H19"), ("레이어의 불투명", Theme.Glyphs.SelectAlpha), ("선택 픽셀을 새 레이어로", Theme.Glyphs.Duplicate), ("선택 윤곽", ToolIcons.PathData(Tool.Move)),
            ("레벨", Theme.Glyphs.Levels), ("노출", Theme.Glyphs.Exposure), ("채도", Theme.Glyphs.HueSaturation), ("색조", Theme.Glyphs.HueSaturation), ("흑백", Theme.Glyphs.Adjustment),
            ("색상 반전", Theme.Glyphs.Adjustment), ("가우시안", ToolIcons.PathData(Tool.BlurBrush)), ("곡선", Theme.Glyphs.Curves), ("그라데이션", Theme.Glyphs.GradientMap), ("그레인", Theme.Glyphs.Grain),
            ("선택 조정 레이어", Theme.Glyphs.Sliders), ("사진 현상", Theme.Glyphs.Camera), ("모션 블러", ToolIcons.PathData(Tool.Liquify)), ("노이즈", Theme.Glyphs.Grain), ("렌즈", Theme.Glyphs.Camera),
            ("주변으로 채우기", Theme.Glyphs.FillSelection), ("배경색 제거", Theme.Glyphs.Mask), ("AI", Theme.Glyphs.Sparkle), ("마스크 페더", Theme.Glyphs.Sparkle),
            ("화면에 맞춤", Theme.Glyphs.Fit), ("실제 크기", Theme.Glyphs.Search), ("확대", Theme.Glyphs.Plus), ("가이드", Theme.Glyphs.AlignLeft), ("스냅", Theme.Glyphs.Pin),
            ("픽셀 격자", Theme.Glyphs.Grain), ("명령 찾기", Theme.Glyphs.Search), ("도움말", Theme.Glyphs.Info), ("사진 편집 작업 공간", Theme.Glyphs.Camera), ("디자인 작업 공간", Theme.Glyphs.Shape),
            ("RGB / CMYK", Theme.Glyphs.Palette), ("CMYK ICC", Theme.Glyphs.Palette), ("Windows 기본 CMYK", Theme.Glyphs.Palette), ("패널 배치", Theme.Glyphs.Reset), ("히스토그램", Theme.Glyphs.Levels),
            ("리본", Theme.Glyphs.Sliders), ("샘플", Theme.Glyphs.Learn), ("로컬 연결", Theme.Glyphs.Sparkle), ("연결 설정", Theme.Glyphs.Sliders),
            ("피사체를 글자 앞으로", Theme.Glyphs.SubjectFront),
            ("그림자", Theme.Glyphs.Shadow), ("한계값", Theme.Glyphs.Threshold), ("망점", Theme.Glyphs.Halftone), ("종이·인쇄 질감", Theme.Glyphs.PaperTexture), ("빛 번짐", Theme.Glyphs.Glow),
            ("디자인 스타일", Theme.Glyphs.Style), ("친절한 화면", Theme.Glyphs.ScreenFriendly), ("간결한 화면", Theme.Glyphs.ScreenCompact)
        ];
        if (Loc.Languages.Any(l => l.Native == label)) return Theme.Glyphs.Globe;
        if (UserProfiles.All.FirstOrDefault(p => p.Name == label) is { } profile) return profile.Glyph;
        // PDF · PSD · AI export entries share the icons of the export dialog's list.
        if (CompatibilityExport.Choices.FirstOrDefault(c => label.StartsWith(c.Title, StringComparison.Ordinal)) is { } export) return CompatibilityExportDialog.Glyph(export.Format);
        foreach (var (key, glyph) in map) if (label.StartsWith(key, StringComparison.Ordinal)) return glyph;
        return Theme.Glyphs.More;
    }

    internal void SelectRibbonTab(string tab)
    {
        ribbonTab = tab;
        if (ribbonCollapsed) ribbonCollapsed = false;
        RebuildRibbon();
    }

    internal void SetRibbonCollapsed(bool collapsed) { ribbonCollapsed = collapsed; RebuildRibbon(); }

    internal void SetRibbonMode(bool ribbon) { ribbonMode = ribbon; RebuildRibbon(); }

    internal void AddFavorite(string id) { if (!ribbonFavorites.Contains(id) && ribbonItems.ContainsKey(id)) { ribbonFavorites.Add(id); RebuildRibbon(); } }
    internal void RemoveFavorite(string id) { if (ribbonFavorites.Remove(id)) RebuildRibbon(); }
    internal void MoveFavorite(string id, int delta)
    {
        int index = ribbonFavorites.IndexOf(id), target = index + delta;
        if (index < 0 || target < 0 || target >= ribbonFavorites.Count) return;
        ribbonFavorites.RemoveAt(index); ribbonFavorites.Insert(target, id); RebuildRibbon();
    }
    internal void ResetFavorites() { ribbonFavorites = [.. DefaultFavorites]; RebuildRibbon(); }
}
