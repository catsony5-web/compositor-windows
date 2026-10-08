using System.Text.Json.Nodes;

namespace Compositor.Windows;

// create_map: argument rules and the capability description (maps from OpenStreetMap data).
public static partial class AutomationCatalog
{
    static void ValidateMapArguments(JsonObject arguments)
    {
        bool online = arguments["source"]?.GetValue<string>() == "online" || arguments["source"] == null && !arguments.ContainsKey("path");
        if (online)
        {
            if (arguments.ContainsKey("path")) throw new ArgumentException("source=online does not read a file; omit path or use source=file.");
            bool center = arguments.ContainsKey("centerLatitude") && arguments.ContainsKey("centerLongitude");
            if (arguments.ContainsKey("place") == center) throw new ArgumentException("source=online needs either place or centerLatitude with centerLongitude.");
        }
        else
        {
            if (!arguments.ContainsKey("path")) throw new ArgumentException("source=file needs path (an .osm or .geojson file).");
            if (arguments.ContainsKey("place") || arguments.ContainsKey("radius")) throw new ArgumentException("place and radius belong to source=online.");
        }
        if (arguments.ContainsKey("centerLatitude") != arguments.ContainsKey("centerLongitude")) throw new ArgumentException("centerLatitude and centerLongitude go together.");
        if (arguments.ContainsKey("siteLatitude") != arguments.ContainsKey("siteLongitude")) throw new ArgumentException("siteLatitude and siteLongitude go together.");
        if (NumberValue(arguments, "width", 1772) * NumberValue(arguments, "height", 2362) > 16_777_216)
            throw new ArgumentException("Automation images must not exceed 16,777,216 pixels.");
    }

    static JsonObject MapCapabilities() => new()
    {
        ["command"] = "create_map", ["templates"] = Strings(["poster", "site"]), ["themes"] = Strings(MapThemes.All.Select(t => t.Key)),
        ["fileFormats"] = Strings([".osm", ".geojson"]), ["projection"] = "Web Mercator (EPSG:3857); metersPerPixel is ground metres per document pixel at the center latitude",
        ["layers"] = "One vector layer per class (roads by class, rail, sea, lakes and rivers, waterways, parks, forest, grass, buildings) grouped by kind under one map folder; text, marker, north arrow and scale bar are editable layers.",
        ["attribution"] = MapComposer.Attribution, ["attributionRequired"] = true,
        ["online"] = new JsonObject
        {
            ["source"] = "online", ["maxRadius"] = MapDownload.MaxRadius, ["buildingRadius"] = MapDownload.MaxBuildingRadius,
            ["endpoints"] = Strings([MapDownload.OverpassEndpoint.ToString(), MapDownload.NominatimEndpoint.ToString()]),
            ["permission"] = "Refused (online_map_not_allowed) unless the user allowed AI map requests for this run in the app's consent window; nothing is downloaded automatically.",
            ["sent"] = "Only the latitude/longitude box and the list of map features (Overpass), or the place words (Nominatim), with the app name and version as User-Agent."
        },
        ["batch"] = false, ["undoSteps"] = 0, ["newDocument"] = true
    };
}
