using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class InitialParamsSafetyTests {
  [Fact]
  public void Initial_parameter_write_uses_only_selected_existing_vehicle_parameters() {
    var selected = new ParamCompareRow {
      Name = "INS_GYRO_FILTER",
      Exists = true,
      Use = true,
    };
    var deselected = new ParamCompareRow {
      Name = "MOT_THST_EXPO",
      Exists = true,
      Use = false,
    };
    var absent = new ParamCompareRow {
      Name = "NOT_ON_VEHICLE",
      Exists = false,
      Use = true,
    };

    var writable = ConfigInitialParamsViewModel.SelectWritableRows(
        new[] { selected, deselected, absent },
        new[] { "INS_GYRO_FILTER", "MOT_THST_EXPO", "NOT_ON_VEHICLE" });

    Assert.Single(writable);
    Assert.Same(selected, writable[0]);
  }

  [Fact]
  public void Initial_parameter_write_rechecks_the_live_available_parameter_set() {
    var stale = new ParamCompareRow {
      Name = "FENCE_ENABLE",
      Exists = true,
      Use = true,
    };

    Assert.Empty(ConfigInitialParamsViewModel.SelectWritableRows(
        new[] { stale }, new[] { "ARMING_CHECK" }));
  }
}
