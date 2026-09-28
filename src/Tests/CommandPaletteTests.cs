using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunCommandPaletteTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        test("command palette ranks titles, Korean initials, categories and shortcuts", () =>
        {
            static EditorCommand Command(string id, string title, string category, string shortcut = "") => new(id, title, category, shortcut, () => true, () => { });
            EditorCommand[] commands =
            [
                Command("duplicate", "레이어 복제", "레이어", "Ctrl+J"), Command("delete", "레이어 삭제", "레이어"),
                Command("stamp", "복제 도장 도구", "도구", "S"), Command("blur", "가우시안 흐림…", "보정"), Command("fit", "화면에 맞춤", "보기", "Ctrl+0")
            ];
            string[] Ids(string query, params string[] recent) => CommandPalette.Filter(commands, query, recent).Select(c => c.Id).ToArray();
            Check(Ids("복제").SequenceEqual(["stamp", "duplicate"]), "A title prefix did not outrank a title match");
            Check(Ids("레이어 복제")[0] == "duplicate" && Ids("레이어복제")[0] == "duplicate", "Spacing changed the match");
            Check(Ids("ㄹㅇㅇㅂㅈ").SequenceEqual(["duplicate"]) && Ids("ㅎㄹ").SequenceEqual(["blur"]), "Initial consonants did not match");
            Check(Ids("보정가우")[0] == "blur", "Category and title were not searched together");
            Check(Ids("ctrl+j")[0] == "duplicate" && Ids("CTRL + 0")[0] == "fit", "Shortcut text did not match");
            Check(Ids("존재하지않는명령").Length == 0, "An unrelated query returned results");
            Check(Ids("", "fit", "blur").Take(3).SequenceEqual(["fit", "blur", "duplicate"]), "Recent commands were not listed first");
            Check(Ids("레이어", "delete")[0] == "delete", "A recent command did not win a tie");
            Check(CommandPalette.Initials("가우시안") == "ㄱㅇㅅㅇ" && CommandPalette.Normalize("Ctrl + K…") == "ctrl+k", "Search helpers changed");
        });

        test("command registry covers menus, tools and panels with unique shortcuts", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var empty = window.BuildCommandRegistry();
                Check(empty.Select(c => c.Id).Distinct().Count() == empty.Count, "Command ids repeat");
                var keys = empty.Where(c => c.Shortcut.Length > 0).GroupBy(c => c.Shortcut, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                Check(keys.Length == 0, "Shortcuts listed twice: " + string.Join(", ", keys));
                Check(empty.All(c => c.Id != PaletteCommandId), "The palette listed itself");
                Check(empty.Single(c => c.Id == "menu:레이어/레이어 복제").Shortcut == "Ctrl+J" && empty.Single(c => c.Id == "tool:Brush").Shortcut == "B", "Menu or tool shortcuts are missing");
                Check(empty.All(c => c.Id != "tool:Bucket") && empty.Any(c => c.Id == "menu:편집/버킷 채우기 도구"), "A tool with a menu entry was listed twice");
                Check(empty.Count(c => c.Category == "패널") == 4 && empty.Any(c => c.Id == "menu:레이어/새 조정 레이어/곡선…"), "Panels or nested menus are missing");
                Check(!empty.Single(c => c.Id == "menu:레이어/레이어 복제").IsAvailable() && empty.Single(c => c.Id == "menu:파일/새 캔버스…").IsAvailable(), "Availability ignored the empty workspace");
                window.RunCommand(empty.Single(c => c.Id == "menu:레이어/레이어 복제"));
                Check(window.recentCommands.Count == 0, "An unavailable command was recorded");

                window.AddTab(NewDocumentDialog.CreateDocument("명령", "16", "16", 0), null);
                var commands = window.BuildCommandRegistry();
                Check(commands.Single(c => c.Id == "menu:레이어/레이어 복제").IsAvailable(), "Document commands stayed unavailable");
                window.RunCommand(commands.Single(c => c.Id == "tool:Brush"));
                bool grid = window.canvas.PixelGrid;
                window.RunCommand(commands.Single(c => c.Id == "menu:보기/픽셀 격자 전환"));
                window.RunCommand(commands.Single(c => c.Id == "panel:page2"));
                Check(window.tool == Tool.Brush && window.canvas.PixelGrid != grid && window.studioPage == 2, "Commands did not run their actions");
                Check(window.recentCommands.SequenceEqual(["panel:page2", "menu:보기/픽셀 격자 전환", "tool:Brush"]), "Recent commands are out of order");
                window.SetWorkspaceMode(true);
                Check(window.BuildCommandRegistry().Single(c => c.Id == "panel:page0").Title == "디자인 패널 열기", "Panel titles did not follow the workspace mode");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("ctrl+k opens the command palette with or without a document", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                Check(window.ExecuteEditorShortcut(Key.K, ModifierKeys.Control) && window.lastPalette != null, "Ctrl+K did nothing in the empty workspace");
                var palette = window.lastPalette!;
                palette.SetQuery("ㅂㄹㅅ");
                var brush = palette.Shown.First(c => c.Id == "tool:Brush");
                Check(!palette.Choose(brush) && palette.Chosen == null, "An unavailable command was chosen");

                window.AddTab(NewDocumentDialog.CreateDocument("팔레트", "16", "16", 0), null);
                Check(window.ExecuteEditorShortcut(Key.K, ModifierKeys.Control) && window.lastPalette != palette, "Ctrl+K did nothing with a document");
                palette = window.lastPalette!;
                palette.SetQuery("브러시");
                Check(palette.Shown[0].Id == "tool:Brush" && palette.Choose(palette.Shown[0]), "The palette did not choose the top match");
                window.RunCommand(palette.Chosen!);
                Check(window.tool == Tool.Brush, "The chosen command did not run");
                window.ExecuteEditorShortcut(Key.K, ModifierKeys.None);
                Check(window.tool == Tool.BlurBrush && window.lastPalette == palette, "K alone no longer selects the blur brush");
            }
            finally { window.StopRenderingForShutdown(); }
        });
    }
}
