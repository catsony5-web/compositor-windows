using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// 내 프리셋 in the brush panel: saving the current brush through the dialog, applying a preset in
// one click without history, the active mark, rename/overwrite/move/delete, persistence, limits,
// headless isolation and the command palette entry.
public sealed partial class MainWindow
{
    internal static void RunBrushPresetTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        string root = Path.Combine(directory, "brush-preset-ui"); Directory.CreateDirectory(root);
        string NewStore() => Path.Combine(root, Guid.NewGuid().ToString("N")[..8], "brush-presets.json");
        MainWindow Open(string? store)
        {
            var w = new MainWindow(null) { headlessTesting = true, brushPresetStore = store };
            var document = new Document { Width = 40, Height = 40, Name = "브러시 프리셋 검증" };
            document.Add(new Layer { Name = "원본", Pixels = Raster.Solid(40, 40, Colors.Transparent) });
            w.AddTab(document, null);
            if (store != null) w.LoadBrushPresets();
            return w;
        }
        ParameterSlider Parameter(MainWindow w, string name) => Descendants(w.studioContents[3]).OfType<ParameterSlider>()
            .Single(control => Descendants(control).OfType<Slider>().Any(s => AutomationProperties.GetName(s) == name));
        bool Marked(Button button) => ReferenceEquals(button.Background, Theme.Selected) && ReferenceEquals(button.BorderBrush, Theme.Accent);
        // Sets every brush setting through the panel's own controls.
        void SetBrush(MainWindow w, BrushTip tip, double size, double hardnessPercent, double opacity, double spacingPercent, double angle)
        {
            Click(w.brushTipButtons.TryGetValue(tip, out var tile) ? tile : throw new InvalidOperationException("No tile for " + tip.Name));
            Parameter(w, "크기 px").SetValue(size, true); Parameter(w, "경도 %").SetValue(hardnessPercent, true);
            Parameter(w, "모양 회전 °").SetValue(angle, true); Parameter(w, "찍는 간격 %").SetValue(spacingPercent, true);
            w.opacitySlider!.Value = opacity;
        }

