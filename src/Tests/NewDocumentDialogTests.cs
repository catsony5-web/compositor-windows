using System.IO;
using System.Windows.Controls;

namespace Compositor.Windows;

public static class NewDocumentDialogTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        string Store() => Path.Combine(directory, "new-document-" + Guid.NewGuid().ToString("N") + ".json");

        test("new document presets are grouped by purpose and SNS uses ratios, not platform names", () =>
        {
            var presets = NewDocumentDialog.Presets(1920, 1080);
            foreach (var group in NewDocumentDialog.Groups.Where(g => g != NewDocumentDialog.RecentGroup))
                Check(presets.Count(p => p.Group == group) >= 3, "Too few presets in " + group);
            var sns = presets.Where(p => p.Group == "SNS").ToArray();
            foreach (var ratio in new[] { "1:1", "4:5", "9:16" }) Check(sns.Any(p => p.Name.Contains(ratio) && NewDocumentDialog.Ratio(p.Width, p.Height) == ratio), "SNS ratio missing: " + ratio);
            Check(NewDocumentDialog.Ratio(1920, 1080) == "16:9" && NewDocumentDialog.Ratio(1200, 628) == "1.91:1", "Ratio reduction wrong");
            foreach (var p in presets.Where(p => p.Paper)) _ = NewDocumentDialog.Dimensions(p.Width.ToString(), p.Height.ToString(), true, (p.Dpi ?? 150).ToString());
        });
        test("preset store keeps recent sizes newest first and custom presets by name", () =>
        {
            string store = Store();
            for (int i = 0; i < 8; i++) NewDocumentPresetStore.AddRecent(new("최근", 100 + i, 100, false, 96), store);
            NewDocumentPresetStore.AddRecent(new("최근", 103, 100, false, 96), store);
            var loaded = NewDocumentPresetStore.Load(store);
            Check(loaded.Recent.Count == NewDocumentPresetStore.RecentLimit && loaded.Recent[0].Width == 103 && loaded.Recent.Select(r => r.Width).Distinct().Count() == loaded.Recent.Count, "Recent order/dedupe/cap wrong");
            NewDocumentPresetStore.AddCustom(new("포스터", 420, 594, true, 300, 1), store);
            NewDocumentPresetStore.AddCustom(new("포스터", 400, 500, true, 300, 1), store);
            loaded = NewDocumentPresetStore.Load(store);
            Check(loaded.Custom.Count == 1 && loaded.Custom[0].Width == 400, "Custom preset with same name must replace");
            NewDocumentPresetStore.RemoveCustom("포스터", store);
            Check(NewDocumentPresetStore.Load(store).Custom.Count == 0, "Custom preset not removed");
            File.WriteAllText(store, "{not json"); Check(NewDocumentPresetStore.Load(store).Recent.Count == 0, "Corrupt store must load empty");
        });
        test("new document dialog switches purpose tabs and saves a custom preset to the recent tab", () =>
        {
            string store = Store();
            var dialog = new NewDocumentDialog(null, store);
            Check(dialog.Group == "화면" && dialog.Cards.Count >= 3, "Screen tab must be the default");
            dialog.ShowGroupForTest("SNS");
            Check(dialog.Cards.All(c => c.Tag is SavedDocumentSize { Millimeters: false }), "SNS cards must be pixel sizes");
            dialog.Cards[1].RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            dialog.ShowGroupForTest(NewDocumentDialog.RecentGroup);
            Check(dialog.Cards.Count == 0, "Recent tab must start empty with a fresh store");
            dialog.SaveCustomPreset();
            Check(dialog.Group == NewDocumentDialog.RecentGroup && dialog.Cards.Count == 1 && dialog.Cards[0].Tag is SavedDocumentSize { Width: 1080, Height: 1350 }, "Saved preset must appear in the recent tab");
            dialog.Close();
        });
    }
}
