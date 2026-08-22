using System.IO;
using Avalonia.Headless.XUnit;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class AntennaTrackerOutputTests {
  [Fact]
  public void Factory_exposes_every_official_serial_tracker_interface() {
    Assert.Equal(
        ["Maestro", "ArduTracker", "DegreeTracker"],
        AntennaTrackerOutputFactory.InterfaceNames);
    Assert.True(typeof(AntennaTrackerUIViewModel)
        .IsAssignableFrom(typeof(ConfigAntennaTrackerViewModel)));
    Assert.True(typeof(IActivationAware).IsAssignableFrom(typeof(AntennaTrackerUIViewModel)));
    Assert.True(typeof(IDeactivationAware).IsAssignableFrom(typeof(AntennaTrackerUIViewModel)));
  }

  [AvaloniaFact]
  public void Both_navigation_pages_can_connect_and_disconnect_every_official_driver() {
    foreach (string interfaceName in AntennaTrackerOutputFactory.InterfaceNames) {
      var serial = new RecordingSerial();
      using var viewModel = new AntennaTrackerUIViewModel(
          () => new MissionPlanner.MAVLinkInterface(),
          () => ["TEST"],
          (port, baud) => {
            Assert.Equal("TEST", port);
            Assert.Equal(9600, baud);
            return serial;
          });
      viewModel.SelectedInterface = interfaceName;
      viewModel.SelectedPort = "TEST";
      viewModel.SelectedBaud = "9600";
      viewModel.ManualMode = true;

      viewModel.ConnectCommand.Execute(null);

      Assert.True(viewModel.IsRunning);
      Assert.True(serial.IsOpen);
      Assert.Equal("Connected (" + interfaceName + ").", viewModel.Status);

      viewModel.ConnectCommand.Execute(null);

      Assert.False(viewModel.IsRunning);
      Assert.True(serial.WasClosed);
      Assert.Equal("Disconnected.", viewModel.Status);
    }
  }

  [Fact]
  public void Maestro_emits_official_compact_protocol_setup_and_position_commands() {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured("Maestro", serial);
    tracker.PanSpeed = 100;
    tracker.TiltSpeed = 80;
    tracker.PanAccel = 5;
    tracker.TiltAccel = 7;

    Assert.True(tracker.Init(out string error), error);
    Assert.True(tracker.Setup());
    Assert.True(tracker.PanAndTilt(90, 30));

    Assert.Equal([
      [0x87, 0x00, 0x64, 0x00],
      [0x87, 0x01, 0x50, 0x00],
      [0x89, 0x00, 0x05, 0x00],
      [0x89, 0x01, 0x07, 0x00],
      [0x84, 0x01, 0x24, 0x39],
      [0x84, 0x00, 0x58, 0x36],
    ], serial.ByteWrites);
    Assert.Equal(6, serial.DiscardCount);
  }

  [Fact]
  public void Maestro_preserves_official_180_degree_tilt_flip() {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured("Maestro", serial);
    tracker.TiltStartRange = -90;
    tracker.TiltEndRange = 90;

    Assert.True(tracker.Init(out string error), error);
    Assert.True(tracker.PanAndTilt(150, 10));

    Assert.Equal([
      [0x84, 0x01, 0x40, 0x3e],
      [0x84, 0x00, 0x70, 0x3b],
    ], serial.ByteWrites);
  }

  [Fact]
  public void ArduTracker_emits_official_pwm_text_protocol_with_trim() {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured("ArduTracker", serial);
    tracker.TrimPan = 10;
    tracker.TrimTilt = -5;

    Assert.True(tracker.Init(out string error), error);
    Assert.True(tracker.Setup());
    Assert.True(tracker.PanAndTilt(100, 20));

    Assert.Equal(["!!!PAN:1750,TLT:1777\n"], serial.TextWrites);
  }

  [Fact]
  public void DegreeTracker_emits_official_tenths_of_a_degree_text_protocol() {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured("DegreeTracker", serial);

    Assert.True(tracker.Init(out string error), error);
    Assert.True(tracker.PanAndTilt(12.34, -5.67));

    Assert.Equal(["!!!PAN:0123,TLT:-0056\n"], serial.TextWrites);
  }

  [Theory]
  [InlineData("Maestro")]
  [InlineData("ArduTracker")]
  public void Pwm_trackers_reject_zero_ranges_before_opening_the_port(string interfaceName) {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured(interfaceName, serial);
    tracker.PanStartRange = 0;
    tracker.PanEndRange = 0;

    Assert.False(tracker.Init(out string error));

    Assert.Equal("Invalid pan range.", error);
    Assert.False(serial.WasOpened);
  }

  [Theory]
  [InlineData("Maestro")]
  [InlineData("ArduTracker")]
  public void Pwm_tracker_reverse_flags_round_trip_and_change_the_output(string interfaceName) {
    var serial = new RecordingSerial();
    using IAntennaTrackerOutput tracker = CreateConfigured(interfaceName, serial);
    tracker.PanReverse = true;
    tracker.TiltReverse = true;

    Assert.True(tracker.PanReverse);
    Assert.True(tracker.TiltReverse);
    Assert.True(tracker.Init(out string error), error);
    Assert.True(tracker.PanAndTilt(90, 30));

    if (interfaceName == "Maestro") {
      Assert.Equal([
        [0x84, 0x01, 0x38, 0x24],
        [0x84, 0x00, 0x08, 0x27],
      ], serial.ByteWrites);
    } else {
      Assert.Equal(["!!!PAN:1250,TLT:1166\n"], serial.TextWrites);
    }
  }

  [Fact]
  public async Task Dispose_waits_for_an_inflight_write_and_blocks_later_commands() {
    var serial = new RecordingSerial { BlockWrites = true };
    IAntennaTrackerOutput tracker = CreateConfigured("DegreeTracker", serial);
    Assert.True(tracker.Init(out string error), error);

    Task write = Task.Run(() => tracker.PanAndTilt(1, 2));
    Assert.True(serial.WriteStarted.Wait(TimeSpan.FromSeconds(2)));
    Task dispose = Task.Run(tracker.Dispose);
    await Task.Delay(50);
    Assert.False(dispose.IsCompleted);
    Assert.False(serial.WasClosed);

    serial.AllowWrite.Set();
    await Task.WhenAll(write, dispose);
    int writesAfterDispose = serial.TextWrites.Count;

    Assert.True(serial.WasClosed);
    Assert.False(tracker.PanAndTilt(3, 4));
    Assert.Equal(writesAfterDispose, serial.TextWrites.Count);
  }

  private static IAntennaTrackerOutput CreateConfigured(
      string interfaceName, RecordingSerial serial) {
    IAntennaTrackerOutput tracker = AntennaTrackerOutputFactory.Create(interfaceName, serial);
    tracker.PanStartRange = -180;
    tracker.PanEndRange = 180;
    tracker.TiltStartRange = -45;
    tracker.TiltEndRange = 45;
    tracker.PanPWMRange = 1000;
    tracker.TiltPWMRange = 1000;
    tracker.PanPWMCenter = 1500;
    tracker.TiltPWMCenter = 1500;
    return tracker;
  }

  private sealed class RecordingSerial : ICommsSerial {
    private int _open;

    internal List<byte[]> ByteWrites { get; } = [];
    internal List<string> TextWrites { get; } = [];
    internal int DiscardCount { get; private set; }
    internal bool BlockWrites { get; init; }
    internal ManualResetEventSlim WriteStarted { get; } = new();
    internal ManualResetEventSlim AllowWrite { get; } = new();
    internal bool WasOpened { get; private set; }
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

    public void Open() {
      WasOpened = true;
      Volatile.Write(ref _open, 1);
    }

    public void Close() {
      WasClosed = true;
      Volatile.Write(ref _open, 0);
    }

    public void DiscardInBuffer() => DiscardCount++;

    public void Write(string text) {
      WaitIfRequested();
      TextWrites.Add(text);
    }

    public void Write(byte[] buffer, int offset, int count) {
      WaitIfRequested();
      ByteWrites.Add(buffer.AsSpan(offset, count).ToArray());
    }

    public void Dispose() {
      Close();
      BaseStream.Dispose();
      WriteStarted.Dispose();
      AllowWrite.Dispose();
    }

    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void WriteLine(string text) => Write(text);
    public void toggleDTR() {
    }

    private void WaitIfRequested() {
      if (!IsOpen) {
        throw new IOException("Serial port is closed.");
      }
      if (!BlockWrites) {
        return;
      }
      WriteStarted.Set();
      if (!AllowWrite.Wait(TimeSpan.FromSeconds(5))) {
        throw new TimeoutException("Test did not release the serial write.");
      }
    }
  }
}
