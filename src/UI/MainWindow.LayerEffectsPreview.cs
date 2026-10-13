using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Offscreen review of 레이어 효과: a collage board of cut-outs with white strokes and drop shadows and a
// highlighted title, glowing text and shapes at night, cut-outs recolored by overlays, the properties
// panel with an open stroke, and the same panel on 간결한 화면. Each board is also saved as its own
// render so the effects can be checked at full size.
public sealed partial class MainWindow
{
    /// <summary>The 레이어 효과 captures (also part of --render-studio-previews; alone with --render-effect-previews).</summary>
    public void RenderLayerEffectPreviews(string directory)
    {
        Directory.CreateDirectory(directory); headlessTesting = true;
        void Save(string name) { using var output = File.Create(Path.Combine(directory, name + ".png")); composite!.WritePng(output); }
        void Show(Document document, Guid select, int open)
        {
            AddTab(document, null); SetWorkspaceMode(true);
            doc.ActiveId = select; selectedLayers.Clear(); selectedLayers.Add(select);
            expandedEffectLayer = select; expandedEffect = open;
            ShowStudioPage(1); PrepareCompactCapture();
        }
        var collage = EffectCollageDocument(out var house);
        Show(collage, house, Array.FindIndex(collage.Layers.Single(l => l.Id == house).Effects!, e => e.Kind == LayerEffectKind.Stroke));
        RenderCompactCapture(directory, "effects-collage", 1480, 920); Save("effects-collage-render");
        RenderPane(studioPanes[1], Path.Combine(directory, "effects-properties.png"), 360, 1500);
        TrimPreviewTabs();
        var night = EffectGlowDocument(out var title);
        Show(night, title, 1);
        RenderCompactCapture(directory, "effects-glow-text", 1480, 920); Save("effects-glow-text-render");
        RenderPane(studioPanes[1], Path.Combine(directory, "effects-glow-properties.png"), 360, 1400);
        TrimPreviewTabs();
        var icons = EffectOverlayDocument(out var tinted);
        Show(icons, tinted, 0);
        RenderCompactCapture(directory, "effects-overlay", 1480, 920); Save("effects-overlay-render");
        RenderPane(studioPanes[1], Path.Combine(directory, "effects-overlay-properties.png"), 360, 1200);
        TrimPreviewTabs();
        // 간결한 화면: the same panel in the dock.
        try
        {
            SetScreenStyle(true);
            Show(EffectCollageDocument(out var compactHouse), compactHouse, 1);
            // Room for the properties: the color and navigator groups folded, the 외형 section closed.
            foreach (var group in dockGroups.Where(g => g.Key is "color" or "navigator")) SetDockGroupCollapsed(group, true);
            SectionHeader.SetCollapsedKeys(["외형"]);
            ShowDockTab("properties"); PrepareCompactCapture();
            RenderCompactCapture(directory, "compact-effects-1480x920", 1480, 920);
            RenderCompactCapture(directory, "compact-effects-properties", 1480, 920, dockOnly: true);
        }
        finally { SectionHeader.SetCollapsedKeys([]); ResetDockLayout(); SetScreenStyle(false); expandedEffect = -1; }
        TrimPreviewTabs();
    }
    // Kraft board with a house and a tree cut from photos, each with a white outside stroke and a drop
    // shadow, a cut-out person, and a title highlighted by a thick outside stroke.
    internal static Document EffectCollageDocument(out Guid house)
    {
        var document = new Document { Width = 1600, Height = 1000, Name = Loc.T("레이어 효과 예시 · 콜라주") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = PaperBoard(1600, 1000) });
        var shadow = LayerEffect.Default(LayerEffectKind.DropShadow) with { Distance = 16, Angle = 60, Size = 20, Opacity = .5 };
        var building = new Layer
        {
            Name = Loc.T("오린 사진 · 집"), X = 170, Y = 300, Rotation = -4, Pixels = Imaging.Draw(560, 480, dc =>
            {
                var wall = new LinearGradientBrush(Color.FromRgb(0xE9, 0xDC, 0xC4), Color.FromRgb(0xC9, 0xB1, 0x8E), 90); wall.Freeze();
                var roof = new LinearGradientBrush(Color.FromRgb(0x8A, 0x3B, 0x2E), Color.FromRgb(0x5A, 0x24, 0x1C), 90); roof.Freeze();
                var glass = new LinearGradientBrush(Color.FromRgb(0x9F, 0xC7, 0xE0), Color.FromRgb(0x4E, 0x77, 0x95), 60); glass.Freeze();
                var wood = new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x32)); wood.Freeze();
                dc.DrawGeometry(roof, null, Geometry.Parse("M10 210 L280 18 L550 210 L520 228 L280 58 L40 228 Z"));
                dc.DrawGeometry(wall, null, Geometry.Parse("M60 214 L280 62 L500 214 L500 470 L60 470 Z"));
                dc.DrawRectangle(wood, null, new Rect(380, 40, 44, 110));
                foreach (var x in new[] { 110d, 300d }) dc.DrawRoundedRectangle(glass, new Pen(Brushes.White, 6), new Rect(x, 260, 110, 90), 4, 4);
                dc.DrawRoundedRectangle(glass, new Pen(Brushes.White, 6), new Rect(232, 128, 96, 70), 4, 4);
                dc.DrawRectangle(wood, null, new Rect(236, 360, 88, 110));
            }),
            Effects = [shadow, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 12 }]
        };
        document.Add(building);
        document.Add(new Layer
        {
            Name = Loc.T("오린 사진 · 나무"), X = 870, Y = 260, Rotation = 3, Pixels = Imaging.Draw(360, 520, dc =>
            {
                var leaves = new RadialGradientBrush(Color.FromRgb(0x9B, 0xC4, 0x6A), Color.FromRgb(0x3E, 0x6B, 0x35)) { GradientOrigin = new Point(.35, .3) }; leaves.Freeze();
                var bark = new LinearGradientBrush(Color.FromRgb(0x7A, 0x58, 0x3C), Color.FromRgb(0x4A, 0x33, 0x22), 0); bark.Freeze();
                dc.DrawGeometry(bark, null, Geometry.Parse("M160 300 L200 300 L212 516 L148 516 Z"));
                dc.DrawGeometry(leaves, null, Geometry.Parse("M180 20 C260 20 300 90 300 150 C350 190 340 300 260 320 C220 360 140 360 100 320 C20 300 10 190 60 150 C60 90 100 20 180 20 Z"));
            }),
            Effects = [shadow, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 10 }]
        });
        var person = new Layer
        {
            Name = Loc.T("오린 사진 · 사람"), X = 1280, Y = 470, Pixels = Imaging.Draw(150, 400, dc =>
            {
                var coat = new LinearGradientBrush(Color.FromRgb(0x3C, 0x5A, 0x8C), Color.FromRgb(0x24, 0x36, 0x58), 90); coat.Freeze();
                var skin = new SolidColorBrush(Color.FromRgb(0xE8, 0xC1, 0x9E)); skin.Freeze();
                var trousers = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x30)); trousers.Freeze();
                dc.DrawEllipse(skin, null, new Point(75, 42), 28, 32);
                dc.DrawRoundedRectangle(coat, null, new Rect(30, 82, 90, 170), 30, 30);
                dc.DrawRoundedRectangle(trousers, null, new Rect(40, 230, 30, 165), 12, 12);
                dc.DrawRoundedRectangle(trousers, null, new Rect(80, 230, 30, 165), 12, 12);
            }),
            Effects = [shadow, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 8 }]
        };
        document.Add(person);
        var heading = DocumentFeatures.CreateText(new TextSpec { Content = Loc.T("우리 동네 콜라주"), FontFamily = "Malgun Gothic", FontSize = 84, Bold = true, ColorArgb = 0xFF1E232B }, 170, 90);
        heading.Name = Loc.T("제목"); heading.Effects = [LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 14, ColorArgb = 0xFFFFE45C }];
        document.Add(heading);
        house = building.Id;
        return document;
    }

    // Night street: glowing title, lit windows and a moon.
    internal static Document EffectGlowDocument(out Guid title)
    {
        var document = new Document { Width = 1400, Height = 900, Name = Loc.T("레이어 효과 예시 · 빛나는 글자") };
        var sky = new Raster(1400, 900);
        for (int y = 0; y < 900; y++) for (int x = 0; x < 1400; x++)
        {
            int i = (y * 1400 + x) * 4; double t = y / 900.0;
            sky.Data[i] = Imaging.Byte(70 - 40 * t); sky.Data[i + 1] = Imaging.Byte(30 - 14 * t); sky.Data[i + 2] = Imaging.Byte(22 - 10 * t); sky.Data[i + 3] = 255;
        }
        document.Add(new Layer { Name = Loc.T("밤하늘"), Pixels = sky });
        var moon = VectorShapes.Create(new ShapeSpec { Kind = ShapeKind.Ellipse, Width = 120, Height = 120, FillArgb = 0xFFFFF4D6 }, 1120, 110);
        moon.Name = Loc.T("달"); moon.Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow) with { ColorArgb = 0xFFFFE9B0, Size = 70, Spread = .05, Opacity = .7, Blend = BlendMode.Screen }];
        document.Add(moon);
        var heading = DocumentFeatures.CreateText(new TextSpec { Content = Loc.T("오늘 밤 야경"), FontFamily = "Malgun Gothic", FontSize = 150, Bold = true, ColorArgb = 0xFFFFF6D8 }, 150, 230);
        heading.Name = Loc.T("빛나는 제목");
        heading.Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow) with { ColorArgb = 0xFFFFB13B, Size = 36, Spread = 0, Opacity = .9, Blend = BlendMode.Screen },
            LayerEffect.Default(LayerEffectKind.InnerGlow) with { ColorArgb = 0xFFFFC870, Size = 5, Opacity = .55 }];
        document.Add(heading);
        for (int i = 0; i < 6; i++)
        {
            var window = VectorShapes.Create(new ShapeSpec { Width = 90, Height = 130, CornerRadius = 6, FillArgb = 0xFFFFD27A }, 170 + i * 180, 600);
            window.Name = Loc.T("창문") + $" {i + 1}";
            window.Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow) with { ColorArgb = 0xFFFFC560, Size = 30 + i * 4, Opacity = .8, Blend = BlendMode.Screen }];
            document.Add(window);
        }
        title = heading.Id;
        return document;
    }

    // One cut-out four times: as it is, recolored by a color overlay (normal and multiply) and by a gradient overlay.
    internal static Document EffectOverlayDocument(out Guid tinted)
    {
        var document = new Document { Width = 1400, Height = 760, Name = Loc.T("레이어 효과 예시 · 색 바꾸기") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(1400, 760, Color.FromRgb(0xF6, 0xF4, 0xEF)) });
        var chair = Imaging.Draw(240, 300, dc =>
        {
            var seat = new LinearGradientBrush(Color.FromRgb(0xD9, 0xA0, 0x62), Color.FromRgb(0x9C, 0x62, 0x30), 90); seat.Freeze();
            var legs = new SolidColorBrush(Color.FromRgb(0x5B, 0x3A, 0x22)); legs.Freeze();
            dc.DrawRoundedRectangle(seat, null, new Rect(40, 10, 160, 150), 22, 22);
            dc.DrawRoundedRectangle(seat, null, new Rect(20, 150, 200, 50), 14, 14);
            foreach (var x in new[] { 34d, 186d }) dc.DrawRoundedRectangle(legs, null, new Rect(x, 196, 20, 100), 6, 6);
        });
        string[] captions = ["원본", "색 오버레이", "곱하기", "그라데이션 오버레이"];
        LayerEffect[]?[] looks =
        [
            null,
            [LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFF3A6FDB }],
            [LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFF7FD3A8, Blend = BlendMode.Multiply }],
            [LayerEffect.Default(LayerEffectKind.GradientOverlay) with { ColorArgb = 0xFFFFB86B, EndColorArgb = 0xFFC2416B, Angle = 60 }, LayerEffect.Default(LayerEffectKind.Stroke) with { Size = 6, ColorArgb = 0xFF1E232B }]
        ];
        Guid second = Guid.Empty;
        for (int i = 0; i < 4; i++)
        {
            var layer = new Layer { Name = Loc.T("오린 사진 · 의자") + $" {i + 1}", Pixels = chair, X = 90 + i * 330, Y = 180, Effects = looks[i] };
            document.Add(layer); if (i == 1) second = layer.Id;
            var label = DocumentFeatures.CreateText(new TextSpec { Content = Loc.T(captions[i]), FontFamily = "Malgun Gothic", FontSize = 30, ColorArgb = 0xFF3B414B }, 90 + i * 330, 520);
            label.Name = Loc.T(captions[i]); document.Add(label);
        }
        tinted = second;
        return document;
    }

    // Warm kraft board with a little fibre.
    static Raster PaperBoard(int width, int height)
    {
        var paper = new Raster(width, height);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                uint hash = unchecked((uint)(x * 73856093 ^ y * 19349663)); hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15;
                double noise = (hash & 255) / 255.0 - .5;
                int i = (y * width + x) * 4;
                paper.Data[i] = Imaging.Byte(164 + noise * 10); paper.Data[i + 1] = Imaging.Byte(196 + noise * 10); paper.Data[i + 2] = Imaging.Byte(216 + noise * 10); paper.Data[i + 3] = 255;
            }
        });
        return paper;
    }
}
