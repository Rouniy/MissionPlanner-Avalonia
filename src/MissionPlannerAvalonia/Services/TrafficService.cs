using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

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
    MAVLink.MAV_COLLISION_THREAT_LEVEL ThreatLevel);

internal sealed record TrafficSnapshot(long Revision, IReadOnlyList<TrafficTarget> Targets);

internal sealed class TrafficStore {
  private readonly ConcurrentDictionary<string, TrafficTarget> _targets =
      new(StringComparer.OrdinalIgnoreCase);
  private long _revision;

  internal void Upsert(adsb.PointLatLngAltHdg plane, DateTime nowUtc) {
    string id = string.IsNullOrWhiteSpace(plane.Tag)
        ? plane.CallSign?.Trim() ?? ""
        : plane.Tag.Trim();
    if (id.Length == 0 || !double.IsFinite(plane.Lat) || !double.IsFinite(plane.Lng) ||
        (plane.Lat == 0 && plane.Lng == 0)) {
      return;
    }

    var incoming = new TrafficTarget(id, plane.CallSign?.Trim() ?? "", plane.Lat, plane.Lng,
        plane.Alt, plane.Heading, plane.Speed, plane.VerticalSpeed, nowUtc, plane.ThreatLevel);
    _targets.AddOrUpdate(id, incoming, (_, current) => incoming with {
      // ADSB_VEHICLE updates do not carry COLLISION state. Keep the last explicit COLLISION
      // level until a later COLLISION message changes or clears it, matching upstream.
      ThreatLevel = plane.ThreatLevel == MAVLink.MAV_COLLISION_THREAT_LEVEL.NONE
          ? current.ThreatLevel
          : plane.ThreatLevel,
    });
    Interlocked.Increment(ref _revision);
  }

  internal IReadOnlyList<TrafficTarget> Snapshot(DateTime nowUtc, TimeSpan maximumAge) {
    return SnapshotWithRevision(nowUtc, maximumAge).Targets;
  }

  internal TrafficSnapshot SnapshotWithRevision(DateTime nowUtc, TimeSpan maximumAge) {
    foreach (var pair in _targets) {
      if (nowUtc - pair.Value.UpdatedUtc > maximumAge) {
        if (_targets.TryRemove(pair.Key, out _)) {
          Interlocked.Increment(ref _revision);
        }
      }
    }
    long revision = Interlocked.Read(ref _revision);
    var targets = _targets.Values.OrderBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new TrafficSnapshot(revision, targets);
  }

  internal void UpdateThreat(string id, MAVLink.MAV_COLLISION_THREAT_LEVEL threatLevel) {
    while (_targets.TryGetValue(id, out var current)) {
      if (current.ThreatLevel == threatLevel) {
        return;
      }
      if (_targets.TryUpdate(id, current with { ThreatLevel = threatLevel }, current)) {
        Interlocked.Increment(ref _revision);
        return;
      }
    }
  }
}

/// <summary>
/// Collects ADS-B_VEHICLE traffic decoded by the shared MAVLink reader. Rendering stays in the
/// Avalonia map; no WinForms overlay or upstream background thread is required.
/// </summary>
internal sealed class TrafficService : IDisposable {
  private static readonly TimeSpan _maximumAge = TimeSpan.FromSeconds(30);
  private readonly TrafficStore _store = new();
  private bool _disposed;

  internal TrafficService() {
    MAVLinkInterface.UpdateADSBPlanePosition += OnPlanePosition;
    MAVLinkInterface.UpdateADSBCollision += OnCollision;
  }

  internal IReadOnlyList<TrafficTarget> Snapshot() =>
      _store.Snapshot(DateTime.UtcNow, _maximumAge);

  internal TrafficSnapshot SnapshotWithRevision() =>
      _store.SnapshotWithRevision(DateTime.UtcNow, _maximumAge);

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    MAVLinkInterface.UpdateADSBPlanePosition -= OnPlanePosition;
    MAVLinkInterface.UpdateADSBCollision -= OnCollision;
  }

  private void OnPlanePosition(object? sender, adsb.PointLatLngAltHdg plane) =>
      _store.Upsert(plane, DateTime.UtcNow);

  private void OnCollision(object? sender,
      (string id, MAVLink.MAV_COLLISION_THREAT_LEVEL threat_level) collision) =>
      _store.UpdateThreat(collision.id, collision.threat_level);
}
