using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed record PackageImport(Document Document, IReadOnlyList<string> Warnings);

/// <summary>Subset interop with upstream ProjectStore.swift v1–7 directory packages. Unsupported semantics fail explicitly.</summary>
public static class CompositorPackage
{
    public static PackageImport Import(string path)
    {
        string root = Path.GetFullPath(File.Exists(path) && Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)! : path);
        if (!Directory.Exists(root)) throw new InvalidDataException("Compositor .comp는 폴더 패키지입니다. 압축을 푼 .comp 폴더의 manifest.json을 선택하세요.");
        var metadata = SafeFile(root, "manifest.json", 4 * 1024 * 1024);
        using var json = JsonDocument.Parse(File.ReadAllBytes(metadata)); var m = json.RootElement;
        if (String(m, "format") != "com.compositor.project" || Integer(m, "version") < 1 || Integer(m, "version") > 7 || String(m, "colorSpace") != "sRGB")
            throw new InvalidDataException("지원 범위는 sRGB Compositor 프로젝트 버전 1–7입니다.");
        var doc = new Document { Width = Integer(m, "width"), Height = Integer(m, "height"), Name = Path.GetFileNameWithoutExtension(root) };
        Raster.ValidateSize(doc.Width, doc.Height);
        var records = m.GetProperty("layers");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > Document.MaxLayers) throw new InvalidDataException("레이어 수가 지원 한도를 초과합니다.");
        var warnings = new List<string>();
        if (Number(m, "resolution", 72) != 96) warnings.Add("인쇄 해상도(PPI) 메타데이터는 변환하지 않습니다. 캔버스 픽셀 크기는 유지합니다.");
        foreach (var record in records.EnumerateArray())
        {
            Guid id = Guid.Parse(String(record, "id")); string name = String(record, "name");
            foreach (string unsupported in new[] { "maskSourceID", "maskPlacement", "shape" })
                if (Has(record, unsupported)) throw Unsupported(name, unsupported);
            if (Has(record, "effects") && record.GetProperty("effects").EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null)) throw Unsupported(name, "레이어 효과");
            if (!Boolean(record, "maskLinked", true) || !Boolean(record, "maskEnabled", true)) throw Unsupported(name, "연결 해제/비활성 마스크");
            bool group = Boolean(record, "isGroup", false), adjustment = Has(record, "adjustment");
            if (group && adjustment) throw new InvalidDataException("그룹에 조정 속성이 포함되어 있습니다.");
            Raster pixels;
            if (group || adjustment)
            {
                if (Has(record, "imageFile")) throw new InvalidDataException("그룹/조정 레이어에 이미지 파일이 있습니다.");
                pixels = new Raster(doc.Width, doc.Height);
            }
            else
            {
                var imageName = String(record, "imageFile");
                if (!imageName.Equals(id.ToString().ToUpperInvariant() + ".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("레이어 이미지 경로가 올바르지 않습니다.");
                using var s = File.OpenRead(SafeFile(root, Path.Combine("images", imageName), 100L * 1024 * 1024)); pixels = Raster.Load(s);
            }
            var layer = new Layer { Id = id, Name = name, Pixels = pixels, Kind = group ? LayerKind.Group : adjustment ? LayerKind.Adjustment : LayerKind.Raster,
                Visible = Boolean(record, "isVisible", true), Opacity = Number(record, "opacity", 1), Blend = ParseBlend(String(record, "blendMode", "Normal")),
                ParentId = Has(record, "parentID") ? Guid.Parse(String(record, "parentID")) : null };
            var transform = record.GetProperty("transform");
            var origin = Pair(transform.GetProperty("origin")); var size = Pair(transform.GetProperty("size"));
            if (size.X < 1 || size.Y < 1 || size.X > 300_000 || size.Y > 300_000) throw new InvalidDataException("레이어 변형 크기가 올바르지 않습니다.");
            layer.X = origin.X; layer.Y = origin.Y; layer.ScaleX = size.X / pixels.Width; layer.ScaleY = size.Y / pixels.Height;
            layer.Rotation = Number(transform, "rotation", 0); layer.FlipX = Boolean(transform, "flipX", false); layer.FlipY = Boolean(transform, "flipY", false);
            if (String(transform, "sampling", "High quality") != "High quality") warnings.Add($"{name}: 원본 샘플링 설정 대신 Morupixel의 보간을 사용합니다.");
            if (group && (layer.Opacity != 1 || layer.Blend != BlendMode.Normal)) throw Unsupported(name, "그룹 불투명도/혼합");
            if (adjustment) layer.Adjustment = ImportAdjustment(record.GetProperty("adjustment"), name);
            if (Has(record, "text"))
            {
                if (group || adjustment) throw new InvalidDataException("텍스트 레이어 형식이 올바르지 않습니다.");
                var t = record.GetProperty("text");
                if (Number(t, "tracking", 0) != 0 || Number(t, "leading", 0) != 0 || Has(t, "boxSize")) throw Unsupported(name, "자간/행간/문단 텍스트 상자");
                layer.Text = new TextSpec { Content = String(t, "content"), FontFamily = String(t, "fontName", "Segoe UI"), FontSize = Number(t, "fontSize", 72), ColorArgb = ReadColor(t),
                    Alignment = String(t, "alignment", "Left") switch { "Left" => TextAlignment.Left, "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => throw Unsupported(name, "텍스트 정렬") } };
                layer.Kind = LayerKind.Text;
                warnings.Add($"{name}: 원본 텍스트 픽셀을 유지했습니다. 글자를 수정하면 Windows 글꼴/텍스트 엔진에 따라 모양과 줄바꿈이 달라질 수 있습니다.");
            }
            if (Has(record, "maskFile"))
            {
                string file = String(record, "maskFile");
                if (!file.Equals(id.ToString().ToUpperInvariant() + ".mask.png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("마스크 경로가 올바르지 않습니다.");
                using var s = File.OpenRead(SafeFile(root, Path.Combine("images", file), 100L * 1024 * 1024));
                var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                var frame = decoder.Frames[0]; Raster.ValidateSize(frame.PixelWidth, frame.PixelHeight);
                if (frame.Format != PixelFormats.Gray8) throw new InvalidDataException("Compositor 마스크는 8비트 회색조 PNG여야 합니다.");
                var mask = Raster.FromBitmap(frame);
                if (mask.Width != pixels.Width || mask.Height != pixels.Height)
                {
                    mask = ImportExport.Resize(mask, pixels.Width, pixels.Height);
                    warnings.Add($"{name}: 원본 마스크를 레이어 픽셀 크기로 재샘플링했습니다.");
                }
                layer.Mask = Enumerable.Range(0, pixels.Width * pixels.Height).Select(i => mask.Data[i * 4]).ToArray();
            }
            doc.Add(layer);
        }
        doc.ActiveId = Has(m, "activeLayerID") ? Guid.Parse(String(m, "activeLayerID")) : Guid.Empty;
        doc.Validate();
        ValidateGroupInterop(doc);
        warnings.Add(".comp는 지원 부분만 변환합니다. 저장 전 화면을 확인하세요. 지원하지 않는 기능을 포함한 파일은 가져오기를 중단합니다.");
        return new PackageImport(doc, warnings.Distinct().ToArray());
    }

    /// <summary>Creates a new directory package only. Existing projects are never replaced.</summary>
    public static IReadOnlyList<string> Export(Document doc, string path)
    {
        doc.Validate(); ValidateGroupInterop(doc); string full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("대상 .comp 폴더가 이미 있습니다. 새 이름을 사용하세요.");
        var layers = new List<object>(); var warnings = new List<string>();
        foreach (var layer in doc.Layers)
        {
            double width = layer.Pixels.Width * layer.Scale * layer.ScaleX, height = layer.Pixels.Height * layer.Scale * layer.ScaleY;
            if (width < 1 || height < 1 || width > 300_000 || height > 300_000) throw Unsupported(layer.Name, "원본 범위를 벗어난 변형 크기");
            if (layer.Warp != null || layer.Clipped) throw Unsupported(layer.Name, "원근 왜곡/클리핑 마스크");
            if (layer.Kind == LayerKind.Group && (layer.Opacity != 1 || layer.Blend != BlendMode.Normal)) throw Unsupported(layer.Name, "그룹 불투명도/혼합");
            if (layer.Shape != null) warnings.Add($"{layer.Name}: 도형은 .comp에서 픽셀 이미지로 저장됩니다. 벡터 속성은 .moruproj로 보존하세요.");
            if (layer.Locked) warnings.Add($"{layer.Name}: 원본 형식에 잠금 속성이 없어 잠금 상태는 저장하지 않습니다.");
            object? text = null;
            if (layer.Text is { } advancedText && (advancedText.Tracking != 0 || advancedText.LineHeight != 0))
            {
                warnings.Add($"{layer.Name}: 자간·줄 간격이 있는 텍스트는 .comp에서 모양을 유지하는 픽셀 이미지로 저장됩니다. 편집 가능한 문자 속성은 .moruproj로 보존하세요.");
            }
            else if (layer.Text is { } t)
            {
                if (t.Bold || t.Italic || (t.ColorArgb >> 24) != 255) throw Unsupported(layer.Name, "굵게/기울임/반투명 텍스트 스타일");
                text = new { content = t.Content, fontName = t.FontFamily, fontSize = t.FontSize, red = ((t.ColorArgb >> 16) & 255) / 255.0, green = ((t.ColorArgb >> 8) & 255) / 255.0, blue = (t.ColorArgb & 255) / 255.0,
                    alignment = t.Alignment.ToString(), tracking = 0, leading = 0 };
                warnings.Add("텍스트의 현재 픽셀은 보존되지만 Mac에서 수정 시 글꼴/줄바꿈이 달라질 수 있습니다.");
            }
            string id = layer.Id.ToString().ToUpperInvariant();
            layers.Add(new { id, name = layer.Name, isVisible = layer.Visible, parentID = layer.ParentId?.ToString().ToUpperInvariant(), isGroup = layer.Kind == LayerKind.Group,
                opacity = layer.Opacity, blendMode = BlendName(layer.Blend),
                transform = new { origin = new[] { layer.X, layer.Y }, size = new[] { layer.Pixels.Width * layer.Scale * layer.ScaleX, layer.Pixels.Height * layer.Scale * layer.ScaleY }, rotation = layer.Rotation, flipX = layer.FlipX, flipY = layer.FlipY, sampling = "High quality" },
                imageFile = layer.Kind is LayerKind.Group or LayerKind.Adjustment ? null : id + ".png", maskFile = layer.Mask == null ? null : id + ".mask.png", maskEnabled = layer.Mask == null ? (bool?)null : true,
                adjustment = layer.Adjustment == null ? null : ExportAdjustment(layer.Adjustment, layer.Name), text });
        }
        var manifest = new { format = "com.compositor.project", version = 7, colorSpace = "sRGB", resolution = 96, documentID = Guid.NewGuid().ToString().ToUpperInvariant(), width = doc.Width, height = doc.Height,
            activeLayerID = doc.ActiveId == Guid.Empty ? null : doc.ActiveId.ToString().ToUpperInvariant(), layers };
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("패키지 메타데이터가 너무 큽니다.");
        string staging = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "images"));
            File.WriteAllBytes(Path.Combine(staging, "manifest.json"), metadata);
            foreach (var layer in doc.Layers)
            {
                string id = layer.Id.ToString().ToUpperInvariant();
                if (layer.Kind is not (LayerKind.Group or LayerKind.Adjustment)) using (var s = File.Create(Path.Combine(staging, "images", id + ".png"))) layer.Pixels.WritePng(s);
                if (layer.Mask != null)
                {
                    var bitmap = BitmapSource.Create(layer.Pixels.Width, layer.Pixels.Height, 96, 96, PixelFormats.Gray8, null, layer.Mask, layer.Pixels.Width);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var s = File.Create(Path.Combine(staging, "images", id + ".mask.png")); encoder.Save(s);
                }
            }
            Directory.Move(staging, full);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return warnings.Distinct().ToArray();
    }

    static AdjustmentSpec ImportAdjustment(JsonElement a, string name)
    {
        switch (String(a, "kind"))
        {
            case "Levels":
                var ranges = a.GetProperty("levels").GetProperty("ranges");
                if (ranges.GetArrayLength() != 4) throw new InvalidDataException("레벨 채널은 RGB/빨강/초록/파랑 네 개여야 합니다.");
                static LevelsRange ReadRange(JsonElement v) => new() { Black = Number(v, "black", 0), White = Number(v, "white", 255), Gamma = Number(v, "gamma", 1), OutputBlack = Number(v, "outputBlack", 0), OutputWhite = Number(v, "outputWhite", 255) };
                var v = ReadRange(ranges[0]); return new AdjustmentSpec { Kind = AdjustmentKind.Levels, Black = v.Black, White = v.White, Gamma = v.Gamma, OutputBlack = v.OutputBlack, OutputWhite = v.OutputWhite,
                    RedLevels = ReadRange(ranges[1]), GreenLevels = ReadRange(ranges[2]), BlueLevels = ReadRange(ranges[3]) };
            case "Curves":
                var channels = a.GetProperty("curves").GetProperty("channels");
                if (channels.GetArrayLength() != 4) throw new InvalidDataException("곡선 채널은 RGB/빨강/초록/파랑 네 개여야 합니다.");
                static CurvePoint[] ReadCurve(JsonElement channel)
                {
                    var points = channel.EnumerateArray().Select(p => new CurvePoint(Number(p, "x") / 255, Number(p, "y") / 255)).ToArray();
                    if (points.Length < 2 || points.Length > 32 || points[0].X != 0 || points[^1].X != 1) throw new InvalidDataException("원본 곡선 끝점/점 수가 유효하지 않습니다.");
                    return points;
                }
                return new AdjustmentSpec { Kind = AdjustmentKind.Curves, Curve = ReadCurve(channels[0]), RedCurve = ReadCurve(channels[1]), GreenCurve = ReadCurve(channels[2]), BlueCurve = ReadCurve(channels[3]) };
            case "Exposure":
                var exposure = Has(a, "exposureSettings") ? a.GetProperty("exposureSettings") : default;
                return new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = Number(exposure, "exposure", 0), Offset = Number(exposure, "offset", 0), ExposureGamma = Number(exposure, "gamma", 1) };
            case "Gradient Map":
                if (!Has(a, "gradientMapSettings")) return new AdjustmentSpec { Kind = AdjustmentKind.GradientMap };
                var gradient = a.GetProperty("gradientMapSettings"); uint dark = ReadColor(gradient.GetProperty("shadows")), light = ReadColor(gradient.GetProperty("highlights"));
                return new AdjustmentSpec { Kind = AdjustmentKind.GradientMap, DarkColor = Boolean(gradient, "reversed", false) ? light : dark, LightColor = Boolean(gradient, "reversed", false) ? dark : light };
            case "Grain":
                var grain = Has(a, "grainSettings") ? a.GetProperty("grainSettings") : default;
                double seed = Number(grain, "seed", 0);
                if (seed < 0 || seed > uint.MaxValue || seed != Math.Truncate(seed)) throw new InvalidDataException("그레인 시드가 유효하지 않습니다.");
                return new AdjustmentSpec { Kind = AdjustmentKind.Grain, Amount = Number(grain, "amount", 25) / 100, GrainSize = Number(grain, "size", 1.5), GrainRoughness = Number(grain, "roughness", 50) / 100, Seed = unchecked((int)(uint)seed) };
            default: throw Unsupported(name, "조정 레이어 " + String(a, "kind"));
        }
    }

    static object ExportAdjustment(AdjustmentSpec a, string name)
    {
        static object WriteRange(LevelsRange value) => new { black = value.Black, gamma = value.Gamma, white = value.White, outputBlack = value.OutputBlack, outputWhite = value.OutputWhite };
        var master = new LevelsRange { Black = a.Black, White = a.White, Gamma = a.Gamma, OutputBlack = a.OutputBlack, OutputWhite = a.OutputWhite };
        var channels = Enumerable.Range(0, 4).Select(_ => new[] { new { x = 0d, y = 0d }, new { x = 255d, y = 255d } }).ToArray();
        var result = new Dictionary<string, object> { ["kind"] = a.Kind.ToString(), ["hue"] = 0, ["saturation"] = 0, ["lightness"] = 0, ["colorize"] = false,
            ["levels"] = new { channel = "RGB", ranges = new[] { WriteRange(master), WriteRange(a.RedLevels), WriteRange(a.GreenLevels), WriteRange(a.BlueLevels) } }, ["curves"] = new { channel = "RGB", channels } };
        switch (a.Kind)
        {
            case AdjustmentKind.Levels: break;
            case AdjustmentKind.Curves:
                var curves = new[] { a.Curve, a.RedCurve, a.GreenCurve, a.BlueCurve };
                for (int i = 0; i < curves.Length; i++)
                {
                    if (curves[i].Length > 32 || curves[i][0].X != 0 || curves[i][^1].X != 1) throw Unsupported(name, "곡선은 0/255 끝점과 최대 32개 점 필요");
                    channels[i] = curves[i].Select(p => new { x = p.X * 255, y = p.Y * 255 }).ToArray();
                }
                break;
            case AdjustmentKind.Exposure: result["exposureSettings"] = new { exposure = a.Exposure, offset = a.Offset, gamma = a.ExposureGamma }; break;
            case AdjustmentKind.GradientMap:
                if ((a.DarkColor >> 24) != 255 || (a.LightColor >> 24) != 255) throw Unsupported(name, "반투명 그라디언트 색");
                static object Color(uint c) => new { red = ((c >> 16) & 255) / 255d, green = ((c >> 8) & 255) / 255d, blue = (c & 255) / 255d };
                result["kind"] = "Gradient Map"; result["gradientMapSettings"] = new { shadows = Color(a.DarkColor), highlights = Color(a.LightColor), reversed = false }; break;
            case AdjustmentKind.Grain: result["grainSettings"] = new { amount = a.Amount * 100, size = a.GrainSize, roughness = a.GrainRoughness * 100, seed = unchecked((uint)a.Seed) }; break;
            default: throw Unsupported(name, "조정 레이어 " + a.Kind);
        }
        return result;
    }

    static void ValidateGroupInterop(Document doc)
    {
        foreach (var layer in doc.Layers)
        {
            if (layer.Kind == LayerKind.Group && layer.Mask != null) throw Unsupported(layer.Name, "패스스루 그룹 마스크");
            if (layer.ParentId != null && (layer.Blend != BlendMode.Normal || layer.Kind == LayerKind.Adjustment)) throw Unsupported(layer.Name, "패스스루 그룹 내부 혼합/조정");
        }
    }

    static string SafeFile(string root, string relative, long maximum)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("패키지 밖의 파일 경로입니다.");
        for (string? current = file; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("링크가 포함된 패키지는 지원하지 않습니다.");
            if (current.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        var info = new FileInfo(file); if (!info.Exists || info.Length > maximum) throw new InvalidDataException("패키지 파일이 없거나 너무 큽니다.");
        return file;
    }
    static bool Has(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null;
    static string String(JsonElement e, string p, string? fallback = null) => Has(e, p) ? e.GetProperty(p).GetString() ?? throw new InvalidDataException(p) : fallback ?? throw new InvalidDataException("필수 속성이 없습니다: " + p);
    static double Number(JsonElement e, string p, double? fallback = null)
    {
        double n = Has(e, p) ? e.GetProperty(p).GetDouble() : fallback ?? throw new InvalidDataException("필수 속성이 없습니다: " + p);
        return double.IsFinite(n) ? n : throw new InvalidDataException("유효하지 않은 수: " + p);
    }
    static int Integer(JsonElement e, string p)
    {
        double n = Number(e, p);
        if (n != Math.Truncate(n) || n < int.MinValue || n > int.MaxValue) throw new InvalidDataException("정수가 필요합니다: " + p);
        return (int)n;
    }
    static bool Boolean(JsonElement e, string p, bool fallback) => Has(e, p) ? e.GetProperty(p).GetBoolean() : fallback;
    static Point Pair(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 2) throw new InvalidDataException("좌표 형식이 올바르지 않습니다.");
        double x = a[0].GetDouble(), y = a[1].GetDouble();
        return double.IsFinite(x) && double.IsFinite(y) ? new Point(x, y) : throw new InvalidDataException("좌표가 유효하지 않습니다.");
    }
    static uint ReadColor(JsonElement e)
    {
        var values = new[] { Number(e, "red", 0), Number(e, "green", 0), Number(e, "blue", 0) };
        if (values.Any(v => v < 0 || v > 1)) throw new InvalidDataException("색상이 유효하지 않습니다.");
        return 0xff000000u | (uint)Imaging.Byte(values[0] * 255) << 16 | (uint)Imaging.Byte(values[1] * 255) << 8 | Imaging.Byte(values[2] * 255);
    }
    static BlendMode ParseBlend(string text) => Enum.TryParse<BlendMode>(text.Replace(" ", ""), out var mode) && Enum.IsDefined(mode) ? mode : throw new NotSupportedException("지원하지 않는 혼합 모드: " + text);
    static string BlendName(BlendMode mode) => mode switch { BlendMode.SoftLight => "Soft Light", BlendMode.ColorDodge => "Color Dodge", BlendMode.ColorBurn => "Color Burn", _ => mode.ToString() };
    static NotSupportedException Unsupported(string name, string feature) => new($"'{name}'의 {feature} 기능은 .comp 상호 변환에서 지원하지 않습니다. 원본 파일은 변경하지 않았습니다. 해당 기능을 원본에서 래스터화하거나 PNG로 내보내세요.");
}
