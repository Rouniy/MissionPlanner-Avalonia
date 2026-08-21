using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Projections;
using Mapsui.Tiling.Layers;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal enum MapTileAccessMode {
  ServerOnly,
  ServerAndCache,
  CacheOnly,
}

/// <summary>
/// Creates Mapsui tile sources with the same three access modes exposed by upstream Mission
/// Planner. Each provider has an isolated persistent cache so equal z/x/y coordinates can never
/// return a tile downloaded from another service.
/// </summary>
internal static class MapTileSourceFactory {
  private static readonly ConcurrentDictionary<string, FileCache> _caches = new();
  private static readonly HttpClient _prefetchClient = new() {
    Timeout = TimeSpan.FromSeconds(30),
  };

  internal static event Action? AccessModeChanged;
  internal static event Action<string>? MapTypeChanged;

  internal static MapTileAccessMode CurrentAccessMode =>
      ParseAccessMode(Settings.Instance["mapCache"]);

  internal static string CurrentMapType => NormalizeMapType(Settings.Instance["MapType"]);

  internal static TileLayer CreateMapLayer(string? mapType) {
    string normalized = NormalizeMapType(mapType);
    string url = UrlTemplateFor(normalized);
    string attribution = normalized switch {
      "GoogleSatelliteMap" or "GoogleHybridMap" => "© Google",
      "BingSatelliteMap" => "© Microsoft Bing",
      "OpenStreetMap" => "© OpenStreetMap contributors",
      _ => "© Esri",
    };
    return CreateLayer(normalized, url, attribution);
  }

  internal static string UrlTemplateFor(string? mapType) => NormalizeMapType(mapType) switch {
    "GoogleSatelliteMap" => "https://mt1.google.com/vt/lyrs=s&x={x}&y={y}&z={z}",
    "GoogleHybridMap" => "https://mt1.google.com/vt/lyrs=y&x={x}&y={y}&z={z}",
    "BingSatelliteMap" =>
        "https://ecn.t0.tiles.virtualearth.net/tiles/a{quadkey}.jpeg?g=1&n=z",
    "OpenStreetMap" => "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
    _ =>
        "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
  };

  internal static string NormalizeMapType(string? value) => value switch {
    "GoogleSatelliteMap" or "GoogleHybridMap" or "BingSatelliteMap" or
        "OpenStreetMap" or "EsriWorldImagery" => value,
    _ => "GoogleSatelliteMap",
  };

