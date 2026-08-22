using System;
using System.Collections.Generic;
using System.Globalization;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.Services;

internal static class AntennaTrackerOutputFactory {
  internal const string Maestro = "Maestro";
  internal const string ArduTracker = "ArduTracker";
  internal const string DegreeTracker = "DegreeTracker";

  internal static IReadOnlyList<string> InterfaceNames { get; } =
      Array.AsReadOnly([Maestro, ArduTracker, DegreeTracker]);

  internal static IAntennaTrackerOutput Create(string interfaceName, ICommsSerial serial) {
    ArgumentNullException.ThrowIfNull(serial);
    return interfaceName switch {
      Maestro => new MaestroAntennaTrackerOutput(serial),
      ArduTracker => new ArduAntennaTrackerOutput(serial),
      DegreeTracker => new DegreeAntennaTrackerOutput(serial),
      _ => throw new ArgumentOutOfRangeException(
          nameof(interfaceName), interfaceName, "Unknown antenna tracker interface."),
    };
  }
}

internal interface IAntennaTrackerOutput : IDisposable {
  double TrimPan { get; set; }
  double TrimTilt { get; set; }
  int PanStartRange { get; set; }
  int TiltStartRange { get; set; }
  int PanEndRange { get; set; }
  int TiltEndRange { get; set; }
  int PanPWMRange { get; set; }
  int TiltPWMRange { get; set; }
  int PanPWMCenter { get; set; }
  int TiltPWMCenter { get; set; }
  int PanSpeed { get; set; }
  int TiltSpeed { get; set; }
  int PanAccel { get; set; }
  int TiltAccel { get; set; }
  bool PanReverse { get; set; }
  bool TiltReverse { get; set; }

  bool Init(out string error);
  bool Setup();
  bool PanAndTilt(double pan, double tilt);
  void Close();
}

/// <summary>
/// Cross-platform implementations of Mission Planner's three serial antenna tracker outputs.
/// Access to the serial stream is serialized so disconnect cannot race an in-flight command.
/// </summary>
internal abstract class AntennaTrackerOutputBase : IAntennaTrackerOutput {
  private readonly object _serialLock = new();
  private bool _initialized;
  private bool _disposed;

  protected AntennaTrackerOutputBase(ICommsSerial serial) {
    Serial = serial;
  }

  protected ICommsSerial Serial { get; }

  public double TrimPan { get; set; }
  public double TrimTilt { get; set; }
  public int PanStartRange { get; set; }
  public int TiltStartRange { get; set; }
  public int PanEndRange { get; set; }
  public int TiltEndRange { get; set; }
  public int PanPWMRange { get; set; }
  public int TiltPWMRange { get; set; }
  public int PanPWMCenter { get; set; }
  public int TiltPWMCenter { get; set; }
  public int PanSpeed { get; set; }
  public int TiltSpeed { get; set; }
  public int PanAccel { get; set; }
  public int TiltAccel { get; set; }
  public abstract bool PanReverse { get; set; }
  public abstract bool TiltReverse { get; set; }

  public bool Init(out string error) {
    lock (_serialLock) {
      error = ValidateConfiguration();
      if (error.Length > 0) {
        return false;
      }

      try {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Serial.IsOpen) {
          Serial.Open();
        }
        _initialized = true;
        return true;
      } catch (Exception ex) {
        error = "Error connecting: " + ex.Message;
        return false;
      }
    }
  }

  public bool Setup() {
    lock (_serialLock) {
      return _initialized && SetupCore();
    }
  }

  public bool PanAndTilt(double pan, double tilt) {
    lock (_serialLock) {
      return _initialized && PanAndTiltCore(pan, tilt);
    }
  }

  public void Close() {
    lock (_serialLock) {
      _initialized = false;
      try {
        if (Serial.IsOpen) {
          Serial.Close();
        }
      } catch {
        // Closing a disconnected USB serial adapter is best effort, matching Mission Planner.
      }
    }
  }

  public void Dispose() {
    lock (_serialLock) {
      if (_disposed) {
        return;
      }
      _initialized = false;
      try {
        if (Serial.IsOpen) {
          Serial.Close();
        }
      } catch {
      }
      try {
        Serial.Dispose();
      } catch {
      } finally {
        _disposed = true;
      }
    }
  }

  protected virtual string ValidateConfiguration() => "";

  protected virtual bool SetupCore() => true;

  protected abstract bool PanAndTiltCore(double pan, double tilt);

  protected static double Wrap180(double input) {
    if (input > 180) {
      return input - 360;
    }
    if (input < -180) {
      return input + 360;
    }
    return input;
  }

  protected static short Constrain(double input, double min, double max) {
    if (input < min) {
      return (short)min;
    }
    if (input > max) {
      return (short)max;
    }
    return (short)input;
  }
}

internal sealed class MaestroAntennaTrackerOutput : AntennaTrackerOutputBase {
  private const byte SetTarget = 0x84;
  private const byte SetSpeed = 0x87;
  private const byte SetAccel = 0x89;
  private const byte PanAddress = 0;
  private const byte TiltAddress = 1;

  private int _panReverse = 1;
  private int _tiltReverse = 1;

