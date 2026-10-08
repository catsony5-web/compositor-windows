using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// 내 점경 추가: a picked image becomes an entourage item. The first choice is suggested from the
// image (transparency kept, a light-paper drawing as lines, otherwise a photo cut out with the
// bundled background-removal model). The preview shows the result on paper; the item gets a name,
// a category, a view and its real size. Accept builds Result; the caller stores it in 내 점경.
public sealed class EntourageImportDialog : Window
{
    readonly Raster image;
    readonly SegmentedChoice<EntourageImportMode> mode;
    readonly SegmentedChoice<EntourageView> view;
    readonly ComboBox category = new() { MinHeight = Theme.ControlHeight, Margin = new Thickness(0) };
    readonly TextBox name, meters;
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(10) };
    readonly TextBlock info = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly TextBlock error = DialogShell.ErrorText();
    readonly Button accept;
    byte[]? mask;
    Raster? converted;
    int generation;

    public EntourageCustomItem? Result { get; private set; }
    internal Raster? Converted => converted;
    internal string? Error => string.IsNullOrEmpty(error.Text) ? null : error.Text;
    // Self-tests supply the cut-out mask; the app uses the bundled model on a background thread.
    internal Func<Raster, byte[]>? MaskProvider { get; set; }
    internal bool Synchronous { get; set; }

    public EntourageImportDialog(Window? owner, Raster image, string suggestedName, EntourageCategory initialCategory, EntourageView initialView)
    {
        this.image = image;
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        DialogShell.Prepare(this, owner, "내 점경 추가");
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; Content = panel;
        panel.Children.Add(DialogShell.Title("내 점경 만들기"));
        panel.Children.Add(DialogShell.Subtitle("사람·나무·탈것 이미지를 점경으로 등록합니다. 놓은 뒤에도 높이와 색을 바꿀 수 있습니다."));
        mode = new SegmentedChoice<EntourageImportMode>([(EntourageImportMode.Transparent, "투명 PNG 그대로"), (EntourageImportMode.RemoveBackground, "사진 배경 지우기"),
            (EntourageImportMode.LineDrawing, "선 그림으로")], EntourageImport.Suggest(image)) { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var button in mode.Buttons)
        {
            button.MinWidth = 0; button.Padding = new Thickness(6, 5, 6, 5);
            if (button.Content is string caption) button.Content = new KeepWordsTextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        }
        mode.Buttons[1].ToolTip = "이 컴퓨터의 AI 모델로 사람이나 물체만 남깁니다.";
        mode.Buttons[2].ToolTip = "종이에 그린 그림의 어두운 선만 남겨 선 색을 입힐 수 있게 합니다.";
        System.Windows.Automation.AutomationProperties.SetName(mode, "변환 방법");
        mode.Changed += _ => Update();
        panel.Children.Add(mode);
        var paper = DialogShell.Card(preview, Brushes.White); paper.Height = 220; paper.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(paper);
        info.Margin = new Thickness(1, 6, 1, 0); panel.Children.Add(info);
        panel.Children.Add(DialogShell.FieldLabel("이름"));
        name = new TextBox { Text = EntourageStore.CleanName(suggestedName), Margin = new Thickness(0), MaxLength = EntourageStoreLimits.MaxNameLength };
        System.Windows.Automation.AutomationProperties.SetName(name, "점경 이름");
        panel.Children.Add(name);
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var c in Enum.GetValues<EntourageCategory>()) category.Items.Add(new ComboBoxItem { Content = EntourageLibrary.CategoryName(c), Tag = c });
        category.SelectedIndex = (int)initialCategory;
        System.Windows.Automation.AutomationProperties.SetName(category, "점경 종류");
        meters = PropertyRows.NumberBox(EntourageImport.SuggestedMeters(initialCategory, initialView), "실제 크기");
        meters.ToolTip = "입면은 바닥에서 꼭대기까지 높이, 평면은 긴 쪽 길이(미터)";
        category.SelectionChanged += (_, _) => meters.Text = EntourageImport.SuggestedMeters(Category, view!.Selected).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        void Cell(string caption, FrameworkElement input, int column)
        {
            var label = Theme.Label(caption, Theme.CaptionSize, Theme.Muted); label.Margin = new Thickness(1, 0, 1, 4);
            Grid.SetColumn(label, column); Grid.SetColumn(input, column); Grid.SetRow(input, 1); grid.Children.Add(label); grid.Children.Add(input);
        }
        Cell("종류", category, 0); Cell("실제 크기 · m", meters, 2);
        panel.Children.Add(grid);
        panel.Children.Add(DialogShell.FieldLabel("보기"));
        view = new SegmentedChoice<EntourageView>([(EntourageView.Elevation, "입면·단면"), (EntourageView.Plan, "평면")], initialView);
        view.Changed += v => meters.Text = EntourageImport.SuggestedMeters(Category, v).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        panel.Children.Add(view);
        panel.Children.Add(DialogShell.Note("입면 점경은 아래쪽 끝이 바닥에 닿도록, 평면 점경은 가운데를 기준으로 놓입니다."));
        panel.Children.Add(error);
        var cancel = DialogShell.Secondary("취소", () => DialogResult = false); cancel.IsCancel = true;
        accept = DialogShell.Primary("내 점경에 추가", () => { if (Accept()) DialogResult = true; }); accept.IsDefault = true;
        panel.Children.Add(DialogShell.Footer(cancel, accept));
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); Update(); };
    }

    EntourageCategory Category => category.SelectedItem is ComboBoxItem { Tag: EntourageCategory c } ? c : EntourageCategory.People;
    internal void SetMode(EntourageImportMode value) { mode.Select(value); Update(); }
    internal void SetName(string value) => name.Text = value;
    internal void SetMeters(double value) => meters.Text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    internal void SetView(EntourageView value) => view.Select(value);
    internal void SetCategory(EntourageCategory value) => category.SelectedIndex = (int)value;

    // Converts for the chosen mode. The cut-out mask is made once (on a background thread in the app).
    internal void Update()
    {
        int current = ++generation; var chosen = mode.Selected;
        error.Text = ""; converted = null; accept.IsEnabled = false; preview.Source = null;
        if (chosen == EntourageImportMode.RemoveBackground && mask == null && MaskProvider == null && !Synchronous)
        {
            info.Text = "배경을 지우는 중…";
            var source = image;
            Task.Run(() => BackgroundRemoval.CreateMask(source)).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (current != generation) return;
                if (task.Exception is { } failure) { info.Text = ""; error.Text = failure.InnerException?.Message ?? failure.Message; return; }
                mask = task.Result; Update();
            })), TaskScheduler.Default);
            return;
        }
        try
        {
            converted = EntourageImport.Convert(image, chosen, src => mask ??= (MaskProvider ?? (s => BackgroundRemoval.CreateMask(s)))(src));
            var shown = chosen == EntourageImportMode.LineDrawing ? EntourageRenderer.Style(converted, new EntourageSpec { ItemId = EntourageSpec.CustomId(Guid.NewGuid()), LineDrawing = true, Fill = EntourageFill.None }) : converted;
            preview.Source = shown.Bitmap();
            info.Text = $"{converted.Width} × {converted.Height} px · 투명한 여백은 잘라 냅니다.";
            accept.IsEnabled = true;
        }
        catch (Exception failure) when (failure is InvalidDataException or InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            info.Text = ""; error.Text = failure.Message;
        }
    }

    internal bool Accept()
    {
        if (converted == null) Update();
        if (converted == null) return false;
        if (!double.TryParse(meters.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size) ||
            size < EntourageSpec.MinMeters || size > EntourageSpec.MaxMeters)
        { error.Text = $"실제 크기는 {EntourageSpec.MinMeters}~{EntourageSpec.MaxMeters:0} m로 입력하세요."; return false; }
        Result = new EntourageCustomItem(Guid.NewGuid(), EntourageStore.CleanName(name.Text), Category, view.Selected, size, mode.Selected == EntourageImportMode.LineDrawing, converted);
        return true;
    }
}
