using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class GeoRefOffsetTests {
  [Fact]
  public void Offset_estimate_uses_a_median_to_ignore_a_bad_timestamp() {
    var origin = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    var log = Enumerable.Range(0, 7).Select(i => origin.AddSeconds(i)).ToArray();
    var photos = log.Select(time => time.AddSeconds(12)).ToArray();
    photos[3] = photos[3].AddHours(1);

    Assert.Equal(12, GeoRefViewModel.EstimateOffset(photos, log));
  }

  [Fact]
  public void Offset_estimate_needs_both_photo_and_log_timestamps() {
    Assert.Null(GeoRefViewModel.EstimateOffset([], [DateTime.UtcNow]));
  }

  [Theory]
  [InlineData(120.5, 35.25, 155.75)]
  [InlineData(120.5, -20.25, 100.25)]
  [InlineData(-5, 0, -5)]
  public void Base_altitude_adjustment_is_applied_in_metres(
      double altitude, double adjustment, double expected) {
    Assert.Equal(expected, GeoRefViewModel.AdjustAltitude(altitude, adjustment), 8);
  }

  [Fact]
  public void Invalid_altitude_adjustment_does_not_poison_report_altitude() {
    Assert.Equal(42, GeoRefViewModel.AdjustAltitude(42, double.NaN));
    Assert.Equal(42, GeoRefViewModel.AdjustAltitude(42, double.PositiveInfinity));
  }
}
