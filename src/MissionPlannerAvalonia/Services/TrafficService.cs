using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal enum TrafficKind {
  Aircraft,
  Vessel,
  Obstacle,
}

internal enum TrafficOrigin {
  Mavlink,
  External,
  Ais,
}

internal sealed record TrafficTarget(
    string Id,
    string CallSign,
    double Lat,
    double Lng,
    double Alt,
    float Heading,
    double Speed,
    double VerticalSpeed,
    DateTime UpdatedUtc,
    MAVLink.MAV_COLLISION_THREAT_LEVEL ThreatLevel,
    TrafficKind Kind,
    TrafficOrigin Origin,
    ushort Squawk = 0,
    double Radius = 0,
    double TurnRate = 0);

internal sealed record TrafficSnapshot(long Revision, IReadOnlyList<TrafficTarget> Targets);

internal sealed class TrafficStore {
  private static readonly TimeSpan _aisMaximumAge = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan _obstacleMaximumAge = TimeSpan.FromSeconds(10);
  private readonly ConcurrentDictionary<string, TrafficTarget> _targets =
      new(StringComparer.OrdinalIgnoreCase);
  private long _revision;

  internal void Upsert(adsb.PointLatLngAltHdg plane, DateTime nowUtc,
      TrafficOrigin origin = TrafficOrigin.Mavlink) {
    string id = string.IsNullOrWhiteSpace(plane.Tag)
        ? plane.CallSign?.Trim() ?? ""
        : plane.Tag.Trim();
    if (id.Length == 0 || !ValidPosition(plane.Lat, plane.Lng)) {
      return;
    }

    bool obstacle = plane.Raw is MAVLink.mavlink_adsb_vehicle_t raw
        && raw.emitter_type == byte.MaxValue
        && DecodeText(raw.callsign).Equals("OA_DB", StringComparison.OrdinalIgnoreCase);
    TrafficKind kind = obstacle ? TrafficKind.Obstacle : TrafficKind.Aircraft;
    TrafficKind otherKind = obstacle ? TrafficKind.Aircraft : TrafficKind.Obstacle;
    if (_targets.TryRemove(Key(otherKind, id), out _)) {
      Interlocked.Increment(ref _revision);
    }
    var incoming = new TrafficTarget(id, plane.CallSign?.Trim() ?? "", plane.Lat, plane.Lng,
        plane.Alt, plane.Heading, plane.Speed, plane.VerticalSpeed, nowUtc, plane.ThreatLevel,
        kind, origin, plane.Squawk, obstacle ? plane.Squawk / 100.0 : 0);
    UpsertCore(Key(kind, id), incoming, preserveAircraftThreat: !obstacle);
  }

  internal void UpsertVessel(MAVLink.mavlink_ais_vessel_t vessel, DateTime nowUtc) {
    double lat = vessel.lat / 1e7;
    double lng = vessel.lon / 1e7;
    if (vessel.MMSI == 0 || !ValidPosition(lat, lng)) {
      return;
    }

    string callsign = DecodeText(vessel.callsign);
    string name = DecodeText(vessel.name);
    string label = name.Length > 0 ? name : callsign;
    float heading = vessel.heading == ushort.MaxValue
        ? vessel.COG / 100.0f
        : vessel.heading / 100.0f;
    var incoming = new TrafficTarget(vessel.MMSI.ToString(CultureInfo.InvariantCulture), label,
        lat, lng, 0, heading, vessel.velocity, 0, nowUtc,
        MAVLink.MAV_COLLISION_THREAT_LEVEL.NONE, TrafficKind.Vessel, TrafficOrigin.Ais,
        TurnRate: vessel.turn_rate);
    UpsertCore(Key(TrafficKind.Vessel, incoming.Id), incoming, preserveAircraftThreat: false);
  }

