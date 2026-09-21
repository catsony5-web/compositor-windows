using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

internal sealed class CompatibilityDialog : Window
{
    readonly string path;
    readonly bool pdf, cad;
    readonly TextBox page = new() { Text = "1" }, dpi = new() { Text = "150" }, edge = new() { Text = "2400" };
    readonly CheckBox separate = new() { Margin = new Thickness(3, 12, 3, 12) };
    readonly ComboBox space = new() { DisplayMemberPath = "Name", SelectedValuePath = "Key" };
    readonly StackPanel settings = new();
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 8, 3, 12) };
    readonly TextBlock messages = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 10, 3, 10) };
    readonly Button render, accept;
    readonly CancellationTokenSource lifetime = new();
    CompatibilityResult? prepared;
    Raster? preparedComposite;
    bool closed, busy;
    public Document? Result { get; private set; }
    public CompatibilityDialog(Window owner, string file, bool placeAsLayer = false)
    {
        path = file; string extension = Path.GetExtension(path).ToLowerInvariant(); pdf = extension is ".pdf" or ".ai"; cad = extension is ".dwg" or ".dxf";
        Owner = owner; Title = "Morupixel · 호환 파일 가져오기"; Width = 940; Height = 700; MinWidth = 780; MinHeight = 570;
        Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) }; root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); Content = root;
        root.Children.Add(new Border { Background = Theme.Brush("#11171C"), CornerRadius = new CornerRadius(10), Child = preview, Margin = new Thickness(0, 0, 20, 0) });
        var side = new DockPanel { LastChildFill = true }; Grid.SetColumn(side, 1); root.Children.Add(side);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); side.Children.Add(bottom);
        render = Theme.Button("미리보기 만들기", async () => await RenderAsync());
        accept = Theme.Button("이 설정으로 가져오기", () => { if (prepared != null && preparedComposite != null) { Result = prepared.Document; DialogResult = true; } }); accept.IsEnabled = false; accept.Background = Theme.Primary;
        var cancel = Theme.Button("취소", Close); cancel.IsCancel = true; bottom.Children.Add(render); bottom.Children.Add(accept); bottom.Children.Add(cancel);
        var content = new StackPanel(); side.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        content.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 10) });
        content.Children.Add(details); content.Children.Add(settings);
        void Field(string title, Control control) { settings.Children.Add(Theme.Label(title, 12)); control.Margin = new Thickness(3, 4, 3, 10); settings.Children.Add(control); }
        if (pdf)
        {
            details.Text = "PDF 정보를 읽는 중…"; Field(extension == ".ai" ? "아트보드 / 페이지 번호" : "페이지 번호", page); Field("해상도 · DPI (36~600)", dpi); render.IsEnabled = false;
            separate.Content = "저장된 PDF / AI 레이어 유지"; separate.IsChecked = true; settings.Children.Add(separate);
        }
        else if (cad)
        {
            details.Text = "도면 배치와 외부참조를 확인하는 중…"; render.IsEnabled = false;
            Field("가져올 공간 / 배치", space);
            Field("도면의 긴 변 · px (256~4096)", edge); separate.Content = "CAD 레이어별로 이미지 분리"; separate.IsChecked = true; settings.Children.Add(separate);
        }
        else
        {
            details.Text = "저장된 합성 이미지 사용 · 효과 개별 편집 불가";
            separate.Content = new TextBlock { Text = "개별 픽셀 레이어 가져오기\nRGB / 회색조 · 8비트", TextWrapping = TextWrapping.Wrap }; settings.Children.Add(separate);
            settings.Children.Add(new TextBlock { Text = "픽셀·마스크·혼합 모드 지원. 그룹·조정 레이어는 합성 이미지 모드 사용.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 5, 3, 10) });
        }
        content.Children.Add(messages);
        if (placeAsLayer) content.Children.Add(new TextBlock { Text = "가져온 레이어를 한 그룹으로 추가", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 10, 3, 10) });
        foreach (var box in new[] { page, dpi, edge }) box.TextChanged += (_, _) => InvalidatePrepared();
        separate.Checked += (_, _) => InvalidatePrepared(); separate.Unchecked += (_, _) => InvalidatePrepared();
        space.SelectionChanged += (_, _) => InvalidatePrepared();
        Loaded += async (_, _) =>
        {
            if (!pdf && !cad) return;
            try
            {
                if (pdf)
                {
                    var info = await Task.Run(() => PdfCompatibility.InspectAsync(path, lifetime.Token));
                    if (closed) return; details.Text = $"총 {info.Pages}페이지 · 저장된 레이어 {info.Layers}개\n문자·벡터는 레이어별 픽셀로 변환";
                    separate.IsEnabled = info.Layers > 0;
                }
                else
                {
                    var spaces = await Task.Run(() => CadCompatibility.Inspect(path), lifetime.Token);
                    if (closed) return; space.ItemsSource = new[] { new CadCompatibility.Space("", "자동 선택") }.Concat(spaces); space.SelectedIndex = 0;
                    details.Text = "모델 공간·배치·외부참조를 2D 이미지로 변환";
                }
                render.IsEnabled = true; await RenderAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!closed) messages.Text = FriendlyError(e); }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }
    void InvalidatePrepared() { prepared = null; preparedComposite = null; accept.IsEnabled = false; preview.Source = null; messages.Text = "미리보기 필요"; }
    async Task RenderAsync()
    {
        if (busy || closed) return; busy = true; render.IsEnabled = false; accept.IsEnabled = false; settings.IsEnabled = false; prepared = null;
        try
        {
            var options = new CompatibilityOptions(Page: pdf ? Integer(page.Text, 1, 100000) : 1, Dpi: pdf ? Dialogs.Number(dpi.Text, 36, 600) : 96,
                CadLongEdge: cad ? Integer(edge.Text, 256, 4096) : 2400, SeparateLayers: separate.IsChecked == true,
                CadLayout: cad && space.SelectedItem is CadCompatibility.Space selected && selected.Key.Length > 0 ? selected.Key : null,
                PreservePdfLayers: pdf && separate.IsChecked == true);
            messages.Text = "미리보기 생성 중…";
            var result = await CompatibilityImport.ReadAsync(path, options, lifetime.Token);
            var bitmap = await Task.Run(() =>
            {
                var raster = Imaging.Render(result.Document, lifetime.Token); double scale = Math.Min(1, 1000d / Math.Max(raster.Width, raster.Height));
                return (Full: raster, Preview: (scale < 1 ? ImportExport.Resize(raster, Math.Max(1, (int)(raster.Width * scale)), Math.Max(1, (int)(raster.Height * scale))) : raster).Bitmap());
            }, lifetime.Token);
            if (closed) return; prepared = result; preparedComposite = bitmap.Full; preview.Source = bitmap.Preview; accept.IsEnabled = true;
            messages.Text = $"{result.Document.Width:N0} × {result.Document.Height:N0}px · {result.Document.Layers.Count} 레이어\n\n" + string.Join("\n\n", result.Warnings);
        }
        catch (OperationCanceledException) { if (!closed) messages.Text = "가져오기를 취소했습니다."; }
        catch (Exception e) { if (!closed) messages.Text = FriendlyError(e); }
        finally { busy = false; if (!closed) { render.IsEnabled = true; settings.IsEnabled = true; } }
    }
    static int Integer(string value, int min, int max) { double n = Dialogs.Number(value, min, max); if (n != Math.Truncate(n)) throw new ArgumentException("정수로 입력하세요."); return (int)n; }
    static string FriendlyError(Exception e) => e.HResult == unchecked((int)0x8007052b)
        ? "암호가 필요한 PDF입니다. 암호를 해제한 사본을 저장한 뒤 다시 가져와 주세요." : e.Message;
}

