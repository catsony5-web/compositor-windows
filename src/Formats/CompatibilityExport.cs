using System.IO;

namespace Compositor.Windows;

/// <summary>File types offered by "PDF · PSD · AI로 내보내기". One list feeds the menu, the dialog and the docs.</summary>
public enum CompatibilityExportFormat { PdfSingle, PdfLayers, PsdLayers, PsdSingle, AiLayers }

public sealed record CompatibilityExportChoice(CompatibilityExportFormat Format, string Title, string Description, string Extension, string FileType);

/// <summary>What an export keeps, changes or cannot do, as sentences for the dialog.</summary>
public sealed record CompatibilityExportReport(IReadOnlyList<string> Lines, string? Problem);

public static class CompatibilityExport
{
    public static IReadOnlyList<CompatibilityExportChoice> Choices { get; } =
    [
        new(CompatibilityExportFormat.PdfSingle, "PDF · 한 장으로 합치기 (인쇄·공유용)", "모든 레이어를 한 페이지에 합쳐, 어디서 열어도 같은 모습으로 보입니다.", ".pdf", "PDF 문서"),
        new(CompatibilityExportFormat.PdfLayers, "PDF · 레이어 나누기 (레이어별 켜고 끄기)", "맨 위 레이어와 그룹마다 PDF 레이어를 만들어, 여는 앱에서 하나씩 켜고 끌 수 있습니다.", ".pdf", "PDF 문서"),
        new(CompatibilityExportFormat.PsdLayers, ".psd · 레이어 유지", "레이어와 그룹, 이름·순서·마스크·불투명도·혼합 모드를 그대로 저장합니다.", ".psd", "PSD 이미지"),
        new(CompatibilityExportFormat.PsdSingle, ".psd · 한 장으로 합치기", "현재 모습을 한 장의 이미지로 저장합니다. 레이어가 너무 많을 때 사용하세요.", ".psd", "PSD 이미지"),
        new(CompatibilityExportFormat.AiLayers, ".ai · 레이어 유지 (PDF 호환)", "PDF 호환 .ai 파일입니다. 레이어를 나누고 선과 문자는 벡터로 유지합니다.", ".ai", "AI 파일 (PDF 호환)")
    ];

    public static CompatibilityExportChoice Choice(CompatibilityExportFormat format) => Choices.Single(c => c.Format == format);

    /// <summary>Save-dialog filter, e.g. "PSD 이미지|*.psd".</summary>
    public static string Filter(CompatibilityExportFormat format, Func<string, string>? translate = null)
    {
        var choice = Choice(format); return $"{(translate ?? (s => s))(choice.FileType)}|*{choice.Extension}";
    }

    /// <summary>A safe default file name with the extension of the chosen format.</summary>
    public static string FileName(string documentName, CompatibilityExportFormat format)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string(documentName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Morupixel";
        if (name.Length > 120) name = name[..120];
        return name + Choice(format).Extension;
    }

    /// <summary>Vectors are worth keeping in a single-page PDF when the document has text, shapes, drawings or materials.</summary>
    public static bool SuggestVectors(Document document) => DesignRenderer.HasRetainedContent(document);

