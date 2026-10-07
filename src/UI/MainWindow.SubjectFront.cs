using System.Windows.Controls;
using System.Windows.Input;

namespace Compositor.Windows;

// 피사체를 글자 앞으로 (menu, Ctrl+K, layer right-click menu and the photo/design quick actions):
// the photo's subject is cut out on this computer and a copy of the photo with that mask is placed
// directly above the title text, as one undo step.
public sealed partial class MainWindow
{
    // The cut-out step: the bundled local subject model. Self-tests and offscreen previews put a
    // small synthetic mask here so they never wait for the model on a large image.
    internal Func<Raster, CancellationToken, byte[]> subjectCutOut = (pixels, token) => BackgroundRemoval.CreateMask(pixels, cancellationToken: token);
    // The last run started from a menu, button or command, for the self-tests.
    internal Task<bool>? subjectFrontTask;
    const string SubjectFrontTip = "사진의 피사체를 오려 텍스트 레이어 바로 위에 올립니다 · 사진과 텍스트 레이어를 함께 선택하거나 둘 중 하나만 선택";

    void PlaceSubjectInFront() => subjectFrontTask = PlaceSubjectInFrontAsync();

    // True when the cut-out was placed. A selection that does not fit, a cancel (Esc) or a document
    // changed while the subject is found leave the document and its history untouched.
    internal async Task<bool> PlaceSubjectInFrontAsync()
    {
        if (!HasDocument) return false;
        CommitFocusedInspectorField();
        if (SubjectFront.Resolve(doc, ExportSelectionIds(), out var problem) is not { } pair) { SubjectFrontNotice(problem!); return false; }
        if (Parents(doc, pair.Title).Any(parent => parent.Locked)) { SubjectFrontNotice("텍스트 레이어가 들어 있는 그룹의 잠금을 먼저 해제하세요."); return false; }
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var pixels = pair.Photo.Pixels; var cutOut = subjectCutOut;
        Guid photoId = pair.Photo.Id, titleId = pair.Title.Id;
        string name = Loc.Format("{0} 피사체", pair.Photo.Name.Length > 200 ? pair.Photo.Name[..200] : pair.Photo.Name);
        status.Text = "피사체를 오려 내는 중…  U²-NetP · 이 컴퓨터에서 처리 · Esc: 취소";
        try
        {
            var subject = await Task.Run(() => cutOut(pixels, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { if (ReferenceEquals(document, doc)) status.Text = "피사체를 글자 앞으로 옮기기를 취소했습니다."; return false; }
            if (!ReferenceEquals(document, doc) || revision != doc.Revision)
            {
                if (ReferenceEquals(document, doc)) status.Text = "피사체를 찾는 동안 문서가 바뀌어 적용하지 않았습니다. 다시 실행하세요.";
                return false;
            }
            Layer? created = null;
            Edit("피사체를 글자 앞으로", () =>
            {
                created = SubjectFront.Apply(doc, photoId, titleId, subject, name);
                selectedLayers.Clear(); selectedLayers.Add(created.Id); maskEditing = false; sourceLayerSelection = null;
            });
            if (created == null || !doc.Layers.Any(l => l.Id == created.Id)) return false;
            status.Text = "피사체를 글자 앞에 놓았습니다 · 마스크 브러시로 가장자리를 다듬을 수 있습니다.";
            return true;
        }
        catch (OperationCanceledException) { if (ReferenceEquals(document, doc)) status.Text = "피사체를 글자 앞으로 옮기기를 취소했습니다."; return false; }
        catch (Exception error) { if (headlessTesting) throw; if (ReferenceEquals(document, doc)) MessageDialog.Show(this, error.Message, "피사체를 글자 앞으로"); return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    void SubjectFrontNotice(string message)
    {
        status.Text = message;
        if (!headlessTesting) MessageDialog.Show(this, message, "피사체를 글자 앞으로", NoticeKind.Information);
    }

    // Right-click on a layer row: an unselected row becomes the selection first, so the commands act on
    // what was clicked; a row that is part of a multi-selection keeps the whole selection.
    void AttachLayerMenu(LayerRow row, LayerListEntry entry)
    {
        row.PreviewMouseRightButtonDown += (_, _) => { if (!EntryIds(entry).Any(selectedLayers.Contains)) Guard(() => ClickLayerRow(entry, ModifierKeys.None)); };
        row.ContextMenu = BuildLayerMenu(SketchLayers.IsPhoto(entry.Layer) ? entry.Layer.Id : null);
    }

    // A photo row also starts with 스케치 사진 정리…, which first selects the clicked photo so it
    // cleans that photo even when the menu was opened without changing the selection.
    internal ContextMenu BuildLayerMenu(Guid? photoId = null)
    {
        var menu = new ContextMenu();
        void Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => { if (HasDocument) Guard(action); };
            menu.Items.Add(item);
        }
        if (photoId is { } id)
        {
            Item("스케치 사진 정리…", "", () => { if (doc.Layers.Any(l => l.Id == id)) { SelectLayer(id); CleanSketchPhoto(); } });
            menu.Items.Add(new Separator());
        }
        Item("레이어 복제", "Ctrl+J", Duplicate);
        Item("이름 변경…", "", Rename);
        menu.Items.Add(new Separator());
        Item("피사체를 글자 앞으로", "", PlaceSubjectInFront);
        menu.Items.Add(new Separator());
        Item("레이어 삭제", "", DeleteLayer);
        return menu;
    }
}
