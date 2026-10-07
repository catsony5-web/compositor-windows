using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Tone classes of the screentone plan; each becomes one screentone layer (gradients one per room).</summary>
public enum ScreenTone { Poche, Light, Medium, Dark, Gradient }

// The five design styles as recipes over StyleKit. A recipe reads the context (the document as it
// looks under the style folder, whether it is a drawing, the parameter values) and adds layers
// bottom to top. Recipes are deterministic: every random choice comes from StyleKit.Seed.
public static class StyleRecipes
{
    public static void Build(string styleId, StyleKit kit)
    {
        switch (styleId)
        {
            case DesignStyles.ScreentonePlan: Screentone(kit); break;
            case DesignStyles.DarkSection: DarkSection(kit); break;
            case DesignStyles.Cyanotype: Cyanotype(kit); break;
            case DesignStyles.NeoBrutalistPoster: Poster(kit); break;
            case DesignStyles.TranslucentEditorial: Editorial(kit); break;
            default: throw new ArgumentException("알 수 없는 디자인 스타일입니다.");
        }
    }

    static uint Gray(double value) { byte v = Imaging.Byte(value); return 0xFF000000u | (uint)v << 16 | (uint)v << 8 | v; }
    static double Mix(double a, double b, double t) => a + (b - a) * t;
    static uint MixColor(uint a, uint b, double t) => 0xFF000000u | (uint)Imaging.Byte(Mix(a >> 16 & 255, b >> 16 & 255, t)) << 16 | (uint)Imaging.Byte(Mix(a >> 8 & 255, b >> 8 & 255, t)) << 8 | Imaging.Byte(Mix(a & 255, b & 255, t));
    static double Luminance(uint argb) => (.2126 * (argb >> 16 & 255) + .7152 * (argb >> 8 & 255) + .0722 * (argb & 255)) / 255;

    // ---- 1. 흑백 스크린톤 평면 ----------------------------------------------------------------

    const uint Toner = 0xFF0C0C0C;

    static void Screentone(StyleKit kit)
    {
        var c = kit.Context; double strength = c.Unit("strength"), texture = c.Unit("texture");
        kit.AddGradientMap("흑백 변환", 0xFF000000, 0xFFFFFFFF);
        // Coloured hatch fills of the drawing would muddy the screens: they are hidden while the style is on.
        foreach (var layer in c.Source.Layers.Where(l => l.Kind == LayerKind.Material && Shown(c.Source, l)))
            if (kit.Original(layer.Id) is { } original) kit.Hide(original);
        // The line work snapped to black on white like a copy. The screens are drawn over it afterwards,
        // so they keep their exact tone at every zoom instead of being thresholded into moiré.
        kit.AddThreshold("선 정리", 200 + 30 * strength, 12);
        var map = c.Services.LineRegions(c);
        var classes = map == null ? [] : Classify(map, RegionDetection.Neighbors(map, Math.Max(3, (int)Math.Round(map.Width / 220d)), c.Token), strength);
        if (classes.Count == 0) c.Notes.Add("닫힌 영역을 찾지 못했습니다. 끊긴 벽선을 이으면 방이 채워집니다.");
        else
        {
            // Library screens have 11 dot rows per repeat: rows about 1/200 of the long side apart, already at 45°.
            double tile = c.Measure(11 / 200d, 40, 360);
            int grow = Math.Max(1, (int)Math.Round(map!.Width / 900d));
            List<Point[]> Outlines(params int[] regions) => RegionDetection.Outlines(map, regions, grow, .8, c.Token);
            int[] Of(ScreenTone tone) => classes.Where(p => p.Value == tone).Select(p => p.Key).ToArray();
            static MaterialAsset Screen(HatchPattern pattern) => HatchPatternRenderer.Create(pattern);
            kit.FillRegions("포셰", Outlines(Of(ScreenTone.Poche)), Screen(HatchPattern.SolidBlack), tile, 0, Toner);
            kit.FillRegions("망점 · 밝게", Outlines(Of(ScreenTone.Light)), Screen(strength < .4 ? HatchPattern.DotScreen10 : HatchPattern.DotScreen20), tile, 0, Toner);
            kit.FillRegions("망점 · 중간", Outlines(Of(ScreenTone.Medium)), Screen(strength < .7 ? HatchPattern.DotScreen30 : HatchPattern.DotScreen45), tile, 0, Toner);
            kit.FillRegions("망점 · 어둡게", Outlines(Of(ScreenTone.Dark)), Screen(strength < .45 ? HatchPattern.DotScreen60 : HatchPattern.DotScreen75), tile, 0, Toner);
            // The largest room fades as a grainy photocopied gradient, a second one as a halftone gradient,
            // each along its longer side from dense to light.
            var gradients = Of(ScreenTone.Gradient).Select(label => map.Regions[label - 1]).OrderByDescending(r => r.Area).ToArray();
            for (int i = 0; i < gradients.Length; i++)
            {
                var room = gradients[i]; bool stipple = i % 2 == 0;
                double angle = room.Bounds.Width >= room.Bounds.Height ? (i % 2 == 0 ? 0 : 180) : (i % 2 == 0 ? 90 : 270);
                var gradient = new ToneGradient(angle, Math.Clamp(.78 + .2 * strength, 0, 1), .03, (int)(kit.Seed("gradient " + i) % ToneGradient.MaxSeed));
                string name = stipple ? "점묘 그라데이션" : "점 그라데이션";
                if (i >= 2) name += $" {i / 2 + 1}";
                kit.FillRegions(name, Outlines(room.Label), Screen(stipple ? HatchPattern.StippleGradient : HatchPattern.DotGradient), tile, 0, Toner, gradient);
            }
        }
        // A copy's toner: specks on the paper, dropouts in the black, faint streaks along the page.
        kit.AddPaper("복사 질감", new PaperTextureSpec
        {
            Scale = kit.Grain(), Tint = 0, Grain = .1 + .16 * texture, Fibers = 0, Toner = .05 + .3 * texture, Streaks = .05 + .25 * texture
        });
    }

