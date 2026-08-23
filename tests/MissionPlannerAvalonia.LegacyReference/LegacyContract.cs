using System;
using System.Collections.Generic;
using System.Reflection;
using MissionPlanner.Utilities;

namespace MissionPlanner.Plugin {
  // Test-only reference contract: this deliberately has the old metadata shapes and does not
  // reference MissionPlannerAvalonia.PluginApi.
  public abstract class Plugin {
    public Assembly Assembly = null;

    public PluginHost Host { get; internal set; }

    public string FileName { get; set; }

    public abstract string Name { get; }

    public abstract string Version { get; }

    public abstract string Author { get; }

    public virtual DateTime NextRun { get; set; }

    public abstract bool Init();

    public abstract bool Loaded();

    public virtual bool SetupUI(int gui = 0, object data = null) => true;

    public virtual bool Loop() => true;

    public virtual float loopratehz { get; set; }

    public abstract bool Exit();
  }

  public class PluginHost {
    public event MainV2.WMDeviceChangeEventHandler DeviceChanged;

    public MainV2 MainForm => MainV2.instance;

    public CurrentState cs => MainV2.comPort.MAV.cs;

    public MAVLinkInterface comPort => MainV2.comPort;

    public Settings config => Settings.Instance;

    public int AddWPtoList(MAVLink.MAV_CMD cmd, double p1, double p2, double p3, double p4,
        double x, double y, double z, object tag = null) => 0;

    public int AddWPtoList(MAVLink.MAV_CMD cmd, double p1, double p2, double p3, double p4,
        double x, double y, double z) => 0;

    public void InsertWP(int idx, MAVLink.MAV_CMD cmd, double p1, double p2, double p3, double p4,
        double x, double y, double z, object tag = null) {
    }

    public void InsertWP(int idx, MAVLink.MAV_CMD cmd, double p1, double p2, double p3, double p4,
        double x, double y, double z) {
    }

    public void GetWPs() {
    }
  }
}

namespace MissionPlanner {
  public class MainV2 {
    private static MAVLinkInterface _comPort = new MAVLinkInterface();

    public static MainV2 instance = new MainV2();

    public static MAVLinkInterface comPort {
      get => _comPort;
      set => _comPort = value;
    }

    public static List<MAVLinkInterface> Comports = new List<MAVLinkInterface>();

    public delegate void WMDeviceChangeEventHandler(WM_DEVICECHANGE_enum cause);

    public enum WM_DEVICECHANGE_enum {
      DBT_DEVICEARRIVAL = 0x8000,
      DBT_DEVNODES_CHANGED = 0x7,
    }
  }
}
