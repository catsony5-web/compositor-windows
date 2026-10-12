using System.Windows;
using System.Windows.Media;
using static Compositor.Windows.EntourageShapes;

namespace Compositor.Windows;

// Richer plan symbols: people seen from above (shoulders, arms and head, walking with their feet
// showing, groups, with a pram or a bicycle), more tree crowns (branch structure, leaf texture, palm
// fronds), hedges and ground covers, and more cars and a detailed bicycle. They are new items next
// to the simple plan symbols, which keep their ids and drawings. Symbols face up (-y) and are
// centred; bold main shapes keep them readable at 1:500, the small details show from 1:100 up.
public static partial class EntourageLibrary
{
    enum PlanHair { Short, Long, Cap }

    // A person from above, facing up: shoulders, arms, head with hair and a nose; feet when walking.
    // Positions in centimetres around (x, y), turned by `turn` degrees (0 = facing up).
    static void PlanPerson(EntourageSketch s, double x, double y, double turn, PlanHair hair, double stride = 0, double swing = 0, double scale = 1)
    {
        var t = new TransformGroup { Children = { new ScaleTransform(scale, scale), new RotateTransform(turn), new TranslateTransform(x, y) } }; t.Freeze();
        Geometry T(Geometry g) => Transformed(g, t);
        if (stride != 0)
        {
            // Shoes: one stepping out in front, the other trailing behind.
            s.Body(T(Ellipse(-8.5, -20 * stride, 4.2, 10, -4)), T(Ellipse(8.5, 18 * stride, 4.2, 10, 4)));
        }
        var shoulders = Spline(true, P(-23, -2), P(-21, -9), P(-12, -12.5), P(0, -13), P(12, -12.5), P(21, -9), P(23, -2), P(21, 7), P(12, 11), P(0, 12), P(-12, 11), P(-21, 7));
        s.Body(T(shoulders));
        // Arms lie along the sides with the hands just showing in front; walking swings them opposite to the feet.
        foreach (int side in new[] { -1, 1 })
        {
            double forward = -side * 8 * swing;
            s.Over(T(Union([Ellipse(side * 25, 1 + forward, 5.2, 11.5, -6 * swing), Circle(side * 24.5, -10 + forward, 3.6)])), .45);
        }
        var head = new List<Geometry> { Circle(0, -1, 9.2), Ellipse(-9.2, -1, 1.6, 2.6), Ellipse(9.2, -1, 1.6, 2.6), Ellipse(0, -10.8, 1.7, 2.2) };
        switch (hair)
        {
            case PlanHair.Long: head.Add(Spline(true, P(-9.6, -2), P(-10.4, 6), P(-8.5, 15), P(0, 17), P(8.5, 15), P(10.4, 6), P(9.6, -2), P(0, 3))); break;
            case PlanHair.Cap: head.Add(Ellipse(0, -9.5, 7.2, 5)); break;
        }
        s.Over(T(Union(head)), .6);
        // The hairline (or the cap's crown seam) across the front of the head.
        if (hair == PlanHair.Cap) s.Line(T(Arc(P(0, -1), 6.5, 200, 340)), .4);
        else s.Line(T(Arc(P(0, 2.5), 8, 215, 325)), .4);
    }

    // A pram from above with its hood toward the pusher, handle at y = 0, pointing up.
    static void PlanStroller(EntourageSketch s, double x, double y)
    {
        var t = new TranslateTransform(x, y); t.Freeze();
        Geometry T(Geometry g) => Transformed(g, t);
        foreach (var (wx, wy) in new[] { (-27.0, -78.0), (27.0, -78.0), (-27.0, -12.0), (27.0, -12.0) }) s.Body(T(Capsule(P(wx, wy - 7), 2.6, P(wx, wy + 7), 2.6)));
        s.Body(T(Capsule(P(-25, 0), 2.2, P(25, 0), 2.2)), T(Capsule(P(-22, 0), 1.6, P(-22, -14), 1.6)), T(Capsule(P(22, 0), 1.6, P(22, -14), 1.6)));
        s.Over(T(Spline(true, P(-22, -14), P(22, -14), P(24, -50), P(20, -80), P(0, -86), P(-20, -80), P(-24, -50))), .55);
        // The hood folds over the back half: ribs across it.
        s.Over(T(Spline(true, P(-23, -15), P(23, -15), P(24, -44), P(0, -47), P(-24, -44))), .5);
        for (int i = 1; i <= 2; i++) s.Line(T(Spline(false, P(-23, -15 - i * 10), P(0, -18 - i * 10), P(23, -15 - i * 10))), .35);
    }

