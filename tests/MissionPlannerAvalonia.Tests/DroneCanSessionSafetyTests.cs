using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class DroneCanSessionSafetyTests {
  [Fact]
  public void Results_require_the_same_modem_vehicle_and_monotonic_revisions() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    var expected = new DroneCanSessionTarget(firstLink, 1, 1);

    Assert.True(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(secondLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 2, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        true, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 8, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 6));
  }

  [AvaloniaFact]
  public void Target_switch_immediately_clears_nodes_parameters_selection_and_logs() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    DroneCanSessionTarget? current = new(firstLink, 1, 1);
    using var viewModel = new ConfigDroneCanViewModel(() => current);
    var node = new DroneCanNode { Id = 42, Name = "old-node" };
    viewModel.Nodes.Add(node);
    viewModel.SelectedNode = node;
    viewModel.NodeParams.Add(new DroneCanParam { Name = "OLD_PARAM", Value = "1" });
    viewModel.DebugLog.Add(new DroneCanLog { Node = "42", Text = "old-message" });

    current = new DroneCanSessionTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.Empty(viewModel.Nodes);
    Assert.Empty(viewModel.NodeParams);
    Assert.Empty(viewModel.DebugLog);
    Assert.Null(viewModel.SelectedNode);
    Assert.False(viewModel.IsConnected);
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_view_locks_interface_selection_while_a_session_or_operation_is_active() {
    using var viewModel = new ConfigDroneCanViewModel(() => null);
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<ComboBox>("DroneCanInterfaceSelector"));
    Assert.NotNull(view.FindControl<Button>("DroneCanConnectButton"));
    Assert.True(viewModel.CanChangeInterface);
    Assert.True(viewModel.CanToggleConnection);
  }

  [Fact]
  public void Direct_slcan_port_identity_rejects_the_same_open_device() {
    Assert.True(ConfigDroneCanViewModel.PortsIdentifySameDevice("COM7", "COM7"));
    Assert.True(ConfigDroneCanViewModel.PortsIdentifySameDevice(
        "/dev/ttyACM0", "/dev/ttyACM0"));
    Assert.False(ConfigDroneCanViewModel.PortsIdentifySameDevice(
        "/dev/ttyACM0", "/dev/ttyUSB0"));
    Assert.False(ConfigDroneCanViewModel.PortsIdentifySameDevice(null, "/dev/ttyUSB0"));
  }

  [AvaloniaFact]
  public async Task Direct_slcan_opens_without_mavlink_survives_target_switch_and_releases_port() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    DroneCanSessionTarget? current = null;
    using var viewModel = new ConfigDroneCanViewModel(
        () => current,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";
    viewModel.SelectedSerialBaud = 115200;

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    Assert.True(transport.IsOpen);
    Assert.Contains("direct SLCAN", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("C\r", transport.Stream.WritesText);
    Assert.Contains("O\r", transport.Stream.WritesText);
    int closeCommandsBeforeDisconnect = CountOccurrences(transport.Stream.WritesText, "C\r");

    current = new DroneCanSessionTarget(new MissionPlanner.MAVLinkInterface(), 7, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.True(viewModel.IsConnected);
    Assert.True(transport.IsOpen);

    viewModel.ToggleConnectCommand.Execute(null);
    Dispatcher.UIThread.RunJobs();

    Assert.False(viewModel.IsConnected);
    Assert.False(transport.IsOpen);
    Assert.True(transport.Stream.CloseCount > 0);
    Assert.True(CountOccurrences(transport.Stream.WritesText, "C\r") >
        closeCommandsBeforeDisconnect);
  }

  [AvaloniaFact]
  public void Native_view_exposes_direct_slcan_port_and_baud_controls() {
    using var viewModel = new ConfigDroneCanViewModel(
        () => null, serialPortNames: () => ["/dev/test-slcan"]);
    viewModel.SelectedBusIndex = 2;
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<StackPanel>("DirectSlcanOptions"));
    Assert.NotNull(view.FindControl<ComboBox>("DirectSlcanPortSelector"));
    Assert.NotNull(view.FindControl<ComboBox>("DirectSlcanBaudSelector"));
    Assert.NotNull(view.FindControl<Button>("PrepareAutopilotSlcanButton"));
    Assert.True(viewModel.ShowDirectSerialOptions);
    Assert.True(viewModel.CanEditDirectSerial);
    Assert.Contains("/dev/test-slcan", viewModel.SerialPorts);
  }

  private static async Task WaitForAsync(Func<bool> predicate) {
    DateTime deadline = DateTime.UtcNow.AddSeconds(3);
    while (!predicate() && DateTime.UtcNow < deadline) {
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(10);
    }
    Dispatcher.UIThread.RunJobs();
    Assert.True(predicate(), "Timed out waiting for the direct SLCAN session state.");
  }

  private static int CountOccurrences(string text, string value) {
    int count = 0;
    int offset = 0;
    while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0) {
      count++;
      offset += value.Length;
    }
    return count;
  }

  private sealed class FakeSlcanTransport : ICommsSerial {
    internal FakeSlcanTransport(string portName, int baudRate) {
      PortName = portName;
      BaudRate = baudRate;
    }

    internal FakeSlcanStream Stream { get; } = new();
    public Stream BaseStream => Stream;
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen { get; private set; }
    public string PortName { get; set; }
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }

    public void Open() => IsOpen = true;

    public void Close() {
      IsOpen = false;
      Stream.Close();
    }

    public void Dispose() => Close();
    public void DiscardInBuffer() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(string text) => Stream.Write(System.Text.Encoding.ASCII.GetBytes(text));
    public void Write(byte[] buffer, int offset, int count) => Stream.Write(buffer, offset, count);
    public void WriteLine(string text) => Write(text + "\n");
    public void toggleDTR() { }
  }

  private sealed class FakeSlcanStream : Stream {
    private readonly List<byte> _writes = [];

    internal string WritesText {
      get {
        lock (_writes) {
          return System.Text.Encoding.ASCII.GetString(_writes.ToArray());
        }
      }
    }

    internal int CloseCount { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override bool CanTimeout => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override int ReadTimeout { get; set; }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) {
      lock (_writes) {
        for (int i = 0; i < count; i++) {
          _writes.Add(buffer[offset + i]);
        }
      }
    }

    public override void Close() {
      CloseCount++;
      // Keep the fake readable/writable so late, already-scheduled upstream worker iterations do
      // not turn a deterministic lifecycle test into an ObjectDisposedException race.
    }
  }
}
