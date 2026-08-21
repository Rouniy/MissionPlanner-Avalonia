using MissionPlanner.Utilities;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class SurveyMissionBuilderTests {
  [Fact]
  public void Continuous_distance_trigger_skips_photo_markers_and_stops_at_end() {
    var plan = SurveyMissionBuilder.Build(Grid(), new PointLatLngAlt(1, 2, 3),
        Options(trigger: SurveyMissionBuilder.TriggerDistance));

    Assert.Equal(3, plan.NavigationCount);
    Assert.Equal(2, plan.CameraCommandCount);
    Assert.Equal(new[] {
      MAVLink.MAV_CMD.WAYPOINT,
      MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST,
      MAVLink.MAV_CMD.WAYPOINT,
      MAVLink.MAV_CMD.WAYPOINT,
      MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST,
    }, plan.Commands.Select(c => c.Command));
    Assert.Equal(25, plan.Commands[1].P1);
    Assert.Equal(0, plan.Commands[^1].P1);
  }

  [Fact]
  public void Stop_start_distance_trigger_wraps_each_strip() {
    var plan = SurveyMissionBuilder.Build(Grid(), PointLatLngAlt.Zero,
        Options(trigger: SurveyMissionBuilder.TriggerDistance, stopAtEnds: true));

    Assert.Equal(4, plan.NavigationCount);
    Assert.Equal(3, plan.CameraCommandCount);
    Assert.Equal(25, plan.Commands[2].P1);
    Assert.Equal(0, plan.Commands[4].P1);
    Assert.Equal(0, plan.Commands[^1].P1);
  }

  [Fact]
  public void Digicam_plan_includes_navigation_wrappers_and_selected_flight_commands() {
    var options = Options(trigger: SurveyMissionBuilder.TriggerDigicam) with {
      UseSpeed = true,
      FlyingSpeed = 8,
      AddTakeoff = true,
      TakeoffAltitude = 35,
      FinishAction = SurveyMissionBuilder.FinishLand,
      UseSplineWaypoints = true,
      HoldHeading = true,
      Heading = 370,
    };

    var plan = SurveyMissionBuilder.Build(Grid(), new PointLatLngAlt(9, 10, 11), options);

    Assert.Equal(MAVLink.MAV_CMD.TAKEOFF, plan.Commands[0].Command);
    Assert.Equal(35, plan.Commands[0].Alt);
    Assert.Equal(20, plan.Commands[0].P1);
    Assert.Equal(MAVLink.MAV_CMD.DO_CHANGE_SPEED, plan.Commands[1].Command);
    Assert.Equal(8, plan.Commands[1].P2);
    var digicam = Assert.Single(plan.Commands,
        c => c.Command == MAVLink.MAV_CMD.DO_DIGICAM_CONTROL);
    Assert.Equal(1, digicam.Lat);
    Assert.Equal(0, digicam.Lng);
    Assert.Equal(1, digicam.P1);
    Assert.Contains(plan.Commands, c => c.Command == MAVLink.MAV_CMD.SPLINE_WAYPOINT);
    Assert.All(plan.Commands.Where(c => c.Command == MAVLink.MAV_CMD.CONDITION_YAW),
        c => Assert.Equal(10, c.P1));
    Assert.Equal(MAVLink.MAV_CMD.LAND, plan.Commands[^1].Command);
    Assert.Equal(9, plan.Commands[^1].Lat);
    Assert.Equal(10, plan.Commands[^1].Lng);
  }

  [Fact]
  public void Waypoint_delay_disables_spline_commands() {
    var options = Options() with { UseSplineWaypoints = true, WaypointDelay = 2.5 };

    var plan = SurveyMissionBuilder.Build(Grid(), PointLatLngAlt.Zero, options);

    Assert.DoesNotContain(plan.Commands, c => c.Command == MAVLink.MAV_CMD.SPLINE_WAYPOINT);
    Assert.All(plan.Commands.Where(c => c.Command == MAVLink.MAV_CMD.WAYPOINT),
        c => Assert.Equal(2.5, c.P1));
  }

  [Fact]
  public void Repeat_servo_can_start_and_stop_at_strip_boundaries() {
    var options = Options(trigger: SurveyMissionBuilder.TriggerRepeatServo,
        stopAtEnds: true) with {
      ServoNumber = 10,
      ServoPwm = 1800,
      ServoRepeatSeconds = 0.75,
    };

    var plan = SurveyMissionBuilder.Build(Grid(), PointLatLngAlt.Zero, options);
    var servo = plan.Commands.Where(c => c.Command == MAVLink.MAV_CMD.DO_REPEAT_SERVO).ToList();

    Assert.Equal(2, servo.Count);
    Assert.Equal(10, servo[0].P1);
    Assert.Equal(1800, servo[0].P2);
    Assert.Equal(999, servo[0].P3);
    Assert.Equal(0.75, servo[0].P4);
    Assert.Equal(0, servo[1].P3);
  }

  [Fact]
  public void Split_mission_uses_complete_strip_boundaries_and_jump_targets() {
    var options = Options(trigger: SurveyMissionBuilder.TriggerDistance) with {
      UseSpeed = true,
      FlyingSpeed = 8,
      RestoreSpeed = 5,
      AddTakeoff = true,
      FinishAction = SurveyMissionBuilder.FinishRtl,
      SplitCount = 2,
    };

    var grid = TwoStripGrid();
    var ranges = SurveyMissionBuilder.SplitRanges(grid, 2);
    var plan = SurveyMissionBuilder.Build(grid, PointLatLngAlt.Zero, options);

    Assert.Equal(new[] { (0, 5), (4, 10) }, ranges);
    Assert.Equal(2, plan.SegmentCount);
    Assert.True(plan.JumpTargetsAreRelative);
    var jumps = plan.Commands.Take(2).ToList();
    Assert.All(jumps, command => Assert.Equal(MAVLink.MAV_CMD.DO_JUMP, command.Command));
    Assert.All(jumps, command => Assert.Equal(1, command.P2));
    var takeoffIndexes = plan.Commands.Select((command, index) => (command, index))
        .Where(item => item.command.Command == MAVLink.MAV_CMD.TAKEOFF)
        .Select(item => item.index + 1)
        .ToList();
    Assert.Equal(takeoffIndexes.Select(index => (double)index), jumps.Select(jump => jump.P1));
    Assert.Equal(2, plan.Commands.Count(c => c.Command == MAVLink.MAV_CMD.RETURN_TO_LAUNCH));
    Assert.Equal(4, plan.Commands.Count(c => c.Command == MAVLink.MAV_CMD.DO_CHANGE_SPEED));
    Assert.Equal(2, plan.Commands.Count(c =>
        c.Command == MAVLink.MAV_CMD.DO_CHANGE_SPEED && c.P2 == 5));
  }

  [Fact]
  public void Split_mission_rejects_an_unsafe_unfinished_flight() {
    var options = Options() with { SplitCount = 2, AddTakeoff = true };

    var error = Assert.Throws<InvalidOperationException>(() =>
        SurveyMissionBuilder.Build(TwoStripGrid(), PointLatLngAlt.Zero, options));

    Assert.Contains("RTL or Land", error.Message);
  }

  [Fact]
  public void Appending_split_plan_offsets_jump_targets_for_existing_commands() {
    var options = Options() with {
      SplitCount = 2,
      AddTakeoff = true,
      FinishAction = SurveyMissionBuilder.FinishRtl,
    };
    var plan = SurveyMissionBuilder.Build(TwoStripGrid(), PointLatLngAlt.Zero, options);
    var vm = new FlightPlannerViewModel();
    vm.Waypoints.Add(new WpRow { Command = (ushort)MAVLink.MAV_CMD.WAYPOINT });

    vm.AppendSurveyPlan(plan);

    var appendedJumps = vm.Waypoints.Skip(1).Take(2).ToList();
    Assert.Equal(plan.Commands[0].P1 + 1, appendedJumps[0].P1);
    Assert.Equal(plan.Commands[1].P1 + 1, appendedJumps[1].P1);
  }

  private static List<PointLatLngAlt> Grid() => new() {
    new PointLatLngAlt(1, 1, 100, "S"),
    new PointLatLngAlt(1, 2, 100, "SM"),
    new PointLatLngAlt(1, 3, 100, "M"),
    new PointLatLngAlt(1, 4, 100, "ME"),
    new PointLatLngAlt(2, 4, 100, "E"),
  };

  private static List<PointLatLngAlt> TwoStripGrid() => new() {
    new PointLatLngAlt(1, 1, 100, "S"),
    new PointLatLngAlt(1, 2, 100, "SM"),
    new PointLatLngAlt(1, 3, 100, "M"),
    new PointLatLngAlt(1, 4, 100, "ME"),
    new PointLatLngAlt(1, 5, 100, "E"),
    new PointLatLngAlt(2, 5, 100, "S"),
    new PointLatLngAlt(2, 4, 100, "SM"),
    new PointLatLngAlt(2, 3, 100, "M"),
    new PointLatLngAlt(2, 2, 100, "ME"),
    new PointLatLngAlt(2, 1, 100, "E"),
  };

  private static SurveyMissionOptions Options(string trigger = SurveyMissionBuilder.TriggerNone,
      bool stopAtEnds = false) => new(
      UseSpeed: false,
      FlyingSpeed: 5,
      TriggerMode: trigger,
      TriggerDistance: 25,
      StopTriggerAtStripEnds: stopAtEnds,
      AddTakeoff: false,
      TakeoffAltitude: 30,
      FinishAction: SurveyMissionBuilder.FinishNone,
      UseSplineWaypoints: false,
      HoldHeading: false,
      Heading: 0,
      WaypointDelay: 0,
      ServoNumber: 9,
      ServoPwm: 1900,
      ServoRepeatSeconds: 1,
      ServoLowPwm: 1100,
      ServoHighPwm: 1900);
}
