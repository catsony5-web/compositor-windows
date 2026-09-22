using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    Guid selectedArtboard;
    Artboard? artboardStart;
    int artboardHandle = -1;
    bool addingArtboard;
    readonly StackPanel artboardOptions = new() { Orientation = Orientation.Horizontal };
    Artboard CurrentArtboard => ArtboardEditing.Visible(doc).FirstOrDefault(b => b.Id == selectedArtboard) ?? ArtboardEditing.Visible(doc)[0];

    void BuildArtboardOptions()
    {
        artboardOptions.Children.Add(Theme.Label("빈 곳 드래그: 만들기 · 모서리: 크기"));
        artboardOptions.Children.Add(Theme.Button("새 대지", AddArtboard));
        artboardOptions.Children.Add(Theme.Button("완료", () => SetTool(Tool.Move)));
    }
    void BeginArtboard(Point point, Point screen, bool forceNew)
    {
        var boards = ArtboardEditing.Visible(doc);
        var selected = CurrentArtboard;
        artboardHandle = forceNew ? -1 : CanvasView.ArtboardHandleAt(selected.Bounds, point, canvas.Zoom);
        var hit = artboardHandle >= 0 ? selected : forceNew ? null : boards.Reverse().FirstOrDefault(b => b.Bounds.Contains(point));
        addingArtboard = hit == null;
        artboardStart = hit ?? new Artboard(Guid.Empty, $"대지 {boards.Count + 1}", Math.Round(point.X), Math.Round(point.Y), 1, 1);
        if (hit != null) selectedArtboard = hit.Id;
        start = point; screenStart = screen; beforeGesture = null; dragging = true;
        canvas.SelectedArtboardId = selectedArtboard; canvas.ArtboardDraft = null;
        BuildProperties(); canvas.CaptureMouse(); canvas.InvalidateVisual();
    }
    void MoveArtboard(Point point, Point screen)
    {
        if (artboardStart is not { } board || (screen - screenStart).Length < 3 && canvas.ArtboardDraft == null) return;
        var delta = point - start; Rect bounds;
        if (addingArtboard) bounds = Between(start, point);
        else if (artboardHandle < 0) bounds = new Rect(board.X + delta.X, board.Y + delta.Y, board.Width, board.Height);
        else
        {
            double left = board.X, top = board.Y, right = board.Bounds.Right, bottom = board.Bounds.Bottom;
            if (artboardHandle is 0 or 6 or 7) left = Math.Min(right - 1, left + delta.X);
            if (artboardHandle is 2 or 3 or 4) right = Math.Max(left + 1, right + delta.X);
            if (artboardHandle is 0 or 1 or 2) top = Math.Min(bottom - 1, top + delta.Y);
            if (artboardHandle is 4 or 5 or 6) bottom = Math.Max(top + 1, bottom + delta.Y);
            bounds = new Rect(new Point(left, top), new Point(right, bottom));
        }
        canvas.ArtboardDraft = board with { X = Math.Round(bounds.X), Y = Math.Round(bounds.Y), Width = Math.Max(1, Math.Round(bounds.Width)), Height = Math.Max(1, Math.Round(bounds.Height)) };
        canvas.ArtboardDraftIsNew = addingArtboard; canvas.InvalidateVisual();
        status.Text = $"대지 {canvas.ArtboardDraft.Width:N0} × {canvas.ArtboardDraft.Height:N0} px";
    }
    void EndArtboard(Point point, Point screen)
    {
        MoveArtboard(point, screen); var board = canvas.ArtboardDraft; bool add = addingArtboard;
        dragging = false; artboardStart = null; canvas.ArtboardDraft = null; canvas.ReleaseMouseCapture();
        if (board != null) ApplyArtboard(board, add); else Refresh(false);
    }
    void ApplyArtboard(Artboard board, bool add = false)
    {
        if (!add && ArtboardEditing.Visible(doc).Any(b => b == board)) return;
        int width = doc.Width, height = doc.Height; var oldPan = canvas.Pan;
        Edit(add ? "대지 만들기" : "대지 수정", () =>
        {
            var result = ArtboardEditing.Set(doc, board, add); selectedArtboard = result.Id;
            canvas.Pan = oldPan + new Vector((doc.Width - width) / 2.0 - result.Offset.X, (doc.Height - height) / 2.0 - result.Offset.Y) * canvas.Zoom;
        });
    }
    void AddArtboard()
    {
        var current = CurrentArtboard; var bounds = ArtboardEditing.Bounds(doc);
        ApplyArtboard(current with { Name = $"대지 {ArtboardEditing.Visible(doc).Count + 1}", X = bounds.Right + 40, Y = current.Y }, true);
        canvas.Fit();
    }
    void RemoveArtboard()
    {
        if (ArtboardEditing.Visible(doc).Count <= 1) { status.Text = "마지막 대지는 유지됩니다."; return; }
        var id = CurrentArtboard.Id; Edit("대지 삭제", () => ArtboardEditing.Remove(doc, id));
    }
    void NudgeArtboard(Vector delta)
    { var board = CurrentArtboard; ApplyArtboard(board with { X = board.X + delta.X, Y = board.Y + delta.Y }); }

    void BuildArtboardProperties()
    {
        var board = CurrentArtboard; var document = doc; long version = inspectorVersion;
        properties.Children.Add(Theme.Section("대지 편집"));
        var picker = new ComboBox { MinHeight = 34, Margin = new Thickness(2, 4, 2, 8), DisplayMemberPath = "Name", ItemsSource = ArtboardEditing.Visible(doc), SelectedItem = board };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is Artboard chosen) { selectedArtboard = chosen.Id; Refresh(false); } };
        properties.Children.Add(picker);
        TextBox Field(string label, string value, Panel parent)
        {
            var column = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            column.Children.Add(Theme.Label(label, Theme.CaptionSize, Theme.Muted));
            var box = new TextBox { Text = value, MinHeight = 34, Padding = new Thickness(8, 5, 8, 5) };
            System.Windows.Automation.AutomationProperties.SetName(box, "대지 " + label);
            column.Children.Add(box); parent.Children.Add(column); return box;
        }
        string Number(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
        var name = Field("이름", board.Name, properties);
        var coordinates = new UniformGrid { Columns = 2 }; properties.Children.Add(coordinates);
        var x = Field("X", Number(board.X), coordinates); var y = Field("Y", Number(board.Y), coordinates);
        var dimensions = new UniformGrid { Columns = 2 }; properties.Children.Add(dimensions);
        var width = Field("너비 (px)", Number(board.Width), dimensions); var height = Field("높이 (px)", Number(board.Height), dimensions);
        void Apply()
        {
            if (!ReferenceEquals(document, doc) || version != inspectorVersion) return;
            Guard(() => ApplyArtboard(board with { Name = name.Text.Trim(), X = Dialogs.Number(x.Text, -100000, 100000), Y = Dialogs.Number(y.Text, -100000, 100000), Width = Dialogs.Number(width.Text, 1, Raster.MaxDimension), Height = Dialogs.Number(height.Text, 1, Raster.MaxDimension) }));
        }
        foreach (var box in new[] { name, x, y, width, height }) box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        properties.Children.Add(Theme.Button("변경 적용", Apply));
        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 4) };
        buttons.Children.Add(Theme.Button("새 대지", AddArtboard));
        var remove = Theme.Button("대지 삭제", RemoveArtboard); remove.IsEnabled = ArtboardEditing.Visible(doc).Count > 1; buttons.Children.Add(remove); properties.Children.Add(buttons);
        properties.Children.Add(Theme.Button("이 대지 내보내기…", () => ExportDialog.Show(this, ArtboardEditing.ExportDocument(doc, CurrentArtboard.Id))));
        var hint = Theme.Label("대지의 위치와 크기를 바꿉니다.\n안의 객체 위치는 유지됩니다.", Theme.CaptionSize, Theme.Muted); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(2, 10, 2, 4); properties.Children.Add(hint);
    }
}
