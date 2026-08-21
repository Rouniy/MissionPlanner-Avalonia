using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.ViewModels;

public sealed record SurveyMissionOptions(
    bool UseSpeed,
    double FlyingSpeed,
    string TriggerMode,
    double TriggerDistance,
    bool StopTriggerAtStripEnds,
    bool AddTakeoff,
    double TakeoffAltitude,
    string FinishAction,
    bool UseSplineWaypoints,
    bool HoldHeading,
    double Heading,
    double WaypointDelay,
    int ServoNumber,
    int ServoPwm,
    double ServoRepeatSeconds,
    int ServoLowPwm,
    int ServoHighPwm,
    int SplitCount = 1,
    double RestoreSpeed = 0);

public sealed record SurveyMissionCommand(
    MAVLink.MAV_CMD Command,
    double Lat = 0,
    double Lng = 0,
    double Alt = 0,
    double P1 = 0,
    double P2 = 0,
    double P3 = 0,
    double P4 = 0,
    byte Frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT);

public sealed record SurveyMissionPlan(IReadOnlyList<SurveyMissionCommand> Commands) {
  public int NavigationCount { get; init; }
  public int CameraCommandCount { get; init; }
  public int SegmentCount { get; init; } = 1;
  public bool JumpTargetsAreRelative { get; init; }
}

internal static class SurveyMissionBuilder {
  internal const string TriggerNone = "None";
  internal const string TriggerDistance = "Distance";
  internal const string TriggerDigicam = "Digicam";
  internal const string TriggerRepeatServo = "Repeat servo";
  internal const string TriggerSetServo = "Set servo";
  internal const string FinishNone = "None";
  internal const string FinishRtl = "RTL";
  internal const string FinishLand = "Land";

  internal static SurveyMissionPlan Build(IReadOnlyList<PointLatLngAlt> grid,
      PointLatLngAlt home, SurveyMissionOptions options) {
    int splitCount = options.SplitCount;
    if (splitCount < 1) {
      throw new ArgumentOutOfRangeException(nameof(options), "Split count must be at least one.");
    }
    if (grid.Count == 0) {
      return new SurveyMissionPlan(Array.Empty<SurveyMissionCommand>()) { SegmentCount = 0 };
    }
    if (splitCount == 1) {
      return BuildSegment(grid, home, options);
    }
    if (!options.AddTakeoff || options.FinishAction == FinishNone) {
      throw new InvalidOperationException(
          "Split missions require takeoff and an RTL or Land finish for every flight.");
    }

    var commands = new List<SurveyMissionCommand>();
    var starts = new List<int>();
    int navigationCount = 0;
    int cameraCommandCount = 0;
    foreach (var (start, end) in SplitRanges(grid, splitCount)) {
      starts.Add(commands.Count);
      var segment = BuildSegment(grid.Skip(start).Take(end - start).ToList(), home,
          options with { SplitCount = 1 });
      commands.AddRange(segment.Commands);
      navigationCount += segment.NavigationCount;
      cameraCommandCount += segment.CameraCommandCount;
    }

    int jumpCount = starts.Count;
    var jumps = starts.Select(start => new SurveyMissionCommand(MAVLink.MAV_CMD.DO_JUMP,
        P1: start + jumpCount + 1, P2: 1));
    commands.InsertRange(0, jumps);
    return new SurveyMissionPlan(commands) {
      NavigationCount = navigationCount,
      CameraCommandCount = cameraCommandCount,
      SegmentCount = jumpCount,
      JumpTargetsAreRelative = true,
    };
  }

  internal static IReadOnlyList<(int Start, int End)> SplitRanges(
      IReadOnlyList<PointLatLngAlt> grid, int splitCount) {
    if (splitCount < 1) {
      throw new ArgumentOutOfRangeException(nameof(splitCount));
    }
    if (grid.Count == 0) {
      return Array.Empty<(int, int)>();
    }

    int chunk = Math.Max(1,
        (int)Math.Round(grid.Count / (double)splitCount, MidpointRounding.AwayFromZero));
    var ranges = new List<(int Start, int End)>();
    for (int split = 0; split < splitCount; split++) {
      int start = chunk * split;
      int rawEnd = chunk * (split + 1);
      if (start >= grid.Count) {
        throw new InvalidOperationException("Split count exceeds the available survey strips.");
      }

      while (start != 0 && start < grid.Count && grid[start].Tag != "E") {
        start--;
      }

      int end = Math.Min(rawEnd, grid.Count);
      while (end > 0 && end < grid.Count && grid[end].Tag != "S") {
        end--;
      }
      if (end <= start) {
        throw new InvalidOperationException(
            "The generated grid does not contain enough complete strip boundaries to split safely.");
      }
      ranges.Add((start, end));
    }
    return ranges;
  }

