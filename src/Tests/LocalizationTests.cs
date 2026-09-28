using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Compositor.Windows;

public static class LocalizationTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        test("localization translates exact text, templates, fragments and multi-line notices", () =>
        {
            try
            {
                Loc.Use("en", new Dictionary<string, string>
                {
                    ["레이어 복제"] = "Duplicate layer", ["{0}개 객체"] = "{0} objects", ["배율은 숫자로 입력하세요."] = "Enter a number for the zoom.",
                    ["레이어 표시: "] = "Layer visibility: ", ["파일"] = "File"
                });
                Check(Loc.Active && Loc.T("레이어 복제") == "Duplicate layer" && Loc.T("  레이어 복제") == "  Duplicate layer", "Exact text was not translated");
                Check(Loc.T("12개 객체") == "12 objects", "A template with a number was not translated");
                Check(Loc.T("레이어 표시: 벽체") == "Layer visibility: 벽체", "A known fragment was not replaced");
                Check(Loc.T("레이어 복제\n배율은 숫자로 입력하세요.") == "Duplicate layer\nEnter a number for the zoom.", "Lines were not translated separately");
                Check(Loc.T("Morupixel") == "Morupixel" && Loc.T("모르는 문장") == "모르는 문장", "Untranslatable text changed");
                Check(Loc.T("파일 이름") == "파일 이름", "A short fragment replaced part of a word");
                var block = new TextBlock { Text = "레이어 복제" }; Loc.Apply(block);
                Check(block.Text == "Duplicate layer", "A TextBlock was not translated");
                var name = Loc.Keep(new TextBlock { Text = "레이어 복제" }); Loc.Apply(name);
                Check(name.Text == "레이어 복제", "User data marked Keep was translated");
                var runs = new TextBlock(); runs.Inlines.Add(new Run("레이어 복제")); runs.Inlines.Add(new Run(" · 3개 객체")); Loc.Apply(runs);
                Check(runs.Inlines.OfType<Run>().First().Text == "Duplicate layer", "Formatted runs were not translated individually");
            }
            finally { Loc.Use("ko", null); }
            Check(!Loc.Active && Loc.T("레이어 복제") == "레이어 복제", "Korean mode did not pass text through");
        });

        test("embedded English, Japanese and Chinese tables cover the UI and keep placeholders", () =>
        {
            foreach (var code in new[] { "en", "ja", "zh" })
            {
                using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Morupixel.i18n.{code}.json");
                Check(stream != null, $"The {code} table is not embedded");
                using var document = System.Text.Json.JsonDocument.Parse(stream!);
                var strings = document.RootElement.GetProperty("strings").EnumerateObject().ToArray();
                Check(strings.Length >= 1400, $"The {code} table has only {strings.Length} entries");
                var broken = strings.Where(p =>
                {
                    string value = p.Value.GetString() ?? "";
                    var keys = Regex.Matches(p.Name, @"\{\d+\}").Select(m => m.Value).Order();
                    var values = Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order();
                    return value.Length == 0 || !keys.SequenceEqual(values) || p.Name.Count(c => c == '\n') != value.Count(c => c == '\n');
                }).Select(p => p.Name).Take(5).ToArray();
                Check(broken.Length == 0, $"{code} entries lost placeholders or lines: " + string.Join(" | ", broken));
                Check(!strings.Any(p => Regex.IsMatch(p.Value.GetString() ?? "", "Photoshop|Adobe|Illustrator|AutoCAD|Camera Raw|Content-Aware", RegexOptions.IgnoreCase)), $"{code} translations name third-party products");
            }
            Check(Loc.Languages.Select(l => l.Code).SequenceEqual(["ko", "en", "ja", "zh"]), "Language list changed");
        });
    }
}
