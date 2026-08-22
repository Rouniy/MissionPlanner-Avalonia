using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using BruTile;
using BruTile.Web;
using BruTile.Wmts;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal sealed record OgcTileDefinition(
    string Name,
    string Attribution,
    ITileSchema Schema,
    IUrlBuilder UrlBuilder,
    string CacheIdentity);

internal sealed record WmsLayerChoice(
    string Name,
    string Title,
    string Version,
    string Crs);

internal sealed record WmsDiscovery(string ServerUrl, IReadOnlyList<WmsLayerChoice> Layers);

internal sealed record WmtsLayerChoice(int SourceIndex, string DisplayName);

internal sealed record WmtsDiscovery(
    string ServerUrl,
    byte[] Capabilities,
    string CapabilitiesHash,
    IReadOnlyList<WmtsLayerChoice> Layers);

/// <summary>
/// Discovers and persists the custom WMS/WMTS providers exposed by upstream Mission Planner.
/// Network parsing is deliberately kept out of map construction: WMTS capabilities are validated
/// once and cached locally so startup and offline map use never block on a remote server.
/// </summary>
internal static class OgcMapProvider {
  internal const string WmsMapType = "WMS";
  internal const string WmtsMapType = "WMTS";
  internal const int MaximumCapabilitiesBytes = 8 * 1024 * 1024;

  private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
  private static readonly object _wmtsGate = new();
  private static string? _cachedWmtsKey;
  private static IReadOnlyList<HttpTileSource>? _cachedWmtsSources;

  internal static bool IsOgcMapType(string? mapType) =>
      string.Equals(mapType, WmsMapType, StringComparison.Ordinal)
      || string.Equals(mapType, WmtsMapType, StringComparison.Ordinal);

  internal static bool HasConfiguration(string mapType) =>
      TryCreateDefinition(mapType, out _);

  internal static bool TryCreateDefinition(string mapType, out OgcTileDefinition definition) {
    try {
      if (string.Equals(mapType, WmsMapType, StringComparison.Ordinal)) {
        return TryCreateWmsDefinition(out definition);
      }
      if (string.Equals(mapType, WmtsMapType, StringComparison.Ordinal)) {
        return TryCreateWmtsDefinition(out definition);
      }
    } catch {
      // Damaged/stale settings must fall back to a built-in map provider at startup.
    }
    definition = null!;
    return false;
  }

