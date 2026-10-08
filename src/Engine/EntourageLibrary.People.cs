using System.Windows;
using System.Windows.Media;
using static Compositor.Windows.EntourageShapes;

namespace Compositor.Windows;

// People, drawn from a simple skeleton: joint angles place tapered limbs, the clothes and the head,
// and the parts are unioned into one silhouette. Limbs nearer the viewer are drawn over the others
// with a thinner line where they cross. Proportions follow an adult of 170 cm (head about one eighth
// of the height); a child scales the body and less so the head.
public static partial class EntourageLibrary
{
    enum Hair { Short, Long, Bun, Cap }
    enum Clothes { Jacket, Coat, Skirt, Dress }

    // Side view facing +x. Angles in degrees from straight down; positive turns forward.
    sealed record SidePose
    {
        public double Scale { get; init; } = 1;
        public double HeadScale { get; init; } = 1;
        public double Lean { get; init; }
        public double HeadTilt { get; init; }
        public (double Shoulder, double Elbow) NearArm { get; init; } = (-4, 8);
        public (double Shoulder, double Elbow) FarArm { get; init; } = (4, 8);
        public (double Hip, double Knee, double Foot) NearLeg { get; init; } = (3, 0, 0);
        public (double Hip, double Knee, double Foot) FarLeg { get; init; } = (-2, 0, 0);
        public Hair Hair { get; init; }
        public Clothes Clothes { get; init; }
        public double Build { get; init; } = 1;
        // Hands reaching fixed points (stroller handle, handlebars), in the ground frame before fitting.
        public Point? NearHand { get; init; }
        public Point? FarHand { get; init; }
        // Seated: hip at this point and legs to the feet/pedals given.
        public Point? Hip { get; init; }
        public Point? NearFoot { get; init; }
        public Point? FarFoot { get; init; }
        public bool Bag { get; init; }
        public bool Phone { get; init; }
    }

    // Two-bone reach: the middle joint between root and target, bending toward `bend` (+1 forward of the line, -1 behind).
    static Point Reach(Point root, Point target, double first, double second, int bend)
    {
        var d = target - root; double length = Math.Clamp(d.Length, Math.Abs(first - second) + .01, first + second - .01);
        var direction = d; direction.Normalize();
        double a = (first * first - second * second + length * length) / (2 * length), h = Math.Sqrt(Math.Max(0, first * first - a * a));
        var normal = new Vector(-direction.Y, direction.X) * bend;
        return root + direction * a + normal * h;
    }

