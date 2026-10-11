using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

// Maps from OpenStreetMap data: parsing (.osm and GeoJSON), multipolygons, coastlines, limits,
// Web Mercator scale, the composer's classes, weights and templates, the attribution, themes,
// project round trip, vector export and the consent-gated online source over a fake HTTP layer.
// Nothing here reaches the network.
public static class MapTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    sealed class Recorder : IProgress<double> { public readonly List<double> Values = []; public void Report(double value) { lock (Values) Values.Add(value); } }

    internal static MapData Town() => MapFixtures.Read(MapFixtures.Osm());

    internal static Layer Class(Document doc, string key) => doc.Layers.Single(l => l.Map?.Role == key);
    internal static VectorContent.Scene Scene(Layer layer)
    {
        using var input = layer.Vector!.Open();
        return JsonSerializer.Deserialize<VectorContent.Scene>(input)!;
    }
    static Rect Bounds(VectorContent.PathData path)
    {
        var g = Geometry.Parse(path.Data).CloneCurrentValue(); var m = path.Matrix;
        g.Transform = new MatrixTransform(m[0], m[1], m[2], m[3], m[4], m[5]); return g.Bounds;
    }
    static byte Alpha(Raster r, Point p) => r.Data[((int)p.Y * r.Width + (int)p.X) * 4 + 3];

    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "maps"); Directory.CreateDirectory(root);

        test("map data: .osm export is classified into roads, rail, water, green and buildings", () =>
        {
            var data = Town(); var counts = data.Counts;
            Check(data.BoundsFromFile && data.Bounds == MapFixtures.Bounds, "The file's bounds were not kept");
            Check(counts.GetValueOrDefault(MapClasses.Motorway) == 1 && counts.GetValueOrDefault(MapClasses.Major) == 2 && counts.GetValueOrDefault(MapClasses.Minor) == 1
                && counts.GetValueOrDefault(MapClasses.Local) == 10 && counts.GetValueOrDefault(MapClasses.Service) == 1 && counts.GetValueOrDefault(MapClasses.Path) == 1,
                "Road classes: " + string.Join(", ", counts.Select(p => $"{p.Key}={p.Value}")));
            Check(counts.GetValueOrDefault(MapClasses.Rail) == 1, "The subway tunnel was drawn or the rail line was lost");
            Check(counts.GetValueOrDefault(MapClasses.Building) == MapFixtures.Buildings, "building=no was drawn or buildings were lost");
            Check(counts.GetValueOrDefault(MapClasses.WaterArea) == 1 && counts.GetValueOrDefault(MapClasses.River) == 1 && counts.GetValueOrDefault(MapClasses.Stream) == 1, "Water was not classified");
            Check(counts.GetValueOrDefault(MapClasses.Park) == 1 && counts.GetValueOrDefault(MapClasses.Forest) == 2 && counts.GetValueOrDefault(MapClasses.Grass) == 1, "Green was not classified");
            Check(!counts.ContainsKey(MapClasses.Coastline) && data.Features.All(f => f.Class != "power"), "Unknown ways were drawn");
            Check(data.Warnings.Any(w => w.Contains("받은 부분만")), "The incomplete relation was not reported");
        });

        test("map data: a multipolygon joins two outer ways around an inner island", () =>
        {
            var river = Town().Features.Single(f => f.Class == MapClasses.WaterArea);
            Check(river.Parts.Length == 2 && river.Parts.All(r => r.Length >= 4 && r[0] == r[^1]), "The river is not one closed outer ring and one island");
            var island = MapFixtures.At(MapFixtures.Bounds, MapFixtures.Island.U, MapFixtures.Island.V); var water = MapFixtures.At(MapFixtures.Bounds, MapFixtures.Water.U, MapFixtures.Water.V);
            var outer = river.Parts.OrderByDescending(r => r.Length).First(r => MapImport.Contains(r, water));
            var inner = river.Parts.Single(r => !ReferenceEquals(r, outer));
            Check(MapImport.Contains(outer, island) && MapImport.Contains(inner, island) && !MapImport.Contains(inner, water), "The island is not inside the river");
            var rings = MapImport.JoinRings([[1, 2, 3], [3, 4, 1], [7, 8, 9]], out var open);
            Check(rings.Count == 1 && rings[0].SequenceEqual(new long[] { 1, 2, 3, 4, 1 }) && open.Count == 1, "Ring joining failed");
            var closed = MapImport.CloseChains([[new(0, 0), new(0, 1), new(1, 1)], [new(1, 1.1), new(1, 0)]]);
            Check(closed.Count == 1 && closed[0][0] == closed[0][^1] && closed[0].Length == 6, "Open chains were not joined and closed");
        });

        test("map data: coastline makes the sea to its right and keeps islands as land", () =>
        {
            var box = new MapBounds(37.40, 126.50, 37.44, 126.56);
            var data = MapFixtures.Read(MapFixtures.Coast(box));
            var sea = data.Features.Single(f => f.Class == MapClasses.Sea);
            bool Sea(double u, double v) => sea.Parts.Count(r => MapImport.Contains(r, MapFixtures.At(box, u, v))) % 2 == 1;
            Check(Sea(.5, .35) && Sea(.1, .1) && Sea(.9, .05), "Water south of the coastline is missing");
            Check(!Sea(.5, .8) && !Sea(.1, .9), "Land north of the coastline became sea");
            Check(!Sea(.5, .22), "The island became sea");
            var doc = MapComposer.Build(data, new MapOptions { Width = 400, Height = 400, Fade = false });
            var tag = doc.Layers.First(l => l.Map is { IsRoot: true }).Map!; var layer = Class(doc, MapClasses.Sea);
            var south = tag.View.ToPixel(MapFixtures.At(box, .5, .35).Latitude, MapFixtures.At(box, .5, .35).Longitude);
            var north = tag.View.ToPixel(MapFixtures.At(box, .5, .8).Latitude, MapFixtures.At(box, .5, .8).Longitude);
            Check(Alpha(layer.Pixels, south) == 255 && Alpha(layer.Pixels, north) == 0, "The sea layer is not drawn on the right side");
        });

        test("map data: GeoJSON with OpenStreetMap tags (direct or under tags) is read", () =>
        {
            string json = """
            { "type": "FeatureCollection", "bbox": [126.97, 37.56, 126.99, 37.57],
              "features": [
                { "type": "Feature", "properties": { "highway": "primary", "@id": "way/1" }, "geometry": { "type": "LineString", "coordinates": [[126.971, 37.561], [126.989, 37.569]] } },
                { "type": "Feature", "properties": { "tags": { "building": "yes" } }, "geometry": { "type": "Polygon", "coordinates": [[[126.975, 37.565], [126.976, 37.565], [126.976, 37.566], [126.975, 37.565]]] } },
                { "type": "Feature", "properties": { "natural": "water" }, "geometry": { "type": "MultiPolygon", "coordinates": [[[[126.98, 37.562], [126.985, 37.562], [126.985, 37.566], [126.98, 37.566], [126.98, 37.562]], [[126.982, 37.563], [126.983, 37.563], [126.983, 37.564], [126.982, 37.563]]]] } },
                { "type": "Feature", "properties": { "amenity": "bench" }, "geometry": { "type": "Point", "coordinates": [126.98, 37.565] } }
              ] }
            """;
            string file = Path.Combine(root, "town.geojson"); File.WriteAllText(file, json);
            var data = MapImport.Read(file);
            Check(data.BoundsFromFile && Math.Abs(data.Bounds.North - 37.57) < 1e-9, "The GeoJSON bbox was not used");
            Check(data.Counts.GetValueOrDefault(MapClasses.Major) == 1 && data.Counts.GetValueOrDefault(MapClasses.Building) == 1 && data.Counts.GetValueOrDefault(MapClasses.WaterArea) == 1, "GeoJSON classes: " + string.Join(", ", data.Counts.Keys));
            Check(data.Features.Single(f => f.Class == MapClasses.WaterArea).Parts.Length == 2, "The GeoJSON hole was lost");
            bool rejected = false; try { MapImport.ReadGeoJson(new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"Point\",\"coordinates\":[1,2]}"))); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A bare geometry without tags was accepted");
        });

        test("map data: non-OSM files, DTDs and limits are refused; reading reports progress and cancels", () =>
        {
            bool Refused(string xml, Type kind) { try { MapFixtures.Read(xml); return false; } catch (Exception e) { return kind.IsInstanceOfType(e); } }
            Check(Refused("<?xml version=\"1.0\"?><svg/>", typeof(InvalidDataException)), "Another XML root was accepted");
            Check(Refused("<?xml version=\"1.0\"?><!DOCTYPE osm [<!ENTITY x \"y\">]><osm version=\"0.6\"/>", typeof(System.Xml.XmlException)), "A DTD was processed");
            Check(Refused("<osm version=\"0.6\"><node id=\"1\" lat=\"95\" lon=\"0\"/></osm>", typeof(InvalidDataException)), "An impossible latitude was accepted");
            Check(Refused("<osm version=\"0.6\"><node id=\"1\" lat=\"1\" lon=\"1\"/></osm>", typeof(InvalidDataException)), "A file without drawable ways was accepted");
            string big = Path.Combine(root, "big.osm"); File.WriteAllText(big, MapFixtures.Osm(extraNodes: 200_000));
            var progress = new Recorder(); var data = MapImport.Read(big, progress);
            Check(progress.Values.Count >= 2 && progress.Values[^1] == 1 && data.Counts.GetValueOrDefault(MapClasses.Building) == MapFixtures.Buildings, "Progress was not reported");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            bool cancelled = false; try { MapImport.Read(big, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Reading did not stop when cancelled");
            string empty = Path.Combine(root, "empty.osm"); File.WriteAllText(empty, "");
            bool tooSmall = false; try { MapImport.Read(empty); } catch (InvalidDataException) { tooSmall = true; }
            Check(tooSmall && MapImport.Supports("a.OSM") && MapImport.Supports("b.geojson") && !MapImport.Supports("c.dxf"), "File checks are wrong");
        });

        test("map projection: Web Mercator keeps known ground distances at the stated scale", () =>
        {
            Check(Math.Abs(WebMercator.Distance(37, 127, 38, 127) - 111_195) < 50, "One degree of latitude is not about 111.2 km");
            var view = new MapView(37.5665, 126.978, 2, new Rect(100, 50, 1000, 800));
            var center = view.ToPixel(37.5665, 126.978);
            Check(Math.Abs(center.X - 600) < 1e-6 && Math.Abs(center.Y - 450) < 1e-6, "The center is not in the middle of the frame");
            foreach (double bearing in new[] { 0d, 90, 180, 270, 45 })
            {
                var (lat, lon) = WebMercator.Offset(37.5665, 126.978, 1000, bearing); var p = view.ToPixel(lat, lon);
                double pixels = (p - center).Length;
                Check(Math.Abs(pixels - 500) < 1.5, $"1 km at {bearing}° is {pixels:0.##} px instead of 500 px");
                var (backLat, backLon) = view.ToGeo(p);
                Check(Math.Abs(backLat - lat) < 1e-9 && Math.Abs(backLon - lon) < 1e-9, "Pixel to latitude/longitude does not round-trip");
            }
            var north = view.ToPixel(37.5765, 126.978); Check(north.Y < center.Y, "North is not up");
            Check(Math.Abs(view.ScaleDenominator(150) - 2 * 150 / .0254) < 1e-6, "The paper scale is wrong");
            var (meters, length) = MapComposer.ScaleBarLength(2.37, 300);
            Check(meters == 500 && Math.Abs(length - 500 / 2.37) < 1e-9, $"Scale bar {meters} m for 300 px at 2.37 m/px");
            Check(MapComposer.ScaleBarLength(.8, 300).Meters == 200 && MapComposer.ScaleBarLength(12, 300).Meters == 2500, "Scale bar lengths are not round numbers");
        });

        test("map composer: a poster has class layers in groups, weighted roads, the title and the attribution on top", () =>
        {
            var options = new MapOptions { Title = "Seoul", Subtitle = "South Korea", Width = 900, Height = 1200 };
            var doc = MapComposer.Build(Town(), options);
            var root = doc.Layers.Single(l => l.Map is { IsRoot: true }); var tag = root.Map!; var theme = MapThemes.Get(tag.Theme);
            Check(root.Kind == LayerKind.Group && root.Category == LayerCategory.Drawing && tag.Template == MapTemplate.Poster && tag.MetersPerPixel > 0, "The map folder is missing its tag");
            var groups = doc.Layers.Where(l => l.ParentId == root.Id).Select(l => l.Map!.Role).ToArray();
            Check(groups.SequenceEqual([MapClasses.Background, MapClasses.GreenGroup, MapClasses.WaterGroup, MapClasses.Building, MapClasses.RoadGroup, MapClasses.Rail, MapClasses.Fade, MapClasses.TextGroup, MapClasses.Attribution]),
                "Paint order: " + string.Join(", ", groups));
            var roads = doc.Layers.Where(l => l.ParentId == Class(doc, MapClasses.RoadGroup).Id).Select(l => l.Map!.Role).ToArray();
            Check(roads.SequenceEqual(MapClasses.Roads), "Roads are not painted small to large: " + string.Join(", ", roads));
            double Width(string key) => Scene(Class(doc, key)).Items.Max(i => i.StrokeWidth);
            Check(Width(MapClasses.Motorway) > Width(MapClasses.Major) && Width(MapClasses.Major) > Width(MapClasses.Minor) && Width(MapClasses.Minor) > Width(MapClasses.Local)
                && Width(MapClasses.Local) > Width(MapClasses.Service) && Width(MapClasses.Service) > Width(MapClasses.Path), "Road weights do not follow the hierarchy");
            foreach (var key in MapClasses.Roads.Append(MapClasses.Rail).Append(MapClasses.WaterArea).Append(MapClasses.Park))
            {
                var layer = Class(doc, key);
                Check(layer.Kind == LayerKind.Vector && layer.Vector!.Width == doc.Width && Scene(layer).Items.All(i => i.Color == theme.Color(key) && i.Clip >= 0) && Scene(layer).Items.All(i => i.Fill == MapClasses.IsArea(key)),
                    $"{key} is not a clipped vector layer in the theme colour");
            }
            var title = Class(doc, MapClasses.Title);
            Check(title.Text!.Content == "SEOUL" && title.Text.Tracking >= 500 && title.Text.Bold && Math.Abs(title.X + title.Pixels.Width / 2.0 - doc.Width / 2.0) <= 1, "The title is not a centered, widely tracked upper-case line");
            Check(Class(doc, MapClasses.Subtitle).Text!.Content == "SOUTH KOREA" && Class(doc, MapClasses.Coordinates).Text!.Content.Contains("° N /") && Class(doc, MapClasses.Rule).Kind == LayerKind.Shape, "Subtitle, rule or coordinates are missing");
            var attribution = Class(doc, MapClasses.Attribution);
            Check(ReferenceEquals(doc.Layers[^1], attribution) && attribution.Locked && attribution.Visible && attribution.Text!.Content == "© OpenStreetMap contributors"
                && attribution.X >= 0 && attribution.X + attribution.Pixels.Width <= doc.Width && attribution.Y + attribution.Pixels.Height <= doc.Height, "The attribution is not the visible, locked top layer on the page");
            Check(Class(doc, MapClasses.Fade).Kind == LayerKind.Raster && Class(doc, MapClasses.Fade).Pixels.Data[3] > 0, "The edge fade is missing");
            var island = MapFixtures.At(MapFixtures.Bounds, MapFixtures.Island.U, MapFixtures.Island.V); var water = MapFixtures.At(MapFixtures.Bounds, MapFixtures.Water.U, MapFixtures.Water.V);
            var river = Class(doc, MapClasses.WaterArea).Pixels;
            Check(Alpha(river, tag.View.ToPixel(island.Latitude, island.Longitude)) == 0 && Alpha(river, tag.View.ToPixel(water.Latitude, water.Longitude)) == 255, "The river's island is not a hole");
            var trimmed = MapComposer.Build(Town(), options with { Buildings = false, Paths = false, Rail = false, Green = false, Fade = false });
            Check(!trimmed.Layers.Any(l => l.Map?.Role is MapClasses.Building or MapClasses.Path or MapClasses.Rail or MapClasses.Park or MapClasses.Fade or MapClasses.GreenGroup), "Turned-off classes were drawn");
            var framed = MapComposer.Build(Town(), options with { Frame = true });
            var frameTag = framed.Layers.Single(l => l.Map is { IsRoot: true }).Map!;
            Check(frameTag.FrameX > 0 && frameTag.FrameY > 0 && frameTag.Frame.Bottom < framed.Height * .85 && Class(framed, MapClasses.Title).Y > frameTag.Frame.Bottom && framed.Layers.Any(l => l.Map?.Role == MapClasses.Frame), "The framed poster has no margins or its title is on the map");
            var korean = MapComposer.Build(Town(), options with { Title = "서울" });
            Check(Class(korean, MapClasses.Title).Text!.FontFamily == "Malgun Gothic", "A Korean title does not use a Hangul font");
        });

        test("map composer: a site map has the marker on the site, true-scale rings, a north arrow and a scale bar", () =>
        {
            var site = MapFixtures.At(MapFixtures.Bounds, .3, .3);
            var options = MapOptions.For(MapTemplate.SiteLocation) with { Width = 1200, Height = 900, SiteLatitude = site.Latitude, SiteLongitude = site.Longitude, Rings = [200, 400], Subtitle = "서울시청 일대" };
            var doc = MapComposer.Build(Town(), options);
            var tag = doc.Layers.Single(l => l.Map is { IsRoot: true }).Map!; var at = tag.View.ToPixel(site.Latitude, site.Longitude);
            Check(tag.Template == MapTemplate.SiteLocation && tag.SiteLatitude == site.Latitude && tag.Frame.Contains(at), "The site map is not centred on its site");
            var marker = Class(doc, MapClasses.Marker);
            Check(Math.Abs(marker.X + marker.Pixels.Width / 2.0 - at.X) <= 1.5 && Math.Abs(marker.Y + marker.Pixels.Height / 2.0 - at.Y) <= 1.5, "The marker is not on the site");
            var rings = Scene(Class(doc, MapClasses.Rings)).Items.Select(i => Bounds(i.Path).Width / 2 * tag.MetersPerPixel).OrderBy(r => r).ToArray();
            Check(rings.Length == 2 && Math.Abs(rings[0] - 200) < 1 && Math.Abs(rings[1] - 400) < 1.5, "Ring radii are not true to scale: " + string.Join(", ", rings));
            var bar = Scene(Class(doc, MapClasses.ScaleBar)); double barMeters = Bounds(bar.Items[0].Path).Width * tag.MetersPerPixel;
            var expected = MapComposer.ScaleBarLength(tag.MetersPerPixel, tag.FrameWidth * .22).Meters;
            Check(Math.Abs(barMeters - expected) < expected * .002, $"The scale bar shows {barMeters:0.##} m, expected {expected} m");
            double denominator = Math.Round(tag.View.ScaleDenominator(doc.Dpi) / 10) * 10;
            Check(Class(doc, MapClasses.ScaleText).Text!.Content == "축척 1:" + denominator.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), "The scale text is wrong");
            var arrow = Class(doc, MapClasses.NorthArrow);
            Check(arrow.X + arrow.Pixels.Width <= tag.Frame.Right && arrow.Y >= tag.Frame.Top && Scene(arrow).Items.Length >= 5, "The north arrow is missing or outside the map");
            Check(Class(doc, MapClasses.Title).Text!.Content == "대지 위치도" && Class(doc, MapClasses.Subtitle).Text!.Content == "서울시청 일대" && Class(doc, MapClasses.SiteLabel).Text!.Content == "계획 대지", "Site texts are missing");
            Check(doc.Layers[^1].Map?.Role == MapClasses.Attribution && tag.Frame.Contains(new Rect(doc.Layers[^1].X, doc.Layers[^1].Y, doc.Layers[^1].Pixels.Width, doc.Layers[^1].Pixels.Height)), "The attribution is not on the map");
            var scaled = MapComposer.Build(Town(), options with { MetersPerPixel = 5000 * .0254 / 150 });
            var scaledTag = scaled.Layers.Single(l => l.Map is { IsRoot: true }).Map!;
            Check(Math.Abs(scaledTag.View.ScaleDenominator(150) - 5000) < 1e-6 && Class(scaled, MapClasses.ScaleText).Text!.Content == "축척 1:5,000", "A chosen 1:5,000 scale was not kept");
            var pin = MapComposer.Build(Town(), options with { Marker = MapMarker.Pin, NorthArrow = false, ScaleBar = false, Rings = [] });
            var pinLayer = Class(pin, MapClasses.Marker); var pinTag = pin.Layers.Single(l => l.Map is { IsRoot: true }).Map!; var tip = pinTag.View.ToPixel(site.Latitude, site.Longitude);
            Check(Math.Abs(pinLayer.X + pinLayer.Pixels.Width / 2.0 - tip.X) <= 1.5 && Math.Abs(pinLayer.Y + pinLayer.Pixels.Height - tip.Y) <= 4, "The pin's tip is not on the site");
            Check(!pin.Layers.Any(l => l.Map?.Role is MapClasses.NorthArrow or MapClasses.ScaleBar or MapClasses.Rings), "Turned-off site parts were drawn");
        });

        test("map composer: the preview is the same map drawn smaller", () =>
        {
            var options = new MapOptions { Width = 1200, Height = 1600, Title = "Test" };
            var full = MapComposer.Build(Town(), options); var small = MapComposer.Build(Town(), options, default, .5);
            var a = full.Layers.Single(l => l.Map is { IsRoot: true }).Map!; var b = small.Layers.Single(l => l.Map is { IsRoot: true }).Map!;
            Check(small.Width == 600 && small.Height == 800 && Math.Abs(b.MetersPerPixel - a.MetersPerPixel * 2) < 1e-9 && Math.Abs(b.FrameWidth - a.FrameWidth / 2) < 1e-9, "The preview scale is wrong");
            Check(small.Layers.Select(l => l.Map?.Role).SequenceEqual(full.Layers.Select(l => l.Map?.Role)), "The preview has other layers");
            double ratio = Scene(Class(small, MapClasses.Motorway)).Items[0].StrokeWidth / Scene(Class(full, MapClasses.Motorway)).Items[0].StrokeWidth;
            Check(Math.Abs(ratio - .5) < 1e-6, $"Preview line weights are not half ({ratio})");
            bool refused = false; try { MapComposer.Build(Town(), options with { Width = 50 }); } catch (InvalidDataException) { refused = true; }
            Check(refused, "A tiny map was accepted");
        });

        test("map styling: a theme recolours every class and keeps geometry; a group takes one colour", () =>
        {
            var doc = MapComposer.Build(Town(), new MapOptions { Width = 900, Height = 1200, Title = "Seoul" });
            var root = doc.Layers.Single(l => l.Map is { IsRoot: true }); var before = Scene(Class(doc, MapClasses.Major));
            int changed = MapStyling.ApplyTheme(doc, root.Id, "dark");
            var dark = MapThemes.Get("dark");
            Check(changed > 10 && root.Map!.Theme == "dark", "The theme was not applied");
            var after = Scene(Class(doc, MapClasses.Major));
            Check(after.Items.All(i => i.Color == dark.Major) && after.Items.Select(i => i.Path.Data + string.Join(',', i.Path.Matrix)).SequenceEqual(before.Items.Select(i => i.Path.Data + string.Join(',', i.Path.Matrix))) && after.Items.Select(i => i.StrokeWidth).SequenceEqual(before.Items.Select(i => i.StrokeWidth)),
                "Roads did not take the theme colour or their geometry changed");
            Check(Class(doc, MapClasses.Background).Shape!.FillArgb == dark.Background && Class(doc, MapClasses.Title).Text!.ColorArgb == dark.Text && Class(doc, MapClasses.Attribution).Text!.OutlineArgb == dark.Background, "Page, title or attribution were not recoloured");
            var fade = Class(doc, MapClasses.Fade).Pixels; int i = Array.FindIndex(Enumerable.Range(0, fade.Data.Length / 4).ToArray(), k => fade.Data[k * 4 + 3] > 0) * 4;
            Check(VectorShapes.Argb(Color.FromRgb(fade.Data[i + 2], fade.Data[i + 1], fade.Data[i])) == dark.Background, "The edge fade kept the old page colour");
            var rendered = Class(doc, MapClasses.Major).Pixels; Check(rendered.Data.Where((_, k) => k % 4 == 3).Any(a => a > 0), "The road cache was not redrawn");
            int recoloured = MapStyling.Recolor(doc, Class(doc, MapClasses.RoadGroup).Id, Colors.Red);
            Check(recoloured == MapClasses.Roads.Length && MapClasses.Roads.All(k => Scene(Class(doc, k)).Items.All(item => item.Color == 0xFFFF0000)), "The road group did not take one colour");
            Check(Scene(Class(doc, MapClasses.Rail)).Items.All(item => item.Color == dark.Rail), "Recolouring a group changed another class");
            var site = MapComposer.Build(Town(), MapOptions.For(MapTemplate.SiteLocation) with { Width = 1200, Height = 900 });
            var marker = Class(site, MapClasses.Marker); MapStyling.Recolor(site, marker.Id, Colors.Blue);
            var light = MapThemes.Get(site.Layers.Single(l => l.Map is { IsRoot: true }).Map!.Theme).Light;
            Check(Scene(marker).Items.Any(item => item.Color == 0xFF0000FF) && Scene(marker).Items.Any(item => (item.Color & 0xFFFFFF) == (light & 0xFFFFFF)), "The marker lost its light outline when recoloured");
        });

        test("map project: map tags, classes and vectors survive saving and opening", () =>
        {
            var doc = MapComposer.Build(Town(), MapOptions.For(MapTemplate.SiteLocation) with { Width = 1000, Height = 800, Subtitle = "왕복 검사" });
            string file = Path.Combine(root, "site.moruproj"); ProjectStore.Save(doc, file); var read = ProjectStore.Load(file);
            Check(read.Layers.Select(l => l.Map).SequenceEqual(doc.Layers.Select(l => l.Map)), "Map tags changed");
            Check(read.Layers.Where(l => l.Vector != null).Select(l => l.Vector!.ByteLength).SequenceEqual(doc.Layers.Where(l => l.Vector != null).Select(l => l.Vector!.ByteLength)), "Vector sources changed");
            var a = DesignRenderer.RenderOutput(doc); var b = DesignRenderer.RenderOutput(read);
            Check(a.Data.AsSpan().SequenceEqual(b.Data), "The reopened map looks different");
            var history = new History(); history.Reset(read); var before = read.Snapshot();
            var rootLayer = read.Layers.Single(l => l.Map is { IsRoot: true }); rootLayer.Map = rootLayer.Map! with { Theme = "dark" };
            history.Commit("tag", before, read); Check(history.CanUndo, "A tag change is not an undoable edit");
            bool rejected = false;
            try { var bad = read.Snapshot(); bad.Layers.First(l => l.Kind == LayerKind.Vector).Map = new MapTag { Role = MapTag.RootRole, MetersPerPixel = 1, FrameWidth = 1, FrameHeight = 1 }; bad.Validate(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A map root tag on a vector layer was accepted");
        });

        test("map export: layered PDF and .ai keep each map class as a vector layer", () =>
        {
            var doc = MapComposer.Build(Town(), new MapOptions { Width = 900, Height = 1200, Title = "Seoul" });
            var plan = PdfLayerExport.Build(doc, true);
            Check(plan.Expanded && plan.ImageParts == 1 && plan.VectorParts >= 15, $"Export plan: expanded {plan.Expanded}, vectors {plan.VectorParts}, images {plan.ImageParts}");
            Check(VectorPdfExport.Limitation(doc) == null, "The map cannot be written as a vector PDF");
            string pdf = Path.Combine(root, "poster.ai");
            ProjectStore.AtomicWrite(pdf, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.AiLayers, s));
            using (var file = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                var optional = file.Internals.Catalog.Elements.GetDictionary("/OCProperties")!;
                var names = optional.Elements.GetArray("/OCGs")!.Elements.Select(e => ((PdfSharp.Pdf.Advanced.PdfReference)e).Value).OfType<PdfDictionary>().Select(d => d.Elements.GetString("/Name")).ToArray();
                Check(names.Take(6).SequenceEqual(["배경", "녹지", "물", "건물", "도로", "철도"]) && names[^1].Contains("© OpenStreetMap contributors"), "PDF layers: " + string.Join(", ", names));
            }
            var flat = Task.Run(() => CompatibilityImport.ReadAsync(pdf, new CompatibilityOptions(Dpi: doc.Dpi, PreservePdfLayers: false))).GetAwaiter().GetResult().Document;
            var original = DesignRenderer.RenderOutput(doc); var back = Imaging.Render(flat);
            int w = Math.Min(original.Width, back.Width), h = Math.Min(original.Height, back.Height); double sum = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++) sum += Math.Abs(original.Data[(y * original.Width + x) * 4 + c] - back.Data[(y * back.Width + x) * 4 + c]);
            double difference = sum / (w * h * 3.0);
            Check(Math.Abs(original.Width - back.Width) <= 2 && difference < 6, $"The exported map looks different ({difference:0.###})");
        });

        test("map online: nothing is sent without consent; requests carry only the area or the words", () =>
        {
            MapDownload.ClearCache();
            var http = new FakeMapHttp(); var online = new MapDownload(http, TimeSpan.FromMilliseconds(120));
            bool refused = false; try { online.DownloadAsync(null!).GetAwaiter().GetResult(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && http.Calls.Count == 0, "A download without consent was sent");
            var search = MapDownload.Search("서울시청");
            bool wrong = false; try { online.DownloadAsync(MapConsent.Grant(search)).GetAwaiter().GetResult(); } catch (InvalidOperationException) { wrong = true; }
            Check(wrong && http.Calls.Count == 0, "Consent to a search allowed a download");
            var places = online.SearchAsync(MapConsent.Grant(search)).GetAwaiter().GetResult();
            Check(places.Count == 1 && places[0].Name.StartsWith("서울특별시청") && Math.Abs(places[0].Latitude - 37.5663) < 1e-9 && places[0].Bounds is { } box && box.IsValid, "The search answer was not read");
            var call = http.Calls.Single();
            Check(call.Method == "GET" && call.Url.StartsWith("https://nominatim.openstreetmap.org/search?q=") && call.Url.Contains(Uri.EscapeDataString("서울시청")) && call.UserAgent.StartsWith("Morupixel/") && call.Body.Length == 0,
                "The search request is not the stated one: " + call.Url);
            var center = MapFixtures.At(MapFixtures.Bounds, .5, .5);
            var area = MapDownload.Area(center.Latitude, center.Longitude, 700, true);
            var (data, osm) = online.DownloadAsync(MapConsent.Grant(area)).GetAwaiter().GetResult();
            var post = http.Calls[^1]; string body = System.Net.WebUtility.UrlDecode(post.Body);
            Check(http.Calls.Count == 2 && post.Method == "POST" && post.Url == "https://overpass-api.de/api/interpreter" && post.UserAgent.StartsWith("Morupixel/"), "The area request is not the stated one");
            Check(body.StartsWith("data=[out:xml]") && body.Contains("[bbox:") && body.Contains("way[\"building\"]") && !body.Contains("서울"), "The area request sends more than the box and the feature list");
            Check(data.Bounds == area.Area && data.Counts.GetValueOrDefault(MapClasses.Building) == MapFixtures.Buildings && osm.Length > 1000, "The downloaded data was not read with the requested bounds");
            online.DownloadAsync(MapConsent.Grant(area)).GetAwaiter().GetResult();
            Check(http.Calls.Count == 2, "The same request was sent again instead of using this run's copy");
            var other = MapFixtures.At(MapFixtures.Bounds, .4, .4);
            online.DownloadAsync(MapConsent.Grant(MapDownload.Area(other.Latitude, other.Longitude, 500, false))).GetAwaiter().GetResult();
            Check(http.Calls.Count == 3 && (http.Calls[2].At - http.Calls[1].At).TotalMilliseconds >= 100, "Two requests to one service were not spaced apart");
            Check(!MapDownload.Area(center.Latitude, center.Longitude, 2000, true).Buildings, "Buildings were requested for a large area");
            bool large = false; try { MapDownload.Area(center.Latitude, center.Longitude, 5000, false); } catch (ArgumentException) { large = true; }
            bool blank = false; try { MapDownload.Search(" "); } catch (ArgumentException) { blank = true; }
            Check(large && blank, "A large area or an empty search was accepted");
            var failing = new MapDownload(new FakeMapHttp { Failure = new MapDownloadException("인터넷에 연결할 수 없습니다.") }, TimeSpan.Zero);
            bool offline = false; try { failing.DownloadAsync(MapConsent.Grant(MapDownload.Area(10, 10, 500, false))).GetAwaiter().GetResult(); } catch (MapDownloadException e) { offline = e.Message.Contains("인터넷"); }
            Check(offline, "An offline failure was not reported clearly");
            var busy = new MapDownload(new FakeMapHttp { Answer = _ => Encoding.UTF8.GetBytes("<osm><remark>runtime error: Query timed out</remark></osm>") }, TimeSpan.Zero);
            bool timedOut = false; try { busy.DownloadAsync(MapConsent.Grant(MapDownload.Area(11, 11, 500, false))).GetAwaiter().GetResult(); } catch (MapDownloadException) { timedOut = true; }
            Check(timedOut, "A server-side timeout was not reported");
            Check(MapDownload.UserAgent.Contains("github.com/catsony5-web/compositor-windows"), "The User-Agent does not identify the application");
            MapDownload.ClearCache();
        });
    }
}
