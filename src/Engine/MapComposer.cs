using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Settings of a map made by <see cref="MapComposer"/> (지도 포스터 · 대지 위치도).</summary>
public sealed record MapOptions
{
    public MapTemplate Template { get; init; } = MapTemplate.Poster;
    public string Theme { get; init; } = MapThemes.DefaultKey;
    public int Width { get; init; } = 1772;
    public int Height { get; init; } = 2362;
    public double Dpi { get; init; } = 150;
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool Coordinates { get; init; } = true;
    public bool Buildings { get; init; } = true;
    public bool Paths { get; init; } = true;
    public bool Rail { get; init; } = true;
    public bool Green { get; init; } = true;
    public bool Water { get; init; } = true;
    /// <summary>Multiplier of every line weight (0.25–4).</summary>
    public double LineWeight { get; init; } = 1;
    public double? CenterLatitude { get; init; }
    public double? CenterLongitude { get; init; }
    /// <summary>Ground metres per document pixel; null fits the data to the map frame.</summary>
    public double? MetersPerPixel { get; init; }
    /// <summary>Poster: margins and a border line around the map with the title below it.</summary>
    public bool Frame { get; init; }
    /// <summary>Soften the map toward the page colour at its edges (and behind a poster's title).</summary>
    public bool Fade { get; init; } = true;
    public double? SiteLatitude { get; init; }
    public double? SiteLongitude { get; init; }
    public MapMarker Marker { get; init; } = MapMarker.Circle;
    // Default texts follow the display language: they become document text the user then owns.
    public string SiteLabel { get; init; } = Loc.T("계획 대지");
    /// <summary>Radius rings around the site in metres.</summary>
    public double[] Rings { get; init; } = [];
    public bool NorthArrow { get; init; }
    public bool ScaleBar { get; init; }

    public static MapOptions For(MapTemplate template) => template == MapTemplate.SiteLocation
        ? new() { Template = template, Width = 2480, Height = 1754, Title = Loc.T("대지 위치도"), Fade = false, NorthArrow = true, ScaleBar = true, Rings = [250, 500], Theme = "mono", Paths = true }
        : new() { Template = template };

    public void Validate()
    {
        static bool Coordinate(double? v, double limit) => v is not { } x || double.IsFinite(x) && Math.Abs(x) <= limit;
        if (!Enum.IsDefined(Template) || !Enum.IsDefined(Marker) || !MapThemes.Exists(Theme)) throw new InvalidDataException("지도 양식·표시 모양·테마가 올바르지 않습니다.");
        Raster.ValidateSize(Width, Height);
        if (Width < 200 || Height < 200 || Width > 12000 || Height > 12000 || (long)Width * Height > 64_000_000) throw new InvalidDataException("지도 크기는 한 변 200~12,000px, 전체 6,400만 픽셀 이하로 정해 주세요.");
        if (!double.IsFinite(Dpi) || Dpi < 36 || Dpi > 1200) throw new InvalidDataException("지도 해상도는 36~1,200 DPI로 정해 주세요.");
        if (!double.IsFinite(LineWeight) || LineWeight < .25 || LineWeight > 4) throw new InvalidDataException("선 굵기는 25~400%로 정해 주세요.");
        if (Title == null || Subtitle == null || SiteLabel == null || Title.Length > 200 || Subtitle.Length > 400 || SiteLabel.Length > 100) throw new InvalidDataException("지도 글자가 너무 깁니다.");
        if (!Coordinate(CenterLatitude, WebMercator.MaxLatitude) || !Coordinate(CenterLongitude, 180) || !Coordinate(SiteLatitude, WebMercator.MaxLatitude) || !Coordinate(SiteLongitude, 180) ||
            CenterLatitude.HasValue != CenterLongitude.HasValue || SiteLatitude.HasValue != SiteLongitude.HasValue)
            throw new InvalidDataException("지도 중심·대지 좌표가 올바르지 않습니다.");
        if (MetersPerPixel is { } mpp && (!double.IsFinite(mpp) || mpp < .01 || mpp > 100_000)) throw new InvalidDataException("지도 축척이 올바르지 않습니다.");
        if (Rings == null || Rings.Length > 6 || Rings.Any(r => !double.IsFinite(r) || r < 1 || r > 1_000_000)) throw new InvalidDataException("반경 원은 6개까지, 1m~1,000km로 정해 주세요.");
    }
}

/// <summary>
/// Builds a map document from <see cref="MapData"/>: Web Mercator into document pixels at a known
/// ground scale, one editable vector layer per class (roads by class, rail, water, green, buildings)
/// grouped by kind, then the template's text, marker, north arrow, scale bar and the required
/// "© OpenStreetMap contributors" attribution, which always stays on top. Runs on an STA thread.
/// </summary>
public static class MapComposer
{
    public const string Attribution = "© OpenStreetMap contributors";
    const int FiguresPerPath = 4096;

    sealed record Layout(Rect Frame, double Short, bool Overlay);

