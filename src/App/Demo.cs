using System.IO;

namespace Compositor.Windows;

public static class Demo
{
    // The sample is embedded so a published, standalone build can open it without
    // depending on a working directory or a separate image file.
    const string ArtworkResource = "Morupixel.Sample.SeaWindow";

    public static Document Create()
    {
        using var artwork = typeof(Demo).Assembly.GetManifestResourceStream(ArtworkResource)
            ?? throw new InvalidDataException($"샘플 이미지 리소스를 찾을 수 없습니다: {ArtworkResource}");
        var background = Raster.Load(artwork);
        var doc = new Document { Name = "바다를 향한 창 · 샘플", Width = background.Width, Height = background.Height };
        doc.Add(new Layer { Name = "01 · 바다를 향한 창 · 원본 이미지", Pixels = background });

        // Text remains truly editable. The optional group starts hidden so the
        // document opens with the original artwork, free of overlaid copy.
        var typography = DocumentFeatures.CreateGroup(doc, "02 · 선택형 타이포그래피 (표시 전환)");
        typography.Visible = false;
        doc.Add(typography);

        void AddText(string content, string name, double x, double y, double size, uint color, bool bold)
        {
            var layer = DocumentFeatures.CreateText(new TextSpec
            {
                Content = content,
                FontFamily = "Malgun Gothic",
                FontSize = size,
                ColorArgb = color,
                Bold = bold
            }, x, y);
            layer.Name = name;
            layer.ParentId = typography.Id;
            doc.Add(layer);
        }

        AddText("바다를 향한 창", "제목 · 편집 가능한 텍스트", 667, 92, 55, 0xFFF9F7ED, true);
        AddText("SEA WINDOW  /  MORUPIXEL", "작은 문구 · 편집 가능한 텍스트", 673, 162, 18, 0xFFF5F5EE, false);

        doc.ActiveId = doc.Layers[0].Id;
        return doc;
    }
}
