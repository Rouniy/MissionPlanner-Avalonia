using Avalonia.Input;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class FlightDataShortcutTests {
  [Theory]
  [InlineData(Key.D1, 0)]
  [InlineData(Key.D5, 4)]
  [InlineData(Key.D9, 8)]
  [InlineData(Key.D0, 9)]
  public void Digit_shortcuts_map_to_upstream_action_tabs(Key key, int expected) {
    Assert.Equal(expected, MainWindow.ShortcutTabIndex(key));
  }

  [Theory]
  [InlineData(1.0, 1, 2.0)]
  [InlineData(2.0, 1, 3.0)]
  [InlineData(1.0, -1, 0.5)]
  [InlineData(0.5, -1, 0.25)]
  [InlineData(10.0, 1, 10.0)]
  [InlineData(0.1, -1, 0.1)]
  public void Playback_speed_shortcuts_match_upstream_steps(
      double current, int direction, double expected) {
    Assert.Equal(expected, FlightDataViewModel.NextTlogSpeed(current, direction), 6);
  }
}
