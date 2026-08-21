using System.IO.Compression;
using System.Text;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class PlannerPortParityTests {
  [Theory]
  [InlineData("50S", -50)]
  [InlineData("11N", 11)]
  [InlineData("-55", -55)]
  [InlineData(" 33 n ", 33)]
  public void Parses_upstream_style_utm_zones(string text, int expected) {
    Assert.True(Geo.TryParseUtmZone(text, out int zone));
    Assert.Equal(expected, zone);
  }

  [Theory]
  [InlineData("")]
  [InlineData("0N")]
  [InlineData("61S")]
  [InlineData("north")]
  public void Rejects_invalid_utm_zones(string text) {
    Assert.False(Geo.TryParseUtmZone(text, out _));
  }

  [Fact]
  public void Offsets_a_drawn_polygon_in_projected_metres() {
    var polygon = new[] {
      new PointLatLngAlt(-35.364, 149.164),
      new PointLatLngAlt(-35.364, 149.166),
      new PointLatLngAlt(-35.362, 149.166),
      new PointLatLngAlt(-35.362, 149.164),
    };

    var expanded = PlannerGeometry.OffsetPolygon(polygon, 50);

    Assert.True(expanded.Count >= 3);
    var center = new PointLatLngAlt(-35.363, 149.165);
    Assert.True(expanded.Max(point => point.GetDistance(center)) >
        polygon.Max(point => point.GetDistance(center)) + 35);
  }

  [Fact]
  public void Reads_kml_route_and_poi_without_winforms() {
    const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
          <Placemark><name>Camera</name><Point><coordinates>149.1,-35.1,42</coordinates></Point></Placemark>
          <Placemark><LineString><coordinates>149.2,-35.2,50 149.3,-35.3,60</coordinates></LineString></Placemark>
        </Document></kml>
        """;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

    KmlMissionContent content = KmlMissionReader.Parse(stream);

    Assert.Equal(2, content.Route.Count);
    Assert.Equal(50, content.Route[0].Alt);
    Assert.Equal("Camera", Assert.Single(content.Pois).Name);
    Assert.Equal(3, content.Overlay.Count);
  }

  [Fact]
  public void Reads_a_kml_document_from_a_kmz_archive() {
    string path = Path.Combine(Path.GetTempPath(), $"mp_kmz_{Guid.NewGuid():N}.kmz");
    try {
      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
        using var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open());
        writer.Write("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><LineString>
            <coordinates>28.1,40.1,25 28.2,40.2,30</coordinates>
            </LineString></Placemark></kml>
            """);
      }

      KmlMissionContent content = KmlMissionReader.Read(path);

      Assert.Equal(2, content.Route.Count);
      Assert.Equal(30, content.Route[1].Alt);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Default_altitude_frame_round_trips_and_absolute_rows_are_detected() {
    byte absolute = FlightPlannerViewModel.FrameId("Absolute");

    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL, absolute);
    Assert.Equal("Absolute", FlightPlannerViewModel.FrameName(absolute));
    Assert.True(FlightPlannerViewModel.ContainsAbsoluteAltitude(
        new[] { new WpRow { Frame = absolute } }));
    Assert.False(FlightPlannerViewModel.ContainsAbsoluteAltitude(
        new[] { new WpRow { FrameName = "Terrain" } }));
  }

  [Theory]
  [InlineData(3)]
  [InlineData(12)]
  [InlineData(20)]
  public void Flight_data_map_zoom_persistence_round_trips(int level) {
    double resolution = 156543.03392804097 / Math.Pow(2, level);

    Assert.Equal(level, MapView.ZoomLevelForResolution(resolution), 8);
  }

  [Fact]
  public void Bing_selector_uses_a_quadkey_tile_source_instead_of_esri() {
    string url = MapTileSourceFactory.UrlTemplateFor("BingSatelliteMap");

    Assert.Contains("virtualearth.net", url, StringComparison.Ordinal);
    Assert.Contains("{quadkey}", url, StringComparison.Ordinal);
    Assert.DoesNotContain("arcgisonline", url, StringComparison.Ordinal);
  }

  [Fact]
  public void Modify_alt_matches_upstream_delta_and_multiplier_semantics() {
    var rows = new[] { new WpRow { Alt = 10 }, new WpRow { Alt = 20 } };

    Assert.True(FlightPlannerViewModel.TryModifyAltitudes(rows, "20", 2));
    Assert.Equal(new[] { 20d, 30d }, rows.Select(row => row.Alt));
    Assert.True(FlightPlannerViewModel.TryModifyAltitudes(rows, "*2", 2));
    Assert.Equal(new[] { 40d, 60d }, rows.Select(row => row.Alt));
    Assert.False(FlightPlannerViewModel.TryModifyAltitudes(rows, "bad", 2));
  }

  [Fact]
  public void Planner_altitudes_convert_only_at_the_display_boundary() {
    Assert.Equal(328.084, FlightPlannerViewModel.ToDisplayAltitude(100, 3.28084), 5);
    Assert.Equal(100, FlightPlannerViewModel.FromDisplayAltitude(328.084, 3.28084), 5);
  }

  [Fact]
  public void Planner_route_excludes_roi_and_radius_display_units_convert_to_metres() {
    Assert.True(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.WAYPOINT));
    Assert.True(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.SPLINE_WAYPOINT));
    Assert.False(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.ROI));
    Assert.False(MissionRoute.IsNavigation((ushort)MAVLink.MAV_CMD.DO_SET_ROI));
    Assert.Equal(100, FlightPlannerMap.RadiusInMeters(328.084, 3.28084), 5);
  }

  [Fact]
  public void Auto_wp_text_uses_a_cross_platform_vector_path() {
    IReadOnlyList<(double X, double Y)> points = PlannerTextGeometry.Create("MP", 5, 25);

    Assert.True(points.Count > 10);
    Assert.True(points.Max(point => point.X) - points.Min(point => point.X) > 1);
    Assert.True(points.Max(point => point.Y) - points.Min(point => point.Y) > 1);
  }

  [Fact]
  public void Tile_prefetch_enumerates_visible_area_and_a_route_corridor() {
    var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(149.16, -35.36);
    var area = new BruTile.Extent(x - 500, y - 500, x + 500, y + 500);

    IReadOnlyList<BruTile.TileInfo> areaTiles = MapTileSourceFactory.AreaTiles(area, 12, 13);
    IReadOnlyList<BruTile.TileInfo> pathTiles = MapTileSourceFactory.PathTiles(
        new[] { (-35.36, 149.16), (-35.30, 149.25) }, 12, 13);

    Assert.NotEmpty(areaTiles);
    Assert.NotEmpty(pathTiles);
    Assert.Equal(areaTiles.Count, areaTiles.Select(tile => tile.Index).Distinct().Count());
    Assert.Equal(pathTiles.Count, pathTiles.Select(tile => tile.Index).Distinct().Count());
  }

  [Fact]
  public void Survey_uses_the_drawn_polygon_before_mission_waypoints() {
    var vm = new FlightPlannerViewModel { DefaultAlt = 75 };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 10,
      Lng = 20,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 11,
      Lng = 21,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 12,
      Lng = 22,
    });
    vm.AddPolygonPoint(-35.36, 149.16);
    vm.AddPolygonPoint(-35.36, 149.17);
    vm.AddPolygonPoint(-35.35, 149.17);

    var area = Assert.IsType<ValueTuple<List<PointLatLngAlt>, PointLatLngAlt>>(
        vm.BuildSurveyArea());

    Assert.Equal(3, area.Item1.Count);
    Assert.Equal(-35.36, area.Item1[0].Lat, 6);
    Assert.Equal(149.16, area.Item1[0].Lng, 6);
    Assert.All(area.Item1, point => Assert.Equal(75, point.Alt));
  }

  [Fact]
  public void Survey_keeps_mission_outline_as_a_compatibility_fallback() {
    var vm = new FlightPlannerViewModel { DefaultAlt = 60 };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 1,
      Lng = 2,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 3,
      Lng = 4,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 5,
      Lng = 6,
    });

    var area = Assert.IsType<ValueTuple<List<PointLatLngAlt>, PointLatLngAlt>>(
        vm.BuildSurveyArea());

    Assert.Equal(new[] { 1d, 3d, 5d }, area.Item1.Select(point => point.Lat));
    Assert.All(area.Item1, point => Assert.Equal(60, point.Alt));
  }

  [Fact]
  public void Legacy_fence_transfer_keeps_return_point_and_closes_polygon() {
    var rows = new[] {
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat = 1, Lng = 2 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 3, Lng = 4 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 5, Lng = 6 },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, Lat = 7, Lng = 8 },
    };

    var transfer = FlightPlannerViewModel.BuildLegacyFenceTransfer(rows);

    Assert.Equal(5, transfer.Count);
    Assert.Equal((1d, 2d), (transfer[0].Lat, transfer[0].Lng));
    Assert.Equal((3d, 4d), (transfer[1].Lat, transfer[1].Lng));
    Assert.Equal((3d, 4d), (transfer[^1].Lat, transfer[^1].Lng));
  }

  [Fact]
  public void Legacy_fence_rejects_geometry_it_cannot_represent() {
    var rows = new[] {
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION },
      new WpRow { Command = (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION },
    };

    Assert.Throws<InvalidOperationException>(() =>
        FlightPlannerViewModel.BuildLegacyFenceTransfer(rows));
  }
}
