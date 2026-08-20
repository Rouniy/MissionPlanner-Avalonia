using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.Joystick;

namespace MissionPlannerAvalonia.Services;

internal static class JoystickProvider {
  public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

  public static JoystickBase Create(Func<MAVLinkInterface> currentInterface) {
    JoystickBase joystick = OperatingSystem.IsLinux()
        ? new LinuxJoydevJoystick(currentInterface)
        : JoystickBase.Create(currentInterface);

    // The upstream default opens a synchronous WinForms CustomMessageBox. The Avalonia layer
    // installs its own non-blocking lost-device callback for an active joystick.
    joystick.LostAction = () => { };
    return joystick;
  }

  public static IReadOnlyList<string> GetDevices() {
    if (OperatingSystem.IsLinux()) {
      return LinuxJoydevJoystick.GetDevices();
    }
    if (OperatingSystem.IsWindows()) {
      return JoystickBase.getDevices().ToList();
    }
    return Array.Empty<string>();
  }
}

internal sealed class LinuxJoydevJoystick : JoystickBase {
  private const int _eventSize = 8;
  private const byte _eventButton = 0x01;
  private const byte _eventAxis = 0x02;
  private const byte _eventInit = 0x80;
  private const ushort _midpoint = 32768;
  private const short _pollInput = 0x0001;
  private const short _pollFailure = 0x0008 | 0x0010 | 0x0020;

  private readonly object _lifecycleSync = new();
  private readonly object _stateSync = new();
  private readonly ushort[] _axes = Enumerable.Repeat(_midpoint, 128).ToArray();
  private readonly bool[] _buttons = new bool[128];

  private FileStream? _stream;
  private Thread? _readerThread;
  private JoydevState _snapshot = JoydevState.Empty;
  private int _buttonCount;
  private bool _stopping;

  public LinuxJoydevJoystick(Func<MAVLinkInterface> currentInterface) : base(currentInterface) {
    state = _snapshot;
  }

  public override bool AcquireJoystick(string name) {
    lock (_lifecycleSync) {
      if (_stream != null) {
        return true;
      }
    }

    FileStream stream;
    try {
      var path = ResolveDevicePath(name);
      stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
          _eventSize, FileOptions.None);
      ReadDeviceCounts(stream);
      log.Info("Opened joydev device " + path);
    } catch (Exception ex) {
      log.Error("Unable to open joydev device " + name, ex);
      return false;
    }

    using var firstEvent = new ManualResetEventSlim(false);
    var thread = new Thread(() => ReadLoop(stream, firstEvent)) {
      IsBackground = true,
      Name = "Linux joydev reader",
    };

    lock (_lifecycleSync) {
      _stopping = false;
      _stream = stream;
      _readerThread = thread;
    }
    thread.Start();

