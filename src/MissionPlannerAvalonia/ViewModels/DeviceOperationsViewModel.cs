using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;

namespace MissionPlannerAvalonia.ViewModels;

public partial class DeviceOperationsViewModel : ViewModelBase, IDisposable {
  private MAVLinkInterface _comPort = AppState.comPort;
  private DeviceOperationTarget? _sessionTarget;
  private volatile bool _targetInvalidated;
  private long _targetRevision;
  private bool _disposed;

  public DeviceOperationsViewModel() {
    BindActiveTarget(initial: true);
    AppState.ConnectionChanged += OnConnectionChanged;
  }

  public ObservableCollection<string> BusTypes { get; } = ["SPI", "I2C"];

  [ObservableProperty]
  private int _systemId = 1;

  [ObservableProperty]
  private int _componentId = 1;

  [ObservableProperty]
  private string _busType = "SPI";

  [ObservableProperty]
  private string _busName = "icm20948_ext";

  [ObservableProperty]
  private int _busNumber;

  [ObservableProperty]
  private int _address;

  [ObservableProperty]
  private int _registerStart = 255;

  [ObservableProperty]
  private int _count = 1;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private bool _requiresTargetRebind;

  [ObservableProperty]
  private string _output =
      "DEVICE_OP directly accesses a flight-controller peripheral bus. Use only with known hardware.";

  public bool IsSpi => BusType == "SPI";
  public bool CanOperate => !Busy && !RequiresTargetRebind && _sessionTarget != null;