    public static void Write(Document document, CompatibilityExportFormat format, Stream output, bool keepVectors = true,
        Action<int, int>? progress = null, CancellationToken token = default)
    {
        switch (format)
        {
            case CompatibilityExportFormat.PdfSingle:
                if (keepVectors) PdfLayerExport.Write(document, output, false, progress, token); else PdfCompatibility.Write(document, output, token);
                break;
            case CompatibilityExportFormat.PdfLayers:
            case CompatibilityExportFormat.AiLayers: PdfLayerExport.Write(document, output, true, progress, token); break;
            case CompatibilityExportFormat.PsdLayers: PsdLayerExport.Write(document, output, progress, token); break;
            case CompatibilityExportFormat.PsdSingle: PsdCompatibility.Write(document, output, false, token); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    static string Parts(int vectors, int images) => (vectors, images) switch
    {
        (0, 0) => "보이는 내용이 없는 빈 페이지입니다.",
        (_, 0) => $"선·문자·도형 {vectors:N0}개를 모두 벡터로 담습니다.",
        (0, _) => $"사진과 효과가 있는 부분 {images:N0}개를 이미지로 담습니다.",
        _ => $"선·문자·도형 {vectors:N0}개는 벡터로, 사진과 효과가 있는 부분 {images:N0}개는 이미지로 담습니다."
    };

    public static CompatibilityExportReport Describe(Document document, CompatibilityExportFormat format, bool keepVectors = true)
    {
        var lines = new List<string>();
        try
        {
            switch (format)
            {
                case CompatibilityExportFormat.PdfSingle:
                    double width = document.Width * 25.4 / document.Dpi, height = document.Height * 25.4 / document.Dpi;
                    lines.Add($"용지 {width:0.#} × {height:0.#} mm · {document.Dpi:0.##} DPI 기준 한 페이지입니다.");
                    if (keepVectors)
                    {
                        var plan = PdfLayerExport.Build(document, false);
                        lines.Add(Parts(plan.VectorParts, plan.ImageParts));
                    }
                    else lines.Add("현재 모습을 한 장의 이미지로 담습니다. 확대하면 픽셀이 보일 수 있습니다.");
                    lines.Add("숨긴 레이어는 포함하지 않습니다.");
                    break;
                case CompatibilityExportFormat.PdfLayers:
                case CompatibilityExportFormat.AiLayers:
                {
                    var plan = PdfLayerExport.Build(document, true);
                    lines.Add($"PDF 레이어 {plan.Units.Count:N0}개를 만듭니다. 맨 위 레이어와 그룹마다 하나씩입니다.");
                    if (plan.Expanded) lines.Add("도면 파일 묶음은 안에 있는 도면 레이어별로 나눕니다.");
                    lines.Add(Parts(plan.VectorParts, plan.ImageParts));
                    if (plan.HiddenLayers > 0) lines.Add($"숨긴 레이어 {plan.HiddenLayers:N0}개는 꺼진 레이어로 저장되어 여는 앱에서 다시 켤 수 있습니다.");
                    if (plan.OmitsHiddenChildren) lines.Add("그룹 안에서 숨긴 레이어는 포함하지 않습니다.");
                    if (format == CompatibilityExportFormat.AiLayers) lines.Add("PDF 부분만 담은 .ai 파일입니다. 여는 앱에 따라 레이어 표시 방식이 다를 수 있습니다.");
                    break;
                }
                case CompatibilityExportFormat.PsdLayers:
                {
                    var plan = PsdLayerExport.Build(document);
                    lines.Add($"레이어 {plan.Layers:N0}개와 그룹 {plan.Folders:N0}개를 나눠 저장합니다.");
                    lines.Add("이름·순서·표시·불투명도·혼합 모드·마스크·클리핑을 유지합니다.");
                    if (plan.Converted > 0) lines.Add($"문자·도형·벡터 레이어 {plan.Converted:N0}개는 보이는 모습 그대로 픽셀 레이어로 바뀝니다.");
                    if (plan.Adjustments > 0) lines.Add($"조정 레이어 {plan.Adjustments:N0}개는 아래 레이어에 적용한 결과를 픽셀로 담습니다.");
                    if (plan.Perspective > 0) lines.Add($"원근 변형이 있는 그룹 {plan.Perspective:N0}개는 한 장의 레이어로 합칩니다.");
                    if (plan.Collapsed > 0) lines.Add($"레이어가 {Document.MaxLayers}개를 넘지 않도록 그룹 {plan.Collapsed:N0}개를 각각 한 장의 레이어로 합칩니다.");
                    break;
                }
                case CompatibilityExportFormat.PsdSingle:
                    if (document.Width > 30_000 || document.Height > 30_000) throw new InvalidDataException("PSD 내보내기는 한 변 30,000px까지 지원합니다. 더 큰 이미지는 PNG 또는 TIFF로 저장해 주세요.");
                    lines.Add("현재 모습을 픽셀 레이어 하나와 합성 이미지로 저장합니다.");
                    break;
            }
            lines.Add("RGB로 저장합니다. 편집할 수 있는 원본은 .moruproj로도 보관하세요.");
            return new CompatibilityExportReport(lines, null);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            return new CompatibilityExportReport(lines, e.Message);
        }
    }
}
