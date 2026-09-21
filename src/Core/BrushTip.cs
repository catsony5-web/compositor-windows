using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

/// <summary>An immutable, monochrome brush silhouette. Its longest side is the brush diameter.</summary>
public sealed class BrushTip
{
    enum Shape { Round, Square, Diamond, Star, Image }
    readonly Shape shape;
    readonly byte[]? alpha;
    static readonly Point[] star = Enumerable.Range(0, 10).Select(i =>
    {
        double angle = -Math.PI / 2 + i * Math.PI / 5, radius = i % 2 == 0 ? 1 : .42;
        return new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }).ToArray();

    public static BrushTip Round { get; } = new("round", "원형", Shape.Round);
    public static BrushTip Square { get; } = new("square", "사각형", Shape.Square);
    public static BrushTip Diamond { get; } = new("diamond", "마름모", Shape.Diamond);
    public static BrushTip Star { get; } = new("star", "별", Shape.Star);
    public static IReadOnlyList<BrushTip> BuiltIns { get; } = Array.AsReadOnly(new[] { Round, Square, Diamond, Star });
    public const int MaxDimension = 256;
    public string Id { get; }
    public string Name { get; }
    public bool IsCustom => shape == Shape.Image;
    public int Width { get; }
    public int Height { get; }

    BrushTip(string id, string name, Shape shape, int width = MaxDimension, int height = MaxDimension, byte[]? alpha = null)
    {
        Id = id; Name = name; this.shape = shape; Width = width; Height = height; this.alpha = alpha;
    }

    public static BrushTip FromAlpha(string name, int width, int height, ReadOnlySpan<byte> alpha)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || alpha.Length != width * height)
            throw new InvalidDataException("브러시 모양은 한 변 256px 이하의 유효한 이미지여야 합니다.");
        var immutable = alpha.ToArray();
        if (!immutable.Any(a => a != 0)) throw new InvalidDataException("브러시로 사용할 모양이 없습니다. 투명 이미지 또는 흰 배경에 어두운 모양을 사용하세요.");
        var identity = new byte[immutable.Length + 8];
        BitConverter.GetBytes(width).CopyTo(identity, 0); BitConverter.GetBytes(height).CopyTo(identity, 4); immutable.CopyTo(identity, 8);
        return new BrushTip(Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant(),
            string.IsNullOrWhiteSpace(name) ? "사용자 브러시" : name.Trim(), Shape.Image, width, height, immutable);
    }

    /// <summary>Transparent artwork uses alpha; fully opaque artwork uses inverse luminance.</summary>
    public static BrushTip FromImage(string name, Raster image)
    {
        if (image.Width > MaxDimension || image.Height > MaxDimension) throw new InvalidDataException("브러시 이미지를 먼저 256px 이하로 줄여 주세요.");
        bool transparent = false;
        for (int i = 3; i < image.Data.Length; i += 4) if (image.Data[i] != 255) { transparent = true; break; }
        var mask = new byte[image.Width * image.Height];
        for (int p = 0; p < mask.Length; p++)
        {
            int i = p * 4;
            mask[p] = transparent ? image.Data[i + 3] : Imaging.Byte(255 - (.2126 * image.Data[i + 2] + .7152 * image.Data[i + 1] + .0722 * image.Data[i]));
        }
        return FromAlpha(name, image.Width, image.Height, mask);
    }

    // Coordinates are relative to the centre in [-1, 1], before tip rotation.
    internal double Sample(double x, double y, double hardness)
    {
        if (shape == Shape.Image)
        {
            double longest = Math.Max(Width, Height), halfWidth = Width / longest, halfHeight = Height / longest;
            if (Math.Abs(x) > halfWidth || Math.Abs(y) > halfHeight) return 0;
            double px = (x * longest + Width) / 2 - .5, py = (y * longest + Height) / 2 - .5;
            int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
            double fx = px - x0, fy = py - y0;
            double At(int ix, int iy) => ix < 0 || iy < 0 || ix >= Width || iy >= Height ? 0 : alpha![iy * Width + ix] / 255.0;
            return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy)
                + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
        }
        double distance = shape switch
        {
            Shape.Square => Math.Max(Math.Abs(x), Math.Abs(y)),
            Shape.Diamond => Math.Abs(x) + Math.Abs(y),
            Shape.Star => StarDistance(x, y),
            _ => Math.Sqrt(x * x + y * y)
        };
        if (distance > 1) return 0;
        return distance <= hardness ? 1 : BrushStroke.Falloff((distance - hardness) / (1 - hardness));
    }

    static double StarDistance(double x, double y)
    {
        double length = Math.Sqrt(x * x + y * y); if (length < 1e-12) return 0;
        double angle = (Math.Atan2(y, x) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2);
        int index = Math.Min(9, (int)(angle / (Math.PI / 5)));
        var a = star[index]; var b = star[(index + 1) % 10];
        double cross = (x / length) * (b.Y - a.Y) - (y / length) * (b.X - a.X);
        double boundary = (a.X * b.Y - a.Y * b.X) / cross;
        return length / boundary;
    }

    public BitmapSource Preview(Color color, int size = 48, bool inset = true)
    {
        size = Math.Clamp(size, 8, MaxDimension);
        var image = new Raster(size, size);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            image.Data[i] = color.B; image.Data[i + 1] = color.G; image.Data[i + 2] = color.R;
            double radius = size * (inset ? .44 : .5);
            image.Data[i + 3] = Imaging.Byte(Sample((x + .5 - size / 2.0) / radius, (y + .5 - size / 2.0) / radius, 1) * color.A);
        }
        return image.Bitmap();
    }

    internal Raster MaskImage()
    {
        if (!IsCustom) throw new InvalidOperationException("기본 브러시는 별도로 저장하지 않습니다.");
        var image = new Raster(Width, Height);
        // Black RGB also preserves fully opaque imported tips when loaded through inverse luminance.
        for (int p = 0; p < alpha!.Length; p++) image.Data[p * 4 + 3] = alpha[p];
        return image;
    }
}
