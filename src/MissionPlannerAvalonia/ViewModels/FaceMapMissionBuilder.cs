using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.ViewModels;

internal sealed record FaceMapGeometryOptions(
    double BenchHeight,
    double VerticalSpacing,
    double DistanceFromFace,
    double FaceAngle,
    double CameraPitch,
    bool FlipDirection,
    double BermDepth,
    int BenchCount,
    double ToeHeight,
    double ToePointHeight,
    int ToePointRuns,
    bool FollowPathHome,
    double AltitudeOffset = 0);

internal sealed record FaceMapMissionOptions(
    bool UseSpeed,
    double FlyingSpeed,
    string TriggerMode,
    double TriggerDistance,
    bool StopTriggerAtStripEnds,
    bool AddTakeoff,
    string FinishAction,
    bool ExtraImages,
    double CopterDelay,
    double CameraPitch,
    int ToePointRuns,
    double ToePitchStep,
    bool FlipDirection,
    bool FollowPathHome,
    int SplitCount,
    int ServoNumber,
    int ServoPwm,
    double ServoRepeatSeconds,
    int ServoLowPwm,
    int ServoHighPwm,
    byte Frame,
    double RestoreSpeed = 0,
    double EntryClearance = 10);

/// <summary>
/// Native implementation of the official FaceMap plug-in's offset-path geometry.
/// It intentionally keeps the plug-in's S/SM/M/ME/E/R tag contract because the mission
/// generator uses those tags to place camera and transition commands.
/// </summary>
internal static class FaceMapGeometry {
  private const double _degToRad = Math.PI / 180.0;
  private const int _maximumGeneratedPoints = 100_000;

  internal static List<PointLatLngAlt> Create(
      IReadOnlyList<PointLatLngAlt> path, FaceMapGeometryOptions options) {
    Validate(path, options);

    List<PointLatLngAlt> cleanPath = RemoveAdjacentDuplicates(path);
    if (cleanPath.Count < 3) {
      throw new InvalidOperationException(
          "Face Map needs at least three distinct path points.");
    }

    int direction = options.FlipDirection ? -1 : 1;
    int zone = cleanPath[0].GetUTMZone();
    var projected = cleanPath.Select(point => {
      double[] value = point.ToUTM(zone);
      return new utmpos(value[0], value[1], zone);
    }).ToList();

    double verticalSpacing = Math.Max(0.1, options.VerticalSpacing);
    double verticalIncrement = verticalSpacing * Math.Sin(options.FaceAngle * _degToRad);
    if (!double.IsFinite(verticalIncrement) || verticalIncrement <= 0) {
      throw new InvalidOperationException("Vertical camera spacing must be greater than zero.");
    }

    // Keep Math.Round's ToEven behavior: this is the exact lane-count rule in FaceMap.cs.
    int lanes = checked((int)Math.Round(
        (options.BenchHeight - options.ToePointHeight) / verticalIncrement) +
        options.ToePointRuns + 1);
    if (lanes < 1) {
      throw new InvalidOperationException(
          "The toe-point height is above the selected bench height.");
    }
    long estimatedPoints = (long)lanes * options.BenchCount * (cleanPath.Count + 2) +
                           Math.Max(0, options.BenchCount - 1) +
                           (options.FollowPathHome ? cleanPath.Count + 2 : 0);
    if (estimatedPoints > _maximumGeneratedPoints) {
      throw new InvalidOperationException(
          $"Face Map would generate about {estimatedPoints:N0} points; reduce benches, " +
          "face height or overlap.");
    }

    var result = new List<PointLatLngAlt>();
    double horizontalOffset = 0;
    double verticalOffset = 0;
    int toeRunCount = 0;
    double faceTangent = Math.Tan(options.FaceAngle * _degToRad);

    for (int bench = 0; bench < options.BenchCount; bench++) {
      for (int lane = 0; lane < lanes; lane++) {
        double laneHeight;
        if (toeRunCount < options.ToePointRuns) {
          laneHeight = options.ToePointHeight;
          toeRunCount++;
        } else {
          laneHeight = options.ToePointHeight +
                       (lane - options.ToePointRuns) * verticalIncrement;
        }

        verticalOffset = options.DistanceFromFace *
                         Math.Sin(options.CameraPitch * _degToRad) +
                         laneHeight + bench * options.BenchHeight + options.ToeHeight +
                         options.AltitudeOffset;
        horizontalOffset = options.DistanceFromFace *
                           Math.Cos(options.CameraPitch * _degToRad) -
                           laneHeight / faceTangent -
                           bench * (options.BermDepth + options.BenchHeight / faceTangent);

        EnsureFinite(verticalOffset, nameof(verticalOffset));
        EnsureFinite(horizontalOffset, nameof(horizontalOffset));

        // First climb vertically before moving sideways to the next bench. This is the
        // official transition, but with the selected altitude frame already applied.
        if (lane == 0 && result.Count > 0) {
          result.Add(new PointLatLngAlt(result[^1].Lat, result[^1].Lng, verticalOffset) {
            Tag = "S",
          });
        }

        foreach (PointLatLngAlt point in GenerateOffsetPath(
                     projected, horizontalOffset * direction, zone)) {
          point.Alt = verticalOffset;
          result.Add(point);
        }

        projected.Reverse();
        direction = -direction;
      }
    }

    if (options.FollowPathHome && (lanes * options.BenchCount) % 2 == 1) {
      foreach (PointLatLngAlt point in GenerateOffsetPath(
                   projected, horizontalOffset * direction, zone)) {
        point.Alt = verticalOffset;
        point.Tag = "R";
        result.Add(point);
      }
    }

    if (result.Any(point => !ValidCoordinate(point))) {
      throw new InvalidOperationException("Face Map generated an invalid geographic coordinate.");
    }
    return result;
  }

