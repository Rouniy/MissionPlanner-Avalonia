using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.ArduPilot;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

/// <summary>
/// Console for ArduPilot's onboard Lua REPL. Unlike ConfigScriptReplViewModel,
/// commands execute on the flight controller and are exchanged through MAVFTP.
/// </summary>
public partial class ConfigOnboardReplViewModel : ViewModelBase, IDisposable, IDeactivationAware {
  private readonly StringBuilder _buffer = new();
  private AP_REPL? _repl;
  private bool _starting;
  private bool _startFailed;
  private bool _stopRequested;
  private bool _disposed;

  [ObservableProperty]
  private string _input = "";

  [ObservableProperty]
  private string _output = "";

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartCommand))]
  [NotifyCanExecuteChangedFor(nameof(StopCommand))]
  [NotifyCanExecuteChangedFor(nameof(SendCommand))]
  private bool _running;

  [ObservableProperty]
  private string _status = "Connect to firmware that exposes the ArduPilot onboard Lua REPL, then start it.";

  public ConfigOnboardReplViewModel() {
    AppendLine("ArduPilot onboard Lua REPL (MAV_CMD_SCRIPTING + MAVFTP)");
    AppendLine("This is separate from the local Mission Planner Lua/MoonSharp console.");
    AppendLine("");
    AppState.ConnectionChanged += OnConnectionChanged;
  }

  private bool CanStart => !_starting && !Running && AppState.IsConnected;

  [RelayCommand(CanExecute = nameof(CanStart))]
  private async Task Start() {
    if (!AppState.IsConnected) {
      Status = "Not connected.";
      return;
    }

    _starting = true;
    _startFailed = false;
    _stopRequested = false;
    StartCommand.NotifyCanExecuteChanged();
    Status = "Starting onboard REPL…";
    var repl = new AP_REPL(AppState.comPort);
    repl.NewResponse += OnResponse;
    _repl = repl;
    try {
      await Task.Run(repl.Start);
      if (_startFailed) {
        throw new InvalidOperationException("The flight controller rejected MAV_CMD_SCRIPTING.");
      }
      if (_stopRequested || _disposed || !AppState.IsConnected) {
        await Task.Run(repl.Stop);
        repl.NewResponse -= OnResponse;
        if (ReferenceEquals(_repl, repl)) {
          _repl = null;
        }
        Running = false;
        Status = "Onboard REPL stopped.";
        return;
      }
      Running = true;
      Status = "Onboard REPL active.";
    } catch (Exception ex) {
      try {
        await Task.Run(repl.Stop);
      } catch {
        // Preserve the original startup error.
      }
      repl.NewResponse -= OnResponse;
      _repl = null;
      Running = false;
      Status = "Unable to start onboard REPL: " + ex.Message;
      AppendLine(Status);
    } finally {
      _starting = false;
      StartCommand.NotifyCanExecuteChanged();
    }
  }

  [RelayCommand(CanExecute = nameof(Running))]
  private async Task Stop() {
    _stopRequested = true;
    var repl = _repl;
    _repl = null;
    Running = false;
    if (repl == null) {
      return;
    }

    try {
      await Task.Run(repl.Stop);
      Status = "Onboard REPL stopped.";
    } catch (Exception ex) {
      Status = "REPL stop failed: " + ex.Message;
    } finally {
      repl.NewResponse -= OnResponse;
    }
  }

  [RelayCommand(CanExecute = nameof(Running))]
  private async Task Send() {
    string command = (Input ?? "").TrimEnd('\r', '\n');
    if (command.Length == 0 || _repl == null) {
      return;
    }

    Input = "";
    AppendLine("> " + command);
    try {
      var bytes = Encoding.UTF8.GetBytes(command == "+++" ? command : command + "\n");
      await Task.Run(() => _repl?.Write(bytes, 0, bytes.Length));
    } catch (Exception ex) {
      Status = "REPL write failed: " + ex.Message;
      AppendLine(Status);
    }
  }

  [RelayCommand]
  private void Clear() {
    _buffer.Clear();
    Output = "";
  }

  private void OnResponse(object? sender, string text) {
    if (text.Contains("Failed to start REPL", StringComparison.OrdinalIgnoreCase)) {
      _startFailed = true;
    }
    if (Dispatcher.UIThread.CheckAccess()) {
      Append(text);
    } else {
      Dispatcher.UIThread.Post(() => Append(text));
    }
  }

  private void OnConnectionChanged() {
    StartCommand.NotifyCanExecuteChanged();
    if (!AppState.IsConnected && _starting) {
      _stopRequested = true;
    }
    if (!AppState.IsConnected && Running) {
      Dispatcher.UIThread.Post(() => _ = Stop());
    }
  }

  private void AppendLine(string text) => Append(text + Environment.NewLine);

  private void Append(string text) {
    _buffer.Append(text.Replace("\0", "", StringComparison.Ordinal));
    const int maximumLength = 128 * 1024;
    if (_buffer.Length > maximumLength) {
      _buffer.Remove(0, _buffer.Length - maximumLength);
    }
    Output = _buffer.ToString();
  }

  public void Deactivate() {
    _stopRequested = true;
    if (Running) {
      _ = Stop();
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _stopRequested = true;
    AppState.ConnectionChanged -= OnConnectionChanged;
    var repl = _repl;
    _repl = null;
    Running = false;
    if (repl == null) {
      return;
    }
    repl.NewResponse -= OnResponse;
    _ = Task.Run(() => {
      try {
        repl.Stop();
      } catch {
        // Application or connection teardown may already have closed MAVLink.
      }
    });
  }
}
