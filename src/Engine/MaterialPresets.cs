using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Built-in, seamlessly tiling 256 px material swatches for recommended hatch fills.
// They are drawn procedurally (no bundled images) in light tones meant for Multiply.
public static class MaterialPresets
{
    public const int Size = 256;

    public static MaterialAsset Create(MaterialKind kind) =>
        new(StableId(kind), "추천 · " + DrawingCleanup.MaterialName(kind), Render(kind), "morupixel:preset/" + kind.ToString().ToLowerInvariant(), true);

    // A fixed id per preset lets repeated imports share one library entry.
    static Guid StableId(MaterialKind kind) => new(0x4d6f7275, 0x7069, 0x7865, 0x6c, 0x70, 0x72, 0x65, 0x73, 0x65, 0x74, (byte)kind);

    public static Raster Render(MaterialKind kind) => Imaging.Draw(Size, Size, dc =>
    {
        var random = new Random(1000 + (int)kind);
        Brush Solid(byte r, byte g, byte b, byte a = 255) { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
        Pen Line(byte r, byte g, byte b, double width, byte a = 255) { var pen = new Pen(Solid(r, g, b, a), width); pen.Freeze(); return pen; }
        void Wrap(Action<double, double> draw) { foreach (var dx in new[] { -Size, 0, Size }) foreach (var dy in new[] { -Size, 0, Size }) draw(dx, dy); }
        var area = new Rect(0, 0, Size, Size);
        switch (kind)
        {
            case MaterialKind.Concrete:
                dc.DrawRectangle(Solid(0xE6, 0xE4, 0xE0), null, area);
                for (int i = 0; i < 700; i++)
                {
                    double x = random.NextDouble() * Size, y = random.NextDouble() * Size, r = .5 + random.NextDouble() * 1.6;
                    byte tone = (byte)random.Next(0x9A, 0xC8); var dot = Solid(tone, tone, tone);
                    Wrap((dx, dy) => dc.DrawEllipse(dot, null, new Point(x + dx, y + dy), r, r));
                }
                for (int i = 0; i < 26; i++)
                {
                    double x = random.NextDouble() * Size, y = random.NextDouble() * Size, s = 2 + random.NextDouble() * 3;
                    var stone = new StreamGeometry();
                    using (var g = stone.Open()) { g.BeginFigure(new Point(x, y - s), true, true); g.LineTo(new Point(x + s, y + s * .6), true, false); g.LineTo(new Point(x - s * .8, y + s * .4), true, false); }
                    stone.Freeze(); var pen = Line(0x8C, 0x8C, 0x8C, .8);
                    Wrap((dx, dy) => { dc.PushTransform(new TranslateTransform(dx, dy)); dc.DrawGeometry(null, pen, stone); dc.Pop(); });
                }
                break;
            case MaterialKind.Brick:
                dc.DrawRectangle(Solid(0xEF, 0xE3, 0xDA), null, area);
                var mortar = Line(0xB9, 0xA4, 0x96, 2);
                for (int row = 0; row <= 8; row++)
                {
                    double y = row * 32; dc.DrawLine(mortar, new Point(0, y), new Point(Size, y));
                    for (int column = 0; column <= 4; column++) { double x = column * 64 + (row % 2) * 32; dc.DrawLine(mortar, new Point(x, y), new Point(x, y + 32)); }
                }
                break;
            case MaterialKind.Wood:
                dc.DrawRectangle(Solid(0xF1, 0xE6, 0xD4), null, area);
                var seam = Line(0xB8, 0x9C, 0x7A, 1.6); var grain = Line(0xD3, 0xBE, 0xA0, .9);
                for (int plank = 0; plank < 8; plank++)
                {
                    double y = plank * 32; dc.DrawLine(seam, new Point(0, y), new Point(Size, y));
                    double joint = (plank * 97) % Size; dc.DrawLine(seam, new Point(joint, y), new Point(joint, y + 32));
                    for (int line = 0; line < 3; line++)
                    {
                        double gy = y + 7 + line * 8 + random.NextDouble() * 3, bend = random.NextDouble() * 4 - 2;
                        var curve = new StreamGeometry();
                        using (var g = curve.Open()) { g.BeginFigure(new Point(0, gy), false, false); g.BezierTo(new Point(Size / 3d, gy + bend), new Point(Size * 2 / 3d, gy - bend), new Point(Size, gy), true, false); }
                        curve.Freeze(); dc.DrawGeometry(null, grain, curve);
                    }
                }
                break;
            case MaterialKind.Tile:
                dc.DrawRectangle(Solid(0xF3, 0xF3, 0xF1), null, area);
                var joints = Line(0xC2, 0xC4, 0xC6, 2);
                for (int i = 0; i <= 4; i++) { dc.DrawLine(joints, new Point(i * 64, 0), new Point(i * 64, Size)); dc.DrawLine(joints, new Point(0, i * 64), new Point(Size, i * 64)); }
                break;
            case MaterialKind.Stone:
                dc.DrawRectangle(Solid(0xEC, 0xEA, 0xE6), null, area);
                var vein = Line(0xC4, 0xC0, 0xB8, 1.2);
                for (int i = 0; i < 9; i++)
                {
                    double y = random.NextDouble() * Size; var path = new StreamGeometry();
                    using (var g = path.Open()) { g.BeginFigure(new Point(0, y), false, false); for (int s = 1; s <= 8; s++) g.LineTo(new Point(s * 32, y + (random.NextDouble() - .5) * 26), true, false); g.LineTo(new Point(Size, y), true, false); }
                    path.Freeze(); dc.DrawGeometry(null, vein, path);
                }
                var slab = Line(0xB6, 0xB2, 0xAA, 1.6);
                dc.DrawLine(slab, new Point(0, 0), new Point(Size, 0)); dc.DrawLine(slab, new Point(0, 0), new Point(0, Size)); dc.DrawLine(slab, new Point(0, 128), new Point(Size, 128));
                break;
            case MaterialKind.Insulation:
                dc.DrawRectangle(Solid(0xF5, 0xF0, 0xE6), null, area);
                var loop = Line(0xC9, 0xB8, 0x9A, 1.3);
                for (int row = 0; row < 4; row++)
                {
                    var wave = new StreamGeometry(); double top = row * 64 + 8, bottom = top + 48;
                    using (var g = wave.Open()) { g.BeginFigure(new Point(0, bottom), false, false); for (int s = 0; s < 8; s++) { double x = s * 32; g.BezierTo(new Point(x + 4, top), new Point(x + 28, top), new Point(x + 32, bottom), true, false); } }
                    wave.Freeze(); dc.DrawGeometry(null, loop, wave);
                }
                break;
            case MaterialKind.Gravel:
                dc.DrawRectangle(Solid(0xEE, 0xEA, 0xE2), null, area);
                var pebble = Line(0xA8, 0xA2, 0x98, 1);
                for (int i = 0; i < 90; i++)
                {
                    double x = random.NextDouble() * Size, y = random.NextDouble() * Size, rx = 3 + random.NextDouble() * 5, ry = 2 + random.NextDouble() * 4;
                    Wrap((dx, dy) => dc.DrawEllipse(null, pebble, new Point(x + dx, y + dy), rx, ry));
                }
                break;
            case MaterialKind.Diagonal:
                dc.DrawRectangle(Solid(0xF6, 0xF6, 0xF6), null, area);
                var hatch = Line(0xB0, 0xB4, 0xBA, 1.4);
                for (int i = -8; i <= 8; i++) dc.DrawLine(hatch, new Point(i * 32, Size), new Point(i * 32 + Size, 0));
                break;
            default:
                dc.DrawRectangle(Solid(0xD8, 0xDA, 0xDE), null, area);
                break;
        }
    });
}
