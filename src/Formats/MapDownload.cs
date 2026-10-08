using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Compositor.Windows;

public enum MapRequestKind { PlaceSearch, AreaData }

/// <summary>
/// Exactly what one optional online request sends and where: shown to the user before anything is
/// sent. A place search sends the typed words to the public OpenStreetMap search (Nominatim); an
/// area download sends a latitude/longitude box and the list of map features to the public
/// OpenStreetMap Overpass API. Nothing else (no file, document, name or account) is sent.
/// </summary>
public sealed record MapRequest(MapRequestKind Kind, Uri Endpoint, string? Query, MapBounds? Area, bool Buildings, string Language)
{
    /// <summary>The full request URL (the search words travel in it).</summary>
    public Uri Address => Kind == MapRequestKind.PlaceSearch
        ? new Uri(Endpoint + "?q=" + Uri.EscapeDataString(Query!) + "&format=jsonv2&limit=6&accept-language=" + Uri.EscapeDataString(Language))
        : Endpoint;
    /// <summary>The POST body of an area download (an Overpass query); null for a search.</summary>
    public string? Body => Kind == MapRequestKind.AreaData ? MapDownload.OverpassQuery(Area!.Value, Buildings) : null;
    public string Key => Address + "\n" + Body;
}

/// <summary>Proof that the user agreed to send one exact <see cref="MapRequest"/>; made only by the consent step.</summary>
public sealed class MapConsent
{
    public MapRequest Request { get; }
    MapConsent(MapRequest request) => Request = request;
    /// <summary>Called by the consent dialog after the user confirms, or by the AI connection when the user allowed it for this session.</summary>
    internal static MapConsent Grant(MapRequest request) => new(request ?? throw new ArgumentNullException(nameof(request)));
}

public sealed record MapPlace(string Name, double Latitude, double Longitude, MapBounds? Bounds);

/// <summary>Sends one HTTP request and returns the body. Self-tests replace it with a fake; nothing in the tests reaches the network.</summary>
public interface IMapHttp
{
    Task<byte[]> SendAsync(HttpRequestMessage request, long maxBytes, CancellationToken token);
}

public sealed class MapDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The optional online map data source. Off unless the user starts it: every request needs a
/// <see cref="MapConsent"/> for exactly that request. It follows the public services' usage rules:
/// an identifying User-Agent, at most one request per second to each service, one at a time,
/// small areas only, and answers kept in memory for this run so repeating a preview does not ask
/// again. Nothing is written to disk unless the user saves the data as an .osm file.
/// </summary>
public sealed class MapDownload(IMapHttp http, TimeSpan? interval = null)
{
    public static readonly Uri NominatimEndpoint = new("https://nominatim.openstreetmap.org/search");
    public static readonly Uri OverpassEndpoint = new("https://overpass-api.de/api/interpreter");
    public const double MaxRadius = 2500, MaxBuildingRadius = 1500;
    public const long MaxResponseBytes = 256L << 20, MaxSearchBytes = 1 << 20;
    const int CacheEntries = 6;
    const long CacheBytes = 384L << 20;

    public static string UserAgent { get; } = "Morupixel/" + (typeof(MapDownload).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0")
        + " (+https://github.com/catsony5-web/compositor-windows)";

    readonly TimeSpan spacing = interval ?? TimeSpan.FromSeconds(1.1);
    static readonly Dictionary<string, SemaphoreSlim> gates = new();
    static readonly Dictionary<string, DateTime> last = new();
    // Shared by every window of this run; never written to disk.
    static readonly LinkedList<(string Key, byte[] Data)> cache = new();

    /// <summary>The real network layer. Created on first use only, never at start-up.</summary>
    public static MapDownload Online => online.Value;
    static readonly Lazy<MapDownload> online = new(() => new MapDownload(new HttpMapClient()));

    public static MapRequest Search(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length is < 2 or > 200) throw new ArgumentException("찾을 장소를 2~200자로 입력해 주세요.");
        return new(MapRequestKind.PlaceSearch, NominatimEndpoint, query, null, false, Loc.Language);
    }

    /// <summary>An area around a point. Radius at most <see cref="MaxRadius"/> m; buildings up to <see cref="MaxBuildingRadius"/> m.</summary>
    public static MapRequest Area(double latitude, double longitude, double radius, bool buildings)
    {
        if (!double.IsFinite(latitude) || Math.Abs(latitude) > 85 || !double.IsFinite(longitude) || Math.Abs(longitude) > 180)
            throw new ArgumentException("중심 위도는 -85~85, 경도는 -180~180으로 입력해 주세요.");
        if (!double.IsFinite(radius) || radius < 100 || radius > MaxRadius)
            throw new ArgumentException($"인터넷에서 받는 영역은 반경 100~{MaxRadius:N0}m입니다. 더 넓은 지도는 openstreetmap.org에서 내보낸 .osm 파일을 사용해 주세요.");
        var box = MapBounds.Around(latitude, longitude, radius);
        return new(MapRequestKind.AreaData, OverpassEndpoint, null, Round(box), buildings && radius <= MaxBuildingRadius, "");
    }
    static MapBounds Round(MapBounds b) => new(Math.Round(b.South, 5), Math.Round(b.West, 5), Math.Round(b.North, 5), Math.Round(b.East, 5));

