using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

// 내 프리셋 values and the per-user brush-presets.json store: ranges, names, round trips,
// damaged and oversized files, and the 64-preset limit. Only temporary paths are used.
public static class BrushPresetTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "brush-preset-store"); Directory.CreateDirectory(root);
        string NewStore() => Path.Combine(root, Guid.NewGuid().ToString("N")[..8], "brush-presets.json");
        static BrushPreset Sample(string name, string tip = "star", double size = 77) =>
            new(Guid.NewGuid(), name, tip, size, .35, .6, .5, 30);

        test("brush preset values stay in the panel's ranges and names are cleaned", () =>
        {
            var wild = new BrushPreset(Guid.NewGuid(), "  굵은\n별\t", "round", 5000, 1.5, 0, 9, -400);
            var clean = BrushPreset.Normalize(wild)!;
            Check(clean.Size == BrushPreset.MaxSize && clean.Hardness == 1 && clean.Opacity == BrushPreset.MinOpacity
                && clean.Spacing == BrushPreset.MaxSpacing && clean.Angle == -180 && clean.Name == "굵은별", "Values were not clamped or the name was not cleaned: " + clean);
            Check(BrushPreset.Normalize(wild with { Size = double.NaN }) == null && BrushPreset.Normalize(wild with { Angle = double.PositiveInfinity }) == null,
                "A value that is not a number was accepted");
            Check(BrushPreset.Normalize(wild with { Id = Guid.Empty }) == null && BrushPreset.Normalize(wild with { TipId = "../brush" }) == null
                && BrushPreset.Normalize(wild with { TipId = "" }) == null && BrushPreset.Normalize(null) == null, "An unusable ID or shape was accepted");
            Check(BrushPreset.CleanName(new string('가', 90)).Length == BrushPreset.MaxNameLength && BrushPreset.CleanName(" \r\n ", "기본") == "기본",
                "Long or empty names were not limited");
            var hex = new string('a', 64);
            Check(BrushPreset.IsValidTipId(hex) && BrushPreset.IsValidTipId("round") && !BrushPreset.IsValidTipId(new string('a', 65)), "Shape IDs of built-in and image brushes are not accepted");
        });

        test("brush preset matching compares displayed settings, not names", () =>
        {
            var a = Sample("하나");
            Check(a.SameSettings(a with { Id = Guid.NewGuid(), Name = "둘", Size = 77.3, Hardness = .352, Angle = 30.2 }), "Display-equal settings did not match");
            Check(!a.SameSettings(a with { Size = 78 }) && !a.SameSettings(a with { Hardness = .36 }) && !a.SameSettings(a with { Opacity = .61 })
                && !a.SameSettings(a with { Spacing = .51 }) && !a.SameSettings(a with { Angle = 31 }) && !a.SameSettings(a with { TipId = "round" }),
                "A changed setting still matched");
        });

        test("brush preset store round-trips every setting in order", () =>
        {
            string store = NewStore();
            Check(BrushPresetStore.Load(store).Count == 0, "A missing store is not empty");
            var custom = new string('c', 64);
            BrushPreset[] saved = [Sample("굵은 별"), new(Guid.NewGuid(), "이미지 붓", custom, 12.5, .8, 1, .1, -45.5), Sample("세 번째", "diamond", 300)];
            BrushPresetStore.Save(saved, store);
            var loaded = BrushPresetStore.Load(store);
            Check(loaded.SequenceEqual(saved), "Loaded presets differ from the saved ones");
            Check(Directory.GetFiles(Path.GetDirectoryName(store)!).Length == 1, "A temporary file was left next to the store");
            using var json = JsonDocument.Parse(File.ReadAllText(store));
            Check(json.RootElement.GetProperty("Version").GetInt32() == 1 && json.RootElement.GetProperty("Presets").GetArrayLength() == 3, "The store is not version 1 with three presets");
        });

        test("brush preset store skips damaged entries and ignores broken or oversized files", () =>
        {
            string store = NewStore(); Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            var good = Sample("멀쩡한 브러시"); var duplicate = good with { Name = "중복" };
            string Entry(BrushPreset p) => JsonSerializer.Serialize(p);
            File.WriteAllText(store, "{\"Version\":1,\"Presets\":[" + string.Join(",",
                Entry(good),
                "{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"숫자 아님\",\"TipId\":\"round\",\"Size\":\"큼\",\"Hardness\":1,\"Opacity\":1,\"Spacing\":0.1,\"Angle\":0}",
                "{\"Name\":\"ID 없음\",\"TipId\":\"round\",\"Size\":10,\"Hardness\":1,\"Opacity\":1,\"Spacing\":0.1,\"Angle\":0}",
                Entry(Sample("잘못된 모양", "C:\\\\brush")),
                "42", "null",
                Entry(duplicate),
                "{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":null,\"TipId\":\"square\",\"Size\":20000,\"Hardness\":0.5,\"Opacity\":0.5,\"Spacing\":0.2,\"Angle\":10}") + "]}");
            var loaded = BrushPresetStore.Load(store);
            Check(loaded.Count == 2 && loaded[0] == good && loaded[1].TipId == "square" && loaded[1].Size == BrushPreset.MaxSize && loaded[1].Name.Length > 0,
                "Damaged entries were not skipped or kept entries not cleaned: " + string.Join(" | ", loaded));
            foreach (string broken in new[] { "{ 깨진", "[]", "{\"Version\":2,\"Presets\":[]}", "{\"Version\":1}", "{\"Version\":\"1\",\"Presets\":[]}", "" })
            {
                File.WriteAllText(store, broken);
                Check(BrushPresetStore.Load(store).Count == 0, "A broken store was not read as empty: " + broken);
            }
            BrushPresetStore.Save([good], store);
            File.WriteAllText(store, File.ReadAllText(store).TrimEnd() + new string(' ', 300 * 1024));
            Check(BrushPresetStore.Load(store).Count == 0, "An oversized store was read");
            File.WriteAllBytes(store, [0xFF, 0xFE, 0x00, 0x01, 0x02]);
            Check(BrushPresetStore.Load(store).Count == 0, "A binary store was not read as empty");
        });

        test("brush preset store keeps at most 64 presets and drops duplicates on save", () =>
        {
            string store = NewStore();
            var many = Enumerable.Range(1, 70).Select(i => Sample($"브러시 {i}", size: i)).ToList();
            many.Insert(1, many[0] with { Name = "같은 ID" });
            BrushPresetStore.Save(many, store);
            var loaded = BrushPresetStore.Load(store);
            Check(loaded.Count == BrushPresetStore.MaxPresets && loaded[0].Name == "브러시 1" && loaded[1].Name == "브러시 2" && loaded.Select(p => p.Id).Distinct().Count() == loaded.Count,
                "The store did not keep the first 64 unique presets");
            // A file written by hand with more entries is cut at the limit when read.
            var stored = new { Version = 1, Presets = Enumerable.Range(1, 80).Select(i => Sample($"손 {i}")).ToArray() };
            File.WriteAllText(store, JsonSerializer.Serialize(stored));
            Check(BrushPresetStore.Load(store).Count == BrushPresetStore.MaxPresets, "Reading did not stop at the limit");
        });
    }
}
