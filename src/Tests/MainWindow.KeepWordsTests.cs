using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Compositor.Windows;

// Wrapped Korean breaks only between words (KeepWordsTextBlock), while Text, Loc and accessible
// names keep the source string. The texts are the Preview 37 review cases.
public sealed partial class MainWindow
{
    internal static void RunKeepWordsTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static bool Hangul(char c) => c is >= '\uAC00' and <= '\uD7A3';
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '·';

        // Character indices where each wrapped line after the first begins (independent of the control's own helper).
        static List<int> LineStarts(TextBlock block)
        {
            var starts = new List<int>();
            var line = block.ContentStart.GetLineStartPosition(1);
            while (line != null)
            {
                starts.Add(new TextRange(block.ContentStart, line).Text.Replace("\r\n", "\n").Length);
                var next = line.GetLineStartPosition(1);
                if (next == null || next.CompareTo(line) <= 0) break;
                line = next;
            }
            return starts;
        }
        // A break inside a word that contains Hangul, e.g. "않습니" / "다.".
        static bool MidWord(string text, int start) => start > 0 && start < text.Length && Word(text[start - 1]) && Word(text[start]) && (Hangul(text[start - 1]) || Hangul(text[start]));
        static void Layout(FrameworkElement element, double width)
        {
            if (element.Parent is Border previous) previous.Child = null;
            var host = new Border { Width = width, Child = element };
            host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
        }
        static TextBlock Plain(KeepWordsTextBlock source) => new()
        {
            Text = source.Text, FontFamily = source.FontFamily, FontSize = source.FontSize, TextWrapping = TextWrapping.Wrap,
            Padding = source.Padding, TextAlignment = source.TextAlignment
        };

        // The copy may only turn spaces into line breaks, and no line may start inside a Hangul word.
        static void Verify(KeepWordsTextBlock block, string text, string where)
        {
            var copy = block.DisplayCopy;
            string shown = copy?.Text ?? block.Text;
            Check(block.Text == text && block.DisplayedText == shown, $"Text changed {where}");
            Check(shown.Length == text.Length && Enumerable.Range(0, text.Length).All(i => shown[i] == text[i] || text[i] == ' ' && shown[i] == '\n'), $"The display copy changed more than spaces {where}");
            Check(LineStarts((TextBlock?)copy ?? block).All(start => !MidWord(shown, start)), $"'{text}' broke inside a word {where}");
        }

        test("keep-words detects lines that split a Hangul word", () =>
        {
            string text = "연결을 끄면 새 명령을 받지 않습니다.";
            Check(KeepWordsTextBlock.SplitsWord(text, text.IndexOf("다.")), "A split inside '않습니다' was not detected");
            Check(!KeepWordsTextBlock.SplitsWord(text, text.IndexOf("않")) && !KeepWordsTextBlock.SplitsWord(text, text.IndexOf(".")), "A break at a space or before punctuation counted as a split");
            Check(KeepWordsTextBlock.SplitsWord("PDF로", 3) && KeepWordsTextBlock.SplitsWord("구조·벽", 2) && !KeepWordsTextBlock.SplitsWord("Brush panel", 3), "Mixed words, tight separators or Latin words are misjudged");
            Check(!KeepWordsTextBlock.SplitsWord(text, 0) && !KeepWordsTextBlock.SplitsWord(text, text.Length) && !KeepWordsTextBlock.SplitsWord("", 0), "Edges counted as splits");
        });

        test("wrapped Korean review texts break only between words and keep their Text and names", () =>
        {
            var cases = new (string Text, double Size, double From, double To)[]
            {
                ("같은 Windows 계정의 로컬 도구가 문서와 파일을 편집합니다. 연결을 끄면 새 명령을 받지 않습니다.", Theme.BodySize, 140, 640),
                ("누르면 선택 영역 모양의 재질 레이어를 도면 선 아래에 만듭니다.", Theme.CaptionSize, 110, 420),
                ("그림자가 드리우는 모양을 또렷한 면이나 윤곽선으로 보여 줍니다. 일조 검토와 모양 다듬기에 알맞습니다.", Theme.CaptionSize, 110, 520),
                ("현재 문서 닫기", Theme.CaptionSize, 40, 90),
            };
            foreach (var (text, size, from, to) in cases)
            {
                bool plainSplit = false, copied = false;
                for (double width = from; width <= to; width += 3)
                {
                    var block = (KeepWordsTextBlock)Theme.Label(text, size, Theme.Muted); block.Margin = new Thickness(0);
                    Layout(block, width);
                    var plain = Plain(block); Layout(plain, width);
                    plainSplit |= LineStarts(plain).Any(start => MidWord(text, start));
                    Check(block.Text == text, $"Text changed at {width}");
                    Check(UIElementAutomationPeer.CreatePeerForElement(block).GetName() == text, $"The accessible name changed at {width}");
                    var copy = block.DisplayCopy;
                    Verify(block, text, $"at {width}");
                    if (copy == null) continue;
                    copied = true;
                    Check(block.ActualHeight + 0.5 >= copy.DesiredSize.Height, $"The display copy is taller than its block at {width}");
                    Check(copy.Foreground == block.Foreground && copy.FontSize == block.FontSize, "The display copy does not inherit the block's font and color");
                    Check(block.Inlines.FirstInline is Run { Foreground: var hidden } && hidden == Brushes.Transparent, "The block's own glyphs are still drawn");
                    Check(UIElementAutomationPeer.CreatePeerForElement(block).GetChildren() is null or { Count: 0 }, "The display copy is exposed to accessibility");
                    Check(!copy.IsHitTestVisible && (bool)copy.GetValue(Loc.KeepProperty), "The display copy takes input or translation");
                }
                Check(copied, $"'{text}' never needed a display copy");
                Check(plainSplit, $"The plain block never split '{text}' inside a word; the check proves nothing");
            }
        });