    /// <summary>The Overpass query of an area: the ways and multipolygons the map draws, with their nodes.</summary>
    public static string OverpassQuery(MapBounds b, bool buildings)
    {
        string box = string.Format(CultureInfo.InvariantCulture, "{0:0.#####},{1:0.#####},{2:0.#####},{3:0.#####}", b.South, b.West, b.North, b.East);
        const string natural = "^(water|bay|coastline|wood|grassland|heath|scrub)$";
        const string landuse = "^(reservoir|basin|forest|grass|meadow|orchard|vineyard|allotments|recreation_ground|village_green|cemetery)$";
        const string leisure = "^(park|garden|playground|recreation_ground|golf_course|nature_reserve|common|dog_park|pitch)$";
        var query = new StringBuilder();
        query.Append("[out:xml][timeout:90][maxsize:268435456][bbox:").Append(box).Append("];(");
        query.Append("way[\"highway\"];way[\"railway\"];way[\"waterway\"];way[\"water\"];");
        query.Append("way[\"natural\"~\"").Append(natural).Append("\"];way[\"landuse\"~\"").Append(landuse).Append("\"];way[\"leisure\"~\"").Append(leisure).Append("\"];");
        query.Append("relation[\"type\"=\"multipolygon\"][\"natural\"~\"").Append(natural).Append("\"];");
        query.Append("relation[\"type\"=\"multipolygon\"][\"landuse\"~\"").Append(landuse).Append("\"];");
        query.Append("relation[\"type\"=\"multipolygon\"][\"leisure\"~\"").Append(leisure).Append("\"];");
        query.Append("relation[\"type\"=\"multipolygon\"][\"waterway\"=\"riverbank\"];");
        if (buildings) query.Append("way[\"building\"];relation[\"type\"=\"multipolygon\"][\"building\"];");
        query.Append(");(._;>;);out body qt;");
        return query.ToString();
    }

    /// <summary>Finds places by name. Requires the user's consent to this exact search.</summary>
    public async Task<IReadOnlyList<MapPlace>> SearchAsync(MapConsent consent, CancellationToken token = default)
    {
        var request = Check(consent, MapRequestKind.PlaceSearch);
        var bytes = await FetchAsync(request, MaxSearchBytes, token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var places = new List<MapPlace>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                static double Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble()
                    : double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                string name = item.TryGetProperty("display_name", out var n) ? n.GetString() ?? "" : "";
                double lat = item.TryGetProperty("lat", out var a) ? Number(a) : double.NaN, lon = item.TryGetProperty("lon", out var o) ? Number(o) : double.NaN;
                if (name.Length == 0 || !double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;
                MapBounds? box = null;
                if (item.TryGetProperty("boundingbox", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() == 4)
                {
                    var b = new MapBounds(Number(bb[0]), Number(bb[2]), Number(bb[1]), Number(bb[3]));
                    if (b.IsValid) box = b;
                }
                places.Add(new(name.Length > 300 ? name[..300] : name, lat, lon, box));
            }
            return places;
        }
        catch (JsonException e) { throw new MapDownloadException("장소 검색 결과를 읽지 못했습니다. 잠시 후 다시 시도해 주세요.", e); }
        catch (InvalidOperationException e) { throw new MapDownloadException("장소 검색 결과를 읽지 못했습니다. 잠시 후 다시 시도해 주세요.", e); }
    }

    /// <summary>Downloads the map data of an area. Requires the user's consent to this exact area. Returns the parsed data and the raw .osm bytes (to save as a file).</summary>
    public async Task<(MapData Data, byte[] Osm)> DownloadAsync(MapConsent consent, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var request = Check(consent, MapRequestKind.AreaData);
        var bytes = await FetchAsync(request, MaxResponseBytes, token).ConfigureAwait(false);
        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (head.Contains("<remark>", StringComparison.Ordinal) && head.Contains("error", StringComparison.OrdinalIgnoreCase) && !head.Contains("<way", StringComparison.Ordinal))
            throw new MapDownloadException("지도 데이터 서버가 이 영역을 처리하지 못했습니다(시간·용량 초과). 반경을 줄이거나 건물을 빼고 다시 받아 주세요.");
        try
        {
            using var input = new MemoryStream(bytes, false);
            return (MapImport.ReadOsm(input, bytes.Length, progress, token, request.Area), bytes);
        }
        catch (System.Xml.XmlException e) { throw new MapDownloadException("받은 지도 데이터를 읽지 못했습니다. 잠시 후 다시 시도해 주세요.", e); }
    }

    static MapRequest Check(MapConsent? consent, MapRequestKind kind)
    {
        if (consent == null) throw new InvalidOperationException("인터넷에 요청을 보내려면 먼저 사용자 동의가 필요합니다.");
        if (consent.Request.Kind != kind) throw new InvalidOperationException("동의한 요청과 다른 요청입니다.");
        return consent.Request;
    }

    async Task<byte[]> FetchAsync(MapRequest request, long maxBytes, CancellationToken token)
    {
        lock (cache)
        {
            var hit = cache.FirstOrDefault(entry => entry.Key == request.Key);
            if (hit.Data != null) { cache.Remove(hit); cache.AddFirst(hit); return hit.Data; }
        }
        string host = request.Endpoint.Host; SemaphoreSlim? gate;
        lock (gates) { if (!gates.TryGetValue(host, out gate)) gates[host] = gate = new SemaphoreSlim(1, 1); }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // One request at a time per service, at least `spacing` apart.
            DateTime previous; lock (last) previous = last.GetValueOrDefault(host);
            var wait = previous + spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            using var message = new HttpRequestMessage(request.Kind == MapRequestKind.AreaData ? HttpMethod.Post : HttpMethod.Get, request.Address);
            message.Headers.UserAgent.ParseAdd(UserAgent);
            message.Headers.Accept.ParseAdd(request.Kind == MapRequestKind.AreaData ? "application/osm3s+xml, application/xml" : "application/json");
            if (request.Body is { } body) message.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", body)]);
            byte[] data;
            try { data = await http.SendAsync(message, maxBytes, token).ConfigureAwait(false); }
            finally { lock (last) last[host] = DateTime.UtcNow; }
            lock (cache)
            {
                cache.AddFirst((request.Key, data));
                while (cache.Count > CacheEntries || cache.Count > 1 && cache.Sum(e => (long)e.Data.Length) > CacheBytes) cache.RemoveLast();
            }
            return data;
        }
        finally { gate.Release(); }
    }