    // A bicycle from above, pointing up, its middle at (x, y): tyres, frame, bars, saddle and cranks.
    static void PlanBike(EntourageSketch s, double x, double y, double turn = 0)
    {
        var t = new TransformGroup { Children = { new RotateTransform(turn), new TranslateTransform(x, y) } }; t.Freeze();
        Geometry T(Geometry g) => Transformed(g, t);
        s.Body(T(Capsule(P(0, -88), 2.6, P(0, -20), 2.6)), T(Capsule(P(0, 18), 2.6, P(0, 86), 2.6)));
        s.Body(T(Capsule(P(0, -54), 1.6, P(0, 26), 1.6)), T(Capsule(P(-3, -54), 1.3, P(-3, 52), 1.3)), T(Capsule(P(3, -54), 1.3, P(3, 52), 1.3)));
        // Swept handlebars with grips, saddle, cranks and pedals.
        s.Body(T(Spline(false, P(-28, -48), P(-16, -56), P(0, -56), P(16, -56), P(28, -48)).GetWidenedPathGeometry(new Pen(Brushes.Black, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Tolerance, ToleranceType.Absolute)));
        s.Over(T(Spline(true, P(0, 18), P(6, 24), P(5, 34), P(0, 36), P(-5, 34), P(-6, 24))), .5);
        s.Body(T(Circle(0, 4, 5)), T(Capsule(P(-13, 0), 1.8, P(13, 8), 1.8)), T(Capsule(P(-17, -2), 2.2, P(-11, -2), 2.2)), T(Capsule(P(11, 10), 2.2, P(17, 10), 2.2)));
        s.Line(T(Capsule(P(-31, -47), 2.4, P(-25, -51), 2.4)), .45); s.Line(T(Capsule(P(25, -51), 2.4, P(31, -47), 2.4)), .45);
    }

    static void PlanRider(EntourageSketch s, double turn)
    {
        PlanBike(s, 0, 0, turn);
        var t = new RotateTransform(turn); t.Freeze();
        Geometry T(Geometry g) => Transformed(g, t);
        // Leaning forward over the bars: hips on the saddle, shoulders ahead, arms reaching to the grips.
        s.Over(T(Spline(true, P(-11, 30), P(-18, 6), P(-22, -14), P(-14, -22), P(0, -24), P(14, -22), P(22, -14), P(18, 6), P(11, 30), P(0, 34))), .55);
        s.Over(T(Union([Capsule(P(-20, -16), 4.6, P(-27, -47), 3)])), .45);
        s.Over(T(Union([Capsule(P(20, -16), 4.6, P(27, -47), 3)])), .45);
        s.Over(T(Union([Ellipse(0, -29, 8.4, 9.8), Ellipse(0, -38.5, 6.8, 4.4)])), .6);
    }

    static void PlanCompact(EntourageSketch s, bool van)
    {
        double half = van ? 250 : 200, w = van ? 95 : 87;
        if (van)
        {
            s.Body(Spline(true, P(-w + 4, -half + 22), P(-w, -half + 80), P(-w, half - 30), P(-w + 6, half - 4), P(-60, half), P(60, half), P(w - 6, half - 4), P(w, half - 30), P(w, -half + 80), P(w - 4, -half + 22), P(60, -half), P(-60, -half)));
            s.Body(Ellipse(-w - 9, -half + 104, 9, 6, 15), Ellipse(w + 9, -half + 104, 9, 6, -15));
            s.Line(Poly(true, P(-w + 12, -half + 64), P(w - 12, -half + 64), P(w - 16, -half + 112), P(-w + 16, -half + 112)), .5);
            s.Line(new RectangleGeometry(new Rect(-w + 14, -half + 116, (w - 14) * 2, half * 2 - 144), 14, 14), .45);
            foreach (double x in new[] { -w + 26, w - 26 }) s.Line(Segment(P(x, -half + 140), P(x, half - 60)), .4);
            for (int i = 0; i < 4; i++) s.Line(Segment(P(-w + 26, -half + 170 + i * 90), P(w - 26, -half + 170 + i * 90)), .3);
            s.Line(Spline(false, P(-60, -half + 16), P(0, -half + 8), P(60, -half + 16)), .35);
        }
        else
        {
            s.Body(Spline(true, P(-w + 4, -half + 22), P(-w, -half + 70), P(-w + 2, half - 40), P(-w + 10, half - 6), P(-50, half), P(50, half), P(w - 10, half - 6), P(w - 2, half - 40), P(w, -half + 70), P(w - 4, -half + 22), P(56, -half), P(-56, -half)));
            s.Body(Ellipse(-w - 9, -half + 128, 9, 6, 15), Ellipse(w + 9, -half + 128, 9, 6, -15));
            s.Line(Poly(true, P(-w + 14, -half + 92), P(w - 14, -half + 92), P(w - 22, -half + 148), P(-w + 22, -half + 148)), .5);
            s.Line(new RectangleGeometry(new Rect(-w + 19, -half + 150, (w - 19) * 2, half * 2 - 220), 18, 18), .45);
            s.Line(Poly(true, P(-w + 18, half - 66), P(w - 18, half - 66), P(w - 24, half - 26), P(-w + 24, half - 26)), .5);
            s.Line(Spline(false, P(-56, -half + 14), P(0, -half + 6), P(56, -half + 14)), .35);
            s.Line(Spline(false, P(-w + 20, -half + 86), P(0, -half + 74), P(w - 20, -half + 86)), .3);
        }
        // Lights at the corners and the tyres just showing at the sides.
        foreach (int side in new[] { -1, 1 })
        {
            s.Line(Spline(false, P(side * (w - 6), -half + 26), P(side * (w - 20), -half + 10), P(side * (w - 40), -half + 4)), .45);
            s.Line(Segment(P(side * (w - 8), half - 10), P(side * (w - 34), half - 3)), .45);
            foreach (double y in van ? new[] { -half + 80, half - 70 } : new[] { -half + 62, half - 60 })
                s.Line(Segment(P(side * (w + 1), y - 26), P(side * (w + 1), y + 26)), .7);
        }
    }

    static void PlanCanopyTree(EntourageSketch s, EntourageRandom random, double radius)
    {
        // A rim of overlapping arcs (the leaf masses' edges) and the branch structure from the trunk:
        // limbs that wander out and fork into shorter twigs, thinning as they go, short of the rim.
        int n = 16 + random.Below(5); double phase = random.Range(0, Math.PI * 2);
        var parts = new List<Geometry> { Circle(0, 0, radius * .84) };
        for (int i = 0; i < n; i++)
        {
            double a = phase + i * Math.PI * 2 / n + random.Range(-.08, .08), r = radius * random.Range(.17, .23);
            parts.Add(Circle(Math.Cos(a) * (radius - r), Math.Sin(a) * (radius - r), r));
        }
        s.OutlineWeight = .9;
        s.Body(Union(parts));
        void Branch(Point from, double angle, double length, double weight, int depth, ref EntourageRandom r)
        {
            double bend = r.Range(-.35, .35);
            var mid = new Point(from.X + Math.Cos(angle + bend * .5) * length * .5, from.Y + Math.Sin(angle + bend * .5) * length * .5);
            var to = new Point(mid.X + Math.Cos(angle + bend) * length * .5, mid.Y + Math.Sin(angle + bend) * length * .5);
            s.Line(Spline(false, from, mid, to), weight);
            if (depth == 0) return;
            // Side twigs from the middle and a fork at the end, all turning away a little.
            double side = r.Chance(.5) ? 1 : -1;
            Branch(mid, angle + bend * .5 + side * r.Range(.55, .85), length * r.Range(.34, .44), weight * .68, depth - 1, ref r);
            Branch(to, angle + bend - side * r.Range(.2, .45), length * r.Range(.5, .62), weight * .75, depth - 1, ref r);
            if (depth > 1) Branch(to, angle + bend + side * r.Range(.3, .55), length * r.Range(.36, .46), weight * .65, depth - 2, ref r);
        }
        int limbs = 7 + random.Below(3);
        for (int i = 0; i < limbs; i++)
        {
            double a = phase + .2 + i * Math.PI * 2 / limbs + random.Range(-.3, .3);
            Branch(new Point(Math.Cos(a) * radius * .07, Math.Sin(a) * radius * .07), a, radius * random.Range(.3, .4), .75, 3, ref random);
        }
        s.Line(Circle(0, 0, radius * .07), .7);
    }

    static void PlanTexturedTree(EntourageSketch s, EntourageRandom random, double radius)
    {
        // An irregular crown filled with small leaf curls.
        int n = 26; var rim = new List<Point>(); double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < n; i++)
        {
            double a = phase + i * Math.PI * 2 / n, r = radius * (.9 + .1 * Math.Sin(a * 3 + phase) * random.Range(.5, 1)) * random.Range(.96, 1.02);
            rim.Add(new Point(Math.Cos(a) * r, Math.Sin(a) * r));
        }
        s.Body(Spline(true, rim.ToArray()));
        var placed = new List<Point>();
        for (int attempt = 0; attempt < 900 && placed.Count < 46; attempt++)
        {
            var p = new Point(random.Range(-radius, radius), random.Range(-radius, radius));
            if (p.X * p.X + p.Y * p.Y > radius * radius * .6 || placed.Any(q => (q - p).Length < radius * .17)) continue;
            placed.Add(p);
            double a = random.Range(0, 360), r = radius * random.Range(.045, .07);
            s.Line(Arc(p, r, a, a + random.Range(190, 250)), .45);
        }
        s.Line(Circle(0, 0, radius * .05), .6);
    }

