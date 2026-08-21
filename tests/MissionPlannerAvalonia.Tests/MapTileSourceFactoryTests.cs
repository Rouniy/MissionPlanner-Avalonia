using System.Net;
using BruTile;
using BruTile.Cache;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class MapTileSourceFactoryTests {
  [Theory]
  [InlineData("ServerOnly", "ServerOnly")]
  [InlineData("serverandcache", "ServerAndCache")]
  [InlineData("CACHEONLY", "CacheOnly")]
  [InlineData("not-a-mode", "ServerAndCache")]
  [InlineData(null, "ServerAndCache")]
  public void Access_mode_parser_is_case_insensitive_and_safe(
      string? value, string expected) {
    Assert.Equal(expected, MapTileSourceFactory.ParseAccessMode(value).ToString());
  }

  [Fact]
  public void Provider_cache_directories_are_isolated_by_name_and_url() {
    string google = MapTileSourceFactory.ProviderCacheDirectory(
        "Satellite", "https://google.invalid/{z}/{x}/{y}");
    string esri = MapTileSourceFactory.ProviderCacheDirectory(
        "Satellite", "https://esri.invalid/{z}/{x}/{y}");
    string custom = MapTileSourceFactory.ProviderCacheDirectory(
        "Custom", "https://google.invalid/{z}/{x}/{y}");

    Assert.NotEqual(google, esri);
    Assert.NotEqual(google, custom);
    Assert.StartsWith(AppPaths.MapTileCacheRoot, google, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Cache_only_reads_hits_and_never_uses_http_for_misses() {
    string directory = NewTemporaryDirectory();
    try {
      var cache = new FileCache(directory, "tile");
      var hit = Tile(3, 4, 5);
      var miss = Tile(6, 7, 8);
      byte[] expected = { 1, 2, 3, 4 };
      cache.Add(hit.Index, expected);

      var handler = new TrackingHandler(new byte[] { 9 });
      using var client = new HttpClient(handler);
      var source = MapTileSourceFactory.CreateSource(
          "Offline", "https://tiles.invalid/{z}/{x}/{y}",
          MapTileAccessMode.CacheOnly, cache);

      Assert.Equal(expected, await source.GetTileAsync(client, hit));
      Assert.Null(await source.GetTileAsync(client, miss));
      Assert.Equal(0, handler.RequestCount);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task Server_and_cache_downloads_once_then_reuses_disk_tile() {
    string directory = NewTemporaryDirectory();
    try {
      var cache = new FileCache(directory, "tile");
      var tile = Tile(9, 10, 11);
      byte[] expected = { 5, 6, 7 };
      var handler = new TrackingHandler(expected);
      using var client = new HttpClient(handler);
      var source = MapTileSourceFactory.CreateSource(
          "Online", "https://tiles.invalid/{z}/{x}/{y}",
          MapTileAccessMode.ServerAndCache, cache);

      Assert.Equal(expected, await source.GetTileAsync(client, tile));
      Assert.Equal(expected, await source.GetTileAsync(client, tile));
      Assert.Equal(1, handler.RequestCount);
      Assert.True(handler.SawUserAgent);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static TileInfo Tile(int col, int row, int level) => new() {
    Index = new TileIndex(col, row, level),
    Extent = new Extent(0, 0, 1, 1),
  };

  private static string NewTemporaryDirectory() {
    string path = Path.Combine(Path.GetTempPath(), "mpa-map-cache-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  private sealed class TrackingHandler(byte[] response) : HttpMessageHandler {
    public int RequestCount { get; private set; }
    public bool SawUserAgent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      RequestCount++;
      SawUserAgent = request.Headers.UserAgent.Count > 0;
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new ByteArrayContent(response),
      });
    }
  }
}
