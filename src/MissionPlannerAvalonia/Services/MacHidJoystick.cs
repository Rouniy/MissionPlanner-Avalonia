using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;
using MissionPlanner;
using MissionPlanner.Joystick;

namespace MissionPlannerAvalonia.Services;

internal sealed class MacHidJoystick : JoystickBase {
  private readonly object _lifecycleSync = new();

  private HidStream? _stream;
  private HidDeviceInputReceiver? _receiver;
  private Thread? _readerThread;
  private HidJoystickReportDecoder? _decoder;
  private MacHidState _snapshot = MacHidState.Empty;
  private bool _stopping;

  public MacHidJoystick(Func<MAVLinkInterface> currentInterface) : base(currentInterface) {
    state = _snapshot;
  }

  public override bool AcquireJoystick(string name) {
    this.name = name;

    lock (_lifecycleSync) {
      if (_stream != null) {
        return true;
      }
    }

    MacHidDeviceInfo? info;
    HidStream? stream = null;
    try {
      info = EnumerateDevices().FirstOrDefault(device =>
          string.Equals(device.DisplayName, name, StringComparison.Ordinal));
      if (info == null) {
        log.Error("Unable to find macOS HID joystick " + name);
        return false;
      }

      var decoder = new HidJoystickReportDecoder(info.DeviceItems);
      if (!decoder.HasControls || !info.Device.TryOpen(out stream) || stream == null) {
        log.Error("Unable to open macOS HID joystick " + name);
        stream?.Dispose();
        return false;
      }

      var receiver = info.ReportDescriptor.CreateHidDeviceInputReceiver();
      receiver.Start(stream);
      if (!receiver.IsRunning) {
        stream.Dispose();
        log.Error("macOS HID joystick has no readable input reports: " + name);
        return false;
      }

      var thread = new Thread(() => ReadLoop(stream, receiver, decoder,
          info.ReportDescriptor.MaxInputReportLength)) {
        IsBackground = true,
        Name = "macOS IOKit HID joystick reader",
      };

      lock (_lifecycleSync) {
        _stopping = false;
        _stream = stream;
        _receiver = receiver;
        _decoder = decoder;
        _snapshot = decoder.Snapshot;
        state = _snapshot;
        _readerThread = thread;
      }
      thread.Start();
      log.Info("Opened macOS IOKit HID joystick " + name);
      return IsJoystickValid();
    } catch (Exception ex) {
      bool assigned;
      lock (_lifecycleSync) {
        assigned = stream != null && ReferenceEquals(_stream, stream);
      }
      if (assigned) {
        UnAcquireJoyStick();
      } else {
        try {
          stream?.Dispose();
        } catch {
        }
      }
      log.Error("Unable to acquire macOS HID joystick " + name, ex);
      return false;
    }
  }

  public override void UnAcquireJoyStick() {
    HidStream? stream;
    Thread? thread;
    lock (_lifecycleSync) {
      _stopping = true;
      enabled = false;
      stream = _stream;
      thread = _readerThread;
    }

    try {
      stream?.Dispose();
    } catch (Exception ex) {
      log.Debug("Error closing macOS HID joystick", ex);
    }

    if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) {
      thread.Join(TimeSpan.FromSeconds(1));
    }

