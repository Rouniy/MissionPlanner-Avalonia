using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public sealed class DeveloperBarometerAdjustmentTests {
  [Theory]
  [InlineData(101325, 30.48, 101663.328)]
  [InlineData(101325, -30.48, 100986.672)]
  [InlineData(100000, 100, 101110)]
  [InlineData(100000, -100, 98890)]
  public void Pressure_adjustment_matches_official_mission_planner_formula(
      double currentPressure, double offsetMetres, double expectedPressure) {
    bool valid = ConfigDeveloperToolsViewModel.TryCalculateBarometerPressure(
        currentPressure, offsetMetres, out double targetPressure);

    Assert.True(valid);
    Assert.Equal(expectedPressure, targetPressure, 6);
  }

  [Theory]
  [InlineData(101325, 100.001)]
  [InlineData(101325, -100.001)]
  [InlineData(79999, 0)]
  [InlineData(120001, 0)]
  [InlineData(double.NaN, 1)]
  [InlineData(101325, double.PositiveInfinity)]
  public void Pressure_adjustment_rejects_unsafe_or_non_finite_values(
      double currentPressure, double offsetMetres) {
    Assert.False(ConfigDeveloperToolsViewModel.TryCalculateBarometerPressure(
        currentPressure, offsetMetres, out _));
  }

  [Fact]
  public void Vehicle_target_guard_rejects_selection_and_same_id_state_replacement() {
    using var link = new MAVLinkInterface();
    link.sysidcurrent = 42;
    link.compidcurrent = 1;
    MAVState captured = link.MAV;

    Assert.True(ConfigDeveloperToolsViewModel.IsSelectedVehicleTarget(
        link, captured, 42, 1));

    link.sysidcurrent = 43;
    Assert.False(ConfigDeveloperToolsViewModel.IsSelectedVehicleTarget(
        link, captured, 42, 1));

    using var replacementLink = new MAVLinkInterface();
    replacementLink.sysidcurrent = 42;
    replacementLink.compidcurrent = 1;
    Assert.False(ConfigDeveloperToolsViewModel.IsSelectedVehicleTarget(
        replacementLink, captured, 42, 1));
  }

  [AvaloniaFact]
  public void Developer_tools_exposes_official_barometer_altitude_adjustment() {
    using var tools = new ConfigDeveloperToolsViewModel();

    Assert.Contains(tools.Actions,
        action => action.Label == "Adjust Barometer Altitude");
  }
}