  internal IReadOnlyList<TrafficTarget> Snapshot(DateTime nowUtc, TimeSpan maximumAge) {
    return SnapshotWithRevision(nowUtc, maximumAge).Targets;
  }

  internal TrafficSnapshot SnapshotWithRevision(DateTime nowUtc, TimeSpan maximumAge) {
    foreach (var pair in _targets) {
      TimeSpan age = pair.Value.Kind switch {
        TrafficKind.Vessel => _aisMaximumAge,
        TrafficKind.Obstacle => _obstacleMaximumAge,
        _ => maximumAge,
      };
      if (nowUtc - pair.Value.UpdatedUtc > age && _targets.TryRemove(pair.Key, out _)) {
        Interlocked.Increment(ref _revision);
      }
    }
    long revision = Interlocked.Read(ref _revision);
    var targets = _targets.Values
        .OrderBy(target => target.Kind)
        .ThenBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new TrafficSnapshot(revision, targets);
  }

  internal void UpdateThreat(string id, MAVLink.MAV_COLLISION_THREAT_LEVEL threatLevel) {
    string key = Key(TrafficKind.Aircraft, id);
    while (_targets.TryGetValue(key, out var current)) {
      if (current.ThreatLevel == threatLevel) {
        return;
      }
      if (_targets.TryUpdate(key, current with { ThreatLevel = threatLevel }, current)) {
        Interlocked.Increment(ref _revision);
        return;
      }
    }
  }

  internal void Clear() {
    if (_targets.IsEmpty) {
      return;
    }
    _targets.Clear();
    Interlocked.Increment(ref _revision);
  }

  internal void ClearAircraft() {
    bool changed = false;
    foreach (var pair in _targets) {
      if (pair.Value.Kind != TrafficKind.Vessel && _targets.TryRemove(pair.Key, out _)) {
        changed = true;
      }
    }
    if (changed) {
      Interlocked.Increment(ref _revision);
    }
  }

  private void UpsertCore(string key, TrafficTarget incoming, bool preserveAircraftThreat) {
    while (true) {
      if (!_targets.TryGetValue(key, out var current)) {
        if (_targets.TryAdd(key, incoming)) {
          Interlocked.Increment(ref _revision);
          return;
        }
        continue;
      }

      // Official Mission Planner keeps a directly received aircraft over the same aircraft
      // repeated through MAVLink. This avoids a lower-fidelity relay overwriting local data.
      if (current.Origin == TrafficOrigin.External && incoming.Origin == TrafficOrigin.Mavlink) {
        return;
      }

      var replacement = preserveAircraftThreat
          && incoming.ThreatLevel == MAVLink.MAV_COLLISION_THREAT_LEVEL.NONE
          ? incoming with { ThreatLevel = current.ThreatLevel }
          : incoming;
      if (_targets.TryUpdate(key, replacement, current)) {
        Interlocked.Increment(ref _revision);
        return;
      }
    }
  }

  private static bool ValidPosition(double lat, double lng) =>
      double.IsFinite(lat) && double.IsFinite(lng)
      && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
      && (lat != 0 || lng != 0);

  private static string Key(TrafficKind kind, string id) => $"{kind}:{id.Trim()}";

  internal static string DecodeText(byte[]? bytes) => bytes is null
      ? ""
      : Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
}

