using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot;

namespace MissionPlannerAvalonia.Services;

internal enum WaypointLeaderMode {
  Idle,
  Takeoff,
  FlyToGroundMaster,
  FollowGroundMaster,
  ReturnAlongMission,
  LandAltitude,
  Landing,
}

internal sealed record WaypointLeaderFollower(FormationVehicleId Id, int Order);

internal sealed record WaypointLeaderSettings(
    double SeparationM,
    double LeadM,
    double OffPathTriggerM,
    double TakeoffLandAltitudeSeparationM,
    double NavigationAccelerationMps2,
    bool VFormation,
    bool AltitudeInterleave);

internal sealed record WaypointLeaderPlan(
    FormationVehicleId GroundMaster,
    FormationVehicleId AirMaster,
    IReadOnlyList<WaypointLeaderFollower> Followers,
    WaypointLeaderSettings Settings,
    string MissionSignature);

public readonly record struct WaypointLeaderProfilePoint(double DistanceM, double AltitudeM);

internal sealed record WaypointLeaderCommand(
    FormationVehicleSource Vehicle,
    FollowPathPoint Target,
    double VelocityNorth,
    double VelocityEast,
    double VelocityDown);

internal readonly record struct WaypointLeaderTickResult(
    bool Continue,
    WaypointLeaderMode Mode,
    string Status,
    IReadOnlyList<WaypointLeaderCommand> Commands) {
  internal static WaypointLeaderTickResult Stop(WaypointLeaderMode mode, string status) =>
      new(false, mode, status, []);

  internal static WaypointLeaderTickResult Active(
      WaypointLeaderMode mode,
      string status,
      IReadOnlyList<WaypointLeaderCommand>? commands = null) =>
      new(true, mode, status, commands ?? []);
}

/// <summary>
/// Exact, compact representation of the official WaypointLeader mission profile. The upstream
/// implementation expands every mission leg into 0.1 m samples; this preserves the same linear
/// path/altitude semantics without allocating hundreds of thousands of temporary points.
/// </summary>
internal sealed class WaypointLeaderMissionPath {
  internal const double MaximumMissionSegmentM = 5000;

  private readonly IReadOnlyList<PathVertex> _vertices;

  private WaypointLeaderMissionPath(IReadOnlyList<PathVertex> vertices, string signature) {
    _vertices = vertices;
    Signature = signature;
    LengthM = vertices[^1].DistanceM;
    Profile = vertices
        .Select(vertex => new WaypointLeaderProfilePoint(
            vertex.DistanceM, vertex.Point.Altitude))
        .ToArray();
  }

  internal string Signature { get; }
  internal double LengthM { get; }
  internal IReadOnlyList<WaypointLeaderProfilePoint> Profile { get; }
  internal FollowPathPoint Start => _vertices[0].Point;
  internal FollowPathPoint End => _vertices[^1].Point;

  internal static bool TryBuild(
      MAVState state, out WaypointLeaderMissionPath path, out string error) {
    path = null!;
    KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>[] mission = state.wps
        .ToArray()
        .OrderBy(item => item.Key)
        .ToArray();
    if (mission.Length < 2) {
      error = "air master has no downloaded waypoint mission.";
      return false;
    }

    string signature = SignatureOf(mission);
    MAVLink.mavlink_mission_item_int_t? previous = null;
    var vertices = new List<PathVertex>();
    double cumulative = 0;
    foreach ((_, MAVLink.mavlink_mission_item_int_t item) in mission) {
      if (previous == null) {
        previous = item;
        continue;
      }
      if (item.command != (ushort)MAVLink.MAV_CMD.WAYPOINT &&
          item.command != (ushort)MAVLink.MAV_CMD.SPLINE_WAYPOINT) {
        continue;
      }

      FollowPathPoint startPoint = MissionPoint(previous.Value);
      FollowPathPoint to = MissionPoint(item);
      if (!FollowPathTrail.IsValid(startPoint with {
        Altitude = Math.Abs(startPoint.Altitude) <= double.Epsilon
                  ? to.Altitude
                  : startPoint.Altitude,
      }) || !FollowPathTrail.IsValid(to)) {
        error = "air-master mission contains an invalid waypoint coordinate or altitude.";
        return false;
      }
      double segment = FormationGeometry.DistanceAndBearing(
          startPoint.Latitude, startPoint.Longitude, to.Latitude, to.Longitude).Distance;
      if (!double.IsFinite(segment) || segment <= FollowPathTrail.MinimumSampleDistanceM) {
        previous = item;
        continue;
      }
      if (segment > MaximumMissionSegmentM) {
        // Match upstream: ignore the excessive leg and keep the previous accepted waypoint.
        continue;
      }

      double startAltitude = Math.Abs(startPoint.Altitude) <= double.Epsilon
          ? to.Altitude
          : startPoint.Altitude;
      if (vertices.Count == 0) {
        vertices.Add(new PathVertex(0, startPoint with { Altitude = startAltitude }));
      } else {
        FollowPathPoint last = vertices[^1].Point;
        if (FormationGeometry.DistanceAndBearing(
                last.Latitude, last.Longitude,
                startPoint.Latitude, startPoint.Longitude).Distance > 0.5) {
          error = "air-master mission has a discontinuous waypoint path.";
          return false;
        }
      }
      cumulative += segment;
      vertices.Add(new PathVertex(cumulative, to));
      previous = item;
    }

    if (vertices.Count < 2 || cumulative <= FollowPathTrail.MinimumSampleDistanceM) {
      error = "air-master mission contains no usable waypoint legs under 5 km.";
      return false;
    }
    path = new WaypointLeaderMissionPath(vertices, signature);
    error = "";
    return true;
  }

