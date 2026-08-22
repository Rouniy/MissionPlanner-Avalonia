using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct FollowLeaderVelocity(
    double North,
    double East,
    double Down) {
  internal FollowLeaderVelocity Scale(double factor) =>
      new(North * factor, East * factor, Down * factor);
}

internal sealed record FollowLeaderFollower(FormationVehicleId Id, int Order);

internal sealed record FollowLeaderSettings(
    double SeparationM,
    double LeadM,
    double AltitudeM);

internal sealed record FollowLeaderPlan(
    FormationVehicleId GroundMaster,
    FormationVehicleId AirMaster,
    IReadOnlyList<FollowLeaderFollower> Followers,
    FollowLeaderSettings Settings);

internal sealed record FollowLeaderCommand(
    FormationVehicleSource Vehicle,
    FollowPathPoint Target,
    FollowLeaderVelocity Velocity,
    string Role,
    double DistanceBehindM);

internal readonly record struct FollowLeaderTickResult(
    bool Continue,
    string Status,
    IReadOnlyList<FollowLeaderCommand> Commands) {
  internal static FollowLeaderTickResult Stop(string status) => new(false, status, []);

  internal static FollowLeaderTickResult Waiting(double available, double required) => new(
      true,
      $"Recording ground-master trail: {available:0.0} of {required:0.0} m required.",
      []);

  internal static FollowLeaderTickResult Sent(
      IReadOnlyList<FollowLeaderCommand> commands, double trailLength) => new(
      true,
      $"Follow Leader active: {commands.Count} target(s) sent; trail {trailLength:0.0} m.",
      commands);
}

internal interface IFollowLeaderCommandSink {
  void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles);
  void SendPositionVelocity(
      FormationVehicleSource vehicle,
      FollowPathPoint target,
      FollowLeaderVelocity velocity);
  bool Arm(FormationVehicleSource vehicle, bool arm);
  void SetMode(FormationVehicleSource vehicle, string mode);
  bool Takeoff(FormationVehicleSource vehicle, double altitudeM);
  bool EnableNavGuided(FormationVehicleSource vehicle);
}

internal sealed class MavlinkFollowLeaderCommandSink : IFollowLeaderCommandSink {
  private readonly MavlinkFormationCommandSink _common = new();

  public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) {
    foreach (FormationVehicleSource vehicle in vehicles) {
      vehicle.Id.Link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, 10,
          vehicle.Id.SystemId, vehicle.Id.ComponentId);
      vehicle.State.cs.rateposition = 10;
    }
  }

  public void SendPositionVelocity(
      FormationVehicleSource vehicle,
      FollowPathPoint target,
      FollowLeaderVelocity velocity) {
#pragma warning disable CS0612 // Required MAVLink SET_POSITION_TARGET_GLOBAL_INT coordinate frame.
    vehicle.Id.Link.setPositionTargetGlobalInt(
        vehicle.Id.SystemId, vehicle.Id.ComponentId,
        true, true, false, false,
        MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
        target.Latitude, target.Longitude, target.Altitude,
        velocity.North, velocity.East, velocity.Down, 0, 0);
#pragma warning restore CS0612
    vehicle.State.GuidedMode.x = (int)(target.Latitude * 1e7);
    vehicle.State.GuidedMode.y = (int)(target.Longitude * 1e7);
    vehicle.State.GuidedMode.z = (float)target.Altitude;
  }

  public bool Arm(FormationVehicleSource vehicle, bool arm) => _common.Arm(vehicle, arm);

  public void SetMode(FormationVehicleSource vehicle, string mode) => _common.SetMode(vehicle, mode);

  public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) =>
      _common.Takeoff(vehicle, altitudeM);

  public bool EnableNavGuided(FormationVehicleSource vehicle) =>
      vehicle.Id.Link.doCommand(
          vehicle.Id.SystemId, vehicle.Id.ComponentId,
          MAVLink.MAV_CMD.GUIDED_ENABLE, 1, 0, 0, 0, 0, 0, 0, false);
}

/// <summary>
/// Port of MissionPlanner.Swarm.FollowLeader.DroneGroup. The official path placement and velocity
/// factors are retained, while link identity, telemetry age and the complete command batch are
/// validated before any setpoint is emitted.
/// </summary>
internal sealed class FollowLeaderCommandRunner {
  internal const double MinimumSeparationM = 1;
  internal const double MaximumSeparationM = 500;
  internal const double MinimumAltitudeM = 1;
  internal const double MaximumAltitudeM = 10000;
  // The official controller prepends the current ground position to up to 20 historical
  // trail points, so it can command 21 followers in total.
  internal const int MaximumFollowers = 21;

  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowLeaderCommandSink _sink;
  private readonly FollowPathTrail _trail;

  internal FollowLeaderCommandRunner(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowLeaderCommandSink sink,
      FollowPathTrail? trail = null) {
    _snapshot = snapshot;
    _sink = sink;
    _trail = trail ?? new FollowPathTrail();
  }

