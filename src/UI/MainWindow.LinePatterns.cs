using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

// Hatch pattern library in the material palette: starred patterns first (즐겨찾기), the built-in line
// patterns, the tone screens (스크린톤), then the user's own line patterns (내 패턴) made from an image. The library lives in
// %LOCALAPPDATA%\Morupixel\Patterns (LinePatternStore); favorites are saved with the workspace
// layout. Headless checks and offscreen previews keep both in memory unless a test path is given.
public sealed partial class MainWindow
{
    // Starred patterns in the order they were starred (LinePatterns.FavoriteKey).
    internal List<string> patternFavorites = [];
    internal string? linePatternStore;
    // Tests run the conversion dialog's steps without showing a window.
    internal Func<LinePatternDialog, bool>? linePatternDialogRunner;
    List<MaterialAsset>? linePatternLibrary;
    string? LinePatternDirectory => linePatternStore ?? (headlessTesting ? null : LinePatternStore.DefaultDirectory);

    // The user's line patterns, read once on first use.
    internal IReadOnlyList<MaterialAsset> LinePatternLibrary()
    {
        if (linePatternLibrary != null) return linePatternLibrary;
        linePatternLibrary = [];
        if (LinePatternDirectory is { } directory) linePatternLibrary.AddRange(LinePatternStore.Load(directory));
        return linePatternLibrary;
    }

    List<MaterialAsset> Library() { LinePatternLibrary(); return linePatternLibrary!; }

    void SaveLinePatternLibrary()
    {
        if (LinePatternDirectory is { } directory && linePatternLibrary != null) LinePatternStore.Save(linePatternLibrary, directory);
    }

    // 내 패턴: the library, then line patterns this document carries that the library does not have
    // (a document from another PC still offers its patterns).
    internal IReadOnlyList<MaterialAsset> CustomPatternChoices()
    {
        var result = LinePatternLibrary().ToList();
        if (!HasDocument) return result;
        try { result.AddRange(MaterialEditing.Assets(doc).Where(a => LinePatterns.IsCustom(a) && result.All(p => p.Id != a.Id))); }
        catch (InvalidDataException) { }
        return result;
    }

    internal bool IsFavoritePattern(MaterialAsset asset) => patternFavorites.Contains(LinePatterns.FavoriteKey(asset));

    internal void ToggleFavoritePattern(MaterialAsset asset, string displayName)
    {
        string key = LinePatterns.FavoriteKey(asset);
        if (patternFavorites.Remove(key)) status.Text = $"즐겨찾기에서 뺐습니다: {displayName}";
        else
        {
            if (patternFavorites.Count >= WorkspaceLayoutStore.MaxPatternFavorites) { status.Text = $"즐겨찾기는 {WorkspaceLayoutStore.MaxPatternFavorites}개까지 보관합니다."; return; }
            patternFavorites.Add(key); status.Text = $"즐겨찾기에 추가했습니다: {displayName}";
        }
        SaveWorkspace();
    }

    // Registers a converted pattern in 내 패턴 and returns the stored entry (an existing ID is reused).
    internal MaterialAsset AddLinePattern(MaterialAsset asset)
    {
        if (!LinePatterns.IsCustom(asset)) throw new InvalidDataException("선 패턴이 아닙니다.");
        var library = Library();
        if (library.FirstOrDefault(p => p.Id == asset.Id) is { } known) return known;
        if (library.Count >= LinePatternStore.MaxPatterns) throw new InvalidDataException($"내 패턴은 {LinePatternStore.MaxPatterns}개까지 보관합니다. 쓰지 않는 패턴을 삭제하세요.");
        library.Add(asset);
        try { SaveLinePatternLibrary(); }
        catch { library.Remove(asset); throw; }
        return asset;
    }

    internal void RenameLinePattern(Guid id, string name)
    {
        var library = Library();
        int index = library.FindIndex(p => p.Id == id);
        if (index < 0) return;
        var before = library[index];
        library[index] = before with { Name = LinePatternStore.CleanName(name) };
        try { SaveLinePatternLibrary(); }
        catch { library[index] = before; throw; }
        status.Text = $"패턴 이름을 바꿨습니다: {library[index].Name}";
    }

    // Removes a pattern from 내 패턴. Documents that use it keep their own copy.
    internal void RemoveLinePattern(Guid id)
    {
        var library = Library();
        var removed = library.FirstOrDefault(p => p.Id == id);
        if (removed == null) return;
        int index = library.IndexOf(removed);
        library.Remove(removed);
        try { SaveLinePatternLibrary(); }
        catch { library.Insert(index, removed); throw; }
        if (patternFavorites.Remove(LinePatterns.FavoriteKey(removed))) SaveWorkspace();
        status.Text = $"내 패턴에서 삭제했습니다: {removed.Name} · 이 패턴을 쓴 문서는 그대로 남습니다.";
    }

