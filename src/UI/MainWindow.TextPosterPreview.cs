using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Offscreen review of poster text: the text panel with its outline rows, a hollow outlined title
// with a justified paragraph box, and 피사체를 글자 앞으로 with the layer list afterwards. The photo is
// drawn here (a tower against the sky) and its subject mask stands in for the local model.
public sealed partial class MainWindow
{
    static void RenderTextPosterPreviews(string directory)
    {
        var poster = new MainWindow(null) { headlessTesting = true };
        try
        {
            var document = PosterSample(out var photo, out var hollow);
            poster.AddTab(document, null); poster.SetWorkspaceMode(true);
            poster.SelectLayer(hollow.Id); poster.ShowStudioPage(1);
            poster.RenderPane(poster.studioPanes[1], Path.Combine(directory, "text-properties-outline.png"), 360, 1400);
            poster.RenderPreview(Path.Combine(directory, "text-poster-title.png"));
            poster.subjectCutOut = (pixels, _) => PosterSubject(pixels.Width, pixels.Height);
            poster.SelectLayer(photo.Id);
            WaitOnDispatcher(poster.PlaceSubjectInFrontAsync);
            poster.RenderPreview(Path.Combine(directory, "subject-front.png"));
        }
        finally { poster.StopRenderingForShutdown(); }
    }

    static string PosterFont(params string[] names) =>
        names.FirstOrDefault(name => Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase))) ?? "Segoe UI";

    // A tall tower against a warm evening sky, a huge red title, a hollow outlined name and two small text blocks.
    internal static Document PosterSample(out Layer photo, out Layer hollow)
    {
        const int width = 800, height = 1000;
        var document = new Document { Width = width, Height = height, Name = Loc.T("포스터 예시 · 피사체를 글자 앞으로") };
        var pixels = new Raster(width, height); var tower = PosterSubject(width, height);
        for (int y = 0; y < height; y++)
        {
            double t = y / (double)height;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                double hill = 900 + 26 * Math.Sin(x / 70.0) + 14 * Math.Sin(x / 23.0);
                (double r, double g, double b) sky = (218 + 32 * t, 228 - 14 * t, 240 - 70 * t);
                (double r, double g, double b) color = y > hill ? (36, 58, 44) : sky;
                if (tower[y * width + x] > 0) { double shade = .82 + .18 * Math.Sin(x / 9.0); color = (52 * shade, 56 * shade, 66 * shade); }
                pixels.Data[i] = Imaging.Byte(color.b); pixels.Data[i + 1] = Imaging.Byte(color.g); pixels.Data[i + 2] = Imaging.Byte(color.r); pixels.Data[i + 3] = 255;
            }
        }
        document.Add(photo = new Layer { Name = Loc.T("포스터 사진"), Pixels = pixels });
        string condensed = PosterFont("Impact", "Bahnschrift SemiBold Condensed", "Arial Black");
        // Titles sized to span the poster whatever condensed face this PC has.
        Layer Spanning(TextSpec spec, double share, double y)
        {
            double size = Math.Clamp(spec.FontSize * width * share / DocumentFeatures.RenderText(spec).Width, 8, 1024);
            var layer = DocumentFeatures.CreateText(spec with { FontSize = Math.Round(size) }, 0, y); layer.X = (width - layer.Pixels.Width) / 2.0; return layer;
        }
        document.Add(Spanning(new TextSpec { Content = "TOWER", FontFamily = condensed, FontSize = 300, ColorArgb = 0xFFE5412F }, .96, 380));
        document.Add(hollow = Spanning(new TextSpec { Content = "MORUPIXEL", FontFamily = condensed, FontSize = 130, ColorArgb = 0xFFFFFFFF,
            Outline = true, OutlineOnly = true, OutlineWidth = 3, OutlinePosition = TextOutlinePosition.Center, OutlineArgb = 0xFF14161A }, .9, 70));
        document.Add(DocumentFeatures.CreateText(new TextSpec { Content = Loc.T("작은 글 상자는 정해진 폭 안에서 낱말 사이로 줄을 바꾸고, 양쪽 정렬로 두 가장자리를 맞춥니다. 큰 제목 둘레에 여러 개를 두면 편집 디자인처럼 보입니다."),
            FontFamily = "Malgun Gothic", FontSize = 17, LineHeight = 27, ColorArgb = 0xFF14161A, BoxWidth = 280, Alignment = TextAlignment.Justify }, 40, 730));
        document.Add(DocumentFeatures.CreateText(new TextSpec { Content = "FOR A MOMENT\nEVERY LINE\nMEETS THE EDGE", FontFamily = condensed, FontSize = 26, LineHeight = 30,
            ColorArgb = 0xFF14161A, BoxWidth = 210, Alignment = TextAlignment.Right, Tracking = 60 }, 550, 730));
        document.ActiveId = photo.Id;
        return document;
    }

    // The tower: antenna, observation disc and shaft widening to its base.
    internal static byte[] PosterSubject(int width, int height)
    {
        var mask = new byte[width * height];
        double cx = width / 2.0, sx = width / 800.0, sy = height / 1000.0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double px = (x + .5) / sx, py = (y + .5) / sy, dx = Math.Abs(px - cx / sx);
            bool antenna = py is >= 60 and < 340 && dx < 4 + (py - 60) / 90;
            bool disc = Math.Pow((px - cx / sx) / 112, 2) + Math.Pow((py - 425) / 40, 2) <= 1;
            bool cabin = py is >= 330 and < 480 && dx < 50 - (py - 330) / 10;
            bool shaft = py is >= 450 and < 960 && dx < 30 + Math.Pow((py - 450) / 510, 2) * 80;
            if (antenna || disc || cabin || shaft) mask[y * width + x] = 255;
        }
        return mask;
    }
}
