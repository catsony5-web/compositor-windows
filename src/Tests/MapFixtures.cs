using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Compositor.Windows;

// Synthetic OpenStreetMap data for the map tests, generated in code (never downloaded): roads of
// every class, rail (and a subway tunnel that must be skipped), a river multipolygon of two outer
// ways around an island, a river line, a park, a forest, grass, buildings, an incomplete forest
// relation, a deleted way and a building=no. Positions are fractions of the bounds.
internal static class MapFixtures
{
    public static readonly MapBounds Bounds = new(37.5600, 126.9700, 37.5730, 126.9860);
    public const int Buildings = 12;
    // The island inside the river and a point of open water beside it (fractions of the bounds).
    public static readonly (double U, double V) Island = (.44, .535), Water = (.2, .54), Land = (.2, .75);

    public static GeoPoint At(MapBounds b, double u, double v) => new(b.South + v * (b.North - b.South), b.West + u * (b.East - b.West));

    public static string Osm(MapBounds? area = null, bool bounds = true, int extraNodes = 0)
    {
        var b = area ?? Bounds; var xml = new StringBuilder(); long nextNode = 1, nextWay = 1000;
        var nodes = new StringBuilder(); var ways = new StringBuilder(); var relations = new StringBuilder();
        string F(double v) => v.ToString("0.0000000", CultureInfo.InvariantCulture);
        long Node(double u, double v) { var p = At(b, u, v); long id = nextNode++; nodes.Append($"  <node id=\"{id}\" lat=\"{F(p.Latitude)}\" lon=\"{F(p.Longitude)}\" version=\"1\"/>\n"); return id; }
        long Way(IEnumerable<long> ids, string tags, string attributes = "")
        {
            long id = nextWay++;
            ways.Append($"  <way id=\"{id}\" version=\"1\"{attributes}>\n");
            foreach (long n in ids) ways.Append($"    <nd ref=\"{n}\"/>\n");
            foreach (var pair in tags.Split(';', StringSplitOptions.RemoveEmptyEntries)) { var kv = pair.Split('='); ways.Append($"    <tag k=\"{kv[0]}\" v=\"{kv[1]}\"/>\n"); }
            ways.Append("  </way>\n"); return id;
        }
        long Line(string tags, params (double U, double V)[] points) => Way(points.Select(p => Node(p.U, p.V)).ToArray(), tags);
        long Ring(string tags, double u0, double v0, double u1, double v1)
        {
            var ids = new[] { Node(u0, v0), Node(u1, v0), Node(u1, v1), Node(u0, v1) };
            return Way(ids.Append(ids[0]), tags);
        }
        Line("highway=motorway", (-.05, .66), (.5, .67), (1.05, .64));
        Line("highway=primary;name=세종대로", (.3, -.02), (.31, .45), (.32, 1.02));
        Line("highway=secondary", (.75, 0), (.72, 1));
        Line("highway=tertiary", (0, .3), (1, .28));
        foreach (double u in new[] { .1, .2, .4, .5, .6, .85, .9 }) Line("highway=residential", (u, .05), (u, .45));
        foreach (double v in new[] { .1, .2, .45 }) Line("highway=residential", (.05, v), (.95, v));
        Line("highway=service", (.62, .12), (.68, .2));
        Line("highway=footway", (.55, .7), (.6, .8), (.65, .9));
        Line("railway=rail", (0, .85), (1, .78));
        Line("railway=subway;tunnel=yes", (0, .9), (1, .9));
        Line("power=line", (0, .95), (1, .95));
        Way([Node(.1, .02), Node(.9, .02)], "highway=primary", " action=\"delete\"");
        // The river: two outer ways that close a ring together, and an island (inner).
        long nw = Node(-.1, .58), ne = Node(1.1, .58);
        long north = Way([nw, Node(.5, .6), ne], "");
        long south = Way([ne, Node(1.1, .5), Node(.5, .48), Node(-.1, .5), nw], "");
        long island = Ring("", .4, .52, .48, .555);
        relations.Append($"  <relation id=\"5000\" version=\"1\">\n    <member type=\"way\" ref=\"{north}\" role=\"outer\"/>\n    <member type=\"way\" ref=\"{south}\" role=\"outer\"/>\n    <member type=\"way\" ref=\"{island}\" role=\"inner\"/>\n    <tag k=\"type\" v=\"multipolygon\"/>\n    <tag k=\"natural\" v=\"water\"/>\n    <tag k=\"water\" v=\"river\"/>\n  </relation>\n");
        Line("waterway=river", (-.1, .54), (.5, .545), (1.1, .54));
        Line("waterway=stream", (.8, .7), (.9, .95));
        Ring("leisure=park", .55, .12, .7, .3);
        Ring("landuse=forest", .05, .7, .25, .95);
        Ring("landuse=grass", .4, .7, .5, .76);
        int built = 0;
        for (int row = 0; row < 3; row++) for (int column = 0; column < 4; column++, built++)
            Ring("building=yes", .12 + column * .065, .12 + row * .1, .16 + column * .065, .17 + row * .1);
        Ring("building=no", .8, .12, .83, .15);
        // A forest relation whose second member lies outside the export.
        long edge = Way([Node(.75, .82), Node(.95, .82), Node(.95, .97), Node(.75, .97)], "");
        relations.Append($"  <relation id=\"5001\" version=\"1\">\n    <member type=\"way\" ref=\"{edge}\" role=\"outer\"/>\n    <member type=\"way\" ref=\"999999\" role=\"outer\"/>\n    <tag k=\"type\" v=\"multipolygon\"/>\n    <tag k=\"natural\" v=\"wood\"/>\n  </relation>\n");
        for (int i = 0; i < extraNodes; i++) Node(.5 + i % 1000 * 1e-5, .5);
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<osm version=\"0.6\" generator=\"Morupixel self-test\">\n");
        if (bounds) xml.Append($"  <bounds minlat=\"{F(b.South)}\" minlon=\"{F(b.West)}\" maxlat=\"{F(b.North)}\" maxlon=\"{F(b.East)}\"/>\n");
        xml.Append(nodes).Append(ways).Append(relations).Append("</osm>\n");
        return xml.ToString();
    }

    // A coastline crossing the box west to east (land north, sea south) and an island in the sea.
    public static string Coast(MapBounds b)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\"?>\n<osm version=\"0.6\">\n");
        string F(double v) => v.ToString("0.0000000", CultureInfo.InvariantCulture);
        xml.Append($"  <bounds minlat=\"{F(b.South)}\" minlon=\"{F(b.West)}\" maxlat=\"{F(b.North)}\" maxlon=\"{F(b.East)}\"/>\n");
        var points = new[] { (-.2, .5), (.3, .55), (.6, .45), (1.2, .5) };
        for (int i = 0; i < points.Length; i++) { var p = At(b, points[i].Item1, points[i].Item2); xml.Append($"  <node id=\"{i + 1}\" lat=\"{F(p.Latitude)}\" lon=\"{F(p.Longitude)}\"/>\n"); }
        // The island runs counter-clockwise (land on the left).
        var island = new[] { (.4, .15), (.6, .15), (.6, .3), (.4, .3) };
        for (int i = 0; i < island.Length; i++) { var p = At(b, island[i].Item1, island[i].Item2); xml.Append($"  <node id=\"{i + 11}\" lat=\"{F(p.Latitude)}\" lon=\"{F(p.Longitude)}\"/>\n"); }
        // Split into two ways that continue each other.
        xml.Append("  <way id=\"1\"><nd ref=\"1\"/><nd ref=\"2\"/><tag k=\"natural\" v=\"coastline\"/></way>\n");
        xml.Append("  <way id=\"2\"><nd ref=\"2\"/><nd ref=\"3\"/><nd ref=\"4\"/><tag k=\"natural\" v=\"coastline\"/></way>\n");
        xml.Append("  <way id=\"3\"><nd ref=\"11\"/><nd ref=\"12\"/><nd ref=\"13\"/><nd ref=\"14\"/><nd ref=\"11\"/><tag k=\"natural\" v=\"coastline\"/></way>\n");
        xml.Append("  <way id=\"4\"><nd ref=\"1\"/><nd ref=\"4\"/><tag k=\"highway\" v=\"residential\"/></way>\n");
        xml.Append("</osm>\n");
        return xml.ToString();
    }

    // A deterministic made-up city for review captures: a meandering river with islands, a bay in the
    // south-west corner, wooded hills, parks, two rotated street grids with arterials and a ring
    // expressway, a railway and blocks of buildings around the centre.
    public static MapData City(MapBounds b, int seed = 7)
    {
        var random = new Random(seed); var features = new List<MapFeature>();
        GeoPoint P(double u, double v) => At(b, u, v);
        double River(double u) => .47 + .08 * Math.Sin(2 * Math.PI * (u * 1.25 + .1)) + .025 * Math.Sin(2 * Math.PI * u * 3.1);
        double Half(double u) => .017 + .006 * Math.Sin(2 * Math.PI * u * 2 + 1);
        double Coast(double u) => .28 - (u + .1) * .8 + .04 * Math.Sin(u * 9);
        bool Sea(double u, double v) => v < Coast(u);
        bool Wet(double u, double v) => Sea(u, v) || Math.Abs(v - River(u)) < Half(u) + .006;
        GeoPoint[] Blob(double cu, double cv, double ru, double rv, int n, double rough)
        {
            var points = Enumerable.Range(0, n).Select(i =>
            {
                double a = 2 * Math.PI * i / n, r = 1 + rough * (Math.Sin(a * 3 + cu * 10) * .5 + Math.Sin(a * 5 + cv * 7) * .3);
                return P(cu + Math.Cos(a) * ru * r, cv + Math.Sin(a) * rv * r);
            }).ToList();
            points.Add(points[0]); return points.ToArray();
        }
        void Add(string kind, params GeoPoint[][] parts) => features.Add(new(kind, parts));
        // Water: the bay (coastline), the river with two islands, a lake and streams.
        var coast = Enumerable.Range(0, 41).Select(i => { double u = -.15 + i * .5 / 40; return P(u, Coast(u)); }).ToArray();
        features.AddRange(MapImport.Sea([coast], b, []).Select(r => new MapFeature(MapClasses.Sea, [r])));
        var north = Enumerable.Range(0, 81).Select(i => { double u = -.05 + i * 1.1 / 80; return P(u, River(u) + Half(u)); });
        var south = Enumerable.Range(0, 81).Select(i => { double u = 1.05 - i * 1.1 / 80; return P(u, River(u) - Half(u)); });
        var bank = north.Concat(south).ToList(); bank.Add(bank[0]);
        Add(MapClasses.WaterArea, bank.ToArray(), Blob(.36, River(.36), .035, .007, 24, .1), Blob(.63, River(.63), .025, .006, 24, .1));
        Add(MapClasses.WaterArea, Blob(.8, .84, .045, .035, 40, .25));
        Add(MapClasses.River, Enumerable.Range(0, 81).Select(i => { double u = -.05 + i * 1.1 / 80; return P(u, River(u)); }).ToArray());
        foreach (double start in new[] { .15, .55, .9 })
            Add(MapClasses.Stream, Enumerable.Range(0, 30).Select(i => { double t = i / 29.0, u = start + .03 * Math.Sin(t * 9), v = 1.02 - t * (1.02 - River(start) - Half(start)); return P(u, v); }).ToArray());
        // Green: hills, parks and grass.
        Add(MapClasses.Forest, Blob(.2, .86, .16, .1, 60, .3)); Add(MapClasses.Forest, Blob(.88, .16, .11, .09, 50, .3)); Add(MapClasses.Forest, Blob(.55, .9, .07, .05, 40, .3));
        foreach (var (u, v) in new[] { (.42, .66), (.68, .3), (.25, .3), (.58, .56), (.8, .62) }) Add(MapClasses.Park, Blob(u, v, .03 + random.NextDouble() * .02, .02 + random.NextDouble() * .015, 24, .15));
        Add(MapClasses.Grass, Blob(.5, .38, .02, .012, 20, .1)); Add(MapClasses.Grass, Blob(.33, .58, .018, .01, 20, .1));
        // Streets: two grids, one each side of the river, rotated differently, with jittered corners.
        void Grid(double angle, double cu, double cv, double spacing, Func<double, double, bool> inside)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle); int n = 32;
            (double U, double V) Corner(int i, int j)
            {
                double x = (i - n / 2) * spacing, y = (j - n / 2) * spacing;
                var r = new Random(i * 7919 + j * 104729 + seed);
                x += (r.NextDouble() - .5) * spacing * .25; y += (r.NextDouble() - .5) * spacing * .25;
                return (cu + x * cos - y * sin, cv + x * sin + y * cos);
            }
            for (int i = 0; i <= n; i++) for (int j = 0; j <= n; j++)
            foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
            {
                if (i + di > n || j + dj > n) continue;
                var a = Corner(i, j); var c = Corner(i + di, j + dj);
                if (!inside(a.U, a.V) || !inside(c.U, c.V) || Wet(a.U, a.V) || Wet(c.U, c.V) || Wet((a.U + c.U) / 2, (a.V + c.V) / 2)) continue;
                bool major = (di == 1 ? j : i) % 6 == 0, minor = (di == 1 ? j : i) % 3 == 0;
                double d = Math.Sqrt((a.U - .5) * (a.U - .5) + (a.V - .5) * (a.V - .5));
                if (!major && random.NextDouble() < Math.Clamp((d - .25) * 1.6, 0, .85)) continue;
                string kind = major ? MapClasses.Major : minor ? MapClasses.Minor : random.NextDouble() < .12 ? MapClasses.Service : MapClasses.Local;
                Add(kind, [P(a.U, a.V), P(c.U, c.V)]);
                if (!major && !minor && d < .22 && random.NextDouble() < .5)
                {
                    // Buildings along the block.
                    double bu = (a.U + c.U) / 2, bv = (a.V + c.V) / 2, s = spacing * .16;
                    foreach (int side in new[] { -1, 1 })
                    {
                        double ou = bu + (di == 1 ? -sin : cos) * spacing * .3 * side, ov = bv + (di == 1 ? cos : sin) * spacing * .3 * side;
                        if (Wet(ou, ov)) continue;
                        var corners = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1), (-1, -1) }.Select(k => P(ou + (k.Item1 * cos - k.Item2 * sin) * s, ov + (k.Item1 * sin + k.Item2 * cos) * s * .8)).ToArray();
                        Add(MapClasses.Building, corners);
                    }
                }
            }
        }
        Grid(.21, .5, .7, .018, (u, v) => v > River(u) && v < 1.05 && Math.Abs(u - .2) + Math.Abs(v - .86) > .2);
        Grid(-.14, .52, .25, .018, (u, v) => v < River(u) && !Sea(u, v) && Math.Abs(u - .88) + Math.Abs(v - .16) > .16);
        // Arterials crossing the river on bridges, the ring expressway and the railway.
        foreach (double u0 in new[] { .3, .5, .7 }) Add(MapClasses.Major, Enumerable.Range(0, 60).Select(i => { double t = i / 59.0; return P(u0 + .04 * Math.Sin(t * 5 + u0 * 9), -.02 + t * 1.04); }).Where(p => !Sea((p.Longitude - b.West) / (b.East - b.West), (p.Latitude - b.South) / (b.North - b.South))).ToArray());
        Add(MapClasses.Motorway, Enumerable.Range(0, 121).Select(i => { double a = 2 * Math.PI * i / 120; return P(.52 + .36 * Math.Cos(a), .5 + .31 * Math.Sin(a)); }).ToArray());
        Add(MapClasses.Motorway, Enumerable.Range(0, 81).Select(i => { double u = -.05 + i * 1.1 / 80; return P(u, River(u) + Half(u) + .012); }).Where(p => !Sea((p.Longitude - b.West) / (b.East - b.West), (p.Latitude - b.South) / (b.North - b.South))).ToArray());
        Add(MapClasses.Rail, Enumerable.Range(0, 80).Select(i => { double t = i / 79.0; return P(-.02 + t * 1.04, .2 + t * .55 + .05 * Math.Sin(t * 4)); }).ToArray());
        foreach (var (u, v) in new[] { (.42, .66), (.68, .3), (.58, .56) })
            Add(MapClasses.Path, Enumerable.Range(0, 12).Select(i => P(u - .025 + i * .0045, v + .01 * Math.Sin(i))).ToArray());
        return new MapData { Bounds = b, BoundsFromFile = true, Features = features };
    }

    public static MapData Read(string xml, MapBounds? area = null)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var stream = new MemoryStream(bytes);
        return MapImport.ReadOsm(stream, bytes.Length, null, default, area);
    }
}

