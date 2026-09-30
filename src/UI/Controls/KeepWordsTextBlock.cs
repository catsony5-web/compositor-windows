using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Compositor.Windows;

// Wrapped Korean breaks between words, not between syllables ("keep-all").
// WPF may break Hangul between any two syllables, so a wrapped description can end a line with
// "…받지 않습니" and leave "다." alone below it. TextBlock's layout and rendering are sealed, so this
// block keeps its Text exactly as set — code, self-tests, Loc lookups, tooltips and accessible names
// all read the real string. Only when its own wrapped layout actually splits a Hangul word does it
// show a display copy in which the space before each split word is a line break. (Joining the
// syllables with U+2060 WORD JOINER works too, but formats five to ten times slower in WPF.)
// The copy is a non-hit-testable visual child without an automation peer; the block's own glyphs
// are drawn transparent through its inner Run (its Foreground is untouched and the copy inherits
// it), and its MinHeight follows the copy so an extra line never clips. A word wider than the
// whole line still breaks inside. Kept user data (Loc.Keep), trimming, height-limited and
// non-wrapping blocks, formatted Runs and text without Hangul are left alone.
public class KeepWordsTextBlock : TextBlock
{
    sealed class Face : TextBlock
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }

    Face? face;
    Run? hidden;
    double ownMinHeight;
    bool updating;

    static KeepWordsTextBlock()
    {
        var refresh = new PropertyChangedCallback((d, _) => ((KeepWordsTextBlock)d).Refresh());
        foreach (var property in new[] { TextProperty, TextWrappingProperty, TextTrimmingProperty, PaddingProperty, TextAlignmentProperty, LineHeightProperty, LineStackingStrategyProperty, MaxHeightProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty })
            property.OverrideMetadata(typeof(KeepWordsTextBlock), new FrameworkPropertyMetadata(refresh));
        Loc.KeepProperty.OverrideMetadata(typeof(KeepWordsTextBlock), new PropertyMetadata(false, refresh));
    }

    public KeepWordsTextBlock()
    {
        // Implicit styles match the exact type; keep the theme's TextBlock style (font, size, color).
        SetResourceReference(StyleProperty, typeof(TextBlock));
        SizeChanged += (_, _) => Refresh();
    }

    // The text actually drawn (with line breaks before split words) while the copy is shown.
    public string DisplayedText => Shown ? face!.Text : Text;
    internal TextBlock? DisplayCopy => Shown ? face : null;

    static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '·';
    static bool HasHangul(string text) { foreach (char c in text) if (IsHangul(c)) return true; return false; }

    // True when a line starting at `start` splits a word that contains Hangul ("않습니" / "다.").
    public static bool SplitsWord(string text, int start) =>
        start > 0 && start < text.Length && IsWordChar(text[start - 1]) && IsWordChar(text[start]) && (IsHangul(text[start - 1]) || IsHangul(text[start]));

    // Character index where each wrapped line after the first begins, for a laid-out block with plain text.
    internal static List<int> LineStarts(TextBlock block)
    {
        var starts = new List<int>();
        var origin = block.ContentStart;
        var line = origin.GetLineStartPosition(1);
        while (line != null)
        {
            starts.Add(Math.Max(0, origin.GetOffsetToPosition(line) - 1));
            var next = line.GetLineStartPosition(1);
            if (next == null || next.CompareTo(line) <= 0) break;
            line = next;
        }
        return starts;
    }

    // First line start that splits a word and can move back to a space on the same line; -1 when none.
    static int SpaceBeforeSplit(string text, List<int> starts)
    {
        int lineStart = 0;
        foreach (int start in starts)
        {
            if (SplitsWord(text, start))
            {
                int space = text.LastIndexOf(' ', Math.Min(start, text.Length) - 1, start - lineStart);
                if (space > lineStart) return space;
            }
            lineStart = start;
        }
        return -1;
    }

    bool Eligible => !(bool)GetValue(Loc.KeepProperty) && TextWrapping == TextWrapping.Wrap && TextTrimming == TextTrimming.None && double.IsPositiveInfinity(MaxHeight);

    double LineStep => double.IsNaN(LineHeight) || LineHeight <= 0 ? FontSize * FontFamily.LineSpacing : LineHeight;

    bool SpansLines(double height) => height - Padding.Top - Padding.Bottom > LineStep * 1.6;

    bool Shown => face is { Visibility: Visibility.Visible };

    void Refresh()
    {
        if (updating) return;
        double width = ActualWidth;
        string text = Text;
        // Cheap checks first: single-line labels (most of them) and Latin text never build a copy.
        if (!Eligible || width <= 0 || !HasHangul(text) || !Shown && !SpansLines(ActualHeight)) { Deactivate(); return; }
        updating = true;
        try
        {
            if (face == null)
            {
                face = new Face { IsHitTestVisible = false, Focusable = false, Visibility = Visibility.Hidden };
                // Line positions need the Run-based content; create it before the first layout so reading
                // them never invalidates the copy's measure.
                _ = face.ContentStart;
                Loc.Keep(face);
                ownMinHeight = MinHeight;
                AddVisualChild(face);
            }
            var copy = face;
            copy.Padding = Padding; copy.TextWrapping = TextWrapping; copy.TextAlignment = TextAlignment;
            copy.LineHeight = LineHeight; copy.LineStackingStrategy = LineStackingStrategy; copy.TextDecorations = TextDecorations;
            // Greedy: lay out, break at the space before the first split word, repeat. The first pass
            // is the block's own layout, so an unsplit label stays hidden behind the block.
            string shown = text;
            double height = 0;
            for (int pass = 0; pass < 24; pass++)
            {
                copy.Text = shown;
                copy.Measure(new Size(width, double.PositiveInfinity));
                height = copy.DesiredSize.Height;
                copy.Arrange(new Rect(0, 0, width, height));
                int next = SpaceBeforeSplit(shown, LineStarts(copy));
                if (next < 0) break;
                shown = Broken(shown, next);
            }
            // Formatted text (several Runs, bold spans) keeps its own layout.
            if (ReferenceEquals(shown, text) || Inlines.Count != 1 || Inlines.FirstInline is not Run run) { Hide(); return; }
            copy.Visibility = Visibility.Visible;
            if (!ReferenceEquals(hidden, run)) { hidden?.ClearValue(TextElement.ForegroundProperty); hidden = run; run.Foreground = Brushes.Transparent; }
            // Moving words down never needs fewer lines, so max(own, copy) is the block's height.
            // Skip the extra layout pass when the block is already tall enough.
            double minimum = Math.Max(ownMinHeight, height);
            bool raised = Math.Abs(MinHeight - ownMinHeight) > 0.01;
            if ((raised || height > ActualHeight + 0.5) && Math.Abs(MinHeight - minimum) > 0.01) MinHeight = minimum;
        }
        finally { updating = false; }
    }

    // The block draws itself again; the copy stays as a hidden probe for the next change.
    void Hide()
    {
        if (face != null) face.Visibility = Visibility.Hidden;
        hidden?.ClearValue(TextElement.ForegroundProperty); hidden = null;
        if (Math.Abs(MinHeight - ownMinHeight) > 0.01) MinHeight = ownMinHeight;
    }

    static string Broken(string text, int space) => string.Concat(text.AsSpan(0, space), "\n", text.AsSpan(space + 1));

    void Deactivate()
    {
        if (face == null) return;
        bool was = updating; updating = true;
        try { Hide(); RemoveVisualChild(face); face = null; }
        finally { updating = was; }
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + (face == null ? 0 : 1);

    protected override Visual GetVisualChild(int index) => face != null && index == base.VisualChildrenCount ? face : base.GetVisualChild(index);
}
