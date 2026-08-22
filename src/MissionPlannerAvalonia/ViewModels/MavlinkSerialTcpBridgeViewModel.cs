using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed record SerialControlDeviceOption(
    string Label, MAVLink.SERIAL_CONTROL_DEV Device) {
  public override string ToString() => Label;
}

public sealed record SerialControlBaudOption(string Label, uint BaudRate) {
  public override string ToString() => Label;
}

public partial class MavlinkSerialTcpBridgeViewModel : ViewModelBase, IDisposable {
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private MavlinkSerialTcpBridge? _bridge;
  private SerialControlTarget? _boundTarget;
  private bool _disposed;

  public MavlinkSerialTcpBridgeViewModel() {
    SelectedDevice = Devices[2];
    SelectedBaud = BaudRates[0];
    AppState.ConnectionChanged += OnConnectionChanged;
    RefreshTargetDescription();
  }

  public ObservableCollection<SerialControlDeviceOption> Devices { get; } = new() {
      new("TELEM1 — first telemetry port", MAVLink.SERIAL_CONTROL_DEV.TELEM1),
      new("TELEM2 — second telemetry port", MAVLink.SERIAL_CONTROL_DEV.TELEM2),
      new("GPS1 — first GPS port", MAVLink.SERIAL_CONTROL_DEV.GPS1),
      new("GPS2 — second GPS port", MAVLink.SERIAL_CONTROL_DEV.GPS2),
      new("SHELL — system shell", MAVLink.SERIAL_CONTROL_DEV.SHELL),
      new("SERIAL0", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL0),
      new("SERIAL1", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL1),
      new("SERIAL2", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL2),
      new("SERIAL3", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL3),
      new("SERIAL4", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL4),
      new("SERIAL5", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL5),
      new("SERIAL6", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL6),
      new("SERIAL7", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL7),
      new("SERIAL8", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL8),
      new("SERIAL9", MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL9),
  };

  public ObservableCollection<SerialControlBaudOption> BaudRates { get; } = new() {
      new("Keep current baud", 0),
      new("4,800", 4800),
      new("9,600", 9600),
      new("19,200", 19200),
      new("38,400", 38400),
      new("57,600", 57600),
      new("115,200", 115200),
      new("230,400", 230400),
      new("460,800", 460800),
      new("921,600", 921600),
  };

  [ObservableProperty]
  private SerialControlDeviceOption? _selectedDevice;

  [ObservableProperty]
  private SerialControlBaudOption? _selectedBaud;

  [ObservableProperty]
  private decimal _listenPort = 500;

  [ObservableProperty]
  private bool _allowRemoteClients;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private string _connectButtonText = "Start";

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _targetDescription = "No connected vehicle.";

  [ObservableProperty]
  private string _counters = "TCP → vehicle: 0 B; vehicle → TCP: 0 B; dropped: 0 B";

  public bool IsRunning => _bridge != null;
  public bool CanEditSettings => !Busy && !IsRunning;

  partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditSettings));

  [RelayCommand]
  private async Task ToggleAsync() {
    await _lifecycleGate.WaitAsync();
    Busy = true;
    try {
      if (_bridge == null) {
        await StartCoreAsync();
      } else {
        await StopCoreAsync("Stopped.");
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
    if (SelectedDevice == null || SelectedBaud == null) {
      Status = "Select a SERIAL_CONTROL device and baud policy.";
      return;
    }
    if (ListenPort != decimal.Truncate(ListenPort) || ListenPort is < 1 or > 65535) {
      Status = "Enter a TCP listen port from 1 to 65535.";
      return;
    }
    if (!SerialControlTargetGuard.TryCapture(out SerialControlTarget? target, out string error)
        || target == null) {
      Status = error;
      RefreshTargetDescription();
      return;
    }

    string exposure = AllowRemoteClients
        ? "The listener will accept connections on all IPv4 interfaces. It has no encryption or "
            + "authentication; firewall the selected port and use only a trusted network."
        : "The listener will accept connections only from this computer (127.0.0.1).";
    bool confirmed = await Dialogs.ConfirmDangerous(
        "Start MAVLink Serial TCP Bridge",
        $"Bind TCP port {(int)ListenPort} to {target.SystemId}:{target.ComponentId} / "
            + $"{SelectedDevice.Device}. The autopilot UART is held in exclusive SERIAL_CONTROL "
            + "mode while a TCP client is connected. The session stops if the selected modem or "
            + $"vehicle changes or becomes armed.\n\n{exposure}",
        "Start Bridge");
    if (!confirmed) {
      Status = "Start cancelled.";
      return;
    }
    if (!SerialControlTargetGuard.IsCurrent(target)) {
      Status = "The selected target changed after confirmation; start was cancelled.";
      RefreshTargetDescription();
      return;
    }

    var session = new MavlinkSerialControlSession(
        target, SelectedDevice.Device, SelectedBaud.BaudRate);
    var bridge = new MavlinkSerialTcpBridge(
        session,
        AllowRemoteClients ? IPAddress.Any : IPAddress.Loopback,
        (int)ListenPort);
    bridge.StatusChanged += OnBridgeStatusChanged;
    bridge.CountersChanged += OnBridgeCountersChanged;
    _bridge = bridge;
    _boundTarget = target;
    try {
      bridge.Start();
      ConnectButtonText = "Stop";
      TargetDescription = TargetText(target);
      NotifyRunningState();
      _ = ObserveBridgeAsync(bridge);
    } catch (Exception ex) {
      _bridge = null;
      _boundTarget = null;
      bridge.StatusChanged -= OnBridgeStatusChanged;
      bridge.CountersChanged -= OnBridgeCountersChanged;
      await bridge.DisposeAsync();
      Status = "Unable to start TCP bridge: " + ex.Message;
      RefreshTargetDescription();
      NotifyRunningState();
    }
  }

  private async Task ObserveBridgeAsync(MavlinkSerialTcpBridge bridge) {
    string finalStatus;
    try {
      await bridge.Completion.ConfigureAwait(false);
      finalStatus = "Bridge stopped.";
    } catch (OperationCanceledException) {
      finalStatus = "Bridge stopped.";
    } catch (SerialControlTargetChangedException ex) {
      finalStatus = ex.Message + " The bridge was stopped; verify the target before restarting.";
    } catch (Exception ex) {
      finalStatus = "Serial TCP bridge stopped: " + ex.Message;
    }
    Dispatcher.UIThread.Post(() => _ = CompleteObservedBridgeAsync(bridge, finalStatus));
  }

  private async Task CompleteObservedBridgeAsync(
      MavlinkSerialTcpBridge bridge, string finalStatus) {
    await _lifecycleGate.WaitAsync();
    try {
      if (ReferenceEquals(_bridge, bridge)) {
        await StopCoreAsync(finalStatus);
      }
    } finally {
      _lifecycleGate.Release();
    }
  }

  private async Task StopCoreAsync(string finalStatus) {
    MavlinkSerialTcpBridge? bridge = _bridge;
    _bridge = null;
    _boundTarget = null;
    if (bridge != null) {
      bridge.StatusChanged -= OnBridgeStatusChanged;
      bridge.CountersChanged -= OnBridgeCountersChanged;
      await bridge.DisposeAsync();
    }
    ConnectButtonText = "Start";
    Status = finalStatus;
    NotifyRunningState();
    RefreshTargetDescription();
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      await StopCoreAsync("Stopped.");
    } finally {
      _lifecycleGate.Release();
    }
  }

  private void OnBridgeStatusChanged(string value) {
    Dispatcher.UIThread.Post(() => {
      if (!_disposed) {
        Status = value;
      }
    });
  }

  private void OnBridgeCountersChanged() {
    MavlinkSerialTcpBridge? bridge = _bridge;
    if (bridge == null) {
      return;
    }
    string value = $"TCP → vehicle: {bridge.BytesFromTcp} B; vehicle → TCP: "
        + $"{bridge.BytesToTcp} B; dropped: {bridge.DroppedSerialBytes} B";
    Dispatcher.UIThread.Post(() => {
      if (!_disposed && ReferenceEquals(_bridge, bridge)) {
        Counters = value;
      }
    });
  }

  private void OnConnectionChanged() {
    Dispatcher.UIThread.Post(() => {
      if (!_disposed && _boundTarget == null) {
        RefreshTargetDescription();
      }
    });
  }

  private void RefreshTargetDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    TargetDescription = SerialControlTargetGuard.TryCapture(
        out SerialControlTarget? target, out string error) && target != null
        ? TargetText(target)
        : error;
  }

  private static string TargetText(SerialControlTarget target) =>
      $"Target: {target.SystemId}:{target.ComponentId} on the active modem (single-system link).";

  private void NotifyRunningState() {
    OnPropertyChanged(nameof(IsRunning));
    OnPropertyChanged(nameof(CanEditSettings));
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.ConnectionChanged -= OnConnectionChanged;
    MavlinkSerialTcpBridge? bridge = _bridge;
    _bridge = null;
    _boundTarget = null;
    bridge?.Cancel();
  }
}
