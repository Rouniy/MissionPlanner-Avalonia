using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Services;

internal static class PluginService {
  private static readonly object _gate = new();
  private static readonly object _logGate = new();
  private static readonly List<HudRegistration> _hudPainters = [];
  private static PluginRuntime? _runtime;
  private static bool _restartRequired;

  public static string UserPluginDirectory => AppPaths.PluginRoot;

  public static string InstalledPluginDirectory => Path.Combine(AppPaths.InstallRoot, "plugins");

  public static bool RestartRequired {
    get {
      lock (_gate) {
        return _restartRequired;
      }
    }
  }

  public static event Action? Changed;

  public static void Initialize(MainWindowViewModel mainViewModel) {
    ArgumentNullException.ThrowIfNull(mainViewModel);
    lock (_gate) {
      if (_runtime != null) {
        return;
      }
      string[] disabled = ReadDisabledNames();
      _runtime = new PluginRuntime(
          [UserPluginDirectory, InstalledPluginDirectory],
          disabled,
          (path, type) => new AvaloniaPluginHost(mainViewModel, path, type),
          InvokeLoadedOnUiAsync,
          Report);
      _runtime.Changed += OnRuntimeChanged;
    }
  }

  public static Task RefreshAsync() {
    PluginRuntime? runtime;
    lock (_gate) {
      runtime = _runtime;
    }
    return runtime?.RefreshAsync() ?? Task.CompletedTask;
  }

  public static IReadOnlyList<PluginFileSnapshot> Snapshot() {
    lock (_gate) {
      return _runtime?.Snapshot() ?? [];
    }
  }

  public static IReadOnlySet<string> DisabledNames() =>
      ReadDisabledNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

  public static void SaveDisabled(IEnumerable<string> disabledPluginNames) {
    string[] disabled = disabledPluginNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => Path.GetFileName(name).ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    string[] previous = ReadDisabledNames()
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (previous.SequenceEqual(disabled, StringComparer.OrdinalIgnoreCase)) {
      return;
    }

    Settings.Instance.SetList("DisabledPlugins", disabled);
    lock (_gate) {
      _restartRequired = true;
      _runtime?.UpdateDisabled(disabled);
    }
    Changed?.Invoke();
  }

  public static void DrawHud(DrawingContext drawingContext, Avalonia.Rect bounds,
      double renderScaling) {
    HudRegistration[] painters;
    lock (_gate) {
      painters = _hudPainters.ToArray();
    }
    var context = new HudOverlayContext(drawingContext, bounds, renderScaling);
    foreach (HudRegistration painter in painters) {
      try {
        painter.Painter(context);
      } catch (Exception ex) {
        Report($"Plugin HUD callback failed: {ex.Message}");
      }
    }
  }

  public static async ValueTask ShutdownAsync() {
    PluginRuntime? runtime;
    lock (_gate) {
      runtime = _runtime;
      _runtime = null;
    }
    if (runtime != null) {
      runtime.Changed -= OnRuntimeChanged;
      await runtime.DisposeAsync().ConfigureAwait(false);
    }
    lock (_gate) {
      _hudPainters.Clear();
    }
  }

  internal static IDisposable RegisterHudOverlay(Action<HudOverlayContext> painter) {
    ArgumentNullException.ThrowIfNull(painter);
    var registration = new HudRegistration(painter);
    lock (_gate) {
      _hudPainters.Add(registration);
    }
    return new ActionRegistration(() => {
      lock (_gate) {
        _hudPainters.Remove(registration);
      }
    });
  }

