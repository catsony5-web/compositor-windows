using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;

namespace Compositor.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        {
            try { return SelfTests.Run(args.Length > 1 ? args[1] : "test-results.txt"); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }
        if (args.Length > 0 && args[0] is "--mcp" or "--automation-list" or "--automation-command")
        {
            // A Windows GUI executable may have redirected handles without a console.
            // Setting the console code page would fail even though these streams are valid.
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true)));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            if (args[0] == "--mcp")
            {
                if (args.Length != 1) return 2;
                return AutomationMcpServer.RunAsync(Console.In, Console.Out, Console.Error).GetAwaiter().GetResult();
            }
            return AutomationCommandLine.Run(args);
        }
        var app = new Application();
        if (args.Length > 0 && args[0] == "--automation-headless")
        {
            if (args.Length != 2) return 2;
            MainWindow? host = null;
            try
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown; Theme.Apply(app);
                host = new MainWindow(null);
                string session = host.EnableAutomation(headless: true);
                // The ready file must be new: starting a helper never replaces a user's file.
                using (var ready = new FileStream(Path.GetFullPath(args[1]), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(ready, new UTF8Encoding(false)))
                    writer.Write(new JsonObject { ["sessionId"] = session, ["processId"] = Environment.ProcessId }.ToJsonString());
                app.Exit += (_, _) => host.DisableAutomation();
                return app.Run();
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            finally { host?.DisableAutomation(); }
        }
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
        bool automation = args.Length > 0 && args[0] == "--automation";
        var window = new MainWindow(automation ? args.Skip(1).FirstOrDefault() : args.FirstOrDefault());
        if (automation) window.EnableAutomation();
        app.Exit += (_, _) => window.DisableAutomation();
        return app.Run(window);
    }
}
