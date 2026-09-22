using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunWandCommandTests(Action<string, Action> test)
    {
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static bool Complete(Func<Task<bool>> start)
        {
            var previous = SynchronizationContext.Current; var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = start();
                if (!task.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                }
                return task.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        test("UI wand selects vectors without changing history and exposes live precision options", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true }; window.AddTab(PrecisionWandTests.Drawing(), null);
            window.canvas.DesignMode = true; window.canvas.Zoom = 32; window.SetTool(Tool.MagicWand);
            var revision = window.doc.Revision; var pixels = window.doc.Active!.Pixels;
            window.wandToleranceSlider!.Value = 12;
            Assert(window.wandTolerance == 12 && window.wandOptions.Visibility == Visibility.Visible, "Toolbar does not reach wand settings");
            var checkbox = window.wandOptions.Children.OfType<CheckBox>().Single(c => Equals(c.Content, "가장자리 보정"));
            checkbox.IsChecked = false; checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(!window.wandAntialias, "Antialias toggle does not reach the command");
            Assert(Complete(() => window.SelectWandAsync(new(47, 38), true, SelectionCombine.Replace)), "UI selection did not complete");
            Assert(window.selection?.Contour != null && window.selection.CoverageBounds != null && window.selection.Contains(41.5, 35), "UI discarded precision or cached contour");
            Assert(window.doc.Revision == revision && !window.history.CanUndo && !window.history.Dirty(window.doc) && ReferenceEquals(pixels, window.doc.Active.Pixels), "Selection changed source or document history");
            window.SetTool(Tool.Move); Assert(window.wandOptions.Visibility == Visibility.Collapsed, "Wand options remained on another tool"); window.canvas.CancelDesignPreview();
        });
        test("UI wand cancellation and changed selection reject stale results", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true }; window.AddTab(PrecisionWandTests.Drawing(), null); window.canvas.DesignMode = true; window.canvas.Zoom = 16;
            var previous = window.selection = new Selection(new Rect(0, 0, 1, 1));
            Assert(!Complete(() => { var task = window.SelectWandAsync(new(47, 38), true, SelectionCombine.Replace); window.jobCts!.Cancel(); return task; }), "Cancelled result was committed");
            Assert(ReferenceEquals(window.selection, previous), "Cancellation destroyed the previous selection");
            var replacement = new Selection(new Rect(5, 5, 2, 2));
            Assert(!Complete(() => { var task = window.SelectWandAsync(new(47, 38), true, SelectionCombine.Replace); window.selection = replacement; return task; }), "Stale selection overwrote a newer choice");
            Assert(ReferenceEquals(window.selection, replacement) && window.jobCts == null, "Stale work left incorrect selection or job state"); window.canvas.CancelDesignPreview();
        });
    }
}
