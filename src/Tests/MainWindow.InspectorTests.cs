using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunInspectorTests(Action<string, Action> test)
    {
        var window = new MainWindow(null) { headlessTesting = true };
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Reset()
        {
            window.renderCts?.Cancel(); window.jobCts?.Cancel();
            window.doc = new Document { Width = 8, Height = 8, Name = "속성 테스트" };
            window.doc.Add(new Layer { Name = "아래", Pixels = Raster.Solid(8, 8, Colors.Blue) });
            window.doc.Add(new Layer { Name = "위", Pixels = Raster.Solid(8, 8, Colors.Red) });
            window.history = new History(); window.history.Reset(window.doc);
            window.tabs.Clear(); window.InitializeWorkspace(); window.activeTab = 0;
            window.canvas.Document = window.doc; window.pendingInspectorCommit = null;
            window.BuildProperties();
        }
        TextBox Box(int index) => window.properties.Children.OfType<Grid>()
            .SelectMany(grid => grid.Children.OfType<TextBox>()).ElementAt(index);
        static void Blur(TextBox box) => box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, box));
        static void Focus(TextBox box) => box.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, box));
        void Case(string name, Action action) => test("UI inspector: " + name, () => { Reset(); action(); });

        Case("blur commits opacity once and undo restores it", () =>
        {
            var opacity = Box(0); opacity.Text = "37.5"; Blur(opacity);
            Assert(Math.Abs(window.doc.Active!.Opacity - .375) < 0.000001, "Opacity did not commit");
            Assert(window.history.Dirty(window.doc), "Valid edit was not recorded");
            window.Undo();
            Assert(window.doc.Active!.Opacity == 1 && !window.history.Dirty(window.doc), "Undo did not restore opacity");
        });
        Case("numeric no-op keeps redo available", () =>
        {
            var x = Box(1); x.Text = "12"; Blur(x); window.Undo();
            Assert(window.history.CanRedo, "Setup did not create redo");
            var opacity = Box(0); opacity.Text = "100.0"; Blur(opacity);
            Assert(window.history.CanRedo && !window.history.Dirty(window.doc), "No-op edit changed history");
        });
        Case("invalid number stays inline without history", () =>
        {
            var opacity = Box(0); opacity.Text = "101"; Blur(opacity);
            Assert(ReferenceEquals(opacity.BorderBrush, inspectorInvalid), "Invalid value was not marked on the field");
            Assert(opacity.ToolTip is string message && message.Contains("0~100"), "Validation hint is missing");
            Assert(window.status.Text.Contains("0~100") && window.doc.Active!.Opacity == 1, "Invalid value changed the layer");
            Assert(!window.history.Dirty(window.doc) && !window.history.CanUndo, "Invalid value changed history");
            opacity.Text = "80"; Blur(opacity);
            Assert(Math.Abs(window.doc.Active!.Opacity - .8) < 0.000001, "Corrected value did not commit");
        });
        Case("selecting another layer commits the focused field first", () =>
        {
            var first = window.doc.Active!; var other = window.doc.Layers[0];
            var opacity = Box(0); Focus(opacity); opacity.Text = "64";
            window.SelectLayer(other.Id);
            Assert(Math.Abs(window.doc.Layers.Single(l => l.Id == first.Id).Opacity - .64) < 0.000001, "Pending edit was lost");
            Assert(window.doc.ActiveId == other.Id && window.history.Dirty(window.doc), "Layer selection or history changed incorrectly");
        });
        Case("stale field cannot change another layer or an updated inspector", () =>
        {
            var old = Box(0); window.BuildProperties();
            old.Text = "32"; Blur(old);
            Assert(window.doc.Active!.Opacity == 1 && !window.history.Dirty(window.doc), "Rebuilt field edited the document");
            var current = Box(0); window.SelectLayer(window.doc.Layers[0].Id);
            current.Text = "17"; Blur(current);
            Assert(window.doc.Layers.All(l => l.Opacity == 1), "Old selection edited a different layer");
        });
        Case("parent lock disables inline values and prevents history", () =>
        {
            var child = window.doc.Active!;
            var group = DocumentFeatures.CreateGroup(window.doc); window.doc.Add(group);
            child.ParentId = group.Id; group.Locked = true; window.doc.ActiveId = child.Id;
            window.BuildProperties();
            var x = Box(1); Assert(!x.IsEnabled, "Parent lock did not disable field");
            x.Text = "20"; Blur(x);
            Assert(child.X == 0 && !window.history.Dirty(window.doc), "Locked child was edited");
        });
    }
}
