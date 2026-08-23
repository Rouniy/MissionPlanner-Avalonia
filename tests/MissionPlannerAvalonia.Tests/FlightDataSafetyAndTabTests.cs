using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class FlightDataSafetyAndTabTests {
  private static readonly string[] _headers = {
    "Quick", "Actions", "Messages", "Simple Actions", "PreFlight", "Drone ID", "Gauges", "Status", "Servo/Relay",
    "Scripts", "Payload Control", "Telemetry Logs", "DataFlash Logs", "Transponder",
    "Aux Function",
  };

  [Fact]
  public void Immediate_actions_skip_confirmation_but_vehicle_state_changes_are_gated() {
    Assert.True(FlightDataViewModel.ActionRequiresConfirmation("Terminate_Flight"));
    Assert.True(FlightDataViewModel.ActionRequiresConfirmation("Format_SD_Card"));
    Assert.True(FlightDataViewModel.ActionRequiresConfirmation("Return_To_Launch"));
    Assert.False(FlightDataViewModel.ActionRequiresConfirmation("Trigger_Camera"));
    Assert.False(FlightDataViewModel.ActionRequiresConfirmation("System_Time"));
  }

  [Fact]
  public void Destructive_action_prompts_explain_the_consequence() {
    Assert.Contains("cannot be undone",
        FlightDataViewModel.ActionConfirmationText("Terminate_Flight"));
    Assert.Contains("permanently erased",
        FlightDataViewModel.ActionConfirmationText("Format_SD_Card"));
    Assert.Contains("Disable automatic parachute release",
        FlightDataViewModel.ActionConfirmationText("Do_Parachute"));
    Assert.Equal(MAVLink.PARACHUTE_ACTION.PARACHUTE_DISABLE,
        FlightDataViewModel.ParachuteCommandAction);
  }

  [Fact]
  public void Flight_mode_options_come_from_the_connected_vehicle_family() {
    string[] trackerModes = FlightDataViewModel.ModesForFirmware(
        MissionPlanner.ArduPilot.Firmwares.ArduTracker);

    Assert.Contains("SCAN", trackerModes);
    Assert.Contains("SERVO_TEST", trackerModes);
    Assert.DoesNotContain("ALT_HOLD", trackerModes);
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, true)]
  public void Mode_changes_are_confirmation_gated_during_failsafe(
      bool failsafe, bool expected) {
    Assert.Equal(expected, FlightDataViewModel.RequiresModeFailsafeConfirmation(failsafe));
  }

  [Fact]
  public void Home_waypoint_has_the_upstream_label() {
    Assert.Equal("0 (Home)", new WaypointOption(0, "0 (Home)").ToString());
  }

  [AvaloniaFact]
  public void Built_in_action_selector_matches_the_upstream_enum() {
    var vm = new FlightDataViewModel();
    try {
      string[] expected = {
        "Loiter_Unlim", "Return_To_Launch", "Preflight_Calibration", "Mission_Start",
        "Preflight_Reboot_Shutdown", "Trigger_Camera", "System_Time", "Battery_Reset",
        "ADSB_Out_Ident", "Scripting_cmd_stop_and_restart", "Scripting_cmd_stop",
        "HighLatency_Enable", "HighLatency_Disable", "Toggle_Safety_Switch", "Do_Parachute",
        "Engine_Start", "Engine_Stop", "Terminate_Flight", "Format_SD_Card",
      };

      Assert.Equal(expected, vm.Actions);
      Assert.Equal(7, vm.AuxOptions.Count);
      Assert.Equal(Enumerable.Range(0, 7), vm.AuxOptions.Select(row => row.Index));
    } finally {
      vm.Dispose();
    }
  }

  [Theory]
  [InlineData("0:0", 0, 0)]
  [InlineData("3:1", 3, 1)]
  [InlineData("6:2", 6, 2)]
  public void Aux_requests_preserve_upstream_function_row_and_switch_level(
      string spec, int expectedIndex, int expectedLevel) {
    Assert.True(FlightDataViewModel.TryParseAuxRequest(spec, out int index, out int level));
    Assert.Equal(expectedIndex, index);
    Assert.Equal(expectedLevel, level);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("7:0")]
  [InlineData("0:3")]
  [InlineData("bad")]
  public void Invalid_aux_requests_are_rejected(string? spec) {
    Assert.False(FlightDataViewModel.TryParseAuxRequest(spec, out _, out _));
  }

  [Fact]
  public void Aux_switch_levels_match_the_Mavlink_enum() {
    Assert.Equal("Low", FlightDataViewModel.AuxLevelName(0));
    Assert.Equal("Middle", FlightDataViewModel.AuxLevelName(1));
    Assert.Equal("High", FlightDataViewModel.AuxLevelName(2));
  }

  [Theory]
  [InlineData("34.1234567;33.7654321", 34.1234567, 33.7654321, null)]
  [InlineData(" 34.1 ; 33.2 ; 125.5 ", 34.1, 33.2, 125.5)]
  public void Coordinate_dialogs_use_unambiguous_invariant_semicolon_format(
      string text, double expectedLat, double expectedLng, double? expectedAltitude) {
    Assert.True(FlightDataViewModel.TryParseCoordinates(
        text, out double lat, out double lng, out double? altitude));
    Assert.Equal(expectedLat, lat, 7);
    Assert.Equal(expectedLng, lng, 7);
    Assert.Equal(expectedAltitude, altitude);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("34.1,33.2")]
  [InlineData("91;33")]
  [InlineData("34;181")]
  [InlineData("0;0")]
  [InlineData("34;33;bad")]
  public void Invalid_coordinate_dialog_values_are_rejected(string? text) {
    Assert.False(FlightDataViewModel.TryParseCoordinates(text, out _, out _, out _));
  }

  [AvaloniaFact]
  public void Action_tabs_and_shortcuts_follow_the_upstream_visible_order() {
    var view = new FlightDataView();
    var vm = new FlightDataViewModel();
    try {
      view.DataContext = vm;
      var tabs = Assert.IsType<TabControl>(view.FindControl<TabControl>("FdTabs"));
      var items = tabs.Items.OfType<TabItem>().ToArray();
      string[] expected = {
        "Quick", "Actions", "Messages", "Simple Actions", "PreFlight", "Drone ID", "Gauges",
        "Transponder", "Status", "Servo/Relay", "Aux Function", "Scripts",
        "Payload Control", "Telemetry Logs", "DataFlash Logs",
      };
      Assert.Equal(expected, items.Select(item => item.Header?.ToString()));

      foreach (var item in items) {
        item.IsVisible = true;
      }
      items[0].IsVisible = false;
      vm.SelectActionTab(0);
      Assert.Same(items[1], tabs.SelectedItem);
    } finally {
      vm.Dispose();
    }
  }

  [Fact]
  public void Upstream_visible_tab_setting_is_not_inverted() {
    var hidden = FlightDataView.ResolveHiddenTabs(
        _headers, null, "tabQuick;tabActions;tabPagemessages;tabTLogs;");

    Assert.DoesNotContain("Quick", hidden);
    Assert.DoesNotContain("Actions", hidden);
    Assert.DoesNotContain("Messages", hidden);
    Assert.DoesNotContain("Telemetry Logs", hidden);
    Assert.DoesNotContain("Drone ID", hidden);
    Assert.Contains("Status", hidden);
    Assert.Contains("DataFlash Logs", hidden);
  }

  [Fact]
  public void Early_Avalonia_hidden_header_setting_is_migrated_as_hidden() {
    var hidden = FlightDataView.ResolveHiddenTabs(
        _headers, null, "Messages;Payload Control;DataFlash Logs");

    Assert.Equal(3, hidden.Count);
    Assert.Contains("Messages", hidden);
    Assert.Contains("Payload Control", hidden);
    Assert.Contains("DataFlash Logs", hidden);
    Assert.DoesNotContain("Quick", hidden);
  }

  [Fact]
  public void Port_specific_setting_wins_after_migration() {
    var hidden = FlightDataView.ResolveHiddenTabs(
        _headers, "Status;Scripts", "tabQuick;tabActions;");

    Assert.Equal(2, hidden.Count);
    Assert.Contains("Status", hidden);
    Assert.Contains("Scripts", hidden);
  }

  [Fact]
  public void Visible_tabs_are_saved_using_upstream_internal_names() {
    var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "Messages", "Payload Control",
    };

    string encoded = FlightDataView.EncodeUpstreamVisibleTabs(_headers, hidden);
    var names = encoded.Split(';');

    Assert.Contains("tabQuick", names);
    Assert.Contains("tabActions", names);
    Assert.Contains("tabActionsSimple", names);
    Assert.Contains("tabTLogs", names);
    Assert.DoesNotContain("tabPagemessages", names);
    Assert.DoesNotContain("tabPayload", names);
  }
}
