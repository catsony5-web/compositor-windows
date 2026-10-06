using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// 내 프리셋 in the brush panel: the user's saved brushes, each a stroke sample of its own settings.
// One click applies shape, size, hardness, opacity, rotation and spacing; it changes no document
// and adds no history. The list lives in %LOCALAPPDATA%\Morupixel\brush-presets.json
// (BrushPresetStore), read when the window is loaded; headless checks and offscreen previews keep
// it in memory unless a test path is given.
public sealed partial class MainWindow
{
    internal string? brushPresetStore;
    // Tests run the save dialog's steps without showing a window.
    internal Func<BrushPresetDialog, bool>? brushPresetDialogRunner;
    internal readonly List<BrushPreset> brushPresets = [];
    internal readonly List<(BrushPreset Preset, Button Button)> brushPresetRows = [];
    StackPanel? brushPresetList;
    string? BrushPresetPath => brushPresetStore ?? (headlessTesting ? null : BrushPresetStore.DefaultStorePath);

    internal void LoadBrushPresets()
    {
        brushPresets.Clear();
        try { if (BrushPresetPath is { } path) brushPresets.AddRange(BrushPresetStore.Load(path)); }
        finally { RebuildBrushPresetList(); }
    }

    // Changes the list and writes it; when the file cannot be written the list returns to its previous state.
    void ChangeBrushPresets(Action change)
    {
        var before = brushPresets.ToList();
        change();
        try { if (BrushPresetPath is { } path) BrushPresetStore.Save(brushPresets, path); }
        catch
        {
            brushPresets.Clear(); brushPresets.AddRange(before); RebuildBrushPresetList();
            throw;
        }
        RebuildBrushPresetList();
    }

    /// <summary>The panel's current brush settings as an unsaved preset.</summary>
    internal BrushPreset CurrentBrushSettings(string name = "", Guid? id = null) =>
        BrushPreset.Normalize(new BrushPreset(id ?? Guid.NewGuid(), name, brushTip.Id, brushSize, hardness, brushOpacity, brushSpacing, brushAngle))!;

    internal bool IsBrushPresetActive(BrushPreset preset) => preset.SameSettings(CurrentBrushSettings());

    BrushTip? FindBrushTip(string id) => BrushTip.BuiltIns.FirstOrDefault(t => t.Id == id) ?? customBrushTips.FirstOrDefault(t => t.Id == id);

