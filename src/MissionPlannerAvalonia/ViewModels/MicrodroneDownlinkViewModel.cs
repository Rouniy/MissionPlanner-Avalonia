using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class MicrodroneDownlinkViewModel : ViewModelBase, IDisposable {
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly Func<MicrodroneSourceTarget?> _activeTarget;
  private readonly Func<string, int, ICommsSerial> _openSerial;
  private readonly bool _subscribedToAppState;
  private CancellationTokenSource? _cts;
  private Task? _senderTask;
  private ICommsSerial? _output;
  private MicrodroneSourceTarget? _boundTarget;
  private volatile bool _targetInvalidated;
  private int _stopScheduled;
  private bool _disposed;

  public MicrodroneDownlinkViewModel()
      : this(CaptureAppStateTarget, OpenSerial, subscribeToAppState: true) {
  }

  internal MicrodroneDownlinkViewModel(
      Func<MicrodroneSourceTarget?> activeTarget,
      Func<string, int, ICommsSerial> openSerial,
      bool subscribeToAppState = false) {
    _activeTarget = activeTarget;
    _openSerial = openSerial;
    _subscribedToAppState = subscribeToAppState;
    SelectedBaud = 57600;
    RefreshPorts();
    RefreshSourceDescription();
    if (_subscribedToAppState) {
      AppState.ConnectionChanged += OnConnectionChanged;
    }
  }

  public ObservableCollection<string> Ports { get; } = new();

  public ObservableCollection<int> Bauds { get; } = new() {
      4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200,
  };

  [ObservableProperty]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private string _connectButtonText = "Connect";

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _sourceDescription = "No connected telemetry source.";

  [ObservableProperty]
  private string _lastLine = "";

  public bool IsRunning => _cts != null;
  public bool CanEditSettings => !Busy && !IsRunning;

  partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditSettings));

  [RelayCommand]
  private void RefreshPorts() {
    string? selected = SelectedPort;
    Ports.Clear();
    foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(item => item)) {
      Ports.Add(port);
    }
    SelectedPort = selected != null && Ports.Contains(selected)
        ? selected
        : Ports.FirstOrDefault();
  }

  [RelayCommand]
  private async Task ToggleConnectAsync() {
    await _lifecycleGate.WaitAsync();
    Busy = true;
    try {
      if (_cts != null) {
        await StopCoreAsync("Stopped.");
      } else {
        await StartCoreAsync();
      }
    } finally {
      Busy = false;
      _lifecycleGate.Release();
    }
  }

  private async Task StartCoreAsync() {
    if (_disposed) {
      return;
    }
    if (string.IsNullOrWhiteSpace(SelectedPort)) {
      Status = "Select a serial output port first.";
      return;
    }
    if (!Bauds.Contains(SelectedBaud)) {
      Status = "Select a supported baud rate.";
      return;
    }

    MicrodroneSourceTarget? target = CaptureActiveTarget();
    if (target == null) {
      Status = "Connect and select a vehicle before starting MicroDrone output.";
      RefreshSourceDescription();
      return;
    }

    _boundTarget = target;
    _targetInvalidated = false;
    SourceDescription = TargetDescription(target);
    ICommsSerial? opened = null;
    try {
      string port = SelectedPort;
      int baud = SelectedBaud;
      Status = $"Opening {port} at {baud} baud…";
      opened = await Task.Run(() => _openSerial(port, baud));
      if (_disposed || !IsTargetCurrent(target)) {
        throw new InvalidOperationException(TargetChangedMessage);
      }

      var cts = new CancellationTokenSource();
      ICommsSerial activeOutput = opened;
      _output = activeOutput;
      _cts = cts;
      _senderTask = Task.Run(
          () => SendLoopAsync(activeOutput, target, cts.Token), cts.Token);
      opened = null;
      ConnectButtonText = "Stop";
      Status = $"Emitting official MicroDrone downlink frames at 10 Hz on {port}.";
      NotifyRunningState();
    } catch (Exception ex) {
      CloseOutput(opened);
      _boundTarget = null;
      Status = _targetInvalidated ? TargetChangedMessage : "MicroDrone output failed: " + ex.Message;
      RefreshSourceDescription();
    }
  }

  private async Task SendLoopAsync(
      ICommsSerial output, MicrodroneSourceTarget target, CancellationToken cancellationToken) {
    int counter = 0;
    try {
      while (true) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }

        MicrodroneTelemetry telemetry = MicrodroneTelemetry.Capture(target.Link.MAV.cs);
        string frame = MicrodroneDownlinkEncoder.EncodeFrame(
            telemetry, DateTimeOffset.UtcNow, counter);
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }
        output.Write(frame);
        string finalLine = frame.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[^1];
        Dispatcher.UIThread.Post(() => {
          if (!_disposed) {
            LastLine = finalLine;
          }
        });
        counter++;
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      RequestStop("MicroDrone serial output stopped: " + ex.Message);
    }
  }

  private async Task StopCoreAsync(string reason) {
    CancellationTokenSource? cts = _cts;
    Task? senderTask = _senderTask;
    ICommsSerial? output = _output;
    _cts = null;
    _senderTask = null;
    _output = null;
    cts?.Cancel();
    CloseOutput(output);
    if (senderTask != null) {
      try {
        await senderTask;
      } catch (OperationCanceledException) {
      } catch {
      }
    }
    cts?.Dispose();
    _boundTarget = null;
    _targetInvalidated = false;
    ConnectButtonText = "Connect";
    Status = reason;
    NotifyRunningState();
    RefreshSourceDescription();
  }

  private void OnConnectionChanged() {
    if (_disposed) {
      return;
    }
    MicrodroneSourceTarget? bound = _boundTarget;
    MicrodroneSourceTarget? current = CaptureActiveTarget();
    if (bound == null) {
      Dispatcher.UIThread.Post(RefreshSourceDescription);
      return;
    }
    if (TargetsMatch(bound, current)) {
      return;
    }
    InvalidateTarget();
  }

  private void InvalidateTarget() {
    _targetInvalidated = true;
    _cts?.Cancel();
    RequestStop(TargetChangedMessage);
  }

  private void RequestStop(string reason) {
    if (_disposed || Interlocked.Exchange(ref _stopScheduled, 1) != 0) {
      return;
    }
    Dispatcher.UIThread.Post(() => _ = StopForReasonAsync(reason));
  }

  private async Task StopForReasonAsync(string reason) {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync(reason);
      } else if (!_disposed) {
        Status = reason;
      }
    } finally {
      _lifecycleGate.Release();
      Interlocked.Exchange(ref _stopScheduled, 0);
    }
  }

  internal static bool TargetsMatch(
      MicrodroneSourceTarget? expected, MicrodroneSourceTarget? current) => expected == current;

  internal static bool ShouldContinue(
      bool invalidated, MicrodroneSourceTarget? expected, MicrodroneSourceTarget? current) =>
      !invalidated && expected != null && TargetsMatch(expected, current);

  private bool IsTargetCurrent(MicrodroneSourceTarget target) =>
      ShouldContinue(_targetInvalidated, target, CaptureActiveTarget());

  internal void SynchronizeActiveTarget() => OnConnectionChanged();

  private MicrodroneSourceTarget? CaptureActiveTarget() => _activeTarget();

  private static MicrodroneSourceTarget? CaptureAppStateTarget() {
    MAVLinkInterface link = AppState.comPort;
    return link.BaseStream?.IsOpen == true
        ? new MicrodroneSourceTarget(link, link.MAV.sysid, link.MAV.compid)
        : null;
  }

  private static ICommsSerial OpenSerial(string port, int baud) {
    var output = new SerialPort { PortName = port, BaudRate = baud };
    try {
      output.Open();
      return output;
    } catch {
      output.Dispose();
      throw;
    }
  }

  private static void CloseOutput(ICommsSerial? output) {
    if (output == null) {
      return;
    }
    try {
      if (output.IsOpen) {
        output.Close();
      }
    } catch {
    }
    (output as IDisposable)?.Dispose();
  }

  private void RefreshSourceDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    MicrodroneSourceTarget? current = CaptureActiveTarget();
    SourceDescription = current == null
        ? "No connected telemetry source."
        : TargetDescription(current);
  }

  private static string TargetDescription(MicrodroneSourceTarget target) =>
      $"Telemetry source: {target.SystemId}:{target.ComponentId} on the active modem.";

  private void NotifyRunningState() {
    OnPropertyChanged(nameof(IsRunning));
    OnPropertyChanged(nameof(CanEditSettings));
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_subscribedToAppState) {
      AppState.ConnectionChanged -= OnConnectionChanged;
    }
    _targetInvalidated = true;
    CancellationTokenSource? cts = _cts;
    _cts = null;
    _senderTask = null;
    _boundTarget = null;
    cts?.Cancel();
    CloseOutput(_output);
    _output = null;
    cts?.Dispose();
  }

  private const string TargetChangedMessage =
      "The active modem or vehicle changed. MicroDrone output was stopped; start it again "
      + "after verifying the selected telemetry source.";
}

internal sealed record MicrodroneSourceTarget(
    MAVLinkInterface Link, byte SystemId, byte ComponentId);
