using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MissionPlanner.ArduPilot;
using MissionPlanner.ArduPilot.Mavlink;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public sealed class ConfigDeveloperToolsViewModel : ActionPageViewModel, IDisposable {
  private readonly SemaphoreSlim _operationGate = new(1, 1);
  private RemoteLog? _remoteLog;

  public ConfigDeveloperToolsViewModel() {
    Title = "Developer Tools";
    Instructions =
        "Cross-platform diagnostics and recovery tools ported from Mission Planner's hidden developer window. " +
        "Vehicle-changing actions require a connection, a disarmed vehicle, and explicit confirmation.";

    Action("Decode MAVLink Packet", () => _ = DecodePacketAsync());
    Action("Decode Hardware ID", () => _ = DecodeHardwareIdAsync());
    Action("MAVLink Device Operations", () => Views.DeviceOperationsWindow.OpenWindow());
    Action("MicroDrone Downlink", () => Views.MicrodroneDownlinkWindow.OpenWindow());
    Action("Probe MAVLink Camera", () => _ = ProbeCameraAsync());
    Action("Embed Defaults in APJ", () => _ = EmbedDefaultsAsync());
    Action("Split DataFlash Log", () => _ = SplitDataFlashAsync());
    Action("Create DashWare CSV", () => _ = CreateDashWareAsync());
    Action("Extract GPS Corrections", () => _ = ExtractGpsCorrectionsAsync());
    Action("Offline Magnetometer Calibration (MagFit)", () => Views.OfflineMagFitWindow.OpenWindow());
    Action("Flight Log Index", () => Views.LogIndexWindow.OpenWindow(Settings.Instance.LogDir));
    Action("Organize Log Directory", () => _ = OrganizeLogsAsync());
    Action("Download DataFlash Logs over SFTP", () => Views.SftpLogDownloadWindow.OpenWindow());
    Action("Download MAVFTP File", () => _ = DownloadMavFtpFileAsync());
    Action("Restore Parameters (Recovery)", () => _ = RestoreParametersAsync());
    Action("Set QNH", () => _ = SetQnhAsync());
    Action("Force Accel Calibrated", () => _ = ForceCalibrationAsync(accelerometer: true));
    Action("Force Compass Calibrated", () => _ = ForceCalibrationAsync(accelerometer: false));
    Action("Reboot Vehicle", () => _ = RebootVehicleAsync());
    Action("Upgrade Bootloader", () => _ = UpgradeBootloaderAsync());
    Action("Reboot to DFU", () => _ = RebootToDfuAsync());
    Action("Start Remote DataFlash Log", () => _ = StartRemoteLogAsync());
    Action("Stop Remote DataFlash Log", StopRemoteLog);
  }

  private async Task DecodePacketAsync() {
    var input = await Dialogs.InputBox(
        "Decode MAVLink Packet",
        "Enter compact hex (fd0500...) or separated decimal/hex bytes");
    if (input == null) {
      return;
    }

    try {
      AppendLog(DeveloperToolParsers.DecodeMavlinkPacket(input));
    } catch (Exception ex) {
      AppendLog("MAVLink decode failed: " + ex.Message);
    }
  }

  private async Task DecodeHardwareIdAsync() {
    var id = await Dialogs.InputBox("Decode Hardware ID", "Device ID (decimal or 0x-prefixed hex)");
    if (id == null) {
      return;
    }
    var name = await Dialogs.InputBox(
        "Decode Hardware ID",
        "Optional parameter name (for example COMPASS_DEV_ID or INS_ACC_ID)");
    if (name == null) {
      return;
    }

    try {
      AppendLog(DeveloperToolParsers.DecodeHardwareId(id, name));
    } catch (Exception ex) {
      AppendLog("Hardware ID decode failed: " + ex.Message);
    }
  }

  private async Task ProbeCameraAsync() {
    if (!RequireConnection()) {
      return;
    }

    var camera = _comPort.MAVlist.FirstOrDefault(item =>
        item.compid == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_CAMERA);
    if (camera == null) {
      AppendLog("Camera probe: no MAV_COMP_ID_CAMERA component is currently known on this link.");
      return;
    }
    if (!await Dialogs.Confirm(
            "Probe MAVLink Camera",
            $"Request information, settings and storage status from camera {camera.sysid}:{camera.compid}, "
            + "select its default camera mode and request video streaming?")) {
      return;
    }

    AppendLog($"Camera probe: sending Camera Protocol requests to {camera.sysid}:{camera.compid} …");
    try {
      await Task.Run(() => new Camera().test(_comPort));
      AppendLog("Camera probe sent. Responses are visible in MAVLink Inspector; a reported stream URI "
                + "can be opened from Flight Data video controls.");
    } catch (Exception ex) {
      AppendLog("Camera probe failed: " + ex.Message);
    }
  }

  private async Task EmbedDefaultsAsync() {
    var firmware = await PickFileAsync("Select APJ firmware", "APJ firmware", "*.apj");
    if (firmware == null) {
      return;
    }
    var parameters = await PickFileAsync("Select parameter defaults", "Parameter file", "*.param", "*.parm");
    if (parameters == null) {
      return;
    }

    string output = firmware + "new.apj";
    await RunFileToolAsync("APJ defaults", () => apj_tool.Process(firmware, parameters),
        $"New firmware written to {output}");
  }

  private async Task SplitDataFlashAsync() {
    var input = await PickFileAsync("Select DataFlash log", "DataFlash log", "*.bin", "*.log");
    if (input == null) {
      return;
    }
    var countText = await Dialogs.InputBox("Split DataFlash Log", "Number of pieces", "10");
    if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
        || count is < 2 or > 1000) {
      if (countText != null) {
        AppendLog("Split DataFlash: enter a piece count between 2 and 1000.");
      }
      return;
    }

    await RunFileToolAsync("Split DataFlash", () => {
      using var log = new DFLogBuffer(input);
      log.SplitLog(count);
    }, $"Created {count} pieces next to {input}");
  }

  private async Task CreateDashWareAsync() {
    var input = await PickFileAsync("Select DataFlash log", "DataFlash log", "*.bin", "*.log");
    if (input == null) {
      return;
    }
    var types = await Dialogs.InputBox(
        "Create DashWare CSV",
        "Semicolon-separated DataFlash message types (empty means all)",
        "GPS;ATT;NTUN;CTUN;MODE;BAT");
    if (types == null) {
      return;
    }

    var output = await PickSaveFileAsync(
        "Save DashWare CSV", Path.GetFileNameWithoutExtension(input) + "-dashware.csv", "csv");
    if (output == null) {
      return;
    }
    var selected = types.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => item.ToUpperInvariant()).ToList();
    await RunFileToolAsync("DashWare CSV", () => DashWare.Create(input, output,
        selected.Count == 0 ? null : selected), $"CSV written to {output}");
  }

  private async Task ExtractGpsCorrectionsAsync() {
    var input = await PickFileAsync("Select telemetry log", "Telemetry log", "*.tlog");
    if (input == null) {
      return;
    }
    var output = await PickSaveFileAsync(
        "Save GPS correction stream", Path.GetFileNameWithoutExtension(input) + "-corrections.dat", "dat");
    if (output == null) {
      return;
    }

    await RunFileToolAsync("GPS correction extraction", () => ExtractGpsCorrections(input, output),
        $"Correction bytes written to {output}");
  }

  internal static int ExtractGpsCorrections(string input, string output) {
    int messages = 0;
    using var source = File.OpenRead(input);
    using var destination = File.Create(output);
    using var mav = new MissionPlanner.MAVLinkInterface(source) { logreadmode = true };
    while (mav.logplaybackfile.BaseStream.Position < mav.logplaybackfile.BaseStream.Length) {
      var packet = mav.readPacketAsync().GetAwaiter().GetResult();
      if (packet == null) {
        break;
      }
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_INJECT_DATA) {
        var item = packet.ToStructure<MAVLink.mavlink_gps_inject_data_t>();
        destination.Write(item.data, 0, item.len);
        messages++;
      } else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA) {
        var item = packet.ToStructure<MAVLink.mavlink_gps_rtcm_data_t>();
        destination.Write(item.data, 0, item.len);
        messages++;
      }
    }
    return messages;
  }

  private async Task OrganizeLogsAsync() {
    var folders = await (Dialogs.Owner?.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Select log directory to organize",
      AllowMultiple = false,
    }) ?? Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>()));
    var root = folders.FirstOrDefault()?.TryGetLocalPath();
    if (root == null) {
      return;
    }
    if (!await Dialogs.Confirm(
            "Organize Log Directory",
            "Sort all .tlog, .rlog, .bin and .log files below this directory into vehicle/date folders?")) {
      return;
    }

    await RunFileToolAsync("Log organizer", () => {
      int count = LogOrganizer.Organize(root);
      AppendLog($"Log organizer processed {count} candidate files in {root}.");
    });
  }

  private async Task DownloadMavFtpFileAsync() {
    if (!RequireConnection()) {
      return;
    }
    var remotePath = await Dialogs.InputBox("Download MAVFTP File", "Remote path", "@SYS/threads.txt");
    if (string.IsNullOrWhiteSpace(remotePath)) {
      return;
    }
    var output = await PickSaveFileAsync(
        "Save MAVFTP file", Path.GetFileName(remotePath.Replace('\\', '/')), null);
    if (output == null) {
      return;
    }

    AppendLog($"MAVFTP: downloading {remotePath} …");
    if (!_operationGate.Wait(0)) {
      AppendLog("Another developer operation is already running.");
      return;
    }
    try {
      var data = await Task.Run(() => {
        var ftp = new MAVFtp(_comPort, (byte)_comPort.sysidcurrent, (byte)_comPort.compidcurrent);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stream = ftp.GetFile(remotePath, timeout, true);
        return stream.ToArray();
      });
      await File.WriteAllBytesAsync(output, data);
      AppendLog($"MAVFTP: wrote {data.Length} bytes to {output}.");
    } catch (Exception ex) {
      AppendLog("MAVFTP download failed: " + ex.Message);
    } finally {
      _operationGate.Release();
    }
  }

  private async Task RestoreParametersAsync() {
    if (!RequireSafeVehicle("Parameter recovery")) {
      return;
    }
    var input = await PickFileAsync("Select recovery parameter file", "Parameter file", "*.param", "*.parm");
    if (input == null || !await Dialogs.Confirm(
            "Restore Parameters (Recovery)",
            "This recovery path writes ENABLE parameters first, resets matching *_ID parameters to zero, " +
            "and then writes all compatible values. Continue while the vehicle is disarmed?")) {
      return;
    }

    AppendLog("Parameter recovery: loading and prefetching parameter names …");
    if (!_operationGate.Wait(0)) {
      AppendLog("Another developer operation is already running.");
      return;
    }
    try {
      var result = await Task.Run(() => RestoreParameters(input));
      AppendLog($"Parameter recovery complete: {result.Set} set, {result.Unchanged} unchanged, " +
                $"{result.Failed.Count} failed." +
                (result.Failed.Count == 0 ? "" : " Failed: " + string.Join(", ", result.Failed)));
    } catch (Exception ex) {
      AppendLog("Parameter recovery failed: " + ex.Message);
    } finally {
      _operationGate.Release();
    }
  }

  private (int Set, int Unchanged, List<string> Failed) RestoreParameters(string path) {
    var values = ParamFile.loadParamFile(path);
    foreach (var item in values) {
      _comPort.GetParam(_comPort.MAV.sysid, _comPort.MAV.compid, item.Key, requireresponce: false);
    }
    foreach (var item in values.Where(item => item.Key.Contains("ENABLE", StringComparison.OrdinalIgnoreCase))) {
      try {
        _comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, item.Key, item.Value, true);
      } catch {
        // The complete pass below records failures and reports them to the user.
      }
    }

    int set = 0;
    int unchanged = 0;
    var failed = new List<string>();
    foreach (var item in values) {
      try {
        if (_comPort.MAV.param.ContainsKey(item.Key)
            && Math.Abs(_comPort.MAV.param[item.Key].Value - item.Value) < 1e-9) {
          unchanged++;
          continue;
        }
        _comPort.GetParam(_comPort.MAV.sysid, _comPort.MAV.compid, item.Key);
        if (item.Key.EndsWith("_ID", StringComparison.OrdinalIgnoreCase)) {
          _comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, item.Key, 0, true);
        }
        if (_comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, item.Key, item.Value, true)) {
          set++;
        } else {
          failed.Add(item.Key);
        }
      } catch {
        failed.Add(item.Key);
      }
    }
    return (set, unchanged, failed);
  }

  private async Task SetQnhAsync() {
    if (!RequireSafeVehicle("Set QNH")) {
      return;
    }
    string parameter = _comPort.MAV.param.ContainsKey("GND_ABS_PRESS")
        ? "GND_ABS_PRESS"
        : "BARO1_GND_PRESS";
    string current = _comPort.MAV.param.ContainsKey(parameter)
        ? _comPort.MAV.param[parameter].Value.ToString(CultureInfo.InvariantCulture)
        : "101325";
    var input = await Dialogs.InputBox(
        "Set QNH", $"{parameter} in pascals (103040 = 1030.4 hPa)", current);
    if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        || value is < 80000 or > 120000) {
      if (input != null) {
        AppendLog("Set QNH: enter an invariant value between 80000 and 120000 Pa.");
      }
      return;
    }

    await RunVehicleToolAsync("Set QNH", () =>
        _comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, parameter, value, true));
  }

  private async Task ForceCalibrationAsync(bool accelerometer) {
    string target = accelerometer ? "accelerometer" : "compass";
    if (!RequireSafeVehicle("Force calibration status") || !await Dialogs.Confirm(
            "Force Calibration Status",
            $"Mark the current {target} calibration as valid without performing a normal calibration? " +
            "Use this only when recovering a board after a parameter wipe.")) {
      return;
    }
    await RunVehicleToolAsync($"Force {target} calibrated", () =>
        _comPort.doCommand(
            _comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION,
            0, accelerometer ? 0 : 76, 0, 0, accelerometer ? 76 : 0, 0, 0, true));
  }

  private async Task RebootVehicleAsync() {
    if (!RequireSafeVehicle("Reboot") || !await Dialogs.Confirm(
            "Reboot Vehicle", "Reboot the connected flight controller now?")) {
      return;
    }
    await RunVehicleToolAsync("Reboot", () => _comPort.doReboot(false, true));
  }

  private async Task UpgradeBootloaderAsync() {
    if (!RequireSafeVehicle("Bootloader upgrade") || !await Dialogs.Confirm(
            "Upgrade Bootloader",
            "This asks the flight controller to flash its embedded bootloader. An interruption or " +
            "incompatible image can make the board unbootable. Continue?")
        || !await Dialogs.Confirm(
            "Confirm Bootloader Upgrade",
            "Final confirmation: keep power and the data link connected for at least five minutes. Start now?")) {
      return;
    }
    await RunVehicleToolAsync("Bootloader upgrade", () =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.FLASH_BOOTLOADER,
            0, 0, 0, 0, 290876, 0, 0, true));
  }

  private async Task RebootToDfuAsync() {
    if (!RequireSafeVehicle("DFU reboot") || !await Dialogs.Confirm(
            "Reboot to DFU", "Reboot the connected flight controller into DFU mode? The link will close.")) {
      return;
    }
    await RunVehicleToolAsync("DFU reboot", () =>
        _comPort.doDFUBoot(_comPort.MAV.sysid, _comPort.MAV.compid));
  }

  private async Task StartRemoteLogAsync() {
    if (!RequireSafeVehicle("Remote DataFlash log")) {
      return;
    }
    try {
      StopRemoteLog();
      _remoteLog = RemoteLog.StartRemoteLog(_comPort, _comPort.MAV.sysid, _comPort.MAV.compid);
      AppendLog($"Remote DataFlash logging started in {Settings.GetDefaultLogDir()}.");
    } catch (Exception ex) {
      AppendLog("Remote DataFlash logging failed: " + ex.Message);
    }
    await Task.CompletedTask;
  }

  private void StopRemoteLog() {
    var logger = _remoteLog;
    _remoteLog = null;
    if (logger == null) {
      AppendLog("Remote DataFlash logger is not running from this page.");
      return;
    }
    try {
      if (IsConnected) {
        logger.Stop(_comPort.MAV.sysid, _comPort.MAV.compid);
      }
    } catch (Exception ex) {
      AppendLog("Remote DataFlash stop warning: " + ex.Message);
    } finally {
      logger.Dispose();
    }
    AppendLog("Remote DataFlash logging stopped.");
  }

  private bool RequireSafeVehicle(string action) {
    if (!RequireConnection()) {
      return false;
    }
    if (_comPort.MAV.cs.armed) {
      AppendLog($"{action}: blocked while the vehicle is armed.");
      return false;
    }
    return true;
  }

  private async Task RunVehicleToolAsync(string name, Func<bool> action) {
    if (!_operationGate.Wait(0)) {
      AppendLog(name + ": another developer operation is already running.");
      return;
    }
    AppendLog(name + ": running …");
    try {
      bool ok = await Task.Run(action);
      AppendLog(name + (ok ? ": completed." : ": vehicle rejected the request."));
    } catch (Exception ex) {
      AppendLog(name + " failed: " + ex.Message);
    } finally {
      _operationGate.Release();
    }
  }

  private async Task RunFileToolAsync(string name, Action action, string? success = null) {
    if (!_operationGate.Wait(0)) {
      AppendLog(name + ": another developer operation is already running.");
      return;
    }
    AppendLog(name + ": running …");
    try {
      await Task.Run(action);
      AppendLog(success ?? name + ": completed.");
    } catch (Exception ex) {
      AppendLog(name + " failed: " + ex.Message);
    } finally {
      _operationGate.Release();
    }
  }

  private static async Task<string?> PickFileAsync(
      string title, string typeName, params string[] patterns) {
    var owner = Dialogs.Owner;
    if (owner == null) {
      return null;
    }
    var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = title,
      AllowMultiple = false,
      FileTypeFilter = new[] {
        new FilePickerFileType(typeName) { Patterns = patterns },
        new FilePickerFileType("All files") { Patterns = new[] { "*" } },
      },
    });
    return files.FirstOrDefault()?.TryGetLocalPath();
  }

  private static async Task<string?> PickSaveFileAsync(
      string title, string suggestedName, string? extension) {
    var owner = Dialogs.Owner;
    if (owner == null) {
      return null;
    }
    var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = title,
      SuggestedFileName = suggestedName,
      DefaultExtension = extension,
    });
    return file?.TryGetLocalPath();
  }

  public void Dispose() {
    var logger = _remoteLog;
    _remoteLog = null;
    if (logger == null) {
      return;
    }
    try {
      if (IsConnected) {
        logger.Stop(_comPort.MAV.sysid, _comPort.MAV.compid);
      }
    } catch {
      // The connection may already be gone while the application is shutting down.
    }
    logger.Dispose();
  }
}