  private static List<PointLatLngAlt> GenerateOffsetPath(
      IReadOnlyList<utmpos> path, double distance, int zone) {
    var result = new List<PointLatLngAlt>();
    for (int index = 0; index < path.Count - 2; index++) {
      utmpos previous = path[index];
      utmpos current = path[index + 1];
      utmpos next = path[index + 2];

      double firstBearing = previous.GetBearing(current);
      double secondBearing = current.GetBearing(next);
      utmpos firstStart = Offset(previous, firstBearing + 90, distance);
      utmpos firstEnd = Offset(current, firstBearing + 90, distance);
      utmpos secondStart = Offset(current, secondBearing + 90, distance);
      utmpos secondEnd = Offset(next, secondBearing + 90, distance);
      utmpos join = JoinOffsetLines(firstStart, firstEnd, secondStart, secondEnd, distance);

      if (index == 0) {
        result.Add(ToPoint(firstStart, "S"));
        result.Add(ToPoint(firstStart, "SM"));
      }
      result.Add(ToPoint(join, "M"));
      if (index + 3 == path.Count) {
        result.Add(ToPoint(secondEnd, "ME"));
        result.Add(ToPoint(secondEnd, "E"));
      }
    }
    return result;
  }

  private static utmpos JoinOffsetLines(utmpos start1, utmpos end1,
      utmpos start2, utmpos end2, double offset) {
    double dx1 = end1.x - start1.x;
    double dy1 = end1.y - start1.y;
    double dx2 = end2.x - start2.x;
    double dy2 = end2.y - start2.y;
    double denominator = dx1 * dy2 - dy1 * dx2;

    // Upstream returns utmpos.Zero here, which turns an ordinary straight path into a
    // waypoint in the Gulf of Guinea. A bevel at the shared shifted vertex is the safe,
    // geometrically continuous interpretation for parallel or almost-parallel legs.
    if (Math.Abs(denominator) < 1e-9) {
      return Midpoint(end1, start2);
    }

    double numerator = (start1.y - start2.y) * dx2 -
                       (start1.x - start2.x) * dy2;
    double ratio = numerator / denominator;
    var intersection = new utmpos(start1.x + ratio * dx1,
        start1.y + ratio * dy1, start1.zone);

    double miter = intersection.GetDistance(Midpoint(end1, start2));
    double maximumMiter = Math.Max(100, Math.Abs(offset) * 20);
    return double.IsFinite(intersection.x) && double.IsFinite(intersection.y) &&
           miter <= maximumMiter
        ? intersection
        : Midpoint(end1, start2);
  }

  private static utmpos Offset(utmpos input, double bearing, double distance) {
    double northAngle = (90 - bearing) * _degToRad;
    return new utmpos(input.x + distance * Math.Cos(northAngle),
        input.y + distance * Math.Sin(northAngle), input.zone);
  }

