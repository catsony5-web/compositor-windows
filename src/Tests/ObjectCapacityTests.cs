using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ObjectCapacityTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}");
        }
        static Layer Folder(Raster pixels, string name = "원본 레이어") => new() { Name = name, Kind = LayerKind.Group, Pixels = pixels };
        static VectorContent Vector() => VectorContent.FromPaths(1, 1,
            [new VectorPrimitive(new RectangleGeometry(new Rect(0, 0, 1, 1)), Colors.Green, true, 0)]);
        static Layer Object(VectorContent source, Guid? parent = null) => new()
        {
            Name = "선", Kind = LayerKind.Vector, Pixels = Raster.Solid(1, 1, Colors.Green), Vector = source, ParentId = parent
        };

        test("object scenes retain independent selection and native persistence beyond 128 layers", () =>
        {
            var doc = new Document { Width = 64, Height = 64, Name = "객체 도면" };
            var folder = Folder(new Raster(64, 64)); doc.Add(folder);
            var source = Vector();
            for (int i = 0; i < 2048; i++)
            {
                var layer = Object(source, folder.Id); layer.X = i % 64; layer.Y = i / 64;
                layer.Name = $"선 {i} " + new string('도', 70); doc.Add(layer);
            }
            doc.Validate();
            Check(LayerPicking.Pick(doc, new Point(12.5, 10.5))?.Id == doc.Layers[1 + 10 * 64 + 12].Id, "Grouped object did not select independently");
            string path = Path.Combine(directory, "object-scene-capacity.moruproj"); ProjectStore.Save(doc, path);
            using (var zip = ZipFile.OpenRead(path)) Check(zip.GetEntry("document.json")!.Length > 1024 * 1024, "Fixture did not exceed the previous 1 MiB header limit");
            var opened = ProjectStore.Load(path);
            Check(opened.Layers.Count == doc.Layers.Count && opened.ActiveId == doc.ActiveId, "Large scene changed on reopen");
            Check(opened.Layers.Select(l => (l.Id, l.ParentId, l.Name)).SequenceEqual(doc.Layers.Select(l => (l.Id, l.ParentId, l.Name))), "Object hierarchy or names changed");
            Check(Imaging.Render(opened).Data.SequenceEqual(Imaging.Render(doc).Data), "Large scene pixels changed on reopen");
        });
        test("object node allowance is bounded independently of bitmap layers", () =>
        {
            var doc = new Document { Width = 1, Height = 1 }; var pixels = new Raster(1, 1);
            for (int i = 0; i < Document.MaxNodes; i++) doc.Layers.Add(Folder(pixels));
            doc.Validate();
            Reject<InvalidOperationException>(() => doc.Add(Folder(pixels)));
            Check(doc.Layers.Count == Document.MaxNodes, "Rejected addition mutated the scene");
            doc.Layers.Add(Folder(pixels)); Reject<InvalidDataException>(doc.Validate);
        });
        test("bitmap layer ceiling remains 128 in object scenes", () =>
        {
            var doc = new Document { Width = 1, Height = 1 };
            for (int i = 0; i < Document.MaxLayers; i++) doc.Add(new Layer { Pixels = new Raster(1, 1) });
            doc.Add(Folder(new Raster(1, 1))); doc.Add(Object(Vector())); doc.Validate();
            Reject<InvalidOperationException>(() => doc.Add(new Layer { Pixels = new Raster(1, 1) }));
            doc.Layers.Add(new Layer { Pixels = new Raster(1, 1) }); Reject<InvalidDataException>(doc.Validate);
        });
        test("object budgets count shared group surfaces once while retaining bitmap and mask charges", () =>
        {
            var doc = new Document(8) { Width = 1, Height = 1 }; var pixels = new Raster(1, 1);
            doc.Add(Folder(pixels)); doc.Add(Folder(pixels)); doc.Add(Object(Vector())); doc.Validate();
            doc.Add(Folder(pixels)); doc.Validate();
            Reject<InvalidOperationException>(() => doc.Add(Folder(new Raster(1, 1))));
            var duplicate = doc.Layers[2].Snapshot(); duplicate.Id = Guid.NewGuid();
            Reject<InvalidOperationException>(() => doc.Add(duplicate));
            var maskedGroup = Folder(pixels); maskedGroup.Mask = [255];
            Reject<InvalidOperationException>(() => doc.Add(maskedGroup));
            var masked = new Document(4) { Width = 1, Height = 1 };
            var layer = Object(Vector()); layer.Mask = [255];
            Reject<InvalidOperationException>(() => masked.Add(layer));
        });
        test("native loading shares verified empty group surfaces without sharing nonempty pixels", () =>
        {
            var doc = new Document { Width = 16, Height = 16 }; var pixels = new Raster(16, 16);
            var one = Folder(pixels, "첫 레이어"); var two = Folder(pixels, "둘째 레이어");
            var nonempty = Folder(Raster.Solid(16, 16, Colors.Red), "기존 그룹 표면");
            doc.Add(one); doc.Add(two); doc.Add(nonempty);
            var item = Object(Vector(), two.Id); item.X = 11; item.Y = 7; doc.Add(item);
            string path = Path.Combine(directory, "object-group-sharing.moruproj"); ProjectStore.Save(doc, path);
            var opened = ProjectStore.Load(path);
            Check(ReferenceEquals(opened.Layers[0].Pixels, opened.Layers[1].Pixels), "Empty group surfaces were duplicated");
            Check(!ReferenceEquals(opened.Layers[0].Pixels, opened.Layers[2].Pixels) && opened.Layers[2].Pixels.Data[2] == 255, "Nonempty payload changed or was incorrectly interned");
            Check(Imaging.Render(doc).Data.SequenceEqual(Imaging.Render(opened).Data), "Group sharing changed the composite");
        });
        test("native metadata cap rejects oversized load before payload decoding", () =>
        {
            string path = Path.Combine(directory, "object-oversized-header.moruproj");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("document.json", CompressionLevel.Optimal).Open())
            {
                var chunk = new byte[1024 * 1024]; Array.Fill(chunk, (byte)' ');
                for (int i = 0; i < 33; i++) output.Write(chunk);
            }
            Reject<InvalidDataException>(() => ProjectStore.Load(path));
        });
        test("oversized scene metadata is rejected before replacing an existing project", () =>
        {
            var doc = new Document { Width = 1, Height = 1 }; var pixels = new Raster(1, 1);
            string name = new('가', 4000);
            for (int i = 0; i < 1500; i++) doc.Layers.Add(Folder(pixels, name));
            string path = Path.Combine(directory, "object-atomic-metadata.moruproj");
            byte[] original = [1, 3, 7, 9]; File.WriteAllBytes(path, original);
            Reject<InvalidDataException>(() => ProjectStore.Save(doc, path));
            Check(File.ReadAllBytes(path).SequenceEqual(original), "Metadata failure replaced the previous project");
            Check(!Directory.EnumerateFiles(directory, "object-atomic-metadata.moruproj.*.tmp").Any(), "Metadata failure left a partial file");
        });
        test("large object scenes cannot create unreadable legacy layered exports", () =>
        {
            var doc = new Document { Width = 1, Height = 1 }; var source = Vector();
            for (int i = 0; i < Document.MaxLayers; i++) doc.Add(Object(source));
            Check(PhotoshopCompatibility.CanWriteLayers(doc), "The existing 128-layer PSD limit was narrowed");
            doc.Add(Object(source));
            Check(!PhotoshopCompatibility.CanWriteLayers(doc), "Layered PSD must reject scenes the importer cannot reopen");
            using var output = new MemoryStream();
            Reject<InvalidDataException>(() => PhotoshopCompatibility.Write(doc, output, true));
            Check(output.Length == 0, "Rejected PSD wrote a partial header");
            string package = Path.Combine(directory, "too-many-legacy-objects.comp");
            Reject<InvalidDataException>(() => CompositorPackage.Export(doc, package));
            Check(!Directory.Exists(package) && !Directory.EnumerateDirectories(directory, "too-many-legacy-objects.comp.*.tmp").Any(), "Rejected package created output folders");
            string flattened = Path.Combine(directory, "many-objects-flattened.psd");
            using (var file = File.Create(flattened)) PhotoshopCompatibility.Write(doc, file, false);
            var reopened = PhotoshopCompatibility.Read(flattened, true).Document;
            Check(reopened.Layers.Count == 1 && Imaging.Render(reopened).Data.SequenceEqual(Imaging.Render(doc).Data), "Large scenes must still export a usable merged PSD");
        });
        test("native version four stores shared group pixels once and preserves each mask and transform", () =>
        {
            var doc = new Document { Width = 32, Height = 32 }; var surface = new Raster(32, 32);
            for (int i = 0; i < 1500; i++) doc.Layers.Add(Folder(surface, $"CAD 레이어 {i}"));
            doc.Layers[0].X = 3; doc.Layers[0].Y = 1;
            doc.Layers[1].X = 11; doc.Layers[1].Opacity = .5;
            doc.Layers[1].Mask = Enumerable.Repeat((byte)128, 32 * 32).ToArray();
            var one = Object(Vector(), doc.Layers[0].Id); var two = Object(Vector(), doc.Layers[1].Id);
            doc.Layers.Add(one); doc.Layers.Add(two); doc.ActiveId = two.Id; doc.Validate();
            string path = Path.Combine(directory, "shared-group-v4.moruproj"); ProjectStore.Save(doc, path);
            using (var zip = ZipFile.OpenRead(path))
            {
                using var json = zip.GetEntry("document.json")!.Open(); var manifest = JsonSerializer.Deserialize<ProjectStore.Manifest>(json)!;
                Check(manifest.Version == 4 && manifest.Layers![0]!.SharedGroupPixelsIndex == null && manifest.Layers[1499]!.SharedGroupPixelsIndex == 0, "Shared group references were not stored in version four");
                Check(zip.Entries.Count(e => e.FullName.EndsWith(".png")) == 3 && zip.GetEntry("layers/1.mask") != null, "Repeated group surfaces still produce PNG payloads, or a distinct mask was lost");
            }
            var opened = ProjectStore.Load(path);
            Check(opened.Layers.Take(1500).All(l => ReferenceEquals(l.Pixels, opened.Layers[0].Pixels)), "Shared group references were decoded into duplicate surfaces");
            Check(opened.Layers[0].X == 3 && opened.Layers[1].X == 11 && opened.Layers[1].Opacity == .5 && opened.Layers[0].Mask == null && opened.Layers[1].Mask!.All(v => v == 128), "Shared surfaces coupled unrelated group properties");
            Check(Imaging.Render(doc).Data.SequenceEqual(Imaging.Render(opened).Data), "Version four changed grouped pixels");
        });
        test("native shared group references reject forward self negative and nongroup references before decoding", () =>
        {
            ProjectStore.LayerInfo Info(LayerKind kind = LayerKind.Group, int? reference = null) => new()
            {
                Id = Guid.NewGuid(), Name = "그룹", Kind = kind, Scale = 1, Opacity = 1, Visible = true, SharedGroupPixelsIndex = reference
            };
            var cases = new (int Version, ProjectStore.LayerInfo[] Layers)[]
            {
                (4, [Info(reference: 1), Info()]), (4, [Info(reference: 0)]), (4, [Info(reference: -1)]),
                (4, [Info(LayerKind.Raster), Info(reference: 0)]), (4, [Info(), Info(LayerKind.Raster, 0)]),
                (3, [Info(), Info(reference: 0)])
            };
            for (int index = 0; index < cases.Length; index++)
            {
                var item = cases[index]; string path = Path.Combine(directory, $"bad-shared-group-{index}.moruproj");
                using (var file = File.Create(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                using (var json = zip.CreateEntry("document.json").Open())
                    JsonSerializer.Serialize(json, new ProjectStore.Manifest { Version = item.Version, Width = 1, Height = 1, Name = "잘못된 참조", Layers = item.Layers.Cast<ProjectStore.LayerInfo?>().ToList() });
                bool rejected = false;
                try { ProjectStore.Load(path); } catch (InvalidDataException e) { rejected = e.Message.Contains("공유 그룹 이미지 참조"); }
                Check(rejected, $"Case {index} was not rejected before searching for pixel payloads");
            }
        });
        test("native projects without shared group surfaces retain earlier format versions", () =>
        {
            var doc = new Document { Width = 1, Height = 1 }; doc.Add(Object(Vector()));
            string path = Path.Combine(directory, "unshared-vector-v3.moruproj"); ProjectStore.Save(doc, path);
            using (var zip = ZipFile.OpenRead(path))
            using (var json = zip.GetEntry("document.json")!.Open())
                Check(JsonSerializer.Deserialize<ProjectStore.Manifest>(json)!.Version == 3, "An unshared project unnecessarily requires the new format");
            Check(ProjectStore.Load(path).Layers[0].Vector != null, "Previous vector project format no longer loads");
        });
    }
}
