using System;
using System.Collections.Concurrent;
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

  internal static event Action? AccessModeChanged;

  internal static MapTileAccessMode CurrentAccessMode =>
      ParseAccessMode(Settings.Instance["mapCache"]);

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
          new GlobalSphericalMercator(), urlTemplate, name, cache ?? new NullCache());
    }

    return new HttpTileSource(
        new GlobalSphericalMercator(),
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
