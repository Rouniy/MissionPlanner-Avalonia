using System;
using Avalonia.Media;
using MissionPlanner.Utilities;

namespace MissionPlanner.Plugin;

/// <summary>Portable services exposed to a MissionPlanner-Avalonia plugin.</summary>
public abstract class PluginHost {
  /// <summary>The currently selected MAVLink interface.</summary>
  public abstract MAVLinkInterface comPort { get; }

  /// <summary>The current state for the selected MAVLink system/component.</summary>
  public CurrentState cs => comPort.MAV.cs;

  /// <summary>Mission Planner-compatible settings storage.</summary>
  public virtual Settings config => Settings.Instance;

  public bool IsConnected => comPort.BaseStream?.IsOpen == true;

  /// <summary>A writable, plugin-specific directory.</summary>
  public abstract string DataDirectory { get; }

  /// <summary>Raised when the active connection or selected vehicle changes.</summary>
  public abstract event Action? ConnectionChanged;

  /// <summary>
  /// Adds an action to the Flight Data action selector. Disposing the returned registration removes
  /// the action; the host also removes it automatically when the plugin exits.
  /// </summary>
  public abstract IDisposable RegisterFlightAction(
      string action, Action<string> handler, string? after = null, string? before = null);

  /// <summary>
  /// Adds a drawing callback after the built-in HUD has rendered. The callback always runs on the UI
  /// thread and is automatically removed when the plugin exits.
  /// </summary>
  public abstract IDisposable RegisterHudOverlay(Action<HudOverlayContext> painter);

  /// <summary>Schedules work on Avalonia's UI thread.</summary>
  public abstract void PostToUi(Action action);

  /// <summary>Navigates the main shell to DATA, PLAN, SETUP, CONFIG, SIMULATION or HELP.</summary>
  public abstract void Navigate(string screen);

  /// <summary>Writes a plugin-scoped diagnostic message to the application state log.</summary>
  public abstract void Log(string message);
}

public readonly record struct HudOverlayContext(
    DrawingContext DrawingContext,
    Avalonia.Rect Bounds,
    double RenderScaling);
