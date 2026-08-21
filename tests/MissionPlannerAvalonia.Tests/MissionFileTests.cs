using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MissionPlannerAvalonia.Tests;

public class MissionFileTests {
  [AvaloniaFact]
  public async Task Save_then_Load_round_trips_waypoints() {
    var vm = new FlightPlannerViewModel();
    vm.Waypoints.Add(
        new WpRow {
          Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
          Lat = 40.1,
          Lng = 28.2,
          Alt = 120,
        }
    );
    vm.Waypoints.Add(
        new WpRow {
          Command = (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH,
          Lat = 0,
          Lng = 0,
          Alt = 0,
        }
    );

    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.waypoints");
    try {
      await vm.SaveFileAsync(path);
      Assert.True(File.Exists(path));
      var lines = File.ReadAllLines(path);
      Assert.StartsWith("QGC WPL 110", lines[0]);
      Assert.StartsWith("0\t1\t0\t16", lines[1]);

      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);

      Assert.Equal(2, loaded.Waypoints.Count);
      Assert.Equal((ushort)MAVLink.MAV_CMD.WAYPOINT, loaded.Waypoints[0].Command);
      Assert.Equal(40.1, loaded.Waypoints[0].Lat, 6);
      Assert.Equal(28.2, loaded.Waypoints[0].Lng, 6);
      Assert.Equal(120, loaded.Waypoints[0].Alt, 3);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Append_waypoint_file_skips_its_home_row() {
    var source = new FlightPlannerViewModel { HomeLat = 40, HomeLng = 28, HomeAlt = 100 };
    source.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 40.1,
      Lng = 28.1,
      Alt = 50,
    });
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.waypoints");
    try {
      await source.SaveFileAsync(path);
      var target = new FlightPlannerViewModel { HomeLat = 41, HomeLng = 29, HomeAlt = 110 };
      target.Waypoints.Add(new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.TAKEOFF,
        Lat = 41,
        Lng = 29,
        Alt = 30,
      });

      await target.LoadFileAsync(path, append: true);

      Assert.Equal(2, target.Waypoints.Count);
      Assert.Equal((ushort)MAVLink.MAV_CMD.TAKEOFF, target.Waypoints[0].Command);
      Assert.Equal((ushort)MAVLink.MAV_CMD.WAYPOINT, target.Waypoints[1].Command);
      Assert.Equal(41, target.HomeLat, 6);
      Assert.Equal(29, target.HomeLng, 6);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Waypoint_file_switches_out_of_the_fence_store() {
    var source = new FlightPlannerViewModel { HomeLat = 40, HomeLng = 28, HomeAlt = 100 };
    source.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Lat = 40.1,
      Lng = 28.1,
      Alt = 50,
    });
    string path = Path.Combine(Path.GetTempPath(), $"mp_test_{Guid.NewGuid():N}.waypoints");
    try {
      await source.SaveFileAsync(path);
      var target = new FlightPlannerViewModel { MissionType = "Fence" };
      target.SetFenceReturn(41, 29);

      await target.LoadFileAsync(path);

      Assert.Equal("Mission", target.MissionType);
      Assert.Single(target.Waypoints);
      Assert.Equal((ushort)MAVLink.MAV_CMD.WAYPOINT, target.Waypoints[0].Command);
    } finally {
      File.Delete(path);
    }
  }

  [AvaloniaFact]
  public async Task Polygon_round_trip_preserves_vertices_and_closure_is_not_duplicated() {
    var vm = new FlightPlannerViewModel();
    vm.AddPolygonPoint(40.1, 28.2);
    vm.AddPolygonPoint(40.2, 28.3);
    vm.AddPolygonPoint(40.3, 28.1);
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.poly");
    try {
      await vm.SaveFileAsync(path);
      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);
      Assert.Equal(3, loaded.DrawnPolygon.Count);
      Assert.Equal(40.1, loaded.DrawnPolygon[0].Lat, 6);
      Assert.Equal(28.2, loaded.DrawnPolygon[0].Lng, 6);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Legacy_fence_round_trip_preserves_return_and_polygon_type() {
    var vm = new FlightPlannerViewModel { MissionType = "Fence" };
    vm.SetFenceReturn(40.0, 28.0);
    vm.AddPolygonPoint(40.1, 28.1);
    vm.AddPolygonPoint(40.2, 28.2);
    vm.AddPolygonPoint(40.3, 28.1);
    vm.AddDrawnPolygonToFence(true);
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.fen");
    var planPath = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await vm.SaveFileAsync(path);
      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);
      Assert.Equal("Fence", loaded.MissionType);
      Assert.Equal(4, loaded.Waypoints.Count);
      Assert.Equal((ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT, loaded.Waypoints[0].Command);
      Assert.All(loaded.Waypoints.Skip(1), row => {
        Assert.Equal((ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, row.Command);
        Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL, row.Frame);
        Assert.Equal(3, row.P1);
      });

      await loaded.SaveFileAsync(planPath);
      var breachReturn = Assert.IsType<JArray>(
          JObject.Parse(File.ReadAllText(planPath))["geoFence"]!["breachReturn"]);
      Assert.Equal(40, breachReturn[0]!.Value<double>(), 6);
      Assert.Equal(28, breachReturn[1]!.Value<double>(), 6);
      Assert.Equal(0, breachReturn[2]!.Value<double>(), 3);

      await loaded.LoadFileAsync(path, append: true);
      Assert.Single(loaded.Waypoints, row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT);
      Assert.Equal(7, loaded.Waypoints.Count);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
      if (File.Exists(planPath)) {
        File.Delete(planPath);
      }
    }
  }

  [AvaloniaFact]
  public async Task Legacy_fence_save_uses_fence_store_when_mission_grid_is_active() {
    var vm = new FlightPlannerViewModel { MissionType = "Fence" };
    vm.SetFenceReturn(40.0, 28.0);
    vm.AddPolygonPoint(40.1, 28.1);
    vm.AddPolygonPoint(40.2, 28.2);
    vm.AddPolygonPoint(40.3, 28.1);
    vm.AddDrawnPolygonToFence(true);
    vm.MissionType = "Mission";
    vm.Waypoints.Add(new WpRow { Command = (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH });

    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.fen");
    try {
      await vm.SaveFileAsync(path);
      Assert.StartsWith("# saved by MissionPlanner-Avalonia", File.ReadAllLines(path)[0]);

      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);
      Assert.Equal(4, loaded.Waypoints.Count);
      Assert.Equal((ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT, loaded.Waypoints[0].Command);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Json_mission_round_trip_preserves_home_frame_and_commands() {
    var vm = new FlightPlannerViewModel {
      HomeLat = 40.0,
      HomeLng = 28.0,
      HomeAlt = 123,
    };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      Lat = 40.1,
      Lng = 28.1,
      Alt = 80,
    });
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.mission");
    try {
      await vm.SaveFileAsync(path);
      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);
      Assert.Equal(40.0, loaded.HomeLat, 6);
      Assert.Equal(28.0, loaded.HomeLng, 6);
      Assert.Single(loaded.Waypoints);
      Assert.Equal((ushort)MAVLink.MAV_CMD.WAYPOINT, loaded.Waypoints[0].Command);
      Assert.Equal(80, loaded.Waypoints[0].Alt, 3);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Qgc_plan_round_trip_preserves_mission_fence_and_rally_sections() {
    var vm = new FlightPlannerViewModel { HomeLat = 40, HomeLng = 28, HomeAlt = 100 };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
      Lat = 40.1,
      Lng = 28.1,
      Alt = 80,
    });

    vm.MissionType = "Fence";
    vm.AddPolygonPoint(40.2, 28.2);
    vm.AddPolygonPoint(40.3, 28.3);
    vm.AddPolygonPoint(40.4, 28.2);
    vm.AddDrawnPolygonToFence(false);
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
      P1 = 125,
      Lat = 40.25,
      Lng = 28.25,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      Lat = 40.15,
      Lng = 28.15,
      Alt = 45.5,
    });

    vm.MissionType = "Rally";
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.RALLY_POINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      Lat = 40.5,
      Lng = 28.5,
      Alt = 60,
    });

    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await vm.SaveFileAsync(path);
      string firstJson = File.ReadAllText(path);
      var breachReturn = Assert.IsType<JArray>(
          JObject.Parse(firstJson)["geoFence"]!["breachReturn"]);
      Assert.Equal(40.15, breachReturn[0]!.Value<double>(), 6);
      Assert.Equal(28.15, breachReturn[1]!.Value<double>(), 6);
      Assert.Equal(45.5, breachReturn[2]!.Value<double>(), 3);
      await vm.SaveFileAsync(path);
      Assert.Equal(firstJson, File.ReadAllText(path));

      var loaded = new FlightPlannerViewModel();
      await loaded.LoadFileAsync(path);

      Assert.Single(loaded.Waypoints);
      Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT, loaded.Waypoints[0].Frame);
      loaded.MissionType = "Fence";
      Assert.Equal(5, loaded.Waypoints.Count);
      Assert.Equal(3, loaded.Waypoints.Count(row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION));
      Assert.Contains(loaded.Waypoints, row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION && row.P1 == 125);
      var loadedReturn = Assert.Single(loaded.Waypoints, row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT);
      Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, loadedReturn.Frame);
      Assert.Equal(40.15, loadedReturn.Lat, 6);
      Assert.Equal(28.15, loadedReturn.Lng, 6);
      Assert.Equal(45.5, loadedReturn.Alt, 3);
      loaded.MissionType = "Rally";
      Assert.Single(loaded.Waypoints);
      Assert.Equal((ushort)MAVLink.MAV_CMD.RALLY_POINT, loaded.Waypoints[0].Command);
      Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, loaded.Waypoints[0].Frame);
      Assert.Equal(60, loaded.Waypoints[0].Alt, 3);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Qgc_plan_without_return_omits_breach_return() {
    var vm = new FlightPlannerViewModel();
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await vm.SaveFileAsync(path);

      var root = JObject.Parse(File.ReadAllText(path));
      Assert.Null(root["geoFence"]!["breachReturn"]);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Invalid_qgc_breach_return_is_rejected_before_planner_state_changes() {
    var source = new FlightPlannerViewModel();
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await source.SaveFileAsync(path);
      var root = JObject.Parse(File.ReadAllText(path));
      root["geoFence"]!["breachReturn"] = new JArray(40.1, 28.1);
      File.WriteAllText(path, root.ToString(Formatting.Indented));

      var target = new FlightPlannerViewModel { HomeLat = 51, HomeLng = 7, HomeAlt = 90 };
      target.Waypoints.Add(new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        Lat = 51.1,
        Lng = 7.1,
        Alt = 30,
      });
      await target.LoadFileAsync(path);

      Assert.StartsWith("Load failed: QGC Plan geoFence.breachReturn", target.Status);
      Assert.Equal(51, target.HomeLat, 6);
      Assert.Single(target.Waypoints);
      Assert.Equal(51.1, target.Waypoints[0].Lat, 6);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Malformed_qgc_geofence_is_rejected_before_planner_state_changes() {
    var source = new FlightPlannerViewModel();
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await source.SaveFileAsync(path);
      var root = JObject.Parse(File.ReadAllText(path));
      root["geoFence"] = "not an object";
      File.WriteAllText(path, root.ToString(Formatting.Indented));

      var target = new FlightPlannerViewModel();
      target.Waypoints.Add(new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        Lat = 51.1,
        Lng = 7.1,
        Alt = 30,
      });
      await target.LoadFileAsync(path);

      Assert.Equal("Load failed: QGC Plan geoFence must be an object.", target.Status);
      Assert.Single(target.Waypoints);
      Assert.Equal(51.1, target.Waypoints[0].Lat, 6);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Qgc_plan_rejects_multiple_breach_return_rows_before_writing() {
    var vm = new FlightPlannerViewModel { MissionType = "Fence" };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
      Lat = 40,
      Lng = 28,
      Alt = 30,
    });
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
      Lat = 41,
      Lng = 29,
      Alt = 40,
    });
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await vm.SaveFileAsync(path);

      Assert.StartsWith("Save failed: QGC Plan supports exactly one", vm.Status);
      Assert.False(File.Exists(path));
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Qgc_plan_rejects_absolute_breach_return_altitude_without_overwriting() {
    var vm = new FlightPlannerViewModel { MissionType = "Fence" };
    vm.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
      Lat = 40,
      Lng = 28,
      Alt = 130,
    });
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      File.WriteAllText(path, "existing file");
      await vm.SaveFileAsync(path);

      Assert.StartsWith(
          "Save failed: QGC Plan fence breach-return altitude must use a global-relative frame.",
          vm.Status);
      Assert.Equal("existing file", File.ReadAllText(path));
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }

  [AvaloniaFact]
  public async Task Appending_qgc_plan_does_not_duplicate_existing_breach_return() {
    var source = new FlightPlannerViewModel { MissionType = "Fence" };
    source.Waypoints.Add(new WpRow {
      Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      Lat = 41,
      Lng = 29,
      Alt = 40,
    });
    var path = Path.Combine(Path.GetTempPath(), $"mp_test_{System.Guid.NewGuid():N}.plan");
    try {
      await source.SaveFileAsync(path);
      var target = new FlightPlannerViewModel { MissionType = "Fence" };
      target.Waypoints.Add(new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT,
        Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        Lat = 40,
        Lng = 28,
        Alt = 30,
      });

      await target.LoadFileAsync(path, append: true);

      var returnPoint = Assert.Single(target.Waypoints, row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT);
      Assert.Equal(40, returnPoint.Lat, 6);
      Assert.Equal(28, returnPoint.Lng, 6);
      Assert.Equal(30, returnPoint.Alt, 3);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }
}
