using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// A synthetic phone photo of a sketchbook page for the self-tests, the offscreen previews and the timing
// note: an A4 sheet seen through a pinhole camera tilted toward it (focal length 0.6 × the image
// diagonal, like a phone's main camera), lying on a wooden table under uneven light with a soft shadow,
// with faint light-blue ruled lines, dark blue and black pen lines, a straight ruler line, small specks,
// a pen lying across the right edge and sensor noise. Deterministic; the true corners and the mapping
// from sheet coordinates (u across, v down, 0..1) to the photo are known.
public static class SyntheticSketch
{
    public sealed record Sketch(Raster Photo, Point[] Corners, double SheetAspect, Func<Point, Point> ToPhoto);

    public static readonly Color Paper = Color.FromRgb(242, 236, 222);
    public static readonly Color BluePen = Color.FromRgb(28, 52, 150), BlackPen = Color.FromRgb(24, 24, 28), Rule = Color.FromRgb(178, 206, 240);
    // The ruler line runs straight across the sheet at this height, from u = .1 to .9.
    public const double RulerV = .62;
    // Sample points (sheet coordinates) on the blue house's left wall, the black ruler line, a ruled line
    // away from the drawings and blank paper inside the shadow (below the last ruled line and the specks).
    public static readonly Point BlueSample = new(.18, .45), BlackSample = new(.50, RulerV), RuleSample = new(.82, .1953), ShadowPaper = new(.30, .97);

