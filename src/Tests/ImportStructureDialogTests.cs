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
                Check(dialog.structure.SelectedItem.ToString() == "객체별 · 원본 레이어를 그룹으로", "Themed selection displays record metadata instead of its label");
                Check(dialog.structureHint.Text.Contains("폴리라인"), "Topology guidance missing");
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
                dialog.details.Text = "모델 공간 · 2D 벡터 유지";
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
    }
}
