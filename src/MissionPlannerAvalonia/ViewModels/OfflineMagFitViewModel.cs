using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed partial class OfflineMagFitViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _comPort;
  private CancellationTokenSource? _cancellation;
  private Task? _activeOperation;
  private bool _disposed;

  [ObservableProperty]
  private string _sourcePath = "";

  [ObservableProperty]
  private decimal _throttleThreshold = 30;

  [ObservableProperty]
  private bool _useEllipsoid = true;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  private string _status =
      "Choose a .tlog, .bin or .log file. Analysis is offline; applying values is a separate confirmed action.";

  [ObservableProperty]
  private bool _canApply;

  public ObservableCollection<OfflineMagFitResult> Results { get; } = [];

  public OfflineMagFitViewModel(string? sourcePath = null) : this(AppState.comPort, sourcePath) {
  }

  internal OfflineMagFitViewModel(MAVLinkInterface comPort, string? sourcePath = null) {
    _comPort = comPort;
    if (!string.IsNullOrWhiteSpace(sourcePath)) {
      _sourcePath = Path.GetFullPath(sourcePath);
    }
  }

  public bool HasInitialSource => SourcePath.Length > 0;

  partial void OnSourcePathChanged(string value) => InvalidateResults("The selected log changed; analyze it again.");

  partial void OnThrottleThresholdChanged(decimal value) =>
      InvalidateResults("The throttle threshold changed; analyze the log again.");

  partial void OnUseEllipsoidChanged(bool value) =>
      InvalidateResults("The fit model changed; analyze the log again.");

  [RelayCommand]
  private async Task Browse() {
    ThrowIfDisposed();
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    var files = await Dialogs.Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select log for offline magnetometer calibration",
      AllowMultiple = false,
      FileTypeFilter = [
        new FilePickerFileType("Flight logs") { Patterns = ["*.tlog", "*.bin", "*.log"] },
        new FilePickerFileType("All files") { Patterns = ["*"] },
      ],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      SourcePath = path;
      Status = "Ready to analyze " + Path.GetFileName(path) + ".";
    }
  }

  [RelayCommand]
  public async Task AnalyzeAsync() {
    ThrowIfDisposed();
    if (IsBusy) {
      return;
    }
    if (string.IsNullOrWhiteSpace(SourcePath)) {
      Status = "Choose a flight log first.";
      return;
    }

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;
    IsBusy = true;
    CanApply = false;
    Progress = 0;
    Status = "Reading magnetometer samples…";
    var progress = new Progress<double>(value => {
      Progress = Math.Clamp(value * 100, 0, 100);
      Status = value < 0.72 ? "Reading magnetometer samples…" : "Fitting calibration model…";
    });
    Task<OfflineMagFitReport> operation = OfflineMagFitService.AnalyzeAsync(
        SourcePath,
        (int)Math.Clamp(ThrottleThreshold, 0, 100),
        UseEllipsoid,
        progress,
        cancellation.Token);
    _activeOperation = operation;
    try {
      OfflineMagFitReport report = await operation;
      Results.Clear();
      foreach (OfflineMagFitResult result in report.Results) {
        Results.Add(result);
      }
      Progress = 100;
      CanApply = Results.Count > 0;
      string weakCoverage = Results.Any(result => result.CoverageOctants < 8)
          ? " Warning: at least one compass does not cover all eight octants."
          : "";
      Status = $"Fitted {Results.Count} compass{(Results.Count == 1 ? "" : "es")} from "
          + $"{Path.GetFileName(report.SourcePath)}. Review every value before applying.{weakCoverage}";
    } catch (OperationCanceledException) {
      Status = "Offline MagFit cancelled.";
    } catch (Exception ex) {
      Status = "Offline MagFit failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_cancellation, cancellation)) {
        _cancellation = null;
        _activeOperation = null;
      }
      cancellation.Dispose();
      IsBusy = false;
    }
  }

  [RelayCommand]
  private void Cancel() {
    if (_cancellation == null) {
      return;
    }
    Status = "Cancellation requested…";
    _cancellation.Cancel();
  }

  [RelayCommand]
  private async Task Apply() {
    ThrowIfDisposed();
    if (IsBusy || Results.Count == 0) {
      return;
    }
    if (_comPort.BaseStream?.IsOpen != true) {
      await Dialogs.Alert("Offline MagFit", "Connect to the target vehicle before applying calibration values.");
      return;
    }
    if (_comPort.MAV.cs.armed) {
      await Dialogs.Alert("Offline MagFit", "Calibration values cannot be applied while the vehicle is armed.");
      return;
    }

    IReadOnlyList<(string Name, double Value)> writes;
    try {
      writes = BuildWrites(Results, _comPort);
    } catch (Exception ex) {
      await Dialogs.Alert("Offline MagFit", ex.Message);
      return;
    }
    string summary = string.Join("\n", Results.Select(result =>
        $"Compass {result.Compass}: offsets {result.Offsets}; RMS {result.RmsError:0.00}; "
        + $"coverage {result.CoverageOctants}/8"));
    if (!await Dialogs.ConfirmDangerous(
            "Apply offline magnetometer calibration?",
            "This writes COMPASS_OFS/DIA/ODI parameters to the connected, disarmed vehicle and disables "
            + "COMPASS_LEARN. A poor log can make heading unreliable.\n\n" + summary,
            "Apply calibration")) {
      Status = "Calibration values were not applied.";
      return;
    }

    IsBusy = true;
    CanApply = false;
    Status = "Writing calibration parameters…";
    Task? writeOperation = null;
    try {
      writeOperation = Task.Run(() => ApplyWrites(_comPort, writes));
      _activeOperation = writeOperation;
      await writeOperation;
      Status = $"Applied {writes.Count} calibration parameter values. Reboot the autopilot and verify heading.";
    } catch (Exception ex) {
      Status = "Applying calibration failed: " + ex.Message
          + " Refresh parameters before retrying; some earlier writes may already have succeeded.";
    } finally {
      if (ReferenceEquals(_activeOperation, writeOperation)) {
        _activeOperation = null;
      }
      IsBusy = false;
      CanApply = Results.Count > 0;
    }
  }

  internal static IReadOnlyList<(string Name, double Value)> BuildWrites(
      IEnumerable<OfflineMagFitResult> results, MAVLinkInterface comPort) {
    var writes = new List<(string, double)>();
    if (comPort.MAV.param.ContainsKey("COMPASS_LEARN")) {
      writes.Add(("COMPASS_LEARN", 0));
    }
    foreach (OfflineMagFitResult result in results.OrderBy(item => item.Compass)) {
      string suffix = result.Compass == 1 ? "" : result.Compass.ToString(CultureInfo.InvariantCulture);
      AddRequired(writes, comPort, $"COMPASS_OFS{suffix}_X", result.Offsets.X);
      AddRequired(writes, comPort, $"COMPASS_OFS{suffix}_Y", result.Offsets.Y);
      AddRequired(writes, comPort, $"COMPASS_OFS{suffix}_Z", result.Offsets.Z);

      string diaX = $"COMPASS_DIA{suffix}_X";
      if (result.HasEllipsoid && comPort.MAV.param.ContainsKey(diaX)) {
        AddRequired(writes, comPort, diaX, result.Diagonals.X);
        AddRequired(writes, comPort, $"COMPASS_DIA{suffix}_Y", result.Diagonals.Y);
        AddRequired(writes, comPort, $"COMPASS_DIA{suffix}_Z", result.Diagonals.Z);
        AddRequired(writes, comPort, $"COMPASS_ODI{suffix}_X", result.OffDiagonals.X);
        AddRequired(writes, comPort, $"COMPASS_ODI{suffix}_Y", result.OffDiagonals.Y);
        AddRequired(writes, comPort, $"COMPASS_ODI{suffix}_Z", result.OffDiagonals.Z);
      }
    }
    return writes;
  }

  private static void AddRequired(List<(string, double)> writes, MAVLinkInterface comPort,
      string name, double value) {
    if (!double.IsFinite(value)) {
      throw new InvalidDataException($"Calibration value for {name} is not finite.");
    }
    if (!comPort.MAV.param.ContainsKey(name)) {
      throw new InvalidOperationException(
          $"The connected vehicle does not expose required parameter {name}; nothing was written.");
    }
    writes.Add((name, value));
  }

  private static void ApplyWrites(
      MAVLinkInterface comPort, IReadOnlyList<(string Name, double Value)> writes) {
    if (comPort.BaseStream?.IsOpen != true) {
      throw new InvalidOperationException("The vehicle disconnected before parameters were written.");
    }
    if (comPort.MAV.cs.armed) {
      throw new InvalidOperationException("The vehicle became armed; parameter writing was stopped.");
    }
    foreach ((string name, double value) in writes) {
      if (comPort.MAV.cs.armed) {
        throw new InvalidOperationException("The vehicle became armed; parameter writing was stopped.");
      }
      bool accepted = comPort.setParam(
          (byte)comPort.sysidcurrent, (byte)comPort.compidcurrent, name, value);
      if (!accepted) {
        throw new IOException($"The vehicle rejected {name}.");
      }
    }
  }

  public async Task CancelAndWaitAsync() {
    _cancellation?.Cancel();
    Task? operation = _activeOperation;
    if (operation != null) {
      try {
        await operation;
      } catch (OperationCanceledException) {
      } catch {
        // AnalyzeAsync owns the user-facing error state.
      }
    }
  }

  public bool SetSource(string path) {
    ThrowIfDisposed();
    if (IsBusy) {
      Status = "Finish or cancel the current MagFit operation before opening another log.";
      return false;
    }
    SourcePath = Path.GetFullPath(path);
    Status = "Ready to analyze " + Path.GetFileName(SourcePath) + ".";
    return true;
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _cancellation?.Cancel();
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private void InvalidateResults(string status) {
    if (IsBusy || Results.Count == 0) {
      return;
    }
    Results.Clear();
    CanApply = false;
    Progress = 0;
    Status = status;
  }
}