    lock (_lifecycleSync) {
      if (ReferenceEquals(_stream, stream)) {
        _stream = null;
        _receiver = null;
        _decoder = null;
      }
      if (ReferenceEquals(_readerThread, thread)) {
        _readerThread = null;
      }
      _stopping = false;
    }
  }

  public override bool IsJoystickValid() {
    lock (_lifecycleSync) {
      return _stream != null && _receiver?.IsRunning == true &&
             _readerThread?.IsAlive == true;
    }
  }

  public override IMyJoystickState GetCurrentState() {
    var current = Volatile.Read(ref _snapshot);
    state = current;
    return current;
  }

  public override int getNumButtons() {
    lock (_lifecycleSync) {
      return _decoder?.ButtonCount ?? 0;
    }
  }

  public override int getNumberPOV() {
    lock (_lifecycleSync) {
      return _decoder?.HasHat == true ? 1 : 0;
    }
  }

  public override void Dispose() {
    UnAcquireJoyStick();
    base.Dispose();
  }

  internal static IReadOnlyList<string> GetDevices() {
    if (!OperatingSystem.IsMacOS()) {
      return Array.Empty<string>();
    }

    return EnumerateDevices()
        .Select(device => device.DisplayName)
        .OrderBy(displayName => displayName, StringComparer.Ordinal)
        .ToArray();
  }

  private void ReadLoop(HidStream stream, HidDeviceInputReceiver receiver,
      HidJoystickReportDecoder decoder, int maximumReportLength) {
    var buffer = new byte[maximumReportLength];
    bool unexpectedDisconnect = false;
    try {
      while (true) {
        if (ShouldStop(stream)) {
          return;
        }

        bool received = false;
        while (receiver.TryRead(buffer, 0, out Report? report)) {
          received = true;
          if (decoder.TryApplyReport(buffer, report, out var snapshot)) {
            Volatile.Write(ref _snapshot, snapshot);
          }
        }

        if (!receiver.IsRunning) {
          unexpectedDisconnect = !ShouldStop(stream);
          return;
        }
        if (!received) {
          receiver.WaitHandle.WaitOne(100);
        }
      }
    } catch (ObjectDisposedException) {
      unexpectedDisconnect = !ShouldStop(stream);
    } catch (IOException ex) {
      unexpectedDisconnect = !ShouldStop(stream);
      if (unexpectedDisconnect) {
        log.Error("macOS HID joystick reader stopped", ex);
      }
    } catch (Exception ex) {
      unexpectedDisconnect = !ShouldStop(stream);
      if (unexpectedDisconnect) {
        log.Error("macOS HID joystick reader failed", ex);
      }
    } finally {
      lock (_lifecycleSync) {
        if (ReferenceEquals(_stream, stream)) {
          unexpectedDisconnect = !_stopping;
          _stream = null;
          _receiver = null;
          _decoder = null;
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
          log.Error("macOS HID joystick lost callback failed", ex);
        }
      }
    }
  }

  private bool ShouldStop(HidStream stream) {
    lock (_lifecycleSync) {
      return _stopping || !ReferenceEquals(_stream, stream);
    }
  }

  private static IReadOnlyList<MacHidDeviceInfo> EnumerateDevices() {
    var devices = new List<MacHidDeviceInfo>();
    foreach (var device in DeviceList.Local.GetHidDevices()) {
      try {
        var descriptor = device.GetReportDescriptor();
        var deviceItems = descriptor.DeviceItems
            .Where(HidJoystickReportDecoder.IsControllerDeviceItem)
            .ToArray();
        if (deviceItems.Length == 0) {
          continue;
        }

        var decoder = new HidJoystickReportDecoder(deviceItems);
        if (!decoder.HasControls) {
          continue;
        }

        devices.Add(new MacHidDeviceInfo(DeviceDisplayName(device), device, descriptor,
            deviceItems));
      } catch (Exception ex) {
        log.Debug("Ignoring a non-readable macOS HID device", ex);
      }
    }
    return devices;
  }

  private static string DeviceDisplayName(HidDevice device) {
    string product;
    try {
      product = device.GetProductName()?.Trim() ?? "";
    } catch {
      product = "";
    }
    product = new string(product.Where(character => !char.IsControl(character)).ToArray()).Trim();
    if (string.IsNullOrWhiteSpace(product)) {
      product = "HID joystick";
    }

    string stableIdentity;
    try {
      stableIdentity = device.GetSerialNumber()?.Trim() ?? "";
    } catch {
      stableIdentity = "";
    }
    if (string.IsNullOrWhiteSpace(stableIdentity)) {
      stableIdentity = device.DevicePath;
    }

    var identity = $"{device.VendorID:X4}:{device.ProductID:X4}:{stableIdentity}";
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
        .Substring(0, 8).ToLowerInvariant();
    return $"{product} [{device.VendorID:X4}:{device.ProductID:X4}-{hash}]";
  }

  private sealed record MacHidDeviceInfo(string DisplayName, HidDevice Device,
      ReportDescriptor ReportDescriptor, IReadOnlyList<DeviceItem> DeviceItems);
}

internal sealed class HidJoystickReportDecoder {
  private const int _genericDesktopPage = 0x01;
  private const int _simulationControlsPage = 0x02;
  private const int _buttonPage = 0x09;
  private const int _midpoint = ushort.MaxValue / 2 + 1;

