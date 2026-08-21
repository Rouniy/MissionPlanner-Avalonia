using System.Text;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class AdsbConfigTests {
  [Fact]
  public void Device_identification_text_trims_padding_without_losing_content() {
    byte[] data = Encoding.ASCII.GetBytes("N123AB \0\0");

    Assert.Equal("N123AB", ConfigADSBViewModel.DecodeDeviceText(data));
  }

  [Theory]
  [InlineData(null, true)]
  [InlineData("", true)]
  [InlineData("   ", true)]
  [InlineData("N123AB", false)]
  public void Empty_identification_requires_explicit_clear_confirmation(
      string? value, bool expected) {
    Assert.Equal(expected, ConfigADSBViewModel.NeedsClearConfirmation(value));
  }
}
