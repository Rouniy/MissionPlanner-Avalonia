using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class RawParamsViewModel : ViewModelBase, IDisposable {
  private static readonly HashSet<string> ProtectedFileParameters = new(
      StringComparer.OrdinalIgnoreCase) {
    "SYSID_SW_MREV",
    "WP_TOTAL",
    "CMD_TOTAL",
    "FENCE_TOTAL",
    "SYS_NUM_RESETS",
    "ARSPD_OFFSET",
    "GND_ABS_PRESS",
    "GND_TEMP",
    "BARO1_GND_PRESS",
    "BARO2_GND_PRESS",
    "BARO3_GND_PRESS",
    "BARO_GND_TEMP",
    "CMD_INDEX",
    "LOG_LASTFILE",
    "FORMAT_VERSION",
  };

  private MAVLinkInterface _comPort => AppState.comPort;

  private readonly FrameDefaultCatalogService _frameDefaultCatalog;
  private readonly List<ParamRow> _all = new();
  private readonly object _targetGate = new();
  private CancellationTokenSource? _frameDefaultCancellation;
  private ParameterTarget? _currentTarget;
  private long _targetRevision;
  private bool _disposed;

  public ObservableCollection<ParamRow> Params { get; } = new();

  public ObservableCollection<string> Categories { get; } = new() { "All" };

  public ObservableCollection<FrameDefaultFile> FrameDefaults { get; } = new();

  [ObservableProperty]
  private FrameDefaultFile? _selectedFrameDefault;

  [ObservableProperty]
  private bool _loadingFrameDefaults;

  [ObservableProperty]
  private string _selectedCategory = "All";

  [ObservableProperty]
  private string _searchText = "";

  [ObservableProperty]
  private bool _showModifiedOnly;

  [ObservableProperty]
  private bool _showNonDefaultOnly;

  [ObservableProperty]
  private bool _hasDefaults;

  [ObservableProperty]
  private string _status = "Not connected. Use Load from file / Load Demo to preview, or connect a vehicle.";

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;

  public RawParamsViewModel() : this(FrameDefaultCatalogService.Shared) { }

  internal RawParamsViewModel(FrameDefaultCatalogService frameDefaultCatalog) {
    _frameDefaultCatalog = frameDefaultCatalog;
    AppState.ConnectionChanged += OnConnectionChanged;
    SynchronizeSelectedVehicle();
  }

  partial void OnSearchTextChanged(string value) => ApplyFilter();

  partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

  partial void OnShowModifiedOnlyChanged(bool value) => ApplyFilter();

  partial void OnShowNonDefaultOnlyChanged(bool value) => ApplyFilter();

  [RelayCommand]
  private async Task Refresh() {
    if (!IsConnected) {
      await Services.Dialogs.Alert("Refresh parameters", "Not connected — cannot fetch params.");
      return;
    }
    if (_comPort.MAV.cs.armed && !await Services.Dialogs.MessageShowAgain(
            "Refresh Params", Strings.WarningUpdateParamList, "WarningUpdateParamList")) {
      return;
    }

    try {
      bool loaded = await AppState.ParameterLoads.LoadLatestAsync(
          _comPort.MAV.sysid, _comPort.MAV.compid);
      if (!loaded) {
        return;
      }
      LoadFromMav();
      AppState.RaiseConnectionChanged();
      await Services.Dialogs.Alert("Refresh parameters", $"Loaded {_all.Count} parameters.");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Refresh failed", ex.Message);
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task Write() {
    var dirty = _all.Where(r => r.IsDirty).ToList();
    if (dirty.Count == 0) {
      await Services.Dialogs.Alert("Write parameters", "No changes to write.");
      return;
    }

    var pending = new List<(ParamRow row, double val)>();
    foreach (var r in dirty) {
      if (!TryEval(r.ValueText, out var v)) {
        await Services.Dialogs.Alert("Invalid value",
            $"\"{r.ValueText}\" is not a valid value for {r.Name}.");
        return;
      }
      pending.Add((r, v));
    }

    if (!IsConnected) {

      foreach (var (row, val) in pending) {
        if (_comPort.MAV.param.ContainsKey(row.Name)) {
          _comPort.MAV.param[row.Name].Value = val;
        }
        row.SetCurrent(val);
      }
      await Services.Dialogs.Alert("Write parameters",
          $"Not connected — staged {pending.Count} change(s) into the in-memory list.");
      return;
    }

    pending = OrderPendingForWrite(pending);
    if (!await Services.Dialogs.Confirm(
            "Confirm Parameter Changes", BuildWriteConfirmation(pending))) {
      return;
    }

    int ok = 0;
    bool reboot = false;
    var fw = _comPort.MAV.cs.firmware.ToString();
    try {
      foreach (var (row, val) in pending) {
        var name = row.Name;
        bool wrote = await Task.Run(() => _comPort.setParam(name, val, true));
        if (wrote) {
          row.SetCurrent(val);
          ok++;
          try {
            reboot |= ParameterMetaDataRepository.GetParameterRebootRequired(name, fw);
          } catch { }
        }
      }
    } catch (Exception ex) {
      ApplyFilter();
      await Services.Dialogs.Alert("Write failed",
          $"Wrote {ok}/{pending.Count} before error: {ex.Message}");
      return;
    }

    ApplyFilter();
    if (ok == pending.Count) {
      await Services.Dialogs.Alert("Write parameters",
          $"Wrote {ok} parameter(s)."
          + (reboot ? "\n\nA reboot is required for some changes to take effect." : ""));
    } else {
      await Services.Dialogs.Alert("Write parameters",
          $"Wrote {ok} of {pending.Count}. The rest were not acknowledged — try again.");
    }
    await ReconcileParameterCountAfterWrite();
  }

  private async Task ReconcileParameterCountAfterWrite() {
    var parameters = _comPort.MAV.param;
    if (parameters.TotalReceived == parameters.TotalReported) {
      return;
    }
    if (_comPort.MAV.cs.armed) {
      await Services.Dialogs.Alert("Params",
          "The number of available parameters changed. A full refresh is unsafe while armed, "
          + "so some parameters may remain hidden until the vehicle is disarmed and refreshed.");
      parameters.TotalReported = parameters.TotalReceived;
      return;
    }

    await Services.Dialogs.Alert("Params",
        "The number of available parameters changed. The complete list will now be refreshed.");
    try {
      bool loaded = await AppState.ParameterLoads.LoadLatestAsync(
          _comPort.MAV.sysid, _comPort.MAV.compid);
      if (!loaded) {
        return;
      }
      LoadFromMav();
      AppState.RaiseConnectionChanged();
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Refresh failed", ex.Message);
    }
  }

  internal static string BuildWriteConfirmation(
      IReadOnlyList<(ParamRow row, double val)> pending) {
    const int maxDetails = 20;
    if (pending.Count > maxDetails) {
      return $"You are about to change {pending.Count} parameters. Continue?";
    }
    string details = string.Join(Environment.NewLine, pending.Select(change =>
        $"{change.row.Name}: {change.row.CurrentValue.ToString(CultureInfo.InvariantCulture)}"
        + $" -> {change.val.ToString(CultureInfo.InvariantCulture)}"));
    return $"You are about to change {pending.Count} parameter(s). "
        + $"Review the changes below:\n\n{details}\n\nContinue?";
  }

  internal static IReadOnlyList<string> ParameterWriteOrder(IEnumerable<string> names) {
    var ordered = names.ToList();
    ordered.SortENABLE();
    return ordered;
  }

  private static List<(ParamRow row, double val)> OrderPendingForWrite(
      IReadOnlyList<(ParamRow row, double val)> pending) {
    var byName = pending.ToDictionary(
        change => change.row.Name, StringComparer.OrdinalIgnoreCase);
    return ParameterWriteOrder(byName.Keys)
        .Select(name => byName[name])
        .ToList();
  }

  [RelayCommand]
  private async Task Commit() {
    if (!IsConnected) {
      await Services.Dialogs.Alert("Commit to flash", "Not connected — cannot commit to flash.");
      return;
    }
    try {
      _comPort.doCommand(
          _comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.PREFLIGHT_STORAGE,
          1, 0, 0, 0, 0, 0, 0
      );
      await Services.Dialogs.Alert("Commit to flash", "Commit-to-flash command sent.");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Commit failed", ex.Message);
    }
  }

  [RelayCommand]
  private async Task ResetToDefaults() {
    if (!IsConnected) {
      await Services.Dialogs.Alert("Reset parameters", "Not connected — cannot reset parameters.");
      return;
    }

    if (!await Services.Dialogs.Confirm(
            "Reset all parameters",
            "Reset every vehicle parameter to its firmware default and reboot the autopilot?\n\n" +
            "This cannot be undone unless you have saved a parameter file.")) {
      return;
    }

    try {
      await Task.Run(() => {
        // ArduPilot generations use one of these format-version parameters. The upstream
        // Mission Planner intentionally attempts both names for compatibility.
        byte sysid = _comPort.MAV.sysid;
        byte compid = _comPort.MAV.compid;
        if (!_comPort.setParam(sysid, compid, "FORMAT_VERSION", 0)) {
          _comPort.setParam(sysid, compid, "SYSID_SW_MREV", 0);
        }
        System.Threading.Thread.Sleep(1000);
        _comPort.doReboot(false, true);
        _comPort.BaseStream.Close();
      });
      Status = "Parameters reset; the autopilot is rebooting. Reconnect to reload defaults.";
      await Services.Dialogs.Alert("Reset parameters",
          "The autopilot is rebooting with default parameters. Reconnect after it starts.");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Reset failed", ex.Message);
    }
  }

  [RelayCommand]
  private void LoadDemo() {
    LoadFrom(
        new[]
        {
                MakeRow("ARMING_CHECK", 1, 1),
                MakeRow("BATT_CAPACITY", 5000, 3300),
                MakeRow("FENCE_ENABLE", 0, 0),
                MakeRow("RTL_ALT", 3000, 1500),
                MakeRow("WPNAV_SPEED", 500, 1000),
                MakeRow("ATC_RAT_RLL_P", 0.135, 0.135),
        }
    );
    Status = $"Loaded {_all.Count} demo parameters (edit a value → Write).";
  }

  public void LoadParamFile(string path) {
    if (!TryLoadParamFile(path, out var fileParams)) {
      return;
    }

    var result = StageImportedValues(_all, fileParams);
    ApplyFilter();
    _ = Services.Dialogs.Alert("Load parameters",
        $"{System.IO.Path.GetFileName(path)}: staged {result.Differing} change(s) "
        + $"of {result.Matched} matched param(s)."
        + (result.Protected == 0
            ? " Write to apply."
            : $" Skipped {result.Protected} protected runtime/calibration param(s). Write to apply."));
  }

  public IReadOnlyList<ParamComparisonRow> CompareParamFile(string path) {
    if (!TryLoadParamFile(path, out var fileParams)) {
      return [];
    }

    var rows = BuildComparison(_all, fileParams);
    if (rows.Count == 0) {
      _ = Services.Dialogs.Alert("Compare parameters",
          $"{System.IO.Path.GetFileName(path)}: no differing matched parameters.");
    }
    return rows;
  }

  [RelayCommand]
  private Task RefreshFrameDefaults() => LoadFrameDefaultsAsync(forceRefresh: true);

  internal Task EnsureFrameDefaultsAsync() => FrameDefaults.Count > 0
      ? Task.CompletedTask
      : LoadFrameDefaultsAsync(forceRefresh: false);

  private async Task LoadFrameDefaultsAsync(bool forceRefresh) {
    if (LoadingFrameDefaults) {
      return;
    }
    LoadingFrameDefaults = true;
    CancellationTokenSource cancellation = BeginFrameDefaultOperation();
    try {
      IReadOnlyList<FrameDefaultCatalogItem> catalog =
          await _frameDefaultCatalog.ListAsync(cancellation.Token, forceRefresh);
      var files = catalog.Select(file => new FrameDefaultFile(file.Name, file.Path)).ToList();
      FrameDefaults.Clear();
      foreach (var file in files) {
        FrameDefaults.Add(file);
      }
      SelectedFrameDefault = FrameDefaults.FirstOrDefault();
      Status = files.Count == 0
          ? "No ArduPilot frame-default files were found."
          : $"Loaded {files.Count} ArduPilot frame-default file choices.";
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      if (!_disposed) {
        Status = "Frame-default list request cancelled.";
      }
    } catch (Exception ex) {
      Status = "Frame-default list failed: " + ex.Message;
    } finally {
      CompleteFrameDefaultOperation(cancellation);
    }
  }

  internal async Task<FrameDefaultComparison?> DownloadFrameDefaultComparisonAsync() {
    var selected = SelectedFrameDefault;
    if (selected == null) {
      Status = "Select a frame-default file first.";
      return null;
    }
    TargetSnapshot snapshot = ObserveTargetChange();
    ParameterTarget? target = snapshot.Target;
    if (target == null) {
      Status = "Connect a vehicle before comparing a default profile.";
      return null;
    }
    long revision = snapshot.Revision;
    LoadingFrameDefaults = true;
    CancellationTokenSource cancellation = BeginFrameDefaultOperation();
    if (!IsTargetCurrent(target, revision)) {
      cancellation.Cancel();
    }
    try {
      byte[] bytes = await _frameDefaultCatalog.DownloadAsync(selected.Path, cancellation.Token);
      if (!IsTargetCurrent(target, revision)) {
        Status = "Vehicle changed while the profile was downloading; the old result was discarded.";
        return null;
      }
      string path = FrameDefaultCatalogService.GetCachePath(
          Services.AppPaths.CacheRoot, selected.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      await File.WriteAllBytesAsync(path, bytes, cancellation.Token);
      if (!IsTargetCurrent(target, revision)) {
        Status = "Vehicle changed while the profile was loading; the old result was discarded.";
        return null;
      }
      var comparison = CompareParamFile(path);
      Status = comparison.Count == 0
          ? $"{selected.Name}: no differing matched parameters."
          : $"{selected.Name}: choose which of {comparison.Count} differences to stage.";
      return new FrameDefaultComparison(target, revision, selected, comparison);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      if (!_disposed) {
        Status = IsTargetCurrent(target, revision)
            ? "Frame-default download cancelled."
            : "Vehicle changed during download; the old profile was discarded.";
      }
      return null;
    } catch (Exception ex) {
      Status = "Frame-default download failed: " + ex.Message;
      return null;
    } finally {
      CompleteFrameDefaultOperation(cancellation);
    }
  }

  internal bool TryApplyFrameDefaultComparison(FrameDefaultComparison comparison) {
    TargetSnapshot snapshot = ObserveTargetChange();
    if (!TryStageFrameDefaultComparison(
            _all, comparison, snapshot.Target, snapshot.Revision, out int staged)) {
      Status = "Vehicle changed while the comparison was open; no values were staged.";
      return false;
    }
    ShowModifiedOnly = true;
    ApplyFilter();
    Status = $"Staged {staged} selected parameter change(s). Review them, then use Write Params.";
    return true;
  }

  public void ApplyParamComparison(IEnumerable<ParamComparisonRow> comparison) {
    int staged = StageSelectedComparison(_all, comparison);
    ShowModifiedOnly = true;
    ApplyFilter();
    Status = $"Staged {staged} selected parameter change(s). Review them, then use Write Params.";
  }

  public void SaveParamFile(string path) {
    var table = new Hashtable();
    foreach (var r in _all) {
      table[r.Name] = r.CurrentValue;
    }
    try {
      ParamFile.SaveParamFile(path, table);
      _ = Services.Dialogs.Alert("Save parameters",
          $"Saved {table.Count} parameters to {System.IO.Path.GetFileName(path)}.");
    } catch (Exception ex) {
      _ = Services.Dialogs.Alert("Save failed", ex.Message);
    }
  }

  private static bool TryLoadParamFile(string path, out Dictionary<string, double> fileParams) {
    try {
      fileParams = ParamFile.loadParamFile(path);
      return true;
    } catch (Exception ex) {
      _ = Services.Dialogs.Alert("Load failed", ex.Message);
      fileParams = [];
      return false;
    }
  }

  internal static ParamFileStageResult StageImportedValues(
      IEnumerable<ParamRow> current, IReadOnlyDictionary<string, double> imported) {
    int matched = 0,
        differing = 0,
        protectedCount = 0;
    foreach (var row in current) {
      if (!imported.TryGetValue(row.Name, out double importedValue)) {
        continue;
      }
      matched++;
      if (IsProtectedFileParameter(row.Name)) {
        protectedCount++;
        continue;
      }
      if (importedValue != row.CurrentValue) {
        differing++;
        row.ValueText = importedValue.ToString(CultureInfo.InvariantCulture);
      }
    }
    return new ParamFileStageResult(matched, differing, protectedCount);
  }

  internal static IReadOnlyList<ParamComparisonRow> BuildComparison(
      IEnumerable<ParamRow> current, IReadOnlyDictionary<string, double> imported) {
    var rows = new List<ParamComparisonRow>();
    foreach (var row in current) {
      if (!IsProtectedFileParameter(row.Name)
          && imported.TryGetValue(row.Name, out double importedValue)
          && importedValue != row.CurrentValue) {
        rows.Add(new ParamComparisonRow(row.Name, row.CurrentValue, importedValue));
      }
    }
    return rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
  }

  internal static int StageSelectedComparison(
      IEnumerable<ParamRow> current, IEnumerable<ParamComparisonRow> comparison) {
    var selected = comparison.Where(row => row.Use)
        .ToDictionary(row => row.Name, row => row.FileValue, StringComparer.OrdinalIgnoreCase);
    int staged = 0;
    foreach (var row in current) {
      if (!IsProtectedFileParameter(row.Name)
          && selected.TryGetValue(row.Name, out double importedValue)) {
        row.ValueText = importedValue.ToString(CultureInfo.InvariantCulture);
        staged++;
      }
    }
    return staged;
  }

  internal static bool TryStageFrameDefaultComparison(
      IEnumerable<ParamRow> current,
      FrameDefaultComparison comparison,
      ParameterTarget? currentTarget,
      long currentRevision,
      out int staged) {
    if (currentTarget == null || comparison.Target != currentTarget
        || comparison.TargetRevision != currentRevision) {
      staged = 0;
      return false;
    }
    staged = StageSelectedComparison(current, comparison.Rows);
    return true;
  }

  internal static bool IsProtectedFileParameter(string name) =>
      ProtectedFileParameters.Contains(name);

  public void PersistFavs() {
    var favs = _all.Where(r => r.Fav).Select(r => r.Name).ToList();
    Settings.Instance.SetList("fav_params", favs);
  }

  private void LoadFromMav() {
    var fw = _comPort.MAV.cs.firmware.ToString();
    var favs = Settings.Instance.GetList("fav_params").ToHashSet();

    var snapshot = _comPort.MAV.param.ToArray();
    var rows = snapshot.Select(p => BuildRow(p.Name, p.Value, p.default_value, fw, favs)).ToList();
    LoadFrom(rows);
  }

  private void OnConnectionChanged() {
    if (_disposed) {
      return;
    }
    // Observe the change before posting UI work. This preserves a monotonic selection revision
    // even if the user switches away and back before the dispatcher processes either callback.
    ObserveTargetChange();
    Dispatcher.UIThread.Post(SynchronizeSelectedVehicle);
  }

  internal void SynchronizeSelectedVehicle() {
    if (_disposed) {
      return;
    }
    ObserveTargetChange();
    if (!IsConnected) {
      LoadFrom([]);
      Status = "Not connected. Connect a vehicle or explicitly load a parameter file.";
      return;
    }

    int received = _comPort.MAV.param.TotalReceived;
    int reported = _comPort.MAV.param.TotalReported;
    if (!CanExposeLiveParameters(received, reported)) {
      // PARAM_VALUE packets update MAVState incrementally. Keep the editor empty until the exact
      // selected vehicle has supplied a complete list, otherwise a partial response can look like
      // a valid configuration and invite unsafe writes.
      LoadFrom([]);
      Status = reported == 0
          ? $"Waiting for parameters from {_comPort.MAV.sysid}:{_comPort.MAV.compid}…"
          : $"Loading parameters from {_comPort.MAV.sysid}:{_comPort.MAV.compid} "
            + $"({received} / {reported}); values remain hidden until complete.";
      return;
    }

    LoadFromMav();
    Status = $"Loaded {_comPort.MAV.param.Count} parameters from "
        + $"{_comPort.MAV.sysid}:{_comPort.MAV.compid}.";
  }

  internal static bool CanExposeLiveParameters(int received, int reported) =>
      GCSViews.ConfigurationView.ConfigParamLoadingViewModel.HasAllParameters(
          received, reported);

  private ParameterTarget? CaptureCurrentTarget() => IsConnected
      ? new ParameterTarget(_comPort, _comPort.MAV.sysid, _comPort.MAV.compid)
      : null;

  private TargetSnapshot ObserveTargetChange() {
    ParameterTarget? selected = CaptureCurrentTarget();
    CancellationTokenSource? cancellation = null;
    TargetSnapshot snapshot;
    lock (_targetGate) {
      if (selected != _currentTarget) {
        _currentTarget = selected;
        _targetRevision++;
        cancellation = _frameDefaultCancellation;
      }
      snapshot = new TargetSnapshot(_currentTarget, _targetRevision);
    }
    CancelSafely(cancellation);
    return snapshot;
  }

  private bool IsTargetCurrent(ParameterTarget target, long revision) {
    ParameterTarget? selected = CaptureCurrentTarget();
    lock (_targetGate) {
      return selected == target && _currentTarget == target && _targetRevision == revision;
    }
  }

  private CancellationTokenSource BeginFrameDefaultOperation() {
    var cancellation = new CancellationTokenSource();
    lock (_targetGate) {
      _frameDefaultCancellation = cancellation;
    }
    return cancellation;
  }

  private static void CancelSafely(CancellationTokenSource? cancellation) {
    try {
      cancellation?.Cancel();
    } catch (ObjectDisposedException) {
      // Completion won the race after the target snapshot was taken.
    }
  }

  private void CompleteFrameDefaultOperation(CancellationTokenSource cancellation) {
    bool current;
    lock (_targetGate) {
      current = ReferenceEquals(_frameDefaultCancellation, cancellation);
      if (current) {
        _frameDefaultCancellation = null;
      }
    }
    if (current) {
      if (!_disposed) {
        LoadingFrameDefaults = false;
      }
    }
    cancellation.Dispose();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.ConnectionChanged -= OnConnectionChanged;
    CancellationTokenSource? cancellation;
    lock (_targetGate) {
      cancellation = _frameDefaultCancellation;
    }
    CancelSafely(cancellation);
  }

  private ParamRow BuildRow(string name, double value, double? def, string fw, HashSet<string> favs) {
    string units = Meta(name, ParameterMetaDataConstants.Units, fw);
    string range = Meta(name, ParameterMetaDataConstants.Range, fw);
    string values = Meta(name, ParameterMetaDataConstants.Values, fw);
    string desc = Meta(name, ParameterMetaDataConstants.Description, fw);
    string opts = (range + "\n" + values.Replace(",", "\n")).Trim();

    double min = double.MinValue,
        max = double.MaxValue;
    try {
      ParameterMetaDataRepository.GetParameterRange(name, ref min, ref max, fw);
    } catch {

    }

    return new ParamRow(name, value, def, units, opts, desc, min, max) { Fav = favs.Contains(name) };
  }

  private ParamRow MakeRow(string name, double value, double? def) {
    var fw = _comPort.MAV.cs.firmware.ToString();
    var favs = Settings.Instance.GetList("fav_params").ToHashSet();
    return BuildRow(name, value, def, fw, favs);
  }

  private void LoadFrom(IEnumerable<ParamRow> rows) {
    void Apply() {
      _all.Clear();
      _all.AddRange(rows);
      HasDefaults = _all.Any(r => r.DefaultValue.HasValue);
      Sort();
      RebuildCategories();
      ApplyFilter();
    }
    if (Dispatcher.UIThread.CheckAccess()) {
      Apply();
    } else {
      Dispatcher.UIThread.Post(Apply);
    }
  }

  private void Sort() {
    _all.Sort(
        (a, b) => {
          if (a.Fav != b.Fav) {
            return a.Fav ? -1 : 1;
          }
          return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    );
  }

  private void RebuildCategories() {
    var prefixes = _all
        .Select(r => r.Prefix)
        .Where(p => p.Length > 0)
        .Distinct()
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();
    Categories.Clear();
    Categories.Add("All");
    foreach (var p in prefixes) {
      Categories.Add(p);
    }
    SelectedCategory = "All";
  }

  private void ApplyFilter() {
    var q = SearchText?.Trim() ?? "";
    var cat = SelectedCategory ?? "All";
    Params.Clear();
    foreach (var r in _all) {
      if (q.Length >= 2 && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }
      if (cat != "All" && r.Prefix != cat) {
        continue;
      }
      if (ShowModifiedOnly && !r.IsDirty) {
        continue;
      }
      if (ShowNonDefaultOnly && !r.IsNonDefault) {
        continue;
      }
      Params.Add(r);
    }
  }

  private static string Meta(string name, string key, string fw) {
    try {
      return ParameterMetaDataRepository.GetParameterMetaData(name, key, fw) ?? "";
    } catch {
      return "";
    }
  }

  private static bool TryEval(string? text, out double value) {
    value = 0;
    if (string.IsNullOrWhiteSpace(text)) {
      return false;
    }
    try {
      value = new org.mariuszgromada.math.mxparser.Expression(text).calculate();
      return !double.IsNaN(value) && !double.IsInfinity(value);
    } catch {
      return false;
    }
  }
}

internal readonly record struct ParamFileStageResult(int Matched, int Differing, int Protected);

internal sealed record ParameterTarget(MAVLinkInterface Link, byte SystemId, byte ComponentId);

internal readonly record struct TargetSnapshot(ParameterTarget? Target, long Revision);

internal sealed record FrameDefaultComparison(
    ParameterTarget Target,
    long TargetRevision,
    FrameDefaultFile Source,
    IReadOnlyList<ParamComparisonRow> Rows);

public sealed record FrameDefaultFile(string Name, string Path) {
  public override string ToString() {
    const string root = FrameDefaultCatalogService.CatalogRoot + "/";
    return Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        ? Path[root.Length..].Replace("/", " / ", StringComparison.Ordinal)
        : Name;
  }
}

public partial class ParamComparisonRow : ObservableObject {
  public ParamComparisonRow(string name, double currentValue, double fileValue) {
    Name = name;
    CurrentValue = currentValue;
    FileValue = fileValue;
  }

  public string Name { get; }
  public double CurrentValue { get; }
  public double FileValue { get; }
  public string CurrentText => CurrentValue.ToString(CultureInfo.InvariantCulture);
  public string FileText => FileValue.ToString(CultureInfo.InvariantCulture);

  [ObservableProperty]
  private bool _use = true;
}

public partial class ParamRow : ObservableObject {
  public ParamRow(
      string name,
      double current,
      double? def,
      string units,
      string options,
      string description,
      double min,
      double max
  ) {
    Name = name;
    _currentValue = current;
    _valueText = current.ToString(CultureInfo.InvariantCulture);
    DefaultValue = def;
    Units = units;
    Options = options;
    Description = description;
    Min = min;
    Max = max;
    var us = name.IndexOf('_');
    Prefix = us > 0 ? name.Substring(0, us) : name;
  }

  public string Name { get; }
  public string Prefix { get; }
  public string Units { get; }
  public string Options { get; }
  public string Description { get; }
  public double Min { get; }
  public double Max { get; }
  public double? DefaultValue { get; }

  public string DefaultText =>
      DefaultValue.HasValue ? DefaultValue.Value.ToString(CultureInfo.InvariantCulture) : "";

  [ObservableProperty]
  private double _currentValue;

  [ObservableProperty]
  private string _valueText;

  [ObservableProperty]
  private bool _fav;

  public bool IsDirty {
    get {
      if (double.TryParse(ValueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) {
        return v != CurrentValue;
      }
      return ValueText != CurrentValue.ToString(CultureInfo.InvariantCulture);
    }
  }

  public bool IsNonDefault => DefaultValue.HasValue && DefaultValue.Value != CurrentValue;

  partial void OnValueTextChanged(string value) => OnPropertyChanged(nameof(IsDirty));

  public void SetCurrent(double v) {
    CurrentValue = v;
    ValueText = v.ToString(CultureInfo.InvariantCulture);
    OnPropertyChanged(nameof(IsDirty));
    OnPropertyChanged(nameof(IsNonDefault));
  }
}
