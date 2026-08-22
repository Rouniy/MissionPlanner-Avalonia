using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Headless.XUnit;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Tests;

public sealed class HongKongNoFlyTests {
  private const string _geoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "properties": {
              "name": "Airport\nzone",
              "description": "First\r\nline"
            },
            "geometry": {
              "type": "Polygon",
              "coordinates": [
                [[113.8,22.2],[114.0,22.2],[114.0,22.4],[113.8,22.2]],
                [[113.85,22.25],[113.9,22.25],[113.9,22.3],[113.85,22.25]]
              ]
            }
          },
          {
            "type": "Feature",
            "properties": { "name": "Islands" },
            "geometry": {
              "type": "MultiPolygon",
              "coordinates": [
                [[[114.1,22.1],[114.2,22.1],[114.2,22.2],[114.1,22.1]]],
                [[[114.3,22.1],[114.4,22.1],[114.4,22.2],[114.3,22.1]]]
              ]
            }
          }
        ]
      }
      """;

  [Fact]
  public void Parser_preserves_polygon_holes_and_splits_multipolygons() {
    IReadOnlyList<HongKongNoFlyZone> zones = HongKongNoFlyService.Parse(
        Encoding.UTF8.GetBytes(_geoJson));

    Assert.Equal(3, zones.Count);
    Assert.Equal("Airport zone", zones[0].Name);
    Assert.Equal("First  line", zones[0].Description);
    Assert.Equal(3, zones[0].Outer.Count);
    Assert.Single(zones[0].Holes);
    Assert.Equal("Islands", zones[1].Name);
    Assert.Equal("Islands (2)", zones[2].Name);
  }

  [Theory]
  [InlineData("")]
  [InlineData("{}")]
  [InlineData("{\"type\":\"FeatureCollection\",\"features\":[]}")]
  [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[181,22],[114,22],[114,23],[181,22]]]}}]}")]
  [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[114,22],[114,22],[114,22]]]}}]}")]
  public void Parser_rejects_empty_or_unsafe_geometry(string json) {
    Assert.ThrowsAny<Exception>(() =>
        HongKongNoFlyService.Parse(Encoding.UTF8.GetBytes(json)));
  }

  [Fact]
  public async Task Fresh_valid_cache_avoids_the_network() {
    using var root = new TempDirectory();
    string cache = Path.Combine(root.Path, "nofly", "hknfz.json");
    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
    await File.WriteAllTextAsync(cache, _geoJson);
    var now = new DateTimeOffset(2026, 8, 23, 4, 0, 0, TimeSpan.Zero);
    File.SetLastWriteTimeUtc(cache, now.UtcDateTime);
    var handler = new TrackingHandler((_, _) =>
        throw new InvalidOperationException("Fresh cache must not use the network."));
    using var http = new HttpClient(handler);
    var service = new HongKongNoFlyService(http, cache, new FixedTimeProvider(now));

    HongKongNoFlyResult result = await service.LoadAsync(CancellationToken.None);

    Assert.True(result.FromCache);
    Assert.False(result.Stale);
    Assert.Equal(3, result.Zones.Count);
    Assert.Equal(0, handler.RequestCount);
  }

  [Fact]
  public async Task Live_response_is_validated_then_atomically_cached() {
    using var root = new TempDirectory();
    string cache = Path.Combine(root.Path, "nofly", "hknfz.json");
    var now = new DateTimeOffset(2026, 8, 23, 4, 0, 0, TimeSpan.Zero);
    var handler = new TrackingHandler((request, _) => {
      Assert.Equal(HongKongNoFlyService.OfficialFeed, request.RequestUri);
      return Task.FromResult(JsonResponse(_geoJson));
    });
    using var http = new HttpClient(handler);
    var service = new HongKongNoFlyService(http, cache, new FixedTimeProvider(now));

    HongKongNoFlyResult result = await service.LoadAsync(CancellationToken.None);

    Assert.False(result.FromCache);
    Assert.False(result.Stale);
    Assert.Equal(_geoJson, await File.ReadAllTextAsync(cache));
    Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cache)!, "*.partial-*"));
  }

  [Fact]
  public async Task Network_failure_uses_only_a_previously_valid_stale_cache() {
    using var root = new TempDirectory();
    string cache = Path.Combine(root.Path, "nofly", "hknfz.json");
    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
    await File.WriteAllTextAsync(cache, _geoJson);
    var now = new DateTimeOffset(2026, 8, 23, 20, 0, 0, TimeSpan.Zero);
    File.SetLastWriteTimeUtc(cache, now.AddHours(-13).UtcDateTime);
    var handler = new TrackingHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    using var http = new HttpClient(handler);
    var service = new HongKongNoFlyService(http, cache, new FixedTimeProvider(now));

    HongKongNoFlyResult result = await service.LoadAsync(CancellationToken.None);

    Assert.True(result.FromCache);
    Assert.True(result.Stale);
    Assert.Equal(3, result.Zones.Count);
    Assert.Equal(1, handler.RequestCount);
  }

  [Fact]
  public async Task Cancellation_preserves_existing_cache_and_removes_partial_files() {
    using var root = new TempDirectory();
    string cache = Path.Combine(root.Path, "nofly", "hknfz.json");
    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
    await File.WriteAllTextAsync(cache, _geoJson);
    var now = new DateTimeOffset(2026, 8, 23, 20, 0, 0, TimeSpan.Zero);
    File.SetLastWriteTimeUtc(cache, now.AddHours(-13).UtcDateTime);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new TrackingHandler(async (_, token) => {
      started.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
      throw new InvalidOperationException("unreachable");
    });
    using var http = new HttpClient(handler);
    var service = new HongKongNoFlyService(http, cache, new FixedTimeProvider(now));
    using var cancellation = new CancellationTokenSource();

    Task<HongKongNoFlyResult> load = service.LoadAsync(cancellation.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    Assert.Equal(_geoJson, await File.ReadAllTextAsync(cache));
    Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cache)!, "*.partial-*"));
  }

  [Fact]
  public async Task Concurrent_loads_share_one_download_and_the_second_reads_cache() {
    using var root = new TempDirectory();
    string cache = Path.Combine(root.Path, "nofly", "hknfz.json");
    var now = new DateTimeOffset(2026, 8, 23, 4, 0, 0, TimeSpan.Zero);
    var handler = new TrackingHandler(async (_, token) => {
      await Task.Delay(50, token);
      return JsonResponse(_geoJson);
    });
    using var http = new HttpClient(handler);
    var service = new HongKongNoFlyService(http, cache, new FixedTimeProvider(now));

    HongKongNoFlyResult[] results = await Task.WhenAll(
        service.LoadAsync(CancellationToken.None),
        service.LoadAsync(CancellationToken.None));

    Assert.Equal(1, handler.RequestCount);
    Assert.Single(results, result => !result.FromCache);
    Assert.Single(results, result => result.FromCache && !result.Stale);
  }

  [Fact]
  public void Overlay_matches_upstream_translucent_blue_and_purple_style_and_keeps_holes() {
    using var root = new TempDirectory();
    var zone = new HongKongNoFlyZone(
        "Airport", "description",
        [(22.2, 113.8), (22.2, 114.0), (22.4, 114.0)],
        [[(22.25, 113.85), (22.25, 113.9), (22.3, 113.9)]]);

    ILayer layer = Assert.IsAssignableFrom<ILayer>(
        NoFlyOverlay.BuildLayerFromDirectoryAndHongKong(root.Path, [zone]));
    GeometryFeature feature = Assert.Single(
        layer.GetFeatures(Assert.IsType<Mapsui.MRect>(layer.Extent), 1)
            .OfType<GeometryFeature>());
    var polygon = Assert.IsType<Polygon>(feature.Geometry);
    Assert.Equal(1, polygon.NumInteriorRings);
    VectorStyle style = Assert.Single(feature.Styles.OfType<VectorStyle>());
    Assert.Equal(new Color(0, 0, 255, 30), style.Fill!.Color);
    Assert.Equal(new Color(128, 0, 128, 255), style.Outline!.Color);
    Assert.Equal(2, style.Outline.Width);
  }

  [AvaloniaFact]
  public void Both_operational_maps_replace_their_no_fly_layer() {
    var flightData = new MapView();
    var planner = new FlightPlannerMap();
    var dataFirst = new MemoryLayer { Name = "data-first" };
    var dataSecond = new MemoryLayer { Name = "data-second" };
    var plannerFirst = new MemoryLayer { Name = "planner-first" };
    var plannerSecond = new MemoryLayer { Name = "planner-second" };

    flightData.SetNoFlyLayer(dataFirst);
    flightData.SetNoFlyLayer(dataSecond);
    planner.SetNoFlyLayer(plannerFirst);
    planner.SetNoFlyLayer(plannerSecond);

    Assert.DoesNotContain(dataFirst, flightData.Map.Layers);
    Assert.Contains(dataSecond, flightData.Map.Layers);
    Assert.DoesNotContain(plannerFirst, planner.Map.Layers);
    Assert.Contains(plannerSecond, planner.Map.Layers);
  }

  private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) {
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
  };

  private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }

  private sealed class TrackingHandler(
      Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
      : HttpMessageHandler {
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _requestCount);
      return response(request, cancellationToken);
    }
  }

  private sealed class TempDirectory : IDisposable {
    internal TempDirectory() => Path = Directory.CreateTempSubdirectory("mp-hknfz-").FullName;

    internal string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
  }
}
