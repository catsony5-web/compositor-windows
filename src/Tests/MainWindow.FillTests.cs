using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunBucketCommandTests(Action<string, Action> test)
    {
        static void Assert(bool condition, string message = "Assertion failed")
        { if (!condition) throw new InvalidOperationException(message); }
        static bool Complete(Func<Task<bool>> start)
        {
            var previous = SynchronizationContext.Current;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = start();
                if (!task.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                }
                return task.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        test("UI bucket commits once, undo restores pixels, and no-op preserves clean history", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.doc = new Document { Width = 9, Height = 3 };
            var lineArt = Raster.Solid(9, 3, Colors.White);
            for (int y = 0; y < 3; y++)
            {
                int i = (y * 9 + 4) * 4;
                lineArt.Data[i] = lineArt.Data[i + 1] = lineArt.Data[i + 2] = 0;
            }
            window.doc.Add(new Layer { Pixels = lineArt });
            window.doc.Add(new Layer { Pixels = new Raster(9, 3) });
            window.history = new History(); window.history.Reset(window.doc);
            window.tabs.Clear(); window.InitializeWorkspace(); window.activeTab = 0;
            window.canvas.Document = window.doc; window.selection = null; window.maskEditing = false;
            window.foreground = Colors.Red; window.brushOpacity = 1;
            window.bucketTolerance = 0; window.bucketContiguous = true; window.bucketSampleMerged = true;

            var original = window.doc.Active!.Pixels;
            Assert(Complete(() => window.PaintBucketAsync(new Point(.5, .5))));
            Assert(window.history.CanUndo && window.history.Dirty(window.doc));
            Assert(window.doc.Active!.Pixels.Data[3] == 255 && window.doc.Active.Pixels.Data[(1 * 9 + 7) * 4 + 3] == 0);
            window.Undo();
            Assert(!window.history.Dirty(window.doc) && !window.history.CanUndo && window.history.CanRedo);
            Assert(ReferenceEquals(original, window.doc.Active!.Pixels) && window.doc.Active.Pixels.Data[3] == 0);

            window.foreground = Colors.Transparent;
            Assert(!Complete(() => window.PaintBucketAsync(new Point(.5, .5))));
            Assert(!window.history.Dirty(window.doc) && !window.history.CanUndo && window.history.CanRedo);
        });
    }
}