    static void PlanPalmStar(EntourageSketch s, EntourageRandom random, double radius)
    {
        // Feathered fronds: a smooth leaf outline around each rib, its leaflets as strokes swept toward the tip.
        int fronds = 9 + random.Below(4); double phase = random.Range(0, Math.PI * 2);
        var leaves = new List<Geometry> { Circle(0, 0, radius * .1) };
        var strokes = new List<Geometry>();
        for (int i = 0; i < fronds; i++)
        {
            double a = phase + i * Math.PI * 2 / fronds + random.Range(-.12, .12), length = radius * random.Range(.86, 1), curve = random.Range(-.12, .12);
            var direction = new Vector(Math.Cos(a), Math.Sin(a)); var across = new Vector(-direction.Y, direction.X);
            Point Rib(double t) => new Point(0, 0) + direction * (length * t) + across * (curve * length * t * t);
            double Width(double t) => radius * .17 * Math.Pow(Math.Sin(Math.PI * Math.Min(1, t * 1.04)), .8) + .5;
            var edge = new List<Point>(); var back = new List<Point>();
            for (int j = 0; j <= 10; j++) { double t = j / 10.0; edge.Add(Rib(t) + across * Width(t)); back.Add(Rib(t) - across * Width(t)); }
            edge.AddRange(Enumerable.Reverse(back));
            leaves.Add(Spline(true, edge.ToArray()));
            strokes.Add(Spline(false, Enumerable.Range(0, 11).Select(j => Rib(j / 10.0)).ToArray()));
            for (double t = .26; t < .95; t += .09)
                foreach (int side in new[] { -1, 1 })
                {
                    double w = Width(t) * .92;
                    strokes.Add(Segment(Rib(t), Rib(Math.Min(1, t + .07)) + across * (side * w)));
                }
        }
        s.Body(Union(leaves));
        foreach (var stroke in strokes) s.Line(stroke, .38);
        s.Line(Circle(0, 0, radius * .06), .6);
    }

