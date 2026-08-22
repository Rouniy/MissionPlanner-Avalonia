using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct FormationOffset(double X, double Y, double Z);

internal readonly record struct FormationTarget(
    double Latitude,
    double Longitude,
    double Altitude,
    double VelocityNorth,
    double VelocityEast,
    double VelocityDown,
    double YawDegrees);

internal readonly struct FormationVehicleId : IEquatable<FormationVehicleId> {
  internal FormationVehicleId(MAVLinkInterface link, byte systemId, byte componentId) {
    Link = link;
    SystemId = systemId;
    ComponentId = componentId;
  }

  internal MAVLinkInterface Link { get; }
  internal byte SystemId { get; }
  internal byte ComponentId { get; }

  public bool Equals(FormationVehicleId other) =>
      ReferenceEquals(Link, other.Link) &&
      SystemId == other.SystemId && ComponentId == other.ComponentId;

  public override bool Equals(object? obj) => obj is FormationVehicleId other && Equals(other);

  public override int GetHashCode() =>
      HashCode.Combine(RuntimeHelpers.GetHashCode(Link), SystemId, ComponentId);

  public static bool operator ==(FormationVehicleId left, FormationVehicleId right) =>
      left.Equals(right);

  public static bool operator !=(FormationVehicleId left, FormationVehicleId right) =>
      !left.Equals(right);
}

internal sealed record FormationVehicleSource(
    FormationVehicleId Id,
    MAVState State,
    string Endpoint,
    bool IsOpen) {
  internal bool IsAutopilot =>
      Id.ComponentId == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1 &&
      !State.CANNode;

  internal bool SupportsFormation => IsAutopilot &&
      State.cs.firmware is Firmwares.ArduCopter2 or Firmwares.ArduRover;

  internal bool SupportsFollowPath => IsAutopilot &&
      State.cs.firmware is Firmwares.ArduPlane or Firmwares.ArduCopter2 or Firmwares.ArduRover;

  internal bool SupportsWaypointLeaderFlight => IsAutopilot &&
      State.cs.firmware == Firmwares.ArduCopter2;

  internal bool SupportsFollowLeaderFlight => IsAutopilot &&
      State.cs.firmware == Firmwares.ArduCopter2;

  internal string Label => $"{Endpoint} — {Id.SystemId}:{Id.ComponentId}";
}

internal sealed record FormationFollower(
    FormationVehicleId Id,
    FormationOffset Offset);

internal sealed record FormationPlan(
    FormationVehicleId Leader,
    IReadOnlyList<FormationFollower> Followers,
    bool AlignYaw,
    bool AimGimbals);

internal readonly record struct FormationTickResult(bool Continue, string Status) {
  internal static FormationTickResult Stop(string status) => new(false, status);
  internal static FormationTickResult Sent(int count) =>
      new(true, $"Formation active: setpoints sent to {count} follower(s).");
}

internal static class FormationGeometry {
  private const double EarthRadiusM = 6378137.0;
  private const double DegreesToRadians = Math.PI / 180.0;
  private const double RadiansToDegrees = 180.0 / Math.PI;

  /// <summary>
  /// Applies the same leader-yaw rotation as MissionPlanner.Swarm.Formation. X/Y are formation-local
  /// metres and Z is relative altitude. The spherical projection avoids the upstream UTM zone edge.
  /// </summary>
  internal static FormationTarget TargetFromLeader(
      double latitude,
      double longitude,
      double altitude,
      double yawDegrees,
      double velocityNorth,
      double velocityEast,
      double velocityDown,
      FormationOffset offset) {
    double heading = -yawDegrees * DegreesToRadians;
    double east = offset.X * Math.Cos(heading) - offset.Y * Math.Sin(heading);
    double north = offset.X * Math.Sin(heading) + offset.Y * Math.Cos(heading);
    double distance = Math.Sqrt(east * east + north * north);
    double bearing = Math.Atan2(east, north);
    (double targetLatitude, double targetLongitude) = Project(
        latitude, longitude, bearing, distance);
    return new FormationTarget(
        targetLatitude, targetLongitude, altitude + offset.Z,
        velocityNorth, velocityEast, velocityDown, yawDegrees);
  }