    // Draws a side figure standing on y = 0 (or seated with its hip at pose.Hip). Returns where the near hand ends.
    static Point SideFigure(EntourageSketch s, SidePose pose)
    {
        double k = pose.Scale, w = pose.Build;
        // Joints relative to the hip joint, upright, then leaned about the hip.
        var hip = pose.Hip ?? new Point(0, -92 * k);
        Point Torso(double x, double y) => Rotate(new Point(hip.X + x * k * (x < 0 ? w : 1), hip.Y + y * k), hip, pose.Lean);
        var shoulder = Torso(.5, -47);
        double thigh = 43 * k, shank = 42 * k, upper = 29 * k, fore = 25 * k, hand = 16 * k;

        (Point Knee, Point Ankle) Leg((double Hip, double Knee, double Foot) leg, Point? foot, int bend)
        {
            if (foot is { } target)
            {
                var ankleTarget = target + new Vector(-4 * k, -6 * k);
                var knee = Reach(hip, ankleTarget, thigh, shank, -bend);
                return (knee, ankleTarget);
            }
            var k1 = Hang(hip, thigh, leg.Hip);
            return (k1, Hang(k1, shank, leg.Hip - leg.Knee));
        }
        var near = Leg(pose.NearLeg, pose.NearFoot, 1); var far = Leg(pose.FarLeg, pose.FarFoot, 1);
        (Point Heel, Point Toe) Foot(Point ankle, double angle) =>
            (Rotate(ankle + new Vector(-3.5 * k, 3.2 * k), ankle, angle), Rotate(ankle + new Vector(18 * k, 4.4 * k), ankle, angle));
        var nearFoot = Foot(near.Ankle, pose.NearLeg.Foot); var farFoot = Foot(far.Ankle, pose.FarLeg.Foot);

        // Standing figures rest on their lowest foot; seated ones keep the hip where the seat puts it.
        double lift = 0;
        if (pose.Hip == null) lift = -Math.Max(Math.Max(nearFoot.Heel.Y + 3.8 * k, nearFoot.Toe.Y + 2.6 * k), Math.Max(farFoot.Heel.Y + 3.8 * k, farFoot.Toe.Y + 2.6 * k));
        Point L(Point p) => new(p.X, p.Y + lift);

        (Point P, double R)[] ArmJoints((double Shoulder, double Elbow) arm, Point? target, double radius)
        {
            Point elbow, wrist, tip;
            if (target is { } t)
            {
                var hold = new Point(t.X, t.Y - lift);
                elbow = Reach(shoulder, hold, upper, fore + 4 * k, 1);
                var dir = hold - elbow; dir.Normalize();
                wrist = elbow + dir * fore; tip = wrist + dir * (hand * .8);
            }
            else
            {
                elbow = Hang(shoulder, upper, arm.Shoulder);
                wrist = Hang(elbow, fore, arm.Shoulder + arm.Elbow);
                tip = Hang(wrist, hand, arm.Shoulder + arm.Elbow * 1.3);
            }
            return [(L(shoulder), 5.2 * k * radius), (L(elbow), 4.1 * k * radius), (L(wrist), 3.0 * k * radius), (L(tip), 2.3 * k * radius)];
        }
        bool skirt = pose.Clothes is Clothes.Skirt or Clothes.Dress;
        double legR = skirt ? .82 : 1;
        Geometry LegShape((Point Knee, Point Ankle) leg, (Point Heel, Point Toe) foot) => Union([
            Limb((L(hip), 8.2 * k * legR * w), (L(leg.Knee), 5.5 * k * legR), (L(leg.Ankle), 3.5 * k * legR)),
            Capsule(L(foot.Heel), 3.8 * k, L(foot.Toe), 2.6 * k)]);

        // Far side first: its arm and leg sit behind the body.
        var farArm = ArmJoints(pose.FarArm, pose.FarHand, .95);
        s.Body(Limb(farArm));
        s.Body(LegShape(far, farFoot));
        s.Over(LegShape(near, nearFoot), .55);

        // Torso with the clothes' hem; a coat or skirt flares toward the knees.
        var torso = new List<Point>
        {
            Torso(-3.5, -55), Torso(-9.5, -48), Torso(-11, -36), Torso(-9.5, -20), Torso(-9, -12), Torso(-11.5, -2), Torso(-6, 6), Torso(4, 7), Torso(9, -2),
            Torso(10, -14), Torso(10.5, -27), Torso(12, -38), Torso(10, -48), Torso(4, -55)
        };
        var parts = new List<Geometry> { Spline(true, torso.Select(L).ToArray()) };
        if (pose.Clothes != Clothes.Jacket)
        {
            double hem = pose.Clothes == Clothes.Coat ? 40 : pose.Clothes == Clothes.Dress ? 30 : 33;
            double back = Math.Min(near.Knee.X, far.Knee.X) - (pose.Clothes == Clothes.Coat ? 7 : 8) * k, front = Math.Max(near.Knee.X, far.Knee.X) + 7 * k;
            double hemY = hip.Y + hem * k;
            // A-line: straight from the hips to a gently curved hem.
            var hipBack = Torso(-12, 1); var hipFront = Torso(10.5, 1);
            parts.Add(Spline(true, L(Torso(-9.8, -14)), L(hipBack), L(new Point(back, hemY - 1.5 * k)), L(new Point(back + (front - back) * .33, hemY)),
                L(new Point(back + (front - back) * .67, hemY)), L(new Point(front, hemY - 2 * k)), L(hipFront), L(Torso(10.2, -14))));
        }
        s.Over(Union(parts), .55);

        // Head, neck and hair. The head turns with the lean plus its own tilt.
        var neckBase = Torso(1.5, -53);
        double tilt = pose.Lean + pose.HeadTilt, hs = pose.HeadScale * k;
        var headCenter = Rotate(new Point(neckBase.X + 2.5 * hs, neckBase.Y - 17 * hs), neckBase, tilt);
        var head = new List<Geometry>
        {
            Capsule(L(Rotate(new Point(neckBase.X + .5 * hs, neckBase.Y - 9 * hs), neckBase, tilt)), 4.6 * hs, L(neckBase), 5.6 * k),
            Ellipse(L(headCenter).X, L(headCenter).Y, 9.4 * hs, 11.4 * hs, tilt),
            Ellipse(L(Rotate(headCenter + new Vector(9.4 * hs, 1.2 * hs), headCenter, tilt)).X, L(Rotate(headCenter + new Vector(9.4 * hs, 1.2 * hs), headCenter, tilt)).Y, 1.9 * hs, 2.5 * hs, tilt)
        };
        Point H(double x, double y) => L(Rotate(headCenter + new Vector(x * hs, y * hs), headCenter, tilt));
        switch (pose.Hair)
        {
            case Hair.Short: head.Add(Ellipse(H(-1.8, -2.4).X, H(-1.8, -2.4).Y, 9.9 * hs, 9.6 * hs, tilt)); break;
            case Hair.Long:
                head.Add(Ellipse(H(-1.5, -2.6).X, H(-1.5, -2.6).Y, 10.2 * hs, 9.8 * hs, tilt));
                head.Add(Spline(true, H(-9.8, -3), H(-11.6, 9), H(-10, 21), H(-4, 23), H(-1.5, 10), H(1, -6)));
                break;
            case Hair.Bun:
                head.Add(Ellipse(H(-1.6, -2.5).X, H(-1.6, -2.5).Y, 10 * hs, 9.6 * hs, tilt));
                head.Add(Circle(H(-9.5, -8).X, H(-9.5, -8).Y, 4.4 * hs));
                break;
            case Hair.Cap:
                head.Add(Ellipse(H(-.8, -3.6).X, H(-.8, -3.6).Y, 10 * hs, 9.2 * hs, tilt));
                head.Add(Capsule(H(-3, -7.5), 1.3 * hs, H(14, -6.2), 1.1 * hs));
                break;
        }
        s.Body(Union(head));

        // Near arm in front of the body, then what it holds.
        var nearArm = ArmJoints(pose.NearArm, pose.NearHand, 1);
        s.Over(Limb(nearArm), .6);
        var tip = nearArm[^1].P;
        if (pose.Bag)
        {
            var top = tip + new Vector(0, 2 * k);
            var bag = Union([
                Spline(true, top + new Vector(-12 * k, 7 * k), top + new Vector(12 * k, 7 * k), top + new Vector(13 * k, 34 * k), top + new Vector(-13 * k, 34 * k)),
                Ring(top.X, top.Y + 6 * k, 6 * k, 4.6 * k)]);
            s.Over(bag, .6);
            s.Line(Segment(top + new Vector(-12 * k, 12 * k), top + new Vector(12 * k, 12 * k)), .4);
        }
        if (pose.Phone)
        {
            var phone = Ellipse(tip.X + 1 * k, tip.Y - 1 * k, 1.6 * k, 4.2 * k, 40);
            s.Over(phone, .5);
        }
        return tip;
    }

