using Compositor.Windows; using System.IO;
public static class Runner {
[STAThread] public static int Main(string[] args) {
string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/qa/layer-export");
Directory.CreateDirectory(outputDirectory);
int failed=0;
SelectedLayerExportTests.Run((name, run) => { try {run(); Console.WriteLine("PASS " + name);} catch(Exception e) {failed++; Console.WriteLine("FAIL " + name + ": " + e);}}, outputDirectory);
return failed;
}}