  private static utmpos Midpoint(utmpos first, utmpos second) =>
      new((first.x + second.x) / 2, (first.y + second.y) / 2, first.zone);

  private static PointLatLngAlt ToPoint(utmpos value, string tag) {
    value.Tag = tag;
    return value.ToLLA();
  }

  private static List<PointLatLngAlt> RemoveAdjacentDuplicates(
      IReadOnlyList<PointLatLngAlt> path) {
    var result = new List<PointLatLngAlt>();
    foreach (PointLatLngAlt point in path) {
      if (result.Count == 0 || result[^1].GetDistance(point) > 0.01) {
        result.Add(new PointLatLngAlt(point));
      }
    }
    return result;
  }

  private static void Validate(IReadOnlyList<PointLatLngAlt> path,
      FaceMapGeometryOptions options) {
    ArgumentNullException.ThrowIfNull(path);
    if (path.Count < 3) {
      throw new ArgumentException("Face Map needs at least three path points.", nameof(path));
    }
    if (path.Count > 10_000) {
      throw new ArgumentException("Face Map paths are limited to 10,000 points.", nameof(path));
    }
    if (path.Any(point => !ValidCoordinate(point))) {
      throw new ArgumentException("The Face Map path contains an invalid coordinate.", nameof(path));
    }
    if (!double.IsFinite(options.BenchHeight) || options.BenchHeight <= 0 ||
        !double.IsFinite(options.VerticalSpacing) || options.VerticalSpacing <= 0 ||
        !double.IsFinite(options.DistanceFromFace) || options.DistanceFromFace <= 0 ||
        !double.IsFinite(options.FaceAngle) || options.FaceAngle is < 1 or > 90 ||
        !double.IsFinite(options.CameraPitch) || options.CameraPitch is < 0 or >= 90 ||
        !double.IsFinite(options.BermDepth) || options.BermDepth < 0 ||
        !double.IsFinite(options.ToeHeight) ||
        !double.IsFinite(options.ToePointHeight) || options.ToePointHeight < 0 ||
        !double.IsFinite(options.AltitudeOffset) ||
        options.BenchCount is < 1 or > 10_000 || options.ToePointRuns is < 0 or > 10_000) {
      throw new ArgumentOutOfRangeException(nameof(options),
          "Face Map dimensions and angles are outside their supported range.");
    }
  }

  private static bool ValidCoordinate(PointLatLngAlt point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;

  private static void EnsureFinite(double value, string name) {
    if (!double.IsFinite(value)) {
      throw new InvalidOperationException($"Face Map calculated an invalid {name}.");
    }
  }
}

/// <summary>Builds the full FaceMap MAVLink command sequence without touching a vehicle.</summary>
internal static class FaceMapMissionBuilder {
  internal const string TriggerNone = "None";
  internal const string TriggerDistance = "Distance";
  internal const string TriggerDigicam = "Digicam";
  internal const string TriggerRepeatServo = "Repeat servo";
  internal const string TriggerSetServo = "Set servo";
  internal const string FinishNone = "None";
  internal const string FinishRtl = "RTL";
  internal const string FinishLand = "Land";
  private const double _officialWaypointDelay = 3;

  internal static SurveyMissionPlan Build(IReadOnlyList<PointLatLngAlt> grid,
      PointLatLngAlt home, FaceMapMissionOptions options) {
    ArgumentNullException.ThrowIfNull(grid);
    ArgumentNullException.ThrowIfNull(home);
    Validate(options);
    if (!ValidMissionPoint(home) || grid.Any(point => !ValidMissionPoint(point))) {
      throw new ArgumentException("Face Map contains an invalid mission coordinate.",
          nameof(grid));
    }
    if (grid.Count == 0) {
      return new SurveyMissionPlan(Array.Empty<SurveyMissionCommand>()) { SegmentCount = 0 };
    }

    IReadOnlyList<(int Start, int End)> ranges = options.SplitCount == 1
        ? new[] { (0, grid.Count) }
        : SurveyMissionBuilder.SplitRanges(grid, options.SplitCount);
    if (ranges.Count > 1 && (!options.AddTakeoff || options.FinishAction == FinishNone)) {
      throw new InvalidOperationException(
          "Split Face Map missions require takeoff and an RTL or Land finish.");
    }

    var commands = new List<SurveyMissionCommand>();
    var starts = new List<int>();
    int navigationCount = 0;
    int cameraCount = 0;
    foreach ((int start, int end) in ranges) {
      starts.Add(commands.Count);
      SurveyMissionPlan segment = BuildSegment(
          grid.Skip(start).Take(end - start).ToArray(), home, options);
      commands.AddRange(segment.Commands);
      navigationCount += segment.NavigationCount;
      cameraCount += segment.CameraCommandCount;
    }

    if (starts.Count > 1) {
      int jumpCount = starts.Count;
      commands.InsertRange(0, starts.Select(start => Command(MAVLink.MAV_CMD.DO_JUMP,
          options, p1: start + jumpCount + 1, p2: 1)));
    }

    return new SurveyMissionPlan(commands) {
      NavigationCount = navigationCount,
      CameraCommandCount = cameraCount,
      SegmentCount = starts.Count,
      JumpTargetsAreRelative = starts.Count > 1,
    };
  }

