using System.Text;
using Avalonia.Headless.XUnit;
using DotSpatial.Projections;
using Mapsui;
using Mapsui.Nts;
using Mapsui.Styles;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using netDxf;
using netDxf.Entities;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace MissionPlannerAvalonia.Tests;

public sealed class PlannerMapImportTests {
  [AvaloniaFact]
  public async Task Flight_planner_loads_shapefile_points_as_waypoints() {
    await WithDirectoryAsync(async directory => {
      string path = Path.Combine(directory, "mission.shp");
      WriteFeatures(path, [
        PointFeature(149.1, -35.1, 75, ("id", 1)),
        PointFeature(149.2, -35.2, 80, ("id", 2)),
      ]);
      using var viewModel = new FlightPlannerViewModel();

      await viewModel.LoadFileAsync(path);

      Assert.Equal(2, viewModel.Waypoints.Count);
      Assert.Equal(-35.1, viewModel.Waypoints[0].Lat, 6);
      Assert.Equal(149.1, viewModel.Waypoints[0].Lng, 6);
      Assert.Contains("Loaded 2 SHP waypoint", viewModel.Status);
    });
  }

  [AvaloniaFact]
  public async Task Flight_planner_appends_shapefile_points_to_existing_mission() {
    await WithDirectoryAsync(async directory => {
      string path = Path.Combine(directory, "append.shp");
      WriteFeatures(path, [PointFeature(149.1, -35.1, 75, ("id", 1))]);
      using var viewModel = new FlightPlannerViewModel();
      viewModel.AddWaypointAt(-35, 149);

      await viewModel.LoadFileAsync(path, append: true);

      Assert.Equal(2, viewModel.Waypoints.Count);
      Assert.Equal(-35, viewModel.Waypoints[0].Lat);
      Assert.Equal(-35.1, viewModel.Waypoints[1].Lat, 6);
      Assert.Contains("Appended 1 SHP waypoint", viewModel.Status);
    });
  }

  [Fact]
  public void Shapefile_mission_uses_y_as_latitude_and_sorts_numeric_wp() {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "mission.shp");
      WriteFeatures(path, [
        PointFeature(149.20, -35.20, 12, ("wp", 20), ("ELEVATION", 120)),
        PointFeature(149.10, -35.10, 13, ("wp", 3), ("ELEVATION", 130)),
      ]);

      ShapefileMissionImport result = ShapefileImportService.ReadMission(path);

