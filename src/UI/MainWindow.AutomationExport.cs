using System.IO;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

// export_document: PDF, .psd and PDF-compatible .ai over the AI connection. The file is written by
// CompatibilityExport.Write, the same call the "PDF · PSD · AI로 내보내기" dialog makes, so an AI
// export and a dialog export of the same document are the same file.
public sealed partial class MainWindow
{
    internal static CompatibilityExportFormat AutomationExportFormat(string format, string layers) => (format, layers) switch
    {
        ("pdf", "flatten") => CompatibilityExportFormat.PdfSingle,
        ("pdf", _) => CompatibilityExportFormat.PdfLayers,
        ("psd", "flatten") => CompatibilityExportFormat.PsdSingle,
        ("psd", _) => CompatibilityExportFormat.PsdLayers,
        ("ai", "keep") => CompatibilityExportFormat.AiLayers,
        _ => throw new AutomationFault("invalid_arguments", ".ai 내보내기는 레이어를 유지합니다. 한 장으로 합치려면 format=pdf, layers=flatten을 사용하세요.")
    };

    async Task<JsonObject> AutomationExportDocumentAsync(JsonObject args, CancellationToken token)
    {
        var tab = AutomationTab(args, true); var candidate = doc.Snapshot();
        void Recheck() { RequireAutomationIdle(token); _ = AutomationTab(args, true); }
        string path = AutomationPath(args), formatName = AString(args, "format"), layers = AString(args, "layers", "keep");
        var format = AutomationExportFormat(formatName, layers); var choice = CompatibilityExport.Choice(format);
        if (!string.Equals(Path.GetExtension(path), choice.Extension, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"format={formatName}에는 {choice.Extension} 경로를 지정하세요.");
        bool overwrite = ABool(args, "overwrite");
        if (File.Exists(path) && !overwrite) throw new AutomationFault("file_exists", "파일이 이미 있습니다. 새 경로를 사용하거나 overwrite=true를 지정하세요.");
        Guid? board = args.ContainsKey("artboardId") ? Guid.Parse(AString(args, "artboardId")) : null;
        if (board.HasValue && !ArtboardEditing.Visible(candidate).Any(b => b.Id == board.Value && b.Id != Guid.Empty))
            throw new AutomationFault("artboard_not_found", "대지를 찾을 수 없습니다. get_state의 artboardId를 사용하세요.");
        var target = board.HasValue ? ArtboardEditing.ExportDocument(candidate, board.Value) : candidate;
        // The dialog's default for its vector checkbox; only the single-page PDF reads it.
        bool vectors = ABool(args, "vectors", CompatibilityExport.SuggestVectors(target));
        var report = CompatibilityExport.Describe(target, format, vectors);
        if (report.Problem != null) throw new AutomationFault("export_limit", report.Problem);
        int pdfLayers = format is CompatibilityExportFormat.PdfLayers or CompatibilityExportFormat.AiLayers ? PdfLayerExport.Build(target, true).Units.Count : 0;
        var psd = format == CompatibilityExportFormat.PsdLayers ? PsdLayerExport.Build(target) : null;
        string staging = Path.Combine(Path.GetDirectoryName(path)!, $".morupixel-{Guid.NewGuid():N}{choice.Extension}");
        try
        {
            await CompatibilityImport.OnSta(() =>
            {
                using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CompatibilityExport.Write(target, format, stream, vectors, null, token);
                stream.Flush(true); return true;
            }, token);
            Recheck();
            File.Move(staging, path, overwrite);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        var result = AutomationResult(); result["path"] = path; result["bytes"] = new FileInfo(path).Length;
        result["format"] = formatName; result["layers"] = layers; result["artboardId"] = board?.ToString();
        result["width"] = target.Width; result["height"] = target.Height; result["dpi"] = target.Dpi; result["colorMode"] = "RGB8";
        result["pageCount"] = format == CompatibilityExportFormat.PsdLayers || format == CompatibilityExportFormat.PsdSingle ? 0 : 1;
        result["pdfLayerCount"] = pdfLayers;
        result["psdLayerCount"] = psd?.Layers ?? (format == CompatibilityExportFormat.PsdSingle ? 1 : 0);
        result["psdGroupCount"] = psd?.Folders ?? 0;
        if (format == CompatibilityExportFormat.PdfSingle) result["vectors"] = vectors;
        // The dialog's "저장되는 내용" sentences: what was kept and what became pixels.
        result["notes"] = new JsonArray(report.Lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
        return result;
    }
}