  internal FollowLeaderTickResult Tick(FollowLeaderPlan plan, DateTime nowUtc) {
    if (!TryResolvePlan(plan, nowUtc, out FormationVehicleSource groundMaster,
        out FormationVehicleSource airMaster,
        out List<(FormationVehicleSource Source, int Order)> followers,
        out string error)) {
      return FollowLeaderTickResult.Stop("Follow Leader stopped: " + error);
    }

    var groundPoint = new FollowPathPoint(
        groundMaster.State.cs.lat,
        groundMaster.State.cs.lng,
        groundMaster.State.cs.alt);
    FollowPathTrailUpdate trailUpdate = _trail.Record(groundPoint);
    if (trailUpdate == FollowPathTrailUpdate.ResetAfterJump) {
      return FollowLeaderTickResult.Stop(
          "Follow Leader stopped: ground-master position jumped by more than 500 m; trail was reset.");
    }

    double requiredTrail = followers.Count == 0
        ? 0
        : (followers.Max(item => item.Order) - 1) * plan.Settings.SeparationM;
    var targets = new List<(FormationVehicleSource Source, int Order, FollowPathPoint Target)>();
    foreach ((FormationVehicleSource follower, int order) in followers.OrderBy(item => item.Order)) {
      double distance = (order - 1) * plan.Settings.SeparationM;
      if (!_trail.TryPointBehind(distance, out FollowPathPoint point)) {
        return FollowLeaderTickResult.Waiting(_trail.LengthM, requiredTrail);
      }
      targets.Add((follower, order, point with {
        Altitude = point.Altitude + plan.Settings.AltitudeM,
      }));
    }

    double bearingDegrees = GroundCourseDegrees(groundMaster);
    FollowLeaderVelocity groundVelocity = Velocity(groundMaster);
    FollowLeaderVelocity airVelocity = groundVelocity;
    if (TryMissionTurnBearing(groundMaster, plan.Settings.SeparationM,
            out double correctedBearing)) {
      bearingDegrees = correctedBearing;
      double radians = correctedBearing * Math.PI / 180.0;
      airVelocity = new FollowLeaderVelocity(
          Math.Cos(radians) * groundMaster.State.cs.groundspeed,
          Math.Sin(radians) * groundMaster.State.cs.groundspeed,
          groundVelocity.Down);
    }

    (double airLatitude, double airLongitude) = FormationGeometry.Project(
        groundPoint.Latitude,
        groundPoint.Longitude,
        bearingDegrees * Math.PI / 180.0,
        plan.Settings.SeparationM);
    var commands = new List<FollowLeaderCommand>(targets.Count + 1) {
      new(airMaster,
          new FollowPathPoint(airLatitude, airLongitude, plan.Settings.AltitudeM),
          airVelocity.Scale(0.6),
          "Air master",
          -plan.Settings.SeparationM),
    };
    commands.AddRange(targets.Select(item => new FollowLeaderCommand(
        item.Source,
        item.Target,
        groundVelocity.Scale(0.5),
        $"Follower #{item.Order}",
        (item.Order - 1) * plan.Settings.SeparationM)));

    foreach (FollowLeaderCommand command in commands) {
      _sink.SendPositionVelocity(command.Vehicle, command.Target, command.Velocity);
    }
    return FollowLeaderTickResult.Sent(commands, _trail.LengthM);
  }