  internal static async Task<WmsDiscovery> DiscoverWmsAsync(
      string serverUrl,
      HttpClient? client = null,
      CancellationToken cancellationToken = default) {
    Uri endpoint = ValidateServerUri(serverUrl);
    Uri capabilitiesUri = WithQuery(endpoint, new Dictionary<string, string> {
      ["SERVICE"] = "WMS",
      ["REQUEST"] = "GetCapabilities",
    }, "service", "request");
    byte[] bytes = await DownloadCapabilitiesAsync(
        capabilitiesUri, client ?? _client, cancellationToken).ConfigureAwait(false);
    XDocument document = await ReadSafeXmlAsync(bytes, cancellationToken).ConfigureAwait(false);
    string rootName = document.Root?.Name.LocalName ?? "";
    if (rootName is not ("WMT_MS_Capabilities" or "WMS_Capabilities")) {
      throw new InvalidDataException("The response is not a WMS capabilities document.");
    }

    string? version = document.Root?.Attribute("version")?.Value;
    version = string.IsNullOrWhiteSpace(version) ? "1.1.1" : version.Trim();
    bool png = document.Descendants()
        .Where(element => element.Name.LocalName == "GetMap")
        .SelectMany(element => element.Descendants())
        .Where(element => element.Name.LocalName == "Format")
        .Any(element => element.Value.Contains("image/png", StringComparison.OrdinalIgnoreCase));
    if (!png) {
      throw new InvalidDataException("The WMS server does not advertise PNG GetMap images.");
    }

    var layers = new List<WmsLayerChoice>();
    foreach (XElement layer in document.Descendants().Where(e => e.Name.LocalName == "Layer")) {
      string? name = DirectValue(layer, "Name");
      if (string.IsNullOrWhiteSpace(name)) {
        continue;
      }
      string[] supported = layer.AncestorsAndSelf()
          .SelectMany(e => e.Elements())
          .Where(e => e.Name.LocalName is "SRS" or "CRS")
          .SelectMany(e => e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
          .ToArray();
      string? crs = PreferredWmsCrs(supported);
      if (crs == null) {
        continue;
      }
      string title = DirectValue(layer, "Title")?.Trim() ?? name.Trim();
      layers.Add(new WmsLayerChoice(name.Trim(), title, version, crs));
    }
    if (layers.Count == 0) {
      throw new InvalidDataException(
          "The WMS server has no named PNG layer in EPSG:3857 or EPSG:4326.");
    }
    return new WmsDiscovery(endpoint.AbsoluteUri, layers);
  }

  internal static async Task<WmtsDiscovery> DiscoverWmtsAsync(
      string serverUrl,
      HttpClient? client = null,
      CancellationToken cancellationToken = default) {
    Uri endpoint = ValidateServerUri(serverUrl);
    byte[] bytes = await DownloadCapabilitiesAsync(
        endpoint, client ?? _client, cancellationToken).ConfigureAwait(false);
    byte[] normalized = await NormalizeWmtsCapabilitiesAsync(bytes, cancellationToken)
        .ConfigureAwait(false);
    IReadOnlyList<HttpTileSource> sources = ParseWmtsSources(normalized);
    var choices = sources.Select((source, index) => (source, index))
        .Where(item => IsWebMercator(item.source.Schema))
        .Select(item => new WmtsLayerChoice(item.index, DescribeWmtsSource(item.source)))
        .ToArray();
    if (choices.Length == 0) {
      throw new InvalidDataException(
          "The WMTS server has no Web Mercator image layer compatible with the map.");
    }
    string hash = Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant();
    return new WmtsDiscovery(endpoint.AbsoluteUri, normalized, hash, choices);
  }

  internal static void SaveWms(WmsDiscovery discovery, WmsLayerChoice layer) {
    if (!discovery.Layers.Contains(layer)) {
      throw new ArgumentException("The selected WMS layer does not belong to this discovery.",
          nameof(layer));
    }
    var settings = Settings.Instance;
    settings["WMSserver"] = discovery.ServerUrl;
    settings["WMSLayer"] = layer.Name;
    settings["WMSLayerTitle"] = layer.Title;
    settings["WMSVersion"] = layer.Version;
    settings["WMSCrs"] = layer.Crs;
    settings.Save();
  }

  internal static void SaveWmts(WmtsDiscovery discovery, WmtsLayerChoice layer) {
    if (!discovery.Layers.Contains(layer)) {
      throw new ArgumentException("The selected WMTS layer does not belong to this discovery.",
          nameof(layer));
    }
    string path = WmtsCapabilitiesPath(discovery.ServerUrl);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try {
      File.WriteAllBytes(temporary, discovery.Capabilities);
      File.Move(temporary, path, overwrite: true);
    } finally {
      try {
        File.Delete(temporary);
      } catch {
        // A stale temporary capabilities file is harmless and can be cleaned with the map cache.
      }
    }

    var settings = Settings.Instance;
    // Keep the historical upstream key spelling for settings compatibility.
    settings["WMSTserver"] = discovery.ServerUrl;
    settings["WMSTLayer"] = layer.SourceIndex.ToString(CultureInfo.InvariantCulture);
    settings["WMSTCapabilitiesHash"] = discovery.CapabilitiesHash;
    settings.Save();
    InvalidateWmtsCache();
  }

  internal static Uri BuildWmsTileUri(
      string serverUrl,
      string layer,
      string version,
      string crs,
      TileIndex index) =>
    new WmsUrlBuilder(serverUrl, layer, version, crs).GetUrl(new TileInfo { Index = index });

  internal static string WmtsCapabilitiesPath(string serverUrl) {
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl));
    string suffix = Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    return Path.Combine(AppPaths.MapTileCacheRoot, "ogc", $"wmts-{suffix}.xml");
  }

  private static bool TryCreateWmsDefinition(out OgcTileDefinition definition) {
    var settings = Settings.Instance;
    string? server = settings["WMSserver"];
    string? layer = settings["WMSLayer"];
    if (!TryValidateServerUri(server, out _) || string.IsNullOrWhiteSpace(layer)) {
      definition = null!;
      return false;
    }
    string version = settings["WMSVersion"] ?? "1.1.1";
    string crs = settings["WMSCrs"] ?? "EPSG:4326";
    if (!IsSupportedWmsCrs(crs)) {
      definition = null!;
      return false;
    }
    string title = settings["WMSLayerTitle"];
    if (string.IsNullOrWhiteSpace(title)) {
      title = layer;
    }
    var schema = new BruTile.Predefined.GlobalSphericalMercator(
        "png", minZoomLevel: 0, maxZoomLevel: 21);
    string identity = $"{server}\n{layer}\n{version}\n{crs}";
    definition = new OgcTileDefinition(
        WmsMapType, title, schema, new WmsUrlBuilder(server!, layer, version, crs), identity);
    return true;
  }

  private static bool TryCreateWmtsDefinition(out OgcTileDefinition definition) {
    var settings = Settings.Instance;
    string? server = settings["WMSTserver"];
    string? selectedText = settings["WMSTLayer"];
    string? expectedHash = settings["WMSTCapabilitiesHash"];
    if (!TryValidateServerUri(server, out _)
        || !int.TryParse(selectedText, NumberStyles.None, CultureInfo.InvariantCulture,
            out int selected)
        || selected < 0) {
      definition = null!;
      return false;
    }
    string path = WmtsCapabilitiesPath(server!);
    if (!File.Exists(path)) {
      definition = null!;
      return false;
    }
    string cacheKey = $"{server}\n{selected}\n{expectedHash}\n{File.GetLastWriteTimeUtc(path).Ticks}";
    IReadOnlyList<HttpTileSource> sources;
    lock (_wmtsGate) {
      if (!string.Equals(_cachedWmtsKey, cacheKey, StringComparison.Ordinal)
          || _cachedWmtsSources == null) {
        byte[] bytes = File.ReadAllBytes(path);
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedHash)
            && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase)) {
          definition = null!;
          return false;
        }
        _cachedWmtsSources = ParseWmtsSources(bytes);
        _cachedWmtsKey = cacheKey;
      }
      sources = _cachedWmtsSources;
    }
    if (selected >= sources.Count || !IsWebMercator(sources[selected].Schema)) {
      definition = null!;
      return false;
    }
    HttpTileSource selectedSource = sources[selected];
    string title = string.IsNullOrWhiteSpace(selectedSource.Name)
        ? WmtsMapType
        : selectedSource.Name;
    string identity = $"{server}\n{selected}\n{expectedHash}";
    definition = new OgcTileDefinition(
        WmtsMapType, title, selectedSource.Schema, selectedSource, identity);
    return true;
  }

  private static IReadOnlyList<HttpTileSource> ParseWmtsSources(byte[] capabilities) {
    using var stream = new MemoryStream(capabilities, writable: false);
    List<HttpTileSource> sources = WmtsCapabilitiesParser.Parse(stream);
    if (sources.Count > 2000) {
      throw new InvalidDataException("The WMTS capabilities document contains too many sources.");
    }
    return sources;
  }

  private static async Task<byte[]> NormalizeWmtsCapabilitiesAsync(
      byte[] bytes, CancellationToken cancellationToken) {
    XDocument document = await ReadSafeXmlAsync(bytes, cancellationToken).ConfigureAwait(false);
    if (document.Root?.Name.LocalName != "Capabilities") {
      throw new InvalidDataException("The response is not a WMTS capabilities document.");
    }
    using var stream = new MemoryStream();
    await document.SaveAsync(stream, SaveOptions.DisableFormatting, cancellationToken)
        .ConfigureAwait(false);
    return stream.ToArray();
  }

  private static async Task<XDocument> ReadSafeXmlAsync(
      byte[] bytes, CancellationToken cancellationToken) {
    if (bytes.Length == 0 || bytes.Length > MaximumCapabilitiesBytes) {
      throw new InvalidDataException("The capabilities response is empty or too large.");
    }
    using var stream = new MemoryStream(bytes, writable: false);
    using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings {
      Async = true,
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      MaxCharactersInDocument = MaximumCapabilitiesBytes,
      MaxCharactersFromEntities = 0,
      IgnoreComments = true,
      IgnoreProcessingInstructions = true,
    });
    XDocument document = await XDocument.LoadAsync(
        reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    if (document.Descendants().Take(100_001).Count() > 100_000) {
      throw new InvalidDataException("The capabilities document contains too many XML elements.");
    }
    return document;
  }

  private static async Task<byte[]> DownloadCapabilitiesAsync(
      Uri uri, HttpClient client, CancellationToken cancellationToken) {
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    request.Headers.UserAgent.TryParseAdd("MissionPlannerAvalonia/OGC");
    using HttpResponseMessage response = await client.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > MaximumCapabilitiesBytes) {
      throw new InvalidDataException("The capabilities response is larger than 8 MiB.");
    }
    await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
        .ConfigureAwait(false);
    using var output = new MemoryStream();
    byte[] buffer = new byte[16 * 1024];
    while (true) {
      int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
      if (count == 0) {
        break;
      }
      if (output.Length + count > MaximumCapabilitiesBytes) {
        throw new InvalidDataException("The capabilities response is larger than 8 MiB.");
      }
      output.Write(buffer, 0, count);
    }
    return output.ToArray();
  }

  private static Uri ValidateServerUri(string serverUrl) {
    if (!TryValidateServerUri(serverUrl, out Uri? uri)) {
      throw new ArgumentException("Enter an absolute HTTP or HTTPS server URL.", nameof(serverUrl));
    }
    return uri!;
  }

  private static bool TryValidateServerUri(string? value, out Uri? uri) {
    uri = null;
    bool valid = !string.IsNullOrWhiteSpace(value)
        && value.Length <= 8192
        && Uri.TryCreate(value, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(uri.Host);
    if (!valid) {
      uri = null;
    }
    return valid;
  }

  private static Uri WithQuery(
      Uri baseUri,
      IReadOnlyDictionary<string, string> additions,
      params string[] removeKeys) {
    var remove = new HashSet<string>(removeKeys, StringComparer.OrdinalIgnoreCase);
    var parts = baseUri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Where(part => !remove.Contains(Uri.UnescapeDataString(part.Split('=', 2)[0])))
        .ToList();
    parts.AddRange(additions.Select(pair =>
        $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    var builder = new UriBuilder(baseUri) {
      Query = string.Join('&', parts),
      Fragment = "",
    };
    return builder.Uri;
  }

  private static string? DirectValue(XElement parent, string localName) =>
      parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

  private static string? PreferredWmsCrs(IEnumerable<string> values) {
    string[] normalized = values.Select(value => value.Trim().ToUpperInvariant()).ToArray();
    return normalized.FirstOrDefault(IsWebMercatorCrs)
        ?? normalized.FirstOrDefault(value => value == "EPSG:4326");
  }

  private static bool IsSupportedWmsCrs(string crs) =>
      IsWebMercatorCrs(crs.Trim().ToUpperInvariant())
      || string.Equals(crs.Trim(), "EPSG:4326", StringComparison.OrdinalIgnoreCase);

  private static bool IsWebMercator(ITileSchema schema) {
    if (!IsWebMercatorCrs(schema.Srs ?? "")
        && !(schema.Name?.Contains("GoogleMapsCompatible", StringComparison.OrdinalIgnoreCase)
            ?? false)) {
      return false;
    }
    if (schema.Resolutions.Count == 0 || schema.Resolutions.Keys.Min() != 0) {
      return false;
    }
    foreach ((int level, Resolution resolution) in schema.Resolutions) {
      if (level is < 0 or > 30 || resolution.TileWidth != 256 || resolution.TileHeight != 256) {
        return false;
      }
      long expectedMatrixSize = 1L << level;
      double expectedResolution = 156543.03392804097 / expectedMatrixSize;
      if (resolution.MatrixWidth != expectedMatrixSize
          || resolution.MatrixHeight != expectedMatrixSize
          || Math.Abs(resolution.UnitsPerPixel - expectedResolution) > expectedResolution * 0.001
          || Math.Abs(resolution.Left + 20037508.342789244) > 1
          || Math.Abs(resolution.Top - 20037508.342789244) > 1) {
        return false;
      }
    }
    return true;
  }

  private static bool IsWebMercatorCrs(string crs) =>
      crs.Contains("3857", StringComparison.OrdinalIgnoreCase)
      || crs.Contains("900913", StringComparison.OrdinalIgnoreCase)
      || crs.Contains("102100", StringComparison.OrdinalIgnoreCase)
      || crs.Contains("102113", StringComparison.OrdinalIgnoreCase);

  private static string DescribeWmtsSource(HttpTileSource source) {
    string title = string.IsNullOrWhiteSpace(source.Name) ? "Unnamed layer" : source.Name;
    string schema = string.IsNullOrWhiteSpace(source.Schema.Name) ? "matrix set" : source.Schema.Name;
    return $"{title} — {schema} ({source.Schema.Format})";
  }

  private static void InvalidateWmtsCache() {
    lock (_wmtsGate) {
      _cachedWmtsKey = null;
      _cachedWmtsSources = null;
    }
  }

  private sealed class WmsUrlBuilder(
      string serverUrl,
      string layer,
      string version,
      string crs) : IUrlBuilder {
    private const double _origin = 20037508.342789244;

    public Uri GetUrl(TileInfo info) {
      TileIndex index = info.Index;
      if (index.Level is < 0 or > 30 || index.Col < 0 || index.Row < 0) {
        throw new ArgumentOutOfRangeException(nameof(info), "Invalid Web Mercator tile index.");
      }
      (double minX, double minY, double maxX, double maxY) =
          IsWebMercatorCrs(crs) ? WebMercatorBounds(index) : GeographicBounds(index);
      bool latitudeFirst = version.StartsWith("1.3", StringComparison.Ordinal)
          && string.Equals(crs, "EPSG:4326", StringComparison.OrdinalIgnoreCase);
      string bbox = latitudeFirst
          ? Coordinates(minY, minX, maxY, maxX)
          : Coordinates(minX, minY, maxX, maxY);
      string coordinateKey = version.StartsWith("1.3", StringComparison.Ordinal) ? "CRS" : "SRS";
      Uri endpoint = ValidateServerUri(serverUrl);
      return WithQuery(endpoint, new Dictionary<string, string> {
        ["SERVICE"] = "WMS",
        ["REQUEST"] = "GetMap",
        ["VERSION"] = version,
        ["LAYERS"] = layer,
        ["STYLES"] = "",
        ["BBOX"] = bbox,
        ["WIDTH"] = "256",
        ["HEIGHT"] = "256",
        [coordinateKey] = crs,
        ["FORMAT"] = "image/png",
        ["TRANSPARENT"] = "TRUE",
      }, "service", "request", "version", "layers", "styles", "bbox", "width", "height",
          "srs", "crs", "format", "transparent");
    }

    private static (double, double, double, double) WebMercatorBounds(TileIndex index) {
      double tiles = Math.Pow(2, index.Level);
      double size = _origin * 2 / tiles;
      double minX = -_origin + index.Col * size;
      double maxX = minX + size;
      double maxY = _origin - index.Row * size;
      double minY = maxY - size;
      return (minX, minY, maxX, maxY);
    }

    private static (double, double, double, double) GeographicBounds(TileIndex index) {
      double tiles = Math.Pow(2, index.Level);
      double minLon = index.Col / tiles * 360 - 180;
      double maxLon = (index.Col + 1) / tiles * 360 - 180;
      double maxLat = TileLatitude(index.Row, tiles);
      double minLat = TileLatitude(index.Row + 1, tiles);
      return (minLon, minLat, maxLon, maxLat);
    }

    private static double TileLatitude(double row, double tiles) =>
      Math.Atan(Math.Sinh(Math.PI * (1 - 2 * row / tiles))) * 180 / Math.PI;

    private static string Coordinates(double a, double b, double c, double d) =>
      string.Join(',', new[] { a, b, c, d }
          .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
  }
}
