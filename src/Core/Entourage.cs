using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Compositor.Windows;

// 점경 (entourage): people, trees, vehicles and street furniture placed on plans, sections and
// axonometrics. Built-in items are original line art drawn from code (Engine/EntourageLibrary*.cs)
// and placed as retained vector layers; 내 점경 are the user's own images (Formats/EntourageStore.cs)
// placed as image layers that carry their original. Both keep an EntourageSpec, so size, colour,
// fill and shape variant stay editable after placement.
public enum EntourageCategory { People, Plants, Vehicles, Props }
// Elevation also serves sections and axonometric boards; plan is the top-down symbol.
public enum EntourageView { Elevation, Plan }
// None: lines only. White / Gray: the silhouette filled under the lines. Solid: the silhouette in the line colour.
// A user's photo cut-out reads them as original colours / faded / greyscale / silhouette.
public enum EntourageFill { None, White, Gray, Solid }

/// <summary>
/// What a placed entourage layer is and how it is drawn. Meters is the real size (height in
/// elevation, the longer side or canopy diameter in plan); PixelsPerMeter is the drawing scale it
/// was placed at, so Meters × PixelsPerMeter is its size in the layer's parent pixels. LineWeight
/// multiplies the pen that the scale suggests, so items placed at one scale share one line weight.
/// </summary>
public sealed record EntourageSpec
{
    public const double MinMeters = .05, MaxMeters = 200, MinPixelsPerMeter = .5, MaxPixelsPerMeter = 2000, MinLineWeight = .25, MaxLineWeight = 4;
    // Longest side of a placed item in pixels (the retained preview is that large).
    public const int MaxSide = 4096, MaxVariant = 63;
    public const string CustomPrefix = "custom:";
    static readonly Regex BuiltInId = new("^[a-z]+(\\.[a-z0-9]+(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);

    public string ItemId { get; init; } = "";
    public int Variant { get; init; }
    public double Meters { get; init; } = 1.7;
    public double PixelsPerMeter { get; init; } = 40;
    public uint LineArgb { get; init; } = DefaultLine;
    public EntourageFill Fill { get; init; } = EntourageFill.White;
    public double LineWeight { get; init; } = 1;
    public EntourageView View { get; init; }
    // 내 점경 only: the image is a line drawing (ink coverage recoloured with the line colour)
    // rather than a cut-out photo or drawing in its own colours.
    public bool LineDrawing { get; init; }
    // 내 점경 only: the original image the layer's pixels are styled from. Saved beside the layer's
    // pixels (ProjectStore), never inside the manifest.
    [JsonIgnore] public Raster? Source { get; init; }

    public const uint DefaultLine = 0xFF262626;
    [JsonIgnore] public bool IsCustom => IsCustomId(ItemId);
    [JsonIgnore] public double SizePixels => Meters * PixelsPerMeter;

    public static bool IsCustomId(string? id) => id != null && id.StartsWith(CustomPrefix, StringComparison.Ordinal) && id.Length == CustomPrefix.Length + 32 &&
        Guid.TryParseExact(id[CustomPrefix.Length..], "N", out var guid) && guid != Guid.Empty;
    public static bool IsValidId(string? id) => id != null && id.Length <= 80 && (IsCustomId(id) || BuiltInId.IsMatch(id));
    public static string CustomId(Guid id) => CustomPrefix + id.ToString("N");

    public void Validate()
    {
        static bool In(double n, double low, double high) => double.IsFinite(n) && n >= low && n <= high;
        if (!IsValidId(ItemId)) throw new InvalidDataException("점경 항목 ID가 올바르지 않습니다.");
        if (Variant < 0 || Variant > MaxVariant || !In(Meters, MinMeters, MaxMeters) || !In(PixelsPerMeter, MinPixelsPerMeter, MaxPixelsPerMeter) ||
            !In(LineWeight, MinLineWeight, MaxLineWeight) || !Enum.IsDefined(Fill) || !Enum.IsDefined(View) || LineArgb >> 24 == 0)
            throw new InvalidDataException("점경 크기·축척·선 설정이 올바르지 않습니다.");
        if (SizePixels > MaxSide) throw new InvalidDataException($"점경은 긴 변 {MaxSide:N0}px까지 놓을 수 있습니다. 높이나 축척을 줄이세요.");
        if (Source != null && !IsCustom) throw new InvalidDataException("내장 점경에는 원본 이미지가 없습니다.");
        if (LineDrawing && !IsCustom) throw new InvalidDataException("선 그림 표시는 내 점경에만 쓸 수 있습니다.");
    }

    // A layer keeps its entourage description only for its own kind: built-in items are vector
    // layers, 내 점경 image layers with their original. Headers are checked before payloads load.
    internal static void ValidateLayer(Layer layer, bool payloads)
    {
        if (layer.Entourage is not { } spec) return;
        spec.Validate();
        if (spec.IsCustom ? layer.Kind != LayerKind.Raster : layer.Kind != LayerKind.Vector)
            throw new InvalidDataException("점경 정보와 레이어 종류가 맞지 않습니다.");
        if (payloads && spec.IsCustom && spec.Source == null) throw new InvalidDataException("내 점경의 원본 이미지가 없습니다.");
        if (payloads && spec.Source is { } source && ((long)source.Width * source.Height > EntourageStoreLimits.MaxPixels || Math.Max(source.Width, source.Height) > EntourageStoreLimits.MaxSide))
            throw new InvalidDataException("내 점경 원본 이미지가 너무 큽니다.");
    }
}

/// <summary>Bounds shared by the 내 점경 library and the documents that carry its images.</summary>
public static class EntourageStoreLimits
{
    public const int MaxSide = 1024, MaxPixels = 1024 * 1024, MaxItems = 64, MaxNameLength = 80;
}