  internal static FormationOffset OffsetFromLeader(
      double leaderLatitude,
      double leaderLongitude,
      double leaderAltitude,
      double leaderYawDegrees,
      double followerLatitude,
      double followerLongitude,
      double followerAltitude) {
    (double distance, double bearing) = DistanceAndBearing(
        leaderLatitude, leaderLongitude, followerLatitude, followerLongitude);
    double east = distance * Math.Sin(bearing);
    double north = distance * Math.Cos(bearing);
    double heading = -leaderYawDegrees * DegreesToRadians;
    double x = east * Math.Cos(heading) + north * Math.Sin(heading);
    double y = -east * Math.Sin(heading) + north * Math.Cos(heading);
    return new FormationOffset(x, y, followerAltitude - leaderAltitude);
  }

  internal static (double Latitude, double Longitude) Project(
      double latitude, double longitude, double bearing, double distance) {
    if (distance <= double.Epsilon) {
      return (latitude, longitude);
    }
    double angularDistance = distance / EarthRadiusM;
    double lat1 = latitude * DegreesToRadians;
    double lon1 = longitude * DegreesToRadians;
    double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDistance) +
        Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing));
    double lon2 = lon1 + Math.Atan2(
        Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1),
        Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));
    double normalizedLongitude = (lon2 * RadiansToDegrees + 540) % 360 - 180;
    return (lat2 * RadiansToDegrees, normalizedLongitude);
  }

  internal static (double Distance, double Bearing) DistanceAndBearing(
      double fromLatitude, double fromLongitude, double toLatitude, double toLongitude) {
    double lat1 = fromLatitude * DegreesToRadians;
    double lat2 = toLatitude * DegreesToRadians;
    double deltaLat = (toLatitude - fromLatitude) * DegreesToRadians;
    double deltaLon = (toLongitude - fromLongitude) * DegreesToRadians;
    double a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
        Math.Cos(lat1) * Math.Cos(lat2) *
        Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
    a = Math.Clamp(a, 0, 1);
    double distance = EarthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    double bearing = Math.Atan2(
        Math.Sin(deltaLon) * Math.Cos(lat2),
        Math.Cos(lat1) * Math.Sin(lat2) -
        Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon));
    return (distance, bearing);
  }
}

internal interface IFormationCommandSink {
  void RequestLeaderStreams(FormationVehicleSource leader);
  void SendSetpoint(FormationVehicleSource follower, FormationTarget target,
      bool alignYaw, bool aimGimbal);
  bool Arm(FormationVehicleSource vehicle, bool arm);
  void SetMode(FormationVehicleSource vehicle, string mode);
  bool Takeoff(FormationVehicleSource vehicle, double altitudeM);
}

internal sealed class MavlinkFormationCommandSink : IFormationCommandSink {
  private readonly Dictionary<FormationVehicleId, DateTime> _lastYawCommand = [];

