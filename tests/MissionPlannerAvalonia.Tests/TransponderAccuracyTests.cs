using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class TransponderAccuracyTests {
  [Fact]
  public void NicAndNacpCodesUseUpstreamContainmentLabels() {
    Assert.Equal("<75 m", TransponderAccuracy.Nic(9));
    Assert.Equal("<30 m", TransponderAccuracy.Nacp(9));
    Assert.Equal("UNKNOWN", TransponderAccuracy.Nic(99));
    Assert.Equal("UNKNOWN", TransponderAccuracy.Nacp(99));
  }
}
