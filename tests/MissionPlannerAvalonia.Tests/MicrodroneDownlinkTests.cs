using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class MicrodroneDownlinkTests {
  private static readonly MicrodroneTelemetry Telemetry = new(
      Latitude: 0,
      Longitude: 0,
      Altitude: 100,
      GpsHdop: 1.5,
      SatelliteCount: 12,
      GroundSpeed: 10,
      GroundCourse: 90,
      VerticalSpeed: -1.25,
      Roll: 10,
      Pitch: -5,
      Yaw: 90,
      PressureTemperature: 24.5,
      MagnetometerX: -139,
      MagnetometerY: 12,
      MagnetometerZ: 431);

  [Fact]
  public void Encoder_emits_official_record_set_with_decimal_checksums_and_crlf() {
    string frame = MicrodroneDownlinkEncoder.EncodeFrame(
        Telemetry, new DateTimeOffset(1980, 1, 13, 0, 0, 5, TimeSpan.Zero), 29);
    string[] lines = frame.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    Assert.Equal(7, lines.Length);
    Assert.StartsWith("#1,28,07,2,1,1,1,2,16000,0,2,", lines[0]);
    Assert.StartsWith("#4,2,5,1,25,", lines[1]);
    Assert.StartsWith("#5,637813701,0,0,1.51,12,", lines[2]);
    Assert.StartsWith("#6,10,", lines[3]);
    Assert.StartsWith("#7,", lines[4]);
    Assert.StartsWith("#8,100,100,24.5,", lines[5]);
    Assert.StartsWith("#9,-139,12,431,", lines[6]);
    Assert.EndsWith("\r\n", frame, StringComparison.Ordinal);
    foreach (string line in lines) {
      Assert.Matches(@"^#[0-9],.*,[0-9]{1,3}$", line);
      int finalComma = line.LastIndexOf(',');
      string payload = line[..(finalComma + 1)];
      Assert.Equal(
          MicrodroneDownlinkEncoder.Checksum(payload).ToString(CultureInfo.InvariantCulture),
          line[(finalComma + 1)..]);
    }
    Assert.Equal(85, MicrodroneDownlinkEncoder.Checksum(
        "#1,27,48,1,1,1,1,0,25343,8192,"));
  }

  [Fact]
  public void Encoder_is_invariant_under_a_decimal_comma_process_culture() {
    CultureInfo previousCulture = CultureInfo.CurrentCulture;
    CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
    try {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
      CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");

      string frame = MicrodroneDownlinkEncoder.EncodeFrame(
          Telemetry, new DateTimeOffset(2026, 8, 22, 12, 34, 56, TimeSpan.FromHours(3)), 1);

      Assert.Contains("1.51", frame);
      Assert.Contains("24.5", frame);
      Assert.DoesNotContain("1,51", frame);
      Assert.DoesNotContain("24,5", frame);
    } finally {
      CultureInfo.CurrentCulture = previousCulture;
      CultureInfo.CurrentUICulture = previousUiCulture;
    }
  }

  [Fact]
  public void Gps_week_and_seconds_use_the_absolute_utc_instant() {
    var localOffsetTime = new DateTimeOffset(1980, 1, 13, 3, 0, 5, TimeSpan.FromHours(3));

    MicrodroneDownlinkEncoder.GetGpsTime(localOffsetTime, out int week, out int seconds);

    Assert.Equal(1, week);
    Assert.Equal(5, seconds);
  }

  [Fact]
  public void Source_identity_rejects_modem_and_vehicle_switches_and_stays_invalidated() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    var expected = new MicrodroneSourceTarget(firstLink, 1, 1);

    Assert.True(MicrodroneDownlinkViewModel.ShouldContinue(
        false, expected, new MicrodroneSourceTarget(firstLink, 1, 1)));
    Assert.False(MicrodroneDownlinkViewModel.ShouldContinue(
        false, expected, new MicrodroneSourceTarget(secondLink, 1, 1)));
    Assert.False(MicrodroneDownlinkViewModel.ShouldContinue(
        false, expected, new MicrodroneSourceTarget(firstLink, 2, 1)));
    Assert.False(MicrodroneDownlinkViewModel.ShouldContinue(
        true, expected, new MicrodroneSourceTarget(firstLink, 1, 1)));
  }

  [AvaloniaFact]
  public async Task Active_target_switch_closes_output_and_prevents_further_frames() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    MicrodroneSourceTarget? current = new(firstLink, 1, 1);
    var output = new RecordingSerial();
    using var viewModel = new MicrodroneDownlinkViewModel(
        () => current,
        (_, _) => {
          output.Open();
          return output;
        });
    viewModel.SelectedPort = "TEST";

    await viewModel.ToggleConnectCommand.ExecuteAsync(null);
    await WaitUntilAsync(() => output.WriteCount > 0);
    current = new MicrodroneSourceTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    await WaitUntilAsync(() => !viewModel.IsRunning);
    int writesAfterStop = output.WriteCount;
    await Task.Delay(150);

    Assert.True(output.WasClosed);
    Assert.Equal(writesAfterStop, output.WriteCount);
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_view_and_developer_tools_expose_the_official_workflow() {
    using var viewModel = new MicrodroneDownlinkViewModel();
    using var developerTools = new ConfigDeveloperToolsViewModel();
    var view = new MicrodroneDownlinkView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<Button>("ToggleMicrodroneOutputButton"));
    Assert.NotNull(view.FindControl<Button>("RefreshMicrodronePortsButton"));
    Assert.Contains(developerTools.Actions, action => action.Label == "MicroDrone Downlink");
    Assert.Equal(new[] { 4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200 },
        viewModel.Bauds);
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for (int attempt = 0; attempt < 100; attempt++) {
      Dispatcher.UIThread.RunJobs();
      if (condition()) {
        return;
      }
      await Task.Delay(10);
    }
    Assert.Fail("Condition was not reached before the test timeout.");
  }

  private sealed class RecordingSerial : ICommsSerial {
    private int _open;
    private int _writes;

    public int WriteCount => Volatile.Read(ref _writes);
    public bool WasClosed { get; private set; }
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

    public void Write(string text) {
      if (!IsOpen) {
        throw new IOException("Output is closed.");
      }
      Interlocked.Increment(ref _writes);
    }

    public void Dispose() {
      Close();
      BaseStream.Dispose();
    }

    public void DiscardInBuffer() {
    }

    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(byte[] buffer, int offset, int count) => Write("");
    public void WriteLine(string text) => Write(text);
    public void toggleDTR() {
    }
  }
}
