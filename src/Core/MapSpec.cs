using System.IO;
using System.Text.Json.Serialization;
using System.Windows;

namespace Compositor.Windows;

// Maps made from OpenStreetMap data (지도 포스터 · 대지 위치도): the tag kept on map layers, the
// class keys of their layers, the colour themes and the Web Mercator projection that places
// latitude/longitude in document pixels with a known ground scale.
public enum MapTemplate { Poster, SiteLocation }
public enum MapMarker { Circle, Pin }

/// <summary>
/// Kept on map layers. The root folder (Role "map") records where the map is: the center, the
/// ground metres per document pixel at the center latitude and the map frame in the folder's own
/// pixels, so a scale bar or a later marker can be placed at true scale. Every other map layer
/// records only its class (a <see cref="MapClasses"/> key) so a theme can recolour it.
/// </summary>
public sealed record MapTag
{
    public const string RootRole = "map";
    public string Role { get; init; } = "";
    public double CenterLatitude { get; init; }
    public double CenterLongitude { get; init; }
    public double MetersPerPixel { get; init; }
    public double FrameX { get; init; }
    public double FrameY { get; init; }
    public double FrameWidth { get; init; }
    public double FrameHeight { get; init; }
    public MapTemplate Template { get; init; }
    public string Theme { get; init; } = MapThemes.DefaultKey;
    public double? SiteLatitude { get; init; }
    public double? SiteLongitude { get; init; }
    [JsonIgnore] public bool IsRoot => Role == RootRole;
    [JsonIgnore] public Rect Frame => new(FrameX, FrameY, FrameWidth, FrameHeight);
    [JsonIgnore] public MapView View => new(CenterLatitude, CenterLongitude, MetersPerPixel, Frame);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Role) || Role.Length > 64 || !Enum.IsDefined(Template) || Theme == null || Theme.Length > 64)
            throw new InvalidDataException("지도 레이어 정보가 올바르지 않습니다.");
        if (!IsRoot) return;
        static bool Finite(double v) => double.IsFinite(v);
        if (!Finite(CenterLatitude) || Math.Abs(CenterLatitude) > WebMercator.MaxLatitude || !Finite(CenterLongitude) || Math.Abs(CenterLongitude) > 180 ||
            !Finite(MetersPerPixel) || MetersPerPixel <= 0 || MetersPerPixel > 1e6 ||
            !Finite(FrameX) || !Finite(FrameY) || !Finite(FrameWidth) || !Finite(FrameHeight) || FrameWidth < 1 || FrameHeight < 1 ||
            Math.Abs(FrameX) > 1e6 || Math.Abs(FrameY) > 1e6 || FrameWidth > 1e6 || FrameHeight > 1e6 ||
            SiteLatitude is { } lat && (!Finite(lat) || Math.Abs(lat) > WebMercator.MaxLatitude) || SiteLongitude is { } lon && (!Finite(lon) || Math.Abs(lon) > 180) ||
            SiteLatitude.HasValue != SiteLongitude.HasValue)
            throw new InvalidDataException("지도의 위치·축척 정보가 올바르지 않습니다.");
    }
}

/// <summary>Class keys of map layers, in paint order groups. Stored in projects: never rename a key.</summary>
public static class MapClasses
{
    public const string Background = "background", Frame = "frame", Fade = "fade";
    public const string Park = "green-park", Forest = "green-forest", Grass = "green-grass";
    public const string Sea = "water-sea", WaterArea = "water-area", River = "waterway-river", Stream = "waterway-stream";
    public const string Building = "building", Rail = "rail";
    public const string Motorway = "road-motorway", Major = "road-major", Minor = "road-minor", Local = "road-local", Service = "road-service", Path = "road-path";
    public const string Coastline = "coastline";
    public const string Rings = "site-rings", RingLabel = "site-ring-label", Marker = "site-marker", SiteLabel = "site-label", NorthArrow = "north-arrow", ScaleBar = "scale-bar", ScaleText = "scale-text";
    public const string Title = "title", Rule = "rule", Subtitle = "subtitle", Coordinates = "coordinates", Attribution = "attribution";
    public const string GreenGroup = "group-green", WaterGroup = "group-water", RoadGroup = "group-roads", SiteGroup = "group-site", TextGroup = "group-text";

    /// <summary>Road classes from the smallest (painted first) to the largest.</summary>
    public static readonly string[] Roads = [Path, Service, Local, Minor, Major, Motorway];

    public static bool IsArea(string key) => key is Park or Forest or Grass or Sea or WaterArea or Building;

