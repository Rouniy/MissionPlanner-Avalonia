using System;
using System.Collections.Generic;

namespace MissionPlanner;

/// <summary>
/// Non-visual MainV2 facade for legacy plugins that use the official static connection accessors.
/// </summary>
public class MainV2 {
  private static readonly MAVLinkInterface _fallbackPort = new();

  internal static Func<MAVLinkInterface>? ComPortProvider { get; set; }

  public static MainV2 instance = new();

  public static MAVLinkInterface comPort {
    get => ComPortProvider?.Invoke() ?? _fallbackPort;
    set => ComPortProvider = () => value;
  }

  public static List<MAVLinkInterface> Comports = [];

  public static string titlebar = "";

  public static string comPortName = "";

  public static int comPortBaud = 57600;

  public delegate void WMDeviceChangeEventHandler(WM_DEVICECHANGE_enum cause);

  public event WMDeviceChangeEventHandler? DeviceChanged;

  internal void ProcessDeviceChanged(WM_DEVICECHANGE_enum cause) {
    try {
      DeviceChanged?.Invoke(cause);
    } catch {
      // Match the official event boundary: one plugin callback must not escape dispatch.
    }
  }

  public enum WM_DEVICECHANGE_enum {
    DBT_CONFIGCHANGECANCELED = 0x19,
    DBT_CONFIGCHANGED = 0x18,
    DBT_CUSTOMEVENT = 0x8006,
    DBT_DEVICEARRIVAL = 0x8000,
    DBT_DEVICEQUERYREMOVE = 0x8001,
    DBT_DEVICEQUERYREMOVEFAILED = 0x8002,
    DBT_DEVICEREMOVECOMPLETE = 0x8004,
    DBT_DEVICEREMOVEPENDING = 0x8003,
    DBT_DEVICETYPESPECIFIC = 0x8005,
    DBT_DEVNODES_CHANGED = 0x7,
    DBT_QUERYCHANGECONFIG = 0x17,
    DBT_USERDEFINED = 0xFFFF,
  }
}