    static void PlanHedge(EntourageSketch s, EntourageRandom random, bool clipped)
    {
        double half = 200, w = 45;
        if (clipped)
        {
            var strip = new RectangleGeometry(new Rect(-half, -w, half * 2, w * 2), 8, 8);
            s.Body(strip);
            for (double x = -half + 10; x < half + w * 2; x += 18) s.Line(Segment(P(x, -w), P(x - w * 1.3, w)), .3, strip);
            s.Line(Segment(P(-half + 4, 0), P(half - 4, 0)), .4);
            return;
        }
        var parts = new List<Geometry> { Capsule(P(-half + w, 0), w * .8, P(half - w, 0), w * .8) };
        for (double x = -half + w * .7; x <= half - w * .7; x += w * .55)
            foreach (int side in new[] { -1, 1 })
            {
                double r = w * random.Range(.38, .5);
                parts.Add(Circle(x + random.Range(-5, 5), side * (w - r), r));
            }
        parts.Add(Circle(-half + w * .9, 0, w)); parts.Add(Circle(half - w * .9, 0, w));
        s.Body(Union(parts));
        for (double x = -half + w * .8; x < half - w * .6; x += w * .7)
            s.Line(Arc(P(x + random.Range(-6, 6), random.Range(-w * .3, w * .3)), w * .22, random.Range(0, 90), random.Range(200, 300)), .4);
    }

