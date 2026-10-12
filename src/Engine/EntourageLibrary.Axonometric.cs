using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using static Compositor.Windows.EntourageShapes;

namespace Compositor.Windows;

// Axonometric entourage: people, trees, vehicles and street furniture modelled in centimetres around
// their ground point and drawn in the document's parallel projection (AxoSketch). People are built
// on the same proportions as the elevation set (170 cm adult, head about one eighth); trees grow
// from seeded random numbers so a variant is another tree of the same kind. Headings are chosen so a
// left-right flip of the layer (which swaps the two plan axes in these symmetric projections) gives
// the item turned the other way on the same ground.
public static partial class EntourageLibrary
{
    static Point3D Q(double x, double y, double z) => new(x, y, z);
    static Vector3D V(double x, double y, double z) => new(x, y, z);
    static double Rad(double degrees) => degrees * Math.PI / 180;

    sealed record AxoPose
    {
        public double Heading { get; init; }
        public double Scale { get; init; } = 1;
        public double HeadScale { get; init; } = 1;
        public double Build { get; init; } = 1;
        public double Lean { get; init; }
        public double HeadTurn { get; init; }
        public double HeadTilt { get; init; }
        // Legs: swing forward from the hip, knee bend, foot pitch (toe down). Arms: swing forward, elbow bend, spread outward.
        public (double Swing, double Knee, double Foot) LeftLeg { get; init; } = (2, 0, 0);
        public (double Swing, double Knee, double Foot) RightLeg { get; init; } = (-2, 0, 0);
        public (double Swing, double Elbow, double Out) LeftArm { get; init; } = (-3, 8, 7);
        public (double Swing, double Elbow, double Out) RightArm { get; init; } = (3, 8, 7);
        public Hair Hair { get; init; }
        public Clothes Clothes { get; init; }
        // Seated: the hip centre and the feet (local, before the heading).
        public Point3D? Seat { get; init; }
        public Point3D? LeftFoot { get; init; }
        public Point3D? RightFoot { get; init; }
        public Point3D? LeftHand { get; init; }
        public Point3D? RightHand { get; init; }
        public bool Bag { get; init; }
        public bool Phone { get; init; }
    }

    static AxoPose AxoWalk(int variant) => variant switch
    {
        1 => new AxoPose { Hair = Hair.Long, Clothes = Clothes.Skirt, Build = .94, LeftLeg = (16, 0, -6), RightLeg = (-11, 18, 20), LeftArm = (-16, 12, 6), RightArm = (14, 20, 6) },
        2 => new AxoPose { Hair = Hair.Cap, Clothes = Clothes.Coat, Lean = 3, LeftLeg = (17, 0, -6), RightLeg = (-12, 19, 21), LeftArm = (-15, 12, 6), RightArm = (13, 18, 6) },
        _ => new AxoPose { Hair = Hair.Short, Clothes = Clothes.Jacket, Lean = 2, LeftLeg = (18, 0, -6), RightLeg = (-12, 20, 22), LeftArm = (-18, 12, 6), RightArm = (16, 20, 6) }
    };