internal static class TrafficUplink {
  internal static bool TryBuildNext(IReadOnlyList<TrafficTarget> targets,
      double observerLat, double observerLng, ref int index,
      out MAVLink.mavlink_adsb_vehicle_t packet) {
    packet = default;
    if (!double.IsFinite(observerLat) || !double.IsFinite(observerLng)
        || (observerLat == 0 && observerLng == 0)) {
      return false;
    }

    var relevant = targets
        .Where(target => target.Kind == TrafficKind.Aircraft
            && target.Origin == TrafficOrigin.External)
        .Select(target => (Target: target,
            Distance: DistanceMeters(observerLat, observerLng, target.Lat, target.Lng)))
        .Where(item => item.Distance <= 10_000)
        .OrderBy(item => item.Distance)
        .Take(10)
        .Select(item => item.Target)
        .ToArray();
    if (relevant.Length == 0) {
      index = -1;
      return false;
    }

    index = (index + 1) % relevant.Length;
    TrafficTarget target = relevant[index];
    uint.TryParse(target.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint icao);
    byte[] callsign = new byte[9];
    string paddedCallsign = (target.CallSign ?? "").Trim().PadRight(8);
    Encoding.ASCII.GetBytes(paddedCallsign, 0, 8, callsign, 0);
    packet = new MAVLink.mavlink_adsb_vehicle_t {
      ICAO_address = icao,
      lat = ScaleInt(target.Lat, 1e7),
      lon = ScaleInt(target.Lng, 1e7),
      altitude = ScaleInt(target.Alt, 1000),
      altitude_type = (byte)MAVLink.ADSB_ALTITUDE_TYPE.GEOMETRIC,
      heading = (ushort)Math.Clamp(Math.Round(NormalizeHeading(target.Heading) * 100), 0,
          ushort.MaxValue),
      hor_velocity = (ushort)Math.Clamp(Math.Round(target.Speed), 0, ushort.MaxValue),
      ver_velocity = (short)Math.Clamp(Math.Round(target.VerticalSpeed), short.MinValue,
          short.MaxValue),
      callsign = callsign,
      squawk = target.Squawk,
      emitter_type = (byte)MAVLink.ADSB_EMITTER_TYPE.NO_INFO,
      flags = (ushort)(MAVLink.ADSB_FLAGS.VALID_ALTITUDE
          | MAVLink.ADSB_FLAGS.VALID_COORDS
          | MAVLink.ADSB_FLAGS.VALID_VELOCITY
          | MAVLink.ADSB_FLAGS.VALID_HEADING
          | MAVLink.ADSB_FLAGS.VALID_CALLSIGN),
    };
    return true;
  }

  private static int ScaleInt(double value, double scale) =>
      (int)Math.Clamp(Math.Round(value * scale), int.MinValue, int.MaxValue);

  private static double NormalizeHeading(double heading) =>
      double.IsFinite(heading) ? (heading % 360 + 360) % 360 : 0;

  private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2) {
    const double radius = 6_371_000;
    double dLat = (lat2 - lat1) * Math.PI / 180;
    double dLng = (lng2 - lng1) * Math.PI / 180;
    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
        + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
        * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
    return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
  }
}

/// <summary>
/// Combines MAVLink ADS-B/AIS traffic with the portable external ADS-B receiver. Rendering stays
/// in the Avalonia map and all background work has a cancellable lifecycle.
/// </summary>
internal sealed class TrafficService : IDisposable {
  private static readonly TimeSpan _maximumAge = TimeSpan.FromSeconds(30);
  private readonly TrafficStore _store = new();
  private readonly MAVLinkInterface _link;
  private readonly ExternalAdsbReceiver _external;
  private readonly CancellationTokenSource _shutdown = new();
  private readonly Task _uplinkTask;
  private readonly object _positionSync = new();
  private (double Lat, double Lng)? _observerPosition;
  private bool _disposed;
  private int _uplinkIndex = -1;

  internal TrafficService(MAVLinkInterface? link = null, bool applySavedSettings = false,
      System.Net.Http.HttpClient? httpClient = null) {
    _link = link ?? new MAVLinkInterface();
    _external = new ExternalAdsbReceiver(OnExternalPlane, GetObserverPosition, httpClient);
    MAVLinkInterface.UpdateADSBPlanePosition += OnPlanePosition;
    MAVLinkInterface.UpdateADSBCollision += OnCollision;
    _link.OnPacketReceived += OnPacketReceived;
    _uplinkTask = Task.Run(() => UplinkLoopAsync(_shutdown.Token));
    if (applySavedSettings) {
      var settings = Settings.Instance;
      _ = ConfigureExternalAsync(settings.GetBoolean("enableadsb", false),
          settings.GetString("adsbserver", ExternalAdsbOptions.DefaultServer),
          settings.GetInt32("adsbport", ExternalAdsbOptions.DefaultPort));
    }
  }

