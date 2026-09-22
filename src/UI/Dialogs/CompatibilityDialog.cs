using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

internal sealed partial class CompatibilityDialog : Window
{
    readonly string path;
    readonly bool pdf, cad;
    readonly TextBox page = new() { Text = "1" }, dpi = new() { Text = "150" }, edge = new() { Text = "2400" };
    readonly CheckBox separate = new() { Margin = new Thickness(3, 8, 3, 12) };
    readonly CheckBox retain = new() { Content = "확대해도 선명하게 (벡터)", IsChecked = true, Margin = new Thickness(3, 8, 3, 12),
        ToolTip = "선과 도형은 벡터로, 사진은 원본 해상도로 유지합니다." };
    readonly ComboBox space = new() { DisplayMemberPath = "Name", SelectedValuePath = "Key" };
    sealed record StructureChoice(CadImportStructure Value, string Name)
    {
        public override string ToString() => Name;
    }
    readonly ComboBox structure = new() { DisplayMemberPath = "Name", ItemsSource = new[] {
        new StructureChoice(CadImportStructure.Objects, "부분별로 편집 (추천)"),
        new StructureChoice(CadImportStructure.Layers, "레이어별로 편집"),
        new StructureChoice(CadImportStructure.Combined, "한 장으로 가져오기") }, SelectedIndex = 0 };
    readonly TextBlock structureHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 0, 3, 10) };
    readonly StackPanel settings = new();
    readonly StackPanel advancedSettings = new();
    readonly Expander advanced = new() { Header = "세부 설정", Margin = new Thickness(0, 8, 0, 0) };
    readonly Expander information = new() { Header = "변환 안내", Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly StackPanel notices = new();
    readonly ScrollViewer contentScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 8, 3, 12) };
    readonly TextBlock messages = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(3, 8, 3, 10) };
    readonly Button render, accept;
    readonly CancellationTokenSource lifetime = new();
    CompatibilityResult? prepared;
    Raster? preparedComposite;
    bool closed, busy;
    public Document? Result { get; private set; }
    public CompatibilityDialog(Window? owner, string file, bool placeAsLayer = false)
    {
        path = file; string extension = Path.GetExtension(path).ToLowerInvariant(); pdf = extension is ".pdf" or ".ai"; cad = extension is ".dwg" or ".dxf";
        Owner = owner; Title = "Morupixel · 호환 파일 가져오기"; Width = 940; Height = 700; MinWidth = 780; MinHeight = 570;
        Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) }; root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); Content = root;
        root.Children.Add(new Border { Background = Theme.Brush("#11171C"), CornerRadius = new CornerRadius(10), Child = preview, Margin = new Thickness(0, 0, 20, 0) });
        var side = new DockPanel { LastChildFill = true }; Grid.SetColumn(side, 1); root.Children.Add(side);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); side.Children.Add(bottom);
        bottom.Children.Add(new ScrollViewer { Content = messages, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        render = Theme.Button("미리보기 새로고침", async () => await RenderAsync()); render.IsEnabled = false;
        accept = Theme.Button("가져오기", () => { if (prepared != null && preparedComposite != null) { Result = prepared.Document; DialogResult = true; } }); accept.IsEnabled = false; accept.Background = Theme.Primary; accept.IsDefault = true;
        var cancel = Theme.Button("취소", Close); cancel.IsCancel = true; bottom.Children.Add(render);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(cancel); Grid.SetColumn(accept, 1); actions.Children.Add(accept); bottom.Children.Add(actions);
        var content = new StackPanel(); contentScroll.Content = content; side.Children.Add(contentScroll);
        content.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 10) });
        content.Children.Add(details); content.Children.Add(settings);
        advanced.SetResourceReference(StyleProperty, "ImportDetailsExpander"); advanced.Content = advancedSettings;
        information.SetResourceReference(StyleProperty, "ImportDetailsExpander"); information.Content = notices;
        void Field(StackPanel panel, string title, Control control, string? hint = null)
        {
            panel.Children.Add(Theme.Label(title, 12)); control.Margin = new Thickness(3, 4, 3, 10); panel.Children.Add(control);
            System.Windows.Automation.AutomationProperties.SetName(control, title);
            if (hint != null) panel.Children.Add(Theme.Label(hint, 12, Theme.Muted));
        }
        if (pdf)
        {
            details.Text = "파일 정보를 확인하고 있어요…"; Field(settings, extension == ".ai" ? "아트보드" : "페이지", page);
            separate.Content = "원본 레이어 유지"; separate.IsChecked = true; settings.Children.Add(separate);
            Field(advancedSettings, "이미지 해상도 (DPI)", dpi, "36~600 DPI · 높을수록 작업 이미지가 커져요.");
        }
        else if (cad)
        {
            details.Text = "도면 정보를 확인하고 있어요…";
            Field(settings, "편집 방식", structure); settings.Children.Add(structureHint); DescribeStructure();
            Field(advancedSettings, "가져올 도면", space);
            space.ToolTip = "자동 선택으로 시작하세요. 다른 도면이 보이면 모델 공간이나 배치를 직접 선택할 수 있어요.";
            Field(advancedSettings, "작업 크기 (px)", edge, "가로·세로 중 긴 쪽 기준 · 256~4,096 px");
        }
        else
        {
            details.Text = "Photoshop 이미지";
            separate.Content = "레이어별로 편집";
            separate.ToolTip = "RGB / 회색조 · 8비트 픽셀 레이어를 가져옵니다. 그룹이나 조정 레이어가 있으면 이 옵션을 꺼 주세요.";
            settings.Children.Add(separate);
        }
        if (pdf || cad) { advancedSettings.Children.Add(retain); settings.Children.Add(advanced); }
        content.Children.Add(information);
        if (placeAsLayer) content.Children.Add(Theme.Label("현재 문서에 그룹으로 추가해요.", 12, Theme.Muted));
        messages.Text = "미리보기를 준비하고 있어요…";
        foreach (var box in new[] { page, dpi, edge }) box.TextChanged += (_, _) => InvalidatePrepared();
        separate.Checked += (_, _) => InvalidatePrepared(); separate.Unchecked += (_, _) => InvalidatePrepared();
        retain.Checked += (_, _) => InvalidatePrepared(); retain.Unchecked += (_, _) => InvalidatePrepared();
        space.SelectionChanged += (_, _) => InvalidatePrepared();
        structure.SelectionChanged += (_, _) => { DescribeStructure(); InvalidatePrepared(); };
        Loaded += async (_, _) =>
        {
            try
            {
                if (pdf)
                {
                    var info = await Task.Run(() => PdfCompatibility.InspectAsync(path, lifetime.Token));
                    if (closed) return; details.Text = extension == ".ai" ? $"아트보드 {info.Pages}개" : $"총 {info.Pages}페이지";
                    separate.IsEnabled = info.Layers > 0;
                    separate.ToolTip = info.Layers > 0 ? $"저장된 레이어 {info.Layers}개를 유지합니다." : "이 파일에는 따로 저장된 레이어가 없어요.";
                }
                else if (cad)
                {
                    var spaces = await Task.Run(() => CadCompatibility.Inspect(path), lifetime.Token);
                    if (closed) return; space.ItemsSource = new[] { new CadCompatibility.Space("", "자동 선택") }.Concat(spaces); space.SelectedIndex = 0;
                    details.Text = "CAD 도면";
                }
                render.IsEnabled = true; await RenderAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!closed) { ShowError(e); render.IsEnabled = true; } }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }
    void DescribeStructure() => structureHint.Text = SelectedStructure switch
    {
        CadImportStructure.Objects => "선과 도형을 각각 선택할 수 있어요.",
        CadImportStructure.Layers => "같은 레이어의 내용을 함께 선택해요.",
        _ => "도면 전체를 한 번에 이동하고 조절해요."
    };
    CadImportStructure SelectedStructure => structure.SelectedItem is StructureChoice choice ? choice.Value : CadImportStructure.Objects;
    CompatibilityOptions ReadOptions() => new(Page: pdf ? Integer(page.Text, 1, 100000) : 1, Dpi: pdf ? Dialogs.Number(dpi.Text, 36, 600) : 96,
        CadLongEdge: cad ? Integer(edge.Text, 256, 4096) : 2400, SeparateLayers: separate.IsChecked == true,
        CadLayout: cad && space.SelectedItem is CadCompatibility.Space selected && selected.Key.Length > 0 ? selected.Key : null,
        PreservePdfLayers: pdf && separate.IsChecked == true, CadStructure: cad ? SelectedStructure : null, RetainVectors: retain.IsChecked == true);
    void ClearPrepared()
    {
        prepared = null; preparedComposite = null; accept.IsEnabled = false; preview.Source = null;
        information.Visibility = Visibility.Collapsed; information.IsExpanded = false; notices.Children.Clear();
    }
    void InvalidatePrepared() { ClearPrepared(); messages.Foreground = Theme.Muted; messages.Text = "설정을 바꿨어요. 미리보기를 새로고침해 주세요."; }
    void ShowPrepared(CompatibilityResult result, Raster full, ImageSource bitmap, CompatibilityOptions options)
    {
        prepared = result; preparedComposite = full; preview.Source = bitmap; accept.IsEnabled = true;
        var layers = result.Document.Layers;
        string count = cad && options.CadStructure == CadImportStructure.Objects
            ? $"{layers.Count(l => l.Kind == LayerKind.Vector || l.Kind == LayerKind.Raster):N0}개 요소 · {layers.Count(l => l.Kind == LayerKind.Group):N0}개 그룹"
            : $"레이어 {layers.Count:N0}개";
        notices.Children.Clear();
        notices.Children.Add(Theme.Label($"{result.Document.Width:N0} × {result.Document.Height:N0} px · {count}", 12, Theme.Muted));
        foreach (string warning in result.Warnings)
        {
            var note = Theme.Label(warning, 12, Theme.Muted); note.Margin = new Thickness(3, 12, 3, 0); notices.Children.Add(note);
        }
        information.Header = result.Warnings.Count > 0 ? $"변환 안내 ({result.Warnings.Count})" : "가져오기 정보";
        information.Visibility = Visibility.Visible;
        messages.Foreground = Theme.Muted; messages.Text = "미리보기를 확인하고 가져오세요.";
    }
    void ShowError(Exception error) { ClearPrepared(); messages.Foreground = Theme.Brush("#FFB8B8"); messages.Text = FriendlyError(error); }
    async Task RenderAsync()
    {
        if (busy || closed) return; busy = true; render.IsEnabled = false; settings.IsEnabled = false; ClearPrepared();
        try
        {
            var options = ReadOptions();
            messages.Foreground = Theme.Muted; messages.Text = "미리보기를 만들고 있어요…";
            var result = await CompatibilityImport.ReadAsync(path, options, lifetime.Token);
            var bitmap = await Task.Run(() =>
            {
                var raster = Imaging.Render(result.Document, lifetime.Token); double scale = Math.Min(1, 1000d / Math.Max(raster.Width, raster.Height));
                return (Full: raster, Preview: (scale < 1 ? ImportExport.Resize(raster, Math.Max(1, (int)(raster.Width * scale)), Math.Max(1, (int)(raster.Height * scale))) : raster).Bitmap());
            }, lifetime.Token);
            if (closed) return; ShowPrepared(result, bitmap.Full, bitmap.Preview, options);
        }
        catch (OperationCanceledException) { if (!closed) messages.Text = "가져오기를 취소했습니다."; }
        catch (Exception e) { if (!closed) ShowError(e); }
        finally { busy = false; if (!closed) { render.IsEnabled = true; settings.IsEnabled = true; } }
    }
    static int Integer(string value, int min, int max) { double n = Dialogs.Number(value, min, max); if (n != Math.Truncate(n)) throw new ArgumentException("정수로 입력하세요."); return (int)n; }
    static string FriendlyError(Exception e) => e.HResult == unchecked((int)0x8007052b)
        ? "암호가 필요한 PDF입니다. 암호를 해제한 사본을 저장한 뒤 다시 가져와 주세요." : e.Message;
}