  private static readonly joystickaxis[] _fallbackAxes = {
    joystickaxis.AX, joystickaxis.AY, joystickaxis.AZ,
    joystickaxis.ARx, joystickaxis.ARy, joystickaxis.ARz,
    joystickaxis.FX, joystickaxis.FY, joystickaxis.FZ,
    joystickaxis.FRx, joystickaxis.FRy, joystickaxis.FRz,
    joystickaxis.VX, joystickaxis.VY, joystickaxis.VZ,
    joystickaxis.VRx, joystickaxis.VRy, joystickaxis.VRz,
  };

  private readonly List<ParserBindings> _parsers = new();
  private readonly int[] _axes = Enumerable.Repeat(_midpoint,
      (int)joystickaxis.UINT16_MAX + 1).ToArray();
  private readonly bool[] _buttons = new bool[128];
  private readonly bool[] _dpad = new bool[4];
  private readonly bool _hasExplicitHat;
  private int _explicitPov = -1;

  public HidJoystickReportDecoder(IEnumerable<DeviceItem> deviceItems) {
    var usedAxes = new HashSet<joystickaxis>();
    var usedButtons = new HashSet<int>();
    bool hasExplicitHat = false;
    bool hasDpad = false;

    foreach (var item in deviceItems) {
      var parser = item.CreateDeviceItemInputParser();
      var bindings = new List<HidControlBinding>();
      for (int valueIndex = 0; valueIndex < parser.ValueCount; valueIndex++) {
        var value = parser.GetValue(valueIndex);
        if (value.DataItem.IsConstant) {
          continue;
        }

        foreach (uint usage in value.Usages) {
          int page = (int)(usage >> 16);
          int id = (int)(usage & ushort.MaxValue);

          if (page == _genericDesktopPage && id == 0x39 && !hasExplicitHat) {
            bindings.Add(HidControlBinding.Hat(valueIndex));
            hasExplicitHat = true;
            break;
          }

          int dpadIndex = DpadIndex(page, id);
          if (dpadIndex >= 0) {
            bindings.Add(HidControlBinding.Dpad(valueIndex, dpadIndex));
            hasDpad = true;
            break;
          }

          if (TryGetButtonIndex(value, page, id, usedButtons, out int buttonIndex)) {
            bindings.Add(HidControlBinding.Button(valueIndex, buttonIndex));
            ButtonCount = Math.Max(ButtonCount, buttonIndex + 1);
            break;
          }

          var preferredAxis = PreferredAxis(page, id);
          if (preferredAxis != joystickaxis.None && value.DataItem.IsAbsolute &&
              !value.DataItem.IsBoolean) {
            var axis = AssignAxis(preferredAxis, usedAxes);
            if (axis != joystickaxis.None) {
              bindings.Add(HidControlBinding.ForAxis(valueIndex, axis));
            }
            break;
          }
        }
      }

      if (bindings.Count > 0) {
        _parsers.Add(new ParserBindings(parser, bindings));
      }
    }

    _hasExplicitHat = hasExplicitHat;
    HasHat = hasExplicitHat || hasDpad;
    Snapshot = new MacHidState(_axes, _buttons, -1);
  }

  public bool HasControls => _parsers.Count > 0;
  public bool HasHat { get; }
  public int ButtonCount { get; private set; }
  public MacHidState Snapshot { get; private set; }

  public bool TryApplyReport(byte[] buffer, Report report, out MacHidState snapshot) {
    bool parsed = false;
    foreach (var context in _parsers) {
      if (!context.Parser.TryParseReport(buffer, 0, report)) {
        continue;
      }

      parsed = true;
      foreach (var binding in context.Bindings) {
        var value = context.Parser.GetValue(binding.ValueIndex);
        switch (binding.Kind) {
          case HidControlKind.Axis:
            _axes[(int)binding.Axis] = NormalizeAxis(value);
            break;
          case HidControlKind.Button:
            _buttons[binding.TargetIndex] = !value.IsNull && value.GetLogicalValue() != 0;
            break;
          case HidControlKind.Hat:
            _explicitPov = NormalizeHat(value);
            break;
          case HidControlKind.Dpad:
            _dpad[binding.TargetIndex] = !value.IsNull && value.GetLogicalValue() != 0;
            break;
        }
      }
    }

    if (parsed) {
      int pov = _hasExplicitHat ? _explicitPov : DpadPov(_dpad);
      Snapshot = new MacHidState(_axes, _buttons, pov);
    }
    snapshot = Snapshot;
    return parsed;
  }

