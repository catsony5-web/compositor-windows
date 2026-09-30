using System.IO;
using System.Windows;

namespace Compositor.Windows;

public enum CadImportStructure { Combined, Layers, Objects }

public sealed record CompatibilityOptions(int Page = 1, double Dpi = 150, int CadLongEdge = 2400, bool SeparateLayers = false,
    string? CadLayout = null, bool PreservePdfLayers = true, bool RetainVectors = true, CadImportStructure? CadStructure = null, bool GroupDrawingObjects = false, CadCleanup? Cleanup = null,
    bool Artboard = false, bool CadLayoutOptional = false);
public sealed record CompatibilityResult(Document Document, IReadOnlyList<string> Warnings);
/// <summary>One file of a multi-file import: its document, or the reason it could not be read.</summary>
public sealed record ImportedFile(string Path, Document? Document, IReadOnlyList<string> Warnings, string? Error = null);

public static class CompatibilityImport
{
    public const long MaxFileBytes = 8L * 1024 * 1024 * 1024;
    public const string Filter = "지원 파일|*.moruproj;*.cwproj;*.pdf;*.ai;*.psd;*.psb;*.dwg;*.dxf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.heic;*.heif|PDF / AI (PDF 호환)|*.pdf;*.ai|PSD / PSB 이미지|*.psd;*.psb|CAD 도면 (DWG / DXF)|*.dwg;*.dxf|모든 파일|*.*";
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".ai" or ".psd" or ".psb" or ".dwg" or ".dxf";
    public static ImportFamily Family(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dwg" or ".dxf" => ImportFamily.Cad, ".pdf" or ".ai" => ImportFamily.Pdf, ".psd" or ".psb" => ImportFamily.Psd, _ => ImportFamily.None
    };
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
        var result = extension is ".pdf" or ".ai" ? await PdfCompatibility.ReadAsync(path, options, token).ConfigureAwait(false)
            : await OnSta(() => extension switch
        {
            ".psd" or ".psb" => PsdCompatibility.Read(path, options.SeparateLayers, token),
            ".dwg" or ".dxf" => CadCompatibility.Read(path, options, token),
            _ => throw new NotSupportedException("지원하지 않는 호환 파일 형식입니다.")
        }, token).ConfigureAwait(false);
        // CAD imports place their own artboard (a layout's paper); a PDF page is the drawing.
        if (options.Artboard && extension is ".pdf" or ".ai" && result.Document.Artboards.Count == 0)
            result.Document.Artboards.Add(new Artboard(Guid.NewGuid(), result.Document.Name, 0, 0, result.Document.Width, result.Document.Height));
        if (options.GroupDrawingObjects && extension is ".dwg" or ".dxf" or ".pdf" or ".ai") DrawingLayers.Wrap(result.Document);
        return result;
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
        return PlacementLayers(imported, scale, (width - imported.Width * scale) / 2, (height - imported.Height * scale) / 2);
    }
    /// <summary>Adds an imported document to <paramref name="target"/> as one group and returns the new layers.
    /// With <paramref name="artboard"/>, a target that already has explicit artboards gets the drawing on a new
    /// artboard 40px right of them, top-aligned with the last one and no larger than it, so no existing artboard
    /// is covered. Otherwise the drawing is centered and fitted to <paramref name="width"/> × <paramref name="height"/>.
    /// When several drawings are placed in one import, pass the last artboard from before the import as
    /// <paramref name="reference"/>: each drawing is then sized against the user's artboard, not the one just added.</summary>
    public static IReadOnlyList<Layer> Place(Document target, Document imported, bool artboard, int width, int height, Artboard? reference = null)
    {
        IReadOnlyList<Layer>? layers = null;
        if (artboard && target.Artboards.Count is > 0 and < ArtboardEditing.MaxArtboards)
        {
            imported.Validate();
            var source = imported.Artboards.FirstOrDefault()?.Bounds ?? new Rect(0, 0, imported.Width, imported.Height);
            reference ??= target.Artboards[^1];
            double scale = Math.Clamp(Math.Min(reference.Width / source.Width, reference.Height / source.Height), .01, 1);
            var board = new Artboard(Guid.Empty, imported.Artboards.FirstOrDefault()?.Name ?? imported.Name, Math.Round(target.Artboards.Max(b => b.Bounds.Right) + 40), Math.Round(reference.Y),
                Math.Max(1, Math.Round(source.Width * scale)), Math.Max(1, Math.Round(source.Height * scale)));
            try
            {
                // Right of every artboard and top-aligned: the canvas only grows right or down, so nothing moves.
                ArtboardEditing.Set(target, board, add: true);
                layers = PlacementLayers(imported, scale, board.X - source.X * scale, board.Y - source.Y * scale);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException) { layers = null; }
        }
        layers ??= PlacementLayers(imported, width, height);
        target.Layers.AddRange(layers); target.ActiveId = layers[^1].Id;
        return layers;
    }
    static IReadOnlyList<Layer> PlacementLayers(Document imported, double scale, double x, double y)
    {
        var roots = imported.Layers.Where(l => l.ParentId == null).ToArray();
        if (roots.Length == 1 && roots[0].Kind == LayerKind.Group && roots[0].Category == LayerCategory.Drawing)
        {
            var mapping = imported.Layers.ToDictionary(l => l.Id, _ => Guid.NewGuid());
            return imported.Layers.Select(original =>
            {
                var copy = original.Snapshot(); copy.Id = mapping[original.Id];
                if (copy.ParentId is { } parent) copy.ParentId = mapping[parent];
                else { copy.Scale *= scale; copy.X = x; copy.Y = y; }
                return copy;
            }).ToArray();
        }
        var group = new Layer { Name = imported.Name, Kind = LayerKind.Group, Pixels = new Raster(imported.Width, imported.Height), Scale = scale, X = x, Y = y };
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
