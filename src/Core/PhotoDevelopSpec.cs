using System.IO;

namespace Compositor.Windows;

/// <summary>Relative photo adjustments on the document's decoded RGB pixels, not camera RAW metadata.</summary>
public sealed record PhotoDevelopSpec
{
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Texture { get; init; }
    public double Clarity { get; init; }
    public double Dehaze { get; init; }
    public double Vibrance { get; init; }
    public double Saturation { get; init; }

    internal bool IsNeutral => this == new PhotoDevelopSpec();
    public void Validate()
    {
        if (!double.IsFinite(Exposure) || Exposure is < -5 or > 5 ||
            new[] { Temperature, Tint, Contrast, Highlights, Shadows, Whites, Blacks, Texture, Clarity, Dehaze, Vibrance, Saturation }
                .Any(value => !double.IsFinite(value) || value is < -100 or > 100))
            throw new InvalidDataException("사진 현상은 노출 -5~5 EV, 나머지 값 -100~100 범위로 입력하세요.");
    }
}
