using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class MavlinkSerialTcpBridgeTests {
  [Fact]
  public async Task Loopback_bridge_carries_bytes_in_both_directions_and_releases_uart() {
    var serial = new FakeSerialControlSession();
    await using var bridge = new MavlinkSerialTcpBridge(serial, IPAddress.Loopback, 0);
    bridge.Start();

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, bridge.BoundPort);
    await WaitUntilAsync(() => serial.OpenCount == 1);

    byte[] tcpPayload = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
    await client.GetStream().WriteAsync(tcpPayload);
    await WaitUntilAsync(() => serial.Writes.Sum(item => item.Length) >= tcpPayload.Length);
    Assert.Equal(tcpPayload, serial.Writes.SelectMany(item => item).ToArray());

    byte[] vehiclePayload = [0xD3, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD];
    serial.Emit(vehiclePayload);
    var received = new byte[vehiclePayload.Length];
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2))) {
      await client.GetStream().ReadExactlyAsync(received, timeout.Token);
    }

    Assert.Equal(vehiclePayload, received);
    Assert.Equal(tcpPayload.Length, bridge.BytesFromTcp);
    Assert.Equal(vehiclePayload.Length, bridge.BytesToTcp);
    Assert.Equal(0, bridge.DroppedSerialBytes);
    Assert.True(serial.RequestCount > 0);

    client.Close();
    await WaitUntilAsync(() => serial.CloseCount == 1);
    await bridge.StopAsync();
    Assert.True(serial.WasDisposed);
  }

  [Fact]
  public async Task Target_loss_stops_listener_without_waiting_for_a_tcp_client() {
    var serial = new FakeSerialControlSession();
    await using var bridge = new MavlinkSerialTcpBridge(serial, IPAddress.Loopback, 0);
    bridge.Start();
    serial.Current = false;

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    Task completed = await Task.WhenAny(bridge.Completion, Task.Delay(Timeout.Infinite, timeout.Token));
    Assert.Same(bridge.Completion, completed);
    await Assert.ThrowsAsync<SerialControlTargetChangedException>(() => bridge.Completion);
    Assert.True(serial.WasDisposed);
  }

  [Fact]
  public async Task Explicit_stop_is_bounded_and_closes_an_active_serial_session() {
    var serial = new FakeSerialControlSession();
    await using var bridge = new MavlinkSerialTcpBridge(serial, IPAddress.Loopback, 0);
    bridge.Start();
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, bridge.BoundPort);
    await WaitUntilAsync(() => serial.OpenCount == 1);

    Task stop = bridge.StopAsync();
    Task completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2)));

    Assert.Same(stop, completed);
    await stop;
    Assert.Equal(1, serial.CloseCount);
    Assert.True(serial.WasDisposed);
  }

  [Fact]
  public async Task Target_loss_with_a_client_closes_uart_and_prevents_later_forwarding() {
    var serial = new FakeSerialControlSession();
    await using var bridge = new MavlinkSerialTcpBridge(serial, IPAddress.Loopback, 0);
    bridge.Start();
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, bridge.BoundPort);
    await WaitUntilAsync(() => serial.OpenCount == 1);

    serial.Current = false;
    await Assert.ThrowsAsync<SerialControlTargetChangedException>(() => bridge.Completion);
    int writesAtStop = serial.Writes.Count;
    try {
      await client.GetStream().WriteAsync(new byte[] { 1, 2, 3 });
    } catch (IOException) {
    } catch (ObjectDisposedException) {
    }
    await Task.Delay(150);

    Assert.Equal(1, serial.CloseCount);
    Assert.Equal(writesAtStop, serial.Writes.Count);
    Assert.True(serial.WasDisposed);
  }

  [Fact]
  public async Task Listener_accepts_a_new_client_after_the_previous_uart_session_is_released() {
    var serial = new FakeSerialControlSession();
    await using var bridge = new MavlinkSerialTcpBridge(serial, IPAddress.Loopback, 0);
    bridge.Start();

    using (var first = new TcpClient()) {
      await first.ConnectAsync(IPAddress.Loopback, bridge.BoundPort);
      await WaitUntilAsync(() => serial.OpenCount == 1);
    }
    await WaitUntilAsync(() => serial.CloseCount == 1);

    using (var second = new TcpClient()) {
      await second.ConnectAsync(IPAddress.Loopback, bridge.BoundPort);
      await WaitUntilAsync(() => serial.OpenCount == 2);
      await second.GetStream().WriteAsync(new byte[] { 9, 8, 7 });
      await WaitUntilAsync(() => serial.Writes.Count > 0);
    }
    await WaitUntilAsync(() => serial.CloseCount == 2);

    Assert.False(bridge.Completion.IsCompleted);
    Assert.Equal(new byte[] { 9, 8, 7 }, serial.Writes.SelectMany(item => item).ToArray());
  }

  [Fact]
  public void Target_guard_rejects_link_state_and_multi_system_substitution() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    MissionPlanner.MAVState firstVehicle = firstLink.MAVlist[1, 1];
    MissionPlanner.MAVState sameSystemComponent = firstLink.MAVlist[1, 2];
    firstVehicle.sysid = 1;
    firstVehicle.compid = 1;
    sameSystemComponent.sysid = 1;
    sameSystemComponent.compid = 2;
    firstLink.MAVlist[1, 1] = firstVehicle;
    firstLink.MAVlist[1, 2] = sameSystemComponent;
    var target = new SerialControlTarget(firstLink, firstVehicle, 1, 1);

    Assert.True(SerialControlTargetGuard.MatchesSelection(target, firstLink, firstVehicle));
    Assert.False(SerialControlTargetGuard.MatchesSelection(target, secondLink, firstVehicle));
    Assert.False(SerialControlTargetGuard.MatchesSelection(target, firstLink, sameSystemComponent));
    Assert.True(SerialControlTargetGuard.IsAutopilotComponent(1));
    Assert.False(SerialControlTargetGuard.IsAutopilotComponent(100));
    Assert.True(SerialControlTargetGuard.HasSingleSystemTarget(firstLink, 1));

    MissionPlanner.MAVState secondSystem = firstLink.MAVlist[2, 1];
    secondSystem.sysid = 2;
    secondSystem.compid = 1;
    firstLink.MAVlist[2, 1] = secondSystem;

    Assert.False(SerialControlTargetGuard.HasSingleSystemTarget(firstLink, 1));
  }

  [AvaloniaFact]
  public void Native_view_and_developer_tools_expose_the_official_bridge_safely() {
    using var viewModel = new MavlinkSerialTcpBridgeViewModel();
    using var developerTools = new ConfigDeveloperToolsViewModel();
    var view = new MavlinkSerialTcpBridgeView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<Button>("ToggleMavlinkSerialTcpBridgeButton"));
    Assert.NotNull(view.FindControl<CheckBox>("AllowRemoteSerialBridgeClientsCheckBox"));
    Assert.Contains(developerTools.Actions,
        action => action.Label == "MAVLink Serial TCP Bridge");
    Assert.Equal(MAVLink.SERIAL_CONTROL_DEV.GPS1, viewModel.SelectedDevice?.Device);
    Assert.Equal((uint)0, viewModel.SelectedBaud?.BaudRate);
    Assert.Equal(500, viewModel.ListenPort);
    Assert.False(viewModel.AllowRemoteClients);
    Assert.Equal(15, viewModel.Devices.Count);
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for (int attempt = 0; attempt < 200; attempt++) {
      if (condition()) {
        return;
      }
      await Task.Delay(10);
    }
    Assert.Fail("Condition was not reached before the test timeout.");
  }

  private sealed class FakeSerialControlSession : ISerialControlSession {
    private int _openCount;
    private int _closeCount;
    private int _requestCount;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public bool Current { get; set; } = true;
    public bool IsCurrent => Current;
    public string TargetDescription => "1:1 / GPS1";
    public int OpenCount => Volatile.Read(ref _openCount);
    public int CloseCount => Volatile.Read(ref _closeCount);
    public int RequestCount => Volatile.Read(ref _requestCount);
    public bool WasDisposed { get; private set; }
    public ConcurrentQueue<byte[]> Writes { get; } = new();

    public void Open() => Interlocked.Increment(ref _openCount);

    public void RequestData() {
      if (!Current) {
        throw new SerialControlTargetChangedException();
      }
      Interlocked.Increment(ref _requestCount);
    }

    public void Write(ReadOnlyMemory<byte> data) {
      if (!Current) {
        throw new SerialControlTargetChangedException();
      }
      Writes.Enqueue(data.ToArray());
    }

    public void Close() => Interlocked.Increment(ref _closeCount);

    public void Emit(byte[] data) => DataReceived?.Invoke(data);

    public void Dispose() {
      WasDisposed = true;
    }
  }
}
