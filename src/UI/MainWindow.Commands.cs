using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    const int MaxRecentCommands = 8;
    const string PaletteCommandId = "menu:보기/명령 찾기…";
    Menu? mainMenu;
    readonly Dictionary<Tool, (string Name, string Key)> toolShortcuts = [];
    readonly List<string> recentCommands = [];
    internal CommandPalette? lastPalette;

    // One list for every action a user can reach by pointer: menu leaves (with their
    // gesture text), tools and the studio tabs. Built on demand, so titles and
    // availability follow the current workspace mode and document state.
    internal IReadOnlyList<EditorCommand> BuildCommandRegistry()
    {
        var commands = new List<EditorCommand>();
        if (mainMenu != null)
            foreach (var top in mainMenu.Items.OfType<MenuItem>()) AddMenuCommands(commands, top, []);
        var shortcuts = commands.Where(c => c.Shortcut.Length > 0).Select(c => c.Shortcut).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, (name, key)) in toolShortcuts)
        {
            string title = ToolDisplayName(tool), alias = name.Split(" / ")[0];
            // A tool already reachable from a menu (G: 버킷 채우기) keeps the menu entry only.
            if (shortcuts.Contains(key)) continue;
            string toolTitle = $"{title} 도구", toolCategory = alias == title ? "도구" : "도구 · " + alias;
            commands.Add(new EditorCommand("tool:" + tool, Loc.T(toolTitle), Loc.T(toolCategory), key, () => HasDocument, () => SetTool(tool), toolTitle, toolCategory));
        }
        const string savePreset = "현재 브러시를 프리셋으로 저장…";
        commands.Add(new EditorCommand("brush:save-preset", Loc.T(savePreset), Loc.T("브러시"), "", () => true, () => SaveBrushPresetWithDialog(), savePreset, "브러시"));
        for (int page = 0; page < studioTabs.Count; page++)
        {
            int target = page; string caption = studioPanes.Length > page ? studioPanes[page].Caption : studioTabs[page].Content?.ToString() ?? "";
            string panelTitle = $"{caption} 패널 열기";
            commands.Add(new EditorCommand("panel:page" + page, Loc.T(panelTitle), Loc.T("패널"), "", () => true, () => ShowStudioPage(target), panelTitle, "패널"));
        }
        return commands;
    }

    static void AddMenuCommands(List<EditorCommand> commands, MenuItem item, string[] path)
    {
        string header = item.Header?.ToString() ?? "";
        string[] here = [.. path, header];
        var children = item.Items.OfType<MenuItem>().ToArray();
        if (children.Length > 0) { foreach (var child in children) AddMenuCommands(commands, child, here); return; }
        string id = "menu:" + string.Join("/", here);
        if (header.Length == 0 || id == PaletteCommandId) return;
        commands.Add(new EditorCommand(id, Loc.T(header), string.Join(" › ", path.Select(Loc.T)), item.InputGestureText ?? "", () => item.IsEnabled, () => InvokeMenuItem(item),
            header, string.Join(" › ", path)));
    }

    // Same effect as clicking: checkable items flip first, then the Click handlers run.
    static void InvokeMenuItem(MenuItem item)
    {
        if (item.IsCheckable) item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    void ShowCommandPalette()
    {
        CancelGesture();
        var palette = lastPalette = new CommandPalette(this, BuildCommandRegistry(), recentCommands.ToArray());
        if (headlessTesting) return;
        palette.Closed += (_, _) =>
        {
            if (lastPalette == palette) lastPalette = null;
            if (palette.Chosen is { } chosen) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RunCommand(chosen)));
            else if (HasDocument) canvas.Focus();
        };
        palette.Show();
    }

    internal void RunCommand(EditorCommand command)
    {
        if (!command.IsAvailable()) return;
        recentCommands.Remove(command.Id); recentCommands.Insert(0, command.Id);
        if (recentCommands.Count > MaxRecentCommands) recentCommands.RemoveRange(MaxRecentCommands, recentCommands.Count - MaxRecentCommands);
        Guard(command.Execute);
    }
}