    /// <summary>
    /// The map document. <paramref name="renderScale"/> below 1 draws a smaller copy for previews:
    /// the same layout, scale and line weights, every length multiplied by it.
    /// </summary>
    public static Document Build(MapData data, MapOptions options, CancellationToken token = default, double renderScale = 1)
    {
        options.Validate();
        if (!double.IsFinite(renderScale) || renderScale <= 0 || renderScale > 1) throw new ArgumentOutOfRangeException(nameof(renderScale));
        double k = renderScale;
        int width = Math.Max(16, (int)Math.Round(options.Width * k)), height = Math.Max(16, (int)Math.Round(options.Height * k));
        var theme = MapThemes.Get(options.Theme);
        var layout = Arrange(options);
        var full = View(data, options, layout.Frame);
        var view = full with { MetersPerPixel = full.MetersPerPixel / k, Frame = Scale(layout.Frame, k) };
        double s = layout.Short * k, weight = LineFactor(layout.Short, full.MetersPerPixel) * options.LineWeight * k;

        string title = string.IsNullOrWhiteSpace(options.Title) ? (options.Template == MapTemplate.SiteLocation ? Loc.T("대지 위치도") : Loc.T("지도")) : options.Title.Trim();
        var document = new Document { Name = title, Width = width, Height = height, Dpi = options.Dpi };
        var canvas = new Raster(width, height);
        var site = options.SiteLatitude is { } siteLat && options.SiteLongitude is { } siteLon ? new GeoPoint(siteLat, siteLon)
            : options.Template == MapTemplate.SiteLocation ? new GeoPoint(full.CenterLatitude, full.CenterLongitude) : (GeoPoint?)null;
        string kind = options.Template == MapTemplate.SiteLocation ? "대지 위치도" : "지도 포스터";
        string about = title != Loc.T(kind) ? title : options.Subtitle.Trim();
        var root = new Layer
        {
            Name = about.Length == 0 ? Loc.T(kind) : Loc.T(kind) + " · " + about, Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = canvas,
            Map = new MapTag
            {
                Role = MapTag.RootRole, CenterLatitude = view.CenterLatitude, CenterLongitude = view.CenterLongitude, MetersPerPixel = view.MetersPerPixel,
                FrameX = view.Frame.X, FrameY = view.Frame.Y, FrameWidth = view.Frame.Width, FrameHeight = view.Frame.Height,
                Template = options.Template, Theme = theme.Key, SiteLatitude = site?.Latitude, SiteLongitude = site?.Longitude
            }
        };
        document.Layers.Add(root);
        Layer Group(string key, Guid parent)
        {
            var group = new Layer { Name = Loc.T(MapClasses.Name(key)), Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = canvas, ParentId = parent, Map = new MapTag { Role = key } };
            document.Layers.Add(group); return group;
        }
        void Add(Layer layer, string key, Guid parent)
        {
            layer.ParentId = parent; layer.Map = new MapTag { Role = key }; layer.Category = LayerCategory.Drawing;
            if (layer.Kind != LayerKind.Text) layer.Name = Loc.T(MapClasses.Name(key));
            document.Layers.Add(layer);
        }

        var background = VectorShapes.Create(new ShapeSpec { Width = width, Height = height, FillArgb = theme.Background });
        Add(background, MapClasses.Background, root.Id);

        // Map content, back to front: green, water, buildings, roads (small to large), rail.
        var clip = new RectangleGeometry(view.Frame); clip.Freeze();
        var byClass = data.Features.GroupBy(f => f.Class).ToDictionary(g => g.Key, g => g.ToArray());
        bool Wanted(string key) => key switch
        {
            MapClasses.Park or MapClasses.Forest or MapClasses.Grass => options.Green,
            MapClasses.Sea or MapClasses.WaterArea or MapClasses.River or MapClasses.Stream => options.Water,
            MapClasses.Building => options.Buildings, MapClasses.Rail => options.Rail, MapClasses.Path => options.Paths,
            _ => true
        };
        Layer? ClassLayer(string key)
        {
            token.ThrowIfCancellationRequested();
            if (!Wanted(key) || !byClass.TryGetValue(key, out var features)) return null;
            uint color = theme.Color(key);
            var geometries = MapClasses.IsArea(key) ? Areas(features, view, token) : Lines(features, view, token);
            if (geometries.Count == 0) return null;
            double stroke = MapClasses.IsArea(key) ? 0 : BaseWeight(key) * weight;
            var primitives = geometries.Select(g => new VectorPrimitive(g, VectorShapes.Color(color), MapClasses.IsArea(key), stroke, clip)).ToArray();
            return VectorLayer(width, height, primitives, 0, 0);
        }
        void Section(string groupKey, params string[] keys)
        {
            var layers = keys.Select(key => (Key: key, Layer: ClassLayer(key))).Where(p => p.Layer != null).ToArray();
            if (layers.Length == 0) return;
            if (groupKey.Length == 0) { foreach (var (key, layer) in layers) Add(layer!, key, root.Id); return; }
            var group = Group(groupKey, root.Id);
            foreach (var (key, layer) in layers) Add(layer!, key, group.Id);
        }
        Section(MapClasses.GreenGroup, MapClasses.Forest, MapClasses.Grass, MapClasses.Park);
        Section(MapClasses.WaterGroup, MapClasses.Sea, MapClasses.WaterArea, MapClasses.River, MapClasses.Stream);
        Section("", MapClasses.Building);
        Section(MapClasses.RoadGroup, MapClasses.Roads);
        Section("", MapClasses.Rail);
        if (document.Layers.Count(l => l.Kind == LayerKind.Vector) == 0)
            throw new InvalidDataException("고른 범위와 항목으로는 그릴 지도 요소가 없습니다. 그릴 것을 더 켜거나 다른 영역을 골라 주세요.");

        if (options.Fade) Add(FadeLayer(width, height, view.Frame, s, layout.Overlay, theme.Background), MapClasses.Fade, root.Id);
        if (options.Frame || options.Template == MapTemplate.SiteLocation)
        {
            double line = Math.Max(1.5, s * .0012);
            var frame = VectorShapes.Create(new ShapeSpec
            {
                Width = Math.Max(1, (int)Math.Round(view.Frame.Width + line * 2)), Height = Math.Max(1, (int)Math.Round(view.Frame.Height + line * 2)),
                FillEnabled = false, StrokeEnabled = true, StrokeWidth = line, StrokeArgb = theme.Color(MapClasses.Frame)
            }, view.Frame.X - line, view.Frame.Y - line);
            Add(frame, MapClasses.Frame, root.Id);
        }

        // Site marker, rings, north arrow and scale bar.
        Layer? siteGroup = null;
        Guid SiteParent() => (siteGroup ??= Group(MapClasses.SiteGroup, root.Id)).Id;
        TextSpec Label(string text, double size, uint color, bool bold = false) => new()
        {
            Content = text, FontFamily = Font(text, false), FontSize = Math.Clamp(Math.Round(size), 6, 1024), Bold = bold, ColorArgb = color,
            Outline = true, OutlineWidth = Math.Clamp(Math.Round(size * .18, 1), .5, 64), OutlineArgb = theme.Background, OutlinePosition = TextOutlinePosition.Outside
        };
        if (site is { } point && view.Frame.Contains(view.ToPixel(point.Latitude, point.Longitude)))
        {
            var at = view.ToPixel(point.Latitude, point.Longitude);
            var rings = options.Rings.Distinct().OrderBy(r => r).ToArray();
            if (rings.Length > 0)
            {
                double line = Math.Max(1.2, s * .0011);
                var circles = rings.Select(r => (Geometry)new EllipseGeometry(at, r / view.MetersPerPixel, r / view.MetersPerPixel)).ToArray();
                Add(VectorLayer(width, height, circles.Select(c => new VectorPrimitive(c, VectorShapes.Color(theme.Accent), false, line, clip)).ToArray(), 0, 0), MapClasses.Rings, SiteParent());
                foreach (double r in rings)
                {
                    // On the ring at the top, or the first place round it that stays inside the map.
                    double radius = r / view.MetersPerPixel;
                    var label = DocumentFeatures.CreateText(Label(Distance(r), s * .016, theme.Accent));
                    var inner = view.Frame; double padX = s * .01 + label.Pixels.Width / 2.0, padY = s * .01 + label.Pixels.Height / 2.0;
                    if (inner.Width <= padX * 2 || inner.Height <= padY * 2) continue;
                    inner.Inflate(-padX, -padY);
                    foreach (double angle in new double[] { -90, -60, -120, -30, -150, 0, 180, 30, 150, 60, 120, 90 })
                    {
                        var p = new Point(at.X + radius * Math.Cos(angle * Math.PI / 180), at.Y + radius * Math.Sin(angle * Math.PI / 180));
                        if (!inner.Contains(p)) continue;
                        label.X = Math.Round(p.X - label.Pixels.Width / 2.0); label.Y = Math.Round(p.Y - label.Pixels.Height / 2.0);
                        Add(label, MapClasses.RingLabel, SiteParent()); break;
                    }
                }
            }
            var marker = Marker(options.Marker, s, theme);
            var markerLayer = VectorLayer(marker.Width, marker.Height, marker.Primitives, 0, 0);
            markerLayer.X = Math.Round(at.X - marker.Anchor.X); markerLayer.Y = Math.Round(at.Y - marker.Anchor.Y);
            Add(markerLayer, MapClasses.Marker, SiteParent());
            if (!string.IsNullOrWhiteSpace(options.SiteLabel))
            {
                var label = DocumentFeatures.CreateText(Label(options.SiteLabel.Trim(), s * .022, theme.Text, true));
                label.X = Math.Round(markerLayer.X + marker.Width + s * .004); label.Y = Math.Round(markerLayer.Y + marker.Anchor.Y * (options.Marker == MapMarker.Pin ? .45 : 1) - label.Pixels.Height / 2.0);
                Add(label, MapClasses.SiteLabel, SiteParent());
            }
        }
        if (options.NorthArrow)
        {
            var arrow = NorthArrow(s, theme);
            var layer = VectorLayer(arrow.Width, arrow.Height, arrow.Primitives, 0, 0);
            layer.X = Math.Round(view.Frame.Right - s * .03 - arrow.Width); layer.Y = Math.Round(view.Frame.Top + s * .03);
            Add(layer, MapClasses.NorthArrow, SiteParent());
        }
        if (options.ScaleBar)
        {
            var (meters, pixels) = ScaleBarLength(view.MetersPerPixel, view.Frame.Width * (options.Template == MapTemplate.SiteLocation ? .22 : .25));
            var bar = ScaleBar(meters, pixels, s, theme);
            var layer = VectorLayer(bar.Width, bar.Height, bar.Primitives, 0, 0);
            double denominator = Math.Max(1, Math.Round(view.ScaleDenominator(options.Dpi * k) / 10) * 10);
            var ratio = DocumentFeatures.CreateText(new TextSpec
            {
                Content = Loc.Format("축척 1:{0}", denominator.ToString("N0", CultureInfo.InvariantCulture)),
                FontFamily = Font("축척", false), FontSize = Math.Clamp(Math.Round(s * .015), 6, 1024), ColorArgb = theme.Muted
            });
            if (options.Template == MapTemplate.SiteLocation)
            {
                // In the band under the map, right-aligned.
                double right = width - s * .035, top = view.Frame.Bottom + s * .028;
                layer.X = Math.Round(right - bar.Width); layer.Y = Math.Round(top);
                ratio.X = Math.Round(right - ratio.Pixels.Width); ratio.Y = Math.Round(top + bar.Height + s * .004);
            }
            else
            {
                layer.X = Math.Round(view.Frame.Left + s * .03); layer.Y = Math.Round(view.Frame.Top + s * .03);
                ratio.X = layer.X; ratio.Y = Math.Round(layer.Y + bar.Height + s * .004);
            }
            Add(layer, MapClasses.ScaleBar, SiteParent());
            Add(ratio, MapClasses.ScaleText, SiteParent());
        }

        // Text block of the template.
        var textGroup = Group(MapClasses.TextGroup, root.Id);
        var coordinates = site is { } c && options.Template == MapTemplate.SiteLocation ? c : new GeoPoint(view.CenterLatitude, view.CenterLongitude);
        string coordinateText = string.Format(CultureInfo.InvariantCulture, "{0:0.0000}° {1} / {2:0.0000}° {3}",
            Math.Abs(coordinates.Latitude), coordinates.Latitude >= 0 ? "N" : "S", Math.Abs(coordinates.Longitude), coordinates.Longitude >= 0 ? "E" : "W");
        if (options.Template == MapTemplate.SiteLocation) SiteText(title, options, coordinateText, view.Frame, s, theme, (l, key) => Add(l, key, textGroup.Id));
        else PosterText(title, options, coordinateText, layout, width, height, k, s, theme, (l, key) => Add(l, key, textGroup.Id));

        // The attribution OpenStreetMap's licence requires: top-most, locked, legible on any theme.
        var attribution = DocumentFeatures.CreateText(new TextSpec
        {
            Content = Attribution, FontFamily = "Consolas", FontSize = Math.Clamp(Math.Round(Math.Max(11 * k, s * .0105)), 6, 1024), ColorArgb = theme.Muted,
            Outline = true, OutlineWidth = Math.Clamp(Math.Round(s * .002, 1), .5, 64), OutlineArgb = theme.Background
        });
        double inset = s * .018;
        var page = options.Template == MapTemplate.SiteLocation ? view.Frame : new Rect(0, 0, width, height);
        attribution.X = Math.Round(page.Right - inset - attribution.Pixels.Width); attribution.Y = Math.Round(page.Bottom - inset - attribution.Pixels.Height);
        attribution.Locked = true;
        Add(attribution, MapClasses.Attribution, root.Id);
        attribution.Name = Loc.T("데이터 출처") + " · " + Attribution;

        document.ActiveId = root.Id;
        token.ThrowIfCancellationRequested();
        document.Validate();
        return document;
    }

