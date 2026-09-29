using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Windows;

public enum ImportFamily { None, Cad, Pdf, Psd }

/// <summary>The last import choices, per file kind. They prefill the import dialog, are applied to every
/// file of a multi-file import and are used as-is when DWG/DXF imports skip the dialog. PDF page
/// numbers belong to one file and are not remembered.</summary>
public sealed record ImportSettings
{
    public const int MaxRoles = 300, MaxNameLength = 256;
    public const string ModelSpace = "*Model_Space";
    public CadImportStructure CadStructure { get; init; } = CadImportStructure.Objects;
    public bool CadLineWeights { get; init; } = true;
    public HatchTreatment CadHatches { get; init; } = HatchTreatment.Suggest;
    public string? CadMaterialImage { get; init; }
    public int CadLongEdge { get; init; } = 2400;
    public bool CadRetainVectors { get; init; } = true;
    /// <summary>Null picks automatically, <see cref="ModelSpace"/> is the model space, anything else a layout name.</summary>
    public string? CadLayout { get; init; }
    /// <summary>Roles the user changed from the automatic guess, by CAD layer name. They apply to layers of the same name in any drawing.</summary>
    public IReadOnlyDictionary<string, DrawingRole>? CadRoles { get; init; }
    /// <summary>DWG/DXF files open with these settings without the dialog; Shift or the File menu shows it again.</summary>
    public bool CadSkipDialog { get; init; }
    public double PdfDpi { get; init; } = 150;
    public bool PdfLayers { get; init; } = true;
    public bool PdfRetainVectors { get; init; } = true;
    public bool PsdLayers { get; init; }
    /// <summary>DWG/DXF and PDF/AI imports get an artboard sized to the drawing (a layout's paper when one is imported).</summary>
    public bool Artboard { get; init; } = true;

    public CadCleanup? Cleanup(IReadOnlyDictionary<string, DrawingRole>? roles = null) => !CadLineWeights && CadHatches == HatchTreatment.Keep ? null
        : new CadCleanup(CadLineWeights, CadHatches, CadHatches == HatchTreatment.Image ? CadMaterialImage : null, roles ?? CadRoles);

    /// <summary>Import options for one file. A remembered layout that the file does not have falls back to the automatic choice.</summary>
    public CompatibilityOptions Options(string path, int page = 1) => CompatibilityImport.Family(path) switch
    {
        ImportFamily.Cad => new(Dpi: 96, CadLongEdge: CadLongEdge, CadLayout: CadLayout, PreservePdfLayers: false, RetainVectors: CadRetainVectors,
            CadStructure: CadStructure, GroupDrawingObjects: true, Cleanup: Cleanup(), Artboard: Artboard, CadLayoutOptional: CadLayout is not (null or ModelSpace)),
        ImportFamily.Pdf => new(Page: page, Dpi: PdfDpi, SeparateLayers: PdfLayers, PreservePdfLayers: PdfLayers, RetainVectors: PdfRetainVectors,
            GroupDrawingObjects: PdfRetainVectors, Artboard: Artboard),
        _ => new(Dpi: 96, SeparateLayers: PsdLayers, PreservePdfLayers: false)
    };

    /// <summary>Values from disk may be edited or come from another version: clamp numbers, drop unknown choices and bad paths.</summary>
    public ImportSettings Sanitized()
    {
        var defaults = new ImportSettings();
        string? image = CadMaterialImage is { Length: > 0 and < 1024 } file && file.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(file) ? file : null;
        var hatches = Enum.IsDefined(CadHatches) ? CadHatches : defaults.CadHatches;
        if (hatches == HatchTreatment.Image && image == null) hatches = HatchTreatment.Suggest;
        static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxNameLength && !value.Any(char.IsControl);
        var roles = new Dictionary<string, DrawingRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layer, role) in CadRoles ?? new Dictionary<string, DrawingRole>())
            if (roles.Count < MaxRoles && Name(layer) && Enum.IsDefined(role)) roles.TryAdd(layer, role);
        return this with
        {
            CadStructure = Enum.IsDefined(CadStructure) ? CadStructure : defaults.CadStructure,
            CadHatches = hatches, CadMaterialImage = image,
            CadLongEdge = Math.Clamp(CadLongEdge, 256, 4096),
            CadLayout = Name(CadLayout) ? CadLayout : null,
            CadRoles = roles.Count > 0 ? roles : null,
            PdfDpi = double.IsFinite(PdfDpi) ? Math.Clamp(PdfDpi, 36, 600) : defaults.PdfDpi
        };
    }

    public bool SameAs(ImportSettings other) => this with { CadRoles = null } == other with { CadRoles = null }
        && (CadRoles ?? new Dictionary<string, DrawingRole>()).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual((other.CadRoles ?? new Dictionary<string, DrawingRole>()).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
}

// Per-user import settings in %LOCALAPPDATA%\Morupixel, never in the repository or a release package.
public static class ImportSettingsStore
{
    const int MaxStoreBytes = 256 * 1024;
    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "import-settings.json");

    public static ImportSettings Load(string? store = null)
    {
        try
        {
            var file = new FileInfo(store ?? DefaultStorePath);
            if (!file.Exists || file.Length > MaxStoreBytes) return new();
            return (JsonSerializer.Deserialize<ImportSettings>(File.ReadAllText(file.FullName), json) ?? new()).Sanitized();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { return new(); }
    }

    public static ImportSettings Save(ImportSettings settings, string? store = null)
    {
        var clean = settings.Sanitized();
        try
        {
            string path = store ?? DefaultStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(clean, json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return clean;
    }
}
