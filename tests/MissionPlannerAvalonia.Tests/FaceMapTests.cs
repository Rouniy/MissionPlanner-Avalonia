using System.Xml;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class FaceMapTests {
  [Fact]
  public void Collinear_face_path_stays_local_and_preserves_official_tags() {
    List<PointLatLngAlt> result = FaceMapGeometry.Create(StraightPath(),
        GeometryOptions(followPathHome: true));

    Assert.Equal(20, result.Count);
    Assert.Equal(new[] { "S", "SM", "M", "ME", "E" },
        result.Take(5).Select(point => point.Tag?.ToString()));
    Assert.All(result, point => {
      Assert.InRange(point.Lat, 34.9, 35.1);
      Assert.InRange(point.Lng, 32.9, 33.1);
      Assert.True(double.IsFinite(point.Alt));
    });
    Assert.Equal(5, result[0].Alt, 6);
    Assert.Equal(7, result[5].Alt, 6);
    Assert.Equal(9, result[10].Alt, 6);
    Assert.All(result.TakeLast(5), point => Assert.Equal("R", point.Tag));
  }

  [Fact]
  public void Multi_bench_geometry_adds_vertical_transition_and_absolute_offset() {
    var options = GeometryOptions(followPathHome: false) with {
      BenchCount = 2,
      AltitudeOffset = 420,
      BermDepth = 8,
    };

    List<PointLatLngAlt> result = FaceMapGeometry.Create(BentPath(), options);

    Assert.NotEmpty(result);
    Assert.All(result, point => Assert.True(point.Alt >= 425));
    Assert.True(result.Count(point => point.Tag?.ToString() == "S") > 6);
    PointLatLngAlt transition = result.First(point =>
        point.Tag?.ToString() == "S" && point.Alt >= 435);
    Assert.InRange(transition.Lat, 34.9, 35.1);
    Assert.InRange(transition.Lng, 32.9, 33.1);
  }

  [Fact]
  public void Geometry_rejects_an_unbounded_mission_before_allocating_it() {
    var options = GeometryOptions(followPathHome: false) with { BenchCount = 10_000 };

    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        FaceMapGeometry.Create(StraightPath(), options));

    Assert.Contains("reduce benches", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Face_map_mission_contains_entry_gimbal_yaw_trigger_and_finish_commands() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerDistance) with {
      UseSpeed = true,
      FlyingSpeed = 4.5,
      AddTakeoff = true,
      FinishAction = FaceMapMissionBuilder.FinishRtl,
      StopTriggerAtStripEnds = true,
      CameraPitch = 20,
      ToePointRuns = 2,
      ToePitchStep = 12,
    };

    SurveyMissionPlan plan = FaceMapMissionBuilder.Build(TaggedStrip(),
        new PointLatLngAlt(35, 33, 100), options);

    Assert.Equal(MAVLink.MAV_CMD.TAKEOFF, plan.Commands[0].Command);
    Assert.Equal(15, plan.Commands[0].Alt);
    Assert.Equal(20, plan.Commands[0].P1);
    Assert.Contains(plan.Commands, command =>
        command.Command == MAVLink.MAV_CMD.DO_CHANGE_SPEED && command.P2 == 4.5);
    Assert.Equal(-44, plan.Commands.First(command =>
        command.Command == MAVLink.MAV_CMD.DO_MOUNT_CONTROL).P1);
    Assert.Equal(2, plan.Commands.Count(command =>
        command.Command == MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST));
    Assert.Contains(plan.Commands, command =>
        command.Command == MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST && command.P1 == 7.5);
    Assert.Contains(plan.Commands, command =>
        command.Command == MAVLink.MAV_CMD.CONDITION_YAW && command.P1 is >= 0 and < 360);
    Assert.Equal(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, plan.Commands[^1].Command);
    Assert.All(plan.Commands, command => Assert.Equal(options.Frame, command.Frame));
    // S/SM and ME/E are deliberately colocated trigger markers; official FaceMap avoids
    // duplicating them as navigation waypoints.
    Assert.Equal(4, plan.NavigationCount);
  }

  [Fact]
  public void Extra_images_bracket_turns_with_official_three_second_delay() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerNone) with {
      ExtraImages = true,
      FollowPathHome = false,
    };

    SurveyMissionPlan plan = FaceMapMissionBuilder.Build(TaggedStrip(),
        PointLatLngAlt.Zero, options);

    Assert.True(plan.Commands.Count(command =>
        command.Command == MAVLink.MAV_CMD.DO_DIGICAM_CONTROL) >= 2);
    Assert.Contains(plan.Commands, command =>
        command.Command == MAVLink.MAV_CMD.DELAY && command.P1 == 3);
    Assert.All(plan.Commands.Where(command =>
            command.Command == MAVLink.MAV_CMD.WAYPOINT),
        command => Assert.Equal(3, command.P1));
  }

  [Fact]
  public void Continuous_repeat_servo_has_one_start_and_one_stop() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerRepeatServo) with {
      StopTriggerAtStripEnds = false,
      ServoNumber = 10,
      ServoPwm = 1750,
      ServoRepeatSeconds = 0.75,
    };

    SurveyMissionPlan plan = FaceMapMissionBuilder.Build(TaggedStrip(),
        PointLatLngAlt.Zero, options);
    List<SurveyMissionCommand> servo = plan.Commands.Where(command =>
        command.Command == MAVLink.MAV_CMD.DO_REPEAT_SERVO).ToList();

    Assert.Equal(2, servo.Count);
    Assert.Equal((10d, 1750d, 999d, 0.75d),
        (servo[0].P1, servo[0].P2, servo[0].P3, servo[0].P4));
    Assert.Equal(0, servo[1].P3);
  }

  [Fact]
  public void Split_face_map_builds_independent_safe_flights_and_jump_targets() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerDistance) with {
      AddTakeoff = true,
      FinishAction = FaceMapMissionBuilder.FinishRtl,
      SplitCount = 2,
    };

    SurveyMissionPlan plan = FaceMapMissionBuilder.Build(TwoTaggedStrips(),
        new PointLatLngAlt(35, 33, 100), options);

    Assert.Equal(2, plan.SegmentCount);
    Assert.True(plan.JumpTargetsAreRelative);
    Assert.Equal(2, plan.Commands.Take(2).Count(command =>
        command.Command == MAVLink.MAV_CMD.DO_JUMP));
    Assert.Equal(2, plan.Commands.Count(command => command.Command == MAVLink.MAV_CMD.TAKEOFF));
    Assert.Equal(2, plan.Commands.Count(command =>
        command.Command == MAVLink.MAV_CMD.RETURN_TO_LAUNCH));
    List<int> takeoffs = plan.Commands.Select((command, index) => (command, index))
        .Where(item => item.command.Command == MAVLink.MAV_CMD.TAKEOFF)
        .Select(item => item.index + 1).ToList();
    Assert.Equal(takeoffs.Select(index => (double)index),
        plan.Commands.Take(2).Select(command => command.P1));
  }

  [Fact]
  public void Split_face_map_rejects_flights_without_takeoff_and_finish() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerNone) with { SplitCount = 2 };

    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        FaceMapMissionBuilder.Build(TwoTaggedStrips(), PointLatLngAlt.Zero, options));

    Assert.Contains("takeoff", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Mission_builder_rejects_unbounded_or_non_finite_options() {
    Assert.Throws<ArgumentOutOfRangeException>(() => FaceMapMissionBuilder.Build(
        TaggedStrip(), PointLatLngAlt.Zero,
        MissionOptions(FaceMapMissionBuilder.TriggerNone) with { SplitCount = 301 }));
    Assert.Throws<ArgumentOutOfRangeException>(() => FaceMapMissionBuilder.Build(
        TaggedStrip(), PointLatLngAlt.Zero,
        MissionOptions(FaceMapMissionBuilder.TriggerRepeatServo) with {
          ServoRepeatSeconds = double.NaN,
        }));
  }

  [Fact]
  public void Mission_builder_rejects_terrain_altitude_mode() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerNone) with {
      Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
    };

    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        FaceMapMissionBuilder.Build(TaggedStrip(), PointLatLngAlt.Zero, options));

    Assert.Contains("Terrain", error.Message);
  }

  [Fact]
  public void Land_finish_climbs_only_to_home_clearance_before_returning() {
    var options = MissionOptions(FaceMapMissionBuilder.TriggerNone) with {
      AddTakeoff = true,
      FinishAction = FaceMapMissionBuilder.FinishLand,
    };

    SurveyMissionPlan plan = FaceMapMissionBuilder.Build(TaggedStrip(),
        new PointLatLngAlt(34.9, 32.9, 100), options);
    List<SurveyMissionCommand> finalNavigation = plan.Commands.Where(command =>
        command.Command == MAVLink.MAV_CMD.WAYPOINT).TakeLast(2).ToList();

    Assert.Equal(10, finalNavigation[0].Alt);
    Assert.Equal((34.9, 32.9, 10d),
        (finalNavigation[1].Lat, finalNavigation[1].Lng, finalNavigation[1].Alt));
    Assert.Equal(MAVLink.MAV_CMD.LAND, plan.Commands[^1].Command);
  }

  [Fact]
  public void Face_map_file_round_trips_official_contract_and_native_extensions() {
    string root = MakeTempDirectory();
    try {
      string filename = Path.Combine(root, "quarry.facemap");
      var data = new FaceMapFileData {
        poly = BentPath(),
        camera = "Camera A",
        benchheight = 12.5m,
        angle = 75,
        facedirection = true,
        speed = 3.5m,
        usespeed = true,
        autotakeoff = true,
        autotakeoff_RTL = true,
        extraimages = true,
        height_test = 4,
        toepoint_runs = 2,
        splitmission = 3,
        bermdepth = 6,
        numbenches = 2,
        camerapitch = 15,
        toeheight = -1,
        campitchunlock = true,
        dist = 18,
        overlap = 70,
        sidelap = 65,
        spacing = 4,
        copter_delay = 2,
        trigdist = true,
        breaktrigdist = true,
        repeatservo_no = 9,
        repeatservo_pwm = 1800,
        repeatservo_cycle = 1,
        setservo_no = 10,
        setservo_low = 1100,
        setservo_high = 1900,
        followpathhome = false,
        radialpitchoffset = 8.5m,
      };

      FaceMapSupport.Save(filename, data);
      FaceMapFileData loaded = FaceMapSupport.Load(filename);

      Assert.Equal("Camera A", loaded.camera);
      Assert.Equal(3, loaded.poly.Count);
      Assert.Equal(12.5m, loaded.benchheight);
      Assert.Equal(3, loaded.splitmission);
      Assert.True(loaded.trigdist);
      Assert.False(loaded.followpathhome);
      Assert.Equal(8.5m, loaded.radialpitchoffset);
      Assert.Contains("<FaceMapData", File.ReadAllText(filename));
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public void Face_map_file_rejects_dtds() {
    string root = MakeTempDirectory();
    try {
      string filename = Path.Combine(root, "unsafe.facemap");
      File.WriteAllText(filename,
          "<!DOCTYPE FaceMapData [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]>" +
          "<FaceMapData><camera>&xxe;</camera></FaceMapData>");

      InvalidOperationException error = Assert.Throws<InvalidOperationException>(
          () => FaceMapSupport.Load(filename));
      Assert.IsType<XmlException>(error.InnerException);
    } finally {
      Directory.Delete(root, true);
    }
  }

  [AvaloniaFact]
  public void Native_face_map_view_constructs_with_compiled_bindings() {
    var viewModel = new FaceMapViewModel(BentPath(),
        new PointLatLngAlt(35, 33, 100),
        (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT);
    var view = new FaceMapView { DataContext = viewModel };

    view.Measure(new Avalonia.Size(1000, 800));

    Assert.NotNull(view.FindControl<MissionPlannerAvalonia.Controls.FaceMapPreviewMap>(
        "PreviewMap"));
    Assert.NotEmpty(viewModel.Result);
  }

  [AvaloniaFact]
  [Obsolete]
  public void Planner_auto_wp_menu_exposes_face_map() {
    var view = new FlightPlannerView();
    var map = Assert.IsType<MissionPlannerAvalonia.Controls.FlightPlannerMap>(
        view.FindControl<MissionPlannerAvalonia.Controls.FlightPlannerMap>("Map"));
    var menu = Assert.IsType<ContextMenu>(map.ContextMenu);
    MenuItem autoWp = Assert.Single(menu.Items.OfType<MenuItem>(),
        item => Equals(item.Header, "Auto WP"));

    Assert.Single(autoWp.Items.OfType<MenuItem>(), item => Equals(item.Header, "Face Map"));
  }

  private static FaceMapGeometryOptions GeometryOptions(bool followPathHome) => new(
      BenchHeight: 10,
      VerticalSpacing: 2,
      DistanceFromFace: 10,
      FaceAngle: 90,
      CameraPitch: 0,
      FlipDirection: false,
      BermDepth: 5,
      BenchCount: 1,
      ToeHeight: 0,
      ToePointHeight: 5,
      ToePointRuns: 0,
      FollowPathHome: followPathHome);

  private static FaceMapMissionOptions MissionOptions(string trigger) => new(
      UseSpeed: false,
      FlyingSpeed: 5,
      TriggerMode: trigger,
      TriggerDistance: 7.5,
      StopTriggerAtStripEnds: false,
      AddTakeoff: false,
      FinishAction: FaceMapMissionBuilder.FinishNone,
      ExtraImages: false,
      CopterDelay: 2,
      CameraPitch: 0,
      ToePointRuns: 0,
      ToePitchStep: 0,
      FlipDirection: false,
      FollowPathHome: true,
      SplitCount: 1,
      ServoNumber: 9,
      ServoPwm: 1900,
      ServoRepeatSeconds: 1,
      ServoLowPwm: 1100,
      ServoHighPwm: 1900,
      Frame: (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT);

  private static List<PointLatLngAlt> StraightPath() => [
    new(35, 33),
    new(35, 33.001),
    new(35, 33.002),
  ];

  private static List<PointLatLngAlt> BentPath() => [
    new(35, 33),
    new(35, 33.001),
    new(35.001, 33.0015),
  ];

  private static List<PointLatLngAlt> TaggedStrip() => [
    new(35, 33, 5, "S"),
    new(35, 33, 5, "SM"),
    new(35, 33.001, 5, "M"),
    new(35.001, 33.001, 5, "ME"),
    new(35.001, 33.001, 5, "E"),
  ];

  private static List<PointLatLngAlt> TwoTaggedStrips() => [
    .. TaggedStrip(),
    new(35.001, 33.001, 7, "S"),
    new(35.001, 33.001, 7, "SM"),
    new(35.001, 33, 7, "M"),
    new(35, 33, 7, "ME"),
    new(35, 33, 7, "E"),
  ];

  private static string MakeTempDirectory() {
    string path = Path.Combine(Path.GetTempPath(),
        "mpa-facemap-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }
}
