using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Compositor.Windows;

// 점경 (entourage) in the editor: the library palette (디자인 tab section, 간결한 화면 dock tab),
// placing items by click or drag (one layer, or a group when scattering), the properties of placed
// items, and 내 점경 — the user's own images kept in %LOCALAPPDATA%\Morupixel\Entourage. Headless
// checks and offscreen previews keep 내 점경 in memory unless a test path is given.
public sealed partial class MainWindow
{
    /// <summary>A palette entry: a built-in item or one of the user's images.</summary>
    internal sealed record EntourageChoice(EntourageItem? Item, EntourageCustomItem? Custom)
    {
        public string Name => Item?.Name ?? Custom!.Name;
        public EntourageView View => Item?.View ?? Custom!.View;
        public EntourageCategory Category => Item?.Category ?? Custom!.Category;
        public double Meters => Item?.DefaultMeters ?? Custom!.Meters;
        public int Variants => Item?.Variants ?? 1;
        public string Id => Item?.Id ?? Custom!.ItemId;
    }

    internal const int EntourageCustomTab = 4;
    internal int entourageTab;
    internal EntourageView entourageView = EntourageView.Elevation;
    internal bool entourageScatter;
    internal int entourageScatterCount = 5;
    int entourageSeed = 1;
    // Line colour and weight last chosen for an item: new items match them.
    uint entourageLine = EntourageSpec.DefaultLine;
    double entourageWeight = 1;
    // The 1 m = px a user set for a document tab; otherwise the document's own entourage or 1:100.
    readonly Dictionary<Guid, double> entourageScaleByTab = [];
    internal string? entourageStore;
    List<EntourageCustomItem>? entourageLibrary;
    internal Func<EntourageImportDialog, bool>? entourageDialogRunner;
    readonly Dictionary<string, Action> entourageRefresh = new(StringComparer.Ordinal);
    static readonly Dictionary<(string, int, uint), BitmapSource> entourageThumbnails = [];
    bool entourageDropReady;

    string? EntourageDirectory => entourageStore ?? (headlessTesting ? null : EntourageStore.DefaultDirectory);

    // ---- Scale ------------------------------------------------------------------------------

    internal double EntourageScale
    {
        get
        {
            if (!HasDocument) return 40;
            if (activeTab >= 0 && entourageScaleByTab.TryGetValue(tabs[activeTab].Id, out var chosen)) return chosen;
            return EntourageRenderer.DocumentPixelsPerMeter(doc) ?? EntourageRenderer.DefaultPixelsPerMeter(doc);
        }
    }

    internal void SetEntourageScale(double pixelsPerMeter)
    {
        if (!HasDocument || activeTab < 0) return;
        entourageScaleByTab[tabs[activeTab].Id] = Math.Round(Math.Clamp(pixelsPerMeter, EntourageSpec.MinPixelsPerMeter, EntourageSpec.MaxPixelsPerMeter), 3);
        RefreshEntouragePalettes();
    }

    string EntourageScaleNote()
    {
        if (!HasDocument) return "문서를 열면 축척을 정할 수 있습니다.";
        double ppm = EntourageScale; int person = (int)Math.Round(1.7 * ppm);
        string basis = activeTab >= 0 && entourageScaleByTab.ContainsKey(tabs[activeTab].Id) ? "직접 정한 축척"
            : EntourageRenderer.DocumentPixelsPerMeter(doc) != null ? "이 문서의 점경과 같은 축척" : $"문서 해상도 {doc.Dpi:0.#} DPI에서 1:100";
        return $"사람(1.7 m) {person:N0} px · {Loc.T(basis)}";
    }

    // ---- 내 점경 library ---------------------------------------------------------------------

    internal IReadOnlyList<EntourageCustomItem> CustomEntourageLibrary()
    {
        if (entourageLibrary != null) return entourageLibrary;
        entourageLibrary = [];
        if (EntourageDirectory is { } directory) entourageLibrary.AddRange(EntourageStore.Load(directory));
        return entourageLibrary;
    }

    void SaveEntourageLibrary()
    {
        if (EntourageDirectory is { } directory && entourageLibrary != null) EntourageStore.Save(entourageLibrary, directory);
    }

    // The library, then the user's items this document carries that the library does not have.
    internal IReadOnlyList<EntourageCustomItem> CustomEntourageChoices()
    {
        var result = CustomEntourageLibrary().ToList();
        if (!HasDocument) return result;
        foreach (var layer in doc.Layers)
            if (layer.Entourage is { IsCustom: true, Source: { } source } spec && result.All(i => i.ItemId != spec.ItemId) &&
                Guid.TryParseExact(spec.ItemId[EntourageSpec.CustomPrefix.Length..], "N", out var id))
                result.Add(new EntourageCustomItem(id, layer.Name, EntourageCategory.Props, spec.View, spec.Meters, spec.LineDrawing, source));
        return result;
    }

    internal EntourageCustomItem AddCustomEntourage(EntourageCustomItem item)
    {
        EntourageStore.ValidatePixels(item.Pixels);
        CustomEntourageLibrary(); var library = entourageLibrary!;
        if (library.FirstOrDefault(i => i.Id == item.Id) is { } known) return known;
        if (library.Count >= EntourageStoreLimits.MaxItems) throw new InvalidDataException($"내 점경은 {EntourageStoreLimits.MaxItems}개까지 보관합니다. 쓰지 않는 항목을 삭제하세요.");
        var clean = item with { Name = EntourageStore.CleanName(item.Name) };
        library.Add(clean);
        try { SaveEntourageLibrary(); }
        catch { library.Remove(clean); throw; }
        RefreshEntouragePalettes();
        return clean;
    }