    /// <summary>Forgets answers kept in memory (self-tests).</summary>
    internal static void ClearCache() { lock (cache) cache.Clear(); lock (last) last.Clear(); }

    // The real HTTP layer: HTTPS only, bounded size and time, clear messages for offline and busy servers.
    sealed class HttpMapClient : IMapHttp
    {
        static readonly HttpClient client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false, UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(150) };

        public async Task<byte[]> SendAsync(HttpRequestMessage request, long maxBytes, CancellationToken token)
        {
            if (request.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new MapDownloadException("지도 데이터는 HTTPS로만 받습니다.");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new MapDownloadException("인터넷에 연결할 수 없습니다. 연결을 확인하거나, openstreetmap.org에서 내보낸 .osm 파일로 만들어 주세요.", e); }
            catch (TaskCanceledException e) when (!token.IsCancellationRequested) { throw new MapDownloadException("지도 데이터 서버가 시간 안에 응답하지 않았습니다. 반경을 줄이거나 잠시 후 다시 시도해 주세요.", e); }
            using (response)
            {
                int status = (int)response.StatusCode;
                if (status == 429) throw new MapDownloadException("지도 데이터 서버에 요청이 많습니다. 1~2분 뒤에 다시 시도해 주세요.");
                if (status is 504 or 503) throw new MapDownloadException("지도 데이터 서버가 바쁘거나 시간 안에 처리하지 못했습니다. 반경을 줄이거나 잠시 후 다시 시도해 주세요.");
                if (status is 400) throw new MapDownloadException("지도 데이터 서버가 요청을 받아들이지 않았습니다. 영역을 바꿔 다시 시도해 주세요.");
                if (status is < 200 or >= 300) throw new MapDownloadException($"지도 데이터 서버 오류입니다(HTTP {status}). 잠시 후 다시 시도해 주세요.");
                if (response.Content.Headers.ContentLength is long declared && declared > maxBytes) throw new MapDownloadException("받을 지도 데이터가 너무 큽니다. 반경을 줄이거나 건물을 빼고 다시 받아 주세요.");
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[1 << 16]; int read;
                try
                {
                    while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if (output.Length + read > maxBytes) throw new MapDownloadException("받을 지도 데이터가 너무 큽니다. 반경을 줄이거나 건물을 빼고 다시 받아 주세요.");
                        output.Write(buffer, 0, read);
                    }
                }
                catch (IOException e) { throw new MapDownloadException("지도 데이터를 받는 중 연결이 끊겼습니다. 다시 시도해 주세요.", e); }
                return output.ToArray();
            }
        }
    }
}
