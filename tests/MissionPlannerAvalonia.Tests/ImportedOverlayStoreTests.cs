using Avalonia.Headless.XUnit;
using Mapsui;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

[Collection("Imported overlay store")]
public sealed class ImportedOverlayStoreTests {
  [AvaloniaFact]
  public void FlightData_copy_matches_upstream_routes_only_semantics() {
    var source = new ImportedMapOverlay(
        [new ImportedOverlayRoute("route", [
          new ImportedGeoPoint(40, 30), new ImportedGeoPoint(41, 31),
        ], new ImportedMapColor(255, 255, 255))],
        [new ImportedOverlayMarker("point", new ImportedGeoPoint(42, 32))],
        [new ImportedOverlayRaster("image", [1], 42, 40, 32, 30)]);
    int notifications = 0;
    void Changed() => notifications++;
    ImportedOverlayStore.FlightDataChanged += Changed;
    try {
      ImportedOverlayStore.CopyRoutesToFlightData(source);

      Assert.Single(ImportedOverlayStore.FlightData.Routes);
      Assert.Empty(ImportedOverlayStore.FlightData.Markers);
      Assert.Empty(ImportedOverlayStore.FlightData.Rasters);
      Assert.Equal(1, notifications);

      var map = new MapView();
      Mapsui.Layers.ILayer layer = Assert.Single(map.Map.Layers,
          candidate => candidate.Name == "Imported map overlay");
      MRect extent = Assert.IsType<MRect>(layer.Extent);
      Assert.Single(layer.GetFeatures(extent, 1));

      ImportedOverlayStore.ClearFlightData();
      Assert.False(ImportedOverlayStore.FlightData.HasContent);
      Assert.Equal(2, notifications);
    } finally {
      ImportedOverlayStore.FlightDataChanged -= Changed;
      ImportedOverlayStore.ClearFlightData();
    }
  }
}
