using System;
using System.IO;
using System.Threading;
using MissionPlanner;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.LegacyTestPlugin;

public sealed class LegacyLifecyclePlugin : Plugin {
  private static readonly object FileGate = new object();
  private int _looped;

  public override string Name => "Legacy Binary Fixture";

  public override string Version => "1.3-era";

  public override string Author => "Mission Planner ABI test";

  public override bool Init() {
    if (Assembly == null || Host == null || string.IsNullOrEmpty(FileName)) {
      throw new InvalidOperationException("The official plugin fields were not populated.");
    }
    if (!ReferenceEquals(Host.comPort, MainV2.comPort) || !ReferenceEquals(Host.cs, Host.comPort.MAV.cs)) {
      throw new InvalidOperationException("The official connection facade is not live.");
    }
    if (!ReferenceEquals(Host.MainForm, MainV2.instance)
        || !ReferenceEquals(Host.config, Settings.Instance)) {
      throw new InvalidOperationException("The official host facade is not live.");
    }
    if (!MainV2.Comports.Contains(MainV2.comPort)) {
      throw new InvalidOperationException("The official connection list is not live.");
    }
    loopratehz = 50;
    Write("Init:" + FileName);
    return true;
  }

  public override bool Loaded() {
    Host.DeviceChanged += DeviceChanged;
    int row = Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, 1, 2, 3, 4,
        33.25, 44.5, 120, "legacy-tag");
    Host.InsertWP(0, MAVLink.MAV_CMD.DO_SET_SERVO, 9, 1500, 0, 0, 0, 0, 0);
    Host.GetWPs();
    Write("Loaded:row=" + row);
    return true;
  }

  public override bool Loop() {
    if (Interlocked.Exchange(ref _looped, 1) == 0) {
      Write("Loop");
      loopratehz = 0;
    }
    return true;
  }

  public override bool Exit() {
    Host.DeviceChanged -= DeviceChanged;
    Write("Exit");
    return true;
  }

  private static void DeviceChanged(MainV2.WM_DEVICECHANGE_enum cause) =>
      Write("DeviceChanged:" + cause);

  private static void Write(string line) {
    string path = Environment.GetEnvironmentVariable("MP_LEGACY_PLUGIN_FIXTURE")
        ?? throw new InvalidOperationException("Fixture output path was not configured.");
    lock (FileGate) {
      File.AppendAllText(path, line + Environment.NewLine);
    }
  }
}