        test("keep-words leaves single lines, user data and non-wrapping text untouched and follows text changes", () =>
        {
            string longText = "그림자가 드리우는 모양을 또렷한 면이나 윤곽선으로 보여 줍니다. 일조 검토와 모양 다듬기에 알맞습니다.";
            var single = (KeepWordsTextBlock)Theme.Label("기준 레이어"); Layout(single, 300);
            Check(single.DisplayCopy == null && VisualTreeHelper.GetChildrenCount(single) == 0 && single.DisplayedText == "기준 레이어", "A single-line label built a display copy");
            var kept = Loc.Keep((KeepWordsTextBlock)Theme.Label(longText)); Layout(kept, 200);
            Check(kept.DisplayCopy == null, "User data marked Keep was re-wrapped");
            var noWrap = (KeepWordsTextBlock)Theme.Label(longText); noWrap.TextWrapping = TextWrapping.NoWrap; Layout(noWrap, 200);
            Check(noWrap.DisplayCopy == null, "A non-wrapping label built a display copy");
            var trimmed = (KeepWordsTextBlock)Theme.Label(longText); trimmed.TextTrimming = TextTrimming.CharacterEllipsis; Layout(trimmed, 200);
            Check(trimmed.DisplayCopy == null, "A trimmed label built a display copy");

            var block = (KeepWordsTextBlock)Theme.Label(longText, Theme.CaptionSize); Layout(block, 200);
            Check(block.DisplayCopy != null && VisualTreeHelper.GetChildrenCount(block) == 1, "A wrapped Korean label has no display copy");
            Verify(block, longText, "at 200");
            foreach (double width in new[] { 150.0, 230, 310 }) { Layout(block, width); Verify(block, longText, $"after resizing to {width}"); }
            string changed = "물체에 닿은 곳은 선명하고, 멀어질수록 부드럽게 퍼집니다. 그림자 종류를 바꾸면 이 설명도 바뀝니다.";
            block.Text = changed; Layout(block, 200);
            Verify(block, changed, "after a text change");
            block.Text = "Shadow shape shows where the shadow falls with clear faces or outlines.";
            Layout(block, 200);
            Check(block.DisplayCopy == null && VisualTreeHelper.GetChildrenCount(block) == 0 && block.MinHeight == 0, "Latin text kept the display copy or its height");
            Check(block.Inlines.FirstInline is not Run run || run.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue, "The block's glyphs stayed hidden");

            var formatted = (KeepWordsTextBlock)Theme.Label(""); formatted.Inlines.Add(new Run("굵은 ") { FontWeight = FontWeights.Bold }); formatted.Inlines.Add(new Run(longText));
            Layout(formatted, 200);
            Check(formatted.DisplayCopy == null, "Formatted runs were replaced by a plain copy");
        });

        test("translated labels keep English layout and panel, note, button and ribbon labels keep Korean words", () =>
        {
            try
            {
                Loc.Use("en", new Dictionary<string, string> { ["누르면 선택 영역 모양의 재질 레이어를 도면 선 아래에 만듭니다."] = "Click to create a material layer shaped like the selection under the drawing lines." });
                var block = (KeepWordsTextBlock)Theme.Label("누르면 선택 영역 모양의 재질 레이어를 도면 선 아래에 만듭니다.", Theme.CaptionSize);
                Loc.Apply(block); Layout(block, 160);
                Check(block.Text.StartsWith("Click to create") && block.DisplayCopy == null, "An English label built a Korean display copy");
            }
            finally { Loc.Use("ko", null); }

            Check(Theme.Label("설명") is KeepWordsTextBlock && DialogShell.Note("설명") is KeepWordsTextBlock, "Labels and dialog notes do not keep Korean words");
            var tile = QuickActions.Tile(Theme.Glyphs.Export, "선택 레이어 내보내기", () => { }, "선택 레이어 내보내기");
            Check(tile.Content is StackPanel tileContent && tileContent.Children.OfType<KeepWordsTextBlock>().Count() == 1, "Tile labels do not keep Korean words");
            var feature = QuickActions.Feature(Theme.Glyphs.Export, "제목", "설명 문장", () => { }, "제목");
            Check(((Grid)feature.Content).Children.OfType<StackPanel>().Single().Children.OfType<KeepWordsTextBlock>().Count() == 2, "Feature card texts do not keep Korean words");

            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.SetRibbonMode(true); w.SelectRibbonTab("파일");
                static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
                {
                    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                    {
                        yield return child;
                        foreach (var nested in Descendants(child)) yield return nested;
                    }
                }
                var close = Descendants(w.ribbonBody!).OfType<Button>().First(b => AutomationProperties.GetName(b) == "현재 문서 닫기");
                var label = ((StackPanel)close.Content).Children.OfType<KeepWordsTextBlock>().Single();
                close.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); close.Arrange(new Rect(close.DesiredSize)); close.UpdateLayout();
                Check(label.Text == "현재 문서 닫기", "The ribbon label text changed");
                Verify(label, "현재 문서 닫기", "in the ribbon");
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }
}