  internal static bool IsControllerDeviceItem(DeviceItem item) =>
      item.InputReports.Any() && item.Usages.GetAllValues().Any(IsControllerUsage);

  internal static int NormalizeAxis(int logicalValue, int logicalMinimum,
      int logicalMaximum) {
    long range = (long)logicalMaximum - logicalMinimum;
    if (range <= 0) {
      return _midpoint;
    }

    long clamped = Math.Clamp((long)logicalValue, logicalMinimum, logicalMaximum);
    long numerator = (clamped - logicalMinimum) * ushort.MaxValue;
    return (int)((numerator + range / 2) / range);
  }

  private static int NormalizeAxis(DataValue value) => value.IsNull
      ? _midpoint
      : NormalizeAxis(value.GetLogicalValue(), value.DataItem.LogicalMinimum,
          value.DataItem.LogicalMaximum);

  private static int NormalizeHat(DataValue value) {
    if (value.IsNull) {
      return -1;
    }

    int minimum = value.DataItem.LogicalMinimum;
    int maximum = value.DataItem.LogicalMaximum;
    int logical = value.GetLogicalValue();
    int positions = maximum - minimum + 1;
    if (positions is < 4 or > 16 || logical < minimum || logical > maximum) {
      return -1;
    }
    return (int)Math.Round((logical - minimum) * 36000d / positions) % 36000;
  }

  private static int DpadPov(bool[] dpad) {
    int horizontal = (dpad[1] ? 1 : 0) - (dpad[3] ? 1 : 0);
    int vertical = (dpad[2] ? 1 : 0) - (dpad[0] ? 1 : 0);
    return (horizontal, vertical) switch {
      (0, -1) => 0,
      (1, -1) => 4500,
      (1, 0) => 9000,
      (1, 1) => 13500,
      (0, 1) => 18000,
      (-1, 1) => 22500,
      (-1, 0) => 27000,
      (-1, -1) => 31500,
      _ => -1,
    };
  }

  private static bool IsControllerUsage(uint usage) {
    int page = (int)(usage >> 16);
    int id = (int)(usage & ushort.MaxValue);
    if (page == _genericDesktopPage) {
      return id is 0x04 or 0x05 or 0x08;
    }
    return page == _simulationControlsPage &&
           id is 0x01 or 0x04 or 0x09 or 0x0A or 0x20 or 0x21 or 0x24;
  }

  private static int DpadIndex(int page, int id) => page == _genericDesktopPage
      ? id switch {
        0x90 => 0,
        0x91 => 1,
        0x92 => 2,
        0x93 => 3,
        _ => -1,
      }
      : -1;

  private static bool TryGetButtonIndex(DataValue value, int page, int id,
      HashSet<int> usedButtons, out int buttonIndex) {
    int preferred = -1;
    if (page == _buttonPage && id is >= 1 and <= 128) {
      preferred = id - 1;
    } else if (value.DataItem.IsBoolean &&
               (page == _simulationControlsPage ||
                page == _genericDesktopPage && id is 0x3D or 0x3E)) {
      preferred = Enumerable.Range(0, 128).FirstOrDefault(index => !usedButtons.Contains(index));
    }

    if (preferred < 0 || preferred >= 128 || !usedButtons.Add(preferred)) {
      buttonIndex = -1;
      return false;
    }
    buttonIndex = preferred;
    return true;
  }

  private static joystickaxis PreferredAxis(int page, int id) {
    if (page == _genericDesktopPage) {
      return id switch {
        0x30 => joystickaxis.X,
        0x31 => joystickaxis.Y,
        0x32 => joystickaxis.Z,
        0x33 => joystickaxis.Rx,
        0x34 => joystickaxis.Ry,
        0x35 => joystickaxis.Rz,
        0x36 => joystickaxis.Slider1,
        0x37 => joystickaxis.AX,
        0x38 => joystickaxis.AY,
        _ => joystickaxis.None,
      };
    }
    if (page != _simulationControlsPage) {
      return joystickaxis.None;
    }
    return id switch {
      0xB0 => joystickaxis.X,
      0xB1 => joystickaxis.AX,
      0xB2 => joystickaxis.Rz,
      0xB5 => joystickaxis.Z,
      0xB6 => joystickaxis.Slider2,
      0xB8 => joystickaxis.Y,
      0xB9 => joystickaxis.AY,
      0xBA => joystickaxis.Rz,
      0xBB => joystickaxis.Slider1,
      0xBF => joystickaxis.Slider2,
      _ => joystickaxis.None,
    };
  }

