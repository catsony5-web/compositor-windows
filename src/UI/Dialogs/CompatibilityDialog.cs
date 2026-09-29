using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

// Import settings for PDF/AI, PSD/PSB and DWG/DXF. It opens prefilled with the last used settings,
// can apply them to every file chosen together (one tab each), and for DWG/DXF can skip itself:
// in that quick mode it only shows progress and falls back to the settings when a file fails.
internal sealed partial class CompatibilityDialog : Window
{
    readonly string path;
    readonly IReadOnlyList<string> files;
    readonly ImportSettings remembered;
    readonly bool pdf, cad;
    bool direct;
    readonly TextBox page = new() { Text = "1" }, dpi = new() { Text = "150" }, edge = new() { Text = "2400" };
    readonly CheckBox separate = new() { Margin = new Thickness(3, 8, 3, 12) };
    readonly CheckBox retain = new() { Content = "확대해도 선명하게 (벡터)", IsChecked = true, Margin = new Thickness(3, 8, 3, 12),
        ToolTip = "선과 도형은 벡터로, 사진은 원본 해상도로 유지합니다." };
    readonly CheckBox artboard = new() { Content = "도면 크기에 맞춰 대지 만들기", IsChecked = true, Margin = new Thickness(3, 0, 3, 8),
        ToolTip = "새 문서는 도면 범위로 대지를 만들어요. 배치를 가져오면 용지 크기로 만들어요. 대지가 있는 문서에 추가하면 기존 대지 오른쪽에 새 대지로 놓아요." };
    readonly CheckBox applyAll = new() { IsChecked = true, Margin = new Thickness(3, 8, 3, 2), Visibility = Visibility.Collapsed };
    readonly CheckBox skipDialog = new() { Content = "다음부터 묻지 않고 이 설정으로 가져오기", Margin = new Thickness(3, 6, 3, 2), Visibility = Visibility.Collapsed,
        ToolTip = "DWG·DXF를 열면 이 창 없이 같은 설정으로 바로 가져와요. Shift를 누른 채 열거나 파일 메뉴의 ‘도면 가져오기 설정 다시 묻기’로 다시 볼 수 있어요." };
    readonly TextBlock skipHint = Theme.Label("Shift를 누른 채 열면 이 창이 다시 열려요.", Theme.CaptionSize, Theme.Subtle);
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
    string? startNotice;
    bool closed, busy;
    /// <summary>Every imported file in order, one document each (or the reason it failed).</summary>
    public IReadOnlyList<ImportedFile> Results { get; private set; } = [];
    public Document? Result => Results.Count > 0 ? Results[0].Document : null;
    /// <summary>The settings that were used, to be remembered for the next import.</summary>
    public ImportSettings? UsedSettings { get; private set; }
    internal bool Direct => direct;
    public CompatibilityDialog(Window? owner, string file, bool placeAsLayer = false, ImportSettings? lastSettings = null, IReadOnlyList<string>? batch = null, bool quick = false)
    {
        path = file; files = batch is { Count: > 0 } ? batch : [file]; remembered = lastSettings ?? new ImportSettings();
        string extension = Path.GetExtension(path).ToLowerInvariant(); pdf = extension is ".pdf" or ".ai"; cad = extension is ".dwg" or ".dxf";
        direct = quick && cad;
        // A remembered material image that has gone missing is not silently replaced by the quick path.
        if (direct && remembered.CadHatches == HatchTreatment.Image && !File.Exists(remembered.CadMaterialImage))
        { direct = false; startNotice = "지난번 재질 이미지를 찾을 수 없어 설정 창을 열었어요."; }
        Owner = owner; Title = "Morupixel · 호환 파일 가져오기"; Width = 940; Height = 700; MinWidth = 780; MinHeight = 570;
        Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) }; root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); Content = root;
        root.Children.Add(new Border { Background = Theme.Stage, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = preview, Margin = new Thickness(0, 0, 20, 0) });
        var side = new DockPanel { LastChildFill = true }; Grid.SetColumn(side, 1); root.Children.Add(side);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); side.Children.Add(bottom);
        bottom.Children.Add(new ScrollViewer { Content = messages, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        render = Theme.Button("미리보기 새로고침", async () => await RenderAsync()); render.IsEnabled = false;
        accept = DialogShell.Primary("가져오기", async () => { if (await ImportAsync() && !closed) DialogResult = true; }); accept.IsEnabled = false; accept.IsDefault = true; accept.Margin = new Thickness(8, 0, 0, 0);
        var cancel = DialogShell.Secondary("취소", Close); cancel.IsCancel = true;
        // Batch and quick-path choices sit with the action they change.
        if (files.Count > 1)
        {
            applyAll.Content = $"모든 파일에 같은 설정 적용 ({files.Count}개)"; applyAll.Visibility = Visibility.Visible;
            applyAll.ToolTip = "첫 파일로 미리보기를 만들고 나머지 파일도 같은 설정으로 가져와요. 파일마다 새 탭이 열리고, 바꾼 레이어 역할은 이름이 같은 레이어에만 적용돼요.";
            applyAll.Checked += (_, _) => DescribeAccept(); applyAll.Unchecked += (_, _) => DescribeAccept();
            bottom.Children.Add(applyAll);
        }
        if (cad)
        {
            skipDialog.Visibility = Visibility.Visible; bottom.Children.Add(skipDialog);
            skipHint.Margin = new Thickness(24, 0, 3, 2); skipHint.Visibility = Visibility.Collapsed; bottom.Children.Add(skipHint);
            skipDialog.Checked += (_, _) => skipHint.Visibility = Visibility.Visible; skipDialog.Unchecked += (_, _) => skipHint.Visibility = Visibility.Collapsed;
        }
        render.Margin = new Thickness(0, 8, 0, 0); bottom.Children.Add(render);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(cancel); Grid.SetColumn(accept, 1); actions.Children.Add(accept); bottom.Children.Add(actions);
        var content = new StackPanel(); contentScroll.Content = content; side.Children.Add(contentScroll);
        content.Children.Add(Loc.Keep(new TextBlock { Text = Path.GetFileName(path), FontSize = Theme.TitleSize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, files.Count > 1 ? 2 : 10) }));
        if (files.Count > 1)
        {
            content.Children.Add(Theme.Label($"함께 고른 파일 {files.Count}개 · 미리보기는 첫 파일이에요.", Theme.CaptionSize, Theme.Muted));
            var names = files.Skip(1).Take(5).Select(Path.GetFileName).ToList(); if (files.Count > 6) names.Add("…");
            var list = Loc.Keep(Theme.Label(string.Join(" · ", names), Theme.CaptionSize, Theme.Subtle)); list.ToolTip = string.Join("\n", files.Select(Path.GetFileName)); content.Children.Add(list);
        }
        content.Children.Add(details); content.Children.Add(settings);
        advanced.SetResourceReference(StyleProperty, "ImportDetailsExpander"); advanced.Content = advancedSettings;
        information.SetResourceReference(StyleProperty, "ImportDetailsExpander"); information.Content = notices;
        void Field(StackPanel panel, string title, Control control, string? hint = null)
        {
            panel.Children.Add(Theme.Label(title, Theme.CaptionSize, Theme.Muted)); control.Margin = new Thickness(3, 4, 3, 10); panel.Children.Add(control);
            System.Windows.Automation.AutomationProperties.SetName(control, title);
            if (hint != null) panel.Children.Add(Theme.Label(hint, Theme.CaptionSize, Theme.Subtle));
        }
        if (pdf)
        {
            details.Text = "파일 정보를 확인하고 있어요…"; Field(settings, extension == ".ai" ? "아트보드" : "페이지", page);
            separate.Content = "원본 레이어 유지"; separate.IsChecked = true; settings.Children.Add(separate);
            settings.Children.Add(artboard);
            Field(advancedSettings, "이미지 해상도 (DPI)", dpi, "36~600 DPI · 높을수록 작업 이미지가 커져요.");
        }
        else if (cad)
        {
            details.Text = "도면 정보를 확인하고 있어요…";
            Field(settings, "편집 방식", structure); settings.Children.Add(structureHint); DescribeStructure();
            settings.Children.Add(artboard);
            BuildCleanupSettings(settings);
            Field(advancedSettings, "가져올 도면", space);
            space.ToolTip = "자동 선택으로 시작하세요. 다른 도면이 보이면 모델 공간이나 배치를 직접 선택할 수 있어요.";
            Field(advancedSettings, "작업 크기 (px)", edge, "가로·세로 중 긴 쪽 기준 · 256~4,096 px");
        }
        else
        {
            details.Text = "PSD 이미지";
            separate.Content = "레이어별로 편집";
            separate.ToolTip = "RGB / 회색조 · 8비트 픽셀 레이어를 가져옵니다. 그룹이나 조정 레이어가 있으면 이 옵션을 꺼 주세요.";
            settings.Children.Add(separate);
        }
        if (pdf || cad) { advancedSettings.Children.Add(retain); settings.Children.Add(advanced); }
        content.Children.Add(information);
        if (placeAsLayer) content.Children.Add(Theme.Label("현재 문서에 그룹으로 추가해요.", Theme.CaptionSize, Theme.Muted));
        // Longer translations of the section headers wrap instead of being cut off.
        foreach (var expander in new[] { advanced, information, cleanupExpander, roleExpander }) expander.Resources[new DataTemplateKey(typeof(string))] = WrappingHeader();
        Prefill(remembered);
        DescribeAccept();
        messages.Text = direct ? "이전 설정으로 가져오고 있어요…" : "미리보기를 준비하고 있어요…";
        foreach (var box in new[] { page, dpi, edge }) box.TextChanged += (_, _) => InvalidatePrepared();
        separate.Checked += (_, _) => InvalidatePrepared(); separate.Unchecked += (_, _) => InvalidatePrepared();
        retain.Checked += (_, _) => InvalidatePrepared(); retain.Unchecked += (_, _) => InvalidatePrepared();
        artboard.Checked += (_, _) => InvalidatePrepared(); artboard.Unchecked += (_, _) => InvalidatePrepared();
        space.SelectionChanged += (_, _) => InvalidatePrepared();
        structure.SelectionChanged += (_, _) => { DescribeStructure(); InvalidatePrepared(); };
        Loaded += async (_, _) =>
        {
            if (!direct) { await PrepareAsync(); return; }
            SetInputs(false);
            if (await ImportAsync() && !closed) DialogResult = true;
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }
    // Remembered choices first; the drawing's own layers and layouts are matched once they are read.
    void Prefill(ImportSettings s)
    {
        if (cad)
        {
            structure.SelectedIndex = Math.Max(0, structure.Items.Cast<StructureChoice>().ToList().FindIndex(c => c.Value == s.CadStructure));
            lineWeights.IsChecked = s.CadLineWeights;
            if (s.CadHatches == HatchTreatment.Image && s.CadMaterialImage is { } image && File.Exists(image)) UseMaterialImage(image);
            else { hatchMode.SelectedIndex = s.CadHatches == HatchTreatment.Keep ? 1 : 0; lastHatchIndex = hatchMode.SelectedIndex; }
            edge.Text = s.CadLongEdge.ToString(CultureInfo.InvariantCulture); retain.IsChecked = s.CadRetainVectors;
            artboard.IsChecked = s.Artboard; skipDialog.IsChecked = s.CadSkipDialog;
        }
        else if (pdf)
        {
            dpi.Text = s.PdfDpi.ToString("0.##", CultureInfo.InvariantCulture); separate.IsChecked = s.PdfLayers; retain.IsChecked = s.PdfRetainVectors; artboard.IsChecked = s.Artboard;
        }
        else separate.IsChecked = s.PsdLayers;
    }
    static DataTemplate WrappingHeader()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        return new DataTemplate(typeof(string)) { VisualTree = text };
    }
    void DescribeAccept() => accept.Content = direct ? "가져오는 중…" : files.Count > 1 && applyAll.IsChecked == true ? $"{files.Count}개 모두 가져오기" : "가져오기";
    void DescribeStructure() => structureHint.Text = SelectedStructure switch
    {
        CadImportStructure.Objects => "선과 도형을 각각 선택할 수 있어요.",
        CadImportStructure.Layers => "같은 레이어의 내용을 함께 선택해요.",
        _ => "도면 전체를 한 번에 이동하고 조절해요."
    };
    CadImportStructure SelectedStructure => structure.SelectedItem is StructureChoice choice ? choice.Value : CadImportStructure.Objects;
    // The dialog's choices as settings to remember. Other file kinds' settings are kept as they were.
    internal ImportSettings CaptureSettings()
    {
        if (cad)
        {
            // Roles changed here win; roles remembered for layers this drawing does not have are kept.
            var roles = new Dictionary<string, DrawingRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, picker) in rolePickers)
                if (picker.SelectedItem is RoleChoice choice && choice.Value != DrawingCleanup.Classify(name)) roles[name] = choice.Value;
            foreach (var (name, role) in remembered.CadRoles ?? new Dictionary<string, DrawingRole>())
                if (roles.Count < ImportSettings.MaxRoles && !rolePickers.ContainsKey(name)) roles.TryAdd(name, role);
            string? layout = space.Items.Count == 0 ? remembered.CadLayout
                : space.SelectedItem is CadCompatibility.Space { Key.Length: > 0 } chosen ? (chosen.Key == ImportSettings.ModelSpace ? ImportSettings.ModelSpace : chosen.Name) : null;
            return remembered with
            {
                CadStructure = SelectedStructure, CadLineWeights = lineWeights.IsChecked == true, CadHatches = SelectedHatches,
                CadMaterialImage = SelectedHatches == HatchTreatment.Image ? materialImage : remembered.CadMaterialImage,
                CadLongEdge = Integer(edge.Text, 256, 4096), CadRetainVectors = retain.IsChecked == true, CadLayout = layout,
                CadRoles = roles.Count > 0 ? roles : null, CadSkipDialog = skipDialog.IsChecked == true, Artboard = artboard.IsChecked == true
            };
        }
        if (pdf) return remembered with { PdfDpi = Dialogs.Number(dpi.Text, 36, 600), PdfLayers = separate.IsChecked == true, PdfRetainVectors = retain.IsChecked == true, Artboard = artboard.IsChecked == true };
        return remembered with { PsdLayers = separate.IsChecked == true };
    }
    // Options for the previewed file: its exact layout once the drawing has been read. Layer roles
    // are the ones changed here plus those remembered for layers of the same name.
    CompatibilityOptions ReadOptions()
    {
        var options = CaptureSettings().Options(path, pdf ? Integer(page.Text, 1, 100000) : 1);
        if (cad && space.Items.Count > 0)
            options = options with { CadLayout = space.SelectedItem is CadCompatibility.Space selected && selected.Key.Length > 0 ? selected.Key : null, CadLayoutOptional = false };
        return options;
    }
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
        notices.Children.Add(Theme.Label($"{result.Document.Width:N0} × {result.Document.Height:N0} px · {count}", Theme.CaptionSize, Theme.Muted));
        foreach (string warning in result.Warnings)
        {
            var note = Theme.Label(warning, Theme.CaptionSize, Theme.Muted); note.Margin = new Thickness(3, 12, 3, 0); notices.Children.Add(note);
        }
        information.Header = result.Warnings.Count > 0 ? $"변환 안내 ({result.Warnings.Count})" : "가져오기 정보";
        information.Visibility = Visibility.Visible;
        messages.Foreground = Theme.Muted; messages.Text = startNotice ?? "미리보기를 확인하고 가져오세요."; startNotice = null;
    }
    void ShowError(Exception error) { ClearPrepared(); messages.Foreground = Theme.Danger; messages.Text = FriendlyError(error); }
    // Reads the drawing's layouts and layers, then renders the first preview.
    internal async Task PrepareAsync()
    {
        try
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
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
                if (closed) return; ShowSpaces(spaces);
                var layers = await Task.Run(() => CadCompatibility.InspectLayers(path), lifetime.Token);
                if (closed) return; ShowDrawingInfo(layers);
                details.Text = "CAD 도면";
            }
            render.IsEnabled = true; await RenderAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!closed) { ShowError(e); render.IsEnabled = true; } }
    }
    // The remembered layout is chosen again when this drawing has one of that name.
    internal void ShowSpaces(IReadOnlyList<CadCompatibility.Space> spaces)
    {
        var items = new[] { new CadCompatibility.Space("", "자동 선택") }.Concat(spaces).ToArray();
        space.ItemsSource = items;
        int match = remembered.CadLayout is { } name ? Array.FindIndex(items, s => s.Key.Length > 0 && (s.Key == name || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) : -1;
        space.SelectedIndex = Math.Max(0, match);
    }
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
    void SetInputs(bool enabled)
    {
        settings.IsEnabled = enabled; applyAll.IsEnabled = enabled; skipDialog.IsEnabled = enabled; render.IsEnabled = enabled;
        accept.IsEnabled = enabled && prepared != null && preparedComposite != null;
    }
    /// <summary>Imports the previewed file, and with "apply to all" every other chosen file with the same
    /// settings. A file that fails is reported and the rest continue. In the quick mode nothing was
    /// previewed: a failing first file brings the settings back instead. Returns true when done.</summary>
    internal async Task<bool> ImportAsync()
    {
        if (busy || closed || !direct && (prepared == null || preparedComposite == null)) return false;
        ImportSettings used; CompatibilityOptions first;
        try { used = CaptureSettings(); first = ReadOptions(); }
        catch (Exception e) { ShowError(e); return false; }
        IReadOnlyList<string> batch = direct || applyAll.IsChecked == true ? files : [path];
        var results = new List<ImportedFile>(); bool fallback = false;
        busy = true; SetInputs(false);
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                messages.Foreground = Theme.Muted;
                messages.Text = batch.Count > 1 ? $"가져오는 중 {i + 1}/{batch.Count} · {Path.GetFileName(batch[i])}" : "가져오는 중…";
                if (i == 0 && !direct) { results.Add(new(batch[0], prepared!.Document, prepared.Warnings)); continue; }
                try
                {
                    var result = await CompatibilityImport.ReadAsync(batch[i], i == 0 ? first : used.Options(batch[i]), lifetime.Token);
                    results.Add(new(batch[i], result.Document, result.Warnings));
                }
                catch (Exception e) when (e is not OperationCanceledException && i == 0 && direct) { fallback = true; startNotice = "이전 설정으로 가져오지 못했어요. 설정을 확인해 주세요."; break; }
                catch (Exception e) when (e is not OperationCanceledException) { results.Add(new(batch[i], null, [], FriendlyError(e))); }
            }
        }
        catch (OperationCanceledException) { if (!closed) messages.Text = "가져오기를 취소했습니다."; return false; }
        finally { busy = false; if (!closed) SetInputs(true); }
        if (closed) return false;
        if (fallback)
        {
            direct = false; DescribeAccept();
            await PrepareAsync();
            return false;
        }
        Results = results; UsedSettings = used;
        return true;
    }
    static int Integer(string value, int min, int max) { double n = Dialogs.Number(value, min, max); if (n != Math.Truncate(n)) throw new ArgumentException("정수로 입력하세요."); return (int)n; }
    static string FriendlyError(Exception e) => e.HResult == unchecked((int)0x8007052b)
        ? "암호가 필요한 PDF입니다. 암호를 해제한 사본을 저장한 뒤 다시 가져와 주세요." : e.Message;
}