    // joydev emits JS_EVENT_INIT records immediately after open. Waiting for the first record
    // makes acquisition deterministic; a short quiet period lets the complete init burst land
    // before auto-detection snapshots the neutral state.
    if (!firstEvent.Wait(TimeSpan.FromSeconds(1))) {
      log.Error("The joydev reader did not receive initial state from " + name);
      UnAcquireJoyStick();
      return false;
    }
    Thread.Sleep(100);
    return IsJoystickValid();
  }

  public override void UnAcquireJoyStick() {
    FileStream? stream;
    Thread? thread;
    lock (_lifecycleSync) {
      _stopping = true;
      enabled = false;
      stream = _stream;
      thread = _readerThread;
    }

    if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) {
      thread.Join(TimeSpan.FromSeconds(1));
    }

    lock (_lifecycleSync) {
      if (ReferenceEquals(_stream, stream)) {
        _stream = null;
      }
      if (ReferenceEquals(_readerThread, thread)) {
        _readerThread = null;
      }
      _stopping = false;
    }

    try {
      stream?.Dispose();
    } catch (Exception ex) {
      log.Debug("Error closing joydev device", ex);
    }
  }

  public override bool IsJoystickValid() {
    lock (_lifecycleSync) {
      return _stream != null && _readerThread?.IsAlive == true;
    }
  }

  public override IMyJoystickState GetCurrentState() {
    var current = Volatile.Read(ref _snapshot);
    state = current;
    return current;
  }

  public override int getNumButtons() => Math.Clamp(Volatile.Read(ref _buttonCount), 0, 128);

  public override int getNumberPOV() => 0;

  public override void Dispose() {
    UnAcquireJoyStick();
    base.Dispose();
  }

  internal static ushort NormalizeAxis(short value) => (ushort)((int)value - short.MinValue);

  internal static IReadOnlyList<string> GetDevices() {
    var devices = new List<string>();
    var representedPaths = new HashSet<string>(StringComparer.Ordinal);

    try {
      const string byId = "/dev/input/by-id";
      if (Directory.Exists(byId)) {
        foreach (var path in Directory.GetFiles(byId, "*joystick")
                     .Where(path => !Path.GetFileName(path).Contains("-event-", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.Ordinal)) {
          devices.Add(Path.GetFileName(path));
          representedPaths.Add(CanonicalPath(path));
        }
      }

      const string input = "/dev/input";
      if (Directory.Exists(input)) {
        foreach (var path in Directory.GetFiles(input, "js*")
                     .OrderBy(path => path, StringComparer.Ordinal)) {
          if (representedPaths.Add(CanonicalPath(path))) {
            devices.Add(path);
          }
        }
      }
    } catch (Exception ex) {
      log.Error("Unable to enumerate joydev devices", ex);
    }

    return devices;
  }

  private void ReadLoop(FileStream stream, ManualResetEventSlim firstEvent) {
    var raw = new byte[_eventSize];
    var pollDescriptors = new[] {
      new PollDescriptor {
        FileDescriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
        Events = _pollInput,
      },
    };
    bool unexpectedDisconnect = false;
    bool firstEventSignalled = false;
    try {
      while (true) {
        int offset = 0;
        while (offset < raw.Length) {
          if (ShouldStop(stream)) {
            return;
          }

          pollDescriptors[0].ReturnedEvents = 0;
          int pollResult = Poll(pollDescriptors, 1, 100);
          if (pollResult == 0) {
            continue;
          }
          if (pollResult < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == 4) { // EINTR
              continue;
            }
            throw new IOException("poll() failed for joydev, errno " + error);
          }
          if ((pollDescriptors[0].ReturnedEvents & _pollFailure) != 0) {
            throw new EndOfStreamException("The joydev device was disconnected.");
          }
          if ((pollDescriptors[0].ReturnedEvents & _pollInput) == 0) {
            continue;
          }

          int read = stream.Read(raw, offset, raw.Length - offset);
          if (read == 0) {
            throw new EndOfStreamException("The joydev device was disconnected.");
          }
          offset += read;
        }

        ApplyEvent(raw);
        if (!firstEventSignalled) {
          firstEvent.Set();
          firstEventSignalled = true;
        }
      }
    } catch (ObjectDisposedException) {
      // Normal shutdown closes the blocking stream.
    } catch (IOException ex) {
      lock (_lifecycleSync) {
        unexpectedDisconnect = !_stopping && ReferenceEquals(_stream, stream);
      }
      if (unexpectedDisconnect) {
        log.Error("Joydev reader stopped", ex);
      }
    } catch (Exception ex) {
      lock (_lifecycleSync) {
        unexpectedDisconnect = !_stopping && ReferenceEquals(_stream, stream);
      }
      if (unexpectedDisconnect) {
        log.Error("Joydev reader failed", ex);
      }
    } finally {
      lock (_lifecycleSync) {
        if (ReferenceEquals(_stream, stream)) {
          unexpectedDisconnect = !_stopping;
          _stream = null;
          _readerThread = null;
        }
      }
      try {
        stream.Dispose();
      } catch {
      }

      if (unexpectedDisconnect) {
        enabled = false;
        try {
          LostAction();
        } catch (Exception ex) {
          log.Error("Joystick lost callback failed", ex);
        }
      }
    }
  }

  private bool ShouldStop(FileStream stream) {
    lock (_lifecycleSync) {
      return _stopping || !ReferenceEquals(_stream, stream);
    }
  }

  private void ApplyEvent(byte[] raw) {
    byte type = (byte)(raw[6] & ~_eventInit);
    byte number = raw[7];
    short value = (short)(raw[4] | (raw[5] << 8));

    lock (_stateSync) {
      if (type == _eventAxis) {
        _axes[number] = NormalizeAxis(value);
      } else if (type == _eventButton) {
        _buttons[number] = value != 0;
        int observedCount = number + 1;
        int knownCount = _buttonCount;
        if (observedCount > knownCount) {
          Volatile.Write(ref _buttonCount, observedCount);
        }
      } else {
        return;
      }

      Volatile.Write(ref _snapshot, new JoydevState(_axes, _buttons));
    }
  }

  private void ReadDeviceCounts(FileStream stream) {
    try {
      byte count = 0;
      if (Native.ioctl(stream.SafeFileHandle.DangerousGetHandle(),
              unchecked((int)IoReadByte('j', 0x12)), ref count) == 0) {
        Volatile.Write(ref _buttonCount, count);
      }
    } catch (Exception ex) {
      // Init button records provide a reliable fallback when ioctl is unavailable.
      log.Debug("Unable to query joydev button count", ex);
    }
  }

  private static uint IoReadByte(uint type, uint number) =>
      (2u << 30) | (1u << 16) | (type << 8) | number;

  [StructLayout(LayoutKind.Sequential)]
  private struct PollDescriptor {
    public int FileDescriptor;
    public short Events;
    public short ReturnedEvents;
  }

  [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
  private static extern int Poll([In, Out] PollDescriptor[] fileDescriptors,
      uint descriptorCount, int timeoutMilliseconds);

  private static string ResolveDevicePath(string name) {
    if (Path.IsPathRooted(name)) {
      return name;
    }

    var byId = Path.Combine("/dev/input/by-id", name);
    if (File.Exists(byId)) {
      return byId;
    }

    return Path.Combine("/dev/input", name);
  }

  private static string CanonicalPath(string path) {
    try {
      return Path.GetFullPath(new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path);
    } catch {
      return Path.GetFullPath(path);
    }
  }
}