    internal void RenameCustomEntourage(Guid id, string name)
    {
        CustomEntourageLibrary(); var library = entourageLibrary!; int index = library.FindIndex(i => i.Id == id);
        if (index < 0) return;
        var before = library[index]; library[index] = before with { Name = EntourageStore.CleanName(name) };
        try { SaveEntourageLibrary(); } catch { library[index] = before; throw; }
        status.Text = $"내 점경 이름을 바꿨습니다: {library[index].Name}";
        RefreshEntouragePalettes();
    }

    // Documents that placed the item keep their own copy.
    internal void RemoveCustomEntourage(Guid id)
    {
        CustomEntourageLibrary(); var library = entourageLibrary!; int index = library.FindIndex(i => i.Id == id);
        if (index < 0) return;
        var removed = library[index]; library.RemoveAt(index);
        try { SaveEntourageLibrary(); } catch { library.Insert(index, removed); throw; }
        status.Text = $"내 점경에서 삭제했습니다: {removed.Name} · 이 점경을 놓은 문서는 그대로 남습니다.";
        RefreshEntouragePalettes();
    }

    void ChooseCustomEntourageImage()
    {
        var open = new OpenFileDialog { Title = "내 점경으로 쓸 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        ImportCustomEntourage(open.FileName);
    }

    /// <summary>Converts an image in the import dialog, adds it to 내 점경 and places it once when a document is open.</summary>
    internal EntourageCustomItem? ImportCustomEntourage(string path)
    {
        var image = EntourageImport.Load(path);
        var category = entourageTab is >= 0 and < EntourageCustomTab ? (EntourageCategory)entourageTab : EntourageCategory.People;
        var dialog = new EntourageImportDialog(headlessTesting ? null : this, image, Path.GetFileNameWithoutExtension(path), category, entourageView);
        bool accepted = entourageDialogRunner?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!accepted || dialog.Result is not { } created) return null;
        var stored = AddCustomEntourage(created);
        entourageTab = EntourageCustomTab; entourageView = stored.View;
        RefreshEntouragePalettes();
        if (HasDocument) PlaceEntourage(new EntourageChoice(null, stored), scatter: false);
        status.Text = $"내 점경에 추가했습니다: {stored.Name}";
        return stored;
    }

    // ---- Palette -----------------------------------------------------------------------------

    internal void RefreshEntouragePalettes() { foreach (var refresh in entourageRefresh.Values.ToArray()) refresh(); }

    // The section in the 디자인 tab (and wherever a profile places it).
    void AddEntourageSection(StackPanel panel)
    {
        WorkspaceSection(panel, "점경", "사람·나무·탈것·소품을 도면에 놓습니다. 눌러서 화면 가운데에, 끌어서 원하는 자리에 놓고, 놓은 뒤에도 높이·색·채우기를 바꿀 수 있습니다.");
        panel.Children.Add(EntouragePalette("design"));
    }

    UIElement BuildDockEntourage() => DockScroll(EntouragePalette("dock"));

