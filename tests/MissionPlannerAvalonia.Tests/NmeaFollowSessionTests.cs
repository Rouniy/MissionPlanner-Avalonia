using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class NmeaFollowSessionTests {
  private const string Gga =
      "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

  [Fact]
  public void Exact_target_identity_includes_link_system_component_and_open_state() {
    var firstLink = OpenLink();
    var secondLink = OpenLink();
    var expected = new NmeaVehicleTarget(firstLink, 1, 1);

    Assert.True(NmeaVehicleSession.ShouldContinue(
        false, expected, new NmeaVehicleTarget(firstLink, 1, 1), requireOpen: true));
    Assert.False(NmeaVehicleSession.ShouldContinue(
        false, expected, new NmeaVehicleTarget(secondLink, 1, 1), requireOpen: true));
    Assert.False(NmeaVehicleSession.ShouldContinue(
        false, expected, new NmeaVehicleTarget(firstLink, 2, 1), requireOpen: true));
    Assert.False(NmeaVehicleSession.ShouldContinue(
        false, expected, new NmeaVehicleTarget(firstLink, 1, 2), requireOpen: true));
    Assert.False(NmeaVehicleSession.ShouldContinue(
        true, expected, new NmeaVehicleTarget(firstLink, 1, 1), requireOpen: true));

    firstLink.BaseStream.Close();
    Assert.False(NmeaVehicleSession.ShouldContinue(
        false, expected, new NmeaVehicleTarget(firstLink, 1, 1), requireOpen: true));
  }

  [AvaloniaFact]
  public async Task Follow_me_rejects_start_without_confirmation() {
    MAVLinkInterface link = OpenLink();
    NmeaVehicleTarget? current = new(link, 1, 1);
    int commands = 0;
    int confirmations = 0;
    using var viewModel = new FollowMeViewModel(
        () => current,
        (_, _) => throw new InvalidOperationException("serial input must not open"),
        (_, _, _) => Interlocked.Increment(ref commands),
        (_, _) => {
          confirmations++;
          return Task.FromResult(false);
        });
    viewModel.ManualLat = 0;
    viewModel.ManualLng = 0;
    viewModel.RelativeAltM = 25;

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);

    Assert.False(viewModel.IsRunning);
    Assert.Equal(1, confirmations);
    Assert.Equal(0, commands);
    Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Follow_me_accepts_zero_coordinates_but_stops_on_modem_switch() {
    MAVLinkInterface firstLink = OpenLink();
    MAVLinkInterface secondLink = OpenLink();
    NmeaVehicleTarget? current = new(firstLink, 1, 1);
    int commands = 0;
    Locationwp? last = null;
    using var viewModel = new FollowMeViewModel(
        () => current,
        (_, _) => throw new InvalidOperationException("serial input must not open"),
        (_, waypoint, _) => {
          last = waypoint;
          Interlocked.Increment(ref commands);
        },
        (_, _) => Task.FromResult(true));
    viewModel.UpdateRateHz = 2;
    viewModel.ManualLat = 0;
    viewModel.ManualLng = 0;
    viewModel.RelativeAltM = 30;
    firstLink.giveComport = true;

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);
    await Task.Delay(550);
    Assert.Equal(0, Volatile.Read(ref commands));
    Assert.Contains("busy", viewModel.Status, StringComparison.OrdinalIgnoreCase);

    firstLink.giveComport = false;
    await WaitUntilAsync(() => Volatile.Read(ref commands) > 0);

    Assert.NotNull(last);
    Locationwp sent = last.Value;
    Assert.Equal(0, sent.lat);
    Assert.Equal(0, sent.lng);
    Assert.Equal(30, sent.alt);

    current = new NmeaVehicleTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    await WaitUntilAsync(() => !viewModel.IsRunning
        && viewModel.Status.Contains("active modem or vehicle changed",
            StringComparison.OrdinalIgnoreCase));
    int commandsAfterStop = Volatile.Read(ref commands);
    await Task.Delay(600);

    Assert.Equal(commandsAfterStop, Volatile.Read(ref commands));
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Follow_me_closes_blocking_serial_input_on_target_switch() {
    MAVLinkInterface firstLink = OpenLink();
    MAVLinkInterface secondLink = OpenLink();
    NmeaVehicleTarget? current = new(firstLink, 1, 1);
    var gps = new LineSerial(Gga);
    int commands = 0;
    using var viewModel = new FollowMeViewModel(
        () => current,
        (_, _) => gps,
        (_, _, _) => Interlocked.Increment(ref commands),
        (_, _) => Task.FromResult(true));
    viewModel.UseSerialGps = true;
    viewModel.SelectedPort = "TEST-NMEA";
    viewModel.UpdateRateHz = 2;

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);
    await WaitUntilAsync(() => Volatile.Read(ref commands) > 0);
    current = new NmeaVehicleTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    await WaitUntilAsync(() => !viewModel.IsRunning
        && viewModel.Status.Contains("active modem or vehicle changed",
            StringComparison.OrdinalIgnoreCase));

    Assert.True(gps.WasClosed);
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Moving_base_udp_updates_only_the_bound_target_and_stops_on_switch() {
    MAVLinkInterface firstLink = new();
    MAVLinkInterface secondLink = new();
    NmeaVehicleTarget? current = new(firstLink, 1, 1);
    int baseUpdates = 0;
    int port = FreeUdpPort();
    using var viewModel = new MovingBaseViewModel(
        _ => current,
        (_, _) => Interlocked.Increment(ref baseUpdates),
        (_, _) => throw new InvalidOperationException("rally writes are disabled"),
        _ => Task.FromResult(true));
    viewModel.SelectedInput = MovingBaseViewModel.UdpHost;
    viewModel.NetworkPort = port;
    viewModel.UpdateRateHz = 2;

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);
    Assert.True(viewModel.IsRunning, viewModel.Status);
    await SendUdpAsync(port, Gga + "\r\n");
    await WaitUntilAsync(() => Volatile.Read(ref baseUpdates) > 0);

    current = new NmeaVehicleTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    await WaitUntilAsync(() => !viewModel.IsRunning
        && viewModel.Status.Contains("active modem or vehicle changed",
            StringComparison.OrdinalIgnoreCase));
    int updatesAfterStop = Volatile.Read(ref baseUpdates);
    await SendUdpAsync(port, Gga + "\r\n");
    await Task.Delay(150);

    Assert.Equal(updatesAfterStop, Volatile.Read(ref baseUpdates));
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Moving_base_rally_writes_are_reject_by_default() {
    MAVLinkInterface link = OpenLink();
    NmeaVehicleTarget? current = new(link, 1, 1);
    int confirmations = 0;
    using var viewModel = new MovingBaseViewModel(
        _ => current,
        (_, _) => { },
        (_, _) => throw new InvalidOperationException("rally must not run"),
        _ => {
          confirmations++;
          return Task.FromResult(false);
        });
    viewModel.SelectedInput = MovingBaseViewModel.UdpHost;
    viewModel.NetworkPort = FreeUdpPort();
    viewModel.UpdateRallyPoint = true;

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);

    Assert.False(viewModel.IsRunning);
    Assert.Equal(1, confirmations);
    Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_views_and_developer_tools_expose_both_official_workflows() {
    using var follow = new FollowMeViewModel();
    using var moving = new MovingBaseViewModel();
    var advanced = new ConfigAdvancedViewModel();
    var followView = new FollowMeView { DataContext = follow };
    var movingView = new MovingBaseView { DataContext = moving };

    Assert.NotNull(followView.FindControl<Button>("ToggleFollowMeButton"));
    Assert.NotNull(movingView.FindControl<Button>("ToggleMovingBaseButton"));
    Assert.Contains(advanced.Actions, action => action.Label == "Follow Me");
    Assert.Contains(advanced.Actions, action => action.Label == "Moving Base");
    Assert.Equal(new[] { 4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200 },
        follow.Bauds);
  }

  [Fact]
  public void Follow_me_timing_bounds_stale_gps_and_rejects_invalid_positions() {
    Assert.Equal(TimeSpan.FromMilliseconds(500), FollowMeViewModel.UpdateInterval(2));
    Assert.Equal(TimeSpan.FromSeconds(12), FollowMeViewModel.MaxFixAge(0.25));
    Assert.True(FollowMeViewModel.TryValidatePosition(0, 0, 10, out _));
    Assert.False(FollowMeViewModel.TryValidatePosition(double.NaN, 0, 10, out _));
    Assert.False(FollowMeViewModel.TryValidatePosition(0, 181, 10, out _));
    Assert.False(FollowMeViewModel.TryValidatePosition(0, 0, 0, out _));
  }

  private static MAVLinkInterface OpenLink() {
    var link = new MAVLinkInterface();
    var transport = new LineSerial("");
    link.BaseStream = transport;
    return link;
  }

  private static int FreeUdpPort() {
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
  }

  private static async Task SendUdpAsync(int port, string text) {
    using var client = new UdpClient(AddressFamily.InterNetwork);
    byte[] bytes = Encoding.ASCII.GetBytes(text);
    await client.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port));
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for (int attempt = 0; attempt < 250; attempt++) {
      Dispatcher.UIThread.RunJobs();
      if (condition()) {
        return;
      }
      await Task.Delay(10);
    }
    Assert.Fail("Condition was not reached before the test timeout.");
  }

  private sealed class LineSerial : ICommsSerial {
    private readonly string _line;
    private int _open = 1;
    private int _disposed;

    internal LineSerial(string line) => _line = line;

    internal bool WasClosed { get; private set; }
    public Stream BaseStream { get; } = new MemoryStream();
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public string PortName { get; set; } = "TEST";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }

    public void Open() => Volatile.Write(ref _open, 1);

    public void Close() {
      WasClosed = true;
      Volatile.Write(ref _open, 0);
    }

    public string ReadLine() {
      if (!IsOpen) {
        throw new IOException("Input is closed.");
      }
      Thread.Sleep(5);
      return _line;
    }

    public void Dispose() {
      Close();
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        BaseStream.Dispose();
      }
    }

    public void DiscardInBuffer() {
    }

    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public void Write(string text) {
    }
    public void Write(byte[] buffer, int offset, int count) {
    }
    public void WriteLine(string text) {
    }
    public void toggleDTR() {
    }
  }
}