  internal bool TryClosest(
      FollowPathPoint location, out double distanceAlongM, out double distanceFromPathM) {
    distanceAlongM = 0;
    distanceFromPathM = double.PositiveInfinity;
    if (!FollowPathTrail.IsValid(location)) {
      return false;
    }

    for (int index = 1; index < _vertices.Count; index++) {
      PathVertex start = _vertices[index - 1];
      PathVertex end = _vertices[index];
      double segmentLength = end.DistanceM - start.DistanceM;
      (double distance, double bearingToLocation) = FormationGeometry.DistanceAndBearing(
          start.Point.Latitude, start.Point.Longitude,
          location.Latitude, location.Longitude);
      (_, double segmentBearing) = FormationGeometry.DistanceAndBearing(
          start.Point.Latitude, start.Point.Longitude,
          end.Point.Latitude, end.Point.Longitude);
      double along = Math.Clamp(
          Math.Cos(WrapRadians(bearingToLocation - segmentBearing)) * distance,
          0, segmentLength);
      (double latitude, double longitude) = FormationGeometry.Project(
          start.Point.Latitude, start.Point.Longitude, segmentBearing, along);
      double crossTrack = FormationGeometry.DistanceAndBearing(
          latitude, longitude, location.Latitude, location.Longitude).Distance;
      if (crossTrack < distanceFromPathM) {
        distanceFromPathM = crossTrack;
        distanceAlongM = start.DistanceM + along;
      }
    }
    return double.IsFinite(distanceFromPathM);
  }

  internal bool TryPointAt(double distanceM, out FollowPathPoint point) {
    point = default;
    if (!double.IsFinite(distanceM) || distanceM < 0 || distanceM > LengthM) {
      return false;
    }
    if (distanceM <= double.Epsilon) {
      point = Start;
      return true;
    }
    for (int index = 1; index < _vertices.Count; index++) {
      PathVertex start = _vertices[index - 1];
      PathVertex end = _vertices[index];
      if (distanceM > end.DistanceM && index < _vertices.Count - 1) {
        continue;
      }
      double segmentLength = end.DistanceM - start.DistanceM;
      double offset = Math.Clamp(distanceM - start.DistanceM, 0, segmentLength);
      double ratio = segmentLength <= double.Epsilon ? 0 : offset / segmentLength;
      (_, double bearing) = FormationGeometry.DistanceAndBearing(
          start.Point.Latitude, start.Point.Longitude,
          end.Point.Latitude, end.Point.Longitude);
      (double latitude, double longitude) = FormationGeometry.Project(
          start.Point.Latitude, start.Point.Longitude, bearing, offset);
      point = new FollowPathPoint(latitude, longitude,
          start.Point.Altitude + (end.Point.Altitude - start.Point.Altitude) * ratio);
      return true;
    }
    return false;
  }

  internal bool TryLineTargets(
      FollowPathPoint reference,
      double leadM,
      double separationM,
      int count,
      out IReadOnlyList<FollowPathPoint> targets) {
    targets = [];
    if (count <= 0 || !TryClosest(reference, out double referenceDistance, out _)) {
      return false;
    }
    var result = new List<FollowPathPoint>(count);
    for (int index = 0; index < count; index++) {
      double distance = referenceDistance + leadM - index * separationM;
      if (!TryPointAt(distance, out FollowPathPoint target)) {
        return false;
      }
      result.Add(target);
    }
    targets = result;
    return true;
  }

  internal bool TryVTargets(
      FollowPathPoint reference,
      double leadM,
      double separationM,
      int count,
      out IReadOnlyList<FollowPathPoint> targets) {
    targets = [];
    if (count <= 0 || !TryClosest(reference, out double referenceDistance, out _)) {
      return false;
    }
    var result = new List<FollowPathPoint>(count);
    if (!TryPointAt(referenceDistance + leadM, out FollowPathPoint front)) {
      return false;
    }
    result.Add(front);
    for (int index = 1; index < count; index++) {
      int rank = (index + 1) / 2;
      double centerDistance = referenceDistance + leadM - rank * separationM;
      if (!TryPointAt(centerDistance, out FollowPathPoint center)) {
        return false;
      }
      (_, double forwardBearing) = FormationGeometry.DistanceAndBearing(
          center.Latitude, center.Longitude, front.Latitude, front.Longitude);
      double lateralBearing = forwardBearing + (index % 2 == 1 ? Math.PI / 2 : -Math.PI / 2);
      (double latitude, double longitude) = FormationGeometry.Project(
          center.Latitude, center.Longitude,
          lateralBearing, separationM / 2 * rank);
      result.Add(center with { Latitude = latitude, Longitude = longitude });
    }
    targets = result;
    return true;
  }

