using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    Menu BuildMenu()
    {
        var menu = new Menu { Background = Theme.Header, Foreground = Theme.Text, Padding = new Thickness(12, 0, 0, 0) };
        static bool RequiresDocument(string section, string label) => section switch
        {
            "파일" => label is not ("새 캔버스…" or "열기…" or "레이어로 가져오기…" or "Compositor .comp 가져오기…"),
            "편집" => label is not ("이미지 붙여넣기" or "버킷 채우기 도구"),
            "보기" => label is not ("스냅 전환" or "픽셀 격자 전환" or "도움말 / 지원 범위"),
            "배우기" => false,
            _ => true
        };
        void Add(string name, params (string Label, string Shortcut, Action Run)[] actions)
        {
            var top = new MenuItem { Header = name, Foreground = Theme.Text };
            foreach (var a in actions)
            {
                bool requiresDocument = RequiresDocument(name, a.Label);
                var item = new MenuItem { Header = a.Label, InputGestureText = a.Shortcut, Foreground = Theme.Text };
                if (requiresDocument) DocumentControl(item);
                item.Click += (_, _) => { if (!requiresDocument || HasDocument) Guard(a.Run); }; top.Items.Add(item);
            }
            if (actions.All(action => RequiresDocument(name, action.Label))) DocumentControl(top);
            menu.Items.Add(top);
        }
        Add("파일", ("새 캔버스…", "Ctrl+N", NewDocument), ("열기…", "Ctrl+O", Open), ("레이어로 가져오기…", "Ctrl+Shift+O", Import), ("저장", "Ctrl+S", () => Save(false)), ("다른 이름으로 저장…", "Ctrl+Shift+S", () => Save(true)), ("내보내기 미리보기…", "Ctrl+Shift+E", Export), ("Compositor .comp 가져오기…", "", ImportCompositor), ("Compositor .comp 내보내기…", "", ExportCompositor), ("현재 문서 닫기", "Ctrl+W", CloseTab));
        Add("편집", ("실행 취소", "Ctrl+Z", Undo), ("다시 실행", "Ctrl+Shift+Z", Redo), ("합성 이미지 복사", "Ctrl+C", CopyMerged), ("이미지 붙여넣기", "Ctrl+V", Paste), ("선택 픽셀 지우기", "Delete", ClearPixels), ("전경색으로 채우기", "Alt+Delete", Fill), ("배경색으로 채우기", "Ctrl+Delete", FillBackground), ("버킷 채우기 도구", "G", () => SetTool(Tool.Bucket)));
        Add("이미지", ("캔버스 크기…", "", CanvasSize), ("이미지 크기…", "", ImageSize), ("선택 영역으로 자르기", "", CropSelection));
        Add("레이어", ("레이어 복제", "Ctrl+J", Duplicate), ("이름 변경…", "", Rename), ("변형 값 입력…", "Ctrl+T", Transform), ("가로 뒤집기", "", () => EditLayer("가로 뒤집기", l => l.FlipX = !l.FlipX)), ("세로 뒤집기", "", () => EditLayer("세로 뒤집기", l => l.FlipY = !l.FlipY)), ("마스크 추가", "", AddMask), ("마스크 반전", "", InvertMask), ("마스크 제거", "", () => EditLayer("마스크 제거", l => { l.Mask = null; maskEditing = false; })), ("모든 레이어 병합", "", Flatten), ("레이어 삭제", "", DeleteLayer));
        Add("선택", ("전체 선택", "Ctrl+A", () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); }), ("선택 해제", "Ctrl+D", () => { selection = null; Refresh(false); }), ("선택 반전", "Ctrl+Shift+I", InvertSelection), ("페더…", "", () => ModifySelection("feather")), ("확장…", "", () => ModifySelection("expand")), ("축소…", "", () => ModifySelection("contract")), ("레이어의 불투명 픽셀 선택", "", SelectAlpha), ("선택 픽셀을 새 레이어로", "", ExtractSelection), ("선택 윤곽 이동…", "", MoveSelectionOutline));
        Add("보정", ("레벨…", "Ctrl+L", Levels), ("노출…", "", Exposure), ("채도…", "", Saturation), ("흑백", "", () => Adjust("grayscale")), ("색상 반전", "Ctrl+I", () => Adjust("invert")), ("가우시안 흐림…", "", Blur));
        Add("합성", ("선택 레이어 그룹화", "Ctrl+G", GroupSelected), ("그룹 해제", "Ctrl+Shift+G", UngroupSelected), ("그룹으로 이동…", "", MoveToGroup), ("클리핑 마스크 전환", "Ctrl+Alt+G", ToggleClipping), ("아래 레이어와 병합", "Ctrl+E", MergeDown), ("텍스트 내용·서식 편집…", "", EditText), ("픽셀 레이어로 변환", "", RasterizeActive), ("다른 문서로 레이어 복사…", "", CopyLayerToTab));
        Add("조정 레이어", ("레벨…", "", () => ShowAdjustment(AdjustmentKind.Levels)), ("곡선…", "", () => ShowAdjustment(AdjustmentKind.Curves)), ("색조 / 채도…", "Ctrl+U", () => ShowAdjustment(AdjustmentKind.HueSaturation)), ("노출…", "", () => ShowAdjustment(AdjustmentKind.Exposure)), ("그라데이션 맵…", "", () => ShowAdjustment(AdjustmentKind.GradientMap)), ("그레인…", "", () => ShowAdjustment(AdjustmentKind.Grain)), ("선택 조정 레이어 편집…", "", EditAdjustment));
        Add("필터", ("사진 현상…", "Ctrl+Shift+A", () => ShowAdjustment(AdjustmentKind.PhotoDevelop)), ("모션 블러…", "", MotionBlur), ("노이즈…", "", Noise), ("렌즈 왜곡 보정…", "", Lens), ("내용 인식 채우기", "Shift+F5", ContentFill), ("배경색 제거…", "", RemoveColorBackground), ("AI 피사체 배경 제거…", "", RemoveAiBackground), ("마스크 페더…", "", FeatherMask));
        Add("인쇄", ("ICC / CMYK 내보내기…", "", ExportCmyk));
        Add("보기", ("화면에 맞춤", "Ctrl+0", () => { canvas.Fit(); UpdateStatus(); }), ("실제 크기", "Ctrl+1", () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); }), ("가이드 추가…", "", AddGuide), ("가이드 지우기", "", () => { canvas.Guides.Clear(); canvas.InvalidateVisual(); }), ("스냅 전환", "", () => { snapping = !snapping; status.Text = snapping ? "스냅 켜짐" : "스냅 꺼짐"; }), ("픽셀 격자 전환", "", () => { canvas.PixelGrid = !canvas.PixelGrid; canvas.InvalidateVisual(); }), ("도움말 / 지원 범위", "F1", Help));
        Add("배우기", ("샘플 작업 열기", "", OpenLearningSample));
        MenuItem Find(string name) => menu.Items.Cast<MenuItem>().Single(item => Equals(item.Header, name));
        var layerMenu = Find("레이어");
        var compatibilityExport = DocumentControl(new MenuItem { Header = "PDF / Photoshop 파일로 내보내기…" });
        compatibilityExport.Click += (_, _) => { if (HasDocument) Guard(ExportCompatibility); }; Find("파일").Items.Add(compatibilityExport);
        foreach (var target in new[] { Find("파일"), layerMenu })
        {
            var selectedExport = DocumentControl(new MenuItem { Header = "선택 레이어 이미지로 내보내기…" });
            selectedExport.Click += (_, _) => { if (HasDocument) Guard(ExportSelectedLayers); }; target.Items.Add(selectedExport);
        }
        var compositeMenu = Find("합성"); menu.Items.Remove(compositeMenu);
        layerMenu.Items.Add(new Separator());
        while (compositeMenu.Items.Count > 0) { var item = compositeMenu.Items[0]; compositeMenu.Items.RemoveAt(0); layerMenu.Items.Add(item); }
        var adjustments = Find("조정 레이어"); menu.Items.Remove(adjustments); adjustments.Header = "새 조정 레이어";
        layerMenu.Items.Add(new Separator()); layerMenu.Items.Add(adjustments);
        var print = Find("인쇄"); menu.Items.Remove(print);
        var printItem = (MenuItem)print.Items[0]; print.Items.RemoveAt(0); printItem.Header = "인쇄용 CMYK 내보내기…";
        Find("파일").Items.Insert(6, printItem);
        foreach (var (section, before) in new[] { ("파일", "저장"), ("파일", "내보내기 미리보기…"), ("파일", "Compositor .comp 가져오기…"), ("파일", "현재 문서 닫기"), ("편집", "합성 이미지 복사"), ("편집", "선택 픽셀 지우기"), ("레이어", "마스크 추가"), ("보기", "가이드 추가…"), ("보기", "도움말 / 지원 범위") })
        {
            var parent = Find(section); var item = parent.Items.OfType<MenuItem>().First(x => Equals(x.Header, before));
            parent.Items.Insert(parent.Items.IndexOf(item), new Separator());
        }
        var view = Find("보기");
        foreach (var (label, action, requiresDocument) in new (string, Action, bool)[] {
            ("사진 편집 작업 공간", () => SetWorkspaceMode(false), false), ("디자인 작업 공간", () => SetWorkspaceMode(true), false),
            ("RGB / CMYK 미리보기 전환", () => SetProof(!cmykProof), true), ("CMYK ICC 프로필 선택…", ChooseProofProfile, true),
            ("Windows 기본 CMYK 프로필", () => { proofProfile = null; UpdateProofButtons(); if (cmykProof) QueueRender(); }, true),
            ("패널 배치 초기화", ResetPanelLayout, false) })
        {
            var item = new MenuItem { Header = label }; if (requiresDocument) DocumentControl(item);
            item.Click += (_, _) => { if (!requiresDocument || HasDocument) Guard(action); }; view.Items.Add(item);
        }
        return menu;
    }
}
