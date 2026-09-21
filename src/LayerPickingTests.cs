using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class LayerPickingTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static Layer Solid(string name, int width = 4, int height = 4) => new()
        {
            Name = name,
            Pixels = Raster.Solid(width, height, Colors.Red)
        };

        static Document DocumentWith(Layer bottom, int width = 8, int height = 8)
        {
            var document = new Document { Width = width, Height = height, Name = "피킹 테스트" };
            document.Add(bottom);
            return document;
        }

        test("layer picking chooses the topmost visible pixel", () =>
        {
            var bottom = Solid("아래");
            var top = Solid("위");
            var document = DocumentWith(bottom);
            document.Add(top);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(1.5, 1.5)), top), "겹친 레이어의 최상단 레이어를 선택하지 않았습니다.");
        });

        test("layer picking selects through a transparent hole", () =>
        {
            var bottom = Solid("아래", 3, 3);
            var topPixels = Raster.Solid(3, 3, Colors.Blue);
            topPixels.Data[(1 * 3 + 1) * 4 + 3] = 0;
            var top = new Layer { Name = "가운데가 투명한 위", Pixels = topPixels };
            var document = DocumentWith(bottom, 3, 3);
            document.Add(top);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(1.5, 1.5)), bottom), "투명한 구멍 아래의 레이어를 선택하지 않았습니다.");
        });

        test("layer picking skips hidden layers", () =>
        {
            var bottom = Solid("아래");
            var hidden = Solid("숨김");
            hidden.Visible = false;
            var document = DocumentWith(bottom);
            document.Add(hidden);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(.5, .5)), bottom), "숨긴 레이어가 선택되었습니다.");
        });

        test("layer picking skips zero-opacity layers", () =>
        {
            var bottom = Solid("아래");
            var transparent = Solid("불투명도 0");
            transparent.Opacity = 0;
            var document = DocumentWith(bottom);
            document.Add(transparent);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(.5, .5)), bottom), "불투명도가 0인 레이어가 선택되었습니다.");
        });

        test("layer picking respects a zero mask", () =>
        {
            var bottom = Solid("아래");
            var masked = Solid("마스크로 숨김");
            masked.Mask = new byte[masked.Pixels.Width * masked.Pixels.Height];
            var document = DocumentWith(bottom);
            document.Add(masked);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(.5, .5)), bottom), "검정 마스크로 숨긴 레이어가 선택되었습니다.");
        });

        test("layer picking returns a locked top layer", () =>
        {
            var bottom = Solid("아래");
            var locked = Solid("잠김");
            locked.Locked = true;
            var document = DocumentWith(bottom);
            document.Add(locked);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(.5, .5)), locked), "잠긴 최상단 레이어 대신 아래 레이어를 선택했습니다.");
        });

        test("layer picking follows rotation scale and flips", () =>
        {
            var pixels = Raster.Solid(3, 2, Colors.Transparent);
            pixels.Data[(1 * 3 + 0) * 4 + 3] = 255;
            var transformed = new Layer
            {
                Name = "변형",
                Pixels = pixels,
                X = 23,
                Y = 17,
                Scale = 3,
                Rotation = 31,
                FlipX = true,
                FlipY = true
            };
            var document = DocumentWith(transformed, 64, 64);
            var opaquePoint = transformed.Matrix.Transform(new Point(.5, 1.5));
            var transparentPoint = transformed.Matrix.Transform(new Point(2.5, .5));
            Assert(ReferenceEquals(LayerPicking.Pick(document, opaquePoint), transformed), "변형된 불투명 픽셀에서 레이어를 찾지 못했습니다.");
            Assert(LayerPicking.Pick(document, transparentPoint) == null, "변형된 투명 픽셀에서 레이어를 선택했습니다.");
        });

        test("layer picking rejects points outside the canvas", () =>
        {
            var layer = Solid("캔버스", 4, 4);
            var document = DocumentWith(layer, 4, 4);
            Assert(ReferenceEquals(LayerPicking.Pick(document, new Point(0, 0)), layer), "캔버스 안쪽 모서리를 선택하지 못했습니다.");
            Assert(LayerPicking.Pick(document, new Point(-.01, 1)) == null, "캔버스 왼쪽 밖에서 레이어를 선택했습니다.");
            Assert(LayerPicking.Pick(document, new Point(4, 1)) == null, "캔버스 오른쪽 경계 밖에서 레이어를 선택했습니다.");
            Assert(LayerPicking.Pick(document, new Point(1, 4)) == null, "캔버스 아래쪽 경계 밖에서 레이어를 선택했습니다.");
        });
    }
}