  partial void OnBusTypeChanged(string value) => OnPropertyChanged(nameof(IsSpi));
  partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanOperate));
  partial void OnRequiresTargetRebindChanged(bool value) => OnPropertyChanged(nameof(CanOperate));

  [RelayCommand]
  private void UseActiveTarget() {
    if (Busy) {
      return;
    }
    BindActiveTarget(initial: false);
  }

  [RelayCommand]
  private async Task ReadAsync() {
    if (!Validate()) {
      return;
    }

    Busy = true;
    Output = "Waiting for DEVICE_OP_READ_REPLY…";
    try {
      var request = CaptureRequest();
      MAVLinkInterface link = _comPort;
      DeviceOperationTarget operationTarget = _sessionTarget!;
      var result = await Task.Run(() => {
        if (!IsSessionCurrent(operationTarget)) {
          return (stale: true, status: (byte)0, data: Array.Empty<byte>());
        }
        byte status = link.device_op(
            request.SystemId,
            request.ComponentId,
            out byte[] data,
            request.BusType,
            request.BusName,
            request.Bus,
            request.Address,
            request.Register,
            request.Count);
        return (stale: false, status, data);
      });
      Output = result.stale || !IsSessionCurrent(operationTarget)
          ? StaleResultMessage
          : FormatResult(result.status, request.Register, result.data);
    } catch (Exception ex) {
      Output = "DEVICE_OP read failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  [RelayCommand]
  private async Task TestIcm20948Async() {
    if (!Validate(requireDisarmed: true) || !IsSpi || !await Services.Dialogs.Confirm(
            "ICM20948 DEVICE_OP Test",
            $"This reproduces Mission Planner's developer test on {SystemId}:{ComponentId}: "
            + $"write 72 00 to register FF of SPI device '{BusName.Trim()}', then read two bytes back. "
            + "It can disrupt or damage incorrectly selected hardware. Continue only if this exact "
            + "device is an ICM20948 and the vehicle is disarmed?")) {
      return;
    }
    // The active target can change while the confirmation dialog is open.
    if (!Validate(requireDisarmed: true)) {
      return;
    }

    Busy = true;
    Output = "Running the ICM20948 write/read test…";
    try {
      var request = CaptureRequest() with { Register = 0xff, Count = 2 };
      MAVLinkInterface link = _comPort;
      DeviceOperationTarget operationTarget = _sessionTarget!;
      var result = await Task.Run(() => {
        if (!IsSessionCurrent(operationTarget)) {
          return (stale: true, writeStatus: (byte)0, readStatus: (byte)0,
              data: Array.Empty<byte>());
        }
        byte writeStatus = link.device_op(
            request.SystemId,
            request.ComponentId,
            out _,
            MAVLink.DEVICE_OP_BUSTYPE.SPI,
            request.BusName,
            0,
            0,
            0xff,
            2,
            [0x72, 0x00]);
        if (!IsSessionCurrent(operationTarget)) {
          return (stale: true, writeStatus, readStatus: (byte)0, data: Array.Empty<byte>());
        }
        byte readStatus = link.device_op(
            request.SystemId,
            request.ComponentId,
            out byte[] data,
            MAVLink.DEVICE_OP_BUSTYPE.SPI,
            request.BusName,
            0,
            0,
            0xff,
            2);
        return (stale: false, writeStatus, readStatus, data);
      });
      Output = result.stale || !IsSessionCurrent(operationTarget)
          ? StaleResultMessage
          : $"Write result: {FormatStatus(result.writeStatus)}\n"
            + FormatResult(result.readStatus, 0xff, result.data);
    } catch (Exception ex) {
      Output = "ICM20948 DEVICE_OP test failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  private bool Validate(bool requireDisarmed = false) {
    if (_comPort.BaseStream?.IsOpen != true) {
      Output = "Connect a vehicle before using DEVICE_OP.";
      return false;
    }
    if (_targetInvalidated || !IsSessionCurrent(_sessionTarget)) {
      InvalidateTarget();
      return false;
    }
    if (requireDisarmed && _comPort.MAV.cs.armed) {
      Output = "The ICM20948 write/read test is blocked while the selected vehicle is armed.";
      return false;
    }
    if (SystemId is < 1 or > 255 || ComponentId is < 1 or > 255
        || BusNumber is < 0 or > 255 || Address is < 0 or > 255
        || RegisterStart is < 0 or > 255 || Count is < 1 or > 128) {
      Output = "System/component IDs and bus values must fit in one byte; count must be 1–128.";
      return false;
    }
    if (IsSpi && string.IsNullOrWhiteSpace(BusName)) {
      Output = "Enter the ArduPilot SPI bus name.";
      return false;
    }
    if (IsSpi && Encoding.UTF8.GetByteCount(BusName.Trim()) > 40) {
      Output = "The ArduPilot SPI bus name must fit in the 40-byte DEVICE_OP field.";
      return false;
    }
    return true;
  }

  private DeviceOperationRequest CaptureRequest() => new(
      (byte)SystemId,
      (byte)ComponentId,
      IsSpi ? MAVLink.DEVICE_OP_BUSTYPE.SPI : MAVLink.DEVICE_OP_BUSTYPE.I2C,
      IsSpi ? BusName.Trim() : "",
      (byte)BusNumber,
      (byte)Address,
      (byte)RegisterStart,
      (byte)Count);

  internal static string FormatResult(byte status, byte register, byte[] data) {
    if (data.Length == 0) {
      return status == 0
          ? "No DEVICE_OP reply data was received before the one-second upstream timeout."
          : $"DEVICE_OP failed with {FormatStatus(status)}.";
    }

    var text = new StringBuilder();
    text.AppendLine(status == 0
        ? $"DEVICE_OP succeeded: {data.Length} byte(s)."
        : $"DEVICE_OP returned {FormatStatus(status)}: {data.Length} byte(s).");
    for (int offset = 0; offset < data.Length; offset += 16) {
      text.Append($"{(register + offset) & 0xff:X2}: ");
      text.AppendLine(string.Join(" ", data.Skip(offset).Take(16).Select(value => value.ToString("X2"))));
    }
    return text.ToString().TrimEnd();
  }

  internal static string FormatStatus(byte status) => status switch {
    0 => "result 0 (OK)",
    1 => "result 1 (bad bus)",
    2 => "result 2 (bad device)",
    3 => "result 3 (semaphore unavailable)",
    4 => "result 4 (bad response)",
    _ => $"result {status} (unknown)",
  };

  internal static bool TargetsMatch(
      DeviceOperationTarget? expected, DeviceOperationTarget? current) => expected == current;

  internal static bool ShouldAcceptResult(
      bool invalidated, DeviceOperationTarget? expected, DeviceOperationTarget? current) =>
      !invalidated && expected != null && TargetsMatch(expected, current);

  internal static bool IsStableBinding(
      long observedRevision,
      long currentRevision,
      DeviceOperationTarget? captured,
      DeviceOperationTarget? current) =>
      observedRevision == currentRevision && TargetsMatch(captured, current);

  private static DeviceOperationTarget? CaptureActiveTarget() => CaptureTarget(AppState.comPort);

  private static DeviceOperationTarget? CaptureTarget(MAVLinkInterface link) {
    return link.BaseStream?.IsOpen == true
        ? new DeviceOperationTarget(link, link.MAV.sysid, link.MAV.compid)
        : null;
  }

  private bool IsSessionCurrent(DeviceOperationTarget? expected) =>
      ShouldAcceptResult(_targetInvalidated, expected, CaptureActiveTarget());

  private void OnConnectionChanged() {
    if (_disposed || TargetsMatch(_sessionTarget, CaptureActiveTarget())) {
      return;
    }
    Interlocked.Increment(ref _targetRevision);
    _targetInvalidated = true;
    if (Dispatcher.UIThread.CheckAccess()) {
      InvalidateTarget();
    } else {
      Dispatcher.UIThread.Post(InvalidateTarget);
    }
  }

  private void InvalidateTarget() {
    if (_disposed) {
      return;
    }
    _targetInvalidated = true;
    RequiresTargetRebind = true;
    Output = StaleResultMessage;
  }

  private void BindActiveTarget(bool initial) {
    long observedRevision = Volatile.Read(ref _targetRevision);
    MAVLinkInterface activeLink = AppState.comPort;
    _comPort = activeLink;
    _sessionTarget = CaptureTarget(activeLink);
    bool changedDuringBind = !IsStableBinding(
        observedRevision,
        Volatile.Read(ref _targetRevision),
        _sessionTarget,
        CaptureActiveTarget());
    _targetInvalidated = changedDuringBind;
    RequiresTargetRebind = changedDuringBind;
    OnPropertyChanged(nameof(CanOperate));
    if (changedDuringBind) {
      InvalidateTarget();
      return;
    }
    if (_sessionTarget is { } target) {
      SystemId = target.SystemId > 0 ? target.SystemId : 1;
      ComponentId = target.ComponentId > 0 ? target.ComponentId : 1;
      if (!initial) {
        Output = $"Bound DEVICE_OP to active target {SystemId}:{ComponentId}.";
      }
    } else if (!initial) {
      Output = "No active connected target. Connect a vehicle, then choose Use Active Target.";
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.ConnectionChanged -= OnConnectionChanged;
  }

  private const string StaleResultMessage =
      "The active modem or vehicle changed. The old DEVICE_OP result was discarded; choose "
      + "Use Active Target before another operation.";

  private readonly record struct DeviceOperationRequest(
      byte SystemId,
      byte ComponentId,
      MAVLink.DEVICE_OP_BUSTYPE BusType,
      string BusName,
      byte Bus,
      byte Address,
      byte Register,
      byte Count);
}

internal sealed record DeviceOperationTarget(
    MAVLinkInterface Link, byte SystemId, byte ComponentId);