  internal static IReadOnlyList<TileInfo> AreaTiles(
      Extent extent, int minimumZoom, int maximumZoom) {
    var schema = new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21);
    var tiles = new Dictionary<TileIndex, TileInfo>();
    for (int level = Math.Clamp(minimumZoom, 1, 21);
         level <= Math.Clamp(maximumZoom, 1, 21); level++) {
      foreach (TileInfo tile in schema.GetTileInfos(extent, level)) {
        tiles[tile.Index] = tile;
      }
    }
    return [.. tiles.Values];
  }

  internal static IReadOnlyList<TileInfo> PathTiles(
      IReadOnlyList<(double Lat, double Lng)> path, int minimumZoom, int maximumZoom) {
    var schema = new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21);
    var tiles = new Dictionary<TileIndex, TileInfo>();
    if (path.Count == 0) {
      return [];
    }
    for (int level = Math.Clamp(minimumZoom, 1, 21);
         level <= Math.Clamp(maximumZoom, 1, 21); level++) {
      double resolution = schema.Resolutions[level].UnitsPerPixel;
      double sampleDistance = resolution * 128;
      for (int segment = 0; segment < Math.Max(1, path.Count - 1); segment++) {
        var start = path[segment];
        var end = path[Math.Min(segment + 1, path.Count - 1)];
        var (startX, startY) = SphericalMercator.FromLonLat(start.Lng, start.Lat);
        var (endX, endY) = SphericalMercator.FromLonLat(end.Lng, end.Lat);
        double distance = Math.Sqrt(
            (endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
        int samples = Math.Max(1, (int)Math.Ceiling(distance / sampleDistance));
        for (int sample = 0; sample <= samples; sample++) {
          double fraction = sample / (double)samples;
          double x = startX + (endX - startX) * fraction;
          double y = startY + (endY - startY) * fraction;
          double margin = sampleDistance / 2;
          var sampleExtent = new Extent(x - margin, y - margin, x + margin, y + margin);
          foreach (TileInfo tile in schema.GetTileInfos(sampleExtent, level)) {
            tiles[tile.Index] = tile;
          }
        }
      }
    }
    return [.. tiles.Values];
  }

  internal static async Task<MapPrefetchResult> PrefetchAsync(
      string mapType,
      IReadOnlyList<TileInfo> tiles,
      IProgress<(int Done, int Total)>? progress = null,
      CancellationToken cancellationToken = default) {
    if (CurrentAccessMode != MapTileAccessMode.ServerAndCache) {
      throw new InvalidOperationException(
          "Tile prefetch requires the ServerAndCache map-cache mode.");
    }
    if (tiles.Count > 20000) {
      throw new InvalidOperationException(
          $"The requested range contains {tiles.Count} tiles; reduce the area or zoom range.");
    }

    string normalized = NormalizeMapType(mapType);
    HttpTileSource source = CreateSource(
        normalized, UrlTemplateFor(normalized), MapTileAccessMode.ServerAndCache);
    int completed = 0;
    int downloaded = 0;
    int failed = 0;
    await Parallel.ForEachAsync(tiles, new ParallelOptions {
      CancellationToken = cancellationToken,
      MaxDegreeOfParallelism = 4,
    }, async (tile, token) => {
      try {
        byte[]? data = await source.GetTileAsync(_prefetchClient, tile, token);
        if (data is { Length: > 0 }) {
          Interlocked.Increment(ref downloaded);
        } else {
          Interlocked.Increment(ref failed);
        }
      } catch when (!token.IsCancellationRequested) {
        Interlocked.Increment(ref failed);
      } finally {
        int done = Interlocked.Increment(ref completed);
        if (done == tiles.Count || done % 25 == 0) {
          progress?.Report((done, tiles.Count));
        }
      }
    });
    return new MapPrefetchResult(tiles.Count, downloaded, failed);
  }

  internal static TileLayer CreateLayer(
      string name,
      string urlTemplate,
      string? attribution = null) {
    var source = CreateSource(name, urlTemplate, CurrentAccessMode);
    source.Attribution = new BruTile.Attribution(attribution ?? name);
    return new TileLayer(source) { Name = name };
  }

  internal static void SetAccessMode(string? value) {
    var mode = ParseAccessMode(value);
    string normalized = mode.ToString();
    string previous = NormalizeAccessMode(Settings.Instance["mapCache"]);
    Settings.Instance["mapCache"] = normalized;
    if (!string.Equals(previous, normalized, StringComparison.Ordinal)) {
      AccessModeChanged?.Invoke();
    }
  }

  internal static void SetMapType(string? value) {
    string normalized = NormalizeMapType(value);
    string previous = CurrentMapType;
    Settings.Instance["MapType"] = normalized;
    if (!string.Equals(previous, normalized, StringComparison.Ordinal)) {
      MapTypeChanged?.Invoke(normalized);
    }
  }

  internal static MapTileAccessMode ParseAccessMode(string? value) =>
      Enum.TryParse(value, ignoreCase: true, out MapTileAccessMode parsed)
          ? parsed
          : MapTileAccessMode.ServerAndCache;

  internal static string NormalizeAccessMode(string? value) => ParseAccessMode(value).ToString();

  internal static string ProviderCacheDirectory(string name, string urlTemplate) {
    string slug = Sanitize(name);
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(urlTemplate));
    string suffix = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    return Path.Combine(AppPaths.MapTileCacheRoot, $"{slug}-{suffix}");
  }

  internal static HttpTileSource CreateSource(
      string name,
      string urlTemplate,
      MapTileAccessMode mode,
      IPersistentCache<byte[]>? persistentCache = null) {
    IPersistentCache<byte[]>? cache = mode == MapTileAccessMode.ServerOnly
        ? null
        : persistentCache ?? GetCache(name, urlTemplate);

    if (mode == MapTileAccessMode.CacheOnly) {
      return new CacheOnlyHttpTileSource(
          new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21),
          urlTemplate, name, cache ?? new NullCache());
    }

    return new HttpTileSource(
        new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21),
        urlTemplate,
        name: name,
        persistentCache: cache,
        configureHttpRequestMessage: AddUserAgent);
  }

  private static FileCache GetCache(string name, string urlTemplate) {
    string path = ProviderCacheDirectory(name, urlTemplate);
    return _caches.GetOrAdd(path, directory => new FileCache(directory, "tile"));
  }

  private static void AddUserAgent(HttpRequestMessage request) {
    string version = AppVersion.Number;
    string product = string.IsNullOrWhiteSpace(version)
        ? "MissionPlannerAvalonia"
        : $"MissionPlannerAvalonia/{version}";
    request.Headers.UserAgent.TryParseAdd(product);
  }

  private static string Sanitize(string value) {
    var result = new StringBuilder(value.Length);
    foreach (char ch in value) {
      result.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
    }

    string slug = result.ToString().Trim('-');
    while (slug.Contains("--", StringComparison.Ordinal)) {
      slug = slug.Replace("--", "-", StringComparison.Ordinal);
    }
    return string.IsNullOrEmpty(slug) ? "tiles" : slug;
  }

  private sealed class CacheOnlyHttpTileSource : HttpTileSource {
    internal CacheOnlyHttpTileSource(
        ITileSchema schema,
        string urlTemplate,
        string name,
        IPersistentCache<byte[]> persistentCache)
        : base(schema, urlTemplate, name: name, persistentCache: persistentCache) {
    }

    public override Task<byte[]?> GetTileAsync(
        HttpClient httpClient,
        TileInfo tileInfo,
        CancellationToken? cancellationToken = null) =>
      Task.FromResult(PersistentCache.Find(tileInfo.Index));
  }
}

internal sealed record MapPrefetchResult(int Total, int Downloaded, int Failed);
