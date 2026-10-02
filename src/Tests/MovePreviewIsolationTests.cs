using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// The compositor renders a folder as an isolated surface when a child reads its
// backdrop (Multiply, clipping, adjustment) or a clipped layer sits on the folder.
// The move preview's planes must keep that surface instead of flattening the
// children onto whatever is under the folder. Also: a move-tool click that does
// not move anything must not render the document again.
public sealed partial class MainWindow
{
    internal static void RunMovePreviewIsolationTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static Layer Solid(int w, int h, Color color, double x, double y, Guid? parent = null, BlendMode blend = BlendMode.Normal) =>
            new() { Pixels = Raster.Solid(w, h, color), X = x, Y = y, ParentId = parent, Blend = blend };
        static Layer Folder(Document document) => new() { Kind = LayerKind.Group, Name = "folder", Pixels = new Raster(document.Width, document.Height) };
        // The three planes composed as CanvasView draws them, after moving the object.
        static Raster Planes(Document document, Guid movingId, double dx, double dy)
        {
            var (below, above) = CreateLayerMovePreviewStacks(document, movingId);
            var moved = document.Layers.Single(l => l.Id == movingId).Snapshot(); moved.X += dx; moved.Y += dy; moved.ParentId = null;
            var composed = Imaging.Render(below);
            Imaging.Composite(composed, moved);
            if (above.Layers.Count > 0) Imaging.Composite(composed, new Layer { Pixels = Imaging.Render(above) });
            return composed;
        }
        static Raster Expected(Document document, Guid movingId, double dx, double dy)
        {
            var moved = document.Snapshot(); var layer = moved.Layers.Single(l => l.Id == movingId); layer.X += dx; layer.Y += dy;
            return Imaging.Render(moved);
        }
        static Raster Flattened(Document document, Guid movingId, double dx, double dy)
        {
            var flat = document.Snapshot(); flat.Layers.RemoveAll(l => l.Kind == LayerKind.Group);
            foreach (var layer in flat.Layers) layer.ParentId = null;
            var mover = flat.Layers.Single(l => l.Id == movingId); mover.X += dx; mover.Y += dy;
            return Imaging.Render(flat);
        }
        static void Exact(Document document, Layer mover, string what)
        {
            Check(CanPreviewLayerMove(document, mover, 1), what + ": not eligible for the fast move preview");
            Check(Planes(document, mover.Id, 6, 3).Data.AsSpan().SequenceEqual(Expected(document, mover.Id, 6, 3).Data), what + ": the move preview differs from the full render");
        }
        static (Document Document, Layer Folder, Layer Multiply) Scene()
        {
            // An opaque backdrop, then a folder with a fill and a Multiply child that also covers the backdrop.
            var document = new Document { Width = 32, Height = 24 };
            document.Add(Solid(32, 24, Color.FromRgb(90, 140, 200), 0, 0)); document.Layers[0].Locked = true;
            var folder = Folder(document); document.Add(folder);
            document.Add(Solid(12, 10, Color.FromRgb(220, 200, 40), 2, 2, folder.Id));
            var multiply = Solid(14, 12, Color.FromRgb(120, 60, 160), 8, 6, folder.Id, BlendMode.Multiply); document.Add(multiply);
            return (document, folder, multiply);
        }

        test("move preview keeps a folder with a Multiply child below the mover as one isolated surface", () =>
        {
            var (document, folder, _) = Scene();
            var mover = Solid(5, 5, Colors.Red, 20, 14); document.Add(mover); document.ActiveId = mover.Id;
            // The scene must actually distinguish isolated from flattened compositing.
            Check(!Flattened(document, mover.Id, 6, 3).Data.AsSpan().SequenceEqual(Expected(document, mover.Id, 6, 3).Data),
                "The scene does not exercise folder isolation; the check is not meaningful");
            Exact(document, mover, "Object above an isolated folder");
            var (below, _) = CreateLayerMovePreviewStacks(document, mover.Id);
            Check(below.Layers.Any(l => l.Id == folder.Id && l.Kind == LayerKind.Group) && below.Layers.Count(l => l.ParentId == folder.Id) == 2,
                "The isolated folder was not kept intact in the stack below");
        });

