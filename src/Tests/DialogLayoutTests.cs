using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Layout checks for dialog defects found in the offscreen review: inner padding in captures,
// translated text cut off by fixed widths, buttons crowding their neighbours, misaligned fields.
public static class DialogLayoutTests
{
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Visuals(child)) yield return nested;
        }
    }

    // The self-test process has no Application, so the implicit control styles of Theme.xaml
    // (field margins, drop-down template) are attached to the host the way the app applies them.
    static ResourceDictionary? theme;
    internal static T Themed<T>(T host) where T : FrameworkElement
    {
        if (theme == null)
        {
            _ = Application.Current; // registers the pack://application scheme
            theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Morupixel;component/UI/Theme.xaml", UriKind.Absolute) };
        }
        host.Resources.MergedDictionaries.Add(theme); host.Resources["UiFont"] = Theme.UiFont;
        return host;
    }

    internal static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }

    internal static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    // True when the element or an ancestor up to (and including) stop was arranged smaller than it asked for.
    internal static bool Clipped(FrameworkElement element, FrameworkElement stop)
    {
        for (DependencyObject? node = element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement framework && LayoutInformation.GetLayoutClip(framework) is { } clip
                && (clip.Bounds.Width + .5 < framework.RenderSize.Width || clip.Bounds.Height + .5 < framework.RenderSize.Height)) return true;
            if (ReferenceEquals(node, stop)) break;
        }
        return false;
    }

    internal static TextBlock SelectionText(ComboBox combo) =>
        Visuals(combo).OfType<TextBlock>().First(block => block.Text.Length > 0);

    static void InLanguage(string code, Action action)
    {
        string previous = Loc.Language;
        try { Loc.Use(code); action(); }
        finally { Loc.Use(previous); }
    }

    static Document SampleDocument()
    {
        var document = new Document { Width = 64, Height = 48 };
        document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(64, 48, Colors.SteelBlue) });
        return document;
    }

    public static void Run(Action<string, Action> test, string directory)
    {
        test("offscreen dialog captures paint the window background through the root margin", () =>
        {
            var dialog = new CmykExportDialog(null!, SampleDocument());
            try
            {
                var host = Themed(OffscreenPreview.Host(dialog));
                Layout(host, 990, 710);
                var image = new RenderTargetBitmap(990, 710, 96, 96, PixelFormats.Pbgra32); image.Render(host);
                var panel = ((SolidColorBrush)Theme.Panel).Color;
                foreach (var (x, y) in new[] { (4, 4), (985, 4), (4, 705), (985, 705) })
                {
                    var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    Check(pixel[3] == 255 && pixel[2] == panel.R && pixel[1] == panel.G && pixel[0] == panel.B, $"Dialog margin at {x},{y} is not the panel background");
                }
                // The footer keeps the dialog's inner padding below and beside it.
                var save = Visuals(host).OfType<Button>().Single(button => button.Content as string == "CMYK TIFF 저장…");
                var bounds = Bounds(save, host);
                Check(bounds.Bottom <= 710 - 16 && bounds.Right <= 990 - 16, "Footer buttons touch the dialog edge");
            }
            finally { dialog.Close(); }
        });

        test("parameter step mode drop-down fits the English \"Default\" instead of cutting it", () => InLanguage("en", () =>
        {
            var slider = new ParameterSlider("반경 px", 1, 30, 5, 5, showStepControls: true, minimumStep: 1);
            var host = Themed(new Border { Child = slider });
            Layout(host, 400, 200); Loc.PrepareOffscreen(host);
            var combo = Visuals(host).OfType<ComboBox>().Single();
            var text = SelectionText(combo);
            Check(text.Text == Loc.T("기본") && text.Text != "기본", "The step mode is not translated in the test");
            Check(!Clipped(text, combo), $"Step mode \"{text.Text}\" is cut off (width {combo.ActualWidth:0.#})");
            Check(combo.ActualWidth >= 68, "Numeric steps lost their minimum width");
        }));

        test("new document fields line up and the size summary sits unclipped with the create button", () =>
        {
            var dialog = new NewDocumentDialog(null, Path.Combine(directory, "new-document-layout-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                var host = Themed(OffscreenPreview.Host(dialog));
                // Approximate client area of the default 1020 × 760 window.
                Layout(host, 1004, 721);
                var inputs = dialog.SettingInputsForTest;
                var name = Bounds(inputs[0], host);
                foreach (var input in inputs)
                {
                    var bounds = Bounds(input, host);
                    if (input is ComboBox or TextBox && !ReferenceEquals(input, inputs[2]) && !ReferenceEquals(input, inputs[3]))
                        Check(Math.Abs(bounds.Left - name.Left) < .6 && Math.Abs(bounds.Right - name.Right) < .6, $"{input.GetType().Name} edges {bounds.Left:0.#}–{bounds.Right:0.#} differ from the text boxes {name.Left:0.#}–{name.Right:0.#}");
                }
                Check(Math.Abs(Bounds(inputs[2], host).Left - name.Left) < .6 && Math.Abs(Bounds(inputs[3], host).Right - name.Right) < .6, "Width/height row does not line up with the other fields");
                // Text inside the drop-downs starts where text inside the text boxes starts.
                var textStart = Visuals(inputs[0]).OfType<FrameworkElement>().First(v => v.GetType().Name == "TextBoxView");
                foreach (var combo in inputs.OfType<ComboBox>())
                {
                    var selection = (FrameworkElement)combo.Template.FindName("Selection", combo);
                    double delta = Bounds(selection, host).Left - Bounds(textStart, host).Left;
                    Check(Math.Abs(delta) <= 1, $"Drop-down text is {delta:0.#} DIP off the text box text");
                }
                var summary = dialog.SummaryForTest;
                for (DependencyObject? node = summary; node != null; node = VisualTreeHelper.GetParent(node))
                    Check(node is not ScrollViewer, "The size summary is inside the scrolling settings card");
                var summaryBounds = Bounds(summary, host);
                Check(!Clipped(summary, (FrameworkElement)host.Child) && summaryBounds.Height > 8 && summaryBounds.Bottom <= 721, "The size summary is clipped");
                var size = (SavedDocumentSize)dialog.Cards[0].Tag;
                Check(summary.Text.StartsWith(NewDocumentDialog.Describe(size), StringComparison.Ordinal), $"Summary \"{summary.Text}\" and card \"{NewDocumentDialog.Describe(size)}\" format the size differently");
                var settings = Visuals(host).OfType<ScrollViewer>().First(viewer => viewer.Content is StackPanel panel && Visuals(panel).Contains(inputs[0]));
                Check(settings.ExtentHeight <= settings.ViewportHeight + .5, $"Settings need {settings.ExtentHeight:0} DIP but the card shows {settings.ViewportHeight:0} at the default size");
            }
            finally { dialog.Close(); }
        });

        test("photo develop actions keep a gap below the settings panel and their normal height", () =>
        {
            var dialog = new AdjustmentDialog(null, SampleDocument(), new AdjustmentSpec { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = new() });
            try
            {
                var host = Themed(OffscreenPreview.Host(dialog));
                Layout(host, 1040, 760);
                var panel = Bounds(Visuals(host).OfType<GlassPanel>().First(), host);
                foreach (var label in new[] { "취소", "조정 적용" })
                {
                    var button = Bounds(Visuals(host).OfType<Button>().Single(b => b.Content as string == label), host);
                    Check(button.Top - panel.Bottom >= 8, $"{label} sits {button.Top - panel.Bottom:0.#} DIP under the settings panel");
                    Check(button.Height <= 40, $"{label} is stretched to {button.Height:0.#} DIP");
                    Check(760 - button.Bottom >= 8, $"{label} touches the dialog bottom");
                }
            }
            finally { dialog.Close(); }
        });

        test("inline property captions use the same caption style and inset as field captions", () =>
        {
            var inline = PropertyRows.Inline("불투명도 · %", PropertyRows.NumberBox(50, "불투명도"));
            var field = PropertyRows.Field("혼합 모드", PropertyRows.Choice("혼합 모드"));
            var a = inline.Children.OfType<TextBlock>().Single(); var b = field.Children.OfType<TextBlock>().Single();
            Check(a.FontSize == b.FontSize && ReferenceEquals(a.Foreground, b.Foreground) && a.Margin.Left == b.Margin.Left,
                $"Inline caption {a.FontSize}/{a.Margin.Left} differs from field caption {b.FontSize}/{b.Margin.Left}");
        });
    }
}
