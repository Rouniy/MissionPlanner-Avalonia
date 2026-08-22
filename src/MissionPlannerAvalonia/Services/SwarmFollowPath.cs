using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct FollowPathPoint(
    double Latitude,
    double Longitude,
    double Altitude);

internal sealed record FollowPathFollower(FormationVehicleId Id, int Order);

internal sealed record FollowPathPlan(
    FormationVehicleId Leader,
    IReadOnlyList<FollowPathFollower> Followers,
    double SeparationM);

internal sealed record FollowPathCommand(
    FormationVehicleSource Follower,
    FollowPathPoint Target,
    double DistanceBehindM);

internal readonly record struct FollowPathTickResult(
    bool Continue,
    string Status,
    IReadOnlyList<FollowPathCommand> Commands) {
  internal static FollowPathTickResult Stop(string status) => new(false, status, []);

  internal static FollowPathTickResult Waiting(double available, double required) => new(
      true,
      $"Recording leader trail: {available:0.0} of {required:0.0} m required.",
      []);

  internal static FollowPathTickResult Sent(
      IReadOnlyList<FollowPathCommand> commands, double trailLength) => new(
      true,
      $"Follow Path active: {commands.Count} target(s) sent; trail {trailLength:0.0} m.",
      commands);
}

internal enum FollowPathTrailUpdate {
  Added,
  Unchanged,
  ResetAfterJump,
}

/// <summary>
/// Stores the leader's chronological trail and resolves exact positions behind its newest point.
/// This implements the intended behaviour of MissionPlanner.Swarm.FollowPath while avoiding its
/// oldest-point traversal bug and interpolating instead of accumulating sample-rate error.
/// </summary>
internal sealed class FollowPathTrail {
  internal const int MaximumPoints = 5000;
  internal const double MinimumSampleDistanceM = 0.1;
  internal const double MaximumSegmentDistanceM = 500;

  private readonly List<FollowPathPoint> _points = [];
  private double _lengthM;

  internal int Count => _points.Count;
  internal double LengthM => _lengthM;

  internal FollowPathTrailUpdate Record(FollowPathPoint point) {
    if (!IsValid(point)) {
      throw new ArgumentOutOfRangeException(nameof(point), "Leader position is invalid.");
    }
    if (_points.Count == 0) {
      _points.Add(point);
      return FollowPathTrailUpdate.Added;
    }

    FollowPathPoint last = _points[^1];
    double segment = Distance(last, point);
    if (!double.IsFinite(segment) || segment > MaximumSegmentDistanceM) {
      _points.Clear();
      _points.Add(point);
      _lengthM = 0;
      return FollowPathTrailUpdate.ResetAfterJump;
    }
    if (segment < MinimumSampleDistanceM) {
      // Preserve the horizontal sample so sub-threshold motion accumulates, but keep the current
      // leader altitude for the front of the trail.
      _points[^1] = last with { Altitude = point.Altitude };
      return FollowPathTrailUpdate.Unchanged;
    }

    _points.Add(point);
    _lengthM += segment;
    while (_points.Count > MaximumPoints) {
      _lengthM -= Distance(_points[0], _points[1]);
      _points.RemoveAt(0);
    }
    _lengthM = Math.Max(0, _lengthM);
    return FollowPathTrailUpdate.Added;
  }

  internal bool TryPointBehind(double distanceM, out FollowPathPoint point) {
    point = default;
    if (!double.IsFinite(distanceM) || distanceM < 0 || _points.Count == 0) {
      return false;
    }
    if (distanceM <= double.Epsilon) {
      point = _points[^1];
      return true;
    }

    double remaining = distanceM;
    for (int index = _points.Count - 1; index > 0; index--) {
      FollowPathPoint newer = _points[index];
      FollowPathPoint older = _points[index - 1];
      (double segment, double bearing) = FormationGeometry.DistanceAndBearing(
          newer.Latitude, newer.Longitude, older.Latitude, older.Longitude);
      if (segment <= double.Epsilon) {
        continue;
      }
      if (remaining <= segment) {
        double ratio = Math.Clamp(remaining / segment, 0, 1);
        (double latitude, double longitude) = FormationGeometry.Project(
            newer.Latitude, newer.Longitude, bearing, remaining);
        point = new FollowPathPoint(
            latitude,
            longitude,
            newer.Altitude + (older.Altitude - newer.Altitude) * ratio);
        return true;
      }
      remaining -= segment;
    }
    return false;
  }