    internal FrameworkElement EntouragePalette(string host)
    {
        EnsureEntourageDrop();
        var root = new StackPanel();
        AutomationProperties.SetName(root, "점경 라이브러리");
        var tabs = new SegmentedChoice<int>([(0, "사람"), (1, "나무 · 식물"), (2, "탈것"), (3, "소품"), (EntourageCustomTab, "내 점경")], entourageTab) { Margin = new Thickness(2, 0, 2, 6) };
        foreach (var button in tabs.Buttons)
        {
            // Five categories share the row: caption-size labels keep 나무 · 식물 on one line at the usual panel widths.
            button.MinWidth = 0; button.Padding = new Thickness(3, 5, 3, 5);
            if (button.Content is string caption) { button.Content = new KeepWordsTextBlock { Text = caption, FontSize = Theme.CaptionSize, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }; button.ToolTip = caption; }
        }
        FitSegments(tabs);
        root.Children.Add(tabs);
        var views = new SegmentedChoice<EntourageView>([(EntourageView.Elevation, "입면·단면"), (EntourageView.Plan, "평면")], entourageView) { Margin = new Thickness(2, 0, 2, 6) };
        foreach (var button in views.Buttons)
        {
            button.MinWidth = 0; button.Padding = new Thickness(8, 4, 8, 4);
            if (button.Content is string caption) { button.Content = new KeepWordsTextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }; button.ToolTip = caption; }
        }
        root.Children.Add(views);
        var search = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); search.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Theme.Glyph(Theme.Glyphs.Search, 15, Theme.Muted); icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Center; search.Children.Add(icon);
        var query = new TextBox { MinHeight = Theme.ControlHeight, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(query, "점경 검색");
        var placeholder = new TextBlock { Text = "점경 검색 (예: 나무, 자전거, 앉은)", Foreground = Theme.Subtle, FontSize = Theme.CaptionSize, Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(query, 1); Grid.SetColumn(placeholder, 1); search.Children.Add(query); search.Children.Add(placeholder);
        root.Children.Add(search);
        var body = new StackPanel(); root.Children.Add(body);
        var options = new StackPanel(); root.Children.Add(options);
        object? shownKey = null;
        void Rebuild()
        {
            string q = query.Text.Trim();
            var custom = CustomEntourageChoices();
            var key = (entourageTab, entourageView, q, string.Join(",", custom.Select(c => c.Id + c.Name)), HasDocument);
            if (Equals(key, shownKey)) { UpdateOptions(); return; }
            shownKey = key;
            body.Children.Clear();
            IEnumerable<EntourageChoice> choices;
            if (q.Length > 0)
                choices = EntourageLibrary.All.Where(i => EntourageLibrary.Matches(i, q)).Select(i => new EntourageChoice(i, null))
                    .Concat(custom.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(c => new EntourageChoice(null, c)));
            else if (entourageTab == EntourageCustomTab) choices = custom.Where(c => c.View == entourageView).Select(c => new EntourageChoice(null, c));
            else choices = EntourageLibrary.All.Where(i => (int)i.Category == entourageTab && i.View == entourageView).Select(i => new EntourageChoice(i, null));
            var list = choices.ToList();
            if (list.Count > 0) body.Children.Add(QuickActions.Grid(4, list.Select(EntourageTile), 76));
            else
            {
                string empty = q.Length > 0 ? "찾는 점경이 없습니다. 다른 낱말로 찾아 보세요."
                    : entourageTab == EntourageCustomTab ? "이 보기의 내 점경이 없습니다. 투명 PNG, 사진, 선 그림으로 내 점경을 만들 수 있습니다." : "이 보기의 점경이 없습니다.";
                var note = Theme.Label(empty, Theme.CaptionSize, Theme.Subtle); note.Margin = new Thickness(2, 0, 2, 8); body.Children.Add(note);
            }
            if (entourageTab == EntourageCustomTab || q.Length > 0)
                body.Children.Add(Theme.ActionRow("이미지로 점경 추가…", () => Guard(() => { CommitFocusedInspectorField(); ChooseCustomEntourageImage(); }),
                    "투명 PNG는 그대로, 사진은 AI로 배경을 지우고, 종이에 그린 그림은 선만 남겨 내 점경에 등록하고 바로 놓습니다.", Theme.Glyphs.Plus));
            UpdateOptions();
        }
        TextBlock? scaleNote = null; TextBox? scaleBox = null;
        void UpdateOptions()
        {
            if (scaleNote != null) scaleNote.Text = EntourageScaleNote();
            if (scaleBox != null && !scaleBox.IsKeyboardFocusWithin) scaleBox.Text = EntourageScale.ToString("0.##", CultureInfo.InvariantCulture);
        }
        // Scatter and scale.
        var scatterRow = new DockPanel { Margin = new Thickness(0, 2, 2, 6), LastChildFill = false };
        var scatter = new OptionToggle(Theme.Glyphs.Scatter, "자연스럽게 흩어 놓기", "여러 개를 크기·방향·모양을 조금씩 달리해 흩어 놓습니다. 선택 영역이 있으면 그 안(입면은 아래쪽 바닥선)에 놓고, 한 그룹으로 묶습니다.", entourageScatter);
        AutomationProperties.SetName(scatter, "자연스럽게 흩어 놓기");
        scatter.Margin = new Thickness(0, 0, 8, 0);
        scatter.Checked += (_, _) => entourageScatter = true; scatter.Unchecked += (_, _) => entourageScatter = false;
        DockPanel.SetDock(scatter, Dock.Left); scatterRow.Children.Add(scatter);
        var count = PropertyRows.NumberBox(entourageScatterCount, "흩어 놓을 개수"); count.Width = 52; count.ToolTip = "한 번에 놓을 개수 (2~24)";
        void CommitCount()
        {
            if (int.TryParse(count.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 2 and <= 24) entourageScatterCount = n;
            count.Text = entourageScatterCount.ToString(CultureInfo.InvariantCulture);
        }
        count.LostFocus += (_, _) => CommitCount(); count.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitCount(); e.Handled = true; } };
        var times = Theme.Label("×", Theme.CaptionSize, Theme.Muted); times.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(times, Dock.Left); DockPanel.SetDock(count, Dock.Left); scatterRow.Children.Add(times); scatterRow.Children.Add(count);
        options.Children.Add(scatterRow);
        scaleBox = PropertyRows.NumberBox(EntourageScale, "1 m당 px");
        scaleBox.ToolTip = "새로 놓는 점경의 축척입니다. 도면에서 1 m가 몇 px인지 입력하세요.";
        void CommitScale()
        {
            if (!HasDocument) return;
            if (double.TryParse(scaleBox!.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= EntourageSpec.MinPixelsPerMeter && v <= EntourageSpec.MaxPixelsPerMeter)
            { if (Math.Abs(v - EntourageScale) > 1e-9) SetEntourageScale(v); }
            else status.Text = $"1 m당 px는 {EntourageSpec.MinPixelsPerMeter}~{EntourageSpec.MaxPixelsPerMeter:0} 사이로 입력하세요.";
            UpdateOptions();
        }
        scaleBox.LostFocus += (_, _) => CommitScale(); scaleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitScale(); e.Handled = true; } };
        options.Children.Add(PropertyRows.Inline("축척 · 1 m당 px", scaleBox, 78, new Thickness(2, 0, 2, 2)));
        scaleNote = Theme.Label("", Theme.CaptionSize, Theme.Subtle); scaleNote.Margin = new Thickness(2, 0, 2, 4);
        options.Children.Add(scaleNote);
        var hint = Density.Mark(Theme.Label("누르면 화면 가운데에, 끌어 놓으면 그 자리에 놓습니다. 입면은 발끝·밑동이 놓은 곳(바닥선)에 닿습니다.", Theme.CaptionSize, Theme.Subtle), DensityRole.Description);
        hint.Margin = new Thickness(2, 2, 2, 4); options.Children.Add(hint);

        tabs.Changed += t => { entourageTab = t; Rebuild(); };
        views.Changed += v => { entourageView = v; Rebuild(); };
        query.TextChanged += (_, _) => { placeholder.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };
        // The open document can change under the palette: catch up when the pointer comes back.
        root.MouseEnter += (_, _) => Rebuild();
        root.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Rebuild(); };
        entourageRefresh[host] = () => { if (tabs.Selected != entourageTab) tabs.Select(entourageTab); if (views.Selected != entourageView) views.Select(entourageView); scatter.IsChecked = entourageScatter; shownKey = null; Rebuild(); };
        Rebuild();
        return root;
    }

    // A segment row whose labels do not fit (a long translation in a narrow dock) wraps onto a second
    // row instead of breaking words: the columns follow the widest word of any label.
    static void FitSegments<T>(SegmentedChoice<T> choice)
    {
        if (choice.Child is not UniformGrid grid) return;
        int count = grid.Children.Count;
        void Fit()
        {
            double width = choice.ActualWidth;
            if (width <= 0) return;
            double widest = 0;
            foreach (var button in choice.Buttons)
                if (button.Content is TextBlock label)
                    foreach (var word in label.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var text = new FormattedText(word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            new Typeface(label.FontFamily, label.FontStyle, FontWeights.SemiBold, label.FontStretch), label.FontSize, Brushes.Black, 1);
                        widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right + 6);
                    }
            int fits = Math.Clamp((int)((width - 6) / Math.Max(1, widest)), 1, count);
            int rows = (count + fits - 1) / fits, columns = (count + rows - 1) / rows;
            if (grid.Columns != columns || grid.Rows != rows) { grid.Rows = rows; grid.Columns = columns; }
        }
        choice.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(); };
        // The labels are translated when they load, after the first layout; their size then changes.
        foreach (var button in choice.Buttons) if (button.Content is TextBlock label) label.SizeChanged += (_, _) => Fit();
    }
    // A swatch of the item drawn in black on white paper (also in the dark theme), with its name.
    Button EntourageTile(EntourageChoice choice)
    {
        string category = EntourageLibrary.CategoryName(choice.Category), view = EntourageLibrary.ViewName(choice.View);
        string shown = choice.Custom != null ? choice.Name : Loc.T(choice.Name);
        var button = Theme.Button("", () => { }, $"{shown} · {Loc.T(category)} · {Loc.T(view)} · {choice.Meters:0.##} m\n누르면 화면 가운데에, 끌어 놓으면 그 자리에 놓습니다.");
        button.Tag = choice;
        var content = new StackPanel();
        var image = new Image { Source = EntourageThumbnail(choice), Stretch = Stretch.Uniform, Margin = new Thickness(4) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        content.Children.Add(new Border { Height = 64, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = Theme.Stroke, Background = Brushes.White, Child = image });
        var label = new KeepWordsTextBlock { Text = choice.Name, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        if (choice.Custom != null) Loc.Keep(label);
        content.Children.Add(label);
        button.Content = content; button.Padding = new Thickness(4, 4, 4, 6); button.Margin = new Thickness(3);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, choice.Name);
        // A press that moves becomes a drag onto the canvas; a press released in place is a click.
        Point? pressed = null; bool dragged = false;
        button.PreviewMouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(button); dragged = false; };
        button.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(button) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null; dragged = true;
            try { DragDrop.DoDragDrop(button, new DataObject(typeof(EntourageChoice), choice), DragDropEffects.Copy); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        };
        button.Click += (_, _) => { pressed = null; if (dragged) { dragged = false; return; } Guard(() => { CommitFocusedInspectorField(); PlaceEntourage(choice); }); };
        if (choice.Custom is { } custom) button.ContextMenu = CustomEntourageMenu(custom);
        return button;
    }

    ContextMenu CustomEntourageMenu(EntourageCustomItem item)
    {
        var menu = new ContextMenu();
        void Add(string header, Action run) { var entry = new MenuItem { Header = header }; entry.Click += (_, _) => Guard(run); menu.Items.Add(entry); }
        if (CustomEntourageLibrary().Any(i => i.Id == item.Id))
        {
            Add("이름 바꾸기…", () => { var fields = Dialogs.Fields(this, "점경 이름 바꾸기", ("점경 이름", item.Name)); if (fields != null) RenameCustomEntourage(item.Id, fields[0]); });
            Add("내 점경에서 삭제", () => RemoveCustomEntourage(item.Id));
        }
        else Add("내 점경에 저장", () => { AddCustomEntourage(item); status.Text = $"내 점경에 추가했습니다: {item.Name}"; });
        return menu;
    }

    static BitmapSource EntourageThumbnail(EntourageChoice choice)
    {
        const int side = 112;
        if (choice.Custom is { } custom)
        {
            var spec = new EntourageSpec { ItemId = custom.ItemId, View = custom.View, Meters = custom.Meters, Fill = EntourageFill.None, LineDrawing = custom.LineDrawing, Source = custom.Pixels, PixelsPerMeter = 40 };
            return EntourageRenderer.Style(custom.Pixels, spec).Thumbnail(side);
        }
        var item = choice.Item!; var key = (item.Id, 0, EntourageSpec.DefaultLine);
        lock (entourageThumbnails) if (entourageThumbnails.TryGetValue(key, out var cached)) return cached;
        var b = EntourageLibrary.Art(item, 0).Bounds;
        double size = item.View == EntourageView.Plan ? Math.Max(b.Width, b.Height) : Math.Max(1, -b.Top);
        // The longer side fills the swatch; the line stays readable (about 1.15 px) whatever the item's size.
        double ppm = Math.Clamp((side - 8) / Math.Max(b.Width, b.Height) * size / item.DefaultMeters, EntourageSpec.MinPixelsPerMeter, EntourageSpec.MaxPixelsPerMeter);
        var spec2 = EntourageRenderer.Spec(item, ppm) with
        {
            LineWeight = Math.Clamp(1.15 / EntourageRenderer.BasePen(ppm), EntourageSpec.MinLineWeight, EntourageSpec.MaxLineWeight),
            Fill = item.DefaultFill == EntourageFill.None ? EntourageFill.None : EntourageFill.White
        };        var (_, preview, _) = EntourageRenderer.Draw(spec2);
        var bitmap = preview.Bitmap();
        lock (entourageThumbnails) entourageThumbnails[key] = bitmap;
        return bitmap;
    }

    // ---- Placing -----------------------------------------------------------------------------

    void EnsureEntourageDrop()
    {
        if (entourageDropReady) return;
        entourageDropReady = true;
        canvas.AllowDrop = true;
        canvas.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(EntourageChoice))) return;
            e.Effects = HasDocument ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        };
        canvas.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(EntourageChoice)) is not EntourageChoice choice) return;
            e.Handled = true;
            var at = canvas.ToDocument(e.GetPosition(canvas));
            Guard(() => { PlaceEntourage(choice, at); Activate(); canvas.Focus(); });
        };
    }

    EntourageSpec EntourageSpecFor(EntourageChoice choice, double pixelsPerMeter)
    {
        var spec = choice.Item is { } item ? EntourageRenderer.Spec(item, pixelsPerMeter)
            : new EntourageSpec { ItemId = choice.Custom!.ItemId, View = choice.Custom.View, Meters = choice.Custom.Meters, PixelsPerMeter = pixelsPerMeter, Fill = EntourageFill.None, LineDrawing = choice.Custom.LineDrawing, Source = choice.Custom.Pixels };
        spec = spec with { LineArgb = entourageLine, LineWeight = entourageWeight };
        // Very large items at a fine scale are made smaller rather than refused.
        double limit = EntourageSpec.MaxSide * .98 / spec.PixelsPerMeter;
        return spec.Meters > limit ? spec with { Meters = Math.Max(EntourageSpec.MinMeters, Math.Round(limit, 2)) } : spec;
    }

    // Width of one item in pixels at its spec (for spacing a scatter).
    static double EntourageFootprint(EntourageSpec spec)
    {
        if (spec.Source is { } source) return spec.SizePixels * source.Width / (spec.View == EntourageView.Plan ? Math.Max(source.Width, source.Height) : source.Height);
        var item = EntourageLibrary.Find(spec.ItemId)!; var b = EntourageLibrary.Art(item, spec.Variant).Bounds;
        double size = item.View == EntourageView.Plan ? Math.Max(b.Width, b.Height) : Math.Max(1, -b.Top);
        return spec.SizePixels * b.Width / size;
    }

    /// <summary>
    /// Places an item: at `at` (document pixels; the ground point in elevation, the centre in plan), in the
    /// current selection, or in the middle of the view. With scatter several copies go into one group. One undo step.
    /// </summary>
    internal IReadOnlyList<Layer> PlaceEntourage(EntourageChoice choice, Point? at = null, bool? scatter = null, int? count = null)
    {
        if (!HasDocument) { status.Text = "먼저 문서를 열거나 새 캔버스를 만드세요."; return []; }
        CommitFocusedInspectorField(); CancelGesture();
        var spec = EntourageSpecFor(choice, EntourageScale);
        double size = spec.SizePixels, footprint = EntourageFootprint(spec);
        int n = scatter ?? entourageScatter ? Math.Clamp(count ?? entourageScatterCount, 2, 24) : 1;
        Rect? area = null; Func<Point, bool>? inside = null;
        var page = new Rect(0, 0, doc.Width, doc.Height);
        if (at == null && selection is { } region && Rect.Intersect(region.Bounds, page) is { IsEmpty: false } bounds && bounds.Width >= 1 && bounds.Height >= 1)
        { area = bounds; inside = p => region.Contains(p.X, p.Y); }
        Point anchor;
        if (at is { } point) anchor = point;
        else if (area is { } a) anchor = spec.View == EntourageView.Plan ? new Point(a.X + a.Width / 2, a.Y + a.Height / 2) : new Point(a.X + a.Width / 2, a.Bottom);
        else
        {
            var view = canvas.ActualWidth > 0 && canvas.Zoom > 0 ? Rect.Intersect(canvas.VisibleDocumentRect, page) : page;
            if (view.IsEmpty) view = page;
            var center = new Point(view.X + view.Width / 2, view.Y + view.Height / 2);
            anchor = spec.View == EntourageView.Plan ? center : new Point(center.X, Math.Min(page.Bottom, center.Y + Math.Min(size, view.Height * .8) / 2));
        }
        string name = choice.Custom != null ? choice.Name : Loc.T(choice.Name);
        var created = new List<Layer>(); Layer? group = null;
        if (n == 1)
        {
            Edit("점경 넣기", () =>
            {
                var layer = EntourageRenderer.Create(spec, anchor, name);
                doc.Add(layer); created.Add(layer);
                selectedLayers.Clear(); selectedLayers.Add(layer.Id); doc.ActiveId = layer.Id; maskEditing = false;
            });
        }
        else
        {
            var placements = EntourageRenderer.Scatter(spec.IsCustom ? choice.Category : choice.Item!.Category, spec.View, spec.Meters, spec.PixelsPerMeter, footprint, choice.Variants, n, anchor, area, inside, entourageSeed++);
            Edit("점경 흩어 놓기", () =>
            {
                group = DocumentFeatures.CreateGroup(doc, $"{name} ×{placements.Count}");
                doc.Add(group);
                foreach (var p in placements)
                {
                    var layer = EntourageRenderer.Create(spec with { Meters = p.Meters, Variant = p.Variant }, p.Anchor, name, p.Flip, p.Rotation);
                    layer.ParentId = group.Id; doc.Add(layer); created.Add(layer);
                }
                selectedLayers.Clear(); selectedLayers.Add(group.Id); doc.ActiveId = group.Id; maskEditing = false;
            });
        }
        if (created.Count == 0 || !doc.Layers.Contains(created[0])) return [];
        if (tool is not (Tool.Move or Tool.Hand)) SetTool(Tool.Move);
        status.Text = n == 1 ? $"점경을 놓았습니다: {name} · 속성에서 높이·색·채우기를 바꿀 수 있습니다." : $"점경 {created.Count}개를 흩어 놓았습니다: {name} · 그룹을 고르면 한꺼번에 바꿀 수 있습니다.";
        RefreshEntouragePalettes();
        return created;
    }

    // ---- Properties --------------------------------------------------------------------------

    // The placed items an edit applies to: the selected entourage layers and those inside selected groups, unlocked.
    internal Layer[] EntourageTargets()
    {
        if (!HasDocument || doc.Active == null) return [];
        var ids = ExportSelectionIds().ToHashSet(); var children = doc.Layers.ToLookup(l => l.ParentId);
        var result = new List<Layer>(); var seen = new HashSet<Guid>();
        void Visit(Layer layer, int depth)
        {
            if (depth > 16 || !seen.Add(layer.Id)) return;
            if (layer.Entourage != null) { if (!IsLockedWithParents(layer)) result.Add(layer); return; }
            if (layer.Kind == LayerKind.Group) foreach (var child in children[layer.Id]) Visit(child, depth + 1);
        }
        foreach (var layer in doc.Layers.Where(l => ids.Contains(l.Id))) Visit(layer, 0);
        return [.. result];
    }

    internal void EditEntourage(string label, Func<EntourageSpec, EntourageSpec> change, bool resetSize = false)
    {
        CommitFocusedInspectorField();
        var ids = EntourageTargets().Select(l => l.Id).ToArray();
        if (ids.Length == 0) { status.Text = "점경 레이어를 선택하세요. 잠긴 레이어는 먼저 잠금을 해제하세요."; return; }
        Edit(label, () =>
        {
            foreach (var id in ids)
            {
                var layer = doc.Layers.Single(l => l.Id == id);
                var next = change(layer.Entourage!);
                // A built-in item whose drawing is not in this version keeps its content as it is.
                if (!next.IsCustom && EntourageLibrary.Find(next.ItemId) == null) continue;
                EntourageRenderer.Update(layer, next, resetSize);
            }
        });
    }

    void AddEntourageProperties(Layer active)
    {
        var targets = EntourageTargets();
        if (targets.Length == 0) return;
        var first = targets[0].Entourage!; bool many = targets.Length > 1;
        var item = EntourageLibrary.Find(first.ItemId);
        bool custom = first.IsCustom, known = custom || item != null;
        properties.Children.Add(Theme.Section("점경"));
        var identity = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
        var choice = item != null ? new EntourageChoice(item, null) : custom ? new EntourageChoice(null, new EntourageCustomItem(Guid.Empty, targets[0].Name, EntourageCategory.Props, first.View, first.Meters, first.LineDrawing, first.Source!)) : null;
        var swatch = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Background = Brushes.White, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        if (choice != null) swatch.Child = new Image { Source = EntourageThumbnail(choice), Stretch = Stretch.Uniform, Margin = new Thickness(3) };
        DockPanel.SetDock(swatch, Dock.Left); identity.Children.Add(swatch);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Theme.Label(many ? $"점경 {targets.Length}개" : item != null ? item.Name : targets[0].Name, Theme.BodySize); title.FontWeight = FontWeights.SemiBold;
        if (!many && item == null) Loc.Keep(title);
        titles.Children.Add(title);
        string kind = custom ? "내 점경" : item != null ? EntourageLibrary.CategoryName(item.Category) : "점경";
        titles.Children.Add(Theme.Label($"{Loc.T(kind)} · {Loc.T(EntourageLibrary.ViewName(first.View))}", Theme.CaptionSize, Theme.Muted));
        identity.Children.Add(titles); properties.Children.Add(identity);
        if (!known)
        {
            var note = Theme.Label("이 버전에 없는 점경입니다. 모양은 그대로 두고 위치·크기만 바꿀 수 있습니다.", Theme.CaptionSize, Theme.Subtle); note.Margin = new Thickness(2, 0, 2, 6);
            properties.Children.Add(note); return;
        }
        var boundDocument = doc; long version = inspectorVersion;
        bool Current() => ReferenceEquals(doc, boundDocument) && inspectorVersion == version;

        // Real size and scale; editing the size drops a size set with the handles.
        // The size shown includes a size set with the transform handles.
        double shown = targets[0].Scale * targets[0].ScaleY / (custom ? CustomScaleOf(first) : 1);
        var meters = PropertyRows.NumberBox(Math.Round(first.Meters * shown, 3), first.View == EntourageView.Plan ? "점경 길이" : "점경 높이");
        var scale = PropertyRows.NumberBox(Math.Round(first.PixelsPerMeter, 3), "점경 축척");
        meters.ToolTip = first.View == EntourageView.Plan ? "평면에서 긴 쪽 길이(수관은 지름), 미터" : "바닥에서 꼭대기까지 높이, 미터";
        scale.ToolTip = "도면에서 1 m가 몇 px인지입니다. 바꾸면 같은 높이로 다시 그립니다.";
        void Number(TextBox box, double min, double max, string label, Func<EntourageSpec, double, EntourageSpec> apply, bool reset)
        {
            string original = box.Text;
            void Commit()
            {
                if (!Current() || box.Text == original) return;
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < min || v > max)
                { status.Text = $"{label}: {min:0.##}~{max:0.##} 사이의 숫자를 입력하세요."; box.BorderBrush = Theme.Danger; return; }
                original = box.Text; EditEntourage(label, s => apply(s, v), reset);
            }
            Action commit = Commit;
            box.GotFocus += (_, _) => pendingInspectorCommit = commit;
            box.LostFocus += (_, _) => { if (ReferenceEquals(pendingInspectorCommit, commit)) pendingInspectorCommit = null; Commit(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        }
        Number(meters, EntourageSpec.MinMeters, EntourageSpec.MaxMeters, first.View == EntourageView.Plan ? "점경 길이" : "점경 높이", (s, v) => s with { Meters = v }, true);
        Number(scale, EntourageSpec.MinPixelsPerMeter, EntourageSpec.MaxPixelsPerMeter, "점경 축척", (s, v) => s with { PixelsPerMeter = v }, false);
        properties.Children.Add(PropertyRows.Pair(first.View == EntourageView.Plan ? "길이 · m" : "높이 · m", meters, "1 m당 px", scale));

        // Variant, fill, colour and line weight.
        if (!custom && item!.Variants > 1)
            properties.Children.Add(Theme.ActionRow("다른 모양으로 바꾸기", () => Guard(() => { if (Current()) EditEntourage("점경 모양", s => s with { Variant = (s.Variant + 1) % item.Variants }); }),
                $"같은 종류의 다른 모양 {item.Variants}가지를 차례로 보여 줍니다.", Theme.Glyphs.Swap));
        {
            properties.Children.Add(PropertyRows.Caption("채우기"));
            (EntourageFill, string)[] fills = custom && !first.LineDrawing
                ? [(EntourageFill.None, "원본"), (EntourageFill.White, "흐리게"), (EntourageFill.Gray, "회색조"), (EntourageFill.Solid, "실루엣")]
                : [(EntourageFill.None, "없음"), (EntourageFill.White, "흰 바탕"), (EntourageFill.Gray, "회색 바탕"), (EntourageFill.Solid, "실루엣")];
            var fill = new SegmentedChoice<EntourageFill>(fills, first.Fill) { Margin = new Thickness(2, 0, 2, 8) };
            foreach (var button in fill.Buttons)
            {
                button.MinWidth = 0; button.Padding = new Thickness(6, 4, 6, 4);
                if (button.Content is string label) { button.Content = new KeepWordsTextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }; button.ToolTip = label; }
            }
            FitSegments(fill);
            AutomationProperties.SetName(fill, "점경 채우기");
            fill.ToolTip = custom && !first.LineDrawing ? "원본 색, 흐리게, 회색조, 선 색 실루엣" : "선만 · 흰 바탕 · 회색 바탕 · 선 색으로 채운 실루엣";
            fill.Changed += f => Guard(() => { if (Current()) EditEntourage("점경 채우기", s => s with { Fill = f }); });
            properties.Children.Add(fill);
        }
        var row = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var reset = Theme.IconButton(Theme.Glyphs.Revert, () => Guard(() => { if (Current()) { entourageLine = EntourageSpec.DefaultLine; EditEntourage("점경 선 색", s => s with { LineArgb = EntourageSpec.DefaultLine }); } }), "선 색 기본값", 26, 14);
        reset.IsEnabled = first.LineArgb != EntourageSpec.DefaultLine; reset.Margin = new Thickness(4, 0, 0, 0); DockPanel.SetDock(reset, Dock.Right); row.Children.Add(reset);
        var chip = PropertyRows.ColorChip(VectorShapes.Color(first.LineArgb), $"#{first.LineArgb & 0xFFFFFF:X6}", () =>
        {
            if (!Current()) return;
            var dialog = new ColorPickerDialog(this, VectorShapes.Color(first.LineArgb), "점경 선 색");
            if (dialog.ShowDialog() == true && Current()) SetEntourageLineColor(dialog.SelectedColor);
        }, "점경 선 색 변경");
        chip.MinWidth = 120; DockPanel.SetDock(chip, Dock.Right); row.Children.Add(chip);
        var caption = Theme.Label("선 색", Theme.BodySize, Theme.Muted); caption.Margin = new Thickness(1, 2, 8, 2); row.Children.Add(caption);
        properties.Children.Add(row);
        if (!custom || first.LineDrawing)
        {
            var weight = PropertyRows.NumberBox(Math.Round(first.LineWeight * 100), "점경 선 굵기");
            weight.ToolTip = "100%는 축척에 맞춘 기본 굵기입니다. 같은 축척의 점경은 같은 굵기로 그려집니다.";
            Number(weight, EntourageSpec.MinLineWeight * 100, EntourageSpec.MaxLineWeight * 100, "점경 선 굵기", (s, v) => s with { LineWeight = v / 100 }, false);
            weight.LostFocus += (_, _) => { if (double.TryParse(weight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 25 && v <= 400) entourageWeight = v / 100; };
            properties.Children.Add(PropertyRows.Inline("선 굵기 · %", weight));
        }
        var commands = new List<Button>
        {
            QuickActions.Command(Theme.Glyphs.FlipHorizontal, "좌우 뒤집기", Run(() => FlipEntourage()), "선택한 점경을 좌우로 뒤집습니다. 놓인 자리는 그대로입니다.", "점경 좌우 뒤집기"),
            QuickActions.Command(Theme.Glyphs.Shadow, first.View == EntourageView.Plan ? "그림자 드리우기" : "바닥에 그림자", Run(AddShadow),
                first.View == EntourageView.Plan ? "실제 높이에 맞춘 평면 그림자를 새 레이어로 만듭니다." : "발끝·밑동에서 바닥으로 눕는 그림자를 새 레이어로 만듭니다.", "점경 그림자 드리우기"),
            QuickActions.Command(Theme.Glyphs.Transform, "축척 기준으로", Run(UseEntourageAsScale), "이 점경의 지금 크기를 새로 놓을 점경의 축척으로 씁니다. 손잡이로 크기를 맞춘 뒤 누르세요.", "이 크기를 점경 축척 기준으로"),
            QuickActions.Command(Theme.Glyphs.LayerStack, "모두 같은 축척", Run(MatchEntourageScale), "문서의 모든 점경을 이 점경의 축척으로 다시 그립니다. 실제 높이는 그대로입니다.", "문서의 모든 점경을 같은 축척으로")
        };
        properties.Children.Add(QuickActions.Grid(2, commands, QuickActions.CommandWidth));
    }

    internal void SetEntourageLineColor(Color color)
    {
        uint argb = VectorShapes.Argb(color) | 0xFF000000;
        entourageLine = argb;
        EditEntourage("점경 선 색", s => s with { LineArgb = argb });
    }

    // Mirrors each item about its own anchor, so it stays where it stands.
    internal void FlipEntourage()
    {
        var ids = EntourageTargets().Select(l => l.Id).ToArray();
        if (ids.Length == 0) { status.Text = "점경 레이어를 선택하세요."; return; }
        Edit("점경 좌우 뒤집기", () =>
        {
            foreach (var id in ids)
            {
                var layer = doc.Layers.Single(l => l.Id == id); var anchor = EntourageRenderer.Anchor(layer);
                layer.FlipX = !layer.FlipX;
                var moved = EntourageRenderer.Anchor(layer); layer.X += anchor.X - moved.X; layer.Y += anchor.Y - moved.Y;
            }
        });
    }

    // The scale a placed item shows at now (its handle scaling included) becomes the panel's scale.
    internal void UseEntourageAsScale()
    {
        if (EntourageTargets().FirstOrDefault() is not { Entourage: { } spec } layer) { status.Text = "점경 레이어를 선택하세요."; return; }
        double effective = spec.PixelsPerMeter * (spec.IsCustom ? layer.Scale * layer.ScaleY / CustomScaleOf(spec) : layer.Scale * layer.ScaleY);
        SetEntourageScale(effective);
        status.Text = $"점경 축척: 1 m = {EntourageScale:0.##} px · 새로 놓는 점경에 씁니다.";
    }

    static double CustomScaleOf(EntourageSpec spec) => Math.Clamp(spec.SizePixels / (spec.View == EntourageView.Plan ? Math.Max(spec.Source!.Width, spec.Source.Height) : spec.Source!.Height), .01, 20);

    // Every item of the document at the selected item's (effective) scale, sizes set by hand dropped.
    internal void MatchEntourageScale()
    {
        UseEntourageAsScale();
        double ppm = EntourageScale;
        var ids = doc.Layers.Where(l => l.Entourage != null && !IsLockedWithParents(l)).Select(l => l.Id).ToArray();
        Edit("점경 축척 맞추기", () =>
        {
            foreach (var id in ids)
            {
                var layer = doc.Layers.Single(l => l.Id == id);
                if (!layer.Entourage!.IsCustom && EntourageLibrary.Find(layer.Entourage.ItemId) == null) continue;
                var next = layer.Entourage with { PixelsPerMeter = ppm };
                if (next.SizePixels > EntourageSpec.MaxSide) next = next with { Meters = Math.Round(EntourageSpec.MaxSide * .98 / ppm, 2) };
                EntourageRenderer.Update(layer, next, true);
            }
        });
        status.Text = $"점경 {ids.Length}개를 같은 축척(1 m = {ppm:0.##} px)으로 맞췄습니다.";
    }

    // A shadow cast by entourage falls the way it stands: laid on the ground in elevation, or as a plan
    // shadow of the item's real height. The last settings' sun direction is kept.
    ShadowSpec? EntourageShadowSpec(Guid[] sources, Layer first)
    {
        var children = doc.Layers.ToLookup(l => l.ParentId);
        IEnumerable<Layer> Placed(Layer layer) => layer.Entourage != null ? [layer] : layer.Kind == LayerKind.Group ? children[layer.Id].SelectMany(Placed) : [];
        var placed = sources.Select(id => doc.Layers.FirstOrDefault(l => l.Id == id)).OfType<Layer>().SelectMany(Placed).ToArray();
        if (placed.Length == 0 || placed.Select(l => l.Entourage!.View).Distinct().Count() != 1) return null;
        var view = placed[0].Entourage!.View;
        var projection = view == EntourageView.Plan ? ShadowProjection.Plan : ShadowProjection.Ground;
        var spec = lastShadowSpec is { } last && last.Projection == projection ? last : ShadowSpec.Default(projection);
        if (view == EntourageView.Plan)
        {
            double Height(Layer l)
            {
                var e = l.Entourage!; double ratio = EntourageLibrary.Find(e.ItemId)?.ShadowHeight is > 0 and var r ? r : 1.2;
                return e.SizePixels * ratio * (e.IsCustom ? 1 : l.Scale * l.ScaleY);
            }
            spec = spec with { Height = Math.Clamp(Math.Round(placed.Average(Height), 1), 1, ShadowSpec.MaxLength) };
        }
        return spec with { Sources = sources };
    }

    // ---- Menu and showing the library ---------------------------------------------------------

    void AddEntourageMenuItems(MenuItem layerMenu)
    {
        layerMenu.Items.Add(new Separator());
        foreach (var (label, action) in new (string, Action)[] { ("점경 라이브러리…", ShowEntourageLibrary), ("내 점경 추가…", ChooseCustomEntourageImage) })
        {
            var item = DocumentControl(new MenuItem { Header = label, Foreground = Theme.Text });
            item.Click += (_, _) => { if (HasDocument) Guard(action); };
            layerMenu.Items.Add(item);
        }
    }

    // The dock tab in 간결한 화면; the 디자인 tab's 점경 section in 친절한 화면 (switching to design work).
    internal void ShowEntourageLibrary()
    {
        CommitFocusedInspectorField();
        if (screenCompact) { ShowDockTab("entourage"); return; }
        if (!designWorkspace) SetWorkspaceMode(true);
        ShowStudioPage(0);
        var header = studioContents[0].Children.OfType<SectionHeader>().FirstOrDefault(h => h.Key == "점경");
        if (header == null) return;
        if (header.Folded) header.IsChecked = false;
        if (!headlessTesting) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => header.BringIntoView()));
    }
}