        test("brush presets: headless windows never use the real store and never show the save dialog", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                Check(w.BrushPresetPath == null && w.brushPresets.Count == 0 && !w.IsLoaded, "A headless window would read or write the user's brush presets");
                Check(w.SaveBrushPresetWithDialog() == null && w.brushPresets.Count == 0, "A headless save opened a dialog or saved without a name");
                var kept = w.AddBrushPreset("메모리 전용");
                Check(w.brushPresets.Single() == kept && w.BrushPresetPath == null, "A preset without a store was not kept in memory only");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: saving captures every brush setting through the dialog and stores it", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                SetBrush(w, BrushTip.Star, 77, 35, .6, 50, 30);
                string? defaultName = null;
                w.brushPresetDialogRunner = dialog => { dialog.Accept(); defaultName = dialog.PresetName; dialog.SetName("  굵은 별  "); dialog.Accept(); dialog.Close(); return true; };
                var saved = w.SaveBrushPresetWithDialog()!;
                Check(defaultName == Loc.Format("내 브러시 {0}", 1), "The dialog did not suggest the first free default name: " + defaultName);
                Check(saved.Name == "굵은 별" && saved.TipId == "star" && saved.Size == 77 && Math.Abs(saved.Hardness - .35) < 1e-9 && Math.Abs(saved.Opacity - .6) < 1e-9
                    && Math.Abs(saved.Spacing - .5) < 1e-9 && saved.Angle == 30, "The preset did not capture the panel's settings: " + saved);
                Check(BrushPresetStore.Load(store).SequenceEqual(w.brushPresets) && w.brushPresets.Single() == saved, "The preset was not written to the store");
                // Unnamed saves count up: 내 브러시 1, then 2.
                w.brushPresetDialogRunner = dialog => { dialog.SetName(""); dialog.Accept(); dialog.Close(); return true; };
                var first = w.SaveBrushPresetWithDialog()!; var second = w.SaveBrushPresetWithDialog()!;
                Check(first.Name == Loc.Format("내 브러시 {0}", 1) && second.Name == Loc.Format("내 브러시 {0}", 2), "Empty names did not fall back to numbered defaults");
                // Cancel adds nothing.
                w.brushPresetDialogRunner = dialog => { dialog.Close(); return false; };
                Check(w.SaveBrushPresetWithDialog() == null && w.brushPresets.Count == 3, "A cancelled dialog saved a preset");
                var title = Descendants(w.brushPresetRows[0].Button).OfType<TextBlock>().First();
                Check(w.brushPresetRows.Count == 3 && title.Text == "굵은 별" && (bool)title.GetValue(Loc.KeepProperty)
                    && w.brushPresetRows.All(r => Descendants(r.Button).OfType<Image>().Single().Source != null), "Rows lack the kept name or the stroke sample");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: one click restores every setting and control without document history", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                SetBrush(w, BrushTip.Diamond, 140, 20, .45, 80, -60);
                var preset = w.AddBrushPreset("마름모");
                SetBrush(w, BrushTip.Round, 9, 100, 1, 10, 0);
                w.NudgeSelected(new Vector(1, 0)); w.Undo();
                var revision = w.doc.Revision; var pixels = w.doc.Active!.Pixels;
                Check(w.history.CanRedo && !w.history.CanUndo, "Unable to prepare redo history");
                w.SetTool(Tool.Move);
                Check(!w.IsBrushPresetActive(preset) && !Marked(w.brushPresetRows.Single().Button), "A preset with other settings is marked");
                Click(w.brushPresetRows.Single().Button);
                Check(w.tool == Tool.Brush && ReferenceEquals(w.brushTip, BrushTip.Diamond) && w.brushSize == 140 && Math.Abs(w.hardness - .2) < 1e-9
                    && Math.Abs(w.brushOpacity - .45) < 1e-9 && Math.Abs(w.brushSpacing - .8) < 1e-9 && w.brushAngle == -60, "The preset did not restore every setting");
                Check(w.sizeSlider.Value == 140 && Math.Abs(w.hardnessSlider.Value - .2) < 1e-9 && Math.Abs(w.opacitySlider!.Value - .45) < 1e-9
                    && Parameter(w, "크기 px").Value == 140 && Math.Abs(Parameter(w, "경도 %").Value - 20) < 1e-9 && Parameter(w, "모양 회전 °").Value == -60
                    && Math.Abs(Parameter(w, "찍는 간격 %").Value - 80) < 1e-9, "The panel controls do not show the applied values");
                Check(ReferenceEquals(w.brushTipButtons[BrushTip.Diamond].Background, Theme.Selected) && w.canvas.BrushTipAngle == -60 && w.canvas.BrushRadius == 70,
                    "The shape tile or the cursor did not follow the preset");
                Check(w.IsBrushPresetActive(preset) && Marked(w.brushPresetRows.Single().Button), "The applied preset is not marked active");
                Check(w.doc.Revision == revision && ReferenceEquals(pixels, w.doc.Active!.Pixels) && !w.history.CanUndo && w.history.CanRedo && !w.history.Dirty(w.doc),
                    "Applying a preset edited the document or its history");
                // Every setting change clears the mark, including opacity from the options bar.
                w.opacitySlider!.Value = .5; Check(!Marked(w.brushPresetRows.Single().Button), "Changing opacity kept the mark");
                Click(w.brushPresetRows.Single().Button); Check(Marked(w.brushPresetRows.Single().Button), "Reapplying did not mark the preset");
                Parameter(w, "모양 회전 °").SetValue(10, true); Check(!Marked(w.brushPresetRows.Single().Button), "Changing rotation kept the mark");
                Click(w.brushPresetRows.Single().Button);
                w.hardnessSlider.Value = .9; Check(!Marked(w.brushPresetRows.Single().Button), "Changing hardness in the options bar kept the mark");
                // Brush-type tools keep the tool; the eraser erases with the preset.
                w.SetTool(Tool.Eraser); Click(w.brushPresetRows.Single().Button);
                Check(w.tool == Tool.Eraser && w.hardness == .2, "Applying a preset replaced the eraser");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: image shapes are restored by ID and a missing shape falls back to round", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                var custom = BrushTip.FromAlpha("가로 팁", 8, 2, Enumerable.Repeat((byte)255, 16).ToArray());
                w.customBrushTips.Add(custom); w.UpdateCustomBrushList();
                w.customBrushPicker!.SelectedItem = custom;
                var preset = w.AddBrushPreset("이미지 브러시");
                Check(preset.TipId == custom.Id, "The image shape was not saved by its ID");
                w.SelectBrushTip(BrushTip.Square); Click(w.brushPresetRows.Single().Button);
                Check(ReferenceEquals(w.brushTip, custom) && Marked(w.brushPresetRows.Single().Button), "The image shape was not restored");
                w.customBrushTips.Clear(); w.UpdateCustomBrushList(); w.RebuildBrushPresetList();
                Check(Descendants(w.brushPresetRows.Single().Button).OfType<TextBlock>().Any(t => t.Text == "이미지 모양 없음 · 원형으로 적용"), "The row does not say its shape is missing");
                Click(w.brushPresetRows.Single().Button);
                Check(ReferenceEquals(w.brushTip, BrushTip.Round) && w.brushSize == preset.Size && w.status.Text.Contains("원형으로 적용"), "A missing shape did not fall back to round with a notice");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: rename, overwrite, move and delete are stored and survive a restart", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                SetBrush(w, BrushTip.Round, 20, 80, 1, 10, 0); var a = w.AddBrushPreset("하나");
                SetBrush(w, BrushTip.Square, 60, 50, .7, 30, 15); var b = w.AddBrushPreset("둘");
                SetBrush(w, BrushTip.Star, 200, 10, .3, 120, 90); var c = w.AddBrushPreset("셋");
                w.RenameBrushPreset(b.Id, "  새\n이름 " + new string('가', 60));
                var renamed = w.brushPresets[1];
                Check(renamed.Id == b.Id && renamed.Name.StartsWith("새이름 ") && renamed.Name.Length == BrushPreset.MaxNameLength && renamed.SameSettings(b), "Rename changed settings or kept a long name");
                w.RenameBrushPreset(b.Id, "   "); Check(w.brushPresets[1].Name == renamed.Name, "An empty rename cleared the name");
                Check(Marked(w.brushPresetRows[2].Button) && !Marked(w.brushPresetRows[0].Button), "Only the current settings' preset should be marked");
                w.OverwriteBrushPreset(a.Id);
                var overwritten = w.brushPresets[0];
                Check(overwritten.Id == a.Id && overwritten.Name == "하나" && overwritten.SameSettings(c) && !overwritten.SameSettings(a), "Overwrite did not keep the name and place with the current settings");
                Check(Marked(w.brushPresetRows[0].Button) && Marked(w.brushPresetRows[2].Button), "Both presets equal to the current brush should be marked");
                w.MoveBrushPreset(c.Id, -1); w.MoveBrushPreset(c.Id, -1); w.MoveBrushPreset(c.Id, -1);
                Check(w.brushPresets.Select(p => p.Id).SequenceEqual(new[] { c.Id, a.Id, b.Id }), "Moving did not reorder or did not stop at the top");
                var menu = w.brushPresetRows[0].Button.ContextMenu!;
                var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header);
                Check(items.Keys.SequenceEqual(new[] { "적용", "이름 바꾸기…", "현재 설정으로 덮어쓰기", "위로 이동", "아래로 이동", "내 프리셋에서 삭제" })
                    && !items["위로 이동"].IsEnabled && items["아래로 이동"].IsEnabled, "The row menu lacks its commands or offers moving the first row up");
                items["내 프리셋에서 삭제"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(w.brushPresets.Select(p => p.Id).SequenceEqual(new[] { a.Id, b.Id }) && w.brushPresetRows.Count == 2, "Delete from the menu did not remove the preset");
                Check(BrushPresetStore.Load(store).SequenceEqual(w.brushPresets), "The store does not match the list after editing");
                var restarted = Open(store);
                try
                {
                    Check(restarted.brushPresets.SequenceEqual(w.brushPresets) && restarted.brushPresetRows.Count == 2
                        && restarted.brushPresetRows.Select(r => AutomationProperties.GetName(r.Button)).SequenceEqual(w.brushPresets.Select(p => p.Name)), "The presets did not survive a restart");
                }
                finally { restarted.StopRenderingForShutdown(); }
                w.DeleteBrushPreset(a.Id); w.DeleteBrushPreset(b.Id);
                Check(w.brushPresets.Count == 0 && BrushPresetStore.Load(store).Count == 0
                    && Descendants(w.brushPresetList!).OfType<TextBlock>().Any(t => t.Text.StartsWith("자주 쓰는 브러시 설정을 저장하면")), "The empty list does not explain itself");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: the list stops at 64 and a failed write leaves it unchanged", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                for (int i = 0; i < BrushPresetStore.MaxPresets; i++) w.AddBrushPreset($"브러시 {i}");
                bool refused = false;
                try { w.AddBrushPreset("하나 더"); } catch (InvalidDataException) { refused = true; }
                bool dialogRefused = false; w.brushPresetDialogRunner = _ => throw new InvalidOperationException("The dialog opened at the limit");
                try { w.SaveBrushPresetWithDialog(); } catch (InvalidDataException) { dialogRefused = true; }
                Check(refused && dialogRefused && w.brushPresets.Count == BrushPresetStore.MaxPresets && BrushPresetStore.Load(store).Count == BrushPresetStore.MaxPresets,
                    "The limit was not enforced");
                // The store's folder is a file: the write fails and the list is restored.
                string blocker = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]); File.WriteAllText(blocker, "");
                var other = Open(Path.Combine(blocker, "brush-presets.json"));
                try
                {
                    bool failed = false;
                    try { other.AddBrushPreset("저장 실패"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
                    Check(failed && other.brushPresets.Count == 0 && other.brushPresetRows.Count == 0, "A failed write left the preset in the list");
                }
                finally { other.StopRenderingForShutdown(); }
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("brush presets: Ctrl+K offers saving the current brush as a preset", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                var command = w.BuildCommandRegistry().Single(c => c.Id == "brush:save-preset");
                Check(command.SourceTitle == "현재 브러시를 프리셋으로 저장…" && command.IsAvailable(), "The palette entry is missing or unavailable");
                w.brushPresetDialogRunner = dialog => { dialog.Accept(); dialog.Close(); return true; };
                w.RunCommand(command);
                Check(w.brushPresets.Count == 1 && w.recentCommands[0] == "brush:save-preset", "Running the palette entry did not save a preset");
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }
}
