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
}
