using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Compositor.Windows;

/// <summary>
/// "PDF · PSD · AI로 내보내기": a list of clearly named file types with a one-line explanation
/// each, what the chosen type keeps or turns into pixels, and a save dialog whose filter and
/// extension follow the choice. Works on a snapshot, so editing continues unaffected.
/// </summary>
internal sealed class CompatibilityExportDialog : Window
{
    readonly Document snapshot;
    readonly ChoiceList<CompatibilityExportFormat> formats;
    readonly CheckBox vectors = new() { Content = "선과 글자를 벡터로 유지 (확대해도 선명)", Margin = new Thickness(2, 2, 2, 6),
        ToolTip = "효과가 있는 레이어만 이미지로 담고, 나머지 선·문자·도형은 벡터로 담습니다." };
    readonly StackPanel details = new();
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, Margin = new Thickness(1, 10, 1, 0) };
    readonly Button save, close;
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? saving;
    CompatibilityExportReport report = new([], null);

    internal ChoiceList<CompatibilityExportFormat> Formats => formats;
    internal CheckBox KeepVectors => vectors;
    internal Button SaveButton => save;
    internal CompatibilityExportReport Report => report;
    internal IEnumerable<string> DetailTexts => details.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBlock>()).Select(t => t.Text);

    internal static string Glyph(CompatibilityExportFormat format) => format switch
    {
        CompatibilityExportFormat.PdfSingle => Theme.Glyphs.Document,
        CompatibilityExportFormat.PsdSingle => Theme.Glyphs.Image,
        CompatibilityExportFormat.AiLayers => Theme.Glyphs.Shape,
        _ => Theme.Glyphs.LayerStack
    };

    public CompatibilityExportDialog(Window? owner, Document document) : this(owner, document, null) { }

    public CompatibilityExportDialog(Window? owner, Document document, CompatibilityExportFormat? initial)
    {
        snapshot = document.Snapshot();
        DialogShell.Prepare(this, owner, "PDF · PSD · AI로 내보내기");
        Width = 580; SizeToContent = SizeToContent.Height; MinWidth = 460; MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40);
        formats = new ChoiceList<CompatibilityExportFormat>(CompatibilityExport.Choices.Select(c => (c.Format, c.Title, c.Description, (string?)Glyph(c.Format))),
            initial ?? CompatibilityExportFormat.PdfSingle);
        System.Windows.Automation.AutomationProperties.SetName(formats, "파일 형식");
        vectors.IsChecked = CompatibilityExport.SuggestVectors(snapshot);

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20), LastChildFill = true }; Content = root;
        close = DialogShell.Secondary("닫기", () => { if (saving != null) saving.Cancel(); else Close(); }); close.IsCancel = true;
        save = DialogShell.Primary("파일로 저장…", () => _ = SaveAsync()); save.IsDefault = true;
        var footer = DialogShell.Footer(close, save); footer.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        DockPanel.SetDock(message, Dock.Bottom); root.Children.Add(message);
        message.Visibility = Visibility.Collapsed;
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(DialogShell.Title("PDF · PSD · AI로 내보내기"));
        var subtitle = Loc.Keep(DialogShell.Subtitle($"{snapshot.Name} · {snapshot.Width:N0} × {snapshot.Height:N0} px · {snapshot.Dpi:0.##} DPI"));
        body.Children.Add(subtitle);
        var label = DialogShell.FieldLabel("파일 형식"); label.Margin = new Thickness(1, 16, 1, 6); body.Children.Add(label);
        body.Children.Add(formats); body.Children.Add(vectors);
        var saved = DialogShell.FieldLabel("저장되는 내용"); saved.Margin = new Thickness(1, 8, 1, 6); body.Children.Add(saved);
        var card = DialogShell.Card(details); card.Padding = new Thickness(12, 8, 12, 10); body.Children.Add(card);

        formats.Changed += _ => Describe();
        vectors.Checked += (_, _) => Describe(); vectors.Unchecked += (_, _) => Describe();
        Describe();
        Closing += (_, e) => { if (saving != null) { e.Cancel = true; saving.Cancel(); } };
        Closed += (_, _) => lifetime.Cancel();
    }

    static Grid Line(string text, bool problem = false)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement mark = problem ? Theme.Glyph(Theme.Glyphs.Warning, 14, Theme.Warning)
            : new TextBlock { Text = "•", FontSize = Theme.CaptionSize, Foreground = Theme.Subtle };
        mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0); row.Children.Add(mark);
        var body = new TextBlock { Text = text, FontSize = Theme.CaptionSize, Foreground = problem ? Theme.Warning : Theme.Muted, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(body, 1); row.Children.Add(body);
        return row;
    }

    void Describe()
    {
        var format = formats.Selected;
        vectors.Visibility = format == CompatibilityExportFormat.PdfSingle ? Visibility.Visible : Visibility.Collapsed;
        report = CompatibilityExport.Describe(snapshot, format, vectors.IsChecked == true);
        details.Children.Clear();
        if (report.Problem != null) details.Children.Add(Line(report.Problem, problem: true));
        foreach (string line in report.Lines) details.Children.Add(Line(line));
        save.IsEnabled = saving == null && report.Problem == null;
        if (saving == null) Say("");
    }

    // The status line only takes space when it has something to say.
    void Say(string text, System.Windows.Media.Brush? color = null)
    {
        message.Foreground = color ?? Theme.Muted; message.Text = text;
        message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetBusy(bool busy)
    {
        formats.IsEnabled = vectors.IsEnabled = !busy;
        save.IsEnabled = !busy && report.Problem == null;
        close.Content = busy ? "저장 취소" : "닫기";
    }

    /// <summary>
    /// Writes the export off the UI thread and replaces <paramref name="path"/> atomically. A cancel
    /// seen before the replacement leaves the file unchanged (OperationCanceledException); once the
    /// file has been replaced the save is complete even if cancel was pressed meanwhile, so the
    /// dialog never reports an unchanged file that was in fact overwritten.
    /// </summary>
    internal static async Task WriteFileAsync(string path, Action<Stream> write, CancellationToken token, Action<string, Action<Stream>>? replace = null)
    {
        replace ??= ProjectStore.AtomicWrite;
        bool replaced = false;
        try
        {
            await CompatibilityImport.OnSta(() =>
            {
                // The last cancel check is inside the write, before the temporary file is moved over the destination.
                replace(path, stream => { write(stream); token.ThrowIfCancellationRequested(); });
                replaced = true; return true;
            }, token);
        }
        catch (OperationCanceledException) when (replaced) { }
    }

    async Task SaveAsync()
    {
        if (saving != null || report.Problem != null) return;
        var format = formats.Selected; var choice = CompatibilityExport.Choice(format); bool keepVectors = vectors.IsChecked == true;
        var picker = new SaveFileDialog
        {
            Filter = CompatibilityExport.Filter(format, Loc.T), DefaultExt = choice.Extension, AddExtension = true, OverwritePrompt = true,
            FileName = CompatibilityExport.FileName(snapshot.Name, format)
        };
        if (picker.ShowDialog(this) != true) return;
        string path = picker.FileName;
        // A name typed with another extension still gets the chosen type's extension.
        if (!string.Equals(Path.GetExtension(path), choice.Extension, StringComparison.OrdinalIgnoreCase))
        {
            path += choice.Extension;
            if (File.Exists(path)) { Say($"{Path.GetFileName(path)} 파일이 이미 있습니다. 확장자를 {choice.Extension}로 바꿔 다시 저장해 주세요.", Theme.Danger); return; }
        }
        var cancel = saving = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = cancel.Token;
        SetBusy(true); Say("파일을 저장하고 있습니다…");
        void Progress(int done, int total) => Dispatcher.InvokeAsync(() => { if (saving == cancel) Say($"레이어 {done}/{total} 저장 중…"); });
        bool completed = false;
        try
        {
            await WriteFileAsync(path, stream => CompatibilityExport.Write(snapshot, format, stream, keepVectors, Progress, token), token);
            completed = true;
        }
        catch (OperationCanceledException) { Say("저장을 취소했습니다. 파일은 바뀌지 않았습니다."); }
        catch (Exception e) { Say(string.Join("\n", "저장하지 못했습니다.", e.Message), Theme.Danger); }
        finally { saving = null; cancel.Dispose(); SetBusy(false); }
        if (completed) Close();
    }
}
