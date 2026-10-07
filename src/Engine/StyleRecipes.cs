using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Tone classes of the screentone plan; each becomes one pattern layer.</summary>
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
    static uint Argb(double r, double g, double b) => 0xFF000000u | (uint)Imaging.Byte(r * 255) << 16 | (uint)Imaging.Byte(g * 255) << 8 | Imaging.Byte(b * 255);
    static double Mix(double a, double b, double t) => a + (b - a) * t;
    static uint MixColor(uint a, uint b, double t) => 0xFF000000u | (uint)Imaging.Byte(Mix(a >> 16 & 255, b >> 16 & 255, t)) << 16 | (uint)Imaging.Byte(Mix(a >> 8 & 255, b >> 8 & 255, t)) << 8 | Imaging.Byte(Mix(a & 255, b & 255, t));
    static double Luminance(uint argb) => (.2126 * (argb >> 16 & 255) + .7152 * (argb >> 8 & 255) + .0722 * (argb & 255)) / 255;

    // ---- 1. 흑백 스크린톤 평면 ----------------------------------------------------------------

    static void Screentone(StyleKit kit)
    {
        var c = kit.Context; double strength = c.Unit("strength"), texture = c.Unit("texture");
        kit.AddGradientMap("흑백 변환", 0xFF000000, 0xFFFFFFFF);
        // Coloured hatch fills of the drawing would muddy the screens: they are hidden while the style is on.
        foreach (var layer in c.Source.Layers.Where(l => l.Kind == LayerKind.Material && Shown(c.Source, l)))
            if (kit.Original(layer.Id) is { } original) kit.Hide(original);
        var map = LineRegions(c);
        var classes = map == null ? [] : Classify(map, RegionDetection.Neighbors(map, Math.Max(3, (int)Math.Round(map.Width / 220d)), c.Token), strength);
        if (classes.Count == 0) c.Notes.Add("닫힌 영역을 찾지 못했습니다. 끊긴 벽선을 이으면 방이 채워집니다.");
        else
        {
            double pitch = Math.Clamp(Math.Max(c.Width, c.Height) / 250d, 4, 40);
            int grow = Math.Max(1, (int)Math.Round(map!.Width / 900d));
            List<Point[]> Outlines(ScreenTone tone) => RegionDetection.Outlines(map, classes.Where(p => p.Value == tone).Select(p => p.Key).ToArray(), grow, .8, c.Token);
            kit.FillRegions("포셰", Outlines(ScreenTone.Poche), StyleKit.Poche, 16);
            kit.FillRegions("망점 · 밝게", Outlines(ScreenTone.Light), StyleKit.DotScreen, pitch * .8, 45, StyleKit.DotWeight(.13 - .06 * strength));
            kit.FillRegions("망점 · 중간", Outlines(ScreenTone.Medium), StyleKit.DotScreen, pitch, 45, StyleKit.DotWeight(.25 + .08 * strength));
            kit.FillRegions("망점 · 어둡게", Outlines(ScreenTone.Dark), StyleKit.DotScreen, pitch * 1.15, 45, StyleKit.DotWeight(.42 + .2 * strength));
            var gradient = classes.Where(p => p.Value == ScreenTone.Gradient).Select(p => p.Key).ToArray();
            for (int i = 0; i < gradient.Length; i++)
                kit.FillRegions(gradient.Length == 1 ? "점묘 그라데이션" : $"점묘 그라데이션 {i + 1}", RegionDetection.Outlines(map, [gradient[i]], grow, .8, c.Token), StyleKit.Poche, 16,
                    mask: kit.StippleGradient(.62 + .3 * strength, i % 2 == 1));
        }
        kit.AddThreshold("복사 대비", 200 + 22 * strength, 34 - 14 * strength);
        kit.AddGrain("복사기 토너", TextureKind.Toner, .15 + .85 * texture, BlendMode.Multiply);
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
        int longest = Math.Max(c.Width, c.Height); double scale = Math.Min(1, 2000d / longest);
        int w = Math.Max(1, (int)Math.Round(c.Width * scale)), h = Math.Max(1, (int)Math.Round(c.Height * scale));
        if (w < 24 || h < 24) return null;
        var image = DesignRenderer.Render(lines, new Rect(0, 0, c.Width, c.Height), w, h, c.Token);
        var ink = RegionDetection.Close(RegionDetection.Ink(image), w, h);
        return RegionDetection.Find(ink, w, h, w / (double)c.Width, c.Token);
    }

    /// <summary>
    /// Tone class per region. Noise (letter counters, slivers) is skipped; thin or small closed areas
    /// (wall cavities, columns) are poché; rooms get dot screens in turn so neighbours differ, and
    /// the largest rooms a stipple gradient.
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
            if ((k == 0 || k == 5) && rooms.Count >= 3 && room.Area >= total * .05) order.Add(ScreenTone.Gradient);
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
            double cell = Math.Clamp(Math.Max(c.Width, c.Height) / 42d, 12, 400);
            kit.FillCanvas("격자", HatchPatternRenderer.Create(HatchPattern.Grid), cell * 4, 0xFF8C8C8C, 1, BlendMode.Screen, .34);
        }
        kit.AddGrain("단면 질감", TextureKind.Section, .55 + .45 * strength, BlendMode.Screen, .8);
    }

    // ---- 3. 청사진 (사이아노타입) --------------------------------------------------------------

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
        kit.AddGrain("종이 결", TextureKind.Paper, .55 + .45 * strength, BlendMode.Multiply);
        kit.AddExposure("가장자리", -(.7 + 1.1 * strength), kit.EdgeMask(.045, .05));
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
        var c = kit.Context; int scheme = (int)c.Value("color"); double contrast = c.Unit("contrast"), texture = c.Unit("texture");
        var (dark, light, accent) = Scheme(scheme);
        // upgrade: 망점(하프톤) adjustment for the photo (halftone / bitmap look) from codex/style-effects.
        kit.AddGradientMap("듀오톤", dark, light);
        kit.AddCurves("대비", StyleKit.Contrast(.35 + .65 * contrast));
        int w = c.Width, h = c.Height; double unit = Math.Min(w, h);
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
        var probe = new TextSpec { Content = title, FontFamily = family, Bold = hangul, FontSize = 200, LineHeight = 200 * (hangul ? .98 : .84), Tracking = hangul ? -45 : -12, ColorArgb = accent };
        var (size, scale) = StyleKit.FitWidth(probe, w * .92, h * .46);
        var spec = probe with { FontSize = size, LineHeight = size * (hangul ? .98 : .84) };
        double titleHeight = Math.Min(h * .46, StyleKit.Measure(spec).Height * scale);
        // upgrade: "피사체를 글자 앞으로" from codex/text-poster.
        var subject = kit.CutOutSubject("피사체");
        // The title sits behind the top of the subject, so the cut-out overlaps its lower part.
        double titleY = subject is { } found ? Math.Clamp(found.Core.Y - titleHeight * .62, h * .07, h * .5) : h * .085;
        var region = new Rect(w * .04, titleY, w * .92, titleHeight);
        spec = spec with { ColorArgb = Readable(c, region, dark, light, accent) };
        var titleLayer = kit.AddText("제목", spec, w * .04 - size * scale * .02, titleY, scale);
        if (subject != null)
        {
            kit.MoveBelow(titleLayer, subject.Layer);
            kit.AddGradientMap("피사체 듀오톤", dark, light, clipped: true);
            kit.AddCurves("피사체 대비", StyleKit.Contrast(.35 + .65 * contrast), clipped: true);
        }
        // Small text blocks in the corners and a column under the title.
        double small = Math.Max(9, unit * .021);
        string font = StyleKit.HasFont("Bahnschrift") ? "Bahnschrift SemiBold SemiCondensed" : "Segoe UI Semibold";
        void Block(string name, string content, double x, double y, TextAlignment alignment, double sizeFactor = 1)
        {
            var text = new TextSpec { Content = content, FontFamily = font, FontSize = small * sizeFactor, LineHeight = small * sizeFactor * 1.18, Tracking = 30, Alignment = alignment, ColorArgb = dark };
            var (tw, th) = StyleKit.Measure(text);
            double left = alignment == TextAlignment.Right ? x - tw : x;
            text = text with { ColorArgb = Readable(c, new Rect(left, y, tw, th), dark, light, dark) };
            kit.AddText(name, text, left - 4, y - 4);
        }
        int year = c.Services.Year;
        Block("작은 글 · 프로젝트", "PROJECT N°01\n" + Loc.T("프로젝트 이름"), w * .04, h * .03, TextAlignment.Left);
        Block("작은 글 · 연도", year.ToString(System.Globalization.CultureInfo.InvariantCulture), w * .96, h * .03, TextAlignment.Right, 1.6);
        Block("작은 글 · 설명", Loc.T("한 줄 설명을 적어 주세요.") + "\n" + Loc.T("작은 글을 여러 곳에 두면\n포스터에 밀도가 생깁니다."), w * .04, Math.Min(region.Bottom + h * .025, h * .74), TextAlignment.Left, .9);
        Block("작은 글 · 장소", "SITE\n" + Loc.T("장소"), w * .04, h * .9, TextAlignment.Left);
        Block("작은 글 · 스튜디오", "DESIGN STUDIO\n" + Loc.T("이름"), w * .96, h * .9, TextAlignment.Right);
        kit.AddGrain("인쇄 입자", TextureKind.Print, .3 + .7 * texture, BlendMode.Overlay, .45 + .45 * texture);
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

    // ---- 5. 반투명 에디토리얼 ----------------------------------------------------------------

    static void Editorial(StyleKit kit)
    {
        var c = kit.Context; double blur = c.Unit("blur"); int position = (int)c.Value("panel");
        int w = c.Width, h = c.Height; double unit = Math.Min(w, h);
        kit.AddCurves("부드러운 톤", [new(0, .07), new(.5, .52), new(1, .95)]);
        kit.AddHueSaturation("차분한 색", 0, -24);
        var panel = position switch
        {
            0 => new Rect(w * .06, h * .08, w * .34, h * .84),
            1 => new Rect(w * .31, h * .1, w * .38, h * .8),
            3 => new Rect(w * .06, h * .6, w * .88, h * .32),
            _ => new Rect(w * .6, h * .08, w * .34, h * .84)
        };
        double radius = (.006 + .03 * blur) * Math.Max(w, h);
        kit.AddBlurredCopy("반투명 패널 · 흐림", radius, StyleKit.RectangleMask(panel));
        double veil = .26 + .16 * blur;
        int pw = Math.Max(1, (int)Math.Round(panel.Width)), ph = Math.Max(1, (int)Math.Round(panel.Height));
        kit.AddShape("반투명 패널 · 종이", new ShapeSpec { Width = pw, Height = ph, FillArgb = 0xFFFFFFFF, StrokeEnabled = false }, panel.X, panel.Y, veil);
        kit.AddShape("반투명 패널 · 테두리", new ShapeSpec { Width = pw, Height = ph, FillEnabled = false, StrokeEnabled = true, StrokeArgb = 0xB3FFFFFF, StrokeWidth = Math.Max(1, unit / 900) }, panel.X, panel.Y);
        // Text over the veiled panel: dark unless the panel stays dark.
        double under = AverageLuminance(c, panel) * (1 - veil) + veil;
        uint ink = under >= .55 ? 0xFF1C1D20u : 0xFFF6F6F2u;
        double margin = Math.Max(8, unit * .03), small = Math.Max(9, unit * .017);
        string sans = StyleKit.HasFont("Segoe UI") ? "Segoe UI" : "Malgun Gothic", serif = StyleKit.HasFont("Georgia") ? "Georgia" : sans;
        string titleText = c.Source.Name.Trim(); if (titleText.Length == 0) titleText = "Title"; if (titleText.Length > 60) titleText = titleText[..60].TrimEnd();
        bool hangul = StyleKit.HasHangul(titleText);
        var caption = new TextSpec { Content = $"VOL. 01  —  {c.Services.Year}", FontFamily = sans, FontSize = small * .85, Tracking = 160, ColorArgb = ink };
        kit.AddText("작은 글 · 머리", caption, panel.X + margin - 4, panel.Y + margin - 4);
        var title = new TextSpec { Content = titleText, FontFamily = hangul ? "Malgun Gothic" : serif, Italic = !hangul, FontSize = small * 2.4, ColorArgb = ink };
        var (tw, _) = StyleKit.Measure(title);
        double room = panel.Width - margin * 2;
        if (tw > room) title = title with { FontSize = Math.Max(small, title.FontSize * room / tw) };
        var titleSize = StyleKit.Measure(title);
        double titleY = panel.Y + margin + small * 2.2;
        kit.AddText("제목", title, panel.X + margin - 4, titleY - 4);
        var body = new TextSpec { Content = Loc.T("본문을 입력하세요.\n짧은 설명이나 날짜, 장소를 적습니다."), FontFamily = sans, FontSize = small, LineHeight = small * 1.55, ColorArgb = ink };
        kit.AddText("본문", body, panel.X + margin - 4, titleY + titleSize.Height + small * 1.2 - 4);
        var folio = new TextSpec { Content = "01", FontFamily = sans, FontSize = small * .85, Tracking = 120, ColorArgb = ink, Alignment = TextAlignment.Right };
        var (fw, fh) = StyleKit.Measure(folio);
        kit.AddText("작은 글 · 쪽", folio, panel.Right - margin - fw - 4, panel.Bottom - margin - fh - 4);
        kit.AddGrain("부드러운 입자", TextureKind.Soft, .5, BlendMode.Overlay, .45);
    }
}