  private static SurveyMissionPlan BuildSegment(IReadOnlyList<PointLatLngAlt> grid,
      PointLatLngAlt home, FaceMapMissionOptions options) {
    var commands = new List<SurveyMissionCommand>();
    int navigationCount = 0;
    int cameraCount = 0;
    bool distanceTriggerStarted = false;
    bool repeatServoStarted = false;
    int direction = options.FlipDirection ? -1 : 1;
    int toeRunCount = 0;
    double faceHeading = 0;

    double entryAltitude = EntryAltitude(grid[0], home, options);
    if (options.AddTakeoff) {
      commands.Add(Command(MAVLink.MAV_CMD.TAKEOFF, options,
          alt: entryAltitude, p1: 20));
    }

    AddWaypoint(grid[0], -1, false, false, entryAltitude);
    if (options.UseSpeed) {
      commands.Add(Command(MAVLink.MAV_CMD.DO_CHANGE_SPEED, options,
          p2: options.FlyingSpeed));
    }

    AddMountPitch(-Math.Min(90,
        options.CameraPitch + options.ToePointRuns * options.ToePitchStep));

    PointLatLngAlt last = grid[0];
    AddWaypoint(grid[0], -1, false, false);
    for (int index = 1; index < grid.Count; index++) {
      PointLatLngAlt point = grid[index];
      string tag = point.Tag?.ToString() ?? "";
      string lastTag = last.Tag?.ToString() ?? "";
      bool moved = !SameLocation(point, last);

      if (tag != "S" && lastTag.Length > 0 && moved) {
        faceHeading = NormalizeHeading(last.GetBearing(point) - 90 * direction);
      }

      if (tag == "E") {
        direction = -direction;
        toeRunCount++;
        if (toeRunCount < options.ToePointRuns) {
          AddMountPitch(-Math.Min(90, options.CameraPitch +
              (options.ToePointRuns - toeRunCount) * options.ToePitchStep));
        } else if (toeRunCount == options.ToePointRuns) {
          AddMountPitch(-options.CameraPitch);
        }
      }

      if (tag == "R" && lastTag != "R" && options.ExtraImages) {
        AddDigicam();
        if (options.CopterDelay > 0) {
          AddDelay(options.CopterDelay);
        }
      }

      if (moved) {
        switch (tag) {
          case "M":
            AddWaypoint(point, faceHeading,
                options.ExtraImages && lastTag is not ("S" or "SM"),
                options.ExtraImages);
            break;
          case "S":
            AddWaypoint(point, faceHeading, options.ExtraImages, false);
            break;
          case "E":
            AddWaypoint(point, faceHeading, false, false);
            break;
          case "R":
            AddWaypoint(point, -1, false, false);
            if (distanceTriggerStarted) {
              AddDistanceTrigger(0);
              distanceTriggerStarted = false;
            }
            break;
        }
      }

      switch (options.TriggerMode) {
        case TriggerDistance:
          if (options.StopTriggerAtStripEnds) {
            if (tag == "SM") {
              if (moved) {
                AddWaypoint(point, faceHeading, false, options.ExtraImages);
              }
              AddDistanceTrigger(options.TriggerDistance);
              distanceTriggerStarted = true;
            } else if (tag == "ME") {
              AddWaypoint(point, faceHeading, options.ExtraImages, options.ExtraImages);
              AddDistanceTrigger(0);
              distanceTriggerStarted = false;
            }
          } else if (!distanceTriggerStarted && tag != "R") {
            AddDistanceTrigger(options.TriggerDistance);
            distanceTriggerStarted = true;
          } else if (tag == "ME") {
            AddWaypoint(point, faceHeading, false, false);
          }
          break;

        case TriggerDigicam when tag is "SM" or "M" or "ME":
          // The official radio button was never wired in FaceMapUI. Issuing the documented
          // command at each generated path vertex makes the option operational and deterministic.
          if (tag is "SM" or "ME") {
            AddWaypoint(point, faceHeading, false, false);
          }
          AddDigicam();
          break;

        case TriggerRepeatServo:
          if (options.StopTriggerAtStripEnds) {
            if (tag == "SM") {
              if (moved) {
                AddWaypoint(point, faceHeading, false, false);
              }
              AddRepeatServo(999);
            } else if (tag == "ME") {
              AddWaypoint(point, faceHeading, false, false);
              AddRepeatServo(0);
            }
          } else if (tag == "SM" && !repeatServoStarted) {
            AddRepeatServo(999);
            repeatServoStarted = true;
          }
          break;

        case TriggerSetServo:
          if (tag == "SM") {
            if (moved) {
              AddWaypoint(point, faceHeading, false, false);
            }
            commands.Add(Command(MAVLink.MAV_CMD.DO_SET_SERVO, options,
                p1: Math.Clamp(options.ServoNumber, 1, 16),
                p2: Math.Clamp(options.ServoLowPwm, 800, 2200)));
            cameraCount++;
          } else if (tag == "ME") {
            AddWaypoint(point, faceHeading, false, false);
            commands.Add(Command(MAVLink.MAV_CMD.DO_SET_SERVO, options,
                p1: Math.Clamp(options.ServoNumber, 1, 16),
                p2: Math.Clamp(options.ServoHighPwm, 800, 2200)));
            cameraCount++;
          }
          break;
      }

      last = point;
    }

    if (distanceTriggerStarted) {
      AddDistanceTrigger(0);
    }
    if (repeatServoStarted) {
      AddRepeatServo(0);
    }
    if (!options.FollowPathHome && options.ExtraImages) {
      AddDigicam();
      AddDelay(options.CopterDelay);
    }

    AddMountPitch(0);
    if (options.UseSpeed && options.RestoreSpeed > 0) {
      commands.Add(Command(MAVLink.MAV_CMD.DO_CHANGE_SPEED, options,
          p2: options.RestoreSpeed));
    }

    if (options.FinishAction == FinishRtl) {
      commands.Add(Command(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, options));
    } else if (options.FinishAction == FinishLand) {
      double homeClearance = options.Frame == (byte)MAVLink.MAV_FRAME.GLOBAL
          ? home.Alt + Math.Max(0, options.EntryClearance)
          : Math.Max(0, options.EntryClearance);
      double exitAltitude = Math.Max(homeClearance, last.Alt);
      if (last.Alt < exitAltitude) {
        AddWaypoint(last, -1, false, false, exitAltitude);
      }
      AddWaypoint(new PointLatLngAlt(home.Lat, home.Lng, exitAltitude),
          -1, false, false);
      commands.Add(Command(MAVLink.MAV_CMD.LAND, options,
          lat: home.Lat, lng: home.Lng));
    }

    return new SurveyMissionPlan(commands) {
      NavigationCount = navigationCount,
      CameraCommandCount = cameraCount,
    };

    void AddWaypoint(PointLatLngAlt point, double bearing,
        bool imageBefore, bool imageAfter, double? altitude = null) {
      if (imageBefore && bearing >= 0) {
        AddDigicam();
        AddDelay(_officialWaypointDelay);
      }
      if (bearing >= 0) {
        commands.Add(Command(MAVLink.MAV_CMD.CONDITION_YAW, options,
            p1: NormalizeHeading(bearing)));
      }
      if (imageAfter && bearing >= 0) {
        AddDigicam();
        AddDelay(_officialWaypointDelay);
        commands.Add(Command(MAVLink.MAV_CMD.CONDITION_YAW, options,
            p1: NormalizeHeading(bearing)));
      }
      commands.Add(Command(MAVLink.MAV_CMD.WAYPOINT, options,
          lat: point.Lat, lng: point.Lng, alt: altitude ?? point.Alt,
          p1: _officialWaypointDelay));
      navigationCount++;
    }

    void AddMountPitch(double pitch) => commands.Add(
        Command(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, options, p1: pitch));

    void AddDigicam() {
      commands.Add(Command(MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, options, lat: 1));
      cameraCount++;
    }

    void AddDelay(double delay) => commands.Add(
        Command(MAVLink.MAV_CMD.DELAY, options, p1: Math.Max(0, delay)));

    void AddDistanceTrigger(double distance) {
      commands.Add(Command(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, options,
          p1: Math.Max(0, distance)));
      cameraCount++;
    }

    void AddRepeatServo(int repetitions) {
      commands.Add(Command(MAVLink.MAV_CMD.DO_REPEAT_SERVO, options,
          p1: Math.Clamp(options.ServoNumber, 1, 16),
          p2: Math.Clamp(options.ServoPwm, 800, 2200),
          p3: repetitions,
          p4: Math.Max(0, options.ServoRepeatSeconds)));
      cameraCount++;
    }
  }