    static bool Shown(Document doc, Layer layer)
    {
        var index = doc.Layers.ToDictionary(l => l.Id);
        for (Layer? current = layer; current != null; current = current.ParentId is { } p && index.TryGetValue(p, out var next) ? next : null)
            if (!current.Visible) return false;
        return true;
    }

    /// <summary>Closed areas of the line work only: fills, labels, furniture and hatch lines are left out of the analysis.</summary>
    public static RegionMap? LineRegions(StyleContext c)
    {
        var lines = c.Source.Snapshot();
        var skip = new HashSet<Guid>();
        foreach (var role in new[] { DrawingRole.Annotation, DrawingRole.Hatch, DrawingRole.Furniture })
            foreach (var id in DrawingLineCleanup.RoleLayers(lines, role)) skip.Add(id);
        foreach (var layer in lines.Layers) if (layer.Kind is LayerKind.Material or LayerKind.Text || skip.Contains(layer.Id)) layer.Visible = false;
        // At most 2000 px, and a gallery miniature is read at the size its document would be, so it finds the same rooms.
        int longest = Math.Max(c.Width, c.Height);
        double scale = Math.Min(2000, longest * c.Reduction) / longest;
        int w = Math.Max(1, (int)Math.Round(c.Width * scale)), h = Math.Max(1, (int)Math.Round(c.Height * scale));
        if (w < 24 || h < 24) return null;
        var image = DesignRenderer.Render(lines, new Rect(0, 0, c.Width, c.Height), w, h, c.Token);
        var ink = RegionDetection.Close(RegionDetection.Ink(image), w, h);
        return RegionDetection.Find(ink, w, h, w / (double)c.Width, c.Token);
    }

