using System.Text;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class TlogExportServiceTests {
  [Fact]
  public void SensitiveExportWarningNamesLocationAndParameterRisks() {
    Assert.Contains("GPS coordinates", Dialogs.SensitiveExportWarning,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("parameter values", Dialogs.SensitiveExportWarning,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Cancel is the default", Dialogs.SensitiveExportWarning,
        StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ExtractParameters_uses_ArduPilot_float_wire_encoding_and_latest_value() {
    var packets = new[] {
      Packet(MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
          new MAVLink.mavlink_heartbeat_t(
              0, (byte)MAVLink.MAV_TYPE.QUADROTOR,
              (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, 0, 0, 3)),
      Packet(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t(
              41, 1, 0, ParamId("TEST_VALUE"), (byte)MAVLink.MAV_PARAM_TYPE.INT32)),
      Packet(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t(
              42, 1, 0, ParamId("TEST_VALUE"), (byte)MAVLink.MAV_PARAM_TYPE.INT32)),
    };

    var values = TlogExportService.ExtractParameters(packets);

    Assert.Equal(42, values["TEST_VALUE"]);
  }

  [Fact]
  public void ExtractMissionSnapshots_accepts_out_of_order_int_items_and_deduplicates() {
    var count = new MAVLink.mavlink_mission_count_t(
        2, 1, 1, (byte)MAVLink.MAV_MISSION_TYPE.MISSION);
    var first = MissionItem(0, 351234567, 337654321, MAVLink.MAV_CMD.WAYPOINT);
    var second = MissionItem(1, 351235000, 337655000, MAVLink.MAV_CMD.RETURN_TO_LAUNCH);
    var packets = new[] {
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, count),
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, second),
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, first),
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, count),
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, first),
      Packet(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, second),
    };

    var snapshots = TlogExportService.ExtractMissionSnapshots(packets);

    var mission = Assert.Single(snapshots);
    Assert.Equal(2, mission.Count);
    Assert.Equal(35.1234567, mission[0].X, 7);
    Assert.Equal(33.7654321, mission[0].Y, 7);
    Assert.Equal((ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH, mission[1].Command);
  }

  private static MAVLink.mavlink_mission_item_int_t MissionItem(
      ushort sequence, int x, int y, MAVLink.MAV_CMD command) => new(
      0, 0, 0, 0, x, y, 50, sequence, (ushort)command,
      1, 1, 6, // MAV_FRAME_GLOBAL_RELATIVE_ALT_INT
      sequence == 0 ? (byte)1 : (byte)0, 1, (byte)MAVLink.MAV_MISSION_TYPE.MISSION);

  private static byte[] ParamId(string name) {
    var bytes = Encoding.ASCII.GetBytes(name);
    Array.Resize(ref bytes, 16);
    return bytes;
  }

  private static MAVLink.MAVLinkMessage Packet(
      MAVLink.MAVLINK_MSG_ID id, object data, byte system = 1, byte component = 1) {
    var parser = new MAVLink.MavlinkParse();
    return new MAVLink.MAVLinkMessage(parser.GenerateMAVLinkPacket20(
        id, data, false, system, component));
  }
}
