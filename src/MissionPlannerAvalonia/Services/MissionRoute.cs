namespace MissionPlannerAvalonia.Services;

internal static class MissionRoute {
  private const ushort LegacyNavigationRoi = 80;

  internal static bool IsNavigation(ushort command) {
    var value = (MAVLink.MAV_CMD)command;
    return (value >= MAVLink.MAV_CMD.WAYPOINT && value < MAVLink.MAV_CMD.LAST
            && command != LegacyNavigationRoi)
           || value == MAVLink.MAV_CMD.DO_LAND_START;
  }
}