    static Rect Scale(Rect r, double k) => new(r.X * k, r.Y * k, r.Width * k, r.Height * k);

    // Map frame and text arrangement at the requested (full) size.
    static Layout Arrange(MapOptions o)
    {
        double w = o.Width, h = o.Height, s = Math.Min(w, h);
        if (o.Template == MapTemplate.SiteLocation)
        {
            double margin = Math.Round(s * .035), band = Math.Round(s * .14);
            return new(new Rect(margin, margin, w - margin * 2, h - margin - band), s, false);
        }
        if (!o.Frame) return new(new Rect(0, 0, w, h), s, true);
        double m = Math.Round(s * .055), bottom = Math.Round(h * .2);
        return new(new Rect(m, m, w - m * 2, h - m - bottom), s, false);
    }

    /// <summary>
    /// The view of the data in a frame: the given center and scale, or the data box filling the
    /// frame. A site map centers on the site when the data around it fills the frame well enough.
    /// </summary>
    internal static MapView View(MapData data, MapOptions o, Rect frame)
    {
        var bounds = data.Bounds;
        double CoverAt(double lat, double lon)
        {
            double east = WebMercator.Distance(lat, lon, lat, bounds.East), west = WebMercator.Distance(lat, lon, lat, bounds.West);
            double north = WebMercator.Distance(lat, lon, bounds.North, lon), south = WebMercator.Distance(lat, lon, bounds.South, lon);
            return Math.Min(2 * Math.Min(east, west) / frame.Width, 2 * Math.Min(north, south) / frame.Height);
        }
        var (bw, bh) = bounds.Meters;
        double cover = Math.Max(.01, Math.Min(bw / frame.Width, bh / frame.Height));
        double lat0 = bounds.CenterLatitude, lon0 = bounds.CenterLongitude;
        if (o.CenterLatitude is { } lat && o.CenterLongitude is { } lon) { lat0 = lat; lon0 = lon; }
        else if (o.Template == MapTemplate.SiteLocation && o.SiteLatitude is { } siteLat && o.SiteLongitude is { } siteLon && bounds.Contains(siteLat, siteLon))
        {
            double around = CoverAt(siteLat, siteLon);
            if (around >= cover * .35 || o.MetersPerPixel != null) { lat0 = siteLat; lon0 = siteLon; cover = Math.Max(.01, around); }
        }
        return new MapView(Math.Clamp(lat0, -WebMercator.MaxLatitude, WebMercator.MaxLatitude), lon0, o.MetersPerPixel ?? cover, frame);
    }

