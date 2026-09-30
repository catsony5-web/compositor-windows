using System.Windows.Controls;

namespace Compositor.Windows;

// The CAD import hatch summary: SOLID hatches counted apart from line patterns, names kept whole when wrapped.
internal sealed partial class CompatibilityDialog
{
    internal static void RunHatchSummaryTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        test("CAD import pattern summary counts SOLID hatches apart and keeps pattern names whole", () =>
        {
            var dialog = new CompatibilityDialog(null, "평면.dxf", false, new ImportSettings());
            try
            {
                Check(dialog.hatchSummary is KeepWordsTextBlock, "The hatch summary can break inside a pattern name");
                dialog.ShowDrawingInfo(new CadDrawingInfo([new("A-HATCH", DrawingRole.Hatch, 15, 15)],
                    new Dictionary<MaterialKind, int> { [MaterialKind.Solid] = 10, [MaterialKind.Diagonal] = 5 },
                    new Dictionary<HatchPattern, int> { [HatchPattern.Diagonal] = 5 }));
                dialog.hatchMode.SelectedItem = ((HatchChoice[])dialog.hatchMode.ItemsSource).Single(c => c.Value == HatchTreatment.Pattern);
                Check(dialog.hatchSummary.Text == "해치 5개를 선 패턴으로: 사선 5 · 단색 채움 10개", "The summary is " + dialog.hatchSummary.Text);
                Check(PatternSummary(3, new Dictionary<HatchPattern, int> { [HatchPattern.Lines] = 3 }) == "해치 3개를 선 패턴으로: 가로줄 3"
                    && PatternSummary(4, new Dictionary<HatchPattern, int>()) == "해치 4개는 모두 단색 채움으로 가져와요.", "Summaries without SOLID or with only SOLID changed");
            }
            finally { dialog.Close(); }
        });
    }
}
