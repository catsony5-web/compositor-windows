using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    bool cmykProof;
    string? proofProfile;
    GlassSwitch? proofSwitch;
    FrameworkElement BuildProofSwitch()
    {
        proofSwitch = new GlassSwitch("RGB", "CMYK 보기", 164) { Margin = new Thickness(4, 0, 8, 0) };
        proofSwitch.Click += (_, _) => Guard(() => { try { SetProof(proofSwitch.IsChecked == true); } finally { UpdateProofButtons(); } });
        UpdateProofButtons(); return proofSwitch;
    }
    void UpdateProofButtons()
    {
        if (proofSwitch == null) return;
        proofSwitch.IsChecked = cmykProof;
        proofSwitch.ToolTip = "RGB 원본을 유지하며 인쇄색 확인 · " + (proofProfile ?? "Windows 기본 CMYK 프로필") + " · Ctrl+Shift+Y\nCMYK 파일 저장: 파일 → 인쇄용 CMYK 내보내기";
    }
    void SetProof(bool enabled)
    {
        if (enabled == cmykProof) return;
        if (enabled) _ = CmykExport.ProfileName(proofProfile);
        CancelGesture(); ClearTextMovePreview(); cmykProof = enabled; UpdateProofButtons();
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
