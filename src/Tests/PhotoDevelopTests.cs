using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class PhotoDevelopTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static AdjustmentSpec Adjustment(PhotoDevelopSpec photo) => new() { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = photo };
        static Raster Gray(params byte[] values)
        {
            var raster = new Raster(values.Length, 1);
            for (int x = 0; x < values.Length; x++) { int i = x * 4; raster.Data[i] = raster.Data[i + 1] = raster.Data[i + 2] = values[x]; raster.Data[i + 3] = 255; }
            return raster;
        }
        static PhotoDevelopSpec All() => new() { Temperature = 20, Tint = -10, Exposure = .3, Contrast = 18, Highlights = -22, Shadows = 31, Whites = 8, Blacks = -7, Texture = 24, Clarity = 16, Dehaze = 9, Vibrance = 26, Saturation = -4 };

        test("photo develop neutral is byte-exact and returns independent pixels", () =>
        {
            var source = new Raster(256, 1);
            for (int x = 0; x < 256; x++) { source.Data[x * 4] = (byte)x; source.Data[x * 4 + 1] = (byte)(255 - x); source.Data[x * 4 + 2] = (byte)(x / 2); source.Data[x * 4 + 3] = (byte)x; }
            var output = DocumentFeatures.ApplyAdjustment(source, Adjustment(new()));
            Check(output.Data.SequenceEqual(source.Data) && !ReferenceEquals(output.Data, source.Data), "Neutral settings changed pixels or shared a writable buffer");
        });
        test("photo develop relative white balance has warm cool and magenta green directions", () =>
        {
            var source = Gray(120);
            var warm = PhotoDevelop.Apply(source, new() { Temperature = 70 }); var cool = PhotoDevelop.Apply(source, new() { Temperature = -70 });
            Check(warm.Data[2] > 120 && warm.Data[0] < 120 && cool.Data[2] < 120 && cool.Data[0] > 120, "Warm/cool control has wrong channel directions");
            var magenta = PhotoDevelop.Apply(source, new() { Tint = 70 }); var green = PhotoDevelop.Apply(source, new() { Tint = -70 });
            Check(magenta.Data[2] > magenta.Data[1] && magenta.Data[0] > magenta.Data[1] && green.Data[1] > green.Data[2], "Tint does not shift green/magenta");
        });
        test("photo develop exposure uses linear light EV", () =>
        {
            var bright = PhotoDevelop.Apply(Gray(128), new() { Exposure = 1 }); var dark = PhotoDevelop.Apply(Gray(128), new() { Exposure = -1 });
            Check(Math.Abs(bright.Data[0] - 176) <= 1 && Math.Abs(dark.Data[0] - 92) <= 1, "One EV was treated as encoded RGB addition");
        });
        test("photo develop broad tonal controls and endpoints target distinct regions", () =>
        {
            var source = Gray(32, 100, 180, 230);
            var highlights = PhotoDevelop.Apply(source, new() { Highlights = -80 }); var shadows = PhotoDevelop.Apply(source, new() { Shadows = 80 });
            Check(highlights.Data[0] == 32 && highlights.Data[12] < 230 && shadows.Data[0] > 32 && shadows.Data[12] == 230, "Broad tonal controls affected the wrong range");
            var whites = PhotoDevelop.Apply(source, new() { Whites = -80 }); var blacks = PhotoDevelop.Apply(source, new() { Blacks = 80 });
            Check(whites.Data[4] == 100 && whites.Data[12] < 230 && blacks.Data[0] > 32 && blacks.Data[8] == 180, "Endpoint controls affected midtones");
            var contrast = PhotoDevelop.Apply(Gray(64, 192), new() { Contrast = 80 });
            Check(contrast.Data[0] < 64 && contrast.Data[4] > 192, "Contrast did not expand dark/light separation");
        });
        test("photo develop saturation and vibrance have separate useful effects", () =>
        {
            var source = Raster.Solid(1, 1, Color.FromRgb(160, 120, 100));
            var gray = PhotoDevelop.Apply(source, new() { Saturation = -100 });
            Check(gray.Data[0] == gray.Data[1] && gray.Data[1] == gray.Data[2], "Desaturation did not produce neutral RGB");
            var vibrant = PhotoDevelop.Apply(source, new() { Vibrance = 80 });
            Check(vibrant.Data[2] - vibrant.Data[0] > source.Data[2] - source.Data[0], "Vibrance did not strengthen muted color");
            var red = Raster.Solid(1, 1, Colors.Red);
            Check(PhotoDevelop.Apply(red, new() { Vibrance = 100 }).Data.SequenceEqual(red.Data), "Vibrance over-amplified already saturated color");
        });
        test("photo develop dehaze removes and adds a neutral veil", () =>
        {
            var source = Gray(100, 160, 210);
            var clear = PhotoDevelop.Apply(source, new() { Dehaze = 70 }); var hazy = PhotoDevelop.Apply(source, new() { Dehaze = -70 });
            Check(clear.Data[4] < 160 && hazy.Data[4] > 160 && clear.Data[8] - clear.Data[0] > 110, "Dehaze did not change the veil and tonal separation");
        });
        test("photo develop texture sharpens or smooths fine detail", () =>
        {
            var source = Gray(Enumerable.Range(0, 65).Select(x => (byte)(x % 2 == 0 ? 110 : 146)).ToArray());
            var positive = PhotoDevelop.Apply(source, new() { Texture = 80 }); var negative = PhotoDevelop.Apply(source, new() { Texture = -80 });
            int originalDifference = source.Data[31 * 4] - source.Data[32 * 4];
            Check(positive.Data[31 * 4] - positive.Data[32 * 4] > originalDifference && negative.Data[31 * 4] - negative.Data[32 * 4] < originalDifference, "Texture does not control fine local contrast");
        });
        test("photo develop clarity has a broader spatial scale than texture", () =>
        {
            var source = Gray(Enumerable.Range(0, 65).Select(x => (byte)(x < 32 ? 100 : 155)).ToArray());
            var texture = PhotoDevelop.Apply(source, new() { Texture = 80 }); var clarity = PhotoDevelop.Apply(source, new() { Clarity = 80 });
            Check(texture.Data[26 * 4] == 100 && clarity.Data[26 * 4] < 100 && clarity.Data[38 * 4] > 155, "Clarity did not use a wider local contrast region");
            var softened = PhotoDevelop.Apply(source, new() { Clarity = -80 });
            Check(softened.Data[26 * 4] > 100 && softened.Data[38 * 4] < 155, "Negative clarity did not soften broad edges");
        });
        test("photo develop detail excludes hidden RGB and retains constant translucent edges", () =>
        {
            var source = Raster.Solid(11, 7, Color.FromArgb(128, 90, 130, 170));
            for (int y = 0; y < 7; y++) { int i = y * 11 * 4; source.Data[i] = 255; source.Data[i + 1] = 0; source.Data[i + 2] = 255; source.Data[i + 3] = 0; }
            var output = PhotoDevelop.Apply(source, new() { Texture = 90, Clarity = 90 });
            Check(output.Data.SequenceEqual(source.Data), "Detail created fringes from transparent RGB or altered uniform color");
        });
        test("photo develop combined settings preserve source alpha and hidden RGB", () =>
        {
            var source = new Raster(5, 1, [30, 40, 50, 0, 40, 80, 120, 1, 70, 90, 130, 128, 40, 70, 100, 220, 110, 150, 190, 255]);
            var original = (byte[])source.Data.Clone(); var result = DocumentFeatures.ApplyAdjustment(source, Adjustment(All()));
            Check(source.Data.SequenceEqual(original) && result.Data.Take(4).SequenceEqual(original.Take(4)), "Source or fully transparent pixel was modified");
            Check(Enumerable.Range(0, 5).All(x => result.Data[x * 4 + 3] == original[x * 4 + 3]) && !result.Data.SequenceEqual(original), "Alpha changed or all controls were ignored");
        });
        test("photo develop validates all finite bounds and cancellation including neutral", () =>
        {
            foreach (var property in typeof(PhotoDevelopSpec).GetProperties().Where(p => p.PropertyType == typeof(double)))
            {
                foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, property.Name == "Exposure" ? 5.01 : 100.01, property.Name == "Exposure" ? -5.01 : -100.01 })
                {
                    var spec = new PhotoDevelopSpec(); property.SetValue(spec, value);
                    bool rejected = false; try { Adjustment(spec).Validate(); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "Accepted invalid " + property.Name);
                }
            }
            bool nullRejected = false; try { (new AdjustmentSpec { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = null! }).Validate(); } catch (InvalidDataException) { nullRejected = true; }
            Check(nullRejected, "Missing photo settings were accepted");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            foreach (var spec in new[] { new PhotoDevelopSpec(), All() })
            {
                bool canceled = false; try { PhotoDevelop.Apply(Gray(120), spec, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Canceled photo adjustment completed");
            }
        });
        test("photo develop extreme valid settings are deterministic and preserve alpha", () =>
        {
            var source = new Raster(29, 17); var random = new Random(317);
            random.NextBytes(source.Data); var original = (byte[])source.Data.Clone();
            foreach (double edge in new[] { -100d, 100d })
            {
                var spec = new PhotoDevelopSpec();
                foreach (var property in typeof(PhotoDevelopSpec).GetProperties().Where(p => p.PropertyType == typeof(double)))
                    property.SetValue(spec, property.Name == "Exposure" ? edge / 20 : edge);
                var first = PhotoDevelop.Apply(source, spec); var second = PhotoDevelop.Apply(source, spec);
                Check(first.Data.SequenceEqual(second.Data) && source.Data.SequenceEqual(original), "Extreme values are nondeterministic or mutate source");
                Check(Enumerable.Range(0, source.Width * source.Height).All(x => first.Data[x * 4 + 3] == original[x * 4 + 3]), "Extreme values changed coverage");
            }
        });
        test("photo develop masked layer roundtrip and parameter undo remain nondestructive", () =>
        {
            var document = new Document { Width = 4, Height = 1 }; document.Add(new Layer { Pixels = Gray(50, 100, 150, 200) });
            var pixels = document.Active!.Pixels; var layer = DocumentFeatures.CreateAdjustment(document, Adjustment(All())); layer.Mask = [255, 128, 0, 255]; document.Add(layer);
            var history = new History(); history.Reset(document); var before = document.Snapshot();
            layer.Adjustment = Adjustment(All() with { Exposure = 1.2 }); history.Commit("사진 현상 설정", before, document);
            var undone = history.Undo(document); Check(undone.Active!.Adjustment!.PhotoDevelop == All(), "Photo settings did not undo atomically");
            var redone = history.Redo(undone); Check(redone.Active!.Adjustment!.PhotoDevelop.Exposure == 1.2, "Photo settings did not redo");
            string path = Path.Combine(directory, "photo-develop.moruproj"); ProjectStore.Save(redone, path); var loaded = ProjectStore.Load(path);
            Check(DocumentFeatures.SameAdjustment(loaded.Active!.Adjustment, redone.Active.Adjustment) && loaded.Active.Mask!.SequenceEqual(layer.Mask), "Saved editable settings or mask were lost");
            Check(Imaging.Render(loaded).Data.SequenceEqual(Imaging.Render(redone).Data), "Roundtrip changed rendered pixels");
            Check(Imaging.Render(loaded).Data[8] == 150 && pixels.Data.SequenceEqual(Gray(50, 100, 150, 200).Data), "Mask or nondestructive source was lost");
        });
        test("photo develop before after preview respects selection and re-edit", () =>
        {
            var document = new Document { Width = 2, Height = 1 }; document.Add(new Layer { Pixels = Gray(100, 100) });
            var spec = Adjustment(new() { Exposure = 1 });
            var preview = AdjustmentDialog.PreviewDocument(document, null, spec, true, [255, 0]);
            var after = Imaging.Render(preview); Check(after.Data[0] > 100 && after.Data[4] == 100 && document.Layers.Count == 1, "Selection preview differs from masked adjustment");
            var baseline = AdjustmentDialog.PreviewDocument(preview, preview.ActiveId, spec, false, null);
            Check(Imaging.Render(baseline).Data.SequenceEqual(Imaging.Render(document).Data), "Before comparison still contains current correction");
            var edited = AdjustmentDialog.PreviewDocument(preview, preview.ActiveId, Adjustment(new() { Exposure = -1 }), true, null);
            Check(Imaging.Render(edited).Data[0] < 100 && preview.Active!.Adjustment!.PhotoDevelop.Exposure == 1, "Re-edit modified source settings");
        });
        test("photo develop real dialog sliders initialize edit reset and reopen full settings", () =>
        {
            var document = new Document { Width = 3, Height = 1 }; document.Add(new Layer { Pixels = Gray(70, 120, 180) });
            var dialog = new AdjustmentDialog(null, document, Adjustment(All()));
            try
            {
                var sliders = Descendants<Slider>(dialog).ToDictionary(AutomationProperties.GetName);
                Check(sliders.Count == 13 && sliders["노출 EV"].Value == .3 && sliders["색온도 · 상대값"].Value == 20, "Dialog did not restore all 13 parameters");
                sliders["노출 EV"].Value = 1.25; sliders["안개 제거"].Value = 43;
                Check(dialog.Spec.PhotoDevelop.Exposure == 1.25 && dialog.Spec.PhotoDevelop.Dehaze == 43 && dialog.Spec.PhotoDevelop.Texture == 24, "UI edits lost other settings");
                var reopened = new AdjustmentDialog(null, document, dialog.Spec);
                try { Check(Descendants<Slider>(reopened).Single(s => AutomationProperties.GetName(s) == "노출 EV").Value == 1.25, "Reopening lost exposure"); } finally { reopened.Close(); }
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "현상 설정 모두 초기화")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.Spec.PhotoDevelop.IsNeutral && sliders.Values.All(slider => slider.Value == 0), "Reset left stale settings or controls");
                var previewToggle = Descendants<CheckBox>(dialog).Single();
                Check(Equals(previewToggle.Content, "보정 결과 보기") && previewToggle.ToolTip.ToString()!.Contains("보정 전") && !Descendants<ScrollViewer>(dialog).Any(scroll => Descendants<CheckBox>(scroll).Contains(previewToggle)), "Before/after toggle is hidden in the long scroll area");
            }
            finally { dialog.Close(); }
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
