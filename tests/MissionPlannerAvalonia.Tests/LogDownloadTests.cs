using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class LogDownloadTests {
  [Fact]
  public void Untimed_log_uses_a_stable_id_filename() {
    Assert.Equal("log_42.bin", LogDownloadViewModel.SuggestedFileName(
        new LogDownloadRow { Id = 42, TimeUtc = DateTime.MinValue }));
  }

  [Fact]
  public void Timed_log_filename_is_cross_platform_and_collision_resistant() {
    string name = LogDownloadViewModel.SuggestedFileName(new LogDownloadRow {
      Id = 7,
      TimeUtc = new DateTime(2026, 8, 21, 10, 11, 12, DateTimeKind.Local),
    });

    Assert.Equal("2026-08-21 10-11-12_7.bin", name);
    Assert.DoesNotContain(':', name);
  }
}
