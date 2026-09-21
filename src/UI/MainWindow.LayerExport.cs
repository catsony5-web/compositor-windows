using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    void ExportSelectedLayers()
    {
        CommitFocusedInspectorField();
        CancelGesture();
        var ids = ExportSelectionIds();
        if (ids.Length == 0) { status.Text = "내보낼 레이어를 먼저 선택하세요."; return; }
        new SelectedLayerExportDialog(this, doc, ids).ShowDialog();
    }
    Guid[] ExportSelectionIds() => doc.Active == null ? [] : selectedLayers.Contains(doc.ActiveId)
        ? selectedLayers.Where(id => doc.Layers.Any(layer => layer.Id == id)).ToArray() : [doc.ActiveId];
}

internal sealed class SelectedLayerExportDialog : Window
{
    readonly Document snapshot;
    readonly Guid[] selection;
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(14) };
    readonly ComboBox format = new() { ItemsSource = new[] { "PNG · 투명 배경", "JPEG · 흰색 배경", "TIFF · 투명 배경" }, SelectedIndex = 0, Margin = new Thickness(0, 5, 0, 14) };
    readonly ComboBox bounds = new() { ItemsSource = new[] { "내용에 맞게 자르기", "전체 캔버스 크기" }, SelectedIndex = 0, Margin = new Thickness(0, 5, 0, 14) };
    readonly Slider quality = new() { Minimum = 1, Maximum = 100, Value = 95, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(4, 5, 4, 15) };
    readonly TextBlock dimensions = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 6, 3, 10) };
    readonly TextBlock qualityLabel = new() { Text = "JPEG 품질 95", Margin = new Thickness(3) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 8, 3, 12) };
    readonly TextBlock dependencies = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 10, 3, 14), FontSize = 12 };
    readonly Button save;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim encoding = new(1, 1);
    readonly Task<SelectedLayerImage> rendered;
    CancellationTokenSource? pending;
    SelectedLayerImage? ready;
    int generation;
    bool closed, saving;

    public SelectedLayerExportDialog(Window owner, Document document, IEnumerable<Guid> ids)
    {
        snapshot = document.Snapshot(); selection = ids.Distinct().ToArray();
        Owner = owner; Title = "Morupixel · 선택 레이어 내보내기";
        Width = 960; Height = 720; MinWidth = 790; MinHeight = 590;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont;
        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(284) });
        root.Children.Add(new Border { Background = Checkerboard(), CornerRadius = new CornerRadius(10), Child = preview, Margin = new Thickness(0, 0, 16, 0) });
        var side = new StackPanel();
        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); root.Children.Add(scroll);
        side.Children.Add(new TextBlock { Text = "선택 레이어 내보내기", FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 7) });
        side.Children.Add(new TextBlock { Text = selection.Length == 1 ? snapshot.Layers.Single(layer => layer.Id == selection[0]).Name : $"선택한 {selection.Length}개 레이어를 하나의 이미지로", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3), Foreground = Theme.Muted });
        side.Children.Add(dimensions);
        side.Children.Add(Theme.Label("내보낼 영역")); side.Children.Add(bounds);
        side.Children.Add(Theme.Label("파일 형식")); side.Children.Add(format);
        side.Children.Add(qualityLabel); side.Children.Add(quality);
        side.Children.Add(status);
        save = Theme.Button("파일로 저장…", () => _ = SaveAsync()); save.Background = Theme.Primary; save.IsEnabled = false;
        side.Children.Add(save);
        var close = Theme.Button("닫기", Close); close.IsCancel = true; side.Children.Add(close);
        dependencies.Text = "선택 레이어만 합성합니다. 배경·조정 레이어 제외로 색이 달라질 수 있으며, 캔버스 밖은 제외됩니다.";
        dependencies.ToolTip = "선택 레이어는 표시하여 합칩니다. 그룹의 표시된 하위 레이어와 마스크·변형·부모 그룹 속성을 포함합니다.";
        side.Children.Add(dependencies);
        rendered = Task.Run(() => SelectedLayerExport.Render(snapshot, selection, false, lifetime.Token), lifetime.Token);
        // Observe a fault even if the dialog is immediately closed before its first preview.
        _ = rendered.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Loaded += (_, _) => _ = UpdateAsync();
        bounds.SelectionChanged += (_, _) => _ = UpdateAsync();
        format.SelectionChanged += (_, _) => _ = UpdateAsync();
        quality.ValueChanged += (_, _) => _ = UpdateAsync();
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => { closed = true; generation++; lifetime.Cancel(); pending?.Cancel(); };
    }

    string Extension => format.SelectedIndex switch { 1 => ".jpg", 2 => ".tiff", _ => ".png" };

    async Task UpdateAsync()
    {
        if (closed || saving || !IsLoaded) return;
        int id = ++generation; string extension = Extension; int jpegQuality = (int)quality.Value; bool trim = bounds.SelectedIndex == 0;
        pending?.Cancel(); var request = pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        save.IsEnabled = false; ready = null; quality.IsEnabled = extension == ".jpg"; qualityLabel.Text = $"JPEG 품질 {jpegQuality}";
        status.Text = "선택 레이어 미리보기를 준비하고 있습니다…";
        try
        {
            await Task.Delay(100, request.Token);
            var full = await rendered.WaitAsync(request.Token);
            await encoding.WaitAsync(request.Token);
            (SelectedLayerImage Selection, BitmapSource Preview, long Bytes) result;
            try
            {
                result = await Task.Run(() =>
                {
                    request.Token.ThrowIfCancellationRequested();
                    var output = trim ? SelectedLayerExport.Trim(full.Image, full.IndependentClippingCount, request.Token) : full;
                    using var buffer = new MemoryStream(); ImportExport.Write(output.Image, buffer, extension, jpegQuality, snapshot.Dpi);
                    request.Token.ThrowIfCancellationRequested();
                    long bytes = buffer.Length; buffer.Position = 0;
                    BitmapSource image = Raster.Load(buffer).Bitmap();
                    double scale = Math.Min(1, 760d / Math.Max(image.PixelWidth, image.PixelHeight));
                    if (scale < 1) { image = new TransformedBitmap(image, new ScaleTransform(scale, scale)); image.Freeze(); }
                    return (output, image, bytes);
                }, request.Token);
            }
            finally { encoding.Release(); }
            if (closed || id != generation) return;
            ready = result.Selection; preview.Source = result.Preview;
            dimensions.Text = $"{ready.Image.Width:N0} × {ready.Image.Height:N0} px · {snapshot.Dpi:0.##} DPI";
            status.Text = $"예상 파일 크기 {result.Bytes / 1024d:0.#} KB";
            if (ready.IndependentClippingCount > 0) status.Text += $"\n클리핑 {ready.IndependentClippingCount}개: 기준 레이어가 선택되지 않아 독립 이미지로 내보냅니다.";
            save.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && id == generation) { status.Text = ex.Message; preview.Source = null; dimensions.Text = ""; } }
        finally { if (ReferenceEquals(pending, request)) pending = null; request.Dispose(); }
    }

    async Task SaveAsync()
    {
        if (closed || saving || ready == null) return;
        string extension = Extension;
        string name = selection.Length == 1 ? snapshot.Layers.Single(layer => layer.Id == selection[0]).Name : snapshot.Name + "-선택 레이어";
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length > 110) name = name[..110];
        if (string.IsNullOrWhiteSpace(name)) name = "선택 레이어";
        var picker = new SaveFileDialog { Title = "선택 레이어 이미지 저장", FileName = name + extension, DefaultExt = extension, AddExtension = true, Filter = extension.TrimStart('.').ToUpperInvariant() + " 이미지|*" + extension };
        if (picker.ShowDialog(this) != true) return;
        var raster = ready.Image; int jpegQuality = (int)quality.Value;
        saving = true; IsEnabled = false; status.Text = "이미지를 저장하고 있습니다…";
        try
        {
            await Task.Run(() => ProjectStore.AtomicWrite(picker.FileName, stream => ImportExport.Write(raster, stream, extension, jpegQuality, snapshot.Dpi)));
            saving = false; Close();
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { saving = false; if (!closed) IsEnabled = true; }
    }

    static DrawingBrush Checkerboard()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Theme.Brush("#242A33"), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        drawing.Children.Add(new GeometryDrawing(Theme.Brush("#303743"), null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        drawing.Children.Add(new GeometryDrawing(Theme.Brush("#303743"), null, new RectangleGeometry(new Rect(10, 10, 10, 10))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 20, 20) };
        brush.Freeze(); return brush;
    }
}
