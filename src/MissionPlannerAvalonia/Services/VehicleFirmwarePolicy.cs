using System;
using System.Collections.Generic;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal sealed record VehicleFirmwareUpdate(string VehicleType, Version Current, Version Available);

internal static class VehicleFirmwarePolicy {
  internal static VehicleFirmwareUpdate? FindNewerOfficialRelease(
      string? versionString, IEnumerable<APFirmware.FirmwareInfo?> releases) {
    if (string.IsNullOrWhiteSpace(versionString)) {
      return null;
    }

    string firmwareName = versionString.Split(
        ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    Version current = VersionDetection.GetVersion(versionString);

    // Match the first vehicle family exactly as upstream MainV2 does. The manifest commonly uses
    // "Copter" for a version string beginning with "ArduCopter", hence the contains comparison.
    foreach (var release in releases) {
      if (release == null || release.MavFirmwareVersion == null ||
          string.IsNullOrWhiteSpace(release.VehicleType) ||
          !firmwareName.Contains(release.VehicleType, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      return release.MavFirmwareVersion > current
          ? new VehicleFirmwareUpdate(release.VehicleType, current, release.MavFirmwareVersion)
          : null;
    }

    return null;
  }
}