    // Picks an image, converts it in the pattern dialog, registers it and fills with it.
    void ChooseLinePatternImage(Action<MaterialAsset, string> apply)
    {
        var open = new OpenFileDialog { Title = "선 패턴으로 바꿀 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        var source = LinePatternSource.Load(open.FileName);
        if (CreateLinePattern(source, Path.GetFileNameWithoutExtension(open.FileName)) is { } asset) apply(asset, asset.Name);
    }

    internal MaterialAsset? CreateLinePattern(LinePatternSource source, string name)
    {
        var dialog = new LinePatternDialog(headlessTesting ? null : this, source, name);
        bool accepted = linePatternDialogRunner?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!accepted || dialog.Result is not { } created) return null;
        var stored = AddLinePattern(created);
        status.Text = $"내 패턴에 추가했습니다: {stored.Name}";
        return stored;
    }

    // ---- Palette ------------------------------------------------------------------------------

    // Groups: 즐겨찾기, 기본 패턴 (line hatches), 스크린톤 (tone screens, black poché, gradients), 내 패턴.
    void AddPatternTab(StackPanel body, IReadOnlyList<HatchPattern> order, Guid? currentAssetId, bool swapping, Action<MaterialAsset, string> apply, Action refresh)
    {
        var builtIn = order.Select(PatternChoice).ToList();
        var custom = CustomPatternChoices().Select(a => new SelectionMaterialChoice(a, a.Name, false)).ToList();
        var all = builtIn.Concat(custom).ToList();
        var favorites = patternFavorites.Select(key => all.FirstOrDefault(c => LinePatterns.FavoriteKey(c.Asset) == key)).OfType<SelectionMaterialChoice>().ToList();
        var starred = favorites.Select(c => c.Asset.Id).ToHashSet();
        bool Screentone(SelectionMaterialChoice choice) => HatchPatterns.TryGet(choice.Asset, out var p) && HatchPatterns.IsScreentone(p);
        bool grouped = favorites.Count > 0 || custom.Count > 0 || builtIn.Any(Screentone);
        void Group(string title, string glyph, IEnumerable<SelectionMaterialChoice> choices)
        {
            var shown = choices.ToList();
            if (shown.Count == 0) return;
            if (grouped) body.Children.Add(PatternGroupHeader(title, glyph));
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(-1, 0, -1, 4) };
            foreach (var choice in shown) grid.Children.Add(PatternTile(choice, choice.Asset.Id == currentAssetId, swapping, apply, refresh));
            // Below 76 DIP per column the grid drops columns, like QuickActions.Grid.
            grid.SizeChanged += (_, e) => { if (e.WidthChanged) grid.Columns = Math.Clamp((int)(e.NewSize.Width / 76), 1, 4); };
            body.Children.Add(grid);
        }
        Group("즐겨찾기", Theme.Glyphs.StarFilled, favorites);
        Group("기본 패턴", Theme.Glyphs.Hatch, builtIn.Where(c => !starred.Contains(c.Asset.Id) && !Screentone(c)));
        Group("스크린톤", Theme.Glyphs.Screentone, builtIn.Where(c => !starred.Contains(c.Asset.Id) && Screentone(c)));
        Group("내 패턴", Theme.Glyphs.Image, custom.Where(c => !starred.Contains(c.Asset.Id)));
        var note = Density.Mark(Theme.Label("도면용 선 패턴입니다. 바탕이 투명해 아래 색과 선이 그대로 보입니다.", Theme.CaptionSize, Theme.Subtle), DensityRole.Description); note.Margin = new Thickness(2, 0, 2, 4);
        body.Children.Add(note);
        body.Children.Add(Theme.ActionRow("이미지로 패턴 추가…", () => Guard(() => { CommitFocusedInspectorField(); ChooseLinePatternImage(apply); }),
            "스캔하거나 그린 해치 이미지의 어두운 선을 잉크로 바꿔 내 패턴에 등록하고 바로 채웁니다.", Theme.Glyphs.Plus));
    }

    static FrameworkElement PatternGroupHeader(string title, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 2, 2, 4) };
        var icon = Theme.Glyph(glyph, 14, glyph == Theme.Glyphs.StarFilled ? Theme.Warning : Theme.Subtle); icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var label = Theme.Label(title, Theme.CaptionSize, Theme.Muted); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }

    // A pattern swatch with a star in its corner: always shown on favorites, on hover or focus otherwise.
    FrameworkElement PatternTile(SelectionMaterialChoice choice, bool current, bool swapping, Action<MaterialAsset, string> apply, Action refresh)
    {
        var tile = MaterialTile(choice, current, swapping, apply);
        bool favorite = IsFavoritePattern(choice.Asset);
        string tip = favorite ? "즐겨찾기에서 빼기" : "즐겨찾기에 추가";
        var star = Theme.IconButton(favorite ? Theme.Glyphs.StarFilled : Theme.Glyphs.Star, () => Guard(() => { ToggleFavoritePattern(choice.Asset, choice.Name); refresh(); }), tip, 22, 14);
        star.Content = Theme.Glyph(favorite ? Theme.Glyphs.StarFilled : Theme.Glyphs.Star, 14, favorite ? Theme.Warning : Theme.Muted);
        star.Background = Theme.Header; star.BorderBrush = Theme.Stroke; star.BorderThickness = new Thickness(1);
        star.HorizontalAlignment = HorizontalAlignment.Right; star.VerticalAlignment = VerticalAlignment.Top; star.Margin = new Thickness(0, 9, 9, 0);
        star.Tag = "favorite";
        var host = new Grid(); host.Children.Add(tile); host.Children.Add(star);
        if (!favorite)
        {
            star.Opacity = 0;
            void Reveal() => star.Opacity = host.IsMouseOver || host.IsKeyboardFocusWithin ? 1 : 0;
            host.MouseEnter += (_, _) => Reveal(); host.MouseLeave += (_, _) => Reveal();
            host.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        }
        tile.ContextMenu = PatternMenu(choice, refresh);
        return host;
    }

    ContextMenu PatternMenu(SelectionMaterialChoice choice, Action refresh)
    {
        var menu = new ContextMenu();
        void Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Guard(() => { run(); refresh(); });
            menu.Items.Add(item);
        }
        Item(IsFavoritePattern(choice.Asset) ? "즐겨찾기에서 빼기" : "즐겨찾기에 추가", () => ToggleFavoritePattern(choice.Asset, choice.Name));
        if (LinePatterns.IsCustom(choice.Asset))
        {
            if (LinePatternLibrary().Any(p => p.Id == choice.Asset.Id))
            {
                Item("이름 바꾸기…", () =>
                {
                    var fields = Dialogs.Fields(this, "패턴 이름 바꾸기", ("패턴 이름", choice.Asset.Name));
                    if (fields != null) RenameLinePattern(choice.Asset.Id, fields[0]);
                });
                Item("내 패턴에서 삭제", () => RemoveLinePattern(choice.Asset.Id));
            }
            else Item("내 패턴에 저장", () => { AddLinePattern(choice.Asset); status.Text = $"내 패턴에 추가했습니다: {choice.Asset.Name}"; });
        }
        return menu;
    }

    // ---- Properties ---------------------------------------------------------------------------

    // 바탕색: an optional color under the pattern's lines, inside the region. 없음 keeps it transparent.
    void AddPatternBackgroundRow(MaterialFill fill, bool locked, Func<bool> current, Func<UIElement, UIElement> control)
    {
        uint background = fill.Background >> 24 == 0 ? 0 : fill.Background;
        var row = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var clear = Theme.IconButton(Theme.Glyphs.Close, () => Guard(() => { if (current()) EditMaterial("패턴 바탕색", f => f with { Background = 0 }); }), "바탕색 없음", 26, 14);
        clear.IsEnabled = !locked && background != 0; clear.Margin = new Thickness(4, 0, 0, 0); DockPanel.SetDock(clear, Dock.Right); row.Children.Add(control(clear));
        var chip = PropertyRows.ColorChip(background == 0 ? Colors.Transparent : VectorShapes.Color(background), background == 0 ? "없음" : $"#{background & 0xFFFFFF:X6}", () =>
        {
            if (!current()) return;
            var dialog = new ColorPickerDialog(this, background == 0 ? Colors.White : VectorShapes.Color(background), "바탕색");
            if (dialog.ShowDialog() == true && current()) SetPatternBackground(dialog.SelectedColor);
        }, "바탕색 변경");
        chip.MinWidth = 120; chip.IsEnabled = !locked; DockPanel.SetDock(chip, Dock.Right); row.Children.Add(control(chip));
        var caption = Theme.Label("바탕색", Theme.BodySize, Theme.Muted); caption.Margin = new Thickness(1, 2, 8, 2);
        caption.ToolTip = "선 아래 영역을 채울 색입니다. 없음이면 바탕이 투명해 아래 색과 선이 보입니다.";
        row.Children.Add(caption);
        properties.Children.Add(row);
    }

    // A fully transparent pick means no background.
    internal void SetPatternBackground(Color color)
    {
        uint argb = color.A == 0 ? 0 : VectorShapes.Argb(color);
        EditMaterial("패턴 바탕색", f => f with { Background = argb });
    }
}
