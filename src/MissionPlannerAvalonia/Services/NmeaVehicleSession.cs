using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Exact MAVLink selection used by the official Follow Me and Moving Base workflows.
/// The link reference is part of the identity because different modems commonly expose
/// the same system/component ids.
/// </summary>
internal sealed record NmeaVehicleTarget(
    MAVLinkInterface Link, byte SystemId, byte ComponentId);

internal static class NmeaVehicleSession {
  internal static NmeaVehicleTarget? CaptureActive(bool requireOpen) {
    MAVLinkInterface link = AppState.comPort;
    if (requireOpen && link.BaseStream?.IsOpen != true) {
      return null;
    }
    return new NmeaVehicleTarget(link, link.MAV.sysid, link.MAV.compid);
  }

  internal static bool Matches(NmeaVehicleTarget? expected, NmeaVehicleTarget? current) =>
      expected != null && current != null
      && ReferenceEquals(expected.Link, current.Link)
      && expected.SystemId == current.SystemId
      && expected.ComponentId == current.ComponentId;

  internal static bool ShouldContinue(
      bool invalidated, NmeaVehicleTarget? expected, NmeaVehicleTarget? current,
      bool requireOpen) =>
      !invalidated && Matches(expected, current)
      && (!requireOpen || expected!.Link.BaseStream?.IsOpen == true);

  internal static string Describe(NmeaVehicleTarget target) =>
      $"vehicle {target.SystemId}:{target.ComponentId} on the selected modem";
}