  internal static bool IsValid(FollowPathPoint point) =>
      double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) &&
      double.IsFinite(point.Altitude) &&
      point.Latitude is >= -90 and <= 90 &&
      point.Longitude is >= -180 and <= 180 &&
      Math.Abs(point.Latitude) > double.Epsilon &&
      Math.Abs(point.Longitude) > double.Epsilon;

  private static double Distance(FollowPathPoint from, FollowPathPoint to) =>
      FormationGeometry.DistanceAndBearing(
          from.Latitude, from.Longitude, to.Latitude, to.Longitude).Distance;
}

internal interface IFollowPathCommandSink {
  void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles);
  void SendTarget(FormationVehicleSource follower, FollowPathPoint target);
  bool Arm(FormationVehicleSource vehicle, bool arm);
  void SetMode(FormationVehicleSource vehicle, string mode);
  bool Takeoff(FormationVehicleSource vehicle, double altitudeM);
}

internal sealed class MavlinkFollowPathCommandSink : IFollowPathCommandSink {
  private readonly MavlinkFormationCommandSink _common = new();

  public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) {
    foreach (FormationVehicleSource vehicle in vehicles) {
      vehicle.Id.Link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, 5,
          vehicle.Id.SystemId, vehicle.Id.ComponentId);
      vehicle.State.cs.rateposition = 5;
    }
  }

  public void SendTarget(FormationVehicleSource follower, FollowPathPoint target) {
    if (!string.Equals(follower.State.cs.mode, "GUIDED", StringComparison.OrdinalIgnoreCase)) {
      follower.Id.Link.setMode(follower.Id.SystemId, follower.Id.ComponentId, "GUIDED");
    }

    if (follower.State.cs.firmware == Firmwares.ArduPlane) {
      var waypoint = new Locationwp {
        id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        lat = target.Latitude,
        lng = target.Longitude,
        alt = (float)target.Altitude,
        frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      };
      MAVLink.MAV_MISSION_RESULT result = follower.Id.Link.setWP(
          follower.Id.SystemId, follower.Id.ComponentId, waypoint, 0,
          MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, current: 2);
      if (result != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED) {
        throw new InvalidOperationException(
            $"{follower.Label} rejected the guided waypoint: {result}.");
      }
      return;
    }

#pragma warning disable CS0612 // Required MAVLink SET_POSITION_TARGET_GLOBAL_INT coordinate frame.
    follower.Id.Link.setPositionTargetGlobalInt(
        follower.Id.SystemId, follower.Id.ComponentId,
        true, false, false, false,
        MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
        target.Latitude, target.Longitude, target.Altitude,
        0, 0, 0, 0, 0);
#pragma warning restore CS0612
  }

  public bool Arm(FormationVehicleSource vehicle, bool arm) => _common.Arm(vehicle, arm);

  public void SetMode(FormationVehicleSource vehicle, string mode) => _common.SetMode(vehicle, mode);

  public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) =>
      _common.Takeoff(vehicle, altitudeM);
}

internal sealed class FollowPathCommandRunner {
  internal const double MinimumSeparationM = 1;
  internal const double MaximumSeparationM = 500;
  internal const double MinimumPlaneSeparationM = 30;
  internal const int MaximumOrder = 100;

  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowPathCommandSink _sink;
  private readonly FollowPathTrail _trail;

  internal FollowPathCommandRunner(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowPathCommandSink sink,
      FollowPathTrail? trail = null) {
    _snapshot = snapshot;
    _sink = sink;
    _trail = trail ?? new FollowPathTrail();
  }

  internal FollowPathTickResult Tick(FollowPathPlan plan, DateTime nowUtc) {
    if (!TryResolvePlan(plan, nowUtc, out FormationVehicleSource leader,
        out List<(FormationVehicleSource Source, int Order)> followers, out string error)) {
      return FollowPathTickResult.Stop("Follow Path stopped: " + error);
    }

    var leaderPoint = new FollowPathPoint(
        leader.State.cs.lat, leader.State.cs.lng, leader.State.cs.alt);
    FollowPathTrailUpdate update = _trail.Record(leaderPoint);
    if (update == FollowPathTrailUpdate.ResetAfterJump) {
      return FollowPathTickResult.Stop(
          "Follow Path stopped: leader position jumped by more than 500 m; trail was reset.");
    }

    double requiredLength = followers.Max(item => item.Order) * plan.SeparationM;
    var commands = new List<FollowPathCommand>(followers.Count);
    foreach ((FormationVehicleSource follower, int order) in followers.OrderBy(item => item.Order)) {
      double distance = order * plan.SeparationM;
      if (!_trail.TryPointBehind(distance, out FollowPathPoint target)) {
        return FollowPathTickResult.Waiting(_trail.LengthM, requiredLength);
      }
      commands.Add(new FollowPathCommand(follower, target, distance));
    }

    // Every target is resolved before the first command so a missing/stale later follower cannot
    // silently produce a partial group update.
    foreach (FollowPathCommand command in commands) {
      _sink.SendTarget(command.Follower, command.Target);
    }
    return FollowPathTickResult.Sent(commands, _trail.LengthM);
  }

