using System;
using System.Linq;
using System.Threading;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal static class FlightModeNames {
  private static int _initialized;

  internal static void Initialize() {
    if (Interlocked.Exchange(ref _initialized, 1) != 0) {
      return;
    }
    BinaryLog.onFlightMode += Resolve;
  }

  internal static string? Resolve(string firmware, int modeNumber) {
    if (!Enum.TryParse(firmware, ignoreCase: true, out Firmwares parsed)) {
      return null;
    }
    try {
      return MissionPlanner.ArduPilot.Common.getModesList(parsed)?
          .FirstOrDefault(mode => mode.Key == modeNumber).Value;
    } catch {
      return null;
    }
  }
}
