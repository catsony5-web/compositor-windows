using System.IO;

namespace Compositor.Windows;

// Settings of the design-style adjustment layers (한계값, 망점, 종이·인쇄 질감, 빛 번짐).
// Lengths are in the adjustment layer's own pixels, which are document pixels for root layers,
// so screen views, scaled exports and prints keep the same look. Colors are ARGB.

/// <summary>한계값: luminance at or above <see cref="Level"/> becomes white, below it black.</summary>
public sealed record ThresholdSpec
{
    /// <summary>8-bit luminance (0–255) where white begins; a gray of exactly this value is white.</summary>
    public double Level { get; init; } = 128;
    /// <summary>Width in luminance levels (0–64) of a soft ramp just below the level; 0 is pure black and white.</summary>
    public double Smoothness { get; init; }
    /// <summary>False makes alpha 0 or 255 at 50% too (hard bitmap edges); true keeps the original transparency.</summary>
    public bool KeepAlpha { get; init; } = true;

    public void Validate()
    {
        if (!double.IsFinite(Level) || Level is < 0 or > 255 || !double.IsFinite(Smoothness) || Smoothness is < 0 or > 64)
            throw new InvalidDataException("한계값은 0~255, 부드러운 가장자리는 0~64 범위로 입력하세요.");
    }
}

/// <summary>Dot shape of a halftone screen. Values are stored in projects; append only.</summary>
public enum HalftoneShape { Round, Line, Square }

/// <summary>망점: a screen of dots (or lines) whose ink coverage follows the darkness below.</summary>
public sealed record HalftoneSpec
{
    /// <summary>Screen period in layer pixels (2–256).</summary>
    public double CellSize { get; init; } = 8;
    /// <summary>Screen angle in degrees.</summary>
    public double Angle { get; init; } = 45;
    public HalftoneShape Shape { get; init; }
    /// <summary>Dot color. Alpha 0 prints the dots in the image's own colors.</summary>
    public uint InkArgb { get; init; } = 0xFF000000;
    /// <summary>Color between the dots. Alpha 0 keeps the image's colors between the dots.</summary>
    public uint PaperArgb { get; init; } = 0xFFFFFFFF;

    public void Validate()
    {
        if (!double.IsFinite(CellSize) || CellSize is < 2 or > 256 || !double.IsFinite(Angle) || Angle is < -360 or > 360 || !Enum.IsDefined(Shape))
            throw new InvalidDataException("망점 크기는 2~256px, 각도는 -360~360° 범위로 입력하세요.");
    }
}

/// <summary>종이·인쇄 질감: procedural paper, photocopy and edge wear, seeded and without repeating tiles.</summary>
public sealed record PaperTextureSpec
{
    /// <summary>Pattern number; the same number always draws the same texture.</summary>
    public int Seed { get; init; } = 1;
    /// <summary>Size in layer pixels (0.5–32) of the finest paper grain; fibres and specks scale with it.</summary>
    public double Scale { get; init; } = 2;
    /// <summary>Paper color amount (0–1): white areas take <see cref="TintArgb"/> like printing on tinted paper.</summary>
    public double Tint { get; init; } = .5;
    public uint TintArgb { get; init; } = 0xFFF1EADA;
    /// <summary>Paper tooth and cloudy density (0–1).</summary>
    public double Grain { get; init; } = .35;
    /// <summary>Paper fibres (0–1).</summary>
    public double Fibers { get; init; } = .3;
    /// <summary>Photocopy toner specks on light areas and dropouts in dark areas (0–1).</summary>
    public double Toner { get; init; }
    /// <summary>Photocopy streaks along the page (0–1).</summary>
    public double Streaks { get; init; }
    /// <summary>Rough, burned edges (0–1).</summary>
    public double Edges { get; init; }
    /// <summary>How far the edge wear reaches, as a fraction (0.01–0.5) of the page's shorter side.</summary>
    public double EdgeWidth { get; init; } = .08;
    /// <summary>Color the edges turn toward: dark for burned paper, white for unprinted margins.</summary>
    public uint EdgeArgb { get; init; } = 0xFF2A1D12;

    /// <summary>Every strength is zero: the layer leaves the image unchanged.</summary>
    public bool IsNeutral => Tint == 0 && Grain == 0 && Fibers == 0 && Toner == 0 && Streaks == 0 && Edges == 0;

    public void Validate()
    {
        static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
        if (!double.IsFinite(Scale) || Scale is < .5 or > 32 || !Unit(Tint) || !Unit(Grain) || !Unit(Fibers) || !Unit(Toner) || !Unit(Streaks) || !Unit(Edges) ||
            !double.IsFinite(EdgeWidth) || EdgeWidth is < .01 or > .5)
            throw new InvalidDataException("종이·인쇄 질감은 세기 0~1, 질감 크기 0.5~32px, 가장자리 폭 0.01~0.5 범위로 입력하세요.");
    }
}

/// <summary>빛 번짐: light above a luminance threshold spreads into its surroundings.</summary>
public sealed record GlowSpec
{
    /// <summary>Luminance (0–1, display values) where light begins to glow.</summary>
    public double Threshold { get; init; } = .7;
    /// <summary>How far the light reaches in layer pixels (1–1000).</summary>
    public double Radius { get; init; } = 32;
    /// <summary>Glow strength (0–4); 0 leaves the image unchanged.</summary>
    public double Intensity { get; init; } = 1;
    /// <summary>Light color; its alpha is how much the glow takes this color instead of the light's own.</summary>
    public uint TintArgb { get; init; } = 0x00FFFFFF;

    public void Validate()
    {
        if (!double.IsFinite(Threshold) || Threshold is < 0 or > 1 || !double.IsFinite(Radius) || Radius is < 1 or > 1000 ||
            !double.IsFinite(Intensity) || Intensity is < 0 or > 4)
            throw new InvalidDataException("빛 번짐은 기준 밝기 0~1, 반경 1~1000px, 세기 0~4 범위로 입력하세요.");
    }
}
