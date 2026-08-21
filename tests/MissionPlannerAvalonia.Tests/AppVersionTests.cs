using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class AppVersionTests {
  [Theory]
  [InlineData("1.3.83+20260821.8a07b1b", "1.3.83", "2026-08-21", "8a07b1b",
      "1.3.83 (2026-08-21, 8a07b1b)")]
  [InlineData("1.3.83+20260821.8a07b1b.dirty", "1.3.83", "2026-08-21",
      "8a07b1b-dirty", "1.3.83 (2026-08-21, 8a07b1b-dirty)")]
  [InlineData("1.3.83+8A07B1B02FF8", "1.3.83", "", "8A07B1B", "1.3.83 (8A07B1B)")]
  [InlineData("1.3.83+20260821.8a07b1b02ff8b54dc72957154819a6f0f0e4d055",
      "1.3.83", "2026-08-21", "8a07b1b", "1.3.83 (2026-08-21, 8a07b1b)")]
  [InlineData("1.3.83+20260821", "1.3.83", "2026-08-21", "",
      "1.3.83 (2026-08-21)")]
  [InlineData("1.3.83", "1.3.83", "", "", "1.3.83")]
  public void Composite_version_is_split_for_ui(string value, string number,
      string buildDate, string hash, string display) {
    AppVersionParts parsed = AppVersion.Parse(value);

    Assert.Equal(number, parsed.Number);
    Assert.Equal(buildDate, parsed.BuildDate);
    Assert.Equal(hash, parsed.Hash);
    Assert.Equal(display, parsed.Display);
  }

  [Theory]
  [InlineData("1.3.83+not-a-hash")]
  [InlineData("")]
  [InlineData(null)]
  public void Invalid_metadata_is_not_presented_as_a_commit(string? value) {
    AppVersionParts parsed = AppVersion.Parse(value);

    Assert.Empty(parsed.Hash);
  }
}
