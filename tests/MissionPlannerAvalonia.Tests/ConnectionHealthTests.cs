using System.Reflection;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.Services;
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
  public async Task New_parameter_read_cancels_unresponsive_device_and_runs_next_device() {
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int active = 0;
    int maximumActive = 0;
    var coordinator = new VehicleParameterLoadCoordinator(async (sysid, _, token, _) => {
      int nowActive = Interlocked.Increment(ref active);
      maximumActive = Math.Max(maximumActive, nowActive);
      try {
        if (sysid == 1) {
          firstStarted.TrySetResult();
          await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
      } finally {
        Interlocked.Decrement(ref active);
      }
    });

    Task<bool> first = coordinator.LoadLatestAsync(1, 1);
    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Task<bool> second = coordinator.LoadLatestAsync(2, 1);

    Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(1)));
    Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(1)));
    Assert.Equal(1, maximumActive);
  }

  [Fact]
  public void Cancellation_reporter_forwards_token_to_upstream_contract() {
    using var source = new CancellationTokenSource();
    var reporter = new CancellationProgressReporter(source.Token);

    source.Cancel();

    Assert.True(reporter.doWorkArgs.CancelRequested);
    reporter.Dispose();
  }

  [Fact]
  public void Switching_vehicle_clears_parameter_values_types_and_reported_count() {
    using var comPort = new MAVLinkInterface();
    var mav = comPort.MAVlist[42, 1];
    mav.param.Add(new MAVLink.MAVLinkParam(
        "DANGEROUS_OLD_VALUE", 1, MAVLink.MAV_PARAM_TYPE.REAL32));
    mav.param.TotalReported = 1;
    mav.param_types["DANGEROUS_OLD_VALUE"] = MAVLink.MAV_PARAM_TYPE.REAL32;

    ConnectionViewModel.ResetSelectedVehicleParameters(mav);

    Assert.Empty(mav.param);
    Assert.Equal(0, mav.param.TotalReported);
    Assert.Empty(mav.param_types);
  }

  [Fact]
  public void Switching_vehicle_discards_both_previous_and_target_parameter_sessions() {
    using var firstLink = new MAVLinkInterface();
    using var secondLink = new MAVLinkInterface();
    var previous = new MavSystemChoice(firstLink, 7, 1, "UDP:14550", "old modem");
    var next = new MavSystemChoice(secondLink, 8, 1, "UDP:14551", "new modem");
    foreach (MavSystemChoice choice in new[] { previous, next }) {
      var mav = choice.Link.MAVlist[choice.SysId, choice.CompId];
      mav.param.Add(new MAVLink.MAVLinkParam(
          $"OLD_{choice.SysId}", choice.SysId, MAVLink.MAV_PARAM_TYPE.REAL32));
      mav.param.TotalReported = 1;
      mav.param_types[$"OLD_{choice.SysId}"] = MAVLink.MAV_PARAM_TYPE.REAL32;
    }

    ConnectionViewModel.ResetParameterSelection(previous, next);

    foreach (MavSystemChoice choice in new[] { previous, next }) {
      var mav = choice.Link.MAVlist[choice.SysId, choice.CompId];
      Assert.Empty(mav.param);
      Assert.Equal(0, mav.param.TotalReported);
      Assert.Empty(mav.param_types);
    }
  }

  [Fact]
  public void Clearing_a_transient_empty_selection_discards_the_previous_parameters() {
    using var link = new MAVLinkInterface();
    var previous = new MavSystemChoice(link, 9, 1, "UDP:14550", "old modem");
    var mav = link.MAVlist[previous.SysId, previous.CompId];
    mav.param.Add(new MAVLink.MAVLinkParam(
        "OLD_VALUE", 1, MAVLink.MAV_PARAM_TYPE.REAL32));
    mav.param.TotalReported = 1;

    ConnectionViewModel.ResetParameterSelection(previous, null);

    Assert.Empty(mav.param);
    Assert.Equal(0, mav.param.TotalReported);
  }

  [Theory]
  [InlineData(0, 0, false)]
  [InlineData(0, 10, false)]
  [InlineData(9, 10, false)]
  [InlineData(10, 10, true)]
  [InlineData(11, 10, true)]
  public void Parameter_editor_exposes_only_a_complete_live_list(
      int received, int reported, bool expected) {
    Assert.Equal(expected, RawParamsViewModel.CanExposeLiveParameters(received, reported));
  }

  [Fact]
  public void Starting_a_new_session_clears_parameters_for_every_known_vehicle() {
    using var comPort = new MAVLinkInterface();
    foreach (var id in new byte[] { 7, 8 }) {
      var mav = comPort.MAVlist[id, 1];
      mav.param.Add(new MAVLink.MAVLinkParam(
          $"OLD_{id}", id, MAVLink.MAV_PARAM_TYPE.REAL32));
      mav.param.TotalReported = 1;
      mav.param_types[$"OLD_{id}"] = MAVLink.MAV_PARAM_TYPE.REAL32;
    }

    ConnectionViewModel.ResetAllVehicleParameters(comPort);

    foreach (var mav in comPort.MAVlist) {
      Assert.Empty(mav.param);
      Assert.Equal(0, mav.param.TotalReported);
      Assert.Empty(mav.param_types);
    }
  }

  [Fact]
  public void Built_mav_state_has_no_automatic_parameter_cache_subscription() {
    using var comPort = new MAVLinkInterface();
    var mav = comPort.MAVlist[0, 0];
    var eventField = typeof(MAVLink.MAVLinkParamList).GetField(
        "PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic);

    Assert.NotNull(eventField);
    Assert.Null(eventField!.GetValue(mav.param));
  }

  [Theory]
  [InlineData("TCP", "192.0.2.1", "5760")]
  [InlineData("UDPCl", "192.0.2.2", "14550")]
  [InlineData("UDP", "14551", "")]
  [InlineData("WS", "ws://192.0.2.3:8080", "")]
  public void Network_stream_uses_one_preconfigured_dialog_only(
      string kind, string primary, string secondary) {
    var stream = ConnectionViewModel.CreateConfiguredNetworkStream(kind, primary, secondary);
    try {
      Assert.True(Assert.IsAssignableFrom<IPreconfiguredNetworkStream>(stream)
          .SuppressesUpstreamInput);
      if (stream is TcpSerial tcp) {
        Assert.Equal(primary, tcp.Host);
        Assert.Equal(secondary, tcp.Port);
      } else if (stream is UdpSerialConnect udpClient) {
        Assert.Equal(secondary, udpClient.Port);
      } else if (stream is UdpSerial udpListener) {
        Assert.Equal(primary, udpListener.Port);
      }
    } finally {
      (stream as IDisposable)?.Dispose();
    }
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

  [Theory]
  [InlineData(false, false, 64)]
  [InlineData(true, true, 64)]
  [InlineData(true, false, 7)]
  public void Auto_hide_leaves_a_hover_target_at_the_top(
      bool autoHide, bool hovered, double expectedHeight) {
    Assert.Equal(expectedHeight, MainWindowViewModel.HeaderHeightFor(autoHide, hovered));
  }

  [Fact]
  public void Firmware_policy_matches_the_upstream_vehicle_family_and_newer_version() {
    var releases = new[] {
      Release("Plane", new Version(4, 7, 0)),
      Release("Copter", new Version(4, 6, 2)),
    };

    var update = VehicleFirmwarePolicy.FindNewerOfficialRelease(
        "ArduCopter V4.5.7 (abc123)", releases);

    Assert.NotNull(update);
    Assert.Equal("Copter", update.VehicleType);
    Assert.Equal(new Version(4, 5, 7), update.Current);
    Assert.Equal(new Version(4, 6, 2), update.Available);
  }

  [Fact]
  public void Firmware_policy_does_not_cross_match_vehicle_families() {
    var releases = new[] { Release("Plane", new Version(99, 0)) };

    Assert.Null(VehicleFirmwarePolicy.FindNewerOfficialRelease(
        "ArduCopter V4.5.7", releases));
  }

  [Theory]
  [InlineData("ArduCopter V4.6.2", 4, 6, 2)]
  [InlineData("ArduCopter V4.7.0", 4, 6, 2)]
  public void Firmware_policy_ignores_equal_or_older_releases(
      string current, int major, int minor, int patch) {
    var releases = new[] { Release("Copter", new Version(major, minor, patch)) };

    Assert.Null(VehicleFirmwarePolicy.FindNewerOfficialRelease(current, releases));
  }

  private static APFirmware.FirmwareInfo Release(string vehicleType, Version version) =>
      new() { VehicleType = vehicleType, MavFirmwareVersion = version };
}