    // 내 브러시 1, 2, …: the first number no preset uses yet.
    internal string NextBrushPresetName()
    {
        for (int number = 1; ; number++)
        {
            string name = Loc.Format("내 브러시 {0}", number);
            if (brushPresets.All(p => !string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }

    void EnsureBrushPresetRoom()
    {
        if (brushPresets.Count >= BrushPresetStore.MaxPresets)
            throw new InvalidDataException($"내 프리셋은 {BrushPresetStore.MaxPresets}개까지 보관합니다. 쓰지 않는 프리셋을 삭제하세요.");
    }

    // 현재 브러시를 프리셋으로 저장: names the current settings in a small dialog and adds them to the end of 내 프리셋.
    internal BrushPreset? SaveBrushPresetWithDialog()
    {
        EnsureBrushPresetRoom();
        var current = CurrentBrushSettings();
        var dialog = new BrushPresetDialog(headlessTesting ? null : this, FindBrushTip(current.TipId) ?? BrushTip.Round, current, NextBrushPresetName());
        bool accepted;
        if (brushPresetDialogRunner != null) accepted = brushPresetDialogRunner(dialog);
        else if (headlessTesting) { dialog.Close(); return null; }
        else accepted = dialog.ShowDialog() == true;
        if (!accepted || dialog.PresetName is not { } name) return null;
        return AddBrushPreset(name);
    }

    internal BrushPreset AddBrushPreset(string name)
    {
        EnsureBrushPresetRoom();
        var preset = CurrentBrushSettings(BrushPreset.CleanName(name, NextBrushPresetName()));
        ChangeBrushPresets(() => brushPresets.Add(preset));
        status.Text = $"내 프리셋에 저장했습니다: {preset.Name}";
        return preset;
    }

    // Sets every saved setting at once. The tool stays when it already paints with these settings
    // (brush, eraser, retouch brushes); otherwise the brush is chosen. No document history is added.
    internal void ApplyBrushPreset(BrushPreset preset)
    {
        var tip = FindBrushTip(preset.TipId);
        if (!BrushTool) SetTool(Tool.Brush);
        brushTip = tip ?? BrushTip.Round;
        brushSize = preset.Size; hardness = preset.Hardness; brushOpacity = preset.Opacity; brushSpacing = preset.Spacing; brushAngle = preset.Angle;
        SyncBrushControls();
        status.Text = tip == null ? $"저장한 이미지 브러시 모양을 찾지 못해 원형으로 적용했습니다: {preset.Name}" : $"브러시 프리셋을 적용했습니다: {preset.Name}";
    }

    // Moves every brush control to the current values without feeding rounded values back.
    void SyncBrushControls()
    {
        UpdateBrushLabel();
        studioHardness?.SetValue(hardness * 100);
        if (opacitySlider != null && Math.Abs(opacitySlider.Value - brushOpacity) > 1e-9) opacitySlider.Value = brushOpacity;
        studioAngle?.SetValue(brushAngle);
        studioSpacing?.SetValue(brushSpacing * 100);
        UpdateBrushTipControls(); UpdateBrushTipCursor();
    }

    internal void RenameBrushPreset(Guid id, string name)
    {
        int index = brushPresets.FindIndex(p => p.Id == id);
        if (index < 0) return;
        string clean = BrushPreset.CleanName(name, brushPresets[index].Name);
        ChangeBrushPresets(() => brushPresets[index] = brushPresets[index] with { Name = clean });
        status.Text = $"프리셋 이름을 바꿨습니다: {clean}";
    }

    // 현재 설정으로 덮어쓰기: keeps the name and place, replaces every setting.
    internal void OverwriteBrushPreset(Guid id)
    {
        int index = brushPresets.FindIndex(p => p.Id == id);
        if (index < 0) return;
        var updated = CurrentBrushSettings(brushPresets[index].Name, id);
        ChangeBrushPresets(() => brushPresets[index] = updated);
        status.Text = $"현재 설정으로 덮어썼습니다: {updated.Name}";
    }

    internal void DeleteBrushPreset(Guid id)
    {
        var removed = brushPresets.FirstOrDefault(p => p.Id == id);
        if (removed == null) return;
        ChangeBrushPresets(() => brushPresets.Remove(removed));
        status.Text = $"내 프리셋에서 삭제했습니다: {removed.Name}";
    }

    internal void MoveBrushPreset(Guid id, int offset)
    {
        int index = brushPresets.FindIndex(p => p.Id == id), target = index + offset;
        if (index < 0 || target < 0 || target >= brushPresets.Count) return;
        ChangeBrushPresets(() => { var preset = brushPresets[index]; brushPresets.RemoveAt(index); brushPresets.Insert(target, preset); });
    }

    // ---- Panel ---------------------------------------------------------------------------------

    void BuildBrushPresetSection(Panel host)
    {
        host.Children.Add(Theme.Section("내 프리셋"));
        brushPresetList = new StackPanel();
        host.Children.Add(brushPresetList);
        host.Children.Add(Theme.ActionRow("현재 브러시를 프리셋으로 저장…", () => Guard(() => SaveBrushPresetWithDialog()),
            "모양, 크기, 경도, 농도, 모양 회전, 찍는 간격을 이름을 붙여 저장합니다.", Theme.Glyphs.BrushPreset));
        RebuildBrushPresetList();
    }

    internal void RebuildBrushPresetList()
    {
        brushPresetRows.Clear();
        if (brushPresetList == null) return;
        brushPresetList.Children.Clear();
        if (brushPresets.Count == 0)
        {
            var empty = Theme.Label("자주 쓰는 브러시 설정을 저장하면 여기에 표시됩니다. 누르면 한 번에 적용됩니다.", Theme.CaptionSize, Theme.Subtle);
            empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(2, 0, 2, 6);
            brushPresetList.Children.Add(empty);
            return;
        }
        for (int i = 0; i < brushPresets.Count; i++)
        {
            var button = BrushPresetRow(brushPresets[i], i);
            brushPresetRows.Add((brushPresets[i], button)); brushPresetList.Children.Add(button);
        }
        UpdateBrushPresetMarks();
    }

    Button BrushPresetRow(BrushPreset preset, int index)
    {
        var tip = FindBrushTip(preset.TipId);
        // The name spans the row (user names can be long); the settings sit beside the stroke sample below it.
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Loc.Keep(Theme.Label(preset.Name, Theme.BodySize)); title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.Wrap; title.Margin = new Thickness(2, 0, 2, 2);
        Grid.SetColumnSpan(title, 2); row.Children.Add(title);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        text.Children.Add(new KeepWordsTextBlock
        {
            Text = $"{preset.Size:0}px · 경도 {preset.Hardness * 100:0}% · 농도 {preset.Opacity * 100:0}%", FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap
        });
        if (tip == null)
            text.Children.Add(new KeepWordsTextBlock { Text = "이미지 모양 없음 · 원형으로 적용", FontSize = Theme.CaptionSize, Foreground = Theme.Warning, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(text, 1); row.Children.Add(text);
        var sample = new Image
        {
            Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, Opacity = Math.Max(.25, preset.Opacity),
            Source = BrushPreview.Stroke(tip ?? BrushTip.Round, preset.Size, preset.Hardness, preset.Spacing, preset.Angle, 200, 40).Bitmap()
        };
        Grid.SetRow(sample, 1);
        Grid.SetColumn(sample, 1); row.Children.Add(sample);
        var button = Theme.Button("", () => Guard(() => ApplyBrushPreset(preset)), "클릭: 이 브러시 설정 적용 · 마우스 오른쪽 단추: 이름 바꾸기, 덮어쓰기, 삭제");
        button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(8, 6, 8, 6); button.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetName(button, preset.Name);
        button.ContextMenu = BrushPresetMenu(preset, index);
        return button;
    }

    ContextMenu BrushPresetMenu(BrushPreset preset, int index)
    {
        var menu = new ContextMenu();
        void Item(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => Guard(run);
            menu.Items.Add(item);
        }
        Item("적용", () => ApplyBrushPreset(preset));
        Item("이름 바꾸기…", () =>
        {
            var fields = Dialogs.Fields(this, "프리셋 이름 바꾸기", ("프리셋 이름", preset.Name));
            if (fields != null) RenameBrushPreset(preset.Id, fields[0]);
        });
        Item("현재 설정으로 덮어쓰기", () => OverwriteBrushPreset(preset.Id));
        menu.Items.Add(new Separator());
        Item("위로 이동", () => MoveBrushPreset(preset.Id, -1), index > 0);
        Item("아래로 이동", () => MoveBrushPreset(preset.Id, 1), index < brushPresets.Count - 1);
        menu.Items.Add(new Separator());
        Item("내 프리셋에서 삭제", () => DeleteBrushPreset(preset.Id));
        return menu;
    }

    // The preset whose settings equal the current brush is marked like the selected brush shape.
    void UpdateBrushPresetMarks()
    {
        if (brushPresetRows.Count == 0) return;
        var current = CurrentBrushSettings();
        foreach (var (preset, button) in brushPresetRows)
        {
            bool active = preset.SameSettings(current);
            button.Background = active ? Theme.Selected : Theme.Surface;
            button.BorderBrush = active ? Theme.Accent : Theme.Line;
        }
    }
}
