using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Compositor.Windows;

// Drawing cleanup settings for CAD imports: role-based line weights, hatch handling
// (recommended materials first, boundaries only, or the user's own material image)
// and an editable role per CAD layer.
internal sealed partial class CompatibilityDialog
{
    sealed record HatchChoice(HatchTreatment Value, string Name)
    {
        public override string ToString() => Name;
    }
    sealed record RoleChoice(DrawingRole Value)
    {
        public override string ToString() => DrawingCleanup.RoleName(Value);
    }
    readonly CheckBox lineWeights = new() { Content = "레이어별 선 굵기·농도 맞추기", IsChecked = true, Margin = new Thickness(3, 6, 3, 8),
        ToolTip = "벽·구조는 굵고 진하게, 창호는 중간, 가구·치수는 가늘고 연하게 정리합니다." };
    readonly ComboBox hatchMode = new() { ItemsSource = new[] {
        new HatchChoice(HatchTreatment.Suggest, "재질 추천으로 채우기 (추천)"),
        new HatchChoice(HatchTreatment.Keep, "그대로 두기 (경계선만)"),
        new HatchChoice(HatchTreatment.Image, "내 재질 이미지로 채우기…") }, SelectedIndex = 0 };
    readonly TextBlock hatchSummary = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Subtle, FontSize = Theme.CaptionSize, Margin = new Thickness(3, 0, 3, 10) };
    readonly Expander roleExpander = new() { Header = "레이어 역할", Margin = new Thickness(0, 4, 0, 0) };
    // Collapsed by default so the beginner view fits; the header states what will be applied.
    readonly Expander cleanupExpander = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel roleRows = new();
    readonly Dictionary<string, ComboBox> rolePickers = new(StringComparer.OrdinalIgnoreCase);
    CadDrawingInfo? drawingInfo;
    string? materialImage;
    int lastHatchIndex;
    internal const int MaxRoleRows = 300;

    void BuildCleanupSettings(StackPanel panel)
    {
        var body = new StackPanel();
        cleanupExpander.SetResourceReference(StyleProperty, "ImportDetailsExpander"); cleanupExpander.Content = body;
        panel.Children.Add(cleanupExpander); panel = body;
        panel.Children.Add(lineWeights);
        panel.Children.Add(Theme.Label("해치", Theme.CaptionSize, Theme.Muted));
        hatchMode.Margin = new Thickness(3, 4, 3, 4); System.Windows.Automation.AutomationProperties.SetName(hatchMode, "해치 처리");
        panel.Children.Add(hatchMode); panel.Children.Add(hatchSummary);
        roleExpander.SetResourceReference(StyleProperty, "ImportDetailsExpander");
        Grid.SetIsSharedSizeScope(roleRows, true);
        roleExpander.Content = new ScrollViewer { Content = roleRows, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        roleExpander.Visibility = Visibility.Collapsed;
        panel.Children.Add(roleExpander);
        lineWeights.Checked += (_, _) => { DescribeCleanup(); InvalidatePrepared(); }; lineWeights.Unchecked += (_, _) => { DescribeCleanup(); InvalidatePrepared(); };
        hatchMode.SelectionChanged += (_, _) =>
        {
            if (hatchMode.SelectedItem is HatchChoice { Value: HatchTreatment.Image } && !PickMaterialImage()) { hatchMode.SelectedIndex = lastHatchIndex; return; }
            lastHatchIndex = hatchMode.SelectedIndex; DescribeHatches(); InvalidatePrepared();
        };
        DescribeHatches();
    }

    bool PickMaterialImage()
    {
        if (!IsLoaded) return materialImage != null;
        var open = new OpenFileDialog { Title = "해치에 채울 재질 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp" };
        if (open.ShowDialog(this) != true) return false;
        materialImage = open.FileName; return true;
    }

    internal string RoleSummary => roleExpander.Header as string ?? "";
    internal TextBlock MessageText => messages;
    internal TextBlock HatchSummaryText => hatchSummary;

    internal void UseMaterialImage(string path) { materialImage = path; lastHatchIndex = 2; hatchMode.SelectedIndex = 2; }

    internal void ShowDrawingInfo(CadDrawingInfo info)
    {
        drawingInfo = info; roleRows.Children.Clear(); rolePickers.Clear();
        foreach (var layer in info.Layers.Take(MaxRoleRows))
        {
            // The role column sizes to the longest shown role (shared by all rows) so a longer
            // translation widens every picker together instead of being cut off; names trim.
            var row = new Grid { Margin = new Thickness(3, 2, 3, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 60 }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "CadRole" });
            var name = Loc.Keep(new TextBlock { Text = layer.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = Theme.CaptionSize,
                ToolTip = $"{layer.Name} · 객체 {layer.Objects:N0}개" + (layer.Hatches > 0 ? $" · 해치 {layer.Hatches:N0}개" : "") });
            row.Children.Add(name);
            var picker = new ComboBox { ItemsSource = Enum.GetValues<DrawingRole>().Select(r => new RoleChoice(r)).ToArray(), MinHeight = 26, MinWidth = 150, Margin = new Thickness(6, 0, 0, 0), FontSize = Theme.CaptionSize };
            // A role the user chose for a layer of this name before is chosen again.
            picker.SelectedIndex = (int)(remembered.CadRoles is { } roles && roles.TryGetValue(layer.Name, out var role) ? role : layer.Role);
            System.Windows.Automation.AutomationProperties.SetName(picker, "레이어 역할: " + layer.Name);
            picker.SelectionChanged += (_, _) => InvalidatePrepared();
            Grid.SetColumn(picker, 1); row.Children.Add(picker); roleRows.Children.Add(row);
            rolePickers[layer.Name] = picker;
        }
        int shown = Math.Min(info.Layers.Count, MaxRoleRows);
        roleExpander.Header = $"{Loc.T("레이어 역할")} ({info.Layers.Count:N0}) · {DrawingCleanup.RoleSummary(info.Layers.Select(l => l.Role), Loc.T, unbreakable: true)}";
        if (info.Layers.Count > shown) roleRows.Children.Add(Theme.Label($"처음 {shown}개 레이어만 표시합니다. 나머지는 이름으로 자동 분류합니다.", Theme.CaptionSize, Theme.Subtle));
        roleExpander.Visibility = info.Layers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DescribeHatches();
    }

    void DescribeCleanup() => cleanupExpander.Header = "도면 정리 · " + string.Join(" · ", new[]
    {
        lineWeights.IsChecked == true ? "선 굵기 정리" : "선 원본 유지",
        SelectedHatches switch { HatchTreatment.Suggest => "해치 재질 추천", HatchTreatment.Image => "해치 내 재질", _ => "해치 그대로" }
    });

    void DescribeHatches()
    {
        DescribeCleanup();
        int total = drawingInfo?.HatchMaterials.Values.Sum() ?? 0;
        // The user's image name is data: the sentence is translated, the file name is not.
        if (drawingInfo != null && total > 0 && SelectedHatches == HatchTreatment.Image)
        { Loc.SetText(hatchSummary, "해치 {0}개를 ‘{1}’ 이미지로 채워요.", total.ToString("N0"), Path.GetFileName(materialImage)); return; }
        hatchSummary.Text = drawingInfo == null ? "도면을 확인하면 해치 재질을 추천해요."
            : total == 0 ? "이 도면에는 해치가 없어요."
            : SelectedHatches switch
            {
                // Each material keeps its count on the same line (no-break space).
                HatchTreatment.Suggest => $"해치 {total:N0}개 추천: {string.Join(" · ", drawingInfo.HatchMaterials.OrderByDescending(p => p.Value).Select(p => $"{Loc.T(DrawingCleanup.MaterialName(p.Key))}\u00A0{p.Value}"))}",
                _ => $"해치 {total:N0}개를 경계선으로만 가져와요."
            };
    }

    internal static CadDrawingInfo SampleDrawing => new(
        [new("A-WALL", DrawingRole.Structure, 42, 0), new("A-DOOR", DrawingRole.Opening, 12, 0), new("A-FURN-SOFA", DrawingRole.Furniture, 30, 0),
         new("A-ANNO-DIMS", DrawingRole.Annotation, 18, 0), new("A-FLOR-PATT", DrawingRole.Hatch, 9, 9)],
        new Dictionary<MaterialKind, int> { [MaterialKind.Concrete] = 4, [MaterialKind.Tile] = 3, [MaterialKind.Wood] = 2 });

    // Offscreen preview of the settings with a sample drawing (no file is read).
    internal static CompatibilityDialog CleanupPreview(string path)
    {
        var dialog = new CompatibilityDialog(null, path); dialog.ShowDrawingInfo(SampleDrawing);
        dialog.cleanupExpander.IsExpanded = true; dialog.roleExpander.IsExpanded = true; dialog.messages.Text = "미리보기를 확인하고 가져오세요.";
        return dialog;
    }

    // Offscreen previews: three floor plans chosen together (the quick path offered), and the quick
    // path itself importing with the remembered settings.
    internal static CompatibilityDialog BatchPreview(string path, bool quick = false, bool failed = false)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        var dialog = new CompatibilityDialog(null, path, false, new ImportSettings { CadSkipDialog = true },
            [path, Path.Combine(folder, "3층 평면도.dwg"), Path.Combine(folder, "4층 평면도.dwg")], quick);
        dialog.ShowDrawingInfo(SampleDrawing); dialog.details.Text = "CAD 도면";
        if (quick) { dialog.SetInputs(false); dialog.ShowProgress(1, dialog.files); }
        else if (failed) dialog.ShowFileError(new InvalidDataException("블록 배열이 너무 큽니다."));
        else dialog.messages.Text = "미리보기를 확인하고 가져오세요.";
        return dialog;
    }

    internal static void RunCleanupTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        test("CAD import dialog sends cleanup defaults, role overrides and the material image", () =>
        {
            var dialog = new CompatibilityDialog(null, "평면.dxf");
            try
            {
                Check(dialog.ReadCleanup() is { LineWeights: true, Hatches: HatchTreatment.Suggest, Roles: null }, "Cleanup is not on with recommended materials by default");
                dialog.ShowDrawingInfo(SampleDrawing);
                Check(dialog.rolePickers.Count == 5 && dialog.hatchSummary.Text.Contains("콘크리트\u00A04"), "Layer roles or hatch recommendations are not shown");
                dialog.rolePickers["A-WALL"].SelectedIndex = (int)DrawingRole.Furniture;
                Check(dialog.ReadCleanup()!.Roles is { Count: 1 } roles && roles["A-WALL"] == DrawingRole.Furniture, "A changed role was not sent as the only override");
                dialog.UseMaterialImage(@"C:\재질\오크.png");
                Check(dialog.ReadCleanup() is { Hatches: HatchTreatment.Image, MaterialImage: @"C:\재질\오크.png" } && dialog.cleanupExpander.Header.ToString()!.Contains("해치 내 재질"), "The material image choice was not applied");
                dialog.lineWeights.IsChecked = false; dialog.hatchMode.SelectedIndex = 1;
                Check(dialog.ReadCleanup() == null, "Turning cleanup off still sent settings");
            }
            finally { dialog.Close(); }
        });
        test("CAD import role pickers show whole English role names and the refresh button keeps its gap", () =>
        {
            string previous = Loc.Language;
            CompatibilityDialog? dialog = null;
            try
            {
                Loc.Use("en");
                dialog = CleanupPreview("평면.dxf");
                var host = DialogLayoutTests.Themed(OffscreenPreview.Host(dialog));
                DialogLayoutTests.Layout(host, 940, 700); Loc.PrepareOffscreen(host);
                // Every role, including the longest, fits its picker.
                var sample = dialog.rolePickers["A-FURN-SOFA"];
                foreach (int index in new[] { (int)DrawingRole.Structure, (int)DrawingRole.Furniture })
                {
                    sample.SelectedIndex = index; host.UpdateLayout(); Loc.PrepareOffscreen(host);
                    foreach (var (layer, picker) in dialog.rolePickers)
                    {
                        var text = DialogLayoutTests.SelectionText(picker);
                        Check(!text.Text.Any(c => c is >= '가' and <= '힣'), "Role name is not translated in the test: " + text.Text);
                        Check(!DialogLayoutTests.Clipped(text, dialog.roleRows), $"Role \"{text.Text}\" is cut off for {layer} (picker {picker.ActualWidth:0.#} DIP)");
                    }
                    Check(dialog.rolePickers.Values.Select(p => Math.Round(p.ActualWidth)).Distinct().Count() == 1, "Role pickers have different widths");
                }
                var refresh = DialogLayoutTests.Bounds(dialog.render, host); var import = DialogLayoutTests.Bounds(dialog.accept, host);
                Check(import.Top - refresh.Bottom >= 6, $"Refresh preview sits {import.Top - refresh.Bottom:0.#} DIP above Cancel/Import");
            }
            finally { dialog?.Close(); Loc.Use(previous); }
        });
    }

    HatchTreatment SelectedHatches => hatchMode.SelectedItem is HatchChoice choice ? choice.Value : HatchTreatment.Suggest;

    // Only layers whose role was changed from the automatic guess are sent as overrides
    // (with roles remembered for layers of the same name; see CaptureSettings).
    CadCleanup? ReadCleanup() => cad ? CaptureSettings().Cleanup() : null;
}