    // Lines grow a little when the map shows a small area (fewer metres per pixel) and with the page.
    internal static double LineFactor(double shortSide, double metersPerPixel) =>
        shortSide / 2000 * Math.Clamp(Math.Pow(2 / Math.Max(.01, metersPerPixel), .3), .55, 2.2);

    internal static double BaseWeight(string key) => key switch
    {
        MapClasses.Motorway => 4.6, MapClasses.Major => 3.4, MapClasses.Minor => 2.4, MapClasses.Local => 1.5,
        MapClasses.Service => 1.0, MapClasses.Path => .7, MapClasses.Rail => 1.3, MapClasses.River => 3.0, MapClasses.Stream => 1.0, _ => 1
    };

    /// <summary>A round scale bar length near <paramref name="target"/> pixels: 1, 2, 2.5 or 5 × 10ⁿ metres.</summary>
    public static (double Meters, double Pixels) ScaleBarLength(double metersPerPixel, double target)
    {
        double want = Math.Max(1e-6, target * metersPerPixel), unit = Math.Pow(10, Math.Floor(Math.Log10(want)));
        double meters = new[] { 5, 2.5, 2, 1 }.Select(f => f * unit).First(m => m <= want * 1.0001);
        return (meters, meters / metersPerPixel);
    }

    static string Distance(double meters) => meters >= 1000
        ? (meters / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " km"
        : meters.ToString("0.##", CultureInfo.InvariantCulture) + " m";

    // ---- geometry ----------------------------------------------------------------------------

    // Polylines in document pixels, thinned to points at least a third of a pixel apart, without
    // lines that miss the frame, in paths of at most FiguresPerPath figures.
    static List<Geometry> Lines(MapFeature[] features, MapView view, CancellationToken token)
    {
        var result = new List<Geometry>(); StreamGeometry? geometry = null; StreamGeometryContext? context = null; int figures = 0;
        var area = view.Frame; area.Inflate(view.Frame.Width * .02 + 8, view.Frame.Height * .02 + 8);
        var points = new List<Point>();
        void Flush() { if (context == null) return; context.Close(); geometry!.Freeze(); result.Add(geometry); context = null; geometry = null; figures = 0; }
        int visited = 0;
        foreach (var feature in features)
            foreach (var part in feature.Parts)
            {
                if ((++visited & 1023) == 0) token.ThrowIfCancellationRequested();
                Project(part, view, points, .34);
                if (points.Count < 2 || !Touches(points, area)) continue;
                if (context == null) { geometry = new StreamGeometry(); context = geometry.Open(); }
                context.BeginFigure(points[0], false, false); context.PolyLineTo(points.Skip(1).ToArray(), true, true);
                if (++figures >= FiguresPerPath) Flush();
            }
        Flush();
        return result;
    }

    // Areas as nonzero fills: rings are oriented by how deep they nest within their own feature
    // (outer rings one way, holes the other), so overlapping features of a class unite and holes stay.
    static List<Geometry> Areas(MapFeature[] features, MapView view, CancellationToken token)
    {
        var result = new List<Geometry>(); StreamGeometry? geometry = null; StreamGeometryContext? context = null; int figures = 0;
        var area = view.Frame; area.Inflate(8, 8);
        void Flush() { if (context == null) return; context.Close(); geometry!.Freeze(); result.Add(geometry); context = null; geometry = null; figures = 0; }
        int visited = 0;
        foreach (var feature in features)
        {
            if ((++visited & 255) == 0) token.ThrowIfCancellationRequested();
            var rings = new List<Point[]>();
            foreach (var part in feature.Parts)
            {
                var points = new List<Point>(); Project(part, view, points, .34);
                if (points.Count < 3 || !Touches(points, area)) continue;
                double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
                if (maxX - minX < .5 && maxY - minY < .5) continue;
                rings.Add(points.ToArray());
            }
            if (rings.Count == 0) continue;
            for (int i = 0; i < rings.Count; i++)
            {
                int depth = rings.Count == 1 ? 0 : Enumerable.Range(0, rings.Count).Count(j => j != i && Inside(rings[j], rings[i][0]));
                bool clockwise = SignedArea(rings[i]) > 0;
                if (clockwise != (depth % 2 == 0)) Array.Reverse(rings[i]);
                if (context == null) { geometry = new StreamGeometry { FillRule = FillRule.Nonzero }; context = geometry.Open(); }
                context.BeginFigure(rings[i][0], true, true); context.PolyLineTo(rings[i].Skip(1).ToArray(), true, true);
                figures++;
            }
            if (figures >= FiguresPerPath) Flush();
        }
        Flush();
        return result;
    }

    static void Project(GeoPoint[] part, MapView view, List<Point> output, double tolerance)
    {
        output.Clear();
        for (int i = 0; i < part.Length; i++)
        {
            var p = view.ToPixel(part[i].Latitude, part[i].Longitude);
            if (output.Count > 0 && i < part.Length - 1 && Math.Abs(p.X - output[^1].X) < tolerance && Math.Abs(p.Y - output[^1].Y) < tolerance) continue;
            output.Add(p);
        }
    }
    static bool Touches(List<Point> points, Rect area)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        return maxX >= area.Left && minX <= area.Right && maxY >= area.Top && minY <= area.Bottom;
    }
    // Screen coordinates (y down): positive is clockwise on screen.
    static double SignedArea(Point[] ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++) sum += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return sum / 2;
    }
    static bool Inside(Point[] ring, Point p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X) inside = !inside;
        return inside;
    }

    // A vector layer whose cache is drawn from the same retained paths.
    static Layer VectorLayer(int width, int height, IReadOnlyList<VectorPrimitive> primitives, double x, double y)
    {
        var vector = VectorContent.FromPaths(width, height, primitives);
        return new Layer { Kind = LayerKind.Vector, Vector = vector, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(vector.Drawing)), X = x, Y = y };
    }

    // ---- template parts ----------------------------------------------------------------------

    sealed record Part(int Width, int Height, VectorPrimitive[] Primitives, Point Anchor);

    static Part Marker(MapMarker kind, double s, MapTheme theme)
    {
        var accent = VectorShapes.Color(theme.Accent); var light = VectorShapes.Color(theme.Light);
        if (kind == MapMarker.Pin)
        {
            double r = Math.Max(6, s * .014), pad = r * .2, cx = r + pad, cy = r + pad, tipY = cy + r * 2.3;
            var pin = new StreamGeometry();
            using (var c = pin.Open())
            {
                // Tangents from the tip to the head circle, then the arc over the top.
                double d = tipY - cy, angle = Math.Asin(Math.Min(1, r / d));
                double tx = r * Math.Cos(angle), ty = r * Math.Sin(angle);
                c.BeginFigure(new Point(cx, tipY), true, true);
                c.LineTo(new Point(cx - tx, cy + ty), true, true);
                c.ArcTo(new Point(cx + tx, cy + ty), new Size(r, r), 0, true, SweepDirection.Clockwise, true, true);
            }
            pin.Freeze();
            var dot = new EllipseGeometry(new Point(cx, cy), r * .4, r * .4); dot.Freeze();
            int w = (int)Math.Ceiling(cx * 2), h = (int)Math.Ceiling(tipY + pad);
            return new(w, h, [new(pin, accent, true, 0), new(pin, light, false, Math.Max(1, r * .16)), new(dot, light, true, 0)], new Point(cx, tipY));
        }
        double radius = Math.Max(5, s * .011), ring = Math.Max(1.5, radius * .32), size = (radius + ring) * 2 + 2;
        var circle = new EllipseGeometry(new Point(size / 2, size / 2), radius, radius); circle.Freeze();
        int side = (int)Math.Ceiling(size);
        return new(side, side, [new(circle, accent, true, 0), new(circle, light, false, ring)], new Point(size / 2, size / 2));
    }

    static Part NorthArrow(double s, MapTheme theme)
    {
        var ink = VectorShapes.Color(theme.Text); var light = VectorShapes.Color(theme.Light);
        double r = Math.Max(14, s * .032), line = Math.Max(1.2, s * .0012);
        var letter = Glyphs("N", r * .62, true);
        double top = letter.Bounds.Height + r * .25, cx = r + line * 2, cy = top + r + line;
        var face = new EllipseGeometry(new Point(cx, cy), r, r); face.Freeze();
        Geometry Half(bool left)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx, cy - r * .8), true, true);
                c.LineTo(new Point(cx + (left ? -1 : 1) * r * .38, cy + r * .55), true, true);
                c.LineTo(new Point(cx, cy + r * .25), true, true);
            }
            g.Freeze(); return g;
        }
        var shift = new TranslateTransform(cx - letter.Bounds.Width / 2 - letter.Bounds.Left, line - letter.Bounds.Top);
        var placed = letter.Clone(); placed.Transform = shift; placed.Freeze();
        int w = (int)Math.Ceiling(cx * 2), h = (int)Math.Ceiling(cy + r + line * 2);
        return new(w, h,
        [
            new(face, light, true, 0), new(face, ink, false, line),
            new(Half(true), ink, true, 0), new(Half(false), light, true, 0), new(Half(false), ink, false, line * .8),
            new(placed, ink, true, 0)
        ], new Point(cx, cy));
    }

    // Four alternating segments with 0, the middle and the full length written above the bar.
    static Part ScaleBar(double meters, double pixels, double s, MapTheme theme)
    {
        var ink = VectorShapes.Color(theme.Text); var light = VectorShapes.Color(theme.Light);
        double size = Math.Max(8, s * .014), bar = Math.Max(4, s * .007), line = Math.Max(1, s * .0009);
        bool km = meters >= 1000; double unit = km ? 1000 : 1;
        string Value(double m) => (m / unit).ToString("0.##", CultureInfo.InvariantCulture);
        var labels = new[] { (0d, Value(0)), (pixels / 2, Value(meters / 2)), (pixels, Value(meters) + (km ? " km" : " m")) }
            .Select(p => (p.Item1, Glyph: Glyphs(p.Item2, size, false))).ToArray();
        double left = Math.Max(line, labels[0].Glyph.Bounds.Width / 2) + line, textHeight = labels.Max(l => l.Glyph.Bounds.Height);
        double right = Math.Max(labels[^1].Glyph.Bounds.Width - labels[^1].Glyph.Bounds.Width / 2, 0);
        double top = textHeight + size * .35, y = top;
        var primitives = new List<VectorPrimitive>();
        var outline = new RectangleGeometry(new Rect(left, y, pixels, bar)); outline.Freeze();
        primitives.Add(new(outline, light, true, 0));
        for (int i = 0; i < 4; i += 2)
        {
            var segment = new RectangleGeometry(new Rect(left + pixels * i / 4, y, pixels / 4, bar)); segment.Freeze();
            primitives.Add(new(segment, ink, true, 0));
        }
        primitives.Add(new(outline, ink, false, line));
        foreach (var (x, glyph) in labels)
        {
            var placed = glyph.Clone(); placed.Transform = new TranslateTransform(left + x - glyph.Bounds.Width / 2 - glyph.Bounds.Left, top - size * .2 - glyph.Bounds.Bottom); placed.Freeze();
            primitives.Add(new(placed, ink, true, 0));
        }
        int w = (int)Math.Ceiling(left + pixels + right + line * 2), h = (int)Math.Ceiling(y + bar + line * 2);
        return new(w, h, primitives.ToArray(), new Point(left, y));
    }

    static Geometry Glyphs(string text, double size, bool bold)
    {
        var face = new Typeface(new FontFamily(Font(text, false)), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(1, size), Brushes.Black, 1);
        var geometry = formatted.BuildGeometry(new Point()); geometry.Freeze(); return geometry;
    }

    // The page colour rising toward the frame edges, and behind the title of a full-page poster.
    static Layer FadeLayer(int width, int height, Rect frame, double s, bool overlay, uint color)
    {
        var raster = new Raster(width, height); var c = VectorShapes.Color(color);
        double edge = s * (overlay ? .05 : .03), from = frame.Top + frame.Height * .6, to = frame.Top + frame.Height * .84;
        int left = (int)Math.Max(0, Math.Floor(frame.Left)), right = (int)Math.Min(width, Math.Ceiling(frame.Right));
        int top = (int)Math.Max(0, Math.Floor(frame.Top)), bottom = (int)Math.Min(height, Math.Ceiling(frame.Bottom));
        Parallel.For(top, bottom, y =>
        {
            double py = y + .5;
            double bottomFade = overlay ? Smooth((py - from) / (to - from)) * .92 : 0;
            for (int x = left; x < right; x++)
            {
                double px = x + .5, d = Math.Min(Math.Min(px - frame.Left, frame.Right - px), Math.Min(py - frame.Top, frame.Bottom - py));
                double edgeFade = d < edge ? Math.Pow(1 - Math.Max(0, d) / edge, 2) : 0;
                double a = Math.Max(edgeFade, bottomFade);
                if (a <= 0) continue;
                int i = (y * width + x) * 4;
                raster.Data[i] = c.B; raster.Data[i + 1] = c.G; raster.Data[i + 2] = c.R; raster.Data[i + 3] = Imaging.Byte(a * 255);
            }
        });
        return new Layer { Kind = LayerKind.Raster, Pixels = raster };
    }
    static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    static readonly Regex Hangul = new("[\\u1100-\\u11FF\\u3130-\\u318F\\uAC00-\\uD7A3]", RegexOptions.CultureInvariant);
    static readonly Lazy<HashSet<string>> Installed = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase));
    // Hangul uses 맑은 고딕; Latin titles a wide geometric sans when this PC has one.
    internal static string Font(string text, bool title)
    {
        if (Hangul.IsMatch(text)) return "Malgun Gothic";
        foreach (var name in title ? new[] { "Bahnschrift", "Segoe UI" } : new[] { "Segoe UI" })
            if (Installed.Value.Contains(name)) return name;
        return "Segoe UI";
    }

    static void PosterText(string title, MapOptions o, string coordinateText, Layout layout, int width, int height, double k, double s, MapTheme theme, Action<Layer, string> add)
    {
        bool hangul = Hangul.IsMatch(title);
        var layers = new List<(Layer Layer, string Key, double Gap)>();
        double titleSize = s * .072;
        var spec = new TextSpec { Content = title.ToUpper(CultureInfo.InvariantCulture), FontFamily = Font(title, true), FontSize = Math.Clamp(Math.Round(titleSize), 6, 1024), Bold = true, ColorArgb = theme.Text, Tracking = hangul ? 320 : 640 };
        var titleLayer = DocumentFeatures.CreateText(spec);
        // Long titles shrink to fit the page with a margin.
        double limit = width * .84;
        if (titleLayer.Pixels.Width > limit)
        {
            spec = spec with { FontSize = Math.Clamp(Math.Floor(spec.FontSize * limit / titleLayer.Pixels.Width), 6, 1024) };
            titleLayer = DocumentFeatures.CreateText(spec);
        }
        layers.Add((titleLayer, MapClasses.Title, 0));
        int ruleWidth = (int)Math.Round(Math.Max(s * .2, Math.Min(titleLayer.Pixels.Width * .42, width * .5)));
        var rule = VectorShapes.Create(new ShapeSpec { Width = Math.Max(1, ruleWidth), Height = Math.Max(1, (int)Math.Round(Math.Max(1.5 * k, s * .0012))), FillArgb = theme.Text });
        layers.Add((rule, MapClasses.Rule, s * .006));
        if (!string.IsNullOrWhiteSpace(o.Subtitle))
        {
            string subtitle = o.Subtitle.Trim();
            var layer = DocumentFeatures.CreateText(new TextSpec { Content = subtitle.ToUpper(CultureInfo.InvariantCulture), FontFamily = Font(subtitle, false), FontSize = Math.Clamp(Math.Round(s * .026), 6, 1024), ColorArgb = theme.Muted, Tracking = Hangul.IsMatch(subtitle) ? 120 : 160 });
            layers.Add((layer, MapClasses.Subtitle, s * .014));
        }
        if (o.Coordinates)
            layers.Add((DocumentFeatures.CreateText(new TextSpec { Content = coordinateText, FontFamily = "Consolas", FontSize = Math.Clamp(Math.Round(s * .017), 6, 1024), ColorArgb = theme.Muted, Tracking = 40 }), MapClasses.Coordinates, s * .012));
        double total = layers.Sum(l => l.Layer.Pixels.Height + l.Gap);
        double y = layout.Overlay ? height - s * .085 - total : layout.Frame.Bottom + (height - layout.Frame.Bottom - total) / 2;
        foreach (var (layer, key, gap) in layers)
        {
            y += gap; layer.X = Math.Round((width - layer.Pixels.Width) / 2.0); layer.Y = Math.Round(y);
            y += layer.Pixels.Height; add(layer, key);
        }
    }

    static void SiteText(string title, MapOptions o, string coordinateText, Rect frame, double s, MapTheme theme, Action<Layer, string> add)
    {
        double x = frame.Left, y = frame.Bottom + s * .022;
        var heading = DocumentFeatures.CreateText(new TextSpec { Content = title, FontFamily = Font(title, false), FontSize = Math.Clamp(Math.Round(s * .03), 6, 1024), Bold = true, ColorArgb = theme.Text, Tracking = 60 });
        heading.X = Math.Round(x); heading.Y = Math.Round(y); add(heading, MapClasses.Title); y += heading.Pixels.Height;
        if (!string.IsNullOrWhiteSpace(o.Subtitle))
        {
            var subtitle = DocumentFeatures.CreateText(new TextSpec { Content = o.Subtitle.Trim(), FontFamily = Font(o.Subtitle, false), FontSize = Math.Clamp(Math.Round(s * .018), 6, 1024), ColorArgb = theme.Muted });
            subtitle.X = Math.Round(x); subtitle.Y = Math.Round(y); add(subtitle, MapClasses.Subtitle); y += subtitle.Pixels.Height;
        }
        if (o.Coordinates)
        {
            var coordinates = DocumentFeatures.CreateText(new TextSpec { Content = coordinateText, FontFamily = "Consolas", FontSize = Math.Clamp(Math.Round(s * .014), 6, 1024), ColorArgb = theme.Muted, Tracking = 20 });
            coordinates.X = Math.Round(x); coordinates.Y = Math.Round(y); add(coordinates, MapClasses.Coordinates);
        }
    }
}
