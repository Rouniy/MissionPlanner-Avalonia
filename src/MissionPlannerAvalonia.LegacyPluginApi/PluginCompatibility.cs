extern alias PortablePluginContract;
using System;
using System.Collections.Generic;
using System.Reflection;
using MissionPlanner.Utilities;
using PortableHudOverlayContext = PortablePluginContract::MissionPlanner.Plugin.HudOverlayContext;
using PortablePlugin = PortablePluginContract::MissionPlanner.Plugin.Plugin;
using PortablePluginHost = PortablePluginContract::MissionPlanner.Plugin.PluginHost;

namespace MissionPlanner.Plugin;

/// <summary>
/// Binary-compatible base for official Mission Planner plugins that do not depend on WinForms UI
/// types. The extra portable base lets the Avalonia runtime execute the original lifecycle without
/// reflection-only proxies.
/// </summary>
public abstract class Plugin : PortablePlugin {
  // These members are fields/properties rather than aliases because their exact metadata shape is
  // part of the official MissionPlanner.exe plugin ABI.
  public new Assembly? Assembly;

  public new PluginHost Host { get; internal set; } = null!;

  public new string FileName { get; set; } = "";

  // Redeclare the lifecycle so the shim exposes the same MethodDefs as MissionPlanner.exe.
  // Existing plugin binaries bind to these members, while the portable runtime still sees the
  // inherited contract and dispatches to the plugin overrides through the same virtual slots.
  public abstract override string Name { get; }

  public abstract override string Version { get; }

  public abstract override string Author { get; }

  public override DateTime NextRun { get; set; }

  public abstract override bool Init();

  public abstract override bool Loaded();

  public override bool SetupUI(int gui = 0, object? data = null) => true;

  public override bool Loop() => true;

  public override float loopratehz { get; set; }

  public abstract override bool Exit();
}

/// <summary>
/// Portable subset of the official PluginHost ABI. WinForms menu/control properties cannot be
/// represented cross-platform; vehicle, settings, device-change and mission-list members can.
/// </summary>
public class PluginHost : PortablePluginHost {
  private Action? _connectionChanged;

  public event MainV2.WMDeviceChangeEventHandler? DeviceChanged;

  internal void ProcessDeviceChanged(MainV2.WM_DEVICECHANGE_enum cause) {
    try {
      DeviceChanged?.Invoke(cause);
    } catch {
      // Match the official host: one plugin callback must not escape device-change dispatch.
    }
  }

  public virtual MainV2 MainForm => MainV2.instance;

  public override CurrentState cs => comPort.MAV.cs;

  public override MAVLinkInterface comPort => MainV2.comPort;

  public override IReadOnlyList<MAVLinkInterface> comPorts => MainV2.Comports;

  public override Settings config => Settings.Instance;

  public override string DataDirectory => AppContext.BaseDirectory;

  public override event Action? ConnectionChanged {
    add => _connectionChanged += value;
    remove => _connectionChanged -= value;
  }

  internal void ProcessConnectionChanged() => _connectionChanged?.Invoke();

  public override IDisposable RegisterFlightAction(
      string action, Action<string> handler, string? after = null, string? before = null) =>
      throw new NotSupportedException("This host is not attached to the Avalonia application.");

  public override IDisposable RegisterHudOverlay(Action<PortableHudOverlayContext> painter) =>
      throw new NotSupportedException("This host is not attached to the Avalonia application.");

  public override void PostToUi(Action action) => action();

  public override void Navigate(string screen) {
  }

  public override void Log(string message) {
  }

  public override int AddWPtoList(
      MAVLink.MAV_CMD cmd,
      double p1,
      double p2,
      double p3,
      double p4,
      double x,
      double y,
      double z,
      object? tag = null) =>
      throw new NotSupportedException("This host is not attached to the flight planner.");

  public int AddWPtoList(
      MAVLink.MAV_CMD cmd,
      double p1,
      double p2,
      double p3,
      double p4,
      double x,
      double y,
      double z) => AddWPtoList(cmd, p1, p2, p3, p4, x, y, z, null);

  public override void InsertWP(
      int idx,
      MAVLink.MAV_CMD cmd,
      double p1,
      double p2,
      double p3,
      double p4,
      double x,
      double y,
      double z,
      object? tag = null) =>
      throw new NotSupportedException("This host is not attached to the flight planner.");

  public void InsertWP(
      int idx,
      MAVLink.MAV_CMD cmd,
      double p1,
      double p2,
      double p3,
      double p4,
      double x,
      double y,
      double z) => InsertWP(idx, cmd, p1, p2, p3, p4, x, y, z, null);

  public override void GetWPs() =>
      throw new NotSupportedException("This host is not attached to the flight planner.");
}
