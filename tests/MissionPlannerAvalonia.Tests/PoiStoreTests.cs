using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class PoiStoreTests {
  [Fact]
  public void Parses_legacy_upstream_three_column_poi_files() {
    var points = PoiStore.ParseLines(["35.125\t33.25\tLanding zone"]);

    var point = Assert.Single(points);
    Assert.Equal(35.125, point.Lat);
    Assert.Equal(33.25, point.Lng);
    Assert.Equal(0, point.Alt);
    Assert.Equal("Landing zone", point.Name);
  }

  [Fact]
  public void Parses_port_four_column_poi_files_without_losing_altitude() {
    var points = PoiStore.ParseLines(["35.125\t33.25\t412.5\tRidge"]);

    var point = Assert.Single(points);
    Assert.Equal(412.5, point.Alt);
    Assert.Equal("Ridge", point.Name);
  }

  [Fact]
  public void Ignores_invalid_poi_rows_without_rejecting_the_file() {
    var points = PoiStore.ParseLines([
      "not-a-lat\t33.25\tBad",
      "35.125\t33.25\tGood",
      "",
    ]);

    Assert.Equal("Good", Assert.Single(points).Name);
  }
}
