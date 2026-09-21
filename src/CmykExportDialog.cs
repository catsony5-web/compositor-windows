using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Compositor.Windows;

/// <summary>Print output and profile round-trip preview of an immutable document snapshot.</summary>
public sealed class CmykExportDialog : Window
{
    readonly Document snapshot;
    readonly Image originalImage = new() { Stretch = Stretch.Uniform };
    readonly Image proofImage = new() { Stretch = Stretch.Uniform };
    readonly TextBlock profileLabel = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 8, 3, 8) };
    readonly TextBlock printSize = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBox dpiBox = new() { Text = "300", Width = 75 };
    readonly Button chooseProfile, defaultProfile, exportButton;
    Raster? rendered;
    string? profilePath;
    bool closed, busy;

    public CmykExportDialog(Window owner, Document document)
    {
        snapshot = document.Snapshot();
        Owner = owner; Title = "Morupixel · CMYK 인쇄용 내보내기";
        Width = 990; Height = 710; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel; Foreground = Theme.Text;
        FontFamily = new FontFamily("Malgun Gothic");

        var root = new Grid { Margin = new Thickness(22) }; Content = root;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.Children.Add(Theme.Label("인쇄소의 ICC 프로필로 색상을 변환하세요.", 20));

        var profileRow = new Grid { Margin = new Thickness(3, 12, 3, 5) };
        profileRow.ColumnDefinitions.Add(new ColumnDefinition());
        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profileRow.Children.Add(profileLabel);
        chooseProfile = Theme.Button("ICC 프로필 선택…", ChooseProfile);
        defaultProfile = Theme.Button("Windows 기본값", () => { profilePath = null; _ = RefreshPreviewAsync(); });
        Grid.SetColumn(chooseProfile, 1); Grid.SetColumn(defaultProfile, 2);
        profileRow.Children.Add(chooseProfile); profileRow.Children.Add(defaultProfile);
        Grid.SetRow(profileRow, 1); root.Children.Add(profileRow);

        var dimensions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        dimensions.Children.Add(Theme.Label("해상도 (DPI)", 12)); dimensions.Children.Add(dpiBox); dimensions.Children.Add(printSize);
        Grid.SetRow(dimensions, 2); root.Children.Add(dimensions);
        dpiBox.TextChanged += (_, _) => UpdatePrintSize(); UpdatePrintSize();

        var images = new Grid();
        images.ColumnDefinitions.Add(new ColumnDefinition()); images.ColumnDefinitions.Add(new ColumnDefinition());
        images.Children.Add(PreviewPanel("sRGB 원본", originalImage));
        var proof = PreviewPanel("CMYK 프로필 변환 미리보기", proofImage);
        Grid.SetColumn(proof, 1); images.Children.Add(proof); Grid.SetRow(images, 3); root.Children.Add(images);

        var notes = new StackPanel(); notes.Children.Add(status);
        notes.Children.Add(new TextBlock
        {
            Text = "투명 영역은 흰색으로 합쳐집니다. ICC 프로필을 포함한 CMYK TIFF로 저장하며 원본 레이어는 유지됩니다.\n미리보기는 프로필 변환 결과이며, 실제 인쇄색은 인쇄소 프로필·용지·모니터 설정에 따라 달라집니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 4, 3, 12), FontSize = 11
        });
        Grid.SetRow(notes, 4); root.Children.Add(notes);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = Theme.Button("닫기", Close); close.IsCancel = true;
        exportButton = Theme.Button("CMYK TIFF 저장…", () => _ = SaveAsync());
        exportButton.Background = Theme.Brush("#285848"); exportButton.IsEnabled = false;
        actions.Children.Add(close); actions.Children.Add(exportButton); Grid.SetRow(actions, 5); root.Children.Add(actions);
        Loaded += (_, _) => _ = RefreshPreviewAsync();
        Closed += (_, _) => closed = true;
    }

    static Grid PreviewPanel(string title, Image image)
    {
        var panel = new Grid { Margin = new Thickness(4) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(Theme.Label(title, 12));
        var border = new Border { Background = Brushes.White, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = image };
        Grid.SetRow(border, 1); panel.Children.Add(border); return panel;
    }

    void SetBusy(bool value)
    {
        busy = value; chooseProfile.IsEnabled = !value; defaultProfile.IsEnabled = !value;
        dpiBox.IsEnabled = !value; exportButton.IsEnabled = !value && rendered != null && proofImage.Source != null;
    }

    void UpdatePrintSize()
    {
        if (!double.TryParse(dpiBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi) || !double.IsFinite(dpi) || dpi < 1 || dpi > 9600)
            printSize.Text = "DPI: 1–9600";
        else printSize.Text = $"{snapshot.Width} × {snapshot.Height} px  ·  {snapshot.Width / dpi * 2.54:0.##} × {snapshot.Height / dpi * 2.54:0.##} cm";
    }

    void ChooseProfile()
    {
        var open = new OpenFileDialog { Title = "인쇄소에서 제공한 CMYK ICC 프로필 선택", Filter = "ICC 색상 프로필|*.icc;*.icm", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        profilePath = open.FileName; _ = RefreshPreviewAsync();
    }

    async Task RefreshPreviewAsync()
    {
        if (busy || closed) return;
        SetBusy(true); proofImage.Source = null; status.Text = "색상 프로필과 인쇄 미리보기를 준비하고 있습니다…";
        string? requestedProfile = profilePath;
        try
        {
            var result = await Task.Run(() =>
            {
                string name = CmykExport.ProfileName(requestedProfile);
                var source = rendered ?? Imaging.Render(snapshot);
                var proof = CmykExport.Preview(source, requestedProfile);
                return (Name: name, Source: source, Original: Thumbnail(source), Proof: Thumbnail(proof));
            });
            if (closed) return;
            rendered = result.Source; profileLabel.Text = result.Name;
            originalImage.Source = result.Original; proofImage.Source = result.Proof;
            status.Text = "이 프로필로 변환한 색상을 확인한 뒤 CMYK TIFF로 저장할 수 있습니다.";
        }
        catch (Exception ex)
        {
            if (!closed) { profileLabel.Text = requestedProfile == null ? "Windows 기본 CMYK 프로필" : Path.GetFileName(requestedProfile); status.Text = ex.Message; }
        }
        finally { if (!closed) SetBusy(false); }
    }

    static BitmapSource Thumbnail(Raster raster)
    {
        var bitmap = raster.Bitmap(); double scale = Math.Min(1, 850.0 / Math.Max(raster.Width, raster.Height));
        if (scale == 1) return bitmap;
        var thumbnail = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)); thumbnail.Freeze(); return thumbnail;
    }

    async Task SaveAsync()
    {
        if (busy || rendered == null || proofImage.Source == null || closed) return;
        double dpi;
        try { dpi = Dialogs.Number(dpiBox.Text, 1, 9600); }
        catch (Exception ex) { status.Text = ex.Message; return; }
        string name = string.Concat(snapshot.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var save = new SaveFileDialog { Title = "ICC 프로필을 포함한 CMYK TIFF 저장", Filter = "CMYK TIFF|*.tif", DefaultExt = ".tif", AddExtension = true, FileName = name + "-CMYK.tif" };
        if (save.ShowDialog(this) != true) return;
        var source = rendered; string? requestedProfile = profilePath;
        SetBusy(true); status.Text = "CMYK TIFF를 저장하고 있습니다…";
        try
        {
            await Task.Run(() => ProjectStore.AtomicWrite(save.FileName, stream => CmykExport.Write(source, stream, requestedProfile, dpi)));
            if (!closed) status.Text = $"저장 완료: {save.FileName}";
        }
        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally { if (!closed) SetBusy(false); }
    }
}