internal sealed class JoydevState : IMyJoystickState {
  private const int _midpoint = 32768;
  private readonly ushort[] _axes;
  private readonly bool[] _buttons;

  public static JoydevState Empty { get; } = new(
      Enumerable.Repeat((ushort)_midpoint, 128).ToArray(), new bool[128]);

  public JoydevState(ushort[] axes, bool[] buttons) {
    _axes = (ushort[])axes.Clone();
    _buttons = (bool[])buttons.Clone();
  }

  public int[] GetSlider() => new[] { Axis(6), _midpoint };
  public int[] GetPointOfView() => new[] { _midpoint };
  public bool[] GetButtons() => (bool[])_buttons.Clone();

  public int X => Axis(0);
  public int Y => Axis(1);
  public int Z => Axis(2);
  public int Rx => Axis(3);
  public int Ry => Axis(4);
  public int Rz => Axis(5);
  public int AZ => Axis(7);
  public int AY => Axis(8);
  public int AX => Axis(9);
  public int ARx => _midpoint;
  public int ARy => _midpoint;
  public int ARz => _midpoint;
  public int FRx => _midpoint;
  public int FRy => _midpoint;
  public int FRz => _midpoint;
  public int FX => _midpoint;
  public int FY => _midpoint;
  public int FZ => _midpoint;
  public int VRx => _midpoint;
  public int VRy => _midpoint;
  public int VRz => _midpoint;
  public int VX => _midpoint;
  public int VY => _midpoint;
  public int VZ => _midpoint;

  private int Axis(int index) => index < _axes.Length ? _axes[index] : _midpoint;
}

