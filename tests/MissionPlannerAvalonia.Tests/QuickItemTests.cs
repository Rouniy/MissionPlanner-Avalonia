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
}