    public static Sketch Photo(int width = 1600, int height = 1200, int seed = 7, bool shadow = true, bool pen = true,
        double tilt = 24, double turn = 7, double roll = 4, double fill = .8)
    {
        double diagonal = Math.Sqrt((double)width * width + (double)height * height), focal = .6 * diagonal;
        double u0 = width / 2.0, v0 = height / 2.0;
        // The sheet (210 × 297 mm) rotated about x (tilt), y (turn) and z (roll), in front of the camera.
        double a = tilt * Math.PI / 180, b = turn * Math.PI / 180, c = roll * Math.PI / 180;
        double distance = focal * 297 / (fill * height);
        Point Project(double x, double y)
        {
            // Rx(tilt): y' = y cos − z sin, z' = y sin + z cos (z = 0 on the sheet).
            double y1 = y * Math.Cos(a), z1 = y * Math.Sin(a), x1 = x;
            double x2 = x1 * Math.Cos(b) + z1 * Math.Sin(b), z2 = -x1 * Math.Sin(b) + z1 * Math.Cos(b), y2 = y1;
            double x3 = x2 * Math.Cos(c) - y2 * Math.Sin(c), y3 = x2 * Math.Sin(c) + y2 * Math.Cos(c), z3 = z2 + distance;
            return new Point(u0 + focal * x3 / z3, v0 + focal * y3 / z3);
        }
        var corners = new[] { Project(-105, -148.5), Project(105, -148.5), Project(105, 148.5), Project(-105, 148.5) };
        var quad = new WarpQuad(corners[0], corners[1], corners[2], corners[3]);
        var map = quad.Map(); var inverse = map.Inverse();
        Point ToPhoto(Point uv) => map.Transform(uv);
        const int sheetWidth = 1000, sheetHeight = 1414;
        var ink = SheetInk(sheetWidth, sheetHeight);
        // The pen: a capsule from beyond the right edge onto the sheet, a third of the way down.
        Point penStart = ToPhoto(new Point(1, .36)) + new Vector(width * .11, -height * .05), penEnd = ToPhoto(new Point(.86, .42));
        double penRadius = height * .014;
        var image = new Raster(width, height);
        var noise = new double[width * height * 3];
        var random = new Random(seed);
        for (int i = 0; i < noise.Length; i += 2)
        {
            double r1 = Math.Max(1e-12, random.NextDouble()), r2 = random.NextDouble(), m = Math.Sqrt(-2 * Math.Log(r1)) * 3.5;
            noise[i] = m * Math.Cos(2 * Math.PI * r2); if (i + 1 < noise.Length) noise[i + 1] = m * Math.Sin(2 * Math.PI * r2);
        }
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double sb = 0, sg = 0, sr = 0;
                for (int s = 0; s < 4; s++)
                {
                    double px = x + (s % 2 + .5) / 2, py = y + (s / 2 + .5) / 2;
                    var uv = inverse.Transform(new Point(px, py));
                    double light = 1.06 - .30 * px / width - .10 * py / height;
                    if (shadow)
                    {
                        // A soft shadow (of a hand or the phone) over the sheet's lower left.
                        double dx = (px - width * .36) / (width * .2), dy = (py - height * .86) / (height * .2), r = Math.Sqrt(dx * dx + dy * dy);
                        light *= 1 - .42 * (1 - Smooth(.75, 1.25, r));
                    }
                    double b, g, rr;
                    if (uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1)
                    {
                        var (ib, ig, ir, ia) = Sample(ink, uv.X * sheetWidth, uv.Y * sheetHeight);
                        // Ink multiplies the paper.
                        b = Paper.B * (1 - ia + ia * ib / 255); g = Paper.G * (1 - ia + ia * ig / 255); rr = Paper.R * (1 - ia + ia * ir / 255);
                    }
                    else
                    {
                        double grain = Math.Sin(py / height * 80 + Math.Sin(px / width * 7) * 2.4) * 11 + Math.Sin(px / width * 23 + py / height * 3) * 5;
                        b = 58 + grain * .6; g = 88 + grain; rr = 128 + grain;
                    }
                    if (pen && DistanceToSegment(new Point(px, py), penStart, penEnd) is var d && d <= penRadius)
                    {
                        double highlight = Math.Max(0, 1 - Math.Abs(d / penRadius - .35) * 4);
                        b = 48 + 70 * highlight; g = 38 + 70 * highlight; rr = 35 + 70 * highlight;
                    }
                    sb += b * light; sg += g * light; sr += rr * light;
                }
                int i = (y * width + x) * 4, n = (y * width + x) * 3;
                image.Data[i] = Imaging.Byte(sb / 4 + noise[n]); image.Data[i + 1] = Imaging.Byte(sg / 4 + noise[n + 1]); image.Data[i + 2] = Imaging.Byte(sr / 4 + noise[n + 2]); image.Data[i + 3] = 255;
            }
        });
        return new Sketch(image, corners, 297 / 210.0, ToPhoto);
    }

    // The sheet photographed straight on and filling the frame (a scan): no table to find.
    public static Raster Scan(int width = 1000, int height = 1414, int seed = 3)
    {
        var ink = SheetInk(width, height);
        var image = new Raster(width, height); var random = new Random(seed);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4; double ia = ink.Data[i + 3] / 255.0, light = 1 - .05 * x / width;
                double grain = (random.NextDouble() - .5) * 6;
                image.Data[i] = Imaging.Byte(Paper.B * (1 - ia + ia * ink.Data[i] / 255) * light + grain);
                image.Data[i + 1] = Imaging.Byte(Paper.G * (1 - ia + ia * ink.Data[i + 1] / 255) * light + grain);
                image.Data[i + 2] = Imaging.Byte(Paper.R * (1 - ia + ia * ink.Data[i + 2] / 255) * light + grain);
                image.Data[i + 3] = 255;
            }
        return image;
    }

    // The drawing on the sheet (straight BGRA ink on a clear background), in sheet pixels.
    public static Raster SheetInk(int width, int height) => Imaging.Draw(width, height, dc =>
    {
        double s = width / 1000.0;
        Pen Make(Color color, double thickness) { var pen = new Pen(new SolidColorBrush(color), thickness * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
        var rule = Make(Rule, 1.8);
        for (int k = 0; k < 34; k++) { double y = height * (.0953 + k * .025); dc.DrawLine(rule, new Point(0, y), new Point(width, y)); }
        var blue = Make(BluePen, 4.2); var black = Make(BlackPen, 4.6);
        Point P(double u, double v) => new(u * width, v * height);
        // A house in blue pen: walls, roof, door and a window.
        dc.DrawGeometry(null, blue, new RectangleGeometry(new Rect(P(.18, .30), P(.52, .50))));
        var roof = new StreamGeometry();
        using (var g = roof.Open()) { g.BeginFigure(P(.15, .31), false, false); g.LineTo(P(.35, .17), true, true); g.LineTo(P(.55, .31), true, true); }
        dc.DrawGeometry(null, blue, roof);
        dc.DrawGeometry(null, blue, new RectangleGeometry(new Rect(P(.24, .38), P(.31, .50))));
        dc.DrawGeometry(null, blue, new RectangleGeometry(new Rect(P(.38, .35), P(.47, .42))));
        // A tree in black: trunk and a scribbled crown.
        dc.DrawLine(black, P(.74, .50), P(.74, .38));
        var crown = new StreamGeometry();
        using (var g = crown.Open())
        {
            g.BeginFigure(P(.74, .38), false, false);
            for (int k = 1; k <= 40; k++) { double t = k / 40.0 * Math.PI * 2; g.LineTo(P(.74 + Math.Cos(t) * .09 + Math.Sin(t * 7) * .012, .30 + Math.Sin(t) * .075), true, true); }
        }
        dc.DrawGeometry(null, black, crown);
        // Ground line and the straight ruler line in black, hatching under the house.
        dc.DrawLine(black, P(.06, .50), P(.94, .50));
        dc.DrawLine(black, P(.10, RulerV), P(.90, RulerV));
        for (int k = 0; k < 9; k++) dc.DrawLine(blue, P(.18 + k * .04, .53), P(.21 + k * .04, .58));
        // Specks: tiny dots of dust and pen.
        var dot = new SolidColorBrush(BlackPen); dot.Freeze();
        var random = new Random(19);
        for (int k = 0; k < 40; k++) dc.DrawEllipse(dot, null, P(.08 + random.NextDouble() * .84, .66 + random.NextDouble() * .28), 1.3 * s, 1.3 * s);
    });

    static double Smooth(double from, double to, double value) { double t = Math.Clamp((value - from) / (to - from), 0, 1); return t * t * (3 - 2 * t); }

    static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; double t = Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    static (double B, double G, double R, double A) Sample(Raster image, double x, double y)
    {
        double sx = Math.Clamp(x - .5, 0, image.Width - 1), sy = Math.Clamp(y - .5, 0, image.Height - 1);
        int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(image.Width - 1, x0 + 1), y1 = Math.Min(image.Height - 1, y0 + 1);
        double fx = sx - x0, fy = sy - y0, b = 0, g = 0, r = 0, a = 0;
        void Tap(int xx, int yy, double weight)
        {
            int i = (yy * image.Width + xx) * 4; double alpha = image.Data[i + 3] / 255.0 * weight;
            b += image.Data[i] * alpha; g += image.Data[i + 1] * alpha; r += image.Data[i + 2] * alpha; a += alpha;
        }
        Tap(x0, y0, (1 - fx) * (1 - fy)); Tap(x1, y0, fx * (1 - fy)); Tap(x0, y1, (1 - fx) * fy); Tap(x1, y1, fx * fy);
        return a > 1e-9 ? (b / a, g / a, r / a, a) : (255, 255, 255, 0);
    }
}