  private static SurveyMissionPlan BuildSegment(IReadOnlyList<PointLatLngAlt> grid,
      PointLatLngAlt home, SurveyMissionOptions options) {
    var commands = new List<SurveyMissionCommand>();
    if (grid.Count == 0) {
      return new SurveyMissionPlan(commands);
    }

    if (options.AddTakeoff) {
      commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.TAKEOFF,
          Alt: Math.Max(0, options.TakeoffAltitude), P1: 20));
    }

    if (options.UseSpeed && options.FlyingSpeed > 0) {
      commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.DO_CHANGE_SPEED,
          P2: options.FlyingSpeed));
    }

    int navigationCount = 0;
    int cameraCommandCount = 0;
    bool distanceTriggerStarted = false;
    PointLatLngAlt? lastNavigationPoint = null;

    void AddNavigation(PointLatLngAlt point) {
      if (lastNavigationPoint != null && SameLocation(lastNavigationPoint, point)) {
        return;
      }

      if (options.HoldHeading) {
        commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.CONDITION_YAW,
            P1: NormalizeHeading(options.Heading)));
      }

      var command = options.WaypointDelay <= 0 && options.UseSplineWaypoints &&
                    point.Tag is "S" or "SM"
          ? MAVLink.MAV_CMD.SPLINE_WAYPOINT
          : MAVLink.MAV_CMD.WAYPOINT;
      commands.Add(new SurveyMissionCommand(command, point.Lat, point.Lng, point.Alt,
          P1: Math.Max(0, options.WaypointDelay)));
      lastNavigationPoint = point;
      navigationCount++;
    }

    AddNavigation(grid[0]);

    for (int i = 1; i < grid.Count; i++) {
      var point = grid[i];
      string tag = point.Tag ?? "";

      if (tag == "M") {
        if (options.TriggerMode == TriggerDigicam) {
          AddNavigation(point);
          commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.DO_DIGICAM_CONTROL,
              Lat: 1, P1: 1));
          cameraCommandCount++;
        } else if (options.TriggerMode == TriggerRepeatServo &&
                   !options.StopTriggerAtStripEnds) {
          AddNavigation(point);
          commands.Add(RepeatServoCommand(options, 1));
          cameraCommandCount++;
        }

        continue;
      }

      if (tag is "S" or "E") {
        AddNavigation(point);
      }

      switch (options.TriggerMode) {
        case TriggerDistance:
          if (options.StopTriggerAtStripEnds) {
            if (tag == "SM") {
              AddNavigation(point);
              commands.Add(TriggerDistanceCommand(options.TriggerDistance));
              cameraCommandCount++;
            } else if (tag == "ME") {
              AddNavigation(point);
              commands.Add(TriggerDistanceCommand(0));
              cameraCommandCount++;
            }
          } else if (!distanceTriggerStarted) {
            commands.Add(TriggerDistanceCommand(options.TriggerDistance));
            cameraCommandCount++;
            distanceTriggerStarted = true;
          } else if (tag == "ME") {
            AddNavigation(point);
          }
          break;

        case TriggerRepeatServo when options.StopTriggerAtStripEnds:
          if (tag == "SM") {
            AddNavigation(point);
            commands.Add(RepeatServoCommand(options, 999));
            cameraCommandCount++;
          } else if (tag == "ME") {
            AddNavigation(point);
            commands.Add(RepeatServoCommand(options, 0));
            cameraCommandCount++;
          }
          break;

        case TriggerSetServo:
          if (tag == "SM") {
            AddNavigation(point);
            commands.Add(SetServoCommand(options.ServoNumber, options.ServoLowPwm));
            cameraCommandCount++;
          } else if (tag == "ME") {
            AddNavigation(point);
            commands.Add(SetServoCommand(options.ServoNumber, options.ServoHighPwm));
            cameraCommandCount++;
          }
          break;
      }
    }

    if (options.TriggerMode == TriggerDistance) {
      commands.Add(TriggerDistanceCommand(0));
      cameraCommandCount++;
    }

    if (options.UseSpeed && options.RestoreSpeed > 0) {
      commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.DO_CHANGE_SPEED,
          P2: options.RestoreSpeed));
    }

    if (options.FinishAction == FinishRtl) {
      commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.RETURN_TO_LAUNCH));
    } else if (options.FinishAction == FinishLand) {
      commands.Add(new SurveyMissionCommand(MAVLink.MAV_CMD.LAND, home.Lat, home.Lng, 0));
    }

    return new SurveyMissionPlan(commands) {
      NavigationCount = navigationCount,
      CameraCommandCount = cameraCommandCount,
      SegmentCount = 1,
    };
  }

  private static SurveyMissionCommand TriggerDistanceCommand(double distance) =>
      new(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, P1: Math.Max(0, distance), P3: 1);

  private static SurveyMissionCommand RepeatServoCommand(SurveyMissionOptions options,
      int repetitions) => new(MAVLink.MAV_CMD.DO_REPEAT_SERVO,
      P1: Math.Clamp(options.ServoNumber, 1, 16),
      P2: Math.Clamp(options.ServoPwm, 800, 2200),
      P3: Math.Max(0, repetitions),
      P4: Math.Max(0, options.ServoRepeatSeconds));

  private static SurveyMissionCommand SetServoCommand(int servoNumber, int pwm) =>
      new(MAVLink.MAV_CMD.DO_SET_SERVO,
          P1: Math.Clamp(servoNumber, 1, 16), P2: Math.Clamp(pwm, 800, 2200));

  private static bool SameLocation(PointLatLngAlt a, PointLatLngAlt b) =>
      Math.Abs(a.Lat - b.Lat) < 1e-9 && Math.Abs(a.Lng - b.Lng) < 1e-9 &&
      Math.Abs(a.Alt - b.Alt) < 1e-6;

  private static double NormalizeHeading(double heading) {
    heading %= 360;
    return heading < 0 ? heading + 360 : heading;
  }
}
