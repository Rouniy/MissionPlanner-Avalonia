using System;
using System.Reflection;

namespace MissionPlanner.Plugin;

/// <summary>
/// Cross-platform counterpart of Mission Planner's plugin base class.
/// </summary>
/// <remarks>
/// The lifecycle intentionally follows the official order: <see cref="Init"/>,
/// <see cref="Loaded"/>, repeated <see cref="Loop"/> calls, then <see cref="Exit"/>.
/// WinForms controls are replaced by the portable services on <see cref="Host"/>.
/// </remarks>
public abstract class Plugin {
  public Assembly? Assembly { get; internal set; }

  public PluginHost Host { get; internal set; } = null!;

  /// <summary>The plugin assembly file name.</summary>
  public string FileName { get; internal set; } = "";

  public abstract string Name { get; }

  public abstract string Version { get; }

  public abstract string Author { get; }

  /// <summary>
  /// The next UTC time at which the shared plugin scheduler may call <see cref="Loop"/>.
  /// A plugin may change this value from inside <see cref="Loop"/>.
  /// </summary>
  public virtual DateTime NextRun { get; set; }

  /// <summary>Initial validation and lightweight setup.</summary>
  public abstract bool Init();

  /// <summary>One-time setup after the application UI exists.</summary>
  public abstract bool Loaded();

  public virtual bool SetupUI(int gui = 0, object? data = null) => true;

  /// <summary>Periodic callback run outside the UI thread.</summary>
  public virtual bool Loop() => true;

  /// <summary>
  /// Official Mission Planner-compatible loop frequency property. Zero disables the loop.
  /// </summary>
  public virtual float loopratehz { get; set; }

  /// <summary>Pascal-case alias for new portable plugins.</summary>
  public float LoopRateHz {
    get => loopratehz;
    set => loopratehz = value;
  }

  /// <summary>Called during application shutdown after periodic callbacks have stopped.</summary>
  public abstract bool Exit();
}
