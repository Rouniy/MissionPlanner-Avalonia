using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigADSBViewModel : ParamPageBase, IDisposable {
  private static readonly HashSet<string> _bitmaskParams = new(StringComparer.OrdinalIgnoreCase) {
    "ADSB_OPTIONS",
    "ADSB_RF_CAPABLE",
    "ADSB_RF_SELECT",
  };
  private int _flightIdSubscription = -1;
  private int _registrationSubscription = -1;

  public ObservableCollection<ParamField> FilteredFields { get; } = new();

  [ObservableProperty]
  private string _search = "";

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private string _flightId = "";

  [ObservableProperty]
  private string _aircraftRegistration = "";

  public ConfigADSBViewModel() {
    Title = "ADSB";
    Intro = "ADS-B receiver / avoidance. Populated on connect.";
    Setup();
    SubscribeIdentification();
    RequestIdentification();
  }

  protected override void OnRefreshed() {
    Fields.Clear();
    Setup();
  }

  partial void OnSearchChanged(string value) {
    ApplyFilter();
  }

  private void Setup() {
    foreach (var key in comPort.MAV.param.Keys.ToList()
                 .Where(k => k.StartsWith("ADSB_", StringComparison.OrdinalIgnoreCase) ||
                             k.StartsWith("AVD_", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
      F(key, _bitmaskParams.Contains(key) ? "bitmask" : null);
    }
    ApplyFilter();
  }

  private void ApplyFilter() {
    FilteredFields.Clear();
    var term = Search?.Trim() ?? "";
    foreach (var f in Fields) {
      if (term.Length < 2 ||
          f.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
          f.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
          f.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) {
        FilteredFields.Add(f);
      }
    }
  }

  [RelayCommand]
  private async Task SaveFlightId() {
    if (comPort.BaseStream?.IsOpen != true) {
      Status = "offline";
      return;
    }
    if (NeedsClearConfirmation(FlightId)
        && !await Services.Dialogs.Confirm("Clear Flight Identification",
            "The Flight Identification field is empty. Send an empty value and clear the device setting?")) {
      Status = "Flight ID was not changed.";
      return;
    }
    try {
      var flid = new MAVLink.mavlink_uavionix_adsb_out_cfg_flightid_t(FlightId.MakeBytesSize(9));
      await Task.Run(() => {
        comPort.sendPacket(flid, comPort.sysidcurrent, comPort.compidcurrent);
        System.Threading.Thread.Sleep(200);
        comPort.sendPacket(flid, comPort.sysidcurrent, comPort.compidcurrent);
        System.Threading.Thread.Sleep(200);
        comPort.generatePacket(MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_GET,
            new MAVLink.mavlink_uavionix_adsb_get_t(10005), comPort.sysidcurrent,
            comPort.compidcurrent);
      });
      Status = "Flight ID sent.";
    } catch (Exception ex) {
      Status = "send failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task SaveAircraftRegistration() {
    if (comPort.BaseStream?.IsOpen != true) {
      Status = "offline";
      return;
    }
    if (NeedsClearConfirmation(AircraftRegistration)
        && !await Services.Dialogs.Confirm("Clear Aircraft Registration",
            "The Aircraft Registration field is empty. Send an empty value and clear the device setting?")) {
      Status = "Aircraft registration was not changed.";
      return;
    }
    try {
      var acreg = new MAVLink.mavlink_uavionix_adsb_out_cfg_registration_t(
          AircraftRegistration.MakeBytesSize(9));
      await Task.Run(() => {
        comPort.sendPacket(acreg, comPort.sysidcurrent, comPort.compidcurrent);
        System.Threading.Thread.Sleep(200);
        comPort.sendPacket(acreg, comPort.sysidcurrent, comPort.compidcurrent);
        System.Threading.Thread.Sleep(200);
        comPort.generatePacket(MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_GET,
            new MAVLink.mavlink_uavionix_adsb_get_t(10004), comPort.sysidcurrent,
            comPort.compidcurrent);
      });
      Status = "Aircraft registration sent.";
    } catch (Exception ex) {
      Status = "send failed: " + ex.Message;
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task Write() {
    if (comPort.BaseStream?.IsOpen != true) {
      Status = "offline";
      return;
    }

    var ordered = Fields
        .OrderBy(f => f.Name.Contains("ENABLE", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        .ToList();

    bool error = false;
    foreach (var f in ordered) {
      if (!f.Exists) {
        continue;
      }
      try {
        var name = f.Name;
        var value = f.Value;
        if (!await Task.Run(() => comPort.setParam(name, value))) {
          error = true;
        }
      } catch {
        error = true;
      }
    }

    Status = error ? "write failed" : "Parameters successfully saved.";
  }

  private void SubscribeIdentification() {
    if (comPort.BaseStream?.IsOpen != true) {
      return;
    }
    byte sysid = (byte)comPort.sysidcurrent;
    byte compid = (byte)comPort.compidcurrent;
    _flightIdSubscription = comPort.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_CFG_FLIGHTID, message => {
          var value = (MAVLink.mavlink_uavionix_adsb_out_cfg_flightid_t)message.data;
          string decoded = DecodeDeviceText(value.flight_id);
          Dispatcher.UIThread.Post(() => {
            FlightId = decoded;
            Status = "Flight ID read from the device.";
          });
          return true;
        }, sysid, compid);
    _registrationSubscription = comPort.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_CFG_REGISTRATION, message => {
          var value = (MAVLink.mavlink_uavionix_adsb_out_cfg_registration_t)message.data;
          string decoded = DecodeDeviceText(value.registration);
          Dispatcher.UIThread.Post(() => {
            AircraftRegistration = decoded;
            Status = "Aircraft registration read from the device.";
          });
          return true;
        }, sysid, compid);
  }

  private void RequestIdentification() {
    if (comPort.BaseStream?.IsOpen != true) {
      return;
    }
    try {
      Status = "Reading Flight ID and aircraft registration…";
      comPort.generatePacket(MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_GET,
          new MAVLink.mavlink_uavionix_adsb_get_t(10004), comPort.sysidcurrent,
          comPort.compidcurrent);
      comPort.generatePacket(MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_GET,
          new MAVLink.mavlink_uavionix_adsb_get_t(10005), comPort.sysidcurrent,
          comPort.compidcurrent);
    } catch (Exception ex) {
      Status = "Identification read failed: " + ex.Message;
    }
  }

  internal static string DecodeDeviceText(byte[]? value) =>
      value == null ? "" : Encoding.ASCII.GetString(value).TrimEnd('\0', ' ');

  internal static bool NeedsClearConfirmation(string? value) =>
      string.IsNullOrWhiteSpace(value);

  public void Dispose() {
    if (_flightIdSubscription != -1) {
      comPort.UnSubscribeToPacketType(_flightIdSubscription);
      _flightIdSubscription = -1;
    }
    if (_registrationSubscription != -1) {
      comPort.UnSubscribeToPacketType(_registrationSubscription);
      _registrationSubscription = -1;
    }
  }
}
