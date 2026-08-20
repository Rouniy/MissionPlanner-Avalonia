using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class TrafficStoreTests {
  [Fact]
  public void Store_replaces_aircraft_by_icao_and_expires_old_targets() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.Upsert(Plane("ABC123", 1, 2), now.AddSeconds(-40));
    store.Upsert(Plane("DEF456", 3, 4), now.AddSeconds(-5));
    store.Upsert(Plane("DEF456", 5, 6), now);

    var result = store.Snapshot(now, TimeSpan.FromSeconds(30));

    var target = Assert.Single(result);
    Assert.Equal("DEF456", target.Id);
    Assert.Equal(5, target.Lat);
    Assert.Equal(6, target.Lng);
  }

  [Fact]
  public void Store_rejects_unidentified_or_invalid_positions() {
    var store = new TrafficStore();
    var now = DateTime.UtcNow;
    store.Upsert(Plane("", 1, 2), now);
    store.Upsert(Plane("ABC123", 0, 0), now);
    store.Upsert(Plane("DEF456", double.NaN, 2), now);

    Assert.Empty(store.Snapshot(now, TimeSpan.FromMinutes(1)));
  }

  [Fact]
  public void Collision_message_updates_existing_aircraft_threat() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.Upsert(Plane("ABC123", 1, 2), now);

    store.UpdateThreat("ABC123", MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH);

    Assert.Equal(MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH,
        Assert.Single(store.Snapshot(now, TimeSpan.FromMinutes(1))).ThreatLevel);
  }

  [Fact]
  public void Position_update_preserves_explicit_collision_threat() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.Upsert(Plane("ABC123", 1, 2), now);
    store.UpdateThreat("ABC123", MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH);

    store.Upsert(Plane("ABC123", 3, 4), now.AddSeconds(1));

    var target = Assert.Single(store.Snapshot(now.AddSeconds(1), TimeSpan.FromMinutes(1)));
    Assert.Equal(3, target.Lat);
    Assert.Equal(MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH, target.ThreatLevel);
  }

  [Fact]
  public void Snapshot_revision_changes_only_when_store_changes() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    var empty = store.SnapshotWithRevision(now, TimeSpan.FromMinutes(1));
    Assert.Equal(empty.Revision,
        store.SnapshotWithRevision(now.AddSeconds(1), TimeSpan.FromMinutes(1)).Revision);

    store.Upsert(Plane("ABC123", 1, 2), now);

    Assert.True(store.SnapshotWithRevision(now, TimeSpan.FromMinutes(1)).Revision > empty.Revision);
  }

  private static adsb.PointLatLngAltHdg Plane(string id, double lat, double lng) =>
      new(lat, lng, 100, 90, 1200, id, DateTime.UtcNow) { CallSign = id };
}
