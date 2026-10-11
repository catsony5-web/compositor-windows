using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml;

namespace Compositor.Windows;

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>One drawable map object: rings of an area (even-odd holes allowed) or polylines of a line class.</summary>
public sealed record MapFeature(string Class, GeoPoint[][] Parts);

/// <summary>Map data read from an OpenStreetMap export (.osm) or a GeoJSON file, already classified for drawing.</summary>
public sealed class MapData
{
    public required MapBounds Bounds { get; init; }
    /// <summary>True when the file states its own area (OSM &lt;bounds&gt; or a GeoJSON bbox).</summary>
    public bool BoundsFromFile { get; init; }
    public required IReadOnlyList<MapFeature> Features { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public long PointCount => Features.Sum(f => (long)f.Parts.Sum(p => p.Length));
    public IReadOnlyDictionary<string, int> Counts => Features.GroupBy(f => f.Class).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// Reads OpenStreetMap data the user exported (the XML .osm format of the openstreetmap.org
/// Export page and the editing API) or GeoJSON, streaming the XML with bounded memory: ways and
/// multipolygon relations become classified lines and areas, natural=coastline becomes a sea area
/// inside the data's bounds. Nothing is downloaded here and no network link is followed.
/// </summary>
public static class MapImport
{
    public const long MaxOsmBytes = 1L << 30, MaxGeoJsonBytes = 256L << 20;
    public const int MaxNodes = 15_000_000, MaxWays = 2_500_000, MaxRelations = 500_000;
    public const long MaxWayReferences = 50_000_000, MaxFeaturePoints = 20_000_000;
    public const string Filter = "지도 데이터 (OSM · GeoJSON)|*.osm;*.geojson;*.json|모든 파일|*.*";

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".osm" or ".geojson";

    public static MapData Read(string path, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("지도 데이터 파일을 찾을 수 없습니다.", path);
        bool json = Path.GetExtension(path).ToLowerInvariant() is ".geojson" or ".json";
        long limit = json ? MaxGeoJsonBytes : MaxOsmBytes;
        if (file.Length == 0 || file.Length > limit) throw new InvalidDataException($"지도 데이터 파일은 0바이트보다 크고 {limit / (1024 * 1024):N0}MB 이하여야 합니다.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return json ? ReadGeoJson(stream, token) : ReadOsm(stream, file.Length, progress, token);
    }

    // ---- OpenStreetMap XML -------------------------------------------------------------------

    enum Element { None, Node, Way, Relation }

    /// <param name="area">The requested box when the data itself does not state one (an online download).</param>
    public static MapData ReadOsm(Stream input, long length, IProgress<double>? progress = null, CancellationToken token = default, MapBounds? area = null)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true, CloseInput = false, MaxCharactersFromEntities = 1024
        };
        var nodes = new Dictionary<long, long>();
        var wayRefs = new Dictionary<long, long[]>();
        var lineWays = new List<(long Id, string Class)>(); var areaWays = new List<(long Id, string Class)>(); var coastWays = new List<long>();
        var multipolygons = new List<(string Class, (long Ref, string Role)[] Members)>();
        var warnings = new List<string>();
        MapBounds? fileBounds = null; bool sawRoot = false; long references = 0; int ways = 0, relations = 0, elements = 0;
        var element = Element.None; bool skip = false; long id = 0;
        var refs = new List<long>(); var tags = new Dictionary<string, string>(StringComparer.Ordinal); var members = new List<(long, string)>();
        static double Coordinate(string? text, double limit)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || Math.Abs(value) > limit)
                throw new InvalidDataException("지도 데이터에 올바르지 않은 좌표가 있습니다.");
            return value;
        }
        static long Id(string? text) => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            ? value : throw new InvalidDataException("지도 데이터에 올바르지 않은 번호가 있습니다.");
        void FinishWay()
        {
            if (!skip && refs.Count >= 2)
            {
                if (++ways > MaxWays) throw new InvalidDataException($"지도 데이터의 길·면이 {MaxWays:N0}개를 넘습니다. 더 작은 영역을 내보내 주세요.");
                references += refs.Count;
                if (references > MaxWayReferences) throw new InvalidDataException("지도 데이터가 너무 큽니다. 더 작은 영역을 내보내 주세요.");
                var array = refs.ToArray(); wayRefs[id] = array;
                bool closed = array.Length >= 4 && array[0] == array[^1];
                if (MapClasses.Classify(tags, closed) is { } kind)
                {
                    if (kind == MapClasses.Coastline) coastWays.Add(id);
                    else if (MapClasses.IsArea(kind)) { if (closed) areaWays.Add((id, kind)); }
                    else lineWays.Add((id, kind));
                }
            }
            element = Element.None; refs.Clear(); tags.Clear();
        }
        void FinishRelation()
        {
            if (!skip && tags.TryGetValue("type", out var type) && type is "multipolygon" && members.Count > 0)
            {
                if (++relations > MaxRelations) throw new InvalidDataException($"지도 데이터의 관계가 {MaxRelations:N0}개를 넘습니다. 더 작은 영역을 내보내 주세요.");
                if (MapClasses.Classify(tags, true) is { } kind && MapClasses.IsArea(kind)) multipolygons.Add((kind, members.ToArray()));
            }
            element = Element.None; members.Clear(); tags.Clear();
        }
        using (var reader = XmlReader.Create(input, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.LocalName == "way" && element == Element.Way) FinishWay();
                    else if (reader.LocalName == "relation" && element == Element.Relation) FinishRelation();
                    else if (reader.LocalName == "node") element = Element.None;
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if ((++elements & 0xFFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (length > 0 && input.CanSeek) progress?.Report(Math.Clamp(input.Position / (double)length, 0, 1));
                }
                string name = reader.LocalName;
                if (!sawRoot)
                {
                    if (name != "osm") throw new InvalidDataException("OpenStreetMap 데이터(.osm) 파일이 아닙니다. openstreetmap.org의 내보내기 파일을 선택해 주세요.");
                    sawRoot = true; continue;
                }
                bool Deleted() => reader.GetAttribute("action") == "delete" || reader.GetAttribute("visible") == "false";
                switch (name)
                {
                    case "bounds":
                        var b = new MapBounds(Coordinate(reader.GetAttribute("minlat"), 90), Coordinate(reader.GetAttribute("minlon"), 180),
                            Coordinate(reader.GetAttribute("maxlat"), 90), Coordinate(reader.GetAttribute("maxlon"), 180));
                        if (b.IsValid) fileBounds = b;
                        break;
                    case "node":
                        if (!Deleted())
                        {
                            if (nodes.Count >= MaxNodes) throw new InvalidDataException($"지도 데이터의 점이 {MaxNodes:N0}개를 넘습니다. 더 작은 영역을 내보내 주세요.");
                            double lat = Coordinate(reader.GetAttribute("lat"), 90), lon = Coordinate(reader.GetAttribute("lon"), 180);
                            nodes[Id(reader.GetAttribute("id"))] = Pack(lat, lon);
                        }
                        element = reader.IsEmptyElement ? Element.None : Element.Node;
                        break;
                    case "way":
                        element = Element.Way; skip = Deleted(); id = Id(reader.GetAttribute("id")); refs.Clear(); tags.Clear();
                        if (reader.IsEmptyElement) FinishWay();
                        break;
                    case "relation":
                        element = Element.Relation; skip = Deleted(); members.Clear(); tags.Clear();
                        if (reader.IsEmptyElement) FinishRelation();
                        break;
                    case "nd":
                        if (element == Element.Way) refs.Add(Id(reader.GetAttribute("ref")));
                        break;
                    case "tag":
                        if (element is Element.Way or Element.Relation && reader.GetAttribute("k") is { Length: > 0 and <= 255 } key && reader.GetAttribute("v") is { Length: <= 255 } value)
                            tags[key] = value;
                        break;
                    case "member":
                        if (element == Element.Relation && reader.GetAttribute("type") == "way") members.Add((Id(reader.GetAttribute("ref")), reader.GetAttribute("role") ?? ""));
                        break;
                }
            }
        }
        if (!sawRoot) throw new InvalidDataException("OpenStreetMap 데이터(.osm) 파일이 아닙니다.");
        token.ThrowIfCancellationRequested();
        progress?.Report(1);

