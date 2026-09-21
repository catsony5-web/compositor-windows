using System.Windows;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Bound by the bucket options in the main toolbar.
    double bucketTolerance = 32;
    bool bucketContiguous = true;
    bool bucketSampleMerged = true;

    async void PaintBucket(Point point) => await PaintBucketAsync(point);

    internal async Task<bool> PaintBucketAsync(Point point)
    {
        if (doc.Active is not { } active) return false;
        if (maskEditing) { status.Text = "이미지 편집으로 전환한 뒤 채우기 도구를 사용하세요."; return false; }
        if (active.Kind != LayerKind.Raster) { status.Text = "픽셀 레이어를 선택하세요."; return false; }
        if (IsLockedWithParents(active)) { status.Text = "레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return false; }
        if (!active.Visible || Parents(doc, active).Any(parent => !parent.Visible))
        { status.Text = "숨겨진 레이어입니다. 레이어와 부모 그룹을 표시한 뒤 편집하세요."; return false; }
        if (point.X < 0 || point.Y < 0 || point.X >= doc.Width || point.Y >= doc.Height) return false;

        CancelGesture(); jobCts?.Cancel();
        var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var selected = selection;
        var historyAtStart = history; int tabAtStart = activeTab;
        var snapshot = doc.Snapshot(); Guid layerId = active.Id;
        var color = foreground; double tolerance = bucketTolerance, opacity = brushOpacity;
        bool contiguous = bucketContiguous, merged = bucketSampleMerged;
        status.Text = "채우는 중… Esc: 취소";
        try
        {
            var result = await Task.Run(() =>
            {
                var sample = merged ? Imaging.Render(snapshot, cts.Token) : FillTools.RenderLayerSample(snapshot, layerId, cts.Token);
                return FillTools.Fill(snapshot, layerId, selected, sample, point, color, tolerance, contiguous, opacity, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(document, doc) || revision != doc.Revision ||
                !ReferenceEquals(selected, selection) || !ReferenceEquals(historyAtStart, history) ||
                tabAtStart != activeTab || doc.ActiveId != layerId) return false;
            if (result == null) { status.Text = "채울 픽셀이 없습니다."; return false; }
            Edit("페인트 통", () => doc.Layers.Single(l => l.Id == layerId).Pixels = result);
            return true;
        }
        catch (OperationCanceledException) { if (ReferenceEquals(document, doc)) status.Text = "채우기를 취소했습니다."; return false; }
        catch (Exception error) { if (headlessTesting) throw; if (ReferenceEquals(document, doc)) status.Text = "채우기 실패: " + error.Message; return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
}
