using Avalonia.Headless.XUnit;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

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
    Assert.Equal(new Color(255, 0, 0, 25), style.Fill?.Color);

    var center = SphericalMercator.FromLonLat(airport.Lng, airport.Lat);
    double projectedRadius = polygon.ExteriorRing.Coordinates[0].X - center.x;
    double physicalRadius = projectedRadius * Math.Cos(airport.Lat * Math.PI / 180);
    Assert.InRange(physicalRadius, 8999.9, 9000.1);
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
      Assert.Equal(AirportService.MaximumVisibleResolution,
          map.Layers.ElementAt(airport).MaxVisible, 8);
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
