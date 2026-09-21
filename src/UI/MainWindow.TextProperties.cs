using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    TextPropertiesPanel? textPropertiesPanel;

    void OpenTextProperties(Layer? layer, Point? point = null)
    {
        CommitFocusedInspectorField();
        if (layer == null)
        {
            Edit("텍스트 추가", () =>
            {
                var spec = new TextSpec { Content = "새 텍스트", FontFamily = "Malgun Gothic", FontSize = 64,
                    ColorArgb = (uint)(foreground.A << 24 | foreground.R << 16 | foreground.G << 8 | foreground.B) };
                doc.Add(DocumentFeatures.CreateText(spec, point?.X ?? 40, point?.Y ?? 40));
                selectedLayers.Clear(); selectedLayers.Add(doc.ActiveId);
            });
        }
        else
        {
            if (IsLockedWithParents(layer)) { status.Text = "잠긴 텍스트 레이어입니다."; return; }
            if (doc.ActiveId != layer.Id) SelectLayer(layer.Id);
            else BuildProperties();
        }
        ShowStudioPage(1);
        var focusPanel = textPropertiesPanel;
        if (!headlessTesting && focusPanel != null) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (ReferenceEquals(textPropertiesPanel, focusPanel)) focusPanel.FocusContent();
        }));
    }

    void AddTextProperties(Layer layer)
    {
        textPropertiesPanel = null;
        if (layer.Text is not { } initial) return;
        var boundDocument = doc; long version = inspectorVersion; Guid layerId = layer.Id;
        string? Apply(TextSpec spec)
        {
            if (!ReferenceEquals(doc, boundDocument) || inspectorVersion != version || doc.ActiveId != layerId)
                return "선택한 레이어가 바뀌었습니다. 현재 속성에서 편집하세요.";
            if (IsLockedWithParents(layer)) return "레이어 또는 부모 그룹의 잠금을 먼저 해제하세요.";
            if (layer.Text == spec) return null;
            // Prepare on a snapshot: invalid dimensions stay inline, and masks,
            // warps, and rasterization are computed once before recording undo.
            Layer prepared;
            try { prepared = layer.Snapshot(); DocumentFeatures.UpdateText(prepared, spec); }
            catch (Exception error) when (error is System.IO.InvalidDataException or ArgumentException) { return error.Message; }
            EditLayer("문자와 단락 속성", active =>
            {
                active.Pixels = prepared.Pixels; active.Mask = prepared.Mask; active.Text = prepared.Text;
                active.X = prepared.X; active.Y = prepared.Y;
            });
            return null;
        }
        var panel = new TextPropertiesPanel(initial, Apply, color => Dialogs.ColorPicker(this, color), direction => AlignTextToCanvas(layerId, boundDocument, direction));
        panel.IsEnabled = !IsLockedWithParents(layer);
        Action pending = panel.CommitPending;
        panel.EditingStarted += () => pendingInspectorCommit = pending;
        properties.Children.Add(panel); textPropertiesPanel = panel;
    }

    void AlignTextToCanvas(Guid id, Document boundDocument, string direction)
    {
        if (!ReferenceEquals(doc, boundDocument) || doc.ActiveId != id || doc.Active is not { Kind: LayerKind.Text } layer || IsLockedWithParents(layer)) return;
        var points = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }
            .Select(point => DocumentFeatures.ToDocumentSpace(doc, layer, point)).ToArray();
        double left = points.Min(point => point.X), right = points.Max(point => point.X), top = points.Min(point => point.Y), bottom = points.Max(point => point.Y);
        double dx = direction switch { "left" => -left, "center" => (doc.Width - left - right) / 2, "right" => doc.Width - right, _ => 0 };
        double dy = direction switch { "top" => -top, "middle" => (doc.Height - top - bottom) / 2, "bottom" => doc.Height - bottom, _ => 0 };
        // A projective ancestor changes local shape when translated: do not claim
        // exact edge alignment for this case. Affine groups are handled below.
        var parentId = layer.ParentId;
        while (parentId is { } ancestor)
        {
            var group = doc.Layers.Single(item => item.Id == ancestor);
            if (group.Warp != null) { status.Text = "왜곡된 그룹의 텍스트는 그룹 밖으로 이동한 후 캔버스에 정렬하세요."; return; }
            parentId = group.ParentId;
        }
        var start = DocumentFeatures.ToParentSpace(doc, layer, new Point()); var end = DocumentFeatures.ToParentSpace(doc, layer, new Point(dx, dy));
        if ((end - start).LengthSquared < 1e-12) return;
        EditLayer("텍스트 캔버스 정렬", active => { active.X += end.X - start.X; active.Y += end.Y - start.Y; });
    }
}
