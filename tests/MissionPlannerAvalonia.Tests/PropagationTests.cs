using System.Globalization;
using System.Threading;
using Avalonia.Headless.XUnit;
using Mapsui;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class PropagationTests {
  [Fact]
  public void Upstream_defaults_and_setting_keys_are_preserved() {
    PropagationOptions options = PropagationOptions.Default;

    Assert.Equal(5, options.ClearanceMeters);
    Assert.Equal(4, options.ResolutionPixels);
    Assert.Equal(1, options.AzimuthStepDegrees);
    Assert.Equal(1, options.ConvergenceDegrees);
    Assert.Equal(2, options.RangeKilometers);
    Assert.Equal(2, options.BaseHeightMeters);
    Assert.Equal(0.8, options.Tolerance);
    Assert.Equal(100, options.MinimumAltitude);
    Assert.Equal(400, options.MaximumAltitude);
    Assert.Equal("Propagation_Rotational", PropagationOptions.AzimuthStepKey);
    Assert.Equal("Propagation_Converge", PropagationOptions.ConvergenceKey);
    Assert.Equal("Propagation_RFmap", PropagationOptions.RfMapKey);
  }

  [Fact]
  public void Raster_keeps_north_at_the_top_and_uses_upstream_transparency() {
    PropagationOptions options = PropagationOptions.Default with {
      TerrainMap = true,
      ManualAltitudeRange = true,
      MinimumAltitude = 0,
      MaximumAltitude = 300,
      ResolutionPixels = 1,
    };
    var request = new PropagationRasterRequest(
        new MRect(-1000, -1000, 1000, 1000), 2, 2, 0, options);

    PropagationRasterResult result = PropagationService.BuildRaster(
        request,
        (lat, _) => PropagationTerrainSample.Valid(lat > 0 ? 100 : 200));

    Assert.Equal(4, result.ValidSampleCount);
    Assert.Equal(100, result.Rgba[3]);
    Assert.Equal(100, result.Rgba[11]);
    Assert.NotEqual(result.Rgba[0], result.Rgba[8]);
    Assert.NotEmpty(result.EncodePng());
  }

  [Fact]
  public void Raster_does_not_paint_missing_or_ocean_samples() {
    PropagationOptions options = PropagationOptions.Default with {
      TerrainMap = true,
      ManualAltitudeRange = true,
      MinimumAltitude = 0,
      MaximumAltitude = 200,
      ResolutionPixels = 1,
    };
    var request = new PropagationRasterRequest(new MRect(-1000, -10, 1000, 10),
        2, 1, 0, options);
    int call = 0;

    PropagationRasterResult result = PropagationService.BuildRaster(
        request, (_, _) => call++ == 0
            ? PropagationTerrainSample.Missing
            : PropagationTerrainSample.Valid(100));

    Assert.Equal(1, result.ValidSampleCount);
    Assert.Equal(1, result.MissingSampleCount);
    Assert.Equal(0, result.Rgba[3]);
    Assert.Equal(100, result.Rgba[7]);
  }

  [Fact]
  public void Stored_decimals_round_trip_on_comma_decimal_cultures() {
    var german = CultureInfo.GetCultureInfo("de-DE");

    Assert.Equal(0.8,
        PropagationOptions.ParseStoredDouble("0.8", -1, german), 6);
    Assert.Equal(0.8,
        PropagationOptions.ParseStoredDouble("0,8", -1, german), 6);
    Assert.Equal(2.5,
        PropagationOptions.ParseStoredDouble("2.5", -1, german), 6);
  }

  [Fact]
  public void Known_ocean_is_transparent_without_being_marked_unavailable() {
    PropagationOptions options = PropagationOptions.Default with {
      TerrainMap = true,
      ResolutionPixels = 1,
    };
    var request = new PropagationRasterRequest(new MRect(-10, -10, 10, 10),
        2, 2, 0, options);

    PropagationRasterResult result = PropagationService.BuildRaster(
        request, (_, _) => PropagationTerrainSample.Ocean);

    Assert.Equal(0, result.ValidSampleCount);
    Assert.Equal(0, result.MissingSampleCount);
    Assert.All(result.Rgba, value => Assert.Equal(0, value));
  }

  [Fact]
  public void Elevation_palette_uses_vehicle_minus_terrain_clearance() {
    Assert.Equal(50, PropagationService.ElevationPaletteIndex(105, 101, 5));
    Assert.Equal(203, PropagationService.ElevationPaletteIndex(105, 104, 5));
    Assert.Equal(0, PropagationService.ElevationPaletteIndex(105, 100, 5));
    Assert.Equal(254, PropagationService.ElevationPaletteIndex(105, 110, 5));
  }

  [Fact]
  public void Flat_terrain_rf_polygon_reaches_configured_range() {
    PropagationOptions options = PropagationOptions.Default with {
      RfMap = true,
      RangeKilometers = 0.1,
      AzimuthStepDegrees = 10,
      ConvergenceDegrees = 5,
    };
    var home = new PropagationPoint(34, 33, 100);

    PropagationRfResult result = PropagationService.BuildRfCoverage(
        home, 200, options, (_, _) => PropagationTerrainSample.Valid(100));

    Assert.True(result.Complete);
    Assert.Equal(result.Points[0], result.Points[^1]);
    foreach (PropagationPoint point in result.Points.Take(result.Points.Count - 1)) {
      double distance = new PointLatLngAlt(home.Lat, home.Lng)
          .GetDistance(new PointLatLngAlt(point.Lat, point.Lng));
      Assert.InRange(distance, 99, 101);
    }
  }

  [Fact]
  public void Half_degree_azimuth_option_is_honored() {
    PropagationOptions options = PropagationOptions.Default with {
      RfMap = true,
      RangeKilometers = 0.01,
      AzimuthStepDegrees = 0.5,
      ConvergenceDegrees = 15,
    };

    PropagationRfResult result = PropagationService.BuildRfCoverage(
        new PropagationPoint(34, 33, 100), 200, options,
        (_, _) => PropagationTerrainSample.Valid(100));

    Assert.True(result.Complete);
    Assert.Equal(721, result.Points.Count);
  }

  [Fact]
  public void Terrain_above_aircraft_ceiling_limits_rf_rays() {
    PropagationOptions options = PropagationOptions.Default with {
      RfMap = true,
      RangeKilometers = 0.1,
      AzimuthStepDegrees = 10,
      ConvergenceDegrees = 5,
    };
    var home = new PropagationPoint(34, 33, 100);
    var homeLla = new PointLatLngAlt(home.Lat, home.Lng);

    PropagationRfResult result = PropagationService.BuildRfCoverage(
        home, 200, options, (lat, lng) => {
          double distance = homeLla.GetDistance(new PointLatLngAlt(lat, lng));
          return PropagationTerrainSample.Valid(distance >= 45 ? 300 : 100);
        });

    Assert.True(result.Complete);
    foreach (PropagationPoint point in result.Points.Take(result.Points.Count - 1)) {
      double distance = homeLla.GetDistance(new PointLatLngAlt(point.Lat, point.Lng));
      Assert.InRange(distance, 49, 51);
    }
  }

  [Fact]
  public void Ocean_is_a_known_zero_meter_surface_for_rf() {
    PropagationOptions options = PropagationOptions.Default with {
      RfMap = true,
      RangeKilometers = 0.01,
      AzimuthStepDegrees = 10,
      ConvergenceDegrees = 5,
    };

    PropagationRfResult result = PropagationService.BuildRfCoverage(
        new PropagationPoint(34, 33, 0), 20, options,
        (_, _) => PropagationTerrainSample.Ocean);

    Assert.True(result.Complete);
    Assert.Equal(0, result.MissingSampleCount);
  }

  [Fact]
  public void Zero_convergence_is_bounded_and_missing_dem_is_not_false_coverage() {
    PropagationOptions options = PropagationOptions.Default with {
      RfMap = true,
      RangeKilometers = 0.01,
      AzimuthStepDegrees = 10,
      ConvergenceDegrees = 0,
    };
    var home = new PropagationPoint(34, 33, 100);

    PropagationRfResult bounded = PropagationService.BuildRfCoverage(
        home, 200, options, (_, _) => PropagationTerrainSample.Valid(100));
    PropagationRfResult missing = PropagationService.BuildRfCoverage(
        home, 200, options, (_, _) => PropagationTerrainSample.Missing);

    Assert.True(bounded.Complete);
    Assert.False(missing.Complete);
    Assert.Empty(missing.Points);
    Assert.True(missing.MissingSampleCount > 0);
  }

  [Fact]
  public void Distance_circle_is_closed_at_requested_radius() {
    var center = new PropagationPoint(34, 33);

    IReadOnlyList<PropagationPoint> points =
        PropagationService.BuildCircle(center, 250, 36);

    Assert.Equal(37, points.Count);
    Assert.Equal(points[0], points[^1]);
    double distance = new PointLatLngAlt(center.Lat, center.Lng)
        .GetDistance(new PointLatLngAlt(points[9].Lat, points[9].Lng));
    Assert.InRange(distance, 249, 251);
  }

  [Fact]
  public void Builders_observe_cancellation_and_altitude_keys_ignore_small_jitter() {
    PropagationOptions options = PropagationOptions.Default with {
      TerrainMap = true,
      ResolutionPixels = 1,
    };
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(() => PropagationService.BuildRaster(
        new PropagationRasterRequest(new MRect(-10, -10, 10, 10), 10, 10, 0, options),
        (_, _) => PropagationTerrainSample.Valid(100), cancellation.Token));
    Assert.Throws<OperationCanceledException>(() => PropagationService.BuildRfCoverage(
        new PropagationPoint(34, 33, 100), 200,
        options with { RfMap = true, RangeKilometers = 1 },
        (_, _) => PropagationTerrainSample.Valid(100), cancellation.Token));
    Assert.Equal(100,
        PropagationOverlayController.QuantizeAltitude(100.24));
    Assert.Equal(100.5,
        PropagationOverlayController.QuantizeAltitude(100.26));
  }

  [AvaloniaFact]
  public void Both_maps_and_settings_window_expose_propagation_contract() {
    var flightDataMap = new MapView();
    var plannerMap = new FlightPlannerMap();
    var window = new PropagationSettingsWindow();

    foreach (var map in new[] { flightDataMap.Map, plannerMap.Map }) {
      string[] names = map.Layers.Select(layer => layer.Name).ToArray();
      Assert.Contains("Propagation elevation / terrain", names);
      Assert.Contains("RF propagation", names);
      Assert.Contains("Propagation distance left", names);
    }
    Assert.Equal("RF Propagation Settings", window.Title);
    Assert.NotNull(window.Content);
  }
}