    // A person standing (or seated) with the ground point at `at` (local), facing pose.Heading.
    static void AxoFigure(AxoSketch a, AxoPose pose, Point3D at)
    {
        a.Push(at, pose.Heading);
        double k = pose.Scale, w = pose.Build, thigh = 43 * k, shank = 42 * k, upper = 29 * k, fore = 25 * k, hand = 16 * k;
        double lean = Rad(pose.Lean);

        (Point3D Hip, Point3D Knee, Point3D Ankle, Point3D Heel, Point3D Toe) Leg(Point3D hip, double side, (double Swing, double Knee, double Foot) leg, Point3D? foot)
        {
            var joint = hip + V(0, side * 9 * k * w, 0);
            Point3D knee, ankle; double pitch = leg.Foot;
            if (foot is { } target)
            {
                ankle = target + V(-4 * k, 0, 6 * k);
                knee = AxoSketch.Reach(joint, ankle, thigh, shank, V(1, side * .15, .2));
                pitch = 0;
            }
            else
            {
                double s = Rad(leg.Swing), b = Rad(leg.Swing - leg.Knee);
                knee = joint + V(Math.Sin(s), 0, -Math.Cos(s)) * thigh;
                ankle = knee + V(Math.Sin(b), 0, -Math.Cos(b)) * shank;
            }
            double p = Rad(pitch);
            Point3D Foot(double x, double z) => ankle + V(x * Math.Cos(p) + z * Math.Sin(p), 0, -x * Math.Sin(p) + z * Math.Cos(p));
            return (joint, knee, ankle, Foot(-3.5 * k, -3.2 * k), Foot(18 * k, -4.4 * k));
        }
        var hip = pose.Seat ?? Q(0, 0, 92 * k);
        var left = Leg(hip, 1, pose.LeftLeg, pose.LeftFoot); var right = Leg(hip, -1, pose.RightLeg, pose.RightFoot);
        if (pose.Seat == null)
        {
            // Standing figures rest on their lowest foot.
            double lowest = new[] { left.Heel.Z - 3.8 * k, left.Toe.Z - 2.6 * k, right.Heel.Z - 3.8 * k, right.Toe.Z - 2.6 * k }.Min();
            hip += V(0, 0, -lowest);
            left = Leg(hip, 1, pose.LeftLeg, null); right = Leg(hip, -1, pose.RightLeg, null);
        }
        Point3D T(double x, double y, double z) => Q(hip.X + x * Math.Cos(lean) + z * Math.Sin(lean), hip.Y + y, hip.Z - x * Math.Sin(lean) + z * Math.Cos(lean));
        Vector3D Lx(double r) => V(Math.Cos(lean) * r, 0, -Math.Sin(lean) * r);
        Vector3D Lz(double r) => V(Math.Sin(lean) * r, 0, Math.Cos(lean) * r);

        bool skirt = pose.Clothes is Clothes.Skirt or Clothes.Dress;
        double legR = skirt ? .82 : 1;
        Geometry LegShape((Point3D Hip, Point3D Knee, Point3D Ankle, Point3D Heel, Point3D Toe) leg) => Union([
            a.Limb((leg.Hip, 8.2 * k * legR * w), (leg.Knee, 5.5 * k * legR), (leg.Ankle, 3.5 * k * legR)),
            a.Rod(leg.Heel, 3.8 * k, leg.Toe, 2.6 * k)]);

        (Point3D P, double R)[] Arm(double side, (double Swing, double Elbow, double Out) arm, Point3D? target)
        {
            var shoulder = T(0, side * 17.5 * w * k, 46.5 * k);
            Point3D elbow, wrist, tip;
            if (target is { } hold)
            {
                elbow = AxoSketch.Reach(shoulder, hold, upper, fore + 4 * k, V(-1, side * .6, -.4));
                var d = hold - elbow; d.Normalize();
                wrist = elbow + d * fore; tip = wrist + d * (hand * .8);
            }
            else
            {
                Vector3D Dir(double swing, double spread) => V(Math.Sin(Rad(swing)) * Math.Cos(Rad(spread)), side * Math.Sin(Rad(spread)), -Math.Cos(Rad(swing)) * Math.Cos(Rad(spread)));
                elbow = shoulder + Dir(arm.Swing, arm.Out) * upper;
                wrist = elbow + Dir(arm.Swing + arm.Elbow, arm.Out * .5) * fore;
                tip = wrist + Dir(arm.Swing + arm.Elbow * 1.3, arm.Out * .4) * hand;
            }
            return [(shoulder, 5.2 * k), (elbow, 4.1 * k), (wrist, 3.0 * k), (tip, 2.3 * k)];
        }
        var leftArm = Arm(1, pose.LeftArm, pose.LeftHand); var rightArm = Arm(-1, pose.RightArm, pose.RightHand);
        if (pose.Phone)
            rightArm = [rightArm[0], (rightArm[0].P + V(4 * k, -3 * k, -27 * k), 4.1 * k), (T(16 * k, -6 * k, 30 * k), 3.0 * k), (T(22 * k, -3 * k, 38 * k), 2.3 * k)];

        // Torso: elliptic sections from the hips to the neck joined into smooth frustums (a rounded
        // pelvis under them), then the clothes' hem.
        Point3D[] Section(double x, double z, double rx, double ry) => Enumerable.Range(0, 20).Select(i =>
        {
            double t = i * Math.PI / 10; return T((x + Math.Cos(t) * rx) * k, Math.Sin(t) * ry * k, z * k);
        }).ToArray();
        var sections = new[] { Section(0, -6, 10, 16 * w), Section(.5, 18, 9.5, 14.5 * w), Section(1.8, 34, 11.5, 17.5 * w), Section(.8, 45, 8.5, 19.5 * w), Section(1.2, 52, 5, 6.5) };
        var torso = new List<Geometry> { a.Ellipsoid(T(0, 0, -4 * k), Lx(10 * k), V(0, 16 * w * k, 0), Lz(9 * k)) };
        for (int i = 1; i < sections.Length; i++) torso.Add(a.Loft(sections[i - 1], sections[i], smooth: true).Outline);
        Geometry? hemEdges = null;
        if (pose.Clothes != Clothes.Jacket && pose.Seat == null)
        {
            double hem = pose.Clothes == Clothes.Coat ? 42 : pose.Clothes == Clothes.Dress ? 32 : 35;
            double front = Math.Max(left.Knee.X, right.Knee.X) + 8 * k, back = Math.Min(left.Knee.X, right.Knee.X) - 8 * k;
            double cx = (front + back) / 2, rx = Math.Max(14 * k, (front - back) / 2), ry = (pose.Clothes == Clothes.Coat ? 18 : 19.5) * k * w;
            var top = Enumerable.Range(0, 20).Select(i => { double t = i * Math.PI / 10; return T(Math.Cos(t) * 10.8 * k, Math.Sin(t) * 15.2 * w * k, 2 * k); }).ToArray();
            var bottom = Enumerable.Range(0, 20).Select(i => { double t = i * Math.PI / 10; return Q(cx + Math.Cos(t) * rx, Math.Sin(t) * ry, hip.Z - hem * k); }).ToArray();
            var flare = a.Loft(bottom, top, smooth: true);
            torso.Add(flare.Outline); hemEdges = flare.Edges;
        }
        var torsoShape = Union(torso);

        // Head: neck, skull, nose and hair, turned and tilted on the neck.
        double hs = pose.HeadScale * k, turn = Rad(pose.HeadTurn), tilt = Rad(pose.HeadTilt) + lean;
        var neck = T(1.5 * k, 0, 53 * k);
        Vector3D H(double x, double y, double z)
        {
            // Tilt (forward) about the side axis, then turn about the vertical.
            double x1 = x * Math.Cos(tilt) + z * Math.Sin(tilt), z1 = -x * Math.Sin(tilt) + z * Math.Cos(tilt);
            return V(x1 * Math.Cos(turn) - y * Math.Sin(turn), x1 * Math.Sin(turn) + y * Math.Cos(turn), z1) * hs;
        }
        var center = neck + H(1, 0, 15.5);
        var head = new List<Geometry>
        {
            a.Rod(neck + H(0, 0, -2), 5.2 * k, neck + H(.5, 0, 7), 4.5 * hs),
            a.Ellipsoid(center, H(9.6, 0, 0), H(0, 8.4, 0), H(0, 0, 11.2)),
            a.Ball(center + H(9.6, 0, -1.4), 1.9 * hs)
        };
        switch (pose.Hair)
        {
            case Hair.Short: head.Add(a.Ellipsoid(center + H(-1.6, 0, 2.6), H(9.8, 0, 0), H(0, 9, 0), H(0, 0, 9.6))); break;
            case Hair.Long:
                head.Add(a.Ellipsoid(center + H(-1.4, 0, 2.6), H(10, 0, 0), H(0, 9.4, 0), H(0, 0, 9.9)));
                head.Add(a.Ellipsoid(center + H(-6, 0, -9), H(5.5, 0, 0), H(0, 9, 0), H(0, 0, 13)));
                break;
            case Hair.Bun:
                head.Add(a.Ellipsoid(center + H(-1.5, 0, 2.6), H(10, 0, 0), H(0, 9.2, 0), H(0, 0, 9.6)));
                head.Add(a.Ball(center + H(-9, 0, 7), 4.4 * hs));
                break;
            case Hair.Cap:
                head.Add(a.Ellipsoid(center + H(-.6, 0, 3.6), H(10, 0, 0), H(0, 9.2, 0), H(0, 0, 9)));
                head.Add(a.Ellipsoid(center + H(8, 0, 5.4), H(8, 0, 0), H(0, 8, 0), H(0, 0, 1.3)));
                break;
        }
        var headShape = Union(head);

        var painter = new AxoPainter(a);
        painter.Add(left.Knee, () => a.S.Over(LegShape(left), .55));
        painter.Add(right.Knee, () => a.S.Over(LegShape(right), .55));
        painter.Add(T(0, 0, 25 * k), () => { a.S.Over(torsoShape, .55); if (hemEdges != null) a.S.Line(hemEdges, .4); });
        painter.Add(leftArm[1].P, () => a.S.Over(a.Limb(leftArm), .6));
        painter.Add(rightArm[1].P, () => a.S.Over(a.Limb(rightArm), .6));
        painter.Add(center, () => a.S.Over(headShape, .6));
        if (pose.Bag)
        {
            var grip = rightArm[^1].P;
            var bag = a.Box(grip.X - 12 * k, grip.Y - 4 * k, grip.Z - 34 * k, grip.X + 12 * k, grip.Y + 4 * k, grip.Z - 6 * k);
            painter.Add(grip + V(0, 0, -20 * k), () => { a.Over(bag, .55, .4); a.S.Over(a.Rod(grip + V(-6 * k, 0, -6 * k), 1, grip, 1), .4); a.S.Over(a.Rod(grip, 1, grip + V(6 * k, 0, -6 * k), 1), .4); });
        }
        if (pose.Phone) painter.Add(rightArm[^1].P + V(0, 0, 1), () => a.S.Over(a.Ellipsoid(rightArm[^1].P + V(-1 * k, 0, 1 * k), V(1.2 * k, 0, 0), V(0, 3.6 * k, 0), V(0, 0, 4.8 * k)), .5));
        painter.Draw();
        a.Pop();
    }