  internal string ExternalStatus => _external.Status;

  internal IReadOnlyList<TrafficTarget> Snapshot() =>
      _store.Snapshot(DateTime.UtcNow, _maximumAge);

  internal TrafficSnapshot SnapshotWithRevision() =>
      _store.SnapshotWithRevision(DateTime.UtcNow, _maximumAge);

  internal async Task ConfigureExternalAsync(bool enabled, string? server, int port) {
    await _external.ConfigureAsync(new ExternalAdsbOptions(enabled, server, port));
    if (!enabled) {
      // Matches MainV2: disabling the external receiver clears aircraft, while the separate
      // MAVLink AIS store remains available and continues aging normally. Stop first so a final
      // network callback cannot repopulate the aircraft store after the clear.
      _store.ClearAircraft();
    }
  }

  internal void SetObserverPosition(double lat, double lng) {
    if (!double.IsFinite(lat) || !double.IsFinite(lng) || lat is < -90 or > 90
        || lng is < -180 or > 180 || (lat == 0 && lng == 0)) {
      return;
    }
    lock (_positionSync) {
      _observerPosition = (lat, lng);
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    MAVLinkInterface.UpdateADSBPlanePosition -= OnPlanePosition;
    MAVLinkInterface.UpdateADSBCollision -= OnCollision;
    _link.OnPacketReceived -= OnPacketReceived;
    _shutdown.Cancel();
    try {
      _external.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
      _uplinkTask.Wait(TimeSpan.FromSeconds(2));
    } catch {
      // Shutdown must not hold the desktop process open on a failed network stack.
    }
    _shutdown.Dispose();
  }

  private void OnPlanePosition(object? sender, adsb.PointLatLngAltHdg plane) =>
      _store.Upsert(plane, DateTime.UtcNow, TrafficOrigin.Mavlink);

  private void OnExternalPlane(adsb.PointLatLngAltHdg plane) =>
      _store.Upsert(plane, DateTime.UtcNow, TrafficOrigin.External);

  private void OnCollision(object? sender,
      (string id, MAVLink.MAV_COLLISION_THREAT_LEVEL threat_level) collision) =>
      _store.UpdateThreat(collision.id, collision.threat_level);

  private void OnPacketReceived(object? sender, MAVLink.MAVLinkMessage message) {
    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.AIS_VESSEL
        && message.data is MAVLink.mavlink_ais_vessel_t vessel) {
      _store.UpsertVessel(vessel, DateTime.UtcNow);
    }
  }

  private (double Lat, double Lng)? GetObserverPosition() {
    try {
      var location = _link.MAV.cs.Location;
      if (double.IsFinite(location.Lat) && double.IsFinite(location.Lng)
          && (location.Lat != 0 || location.Lng != 0)) {
        return (location.Lat, location.Lng);
      }
    } catch {
    }
    lock (_positionSync) {
      return _observerPosition;
    }
  }

  private async Task UplinkLoopAsync(CancellationToken cancellationToken) {
    try {
      using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
      while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
        if (_link.BaseStream?.IsOpen != true || GetObserverPosition() is not { } observer) {
          continue;
        }
        var targets = _store.Snapshot(DateTime.UtcNow, _maximumAge);
        if (TrafficUplink.TryBuildNext(targets, observer.Lat, observer.Lng, ref _uplinkIndex,
            out var packet)) {
          _link.sendPacket(packet, _link.MAV.sysid, _link.MAV.compid);
        }
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      System.Diagnostics.Trace.WriteLine($"ADS-B uplink stopped: {ex}");
    }
  }
}
