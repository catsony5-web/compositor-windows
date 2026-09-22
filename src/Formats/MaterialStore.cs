using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class MaterialTextures
{
    public const long MaxEncodedBytes = 96L * 1024 * 1024;
    public static Raster Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxEncodedBytes) throw new InvalidDataException("재료 원본 파일이 96MiB를 초과합니다.");
        return Read(input);
    }
    internal static Raster Read(Stream input, int? width = null, int? height = null)
    {
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count < 1) throw new InvalidDataException("재료 이미지 프레임이 없습니다.");
        var frame = decoder.Frames[0]; MaterialEditing.ValidateSize(frame.PixelWidth, frame.PixelHeight);
        if (width.HasValue && (frame.PixelWidth != width || frame.PixelHeight != height)) throw new InvalidDataException("재료 원본 크기가 작업 정보와 다릅니다.");
        return Raster.FromBitmap(frame);
    }
}

public static partial class ProjectStore
{
    public sealed record MaterialInfo(Guid Id, string Name, string Source, bool Tileable, int Width, int Height);
    public sealed record MaterialFillInfo(Guid MaterialId, Guid SourceRegionId, string RegionName, RegionPath Boundary,
        int Width, int Height, double TileWidth, double TileHeight, double Angle, double OffsetX, double OffsetY)
    {
        public static MaterialFillInfo? From(MaterialFill? fill) => fill == null ? null : new(fill.Asset.Id, fill.SourceRegionId, fill.RegionName,
            fill.Boundary, fill.Width, fill.Height, fill.TileWidth, fill.TileHeight, fill.Angle, fill.OffsetX, fill.OffsetY);
        public MaterialFill Fill(IReadOnlyDictionary<Guid, MaterialAsset> assets) => new(
            assets.TryGetValue(MaterialId, out var asset) ? asset : throw new InvalidDataException("재료 원본 참조가 없습니다."),
            SourceRegionId, RegionName, Boundary, Width, Height, TileWidth, TileHeight, Angle, OffsetX, OffsetY);
    }
    static Dictionary<Guid, MaterialAsset> ValidateMaterials(Manifest manifest)
    {
        if (manifest.Materials == null || manifest.MaterialRegions == null || manifest.Materials.Count > MaterialEditing.MaxAssets ||
            manifest.MaterialRegions.Count > MaterialEditing.MaxRegions || manifest.Version < 6 &&
            (manifest.Materials.Count != 0 || manifest.MaterialRegions.Count != 0 || manifest.Layers?.Any(l => l?.Material != null) == true))
            throw new InvalidDataException("재료 정보가 작업 파일 버전 또는 지원 범위와 맞지 않습니다.");
        var placeholders = new Dictionary<Guid, MaterialAsset>(); long bytes = 0;
        foreach (var info in manifest.Materials)
        {
            if (info == null || info.Id == Guid.Empty || placeholders.ContainsKey(info.Id)) throw new InvalidDataException("재료 ID가 올바르지 않습니다.");
            MaterialEditing.ValidateSize(info.Width, info.Height);
            bytes += (long)info.Width * info.Height * 4;
            if (bytes > MaterialEditing.MaxAssetBytes) throw new InvalidDataException("재료 원본 메모리 한도를 초과합니다.");
            var asset = new MaterialAsset(info.Id, info.Name, new Raster(1, 1), info.Source, info.Tileable);
            MaterialEditing.ValidateAsset(asset); placeholders.Add(info.Id, asset);
        }
        return placeholders;
    }
    static Dictionary<Guid, MaterialAsset> ReadMaterials(ZipArchive zip, Manifest manifest)
    {
        var result = new Dictionary<Guid, MaterialAsset>();
        for (int i = 0; i < manifest.Materials!.Count; i++)
        {
            var info = manifest.Materials[i];
            var entry = zip.GetEntry($"materials/{i}.png") ?? throw new InvalidDataException("프로젝트 안에 재료 원본이 없습니다.");
            if (entry.Length > MaterialTextures.MaxEncodedBytes) throw new InvalidDataException("재료 원본 파일이 너무 큽니다.");
            using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); buffer.Position = 0;
            var pixels = MaterialTextures.Read(buffer, info.Width, info.Height);
            result.Add(info.Id, new MaterialAsset(info.Id, info.Name, pixels, info.Source, info.Tileable));
        }
        return result;
    }
}