    // A city bicycle facing local +x with its ground point under the middle of the wheelbase.
    readonly record struct AxoBike(Point3D Saddle, Point3D LeftGrip, Point3D RightGrip, Point3D LeftPedal, Point3D RightPedal);

    static AxoBike AxoBicycle(AxoSketch a, Point3D at, double heading, bool rider)
    {
        a.Push(at, heading);
        var rear = Q(-52, 0, 34); var front = Q(54, 0, 34); var crank = Q(-6, 0, 28);
        var seatTop = Q(-22, 0, 80); var head = Q(36, 0, 82); var headLow = Q(40, 0, 68); var bar = Q(42, 0, 95);
        double c25 = Math.Cos(Rad(25)), s25 = Math.Sin(Rad(25));
        // The pedal on the left side is forward and down, the right one back and up.
        var leftPedal = crank + V(c25 * 17, 9, -s25 * 17); var rightPedal = crank + V(-c25 * 17, -9, s25 * 17);
        bool leftNear = a.Faces(V(0, 1, 0));
        var near = leftNear ? leftPedal : rightPedal; var far = leftNear ? rightPedal : leftPedal;
        Geometry Pedal(Point3D p) => Union([a.Rod(crank + V(0, p.Y, 0), 1.6, p, 1.4), a.Rod(p + V(-4.5, 0, 0), 1.5, p + V(4.5, 0, 0), 1.5)]);
        a.S.Body(Pedal(far));
        foreach (var axle in new[] { rear, front })
        {
            a.S.Body(a.Ring(axle, V(0, 1, 0), 34, 30.6), a.Disc(axle, V(0, 1, 0), 3.4));
            for (int i = 0; i < 8; i++)
            {
                double t = i * Math.PI / 4 + .2;
                a.S.Line(a.Segment(axle + V(Math.Cos(t) * 3.4, 0, Math.Sin(t) * 3.4), axle + V(Math.Cos(t) * 30, 0, Math.Sin(t) * 30)), .22);
            }
        }
        a.S.Body(Union([
            a.Rod(crank, 2.2, seatTop, 1.9), a.Rod(seatTop, 1.9, head, 1.9), a.Rod(headLow, 2.1, crank, 2.1), a.Rod(head, 2.2, headLow, 2.2),
            a.Rod(crank, 1.6, rear + V(0, 2, 0), 1.4), a.Rod(seatTop, 1.5, rear + V(0, 2, 0), 1.3), a.Rod(headLow, 1.8, front, 1.4),
            a.Rod(seatTop, 1.4, Q(-24, 0, 88), 1.4), a.Rod(head, 1.6, Q(38, 0, 94), 1.5), a.Disc(crank, V(0, 1, 0), 5.5)
        ]));
        a.S.Over(Union([a.Rod(bar + V(-2, -24, 0), 1.6, bar + V(-2, 24, 0), 1.6), a.Ellipsoid(Q(-25, 0, 90), 10, 5, 2.6)]), .45);
        if (!rider) a.S.Over(Pedal(near), .4);
        a.S.Line(a.CircleArc(front, V(0, 1, 0), 37, -100, -10), .45);
        a.Pop();
        // In the bicycle's own frame (a rider shares its ground point and heading).
        return new(Q(-25, 0, 92), bar + V(-2, 22, 0), bar + V(-2, -22, 0), leftPedal, rightPedal);
    }

    // ---- People -----------------------------------------------------------------------------

    static void AxoStanding(AxoSketch a, int variant)
    {
        a.S.ModelHeight = 170;
        var pose = variant switch
        {
            1 => new AxoPose { Heading = 70, Hair = Hair.Long, Clothes = Clothes.Dress, Build = .94, LeftArm = (-2, 6, 6), RightArm = (2, 8, 6), LeftLeg = (3, 0, 0), RightLeg = (-3, 2, 0) },
            2 => new AxoPose { Heading = -60, Hair = Hair.Cap, Clothes = Clothes.Coat, Phone = true, HeadTilt = 20, LeftArm = (-4, 10, 6), LeftLeg = (4, 0, 0), RightLeg = (-3, 2, 0) },
            3 => new AxoPose { Heading = 200, Hair = Hair.Bun, Clothes = Clothes.Skirt, Build = .94, Bag = true, LeftArm = (-3, 8, 6), RightArm = (4, 6, 8), LeftLeg = (3, 0, 0), RightLeg = (-2, 2, 0) },
            _ => new AxoPose { Heading = 25, Hair = Hair.Short, Clothes = Clothes.Jacket, LeftArm = (-4, 10, 7), RightArm = (5, 12, 7), LeftLeg = (4, 0, 0), RightLeg = (-3, 2, 0) }
        };
        AxoFigure(a, pose, Q(0, 0, 0));
    }

    static void AxoWalking(AxoSketch a, int variant, bool bag)
    {
        a.S.ModelHeight = 170;
        double heading = variant switch { 1 => -90, 2 => 180, 3 => 90, _ => 0 };
        var pose = AxoWalk(variant % 3) with { Heading = heading };
        if (bag) pose = pose with { Bag = true, RightArm = (4, 6, 9) };
        AxoFigure(a, pose, Q(0, 0, 0));
    }