    /// <summary>
    /// Tone class per region. Noise (letter counters, slivers) is skipped; thin or small closed areas
    /// (wall cavities, columns) are poché; rooms get dot screens in turn so neighbours differ, and
    /// two large rooms (the largest and the fourth largest) a gradient.
    /// </summary>
    public static Dictionary<int, ScreenTone> Classify(RegionMap map, HashSet<(int, int)> neighbors, double strength)
    {
        double minArea = Math.Max(16, map.Width * (double)map.Height * .00002);
        var regions = map.Regions.Where(r => r.Area >= minArea && r.Radius >= 1.2).ToList();
        var result = new Dictionary<int, ScreenTone>();
        if (regions.Count == 0) return result;
        double largest = regions.Max(r => r.Radius), total = regions.Sum(r => (double)r.Area);
        double pocheRadius = Math.Max(2.2, largest * (.14 + .06 * strength));
        var rooms = new List<RegionInfo>();
        foreach (var region in regions)
        {
            if (regions.Count > 1 && (region.Radius <= pocheRadius || region.Area <= total * .0025)) result[region.Label] = ScreenTone.Poche;
            else rooms.Add(region);
        }
        rooms = rooms.OrderByDescending(r => r.Area).ThenBy(r => r.Center.Y).ThenBy(r => r.Center.X).ToList();
        var adjacent = new Dictionary<int, List<int>>();
        foreach (var (a, b) in neighbors)
        {
            (adjacent.TryGetValue(a, out var la) ? la : adjacent[a] = []).Add(b);
            (adjacent.TryGetValue(b, out var lb) ? lb : adjacent[b] = []).Add(a);
        }
        ScreenTone[] cycle = [ScreenTone.Medium, ScreenTone.Light, ScreenTone.Dark, ScreenTone.Light, ScreenTone.Medium, ScreenTone.Dark];
        for (int k = 0; k < rooms.Count; k++)
        {
            var room = rooms[k];
            var order = new List<ScreenTone>();
            if ((k == 0 && rooms.Count >= 3 || k == 3) && room.Area >= total * .05) order.Add(ScreenTone.Gradient);
            for (int i = 0; i < cycle.Length; i++) { var tone = cycle[(k + i) % cycle.Length]; if (!order.Contains(tone)) order.Add(tone); }
            var taken = (adjacent.GetValueOrDefault(room.Label) ?? []).Where(result.ContainsKey).Select(n => result[n]).ToHashSet();
            result[room.Label] = order.FirstOrDefault(t => !taken.Contains(t), order[0]);
        }
        return result;
    }

    // ---- 2. 어두운 단면 -----------------------------------------------------------------------

    static void DarkSection(StyleKit kit)
    {
        var c = kit.Context; double strength = c.Unit("strength");
        // Dark ink becomes light lines and light paper the black ground; hatch tones stay readable as dark greys.
        kit.AddGradientMap("흰 선 · 검은 바탕", Gray(205 + 50 * strength), Gray(16 - 12 * strength));
        kit.AddLevels("선 밝기", 4 + 10 * strength, 250 - 95 * strength, 1 + .25 * strength);
        if (c.Value("grid") >= .5)
        {
            double cell = c.Measure(1 / 42d, 12, 400);
            kit.FillCanvas("격자", HatchPatternRenderer.Create(HatchPattern.Grid), cell * 4, 0xFF8C8C8C, 1, BlendMode.Screen, .4);
        }
        // A printed board: fine grain in the white lines, a faint uneven lift in the black.
        kit.AddPaper("인쇄 질감", new PaperTextureSpec { Scale = kit.Grain(), Tint = 0, Grain = .25 + .2 * strength, Fibers = .06, Toner = .04 });
    }

    // ---- 3. 청사진 (사이아노타입) --------------------------------------------------------------

    const uint CyanotypePaper = 0xFFF3EFE4;

