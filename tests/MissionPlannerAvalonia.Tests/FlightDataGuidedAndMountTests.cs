using Avalonia;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class FlightDataGuidedAndMountTests {
  [Theory]
  [InlineData(Firmwares.ArduCopter2, 1, 10)]
  [InlineData(Firmwares.ArduPlane, 1, 100)]
  [InlineData(Firmwares.ArduCopter2, 3.28084, 32.8084)]
  [InlineData(Firmwares.ArduRover, 3.28084, 328.084)]
  public void Guided_altitude_defaults_match_upstream_display_units(
      Firmwares firmware, double multiplier, double expected) {
    Assert.Equal(expected,
        FlightDataViewModel.GuidedAltitudeDefault(firmware, multiplier), 5);
  }

  [Theory]
  [InlineData(100, 1, 100)]
  [InlineData(328.084, 3.28084, 100)]
  [InlineData(50, 0, 50)]
  public void Display_altitude_is_converted_to_vehicle_metres(
      double displayed, double multiplier, double expectedMetres) {
    Assert.Equal(expectedMetres,
        FlightDataViewModel.DisplayToVehicleAltitude(displayed, multiplier), 5);
  }

  [Theory]
  [InlineData("Absolute", MAVLink.MAV_FRAME.GLOBAL)]
  [InlineData("Relative", MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT)]
  [InlineData("Terrain", MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT)]
  public void Guided_altitude_frame_round_trips(string name, MAVLink.MAV_FRAME frame) {
    byte encoded = FlightDataViewModel.GuidedFrameByte(name);

    Assert.Equal((byte)frame, encoded);
    Assert.Equal(name, FlightDataViewModel.GuidedFrameName(encoded));
  }

  [Fact]
  public void Current_guided_frame_wins_and_saved_frame_is_the_fallback() {
    byte absolute = (byte)MAVLink.MAV_FRAME.GLOBAL;
    byte terrain = (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT;

    Assert.Equal(absolute,
        FlightDataViewModel.ResolveGuidedFrame(true, absolute, terrain.ToString()));
    Assert.Equal(terrain,
        FlightDataViewModel.ResolveGuidedFrame(false, absolute, terrain.ToString()));
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        FlightDataViewModel.ResolveGuidedFrame(false, absolute, "bad"));
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        FlightDataViewModel.ResolveGuidedFrame(false, absolute, "255"));
    Assert.Equal((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        FlightDataViewModel.ResolveGuidedFrame(true, 255, terrain.ToString()));
  }

  [Fact]
  public void Mount_modes_preserve_metadata_values_instead_of_using_combo_indices() {
    var modes = FlightDataViewModel.BuildMountModeOptions(new[] {
      new KeyValuePair<int, string>(4, "GPS Point"),
      new KeyValuePair<int, string>(2, "MAVLink Targeting"),
      new KeyValuePair<int, string>(10, "Home Location"),
    });

    Assert.Equal(new[] { 2, 4, 10 }, modes.Select(mode => mode.Value));
    Assert.Equal("Home Location", modes[2].Text);
  }

  [Fact]
  public void Mount_modes_have_the_upstream_mav_mount_mode_fallback() {
    var modes = FlightDataViewModel.BuildMountModeOptions([]);

    Assert.Equal(new[] { 0, 1, 2, 3, 4 }, modes.Select(mode => mode.Value));
    Assert.Equal("MAVLink Targeting", modes[2].Text);
  }

  [Theory]
  [InlineData(false, 1600, 1000, 1333.333333, 1000, 133.333333, 0)]
  [InlineData(true, 1600, 1000, 1600, 900, 0, 50)]
  [InlineData(false, 800, 1000, 800, 600, 0, 200)]
  public void Hud_aspect_ratio_uses_a_centered_four_three_or_sixteen_nine_viewport(
      bool wide, double availableWidth, double availableHeight,
      double width, double height, double x, double y) {
    Rect viewport = HudLayout.AspectViewport(
        new Size(availableWidth, availableHeight), wide);

    Assert.Equal(width, viewport.Width, 5);
    Assert.Equal(height, viewport.Height, 5);
    Assert.Equal(x, viewport.X, 5);
    Assert.Equal(y, viewport.Y, 5);
  }
}