    // Front view, standing. Variants change the arms, the hair and the clothes.
    static void FrontFigure(EntourageSketch s, int variant)
    {
        bool woman = variant == 1, crossed = variant == 2;
        double hipY = -92, shoulderY = -139;
        // Legs, feet slightly apart and turned out; the weight on the right leg.
        Geometry LegShape(double x, double kneeX, double ankleX, double r) => Union([
            Limb((P(x, hipY), 8.2 * r), (P(kneeX, -50), 5.6 * r), (P(ankleX, -8), 3.6 * r)),
            Ellipse(ankleX + Math.Sign(ankleX) * 1.5, -3.2, 5.4, 3.2)]);
        double legR = woman ? .84 : 1;
        s.Body(LegShape(-8.5, -9.5, -10.5, legR));
        s.Over(LegShape(8.5, 8.2, 7.6, legR), .5);
        var torso = new List<Point>
        {
            P(-6, -147), P(-15, -144), P(-19.5, -139), P(-19.2, -128), P(-16.6, -112), P(-15.2, -102), P(-17.2, -88), P(-8, -84.6), P(0, -84.2), P(8, -84.6), P(17.2, -88),
            P(15.2, -102), P(16.6, -112), P(19.2, -128), P(19.5, -139), P(15, -144), P(6, -147)
        };
        if (woman)
        {
            torso = [P(-5.5, -147), P(-13.5, -144), P(-17.5, -139), P(-17, -128), P(-14, -112), P(-12.5, -102), P(-15.5, -90), P(-21, -66), P(-12, -63.5), P(0, -63), P(12, -63.5), P(21, -66),
                P(15.5, -90), P(12.5, -102), P(14, -112), P(17, -128), P(17.5, -139), P(13.5, -144), P(5.5, -147)];
        }
        s.Over(Spline(true, torso.ToArray()), .5);
        // Collar or neckline.
        s.Line(Spline(false, P(-6, -146), P(0, woman ? -137 : -133), P(6, -146)), .45);
        if (!woman) s.Line(Segment(P(0, -133), P(0, -86)), .35);
        var neck = Capsule(P(0, -150), 4.4, P(0, -142), 5.6);
        var head = new List<Geometry> { neck, Ellipse(0, -158.5, 7.9, 11.3) };
        if (woman) { head.Add(Ellipse(0, -161.5, 9, 9.8)); head.Add(Spline(true, P(-9, -160), P(-10.5, -146), P(-8.5, -134), P(-5, -140), P(-6.2, -152), P(6.2, -152), P(5, -140), P(8.5, -134), P(10.5, -146), P(9, -160))); }
        else if (crossed) head.Add(Ellipse(0, -162.5, 8.4, 8.6));
        else head.Add(Ellipse(0, -162.2, 8.5, 8.9));
        s.Body(Union(head));
        double sh = woman ? 16.5 : 18.5;
        if (crossed)
        {
            // Arms folded across the chest.
            s.Body(Limb((P(-sh, shoulderY + 2), 5.2), (P(-21, -114), 4.3)), Limb((P(sh, shoulderY + 2), 5.2), (P(21, -114), 4.3)));
            s.Over(Union([Capsule(P(-21, -114), 4.3, P(10, -120), 3.4), Capsule(P(10, -120), 3.4, P(17, -124), 2.6)]), .55);
            s.Over(Union([Capsule(P(21, -112), 4.3, P(-10, -117), 3.4), Capsule(P(-10, -117), 3.4, P(-17, -121), 2.6)]), .55);
            return;
        }
        // Left arm hangs; the right hand rests in a pocket (or holds a bag for the second figure).
        s.Over(Limb((P(-sh, shoulderY + 2), 5.1), (P(-sh - 3.4, -111), 4.2), (P(-sh - 3.8, -86), 3.1), (P(-sh - 3.2, -70), 2.3)), .55);
        if (woman)
        {
            s.Over(Limb((P(sh, shoulderY + 2), 4.9), (P(sh + 3.4, -111), 4), (P(sh + 3.8, -86), 3), (P(sh + 3.4, -71), 2.2)), .55);
            s.Over(Union([Spline(true, P(sh - 4, -78), P(sh + 12, -78), P(sh + 13, -58), P(sh - 5, -58)), Ring(sh + 3.6, -78, 4.6, 3.4)]), .55);
        }
        else s.Over(Limb((P(sh, shoulderY + 2), 5.1), (P(sh + 5.5, -112), 4.3), (P(sh - 3, -92), 3.3)), .55);
    }

