using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Offscreen review of 지도 포스터 · 대지 위치도 on a made-up city (Tests/MapFixtures.City): the light and
// dark posters, a framed green poster, a site location map, the map dialog (file, site, online), the
// consent window and the editor with a map open. Nothing is downloaded.
public sealed partial class MainWindow
{
    static readonly MapBounds PreviewCity = new(37.5300, 126.9450, 37.5900, 127.0250);

    void RenderMapPreviews(string directory, Action<Window, string, int, int> capture, Action<Window, string, int> captureFit)
    {
        var data = MapFixtures.City(PreviewCity);
        void Save(Document document, string name)
        {
            var image = DesignRenderer.RenderOutput(document);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image.Bitmap()));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        var poster = new MapOptions { Title = "Morupolis", Subtitle = Loc.T("모루 시 · 예시 데이터"), Width = 1200, Height = 1600 };
        Save(MapComposer.Build(data, poster), "map-poster-light");
        Save(MapComposer.Build(data, poster with { Theme = "dark" }), "map-poster-dark");
        Save(MapComposer.Build(data, poster with { Theme = "green", Frame = true, Title = Loc.T("모루 시") }), "map-poster-green-frame");
        var site = MapFixtures.At(PreviewCity, .47, .62);
        var siteOptions = MapOptions.For(MapTemplate.SiteLocation) with { Width = 1754, Height = 1240, SiteLatitude = site.Latitude, SiteLongitude = site.Longitude, Rings = [500, 1000], Subtitle = Loc.T("모루 시 중앙로 12 · 예시 대지"), MetersPerPixel = 10000 * .0254 / 150 };
        var siteDocument = MapComposer.Build(data, siteOptions);
        Save(siteDocument, "map-site");
        Save(MapComposer.Build(data, siteOptions with { Theme = "light", Marker = MapMarker.Pin, Rings = [] }), "map-site-pin");

        var dialog = CreateMapDialog(MapSource.File);
        dialog.SetData(data, "morupolis", "morupolis.osm"); dialog.TitleBox.Text = "Morupolis"; dialog.SubtitleBox.Text = Loc.T("모루 시 · 예시 데이터");
        dialog.RefreshPreviewNow(); capture(dialog, "map-dialog", 1180, 860); dialog.Close();
        var siteDialog = CreateMapDialog(MapSource.File);
        siteDialog.SetData(data, "morupolis", "morupolis.osm"); siteDialog.SetTemplate(MapTemplate.SiteLocation);
        siteDialog.RefreshPreviewNow(); capture(siteDialog, "map-dialog-site", 1180, 860); siteDialog.Close();
        // Every setting at once: a tall window shows the whole settings column without scrolling.
        var allOptions = CreateMapDialog(MapSource.File);
        allOptions.SetData(data, "morupolis", "morupolis.osm"); allOptions.SetTemplate(MapTemplate.SiteLocation);
        allOptions.RefreshPreviewNow(); capture(allOptions, "map-dialog-site-options", 1180, 1720); allOptions.Close();
        var online = CreateMapDialog(MapSource.Online); online.QueryBox.Text = Loc.T("모루 시청");
        capture(online, "map-dialog-online", 1180, 860); online.Close();
        captureFit(new MapConsentDialog(null, MapDownload.Area(37.5665, 126.978, 1000, true), false), "map-consent", 620);
        captureFit(new MapConsentDialog(null, MapDownload.Search(Loc.T("모루 시청")), false), "map-consent-search", 620);

        AddTab(siteDocument, null); SetWorkspaceMode(true);
        var roads = doc.Layers.First(l => l.Map?.Role == MapClasses.RoadGroup);
        doc.ActiveId = roads.Id; selectedLayers.Clear(); selectedLayers.Add(roads.Id); ShowStudioPage(1);
        Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        capture(this, "map-editor", 1480, 920);
    }
}
