using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HidSharp.Reports;
using MissionPlanner.Joystick;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class JoystickInputTests {
  [Theory]
  [InlineData(short.MinValue, 0)]
  [InlineData(-1, 32767)]
  [InlineData(0, 32768)]
  [InlineData(short.MaxValue, 65535)]
  public void Joydev_axis_normalization_covers_the_full_unsigned_range(short input,
      ushort expected) {
    Assert.Equal(expected, LinuxJoydevJoystick.NormalizeAxis(input));
  }

  [Theory]
  [InlineData(-25337, 0)]
  [InlineData(0, 32768)]
  [InlineData(25337, 65535)]
  [InlineData(short.MinValue, 0)]
  [InlineData(short.MaxValue, 65535)]
  public void Calibrated_joydev_axis_expands_and_clamps_device_endpoints(short input,
      ushort expected) {
    Assert.Equal(expected, LinuxJoydevJoystick.NormalizeAxis(input, -25337, 25337));
  }

  [Fact]
  public void Calibrated_joydev_axis_is_monotonic_over_the_entire_input_range() {
    ushort previous = 0;
    for (int value = short.MinValue; value <= short.MaxValue; value++) {
      ushort current = LinuxJoydevJoystick.NormalizeAxis((short)value, -25337, 25337);
      Assert.True(current >= previous,
          $"Normalization decreased at raw value {value}: {previous} -> {current}");
      previous = current;
    }
  }

  [Fact]
  public void Invalid_range_keeps_the_original_joydev_mapping() {
    const short raw = -25337;

    Assert.Equal(LinuxJoydevJoystick.NormalizeAxis(raw),
        LinuxJoydevJoystick.NormalizeAxis(raw, -100, 100));
  }

  [Fact]
  public void Joydev_state_keeps_button_numbers_indexed() {
    var axes = Enumerable.Repeat((ushort)32768, 128).ToArray();
    var buttons = new bool[128];
    buttons[7] = true;

    var state = new JoydevState(axes, buttons);

    Assert.True(state.GetButtons()[7]);
    Assert.False(state.GetButtons()[6]);
  }

  [Fact]
  public void Axis_detector_selects_the_axis_with_the_largest_real_movement() {
    var baselineAxes = Enumerable.Repeat((ushort)32768, 128).ToArray();
    var movedAxes = (ushort[])baselineAxes.Clone();
    movedAxes[0] = 45000;
    movedAxes[1] = 60000;
    var baseline = new JoydevState(baselineAxes, new bool[128]);
    var moved = new JoydevState(movedAxes, new bool[128]);

    var detected = JoystickDetector.FindMovedAxis(baseline, moved, 8000);

    Assert.Equal(joystickaxis.Y, detected);
  }

  [Fact]
  public void Axis_detector_ignores_changes_below_threshold() {
    var baselineAxes = Enumerable.Repeat((ushort)32768, 128).ToArray();
    var movedAxes = (ushort[])baselineAxes.Clone();
    movedAxes[2] = 40000;

    var detected = JoystickDetector.FindMovedAxis(
        new JoydevState(baselineAxes, new bool[128]),
        new JoydevState(movedAxes, new bool[128]), 8000);

    Assert.Equal(joystickaxis.None, detected);
  }

  [Fact]
  public void Button_detector_only_accepts_a_new_press() {
    var baseline = new bool[16];
    baseline[3] = true;
    var current = (bool[])baseline.Clone();
    current[3] = false;
    current[5] = true;

    Assert.Equal(5, JoystickDetector.FindPressedButton(baseline, current));
  }

  [Theory]
  [InlineData(-127, -127, 127, 0)]
  [InlineData(0, -127, 127, 32768)]
  [InlineData(127, -127, 127, 65535)]
  [InlineData(-500, 0, 1023, 0)]
  [InlineData(2048, 0, 1023, 65535)]
  public void Hid_axis_normalization_uses_and_clamps_descriptor_ranges(int input, int minimum,
      int maximum, int expected) {
    Assert.Equal(expected, HidJoystickReportDecoder.NormalizeAxis(input, minimum, maximum));
  }

  [Fact]
  public void Hid_decoder_maps_generic_flight_stick_axes_hat_and_buttons() {
    var descriptor = new ReportDescriptor(GenericJoystickDescriptor());
    var deviceItem = Assert.Single(descriptor.DeviceItems);
    Assert.True(HidJoystickReportDecoder.IsControllerDeviceItem(deviceItem));
    var decoder = new HidJoystickReportDecoder(new[] { deviceItem });
    var report = Assert.Single(deviceItem.InputReports);
    var input = new byte[report.Length];

    input[1] = 0x81; // X = -127
    input[2] = 0x00; // Y = 0
    input[3] = 0x7F; // Z = 127
    input[4] = 0xC0; // Rx = -64
    input[5] = 0x40; // Ry = 64
    input[6] = 0x20; // Rz = 32
    input[7] = 0x02; // hat = right (2 of 8 positions)
    input[8] = 0x81; // buttons 1 and 8
    input[9] = 0x08; // button 12

    Assert.True(decoder.TryApplyReport(input, report, out var state));
    Assert.Equal(0, state.X);
    Assert.Equal(32768, state.Y);
    Assert.Equal(65535, state.Z);
    Assert.Equal(HidJoystickReportDecoder.NormalizeAxis(-64, -127, 127), state.Rx);
    Assert.Equal(9000, state.GetPointOfView()[0]);
    Assert.Equal(12, decoder.ButtonCount);
    Assert.True(decoder.HasHat);
    Assert.True(state.GetButtons()[0]);
    Assert.True(state.GetButtons()[7]);
    Assert.True(state.GetButtons()[11]);
    Assert.False(state.GetButtons()[1]);

    input[7] = 0x08; // outside the declared 0..7 range: null/centred hat
    input[8] = 0;
    input[9] = 0;
    Assert.True(decoder.TryApplyReport(input, report, out state));
    Assert.Equal(-1, state.GetPointOfView()[0]);
    Assert.DoesNotContain(true, state.GetButtons());
  }

  [Fact]
  public void Hid_decoder_understands_simulation_control_usages() {
    var descriptor = new ReportDescriptor(FlightSimulationDescriptor());
    var deviceItem = Assert.Single(descriptor.DeviceItems);
    Assert.True(HidJoystickReportDecoder.IsControllerDeviceItem(deviceItem));
    var decoder = new HidJoystickReportDecoder(new[] { deviceItem });
    var report = Assert.Single(deviceItem.InputReports);
    var input = new byte[report.Length];

    WriteUInt16(input, 1, 0); // Aileron -> X
    WriteUInt16(input, 3, 512); // Elevator -> Y
    WriteUInt16(input, 5, 1023); // Rudder -> Rz
    WriteUInt16(input, 7, 256); // Throttle -> Slider1

    Assert.True(decoder.TryApplyReport(input, report, out var state));
    Assert.Equal(0, state.X);
    Assert.Equal(HidJoystickReportDecoder.NormalizeAxis(512, 0, 1023), state.Y);
    Assert.Equal(65535, state.Rz);
    Assert.Equal(HidJoystickReportDecoder.NormalizeAxis(256, 0, 1023),
        state.GetSlider()[0]);
  }

  [Fact]
  public void Hid_decoder_keeps_two_generic_slider_controls_distinct() {
    byte[] descriptorBytes = {
      0x05, 0x01, // Usage Page (Generic Desktop)
      0x09, 0x04, // Usage (Joystick)
      0xA1, 0x01, // Collection (Application)
      0x15, 0x00, 0x26, 0xFF, 0x03, // Logical range 0..1023
      0x75, 0x10, 0x95, 0x02,
      0x09, 0x36, 0x09, 0x36, // Two slider usages
      0x81, 0x02,
      0xC0,
    };
    var descriptor = new ReportDescriptor(descriptorBytes);
    var deviceItem = Assert.Single(descriptor.DeviceItems);
    var decoder = new HidJoystickReportDecoder(new[] { deviceItem });
    var report = Assert.Single(deviceItem.InputReports);
    var input = new byte[report.Length];

    WriteUInt16(input, 1, 128);
    WriteUInt16(input, 3, 896);

    Assert.True(decoder.TryApplyReport(input, report, out var state));
    Assert.Equal(HidJoystickReportDecoder.NormalizeAxis(128, 0, 1023), state.GetSlider()[0]);
    Assert.Equal(HidJoystickReportDecoder.NormalizeAxis(896, 0, 1023), state.GetSlider()[1]);
  }

  [Fact]
  public void Hid_decoder_combines_discrete_dpad_buttons_into_a_pov_angle() {
    byte[] descriptorBytes = {
      0x05, 0x01, // Usage Page (Generic Desktop)
      0x09, 0x05, // Usage (Game Pad)
      0xA1, 0x01, // Collection (Application)
      0x15, 0x00, 0x25, 0x01,
      0x75, 0x01, 0x95, 0x04,
      0x09, 0x90, 0x09, 0x91, 0x09, 0x92, 0x09, 0x93, // Up, right, down, left
      0x81, 0x02,
      0x75, 0x04, 0x95, 0x01, 0x81, 0x01,
      0xC0,
    };
    var descriptor = new ReportDescriptor(descriptorBytes);
    var deviceItem = Assert.Single(descriptor.DeviceItems);
    var decoder = new HidJoystickReportDecoder(new[] { deviceItem });
    var report = Assert.Single(deviceItem.InputReports);
    var input = new byte[report.Length];

    input[1] = 0x03; // up + right
    Assert.True(decoder.TryApplyReport(input, report, out var state));
    Assert.True(decoder.HasHat);
    Assert.Equal(4500, state.GetPointOfView()[0]);

    input[1] = 0;
    Assert.True(decoder.TryApplyReport(input, report, out state));
    Assert.Equal(-1, state.GetPointOfView()[0]);
  }

  [Fact]
  public void Hid_decoder_rejects_a_mouse_collection() {
    byte[] descriptorBytes = {
      0x05, 0x01, // Usage Page (Generic Desktop)
      0x09, 0x02, // Usage (Mouse)
      0xA1, 0x01, // Collection (Application)
      0x09, 0x01, // Usage (Pointer)
      0xA1, 0x00, // Collection (Physical)
      0x09, 0x30, 0x09, 0x31, // X, Y
      0x15, 0x81, 0x25, 0x7F,
      0x75, 0x08, 0x95, 0x02,
      0x81, 0x06, // Input (Data, Variable, Relative)
      0xC0, 0xC0,
    };
    var descriptor = new ReportDescriptor(descriptorBytes);

    Assert.False(HidJoystickReportDecoder.IsControllerDeviceItem(
        Assert.Single(descriptor.DeviceItems)));
  }

  [Fact]
  public void Mac_backend_loads_iokit_and_enumerates_on_the_native_runner() {
    if (!OperatingSystem.IsMacOS()) {
      return;
    }

    Assert.NotNull(MacHidJoystick.GetDevices());
  }

  [Fact]
  public async Task Linux_reader_processes_events_after_acquire_and_stops_cleanly() {
    if (!OperatingSystem.IsLinux()) {
      return;
    }

    var path = Path.Combine(Path.GetTempPath(), "mp-joydev-" + Guid.NewGuid().ToString("N"));
    Assert.Equal(0, MkFifo(path, 0x180)); // 0600
    try {
      var writer = Task.Run(() => {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite, 8, FileOptions.None);
        stream.Write(JoydevEvent(0, 0x82, 0)); // JS_EVENT_INIT | JS_EVENT_AXIS
        stream.Flush();
        Thread.Sleep(250);
        stream.Write(JoydevEvent(short.MaxValue, 0x02, 0));
        stream.Flush();
        Thread.Sleep(250);
      });

      using var joystick = new LinuxJoydevJoystick(() => null!);
      Assert.True(joystick.AcquireJoystick(path));
      Assert.Equal(path, joystick.name);

      var deadline = DateTime.UtcNow.AddSeconds(2);
      while (joystick.GetCurrentState().X != ushort.MaxValue && DateTime.UtcNow < deadline) {
        await Task.Delay(20);
      }

      Assert.Equal(ushort.MaxValue, joystick.GetCurrentState().X);
      joystick.UnAcquireJoyStick();
      Assert.False(joystick.IsJoystickValid());
      await writer.WaitAsync(TimeSpan.FromSeconds(2));
    } finally {
      File.Delete(path);
    }
  }

  private static byte[] JoydevEvent(short value, byte type, byte number) {
    var data = new byte[8];
    data[4] = (byte)value;
    data[5] = (byte)(value >> 8);
    data[6] = type;
    data[7] = number;
    return data;
  }

  private static byte[] GenericJoystickDescriptor() => new byte[] {
    0x05, 0x01, // Usage Page (Generic Desktop)
    0x09, 0x04, // Usage (Joystick)
    0xA1, 0x01, // Collection (Application)
    0x15, 0x81, 0x25, 0x7F, // Logical range -127..127
    0x75, 0x08, 0x95, 0x06, // Six 8-bit axes
    0x09, 0x30, 0x09, 0x31, 0x09, 0x32, // X, Y, Z
    0x09, 0x33, 0x09, 0x34, 0x09, 0x35, // Rx, Ry, Rz
    0x81, 0x02, // Input (Data, Variable, Absolute)
    0x15, 0x00, 0x25, 0x07, // Hat logical range 0..7
    0x35, 0x00, 0x46, 0x3B, 0x01, // Physical range 0..315 degrees
    0x65, 0x14, 0x75, 0x04, 0x95, 0x01,
    0x09, 0x39, 0x81, 0x42, // Hat with null state
    0x65, 0x00, 0x75, 0x04, 0x95, 0x01, 0x81, 0x01, // Nibble padding
    0x05, 0x09, 0x19, 0x01, 0x29, 0x0C, // Buttons 1..12
    0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0C, 0x81, 0x02,
    0x75, 0x04, 0x95, 0x01, 0x81, 0x01, // Button padding
    0xC0,
  };

  private static byte[] FlightSimulationDescriptor() => new byte[] {
    0x05, 0x02, // Usage Page (Simulation Controls)
    0x09, 0x01, // Usage (Flight Simulation Device)
    0xA1, 0x01, // Collection (Application)
    0x15, 0x00, 0x26, 0xFF, 0x03, // Logical range 0..1023
    0x75, 0x10, 0x95, 0x04, // Four 16-bit controls
    0x09, 0xB0, // Aileron
    0x09, 0xB8, // Elevator
    0x09, 0xBA, // Rudder
    0x09, 0xBB, // Throttle
    0x81, 0x02, // Input (Data, Variable, Absolute)
    0xC0,
  };

  private static void WriteUInt16(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)value;
    buffer[offset + 1] = (byte)(value >> 8);
  }

  [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
  private static extern int MkFifo(string path, uint mode);
}
