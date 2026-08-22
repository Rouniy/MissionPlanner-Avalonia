using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class SpeechSafetyTests {
  [Theory]
  [InlineData(false, false, false, false)]
  [InlineData(true, false, false, true)]
  [InlineData(true, true, false, false)]
  [InlineData(true, true, true, true)]
  public void Armed_only_setting_gates_regular_speech(
      bool enabled, bool armedOnly, bool armed, bool expected) {
    Assert.Equal(expected, Speech.ShouldSpeakForArmedState(enabled, armedOnly, armed));
  }

  [Fact]
  public void No_data_warning_uses_upstream_grace_and_repeat_intervals() {
    var now = new DateTime(2026, 8, 21, 12, 1, 0, DateTimeKind.Utc);

    Assert.True(SpeechAnnouncer.ShouldWarnNoData(
        now, now.AddSeconds(-4), now.AddSeconds(-31), now.AddSeconds(-6), true));
    Assert.False(SpeechAnnouncer.ShouldWarnNoData(
        now, now.AddSeconds(-4), now.AddSeconds(-30), now.AddSeconds(-6), true));
    Assert.False(SpeechAnnouncer.ShouldWarnNoData(
        now, now.AddSeconds(-3), now.AddSeconds(-31), now.AddSeconds(-6), true));
    Assert.False(SpeechAnnouncer.ShouldWarnNoData(
        now, now.AddSeconds(-4), now.AddSeconds(-31), now.AddSeconds(-5), true));
    Assert.False(SpeechAnnouncer.ShouldWarnNoData(
        now, now.AddSeconds(-4), now.AddSeconds(-31), now.AddSeconds(-6), false));
  }

  [Fact]
  public void No_data_warning_reports_whole_silent_seconds() {
    var now = new DateTime(2026, 8, 21, 12, 1, 0, DateTimeKind.Utc);

    Assert.Equal("WARNING No Data for 7 Seconds",
        SpeechAnnouncer.NoDataMessage(now, now.AddMilliseconds(-7999)));
  }

  [Theory]
  [InlineData("Fence Breach", "", true)]
  [InlineData("Fence Breach", "Fence Breach", false)]
  [InlineData("PreArm: GPS", "", false)]
  [InlineData("PX4v2 boot", "", false)]
  [InlineData("", "", false)]
  public void High_priority_speech_is_new_and_filters_upstream_boot_prearm_noise(
      string message, string previous, bool expected) {
    Assert.Equal(expected, SpeechAnnouncer.ShouldSpeakHighMessage(message, previous));
  }

  [Fact]
  public void Linux_cancel_uses_cancel_all_instead_of_crashing_stop_command() {
    Assert.Contains("-C", Speech.LinuxCancelArguments);
    Assert.DoesNotContain("-S", Speech.LinuxCancelArguments);
    Assert.Contains("MissionPlanner-Avalonia", Speech.LinuxCancelArguments);
  }

  [Fact]
  public void Linux_utterance_selects_real_synthesizer_waits_and_detects_cyrillic() {
    string[] arguments = Speech.LinuxUtteranceArguments("Проверка");

    Assert.Equal("espeak-ng", arguments[Array.IndexOf(arguments, "-o") + 1]);
    Assert.Equal("ru", arguments[Array.IndexOf(arguments, "-l") + 1]);
    Assert.Contains("-w", arguments);
    Assert.Equal("Проверка", arguments[^1]);
    string[] englishArguments = Speech.LinuxUtteranceArguments("Mission Planner warning", "en");
    Assert.Equal("en", englishArguments[Array.IndexOf(englishArguments, "-l") + 1]);
  }

  [Fact]
  public void Speech_queue_coalesces_duplicates_and_discards_oldest_stale_items() {
    var queue = new Queue<string>();
    var queued = new HashSet<string>(StringComparer.Ordinal);

    Assert.True(SpeechQueuePolicy.Add(queue, queued, "one", 3));
    Assert.True(SpeechQueuePolicy.Add(queue, queued, "two", 3));
    Assert.False(SpeechQueuePolicy.Add(queue, queued, "one", 3));
    Assert.True(SpeechQueuePolicy.Add(queue, queued, "three", 3));
    Assert.True(SpeechQueuePolicy.Add(queue, queued, "four", 3));

    Assert.Equal(["two", "three", "four"], queue);
    Assert.DoesNotContain("one", queued);
    Assert.Equal(3, queued.Count);
  }

  [Fact]
  public void Speech_queue_never_exceeds_the_production_bound_during_a_flood() {
    var queue = new Queue<string>();
    var queued = new HashSet<string>(StringComparer.Ordinal);

    for (int index = 0; index < 10000; index++) {
      SpeechQueuePolicy.Add(
          queue, queued, $"message {index}", Speech.MaxPendingUtterances);
      Assert.True(queue.Count <= Speech.MaxPendingUtterances);
      Assert.Equal(queue.Count, queued.Count);
    }

    Assert.Equal(
        ["message 9996", "message 9997", "message 9998", "message 9999"],
        queue);
  }
}