        test("move preview splits an isolated folder that holds the mover and keeps flattening bottom folders", () =>
        {
            var (document, folder, multiply) = Scene();
            var mover = Solid(5, 5, Colors.Red, 4, 4, folder.Id); document.Add(mover); document.ActiveId = mover.Id;
            Check(!Flattened(document, mover.Id, 6, 3).Data.AsSpan().SequenceEqual(Expected(document, mover.Id, 6, 3).Data),
                "The scene does not exercise folder isolation; the check is not meaningful");
            Exact(document, mover, "Object inside an isolated folder over other content");
            folder.Blend = BlendMode.Multiply;
            Check(!CanPreviewLayerMove(document, mover, 1), "A Multiply folder holding the mover was split into normal planes");
            folder.Blend = BlendMode.Normal;
            document.Layers.Remove(multiply); document.Layers.Add(multiply);
            Check(!CanPreviewLayerMove(document, mover, 1), "A Multiply child above the mover inside its folder was flattened");

            // The CAD case: one folder at the bottom holds the paper, a Multiply hatch and the linework.
            var drawing = new Document { Width = 32, Height = 24 };
            var root = Folder(drawing); drawing.Add(root);
            drawing.Add(Solid(32, 24, Colors.White, 0, 0, root.Id));
            drawing.Add(Solid(16, 12, Color.FromRgb(150, 120, 90), 6, 5, root.Id, BlendMode.Multiply));
            var lines = Folder(drawing); lines.ParentId = root.Id; drawing.Add(lines);
            drawing.Add(Solid(10, 2, Colors.Black, 3, 18, lines.Id));
            var wall = Solid(12, 2, Colors.Black, 4, 9, lines.Id); drawing.Add(wall); drawing.ActiveId = wall.Id;
            drawing.Add(Solid(2, 14, Colors.DarkGreen, 24, 4, lines.Id));
            Exact(drawing, wall, "Linework in a bottom drawing folder over Multiply hatch");
            var (below, above) = CreateLayerMovePreviewStacks(drawing, wall.Id);
            Check(below.Layers.Concat(above.Layers).All(l => l.Kind != LayerKind.Group && l.ParentId == null), "A bottom folder was not flattened");
        });

        test("move preview keeps a folder with a clipped layer on top as one surface", () =>
        {
            var document = new Document { Width = 32, Height = 24 };
            document.Add(Solid(32, 24, Color.FromRgb(30, 40, 50), 0, 0)); document.Layers[0].Locked = true;
            var folder = Folder(document); document.Add(folder);
            document.Add(Solid(8, 8, Color.FromRgb(200, 30, 30), 2, 2, folder.Id));
            document.Add(Solid(8, 8, Color.FromRgb(30, 200, 30), 14, 2, folder.Id));
            var clip = Solid(24, 6, Color.FromRgb(240, 240, 80), 0, 4); clip.Clipped = true; document.Add(clip);
            var mover = Solid(4, 4, Colors.Blue, 10, 16); document.Add(mover); document.ActiveId = mover.Id;
            Check(!Flattened(document, mover.Id, 6, 3).Data.AsSpan().SequenceEqual(Expected(document, mover.Id, 6, 3).Data),
                "The scene does not exercise clipping to a folder; the check is not meaningful");
            Exact(document, mover, "Object above a folder with a clipped layer");
        });

        test("a move-tool click without movement does not render the document again or add an undo step", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var (document, _, _) = Scene();
                var mover = Solid(5, 5, Colors.Red, 20, 14); document.Add(mover);
                window.AddTab(document, null); window.SetTool(Tool.Move);
                window.doc.ActiveId = mover.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(mover.Id);
                var point = new Point(22, 16);
                void Finish(bool moved)
                {
                    window.beforeGesture = window.doc.Snapshot(); window.dragging = false; window.moveStarted = moved;
                    if (moved) window.doc.Active!.X += 5;
                    window.CommitPointerGesture(point);
                }
                var revision = window.doc.Revision; int requests = window.FullRenderRequests;
                Finish(false);
                Check(window.FullRenderRequests == requests, "A click without movement queued a full render");
                Check(window.doc.Revision == revision && !window.history.CanUndo && window.beforeGesture == null, "A click without movement changed the document or its history");
                Check(window.canvas.SelectedObjectIds.SetEquals([mover.Id]) && ReferenceEquals(window.canvas.Document, window.doc), "The click did not refresh the selection display");
                Finish(true);
                Check(window.FullRenderRequests == requests + 1 && window.doc.Revision != revision && window.history.CanUndo, "A real move did not render and record its change");
            }
            finally { window.StopRenderingForShutdown(); }
        });
    }
}
