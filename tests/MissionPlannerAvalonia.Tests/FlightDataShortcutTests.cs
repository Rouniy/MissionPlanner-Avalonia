using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MissionPlannerAvalonia.Services;
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

  [Theory]
  [InlineData(Key.A, "Auto")]
  [InlineData(Key.G, "Loiter")]
  [InlineData(Key.U, "AltHold")]
  [InlineData(Key.S, "Stabilize")]
  [InlineData(Key.H, "Rtl")]
  [InlineData(Key.T, "Takeoff")]
  [InlineData(Key.L, "Land")]
  [InlineData(Key.D0, "MinimumThrottle")]
  public void Optional_flight_shortcuts_match_all_official_plugin_actions(
      Key key, string expected) {
    Assert.Equal(
        Enum.Parse<FlightCommandShortcut>(expected),
        MainWindow.FlightCommandShortcutFor(key, KeyModifiers.Alt));
  }

  [Fact]
  public void Flight_shortcuts_require_the_exact_alt_modifier() {
    Assert.Null(MainWindow.FlightCommandShortcutFor(Key.A, KeyModifiers.None));
    Assert.Null(MainWindow.FlightCommandShortcutFor(
        Key.A, KeyModifiers.Alt | KeyModifiers.Control));
    Assert.Null(MainWindow.FlightCommandShortcutFor(Key.F1, KeyModifiers.Alt));
  }

  [AvaloniaFact]
  public void Shell_shortcuts_do_not_steal_editing_keys_or_button_space() {
    Assert.True(MainWindow.ShouldPreserveFocusedInput(new TextBox(), Key.X));
    Assert.True(MainWindow.ShouldPreserveFocusedInput(new NumericUpDown(), Key.OemPlus));
    Assert.True(MainWindow.ShouldPreserveFocusedInput(new Button(), Key.Space));
    Assert.False(MainWindow.ShouldPreserveFocusedInput(new TextBox(), Key.F5));
    var terminalInput = new TextBox();
    terminalInput.Classes.Add("ssh-terminal-input");
    Assert.True(MainWindow.ShouldPreserveFocusedInput(terminalInput, Key.F5));
    Assert.True(MainWindow.ShouldPreserveFocusedInput(terminalInput, Key.F12));
    Assert.False(MainWindow.ShouldPreserveFocusedInput(null, Key.X));
  }
}
