using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Compositor.Windows;

// The release folder leaves out Windows Forms, debugger support, framework texts for languages
// the editor does not translate and the generated Windows SDK projection (src/Packaging.targets).
// These checks prove that what remains still covers every path that used to reach those files.
internal static class PackageTrimTests
{
    // Assemblies the editor must never need. Each was either removed from the package or never shipped.
    internal static readonly string[] UnusedAssemblies =
    [
        "System.Windows.Forms", "System.Windows.Forms.Primitives", "System.Windows.Forms.Design", "WindowsFormsIntegration",
        "Microsoft.VisualBasic.Forms", "Microsoft.Windows.SDK.NET", "WinRT.Runtime"
    ];

    // A self-contained release folder carries WPF next to the editor. Development builds load the shared
    // framework instead (and a single-file build would keep WPF inside the executable).
    static bool AppLocalFramework => File.Exists(Path.Combine(AppContext.BaseDirectory, "PresentationFramework.dll"));

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only development builds read it; an empty location skips the check.")]
    static string? SharedFrameworkFolder => typeof(Freezable).Assembly.Location is { Length: > 0 } location ? Path.GetDirectoryName(location) : null;

    internal static void Run(Action<string, Action> test)
    {
        test("framework texts follow the translated display languages and fall back to English otherwise", () =>
        {
            string english = FrozenChangeMessage(CultureInfo.InvariantCulture);
            Check(english.Length > 0 && english.All(c => c < 0x2E80), "The neutral framework message is not English: " + english);
            string? frameworkFolder = AppLocalFramework ? AppContext.BaseDirectory : SharedFrameworkFolder;
            foreach (string culture in new[] { "ko", "ja", "zh-Hans", "zh-Hant" })
            {
                // A shared framework without language packs (some build machines) has nothing to compare.
                if (!AppLocalFramework && (frameworkFolder == null || !Directory.Exists(Path.Combine(frameworkFolder, culture)))) continue;
                string localized = FrozenChangeMessage(CultureInfo.GetCultureInfo(culture));
                Check(localized != english && localized.Any(c => c >= 0x2E80), $"{culture} framework texts are missing: {localized}");
            }
            if (AppLocalFramework)
            {
                Check(FrozenChangeMessage(CultureInfo.GetCultureInfo("de-DE")) == english, "Languages without a translation must fall back to English framework texts");
                foreach (string culture in new[] { "cs", "de", "es", "fr", "it", "pl", "pt-BR", "ru", "tr" })
                    Check(!Directory.Exists(Path.Combine(AppContext.BaseDirectory, culture)), "Release folder still carries framework texts for " + culture);
            }
        });

        test("image copy and paste clipboard formats work without Windows Forms", () =>
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 20; pixels[i + 2] = 200; pixels[i + 3] = 255; }
            var data = new DataObject(); data.SetImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16));
            // Clipboard.SetImage flushes the clipboard: Windows asks for every advertised format at once.
            // Rendering them here exercises the same conversions without touching the user's clipboard.
            var com = (ComDataObject)data;
            var formats = com.EnumFormatEtc(DATADIR.DATADIR_GET); var one = new FORMATETC[1]; var fetched = new int[1]; int rendered = 0;
            while (formats.Next(1, one, fetched) == 0 && fetched[0] == 1)
            {
                var format = one[0]; com.GetData(ref format, out var medium); ReleaseStgMedium(ref medium); rendered++;
            }
            Check(rendered >= 2, "The copied image advertised too few clipboard formats");
            // Pasting from another program goes through the OLE conversion, not the managed object.
            var pasted = new DataObject(new OleOnlyDataObject(com));
            Check(pasted.ContainsImage(), "A copied image is not offered for pasting");
            var image = pasted.GetImage() ?? throw new Exception("The pasted image could not be read");
            var pixel = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), pixel, 4, 0);
            Check(image.PixelWidth == 4 && image.PixelHeight == 4 && pixel[0] == 10 && pixel[1] == 20 && pixel[2] == 200, "The pasted image changed: " + string.Join(",", pixel));
            Check(!Loaded("System.Windows.Forms"), "Clipboard images loaded Windows Forms");
        });

        test("release folder matches its dependency manifest and leaves out unused runtime files", () =>
        {
            string folder = AppContext.BaseDirectory;
            // The generated Windows SDK projection is not referenced in any build.
            foreach (string name in new[] { "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll" })
                Check(!File.Exists(Path.Combine(folder, name)), name + " is still copied next to the editor");
            if (!AppLocalFramework) return; // development builds use the shared framework
            foreach (string name in new[] { "System.Windows.Forms.dll", "System.Windows.Forms.Primitives.dll", "System.Windows.Forms.Design.dll", "WindowsFormsIntegration.dll",
                "Microsoft.VisualBasic.Forms.dll", "Microsoft.DiaSymReader.Native.amd64.dll", "mscordaccore.dll", "mscordbi.dll", "createdump.exe" })
                Check(!File.Exists(Path.Combine(folder, name)), name + " is still in the release folder");
            // Files WPF still needs must stay: clipboard images load System.Drawing.Common, and XAML type
            // lookup in the WPF namespace loads every assembly mapped to it, the Ribbon control included.
            foreach (string name in new[] { "System.Drawing.Common.dll", "PresentationFramework-SystemDrawing.dll", "D3DCompiler_47_cor3.dll", "System.Windows.Controls.Ribbon.dll" })
                Check(File.Exists(Path.Combine(folder, name)), name + " is missing from the release folder");
            // The host refuses to start when the manifest lists a missing file; check every listed asset.
            using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Morupixel.deps.json")));
            string target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            var missing = new List<string>(); int listed = 0;
            foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
                foreach (string kind in new[] { "runtime", "native" })
                    if (library.Value.TryGetProperty(kind, out var assets))
                        foreach (var asset in assets.EnumerateObject())
                        {
                            listed++;
                            if (!File.Exists(Path.Combine(folder, Path.GetFileName(asset.Name)))) missing.Add(asset.Name);
                        }
            Check(listed > 100 && missing.Count == 0, $"Morupixel.deps.json lists {missing.Count} missing files: {string.Join(", ", missing.Take(8))}");
        });
    }

    // Runs last: no earlier self-test may have needed an assembly the release leaves out.
    internal static void RunLast(Action<string, Action> test)
    {
        test("no self-test loaded Windows Forms or the Windows SDK projection", () =>
        {
            var loaded = UnusedAssemblies.Where(Loaded).ToArray();
            Check(loaded.Length == 0, "Loaded although the release leaves it out: " + string.Join(", ", loaded));
        });
    }

    static bool Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    // Freezable lives in WindowsBase; its read-only message comes from the framework's satellite resources.
    static string FrozenChangeMessage(CultureInfo culture)
    {
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = culture;
            var brush = new SolidColorBrush(Colors.Red); brush.Freeze();
            try { brush.Color = Colors.Blue; }
            catch (InvalidOperationException e) { return e.Message; }
            throw new Exception("A frozen brush accepted a change");
        }
        finally { Thread.CurrentThread.CurrentUICulture = previous; }
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    // Exposes only the COM data object, as the clipboard does for data from another program.
    sealed class OleOnlyDataObject(ComDataObject inner) : ComDataObject
    {
        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) => inner.DAdvise(ref format, advf, sink, out connection);
        public void DUnadvise(int connection) => inner.DUnadvise(connection);
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => inner.EnumDAdvise(out enumAdvise);
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => inner.GetCanonicalFormatEtc(ref formatIn, out formatOut);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => inner.GetData(ref format, out medium);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
        public int QueryGetData(ref FORMATETC format) => inner.QueryGetData(ref format);
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => inner.SetData(ref formatIn, ref medium, release);
    }
}
