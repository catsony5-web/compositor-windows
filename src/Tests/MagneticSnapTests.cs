using System.Windows;

namespace Compositor.Windows;

public static class MagneticSnapTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Near(double actual, double expected, string message) =>
            Assert(Math.Abs(actual - expected) < 1e-7, $"{message}: {actual} != {expected}");
        static Layer Box(double x, double y, int width = 40, int height = 30) =>
            new() { X = x, Y = y, Pixels = new Raster(width, height) };
        static Document Scene(params Layer[] layers) =>
            new() { Width = 2000, Height = 1600, Layers = layers.ToList() };
        static MagneticSnapSession Session(Document document, params Guid[] ids) => new(document, ids, []);

        test("magnetic drag aligns the nearest edge and draws one bounded guide", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var result = Session(Scene(moving, target), moving.Id).Resolve(new Vector(165, 20), 1);
            Near(result.Delta.X, 169, "Right edge did not meet target left edge");
            Near(result.Delta.Y, 20, "Unaligned Y should remain free");
            Assert(result.Guides.Count == 1 && result.Guides[0].Vertical, "Unexpected guide count or direction");
            Near(result.Guides[0].Position, 310, "Incorrect guide position");
            Near(result.Guides[0].Start, 157, "Guide should start at moving bounds");
            Near(result.Guides[0].End, 490, "Guide should end at target bounds");
        });
        test("magnetic drag aligns object centers and canvas center", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var result = Session(Scene(moving, target), moving.Id).Resolve(new Vector(226, 0), 1);
            Near(result.Delta.X, 229, "Object centers did not align");
            Near(result.Guides.Single(guide => guide.Vertical).Position, 350, "Wrong center target");
            result = Session(Scene(moving), moving.Id).Resolve(new Vector(876, 0), 1);
            Near(result.Delta.X, 879, "Canvas center did not align");
        });
        test("magnetic acquire distance is constant in screen coordinates at every zoom", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70); var doc = Scene(moving, target);
            foreach (double zoom in new[] { .5, 1.0, 2.0, 4.0 })
            {
                var near = Session(doc, moving.Id).Resolve(new Vector(169 - 6 / zoom, 0), zoom);
                Near(near.Delta.X, 169, $"Six screen pixels should acquire at zoom {zoom}");
                var outside = Session(doc, moving.Id).Resolve(new Vector(169 - 6.1 / zoom, 0), zoom);
                Near(outside.Delta.X, 169 - 6.1 / zoom, $"Snap acquired too far away at zoom {zoom}");
            }
        });
        test("magnetic hysteresis stays attached until release distance then reacquires", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var session = Session(Scene(moving, target), moving.Id);
            Near(session.Resolve(new Vector(165, 0), 1).Delta.X, 169, "Initial acquire failed");
            Near(session.Resolve(new Vector(177, 0), 1).Delta.X, 169, "Latch released inside its hysteresis range");
            Near(session.Resolve(new Vector(179, 0), 1).Delta.X, 169, "Latch should retain at release boundary");
            Near(session.Resolve(new Vector(180, 0), 1).Delta.X, 180, "Latch did not release beyond ten pixels");
            Near(session.Resolve(new Vector(164, 0), 1).Delta.X, 169, "Released edge did not reacquire");
        });
        test("bypassing magnetic drag clears the latch and all guides immediately", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var session = Session(Scene(moving, target), moving.Id);
            _ = session.Resolve(new Vector(165, 0), 1);
            var bypass = session.Resolve(new Vector(177, 0), 1, enabled: false);
            Near(bypass.Delta.X, 177, "Disabled snap changed the pointer delta");
            Assert(bypass.Guides.Count == 0, "Disabled snap retained a guide");
            Near(session.Resolve(new Vector(177, 0), 1).Delta.X, 177, "Re-enabling restored a stale latch");
        });
        test("multiple moving objects snap as one selection without snapping to each other", () =>
        {
            var first = Box(101, 137); var second = Box(181, 137); var target = Box(310, 420, 80, 70);
            var session = Session(Scene(first, second, target), first.Id, second.Id);
            Near(session.Resolve(new Vector(86, 0), 1).Delta.X, 89, "Selection union edge did not align");
            var free = Session(Scene(first, second), first.Id, second.Id).Resolve(new Vector(4, 3), 1);
            Near(free.Delta.X, 4, "Moving objects attracted to their old X positions");
            Near(free.Delta.Y, 3, "Moving objects attracted to their old Y positions");
        });
        test("moving group excludes its descendants and redundant selected child geometry", () =>
        {
            var group = Box(101, 137); group.Kind = LayerKind.Group;
            var child = Box(210, 260); child.ParentId = group.Id;
            var doc = Scene(group, child);
            foreach (var selection in new[] { new[] { group.Id }, new[] { group.Id, child.Id } })
            {
                var result = new MagneticSnapSession(doc, selection, []).Resolve(new Vector(166, 0), 1);
                Near(result.Delta.X, 166, "Group snapped to its own child or included its child twice");
                Assert(result.Guides.Count == 0, "Excluded descendant left a guide");
            }
        });
        test("moving child excludes ancestor bounds from stationary snap targets", () =>
        {
            var group = Box(300, 300, 80, 70); group.Kind = LayerKind.Group;
            var moving = Box(101, 137); moving.ParentId = group.Id;
            var result = Session(Scene(group, moving), moving.Id).Resolve(new Vector(-59, 0), 1);
            Near(result.Delta.X, -59, "Child snapped to its own group bounds");
        });
        test("hidden and transparent ancestors exclude targets while locked targets still attract", () =>
        {
            var moving = Box(101, 137); var group = Box(0, 0, 2000, 1600); group.Kind = LayerKind.Group;
            var target = Box(310, 420, 80, 70); target.ParentId = group.Id; target.Locked = true;
            var doc = Scene(moving, group, target);
            group.Visible = false;
            Near(Session(doc, moving.Id).Resolve(new Vector(165, 0), 1).Delta.X, 165, "Hidden parent exposed child target");
            group.Visible = true; group.Opacity = 0;
            Near(Session(doc, moving.Id).Resolve(new Vector(165, 0), 1).Delta.X, 165, "Transparent parent exposed child target");
            group.Opacity = 1;
            Near(Session(doc, moving.Id).Resolve(new Vector(165, 0), 1).Delta.X, 169, "Locked visible target should be alignable");
        });
        test("snap geometry follows rotated and scaled parent transforms", () =>
        {
            var moving = Box(101, 137);
            var group = Box(270, 300, 400, 300); group.Kind = LayerKind.Group; group.Scale = 1.4; group.Rotation = 23;
            var target = Box(60, 80, 80, 70); target.ParentId = group.Id;
            var doc = Scene(moving, group, target);
            var corners = new[] { new Point(), new Point(80, 0), new Point(80, 70), new Point(0, 70) }
                .Select(point => DocumentFeatures.ToDocumentSpace(doc, target, point)).ToArray();
            double targetLeft = corners.Min(point => point.X), delta = targetLeft - 141;
            var result = Session(doc, moving.Id).Resolve(new Vector(delta - 2, 0), 1);
            Near(result.Delta.X, delta, "Transformed target edge was not used");
            Near(result.Guides.Single(guide => guide.Vertical).Position, targetLeft, "Wrong transformed guide");
        });
        test("moving geometry includes its parent transform", () =>
        {
            var group = Box(200, 170, 200, 160); group.Kind = LayerKind.Group; group.Scale = 2;
            var moving = Box(40, 50); moving.ParentId = group.Id;
            var target = Box(610, 720, 80, 70); var doc = Scene(group, moving, target);
            var right = DocumentFeatures.ToDocumentSpace(doc, moving, new Point(40, 0)).X;
            var result = Session(doc, moving.Id).Resolve(new Vector(610 - right - 3, 0), 1);
            Near(result.Delta.X, 610 - right, "Moving parent transform was ignored");
        });
        test("axis constrained drag never introduces orthogonal snapping motion", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70); var doc = Scene(moving, target);
            var raw = new Vector(165, 250);
            var horizontal = Session(doc, moving.Id).Resolve(raw, 1, constrainX: true);
            Near(horizontal.Delta.X, 169, "Allowed X axis failed to snap");
            Near(horizontal.Delta.Y, 250, "X-only drag acquired Y movement");
            Assert(horizontal.Guides.All(guide => guide.Vertical), "X-only drag retained horizontal guide");
            var vertical = Session(doc, moving.Id).Resolve(raw, 1, constrainY: true);
            Near(vertical.Delta.X, 165, "Y-only drag acquired X movement");
            Near(vertical.Delta.Y, 253, "Allowed Y axis failed to snap");
            Assert(vertical.Guides.All(guide => !guide.Vertical), "Y-only drag retained vertical guide");
        });
        test("user guides participate with deterministic ties and finite extents", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var session = new MagneticSnapSession(Scene(moving, target), [moving.Id], [(true, 310), (false, 420)]);
            var result = session.Resolve(new Vector(165, 250), 1);
            Near(result.Delta.X, 169, "User vertical guide failed");
            Near(result.Delta.Y, 253, "User horizontal guide failed");
            Assert(result.Guides.Count == 2, "Aligned axes should have exactly two guides");
            Near(result.Guides.Single(guide => guide.Vertical).Start, 0, "Explicit guide did not win an exact target tie");
            Near(result.Guides.Single(guide => guide.Vertical).End, 1600, "Guide extent changed unexpectedly");
        });
        test("snap session caches geometry and rejects unusable zoom without stale latches", () =>
        {
            var moving = Box(101, 137); var target = Box(310, 420, 80, 70);
            var session = Session(Scene(moving, target), moving.Id);
            moving.X = 900; target.X = 1100;
            Near(session.Resolve(new Vector(165, 0), 1).Delta.X, 169, "Drag re-read mutated model positions");
            foreach (double zoom in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
            {
                var result = session.Resolve(new Vector(177, 0), zoom);
                Near(result.Delta.X, 177, "Invalid zoom modified a drag");
                Assert(result.Guides.Count == 0, "Invalid zoom retained a guide");
            }
            Near(session.Resolve(new Vector(177, 0), 1).Delta.X, 177, "Invalid zoom did not clear latch");
        });
        test("adjustment-only and empty moving selections do not magnetize", () =>
        {
            var adjustment = Box(101, 137); adjustment.Kind = LayerKind.Adjustment;
            var target = Box(310, 420, 80, 70); var doc = Scene(adjustment, target);
            foreach (var ids in new[] { Array.Empty<Guid>(), new[] { adjustment.Id } })
            {
                var result = new MagneticSnapSession(doc, ids, []).Resolve(new Vector(165, 0), 1);
                Near(result.Delta.X, 165, "Nonvisual moving selection attracted to target");
                Assert(result.Guides.Count == 0, "Nonvisual selection emitted guide");
            }
        });
    }
}