  internal async Task<string> RunAsync(
      FollowLeaderPlan plan,
      Action<FollowLeaderTickResult>? progress,
      CancellationToken cancellationToken) {
    if (!TryResolvePlan(plan, DateTime.UtcNow, out FormationVehicleSource groundMaster,
        out FormationVehicleSource airMaster,
        out List<(FormationVehicleSource Source, int Order)> followers,
        out string error)) {
      return "Follow Leader could not start: " + error;
    }
    _sink.RequestPositionStreams(
        [groundMaster, airMaster, .. followers.Select(item => item.Source)]);

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    while (!cancellationToken.IsCancellationRequested) {
      FollowLeaderTickResult result;
      try {
        result = Tick(plan, DateTime.UtcNow);
      } catch (Exception ex) {
        return "Follow Leader stopped after a command error: " + UserMessage(ex);
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
    return "Follow Leader stopped by operator.";
  }

  internal bool TryResolvePlan(
      FollowLeaderPlan plan,
      DateTime nowUtc,
      out FormationVehicleSource groundMaster,
      out FormationVehicleSource airMaster,
      out List<(FormationVehicleSource Source, int Order)> followers,
      out string error) {
    groundMaster = null!;
    airMaster = null!;
    followers = [];
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    if (!FormationCommandRunner.TryResolveAutopilot(
            sources, plan.GroundMaster, nowUtc, out groundMaster, out error)) {
      error = "ground master " + error;
      return false;
    }
    if (!FormationCommandRunner.HasPosition(groundMaster.State)) {
      error = "ground-master position is unavailable.";
      return false;
    }
    if (!TryResolveFlightVehicle(sources, plan.AirMaster, nowUtc, out airMaster, out error)) {
      error = "air master " + error;
      return false;
    }
    if (!FormationCommandRunner.HasPosition(airMaster.State)) {
      error = "air-master position is unavailable.";
      return false;
    }
    if (plan.GroundMaster == plan.AirMaster) {
      error = "ground master and air master must be different vehicles.";
      return false;
    }
    FollowLeaderSettings settings = plan.Settings;
    if (!double.IsFinite(settings.SeparationM) ||
        settings.SeparationM is < MinimumSeparationM or > MaximumSeparationM) {
      error = $"separation must be {MinimumSeparationM:0}–{MaximumSeparationM:0} m.";
      return false;
    }
    if (!double.IsFinite(settings.AltitudeM) ||
        settings.AltitudeM is < MinimumAltitudeM or > MaximumAltitudeM) {
      error = $"altitude must be {MinimumAltitudeM:0}–{MaximumAltitudeM:0} m.";
      return false;
    }
    // The official dialog exposes Lead and stores it on DroneGroup, but UpdatePositions never
    // reads it. Retain the value and validate it so files/UI remain source-compatible without
    // inventing flight behaviour that Mission Planner does not have.
    if (!double.IsFinite(settings.LeadM) || Math.Abs(settings.LeadM) > 100000) {
      error = "lead must be finite and within ±100000 m.";
      return false;
    }
    if (plan.Followers.Count > MaximumFollowers) {
      error = $"the official controller supports at most {MaximumFollowers} follower positions.";
      return false;
    }

    var identities = new HashSet<FormationVehicleId> { plan.GroundMaster, plan.AirMaster };
    var orders = new HashSet<int>();
    foreach (FollowLeaderFollower planned in plan.Followers) {
      if (!identities.Add(planned.Id)) {
        error = $"vehicle {planned.Id.SystemId}:{planned.Id.ComponentId} has more than one role.";
        return false;
      }
      if (planned.Order < 1 || !orders.Add(planned.Order)) {
        error = "follower order must contain unique positive values.";
        return false;
      }
      if (!TryResolveFlightVehicle(
              sources, planned.Id, nowUtc, out FormationVehicleSource follower, out error)) {
        error = "follower " + error;
        return false;
      }
      if (!FormationCommandRunner.HasPosition(follower.State)) {
        error = $"follower {follower.Label} position is unavailable.";
        return false;
      }
      followers.Add((follower, planned.Order));
    }
    if (followers.Count > 0 &&
        !orders.SetEquals(Enumerable.Range(1, followers.Count))) {
      error = $"follower order must be contiguous from 1 through {followers.Count}.";
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
    if (!source.SupportsFollowLeaderFlight) {
      error = $"{source.Label} is {source.State.cs.firmware}; the official air group requires ArduCopter.";
      return false;
    }
    error = "";
    return true;
  }

  private static FollowLeaderVelocity Velocity(FormationVehicleSource source) => new(
      source.State.cs.vx,
      source.State.cs.vy,
      source.State.cs.vz);

  private static double GroundCourseDegrees(FormationVehicleSource groundMaster) {
    double bearing = Math.Atan2(groundMaster.State.cs.vy, groundMaster.State.cs.vx) * 180 / Math.PI;
    return (bearing + 360) % 360;
  }

  private static bool TryMissionTurnBearing(
      FormationVehicleSource groundMaster,
      double separationM,
      out double bearingDegrees) {
    bearingDegrees = 0;
    if (!double.IsFinite(groundMaster.State.cs.wp_dist) ||
        groundMaster.State.cs.wp_dist >= separationM * 1.5) {
      return false;
    }
    int currentIndex = (int)groundMaster.State.cs.wpno;
    if (!groundMaster.State.wps.TryGetValue(currentIndex, out MAVLink.mavlink_mission_item_int_t current) ||
        !groundMaster.State.wps.TryGetValue(currentIndex + 1, out MAVLink.mavlink_mission_item_int_t next)) {
      return false;
    }
    var currentPoint = new FollowPathPoint(current.x / 1e7, current.y / 1e7, current.z);
    var nextPoint = new FollowPathPoint(next.x / 1e7, next.y / 1e7, next.z);
    if (!FollowPathTrail.IsValid(currentPoint) || !FollowPathTrail.IsValid(nextPoint)) {
      return false;
    }
    (_, double routeBearing) = FormationGeometry.DistanceAndBearing(
        currentPoint.Latitude, currentPoint.Longitude,
        nextPoint.Latitude, nextPoint.Longitude);
    (double aimLatitude, double aimLongitude) = FormationGeometry.Project(
        currentPoint.Latitude, currentPoint.Longitude, routeBearing, separationM);
    (_, double aimBearing) = FormationGeometry.DistanceAndBearing(
        groundMaster.State.cs.lat, groundMaster.State.cs.lng, aimLatitude, aimLongitude);
    bearingDegrees = (aimBearing * 180 / Math.PI + 360) % 360;
    return double.IsFinite(bearingDegrees);
  }

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