    // Top view: shoulders, head and arms; walking adds the toes stepping out. Faces up (-y).
    static void PlanFigure(EntourageSketch s, int variant, bool walking)
    {
        double turn = variant switch { 1 => 16, 2 => -12, _ => 0 };
        var t = new RotateTransform(turn);
        Geometry T(Geometry g) => Transformed(g, t);
        if (walking) s.Body(T(Ellipse(-7, -11, 4, 7)), T(Ellipse(7, 10, 4, 7)));
        // Arms close to the shoulders; walking (and the second variant) swings one forward.
        double left = walking ? 6 : variant == 1 ? -6 : 1.5, right = walking ? -5 : 1.5;
        // Shoulders as a pill, full at both ends; the arms show as thin contours where they lie on it.
        s.Body(T(Capsule(P(-13.5, 1), 10.5, P(13.5, 1), 10.5)));
        s.Over(T(Ellipse(-18.8, left, 4.8, 9.5, walking ? 8 : 0)), .45); s.Over(T(Ellipse(18.8, right, 4.8, 9.5, walking ? -8 : 0)), .45);
        s.Over(T(Union([Circle(0, 0, 9.2), Ellipse(0, -9.6, 1.9, 2.2)])), .6);
    }
    static void AddPeople(List<EntourageItem> list)
    {
        string standing = K("사람", "person people human standing");
        list.Add(Item("person.standing", "서 있는 사람", EntourageCategory.People, EntourageView.Elevation, 1.70, EntourageFill.White, 3,
            standing + K("정면", " front"), (s, _, v) => FrontFigure(s, v)));
        list.Add(Item("person.standing-side", "옆으로 선 사람", EntourageCategory.People, EntourageView.Elevation, 1.70, EntourageFill.White, 3,
            standing + K("옆모습", "휴대폰", " phone side"), (s, _, v) => SideFigure(s, new SidePose
            {
                Hair = v switch { 1 => Hair.Long, 2 => Hair.Bun, _ => Hair.Short }, Clothes = v == 1 ? Clothes.Dress : Clothes.Jacket,
                NearArm = v == 0 ? (-8, 122) : (-3, 6), FarArm = v == 0 ? (-4, 116) : (3, 8), HeadTilt = v == 0 ? 22 : 0, Phone = v == 0,
                NearLeg = (5, 0, 0), FarLeg = (-4, 2, 0)
            })));
        list.Add(Item("person.walking", "걷는 사람", EntourageCategory.People, EntourageView.Elevation, 1.70, EntourageFill.White, 3,
            K("보행", "산책", "사람", "walking walk pedestrian people"), (s, _, v) => SideFigure(s, Walk(v))));
        list.Add(Item("person.walking-bag", "가방을 든 사람", EntourageCategory.People, EntourageView.Elevation, 1.70, EntourageFill.White, 3,
            K("가방", "쇼핑", "사람", "bag shopping walking people"), (s, _, v) => SideFigure(s, Walk(v + 1) with
            {
                Bag = true, NearArm = (-2, 4), FarArm = (14, 18), NearLeg = (18, 3, -8), FarLeg = (-13, 24, 26)
            })));
        list.Add(Item("person.sitting", "앉은 사람", EntourageCategory.People, EntourageView.Elevation, 1.30, EntourageFill.White, 3,
            K("의자", "벤치", "휴식", "사람", "sitting seated bench people"), (s, _, v) => SideFigure(s, new SidePose
            {
                Hip = new Point(-2, -50), NearFoot = new Point(40 + v * 2, -2), FarFoot = new Point(35, -2), Lean = v == 2 ? 10 : -4,
                Hair = v switch { 1 => Hair.Long, 2 => Hair.Cap, _ => Hair.Short }, Clothes = Clothes.Jacket,
                NearArm = v == 2 ? (34, 30) : (14, 52), FarArm = (18, 48), NearLeg = (0, 0, 0), FarLeg = (0, 0, 0)
            })));
        list.Add(Item("person.stroller", "유모차를 미는 사람", EntourageCategory.People, EntourageView.Elevation, 1.70, EntourageFill.White, 2,
            K("유모차", "아기", "stroller pram baby family people"), (s, _, v) =>
            {
                var handle = new Point(40, -97);
                SideFigure(s, Walk(v == 0 ? 1 : 0) with { NearHand = handle, FarHand = handle + new Vector(-1, 1), Lean = 6, Bag = false, NearLeg = (17, 4, -8), FarLeg = (-12, 24, 24) });
                Stroller(s, handle);
            }));
        list.Add(Item("person.child", "아이", EntourageCategory.People, EntourageView.Elevation, 1.12, EntourageFill.White, 3,
            K("아이", "어린이", "child kid children people"), (s, _, v) => SideFigure(s, Walk(v == 1 ? 1 : v == 2 ? 2 : 0) with
            {
                Scale = .65, HeadScale = .82, Clothes = v == 1 ? Clothes.Dress : Clothes.Jacket, Hair = v switch { 1 => Hair.Long, 2 => Hair.Cap, _ => Hair.Short },
                NearArm = (-18, 26), FarArm = (20, 28)
            })));
        list.Add(Item("person.cyclist", "자전거 타는 사람", EntourageCategory.People, EntourageView.Elevation, 1.65, EntourageFill.White, 2,
            K("자전거", "cyclist bicycle bike rider people"), (s, _, v) => Cyclist(s, v)));
        list.Add(Item("person.plan", "사람 · 평면", EntourageCategory.People, EntourageView.Plan, .6, EntourageFill.White, 3,
            K("사람", "평면", "person plan top people"), (s, _, v) => PlanFigure(s, v, false), shadowHeight: 1.7 / .6));
        list.Add(Item("person.plan-walking", "걷는 사람 · 평면", EntourageCategory.People, EntourageView.Plan, .6, EntourageFill.White, 3,
            K("보행", "평면", "walking plan top people"), (s, _, v) => PlanFigure(s, v, true), shadowHeight: 1.7 / .6));
    }

