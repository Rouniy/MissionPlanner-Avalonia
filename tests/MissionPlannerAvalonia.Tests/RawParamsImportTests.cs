using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class RawParamsImportTests {
  [Theory]
  [InlineData("SYSID_SW_MREV")]
  [InlineData("WP_TOTAL")]
  [InlineData("CMD_TOTAL")]
  [InlineData("FENCE_TOTAL")]
  [InlineData("SYS_NUM_RESETS")]
  [InlineData("ARSPD_OFFSET")]
  [InlineData("GND_ABS_PRESS")]
  [InlineData("GND_TEMP")]
  [InlineData("CMD_INDEX")]
  [InlineData("LOG_LASTFILE")]
  [InlineData("FORMAT_VERSION")]
  public void File_import_protects_upstream_runtime_and_calibration_parameters(string name) {
    Assert.True(RawParamsViewModel.IsProtectedFileParameter(name));
  }

  [Fact]
  public void File_import_stages_normal_values_but_skips_protected_values() {
    var normal = Row("RTL_ALT", 1000);
    var protectedRow = Row("FORMAT_VERSION", 120);

    var result = RawParamsViewModel.StageImportedValues(
        new[] { normal, protectedRow },
        new Dictionary<string, double> { ["RTL_ALT"] = 1500, ["FORMAT_VERSION"] = 0 });

    Assert.Equal(2, result.Matched);
    Assert.Equal(1, result.Differing);
    Assert.Equal(1, result.Protected);
    Assert.Equal("1500", normal.ValueText);
    Assert.Equal("120", protectedRow.ValueText);
  }

  [Fact]
  public void Compare_is_read_only_until_selected_rows_are_explicitly_staged() {
    var first = Row("RTL_ALT", 1000);
    var second = Row("WPNAV_SPEED", 500);

    var comparison = RawParamsViewModel.BuildComparison(
        new[] { first, second },
        new Dictionary<string, double> { ["RTL_ALT"] = 1500, ["WPNAV_SPEED"] = 750 });

    Assert.Equal("1000", first.ValueText);
    Assert.Equal("500", second.ValueText);
    comparison.Single(row => row.Name == "WPNAV_SPEED").Use = false;
    Assert.Equal(1, RawParamsViewModel.StageSelectedComparison(
        new[] { first, second }, comparison));
    Assert.Equal("1500", first.ValueText);
    Assert.Equal("500", second.ValueText);
  }

  [Fact]
  public void Compare_never_exposes_or_stages_protected_parameters() {
    var normal = Row("RTL_ALT", 1000);
    var protectedRow = Row("FORMAT_VERSION", 120);
    var comparison = RawParamsViewModel.BuildComparison(
        new[] { normal, protectedRow },
        new Dictionary<string, double> { ["RTL_ALT"] = 1500, ["FORMAT_VERSION"] = 0 });

    Assert.Single(comparison);
    Assert.Equal("RTL_ALT", comparison[0].Name);

    var forged = new[] { new ParamComparisonRow("FORMAT_VERSION", 120, 0) };
    Assert.Equal(0, RawParamsViewModel.StageSelectedComparison(
        new[] { protectedRow }, forged));
    Assert.Equal("120", protectedRow.ValueText);
  }

  [Fact]
  public void Parameter_write_order_uses_the_upstream_enable_sort() {
    var ordered = RawParamsViewModel.ParameterWriteOrder(
        new[] { "Z_PARAM", "B_ENABLE", "A_PARAM", "A_ENABLE" });

    Assert.Equal(new[] { "A_ENABLE", "B_ENABLE", "A_PARAM", "Z_PARAM" }, ordered);
  }

  [Fact]
  public void Parameter_write_confirmation_lists_small_changes_and_summarizes_large_sets() {
    string detailed = RawParamsViewModel.BuildWriteConfirmation(new[] {
      (row: Row("RTL_ALT", 1000), val: 1500d),
      (row: Row("WPNAV_SPEED", 500), val: 750d),
    });
    Assert.Contains("RTL_ALT: 1000 -> 1500", detailed);
    Assert.Contains("WPNAV_SPEED: 500 -> 750", detailed);

    var many = Enumerable.Range(0, 21)
        .Select(index => (row: Row($"P{index}", index), val: (double)index + 1))
        .ToList();
    string summarized = RawParamsViewModel.BuildWriteConfirmation(many);
    Assert.Contains("21 parameters", summarized);
    Assert.DoesNotContain("P0:", summarized);
  }

  private static ParamRow Row(string name, double value) =>
      new(name, value, null, "", "", "", double.MinValue, double.MaxValue);
}
