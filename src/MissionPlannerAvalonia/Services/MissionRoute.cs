namespace MissionPlannerAvalonia.Services;

internal static class MissionRoute {
  internal static bool IsNavigation(ushort command) {
    var value = (MAVLink.MAV_CMD)command;
    return (value >= MAVLink.MAV_CMD.WAYPOINT && value < MAVLink.MAV_CMD.LAST
            && value != MAVLink.MAV_CMD.ROI)
           || value == MAVLink.MAV_CMD.DO_LAND_START;
  }
}