    /// <summary>Korean layer name of a class.</summary>
    public static string Name(string key) => key switch
    {
        Background => "배경", Frame => "테두리", Fade => "가장자리 흐림",
        Park => "공원", Forest => "숲", Grass => "잔디·초지",
        Sea => "바다", WaterArea => "강·호수", River => "강줄기", Stream => "개울·수로",
        Building => "건물", Rail => "철도",
        Motorway => "고속도로", Major => "주요 도로", Minor => "보조 도로", Local => "생활 도로", Service => "골목·서비스 도로", Path => "보행로",
        Rings => "반경 원", RingLabel => "반경 표시", Marker => "대지 표시", SiteLabel => "대지 이름", NorthArrow => "방위표", ScaleBar => "축척 막대", ScaleText => "축척",
        Title => "제목", Rule => "제목 밑줄", Subtitle => "부제", Coordinates => "좌표", Attribution => "데이터 출처",
        GreenGroup => "녹지", WaterGroup => "물", RoadGroup => "도로", SiteGroup => "위치 표시", TextGroup => "글자",
        _ => key
    };

    /// <summary>
    /// Map class of an OpenStreetMap way or relation from its tags; null when the map does not draw it.
    /// <paramref name="area"/> is true for closed ways and multipolygons.
    /// </summary>
    public static string? Classify(IReadOnlyDictionary<string, string> tags, bool area)
    {
        string? Tag(string key) => tags.TryGetValue(key, out var value) ? value : null;
        bool Yes(string key) => Tag(key) is { } v && v != "no";
        if (Tag("natural") == "coastline") return Coastline;
        if (area)
        {
            if (Tag("building") is { } building && building != "no") return Building;
            if (Tag("natural") is "water" or "bay" || Tag("waterway") is "riverbank" or "dock" || Tag("landuse") is "reservoir" or "basin" || Tag("water") != null && Tag("natural") == null && Tag("landuse") == null && Tag("waterway") == null)
                return WaterArea;
            if (Tag("landuse") == "forest" || Tag("natural") == "wood") return Forest;
            if (Tag("leisure") is "park" or "garden" or "playground" or "recreation_ground" or "golf_course" or "nature_reserve" or "common" or "dog_park" || Tag("landuse") is "recreation_ground" or "village_green" or "cemetery")
                return Park;
            if (Tag("landuse") is "grass" or "meadow" or "orchard" or "vineyard" or "allotments" || Tag("natural") is "grassland" or "heath" or "scrub" || Tag("leisure") == "pitch")
                return Grass;
            if (Tag("area") == "yes" || Tag("highway") == null && Tag("waterway") == null && Tag("railway") == null) return null;
        }
        if (Tag("highway") is { } highway && Tag("area") != "yes")
            return highway switch
            {
                "motorway" or "motorway_link" or "trunk" or "trunk_link" => Motorway,
                "primary" or "primary_link" or "secondary" or "secondary_link" => Major,
                "tertiary" or "tertiary_link" => Minor,
                "residential" or "unclassified" or "living_street" or "road" => Local,
                "service" or "track" => Service,
                "footway" or "path" or "cycleway" or "pedestrian" or "steps" or "bridleway" => Path,
                _ => null
            };
        if (Tag("railway") is "rail" or "light_rail" or "narrow_gauge" or "monorail" or "tram" or "subway" or "funicular" or "preserved" && !Yes("tunnel")) return Rail;
        if (Tag("waterway") is { } waterway && Tag("tunnel") is null or "no")
            return waterway switch { "river" or "canal" => River, "stream" or "ditch" or "drain" or "brook" => Stream, _ => null };
        return null;
    }
}

/// <summary>One colour theme of the map templates (our own palettes). Colours are opaque ARGB.</summary>
public sealed record MapTheme(string Key, string Name, string Description,
    uint Background, uint Water, uint WaterLine, uint Park, uint Forest, uint Grass, uint Building, uint Rail,
    uint Motorway, uint Major, uint Minor, uint Local, uint Service, uint Path,
    uint Text, uint Muted, uint Accent, uint Light)
{
    /// <summary>Colour of a class key in this theme.</summary>
    public uint Color(string key) => key switch
    {
        MapClasses.Background or MapClasses.Fade => Background,
        MapClasses.Sea or MapClasses.WaterArea => Water,
        MapClasses.River or MapClasses.Stream => WaterLine,
        MapClasses.Park => Park, MapClasses.Forest => Forest, MapClasses.Grass => Grass,
        MapClasses.Building => Building, MapClasses.Rail => Rail,
        MapClasses.Motorway => Motorway, MapClasses.Major => Major, MapClasses.Minor => Minor,
        MapClasses.Local => Local, MapClasses.Service => Service, MapClasses.Path => Path,
        MapClasses.Marker or MapClasses.Rings or MapClasses.RingLabel => Accent,
        MapClasses.Subtitle or MapClasses.Coordinates or MapClasses.Attribution or MapClasses.ScaleText or MapClasses.Frame => Muted,
        _ => Text
    };
    /// <summary>Theme roles in a stable order; recolouring maps a colour to its role in the old theme.</summary>
    public uint[] Roles => [Background, Water, WaterLine, Park, Forest, Grass, Building, Rail, Motorway, Major, Minor, Local, Service, Path, Text, Muted, Accent, Light];
}