internal static class JoystickDetector {
  private static readonly (joystickaxis Axis, Func<IMyJoystickState, int> Read)[] _axisReaders = {
    (joystickaxis.X, state => state.X),
    (joystickaxis.Y, state => state.Y),
    (joystickaxis.Z, state => state.Z),
    (joystickaxis.Rx, state => state.Rx),
    (joystickaxis.Ry, state => state.Ry),
    (joystickaxis.Rz, state => state.Rz),
    (joystickaxis.Slider1, state => ElementAtOrMidpoint(state.GetSlider(), 0)),
    (joystickaxis.Slider2, state => ElementAtOrMidpoint(state.GetSlider(), 1)),
    (joystickaxis.AX, state => state.AX),
    (joystickaxis.AY, state => state.AY),
    (joystickaxis.AZ, state => state.AZ),
    (joystickaxis.ARx, state => state.ARx),
    (joystickaxis.ARy, state => state.ARy),
    (joystickaxis.ARz, state => state.ARz),
    (joystickaxis.FRx, state => state.FRx),
    (joystickaxis.FRy, state => state.FRy),
    (joystickaxis.FRz, state => state.FRz),
    (joystickaxis.FX, state => state.FX),
    (joystickaxis.FY, state => state.FY),
    (joystickaxis.FZ, state => state.FZ),
    (joystickaxis.VRx, state => state.VRx),
    (joystickaxis.VRy, state => state.VRy),
    (joystickaxis.VRz, state => state.VRz),
    (joystickaxis.VX, state => state.VX),
    (joystickaxis.VY, state => state.VY),
    (joystickaxis.VZ, state => state.VZ),
  };

  public static async Task<joystickaxis> WaitForAxisAsync(JoystickBase joystick,
      IMyJoystickState baseline, int threshold, TimeSpan timeout, CancellationToken cancellationToken) {
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(50, cancellationToken).ConfigureAwait(false);
      var axis = FindMovedAxis(baseline, joystick.GetCurrentState(), threshold);
      if (axis != joystickaxis.None) {
        return axis;
      }
    }
    return joystickaxis.None;
  }

  public static async Task<int> WaitForButtonAsync(JoystickBase joystick, bool[] baseline,
      TimeSpan timeout, CancellationToken cancellationToken) {
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(25, cancellationToken).ConfigureAwait(false);
      int button = FindPressedButton(baseline, joystick.GetCurrentState().GetButtons());
      if (button >= 0) {
        return button;
      }
    }
    return -1;
  }

  internal static joystickaxis FindMovedAxis(IMyJoystickState baseline,
      IMyJoystickState current, int threshold) {
    joystickaxis bestAxis = joystickaxis.None;
    long bestDelta = threshold;
    foreach (var (axis, read) in _axisReaders) {
      try {
        long delta = Math.Abs((long)read(current) - read(baseline));
        if (delta > bestDelta) {
          bestDelta = delta;
          bestAxis = axis;
        }
      } catch {
        // Some DirectInput devices do not expose every optional axis.
      }
    }

    try {
      var oldPov = baseline.GetPointOfView();
      var newPov = current.GetPointOfView();
      if (oldPov.Length > 0 && newPov.Length > 0 && oldPov[0] != newPov[0]) {
        return joystickaxis.Hatud1;
      }
    } catch {
    }

    return bestAxis;
  }

  internal static int FindPressedButton(bool[] baseline, bool[] current) {
    int count = Math.Min(baseline.Length, current.Length);
    for (int i = 0; i < count; i++) {
      if (!baseline[i] && current[i]) {
        return i;
      }
    }
    return -1;
  }

  public static string Describe(IMyJoystickState state, int buttonCount) {
    var axes = $"X={state.X}  Y={state.Y}  Z={state.Z}  " +
               $"Rx={state.Rx}  Ry={state.Ry}  Rz={state.Rz}";
    var buttons = state.GetButtons();
    var pressed = Enumerable.Range(0, Math.Min(Math.Min(buttonCount, buttons.Length), 128))
        .Where(index => buttons[index])
        .Select(index => index.ToString())
        .ToArray();
    return "Raw input: " + axes + "; pressed buttons: " +
           (pressed.Length == 0 ? "none" : string.Join(", ", pressed));
  }

  private static int ElementAtOrMidpoint(int[] values, int index) =>
      index < values.Length ? values[index] : 32768;
}
