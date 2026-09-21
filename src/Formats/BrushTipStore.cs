using System.IO;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class BrushTipStore
{
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "Brushes");
    static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public static BrushTip Import(string path)
    {
        if (!extensions.Contains(Path.GetExtension(path))) throw new InvalidDataException("PNG, JPEG, BMP, TIFF 이미지를 선택하세요.");
        var file = new FileInfo(path);
        if (file.Length == 0 || file.Length > MaxFileBytes) throw new InvalidDataException("브러시 이미지는 32MB 이하로 사용하세요.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // OnDemand reads dimensions before copying decoded pixels; reject oversized source dimensions too.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("브러시 이미지가 비어 있습니다.");
        var frame = decoder.Frames[0]; Raster.ValidateSize(frame.PixelWidth, frame.PixelHeight);
        double scale = Math.Min(1, BrushTip.MaxDimension / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale)), height = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = width; bitmap.DecodePixelHeight = height; bitmap.StreamSource = stream;
        bitmap.EndInit(); bitmap.Freeze();
        if (bitmap.PixelWidth > BrushTip.MaxDimension || bitmap.PixelHeight > BrushTip.MaxDimension) throw new InvalidDataException("브러시 이미지를 축소하지 못했습니다.");
        return BrushTip.FromImage(Path.GetFileNameWithoutExtension(path), Raster.FromBitmap(bitmap));
    }

    public static string Save(BrushTip tip, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(tip);
        if (!tip.IsCustom) throw new InvalidOperationException("사용자 브러시만 저장할 수 있습니다.");
        directory = Path.GetFullPath(directory ?? DefaultDirectory);
        Directory.CreateDirectory(directory);
        string name = new(tip.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).Take(64).ToArray());
        name = name.Trim(' ', '.'); if (name.Length == 0) name = "사용자 브러시";
        string path = Path.Combine(directory, name + "--" + tip.Id[..12] + ".png");
        ProjectStore.AtomicWrite(path, stream => tip.MaskImage().WritePng(stream));
        return path;
    }

    public static IReadOnlyList<BrushTip> LoadAll(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return Array.Empty<BrushTip>();
        var tips = new List<BrushTip>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.OrdinalIgnoreCase).Take(256))
        {
            try
            {
                var tip = Import(path);
                string name = tip.Name, suffix = "--" + tip.Id[..12];
                if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
                tip = BrushTip.FromImage(name, tip.MaskImage());
                if (seen.Add(tip.Id)) tips.Add(tip);
            }
            catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or FormatException)
            {
                // A removed or invalid user preset must not prevent the remaining brushes from loading.
            }
        }
        return tips.AsReadOnly();
    }
}