    static SidePose Walk(int variant) => variant switch
    {
        1 => new SidePose { Hair = Hair.Long, Clothes = Clothes.Skirt, NearArm = (-20, 14), FarArm = (16, 22), NearLeg = (22, 4, -10), FarLeg = (-15, 26, 28), Build = .94 },
        2 => new SidePose { Hair = Hair.Cap, Clothes = Clothes.Coat, NearArm = (-18, 12), FarArm = (14, 20), NearLeg = (21, 4, -10), FarLeg = (-14, 26, 28), Lean = 3 },
        _ => new SidePose { Hair = Hair.Short, Clothes = Clothes.Jacket, NearArm = (-22, 14), FarArm = (18, 22), NearLeg = (24, 4, -12), FarLeg = (-16, 28, 30), Lean = 2 }
    };

    // A pram with a hood, behind the hands at `handle`.
    static void Stroller(EntourageSketch s, Point handle)
    {
        var frame = new List<Geometry>
        {
            Capsule(handle + new Vector(-1, -1), 1.6, handle + new Vector(10, 31), 1.4),
            Capsule(handle + new Vector(12, 36), 1.2, P(handle.X + 12, -11), 1.2),
            Capsule(handle + new Vector(12, 40), 1.2, P(handle.X + 50, -11), 1.2)
        };
        s.Body(Union(frame));
        // A boat-shaped bassinet with a folding hood over its back half.
        var basket = Spline(true, handle + new Vector(6, 24), handle + new Vector(10, 40), handle + new Vector(24, 48), handle + new Vector(48, 48), handle + new Vector(60, 38), handle + new Vector(63, 25), handle + new Vector(34, 23));
        var hood = Spline(true, handle + new Vector(6, 25), handle + new Vector(6, 10), handle + new Vector(14, 0), handle + new Vector(27, -2), handle + new Vector(36, 6), handle + new Vector(40, 24));
        s.Over(Union([basket, hood]), .55);
        s.Line(Spline(false, handle + new Vector(14, 23), handle + new Vector(16, 8), handle + new Vector(27, 3)), .4);
        s.Line(Spline(false, handle + new Vector(24, 24), handle + new Vector(26, 12), handle + new Vector(33, 8)), .4);
        foreach (double x in new[] { handle.X + 12, handle.X + 50 }) s.Body(Ring(x, -10, 10, 7.6), Circle(x, -10, 1.6));
    }

    static void Cyclist(EntourageSketch s, int variant)
    {
        var bike = BikeSide(s, 0, 0);
        SideFigure(s, new SidePose
        {
            Hip = bike.Saddle + new Vector(0, -6), NearFoot = bike.NearPedal + new Vector(4, -2), FarFoot = bike.FarPedal + new Vector(4, -2), Lean = 32,
            NearHand = bike.Bar + new Vector(-1, 0), FarHand = bike.Bar + new Vector(-2, 1), Hair = variant == 1 ? Hair.Long : Hair.Cap,
            Clothes = Clothes.Jacket, HeadTilt = -18
        });
    }
}