  private static double EntryAltitude(PointLatLngAlt point, PointLatLngAlt home,
      FaceMapMissionOptions options) {
    double reference = options.Frame == (byte)MAVLink.MAV_FRAME.GLOBAL
        ? Math.Max(home.Alt, point.Alt)
        : Math.Max(0, point.Alt);
    return reference + Math.Max(0, options.EntryClearance);
  }

  private static SurveyMissionCommand Command(MAVLink.MAV_CMD command,
      FaceMapMissionOptions options, double lat = 0, double lng = 0, double alt = 0,
      double p1 = 0, double p2 = 0, double p3 = 0, double p4 = 0) =>
      new(command, lat, lng, alt, p1, p2, p3, p4, options.Frame);

  private static bool SameLocation(PointLatLngAlt first, PointLatLngAlt second) =>
      Math.Abs(first.Lat - second.Lat) < 1e-9 &&
      Math.Abs(first.Lng - second.Lng) < 1e-9 &&
      Math.Abs(first.Alt - second.Alt) < 1e-6;

  private static double NormalizeHeading(double heading) {
    heading %= 360;
    return heading < 0 ? heading + 360 : heading;
  }

  private static void Validate(FaceMapMissionOptions options) {
    if (options.SplitCount is < 1 or > 300 ||
        !double.IsFinite(options.FlyingSpeed) || options.FlyingSpeed < 0 ||
        !double.IsFinite(options.TriggerDistance) || options.TriggerDistance < 0 ||
        !double.IsFinite(options.CopterDelay) || options.CopterDelay < 0 ||
        !double.IsFinite(options.CameraPitch) || options.CameraPitch is < 0 or > 90 ||
        !double.IsFinite(options.ToePitchStep) || options.ToePitchStep < 0 ||
        options.ToePointRuns is < 0 or > 10_000 ||
        !double.IsFinite(options.ServoRepeatSeconds) || options.ServoRepeatSeconds < 0 ||
        !double.IsFinite(options.RestoreSpeed) || options.RestoreSpeed < 0 ||
        !double.IsFinite(options.EntryClearance) || options.EntryClearance < 0) {
      throw new ArgumentOutOfRangeException(nameof(options),
          "Face Map mission options are outside their supported range.");
    }
    if (options.UseSpeed && options.FlyingSpeed <= 0) {
      throw new ArgumentOutOfRangeException(nameof(options),
          "Flying speed must be greater than zero when speed control is enabled.");
    }
    if (options.TriggerMode == TriggerDistance && options.TriggerDistance <= 0) {
      throw new ArgumentOutOfRangeException(nameof(options),
          "Camera trigger distance must be greater than zero.");
    }
    if (options.TriggerMode is not (TriggerNone or TriggerDistance or TriggerDigicam or
            TriggerRepeatServo or TriggerSetServo)) {
      throw new ArgumentOutOfRangeException(nameof(options), "Unknown Face Map trigger mode.");
    }
    if (options.FinishAction is not (FinishNone or FinishRtl or FinishLand)) {
      throw new ArgumentOutOfRangeException(nameof(options), "Unknown Face Map finish action.");
    }
    if (options.Frame == (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT) {
      throw new InvalidOperationException("Face Map does not support Terrain altitude mode.");
    }
  }

  private static bool ValidMissionPoint(PointLatLngAlt point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng) && double.IsFinite(point.Alt) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;
}
