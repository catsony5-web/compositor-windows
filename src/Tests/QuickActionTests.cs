using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunQuickActionTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        test("workspace quick actions are icon-led with complete names in both modes", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(NewDocumentDialog.CreateDocument("빠른 작업", "64", "64", 0), null);
                foreach (bool design in new[] { false, true })
                {
                    window.SetWorkspaceMode(design);
                    // The 점경 palette (design) has its own tiles and filters, checked in MainWindow.EntourageTests.
                    var palette = Descendants(window.studioContents[0]).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetName(e) == "점경 라이브러리");
                    var inPalette = palette == null ? [] : Descendants(palette).ToHashSet();
                    Check(design == (palette != null), "The 점경 palette is not (only) in the design panel");
                    var buttons = Descendants(window.studioContents[0]).OfType<Button>().Where(b => !inPalette.Contains(b)).ToArray();
                    // Photo: 16 commands + 스케치 사진 정리 + four 스타일 효과 tiles + 피사체를 글자 앞으로 + 디자인 스타일; design: 16 + 피사체를 글자 앞으로 + 디자인 스타일.
                    Check(buttons.Length == (design ? 18 : 23), "Quick actions are missing or duplicated");
                    Check(buttons.All(b => b.Content is not string && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)) && b.ToolTip is string { Length: > 0 }), "A quick action is text-only or lacks a name or tooltip");
                    var names = buttons.Select(AutomationProperties.GetName).ToArray();
                    Check(names.Distinct().Count() == names.Length && names.All(n => !n.Contains('…') && !n.Contains("...")), "Quick action names repeat or are abbreviated");
                    Check(window.studioContents[0].Children.OfType<SectionHeader>().Count() == 6, "Quick action groups are not collapsible sections");
                }
                Check(Descendants(window.studioContents[0]).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "선택 레이어 내보내기"), "A shortened label lost its complete name");
                window.AddShape(new Rect(20, 24, 10, 8), false);
                var layer = window.doc.Active!;
                Click(Descendants(window.studioContents[0]).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "캔버스 오른쪽 정렬"));
                double right = new[] { new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height) }.Max(p => DocumentFeatures.ToDocumentSpace(window.doc, layer, p).X);
                Check(Math.Abs(right - window.doc.Width) < 1e-6, "The alignment icon did not align the shape");
                window.Undo();
                Check(window.history.CanRedo, "Icon alignment was not one undo step");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("inspector header and layer commands follow the layer and its lock", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(NewDocumentDialog.CreateDocument("명령", "32", "32", 0), null); window.BuildProperties();
                Check(window.properties.Children[0] is DockPanel identity && Descendants(identity).OfType<TextBlock>().Any(t => t.Text == "이미지 레이어"), "The inspector lacks its kind header");
                Button[] Commands() => window.properties.Children.OfType<UniformGrid>().Last().Children.OfType<Button>().ToArray();
                var names = Commands().Select(AutomationProperties.GetName).ToArray();
                Check(names.SequenceEqual(["레이어 이름 변경", "레이어 마스크 추가", "클리핑 마스크 만들기", "선택 레이어 그룹 만들기"]), "Layer commands changed: " + string.Join(", ", names));
                Check(Commands().All(b => b.IsEnabled), "Unlocked layer commands are disabled");
                window.doc.Active!.Locked = true; window.BuildProperties();
                Check(Commands().All(b => !b.IsEnabled), "Locked layer commands stayed enabled");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("user-facing names avoid third-party product and trademarked feature names", () =>
        {
            string[] banned = ["포토샵", "Photoshop", "Adobe", "어도비", "Illustrator", "일러스트레이터", "AutoCAD", "오토캐드", "Camera Raw", "Lightroom", "라이트룸", "내용 인식", "Content-Aware", "스마트 오브젝트", "Figma", "Apple"];
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(NewDocumentDialog.CreateDocument("이름", "32", "32", 0), null);
                var names = new List<string> { CompatibilityImport.Filter };
                names.AddRange(window.BuildCommandRegistry().SelectMany(c => new[] { c.Title, c.Category }));
                foreach (bool design in new[] { false, true })
                {
                    window.SetWorkspaceMode(design);
                    names.AddRange(Descendants(window.studioContents[0]).OfType<Button>().SelectMany(b => new[] { AutomationProperties.GetName(b), b.ToolTip as string ?? "" }));
                }
                names.AddRange(window.layerCategoryButtons.Values.Select(b => b.Content?.ToString() ?? ""));
                var found = names.SelectMany(name => banned.Where(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)).Select(word => word + " in " + name)).Distinct().ToArray();
                Check(found.Length == 0, "Third-party names in the UI: " + string.Join("; ", found));
                Check(window.layerCategoryButtons.Values.Any(b => Equals(b.Content, "사진 레이어")), "The photo layer tab lost its name");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("quick action grids drop columns before labels break mid-word", () =>
        {
            var buttons = Enumerable.Range(0, 4).Select(i => QuickActions.Command(Theme.Glyphs.Plus, "명령 " + i, () => { }, "명령 " + i)).ToArray();
            var grid = QuickActions.Grid(2, buttons, QuickActions.CommandWidth);
            void Layout(double width) { grid.Measure(new Size(width, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height)); grid.UpdateLayout(); }
            Layout(260); Check(grid.Columns == 1, "A narrow grid kept two columns");
            Layout(360); Check(grid.Columns == 2, "A wide grid did not return to two columns");
            var strip = QuickActions.IconStrip(CanvasAlignments.Select(a => (a.Glyph, a.Name, (Action)(() => { }))), out var icons);
            Check(icons.Length == 6 && icons.All(b => b.Content is Image && AutomationProperties.GetName(b) == (string)b.ToolTip), "The alignment strip lacks named icons");
        });
    }
}
