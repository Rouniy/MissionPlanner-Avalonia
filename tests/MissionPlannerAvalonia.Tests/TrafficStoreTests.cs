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

  [Fact]
  public void External_aircraft_wins_over_mavlink_copy_with_the_same_icao() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.Upsert(Plane("ABC123", 1, 2), now, TrafficOrigin.External);
    store.Upsert(Plane("ABC123", 3, 4), now.AddSeconds(1), TrafficOrigin.Mavlink);

    var target = Assert.Single(store.Snapshot(now.AddSeconds(1), TimeSpan.FromMinutes(1)));
    Assert.Equal(TrafficOrigin.External, target.Origin);
    Assert.Equal(1, target.Lat);
    Assert.Equal(2, target.Lng);
  }

  [Fact]
  public void Ais_vessels_are_deduplicated_by_mmsi_and_expire_after_five_minutes() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.UpsertVessel(Vessel(123456789, 34, 33, "OLD"), now.AddMinutes(-6));
    store.UpsertVessel(Vessel(987654321, 35, 34, "FIRST"), now.AddSeconds(-2));
    store.UpsertVessel(Vessel(987654321, 36, 35, "LATEST"), now);

    var target = Assert.Single(store.Snapshot(now, TimeSpan.FromSeconds(30)));
    Assert.Equal(TrafficKind.Vessel, target.Kind);
    Assert.Equal("987654321", target.Id);
    Assert.Equal("LATEST", target.CallSign);
    Assert.Equal(36, target.Lat);
    Assert.Equal(90, target.Heading);
    Assert.Equal(1234, target.Speed);
  }

  [Fact]
  public void Oa_db_is_a_short_lived_radius_and_not_an_aircraft_marker() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    var plane = Plane("000001", 34, 33);
    plane.Squawk = 1250;
    plane.Raw = new MAVLink.mavlink_adsb_vehicle_t {
      emitter_type = byte.MaxValue,
      callsign = System.Text.Encoding.ASCII.GetBytes("OA_DB\0\0\0\0"),
    };
    store.Upsert(plane, now, TrafficOrigin.Mavlink);

    var target = Assert.Single(store.Snapshot(now.AddSeconds(9), TimeSpan.FromSeconds(30)));
    Assert.Equal(TrafficKind.Obstacle, target.Kind);
    Assert.Equal(12.5, target.Radius);
    Assert.Empty(store.Snapshot(now.AddSeconds(11), TimeSpan.FromSeconds(30)));
  }

  [Fact]
  public void Disabling_external_adsb_clears_aircraft_but_keeps_mavlink_ais() {
    var store = new TrafficStore();
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    store.Upsert(Plane("ABC123", 34, 33), now, TrafficOrigin.External);
    store.UpsertVessel(Vessel(123456789, 34.1, 33.1, "VESSEL"), now);

    store.ClearAircraft();

    var target = Assert.Single(store.Snapshot(now, TimeSpan.FromSeconds(30)));
    Assert.Equal(TrafficKind.Vessel, target.Kind);
  }

  [Fact]
  public void Uplink_round_robins_near_external_aircraft_and_scales_mavlink_fields() {
    var targets = new[] {
      Target("ABC123", 34, 33.001, TrafficOrigin.External),
      Target("DEF456", 34, 33.002, TrafficOrigin.External),
      Target("BAD999", 34, 33.003, TrafficOrigin.Mavlink),
      Target("FAR123", 35, 34, TrafficOrigin.External),
      Target("123456789", 34, 33.0005, TrafficOrigin.Ais, TrafficKind.Vessel),
    };
    int index = -1;

    Assert.True(TrafficUplink.TryBuildNext(targets, 34, 33, ref index, out var first));
    Assert.True(TrafficUplink.TryBuildNext(targets, 34, 33, ref index, out var second));

    Assert.Equal(0xABC123u, first.ICAO_address);
    Assert.Equal(340000000, first.lat);
    Assert.Equal(330010000, first.lon);
    Assert.Equal(100000, first.altitude);
    Assert.Equal(9000, first.heading);
    Assert.Equal(1200, first.hor_velocity);
    Assert.Equal("ABC123", TrafficStore.DecodeText(first.callsign));
    Assert.Equal(0xDEF456u, second.ICAO_address);
    Assert.Equal((ushort)(MAVLink.ADSB_FLAGS.VALID_ALTITUDE
        | MAVLink.ADSB_FLAGS.VALID_COORDS
        | MAVLink.ADSB_FLAGS.VALID_VELOCITY
        | MAVLink.ADSB_FLAGS.VALID_HEADING
        | MAVLink.ADSB_FLAGS.VALID_CALLSIGN), first.flags);
  }

  private static adsb.PointLatLngAltHdg Plane(string id, double lat, double lng) =>
      new(lat, lng, 100, 90, 1200, id, DateTime.UtcNow) { CallSign = id };

  private static MAVLink.mavlink_ais_vessel_t Vessel(
      uint mmsi, double lat, double lng, string name) => new() {
        MMSI = mmsi,
        lat = (int)(lat * 1e7),
        lon = (int)(lng * 1e7),
        heading = 9000,
        COG = 8000,
        velocity = 1234,
        turn_rate = 12,
        callsign = System.Text.Encoding.ASCII.GetBytes("CALL123"),
        name = System.Text.Encoding.ASCII.GetBytes(name),
      };

  private static TrafficTarget Target(string id, double lat, double lng, TrafficOrigin origin,
      TrafficKind kind = TrafficKind.Aircraft) => new(id, id, lat, lng, 100, 90, 1200, -25,
          DateTime.UtcNow, MAVLink.MAV_COLLISION_THREAT_LEVEL.NONE, kind, origin, 0x1200);
}
