using System.Windows;
using System.Windows.Media;
using static Compositor.Windows.EntourageShapes;

namespace Compositor.Windows;

// Trees and plants, grown from seeded random numbers: a variant is another tree of the same kind.
// Canopies are clouds of overlapping leaf masses whose union is the outline; a few inner arcs
// suggest the masses; trunks and branches taper.
public static partial class EntourageLibrary
{
    // Tapered branch from a to b; returns the end for further growth.
    static Geometry Branch(Point a, double ra, Point b, double rb) => Capsule(a, ra, b, rb);

    // A cloud of leaf masses around an ellipse: masses along the rim plus a few inside, and arcs
    // under some of the masses for depth.
    static void Canopy(EntourageSketch s, ref EntourageRandom random, Point center, double rx, double ry, int masses, double massSize, double arcs = .45)
    {
        var parts = new List<Geometry> { Ellipse(center.X, center.Y, rx * .82, ry * .82) };
        var inner = new List<(Point, double)>();
        double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < masses; i++)
        {
            double a = phase + i * Math.PI * 2 / masses + random.Range(-.18, .18);
            double r = massSize * random.Range(.78, 1.18) * Math.Min(rx, ry);
            var at = new Point(center.X + Math.Cos(a) * (rx - r * .72), center.Y + Math.Sin(a) * (ry - r * .72));
            parts.Add(Circle(at.X, at.Y, r));
            inner.Add((at, r));
        }
        s.Body(Union(parts));
        if (arcs <= 0) return;
        // Shading arcs: the lower edge of masses inside the crown, pulled toward the centre.
        int count = Math.Max(3, masses / 2);
        for (int i = 0; i < count; i++)
        {
            var (at, r) = inner[(i * 2 + 1) % inner.Count];
            var p = new Point(center.X + (at.X - center.X) * random.Range(.35, .62), center.Y + (at.Y - center.Y) * random.Range(.35, .62));
            double start = random.Range(15, 40), sweep = random.Range(80, 125);
            s.Line(Arc(p, r * random.Range(.55, .8), start, start + sweep), arcs);
        }
    }

    static void RoundTree(EntourageSketch s, EntourageRandom random, int variant, bool oval)
    {
        double height = oval ? 1000 : 800, lean = random.Range(-14, 14);
        double crownBottom = oval ? -270 : -300, crownTop = -height;
        double ry = (crownBottom - crownTop) / 2, rx = oval ? ry * random.Range(.46, .54) : ry * random.Range(1.08, 1.24);
        var center = new Point(lean * 1.5, (crownBottom + crownTop) / 2);
        var fork = new Point(lean * .8, crownBottom - (oval ? 20 : 10));
        // Trunk with a slight flare at the ground and main limbs into the crown.
        s.Body(Branch(new Point(0, -2), 19, fork, 11), Capsule(new Point(-12, -6), 9, new Point(12, -6), 9));
        int limbs = 3 + random.Below(2);
        for (int i = 0; i < limbs; i++)
        {
            double a = -90 + (i - (limbs - 1) / 2.0) * (oval ? 18 : 32) + random.Range(-8, 8);
            var end = new Point(fork.X + Math.Cos(a * Math.PI / 180) * rx * .75, fork.Y + Math.Sin(a * Math.PI / 180) * ry * .9);
            s.Body(Branch(fork, 9, end, 3.5));
            // Limbs show through the lower crown as thin lines.
            if (i % 2 == 0) s.Line(Spline(false, fork, new Point((fork.X + end.X) / 2 + random.Range(-15, 15), (fork.Y + end.Y) / 2), end), .4);
        }
        Canopy(s, ref random, center, rx, ry, oval ? 16 : 18 + variant, oval ? .3 : .26);
    }

    static void AiryTree(EntourageSketch s, EntourageRandom random)
    {
        // Three main limbs fanning out from a low fork, forking twice; leaf clusters at the twigs.
        var tips = new List<(Point P, double Size)>();
        void Grow(Point from, double angle, double length, double radius, int depth, ref EntourageRandom r)
        {
            var to = new Point(from.X + Math.Cos(angle) * length, from.Y + Math.Sin(angle) * length);
            s.Body(Branch(from, radius, to, radius * .68));
            if (depth == 0) { tips.Add((to, length)); return; }
            for (int i = 0; i < 2; i++)
            {
                double spread = (i - .5) * r.Range(.55, .8) + r.Range(-.1, .1);
                Grow(to, angle + spread, length * r.Range(.66, .8), radius * .68, depth - 1, ref r);
            }
        }
        var fork = new Point(random.Range(-15, 15), -250);
        s.Body(Branch(new Point(0, -2), 17, fork, 11), Capsule(new Point(-11, -6), 8, new Point(11, -6), 8));
        foreach (double a in new[] { -128.0, -90, -52 })
            Grow(fork, (a + random.Range(-8, 8)) * Math.PI / 180, random.Range(170, 200) * (a == -90 ? 1.1 : 1), 9, 2, ref random);
        foreach (var (p, size) in tips)
        {
            double r = Math.Clamp(size * random.Range(.8, 1.05), 60, 105);
            var cluster = new List<Geometry>();
            int n = 6 + random.Below(3);
            for (int i = 0; i < n; i++)
            {
                double a = i * Math.PI * 2 / n + random.Range(-.3, .3), d = r * random.Range(.35, .55);
                cluster.Add(Circle(p.X + Math.Cos(a) * d, p.Y - r * .25 + Math.Sin(a) * d * .75, r * random.Range(.36, .5)));
            }
            s.Body(Union(cluster));
            s.Line(Arc(new Point(p.X + random.Range(-10, 10), p.Y - r * .1), r * .42, random.Range(20, 40), random.Range(120, 160)), .4);
        }
    }
    static void Conifer(EntourageSketch s, EntourageRandom random)
    {
        double height = 1100 * random.Range(.94, 1.04), baseWidth = 230 * random.Range(.9, 1.1), trunk = 130;
        int tiers = 7 + random.Below(2);
        s.Body(Branch(new Point(0, -2), 13, new Point(0, -trunk - 60), 9));
        var left = new List<Point>(); var lines = new List<Geometry>();
        double previousW = 0, previousY = -height;
        for (int i = 0; i < tiers; i++)
        {
            // Tier i from the top: it starts tucked under the tier above (the notch) and droops to its tip.
            double u = (i + 1) / (double)tiers;
            double tipY = -height + (height - trunk) * u + 12, tipW = baseWidth * Math.Pow(u, .92) * random.Range(.9, 1.08);
            double notchW = i == 0 ? 0 : previousW * .55, notchY = i == 0 ? -height : previousY - (tipY - previousY) * .3;
            for (int j = i == 0 ? 0 : 1; j <= 4; j++)
            {
                double t = j / 4.0;
                left.Add(new Point(-(notchW + (tipW - notchW) * t), notchY + (tipY - notchY) * Math.Pow(t, 1.5)));
            }
            if (i < tiers - 1) lines.Add(Spline(false, new Point(-tipW * .86, tipY - 3), new Point(0, tipY - 16), new Point(tipW * .86, tipY - 2)));
            previousW = tipW; previousY = tipY;
        }        left[0] = new Point(0, -height);
        var outline = new List<Point>(left);
        outline.AddRange(left.AsEnumerable().Reverse().Select(p => new Point(-p.X * random.Range(.94, 1.04), p.Y + random.Range(-4, 4))));
        s.Body(Poly(true, outline.ToArray()));
        foreach (var line in lines) s.Line(line, .4);
        s.Line(Segment(new Point(0, -height * .9), new Point(0, -trunk - 40)), .35);
    }

    static void Palm(EntourageSketch s, EntourageRandom random)
    {
        double height = 900 * random.Range(.94, 1.04), bend = random.Range(30, 70) * (random.Chance(.5) ? 1 : -1);
        var crown = new Point(bend, -height + 160);
        // Trunk: a gentle curve, wide at the foot, with ring marks.
        var spine = new List<Point>();
        for (int i = 0; i <= 16; i++) { double t = i / 16.0; spine.Add(new Point(bend * t * t, (crown.Y + 10) * t)); }
        var sides = new List<Point>(); var other = new List<Point>();
        for (int i = 0; i < spine.Count; i++)
        {
            var d = spine[Math.Min(i + 1, spine.Count - 1)] - spine[Math.Max(i - 1, 0)]; d.Normalize();
            double r = 19 - 8 * i / 16.0 + (i == 0 ? 6 : 0);
            sides.Add(spine[i] + new Vector(-d.Y, d.X) * r); other.Add(spine[i] - new Vector(-d.Y, d.X) * r);
        }
        sides.AddRange(other.AsEnumerable().Reverse());
        s.Body(Poly(true, sides.ToArray()));
        for (int i = 2; i < spine.Count - 1; i++)
        {
            var d = spine[i + 1] - spine[i - 1]; d.Normalize(); var n = new Vector(-d.Y, d.X); double r = 19 - 8 * i / 16.0;
            s.Line(Spline(false, spine[i] + n * r * .9, spine[i] + d * 3, spine[i] - n * r * .9), .35);
        }
        // Fronds: feather-shaped leaves arching out and down from the crown.
        int fronds = 9 + random.Below(3);
        var leaves = new List<Geometry> { Circle(crown.X, crown.Y, 22) };
        for (int i = 0; i < fronds; i++)
        {
            double a = -Math.PI / 2 + (i - (fronds - 1) / 2.0) * (Math.PI * 1.55 / fronds) + random.Range(-.08, .08);
            double length = random.Range(210, 270), droop = random.Range(.7, 1.1) * (1 - Math.Abs(Math.Sin(a + Math.PI / 2)) * .2);
            var rib = new List<Point>();
            for (int j = 0; j <= 10; j++)
            {
                double t = j / 10.0, x = Math.Cos(a) * length * t, y = Math.Sin(a) * length * t + droop * length * .55 * t * t;
                rib.Add(new Point(crown.X + x, crown.Y + y));
            }
            var edge = new List<Point>(); var back = new List<Point>();
            for (int j = 0; j < rib.Count; j++)
            {
                var d = rib[Math.Min(j + 1, rib.Count - 1)] - rib[Math.Max(j - 1, 0)]; d.Normalize(); var n = new Vector(-d.Y, d.X);
                double t = j / 10.0, w = 26 * Math.Sin(Math.PI * Math.Min(1, t * 1.05)) + 2;
                // Serrated leaflet edge: alternate long and short.
                double tooth = j % 2 == 0 ? 1 : .55;
                edge.Add(rib[j] + n * w * tooth); back.Add(rib[j] - n * w * tooth * .9);
            }
            edge.AddRange(back.AsEnumerable().Reverse());
            leaves.Add(Poly(true, edge.ToArray()));
            s.Line(Spline(false, rib.ToArray()), .4);
        }
        s.Body(Union(leaves));
    }

    static void Shrub(EntourageSketch s, EntourageRandom random)
    {
        double width = 190 * random.Range(.9, 1.1), height = 120 * random.Range(.92, 1.08);
        var parts = new List<Geometry> { Spline(true, new Point(-width * .48, -2), new Point(-width * .4, -height * .6), new Point(0, -height * .82), new Point(width * .4, -height * .6), new Point(width * .48, -2)) };
        int n = 9 + random.Below(3);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)(n - 1), a = Math.PI * (1 + t);
            double r = height * random.Range(.24, .34);
            var at = new Point(Math.Cos(a) * (width / 2 - r * .8), -height * .45 + Math.Sin(a) * (height * .55 - r * .8));
            parts.Add(Circle(at.X, Math.Min(at.Y, -r * .9), r));
        }
        s.Body(Union(parts));
        for (int i = 0; i < 4; i++)
            s.Line(Arc(new Point(random.Range(-width * .3, width * .3), -height * random.Range(.35, .6)), height * .2, random.Range(20, 40), random.Range(130, 160)), .4);
    }

    static void Grass(EntourageSketch s, EntourageRandom random)
    {
        s.OutlineWeight = .8;
        int blades = 16 + random.Below(6); var parts = new List<Geometry>();
        for (int i = 0; i < blades; i++)
        {
            double t = (i + .5) / blades - .5, a = t * 1.7 + random.Range(-.12, .12), length = random.Range(55, 85) * (1 - Math.Abs(t) * .45);
            var root = new Point(t * 12, -.5);
            var mid = new Point(root.X + Math.Sin(a) * length * .45, -length * .55);
            var tip = new Point(root.X + Math.Sin(a) * length * .95 + Math.Sign(a) * length * .15, -length * Math.Cos(a * .75));
            parts.Add(Spline(true, new Point(root.X - 1.3, root.Y), new Point(mid.X - .9, mid.Y), tip, new Point(mid.X + .9, mid.Y), new Point(root.X + 1.3, root.Y)));
        }
        s.Body(Union(parts));
    }
    // ---- Plan symbols ----------------------------------------------------------------------

    static void PlanLobed(EntourageSketch s, EntourageRandom random, double radius)
    {
        int n = 12 + random.Below(5);
        var parts = new List<Geometry> { Circle(0, 0, radius * .78) };
        double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < n; i++)
        {
            double a = phase + i * Math.PI * 2 / n + random.Range(-.1, .1), r = radius * random.Range(.2, .27);
            parts.Add(Circle(Math.Cos(a) * (radius - r), Math.Sin(a) * (radius - r), r));
        }
        s.Body(Union(parts));
        // Inner ring of smaller lobes and the trunk.
        int m = 8 + random.Below(3);
        for (int i = 0; i < m; i++)
        {
            double a = phase + .3 + i * Math.PI * 2 / m, r = radius * .17;
            var c = new Point(Math.Cos(a) * radius * .48, Math.Sin(a) * radius * .48);
            double facing = a * 180 / Math.PI;
            s.Line(Arc(c, r, facing - 70, facing + 70), .45);
        }
        s.Line(Circle(0, 0, radius * .06), .6);
    }

    static void PlanBranches(EntourageSketch s, EntourageRandom random, double radius)
    {
        int n = 22; var rim = new List<Point>(); double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < n; i++) { double a = phase + i * Math.PI * 2 / n; double r = radius * random.Range(.93, 1.0); rim.Add(new Point(Math.Cos(a) * r, Math.Sin(a) * r)); }
        s.Body(Spline(true, rim.ToArray()));
        int limbs = 6 + random.Below(3);
        for (int i = 0; i < limbs; i++)
        {
            double a = phase + i * Math.PI * 2 / limbs + random.Range(-.15, .15), r = radius * random.Range(.72, .86);
            var mid = new Point(Math.Cos(a) * r * .45, Math.Sin(a) * r * .45); var end = new Point(Math.Cos(a + random.Range(-.08, .08)) * r, Math.Sin(a) * r);
            s.Line(Spline(false, new Point(Math.Cos(a) * radius * .07, Math.Sin(a) * radius * .07), mid, end), .55);
            foreach (int side in new[] { -1, 1 })
            {
                double b = a + side * random.Range(.35, .55);
                s.Line(Segment(mid + (end - mid) * .3, new Point(Math.Cos(b) * r * .82, Math.Sin(b) * r * .82)), .4);
            }
        }
        s.Line(Circle(0, 0, radius * .07), .6);
    }

    static void PlanScribble(EntourageSketch s, EntourageRandom random, double radius)
    {
        // A hand-drawn crown: one continuous line looping around the rim (a closed curve of small loops),
        // a looser inner loop and the trunk.
        s.OutlineWeight = 0;
        s.Body(Circle(0, 0, radius));
        int loops = 15 + random.Below(4); double phase = random.Range(0, Math.PI * 2), loop = radius * .12;
        var points = new List<Point>();
        for (int i = 0; i < 480; i++)
        {
            double t = i * Math.PI * 2 / 480, wobble = 1 + .03 * Math.Sin(t * 3 + phase);
            double r = (radius - loop * 1.05) * wobble;
            points.Add(new Point(Math.Cos(t + phase) * r + Math.Cos(t * loops + phase) * loop, Math.Sin(t + phase) * r + Math.Sin(t * loops + phase) * loop));
        }
        s.Line(Poly(true, points.ToArray()), 1);
        var inner = new List<Point>(); int innerLoops = 7 + random.Below(3); double innerLoop = radius * .1;
        for (int i = 0; i < 240; i++)
        {
            double t = i * Math.PI * 2 / 240, r = radius * .42 * (1 + .08 * Math.Sin(t * 2 + phase));
            inner.Add(new Point(Math.Cos(t) * r + Math.Cos(t * innerLoops) * innerLoop, Math.Sin(t) * r + Math.Sin(t * innerLoops) * innerLoop));
        }
        s.Line(Poly(true, inner.ToArray()), .5);
        s.Line(Circle(0, 0, radius * .05), .6);
    }

    static void PlanConifer(EntourageSketch s, EntourageRandom random, double radius)
    {
        int n = 12 + random.Below(3) * 2; var star = new List<Point>(); double phase = random.Range(0, Math.PI);
        for (int i = 0; i < n * 2; i++)
        {
            double a = phase + i * Math.PI / n, r = i % 2 == 0 ? radius * random.Range(.93, 1.0) : radius * random.Range(.66, .74);
            star.Add(new Point(Math.Cos(a) * r, Math.Sin(a) * r));
        }
        s.Body(Poly(true, star.ToArray()));
        // Needles: from a small ring at the trunk out toward every other point.
        for (int i = 0; i < n; i += 2)
        {
            var tip = star[i * 2]; var dir = tip - new Point(0, 0); dir.Normalize();
            s.Line(Segment(new Point(dir.X * radius * .12, dir.Y * radius * .12), tip - dir * radius * .14), .4);
        }
        s.Line(Circle(0, 0, radius * .1), .6);
    }
    static void PlanShrubs(EntourageSketch s, EntourageRandom random)
    {
        int count = 3 + random.Below(3); var centers = new List<(Point, double)>();
        for (int i = 0; i < count; i++)
        {
            double a = i * Math.PI * 2 / count + random.Range(-.3, .3), d = i == 0 && count > 3 ? 0 : random.Range(40, 70);
            centers.Add((new Point(Math.Cos(a) * d, Math.Sin(a) * d), random.Range(42, 62)));
        }
        var parts = new List<Geometry>();
        foreach (var (c, r) in centers)
        {
            int lobes = 7 + random.Below(3);
            parts.Add(Circle(c.X, c.Y, r * .75));
            for (int j = 0; j < lobes; j++) { double a = j * Math.PI * 2 / lobes; double lr = r * .32; parts.Add(Circle(c.X + Math.Cos(a) * (r - lr), c.Y + Math.Sin(a) * (r - lr), lr)); }
        }
        s.Body(Union(parts));
        foreach (var (c, r) in centers) s.Line(Circle(c.X, c.Y, r * .08), .5);
    }

    static void AddPlants(List<EntourageItem> list)
    {
        string tree = K("나무", "수목", "조경", "tree trees landscape");
        list.Add(Item("tree.round", "둥근 활엽수", EntourageCategory.Plants, EntourageView.Elevation, 8, EntourageFill.White, 4,
            tree + K("활엽수", "가로수", " deciduous round"), (s, r, v) => RoundTree(s, r, v, false)));
        list.Add(Item("tree.oval", "키 큰 활엽수", EntourageCategory.Plants, EntourageView.Elevation, 10, EntourageFill.White, 3,
            tree + K("활엽수", " tall columnar"), (s, r, v) => RoundTree(s, r, v, true)));
        list.Add(Item("tree.airy", "가지가 보이는 나무", EntourageCategory.Plants, EntourageView.Elevation, 9, EntourageFill.White, 3,
            tree + K("가지", "단풍", " airy branches"), (s, r, _) => AiryTree(s, r)));
        list.Add(Item("tree.conifer", "침엽수", EntourageCategory.Plants, EntourageView.Elevation, 11, EntourageFill.White, 3,
            tree + K("소나무", "상록수", " conifer pine evergreen"), (s, r, _) => Conifer(s, r)));
        list.Add(Item("tree.palm", "야자수", EntourageCategory.Plants, EntourageView.Elevation, 9, EntourageFill.White, 3,
            tree + K("야자", " palm tropical"), (s, r, _) => Palm(s, r)));
        list.Add(Item("plant.shrub", "관목", EntourageCategory.Plants, EntourageView.Elevation, 1.2, EntourageFill.White, 3,
            K("관목", "생울타리", "식물", "shrub bush hedge plant"), (s, r, _) => Shrub(s, r)));
        list.Add(Item("plant.grass", "풀", EntourageCategory.Plants, EntourageView.Elevation, .8, EntourageFill.White, 3,
            K("풀", "억새", "식물", "grass reeds plant"), (s, r, _) => Grass(s, r)));
        string plan = K("평면", " plan canopy top");
        list.Add(Item("tree.plan-lobed", "수목 · 잎 뭉치", EntourageCategory.Plants, EntourageView.Plan, 6, EntourageFill.None, 4,
            tree + plan + K(" lobed"), (s, r, _) => PlanLobed(s, r, 300), shadowHeight: 1.4));
        list.Add(Item("tree.plan-branches", "수목 · 가지", EntourageCategory.Plants, EntourageView.Plan, 6, EntourageFill.None, 4,
            tree + plan + K("가지", " branches"), (s, r, _) => PlanBranches(s, r, 300), shadowHeight: 1.4));
        list.Add(Item("tree.plan-scribble", "수목 · 손그림", EntourageCategory.Plants, EntourageView.Plan, 5, EntourageFill.None, 4,
            tree + plan + K("손그림", " scribble sketch"), (s, r, _) => PlanScribble(s, r, 250), shadowHeight: 1.4));
        list.Add(Item("tree.plan-conifer", "침엽수 · 평면", EntourageCategory.Plants, EntourageView.Plan, 4, EntourageFill.None, 3,
            tree + plan + K("침엽수", "소나무", " conifer pine star"), (s, r, _) => PlanConifer(s, r, 200), shadowHeight: 2.6));
        list.Add(Item("plant.plan-shrubs", "관목 무리 · 평면", EntourageCategory.Plants, EntourageView.Plan, 2.4, EntourageFill.None, 3,
            K("관목", "식물") + plan + " shrubs planting", (s, r, _) => PlanShrubs(s, r), shadowHeight: .5));
    }
}
