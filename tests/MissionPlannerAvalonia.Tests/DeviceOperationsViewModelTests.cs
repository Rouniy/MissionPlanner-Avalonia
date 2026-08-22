using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class DeviceOperationsViewModelTests {
  [Fact]
  public void Formats_device_register_data_in_addressed_hex_rows() {
    byte[] data = Enumerable.Range(0, 18).Select(value => (byte)value).ToArray();

    string result = DeviceOperationsViewModel.FormatResult(0, 0xf8, data);

    Assert.Contains("18 byte(s)", result);
    Assert.Contains("F8: 00 01 02 03", result);
    Assert.Contains("08: 10 11", result);
  }

  [Fact]
  public void Distinguishes_failed_status_from_empty_timeout() {
    Assert.Contains("timeout", DeviceOperationsViewModel.FormatResult(0, 0, []),
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("result 4", DeviceOperationsViewModel.FormatResult(4, 0, []),
        StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(0, "OK")]
  [InlineData(1, "bad bus")]
  [InlineData(2, "bad device")]
  [InlineData(3, "semaphore unavailable")]
  [InlineData(4, "bad response")]
  [InlineData(99, "unknown")]
  public void Formats_all_upstream_device_operation_statuses(byte status, string meaning) {
    Assert.Contains(meaning, DeviceOperationsViewModel.FormatStatus(status),
        StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Result_is_rejected_after_a_modem_or_target_switch_even_if_selection_returns() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    var expected = new DeviceOperationTarget(firstLink, 1, 1);

    Assert.True(DeviceOperationsViewModel.ShouldAcceptResult(
        invalidated: false, expected, new DeviceOperationTarget(firstLink, 1, 1)));
    Assert.False(DeviceOperationsViewModel.ShouldAcceptResult(
        invalidated: false, expected, new DeviceOperationTarget(secondLink, 1, 1)));
    Assert.False(DeviceOperationsViewModel.ShouldAcceptResult(
        invalidated: false, expected, new DeviceOperationTarget(firstLink, 2, 1)));
    Assert.False(DeviceOperationsViewModel.ShouldAcceptResult(
        invalidated: true, expected, new DeviceOperationTarget(firstLink, 1, 1)));
    Assert.True(DeviceOperationsViewModel.IsStableBinding(
        4, 4, expected, new DeviceOperationTarget(firstLink, 1, 1)));
    Assert.False(DeviceOperationsViewModel.IsStableBinding(
        4, 5, expected, new DeviceOperationTarget(firstLink, 1, 1)));
    Assert.False(DeviceOperationsViewModel.IsStableBinding(
        4, 4, expected, new DeviceOperationTarget(secondLink, 1, 1)));
  }

  [AvaloniaFact]
  public void Device_operations_view_exposes_explicit_active_target_rebinding() {
    using var viewModel = new DeviceOperationsViewModel();
    var view = new DeviceOperationsView { DataContext = viewModel };

    Assert.Same(viewModel, view.DataContext);
    Assert.NotNull(view.FindControl<Avalonia.Controls.Button>("UseActiveTargetButton"));
  }
}
