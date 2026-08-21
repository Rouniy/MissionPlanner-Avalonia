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

  [Fact]
  public void Parameter_cache_must_exist_and_be_newer_than_one_hour() {
    var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Local);

    Assert.True(ParameterCachePolicy.IsFresh(
        "param.json", now, TimeSpan.FromHours(1),
        _ => now.AddMinutes(-59), _ => true));
    Assert.False(ParameterCachePolicy.IsFresh(
        "param.json", now, TimeSpan.FromHours(1),
        _ => now.AddHours(-1), _ => true));
    Assert.False(ParameterCachePolicy.IsFresh(
        "param.json", now, TimeSpan.FromHours(1),
        _ => now, _ => false));
  }

  [Theory]
  [InlineData("/dev/ttyUSB0", true)]
  [InlineData("COM3", true)]
  [InlineData("AUTO", false)]
  [InlineData("TCP", false)]
  [InlineData("UDP", false)]
  [InlineData("UDPCl", false)]
  [InlineData("WS", false)]
  public void Only_physical_serial_endpoints_have_editable_per_port_baud(
      string endpoint, bool expected) {
    Assert.Equal(expected, ConnectionViewModel.IsSerialEndpoint(endpoint));
  }

  [Fact]
  public void Per_port_baud_key_matches_upstream_contract() {
    Assert.Equal("USB_Radio_BAUD", ConnectionViewModel.PortBaudKey("USB Radio"));
  }

  [Theory]
  [InlineData(false, "ArduCopter V4.6", "ABC", "Mission Planner 2026.8.0")]
  [InlineData(true, "", "", "Mission Planner 2026.8.0")]
  [InlineData(true, "ArduCopter V4.6", "ABC",
      "Mission Planner 2026.8.0 ArduCopter V4.6 on ABC")]
  public void Window_title_includes_vehicle_identity_only_while_connected(
      bool connected, string version, string serial, string expected) {
    Assert.Equal(expected, MainWindowViewModel.FormatWindowTitle(
        "Mission Planner 2026.8.0", version, serial, connected));
  }
}
