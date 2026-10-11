using System.Windows;
using System.Windows.Media;
using static Compositor.Windows.EntourageShapes;

namespace Compositor.Windows;

// Vehicles and street furniture. Side views face +x; plan symbols point up (-y), which is how a
// car or bicycle is usually drawn on a site plan before it is rotated into its parking bay.
public static partial class EntourageLibrary
{
    readonly record struct Bike(Point Saddle, Point Bar, Point NearPedal, Point FarPedal);

    // A city bicycle with 68 cm wheels, wheelbase about 104 cm.
    static Bike BikeSide(EntourageSketch s, double ox, double oy)
    {
        Point B(double x, double y) => new(ox + x, oy + y);
        var rear = B(-52, -34); var front = B(54, -34); var crank = B(-6, -28);
        foreach (var axle in new[] { rear, front })
        {
            s.Body(Ring(axle.X, axle.Y, 34, 30.6), Circle(axle.X, axle.Y, 3.4));
            s.Line(Circle(axle.X, axle.Y, 28.4), .3);
            for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4 + .2; s.Line(Segment(axle + new Vector(Math.Cos(a) * 3.4, Math.Sin(a) * 3.4), axle + new Vector(Math.Cos(a) * 28.4, Math.Sin(a) * 28.4)), .22); }
        }
        var seatTop = B(-22, -80); var head = B(36, -82); var headLow = B(40, -68);
        s.Body(Union([
            Capsule(crank, 2.2, seatTop, 1.9), Capsule(seatTop, 1.9, head, 1.9), Capsule(headLow, 2.1, crank, 2.1), Capsule(head, 2.2, headLow, 2.2),
            Capsule(crank, 1.6, rear, 1.4), Capsule(seatTop, 1.5, rear, 1.3), Capsule(headLow, 1.8, front, 1.4),
            Capsule(seatTop, 1.4, B(-24, -88), 1.4), Capsule(head, 1.6, B(34, -94), 1.5), Capsule(B(30, -95), 1.5, B(46, -95), 1.5),
            Ellipse(ox - 25, oy - 90, 10, 2.6, -4), Circle(crank.X, crank.Y, 5.5)
        ]));
        double a0 = 25 * Math.PI / 180;
        var nearPedal = crank + new Vector(Math.Cos(a0) * 17, Math.Sin(a0) * 17); var farPedal = crank - new Vector(Math.Cos(a0) * 17, Math.Sin(a0) * 17);
        s.Body(Capsule(crank, 1.6, farPedal, 1.4), Capsule(farPedal + new Vector(-4.5, 0), 1.3, farPedal + new Vector(4.5, 0), 1.3));
        s.Over(Union([Capsule(crank, 1.8, nearPedal, 1.6), Capsule(nearPedal + new Vector(-5, 0), 1.5, nearPedal + new Vector(5, 0), 1.5)]), .4);
        // Chain line and mudguard.
        s.Line(Spline(false, crank + new Vector(0, -5.5), new Point((crank.X + rear.X) / 2, rear.Y - 4), rear + new Vector(0, -3.4)), .3);
        s.Line(Arc(front, 37, 200, 300), .45); s.Line(Arc(rear, 37, 210, 330), .45);
        return new Bike(B(-25, -92), B(42, -95), nearPedal, farPedal);
    }

    static void BikePlan(EntourageSketch s)
    {
        s.Body(Capsule(P(0, -86), 2.4, P(0, -20), 2.4), Capsule(P(0, 20), 2.4, P(0, 86), 2.4));
        s.Body(Capsule(P(0, -50), 1.8, P(0, 30), 1.8), Capsule(P(-30, -54), 2, P(30, -54), 2), Ellipse(0, 26, 6, 12), Capsule(P(-14, 4), 2, P(14, 4), 2));
        s.Line(Capsule(P(-34, -54), 2.6, P(-26, -54), 2.6), .5); s.Line(Capsule(P(26, -54), 2.6, P(34, -54), 2.6), .5);
    }

    // Saloon or hatchback in side view; wheels sit in arches cut from the body.
    static void CarSide(EntourageSketch s, bool hatch, int variant)
    {
        double length = hatch ? 400 : 470, half = length / 2, rearX = -half, frontX = half;
        double wheelbase = hatch ? 250 : 280, wheelR = hatch ? 30 : 32, roof = hatch ? -148 : -142;
        double rearWheel = -wheelbase / 2 - (hatch ? 6 : 12), frontWheel = wheelbase / 2 - (hatch ? 6 : 12);
        var outline = hatch
            ? new[] { P(rearX + 4, -32), P(rearX, -58), P(rearX + 3, -96), P(rearX + 14, -130), P(rearX + 40, roof), P(10, roof - 2), P(48, -136), P(92, -100), P(frontX - 40, -90),
                P(frontX - 6, -80), P(frontX, -56), P(frontX - 4, -32) }
            : new[] { P(rearX + 4, -34), P(rearX, -60), P(rearX + 4, -84), P(rearX + 34, -92), P(-138, -97), P(-100, -128), P(-40, roof), P(36, roof + 1), P(98, -102),
                P(frontX - 40, -91), P(frontX - 6, -80), P(frontX, -58), P(frontX - 4, -34) };
        var body = Spline(true, outline);
        foreach (double x in new[] { rearWheel, frontWheel }) body = Subtract(body, Circle(x, -wheelR, wheelR + 5));
        // The lower edge is straight between the arches (the spline only closes it).
        body = Intersect(body, Poly(true, P(rearX - 10, 0 - 18), P(frontX + 10, -18), P(frontX + 10, -200), P(rearX - 10, -200)));
        s.Body(body);
        foreach (double x in new[] { rearWheel, frontWheel })
        {
            s.Body(Circle(x, -wheelR, wheelR));
            s.Line(Circle(x, -wheelR, wheelR * .62), .5); s.Line(Circle(x, -wheelR, wheelR * .2), .45);
        }
        // Glass, pillars, doors, lights.
        Point[] glass = hatch
            ? [P(rearX + 22, -104), P(rearX + 40, -136), P(8, -138), P(42, -132), P(78, -104)]
            : [P(-128, -101), P(-96, -124), P(-40, -134), P(32, -133), P(84, -104)];
        s.Line(Poly(true, glass), .5);
        double pillar = hatch ? -30 : -18;
        s.Line(Segment(P(pillar, -103), P(pillar + (hatch ? 4 : 2), -135)), .5);
        s.Line(Segment(P(pillar + 4, -100), P(pillar - 2, -40)), .4);
        if (!hatch || variant == 1) s.Line(Segment(P(frontWheel - wheelR - 18, -100), P(frontWheel - wheelR - 22, -42)), .4);
        s.Line(Spline(false, P(rearX + 6, -74), P(0, -78), P(frontX - 8, -70)), .35);
        s.Line(Spline(false, P(frontX - 26, -84), P(frontX - 8, -80), P(frontX - 10, -72)), .4);
        s.Line(Segment(P(rearX + 4, -86), P(rearX + 4, -70)), .45);
        s.Line(Segment(P(pillar + 14, -92), P(pillar + 26, -92)), .4); s.Line(Segment(P(rearWheel + 52, -92), P(rearWheel + 64, -92)), .4);
    }

    static void CarPlan(EntourageSketch s)
    {
        s.Body(Spline(true, P(-86, -214), P(-91, -150), P(-93, 120), P(-87, 214), P(-56, 234), P(56, 234), P(87, 214), P(93, 120), P(91, -150), P(86, -214), P(56, -234), P(-56, -234)));
        s.Body(Ellipse(-98, -64, 9, 6, 15), Ellipse(98, -64, 9, 6, -15));
        s.Line(Poly(true, P(-74, -96), P(74, -96), P(64, -40), P(-64, -40)), .5);
        s.Line(Spline(true, P(-66, -40), P(66, -40), P(68, 82), P(-68, 82)), .45);
        s.Line(Poly(true, P(-66, 86), P(66, 86), P(56, 140), P(-56, 140)), .5);
        s.Line(Spline(false, P(-60, -214), P(0, -226), P(60, -214)), .35);
        s.Line(Segment(P(-84, -30), P(-84, 70)), .3); s.Line(Segment(P(84, -30), P(84, 70)), .3);
    }

    static void Bench(EntourageSketch s, bool plan)
    {
        if (plan)
        {
            s.Body(Spline(true, P(-90, -24), P(90, -24), P(91, 23), P(-91, 23)));
            for (int i = 1; i < 4; i++) s.Line(Segment(P(-88, -24 + i * 9.5), P(88, -24 + i * 9.5)), .4);
            s.Line(Poly(true, P(-90, 14), P(90, 14), P(90, 24), P(-90, 24)), .5);
            return;
        }
        s.Body(Poly(true, P(-92, -47), P(92, -47), P(92, -42), P(-92, -42)));
        s.Body(Poly(true, P(-92, -82), P(92, -82), P(92, -74), P(-92, -74)), Poly(true, P(-92, -68), P(92, -68), P(92, -61), P(-92, -61)));
        foreach (double x in new[] { -72.0, 72.0 })
            s.Body(Capsule(P(x, -2), 3.4, P(x, -45), 3), Capsule(P(x + 2, -45), 2.6, P(x + 4, -84), 2.4), Capsule(P(x - 6, -1.5), 1.5, P(x + 6, -1.5), 1.5));
        s.Line(Segment(P(-92, -44.5), P(92, -44.5)), .3);
    }

    static void StreetLamp(EntourageSketch s, int variant)
    {
        s.Body(Union([Limb((P(0, -1), 9), (P(0, -40), 6.5), (P(0, -440), 4.4)), Poly(true, P(-14, 0), P(14, 0), P(11, -8), P(-11, -8))]));
        if (variant == 1)
        {
            // Post-top lantern.
            s.Body(Spline(true, P(-16, -440), P(-22, -470), P(0, -492), P(22, -470), P(16, -440)), Capsule(P(-12, -440), 2, P(12, -440), 2));
            s.Line(Segment(P(-14, -458), P(14, -458)), .4);
            return;
        }
        var pen = new Pen(Brushes.Black, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        s.Body(Spline(false, P(0, -430), P(10, -458), P(40, -468), P(70, -462)).GetWidenedPathGeometry(pen, Tolerance, ToleranceType.Absolute));
        s.Body(Spline(true, P(56, -466), P(98, -464), P(104, -452), P(52, -452)));
        s.Line(Segment(P(60, -452), P(100, -452)), .4);
    }

    static void ParasolTable(EntourageSketch s)
    {
        s.Body(Poly(true, P(-48, -76), P(48, -76), P(48, -72), P(-48, -72)), Capsule(P(0, -72), 3, P(0, -6), 3), Poly(true, P(-24, 0), P(24, 0), P(20, -5), P(-20, -5)));
        s.Body(Capsule(P(0, -76), 2.2, P(0, -236), 2));
        s.Body(Spline(true, P(-140, -198), P(-70, -228), P(0, -252), P(70, -228), P(140, -198), P(105, -194), P(70, -199), P(35, -194), P(0, -199), P(-35, -194), P(-70, -199), P(-105, -194)));
        foreach (int side in new[] { -1, 1 })
        {
            double x = side * 82;
            s.Body(Poly(true, P(x - 22, -46), P(x + 22, -46), P(x + 22, -42), P(x - 22, -42)), Capsule(P(x - 18, -44), 1.8, P(x - 20, -1), 1.6), Capsule(P(x + 18, -44), 1.8, P(x + 20, -1), 1.6),
                Capsule(P(x + side * 20, -44), 1.8, P(x + side * 24, -92), 1.6));
        }
        for (int i = -2; i <= 2; i++) s.Line(Segment(P(0, -250), P(i * 66, -200)), .35);
    }

    static void TablePlan(EntourageSketch s)
    {
        s.Body(Circle(0, 0, 45));
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2 + Math.PI / 4;
            var c = new Point(Math.Cos(a) * 72, Math.Sin(a) * 72);
            var chair = Transformed(Spline(true, P(-21, -21), P(21, -21), P(21, 21), P(-21, 21)), new RotateTransform(a * 180 / Math.PI + 90, 0, 0));
            s.Body(Transformed(chair, new TranslateTransform(c.X, c.Y)));
            s.Line(Transformed(Segment(P(-18, 14), P(18, 14)), new TransformGroup { Children = { new RotateTransform(a * 180 / Math.PI + 90), new TranslateTransform(c.X, c.Y) } }), .4);
        }
        s.Line(Circle(0, 0, 38), .35);
    }

    static void AddVehicles(List<EntourageItem> list)
    {
        list.Add(Item("car.sedan", "승용차", EntourageCategory.Vehicles, EntourageView.Elevation, 1.42, EntourageFill.White, 2,
            K("자동차", "승용차", "세단", "car sedan vehicle"), (s, _, v) => CarSide(s, false, v)));
        list.Add(Item("car.hatch", "소형차", EntourageCategory.Vehicles, EntourageView.Elevation, 1.48, EntourageFill.White, 2,
            K("자동차", "소형차", "해치백", "car hatchback small vehicle"), (s, _, v) => CarSide(s, true, v)));
        list.Add(Item("car.plan", "승용차 · 평면", EntourageCategory.Vehicles, EntourageView.Plan, 4.7, EntourageFill.White, 1,
            K("자동차", "주차", "평면", "car parking plan top vehicle"), (s, _, _) => CarPlan(s), shadowHeight: 1.45 / 4.7));
        list.Add(Item("bike.side", "자전거", EntourageCategory.Vehicles, EntourageView.Elevation, 1.0, EntourageFill.White, 1,
            K("자전거", "bicycle bike cycle"), (s, _, _) => BikeSide(s, 0, 0)));
        list.Add(Item("bike.plan", "자전거 · 평면", EntourageCategory.Vehicles, EntourageView.Plan, 1.8, EntourageFill.White, 1,
            K("자전거", "평면", "bicycle bike plan top parking"), (s, _, _) => BikePlan(s), shadowHeight: 1.0 / 1.8));
    }

    static void AddProps(List<EntourageItem> list)
    {
        list.Add(Item("bench.side", "벤치", EntourageCategory.Props, EntourageView.Elevation, .84, EntourageFill.White, 1,
            K("벤치", "의자", "bench seat furniture"), (s, _, _) => Bench(s, false)));
        list.Add(Item("bench.plan", "벤치 · 평면", EntourageCategory.Props, EntourageView.Plan, 1.84, EntourageFill.White, 1,
            K("벤치", "의자", "평면", "bench seat plan top"), (s, _, _) => Bench(s, true), shadowHeight: .45));
        list.Add(Item("lamp.street", "가로등", EntourageCategory.Props, EntourageView.Elevation, 4.9, EntourageFill.White, 2,
            K("가로등", "조명", "street lamp light pole"), (s, _, v) => StreetLamp(s, v)));
        list.Add(Item("table.parasol", "파라솔 테이블", EntourageCategory.Props, EntourageView.Elevation, 2.5, EntourageFill.White, 1,
            K("파라솔", "테이블", "의자", "카페", "테라스", "parasol umbrella table cafe terrace"), (s, _, _) => ParasolTable(s)));
        list.Add(Item("table.plan", "테이블과 의자 · 평면", EntourageCategory.Props, EntourageView.Plan, 1.95, EntourageFill.White, 1,
            K("테이블", "의자", "카페", "평면", "table chairs cafe plan top"), (s, _, _) => TablePlan(s), shadowHeight: .4));
    }
}
