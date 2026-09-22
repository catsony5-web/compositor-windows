using System.IO;

namespace Compositor.Windows;

public enum CadImportStructure { Combined, Layers, Objects }

public sealed record CompatibilityOptions(int Page = 1, double Dpi = 150, int CadLongEdge = 2400, bool SeparateLayers = false,
    string? CadLayout = null, bool PreservePdfLayers = true, bool RetainVectors = true, CadImportStructure? CadStructure = null);
public sealed record CompatibilityResult(Document Document, IReadOnlyList<string> Warnings);

public static class CompatibilityImport
{
    public const long MaxFileBytes = 8L * 1024 * 1024 * 1024;
    public const string Filter = "지원 파일|*.moruproj;*.cwproj;*.pdf;*.ai;*.psd;*.psb;*.dwg;*.dxf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.heic;*.heif|PDF / Illustrator (PDF 호환)|*.pdf;*.ai|Photoshop|*.psd;*.psb|AutoCAD 도면|*.dwg;*.dxf|모든 파일|*.*";
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".ai" or ".psd" or ".psb" or ".dwg" or ".dxf";
    public static void ValidateFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("파일을 찾을 수 없습니다.", path);
        if (file.Length == 0 || file.Length > MaxFileBytes) throw new InvalidDataException($"호환 파일은 0바이트보다 크고 {MaxFileBytes / (1024L * 1024 * 1024):N0}GB 이하여야 합니다.");
    }
    public static async Task<CompatibilityResult> ReadAsync(string path, CompatibilityOptions options, CancellationToken token = default)
    {
        ValidateFile(path); token.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pdf" or ".ai") return await PdfCompatibility.ReadAsync(path, options, token).ConfigureAwait(false);
        return await OnSta(() => extension switch
        {
            ".psd" or ".psb" => PhotoshopCompatibility.Read(path, options.SeparateLayers, token),
            ".dwg" or ".dxf" => CadCompatibility.Read(path, options, token),
            _ => throw new NotSupportedException("지원하지 않는 호환 파일 형식입니다.")
        }, token).ConfigureAwait(false);
    }
    // WPF drawing and color conversion stay on an isolated STA, never the user's UI thread.
    internal static Task<T> OnSta<T>(Func<T> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                T result;
                try { token.ThrowIfCancellationRequested(); result = action(); token.ThrowIfCancellationRequested(); }
                finally
                {
                    // WPF owns native render channels on each importing STA.
                    // Tear them down on that thread before publishing completion.
                    var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                    if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
                }
                completion.SetResult(result);
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true, Name = "Morupixel file import" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    internal static Document Single(string path, Raster raster, double dpi, string layerName)
    {
        var doc = new Document { Name = Path.GetFileNameWithoutExtension(path), Width = raster.Width, Height = raster.Height, Dpi = dpi };
        doc.Add(new Layer { Name = layerName, Pixels = raster }); return doc;
    }
    public static IReadOnlyList<Layer> PlacementLayers(Document imported, int width, int height)
    {
        imported.Validate();
        double scale = Math.Min(1, Math.Min(width / (double)imported.Width, height / (double)imported.Height));
        var group = new Layer { Name = imported.Name, Kind = LayerKind.Group, Pixels = new Raster(imported.Width, imported.Height),
            Scale = scale, X = (width - imported.Width * scale) / 2, Y = (height - imported.Height * scale) / 2 };
        var ids = imported.Layers.ToDictionary(l => l.Id, _ => Guid.NewGuid());
        var layers = new List<Layer> { group };
        foreach (var original in imported.Layers)
        {
            var copy = original.Snapshot(); copy.Id = ids[original.Id]; copy.ParentId = original.ParentId is { } parent ? ids[parent] : group.Id;
            layers.Add(copy);
        }
        return layers;
    }
}
