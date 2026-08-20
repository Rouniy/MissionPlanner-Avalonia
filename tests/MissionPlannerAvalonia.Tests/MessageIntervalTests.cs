using MissionPlannerAvalonia.ViewModels;
using Xunit;

namespace MissionPlannerAvalonia.Tests;

public class MessageIntervalTests {
  [Theory]
  [InlineData(1, 1_000_000)]
  [InlineData(2, 500_000)]
  [InlineData(10, 100_000)]
  [InlineData(50, 20_000)]
  public void ConvertsRateToMicroseconds(double rateHz, float expected) {
    Assert.Equal(expected, FlightDataViewModel.MessageIntervalMicroseconds(rateHz));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void RejectsInvalidRates(double rateHz) {
    Assert.Throws<ArgumentOutOfRangeException>(
        () => FlightDataViewModel.MessageIntervalMicroseconds(rateHz));
  }

  [Fact]
  public void ClampsExcessiveRatesToProtocolSafeMaximum() {
    Assert.Equal(1_000, FlightDataViewModel.MessageIntervalMicroseconds(10_000));
  }
}