  public void RequestLeaderStreams(FormationVehicleSource leader) {
    leader.Id.Link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, 10,
        leader.Id.SystemId, leader.Id.ComponentId);
    leader.Id.Link.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1, 10,
        leader.Id.SystemId, leader.Id.ComponentId);
    leader.State.cs.rateposition = 10;
    leader.State.cs.rateattitude = 10;
  }

  public void SendSetpoint(FormationVehicleSource follower, FormationTarget target,
      bool alignYaw, bool aimGimbal) {
#pragma warning disable CS0612 // Required MAVLink SET_POSITION_TARGET_GLOBAL_INT coordinate frame.
    follower.Id.Link.setPositionTargetGlobalInt(
        follower.Id.SystemId, follower.Id.ComponentId,
        true, true, false, false,
        MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
        target.Latitude, target.Longitude, target.Altitude,
        target.VelocityNorth, target.VelocityEast, target.VelocityDown, 0, 0);
#pragma warning restore CS0612

    if (!alignYaw || Math.Abs(Wrap180(follower.State.cs.yaw - target.YawDegrees)) <= 3) {
      return;
    }
    DateTime now = DateTime.UtcNow;
    if (_lastYawCommand.TryGetValue(follower.Id, out DateTime last) &&
        now < last.AddSeconds(1)) {
      return;
    }
    _lastYawCommand[follower.Id] = now;
    if (aimGimbal) {
      follower.Id.Link.setMountControl(follower.Id.SystemId, follower.Id.ComponentId,
          4500, 0, target.YawDegrees * 100, false);
    } else {
      follower.Id.Link.doCommand(
          follower.Id.SystemId, follower.Id.ComponentId,
          MAVLink.MAV_CMD.CONDITION_YAW, (float)target.YawDegrees,
          100, 0, 0, 0, 0, 0, false);
    }
  }

  public bool Arm(FormationVehicleSource vehicle, bool arm) =>
      vehicle.Id.Link.doARM(vehicle.Id.SystemId, vehicle.Id.ComponentId, arm);

  public void SetMode(FormationVehicleSource vehicle, string mode) =>
      vehicle.Id.Link.setMode(vehicle.Id.SystemId, vehicle.Id.ComponentId, mode);

  public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) =>
      vehicle.Id.Link.doCommand(
          vehicle.Id.SystemId, vehicle.Id.ComponentId,
          MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, (float)altitudeM, false);

  private static double Wrap180(double degrees) {
    while (degrees > 180) {
      degrees -= 360;
    }
    while (degrees < -180) {
      degrees += 360;
    }
    return degrees;
  }
}

internal sealed class FormationCommandRunner {
  internal static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(5);
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFormationCommandSink _sink;

  internal FormationCommandRunner(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFormationCommandSink sink) {
    _snapshot = snapshot;
    _sink = sink;
  }

  internal FormationTickResult Tick(FormationPlan plan, DateTime nowUtc) {
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    if (!TryResolve(sources, plan.Leader, nowUtc, out FormationVehicleSource? leader,
        out string error)) {
      return FormationTickResult.Stop("Formation stopped: leader " + error);
    }
    if (!HasPosition(leader.State)) {
      return FormationTickResult.Stop("Formation stopped: leader position is unavailable.");
    }

    var followers = new List<(FormationVehicleSource Source, FormationOffset Offset)>();
    foreach (FormationFollower planned in plan.Followers) {
      if (planned.Id == plan.Leader) {
        return FormationTickResult.Stop("Formation stopped: leader was also selected as a follower.");
      }
      if (!TryResolve(sources, planned.Id, nowUtc, out FormationVehicleSource? follower,
          out error)) {
        return FormationTickResult.Stop("Formation stopped: follower " + error);
      }
      followers.Add((follower, planned.Offset));
    }
    if (followers.Count == 0) {
      return FormationTickResult.Stop("Formation stopped: no followers are selected.");
    }

    var commands = new List<(FormationVehicleSource Follower, FormationTarget Target)>();
    foreach ((FormationVehicleSource follower, FormationOffset offset) in followers) {
      if (!IsSafeOffset(offset)) {
        return FormationTickResult.Stop(
            $"Formation stopped: follower {follower.Label} has an invalid or excessive offset.");
      }
      FormationTarget target = FormationGeometry.TargetFromLeader(
          leader.State.cs.lat, leader.State.cs.lng, leader.State.cs.alt,
          leader.State.cs.yaw, leader.State.cs.vx, leader.State.cs.vy, leader.State.cs.vz,
          offset);
      if (!IsFinite(target)) {
        return FormationTickResult.Stop(
            $"Formation stopped: follower {follower.Label} target is not finite.");
      }
      commands.Add((follower, target));
    }
    foreach ((FormationVehicleSource follower, FormationTarget target) in commands) {
      _sink.SendSetpoint(follower, target, plan.AlignYaw, plan.AimGimbals);
    }
    return FormationTickResult.Sent(followers.Count);
  }

