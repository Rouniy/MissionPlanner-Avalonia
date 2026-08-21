using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class KIndexServiceTests {
  [Theory]
  [InlineData("The estimated planetary K-index at 0300 UTC on 21 August was 3.33.", 3)]
  [InlineData("THE ESTIMATED PLANETARY K-INDEX AT 1200 UTC WAS 7.", 7)]
  public void Parses_current_noaa_space_weather_wording(string report, int expected) {
    Assert.True(KIndexService.TryParse(report, out int value));
    Assert.Equal(expected, value);
  }

  [Fact]
  public void Rejects_reports_without_a_planetary_k_index() {
    Assert.False(KIndexService.TryParse("Solar flux is 125.", out _));
  }
}
