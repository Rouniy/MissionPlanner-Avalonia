using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class ThemeServiceTests {
  [Fact]
  public void Custom_theme_declares_every_color_required_by_the_shared_palette() {
    string[] keys = ThemeService.EditableColors.Select(item => item.Key).ToArray();

    Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    Assert.Contains("MpAccentColor", keys);
    Assert.Contains("MpTextColor", keys);
    Assert.All(ThemeService.EditableColors,
        item => Assert.True(Avalonia.Media.Color.TryParse(item.Fallback, out _)));
  }
}
