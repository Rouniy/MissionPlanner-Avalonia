using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class FlightMapOverlayTests {
  [Theory]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL)]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_INT)]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT)]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT)]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT)]
  [InlineData(MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT)]
  public void Global_mission_frames_are_renderable(MAVLink.MAV_FRAME frame) {
    var item = Item(frame, 34.1234567, 33.7654321);

    Assert.True(MapView.TryGlobalPosition(item, out double lat, out double lng));
    Assert.Equal(34.1234567, lat, 7);
    Assert.Equal(33.7654321, lng, 7);
  }

  [Fact]
  public void Local_mission_frames_are_not_misread_as_latitude_longitude() {
    var item = Item(MAVLink.MAV_FRAME.LOCAL_NED, 34.1, 33.2);

    Assert.False(MapView.TryGlobalPosition(item, out _, out _));
  }

  [Theory]
  [InlineData(0, 0)]
  [InlineData(91, 33)]
  [InlineData(34, 181)]
  public void Invalid_global_coordinates_are_not_rendered(double lat, double lng) {
    var item = Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, lat, lng);

    Assert.False(MapView.TryGlobalPosition(item, out _, out _));
  }

  [AvaloniaFact]
  public void Flight_map_contains_operational_overlay_layers() {
    var map = new MapView();
    var names = map.Map.Layers.Select(layer => layer.Name).ToArray();

    Assert.Contains("Mission route", names);
    Assert.Contains("Mission waypoints", names);
    Assert.Contains("GeoFence", names);
    Assert.Contains("Rally points", names);
    Assert.Contains("Guided target", names);
    Assert.Contains("POI", names);
    Assert.Contains("ADS-B / AIS traffic", names);
    Assert.Contains("Camera overlap count", names);
    Assert.Contains("Camera target", names);
    Assert.Contains("Other vehicles", names);
  }

  [Fact]
  public void Oa_db_radius_builds_a_closed_red_map_polygon() {
    var feature = MapView.BuildTrafficRadius(33, 34, 25);

    var polygon = Assert.IsType<NetTopologySuite.Geometries.Polygon>(feature.Geometry);
    Assert.Equal(49, polygon.ExteriorRing.Coordinates.Length);
    Assert.Equal(polygon.ExteriorRing.Coordinates[0], polygon.ExteriorRing.Coordinates[^1]);
    Assert.Contains(feature.Styles, style => style is Mapsui.Styles.VectorStyle);
  }

  [Fact]
  public void Ais_vessels_use_a_distinct_boat_marker_style() {
    var style = MavMarker.Vessel(123);

    Assert.Equal(Mapsui.Styles.SymbolType.Rectangle, style.SymbolType);
    Assert.Equal(123, style.SymbolRotation);
  }

  [Fact]
  public void Mission_progress_uses_completed_legs_minus_current_wp_distance() {
    var items = new[] {
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          0, Item(MAVLink.MAV_FRAME.GLOBAL, 34, 33)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          1, Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, 34, 33.001)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          2, Item(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, 34, 33.002)),
      new KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>(
          3, Item(MAVLink.MAV_FRAME.LOCAL_NED, 34, 33.003)),
    };

    var info = FlightDataViewModel.CalculateMissionProgress(34, 33, items, 1, 40);

    Assert.Equal(2, info.ItemCount);
    Assert.InRange(info.TotalDistance, 180, 190);
    Assert.InRange(info.TravelledDistance, 50, 55);
  }

  private static MAVLink.mavlink_mission_item_int_t Item(
      MAVLink.MAV_FRAME frame, double lat, double lng) => new() {
        frame = (byte)frame,
        x = (int)Math.Round(lat * 1e7),
        y = (int)Math.Round(lng * 1e7),
      };
}