public static class MapThemes
{
    public const string DefaultKey = "light";
    public static readonly MapTheme[] All =
    [
        new("light", "밝은", "따뜻한 종이색 바탕에 짙은 회색 길",
            0xFFF3F0E8, 0xFFC8D7E0, 0xFFA9C0CE, 0xFFDDE5D2, 0xFFCFDCC4, 0xFFE3E7D9, 0xFFE2DDD2, 0xFF6E6A66,
            0xFF2E3236, 0xFF43474C, 0xFF5B5F64, 0xFF7C8085, 0xFF9C9FA3, 0xFFB5B6B5, 0xFF24282C, 0xFF5E6266, 0xFFD2452F, 0xFFFFFFFF),
        new("dark", "어두운", "어두운 청회색 바탕에 밝은 길",
            0xFF2A3038, 0xFF1D2329, 0xFF3A4A57, 0xFF323A36, 0xFF2F3833, 0xFF343C38, 0xFF353B43, 0xFF8A949E,
            0xFFE6E9EC, 0xFFC6CBD0, 0xFFA5ACB3, 0xFF858D95, 0xFF6A727A, 0xFF59616A, 0xFFEEF0F2, 0xFFA9B0B8, 0xFFF0A23B, 0xFF2A3038),
        new("green", "녹색", "연한 녹색 바탕에 숲빛 길과 물",
            0xFFEDF2EA, 0xFFBCD5D6, 0xFF97BCC0, 0xFFD3E3C8, 0xFFC4D9B8, 0xFFDCE8D3, 0xFFDDE5D6, 0xFF55705F,
            0xFF1F3A2B, 0xFF2F5240, 0xFF4A6E5A, 0xFF6F8F7C, 0xFF93AE9D, 0xFFAFC3B5, 0xFF1C3427, 0xFF4B6555, 0xFFC4553A, 0xFFFFFFFF),
        new("mono", "흑백", "흰 바탕에 검은 선, 인쇄와 도면에 알맞은 무채색",
            0xFFFFFFFF, 0xFFE4E4E4, 0xFFBDBDBD, 0xFFF0F0F0, 0xFFE8E8E8, 0xFFF4F4F4, 0xFFE9E9E9, 0xFF555555,
            0xFF111111, 0xFF262626, 0xFF404040, 0xFF666666, 0xFF8C8C8C, 0xFFA8A8A8, 0xFF111111, 0xFF555555, 0xFF111111, 0xFFFFFFFF),
        new("navy", "남색", "깊은 남색 바탕에 흰 선, 청사진 느낌",
            0xFF1F3550, 0xFF172A40, 0xFF4F6F93, 0xFF26405A, 0xFF243D56, 0xFF294561, 0xFF2A4560, 0xFF9DB3CB,
            0xFFF4F7FB, 0xFFD8E2EE, 0xFFB5C5D8, 0xFF8EA3BC, 0xFF6F86A2, 0xFF5D7591, 0xFFF4F7FB, 0xFFB8C7D8, 0xFFFFC857, 0xFF1F3550),
        new("warm", "노을", "모래색 바탕에 적갈색 길",
            0xFFF6ECDF, 0xFFCBDCDD, 0xFFA7C2C6, 0xFFE4E6CF, 0xFFD8DFC2, 0xFFEAEBD6, 0xFFEADCCB, 0xFF8E6F5A,
            0xFF7A2E1E, 0xFF94432D, 0xFFAD6247, 0xFFC28469, 0xFFD3A58D, 0xFFDDBBA6, 0xFF5A2418, 0xFF8A5A47, 0xFF2D6A8A, 0xFFFFFFFF)
    ];
    public static MapTheme Get(string? key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];
    public static bool Exists(string? key) => All.Any(t => t.Key == key);
}

