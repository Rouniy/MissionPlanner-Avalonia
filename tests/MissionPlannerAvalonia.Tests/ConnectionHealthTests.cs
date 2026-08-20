using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class ConnectionHealthTests {
  [Fact]
  public void Armed_link_is_not_closed_during_a_telemetry_fade() {
    var now = new DateTime(2026, 8, 21, 12, 0, 20, DateTimeKind.Utc);
    var connected = now.AddSeconds(-20);

    Assert.False(ConnectionHealth.ShouldCloseSilentLink(
        true, now, DateTime.MinValue, connected, TimeSpan.FromSeconds(10)));
    Assert.True(ConnectionHealth.ShouldCloseSilentLink(
        false, now, DateTime.MinValue, connected, TimeSpan.FromSeconds(10)));
  }

  [Fact]
  public void MissingTimestampIsNotTreatedAsAnEstablishedSilentLink() {
    Assert.False(ConnectionHealth.IsSilent(
        new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        DateTime.MinValue, DateTime.MinValue, TimeSpan.FromSeconds(10)));
  }

  [Fact]
  public void LinkBecomesSilentOnlyAfterTimeout() {
    var now = new DateTime(2026, 8, 21, 12, 0, 20, DateTimeKind.Utc);

    Assert.False(ConnectionHealth.IsSilent(
        now, now.AddSeconds(-10), now.AddMinutes(-1), TimeSpan.FromSeconds(10)));
    Assert.True(ConnectionHealth.IsSilent(
        now, now.AddSeconds(-11), now.AddMinutes(-1), TimeSpan.FromSeconds(10)));
  }

  [Fact]
  public void NewSessionGetsGracePeriodEvenWithAStalePreviousTimestamp() {
    var now = new DateTime(2026, 8, 21, 12, 0, 20, DateTimeKind.Utc);

    Assert.False(ConnectionHealth.IsSilent(
        now, now.AddHours(-1), now.AddSeconds(-5), TimeSpan.FromSeconds(10)));
    Assert.True(ConnectionHealth.IsSilent(
        now, DateTime.MinValue, now.AddSeconds(-11), TimeSpan.FromSeconds(10)));
  }
}