internal sealed class CompatibilityExportDialog : Window
{
    readonly Document snapshot;
    readonly ComboBox format = new() { ItemsSource = new[] { "PDF · 합성 이미지", "PSD · 합성 이미지", "PSD · 픽셀 레이어" }, SelectedIndex = 0, Margin = new Thickness(3, 14, 3, 12) };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 8, 3, 16) };
    readonly Button save;
    bool busy;
    public CompatibilityExportDialog(Window owner, Document document)
    {
        snapshot = document.Snapshot(); Owner = owner; Title = "Morupixel · PDF / Photoshop 내보내기"; Width = 510; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(Theme.Label("호환 형식 내보내기", 21)); panel.Children.Add(format); panel.Children.Add(message);
        save = Theme.Button("파일로 저장…", async () => await SaveAsync()); panel.Children.Add(save); var close = Theme.Button("닫기", Close); close.IsCancel = true; panel.Children.Add(close);
        format.SelectionChanged += (_, _) => Describe(); Describe(); Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
    void Describe()
    {
        bool valid = format.SelectedIndex != 2 || PhotoshopCompatibility.CanWriteLayers(snapshot); save.IsEnabled = valid;
        message.Text = format.SelectedIndex switch
        {
            0 => "문서 DPI에 맞춘 한 페이지 RGB PDF입니다. 현재 합성 결과를 이미지로 담습니다. Illustrator에서도 열 수 있지만 문자·벡터·레이어를 개별 편집하는 AI 파일은 아닙니다.",
            1 => "현재 합성 결과를 RGB / 8비트 PSD로 저장합니다. 원본 문서의 레이어·문자·벡터를 개별 편집하려면 .moruproj도 함께 보관하세요.",
            _ => valid ? "레이어 이름·표시·불투명도·혼합 모드를 저장합니다. 문자·도형·변형·마스크는 각 레이어의 픽셀에 적용됩니다. 그룹·조정·클리핑 문서는 합성 PSD로 출력하세요." : "현재 문서에 그룹·조정·클리핑 레이어가 있습니다. 외형 보존을 위해 ‘PSD · 합성 이미지’를 선택해 주세요."
        };
    }
    async Task SaveAsync()
    {
        if (busy) return; int selected = format.SelectedIndex; string extension = selected == 0 ? ".pdf" : ".psd";
        var picker = new SaveFileDialog { Filter = selected == 0 ? "PDF 문서|*.pdf" : "Photoshop PSD|*.psd", DefaultExt = extension, AddExtension = true, FileName = snapshot.Name + extension };
        if (picker.ShowDialog(this) != true) return; busy = true; save.IsEnabled = false; format.IsEnabled = false; message.Text = "파일을 저장하고 있습니다…";
        try
        {
            await Task.Run(() => ProjectStore.AtomicWrite(picker.FileName, stream => { if (selected == 0) PdfCompatibility.Write(snapshot, stream); else PhotoshopCompatibility.Write(snapshot, stream, selected == 2); }));
            busy = false; Close();
        }
        catch (Exception e) { message.Text = "저장 실패: " + e.Message; }
        finally { busy = false; save.IsEnabled = true; format.IsEnabled = true; }
    }
}
