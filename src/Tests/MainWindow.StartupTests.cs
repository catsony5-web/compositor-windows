using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunStartupTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Empty(MainWindow window)
        {
            Check(!window.HasDocument && window.tabs.Count == 0 && window.doc.Layers.Count == 0,
                "Empty workspace contains a document, hidden sample or layers");
            Check(window.canvas.Document == null && window.canvas.Composite == null && window.composite == null,
                "Empty workspace retains a canvas document or rendered image");
            Check(!window.canvas.IsEnabled && window.documentControls.All(control => !control.IsEnabled),
                "Document-only controls remain enabled without a document");
            Check(window.selection == null && window.canvas.Selection == null && window.selectedLayers.Count == 0 && window.canvas.Guides.Count == 0,
                "Empty workspace retains selection or guides");
            Check(window.histogram.Bins.SelectMany(channel => channel).All(count => count == 0)
                && !window.histogramInfo.Text.Contains("STALE") && !window.status.Text.Contains("STALE")
                && !window.documentTitle.Text.Contains("STALE"), "Empty workspace retains old document status or histogram data");
            Check(!window.history.CanUndo && !window.history.CanRedo && !window.history.Dirty(window.doc),
                "Empty workspace has undo history or unsaved phantom changes");
        }
        static void Cleanup(MainWindow window)
        {
            window.headlessTesting = true; window.renderCts?.Cancel(); window.jobCts?.Cancel();
            window.textPreviewCts?.Cancel(); window.gestureRenderTimer.Stop(); window.pendingInspectorCommit = null;
        }

        test("startup initializes without a sample and document shortcuts cannot create phantom work", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                Check(!window.IsLoaded, "Headless startup test unexpectedly showed a window");
                Empty(window); window.InitializeStartup(); Empty(window);
                window.canvas.Fit(); window.canvas.ZoomAt(1.15, new Point(100, 100)); Empty(window);
                // Skip native open/new dialogs and clipboard paste. These are actual no-document editor commands.
                foreach (var (key, modifiers) in new[]
                {
                    (Key.W, ModifierKeys.Control), (Key.Tab, ModifierKeys.Control), (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift),
                    (Key.S, ModifierKeys.Control), (Key.E, ModifierKeys.Control | ModifierKeys.Shift),
                    (Key.L, ModifierKeys.Control), (Key.U, ModifierKeys.Control), (Key.T, ModifierKeys.Control), (Key.C, ModifierKeys.Control),
                    (Key.A, ModifierKeys.Control | ModifierKeys.Shift),
                    (Key.Z, ModifierKeys.Control), (Key.Z, ModifierKeys.Control | ModifierKeys.Shift),
                    (Key.A, ModifierKeys.Control), (Key.D, ModifierKeys.Control), (Key.J, ModifierKeys.Control),
                    (Key.Delete, ModifierKeys.None), (Key.Delete, ModifierKeys.Alt), (Key.Delete, ModifierKeys.Control),
                    (Key.Left, ModifierKeys.None), (Key.Right, ModifierKeys.Shift), (Key.Escape, ModifierKeys.None)
                }) window.ExecuteEditorShortcut(key, modifiers);
                window.CloseTabAt(0); window.SwitchTab(0); window.Refresh(false);
                Empty(window); Check(!window.IsLoaded, "A no-document command opened the test window");
            }
            finally { Cleanup(window); }
        });

        test("explicit learning sample closes to empty and clears stale render work before reopening", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            using var render = new CancellationTokenSource(); using var job = new CancellationTokenSource(); using var preview = new CancellationTokenSource();
            try
            {
                window.InitializeStartup(); window.OpenLearningSample();
                Check(window.HasDocument && window.tabs.Count == 1 && window.doc.Layers.Any(layer => layer.Kind == LayerKind.Text)
                    && window.doc.Layers.Any(layer => layer.Kind == LayerKind.Raster) && !window.history.Dirty(window.doc),
                    "Explicit learning command did not create one clean sample tab");
                window.composite = Raster.Solid(2, 2, Colors.Red); window.canvas.Composite = window.composite.Bitmap();
                window.histogram.Update(window.composite); window.histogramInfo.Text = "STALE HISTOGRAM";
                window.status.Text = "STALE OPEN DOCUMENT"; window.documentTitle.Text = "STALE TITLE";
                window.selection = new Selection(new Rect(0, 0, 1, 1)); window.canvas.Selection = window.selection;
                window.canvas.Guides.Add((true, 1)); window.canvas.BrushPoint = new Point(1, 1);
                window.canvas.GestureBounds = new Rect(0, 0, 1, 1);
                window.renderCts = render; window.jobCts = job; window.textPreviewCts = preview;
                window.pendingFullRender = true; window.pendingGestureRender = true; window.gestureRenderTimer.Start();
                long generation = window.renderGeneration;
                window.CloseTabAt(0); Empty(window);
                Check(render.IsCancellationRequested && job.IsCancellationRequested && preview.IsCancellationRequested
                    && !window.pendingFullRender && !window.pendingGestureRender && !window.gestureRenderTimer.IsEnabled
                    && window.renderGeneration > generation && !window.renderShutdown,
                    "Closing the final tab left render work running or disabled the editor permanently");
                Check(window.canvas.BrushPoint == null && window.canvas.GestureBounds == null && window.canvas.MovePreviewBackground == null
                    && window.canvas.MovePreviewLayer == null, "Closing the final tab left gesture graphics on the canvas");
                window.headlessTesting = false; window.QueueRender(); window.QueueRender(true); window.OnGestureRenderTick(null, EventArgs.Empty);
                Check(!window.pendingFullRender && !window.pendingGestureRender && !window.rendering && !window.gestureRenderTimer.IsEnabled,
                    "An empty-workspace callback scheduled another render");
                window.headlessTesting = true;
                var created = NewDocumentDialog.CreateDocument("새 작업", "12", "9", 0); window.AddTab(created, null);
                Check(window.HasDocument && window.tabs.Count == 1 && ReferenceEquals(window.canvas.Document, created)
                    && window.doc.Width == 12 && window.doc.Height == 9 && window.doc.Layers.Count == 1 && !window.history.Dirty(window.doc),
                    "Creating a document after closing the sample did not restore an editable canvas");
                window.CloseTabAt(0); Empty(window); window.OpenLearningSample();
                Check(window.tabs.Count == 1 && window.HasDocument, "Learning sample cannot be reopened after returning to empty");
            }
            finally { Cleanup(window); }
        });

        test("empty workspace accepts images projects and layer imports without extra sample tabs", () =>
        {
            string imagePath = Path.Combine(directory, "startup-image.png"), largerPath = Path.Combine(directory, "startup-larger.png");
            string projectPath = Path.Combine(directory, "startup-project.moruproj");
            using (var stream = File.Create(imagePath)) Raster.Solid(4, 3, Colors.Blue).WritePng(stream);
            using (var stream = File.Create(largerPath)) Raster.Solid(8, 6, Colors.Red).WritePng(stream);
            ProjectStore.Save(NewDocumentDialog.CreateDocument("파일로 연 작업", "6", "5", 1), projectPath);
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.InitializeStartup(); window.OpenImage(imagePath);
                Check(window.tabs.Count == 1 && window.doc.Width == 4 && window.doc.Height == 3 && window.doc.Layers.Count == 1,
                    "Opening an image kept a phantom tab or used sentinel dimensions");
                var imageDocument = window.doc; window.OpenPath(projectPath);
                Check(window.tabs.Count == 2 && window.doc.Name == "파일로 연 작업", "Project did not open beside the image");
                window.OpenPath(projectPath); Check(window.tabs.Count == 2, "Reopening the project duplicated a tab");
                window.SwitchTab(0); Check(ReferenceEquals(window.doc, imageDocument), "Document navigation lost the image");
                window.CloseTabAt(1); Check(window.tabs.Count == 1 && ReferenceEquals(window.doc, imageDocument), "Closing the inactive project changed the image");
                window.CloseTabAt(0); Empty(window);
                window.ImportFiles([imagePath]);
                Check(window.tabs.Count == 1 && window.doc.Width == 4 && window.doc.Height == 3 && window.doc.Layers.Count == 1,
                    "Importing into an empty workspace did not create an image-sized document");
                window.ImportFiles([largerPath]);
                Check(window.tabs.Count == 1 && window.doc.Layers.Count == 2 && window.doc.Active!.Pixels.Width == 8
                    && Math.Abs(window.doc.Active.Scale - .5) < .0001, "Further import did not add a fitted layer to the existing document");
                window.history.MarkSaved(window.doc); window.CloseTabAt(0); Empty(window);
                window.PasteRaster(Raster.Solid(7, 4, Colors.Green));
                Check(window.tabs.Count == 1 && window.doc.Width == 7 && window.doc.Height == 4 && window.doc.Layers.Count == 1
                    && window.canvas.IsEnabled && window.documentControls.All(control => control.IsEnabled),
                    "Pasting into empty workspace did not restore an image-sized editable document");
                window.PasteRaster(Raster.Solid(2, 2, Colors.Red));
                Check(window.tabs.Count == 1 && window.doc.Layers.Count == 2 && window.history.CanUndo,
                    "Further paste did not remain an undoable layer operation");
                window.history.MarkSaved(window.doc); window.CloseTabAt(0); Empty(window);
            }
            finally { Cleanup(window); }
            var startup = new MainWindow(projectPath) { headlessTesting = true };
            try
            {
                startup.InitializeStartup();
                Check(startup.tabs.Count == 1 && startup.HasDocument && startup.doc.Name == "파일로 연 작업"
                    && startup.doc.Width == 6 && startup.canvas.Document == startup.doc, "Startup file did not replace the empty state directly");
                Check(!startup.IsLoaded, "Startup-path test opened a native window");
            }
            finally { Cleanup(startup); }
        });
    }
}