    static void Cyanotype(StyleKit kit)
    {
        var c = kit.Context; double strength = c.Unit("strength"), paper = c.Unit("paper");
        if (c.IsDrawing)
        {
            // A blueprint: the exposed paper turns Prussian blue, the drawn lines stay pale.
            uint ground = MixColor(0xFF0A2350, 0xFF3B74B0, Math.Clamp(paper * .62 + (1 - strength) * .16, 0, 1));
            kit.AddGradientMap("청사진", 0xFFEEF2F2, ground);
            kit.AddLevels("선 대비", 0, 255 - 70 * strength, 1 + .2 * strength);
        }
        else
        {
            kit.AddGradientMap("흑백 변환", 0xFF000000, 0xFFFFFFFF);
            kit.AddCurves("대비", StyleKit.Contrast(.25 + .55 * strength));
            // Prussian blue from deep shadows through a saturated mid blue to the paper.
            double deep = 1 - .55 * strength;
            double pr = Mix(.80, .96, paper), pg = Mix(.84, .96, paper), pb = Mix(.86, .93, paper);
            kit.AddCurves("사이아노타입 색", [new(0, 0), new(1, 1)],
                red: [new(0, .03 * deep), new(.25, .06 * deep + .02), new(.5, .2), new(.75, .5 + .05 * paper), new(1, pr)],
                green: [new(0, .12 * deep), new(.25, .24 * deep + .03), new(.5, .43), new(.75, .67 + .04 * paper), new(1, pg)],
                blue: [new(0, .3 * deep + .04), new(.25, .5 * deep + .08), new(.5, .69), new(.75, .82), new(1, pb)]);
        }
        // Hand-coated paper: fibres and tooth everywhere, and the unexposed margin left by the brush around the print.
        kit.AddPaper("종이 · 붓 자국", new PaperTextureSpec
        {
            Scale = kit.Grain(1 / 900d), TintArgb = CyanotypePaper, Tint = .2 + .35 * paper, Grain = .45 + .35 * strength, Fibers = .3 + .4 * paper,
            Edges = .7 + .25 * strength, EdgeWidth = .045 + .035 * paper, EdgeArgb = CyanotypePaper
        });
    }

    // ---- 4. 네오 브루탈리즘 포스터 ------------------------------------------------------------

    static (uint Dark, uint Light, uint Accent) Scheme(int index) => index switch
    {
        1 => (0xFF14204A, 0xFFF1D9DB, 0xFFE8382B),
        2 => (0xFF0E2A8A, 0xFFEFEDE6, 0xFF0E2A8A),
        3 => (0xFF151515, 0xFFFF7A1E, 0xFF151515),
        _ => (0xFF0D0D0D, 0xFFF2F0EA, 0xFF0D0D0D)
    };