  internal async Task<string> RunAsync(
      FormationPlan plan,
      Action<string>? progress,
      CancellationToken cancellationToken) {
    IReadOnlyList<FormationVehicleSource> initial = _snapshot();
    if (!TryResolve(initial, plan.Leader, DateTime.UtcNow,
        out FormationVehicleSource? leader, out string error)) {
      return "Formation could not start: leader " + error;
    }
    _sink.RequestLeaderStreams(leader);

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    while (!cancellationToken.IsCancellationRequested) {
      FormationTickResult result;
      try {
        result = Tick(plan, DateTime.UtcNow);
      } catch (Exception ex) {
        return "Formation stopped after a command error: " + UserMessage(ex);
      }
      progress?.Invoke(result.Status);
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
    return "Formation stopped by operator.";
  }

  internal static bool TryResolve(
      IReadOnlyList<FormationVehicleSource> sources,
      FormationVehicleId id,
      DateTime nowUtc,
      out FormationVehicleSource source,
      out string error) {
    if (!TryResolveAutopilot(sources, id, nowUtc, out source, out error)) {
      return false;
    }
    if (!source.SupportsFormation) {
      error = source.State.cs.firmware == Firmwares.ArduPlane
          ? $"{source.Label} is ArduPlane; its upstream attitude/PID controller is not " +
              "enabled in this position-target formation port."
          : $"{source.Label} firmware {source.State.cs.firmware} is unsupported; " +
              "only ArduCopter and ArduRover use this controller.";
      return false;
    }
    error = "";
    return true;
  }

  internal static bool TryResolveAutopilot(
      IReadOnlyList<FormationVehicleSource> sources,
      FormationVehicleId id,
      DateTime nowUtc,
      out FormationVehicleSource source,
      out string error) {
    source = sources.FirstOrDefault(candidate => candidate.Id == id)!;
    if (source == null) {
      error = $"{id.SystemId}:{id.ComponentId} disappeared or moved to another link.";
      return false;
    }
    if (!source.IsOpen) {
      error = $"{source.Label} link is closed.";
      return false;
    }
    if (!source.IsAutopilot) {
      error = $"{source.Label} is not an autopilot component.";
      return false;
    }
    DateTime packetUtc = source.State.lastvalidpacket;
    if (packetUtc == DateTime.MinValue || nowUtc - packetUtc > MaximumTelemetryAge ||
        packetUtc > nowUtc.AddSeconds(1)) {
      error = $"{source.Label} telemetry is stale.";
      return false;
    }
    error = "";
    return true;
  }

  internal static bool HasPosition(MAVState state) =>
      double.IsFinite(state.cs.lat) && double.IsFinite(state.cs.lng) &&
      Math.Abs(state.cs.lat) > double.Epsilon && Math.Abs(state.cs.lng) > double.Epsilon;

  internal static bool IsSafeOffset(FormationOffset offset) =>
      double.IsFinite(offset.X) && double.IsFinite(offset.Y) && double.IsFinite(offset.Z) &&
      Math.Abs(offset.X) <= 100000 && Math.Abs(offset.Y) <= 100000 &&
      Math.Abs(offset.Z) <= 10000;

  private static bool IsFinite(FormationTarget target) =>
      double.IsFinite(target.Latitude) && double.IsFinite(target.Longitude) &&
      double.IsFinite(target.Altitude) && double.IsFinite(target.VelocityNorth) &&
      double.IsFinite(target.VelocityEast) && double.IsFinite(target.VelocityDown) &&
      double.IsFinite(target.YawDegrees);

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

internal static class FormationVehicleDiscovery {
  internal static IReadOnlyList<FormationVehicleSource> Snapshot(
      MavLinkConnectionManager connections) =>
      [.. connections.Snapshot()
          .Where(connection => connection.IsOpen)
          .SelectMany(connection => connection.Link.MAVlist.ToArray()
              .Where(mav => mav.sysid != 0 &&
                  mav.compid != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER)
              .Select(mav => new FormationVehicleSource(
                  new FormationVehicleId(connection.Link, mav.sysid, mav.compid),
                  mav, connection.Endpoint, connection.IsOpen)))
          .OrderBy(source => source.Endpoint, StringComparer.OrdinalIgnoreCase)
          .ThenBy(source => source.Id.SystemId)
          .ThenBy(source => source.Id.ComponentId)];
}
