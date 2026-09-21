using System.IO;
using System.IO.Compression;
using System.Windows.Media;

namespace Compositor.Windows;

public static class PersistenceTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool condition, string message = "Assertion failed")
        {
            if (!condition) throw new Exception(message);
        }
        static void ExpectInvalidData(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Expected InvalidDataException");
        }
        static void ExpectInvalidOperation(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected InvalidOperationException");
        }
        static Document Single(int width = 1, int height = 1)
        {
            var document = new Document { Width = width, Height = height, Name = "테스트" };
            document.Add(new Layer { Name = "레이어", Pixels = Raster.Solid(width, height, Colors.Red) });
            return document;
        }

        test("image decoder rejects an oversized bitmap header", () =>
        {
            const int width = 8193, height = 1;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write((ushort)0x4D42); writer.Write(54 + width * height * 4);
                writer.Write(0); writer.Write(54); writer.Write(40);
                writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(width * height * 4);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(new byte[width * height * 4]);
            }
            stream.Position = 0;
            ExpectInvalidData(() => Raster.Load(stream));
        });

        test("document rejects malformed masks and duplicate IDs", () =>
        {
            var malformed = new Document { Width = 1, Height = 1, Name = "테스트" };
            ExpectInvalidData(() => malformed.Add(new Layer { Pixels = new Raster(1, 1), Mask = [255, 255] }));

            var document = Single();
            var duplicate = document.Active!.Snapshot();
            ExpectInvalidOperation(() => document.Add(duplicate));
        });

        test("document byte budget includes incoming mask", () =>
        {
            var exact = new Document(5) { Width = 1, Height = 1, Name = "테스트" };
            exact.Add(new Layer { Pixels = new Raster(1, 1), Mask = [255] });
            Assert(exact.Layers.Count == 1);

            var over = new Document(4) { Width = 1, Height = 1, Name = "테스트" };
            ExpectInvalidOperation(() => over.Add(new Layer { Pixels = new Raster(1, 1), Mask = [255] }));
        });

        test("history removes a sole over-budget entry", () =>
        {
            var document = Single();
            var history = new History(retainedByteLimit: 0);
            history.Reset(document);
            var before = document.Snapshot();
            document.Layers.Clear();
            document.ActiveId = Guid.Empty;
            history.Commit("삭제", before, document);
            Assert(!history.CanUndo);
            Assert(history.RetainedBytes(document) == 0);
        });

        test("history trims redo after current state changes retained bytes", () =>
        {
            var document = Single(1, 1);
            var history = new History(retainedByteLimit: 5);
            history.Reset(document);
            var before = document.Snapshot();
            document.Active!.Pixels = Raster.Solid(2, 1, Colors.Blue);
            history.Commit("교체", before, document);
            Assert(history.CanUndo && history.RetainedBytes(document) == 4);
            document = history.Undo(document);
            Assert(!history.CanRedo);
            Assert(history.RetainedBytes(document) == 0);
        });

        test("no-op history commit preserves redo and saved revision", () =>
        {
            var document = Single();
            var history = new History();
            history.Reset(document);
            var before = document.Snapshot();
            document.Active!.X = 1;
            history.Commit("이동", before, document);
            document = history.Undo(document);
            Assert(history.CanRedo && !history.Dirty(document));
            var revision = document.Revision;
            history.Commit("변경 없음", document.Snapshot(), document);
            Assert(history.CanRedo && !history.Dirty(document) && document.Revision == revision);
        });

        test("invalid save preserves the previous project", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "CompositorPersistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "safe.cwproj");
                var document = Single();
                ProjectStore.Save(document, path);
                var original = File.ReadAllBytes(path);
                document.Active!.Mask = [0, 255];
                ExpectInvalidData(() => ProjectStore.Save(document, path));
                Assert(File.ReadAllBytes(path).SequenceEqual(original), "Invalid save replaced the previous project");
            }
            finally { Directory.Delete(directory, true); }
        });

        test("null and dangling manifest metadata are rejected", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "CompositorManifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                void Reject(string name, string json)
                {
                    string path = Path.Combine(directory, name + ".cwproj");
                    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(zip.CreateEntry("document.json").Open())) writer.Write(json);
                    ExpectInvalidData(() => ProjectStore.Load(path));
                }
                const string empty = "00000000-0000-0000-0000-000000000000";
                Reject("null-name", $$"""{"Version":1,"Width":1,"Height":1,"Name":null,"ActiveId":"{{empty}}","Layers":[]}""");
                Reject("blank-name", $$"""{"Version":1,"Width":1,"Height":1,"Name":"   ","ActiveId":"{{empty}}","Layers":[]}""");
                Reject("null-layers", $$"""{"Version":1,"Width":1,"Height":1,"Name":"테스트","ActiveId":"{{empty}}","Layers":null}""");
                Reject("null-layer", $$"""{"Version":1,"Width":1,"Height":1,"Name":"테스트","ActiveId":"{{empty}}","Layers":[null]}""");
                Reject("dangling-active", """{"Version":1,"Width":1,"Height":1,"Name":"테스트","ActiveId":"11111111-1111-1111-1111-111111111111","Layers":[]}""");
            }
            finally { Directory.Delete(directory, true); }
        });
    }
}
