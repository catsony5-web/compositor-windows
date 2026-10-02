using System.IO;

namespace Compositor.Windows;

// The user's own line patterns (내 패턴): an image converted to an ink coverage tile. A pattern is an
// ordinary tileable MaterialAsset marked by its Source, so it travels inside documents like any
// material. Its pixels are the default ink with alpha = coverage: a build without line patterns
// shows it as a dark image tile, this build recolors it, changes its line weight and paints the
// fill's background like the built-in hatch patterns (LinePatternRenderer).
public static class LinePatterns
{
    public const string Source = "morupixel:line-pattern@1";
    // Tiles are stored at most this large; larger images are reduced when they are converted.
    public const int MaxSide = 1024, MinSide = 4;
    public const string CustomKeyPrefix = "custom:";

    // A tileable asset with the line-pattern Source and a bounded tile. A built-in ID is never custom.
    public static bool IsCustom(MaterialAsset? asset) =>
        asset != null && asset.Tileable && asset.Source == Source && asset.Pixels.Width >= MinSide && asset.Pixels.Height >= MinSide &&
        asset.Pixels.Width <= MaxSide && asset.Pixels.Height <= MaxSide && !HatchPatterns.TryGet(asset.Id, out _);

    // A built-in hatch pattern or the user's line pattern: drawn as ink with line weight, ink color and background.
    public static bool IsPattern(MaterialAsset? asset) => asset != null && (HatchPatterns.TryGet(asset, out _) || IsCustom(asset));

    // A line pattern asset from a coverage tile: only the alpha channel is kept; the color is the default ink.
    public static MaterialAsset Create(string name, Raster coverage, Guid? id = null)
    {
        MaterialEditing.Name(name);
        if (coverage.Width < MinSide || coverage.Height < MinSide || coverage.Width > MaxSide || coverage.Height > MaxSide)
            throw new InvalidDataException($"패턴 타일은 한 변 {MinSide}–{MaxSide}px로 만드세요.");
        var data = new byte[coverage.Data.Length];
        byte b = unchecked((byte)HatchPatterns.DefaultInk), g = unchecked((byte)(HatchPatterns.DefaultInk >> 8)), r = unchecked((byte)(HatchPatterns.DefaultInk >> 16));
        for (int i = 0; i < data.Length; i += 4) { data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = coverage.Data[i + 3]; }
        var asset = new MaterialAsset(id ?? Guid.NewGuid(), name.Trim(), new Raster(coverage.Width, coverage.Height, data), Source, true);
        MaterialEditing.ValidateAsset(asset);
        return asset;
    }

    // Favorites are kept by a stable key: the built-in pattern's key, or "custom:" and the asset ID.
    public static string FavoriteKey(MaterialAsset asset) =>
        HatchPatterns.TryGet(asset, out var pattern) ? HatchPatterns.Key(pattern) : CustomKeyPrefix + asset.Id.ToString("N");

    public static bool IsFavoriteKey(string? key) => key != null && key.Length <= 80 &&
        (HatchPatterns.TryParseKey(key, out _) || key.StartsWith(CustomKeyPrefix, StringComparison.Ordinal) && key.Length == CustomKeyPrefix.Length + 32
            && Guid.TryParseExact(key[CustomKeyPrefix.Length..], "N", out var id) && id != Guid.Empty);
}