      Assert.True(result.SortedByWaypointAttribute);
      Assert.Equal(2, result.Points.Count);
      Assert.Equal(-35.10, result.Points[0].Lat, 6);
      Assert.Equal(149.10, result.Points[0].Lng, 6);
      Assert.Equal(130, result.Points[0].Alt);
      Assert.Equal(3, result.Points[0].Order);
    });
  }

  [Fact]
  public void Shapefile_mission_altitude_precedence_matches_upstream() {
    WithDirectory(directory => {
      string bothPath = Path.Combine(directory, "both.shp");
      WriteFeatures(bothPath, [
        PointFeature(30, 40, 7, ("ELEVATION", 100), ("alt", 200)),
      ]);
      string altPath = Path.Combine(directory, "alt.shp");
      WriteFeatures(altPath, [PointFeature(30, 40, 7, ("alt", 200))]);
      string zPath = Path.Combine(directory, "z.shp");
      WriteFeatures(zPath, [PointFeature(30, 40, 7, ("id", 1))]);
      string elevationSentinelPath = Path.Combine(directory, "sentinel.shp");
      WriteFeatures(elevationSentinelPath, [
        PointFeature(30, 40, 7, ("ELEVATION", -1), ("alt", 200)),
      ]);

      Assert.Equal(100, Assert.Single(
          ShapefileImportService.ReadMission(bothPath).Points).Alt);
      Assert.Equal(200, Assert.Single(
          ShapefileImportService.ReadMission(altPath).Points).Alt);
      Assert.Equal(7, Assert.Single(
          ShapefileImportService.ReadMission(zPath).Points).Alt);
      Assert.Equal(200, Assert.Single(
          ShapefileImportService.ReadMission(elevationSentinelPath).Points).Alt);
    });
  }

  [Theory]
  [InlineData("1252")]
  [InlineData("ANSI 1252")]
  [InlineData("ISO 88591")]
  [InlineData("not-a-real-code-page")]
  public void Shapefile_cpg_variants_do_not_abort_geometry_import(string cpg) {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "encoding.shp");
      WriteFeatures(path, [PointFeature(30, 40, 7, ("id", 1))]);
      string cpgPath = ShapefileImportService.FindSidecar(path, ".cpg")
          ?? Path.ChangeExtension(path, ".CPG");
      File.WriteAllText(cpgPath, cpg);

      ImportedMissionPoint point = Assert.Single(
          ShapefileImportService.ReadMission(path).Points);

      Assert.Equal(40, point.Lat);
      Assert.Equal(30, point.Lng);
    });
  }

  [Fact]
  public void Shapefile_reads_case_insensitive_sidecars_and_reprojects_prj() {
    WithDirectory(directory => {
      const double expectedLng = 15.2;
      const double expectedLat = 47.1;
      ProjectionInfo wgs84 = KnownCoordinateSystems.Geographic.World.WGS1984;
      ProjectionInfo utm = KnownCoordinateSystems.Projected.UtmWgs1984.WGS1984UTMZone33N;
      double[] xy = { expectedLng, expectedLat };
      double[] z = { 25d };
      Reproject.ReprojectPoints(xy, z, wgs84, utm, 0, 1);
      string path = Path.Combine(directory, "projected.shp");
      WriteFeatures(path, [PointFeature(xy[0], xy[1], z[0], ("id", 1))],
          utm.ToEsriString());
      RenameExtension(path, ".dbf", ".DBF");
      RenameExtension(path, ".prj", ".PRJ");

      ShapefileMissionImport result = ShapefileImportService.ReadMission(path);

      ImportedMissionPoint point = Assert.Single(result.Points);
      Assert.Equal(expectedLat, point.Lat, 5);
      Assert.Equal(expectedLng, point.Lng, 5);
      Assert.NotNull(result.ProjectionName);
    });
  }

  [Fact]
  public void Shapefile_polygon_removes_each_closing_vertex() {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "polygon.shp");
      var factory = new GeometryFactory();
      var attributes = new AttributesTable { { "id", 1 } };
      var polygon = factory.CreatePolygon([
        new Coordinate(30, 40), new Coordinate(31, 40),
        new Coordinate(31, 41), new Coordinate(30, 40),
      ]);
      WriteFeatures(path, [new Feature(polygon, attributes)]);

      ShapefilePolygonImport result = ShapefileImportService.ReadPolygon(path);

      Assert.Equal(1, result.FeatureCount);
      Assert.Equal(3, result.Points.Count);
      Assert.Equal(40, result.Points[0].Lat);
      Assert.Equal(30, result.Points[0].Lng);
    });
  }

  [Fact]
  public void Shapefile_overlay_preserves_polygon_shell_holes_without_dbf() {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "overlay.shp");
      var factory = new GeometryFactory();
      var shell = factory.CreateLinearRing([
        new Coordinate(30, 40), new Coordinate(30, 42),
        new Coordinate(32, 42), new Coordinate(32, 40), new Coordinate(30, 40),
      ]);
      var hole = factory.CreateLinearRing([
        new Coordinate(30.5, 40.5), new Coordinate(31.5, 40.5),
        new Coordinate(31.5, 41.5), new Coordinate(30.5, 41.5),
        new Coordinate(30.5, 40.5),
      ]);
      WriteFeatures(path, [new Feature(factory.CreatePolygon(shell, [hole]),
          new AttributesTable { { "id", 1 } })]);
      File.Delete(Assert.IsType<string>(
          ShapefileImportService.FindSidecar(path, ".dbf")));

      ImportedMapOverlay overlay = ShapefileImportService.ReadOverlay(path);

      Assert.Equal(2, overlay.Routes.Count);
      Assert.All(overlay.Routes, route => Assert.True(route.Closed));
      Assert.Contains(overlay.Routes, route => route.Name.Contains("hole1"));
      Assert.Empty(overlay.Markers);
    });
  }

  [Fact]
  public void Dxf_overlay_reads_supported_visible_entities_and_preserves_colors() {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "overlay.dxf");
      var document = new DxfDocument();
      document.AddEntity(new Line(new Vector2(30, 40), new Vector2(30.1, 40.1)) {
        Color = new AciColor(255, 0, 0),
      });
      document.AddEntity(new Polyline([
        new Vector3(31, 41, 1), new Vector3(31.1, 41.1, 2),
      ]) { Color = new AciColor(0, 255, 0) });
      document.AddEntity(new LwPolyline([
        new Vector2(32, 42), new Vector2(32.1, 42.1),
      ]) { Color = new AciColor(0, 0, 255) });
      document.AddEntity(new MLine([
        new Vector2(33, 43), new Vector2(33.1, 43.1),
      ]) { Color = new AciColor(240, 120, 60) });
      document.AddEntity(new Line(new Vector2(34, 44), new Vector2(34.1, 44.1)) {
        Color = new AciColor(1, 2, 3),
        IsVisible = false,
      });
      Assert.True(document.Save(path));

      ImportedMapOverlay overlay = DxfOverlayReader.Read(path);

      Assert.Equal(4, overlay.Routes.Count);
      Assert.DoesNotContain(overlay.Routes,
          route => route.Color == new ImportedMapColor(1, 2, 3));
      Assert.Contains(overlay.Routes,
          route => route.Color == new ImportedMapColor(255, 0, 0));
      Assert.Contains(overlay.Routes,
          route => route.Color == new ImportedMapColor(0, 255, 0));
      Assert.Contains(overlay.Routes,
          route => route.Color == new ImportedMapColor(0, 0, 255));
      Assert.Contains(overlay.Routes,
          route => route.Color == new ImportedMapColor(240, 120, 60));
      ImportedGeoPoint first = overlay.Routes[0].Points[0];
      Assert.Equal(40, first.Lat, 6);
      Assert.Equal(30, first.Lng, 6);
    });
  }

  [Fact]
  public void Dxf_overlay_validates_and_converts_signed_utm_zone() {
    Assert.Throws<ArgumentOutOfRangeException>(() => DxfOverlayReader.ValidateZone(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => DxfOverlayReader.ValidateZone(61));
    Assert.Throws<ArgumentOutOfRangeException>(() => DxfOverlayReader.ValidateZone(-61));
    DxfOverlayReader.ValidateZone(-60);

    WithDirectory(directory => {
      string path = Path.Combine(directory, "utm.dxf");
      var document = new DxfDocument();
      document.AddEntity(new Line(
          new Vector2(500000, 4649776.22482),
          new Vector2(500100, 4649876.22482)));
      Assert.True(document.Save(path));

      ImportedGeoPoint point = Assert.Single(
          DxfOverlayReader.Read(path, 32).Routes).Points[0];

      Assert.Equal(42, point.Lat, 4);
      Assert.Equal(9, point.Lng, 4);
    });
  }

  [Fact]
  public void Dxf_negative_utm_zone_uses_southern_hemisphere() {
    WithDirectory(directory => {
      string path = Path.Combine(directory, "utm-south.dxf");
      var document = new DxfDocument();
      document.AddEntity(new Line(
          new Vector2(500000, 6200000),
          new Vector2(500100, 6200100)));
      Assert.True(document.Save(path));

      ImportedGeoPoint point = Assert.Single(
          DxfOverlayReader.Read(path, -55).Routes).Points[0];

      Assert.InRange(point.Lat, -35, -34);
      Assert.Equal(147, point.Lng, 4);
    });
  }

  [AvaloniaFact]
  public void Flight_planner_map_renders_imported_routes_and_markers() {
    var map = new FlightPlannerMap();
    map.ShowMapOverlay(new ImportedMapOverlay(
        [new ImportedOverlayRoute("route", [
          new ImportedGeoPoint(40, 30), new ImportedGeoPoint(40.1, 30.1),
        ], new ImportedMapColor(10, 20, 30, 40), Width: 7)],
        [new ImportedOverlayMarker("point", new ImportedGeoPoint(40.2, 30.2),
            ShowLabel: true)]));

    Mapsui.Layers.ILayer layer = Assert.Single(map.Map.Layers,
        layer => layer.Name == "ImportedMapOverlay");
    MRect extent = Assert.IsType<MRect>(layer.Extent);
    Mapsui.IFeature[] features = layer.GetFeatures(extent, 1).ToArray();
    Assert.Equal(2, features.Length);
    GeometryFeature route = Assert.Single(features.OfType<GeometryFeature>());
    VectorStyle routeStyle = Assert.Single(route.Styles.OfType<VectorStyle>());
    Assert.Equal(7, routeStyle.Line!.Width);
    Assert.Equal(new Color(10, 20, 30, 40), routeStyle.Line.Color);
    Mapsui.Layers.PointFeature marker = Assert.Single(
        features.OfType<Mapsui.Layers.PointFeature>());
    Assert.Contains(marker.Styles, style => style is LabelStyle);
  }

  [AvaloniaFact]
  public void Flight_planner_map_renders_imported_raster_in_geographic_extent() {
    byte[] png;
    using (var bitmap = new SkiaSharp.SKBitmap(2, 2))
    using (SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap))
    using (SkiaSharp.SKData encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)) {
      png = encoded.ToArray();
    }
    var map = new FlightPlannerMap();
    map.ShowMapOverlay(new ImportedMapOverlay(
        Array.Empty<ImportedOverlayRoute>(), Array.Empty<ImportedOverlayMarker>(),
        [new ImportedOverlayRaster("image", png, 41, 40, 32, 30)]));

    Mapsui.Layers.ILayer layer = Assert.Single(map.Map.Layers,
        candidate => candidate.Name == "ImportedMapRaster");
    MRect extent = Assert.IsType<MRect>(layer.Extent);
    Mapsui.Layers.RasterFeature feature = Assert.Single(
        layer.GetFeatures(extent, 1).OfType<Mapsui.Layers.RasterFeature>());
    MRect rasterExtent = Assert.IsType<MRect>(feature.Extent);
    var southWest = Mapsui.Projections.SphericalMercator.FromLonLat(30, 40);
    var northEast = Mapsui.Projections.SphericalMercator.FromLonLat(32, 41);
    Assert.Equal(southWest.x, rasterExtent.MinX, 5);
    Assert.Equal(southWest.y, rasterExtent.MinY, 5);
    Assert.Equal(northEast.x, rasterExtent.MaxX, 5);
    Assert.Equal(northEast.y, rasterExtent.MaxY, 5);
  }

  private static Feature PointFeature(double x, double y, double z,
                                      params (string Name, object Value)[] values) {
    var attributes = new AttributesTable();
    foreach (var (name, value) in values) {
      attributes.Add(name, value);
    }
    return new Feature(new NetTopologySuite.Geometries.Point(
        new CoordinateZ(x, y, z)), attributes);
  }

  private static void WriteFeatures(string path, IReadOnlyList<Feature> features,
                                    string? projection = null) =>
      Shapefile.WriteAllFeatures(features, path, projection, Encoding.UTF8);

  private static void RenameExtension(string shpPath, string oldExtension,
                                      string newExtension) {
    string source = Path.ChangeExtension(shpPath, oldExtension);
    File.Move(source, Path.ChangeExtension(shpPath, newExtension));
  }

  private static void WithDirectory(Action<string> action) {
    string directory = Path.Combine(Path.GetTempPath(), $"mp-map-import-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try {
      action(directory);
    } finally {
      Directory.Delete(directory, true);
    }
  }

  private static async Task WithDirectoryAsync(Func<string, Task> action) {
    string directory = Path.Combine(Path.GetTempPath(), $"mp-map-import-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try {
      await action(directory);
    } finally {
      Directory.Delete(directory, true);
    }
  }
}
