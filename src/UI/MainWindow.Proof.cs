using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    bool cmykProof;
    string? proofProfile;
    // The RGB / CMYK toggle is in the status bar (MainWindow.StatusBar.cs).
    void SetProof(bool enabled)
    {
        if (enabled == cmykProof) return;
        if (enabled) _ = CmykExport.ProfileName(proofProfile);
        CancelGesture(); ClearTextMovePreview(); cmykProof = enabled; UpdateProofButtons(); UpdateDocumentInfo();
        if (!enabled && composite != null) { canvas.Composite = composite.Bitmap(); canvas.InvalidateVisual(); }
        QueueRender(); status.Text = enabled ? "CMYK 인쇄색을 준비합니다 · RGB 원본과 레이어는 유지됩니다" : "RGB 편집 화면";
    }
    void ChooseProofProfile()
    {
        var open = new OpenFileDialog { Title = "CMYK 인쇄색 미리보기 프로필", Filter = "ICC 프로필|*.icc;*.icm" };
        if (open.ShowDialog(this) != true) return;
        _ = CmykExport.ProfileName(open.FileName); proofProfile = open.FileName;
        UpdateProofButtons(); if (!cmykProof) SetProof(true); else QueueRender();
    }
    void ExportCmyk() => new CmykExportDialog(this, doc, proofProfile).ShowDialog();
}