  internal async Task<string> RunAsync(
      FollowPathPlan plan,
      Action<FollowPathTickResult>? progress,
      CancellationToken cancellationToken) {
    if (!TryResolvePlan(plan, DateTime.UtcNow, out FormationVehicleSource leader,
        out List<(FormationVehicleSource Source, int Order)> followers, out string error)) {
      return "Follow Path could not start: " + error;
    }
    _sink.RequestPositionStreams([leader, .. followers.Select(item => item.Source)]);

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
    while (!cancellationToken.IsCancellationRequested) {
      FollowPathTickResult result;
      try {
        result = Tick(plan, DateTime.UtcNow);
      } catch (Exception ex) {
        return "Follow Path stopped after a command error: " + UserMessage(ex);
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
    return "Follow Path stopped by operator.";
  }

  internal bool TryResolvePlan(
      FollowPathPlan plan,
      DateTime nowUtc,
      out FormationVehicleSource leader,
      out List<(FormationVehicleSource Source, int Order)> followers,
      out string error) {
    followers = [];
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    if (!TryResolveVehicle(sources, plan.Leader, nowUtc, out leader, out error)) {
      error = "leader " + error;
      return false;
    }
    if (!FormationCommandRunner.HasPosition(leader.State)) {
      error = "leader position is unavailable.";
      return false;
    }
    if (!double.IsFinite(plan.SeparationM) ||
        plan.SeparationM is < MinimumSeparationM or > MaximumSeparationM) {
      error = $"separation must be {MinimumSeparationM:0}–{MaximumSeparationM:0} m.";
      return false;
    }
    if (plan.Followers.Count == 0) {
      error = "no followers are selected.";
      return false;
    }

    var identities = new HashSet<FormationVehicleId>();
    var orders = new HashSet<int>();
    foreach (FollowPathFollower planned in plan.Followers) {
      if (planned.Id == plan.Leader) {
        error = "leader was also selected as a follower.";
        return false;
      }
      if (!identities.Add(planned.Id)) {
        error = $"follower {planned.Id.SystemId}:{planned.Id.ComponentId} is duplicated.";
        return false;
      }
      if (planned.Order is < 1 or > MaximumOrder || !orders.Add(planned.Order)) {
        error = $"follower order must be unique and between 1 and {MaximumOrder}.";
        return false;
      }
      if (!TryResolveVehicle(sources, planned.Id, nowUtc,
          out FormationVehicleSource follower, out error)) {
        error = "follower " + error;
        return false;
      }
      if (!FormationCommandRunner.HasPosition(follower.State)) {
        error = $"follower {follower.Label} position is unavailable.";
        return false;
      }
      if (follower.State.cs.firmware == Firmwares.ArduPlane &&
          plan.SeparationM < MinimumPlaneSeparationM) {
        error = $"ArduPlane follower {follower.Label} requires at least " +
            $"{MinimumPlaneSeparationM:0} m separation.";
        return false;
      }
      followers.Add((follower, planned.Order));
    }
    error = "";
    return true;
  }

  internal static bool TryResolveVehicle(
      IReadOnlyList<FormationVehicleSource> sources,
      FormationVehicleId id,
      DateTime nowUtc,
      out FormationVehicleSource source,
      out string error) {
    if (!FormationCommandRunner.TryResolveAutopilot(
            sources, id, nowUtc, out source, out error)) {
      return false;
    }
    if (!source.SupportsFollowPath) {
      error = $"{source.Label} firmware {source.State.cs.firmware} is unsupported; " +
          "Follow Path supports ArduPlane, ArduCopter and ArduRover.";
      return false;
    }
    error = "";
    return true;
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
