using MissionPlannerAvalonia.ViewModels;

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
}
