using Compositor.Windows;
class Program { [STAThread] static int Main() { int failed=0; ImportExportTests.Run((name, action) => { try { action(); Console.WriteLine("PASS " + name); } catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + " " + e); } }); return failed; } }
