using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class ConfigMountParityTests {
  [Fact]
  public void Modern_mount_parameter_family_wins_over_legacy_names() {
    string prefix = ConfigMountViewModel.ResolveMountParameterPrefix(new[] {
      "MNT_TYPE", "MNT_ANGMIN_TIL", "MNT1_TYPE", "MNT1_PITCH_MIN",
    });

    Assert.Equal("MNT1_", prefix);
  }

  [Theory]
  [InlineData(0, 0, false)]
  [InlineData(20, 100, false)]
  [InlineData(100, 100, true)]
  [InlineData(101, 100, true)]
  public void Empty_parameter_counters_do_not_masquerade_as_a_completed_download(
      int received, int reported, bool expected) {
    Assert.Equal(expected, ConfigParamLoadingViewModel.HasAllParameters(received, reported));
  }

  [Fact]
  public void Empty_parameter_download_never_exposes_stale_configuration_fields() {
    Assert.False(ConfigParamLoadingViewModel.HasAllParameters(0, 0));
  }

  [Theory]
  [InlineData("MNT_", "Tilt", "MNT_ANGMIN_TIL", "MNT_ANGMAX_TIL", "MNT_RC_IN_TILT", 100)]
  [InlineData("MNT_", "Roll", "MNT_ANGMIN_ROL", "MNT_ANGMAX_ROL", "MNT_RC_IN_ROLL", 100)]
  [InlineData("MNT_", "Pan", "MNT_ANGMIN_PAN", "MNT_ANGMAX_PAN", "MNT_RC_IN_PAN", 100)]
  [InlineData("MNT1_", "Tilt", "MNT1_PITCH_MIN", "MNT1_PITCH_MAX", null, 1)]
  [InlineData("MNT1_", "Roll", "MNT1_ROLL_MIN", "MNT1_ROLL_MAX", null, 1)]
  [InlineData("MNT1_", "Pan", "MNT1_YAW_MIN", "MNT1_YAW_MAX", null, 1)]
  public void Axis_parameters_follow_the_actual_legacy_or_modern_firmware_schema(
      string prefix, string axis, string minimum, string maximum, string? input, double scale) {
    var names = ConfigMountViewModel.ResolveAxisParameters(prefix, axis);

    Assert.Equal(minimum, names.Minimum);
    Assert.Equal(maximum, names.Maximum);
    Assert.Equal(input, names.Input);
    Assert.Equal(scale, names.Scale);
  }

  [Theory]
  [InlineData(-4500, 100, -45)]
  [InlineData(12.5, 1, 12.5)]
  public void Mount_angle_scale_round_trips_legacy_centidegrees_and_modern_degrees(
      double parameterValue, double scale, double degrees) {
    Assert.Equal(
        degrees, ConfigMountViewModel.ParameterAngleToDegrees(parameterValue, scale));
    Assert.Equal(
        parameterValue, ConfigMountViewModel.DegreesToParameterAngle(degrees, scale));
  }

  [Fact]
  public void Modern_mount_type_fallback_covers_the_current_ardupilot_backends() {
    var options = ConfigMountViewModel.BuildMountTypeOptions(
        "MNT1_", [], currentValue: 14);

    Assert.Equal(Enumerable.Range(0, 15), options.Select(option => option.Value));
    Assert.Equal("Gremsy", options[6].Text);
    Assert.Equal("XFRobot", options[14].Text);
  }

  [Theory]
  [InlineData("SERVO", "SERVO1", "SERVO14")]
  [InlineData("RC", "RC5", "RC14")]
  public void Output_channel_list_matches_the_loaded_parameter_family(
      string family, string first, string last) {
    var channels = ConfigMountViewModel.BuildChannelNames(family);

    Assert.Equal(first, channels.First());
    Assert.Equal(last, channels.Last());
    Assert.DoesNotContain(channels, name => !name.StartsWith(family, StringComparison.Ordinal));
  }
}
