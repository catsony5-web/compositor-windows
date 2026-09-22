using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace Compositor.Windows;

internal sealed partial class CompatibilityDialog
{
    internal static void RunStructureTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        test("CAD import dialog defaults to original objects and invalidates preview on mode change", () =>
        {
            var dialog = new CompatibilityDialog(null, "객체 구분.dxf");
            try
            {
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Morupixel;component/UI/Theme.xaml", UriKind.Relative) });
                dialog.Resources["UiFont"] = Theme.UiFont;
                Check(dialog.ReadOptions().CadStructure == CadImportStructure.Objects, "Default is not object import");
                Check(dialog.structure.SelectedItem.ToString() == "부분별로 편집 (추천)", "Themed selection displays record metadata instead of its label");
                var doc = new Document { Width = 4, Height = 4 };
                doc.Add(new Layer { Pixels = new Raster(4, 4) });
                dialog.prepared = new(doc, []); dialog.preparedComposite = new Raster(4, 4); dialog.accept.IsEnabled = true;
                dialog.structure.SelectedIndex = 1;
                Check(dialog.ReadOptions().CadStructure == CadImportStructure.Layers, "Layer mode not passed to importer");
                Check(dialog.prepared == null && dialog.preparedComposite == null && !dialog.accept.IsEnabled && dialog.Result == null,
                    "Switching structure can accept a stale preview");
                dialog.structure.SelectedIndex = 2;
                Check(dialog.ReadOptions().CadStructure == CadImportStructure.Combined, "Combined mode not passed to importer");
                dialog.structure.SelectedIndex = 0;
                Check(dialog.ReadOptions().CadStructure == CadImportStructure.Objects, "Cannot return to object import");
                dialog.details.Text = "CAD 도면";
                dialog.space.ItemsSource = new[] { new CadCompatibility.Space("*Model_Space", "모델 공간") }; dialog.space.SelectedIndex = 0;
                var root = (FrameworkElement)dialog.Content;
                root.Resources = dialog.Resources;
                if (root is Panel panel) panel.Background = Theme.Panel;
                root.Measure(new Size(900, 640)); root.Arrange(new Rect(0, 0, 900, 640)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "object-import-dialog.png")); encoder.Save(file);
            }
            finally { dialog.Close(); }
        });
        test("CAD structure selector does not change PDF or Photoshop import options", () =>
        {
            foreach (string name in new[] { "layers.pdf", "layers.ai", "layers.psd", "layers.psb" })
            {
                var dialog = new CompatibilityDialog(null, name);
                try
                {
                    dialog.structure.SelectedIndex = 1;
                    Check(dialog.ReadOptions().CadStructure == null, "CAD structure leaked into " + name);
                    Check(!dialog.settings.Children.Contains(dialog.structure), "CAD controls shown for " + name);
                }
                finally { dialog.Close(); }
            }
        });
        test("Collapsed import details preserve options and warnings and fit the minimum dialog", () =>
        {
            var dialog = new CompatibilityDialog(null, "도면.dwg");
            try
            {
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Morupixel;component/UI/Theme.xaml", UriKind.Relative) });
                var root = (FrameworkElement)dialog.Content; root.Resources = dialog.Resources;
                var raster = Raster.Solid(40, 30, Colors.White);
                var doc = new Document { Width = 40, Height = 30 }; doc.Add(new Layer { Pixels = raster });
                string[] warnings = ["문자 안내", "채움 안내", new string('가', 600)];
                dialog.ShowPrepared(new(doc, warnings), raster, raster.Bitmap(), dialog.ReadOptions());
                void Layout() { root.Measure(new Size(760, 530)); root.Arrange(new Rect(0, 0, 760, 530)); root.UpdateLayout(); }
                Layout();
                Check(!dialog.advanced.IsExpanded && !dialog.information.IsExpanded, "Technical sections expanded by default");
                Check(dialog.contentScroll.ScrollableHeight < 1, "Beginner view requires scrolling at minimum size");
                Check(dialog.ReadOptions().RetainVectors && dialog.ReadOptions().CadLongEdge == 2400, "Recommended defaults changed");
                Check(dialog.notices.Children.OfType<TextBlock>().Skip(1).Select(t => t.Text).SequenceEqual(warnings), "Warnings were lost or shortened");
                var peer = new System.Windows.Automation.Peers.ExpanderAutomationPeer(dialog.advanced);
                ((System.Windows.Automation.Provider.IExpandCollapseProvider)peer).Expand();
                dialog.information.IsExpanded = true; Layout();
                Check(dialog.edge.ActualHeight >= 30 && dialog.space.ActualHeight >= 30, "Advanced inputs are inaccessible");
                Check(dialog.accept.IsEnabled && dialog.prepared != null, "Opening details invalidated a valid preview");
                Check(dialog.contentScroll.ScrollableHeight > 0, "Long notices cannot be scrolled");
                var buttonBounds = dialog.accept.TransformToAncestor(root).TransformBounds(new Rect(dialog.accept.RenderSize));
                Check(buttonBounds.Bottom <= root.ActualHeight + 1, "Details pushed the import action offscreen");
                dialog.advanced.IsExpanded = false; dialog.information.IsExpanded = false;
                dialog.edge.Text = "3200";
                Check(dialog.ReadOptions().CadLongEdge == 3200 && !dialog.accept.IsEnabled && dialog.prepared == null, "Size edit can accept stale content");
                Check(dialog.information.Visibility == Visibility.Collapsed && dialog.notices.Children.Count == 0, "Stale warnings remain after a settings change");
                dialog.ShowPrepared(new(doc, warnings), raster, raster.Bitmap(), dialog.ReadOptions());
                dialog.retain.IsChecked = false;
                Check(!dialog.ReadOptions().RetainVectors && !dialog.accept.IsEnabled && dialog.prepared == null, "Vector edit can accept stale content");
            }
            finally { dialog.Close(); }
        });
        test("Import errors remain visible outside collapsed or scrolled details", () =>
        {
            foreach (string name in new[] { "drawing.dwg", "layers.pdf", "layers.ai", "layers.psd" })
            {
                var dialog = new CompatibilityDialog(null, name);
                try
                {
                    var raster = Raster.Solid(4, 4, Colors.White);
                    var doc = new Document { Width = 4, Height = 4 }; doc.Add(new Layer { Pixels = raster });
                    dialog.ShowPrepared(new(doc, ["old warning"]), raster, raster.Bitmap(), dialog.ReadOptions());
                    dialog.ShowError(new InvalidDataException("가져올 수 없는 파일입니다."));
                    Check(!dialog.accept.IsEnabled && dialog.prepared == null && dialog.preview.Source == null, "Error retained importable content");
                    Check(dialog.messages.Text == "가져올 수 없는 파일입니다." && !dialog.information.IsAncestorOf(dialog.messages)
                        && !dialog.advanced.IsAncestorOf(dialog.messages) && !dialog.contentScroll.IsAncestorOf(dialog.messages), "Error was hidden in collapsed or scrolled details");
                }
                finally { dialog.Close(); }
            }
        });
    }
}