    static void AxoSitting(AxoSketch a, int variant)
    {
        a.S.ModelHeight = 130;
        // Two people facing the first plan axis (a flip turns them to the other) and one seen from behind.
        double heading = variant == 2 ? 180 : 0;
        var pose = new AxoPose
        {
            Heading = heading, Seat = Q(-2, 0, 50), LeftFoot = Q(40, 11, 0), RightFoot = Q(37, -11, 0), Lean = variant == 1 ? 8 : -4,
            Hair = variant switch { 1 => Hair.Long, 2 => Hair.Cap, _ => Hair.Short }, Clothes = Clothes.Jacket, Build = variant == 1 ? .94 : 1,
            LeftArm = (30, 40, 10), RightArm = (26, 46, 10)
        };
        AxoFigure(a, pose, Q(0, 0, 0));
    }

    static void AxoPair(AxoSketch a, int variant)
    {
        a.S.ModelHeight = 170;
        // Two people talking about a metre apart; the second variant is a parent and a child walking together.
        var painter = new AxoPainter(a);
        if (variant == 1)
        {
            painter.Add(Q(0, 30, 80), () => AxoFigure(a, AxoWalk(1) with { Heading = 0 }, Q(0, 30, 0)));
            painter.Add(Q(5, -28, 60), () => AxoFigure(a, AxoWalk(0) with { Heading = 0, Scale = .65, HeadScale = .82, Hair = Hair.Cap, RightArm = (10, 20, 6) }, Q(5, -28, 0)));
        }
        else
        {
            painter.Add(Q(-45, 15, 80), () => AxoFigure(a, new AxoPose { Heading = -15, Hair = Hair.Short, Clothes = Clothes.Jacket, LeftArm = (-3, 10, 7), RightArm = (22, 60, 8), LeftLeg = (4, 0, 0), RightLeg = (-3, 2, 0) }, Q(-45, 15, 0)));
            painter.Add(Q(45, -15, 80), () => AxoFigure(a, new AxoPose { Heading = 165, Hair = Hair.Long, Clothes = Clothes.Coat, Build = .94, Bag = true, LeftArm = (-2, 8, 6), RightArm = (3, 6, 8), LeftLeg = (3, 0, 0), RightLeg = (-2, 2, 0) }, Q(45, -15, 0)));
        }
        painter.Draw();
    }

    static void AxoCyclist(AxoSketch a, int variant)
    {
        a.S.ModelHeight = 165;
        double heading = variant == 1 ? -90 : 0;
        var bike = AxoBicycle(a, Q(0, 0, 0), heading, rider: true);
        AxoFigure(a, new AxoPose
        {
            Heading = heading, Seat = bike.Saddle + V(0, 0, 6), LeftFoot = bike.LeftPedal + V(4, 0, 2), RightFoot = bike.RightPedal + V(4, 0, 2),
            LeftHand = bike.LeftGrip, RightHand = bike.RightGrip, Lean = 30, HeadTilt = -18, Hair = variant == 1 ? Hair.Long : Hair.Cap, Clothes = Clothes.Jacket
        }, Q(0, 0, 0));
    }

    // ---- Trees and plants ----------------------------------------------------------------

