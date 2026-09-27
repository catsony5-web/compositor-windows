using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunPropertyPanelTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        test("section header folds rows up to the next section and restores their visibility", () =>
        {
            try
            {
                SectionHeader.SetCollapsedKeys([]);
                var panel = new StackPanel();
                var first = (SectionHeader)Theme.Section("검사 A"); var shown = new TextBlock(); var hiddenByCode = new TextBlock { Visibility = Visibility.Collapsed };
                var second = (SectionHeader)Theme.Section("검사 B"); var other = new TextBlock();
                foreach (var child in new UIElement[] { first, shown, hiddenByCode, second, other }) panel.Children.Add(child);
                first.IsChecked = true;
                Check(shown.Visibility == Visibility.Collapsed && second.Visibility == Visibility.Visible && other.Visibility == Visibility.Visible, "Folding crossed the next section");
                Check(SectionHeader.CollapsedKeys.SequenceEqual(["검사 A"]), "The folded title was not recorded");
                first.IsChecked = false;
                Check(shown.Visibility == Visibility.Visible && hiddenByCode.Visibility == Visibility.Collapsed, "Opening did not restore the previous visibility");
                SectionHeader.SetCollapsedKeys(["검사 B"]);
                Check(second.Folded && !first.Folded && other.Visibility == Visibility.Collapsed, "Restored keys did not fold live headers");
                var rebuilt = new StackPanel(); var header = (SectionHeader)Theme.Section("검사 B"); var row = new TextBlock();
                rebuilt.Children.Add(header); rebuilt.Children.Add(row); header.Apply();
                Check(header.Folded && row.Visibility == Visibility.Collapsed, "A rebuilt section did not start folded");
            }
            finally { SectionHeader.SetCollapsedKeys([]); }
        });

        test("inspector sections fold, survive a rebuild and round-trip through the workspace layout", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var target = new MainWindow(null) { headlessTesting = true };
            try
            {
                SectionHeader.SetCollapsedKeys([]);
                window.AddTab(NewDocumentDialog.CreateDocument("섹션", "16", "16", 0), null); window.BuildProperties();
                SectionHeader Header(string key) => window.properties.Children.OfType<SectionHeader>().Single(h => h.Key == key);
                var boxes = window.properties.Children.OfType<Grid>().SelectMany(grid => grid.Children.OfType<TextBox>()).ToArray();
                Check(boxes.Length == 5 && boxes.All(box => box.MinHeight == Theme.ControlHeight), "Inspector number boxes are not the shared 30 DIP rows");
                var transform = Header("위치와 변형"); int index = window.properties.Children.IndexOf(transform);
                transform.IsChecked = true;
                Check(window.properties.Children[index + 1].Visibility == Visibility.Collapsed && Header("레이어 작업").Visibility == Visibility.Visible, "The transform rows were not folded");
                var layout = window.CaptureLayout();
                Check(layout.CollapsedSections.SequenceEqual(["위치와 변형"]), "The layout did not capture folded sections");
                window.BuildProperties(); var again = Header("위치와 변형"); again.Apply();
                Check(again.Folded && window.properties.Children[window.properties.Children.IndexOf(again) + 1].Visibility == Visibility.Collapsed, "A rebuilt inspector reopened the section");
                SectionHeader.SetCollapsedKeys([]);
                Check(!again.Folded && window.properties.Children[window.properties.Children.IndexOf(again) + 1].Visibility == Visibility.Visible, "Clearing keys left rows hidden");
                target.ApplyPaneLayout(layout);
                Check(SectionHeader.CollapsedKeys.SequenceEqual(["위치와 변형"]) && again.Folded, "Applying a layout did not restore folded sections");
            }
            finally { SectionHeader.SetCollapsedKeys([]); window.StopRenderingForShutdown(); target.StopRenderingForShutdown(); }
        });
    }
}
