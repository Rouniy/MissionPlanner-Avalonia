using System.Collections.Concurrent;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Tests;

public class SettingsConcurrencyTests {
  [Fact]
  public async Task Shared_settings_store_survives_parallel_reads_writes_removes_and_snapshots() {
    Settings settings = Settings.Instance;
    Assert.IsType<ConcurrentDictionary<string, string>>(Settings.config);
    string prefix = "concurrency_test_" + Guid.NewGuid().ToString("N") + "_";
    const int workers = 12;
    const int iterations = 3000;

    try {
      Task[] tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() => {
        string key = prefix + worker;
        for (int iteration = 0; iteration < iterations; iteration++) {
          settings[key] = iteration.ToString(System.Globalization.CultureInfo.InvariantCulture);
          _ = settings[key];
          _ = settings.ContainsKey(key);
          if (iteration % 7 == 0) {
            _ = settings.Keys.Count();
          }
          if (iteration % 11 == 0) {
            settings.Remove(key);
          }
        }
      })).ToArray();

      await Task.WhenAll(tasks);
      Assert.Same(settings, Settings.Instance);
    } finally {
      for (int worker = 0; worker < workers; worker++) {
        settings.Remove(prefix + worker);
      }
    }
  }

  [Fact]
  public void Singleton_initialization_returns_one_instance_under_contention() {
    var instances = new ConcurrentBag<Settings>();

    Parallel.For(0, 1000, _ => instances.Add(Settings.Instance));

    Settings expected = Assert.Single(instances.Distinct());
    Assert.Same(Settings.Instance, expected);
  }

  [Fact]
  public void Null_assignment_preserves_upstream_missing_value_semantics() {
    Settings settings = Settings.Instance;
    string key = "null_setting_test_" + Guid.NewGuid().ToString("N");

    settings[key] = "temporary";
    settings[key] = null;

    Assert.Null(settings[key]);
    Assert.False(settings.ContainsKey(key));
  }
}