    // A cloud of leaf masses around an ellipsoid; arcs under the masses nearest the viewer.
    static void AxoCanopy(AxoSketch a, ref EntourageRandom random, Point3D center, double r, double rz, int masses, double massSize, bool arcs = true, double floor = double.MinValue)
    {
        var parts = new List<Geometry> { a.Ellipsoid(center, r * .8, r * .8, rz * .8) };
        var placed = new List<(Point3D C, double R)>();
        double golden = Math.PI * (3 - Math.Sqrt(5)), phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < masses; i++)
        {
            double z = 1 - 2 * (i + .5) / masses, ring = Math.Sqrt(1 - z * z), t = phase + i * golden + random.Range(-.2, .2);
            double m = massSize * Math.Min(r, rz) * random.Range(.8, 1.18);
            var c = center + V(Math.Cos(t) * ring * (r - m * .7), Math.Sin(t) * ring * (r - m * .7), z * (rz - m * .7));
            if (c.Z - m < floor) c.Z = floor + m;
            parts.Add(a.Ball(c, m)); placed.Add((c, m));
        }
        a.S.Body(Union(parts));
        if (!arcs) return;
        // Shading: the lower edge of the masses facing the viewer, pulled toward the middle.
        foreach (var (c, m) in placed.OrderByDescending(p => a.Depth(p.C)).Take(Math.Max(3, masses / 3)))
        {
            var inward = center - c; var p = c + inward * random.Range(.3, .5);
            double start = random.Range(15, 40), sweep = random.Range(85, 120);
            a.S.Line(a.SphereArc(p, m * random.Range(.5, .72), start, start + sweep), .45);
        }
    }

    // The canopy's drip line on the ground: an ellipse in isometric and dimetric, a circle in plan oblique.
    static void AxoDripLine(AxoSketch a, double radius) => a.S.Line(a.Disc(Q(0, 0, 0), V(0, 0, 1), radius), .3);

    static void AxoRoundTree(AxoSketch a, EntourageRandom random, int variant, bool oval)
    {
        double height = oval ? 1000 : 800; a.S.ModelHeight = height;
        double crownBottom = oval ? 270 : 300, rz = (height - crownBottom) / 2, r = oval ? rz * random.Range(.46, .54) : rz * random.Range(1.02, 1.12);
        double lean = random.Range(-14, 14), turn = random.Range(0, Math.PI * 2);
        var top = Q(Math.Cos(turn) * lean * 1.5, Math.Sin(turn) * lean * 1.5, (height + crownBottom) / 2);
        var fork = Q(Math.Cos(turn) * lean * .8, Math.Sin(turn) * lean * .8, crownBottom + (oval ? 20 : 10));
        AxoDripLine(a, r * .95);
        a.Body(a.Cylinder(Q(0, 0, 0), 18, fork, 11));
        int limbs = 3 + random.Below(2); double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < limbs; i++)
        {
            double t = phase + i * Math.PI * 2 / limbs + random.Range(-.3, .3), rise = oval ? .9 : .7;
            var end = top + V(Math.Cos(t) * r * .62, Math.Sin(t) * r * .62, -rz * (1 - rise));
            a.S.Body(a.Rod(fork, 9, end, 3.5));
            if (i % 2 == 0) a.S.Line(a.Curve(false, fork, Q((fork.X + end.X) / 2 + random.Range(-15, 15), (fork.Y + end.Y) / 2, (fork.Z + end.Z) / 2 + 10), end), .4);
        }
        AxoCanopy(a, ref random, top, r, rz, oval ? 16 : 20 + variant, oval ? .3 : .26);
    }

    static void AxoBranchTree(AxoSketch a, EntourageRandom random)
    {
        a.S.ModelHeight = 900;
        var tips = new List<(Point3D P, double Size)>();
        void Grow(Point3D from, Vector3D direction, double length, double radius, int depth, ref EntourageRandom r)
        {
            direction.Normalize();
            var to = from + direction * length;
            a.S.Body(a.Rod(from, radius, to, radius * .68));
            if (depth == 0) { tips.Add((to, length)); return; }
            for (int i = 0; i < 2; i++)
            {
                // Fork in a plane turned about the branch.
                double spread = (i - .5) * r.Range(.6, .85), roll = r.Range(0, Math.PI * 2);
                var side = Vector3D.CrossProduct(direction, V(Math.Cos(roll), Math.Sin(roll), 0)); if (side.LengthSquared < 1e-9) side = V(1, 0, 0); side.Normalize();
                var next = direction * Math.Cos(spread) + side * Math.Sin(spread) + V(0, 0, .12);
                Grow(to, next, length * r.Range(.66, .8), radius * .68, depth - 1, ref r);
            }
        }
        var fork = Q(random.Range(-12, 12), random.Range(-12, 12), 250);
        a.Body(a.Cylinder(Q(0, 0, 0), 17, fork, 11));
        double phase = random.Range(0, Math.PI * 2);
        for (int i = 0; i < 4; i++)
        {
            double t = phase + i * Math.PI / 2 + random.Range(-.25, .25), up = random.Range(.9, 1.4);
            Grow(fork, V(Math.Cos(t), Math.Sin(t), up), random.Range(170, 200), 9, 2, ref random);
        }
        AxoDripLine(a, 330);
        foreach (var (p, size) in tips.OrderBy(t => a.Depth(t.P)))
        {
            double r = Math.Clamp(size * random.Range(.75, 1.0), 55, 95);
            var cluster = new List<Geometry>(); int n = 6 + random.Below(3);
            for (int i = 0; i < n; i++)
            {
                double t = i * Math.PI * 2 / n + random.Range(-.3, .3), d = r * random.Range(.35, .55);
                cluster.Add(a.Ball(p + V(Math.Cos(t) * d, Math.Sin(t) * d, r * random.Range(-.1, .35)), r * random.Range(.36, .5)));
            }
            a.S.Body(Union(cluster));
            a.S.Line(a.SphereArc(p + V(0, 0, r * .2), r * .42, random.Range(10, 30), random.Range(110, 150)), .4);
        }
    }

    static void AxoConifer(AxoSketch a, EntourageRandom random)
    {
        // No drip line: the lowest tier's hem already shows the crown's footprint.
        double height = 1100 * random.Range(.94, 1.04), baseWidth = 230 * random.Range(.9, 1.1), trunk = 130;
        a.S.ModelHeight = height;
        a.Body(a.Cylinder(Q(0, 0, 0), 13, Q(0, 0, trunk + 60), 9));
        int tiers = 7 + random.Below(2);
        var shapes = new List<Geometry>(); var rims = new List<(double Z, double R)>();
        for (int i = 0; i < tiers; i++)
        {
            // Tier i from the top: a cone from its drooping hem up into the tier above.
            double u = (i + 1) / (double)tiers;
            double hemZ = height - (height - trunk) * u - 12, hemR = baseWidth * Math.Pow(u, .92) * random.Range(.9, 1.08);
            double apexZ = i == 0 ? height : height - (height - trunk) * (i / (double)tiers) + (height - trunk) / tiers * .45;
            var cone = a.Cylinder(Q(random.Range(-4, 4), random.Range(-4, 4), hemZ), hemR, Q(0, 0, apexZ), .5, 28);
            shapes.Add(cone.Outline);
            rims.Add((hemZ, hemR));
        }
        a.S.Body(Union(shapes));
        // The front of each hem shows across the tier below.
        foreach (var (z, r) in rims.Take(rims.Count - 1)) a.S.Line(a.NearArc(Q(0, 0, z), V(0, 0, 1), r * .97, .8), .4);
        a.S.Line(a.Segment(Q(0, 0, height * .9), Q(0, 0, trunk + 40)), .35);
    }

    static void AxoPalm(AxoSketch a, EntourageRandom random)
    {
        double height = 900 * random.Range(.94, 1.04), bend = random.Range(30, 70), azimuth = random.Range(0, Math.PI * 2);
        a.S.ModelHeight = height;
        var spine = new List<Point3D>();
        for (int i = 0; i <= 12; i++) { double t = i / 12.0; spine.Add(Q(Math.Cos(azimuth) * bend * t * t, Math.Sin(azimuth) * bend * t * t, (height - 150) * t)); }
        var crown = spine[^1];
        AxoDripLine(a, 230);
        var trunk = new List<Geometry>();
        for (int i = 1; i < spine.Count; i++) trunk.Add(a.Rod(spine[i - 1], 19 - 8 * (i - 1) / 12.0 + (i == 1 ? 5 : 0), spine[i], 19 - 8 * i / 12.0));
        a.S.Body(Union(trunk));
        for (int i = 1; i < spine.Count - 1; i++)
        {
            var d = spine[i + 1] - spine[i - 1];
            a.S.Line(a.NearArc(spine[i], d, (19 - 8 * i / 12.0) * .95, .55), .35);
        }
        int fronds = 10 + random.Below(3);
        var leaves = new List<Geometry> { a.Ball(crown, 24) };
        var ribs = new List<(double Depth, Geometry Rib)>();
        for (int i = 0; i < fronds; i++)
        {
            double t0 = i * Math.PI * 2 / fronds + random.Range(-.12, .12), length = random.Range(220, 280), rise = random.Range(.25, .6), droop = random.Range(.8, 1.15);
            var direction = V(Math.Cos(t0), Math.Sin(t0), 0); var across = V(-Math.Sin(t0), Math.Cos(t0), 0);
            var rib = new List<Point3D>();
            for (int j = 0; j <= 10; j++) { double t = j / 10.0; rib.Add(crown + direction * (length * t) + V(0, 0, length * (rise * t - droop * .6 * t * t))); }
            var edge = new List<Point>(); var back = new List<Point>();
            for (int j = 0; j < rib.Count; j++)
            {
                double t = j / 10.0, width = 26 * Math.Sin(Math.PI * Math.Min(1, t * 1.05)) + 2, tooth = j % 2 == 0 ? 1 : .55;
                // Leaflets hang a little below the rib on both sides.
                edge.Add(a.At(rib[j] + across * (width * tooth) + V(0, 0, -width * .25)));
                back.Add(a.At(rib[j] - across * (width * tooth * .9) + V(0, 0, -width * .25)));
            }
            edge.AddRange(Enumerable.Reverse(back));
            leaves.Add(Poly(true, edge.ToArray()));
            ribs.Add((a.Depth(rib[5]), a.Curve(false, rib.ToArray())));
        }
        a.S.Body(Union(leaves));
        foreach (var (_, rib) in ribs.OrderBy(r => r.Depth)) a.S.Line(rib, .4);
    }

    static void AxoShrub(AxoSketch a, EntourageRandom random)
    {
        double width = 190 * random.Range(.9, 1.1), height = 120 * random.Range(.92, 1.08);
        a.S.ModelHeight = height;
        AxoCanopy(a, ref random, Q(0, 0, height * .5), width * .5, height * .5, 14 + random.Below(4), .34, floor: 0);
    }

    static void AxoHedge(AxoSketch a, EntourageRandom random)
    {
        // A clipped hedge 3 m long: a box with a soft, leafy top edge.
        double length = 300, depth = 70, height = 120;
        a.S.ModelHeight = height;
        var box = a.Box(-length / 2, -depth / 2, 0, length / 2, depth / 2, height - 10);
        var parts = new List<Geometry> { box.Outline };
        for (int i = 0; i <= 12; i++)
            foreach (double y in new[] { -depth / 2 + 12, depth / 2 - 12 })
                parts.Add(a.Ball(Q(-length / 2 + 12 + i * (length - 24) / 12 + random.Range(-4, 4), y + random.Range(-3, 3), height - 16 + random.Range(-3, 4)), 14 + random.Range(-2, 3)));
        a.S.Body(Union(parts));
        a.S.Line(box.Edges, .4);
        // Leaf texture: short arcs scattered over the two visible faces.
        for (int i = 0; i < 26; i++)
        {
            bool side = i % 2 == 0;
            var p = side ? Q(random.Range(-length / 2 + 15, length / 2 - 15), a.Faces(V(0, 1, 0)) ? depth / 2 : -depth / 2, random.Range(15, height - 30))
                : Q(random.Range(-length / 2 + 15, length / 2 - 15), random.Range(-depth / 2 + 10, depth / 2 - 10), height - 10);
            a.S.Line(a.SphereArc(p, random.Range(5, 9), 10, 150), .35);
        }
    }

    // ---- Vehicles ---------------------------------------------------------------------------

    static void AxoCar(AxoSketch a, bool hatch, int variant)
    {
        a.S.ModelHeight = hatch ? 148 : 142;
        a.Push(Q(0, 0, 0), variant == 1 ? 180 : 0);
        double half = hatch ? 200 : 235, width = 89, cabinWidth = 77, wheelR = hatch ? 29 : 31;
        double frontWheel = hatch ? 125 : 132, rearWheel = hatch ? -122 : -146;
        Point[] lower = hatch
            ? [P(-198, 28), P(-160, 28), P(160, 28), P(198, 28), P(202, 58), P(195, 82), P(160, 91), P(-182, 96), P(-198, 92), P(-202, 60)]
            : [P(-233, 28), P(-190, 28), P(190, 28), P(233, 28), P(236, 58), P(228, 82), P(190, 91), P(-200, 97), P(-228, 90), P(-236, 60)];
        Point[] cabin = hatch ? [P(-196, 95), P(72, 91), P(20, 146), P(-178, 148)] : [P(-176, 95), P(98, 92), P(38, 140), P(-118, 143)];
        // The body's corners are drawn in at the bumpers and the cabin's sides lean in toward the roof.
        Point3D[] BodySide(double side) => lower.Select((p, i) => Q(p.X, side * (Math.Abs(p.X) > half - 12 ? width - 6 : width), p.Y)).ToArray();
        Point3D[] CabinSide(double side) => cabin.Select(p => Q(p.X, side * (p.Y > 120 ? cabinWidth - 9 : cabinWidth), p.Y)).ToArray();
        bool leftNear = a.Faces(V(0, 1, 0));
        double nearY = leftNear ? 1 : -1;
        AxoSketch.Solid Wheel(double x, double side) => a.Cylinder(Q(x, side * (width - 22), wheelR), wheelR, Q(x, side * (width + 1), wheelR), wheelR, 36);
        // Far wheels, the body, the cabin, then the near wheels over the body side.
        foreach (double x in new[] { rearWheel, frontWheel }) a.Body(Wheel(x, -nearY), .4);
        a.Over(a.Loft(BodySide(-1), BodySide(1)), .55, .4);
        a.Over(a.Loft(CabinSide(-1), CabinSide(1)), .55, .45);
        double sideY = nearY * width;
        Point3D S(double x, double z) => Q(x, sideY, z);
        // Windows: the side glass, the windscreen and the rear window (each on a face turned to the viewer).
        Point3D C(double x, double z) => Q(x, nearY * (cabinWidth - Math.Clamp((z - 95) / 45, 0, 1) * 8), z);
        Point[] glass = hatch ? [P(-184, 99), P(62, 95), P(18, 140), P(-170, 142)] : [P(-162, 99), P(86, 96), P(36, 135), P(-112, 138)];
        a.S.Line(a.Polyline(true, glass.Select(p => C(p.X, p.Y)).ToArray()), .5);
        double pillar = hatch ? -40 : -30;
        a.S.Line(a.Segment(C(pillar, 97), C(pillar + 2, 140)), .5);
        var screen = hatch ? (P(72, 91), P(20, 146)) : (P(98, 92), P(38, 140));
        var screenNormal = V(screen.Item2.Y - screen.Item1.Y, 0, screen.Item1.X - screen.Item2.X);
        if (a.Faces(screenNormal))
        {
            Point3D On(double t, double y) => Q(screen.Item1.X + (screen.Item2.X - screen.Item1.X) * t, y, screen.Item1.Y + (screen.Item2.Y - screen.Item1.Y) * t);
            a.S.Line(a.Polyline(true, On(.1, -cabinWidth + 8), On(.1, cabinWidth - 8), On(.9, cabinWidth - 12), On(.9, -cabinWidth + 12)), .5);
        }
        var rearFace = hatch ? (P(-178, 148), P(-196, 95)) : (P(-118, 143), P(-176, 95));
        var rearNormal = V(rearFace.Item2.Y - rearFace.Item1.Y, 0, rearFace.Item1.X - rearFace.Item2.X);
        if (a.Faces(rearNormal))
        {
            Point3D On(double t, double y) => Q(rearFace.Item1.X + (rearFace.Item2.X - rearFace.Item1.X) * t, y, rearFace.Item1.Y + (rearFace.Item2.Y - rearFace.Item1.Y) * t);
            a.S.Line(a.Polyline(true, On(.1, -cabinWidth + 10), On(.1, cabinWidth - 10), On(.85, cabinWidth - 8), On(.85, -cabinWidth + 8)), .5);
        }
        // Body side: door seams, the waist line, wheel arches and the handles.
        a.S.Line(a.Segment(S(pillar + 4, 94), S(pillar, 34)), .4);
        if (!hatch) a.S.Line(a.Segment(S(frontWheel - wheelR - 18, 92), S(frontWheel - wheelR - 20, 36)), .4);
        a.S.Line(a.Curve(false, S(-half + 6, 70), S(0, 74), S(half - 8, 68)), .35);
        foreach (double x in new[] { rearWheel, frontWheel }) a.S.Line(a.CircleArc(S(x, wheelR), V(0, nearY, 0), wheelR + 6, -105, 105), .45);
        a.S.Line(a.Segment(S(pillar + 14, 86), S(pillar + 26, 86)), .4);
        // Lights on the front and the back face.
        if (a.Faces(V(1, 0, 0))) foreach (double y in new[] { -1.0, 1.0 }) a.S.Line(a.Polyline(true, Q(half, y * 62, 72), Q(half, y * 80, 70), Q(half - 6, y * 80, 80), Q(half - 6, y * 62, 81)), .45);
        if (a.Faces(V(1, 0, 0))) a.S.Line(a.Segment(Q(half + 1, -40, 52), Q(half + 1, 40, 52)), .4);
        if (a.Faces(V(-1, 0, 0))) foreach (double y in new[] { -1.0, 1.0 }) a.S.Line(a.Polyline(true, Q(-half - 1, y * 58, 74), Q(-half - 1, y * 82, 74), Q(-half - 1, y * 82, 84), Q(-half - 1, y * 58, 84)), .45);
        if (a.Faces(V(-1, 0, 0))) a.S.Line(a.Polyline(true, Q(-half - 2, -26, 46), Q(-half - 2, 26, 46), Q(-half - 2, 26, 58), Q(-half - 2, -26, 58)), .35);
        // The mirror on the near side.
        a.Over(a.Box(screen.Item1.X - 26, nearY * cabinWidth, 96, screen.Item1.X - 12, nearY * (cabinWidth + 16), 106), .45, .35);
        foreach (double x in new[] { rearWheel, frontWheel })
        {
            var wheel = Wheel(x, nearY);
            a.Over(wheel, .55, .4);
            var hub = Q(x, nearY * (width + 1), wheelR);
            a.S.Line(a.Disc(hub, V(0, nearY, 0), wheelR * .62), .45); a.S.Line(a.Disc(hub, V(0, nearY, 0), wheelR * .2), .4);
        }
        a.Pop();
    }

    // ---- Street furniture ------------------------------------------------------------------

    static void AxoBench(AxoSketch a)
    {
        a.S.ModelHeight = 86;
        var painter = new AxoPainter(a);
        // Two cast frames, four seat slats and two back slats; the seat faces local +y.
        foreach (double x in new[] { -72.0, 72.0 })
        {
            painter.Add(Q(x, 0, 20), () => a.Over(a.Box(x - 3, -20, 0, x + 3, 20, 42), .5, .35));
            painter.Add(Q(x, -24, 64), () => a.Over(a.Loft([Q(x - 3, -24, 42), Q(x + 3, -24, 42), Q(x + 3, -19, 42), Q(x - 3, -19, 42)], [Q(x - 3, -30, 86), Q(x + 3, -30, 86), Q(x + 3, -25, 86), Q(x - 3, -25, 86)]), .5, .35));
        }
        for (int i = 0; i < 4; i++)
        {
            double y0 = -22 + i * 11.5;
            painter.Add(Q(0, y0 + 4.5, 44), () => a.Over(a.Box(-92, y0, 42, 92, y0 + 9.5, 46), .5, .35));
        }
        foreach (double z in new[] { 54.0, 70.0 })
        {
            double y = -24 - (z - 42) / 44 * 6;
            painter.Add(Q(0, y - 2, z + 4), () => a.Over(a.Loft([Q(-92, y - 3, z), Q(92, y - 3, z), Q(92, y + 1, z), Q(-92, y + 1, z)], [Q(-92, y - 4.5, z + 9), Q(92, y - 4.5, z + 9), Q(92, y - .5, z + 9), Q(-92, y - .5, z + 9)]), .5, .35));
        }
        painter.Draw();
    }

    static void AxoLamp(AxoSketch a, int variant)
    {
        a.S.ModelHeight = variant == 1 ? 492 : 470;
        a.Body(a.Cylinder(Q(0, 0, 0), 14, Q(0, 0, 8), 11, 24), .4);
        a.Over(a.Cylinder(Q(0, 0, 8), 7.5, Q(0, 0, 440), 4.4, 24), .5, .4);
        if (variant == 1)
        {
            a.Over(a.Prism([P(-16, -16), P(16, -16), P(16, 16), P(-16, 16)], 440, 470, 1.35), .5, .4);
            a.Over(a.Prism([P(-24, -24), P(24, -24), P(24, 24), P(-24, 24)], 470, 492, .05), .5, .4);
            return;
        }
        var arm = new[] { Q(0, 0, 430), Q(10, 0, 456), Q(40, 0, 466), Q(70, 0, 462) };
        a.S.Over(Union([a.Rod(arm[0], 3, arm[1], 3), a.Rod(arm[1], 3, arm[2], 3), a.Rod(arm[2], 3, arm[3], 3)]), .45);
        a.Over(a.Loft([Q(54, -11, 452), Q(104, -11, 452), Q(104, 11, 452), Q(54, 11, 452)], [Q(58, -9, 466), Q(98, -9, 466), Q(98, 9, 466), Q(58, 9, 466)]), .5, .4);
    }

    static void AxoParasolTable(AxoSketch a, int variant)
    {
        a.S.ModelHeight = 252;
        var painter = new AxoPainter(a);
        int chairs = variant == 1 ? 4 : 2;
        for (int i = 0; i < chairs; i++)
        {
            double t = (variant == 1 ? 45 : 0) + i * 360.0 / chairs;
            var at = Q(Math.Cos(Rad(t)) * 82, Math.Sin(Rad(t)) * 82, 0);
            painter.Add(at + V(0, 0, 40), () =>
            {
                a.Push(at, t + 180);
                // A café chair facing the table: legs, seat and back.
                var legs = new List<Geometry>();
                foreach (var (x, y) in new[] { (-18.0, -18.0), (18.0, -18.0), (18.0, 18.0), (-18.0, 18.0) }) legs.Add(a.Rod(Q(x, y, 0), 1.6, Q(x * .9, y * .9, 44), 1.8));
                a.S.Over(Union(legs), .4);
                a.Over(a.Box(-21, -21, 42, 21, 21, 46), .5, .35);
                a.Over(a.Loft([Q(-22, -20, 46), Q(-19, -20, 46), Q(-19, 20, 46), Q(-22, 20, 46)], [Q(-26, -20, 88), Q(-23, -20, 88), Q(-23, 20, 88), Q(-26, 20, 88)]), .5, .35);
                a.Pop();
            });
        }
        painter.Add(Q(0, 0, 60), () =>
        {
            a.Body(a.Cylinder(Q(0, 0, 0), 24, Q(0, 0, 4), 22, 32), .35);
            a.Over(a.Cylinder(Q(0, 0, 4), 3, Q(0, 0, 72), 3, 16), .45, .35);
            a.Over(a.Cylinder(Q(0, 0, 72), 48, Q(0, 0, 76), 48, 48), .5, .4);
            a.Over(a.Cylinder(Q(0, 0, 76), 2.2, Q(0, 0, 236), 2, 12), .4, .3);
        });
        painter.Draw();
        // The canopy: an eight-sided cloth roof whose ribs show as edges.
        var rim = Enumerable.Range(0, 8).Select(i => Q(Math.Cos(i * Math.PI / 4 + Math.PI / 8) * 140, Math.Sin(i * Math.PI / 4 + Math.PI / 8) * 140, 200)).ToArray();
        var apex = Enumerable.Range(0, 8).Select(i => Q(Math.Cos(i * Math.PI / 4 + Math.PI / 8) * .6, Math.Sin(i * Math.PI / 4 + Math.PI / 8) * .6, 252)).ToArray();
        a.Over(a.Loft(rim, apex), .55, .35);
    }

    // ---- Items --------------------------------------------------------------------------

    static void AddAxonometric(List<EntourageItem> list)
    {
        string axo = K("액소노메트릭", "아이소메트릭", "등각", "투상", " axonometric isometric axo iso");
        string people = K("사람", " person people human");
        list.Add(Axo("person.axo-standing", "서 있는 사람 · 액소", EntourageCategory.People, 1.70, 4, people + axo + K("서 있는", " standing"), .35, (a, _, v) => AxoStanding(a, v)));
        list.Add(Axo("person.axo-walking", "걷는 사람 · 액소", EntourageCategory.People, 1.70, 4, people + axo + K("보행", "걷는", " walking pedestrian"), .4, (a, _, v) => AxoWalking(a, v, false)));
        list.Add(Axo("person.axo-bag", "가방을 든 사람 · 액소", EntourageCategory.People, 1.70, 3, people + axo + K("가방", "쇼핑", " bag shopping"), .4, (a, _, v) => AxoWalking(a, v, true)));
        list.Add(Axo("person.axo-sitting", "앉은 사람 · 액소", EntourageCategory.People, 1.30, 3, people + axo + K("앉은", "벤치", " sitting seated"), .55, (a, _, v) => AxoSitting(a, v)));
        list.Add(Axo("person.axo-pair", "두 사람 · 액소", EntourageCategory.People, 1.70, 2, people + axo + K("두 사람", "대화", "아이", " pair group talking child"), .8, (a, _, v) => AxoPair(a, v)));
        list.Add(Axo("person.axo-cyclist", "자전거 타는 사람 · 액소", EntourageCategory.People, 1.65, 2, people + axo + K("자전거", " cyclist bicycle rider"), 1.1, (a, _, v) => AxoCyclist(a, v)));
        string tree = K("나무", "수목", "조경", " tree trees landscape");
        list.Add(Axo("tree.axo-round", "둥근 활엽수 · 액소", EntourageCategory.Plants, 8, 4, tree + axo + K("활엽수", "가로수", " deciduous round"), .7, (a, r, v) => AxoRoundTree(a, r, v, false)));
        list.Add(Axo("tree.axo-oval", "키 큰 활엽수 · 액소", EntourageCategory.Plants, 10, 3, tree + axo + K("활엽수", " tall columnar oval"), .35, (a, r, v) => AxoRoundTree(a, r, v, true)));
        list.Add(Axo("tree.axo-branches", "가지가 보이는 나무 · 액소", EntourageCategory.Plants, 9, 3, tree + axo + K("가지", " airy branches"), .7, (a, r, _) => AxoBranchTree(a, r)));
        list.Add(Axo("tree.axo-conifer", "침엽수 · 액소", EntourageCategory.Plants, 11, 3, tree + axo + K("소나무", "상록수", " conifer pine evergreen"), .4, (a, r, _) => AxoConifer(a, r)));
        list.Add(Axo("tree.axo-palm", "야자수 · 액소", EntourageCategory.Plants, 9, 3, tree + axo + K("야자", " palm tropical"), .5, (a, r, _) => AxoPalm(a, r)));
        list.Add(Axo("plant.axo-shrub", "관목 · 액소", EntourageCategory.Plants, 1.2, 3, K("관목", "식물", " shrub bush plant") + axo, 1.6, (a, r, _) => AxoShrub(a, r)));
        list.Add(Axo("plant.axo-hedge", "생울타리 · 액소", EntourageCategory.Plants, 1.2, 2, K("생울타리", "관목", " hedge shrub") + axo, 2.6, (a, r, _) => AxoHedge(a, r)));
        string car = K("자동차", "승용차", " car vehicle");
        list.Add(Axo("car.axo-sedan", "승용차 · 액소", EntourageCategory.Vehicles, 1.42, 2, car + axo + K("세단", " sedan"), 3.4, (a, _, v) => AxoCar(a, false, v)));
        list.Add(Axo("car.axo-hatch", "소형차 · 액소", EntourageCategory.Vehicles, 1.48, 2, car + axo + K("소형차", "해치백", " hatchback small"), 2.8, (a, _, v) => AxoCar(a, true, v)));
        list.Add(Axo("bike.axo", "자전거 · 액소", EntourageCategory.Vehicles, 1.0, 2, K("자전거", " bicycle bike cycle") + axo, 1.8, (a, _, v) =>
        {
            a.S.ModelHeight = 100; AxoBicycle(a, Q(0, 0, 0), v == 1 ? -90 : 0, rider: false);
        }));
        list.Add(Axo("bench.axo", "벤치 · 액소", EntourageCategory.Props, .86, 1, K("벤치", "의자", " bench seat furniture") + axo, 2.2, (a, _, _) => AxoBench(a)));
        list.Add(Axo("lamp.axo-street", "가로등 · 액소", EntourageCategory.Props, 4.7, 2, K("가로등", "조명", " street lamp light pole") + axo, .4, (a, _, v) => AxoLamp(a, v)));
        list.Add(Axo("table.axo-parasol", "파라솔 테이블 · 액소", EntourageCategory.Props, 2.52, 2, K("파라솔", "테이블", "의자", "카페", " parasol umbrella table cafe") + axo, 1.2, (a, _, v) => AxoParasolTable(a, v)));
    }
}
