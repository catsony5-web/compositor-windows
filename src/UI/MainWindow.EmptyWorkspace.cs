using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly List<UIElement> documentControls = [];
    FrameworkElement? emptyWorkspace;
    bool startupInitialized;
    bool HasDocument => tabs.Count > 0;

    T DocumentControl<T>(T control) where T : UIElement
    {
        documentControls.Add(control); control.IsEnabled = HasDocument; return control;
    }

    FrameworkElement BuildEmptyWorkspace()
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var (label, action) in new (string, Action)[] { ("새 문서", NewDocument), ("열기", Open), ("배우기", OpenLearningSample) })
        {
            var button = Theme.Button(label, () => Guard(action));
            button.MinWidth = 94; button.MinHeight = 38; button.Margin = new Thickness(4);
            actions.Children.Add(button);
        }
        return actions;
    }

    void UpdateDocumentAvailability()
    {
        bool opened = HasDocument;
        foreach (var control in documentControls) control.IsEnabled = opened;
        canvas.IsEnabled = opened;
        if (emptyWorkspace != null) emptyWorkspace.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
        studioContents[0].IsEnabled = opened;
        properties.IsEnabled = opened;
        if (histogramCard != null) histogramCard.Visibility = opened && !designWorkspace ? Visibility.Visible : Visibility.Collapsed;
        if (!opened) histogramInfo.Text = "";
    }

    void InitializeStartup()
    {
        if (startupInitialized) return;
        startupInitialized = true;
        UpdateColor(); UpdateBrushLabel(); Refresh(); SetTool(Tool.Move);
        if (startupPath != null && (File.Exists(startupPath) || Directory.Exists(startupPath))) Guard(() => OpenPath(startupPath));
    }

    void OpenLearningSample()
    {
        if (tabs.Count >= 8) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        var sample = Demo.Create(); sample.Name = "배우기 · " + sample.Name;
        AddTab(sample, null);
    }

    void EnterEmptyWorkspace()
    {
        CancelGesture(); jobCts?.Cancel(); jobCts = null;
        ++renderGeneration; renderCts?.Cancel();
        pendingFullRender = false; pendingGestureRender = false;
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        ClearTextMovePreview();
        activeTab = -1;
        doc = new Document { Width = 1, Height = 1 }; history = new History(); history.Reset(doc);
        projectPath = null; selection = null; maskEditing = false; pendingInspectorCommit = null;
        selectedLayers.Clear(); collapsedGroups.Clear();
        cloneSource = null; cloneAnchorDocument = null; cloneAnchorLayer = Guid.Empty; cloneAnchorLocal = null;
        canvas.Document = null; canvas.Composite = null; canvas.Selection = null; composite = null;
        canvas.Guides.Clear(); canvas.BrushPoint = null; canvas.Pan = new(); canvas.Zoom = 1;
        histogram.Update(new Raster(1, 1)); histogramInfo.Text = "";
        Refresh(false);
    }
}
