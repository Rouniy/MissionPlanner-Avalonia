using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.ArduPilot.Mavlink;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public sealed class ConfigDeveloperToolsViewModel : ActionPageViewModel, IDisposable {
  private readonly SemaphoreSlim _operationGate = new(1, 1);
  private CancellationTokenSource? _firmwareArchiveCancel;
  private CancellationTokenSource? _parameterRecoveryCancel;
  private ParameterRecoveryTarget? _parameterRecoveryTarget;
  private int _parameterRecoveryInvalidated;
  private RemoteLog? _remoteLog;
  private bool _disposed;

  public ConfigDeveloperToolsViewModel() {
    Title = "Developer Tools";
    Instructions =
        "Cross-platform diagnostics and recovery tools ported from Mission Planner's hidden developer window. " +
        "Vehicle-changing actions require a connection, a disarmed vehicle, and explicit confirmation.";

    Action("Decode MAVLink Packet", () => _ = DecodePacketAsync());
    Action("Decode Hardware ID", () => _ = DecodeHardwareIdAsync());
    Action("MAVLink Device Operations", () => Views.DeviceOperationsWindow.OpenWindow());
    Action("3D Terrain View", () => Views.Terrain3DWindow.OpenWindow());
    Action("MicroDrone Downlink", () => Views.MicrodroneDownlinkWindow.OpenWindow());
    Action("MAVLink Serial TCP Bridge", () => Views.MavlinkSerialTcpBridgeWindow.OpenWindow());
    Action("Download Firmware Archive", () => _ = DownloadFirmwareArchiveAsync());
    Action("Cancel Firmware Archive", CancelFirmwareArchive);
    Action("Probe MAVLink Camera", () => _ = ProbeCameraAsync());
    Action("Embed Defaults in APJ", () => _ = EmbedDefaultsAsync());
    Action("Split DataFlash Log", () => _ = SplitDataFlashAsync());
    Action("Create DashWare CSV", () => _ = CreateDashWareAsync());
    Action("Extract GPS Corrections", () => _ = ExtractGpsCorrectionsAsync());
    Action("Convert Shapefile to POLY", () => _ = ConvertShapefileToPolyAsync());
    Action("Translation / RESX Editor", () => Views.TranslationEditorWindow.OpenWindow());
    Action("OSD Video — Telemetry Overlay", () => Views.OsdVideoOverlayWindow.OpenWindow());
    Action("Offline Magnetometer Calibration (MagFit)", () => Views.OfflineMagFitWindow.OpenWindow());
    Action("Flight Log Index", () => Views.LogIndexWindow.OpenWindow(Settings.Instance.LogDir));
    Action("Organize Log Directory", () => _ = OrganizeLogsAsync());
    Action("Download DataFlash Logs over SFTP", () => Views.SftpLogDownloadWindow.OpenWindow());
    Action("Download MAVFTP File", () => _ = DownloadMavFtpFileAsync());
    Action("Restore Parameters (Recovery)", () => _ = RestoreParametersAsync());
    Action("Cancel Parameter Restore", CancelParameterRecovery);
    Action("Set QNH", () => _ = SetQnhAsync());
    Action("Adjust Barometer Altitude", () => _ = AdjustBarometerAltitudeAsync());
    Action("Force Accel Calibrated", () => _ = ForceCalibrationAsync(accelerometer: true));
    Action("Force Compass Calibrated", () => _ = ForceCalibrationAsync(accelerometer: false));
    Action("Reboot Vehicle", () => _ = RebootVehicleAsync());
    Action("Upgrade Bootloader", () => _ = UpgradeBootloaderAsync());
    Action("Reboot to DFU", () => _ = RebootToDfuAsync());
    Action("Start Remote DataFlash Log", () => _ = StartRemoteLogAsync());
    Action("Stop Remote DataFlash Log", StopRemoteLog);
    AppState.ConnectionChanged += OnConnectionChanged;
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

  private async Task DownloadFirmwareArchiveAsync() {
    var owner = Dialogs.Owner;
    if (owner?.StorageProvider == null) {
      AppendLog("Firmware archive: no window is available for selecting an output directory.");
      return;
    }
    IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions {
          Title = "Select parent directory for the firmware archive",
          AllowMultiple = false,
        });
    string? parent = folders.FirstOrDefault()?.TryGetLocalPath();
    if (parent == null) {
      return;
    }

    string destination = NextFirmwareArchiveDirectory(parent, DateTime.UtcNow);
    if (!await Dialogs.ConfirmDangerous(
            "Download Firmware Archive",
            "Download every firmware binary referenced by Mission Planner's official "
            + "firmware2.xml? This can transfer a large amount of data and may take a long time. "
            + "The legacy manifest currently contains unsigned HTTP firmware URLs. The downloader "
            + "tries HTTPS first and records SHA-256 for every saved file, but may fall back to HTTP "
            + "for legacy hosts; these digests detect later changes but do not authenticate the source. "
            + "A new directory is published only after all download attempts finish; cancellation "
            + "removes the staging directory. Unavailable legacy files are reported and "
            + $"left as network URLs in the local manifest.\n\nDestination: {destination}",
            "Download all firmware")) {
      return;
    }
    if (!_operationGate.Wait(0)) {
      AppendLog("Firmware archive: another developer operation is already running.");
      return;
    }

    var cancellation = new CancellationTokenSource();
    _firmwareArchiveCancel = cancellation;
    int lastReported = 0;
    var progress = new Progress<FirmwareArchiveProgress>(item => {
      int interval = Math.Max(1, item.Total / 20);
      if (item.Completed == 1 || item.Completed == item.Total
          || item.Completed - lastReported >= interval) {
        lastReported = item.Completed;
        AppendLog($"Firmware archive: {item.Completed}/{item.Total} — {item.Item}");
      }
    });

    AppendLog("Firmware archive: reading the official firmware2.xml mirrors …");
    try {
      using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
      http.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlannerAvalonia/FirmwareArchive");
      var service = new FirmwareArchiveService(http);
      FirmwareArchiveResult result = await service.DownloadAsync(
          FirmwareArchiveService.OfficialManifestUris,
          destination,
          progress,
          cancellation.Token);
      string state = result.FailedFiles == 0 ? "complete" : "published with unavailable files";
      AppendLog($"Firmware archive {state}: {result.FileCount} downloaded, "
                + $"{result.FailedFiles} unavailable, {result.BytesDownloaded:N0} bytes, "
                + $"manifest {result.ManifestSource}. Output: {result.Directory}");
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      AppendLog("Firmware archive: cancelled; the incomplete staging directory was removed.");
    } catch (Exception ex) {
      AppendLog("Firmware archive failed: " + ex.Message);
    } finally {
      if (ReferenceEquals(_firmwareArchiveCancel, cancellation)) {
        _firmwareArchiveCancel = null;
      }
      cancellation.Dispose();
      _operationGate.Release();
    }
  }

  private void CancelFirmwareArchive() {
    CancellationTokenSource? cancellation = _firmwareArchiveCancel;
    if (cancellation == null) {
      AppendLog("Firmware archive: no download is running.");
      return;
    }
    cancellation.Cancel();
    AppendLog("Firmware archive: cancellation requested …");
  }

  internal static string NextFirmwareArchiveDirectory(string parent, DateTime utcNow) {
    string root = Path.GetFullPath(parent);
    string stem = "MissionPlanner-Firmware-Archive-" + utcNow.ToUniversalTime()
        .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    for (int suffix = 1; suffix <= 1000; suffix++) {
      string name = suffix == 1 ? stem : stem + "-" + suffix.ToString(CultureInfo.InvariantCulture);
      string candidate = Path.Combine(root, name);
      if (!Directory.Exists(candidate) && !File.Exists(candidate)) {
        return candidate;
      }
    }
    throw new IOException("Unable to allocate a new firmware archive directory name.");
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

  private async Task ConvertShapefileToPolyAsync() {
    string? input = await PickFileAsync(
        "Select shapefile to convert", "ESRI Shapefile", "*.shp", "*.SHP");
    if (input == null) {
      return;
    }
    string directory = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
    if (!await Dialogs.ConfirmDangerous(
            "Convert Shapefile to POLY",
            "Create one poly-N.poly file per non-empty SHP feature next to the selected " +
            $"shapefile?\n\nOutput directory: {directory}\n\n" +
            "Existing files with the same names will be replaced atomically.",
            "Convert and replace")) {
      return;
    }
    if (!_operationGate.Wait(0)) {
      AppendLog("Shapefile to POLY: another developer operation is already running.");
      return;
    }

    AppendLog("Shapefile to POLY: converting " + input + " …");
    try {
      ShapefilePolyExportResult result = await Task.Run(() =>
          ShapefileImportService.ExportPolyFiles(input));
      if (result.Files.Count == 0) {
        AppendLog("Shapefile to POLY: no non-empty geometry with valid WGS84 coordinates found.");
        return;
      }
      string projection = result.ProjectionName == null
          ? "coordinates treated as WGS84"
          : $"reprojected from {result.ProjectionName}";
      AppendLog($"Shapefile to POLY: wrote {result.Files.Count} file(s), " +
                $"{result.PointCount} point(s), {projection}. First output: {result.Files[0]}");
    } catch (Exception ex) {
      AppendLog("Shapefile to POLY failed: " + ex.Message);
    } finally {
      _operationGate.Release();
    }
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
    var input = await PickFileAsync("Select recovery parameter file", "Parameter file", "*.param", "*.parm");
    if (input == null) {
      return;
    }

    ParameterRecoveryTarget? target = CaptureParameterRecoveryTarget();
    if (target == null) {
      AppendLog("Parameter recovery: connect and select a disarmed vehicle first.");
      return;
    }
    if (!await Dialogs.Confirm(
            "Restore Parameters (Recovery)",
            "This recovery path writes ENABLE parameters first, resets matching *_ID parameters to zero, " +
            $"and then writes all compatible values to {target.SystemId}:{target.ComponentId}. " +
            "Continue while this exact vehicle remains selected and disarmed?")) {
      return;
    }
    if (!IsParameterRecoveryTargetCurrent(target)) {
      AppendLog("Parameter recovery: the selected modem or vehicle changed, disconnected, or became " +
                "armed while the confirmation was open; nothing was written.");
      return;
    }

    if (!_operationGate.Wait(0)) {
      AppendLog("Another developer operation is already running.");
      return;
    }
    using var cancellation = new CancellationTokenSource();
    Volatile.Write(ref _parameterRecoveryInvalidated, 0);
    _parameterRecoveryCancel = cancellation;
    _parameterRecoveryTarget = target;
    AppendLog($"Parameter recovery: loading and prefetching parameter names for " +
              $"{target.SystemId}:{target.ComponentId} …");
    try {
      if (!IsParameterRecoveryTargetCurrent(target)) {
        throw new ParameterRecoveryTargetChangedException();
      }
      var result = await Task.Run(
          () => RestoreParameters(input, target, cancellation.Token), cancellation.Token);
      AppendLog($"Parameter recovery complete: {result.Set} set, {result.Unchanged} unchanged, " +
                $"{result.Failed.Count} failed." +
                (result.Failed.Count == 0 ? "" : " Failed: " + string.Join(", ", result.Failed)));
    } catch (ParameterRecoveryTargetChangedException) {
      AppendLog("Parameter recovery stopped: the active modem or vehicle changed, disconnected, or " +
                "became armed. No further parameter writes were attempted.");
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      string reason = Volatile.Read(ref _parameterRecoveryInvalidated) != 0
          ? "the active modem or vehicle changed, disconnected, or became armed"
          : "cancellation was requested by the operator";
      AppendLog($"Parameter recovery stopped: {reason}. No further parameter writes were attempted.");
    } catch (Exception ex) {
      AppendLog("Parameter recovery failed: " + ex.Message);
    } finally {
      if (ReferenceEquals(_parameterRecoveryCancel, cancellation)) {
        _parameterRecoveryCancel = null;
        _parameterRecoveryTarget = null;
      }
      _operationGate.Release();
    }
  }

  private ParameterRecoveryResult RestoreParameters(
      string path, ParameterRecoveryTarget target, CancellationToken cancellationToken) {
    var values = ParamFile.loadParamFile(path);
    return ParameterRecoveryWorkflow.Run(
        values,
        target,
        IsParameterRecoveryTargetCurrent,
        (captured, name, requireResponse) => captured.Link.GetParam(
            captured.SystemId, captured.ComponentId, name, requireresponce: requireResponse),
        (captured, name, value) => captured.Link.setParam(
            captured.SystemId, captured.ComponentId, name, value, true),
        static (captured, name) => captured.State.param.ContainsKey(name)
            ? captured.State.param[name].Value
            : null,
        cancellationToken);
  }

  private static ParameterRecoveryTarget? CaptureParameterRecoveryTarget() {
    MAVLinkInterface link = AppState.comPort;
    MAVState state = link.MAV;
    return link.BaseStream?.IsOpen == true && !state.cs.armed
        ? new ParameterRecoveryTarget(link, state, state.sysid, state.compid)
        : null;
  }

  private static bool IsParameterRecoveryTargetCurrent(ParameterRecoveryTarget target) {
    MAVLinkInterface activeLink = AppState.comPort;
    return activeLink.BaseStream?.IsOpen == true
        && !target.State.cs.armed
        && ParameterRecoveryWorkflow.TargetsMatch(target, activeLink);
  }

  private void CancelParameterRecovery() {
    CancellationTokenSource? cancellation = _parameterRecoveryCancel;
    if (cancellation == null) {
      AppendLog("Parameter recovery: no restore is running.");
      return;
    }
    try {
      cancellation.Cancel();
      AppendLog("Parameter recovery: cancellation requested …");
    } catch (ObjectDisposedException) {
      AppendLog("Parameter recovery: the restore has already stopped.");
    }
  }

  private void OnConnectionChanged() {
    ParameterRecoveryTarget? target = _parameterRecoveryTarget;
    if (_disposed || target == null || IsParameterRecoveryTargetCurrent(target)) {
      return;
    }
    Interlocked.Exchange(ref _parameterRecoveryInvalidated, 1);
    try {
      _parameterRecoveryCancel?.Cancel();
    } catch (ObjectDisposedException) {
    }
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

  private async Task AdjustBarometerAltitudeAsync() {
    if (!RequireSafeVehicle("Barometer altitude adjustment")) {
      return;
    }

    var target = _comPort.MAV;
    byte targetSysId = target.sysid;
    byte targetCompId = target.compid;
    string parameter = target.param.ContainsKey("GND_ABS_PRESS")
        ? "GND_ABS_PRESS"
        : "BARO1_GND_PRESS";
    if (!target.param.ContainsKey(parameter)) {
      AppendLog("Barometer altitude adjustment: read vehicle parameters first; neither " +
                "GND_ABS_PRESS nor BARO1_GND_PRESS is currently available.");
      return;
    }

    double currentPressure = target.param[parameter].Value;
    string? input = await Dialogs.InputBox(
        "Adjust Barometer Altitude",
        "Altitude correction in metres (-100 to 100). Mission Planner applies 11.1 Pa per metre.",
        "0");
    if (input == null) {
      return;
    }
    bool parsed = double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture,
                      out double altitudeOffset)
                  || double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture,
                      out altitudeOffset);
    if (!parsed || !TryCalculateBarometerPressure(
            currentPressure, altitudeOffset, out double targetPressure)) {
      AppendLog("Barometer altitude adjustment: enter a finite offset from -100 to 100 metres; " +
                "the resulting pressure must remain between 80000 and 120000 Pa.");
      return;
    }
    if (Math.Abs(altitudeOffset) < 1e-9) {
      AppendLog("Barometer altitude adjustment: zero offset; no parameter was changed.");
      return;
    }

    string currentText = currentPressure.ToString("0.###", CultureInfo.InvariantCulture);
    string offsetText = altitudeOffset.ToString("+0.###;-0.###", CultureInfo.InvariantCulture);
    string targetText = targetPressure.ToString("0.###", CultureInfo.InvariantCulture);
    if (!await Dialogs.ConfirmDangerous(
            "Adjust Barometer Altitude",
            $"Write {parameter} from {currentText} Pa to {targetText} Pa using an altitude " +
            $"correction of {offsetText} m?\n\nThis deliberately changes the vehicle's stored " +
            "barometric reference. Use normal calibration unless you specifically need this " +
            "Mission Planner recovery/developer operation.",
            "Write pressure")) {
      return;
    }
    if (!IsConnected || !IsSelectedVehicleTarget(
            _comPort, target, targetSysId, targetCompId) || target.cs.armed) {
      AppendLog("Barometer altitude adjustment: the selected vehicle changed, disconnected, or " +
                "became armed while the confirmation was open; nothing was written.");
      return;
    }

    await RunVehicleToolAsync("Barometer altitude adjustment", () => {
      if (!IsConnected || !IsSelectedVehicleTarget(
              _comPort, target, targetSysId, targetCompId) || target.cs.armed) {
        throw new InvalidOperationException(
            "selected vehicle changed, disconnected, or became armed before the write");
      }
      return _comPort.setParam(targetSysId, targetCompId, parameter, targetPressure, true);
    });
  }

  internal static bool TryCalculateBarometerPressure(
      double currentPressurePa, double altitudeOffsetMetres, out double targetPressurePa) {
    targetPressurePa = double.NaN;
    if (!double.IsFinite(currentPressurePa) || !double.IsFinite(altitudeOffsetMetres)
        || altitudeOffsetMetres is < -100 or > 100) {
      return false;
    }

    // Official temp.cs uses current pressure + offset * 11.1. Its adjacent comment relates
    // 338.6388 Pa to 30.48 m (100 ft), which confirms that the control value is metres.
    targetPressurePa = currentPressurePa + altitudeOffsetMetres * 11.1;
    return targetPressurePa is >= 80000 and <= 120000;
  }

  internal static bool IsSelectedVehicleTarget(
      MAVLinkInterface link, MAVState target, byte sysId, byte compId) =>
      link.sysidcurrent == sysId && link.compidcurrent == compId
      && ReferenceEquals(link.MAV, target);

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
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.ConnectionChanged -= OnConnectionChanged;

    CancellationTokenSource? recovery = _parameterRecoveryCancel;
    _parameterRecoveryCancel = null;
    _parameterRecoveryTarget = null;
    try {
      recovery?.Cancel();
    } catch (ObjectDisposedException) {
    }

    CancellationTokenSource? archive = _firmwareArchiveCancel;
    _firmwareArchiveCancel = null;
    try {
      archive?.Cancel();
    } catch (ObjectDisposedException) {
    }

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
