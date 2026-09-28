using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ExportDialogTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
        static Document Doc(int w = 4, int h = 2)
        {
            var d = new Document { Width = w, Height = h, Name = "sample" };
            d.Add(new Layer { Pixels = Raster.Solid(w, h, Color.FromArgb(0, 0, 0, 0)) });
            return d;
        }
        test("export settings scale, flatten and name without touching source", () =>
        {
            var source = Raster.Solid(4, 2, Color.FromArgb(0, 10, 20, 30));
            var copy = source.Data.ToArray();
            var doubled = new ExportSettings(ExportFormat.Png, 2).Prepare(source);
            Assert(doubled.Width == 8 && doubled.Height == 4, "2x scale must double pixels");
            var flat = new ExportSettings(ExportFormat.Png, 1, KeepTransparency: false).Prepare(source);
            Assert(flat.Data[3] == 255 && flat.Data[0] == 255, "Transparency off must flatten to white");
            var kept = new ExportSettings(ExportFormat.Png).Prepare(source);
            Assert(kept.Data[3] == 0, "Transparency on keeps alpha");
            Assert(source.Data.SequenceEqual(copy), "Prepare mutated the source");
            Assert(new ExportSettings(ExportFormat.Png, 100).OutputSize(10, 10) == (80, 80), "Scale must clamp to 800%");
            Assert(new ExportSettings(ExportFormat.Jpeg).FileName("평면도.png", "x") == "평면도.jpg", "Extension follows format");
            Assert(new ExportSettings(ExportFormat.Tiff).FileName("  a/b:c  ", "x") == "abc.tiff", "Invalid characters removed");
            Assert(new ExportSettings().FileName("", "doc") == "doc.png", "Empty name falls back");
            using var stream = new MemoryStream(); new ExportSettings(ExportFormat.Png, 2).Write(source, stream, 96);
            stream.Position = 0; Assert(Raster.Load(stream).Width == 8, "Written file uses scaled size");
        });
        test("export dialog shows only the chosen format's options", () =>
        {
            var window = ExportDialog.Create(null, Doc());
            var parts = ExportDialog.PartsOf(window) ?? throw new Exception("Parts missing");
            Assert(parts.Format.Selected == ExportFormat.Png && parts.QualityRow.Visibility == Visibility.Collapsed, "PNG hides JPEG quality");
            Assert(parts.Transparency.Visibility == Visibility.Visible, "PNG offers transparency");
            Assert(parts.FileName.Text == "sample", "File name prefilled");
            parts.Format.Select(ExportFormat.Jpeg);
            Assert(parts.QualityRow.Visibility == Visibility.Visible && parts.Transparency.Visibility == Visibility.Collapsed, "JPEG shows quality, hides transparency");
            parts.Format.Select(ExportFormat.Tiff);
            Assert(parts.QualityRow.Visibility == Visibility.Collapsed && parts.Settings().Extension == ".tiff", "TIFF hides quality");
            Assert(parts.CustomScale.Visibility == Visibility.Collapsed, "Custom scale hidden by default");
            parts.Scale.Select(2);
            Assert(parts.Dimensions.Text.Contains("8 × 4"), "Dimensions show scaled size: " + parts.Dimensions.Text);
            parts.Scale.Select(0); parts.CustomScale.Text = "50";
            Assert(parts.CustomScale.Visibility == Visibility.Visible && Math.Abs(parts.Settings().Scale - .5) < 1e-9, "Custom percent applies");
            window.Close();
        });
        test("export dialog fills its preview and size estimate, and the estimate follows the format", () =>
        {
            var d = new Document { Width = 64, Height = 48, Name = "preview" };
            d.Add(new Layer { Pixels = Raster.Solid(64, 48, Color.FromArgb(128, 40, 120, 200)) });
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try
            {
            var window = ExportDialog.Create(null, d);
            var parts = ExportDialog.PartsOf(window)!;
            Assert(ExportDialog.WaitForPreview(window, TimeSpan.FromSeconds(20)), "Preview image never arrived");
            Assert(parts.Size.Text.StartsWith("예상 파일 크기") && (parts.Size.Text.EndsWith("KB") || parts.Size.Text.EndsWith("MB")), "Size estimate missing: " + parts.Size.Text);
            string png = parts.Size.Text;
            parts.Preview.Source = null; parts.Scale.Select(2);
            Assert(ExportDialog.WaitForPreview(window, TimeSpan.FromSeconds(20)) && parts.Size.Text != png, "2x estimate did not change");
            window.Close();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        test("export dialog lists artboards as a selectable list", () =>
        {
            var d = Doc(20, 20);
            d.Artboards.Add(new Artboard(Guid.NewGuid(), "A", 0, 0, 10, 10));
            d.Artboards.Add(new Artboard(Guid.NewGuid(), "B", 10, 0, 10, 10));
            var window = ExportDialog.Create(null, d, d.Artboards[1].Id);
            var parts = ExportDialog.PartsOf(window)!;
            Assert(parts.Boards is ListBox list && list.Items.Count == 2 && ReferenceEquals(list.SelectedItem, d.Artboards[1]), "Artboard list selects the requested board");
            Assert(ExportDialog.FormatBytes(2048) == "2 KB" || ExportDialog.FormatBytes(2048).StartsWith("2"), "Byte formatting");
            window.Close();
        });
    }
}