    static void PlanGroundCover(EntourageSketch s, EntourageRandom random, bool grass)
    {
        // An irregular patch about 3 m across.
        double radius = 150; int n = 22; var rim = new List<Point>(); double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < n; i++)
        {
            double a = phase + i * Math.PI * 2 / n, r = radius * (.78 + .22 * Math.Sin(a * 2 + phase)) * random.Range(.9, 1.05);
            rim.Add(new Point(Math.Cos(a) * r * 1.15, Math.Sin(a) * r * .85));
        }
        var patch = Spline(true, rim.ToArray());
        s.OutlineWeight = grass ? 0 : .6;
        s.Body(patch);
        var placed = new List<Point>();
        for (int attempt = 0; attempt < 1500 && placed.Count < (grass ? 70 : 60); attempt++)
        {
            var p = new Point(random.Range(-radius * 1.2, radius * 1.2), random.Range(-radius, radius));
            if (!patch.FillContains(p) || placed.Any(q => (q - p).Length < (grass ? 20 : 22))) continue;
            placed.Add(p);
            if (grass)
            {
                // A tuft: three blades from a short base, the outer ones curling outward.
                foreach (int blade in new[] { -1, 0, 1 })
                {
                    var root = new Point(p.X + blade * 2.2, p.Y);
                    double l = blade == 0 ? 11 : 8.5, lean = blade * 4.5 + random.Range(-1.2, 1.2);
                    s.Line(Spline(false, root, new Point(root.X + lean * .35, root.Y - l * .55), new Point(root.X + lean, root.Y - l)), .7);
                }
            }
            else
            {
                double a = random.Range(0, 360);
                s.Line(Arc(p, random.Range(5, 8), a, a + random.Range(200, 280)), .45);
                if (random.Chance(.35)) s.Line(Circle(p.X + random.Range(-9, 9), p.Y + random.Range(-9, 9), 1.4), .5);
            }
        }
    }

    // ---- Items --------------------------------------------------------------------------

    static void AddPlanPeople(List<EntourageItem> list)
    {
        string plan = K("평면", " plan top people person");
        double height = 1.7 / .6;
        list.Add(Item("person.plan-standing", "서 있는 사람 · 평면 상세", EntourageCategory.People, EntourageView.Plan, .6, EntourageFill.White, 3,
            K("사람", "서 있는", "어깨") + plan + " standing shoulders", (s, _, v) => PlanPerson(s, 0, 0, v switch { 1 => 14, 2 => -10, _ => 0 }, (PlanHair)v), shadowHeight: height));
        list.Add(Item("person.plan-stride", "걷는 사람 · 평면 상세", EntourageCategory.People, EntourageView.Plan, .75, EntourageFill.White, 3,
            K("사람", "보행", "걷는") + plan + " walking stride feet", (s, _, v) => PlanPerson(s, 0, 0, 0, (PlanHair)v, stride: 1, swing: 1), shadowHeight: 1.7 / .75));
        list.Add(Item("person.plan-pair", "두 사람 · 평면", EntourageCategory.People, EntourageView.Plan, 1.2, EntourageFill.White, 2,
            K("사람", "두 사람", "대화", "함께") + plan + " pair two talking", (s, _, v) =>
            {
                if (v == 1) { PlanPerson(s, -30, 0, 0, PlanHair.Long, 1, 1); PlanPerson(s, 30, 6, 0, PlanHair.Short, -1, -1); }
                else { PlanPerson(s, -36, 0, 90, PlanHair.Short); PlanPerson(s, 36, 4, -96, PlanHair.Long); }
            }, shadowHeight: 1.7 / 1.2));
        list.Add(Item("person.plan-group", "세 사람 · 평면", EntourageCategory.People, EntourageView.Plan, 1.4, EntourageFill.White, 2,
            K("사람", "무리", "여럿", "대화") + plan + " group three crowd", (s, _, v) =>
            {
                double[] angles = v == 1 ? [-100, 10, 130] : [-90, 30, 150];
                PlanHair[] hair = [PlanHair.Short, PlanHair.Long, PlanHair.Cap];
                for (int i = 0; i < 3; i++)
                {
                    double a = angles[i] * Math.PI / 180;
                    PlanPerson(s, Math.Cos(a) * 46, Math.Sin(a) * 46, angles[i] - 90, hair[(i + v) % 3]);
                }
            }, shadowHeight: 1.7 / 1.4));
        list.Add(Item("person.plan-stroller", "유모차와 사람 · 평면", EntourageCategory.People, EntourageView.Plan, 1.4, EntourageFill.White, 2,
            K("사람", "유모차", "아기") + plan + " stroller pram baby", (s, _, v) => { PlanStroller(s, 0, -14); PlanPerson(s, 0, 14, 0, v == 1 ? PlanHair.Long : PlanHair.Short, .7, 0); }, shadowHeight: 1.7 / 1.4));
        list.Add(Item("person.plan-cyclist", "자전거 탄 사람 · 평면", EntourageCategory.People, EntourageView.Plan, 1.8, EntourageFill.White, 2,
            K("사람", "자전거") + plan + " cyclist bicycle rider", (s, _, v) => PlanRider(s, v == 1 ? 8 : 0), shadowHeight: 1.65 / 1.8));
    }

    static void AddPlanPlants(List<EntourageItem> list)
    {
        string tree = K("나무", "수목", "조경", "tree trees landscape"), plan = K("평면", " plan canopy top");
        list.Add(Item("tree.plan-canopy", "수목 · 가지 구조", EntourageCategory.Plants, EntourageView.Plan, 6, EntourageFill.None, 4,
            tree + plan + K("가지", "구조", " branch structure"), (s, r, _) => PlanCanopyTree(s, r, 300), shadowHeight: 1.4));
        list.Add(Item("tree.plan-textured", "수목 · 잎 질감", EntourageCategory.Plants, EntourageView.Plan, 5, EntourageFill.None, 3,
            tree + plan + K("질감", "잎", " textured leaves"), (s, r, _) => PlanTexturedTree(s, r, 250), shadowHeight: 1.4));
        list.Add(Item("tree.plan-palm", "야자수 · 평면", EntourageCategory.Plants, EntourageView.Plan, 5, EntourageFill.None, 3,
            tree + plan + K("야자", " palm fronds star"), (s, r, _) => PlanPalmStar(s, r, 250), shadowHeight: 1.8));
        list.Add(Item("plant.plan-hedge", "생울타리 · 평면", EntourageCategory.Plants, EntourageView.Plan, 4, EntourageFill.None, 2,
            K("생울타리", "관목", "울타리") + plan + " hedge strip", (s, r, v) => PlanHedge(s, r, v == 1), shadowHeight: .3));
        list.Add(Item("plant.plan-groundcover", "지피 식물 · 평면", EntourageCategory.Plants, EntourageView.Plan, 3.4, EntourageFill.None, 3,
            K("지피", "화단", "식재") + plan + " ground cover planting bed", (s, r, _) => PlanGroundCover(s, r, false), shadowHeight: .05));
        list.Add(Item("plant.plan-grass", "잔디 · 평면", EntourageCategory.Plants, EntourageView.Plan, 3.4, EntourageFill.None, 3,
            K("잔디", "풀", "초지") + plan + " grass lawn tufts", (s, r, _) => PlanGroundCover(s, r, true), shadowHeight: .05));
    }

    static void AddPlanVehicles(List<EntourageItem> list)
    {
        list.Add(Item("car.plan-hatch", "소형차 · 평면", EntourageCategory.Vehicles, EntourageView.Plan, 4.0, EntourageFill.White, 1,
            K("자동차", "소형차", "주차", "평면", "car hatchback small parking plan top vehicle"), (s, _, _) => PlanCompact(s, false), shadowHeight: 1.48 / 4.0));
        list.Add(Item("car.plan-van", "승합차 · 평면", EntourageCategory.Vehicles, EntourageView.Plan, 5.0, EntourageFill.White, 1,
            K("자동차", "승합차", "밴", "주차", "평면", "car van minivan parking plan top vehicle"), (s, _, _) => PlanCompact(s, true), shadowHeight: 1.9 / 5.0));
        list.Add(Item("bike.plan-detail", "자전거 · 평면 상세", EntourageCategory.Vehicles, EntourageView.Plan, 1.8, EntourageFill.White, 1,
            K("자전거", "평면", "bicycle bike plan top parking"), (s, _, _) => PlanBike(s, 0, 0), shadowHeight: 1.0 / 1.8));
    }
}