  internal MaestroAntennaTrackerOutput(ICommsSerial serial) : base(serial) {
  }

  public override bool PanReverse {
    get => _panReverse == -1;
    set => _panReverse = value ? -1 : 1;
  }

  public override bool TiltReverse {
    get => _tiltReverse == -1;
    set => _tiltReverse = value ? -1 : 1;
  }

  protected override string ValidateConfiguration() {
    if (PanStartRange == PanEndRange) {
      return "Invalid pan range.";
    }
    return TiltStartRange == TiltEndRange ? "Invalid tilt range." : "";
  }

  protected override bool SetupCore() {
    SendCompactCommand(SetSpeed, PanAddress, PanSpeed);
    SendCompactCommand(SetSpeed, TiltAddress, TiltSpeed);
    SendCompactCommand(SetAccel, PanAddress, PanAccel);
    SendCompactCommand(SetAccel, TiltAddress, TiltAccel);
    return true;
  }

  protected override bool PanAndTiltCore(double pan, double tilt) {
    if (Math.Abs(TiltStartRange - TiltEndRange) > 120) {
      double target = Wrap180(pan - TrimPan);
      if (Math.Abs(target) > 90) {
        return Tilt(180 - tilt) && Pan(target);
      }
    }
    return Tilt(tilt) && Pan(pan);
  }

  private bool Pan(double angle) {
    double angleRange = Math.Abs(PanStartRange - PanEndRange);
    double pulseWidth =
        PanPWMRange / angleRange * Wrap180(angle - TrimPan) * _panReverse + PanPWMCenter;
    short target = Constrain(
        pulseWidth, PanPWMCenter - PanPWMRange / 2.0, PanPWMCenter + PanPWMRange / 2.0);
    target *= 4;
    SendCompactCommand(SetTarget, PanAddress, target);
    return true;
  }

  private bool Tilt(double angle) {
    double angleRange = Math.Abs(TiltStartRange - TiltEndRange);
    double pulseWidth =
        TiltPWMRange / angleRange * (angle - TrimTilt) * _tiltReverse + TiltPWMCenter;
    short target = Constrain(
        pulseWidth, TiltPWMCenter - TiltPWMRange / 2.0, TiltPWMCenter + TiltPWMRange / 2.0);
    target *= 4;
    SendCompactCommand(SetTarget, TiltAddress, target);
    return true;
  }

  private void SendCompactCommand(byte command, byte address, int data) {
    byte[] buffer = {
      command,
      address,
      (byte)(data & 0x7f),
      (byte)((data >> 7) & 0x7f),
    };
    Serial.DiscardInBuffer();
    Serial.Write(buffer, 0, buffer.Length);
  }
}

internal sealed class ArduAntennaTrackerOutput : AntennaTrackerOutputBase {
  private int _panReverse = 1;
  private int _tiltReverse = 1;
  private int _currentPan = 1500;
  private int _currentTilt = 1500;

  internal ArduAntennaTrackerOutput(ICommsSerial serial) : base(serial) {
  }

  public override bool PanReverse {
    get => _panReverse == -1;
    set => _panReverse = value ? -1 : 1;
  }

  public override bool TiltReverse {
    get => _tiltReverse == -1;
    set => _tiltReverse = value ? -1 : 1;
  }

  protected override string ValidateConfiguration() {
    if (PanStartRange == PanEndRange) {
      return "Invalid pan range.";
    }
    return TiltStartRange == TiltEndRange ? "Invalid tilt range." : "";
  }

  protected override bool PanAndTiltCore(double pan, double tilt) {
    Tilt(tilt);
    Pan(pan);
    Serial.Write(string.Format(CultureInfo.InvariantCulture,
        "!!!PAN:{0:0000},TLT:{1:0000}\n", _currentPan, _currentTilt));
    return true;
  }

  private void Pan(double angle) {
    double range = Math.Abs(PanStartRange - PanEndRange);
    short pointAt = Constrain(Wrap180(angle - TrimPan), PanStartRange, PanEndRange);
    _currentPan =
        (int)(pointAt / range * 2.0 * (PanPWMRange / 2) * _panReverse + PanPWMCenter);
  }

  private void Tilt(double angle) {
    double range = Math.Abs(TiltStartRange - TiltEndRange);
    short pointAt = Constrain(angle - TrimTilt, TiltStartRange, TiltEndRange);
    _currentTilt =
        (int)(pointAt / range * 2.0 * (TiltPWMRange / 2) * _tiltReverse + TiltPWMCenter);
  }
}

internal sealed class DegreeAntennaTrackerOutput : AntennaTrackerOutputBase {
  internal DegreeAntennaTrackerOutput(ICommsSerial serial) : base(serial) {
  }

  public override bool PanReverse { get; set; }

  public override bool TiltReverse { get; set; }

  protected override bool PanAndTiltCore(double pan, double tilt) {
    int currentPan = (int)(pan * 10);
    int currentTilt = (int)(tilt * 10);
    Serial.Write(string.Format(CultureInfo.InvariantCulture,
        "!!!PAN:{0:0000},TLT:{1:0000}\n", currentPan, currentTilt));
    return true;
  }
}
