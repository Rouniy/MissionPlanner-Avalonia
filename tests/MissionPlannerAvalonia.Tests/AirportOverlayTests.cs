using Avalonia.Headless.XUnit;
using Mapsui;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public class AirportOverlayTests {
  [Theory]
  [InlineData(34.8751, 33.6249, AirportService.DefaultRadiusMeters)]
  [InlineData(-33.9461, 151.1770, AirportService.AustraliaRadiusMeters)]
  [InlineData(-10, 151, AirportService.DefaultRadiusMeters)]
  [InlineData(-33, 109, AirportService.DefaultRadiusMeters)]
  [InlineData(-33, 180, AirportService.DefaultRadiusMeters)]
  public void Airport_radius_matches_upstream_region_rules(
      double lat, double lng, int expected) {
    Assert.Equal(expected, AirportService.RadiusFor(lat, lng));
  }

  [Fact]
  public void Airport_feature_is_a_closed_translucent_circle_without_outline() {
    var airport = new AirportMapItem(34.8751, 33.6249, "Larnaca", 9000);

    GeometryFeature feature = AirportOverlayController.BuildRadiusFeature(airport);

    var polygon = Assert.IsType<Polygon>(feature.Geometry);
    Assert.Equal(65, polygon.ExteriorRing.Coordinates.Length);
    Assert.Equal(polygon.ExteriorRing.Coordinates[0], polygon.ExteriorRing.Coordinates[^1]);
    var style = Assert.IsType<VectorStyle>(Assert.Single(feature.Styles));
    Assert.Null(style.Line);
    Assert.Null(style.Outline);
    Assert.NotNull(style.Fill);
    Color fill = style.Fill.Color!.Value;
    Assert.Equal(1, style.Opacity);
    Assert.Equal(25, fill.A);
    Assert.Equal(255, fill.R);
    Assert.Equal(0, fill.G);
    Assert.Equal(0, fill.B);

    var center = SphericalMercator.FromLonLat(airport.Lng, airport.Lat);
    double projectedRadius = polygon.ExteriorRing.Coordinates[0].X - center.x;
    double physicalRadius = projectedRadius * Math.Cos(airport.Lat * Math.PI / 180);
    Assert.InRange(physicalRadius, 8999.9, 9000.1);
  }

  [Fact]
  public void Airport_circle_preserves_the_lower_layer_through_translucent_red() {
    var airport = new AirportMapItem(34.8751, 33.6249, "Larnaca", 9000);
    GeometryFeature feature = AirportOverlayController.BuildRadiusFeature(airport);
    var layer = new Mapsui.Layers.MemoryLayer { Features = [feature], Style = null };
    MRect extent = Assert.IsType<MRect>(feature.Extent);
    var viewport = new Viewport(extent.Centroid.X, extent.Centroid.Y,
        extent.Width / 80, 0, 100, 100);
    var renderer = new MapRenderer();
    var renderService = new RenderService();
    Color lowerLayerColor = Color.FromArgb(255, 20, 100, 220);

    using Stream png = renderer.RenderToBitmapStream(viewport, [layer], renderService,
        lowerLayerColor, 1, [], RenderFormat.Png, 100);
    png.Position = 0;
    using SKBitmap bitmap = Assert.IsType<SKBitmap>(SKBitmap.Decode(png));
    SKColor center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    SKColor outside = bitmap.GetPixel(5, 5);

    Assert.Equal(new SKColor(20, 100, 220), outside);
    Assert.Equal(255, center.Alpha);
    // Source-over at alpha 25/255 should retain about 90% of the lower-layer color.
    Assert.InRange(center.Red, 41, 45);
    Assert.InRange(center.Green, 88, 92);
    Assert.InRange(center.Blue, 196, 200);
  }

  [AvaloniaFact]
  public void Both_operational_maps_contain_an_airport_layer_above_propagation() {
    var flightData = new MapView();
    var planner = new FlightPlannerMap();

    foreach (var map in new[] { flightData.Map, planner.Map }) {
      string[] names = map.Layers.Select(layer => layer.Name).ToArray();
      int airport = Array.IndexOf(names, "Airports");
      int propagationStatus = Array.IndexOf(names, "Propagation status / scale");
      Assert.True(propagationStatus >= 0);
      Assert.True(airport > propagationStatus);
      var airportLayer = map.Layers.ElementAt(airport);
      Assert.Null(airportLayer.Style);
      Assert.Equal(AirportService.MaximumVisibleResolution, airportLayer.MaxVisible, 8);
    }
  }

  [Fact]
  public async Task Pinned_official_database_is_packaged_and_queryable() {
    Assert.True(File.Exists(AirportService.DatabasePath), AirportService.DatabasePath);

    AirportLoadResult result = await AirportService.EnsureLoadedAsync();

    Assert.True(result.Available, result.Error);
    Assert.InRange(result.Count, 1000, 10000);
    IReadOnlyList<AirportMapItem> nearby = AirportService.GetNearby(34.8751, 33.6249);
    Assert.Contains(nearby,
        airport => airport.Name.Contains("Larnaca International Airport",
            StringComparison.Ordinal));
  }
}