        var features = new List<MapFeature>(); long points = 0; int missing = 0;
        void Count(int n) { points += n; if (points > MaxFeaturePoints) throw new InvalidDataException("지도 데이터의 점이 너무 많습니다. 더 작은 영역을 내보내 주세요."); }
        GeoPoint[]? Resolve(long[] ids)
        {
            var result = new GeoPoint[ids.Length];
            for (int i = 0; i < ids.Length; i++) { if (!nodes.TryGetValue(ids[i], out long packed)) return null; result[i] = Unpack(packed); }
            return result;
        }
        // Lines split where the export left out nodes; areas need every node.
        foreach (var (wayId, kind) in lineWays)
        {
            var ids = wayRefs[wayId]; var parts = new List<GeoPoint[]>(); var run = new List<GeoPoint>();
            foreach (long node in ids)
            {
                if (nodes.TryGetValue(node, out long packed)) { run.Add(Unpack(packed)); continue; }
                missing++; if (run.Count >= 2) parts.Add(run.ToArray()); run.Clear();
            }
            if (run.Count >= 2) parts.Add(run.ToArray());
            if (parts.Count == 0) continue;
            Count(parts.Sum(p => p.Length)); features.Add(new(kind, parts.ToArray()));
        }
        foreach (var (wayId, kind) in areaWays)
        {
            if (Resolve(wayRefs[wayId]) is not { } ring) { missing++; continue; }
            Count(ring.Length); features.Add(new(kind, [ring]));
        }
        token.ThrowIfCancellationRequested();
        int incomplete = 0;
        foreach (var (kind, relationMembers) in multipolygons)
        {
            var memberWays = new List<long[]>();
            foreach (var (wayRef, _) in relationMembers) if (wayRefs.TryGetValue(wayRef, out var memberIds)) memberWays.Add(memberIds); else incomplete++;
            if (memberWays.Count == 0) continue;
            var closed = JoinRings(memberWays, out var open);
            var rings = new List<GeoPoint[]>();
            foreach (var ring in closed) if (Resolve(ring) is { } coordinates) rings.Add(coordinates);
            var chains = open.Select(Resolve).OfType<GeoPoint[]>().ToList();
            if (chains.Count > 0) { rings.AddRange(CloseChains(chains)); incomplete++; }
            if (rings.Count == 0) continue;
            Count(rings.Sum(r => r.Length)); features.Add(new(kind, rings.ToArray()));
        }
        var bounds = fileBounds ?? area ?? Extent(features, coastWays.Select(w => wayRefs[w]).SelectMany(ids => ids).Where(nodes.ContainsKey).Select(n => Unpack(nodes[n])));
        if (coastWays.Count > 0)
        {
            var coast = coastWays.Select(w => Resolve(wayRefs[w])).OfType<GeoPoint[]>().ToList();
            var sea = Sea(coast, bounds, warnings);
            if (sea.Count > 0) { Count(sea.Sum(r => r.Length)); features.Insert(0, new(MapClasses.Sea, sea.ToArray())); }
        }
        if (features.Count == 0)
            throw new InvalidDataException("지도 데이터에서 그릴 길·물·녹지·건물을 찾지 못했습니다. openstreetmap.org의 ‘내보내기’로 받은 .osm 파일인지 확인해 주세요.");
        if (missing > 0) warnings.Add($"내보낸 영역 밖의 점 {missing:N0}개가 빠져 일부 길과 면을 잘라 그렸습니다.");
        if (incomplete > 0) warnings.Add("영역 밖으로 이어지는 큰 물·녹지 면은 받은 부분만 이어 그렸습니다. 가장자리 모양이 실제와 다를 수 있습니다.");
        return new MapData { Bounds = bounds, BoundsFromFile = fileBounds != null || area != null, Features = features, Warnings = warnings };
    }

    static long Pack(double latitude, double longitude) =>
        (long)Math.Round(latitude * 1e7) << 32 | (uint)(int)Math.Round(longitude * 1e7);
    static GeoPoint Unpack(long packed) => new((int)(packed >> 32) / 1e7, (int)(uint)packed / 1e7);

    /// <summary>Joins member ways end to end (either direction) into closed rings; unclosed chains are returned in <paramref name="open"/>.</summary>
    internal static List<long[]> JoinRings(List<long[]> ways, out List<long[]> open)
    {
        var rings = new List<long[]>(); open = [];
        var used = new bool[ways.Count]; var ends = new Dictionary<long, List<int>>();
        for (int i = 0; i < ways.Count; i++)
        {
            if (ways[i].Length < 2) { used[i] = true; continue; }
            foreach (long end in new[] { ways[i][0], ways[i][^1] }) { if (!ends.TryGetValue(end, out var list)) ends[end] = list = []; list.Add(i); }
        }
        for (int i = 0; i < ways.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true; var chain = new List<long>(ways[i]);
            bool Extend(bool atEnd)
            {
                long tip = atEnd ? chain[^1] : chain[0];
                foreach (int j in ends.GetValueOrDefault(tip) ?? [])
                {
                    if (used[j]) continue;
                    used[j] = true; var way = ways[j];
                    IEnumerable<long> next = way[0] == tip ? way.Skip(1) : way.Reverse().Skip(1);
                    if (atEnd) chain.AddRange(next); else chain.InsertRange(0, next.Reverse());
                    return true;
                }
                return false;
            }
            while (chain[0] != chain[^1] && Extend(true)) { }
            while (chain[0] != chain[^1] && Extend(false)) { }
            if (chain.Count >= 4 && chain[0] == chain[^1]) rings.Add(chain.ToArray()); else open.Add(chain.ToArray());
        }
        return rings;
    }

    /// <summary>
    /// Closes the chains of an area whose other members lie outside the export: each chain is joined
    /// to the chain whose end is nearest, and a chain closes on itself when its own start is nearer.
    /// </summary>
    internal static List<GeoPoint[]> CloseChains(List<GeoPoint[]> chains)
    {
        static double Distance(GeoPoint a, GeoPoint b)
        {
            double k = Math.Cos((a.Latitude + b.Latitude) * Math.PI / 360), dx = (a.Longitude - b.Longitude) * k, dy = a.Latitude - b.Latitude;
            return dx * dx + dy * dy;
        }
        var remaining = chains.Where(c => c.Length >= 2).ToList(); var rings = new List<GeoPoint[]>();
        while (remaining.Count > 0)
        {
            var ring = new List<GeoPoint>(remaining[0]); remaining.RemoveAt(0);
            while (remaining.Count > 0)
            {
                int best = -1; bool reverse = false; double nearest = Distance(ring[^1], ring[0]);
                for (int j = 0; j < remaining.Count; j++)
                {
                    double start = Distance(ring[^1], remaining[j][0]), end = Distance(ring[^1], remaining[j][^1]);
                    if (start < nearest) { nearest = start; best = j; reverse = false; }
                    if (end < nearest) { nearest = end; best = j; reverse = true; }
                }
                if (best < 0) break;
                ring.AddRange(reverse ? remaining[best].Reverse() : remaining[best]); remaining.RemoveAt(best);
            }
            if (ring.Count >= 3) { if (ring[0] != ring[^1]) ring.Add(ring[0]); rings.Add(ring.ToArray()); }
        }
        return rings;
    }

    static MapBounds Extent(IEnumerable<MapFeature> features, IEnumerable<GeoPoint> extra)
    {
        double south = 90, north = -90, west = 180, east = -180;
        foreach (var p in features.SelectMany(f => f.Parts).SelectMany(p => p).Concat(extra))
        { south = Math.Min(south, p.Latitude); north = Math.Max(north, p.Latitude); west = Math.Min(west, p.Longitude); east = Math.Max(east, p.Longitude); }
        if (north <= south || east <= west) throw new InvalidDataException("지도 데이터의 범위를 알 수 없습니다.");
        return new(south, west, north, east);
    }

    // ---- Coastline → sea ---------------------------------------------------------------------

    sealed record Run(List<GeoPoint> Points, double In, double Out);

    /// <summary>
    /// The sea inside <paramref name="b"/> from natural=coastline ways (land on the left of their
    /// direction): chains crossing the box are joined by walking its border clockwise from each exit
    /// to the next entry; closed islands become holes; a box with only islands is sea around them.
    /// </summary>
    internal static List<GeoPoint[]> Sea(List<GeoPoint[]> coastlines, MapBounds b, List<string> warnings)
    {
        var chains = JoinDirected(coastlines);
        var islands = new List<GeoPoint[]>(); var polygons = new List<GeoPoint[]>(); var runs = new List<Run>();
        foreach (var chain in chains)
        {
            if (chain.Length >= 4 && chain[0] == chain[^1])
            {
                if (SignedArea(chain) > 0) islands.Add(chain); else polygons.Add(chain);
                continue;
            }
            runs.AddRange(Clip(chain, b));
        }
        polygons.AddRange(Walk(runs, b));
        if (runs.Count == 0 && polygons.Count == 0 && islands.Count > 0)
            polygons.Add([new(b.North, b.West), new(b.North, b.East), new(b.South, b.East), new(b.South, b.West), new(b.North, b.West)]);
        foreach (var island in islands)
            if (polygons.Any(p => Contains(p, island[0]))) polygons.Add(island);
        if (chains.Count > 0 && polygons.Count == 0) warnings.Add("해안선으로 바다를 채우지 못했습니다. 해안선이 영역을 가로지르는지 확인해 주세요.");
        return polygons;
    }

    // Coastline ways keep their direction; a way continues the chain whose end is its start.
    static List<GeoPoint[]> JoinDirected(List<GeoPoint[]> ways)
    {
        var parts = ways.Where(w => w.Length >= 2).ToArray();
        var starts = new Dictionary<GeoPoint, int>();
        for (int i = 0; i < parts.Length; i++) if (parts[i][0] != parts[i][^1]) starts.TryAdd(parts[i][0], i);
        var continued = new bool[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i][0] != parts[i][^1] && starts.TryGetValue(parts[i][^1], out int j) && j != i) continued[j] = true;
        var used = new bool[parts.Length]; var chains = new List<GeoPoint[]>();
        // Chains start at a way nothing leads into; what is left afterwards are closed loops of ways.
        foreach (int i in Enumerable.Range(0, parts.Length).OrderBy(i => continued[i]))
        {
            if (used[i]) continue;
            used[i] = true; var chain = new List<GeoPoint>(parts[i]);
            while (chain[0] != chain[^1] && starts.TryGetValue(chain[^1], out int next) && !used[next])
            { used[next] = true; chain.AddRange(parts[next].Skip(1)); }
            chains.Add(chain.ToArray());
        }
        return chains;
    }

    // Positive for counter-clockwise rings with north up (x = longitude, y = latitude).
    static double SignedArea(GeoPoint[] ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Length - 1; i++) sum += ring[i].Longitude * ring[i + 1].Latitude - ring[i + 1].Longitude * ring[i].Latitude;
        return sum / 2;
    }

    internal static bool Contains(GeoPoint[] ring, GeoPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var a = ring[i]; var c = ring[j];
            if ((a.Latitude > p.Latitude) != (c.Latitude > p.Latitude) &&
                p.Longitude < (c.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (c.Latitude - a.Latitude) + a.Longitude) inside = !inside;
        }
        return inside;
    }

    // Position along the box border, clockwise with north up: 0 north-west corner, 1 north-east,
    // 2 south-east, 3 south-west.
    static double Border(GeoPoint p, MapBounds b)
    {
        double w = b.East - b.West, h = b.North - b.South;
        double top = b.North - p.Latitude, right = b.East - p.Longitude, bottom = p.Latitude - b.South, left = p.Longitude - b.West;
        double min = Math.Min(Math.Min(top / h, bottom / h), Math.Min(right / w, left / w));
        if (min == top / h) return Math.Clamp((p.Longitude - b.West) / w, 0, 1);
        if (min == right / w) return 1 + Math.Clamp((b.North - p.Latitude) / h, 0, 1);
        if (min == bottom / h) return 2 + Math.Clamp((b.East - p.Longitude) / w, 0, 1);
        return (3 + Math.Clamp((p.Latitude - b.South) / h, 0, 1)) % 4;
    }
    static GeoPoint Corner(int k, MapBounds b) => (k % 4) switch
    {
        0 => new(b.North, b.West), 1 => new(b.North, b.East), 2 => new(b.South, b.East), _ => new(b.South, b.West)
    };
    // The nearest point on the border (a chain that stops inside the box is extended to it).
    static GeoPoint ToBorder(GeoPoint p, MapBounds b)
    {
        double w = b.East - b.West, h = b.North - b.South;
        double top = (b.North - p.Latitude) / h, right = (b.East - p.Longitude) / w, bottom = (p.Latitude - b.South) / h, left = (p.Longitude - b.West) / w;
        double min = Math.Min(Math.Min(top, bottom), Math.Min(right, left));
        return min == top ? p with { Latitude = b.North } : min == bottom ? p with { Latitude = b.South } : min == right ? p with { Longitude = b.East } : p with { Longitude = b.West };
    }

    // Parts of a chain inside the box, each from where it enters to where it leaves.
    static IEnumerable<Run> Clip(GeoPoint[] chain, MapBounds b)
    {
        List<GeoPoint>? current = null;
        Run Finish(List<GeoPoint> points)
        {
            if (Inside(points[0], b, true)) points.Insert(0, ToBorder(points[0], b));
            if (Inside(points[^1], b, true)) points.Add(ToBorder(points[^1], b));
            return new Run(points, Border(points[0], b), Border(points[^1], b));
        }
        for (int i = 0; i < chain.Length - 1; i++)
        {
            if (!Segment(chain[i], chain[i + 1], b, out double t0, out double t1)) continue;
            var p0 = Lerp(chain[i], chain[i + 1], t0); var p1 = Lerp(chain[i], chain[i + 1], t1);
            if (current == null) current = [p0];
            else if (t0 > 0) { yield return Finish(current); current = [p0]; }
            current.Add(p1);
            if (t1 < 1) { yield return Finish(current); current = null; }
        }
        if (current is { Count: >= 2 }) yield return Finish(current);
    }
    static bool Inside(GeoPoint p, MapBounds b, bool strict) => strict
        ? p.Latitude > b.South && p.Latitude < b.North && p.Longitude > b.West && p.Longitude < b.East
        : b.Contains(p.Latitude, p.Longitude);
    static GeoPoint Lerp(GeoPoint a, GeoPoint c, double t) => t <= 0 ? a : t >= 1 ? c : new(a.Latitude + (c.Latitude - a.Latitude) * t, a.Longitude + (c.Longitude - a.Longitude) * t);
    // Liang–Barsky: the parameter range of a segment inside the box.
    static bool Segment(GeoPoint a, GeoPoint c, MapBounds b, out double t0, out double t1)
    {
        t0 = 0; t1 = 1;
        double dx = c.Longitude - a.Longitude, dy = c.Latitude - a.Latitude;
        double[] p = [-dx, dx, -dy, dy], q = [a.Longitude - b.West, b.East - a.Longitude, a.Latitude - b.South, b.North - a.Latitude];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0) { if (q[i] < 0) return false; continue; }
            double r = q[i] / p[i];
            if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        return t1 > t0;
    }

    static List<GeoPoint[]> Walk(List<Run> runs, MapBounds b)
    {
        var result = new List<GeoPoint[]>(); var used = new bool[runs.Count];
        for (int start = 0; start < runs.Count; start++)
        {
            if (used[start]) continue;
            var ring = new List<GeoPoint>(); int current = start;
            for (int guard = 0; guard <= runs.Count; guard++)
            {
                used[current] = true; ring.AddRange(runs[current].Points);
                double from = runs[current].Out; int next = -1; double best = double.MaxValue;
                for (int j = 0; j < runs.Count; j++)
                {
                    if (used[j] && j != start) continue;
                    double d = ((runs[j].In - from) % 4 + 4) % 4;
                    if (d < best) { best = d; next = j; }
                }
                if (next < 0) break;
                // Corners passed walking clockwise from the exit to the next entry.
                for (int k = (int)Math.Floor(from) + 1; k < from + best; k++) ring.Add(Corner(k, b));
                if (next == start) break;
                current = next;
            }
            if (ring.Count >= 3) { ring.Add(ring[0]); result.Add(ring.ToArray()); }
        }
        return result;
    }

    // ---- GeoJSON -----------------------------------------------------------------------------

    /// <summary>GeoJSON features whose properties carry OpenStreetMap tags (directly or under "tags").</summary>
    public static MapData ReadGeoJson(Stream input, CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GeoJSON 파일이 아닙니다.");
        var features = new List<MapFeature>(); var coast = new List<GeoPoint[]>(); var warnings = new List<string>(); long points = 0; int visited = 0;
        MapBounds? fileBounds = null;
        if (root.TryGetProperty("bbox", out var box) && box.ValueKind == JsonValueKind.Array && box.GetArrayLength() >= 4)
        {
            var values = box.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray();
            var candidate = values.Length >= 6 ? new MapBounds(values[1], values[0], values[4], values[3]) : new MapBounds(values[1], values[0], values[3], values[2]);
            if (candidate.IsValid) fileBounds = candidate;
        }
        GeoPoint Point(JsonElement position)
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2) throw new InvalidDataException("GeoJSON 좌표가 올바르지 않습니다.");
            double lon = position[0].GetDouble(), lat = position[1].GetDouble();
            if (!double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) throw new InvalidDataException("GeoJSON 좌표가 올바르지 않습니다.");
            return new(lat, lon);
        }
        GeoPoint[] Line(JsonElement positions)
        {
            if (positions.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GeoJSON 좌표가 올바르지 않습니다.");
            var line = new GeoPoint[positions.GetArrayLength()]; int i = 0;
            foreach (var position in positions.EnumerateArray()) line[i++] = Point(position);
            points += line.Length;
            if (points > MaxFeaturePoints) throw new InvalidDataException("지도 데이터의 점이 너무 많습니다. 더 작은 영역을 내보내 주세요.");
            return line;
        }
        void Geometry(JsonElement geometry, Dictionary<string, string> tags)
        {
            if (geometry.ValueKind != JsonValueKind.Object || !geometry.TryGetProperty("type", out var kind)) return;
            if (++visited % 4096 == 0) token.ThrowIfCancellationRequested();
            var coordinates = geometry.TryGetProperty("coordinates", out var c) ? c : default;
            switch (kind.GetString())
            {
                case "LineString" or "MultiLineString":
                    GeoPoint[][] lines = kind.GetString() == "LineString" ? [Line(coordinates)] : coordinates.EnumerateArray().Select(Line).ToArray();
                    lines = lines.Where(l => l.Length >= 2).ToArray();
                    if (lines.Length == 0) return;
                    bool closed = lines.All(l => l.Length >= 4 && l[0] == l[^1]);
                    if (MapClasses.Classify(tags, closed) is not { } lineClass) return;
                    if (lineClass == MapClasses.Coastline) coast.AddRange(lines);
                    else if (!MapClasses.IsArea(lineClass) || closed) features.Add(new(lineClass, lines));
                    break;
                case "Polygon" or "MultiPolygon":
                    JsonElement[] polygons = kind.GetString() == "Polygon" ? [coordinates] : coordinates.EnumerateArray().ToArray();
                    var rings = polygons.SelectMany(p => p.EnumerateArray().Select(Line)).Where(r => r.Length >= 4).ToArray();
                    if (rings.Length == 0) return;
                    if (MapClasses.Classify(tags, true) is { } areaClass && MapClasses.IsArea(areaClass)) features.Add(new(areaClass, rings));
                    else if (MapClasses.Classify(tags, false) is { } outline && !MapClasses.IsArea(outline) && outline != MapClasses.Coastline) features.Add(new(outline, rings));
                    break;
                case "GeometryCollection":
                    if (geometry.TryGetProperty("geometries", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var part in parts.EnumerateArray()) Geometry(part, tags);
                    break;
            }
        }
        void Feature(JsonElement feature)
        {
            if (feature.ValueKind != JsonValueKind.Object) return;
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (feature.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                void Take(JsonElement source)
                {
                    foreach (var property in source.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String && property.Name.Length <= 255) tags[property.Name] = property.Value.GetString()!;
                }
                Take(properties);
                if (properties.TryGetProperty("tags", out var nested) && nested.ValueKind == JsonValueKind.Object) Take(nested);
            }
            if (feature.TryGetProperty("geometry", out var geometry)) Geometry(geometry, tags);
        }
        switch (type.GetString())
        {
            case "FeatureCollection":
                if (!root.TryGetProperty("features", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GeoJSON 파일에 features가 없습니다.");
                foreach (var feature in list.EnumerateArray()) Feature(feature);
                break;
            case "Feature": Feature(root); break;
            default: throw new InvalidDataException("지도 레이어를 만들려면 OpenStreetMap 태그가 있는 GeoJSON Feature가 필요합니다.");
        }
        var bounds = fileBounds ?? Extent(features, coast.SelectMany(c => c));
        if (coast.Count > 0)
        {
            var sea = Sea(coast, bounds, warnings);
            if (sea.Count > 0) features.Insert(0, new(MapClasses.Sea, sea.ToArray()));
        }
        if (features.Count == 0) throw new InvalidDataException("GeoJSON에서 그릴 길·물·녹지·건물을 찾지 못했습니다. 속성에 OpenStreetMap 태그(highway, building 등)가 있어야 합니다.");
        return new MapData { Bounds = bounds, BoundsFromFile = fileBounds != null, Features = features, Warnings = warnings };
    }
}