  internal static void Report(string message) {
    string line = $"{DateTimeOffset.Now:O} {message}";
    Trace.WriteLine(line);
    try {
      lock (_logGate) {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.PluginLogPath)!);
        if (File.Exists(AppPaths.PluginLogPath)
            && new FileInfo(AppPaths.PluginLogPath).Length > 5 * 1024 * 1024) {
          File.Move(AppPaths.PluginLogPath, AppPaths.PluginLogPath + ".1", overwrite: true);
        }
        File.AppendAllText(AppPaths.PluginLogPath, line + Environment.NewLine);
      }
    } catch {
    }
  }

  private static string[] ReadDisabledNames() {
    try {
      return Settings.Instance.GetList("DisabledPlugins")
          .Where(name => !string.IsNullOrWhiteSpace(name))
          .Select(name => Path.GetFileName(name).ToLowerInvariant())
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
    } catch {
      return [];
    }
  }

  private static async Task<bool> InvokeLoadedOnUiAsync(Func<bool> callback) {
    if (Dispatcher.UIThread.CheckAccess()) {
      return callback();
    }
    return await Dispatcher.UIThread.InvokeAsync(callback);
  }

  private static void OnRuntimeChanged() => Changed?.Invoke();

  private sealed record HudRegistration(Action<HudOverlayContext> Painter);

  internal sealed class ActionRegistration(Action dispose) : IDisposable {
    private Action? _dispose = dispose;

    public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
  }

  private sealed class AvaloniaPluginHost : PluginHost, IDisposable {
    private readonly object _gate = new();
    private readonly MainWindowViewModel _mainViewModel;
    private readonly string _pluginLabel;
    private readonly List<IDisposable> _registrations = [];
    private Action? _connectionChanged;
    private bool _disposed;

    public AvaloniaPluginHost(MainWindowViewModel mainViewModel, string pluginPath, Type pluginType) {
      _mainViewModel = mainViewModel;
      _pluginLabel = pluginType.FullName ?? pluginType.Name;
      string assemblyName = SafePathSegment(Path.GetFileNameWithoutExtension(pluginPath));
      DataDirectory = Path.Combine(AppPaths.PluginDataRoot, assemblyName);
      Directory.CreateDirectory(DataDirectory);
      AppState.ConnectionChanged += OnConnectionChanged;
    }

    public override MissionPlanner.MAVLinkInterface comPort => AppState.comPort;

    public override string DataDirectory { get; }

    public override event Action? ConnectionChanged {
      add {
        lock (_gate) {
          ThrowIfDisposed();
          _connectionChanged += value;
        }
      }
      remove {
        lock (_gate) {
          _connectionChanged -= value;
        }
      }
    }

    public override IDisposable RegisterFlightAction(
        string action, Action<string> handler, string? after = null, string? before = null) {
      ArgumentException.ThrowIfNullOrWhiteSpace(action);
      ArgumentNullException.ThrowIfNull(handler);
      RunOnUi(() => _mainViewModel.FlightData.RegisterCustomAction(
          action,
          value => {
            try {
              handler(value);
            } catch (Exception ex) {
              Report($"Plugin {_pluginLabel} action {action} failed: {ex.Message}");
              throw;
            }
          },
          after,
          before));
      return Track(new ActionRegistration(() =>
          RunOnUiCleanup(() => _mainViewModel.FlightData.UnregisterCustomAction(action))));
    }

    public override IDisposable RegisterHudOverlay(Action<HudOverlayContext> painter) =>
        Track(PluginService.RegisterHudOverlay(painter));

    public override void PostToUi(Action action) {
      ArgumentNullException.ThrowIfNull(action);
      lock (_gate) {
        ThrowIfDisposed();
      }
      Dispatcher.UIThread.Post(() => {
        try {
          action();
        } catch (Exception ex) {
          Report($"Plugin {_pluginLabel} UI callback failed: {ex.Message}");
        }
      });
    }

    public override void Navigate(string screen) {
      string normalized = screen.Trim().ToUpperInvariant();
      if (normalized is not ("DATA" or "PLAN" or "SETUP" or "CONFIG" or "SIMULATION" or "HELP")) {
        throw new ArgumentOutOfRangeException(nameof(screen), "Unknown application screen.");
      }
      PostToUi(() => _mainViewModel.NavigateCommand.Execute(normalized));
    }

    public override void Log(string message) => Report($"Plugin {_pluginLabel}: {message}");

    public void Dispose() {
      IDisposable[] registrations;
      lock (_gate) {
        if (_disposed) {
          return;
        }
        _disposed = true;
        _connectionChanged = null;
        registrations = _registrations.AsEnumerable().Reverse().ToArray();
        _registrations.Clear();
      }
      AppState.ConnectionChanged -= OnConnectionChanged;
      foreach (IDisposable registration in registrations) {
        try {
          registration.Dispose();
        } catch (Exception ex) {
          Report($"Plugin {_pluginLabel} cleanup failed: {ex.Message}");
        }
      }
    }

    private T Track<T>(T registration) where T : IDisposable {
      lock (_gate) {
        ThrowIfDisposed();
        _registrations.Add(registration);
      }
      return registration;
    }

    private void OnConnectionChanged() {
      Action? handler;
      lock (_gate) {
        handler = _disposed ? null : _connectionChanged;
      }
      if (handler == null) {
        return;
      }
      try {
        handler();
      } catch (Exception ex) {
        Report($"Plugin {_pluginLabel} connection callback failed: {ex.Message}");
      }
    }

    private static void RunOnUi(Action action) {
      if (Dispatcher.UIThread.CheckAccess()) {
        action();
        return;
      }
      Dispatcher.UIThread.Invoke(action);
    }

    private static void RunOnUiCleanup(Action action) {
      if (Dispatcher.UIThread.CheckAccess()) {
        action();
      } else {
        Dispatcher.UIThread.Post(action);
      }
    }

    private static string SafePathSegment(string value) {
      char[] invalid = Path.GetInvalidFileNameChars();
      string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character)
          .ToArray());
      return string.IsNullOrWhiteSpace(safe) ? "plugin" : safe;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