internal sealed class CompatibilityExportDialog : Window
{
    readonly Document snapshot;
    readonly ComboBox format = new() { ItemsSource = new[] { "PDF · 합성 이미지", "PSD · 합성 이미지", "PSD · 픽셀 레이어", "PDF · 벡터 원본 유지" }, SelectedIndex = 0, Margin = new Thickness(3, 14, 3, 12) };
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
        format.SelectionChanged += (_, _) => Describe();
        if (DesignRenderer.HasRetainedContent(snapshot) && VectorPdfExport.Limitation(snapshot) == null) format.SelectedIndex = 3;
        Describe(); Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
    void Describe()
    {
        bool valid = (format.SelectedIndex != 2 || PhotoshopCompatibility.CanWriteLayers(snapshot)) && (format.SelectedIndex != 3 || VectorPdfExport.Limitation(snapshot) == null); save.IsEnabled = valid;
        message.Text = format.SelectedIndex switch
        {
            0 => "문서 DPI에 맞춘 한 페이지 RGB PDF입니다. 현재 합성 결과를 이미지로 담습니다. Illustrator에서도 열 수 있지만 문자·벡터·레이어를 개별 편집하는 AI 파일은 아닙니다.",
            1 => "현재 합성 결과를 RGB / 8비트 PSD로 저장합니다. 원본 문서의 레이어·문자·벡터를 개별 편집하려면 .moruproj도 함께 보관하세요.",
            3 => valid ? "CAD 경로·문자·도형과 원본 PDF/AI의 벡터를 유지합니다. 사진은 원본 픽셀로 포함됩니다. 편집 레이어는 .moruproj에도 저장하세요." : VectorPdfExport.Limitation(snapshot),
            _ => valid ? "레이어 이름·표시·불투명도·혼합 모드를 저장합니다. 문자·도형·변형·마스크는 각 레이어의 픽셀에 적용됩니다. 그룹·조정·클리핑 문서는 합성 PSD로 출력하세요."
                : snapshot.Layers.Count > Document.MaxLayers
                    ? $"픽셀 레이어 PSD는 최대 {Document.MaxLayers}개 레이어를 지원합니다. ‘PSD · 합성 이미지’를 선택하고 객체 구조는 .moruproj로 보관하세요."
                    : "현재 문서에 그룹·조정·클리핑 레이어가 있습니다. 외형 보존을 위해 ‘PSD · 합성 이미지’를 선택해 주세요."
        };
    }
    async Task SaveAsync()
    {
        if (busy) return; int selected = format.SelectedIndex; bool pdf = selected is 0 or 3; string extension = pdf ? ".pdf" : ".psd";
        var picker = new SaveFileDialog { Filter = pdf ? "PDF 문서|*.pdf" : "Photoshop PSD|*.psd", DefaultExt = extension, AddExtension = true, FileName = snapshot.Name + extension };
        if (picker.ShowDialog(this) != true) return; busy = true; save.IsEnabled = false; format.IsEnabled = false; message.Text = "파일을 저장하고 있습니다…";
        try
        {
            await Task.Run(() => ProjectStore.AtomicWrite(picker.FileName, stream => { if (selected == 3) VectorPdfExport.Write(snapshot, stream); else if (selected == 0) PdfCompatibility.Write(snapshot, stream); else PhotoshopCompatibility.Write(snapshot, stream, selected == 2); }));
            busy = false; Close();
        }
        catch (Exception e) { message.Text = "저장 실패: " + e.Message; }
        finally { busy = false; save.IsEnabled = true; format.IsEnabled = true; }
    }
}