  private static FollowPathPoint MissionPoint(MAVLink.mavlink_mission_item_int_t item) =>
      new(item.x / 1e7, item.y / 1e7, item.z);

  private static string SignatureOf(
      IReadOnlyList<KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>> mission) {
    var builder = new StringBuilder(mission.Count * 64);
    foreach ((int index, MAVLink.mavlink_mission_item_int_t item) in mission) {
      builder.Append(index).Append(':')
          .Append(item.command).Append(':').Append(item.frame).Append(':')
          .Append(item.x).Append(':').Append(item.y).Append(':')
          .Append(BitConverter.SingleToInt32Bits(item.z)).Append(';');
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
  }

  private static double WrapRadians(double radians) {
    while (radians > Math.PI) {
      radians -= Math.PI * 2;
    }
    while (radians < -Math.PI) {
      radians += Math.PI * 2;
    }
    return radians;
  }

  private sealed record PathVertex(double DistanceM, FollowPathPoint Point);
}

internal interface IWaypointLeaderCommandSink {
  void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles);
  bool SetParameter(FormationVehicleSource vehicle, string name, double value);
  bool Arm(FormationVehicleSource vehicle, bool arm);
  void SetMode(FormationVehicleSource vehicle, string mode);
  bool Takeoff(FormationVehicleSource vehicle, double altitudeM);
  void SendTarget(WaypointLeaderCommand command);
}

internal sealed class MavlinkWaypointLeaderCommandSink : IWaypointLeaderCommandSink {
  private readonly MavlinkFormationCommandSink _common = new();

  public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) {
    foreach (FormationVehicleSource vehicle in vehicles) {
      vehicle.Id.Link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, 5,
          vehicle.Id.SystemId, vehicle.Id.ComponentId);
      vehicle.State.cs.rateposition = 5;
    }
  }

  public bool SetParameter(FormationVehicleSource vehicle, string name, double value) =>
      vehicle.Id.Link.setParam(
          vehicle.Id.SystemId, vehicle.Id.ComponentId, name, value);

  public bool Arm(FormationVehicleSource vehicle, bool arm) => _common.Arm(vehicle, arm);

  public void SetMode(FormationVehicleSource vehicle, string mode) => _common.SetMode(vehicle, mode);

  public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) =>
      _common.Takeoff(vehicle, altitudeM);

  public void SendTarget(WaypointLeaderCommand command) {
#pragma warning disable CS0612 // Required MAVLink SET_POSITION_TARGET_GLOBAL_INT coordinate frame.
    command.Vehicle.Id.Link.setPositionTargetGlobalInt(
        command.Vehicle.Id.SystemId, command.Vehicle.Id.ComponentId,
        true, true, false, false,
        MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
        command.Target.Latitude, command.Target.Longitude, command.Target.Altitude,
        command.VelocityNorth, command.VelocityEast, command.VelocityDown, 0, 0);
#pragma warning restore CS0612
  }
}

internal sealed class WaypointLeaderCommandRunner {
  internal const int MaximumOrder = 20;
  internal const double MinimumSeparationM = 2;
  internal const double MaximumSeparationM = 500;
  internal const double MinimumLeadM = -500;
  internal const double MaximumLeadM = 5000;
  internal const double MinimumOffPathTriggerM = 1;
  internal const double MaximumOffPathTriggerM = 5000;
  internal const double MinimumAltitudeSeparationM = 1;
  internal const double MaximumAltitudeSeparationM = 100;
  internal const double MinimumNavigationAccelerationMps2 = 0.1;
  internal const double MaximumNavigationAccelerationMps2 = 100;

  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IWaypointLeaderCommandSink _sink;
  private readonly HashSet<FormationVehicleId> _takeoffIssued = [];
  private readonly Dictionary<FormationVehicleId, FollowPathPoint> _lastTargets = [];
  private readonly object _modeSync = new();
  private WaypointLeaderMode _mode = WaypointLeaderMode.Idle;
  private WaypointLeaderMode? _requestedMode;
  private bool _initialParametersConfigured;
  private bool _returnParametersConfigured;
  private bool _rtlIssued;

  internal WaypointLeaderCommandRunner(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IWaypointLeaderCommandSink sink) {
    _snapshot = snapshot;
    _sink = sink;
  }

  internal WaypointLeaderMode Mode => _mode;

  internal void RequestMode(WaypointLeaderMode mode) {
    if (mode is not (WaypointLeaderMode.Idle or WaypointLeaderMode.ReturnAlongMission or
        WaypointLeaderMode.LandAltitude)) {
      throw new ArgumentOutOfRangeException(nameof(mode));
    }
    lock (_modeSync) {
      _requestedMode = mode;
    }
  }

