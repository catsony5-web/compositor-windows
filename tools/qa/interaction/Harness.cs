using Compositor.Windows;
using System.IO;
using System.Reflection;

public static class Harness
{
    [STAThread] public static int Main(string[] args)
    {
        string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/qa/interaction");
        Directory.CreateDirectory(outputDirectory);
        int failed = 0, passed = 0;
        Action<string, Action> test = (name, action) =>
        {
            try { action(); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; }
        };
        AdvancedToolTests.Run(test);
        typeof(MainWindow).GetMethod("RunCommandTests", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [test, outputDirectory]);
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