/// <summary>A network layer that never touches the network: it records requests and answers from code.</summary>
internal sealed class FakeMapHttp : IMapHttp
{
    public readonly List<(string Method, string Url, string UserAgent, string Body, DateTime At)> Calls = [];
    public Func<HttpRequestMessage, byte[]>? Answer;
    public Exception? Failure;

    public async Task<byte[]> SendAsync(HttpRequestMessage request, long maxBytes, CancellationToken token)
    {
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
        lock (Calls) Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString(), body, DateTime.UtcNow));
        if (Failure != null) throw Failure;
        if (Answer != null) return Answer(request);
        if (request.Method == HttpMethod.Get)
            return Encoding.UTF8.GetBytes("[{\"display_name\":\"서울특별시청, 세종대로, 중구, 서울특별시, 대한민국\",\"lat\":\"37.5663\",\"lon\":\"126.9779\",\"boundingbox\":[\"37.5660\",\"37.5667\",\"126.9775\",\"126.9783\"]}]");
        // The same synthetic town, placed in the requested box (Overpass answers carry no bounds).
        var match = System.Text.RegularExpressions.Regex.Match(System.Net.WebUtility.UrlDecode(body), @"\[bbox:([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+)\]");
        var area = match.Success ? new MapBounds(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)) : MapFixtures.Bounds;
        return Encoding.UTF8.GetBytes(MapFixtures.Osm(area, bounds: false));
    }
}
