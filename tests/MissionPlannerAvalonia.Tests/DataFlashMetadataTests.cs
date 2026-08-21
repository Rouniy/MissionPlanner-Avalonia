using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class DataFlashMetadataTests {
  [Fact]
  public void Parameter_export_is_sorted_and_uses_mission_planner_format() {
    string path = Path.Combine(Path.GetTempPath(), $"mp_params_{Guid.NewGuid():N}.param");
    try {
      DataFlashLog.ExportParameters([
        new DataFlashParameter("WPNAV_SPEED", "500", "1000"),
        new DataFlashParameter("ARMING_CHECK", "1", "1"),
      ], path);

      string[] lines = File.ReadAllLines(path);
      Assert.StartsWith("#", lines[0]);
      Assert.Equal("ARMING_CHECK,1", lines[1]);
      Assert.Equal("WPNAV_SPEED,500", lines[2]);
    } finally {
      File.Delete(path);
    }
  }
}
