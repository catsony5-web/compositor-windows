using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class MagneticPickingTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static Layer Line(double x = 32, double y = 0, int height = 64) => new()
        {
            Name = "도면 선", Pixels = Raster.Solid(1, height, Colors.Red), X = x, Y = y
        };
        static Document Empty() => new() { Width = 64, Height = 64 };
        static Layer Background(Document document)
        {
            var background = new Layer { Name = "배경", Pixels = Raster.Solid(64, 64, Colors.White), Locked = true };
            document.Add(background);
            return background;
        }

        test("magnetic picking acquires a thin line above a locked white background", () =>
        {
            var document = Empty();
            var background = Background(document);
            var line = Line(); document.Add(line);
            var point = new Point(29.5, 30.5);
            Assert(LayerPicking.Pick(document, point) == background, "정확한 선택 동작이 바뀌었습니다.");
            Assert(LayerPicking.PickNear(document, point, 1) == line, "흰 배경 위의 얇은 선을 찾지 못했습니다.");
            Assert(LayerPicking.Pick(document, point) == background, "자석 선택이 문서를 변경했습니다.");
        });

        test("magnetic picking prefers the nearest ring before more distant upper layers", () =>
        {
            var document = Empty();
            var near = Line(32); var far = Line(35);
            document.Add(near); document.Add(far);
            Assert(LayerPicking.PickNear(document, new(30.5, 30.5), 1) == near, "더 먼 위쪽 레이어에 선택이 끌렸습니다.");
        });

        test("magnetic picking radius stays in screen units across zoom levels", () =>
        {
            var document = Empty(); var line = Line(); document.Add(line);
            foreach (double zoom in new[] { .25, .5, 1, 2 })
            {
                Assert(LayerPicking.PickNear(document, new(32.5 - 3 / zoom, 32.5), zoom) == line, $"확대율 {zoom}에서 가까운 선을 놓쳤습니다.");
                Assert(LayerPicking.PickNear(document, new(32.5 - 7 / zoom, 32.5), zoom) == null, $"확대율 {zoom}에서 먼 선이 선택되었습니다.");
            }
        });

        test("magnetic picking never reaches beneath the exact opaque center hit", () =>
        {
            var document = Empty();
            var behind = Line(34); document.Add(behind);
            var front = new Layer { Pixels = Raster.Solid(4, 8, Colors.Blue), X = 28, Y = 28 };
            document.Add(front);
            var point = new Point(30.5, 32.5);
            Assert(LayerPicking.PickNear(document, point, 1) == front, "앞쪽 객체를 통과해 뒤쪽 선을 선택했습니다.");
        });

        test("magnetic occlusion uses group paint order rather than flat layer indexes", () =>
        {
            var document = Empty();
            var group = new Layer { Kind = LayerKind.Group, Pixels = new Raster(64, 64) };
            var front = new Layer { Pixels = Raster.Solid(4, 8, Colors.Blue), X = 28, Y = 28 };
            var behindChild = Line(34); behindChild.ParentId = group.Id;
            // The child's flat index is above front, but its parent paints below it.
            document.Add(group); document.Add(front); document.Add(behindChild);
            Assert(LayerPicking.PickNear(document, new(30.5, 32.5), 1) == front, "그룹 자식의 배열 순서를 화면 순서로 사용했습니다.");

            var lower = new Layer { Pixels = Raster.Solid(64, 64, Colors.White), Locked = true };
            document.Layers = [behindChild, lower, group];
            Assert(LayerPicking.PickNear(document, new(30.5, 32.5), 1) == behindChild, "위쪽 그룹 안의 가까운 선을 선택하지 못했습니다.");
        });

        test("magnetic picking respects transparent holes and does not use bounding boxes", () =>
        {
            var document = Empty(); var background = Background(document);
            var pixels = new Raster(64, 64);
            for (int y = 0; y < 64; y++) pixels.Data[(y * 64 + 32) * 4 + 3] = 255;
            var sparse = new Layer { Pixels = pixels }; document.Add(sparse);
            Assert(LayerPicking.PickNear(document, new(20.5, 32.5), 1) == background, "투명한 영역의 경계 상자가 선택되었습니다.");
            Assert(LayerPicking.PickNear(document, new(29.5, 32.5), 1) == sparse, "래스터 안의 실제 선을 찾지 못했습니다.");
        });

        test("magnetic picking respects masks visibility and effective opacity", () =>
        {
            var document = Empty(); var background = Background(document);
            var line = Line(); document.Add(line);
            var point = new Point(29.5, 32.5);
            line.Mask = new byte[64];
            Assert(LayerPicking.PickNear(document, point, 1) == background, "마스크로 감춘 선을 선택했습니다.");
            line.Mask = null; line.Visible = false;
            Assert(LayerPicking.PickNear(document, point, 1) == background, "숨긴 선을 선택했습니다.");
            line.Visible = true; line.Opacity = 0;
            Assert(LayerPicking.PickNear(document, point, 1) == background, "투명한 선을 선택했습니다.");
        });

        test("magnetic picking does not select clipped pixels outside their base", () =>
        {
            var document = Empty(); var background = Background(document);
            var clipBase = new Layer { Pixels = Raster.Solid(2, 10, Colors.Black), X = 10, Y = 10 };
            document.Add(clipBase);
            var clipped = Line(); clipped.Clipped = true; document.Add(clipped);
            Assert(LayerPicking.PickNear(document, new(29.5, 32.5), 1) == background, "클리핑 영역 밖의 선을 선택했습니다.");
            clipBase.X = 32; clipBase.Y = 28;
            Assert(LayerPicking.PickNear(document, new(29.5, 32.5), 1) == clipped, "클리핑 영역 안의 선을 선택하지 못했습니다.");
        });

        test("magnetic picking follows transformed parents and respects locked groups", () =>
        {
            var document = new Document { Width = 128, Height = 128 };
            var group = new Layer { Kind = LayerKind.Group, Pixels = new Raster(64, 64), X = 20, Y = 20, Rotation = 90, ScaleX = 1.5 };
            document.Add(group);
            var child = Line(); child.ParentId = group.Id; document.Add(child);
            var exact = DocumentFeatures.ToDocumentSpace(document, child, new(.5, 32.5));
            var near = exact + new Vector(0, -3);
            Assert(LayerPicking.Pick(document, near) == null, "테스트 지점이 선 위에 있습니다.");
            Assert(LayerPicking.PickNear(document, near, 1) == child, "부모 변형을 반영하지 못했습니다.");
            group.Locked = true;
            Assert(LayerPicking.PickNear(document, near, 1) == group, "잠긴 그룹 대신 자식이 선택되었습니다.");
            group.Locked = false; group.Visible = false;
            Assert(LayerPicking.PickNear(document, near, 1) == null, "숨긴 부모의 자식이 선택되었습니다.");
            group.Visible = true; group.Mask = new byte[64 * 64];
            Assert(LayerPicking.PickNear(document, near, 1) == null, "부모 마스크 밖의 자식이 선택되었습니다.");
        });

        test("magnetic picking leaves canvas boundaries and invalid zoom unselected", () =>
        {
            var document = Empty(); Background(document);
            foreach (var point in new[] { new Point(-.01, 30), new Point(64, 30), new Point(30, 64), new Point(double.NaN, 30) })
                Assert(LayerPicking.PickNear(document, point, 1) == null, "캔버스 밖의 포인터가 객체를 선택했습니다.");
            foreach (double zoom in new[] { 0, -1, double.NaN, double.PositiveInfinity })
                Assert(LayerPicking.PickNear(document, new(30, 30), zoom) == null, "잘못된 확대율을 처리하지 못했습니다.");
            Assert(LayerPicking.PickNear(document, new(30, 30), 1, double.NaN) == null, "잘못된 반경을 처리하지 못했습니다.");
        });

        test("zero magnetic radius preserves exact picking", () =>
        {
            var document = Empty(); Background(document); document.Add(Line());
            foreach (var point in new[] { new Point(29.5, 30.5), new Point(32.5, 30.5), new Point(0, 0), new Point(63.9, 63.9) })
                Assert(LayerPicking.PickNear(document, point, 1, 0) == LayerPicking.Pick(document, point), "반경 0에서 정확한 선택과 결과가 다릅니다.");
        });
    }
}
