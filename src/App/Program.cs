using System.IO;
using System.Windows;

namespace Compositor.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : "test-results.txt");
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "Morupixel · 오류", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        Theme.Apply(app);
        if (args.Length == 2 && args[0] == "--render-studio-previews")
        {
            try { new MainWindow(null).RenderStudioPreview(args[1]); app.Shutdown(); return 0; }
            catch (Exception e) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "error.txt"), e.ToString()); app.Shutdown(); return 1; }
        }
        if (args.Length > 0 && args[0] == "--render-preview")
        {
            if (args.Length != 2) return 2;
            try { new MainWindow(null).RenderPreview(args[1]); app.Shutdown(); return 0; }
            catch (Exception e) { File.WriteAllText(args[1] + ".error.txt", e.ToString()); app.Shutdown(); return 1; }
        }
        return app.Run(new MainWindow(args.FirstOrDefault()));
    }
}
