using System.IO;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunWorkspaceLayoutTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        test("workspace layout store keeps known panes and drops invalid values", () =>
        {
            string store = Path.Combine(directory, "workspace-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                WorkspaceLayoutStore.Save(new WorkspaceLayout
                {
                    Window = new WindowBounds(double.NaN, 0, 1400, 900, false), RightPanelWidth = 440, DesignWorkspace = true, StudioPage = 9,
                    Panes = [new("page1", "left", true), new("layers", "float", false, new WindowBounds(10, 20, 400, 560, false)), new("unknown", "left"), new("page2", "top")],
                    CollapsedSections = ["외형", "", "외형"]
                }, store);
                var loaded = WorkspaceLayoutStore.Load(store)!;
                Check(loaded.Window == null && loaded.RightPanelWidth == 440 && loaded.DesignWorkspace && loaded.StudioPage == 3, "Scalar values were not sanitized");
                Check(loaded.Panes.Length == 2 && loaded.Panes[0] == new PaneLayout("page1", "left", true) && loaded.Panes[1].Floating == new WindowBounds(10, 20, 400, 560, false), "Pane entries were not filtered");
                Check(loaded.CollapsedSections.SequenceEqual(["외형"]), "Collapsed sections were not deduplicated");
                File.WriteAllText(store, "{ \"Version\": 2 }");
                Check(WorkspaceLayoutStore.Load(store) == null, "An unknown layout version was accepted");
                File.WriteAllText(store, "not json");
                Check(WorkspaceLayoutStore.Load(store) == null, "A corrupt layout was not ignored");
            }
            finally { if (File.Exists(store)) File.Delete(store); }
        });

        test("workspace layout restores docking, width, mode and tab on a new window", () =>
        {
            var source = new MainWindow(null) { headlessTesting = true };
            var target = new MainWindow(null) { headlessTesting = true };
            try
            {
                Check(!source.persistWorkspace && source.savedLayout == null, "A headless window loaded the user's layout");
                source.MovePane(source.studioPanes[1], "left"); source.studioPanes[1].SetPinned(true);
                source.rightPanelColumn.Width = new System.Windows.GridLength(450);
                source.SetWorkspaceMode(true); source.ShowStudioPage(2);
                var layout = source.CaptureLayout();
                Check(layout.RightPanelWidth == 450 && layout.DesignWorkspace && layout.StudioPage == 2 && layout.Window == null, "Captured values are incomplete");
                Check(layout.Panes.Single() == new PaneLayout("page1", "left", true), "Captured panes are incorrect");
                target.ApplyPaneLayout(layout);
                Check(target.studioPanes[1].Location == "left" && target.studioPanes[1].Pinned && target.leftPanels.Children.Contains(target.studioPanes[1]), "Pane docking was not restored");
                Check(target.rightPanelColumn.Width.Value == 450 && target.designWorkspace && target.studioPage == 2, "Width, mode or tab was not restored");
                target.ApplyPaneLayout(new WorkspaceLayout { RightPanelWidth = 9000 });
                Check(target.rightPanelColumn.Width.Value == target.rightPanelColumn.MaxWidth, "Restored width escaped the panel limits");
            }
            finally { source.StopRenderingForShutdown(); target.StopRenderingForShutdown(); }
        });
    }
}