  internal WaypointLeaderTickResult Tick(WaypointLeaderPlan plan, DateTime nowUtc) {
    if (!TryResolvePlan(plan, nowUtc, out FormationVehicleSource ground,
        out FormationVehicleSource air, out List<FormationVehicleSource> flight,
        out WaypointLeaderMissionPath path, out string error)) {
      return WaypointLeaderTickResult.Stop(
          _mode, "Waypoint Leader stopped: " + error);
    }

    ApplyRequestedMode();
    if (TryCollisionAvoidance(plan.Settings, flight, out List<WaypointLeaderCommand> emergency)) {
      Send(emergency);
      return WaypointLeaderTickResult.Active(
          _mode, "Collision separation override active; normal progression is paused.", emergency);
    }

    return _mode switch {
      WaypointLeaderMode.Idle => Initialize(plan.Settings, ground, flight),
      WaypointLeaderMode.Takeoff => Takeoff(plan.Settings, air, flight, path),
      WaypointLeaderMode.FlyToGroundMaster =>
          FlyToGroundMaster(plan.Settings, ground, air, flight, path),
      WaypointLeaderMode.FollowGroundMaster =>
          FollowGroundMaster(plan.Settings, ground, flight, path),
      WaypointLeaderMode.ReturnAlongMission =>
          ReturnAlongMission(plan.Settings, air, flight, path),
      WaypointLeaderMode.LandAltitude => LandAltitude(plan.Settings, flight),
      WaypointLeaderMode.Landing => Landing(flight),
      _ => WaypointLeaderTickResult.Stop(_mode, "Waypoint Leader stopped: invalid state."),
    };
  }

