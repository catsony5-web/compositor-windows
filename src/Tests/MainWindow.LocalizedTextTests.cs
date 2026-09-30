using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

// Display-language details found in the Preview 37 review: plural-free English counts, app-made
// layer names in the display language, user file names kept out of translation, one-sentence
// range hints, translated command palette search results and a readable CAD layer-role summary.
public sealed partial class MainWindow
{
    internal static void RunLocalizedTextTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static bool Hangul(string text) => text.Any(c => c is >= '가' and <= '힣');
        static void InLanguage(string code, Action body)
        {
            string previous = Loc.Language;
            try { Loc.Use(code); body(); } finally { Loc.Use(previous); }
        }
        static IEnumerable<DependencyObject> Tree(DependencyObject node)
        {
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                foreach (var item in Tree(child)) yield return item;
        }
        static string Shown(TextBlock block) => block.Inlines.Count == 0 ? block.Text : string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));
        string root = Path.Combine(directory, "localized-text"); Directory.CreateDirectory(root);

        test("display translation handles a single formatted run and leaves kept runs alone", () =>
        {
            try
            {
                Loc.Use("en", new Dictionary<string, string> { ["도구"] = "Tools", ["가져오는 중 {0}/{1} · {2}"] = "Importing {0}/{1} · {2}", ["층 평면도"] = "floor plan" });
                var single = new TextBlock(); single.Inlines.Add(new Run("도구")); Loc.Apply(single);
                Check(Shown(single) == "Tools", "A TextBlock built from one run was not translated: " + Shown(single));
                var progress = new TextBlock(); Loc.SetText(progress, "가져오는 중 {0}/{1} · {2}", 2, 3, "3층 평면도.dwg"); Loc.Apply(progress);
                Check(Shown(progress) == "Importing 2/3 · 3층 평면도.dwg", "A file name inside a translated sentence changed: " + Shown(progress));
                Check(Loc.Format("가져오는 중 {0}/{1} · {2}", 2, 3, "3층 평면도.dwg") == "Importing 2/3 · 3층 평면도.dwg", "Loc.Format translated a value");
            }
            finally { Loc.Use("ko", null); }
            var korean = new TextBlock(); Loc.SetText(korean, "가져오는 중 {0}/{1} · {2}", 2, 3, "3층 평면도.dwg");
            Check(Shown(korean) == "가져오는 중 2/3 · 3층 평면도.dwg", "Korean progress text changed: " + Shown(korean));
        });

        test("English count readouts avoid wrong plurals such as 1 layers", () =>
        {
            InLanguage("en", () =>
            {
                Check(Loc.T("1개 레이어") == "Layers: 1" && Loc.T("레이어 1개") == "Layers: 1" && Loc.T("1개 레이어 · 15개 객체") == "Layers: 1 · Objects: 15" && Loc.T("1개 객체") == "Objects: 1",
                    "Layer panel counts: " + Loc.T("1개 레이어") + " | " + Loc.T("1개 레이어 · 15개 객체"));
                var window = new MainWindow(null) { headlessTesting = true };
                try
                {
                    window.AddTab(NewDocumentDialog.CreateDocument("수", "16", "16", 0), null);
                    Check(Loc.T(window.layerCountLabel.Text) == "Layers: 1", "Layer panel header: " + window.layerCountLabel.Text + " -> " + Loc.T(window.layerCountLabel.Text));
                }
                finally { window.StopRenderingForShutdown(); }
            });
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Morupixel.i18n.en.json")!;
            using var table = System.Text.Json.JsonDocument.Parse(stream);
            var counter = new Regex(@"\{\d+\}\s*(개|건|페이지)|(개|건) \{\d+\}");
            var plural = new Regex(@"\{\d+\}(?:\s+[A-Za-z/.\-]+){0,2}\s+[a-z]+s\b");
            // Fixed limits ("up to {0} layers") are never one.
            var limit = new Regex(@"(?i)up to \{|first \{|within \{|limit|more than|exceed|would make|all \{|other \{");
            var wrong = table.RootElement.GetProperty("strings").EnumerateObject()
                .Where(p => counter.IsMatch(p.Name) && plural.IsMatch(p.Value.GetString() ?? "") && !limit.IsMatch(p.Value.GetString() ?? ""))
                .Select(p => p.Value.GetString()).Take(5).ToArray();
            Check(wrong.Length == 0, "Count templates read wrong for one: " + string.Join(" | ", wrong));
        });

        test("command palette search results are translated like the full list", () =>
        {
            InLanguage("en", () =>
            {
                var window = new MainWindow(null) { headlessTesting = true };
                try
                {
                    window.AddTab(NewDocumentDialog.CreateDocument("명령", "16", "16", 0), null);
                    var commands = window.BuildCommandRegistry();
                    var korean = CommandPalette.Filter(commands, "브러시", []); var english = CommandPalette.Filter(commands, "brush", []);
                    Check(korean.Any(c => c.Id == "tool:Brush") && english.Any(c => c.Id == "tool:Brush"), "Brush tool was not found by Korean and English queries");
                    var mixed = korean.Concat(english).Where(c => Hangul(c.Title) || Hangul(c.Category)).Select(c => c.Title + " / " + c.Category).Take(5).ToArray();
                    Check(mixed.Length == 0, "Search results kept Korean text: " + string.Join(" | ", mixed));
                    Check(korean.Any(c => c.Id.StartsWith("panel:", StringComparison.Ordinal) && c.Title == "Open Brush panel" && c.Category == "Panels"),
                        "The brush panel command reads: " + string.Join(" | ", korean.Where(c => c.Id.StartsWith("panel:", StringComparison.Ordinal)).Select(c => c.Title)));
                    Check(CommandPalette.Filter(commands, "레이어 복제", [])[0].Id == "menu:레이어/레이어 복제", "A Korean menu title no longer matched in English");
                    var palette = new CommandPalette(null, commands, []); palette.SetQuery("브러시");
                    try
                    {
                        var content = (FrameworkElement)palette.Content; Loc.PrepareOffscreen(content);
                        var texts = palette.Results.Items.OfType<ListBoxItem>().SelectMany(item => Tree((DependencyObject)item.Content)).OfType<TextBlock>().Select(Shown).ToArray();
                        Check(texts.Length >= 4 && !texts.Any(Hangul), "Palette rows kept Korean text: " + string.Join(" | ", texts.Where(Hangul).Take(5)));
                    }
                    finally { palette.Close(); }
                }
                finally { window.StopRenderingForShutdown(); }
            });
        });

        test("parameter range hints are one translated sentence", () =>
        {
            var fields = new ParameterField[] { new("반경 px", 1, 30, 5, 5) };
            var dialog = new ParameterDialog(null, "흐림", fields);
            try { Check(Tree(dialog).OfType<TextBlock>().Any(t => t.Text == "범위 1 ~ 30"), "The Korean range hint changed"); }
            finally { dialog.Close(); }
            InLanguage("en", () =>
            {
                var english = new ParameterDialog(null, "흐림", fields);
                try
                {
                    Loc.PrepareOffscreen((FrameworkElement)english.Content);
                    var hints = Tree(english).OfType<TextBlock>().Where(t => t.Text.Contains("1 ~ 30")).ToArray();
                    Check(hints.Length == 1 && hints[0].Text == "Range 1 ~ 30" && !AutomationProperties.GetName(hints[0]).Contains('범'),
                        "English range hint: " + string.Join(" | ", hints.Select(h => h.Text + " / " + AutomationProperties.GetName(h))));
                }
                finally { english.Close(); }
            });
        });

        test("app-made layer names follow the display language and user names do not", () =>
        {
            Check(BrushTip.Square.Name == "정사각형" && BrushTip.Round.Name == "원형", "Built-in brush tip names changed");
            string shape = "", ellipse = "", material = "", custom = "";
            void Make()
            {
                var window = new MainWindow(null) { headlessTesting = true };
                try
                {
                    window.AddTab(NewDocumentDialog.CreateDocument("이름", "120", "80", 0), null);
                    window.AddShape(new Rect(4, 4, 30, 12), false); shape = window.doc.Layers[^1].Name;
                    window.AddShape(new Rect(40, 4, 20, 20), true); ellipse = window.doc.Layers[^1].Name;
                    window.selection = new Selection(new Rect(10, 30, 40, 30)); window.Refresh(false);
                    material = window.ApplySelectionMaterial(MaterialPresets.Create(MaterialKind.Wood), DrawingCleanup.MaterialName(MaterialKind.Wood))?.Name ?? "(none)";
                    // Swapping to the user's own image keeps its file name as it is.
                    custom = window.ApplySelectionMaterial(new MaterialAsset(Guid.NewGuid(), "오크 마루", Raster.Solid(8, 8, Colors.SaddleBrown), "오크 마루.png", false), "오크 마루")?.Name ?? "(none)";
                }
                finally { window.StopRenderingForShutdown(); }
            }
            Make();
            Check(shape == "사각형" && ellipse == "타원" && material == "재질 · 목재 마루" && custom == "재질 · 오크 마루", $"Korean names changed: {shape}, {ellipse}, {material}, {custom}");
            InLanguage("en", () =>
            {
                Check(Loc.T("사각형") == "Rectangle" && Loc.T(BrushTip.Square.Name) == "Square", "Rectangle and square are not told apart: " + Loc.T("사각형") + " / " + Loc.T(BrushTip.Square.Name));
                Make();
                Check(shape == "Rectangle" && ellipse == "Ellipse", $"Shape layers were named {shape} and {ellipse}");
                Check(material == "Material · Wood flooring" && custom == "Material · 오크 마루", $"Material layers were named {material} and {custom}");
            });
        });

        test("CAD cleanup names its layers in the display language and keeps each role with its count", () =>
        {
            var cad = new CadDocument();
            var wall = new ACadSharp.Tables.Layer("A-WALL"); var door = new ACadSharp.Tables.Layer("A-DOOR"); var hatches = new ACadSharp.Tables.Layer("A-HATCH");
            foreach (var layer in new[] { wall, door, hatches }) cad.Layers.Add(layer);
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 60, 0), EndPoint = new XYZ(200, 60, 0), Layer = door });
            var hatch = new Hatch { Pattern = new ACadSharp.Entities.HatchPattern("AR-CONC"), Layer = hatches };
            var boundary = new Hatch.BoundaryPath(); boundary.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 100, 0), new XYZ(180, 100, 0), new XYZ(180, 180, 0), new XYZ(20, 180, 0) }, true));
            hatch.Paths.Add(boundary); cad.Entities.Add(hatch);
            string file = Path.Combine(root, "3층 평면도.dxf"); DxfWriter.Write(file, cad);
            CompatibilityResult Read() => CompatibilityImport.ReadAsync(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", RetainVectors: false,
                CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: new CadCleanup())).GetAwaiter().GetResult();
            var korean = Read();
            Check(korean.Document.Layers.Any(l => l.Kind == LayerKind.Material && l.Name == "재질 · 콘크리트") && korean.Document.Layers.Any(l => l.Name == "도면 배경"),
                "Korean import names: " + string.Join(", ", korean.Document.Layers.Select(l => l.Name)));
            Check(korean.Warnings.Any(w => w.Contains("구조·벽\u00A01, 창호·문\u00A01")), "Cleanup notice: " + string.Join(" | ", korean.Warnings));
            Check(DrawingCleanup.RoleSummary([DrawingRole.Opening, DrawingRole.Structure, DrawingRole.Structure]) == "구조·벽\u00A02, 창호·문\u00A01", "Role summary format changed");

            var dialog = CompatibilityDialog.CleanupPreview(Path.Combine(root, "평면 예시.dxf"));
            // Word joiners keep each role and its count on one line; only ", " may wrap.
            static bool Joined(string item) => item.Length > 1 && Enumerable.Range(1, item.Length - 1).All(i => (item[i] == '\u2060') != (item[i - 1] == '\u2060'));
            try
            {
                var items = dialog.RoleSummary.Split(" · ", 2)[1].Split(", ");
                Check(dialog.RoleSummary.Replace("\u2060", "") == "레이어 역할 (5) · 구조·벽\u00A01, 창호·문\u00A01, 가구·집기\u00A01, 치수·문자\u00A01, 해치·마감\u00A01" && items.Length == 5 && items.All(Joined),
                    "Korean role header: " + dialog.RoleSummary);
            }
            finally { dialog.Close(); }
            InLanguage("en", () =>
            {
                var english = Read();
                Check(english.Document.Layers.Any(l => l.Kind == LayerKind.Material && l.Name == "Material · Concrete") && english.Document.Layers.Any(l => l.Name == "Drawing background"),
                    "English import names: " + string.Join(", ", english.Document.Layers.Select(l => l.Name)));
                var preview = CompatibilityDialog.CleanupPreview(Path.Combine(root, "평면 예시.dxf"));
                try
                {
                    string header = preview.RoleSummary; var roles = header.Split(" · ", 2)[1].Split(", ");
                    Check(!Hangul(header) && header.StartsWith("Layer roles (5) · ", StringComparison.Ordinal) && roles.Length == 5 && roles.All(Joined)
                        && roles.All(r => Regex.IsMatch(r.Replace("\u2060", ""), @"^[^\s·]+\u00A0\d+$")), "English role header: " + header);
                    string hatches = Loc.T(preview.HatchSummaryText.Text);
                    Check(hatches == "Hatches: 9 · Suggested: Concrete\u00A04 · Tile\u00A03 · Wood flooring\u00A02", "Hatch suggestion: " + hatches);
                }
                finally { preview.Close(); }
            });
        });

        test("import progress keeps the user's file name as it is", () =>
        {
            string path = Path.Combine(root, "2층 평면도.dwg");
            var korean = CompatibilityDialog.BatchPreview(path, quick: true);
            try { Check(Shown(korean.MessageText) == "가져오는 중 2/3 · 3층 평면도.dwg", "Korean progress: " + Shown(korean.MessageText)); }
            finally { korean.Close(); }
            InLanguage("en", () =>
            {
                var dialog = CompatibilityDialog.BatchPreview(path, quick: true);
                try
                {
                    Loc.PrepareOffscreen((FrameworkElement)dialog.Content);
                    Check(Shown(dialog.MessageText) == "Importing 2/3 · 3층 평면도.dwg", "English progress: " + Shown(dialog.MessageText));
                    dialog.UseMaterialImage(Path.Combine(root, "3층 마감 재질.png"));
                    Loc.PrepareOffscreen((FrameworkElement)dialog.Content);
                    Check(Shown(dialog.HatchSummaryText) == "Hatches (9) will be filled with the ‘3층 마감 재질.png’ image.", "Hatch image hint: " + Shown(dialog.HatchSummaryText));
                }
                finally { dialog.Close(); }
            });
        });
    }
}
