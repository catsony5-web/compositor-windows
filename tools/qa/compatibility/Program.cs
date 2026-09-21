using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Windows;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; Application.Current?.Shutdown(); }
    }
    static void Run(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/qa/compatibility"); Directory.CreateDirectory(output);
        var app = new Application(); Theme.Apply(app);
        var type = typeof(MainWindow).Assembly.GetType("Compositor.Windows.CompatibilityDialog")!;
        foreach (var name in new[] { "two-pages.pdf", "2layers.psd", "block-rotation.dwg" })
        {
            string path = Path.GetFullPath(Path.Combine("artifacts/test-results/compatibility", name));
            var result = Task.Run(() => CompatibilityImport.ReadAsync(path, new(CadLongEdge: 1000))).GetAwaiter().GetResult();
            var dialog = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object?[] { null, path, false }, null)!;
            ((Image)type.GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!).Source = Imaging.Render(result.Document).Bitmap();
            ((TextBlock)type.GetField("messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!).Text = $"{result.Document.Width} × {result.Document.Height}px\n\n" + string.Join("\n\n", result.Warnings);
            ((TextBlock)type.GetField("details", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!).Text = "외부 파일 읽기 검증 완료 · 미리보기와 변환 안내";
            foreach (int width in new[] { 940, 780 }) Capture(dialog, Path.Combine(output, Path.GetFileNameWithoutExtension(name) + "-" + width + ".png"), width, width == 940 ? 660 : 530);
        }
        var exportType = typeof(MainWindow).Assembly.GetType("Compositor.Windows.CompatibilityExportDialog")!;
        var export = (Window)Activator.CreateInstance(exportType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object?[] { null, Demo.Create() }, null)!;
        Capture(export, Path.Combine(output, "export.png"), 510, 410);
        app.Shutdown(); Console.WriteLine("Offscreen compatibility previews: " + output);
    }
    static void Capture(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content; var size = new Size(width, height); content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(size));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
