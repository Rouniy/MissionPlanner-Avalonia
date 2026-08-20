using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

  [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
  private static extern int MkFifo(string path, uint mode);
}
