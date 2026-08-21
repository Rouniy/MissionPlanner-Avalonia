using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class QuickItemTests {
  [Fact]
  public void WarningColorKeepsTextReadable() {
    var style = QuickWarningStyle.Resolve("Yellow", Color.Parse("#D197F8"));

    Assert.Equal(Colors.Yellow, style.Background);
    Assert.Equal(Colors.Black, style.Foreground);
  }

  [Fact]
  public void NoColorRestoresConfiguredNumberColor() {
    var configured = Color.Parse("#D197F8");
    var style = QuickWarningStyle.Resolve("NoColor", configured);

    Assert.Null(style.Background);
    Assert.Equal(configured, style.Foreground);
  }

  [AvaloniaFact]
  public void Warning_and_reset_keep_description_readable() {
    var item = new QuickItem("battery_voltage", "#D197F8");

    item.ApplyWarningColor("Yellow");
    Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(item.LabelBrush).Color);

    item.ApplyWarningColor("NoColor");
    Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(item.LabelBrush).Color);
    Assert.Equal(Colors.Transparent,
        Assert.IsAssignableFrom<ISolidColorBrush>(item.BackgroundBrush).Color);
  }

  [Theory]
  [InlineData("2", "3", 2, 6)]
  [InlineData("4", "3", 4, 12)]
  [InlineData("6", "2", 6, 12)]
  public void QuickViewLayout_accepts_supported_grid(
      string columnsText, string rowsText, int expectedColumns, int expectedCount) {
    Assert.True(FlightDataViewModel.TryParseQuickViewLayout(
        columnsText, rowsText, out int columns, out int count));
    Assert.Equal(expectedColumns, columns);
    Assert.Equal(expectedCount, count);
  }

  [Theory]
  [InlineData("0", "3")]
  [InlineData("7", "1")]
  [InlineData("4", "4")]
  [InlineData("6", "1431655766")]
  [InlineData("two", "3")]
  public void QuickViewLayout_rejects_invalid_or_oversized_grid(
      string columnsText, string rowsText) {
    Assert.False(FlightDataViewModel.TryParseQuickViewLayout(
        columnsText, rowsText, out _, out _));
  }
}