  internal async Task<string> RunAsync(
      WaypointLeaderPlan plan,
      Action<WaypointLeaderTickResult>? progress,
      CancellationToken cancellationToken) {
    if (!TryResolvePlan(plan, DateTime.UtcNow, out FormationVehicleSource ground,
        out _, out List<FormationVehicleSource> flight,
        out _, out string error)) {
      return "Waypoint Leader could not start: " + error;
    }
    _sink.RequestPositionStreams([ground, .. flight]);

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    while (!cancellationToken.IsCancellationRequested) {
      WaypointLeaderTickResult result;
      try {
        result = Tick(plan, DateTime.UtcNow);
      } catch (Exception ex) {
        return "Waypoint Leader stopped after a command error: " + UserMessage(ex);
      }
      progress?.Invoke(result);
      if (!result.Continue) {
        return result.Status;
      }
      try {
        if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
          break;
        }
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        break;
      }
    }
    return "Waypoint Leader stopped by operator.";
  }

  internal bool TryResolvePlan(
      WaypointLeaderPlan plan,
      DateTime nowUtc,
      out FormationVehicleSource ground,
      out FormationVehicleSource air,
      out List<FormationVehicleSource> flight,
      out WaypointLeaderMissionPath path,
      out string error) {
    ground = null!;
    air = null!;
    flight = [];
    path = null!;
    if (!ValidateSettings(plan.Settings, out error)) {
      return false;
    }
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    if (!FormationCommandRunner.TryResolveAutopilot(
            sources, plan.GroundMaster, nowUtc, out ground, out error)) {
      error = "ground master " + error;
      return false;
    }
    if (!FormationCommandRunner.HasPosition(ground.State)) {
      error = "ground-master position is unavailable.";
      return false;
    }
    if (!TryResolveFlightVehicle(sources, plan.AirMaster, nowUtc, out air, out error)) {
      error = "air master " + error;
      return false;
    }
    if (plan.GroundMaster == plan.AirMaster) {
      error = "ground master and air master must be different vehicles.";
      return false;
    }
    if (!WaypointLeaderMissionPath.TryBuild(air.State, out path, out error)) {
      return false;
    }
    if (!string.Equals(path.Signature, plan.MissionSignature, StringComparison.Ordinal)) {
      error = "air-master mission changed after the plan was confirmed.";
      return false;
    }

    var identities = new HashSet<FormationVehicleId> { plan.GroundMaster, plan.AirMaster };
    var orders = new HashSet<int>();
    var resolvedFollowers = new List<(FormationVehicleSource Source, int Order)>();
    foreach (WaypointLeaderFollower planned in plan.Followers) {
      if (!identities.Add(planned.Id)) {
        error = "a master was also selected as follower, or a follower is duplicated.";
        return false;
      }
      if (planned.Order is < 1 or > MaximumOrder || !orders.Add(planned.Order)) {
        error = $"follower order must be unique and between 1 and {MaximumOrder}.";
        return false;
      }
      if (!TryResolveFlightVehicle(
              sources, planned.Id, nowUtc, out FormationVehicleSource follower, out error)) {
        error = "follower " + error;
        return false;
      }
      resolvedFollowers.Add((follower, planned.Order));
    }
    flight.Add(air);
    flight.AddRange(resolvedFollowers.OrderBy(item => item.Order).Select(item => item.Source));

    if (flight.Any(vehicle => !FormationCommandRunner.HasPosition(vehicle.State))) {
      error = "an air vehicle position is unavailable.";
      return false;
    }
    if (!HasFiniteVelocity(ground) || flight.Any(vehicle => !HasFiniteVelocity(vehicle))) {
      error = "a vehicle velocity is invalid.";
      return false;
    }
    double takeoffLead = (flight.Count + 1) * plan.Settings.SeparationM;
    if (!path.TryLineTargets(path.Start, takeoffLead,
            plan.Settings.SeparationM, flight.Count, out _)) {
      error = "air-master mission is too short to stage all selected air vehicles.";
      return false;
    }
    error = "";
    return true;
  }

  internal static bool ValidateSettings(WaypointLeaderSettings settings, out string error) {
    if (!InRange(settings.SeparationM, MinimumSeparationM, MaximumSeparationM)) {
      error = $"separation must be {MinimumSeparationM:0}–{MaximumSeparationM:0} m.";
      return false;
    }
    if (!InRange(settings.LeadM, MinimumLeadM, MaximumLeadM)) {
      error = $"lead must be {MinimumLeadM:0}–{MaximumLeadM:0} m.";
      return false;
    }
    if (!InRange(settings.OffPathTriggerM,
            MinimumOffPathTriggerM, MaximumOffPathTriggerM)) {
      error = $"off-path trigger must be {MinimumOffPathTriggerM:0}–" +
          $"{MaximumOffPathTriggerM:0} m.";
      return false;
    }
    if (!InRange(settings.TakeoffLandAltitudeSeparationM,
            MinimumAltitudeSeparationM, MaximumAltitudeSeparationM)) {
      error = $"altitude separation must be {MinimumAltitudeSeparationM:0}–" +
          $"{MaximumAltitudeSeparationM:0} m.";
      return false;
    }
    if (!InRange(settings.NavigationAccelerationMps2,
            MinimumNavigationAccelerationMps2, MaximumNavigationAccelerationMps2)) {
      error = $"navigation acceleration must be {MinimumNavigationAccelerationMps2:0.0}–" +
          $"{MaximumNavigationAccelerationMps2:0} m/s².";
      return false;
    }
    error = "";
    return true;
  }

  internal static bool TryResolveFlightVehicle(
      IReadOnlyList<FormationVehicleSource> sources,
      FormationVehicleId id,
      DateTime nowUtc,
      out FormationVehicleSource source,
      out string error) {
    if (!FormationCommandRunner.TryResolveAutopilot(
            sources, id, nowUtc, out source, out error)) {
      return false;
    }
    if (!source.SupportsWaypointLeaderFlight) {
      error = $"{source.Label} firmware {source.State.cs.firmware} is unsupported; " +
          "the official WaypointLeader takeoff/RTL state machine requires ArduCopter.";
      return false;
    }
    error = "";
    return true;
  }

  private WaypointLeaderTickResult Initialize(
      WaypointLeaderSettings settings,
      FormationVehicleSource ground,
      IReadOnlyList<FormationVehicleSource> flight) {
    if (!_initialParametersConfigured) {
      foreach (FormationVehicleSource vehicle in flight) {
        if (!SetFirstAvailable(vehicle, [("RTL_ALT", 0), ("RTL_ALT_M", 0)]) ||
            !SetFirstAvailable(vehicle, [("WPNAV_ACCEL", 100), ("WP_ACC", 1)])) {
          return WaypointLeaderTickResult.Stop(
              _mode, $"Waypoint Leader stopped: {vehicle.Label} rejected initial parameters.");
        }
      }
      _initialParametersConfigured = true;
    }
    _takeoffIssued.Clear();
    _lastTargets.Clear();
    _returnParametersConfigured = false;
    _rtlIssued = false;
    _mode = WaypointLeaderMode.Takeoff;
    return WaypointLeaderTickResult.Active(
        _mode,
        $"Waypoint Leader initialized: ground master {ground.Label}; beginning staged takeoff.");
  }

  private WaypointLeaderTickResult Takeoff(
      WaypointLeaderSettings settings,
      FormationVehicleSource air,
      IReadOnlyList<FormationVehicleSource> flight,
      WaypointLeaderMissionPath path) {
    double lead = (flight.Count + 1) * settings.SeparationM;
    if (!path.TryLineTargets(path.Start, lead, settings.SeparationM,
            flight.Count, out IReadOnlyList<FollowPathPoint> positions)) {
      return WaypointLeaderTickResult.Stop(
          _mode, "Waypoint Leader stopped: mission no longer fits takeoff staging.");
    }

    var commands = new List<WaypointLeaderCommand>(flight.Count);
    bool allAtAltitude = true;
    bool allAtTarget = true;
    for (int index = 0; index < flight.Count; index++) {
      FormationVehicleSource vehicle = flight[index];
      FollowPathPoint target = positions[index] with {
        Altitude = positions[index].Altitude +
            settings.TakeoffLandAltitudeSeparationM * (index % 3),
      };
      _lastTargets[vehicle.Id] = target;
      if (!string.Equals(vehicle.State.cs.mode, "GUIDED", StringComparison.OrdinalIgnoreCase)) {
        _sink.SetMode(vehicle, "GUIDED");
      }
      if (!vehicle.State.cs.armed && !_sink.Arm(vehicle, true)) {
        return WaypointLeaderTickResult.Stop(
            _mode, $"Waypoint Leader stopped: {vehicle.Label} rejected ARM.");
      }
      if (vehicle.State.cs.alt < target.Altitude - 0.5) {
        allAtAltitude = false;
        allAtTarget = false;
        if (_takeoffIssued.Add(vehicle.Id) && !_sink.Takeoff(vehicle, target.Altitude)) {
          return WaypointLeaderTickResult.Stop(
              _mode, $"Waypoint Leader stopped: {vehicle.Label} rejected TAKEOFF.");
        }
        continue;
      }
      WaypointLeaderCommand command = Command(vehicle, target, 0, 0, 0);
      commands.Add(command);
      if (Distance(vehicle, target) > settings.SeparationM) {
        allAtTarget = false;
      }
    }
    Send(commands);
    if (allAtAltitude && allAtTarget) {
      _mode = WaypointLeaderMode.FlyToGroundMaster;
      return WaypointLeaderTickResult.Active(
          _mode, "Staged takeoff complete; flying the formation toward the ground master.", commands);
    }
    return WaypointLeaderTickResult.Active(
        _mode,
        $"Staged takeoff: {commands.Count}/{flight.Count} vehicle(s) at target altitude.",
        commands);
  }

  private WaypointLeaderTickResult FlyToGroundMaster(
      WaypointLeaderSettings settings,
      FormationVehicleSource ground,
      FormationVehicleSource air,
      IReadOnlyList<FormationVehicleSource> flight,
      WaypointLeaderMissionPath path) {
    if (!TryTargets(path, Point(air), settings.SeparationM, settings,
            flight.Count, vFormation: false, out IReadOnlyList<FollowPathPoint> targets)) {
      if (Distance(Point(air), path.End) < settings.SeparationM) {
        _mode = WaypointLeaderMode.ReturnAlongMission;
        return WaypointLeaderTickResult.Active(
            _mode, "Air master reached the mission end before the ground master; returning.");
      }
      return WaypointLeaderTickResult.Active(
          _mode, "Waiting for enough mission path around the air master.");
    }
    List<WaypointLeaderCommand> commands = Commands(flight, targets, settings, 0, 0, 0);
    Send(commands);

    if (TryTargets(path, Point(ground), settings.LeadM, settings,
            flight.Count, settings.VFormation, out IReadOnlyList<FollowPathPoint> followTargets) &&
        Distance(Point(air), followTargets[0]) < settings.SeparationM) {
      if (!ConfigureNavigationAcceleration(flight, settings.NavigationAccelerationMps2)) {
        return WaypointLeaderTickResult.Stop(
            _mode, "Waypoint Leader stopped: a vehicle rejected follow-mode acceleration.");
      }
      foreach (FormationVehicleSource vehicle in flight) {
        _sink.SetMode(vehicle, "GUIDED");
      }
      _mode = WaypointLeaderMode.FollowGroundMaster;
      return WaypointLeaderTickResult.Active(
          _mode, "Formation reached the ground master and entered follow mode.", commands);
    }
    return WaypointLeaderTickResult.Active(
        _mode, $"Flying toward ground master; {commands.Count} target(s) sent.", commands);
  }

  private WaypointLeaderTickResult FollowGroundMaster(
      WaypointLeaderSettings settings,
      FormationVehicleSource ground,
      IReadOnlyList<FormationVehicleSource> flight,
      WaypointLeaderMissionPath path) {
    if (!path.TryClosest(Point(ground), out _, out double offPath)) {
      return WaypointLeaderTickResult.Stop(
          _mode, "Waypoint Leader stopped: ground-master path distance is unavailable.");
    }
    if (offPath > settings.OffPathTriggerM) {
      _mode = WaypointLeaderMode.ReturnAlongMission;
      return WaypointLeaderTickResult.Active(
          _mode,
          $"Ground master is {offPath:0.0} m off path; switching to return mode.");
    }
    if (!TryTargets(path, Point(ground), settings.LeadM, settings,
            flight.Count, settings.VFormation, out IReadOnlyList<FollowPathPoint> targets)) {
      return WaypointLeaderTickResult.Active(
          _mode, "Waiting for complete follow targets within the mission path.");
    }
    double north = ground.State.cs.vx / 3;
    double east = ground.State.cs.vy / 3;
    double down = ground.State.cs.vz / 3;
    List<WaypointLeaderCommand> commands = Commands(
        flight, targets, settings, north, east, down);
    Send(commands);
    return WaypointLeaderTickResult.Active(
        _mode,
        $"Following ground master: {commands.Count} target(s); off path {offPath:0.0} m.",
        commands);
  }

  private WaypointLeaderTickResult ReturnAlongMission(
      WaypointLeaderSettings settings,
      FormationVehicleSource air,
      IReadOnlyList<FormationVehicleSource> flight,
      WaypointLeaderMissionPath path) {
    if (!_returnParametersConfigured) {
      if (!ConfigureNavigationAcceleration(flight, 1)) {
        return WaypointLeaderTickResult.Stop(
            _mode, "Waypoint Leader stopped: a vehicle rejected return-mode acceleration.");
      }
      _returnParametersConfigured = true;
    }
    if (!TryTargets(path, Point(air), settings.SeparationM, settings,
            flight.Count, vFormation: false, out IReadOnlyList<FollowPathPoint> targets)) {
      if (Distance(Point(air), path.End) < settings.SeparationM) {
        PrepareLandingTargets(flight);
        _mode = WaypointLeaderMode.LandAltitude;
        return WaypointLeaderTickResult.Active(
            _mode, "Mission end reached; establishing separated RTL altitudes.");
      }
      return WaypointLeaderTickResult.Active(
          _mode, "Returning along mission; waiting for complete formation targets.");
    }
    List<WaypointLeaderCommand> commands = Commands(flight, targets, settings, 0, 0, 0);
    Send(commands);
    return WaypointLeaderTickResult.Active(
        _mode, $"Returning along mission: {commands.Count} target(s) sent.", commands);
  }

  private WaypointLeaderTickResult LandAltitude(
      WaypointLeaderSettings settings,
      IReadOnlyList<FormationVehicleSource> flight) {
    if (_lastTargets.Count != flight.Count) {
      PrepareLandingTargets(flight);
    }
    var commands = new List<WaypointLeaderCommand>(flight.Count);
    bool allReady = true;
    for (int index = 0; index < flight.Count; index++) {
      FormationVehicleSource vehicle = flight[index];
      FollowPathPoint baseTarget = _lastTargets[vehicle.Id];
      FollowPathPoint target = baseTarget with {
        Altitude = baseTarget.Altitude + settings.TakeoffLandAltitudeSeparationM * index,
      };
      commands.Add(Command(vehicle, target, 0, 0, 0));
      if (vehicle.State.cs.armed && vehicle.State.cs.alt < target.Altitude - 0.5) {
        allReady = false;
      }
    }
    Send(commands);
    if (!allReady) {
      return WaypointLeaderTickResult.Active(
          _mode, "Establishing separated RTL altitudes before landing.", commands);
    }
    if (!_rtlIssued) {
      foreach (FormationVehicleSource vehicle in flight) {
        _sink.SetMode(vehicle, "RTL");
      }
      _rtlIssued = true;
    }
    _mode = WaypointLeaderMode.Landing;
    return WaypointLeaderTickResult.Active(
        _mode, "All air vehicles reached separated altitudes; RTL landing issued.", commands);
  }

  private WaypointLeaderTickResult Landing(IReadOnlyList<FormationVehicleSource> flight) {
    int armed = flight.Count(vehicle => vehicle.State.cs.armed);
    if (armed == 0) {
      return WaypointLeaderTickResult.Stop(
          _mode, "Waypoint Leader completed: all commanded air vehicles are disarmed.");
    }
    foreach (FormationVehicleSource vehicle in flight.Where(vehicle => vehicle.State.cs.armed)) {
      if (!string.Equals(vehicle.State.cs.mode, "RTL", StringComparison.OrdinalIgnoreCase)) {
        _sink.SetMode(vehicle, "RTL");
        break;
      }
    }
    return WaypointLeaderTickResult.Active(
        _mode, $"RTL landing in progress: {armed} air vehicle(s) still armed.");
  }

  private bool TryCollisionAvoidance(
      WaypointLeaderSettings settings,
      IReadOnlyList<FormationVehicleSource> flight,
      out List<WaypointLeaderCommand> commands) {
    commands = [];
    FormationVehicleSource[] armed = flight.Where(vehicle => vehicle.State.cs.armed).ToArray();
    for (int first = 0; first < armed.Length; first++) {
      for (int second = first + 1; second < armed.Length; second++) {
        FormationVehicleSource left = armed[first];
        FormationVehicleSource right = armed[second];
        if (Distance(Point(left), Point(right)) < settings.SeparationM / 2 &&
            Math.Abs(left.State.cs.alt - right.State.cs.alt) < 1) {
          FormationVehicleSource climb = left.State.cs.alt > right.State.cs.alt ? left : right;
          FollowPathPoint target = Point(climb) with {
            Altitude = climb.State.cs.alt + settings.TakeoffLandAltitudeSeparationM,
          };
          commands.Add(Command(climb, target, 0, 0, 0));
          return true;
        }

        FollowPathPoint leftProjected = ProjectOneSecond(left);
        FollowPathPoint rightProjected = ProjectOneSecond(right);
        if (Distance(leftProjected, rightProjected) >= settings.SeparationM / 2 ||
            Math.Abs(left.State.cs.alt - right.State.cs.alt) >= 1) {
          continue;
        }
        double headingDifference = HeadingDifference(left, right);
        if (headingDifference < 45 && left.State.cs.groundspeed > 0.5) {
          commands.Add(Command(left, Point(left), 0, 0, 0));
          return true;
        }
        if (headingDifference > 135) {
          commands.Add(Command(left, Point(left) with {
            Altitude = left.State.cs.alt + settings.TakeoffLandAltitudeSeparationM,
          }, 0, 0, 0));
          commands.Add(Command(right, Point(right), 0, 0, 0));
          return true;
        }
      }
    }
    return false;
  }

  private void ApplyRequestedMode() {
    WaypointLeaderMode? requested;
    lock (_modeSync) {
      requested = _requestedMode;
      _requestedMode = null;
    }
    if (requested == null) {
      return;
    }
    _mode = requested.Value;
    if (_mode == WaypointLeaderMode.Idle) {
      _initialParametersConfigured = false;
      _takeoffIssued.Clear();
      _lastTargets.Clear();
      _returnParametersConfigured = false;
      _rtlIssued = false;
    } else if (_mode == WaypointLeaderMode.ReturnAlongMission) {
      _returnParametersConfigured = false;
    } else if (_mode == WaypointLeaderMode.LandAltitude) {
      _rtlIssued = false;
    }
  }

  private bool ConfigureNavigationAcceleration(
      IReadOnlyList<FormationVehicleSource> flight, double accelerationMps2) {
    foreach (FormationVehicleSource vehicle in flight) {
      if (!SetFirstAvailable(vehicle,
              [("WPNAV_ACCEL", accelerationMps2 * 100), ("WP_ACC", accelerationMps2)])) {
        return false;
      }
    }
    return true;
  }

  private bool SetFirstAvailable(
      FormationVehicleSource vehicle,
      IReadOnlyList<(string Name, double Value)> candidates) {
    foreach ((string name, double value) in candidates) {
      if (vehicle.State.param.ContainsKey(name)) {
        return _sink.SetParameter(vehicle, name, value);
      }
    }
    return true;
  }

  private static bool TryTargets(
      WaypointLeaderMissionPath path,
      FollowPathPoint reference,
      double leadM,
      WaypointLeaderSettings settings,
      int count,
      bool vFormation,
      out IReadOnlyList<FollowPathPoint> targets) {
    bool found = vFormation
        ? path.TryVTargets(reference, leadM, settings.SeparationM, count, out targets)
        : path.TryLineTargets(reference, leadM, settings.SeparationM, count, out targets);
    if (!found || !settings.AltitudeInterleave) {
      return found;
    }
    targets = targets
        .Select((target, index) => target with {
          Altitude = target.Altitude +
              settings.TakeoffLandAltitudeSeparationM * (index % 2),
        })
        .ToArray();
    return true;
  }

  private List<WaypointLeaderCommand> Commands(
      IReadOnlyList<FormationVehicleSource> flight,
      IReadOnlyList<FollowPathPoint> targets,
      WaypointLeaderSettings settings,
      double north,
      double east,
      double down) {
    var commands = new List<WaypointLeaderCommand>(flight.Count);
    for (int index = 0; index < flight.Count; index++) {
      FollowPathPoint target = targets[index];
      _lastTargets[flight[index].Id] = settings.AltitudeInterleave
          ? target with {
            Altitude = target.Altitude -
                settings.TakeoffLandAltitudeSeparationM * (index % 2),
          }
          : target;
      commands.Add(Command(flight[index], target, north, east, down));
    }
    return commands;
  }

  private void PrepareLandingTargets(IReadOnlyList<FormationVehicleSource> flight) {
    for (int index = 0; index < flight.Count; index++) {
      FormationVehicleSource vehicle = flight[index];
      if (!_lastTargets.ContainsKey(vehicle.Id)) {
        _lastTargets[vehicle.Id] = Point(vehicle);
      }
    }
  }

  private void Send(IReadOnlyList<WaypointLeaderCommand> commands) {
    foreach (WaypointLeaderCommand command in commands) {
      _sink.SendTarget(command);
    }
  }

  private static WaypointLeaderCommand Command(
      FormationVehicleSource vehicle,
      FollowPathPoint target,
      double north,
      double east,
      double down) => new(vehicle, target, north, east, down);

  private static FollowPathPoint Point(FormationVehicleSource vehicle) =>
      new(vehicle.State.cs.lat, vehicle.State.cs.lng, vehicle.State.cs.alt);

  private static double Distance(FormationVehicleSource vehicle, FollowPathPoint point) =>
      Distance(Point(vehicle), point);

  private static double Distance(FollowPathPoint left, FollowPathPoint right) =>
      FormationGeometry.DistanceAndBearing(
          left.Latitude, left.Longitude, right.Latitude, right.Longitude).Distance;

  private static FollowPathPoint ProjectOneSecond(FormationVehicleSource vehicle) {
    double north = vehicle.State.cs.vx;
    double east = vehicle.State.cs.vy;
    double distance = Math.Sqrt(north * north + east * east);
    double bearing = Math.Atan2(east, north);
    (double latitude, double longitude) = FormationGeometry.Project(
        vehicle.State.cs.lat, vehicle.State.cs.lng, bearing, distance);
    return new FollowPathPoint(latitude, longitude,
        vehicle.State.cs.alt - vehicle.State.cs.vz);
  }

  private static double HeadingDifference(
      FormationVehicleSource left, FormationVehicleSource right) {
    double leftHeading = Math.Atan2(left.State.cs.vy, left.State.cs.vx) * 180 / Math.PI;
    double rightHeading = Math.Atan2(right.State.cs.vy, right.State.cs.vx) * 180 / Math.PI;
    double difference = Math.Abs(leftHeading - rightHeading) % 360;
    return difference > 180 ? 360 - difference : difference;
  }

  private static bool HasFiniteVelocity(FormationVehicleSource vehicle) =>
      double.IsFinite(vehicle.State.cs.vx) && double.IsFinite(vehicle.State.cs.vy) &&
      double.IsFinite(vehicle.State.cs.vz);

  private static bool InRange(double value, double minimum, double maximum) =>
      double.IsFinite(value) && value >= minimum && value <= maximum;

  private static string UserMessage(Exception exception) {
    Exception current = exception;
    while (current.InnerException != null &&
           current is AggregateException or System.Reflection.TargetInvocationException) {
      current = current.InnerException;
    }
    return string.IsNullOrWhiteSpace(current.Message)
        ? current.GetType().Name
        : current.Message;
  }
}
