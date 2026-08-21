using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;

namespace MissionPlannerAvalonia.ViewModels;

public partial class DeviceOperationsViewModel : ViewModelBase {
  private readonly MAVLinkInterface _comPort = AppState.comPort;

  public DeviceOperationsViewModel() {
    SystemId = _comPort.MAV?.sysid is > 0 ? _comPort.MAV.sysid : 1;
    ComponentId = _comPort.MAV?.compid is > 0 ? _comPort.MAV.compid : 1;
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
  private string _output =
      "DEVICE_OP directly accesses a flight-controller peripheral bus. Use only with known hardware.";

  public bool IsSpi => BusType == "SPI";

  partial void OnBusTypeChanged(string value) => OnPropertyChanged(nameof(IsSpi));

  [RelayCommand]
  private async Task ReadAsync() {
    if (!Validate()) {
      return;
    }

    Busy = true;
    Output = "Waiting for DEVICE_OP_READ_REPLY…";
    try {
      var request = CaptureRequest();
      var result = await Task.Run(() => {
        byte status = _comPort.device_op(
            request.SystemId,
            request.ComponentId,
            out byte[] data,
            request.BusType,
            request.BusName,
            request.Bus,
            request.Address,
            request.Register,
            request.Count);
        return (status, data);
      });
      Output = FormatResult(result.status, request.Register, result.data);
    } catch (Exception ex) {
      Output = "DEVICE_OP read failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  [RelayCommand]
  private async Task TestIcm20948Async() {
    if (!Validate() || !IsSpi || !await Services.Dialogs.Confirm(
            "ICM20948 DEVICE_OP Test",
            "This reproduces Mission Planner's developer test: write 72 00 to register FF, "
            + "then read two bytes back. Continue only if the selected SPI device is an ICM20948?")) {
      return;
    }

    Busy = true;
    Output = "Running the ICM20948 write/read test…";
    try {
      var request = CaptureRequest() with { Register = 0xff, Count = 2 };
      var result = await Task.Run(() => {
        byte writeStatus = _comPort.device_op(
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
        byte readStatus = _comPort.device_op(
            request.SystemId,
            request.ComponentId,
            out byte[] data,
            MAVLink.DEVICE_OP_BUSTYPE.SPI,
            request.BusName,
            0,
            0,
            0xff,
            2);
        return (writeStatus, readStatus, data);
      });
      Output = $"Write result: {result.writeStatus}\n"
          + FormatResult(result.readStatus, 0xff, result.data);
    } catch (Exception ex) {
      Output = "ICM20948 DEVICE_OP test failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  private bool Validate() {
    if (_comPort.BaseStream?.IsOpen != true) {
      Output = "Connect a vehicle before using DEVICE_OP.";
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
          : $"DEVICE_OP failed with result {status}.";
    }

    var text = new StringBuilder();
    text.AppendLine(status == 0
        ? $"DEVICE_OP succeeded: {data.Length} byte(s)."
        : $"DEVICE_OP returned result {status}: {data.Length} byte(s).");
    for (int offset = 0; offset < data.Length; offset += 16) {
      text.Append($"{(register + offset) & 0xff:X2}: ");
      text.AppendLine(string.Join(" ", data.Skip(offset).Take(16).Select(value => value.ToString("X2"))));
    }
    return text.ToString().TrimEnd();
  }

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