/// <summary>Spherical Web Mercator (EPSG:3857), the projection of OpenStreetMap's web maps.</summary>
public static class WebMercator
{
    public const double EarthRadius = 6_378_137, MaxLatitude = 85.0511287798;
    static double Radians(double degrees) => degrees * Math.PI / 180;
    /// <summary>Projected metres (x east, y north).</summary>
    public static Point Project(double latitude, double longitude)
    {
        double lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        return new(EarthRadius * Radians(longitude), EarthRadius * Math.Log(Math.Tan(Math.PI / 4 + Radians(lat) / 2)));
    }
    public static (double Latitude, double Longitude) Unproject(Point projected) =>
        ((2 * Math.Atan(Math.Exp(projected.Y / EarthRadius)) - Math.PI / 2) * 180 / Math.PI, projected.X / EarthRadius * 180 / Math.PI);
    /// <summary>Great-circle distance in metres (haversine on the mean Earth radius).</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double mean = 6_371_008.8;
        double dLat = Radians(lat2 - lat1), dLon = Radians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * mean * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
    /// <summary>The point <paramref name="meters"/> away along a compass bearing (degrees clockwise from north).</summary>
    public static (double Latitude, double Longitude) Offset(double latitude, double longitude, double meters, double bearing)
    {
        const double mean = 6_371_008.8;
        double d = meters / mean, b = Radians(bearing), lat = Radians(latitude), lon = Radians(longitude);
        double lat2 = Math.Asin(Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(b));
        double lon2 = lon + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(lat), Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));
        return (lat2 * 180 / Math.PI, (lon2 * 180 / Math.PI + 540) % 360 - 180);
    }
}

/// <summary>
/// A Web Mercator view: the center lands in the middle of <see cref="Frame"/> and one document
/// pixel covers <see cref="MetersPerPixel"/> metres of ground at the center latitude.
/// </summary>
public sealed record MapView(double CenterLatitude, double CenterLongitude, double MetersPerPixel, Rect Frame)
{
    /// <summary>Document pixels per projected (Mercator) metre.</summary>
    public double Scale => Math.Cos(CenterLatitude * Math.PI / 180) / MetersPerPixel;
    Point Center => WebMercator.Project(CenterLatitude, CenterLongitude);
    public Point ToPixel(double latitude, double longitude)
    {
        var p = WebMercator.Project(latitude, longitude); var c = Center; double s = Scale;
        return new(Frame.X + Frame.Width / 2 + (p.X - c.X) * s, Frame.Y + Frame.Height / 2 - (p.Y - c.Y) * s);
    }
    public (double Latitude, double Longitude) ToGeo(Point pixel)
    {
        var c = Center; double s = Scale;
        return WebMercator.Unproject(new Point(c.X + (pixel.X - Frame.X - Frame.Width / 2) / s, c.Y - (pixel.Y - Frame.Y - Frame.Height / 2) / s));
    }
    /// <summary>The latitude/longitude box the frame shows.</summary>
    public MapBounds Bounds
    {
        get
        {
            var (north, west) = ToGeo(Frame.TopLeft); var (south, east) = ToGeo(Frame.BottomRight);
            return new(south, west, north, east);
        }
    }
    /// <summary>Paper scale denominator (1:N) when the document is printed at <paramref name="dpi"/>.</summary>
    public double ScaleDenominator(double dpi) => MetersPerPixel * dpi / .0254;
}

/// <summary>A latitude/longitude box (degrees).</summary>
public readonly record struct MapBounds(double South, double West, double North, double East)
{
    public double CenterLatitude => (South + North) / 2;
    public double CenterLongitude => (West + East) / 2;
    public bool IsValid => double.IsFinite(South) && double.IsFinite(West) && double.IsFinite(North) && double.IsFinite(East) &&
        South >= -90 && North <= 90 && West >= -180 && East <= 180 && North > South && East > West;
    public bool Contains(double latitude, double longitude) => latitude >= South && latitude <= North && longitude >= West && longitude <= East;
    /// <summary>Ground width and height in metres through the center.</summary>
    public (double Width, double Height) Meters => (WebMercator.Distance(CenterLatitude, West, CenterLatitude, East), WebMercator.Distance(South, CenterLongitude, North, CenterLongitude));
    public static MapBounds Around(double latitude, double longitude, double radiusMeters)
    {
        var (north, _) = WebMercator.Offset(latitude, longitude, radiusMeters, 0); var (south, _) = WebMercator.Offset(latitude, longitude, radiusMeters, 180);
        var (_, east) = WebMercator.Offset(latitude, longitude, radiusMeters, 90); var (_, west) = WebMercator.Offset(latitude, longitude, radiusMeters, 270);
        return new(south, west, north, east);
    }
}
