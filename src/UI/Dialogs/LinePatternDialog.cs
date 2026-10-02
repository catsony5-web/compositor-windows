using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// 내 패턴 추가: a picked image becomes a line pattern. The preview repeats the converted tile 2×2 on
// paper so seams show; the line threshold starts at the automatic value and blank margins can be
// trimmed. Accept builds Result (a LinePatterns asset); the caller registers it.
public sealed class LinePatternDialog : Window
{
    readonly LinePatternSource source;
    readonly TextBox name;
    readonly ParameterSlider threshold;
    readonly CheckBox trim = new() { Content = "가장자리 빈 여백 자르기", IsChecked = false, Margin = new Thickness(1, 10, 1, 0) };
    readonly Border paper = new() { Background = Brushes.White, Height = 200, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    readonly System.Windows.Shapes.Rectangle preview = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock size = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly TextBlock error = DialogShell.ErrorText();
    readonly Button accept;
    Raster? converted;

    public MaterialAsset? Result { get; private set; }
    internal Raster? Converted => converted;
    internal string? Error => string.IsNullOrEmpty(error.Text) ? null : error.Text;

    public LinePatternDialog(Window? owner, LinePatternSource source, string suggestedName)
    {
        this.source = source;
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        DialogShell.Prepare(this, owner, "내 패턴 추가");
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; Content = panel;
        panel.Children.Add(DialogShell.Title("이미지로 선 패턴 만들기"));
        panel.Children.Add(DialogShell.Subtitle("어두운 선은 잉크로, 밝은 바탕은 투명하게 바꿔 반복되는 선 패턴으로 등록합니다."));
        paper.Child = preview; paper.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(paper);
        size.Margin = new Thickness(1, 6, 1, 0); panel.Children.Add(size);
        panel.Children.Add(DialogShell.FieldLabel("패턴 이름"));
        name = new TextBox { Text = LinePatternStore.CleanName(suggestedName), Margin = new Thickness(0), MaxLength = LinePatternStore.MaxNameLength };
        System.Windows.Automation.AutomationProperties.SetName(name, "패턴 이름");
        panel.Children.Add(name);
        double auto = Math.Round(source.AutoThreshold * 100);
        threshold = new ParameterSlider("선 인식 기준 %", 1, 99, auto, auto)
        {
            Margin = new Thickness(0, 12, 0, 0),
            ToolTip = "이 값보다 어두운 부분을 선으로 봅니다. 처음 값은 이미지에서 자동으로 정했습니다."
        };
        threshold.Changed += _ => Update();
        panel.Children.Add(threshold);
        panel.Children.Add(DialogShell.Note("흐린 선이 빠지면 값을 낮추고, 종이 얼룩이 선으로 보이면 높이세요."));
        trim.ToolTip = "그림 둘레의 빈 종이를 잘라 반복 간격을 그림에 맞춥니다. 타일의 여백이 무늬 간격이면 끄세요.";
        trim.Checked += (_, _) => Update(); trim.Unchecked += (_, _) => Update();
        panel.Children.Add(trim);
        panel.Children.Add(error);
        var cancel = DialogShell.Secondary("취소", () => DialogResult = false); cancel.IsCancel = true;
        accept = DialogShell.Primary("패턴 추가", () => { if (Accept()) DialogResult = true; }); accept.IsDefault = true;
        panel.Children.Add(DialogShell.Footer(cancel, accept));
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        Update();
    }

    internal void SetThreshold(double percent) { threshold.SetValue(percent); Update(); }
    internal void SetTrim(bool value) { trim.IsChecked = value; Update(); }
    internal void SetName(string value) => name.Text = value;

    void Update()
    {
        try
        {
            converted = source.Convert(threshold.Value / 100, trim.IsChecked == true);
            error.Text = ""; accept.IsEnabled = true;
            preview.Fill = PreviewBrush(converted, out double width, out double height);
            preview.Width = width; preview.Height = height;
            size.Text = $"패턴 타일 {converted.Width}×{converted.Height}px · 미리보기는 2×2로 반복합니다.";
        }
        catch (InvalidDataException failure)
        {
            converted = null; error.Text = failure.Message; accept.IsEnabled = false;
            preview.Fill = null; size.Text = "";
        }
    }

    // The tile in the default ink, repeated twice across and down inside the paper card.
    ImageBrush PreviewBrush(Raster tile, out double width, out double height)
    {
        var data = new byte[tile.Data.Length];
        byte b = unchecked((byte)HatchPatterns.DefaultInk), g = unchecked((byte)(HatchPatterns.DefaultInk >> 8)), r = unchecked((byte)(HatchPatterns.DefaultInk >> 16));
        for (int i = 0; i < data.Length; i += 4) { data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = tile.Data[i + 3]; }
        var bitmap = BitmapSource.Create(tile.Width, tile.Height, 96, 96, PixelFormats.Bgra32, null, data, tile.Width * 4); bitmap.Freeze();
        double available = Math.Max(120, Width - 48 - 16), scale = Math.Min((available) / (2d * tile.Width), (paper.Height - 16) / (2d * tile.Height));
        width = tile.Width * scale * 2; height = tile.Height * scale * 2;
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, width / 2, height / 2), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); brush.Freeze();
        return brush;
    }

    internal bool Accept()
    {
        threshold.TryCommit();
        Update();
        if (converted == null) return false;
        try { Result = LinePatterns.Create(LinePatternStore.CleanName(name.Text), converted); return true; }
        catch (InvalidDataException failure) { error.Text = failure.Message; return false; }
    }
}
