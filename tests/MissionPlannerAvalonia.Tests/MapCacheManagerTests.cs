using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class MapCacheManagerTests {
  [Fact]
  public void Scans_and_cleans_provider_or_total_cache_safely() {
    string root = Path.Combine(Path.GetTempPath(), "mp-map-cache-" + Guid.NewGuid().ToString("N"));
    string providerA = Path.Combine(root, "provider-a", "1", "2");
    string providerB = Path.Combine(root, "provider-b");
    Directory.CreateDirectory(providerA);
    Directory.CreateDirectory(providerB);
    string oldTile = Path.Combine(providerA, "old.tile");
    string newTile = Path.Combine(providerA, "new.tile");
    string otherTile = Path.Combine(providerB, "other.tile");
    try {
      File.WriteAllBytes(oldTile, [1, 2, 3]);
      File.WriteAllBytes(newTile, [1, 2, 3, 4]);
      File.WriteAllBytes(otherTile, [1, 2]);
      File.SetLastWriteTimeUtc(oldTile, DateTime.UtcNow.AddDays(-40));

      var entries = MapCacheManager.Scan(root);
      Assert.Equal(3, entries.Count);
      var a = Assert.Single(entries, item => item.Name == "provider-a");
      var total = Assert.Single(entries, item => item.IsTotal);
      Assert.Equal(2, a.FileCount);
      Assert.Equal(7, a.SizeBytes);
      Assert.Equal(3, total.FileCount);
      Assert.Equal(9, total.SizeBytes);

      var oldResult = MapCacheManager.DeleteOlderThan(a, DateTime.UtcNow.AddDays(-30), root);
      Assert.Equal(1, oldResult.RemovedFiles);
      Assert.Equal(3, oldResult.FreedBytes);
      Assert.False(File.Exists(oldTile));
      Assert.True(File.Exists(newTile));

      var allResult = MapCacheManager.DeleteOlderThan(total, DateTime.MaxValue, root);
      Assert.Equal(2, allResult.RemovedFiles);
      Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    } finally {
      if (Directory.Exists(root)) {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void Refuses_cleanup_outside_the_configured_cache_root() {
    string root = Path.Combine(Path.GetTempPath(), "mp-map-root-" + Guid.NewGuid().ToString("N"));
    string outside = Path.Combine(Path.GetTempPath(), "mp-map-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    try {
      var entry = new MapCacheSnapshot("outside", outside, 0, 0, null);
      Assert.Throws<InvalidOperationException>(() =>
          MapCacheManager.DeleteOlderThan(entry, DateTime.MaxValue, root));
    } finally {
      Directory.Delete(root, recursive: true);
      Directory.Delete(outside, recursive: true);
    }
  }
}
