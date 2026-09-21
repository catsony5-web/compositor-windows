using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Compositor.Windows;

public static class ParameterDialogTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static T Named<T>(DependencyObject dialog, string name) where T : DependencyObject => Descendants<T>(dialog).Single(item => AutomationProperties.GetName(item) == name);
        static void Case(string name, ParameterField[] fields, Action<ParameterDialog> body, Func<IReadOnlyList<double>, string?>? validate = null)
        {
            var dialog = new ParameterDialog(null, name, fields, validate);
            try { body(dialog); } finally { dialog.Close(); }
        }

        test("parameter dialog links actual sliders and pending numeric input to accepted values", () =>
        {
            Case("노출", [new("노출 EV", -5, 5, 0), new("오프셋", -.5, .5, 0)], dialog =>
            {
                Named<Slider>(dialog, "노출 EV").Value = 1.25;
                Check(Named<TextBox>(dialog, "노출 EV").Text == "1.25", "Slider movement did not update its number box");
                Named<TextBox>(dialog, "오프셋").Text = "-0.125";
                Check(dialog.Values.SequenceEqual(new double[] { 0, 0 }), "Uncommitted controls leaked into accepted values");
                Check(dialog.TryCommitFields() && dialog.Values.SequenceEqual(new[] { 1.25, -.125 }), "Valid typed value was replaced by the old slider value");
                Check(Math.Abs(Named<Slider>(dialog, "오프셋").Value + .125) < 1e-9, "Typed value did not synchronize the slider");
                Check(Descendants<ComboBox>(dialog).Count() == 2, "Each parameter needs its own step selector");
            });
        });
        test("parameter dialog rejects malformed nonfinite and out of range numbers without closing", () =>
        {
            Case("채도", [new("채도", -100, 100, 0)], dialog =>
            {
                var box = Named<TextBox>(dialog, "채도");
                foreach (string invalid in new[] { "", "색상", "NaN", "Infinity", "-Infinity", "101", "-100.01" })
                {
                    box.Text = invalid;
                    Check(!dialog.TryCommitFields() && dialog.Values[0] == 0 && box.Text == invalid, "Invalid pending value was silently discarded: " + invalid);
                    Check(dialog.ValidationMessage.Contains("채도"), "Invalid field did not report inline feedback");
                }
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "적용")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.DialogResult != true && box.Text == "-100.01", "Apply accepted an invalid number");
                box.Text = "-23.5";
                Check(dialog.TryCommitFields() && dialog.Values[0] == -23.5 && dialog.ValidationMessage.Length == 0, "Corrected input could not be committed");
            });
        });
        test("parameter dialog validates levels relationships after all numeric commits", () =>
        {
            Case("레벨", [new("입력 검정", 0, 254, 0), new("입력 흰색", 1, 255, 255, 255), new("감마", .1, 9.99, 1, 1)], dialog =>
            {
                Named<TextBox>(dialog, "입력 검정").Text = "200";
                Named<TextBox>(dialog, "입력 흰색").Text = "100";
                Named<TextBox>(dialog, "감마").Text = "1.7";
                Check(!dialog.TryCommitFields() && dialog.ValidationMessage.Contains("흰색"), "Levels white/black relationship was not checked");
                Check(dialog.Values.SequenceEqual(new double[] { 0, 255, 1 }), "A failed relationship validation partially accepted settings");
                Named<TextBox>(dialog, "입력 흰색").Text = "240";
                Check(dialog.TryCommitFields() && dialog.Values.SequenceEqual(new[] { 200d, 240, 1.7 }), "Corrected levels were not accepted together");
            }, values => values[1] >= values[0] + 1 ? null : "입력 흰색은 입력 검정보다 1 이상 커야 합니다.");
        });
        test("parameter dialog checks every invalid field and does not run relationship validation early", () =>
        {
            int validations = 0;
            Case("레벨", [new("입력 검정", 0, 254, 0), new("감마", .1, 9.99, 1, 1)], dialog =>
            {
                Named<TextBox>(dialog, "입력 검정").Text = "-1"; Named<TextBox>(dialog, "감마").Text = "NaN";
                Check(!dialog.TryCommitFields() && validations == 0, "Cross-field validation ran with invalid individual values");
                Check(dialog.ValidationMessage.Contains("입력 검정") && dialog.ValidationMessage.Contains("감마"), "Only the first invalid field was reported");
                Named<TextBox>(dialog, "입력 검정").Text = "12"; Named<TextBox>(dialog, "감마").Text = "1.2";
                Check(dialog.TryCommitFields() && validations == 1, "Valid fields were not passed to the validator once");
            }, _ => { validations++; return null; });
        });
        test("parameter dialog reset updates both controls before applying", () =>
        {
            Case("감마", [new("감마", .1, 9.99, 2.4, 1)], dialog =>
            {
                Descendants<Button>(dialog).Single(button => Equals(button.ToolTip, "감마 초기화")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Named<TextBox>(dialog, "감마").Text == "1" && Named<Slider>(dialog, "감마").Value == 1, "Reset did not update both input types");
                Check(dialog.TryCommitFields() && dialog.Values[0] == 1, "Reset value was not committed");
            });
        });
        test("parameter dialog cancel does not validate or modify caller owned state", () =>
        {
            var fields = new[] { new ParameterField("노출 EV", -5, 5, .5) }; int validations = 0;
            var dialog = new ParameterDialog(null, "노출", fields, _ => { validations++; return null; });
            try
            {
                Named<TextBox>(dialog, "노출 EV").Text = "2.3";
                var exposed = dialog.Values; exposed[0] = 4;
                Check(dialog.Values[0] == .5 && fields[0].Value == .5, "Accepted values share caller-visible mutable storage");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "취소")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(validations == 0 && dialog.Values[0] == .5 && fields[0].Value == .5, "Cancellation accepted or published pending input");
            }
            finally { dialog.Close(); }
        });
        test("parameter dialog scrolls fields but keeps apply cancel and inline errors outside scrolling", () =>
        {
            Case("레벨", [new("입력 검정", 0, 254, 0), new("입력 흰색", 1, 255, 255, 255), new("감마", .1, 9.99, 1, 1), new("출력 검정", 0, 255, 0), new("출력 흰색", 0, 255, 255, 255)], dialog =>
            {
                var scroll = Descendants<ScrollViewer>(dialog).Single();
                Check(Descendants<ParameterSlider>(scroll).Count() == 5, "Long parameter dialog does not scroll its fields");
                Check(!Descendants<Button>(scroll).Any(button => Equals(button.Content, "적용") || Equals(button.Content, "취소")), "Footer actions scroll out of reach");
                Check(!Descendants<TextBlock>(scroll).Any(text => AutomationProperties.GetName(text) == "입력 확인"), "Validation feedback scrolls out of reach");
                Check(dialog.MinWidth >= 400 && dialog.Width >= dialog.MinWidth, "Dialog lacks usable resizing bounds");
            });
        });
        test("parameter dialog rejects invalid field definitions before constructing controls", () =>
        {
            foreach (var fields in new[]
            {
                Array.Empty<ParameterField>(), new[] { new ParameterField("범위", 1, 1, 1) }, new[] { new ParameterField("범위", 0, 1, 2) },
                new[] { new ParameterField("범위", 0, 1, 0, double.NaN) }, new[] { new ParameterField("범위", 0, 1, 0, 0, -1) }
            })
            {
                bool rejected = false;
                try { var invalid = new ParameterDialog(null, "잘못된 정의", fields); invalid.Close(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid field definition was accepted");
            }
        });
        test("quick adjustment menu dialogs expose working ranges defaults integer blur and level constraints", () =>
        {
            foreach (string kind in new[] { "exposure", "levels", "saturation", "blur" })
            {
                var dialog = MainWindow.CreateQuickAdjustmentDialog(null, kind);
                try
                {
                    (string Name, double Min, double Max, double Initial)[] expected = kind switch
                    {
                        "exposure" => new[] { (Name: "노출 EV", Min: -5d, Max: 5d, Initial: .5) },
                        "levels" => new[] { ("입력 검정", 0d, 254d, 0d), ("입력 흰색", 1d, 255d, 255d), ("감마", .1, 9.99, 1d) },
                        "saturation" => new[] { ("채도", -100d, 100d, 20d) },
                        _ => new[] { ("반경 px", 1d, 30d, 5d) }
                    };
                    Check(Descendants<Slider>(dialog).Count() == expected.Length && dialog.TryCommitFields(), kind + " did not build usable menu controls");
                    for (int index = 0; index < expected.Length; index++)
                    {
                        var field = expected[index]; var slider = Named<Slider>(dialog, field.Name);
                        Check(slider.Minimum == field.Min && slider.Maximum == field.Max && dialog.Values[index] == field.Initial, kind + " changed the supported range or initial operation value");
                    }
                    if (kind == "levels")
                    {
                        Named<TextBox>(dialog, "입력 검정").Text = "200"; Named<TextBox>(dialog, "입력 흰색").Text = "100";
                        Check(!dialog.TryCommitFields() && dialog.ValidationMessage.Contains("흰색"), "Actual menu levels factory omitted its cross-field validator");
                        Named<TextBox>(dialog, "입력 흰색").Text = "240";
                        Check(dialog.TryCommitFields() && dialog.Values[0] == 200 && dialog.Values[1] == 240, "Corrected menu levels did not commit");
                    }
                    else
                    {
                        var slider = Named<Slider>(dialog, expected[0].Name);
                        slider.Value = kind == "blur" ? 8.6 : 2.3;
                        Check(dialog.TryCommitFields(), kind + " slider value was rejected");
                        Check(kind == "blur" ? dialog.Values[0] == 9 : Math.Abs(dialog.Values[0] - 2.3) < 1e-9, kind + " slider did not reach its required numeric precision");
                        if (kind == "blur")
                        {
                            Named<TextBox>(dialog, "반경 px").Text = "6.7";
                            Check(dialog.TryCommitFields() && dialog.Values[0] == 7, "Actual blur menu can emit a fractional radius");
                        }
                    }
                }
                finally { dialog.Close(); }
            }
        });
        test("adjustment layer dialogs route snap movement into specs without losing pending numeric edits", () =>
        {
            var document = new Document { Width = 2, Height = 1 };
            document.Add(new Layer { Pixels = Raster.Solid(2, 1, System.Windows.Media.Colors.Gray) });
            foreach (var kind in new[] { AdjustmentKind.PhotoDevelop, AdjustmentKind.Levels })
            {
                var dialog = new AdjustmentDialog(null, document, new AdjustmentSpec { Kind = kind });
                try
                {
                    bool photo = kind == AdjustmentKind.PhotoDevelop;
                    string primaryName = photo ? "대비" : "입력 검정", secondName = photo ? "색온도 · 상대값" : "감마";
                    double Primary() => photo ? dialog.Spec.PhotoDevelop.Contrast : dialog.Spec.Black;
                    double Second() => photo ? dialog.Spec.PhotoDevelop.Temperature : dialog.Spec.Gamma;
                    var chooser = Named<ComboBox>(dialog, primaryName + " 이동 간격");
                    var stepFive = chooser.Items.Cast<ComboBoxItem>().Single(item => item.Tag is double step && step == 5);
                    chooser.SelectedItem = stepFive;
                    var slider = Named<Slider>(dialog, primaryName); slider.Value = 12.4;
                    Check(Primary() == 10 && slider.Value == 10, kind + " snapped value did not reach its adjustment specification");
                    var beforeModeChange = dialog.Spec.Snapshot(); chooser.SelectedIndex = 0;
                    Check(DocumentFeatures.SameAdjustment(beforeModeChange, dialog.Spec), kind + " mode selection changed the adjustment value");
                    chooser.SelectedItem = stepFive;
                    var primaryNumber = Named<TextBox>(dialog, primaryName); var secondNumber = Named<TextBox>(dialog, secondName);
                    secondNumber.Text = "NaN"; slider.Value = 17;
                    Check(secondNumber.Text == "NaN" && !dialog.TryCommitParameters(), kind + " model synchronization discarded an invalid pending field");
                    primaryNumber.Text = "12.34"; secondNumber.Text = photo ? "7.25" : "1.25";
                    Check(dialog.TryCommitParameters(), kind + " corrected numeric settings could not apply");
                    Check(Math.Abs(Primary() - 12.34) < 1e-9 && Math.Abs(Second() - (photo ? 7.25 : 1.25)) < 1e-9,
                        kind + " pending values were snapped or reset by another parameter's commit");
                }
                finally { dialog.Close(); }
            }
        });
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject dependency) continue;
            if (dependency is T match) yield return match;
            foreach (T nested in Descendants<T>(dependency)) yield return nested;
        }
    }
}
