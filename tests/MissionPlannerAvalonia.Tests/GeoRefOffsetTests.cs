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
}
