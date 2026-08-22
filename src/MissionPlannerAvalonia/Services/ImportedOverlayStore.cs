using System;
using System.Linq;

namespace MissionPlannerAvalonia.Services;

// The official Mission Planner keeps a separate static KML overlay for Flight Data.
// Store only immutable import models here; each Mapsui map builds its own features.
internal static class ImportedOverlayStore {
  private static ImportedMapOverlay _flightData = ImportedMapOverlay.Empty;

  internal static event Action? FlightDataChanged;

  internal static ImportedMapOverlay FlightData => _flightData;

  internal static void ClearFlightData() => SetFlightData(ImportedMapOverlay.Empty);

  internal static void CopyRoutesToFlightData(ImportedMapOverlay source) =>
      SetFlightData(new ImportedMapOverlay(
          source.Routes.ToArray(),
          Array.Empty<ImportedOverlayMarker>(),
          Array.Empty<ImportedOverlayRaster>()));

  internal static void CopyVectorGeometryToFlightData(ImportedMapOverlay source) =>
      SetFlightData(new ImportedMapOverlay(
          source.Routes.ToArray(),
          source.Markers.ToArray(),
          Array.Empty<ImportedOverlayRaster>()));

  private static void SetFlightData(ImportedMapOverlay overlay) {
    _flightData = overlay ?? throw new ArgumentNullException(nameof(overlay));
    FlightDataChanged?.Invoke();
  }
}