  private static joystickaxis AssignAxis(joystickaxis preferred,
      HashSet<joystickaxis> usedAxes) {
    if (usedAxes.Add(preferred)) {
      return preferred;
    }
    if (preferred == joystickaxis.Slider1 && usedAxes.Add(joystickaxis.Slider2)) {
      return joystickaxis.Slider2;
    }
    foreach (var fallback in _fallbackAxes) {
      if (usedAxes.Add(fallback)) {
        return fallback;
      }
    }
    return joystickaxis.None;
  }

  private sealed record ParserBindings(DeviceItemInputParser Parser,
      IReadOnlyList<HidControlBinding> Bindings);

  private enum HidControlKind {
    Axis,
    Button,
    Hat,
    Dpad,
  }

  private readonly record struct HidControlBinding(int ValueIndex, HidControlKind Kind,
      joystickaxis Axis, int TargetIndex) {
    public static HidControlBinding ForAxis(int valueIndex, joystickaxis axis) =>
        new(valueIndex, HidControlKind.Axis, axis, -1);

    public static HidControlBinding Button(int valueIndex, int buttonIndex) =>
        new(valueIndex, HidControlKind.Button, joystickaxis.None, buttonIndex);

    public static HidControlBinding Hat(int valueIndex) =>
        new(valueIndex, HidControlKind.Hat, joystickaxis.None, -1);

    public static HidControlBinding Dpad(int valueIndex, int dpadIndex) =>
        new(valueIndex, HidControlKind.Dpad, joystickaxis.None, dpadIndex);
  }
}

internal sealed class MacHidState : IMyJoystickState {
  private const int _midpoint = ushort.MaxValue / 2 + 1;
  private readonly int[] _axes;
  private readonly bool[] _buttons;
  private readonly int _pov;

  public static MacHidState Empty { get; } = new(
      Enumerable.Repeat(_midpoint, (int)joystickaxis.UINT16_MAX + 1).ToArray(),
      new bool[128], -1);

  public MacHidState(int[] axes, bool[] buttons, int pov) {
    _axes = (int[])axes.Clone();
    _buttons = (bool[])buttons.Clone();
    _pov = pov;
  }

  public int[] GetSlider() => new[] { Axis(joystickaxis.Slider1), Axis(joystickaxis.Slider2) };
  public int[] GetPointOfView() => new[] { _pov };
  public bool[] GetButtons() => (bool[])_buttons.Clone();

  public int AZ => Axis(joystickaxis.AZ);
  public int AY => Axis(joystickaxis.AY);
  public int AX => Axis(joystickaxis.AX);
  public int ARz => Axis(joystickaxis.ARz);
  public int ARy => Axis(joystickaxis.ARy);
  public int ARx => Axis(joystickaxis.ARx);
  public int FRx => Axis(joystickaxis.FRx);
  public int FRy => Axis(joystickaxis.FRy);
  public int FRz => Axis(joystickaxis.FRz);
  public int FX => Axis(joystickaxis.FX);
  public int FY => Axis(joystickaxis.FY);
  public int FZ => Axis(joystickaxis.FZ);
  public int Rx => Axis(joystickaxis.Rx);
  public int Ry => Axis(joystickaxis.Ry);
  public int Rz => Axis(joystickaxis.Rz);
  public int VRx => Axis(joystickaxis.VRx);
  public int VRy => Axis(joystickaxis.VRy);
  public int VRz => Axis(joystickaxis.VRz);
  public int VX => Axis(joystickaxis.VX);
  public int VY => Axis(joystickaxis.VY);
  public int VZ => Axis(joystickaxis.VZ);
  public int X => Axis(joystickaxis.X);
  public int Y => Axis(joystickaxis.Y);
  public int Z => Axis(joystickaxis.Z);

  private int Axis(joystickaxis axis) => (int)axis < _axes.Length ? _axes[(int)axis] : _midpoint;
}