    static void Poster(StyleKit kit)
    {
        var c = kit.Context; int scheme = (int)c.Value("color"), look = (int)c.Value("photo"); bool hollow = c.Value("title") >= .5;
        var (dark, light, accent) = Scheme(scheme);
        int w = c.Width, h = c.Height; double unit = Math.Min(w, h);
        // The photo in two colors: a coarse print screen, a hard bitmap or a smooth duotone. The cut-out in
        // front of the title gets the same layers clipped to it, so the two parts print alike.
        double cell = c.Measure(1 / 230d, 3, 48), level = Math.Clamp(MedianLuminance(c) * 255, 70, 190);
        void Treatment(bool subject)
        {
            switch (look)
            {
                case 1:
                    kit.AddThreshold(subject ? "피사체 · 비트맵" : "비트맵", level, 3, subject);
                    kit.AddGradientMap(subject ? "피사체 · 두 색" : "두 색", dark, light, clipped: subject);
                    break;
                case 2:
                    kit.AddGradientMap(subject ? "피사체 · 듀오톤" : "듀오톤", dark, light, clipped: subject);
                    kit.AddCurves(subject ? "피사체 · 대비" : "대비", StyleKit.Contrast(.8), clipped: subject);
                    break;
                default:
                    kit.AddCurves(subject ? "피사체 · 대비" : "대비", StyleKit.Contrast(.85), clipped: subject);
                    kit.AddHalftone(subject ? "피사체 · 망점" : "망점", cell, 45, dark, light, HalftoneShape.Round, subject);
                    break;
            }
        }
        Treatment(false);
        // The title: the document name in a huge condensed face, set tight across the top.
        string title = c.Source.Name.Trim().ToUpperInvariant();
        if (title.Length == 0) title = "TITLE";
        if (title.Length > 40) title = title[..40].TrimEnd();
        bool hangul = StyleKit.HasHangul(title);
        string family = hangul ? "Malgun Gothic" : StyleKit.HasFont("Bahnschrift") ? "Bahnschrift Bold Condensed" : StyleKit.HasFont("Impact") ? "Impact" : "Arial Black";
        string[] words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && title.Length > (hangul ? 7 : 11))
        {
            // Two lines broken at the space nearest the middle.
            int best = 1; double half = title.Length / 2d;
            for (int k = 1; k < words.Length; k++) if (Math.Abs(string.Join(' ', words.Take(k)).Length - half) < Math.Abs(string.Join(' ', words.Take(best)).Length - half)) best = k;
            title = string.Join(' ', words.Take(best)) + "\n" + string.Join(' ', words.Skip(best));
        }
        // Hollow letters: only a centred outline, about 3.5% of the letter size.
        var probe = new TextSpec
        {
            Content = title, FontFamily = family, Bold = hangul, FontSize = 200, LineHeight = 200 * (hangul ? .98 : .84), Tracking = hangul ? -45 : -12, ColorArgb = accent,
            Outline = hollow, OutlineOnly = hollow, OutlineWidth = 7, OutlinePosition = TextOutlinePosition.Center, OutlineArgb = accent
        };
        var (size, scale) = StyleKit.FitWidth(probe, w * .92, h * .46);
        var spec = probe with { FontSize = size, LineHeight = size * (hangul ? .98 : .84), OutlineWidth = Math.Max(.5, size * .035) };
        double titleHeight = Math.Min(h * .46, StyleKit.Measure(spec).Height * scale);
        // A drawing has no subject to lift: its line work stays under the title.
        var subject = c.IsDrawing ? null : kit.CutOutSubject("피사체");
        // The title sits behind the top of the subject, so the cut-out overlaps its lower part: a little for a wide
        // subject (a view, a facade), more for a narrow one (a tower, a person), and the letters stay readable.
        double overlap = subject is { } wide && wide.Core.Width > w * .55 ? .2 : .38;
        double titleY = subject is { } found ? Math.Clamp(found.Core.Y - titleHeight * (1 - overlap), h * .07, h * .5) : h * .085;
        var region = new Rect(w * .04, titleY, w * .92, titleHeight);
        uint titleColor = Readable(c, region, dark, light, accent);
        spec = spec with { ColorArgb = titleColor, OutlineArgb = titleColor };
        // Over a hard bitmap the letters cross solid ink as well as paper: a thin knockout edge keeps them whole.
        if (!hollow && look == 1)
        {
            uint edge = Math.Abs(Luminance(titleColor) - Luminance(light)) >= Math.Abs(Luminance(titleColor) - Luminance(dark)) ? light : dark;
            spec = spec with { Outline = true, OutlinePosition = TextOutlinePosition.Outside, OutlineWidth = Math.Max(.5, spec.FontSize * .018), OutlineArgb = edge };
        }
        var titleLayer = kit.AddText("제목", spec, w * .04 - size * scale * .02, titleY, scale);
        if (subject != null) { kit.MoveBelow(titleLayer, subject.Layer); Treatment(true); }
        // Small text blocks in the corners and a justified column under the title, each on a solid label so it
        // reads over the printed photo.
        double small = c.AtLeast(unit * .021, 9);
        string font = StyleKit.HasFont("Bahnschrift") ? "Bahnschrift SemiBold SemiCondensed" : "Segoe UI Semibold";
        void Block(string name, string content, double x, double y, TextAlignment alignment, double sizeFactor = 1, double box = 0)
        {
            var text = new TextSpec { Content = content, FontFamily = font, FontSize = small * sizeFactor, LineHeight = small * sizeFactor * 1.18, Tracking = 30, Alignment = alignment, ColorArgb = dark, BoxWidth = box };
            var (tw, th) = StyleKit.Measure(text);
            double left = alignment == TextAlignment.Right ? x - tw : x, pad = small * .45;
            kit.AddShape("라벨", new ShapeSpec { Width = Math.Max(1, (int)Math.Ceiling(tw + pad * 2)), Height = Math.Max(1, (int)Math.Ceiling(th + pad * 2)), FillArgb = light, StrokeEnabled = false }, left - pad, y - pad);
            kit.AddText(name, text, left - 4, y - 4);
        }
        int year = c.Services.Year;
        Block("작은 글 · 프로젝트", "PROJECT N°01\n" + Loc.T("프로젝트 이름"), w * .04, h * .03, TextAlignment.Left);
        Block("작은 글 · 연도", year.ToString(System.Globalization.CultureInfo.InvariantCulture), w * .96, h * .03, TextAlignment.Right, 1.6);
        Block("작은 글 · 설명", Loc.T("한 줄 설명을 적어 주세요.") + "\n" + Loc.T("작은 글을 여러 곳에 두면 포스터에 밀도가 생깁니다. 이 단락은 상자 폭에 맞춰 양쪽 정렬됩니다."),
            w * .04, Math.Min(region.Bottom + h * .025, h * .7), TextAlignment.Justify, .9, Math.Max(small * 8, w * .27));
        Block("작은 글 · 장소", "SITE\n" + Loc.T("장소"), w * .04, h * .9, TextAlignment.Left);
        Block("작은 글 · 스튜디오", "DESIGN STUDIO\n" + Loc.T("이름"), w * .96, h * .9, TextAlignment.Right);
        // Rough print: grain, toner specks and a slightly tinted stock.
        kit.AddPaper("인쇄 질감", new PaperTextureSpec { Scale = kit.Grain(), Tint = .15, TintArgb = MixColor(light, 0xFFFFFFFF, .4), Grain = .45, Fibers = .1, Toner = .2, Streaks = .1 });
    }

    // The accent where it stands out from the duotone under it, otherwise dark or light.
    static uint Readable(StyleContext c, Rect area, uint dark, uint light, uint accent)
    {
        double lum = AverageLuminance(c, area);
        uint under = MixColor(dark, light, lum);
        double contrast(uint a, uint b) => Math.Abs(Luminance(a) - Luminance(b));
        if (contrast(accent, under) >= .35) return accent;
        return contrast(dark, under) >= contrast(light, under) ? dark : light;
    }

    static double AverageLuminance(StyleContext c, Rect area)
    {
        var image = c.Composite; area.Intersect(new Rect(0, 0, image.Width, image.Height));
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) return .5;
        int x0 = (int)area.X, y0 = (int)area.Y, x1 = (int)Math.Ceiling(area.Right), y1 = (int)Math.Ceiling(area.Bottom);
        int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 40000)); double sum = 0, count = 0;
        for (int y = y0; y < y1; y += step) for (int x = x0; x < x1; x += step)
        {
            int i = (y * image.Width + x) * 4; double a = image.Data[i + 3] / 255d;
            sum += ((.0722 * image.Data[i] + .7152 * image.Data[i + 1] + .2126 * image.Data[i + 2]) / 255 * a + (1 - a)); count++;
        }
        return count == 0 ? .5 : sum / count;
    }

    // The middle luminance (0–1) of the image, so a bitmap splits it into about as much black as white.
    static double MedianLuminance(StyleContext c)
    {
        var image = c.Composite; var histogram = new int[256]; int count = 0;
        int step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / 160000));
        for (int y = 0; y < image.Height; y += step) for (int x = 0; x < image.Width; x += step)
        {
            int i = (y * image.Width + x) * 4; if (image.Data[i + 3] < 128) continue;
            histogram[Imaging.Byte(.0722 * image.Data[i] + .7152 * image.Data[i + 1] + .2126 * image.Data[i + 2])]++; count++;
        }
        for (int v = 0, seen = 0; v < 256; v++) if ((seen += histogram[v]) * 2 >= count && count > 0) return v / 255d;
        return .5;
    }

    // ---- 5. 반투명 에디토리얼 ----------------------------------------------------------------

    static void Editorial(StyleKit kit)
    {
        var c = kit.Context; double blur = c.Unit("blur"), glow = c.Unit("glow"); int position = (int)c.Value("panel");
        int w = c.Width, h = c.Height; double unit = Math.Min(w, h);
        var panel = position switch
        {
            0 => new Rect(w * .06, h * .08, w * .34, h * .84),
            1 => new Rect(w * .31, h * .1, w * .38, h * .8),
            3 => new Rect(w * .06, h * .6, w * .88, h * .32),
            _ => new Rect(w * .6, h * .08, w * .34, h * .84)
        };
        // The frosted panel first, so the soft tone and the glow below act on it and on the photo alike.
        double radius = (.006 + .03 * blur) * Math.Max(w, h);
        kit.AddBlurredCopy("반투명 패널 · 흐림", radius, StyleKit.RectangleMask(panel));
        kit.AddCurves("부드러운 톤", [new(0, .07), new(.5, .52), new(1, .95)]);
        kit.AddHueSaturation("차분한 색", 0, -24);
        if (glow > 0) kit.AddGlow("빛 번짐", .78 - .14 * glow, c.Measure(1 / 26d, 2, 1000), .3 + 1.6 * glow);
        double veil = .26 + .16 * blur;
        int pw = Math.Max(1, (int)Math.Round(panel.Width)), ph = Math.Max(1, (int)Math.Round(panel.Height));
        kit.AddShape("반투명 패널 · 종이", new ShapeSpec { Width = pw, Height = ph, FillArgb = 0xFFFFFFFF, StrokeEnabled = false }, panel.X, panel.Y, veil);
        kit.AddShape("반투명 패널 · 테두리", new ShapeSpec { Width = pw, Height = ph, FillEnabled = false, StrokeEnabled = true, StrokeArgb = 0xB3FFFFFF, StrokeWidth = c.AtLeast(unit / 900, 1) }, panel.X, panel.Y);
        // Text over the veiled panel: dark unless the panel stays dark.
        double under = AverageLuminance(c, panel) * (1 - veil) + veil;
        uint ink = under >= .55 ? 0xFF1C1D20u : 0xFFF6F6F2u;
        double margin = c.AtLeast(unit * .03, 8), small = c.AtLeast(unit * .017, 9), room = Math.Max(small * 4, panel.Width - margin * 2);
        string sans = StyleKit.HasFont("Segoe UI") ? "Segoe UI" : "Malgun Gothic", serif = StyleKit.HasFont("Georgia") ? "Georgia" : sans;
        string titleText = c.Source.Name.Trim(); if (titleText.Length == 0) titleText = "Title"; if (titleText.Length > 60) titleText = titleText[..60].TrimEnd();
        bool hangul = StyleKit.HasHangul(titleText);
        var caption = new TextSpec { Content = $"VOL. 01  —  {c.Services.Year}", FontFamily = sans, FontSize = small * .85, Tracking = 160, ColorArgb = ink };
        kit.AddText("작은 글 · 머리", caption, panel.X + margin - 4, panel.Y + margin - 4);
        // The title wraps inside the panel; the body is a paragraph box of the panel's width.
        var title = new TextSpec { Content = titleText, FontFamily = hangul ? "Malgun Gothic" : serif, Italic = !hangul, FontSize = small * 2.4, LineHeight = small * 2.7, ColorArgb = ink, BoxWidth = room };
        var titleSize = StyleKit.Measure(title);
        double titleY = panel.Y + margin + small * 2.2;
        kit.AddText("제목", title, panel.X + margin - 4, titleY - 4);
        var body = new TextSpec
        {
            Content = Loc.T("본문을 입력하세요. 짧은 설명이나 날짜, 장소를 적으면 반투명 종이 위에 작은 단락으로 놓입니다."),
            FontFamily = sans, FontSize = small, LineHeight = small * 1.55, ColorArgb = ink, BoxWidth = Math.Min(room, small * 24)
        };
        kit.AddText("본문", body, panel.X + margin - 4, titleY + titleSize.Height + small * 1.2 - 4);
        var folio = new TextSpec { Content = "01", FontFamily = sans, FontSize = small * .85, Tracking = 120, ColorArgb = ink, Alignment = TextAlignment.Right };
        var (fw, fh) = StyleKit.Measure(folio);
        kit.AddText("작은 글 · 쪽", folio, panel.Right - margin - fw - 4, panel.Bottom - margin - fh - 4);
        kit.AddPaper("종이 질감", new PaperTextureSpec { Scale = kit.Grain(), Tint = .12, TintArgb = 0xFFF4F1EA, Grain = .22, Fibers = .16 });
    }
}
