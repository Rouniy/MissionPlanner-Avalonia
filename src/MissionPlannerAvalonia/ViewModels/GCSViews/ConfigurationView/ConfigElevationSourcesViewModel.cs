using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public sealed partial class ConfigElevationSourcesViewModel : ViewModelBase, IDisposable {
  private CancellationTokenSource? _scanCancellation;
  private Task? _activeScan;
  private bool _disposed;

  [ObservableProperty]
  private string _directoryPath = ElevationSourceService.SavedDirectory;

  [ObservableProperty]
  private string _status = "Choose a directory containing local elevation or raster-map files.";

  [ObservableProperty]
  private string _nativeGdalStatus = "Checking the optional native GDAL raster backend…";

  [ObservableProperty]
  private int _progress;

  [ObservableProperty]
  private int _progressMaximum = 1;

  [ObservableProperty]
  private bool _isBusy;

  public ObservableCollection<ElevationSourceFile> Files { get; } = [];
  public ObservableCollection<NativeGdalRasterFile> RasterFiles { get; } = [];

  public ConfigElevationSourcesViewModel() {
    if (ElevationSourceService.LastResult is { } result) {
      ApplyElevationResult(result);
    }
    if (NativeGdalMapService.LastResult is { } rasterResult) {
      ApplyNativeGdalResult(rasterResult);
    } else {
      NativeGdalStatus = "Raster map: " + NativeGdalMapService.BackendStatus;
    }

    Task<ElevationScanResult>? elevationStartup = ElevationSourceService.LastResult == null
        ? ElevationSourceService.StartupTask
        : null;
    Task<NativeGdalScanResult>? nativeStartup = NativeGdalMapService.LastResult == null
        ? NativeGdalMapService.StartupTask
        : null;
    if (elevationStartup != null || nativeStartup != null) {
      IsBusy = elevationStartup is { IsCompleted: false }
          || nativeStartup is { IsCompleted: false };
      if (IsBusy) {
        Status = "Restoring the saved local elevation and raster directory in the background…";
      }
      _activeScan = ObserveStartupAsync(elevationStartup, nativeStartup);
    }
  }

  public async Task SelectAndScanAsync(string directory) {
    if (IsBusy) {
      return;
    }
    string normalized = ElevationSourceService.SaveDirectory(directory);
    DirectoryPath = normalized;
    if (ElevationSourceService.RequiresRestartToSwitch(normalized)) {
      Status = "The new elevation directory is saved. Restart Mission Planner to unload the " +
               "currently active DEM index and switch sources safely.";
      return;
    }
    await ScanDirectoryAsync(normalized);
  }

  [RelayCommand]
  private async Task RescanAsync() {
    if (IsBusy) {
      return;
    }
    try {
      string normalized = ElevationSourceService.SaveDirectory(DirectoryPath);
      DirectoryPath = normalized;
      if (ElevationSourceService.RequiresRestartToSwitch(normalized)) {
        Status = "The new elevation directory is saved. Restart Mission Planner to unload the " +
                 "currently active DEM index and switch sources safely.";
        return;
      }
      await ScanDirectoryAsync(normalized);
    } catch (Exception ex) {
      Status = "Local-source scan failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private void Cancel() {
    if (_scanCancellation == null) {
      return;
    }
    Status = "Cancellation requested; finishing the current file…";
    _scanCancellation.Cancel();
  }

  [RelayCommand]
  private async Task ClearSavedAsync() {
    if (IsBusy || !await Dialogs.Confirm(
            "Clear Elevation Directory",
            "Stop loading this local GeoTIFF/DTED directory on future starts? " +
            "Files already indexed remain active until Mission Planner is restarted.")) {
      return;
    }
    ElevationSourceService.ClearSavedDirectory();
    NativeGdalMapService.Unload();
    DirectoryPath = "";
    RasterFiles.Clear();
    NativeGdalStatus = "Raster map unloaded. " + NativeGdalMapService.BackendStatus;
    Status = "Saved elevation directory cleared. Restart to unload files already indexed in this session.";
  }

  private async Task ScanDirectoryAsync(string directory) {
    ThrowIfDisposed();
    Progress = 0;
    ProgressMaximum = 1;
    IsBusy = true;
    Status = "Discovering local elevation and GDAL raster-map files…";
    NativeGdalStatus = "Scanning the directory with the optional native GDAL backend…";
    var cancellation = new CancellationTokenSource();
    _scanCancellation = cancellation;
    int elevationCompleted = 0;
    int elevationTotal = 0;
    int rasterCompleted = 0;
    int rasterTotal = 0;
    void UpdateProgress() {
      ProgressMaximum = Math.Max(1, elevationTotal + rasterTotal);
      Progress = elevationCompleted + rasterCompleted;
    }
    var elevationProgress = new Progress<ElevationScanProgress>(item => {
      if (_disposed) {
        return;
      }
      elevationCompleted = item.Completed;
      elevationTotal = item.Total;
      UpdateProgress();
      Status = item.Completed >= item.Total
          ? "Finishing elevation index…"
          : $"Indexing {item.Completed + 1}/{item.Total}: {Path.GetFileName(item.CurrentFile)}";
    });
    var rasterProgress = new Progress<NativeGdalScanProgress>(item => {
      if (_disposed) {
        return;
      }
      rasterCompleted = item.Completed;
      rasterTotal = item.Total;
      UpdateProgress();
      NativeGdalStatus = item.Completed >= item.Total
          ? "Finishing native GDAL raster index…"
          : $"GDAL {item.Completed + 1}/{item.Total}: {Path.GetFileName(item.CurrentFile)}";
    });

    Task<ElevationScanResult> elevationOperation = ElevationSourceService.ScanAsync(
        directory, elevationProgress, cancellation.Token);
    Task<NativeGdalScanResult> rasterOperation = NativeGdalMapService.ScanAsync(
        directory, rasterProgress, cancellation.Token);
    Task operation = Task.WhenAll(elevationOperation, rasterOperation);
    _activeScan = operation;
    try {
      await operation;
      ApplyElevationResult(await elevationOperation);
      ApplyNativeGdalResult(await rasterOperation);
      if (string.Equals(
              MissionPlanner.Utilities.Settings.Instance["MapType"],
              NativeGdalMapService.MapType,
              StringComparison.Ordinal)) {
        MapTileSourceFactory.RefreshMapType(NativeGdalMapService.MapType);
      }
    } catch (OperationCanceledException) {
      Status = "Local-source indexing cancelled. The previously complete indexes remain active.";
      NativeGdalStatus = "Native GDAL raster indexing cancelled.";
    } catch (Exception ex) {
      Status = "Local-source scan failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_scanCancellation, cancellation)) {
        _scanCancellation = null;
        _activeScan = null;
      }
      cancellation.Dispose();
      if (!_disposed) {
        IsBusy = false;
      }
    }
  }

  private async Task ObserveStartupAsync(
      Task<ElevationScanResult>? elevationStartup,
      Task<NativeGdalScanResult>? nativeStartup) {
    try {
      if (elevationStartup != null) {
        try {
          ElevationScanResult result = await elevationStartup;
          if (!_disposed) {
            ApplyElevationResult(result);
          }
        } catch (Exception ex) {
          if (!_disposed) {
            Status = "Saved elevation directory failed to load: " + ex.Message;
          }
        }
      }
      if (nativeStartup != null) {
        try {
          NativeGdalScanResult result = await nativeStartup;
          if (!_disposed) {
            ApplyNativeGdalResult(result);
          }
        } catch (Exception ex) {
          if (!_disposed) {
            NativeGdalStatus = "Saved native raster directory failed to load: " + ex.Message;
          }
        }
      }
    } finally {
      if (!_disposed) {
        IsBusy = false;
        _activeScan = null;
      }
    }
  }

  private void ApplyElevationResult(ElevationScanResult result) {
    if (!Dispatcher.UIThread.CheckAccess()) {
      Dispatcher.UIThread.Post(() => ApplyElevationResult(result));
      return;
    }
    DirectoryPath = result.Directory;
    Files.Clear();
    foreach (ElevationSourceFile file in result.Files) {
      Files.Add(file);
    }
    ProgressMaximum = Math.Max(1, result.Files.Count);
    Progress = result.Files.Count;
    Status = result.Files.Count == 0
        ? "No .tif, .tiff, .dt0, .dt1 or .dt2 files were found in this directory."
        : $"Indexed {result.IndexedCount}/{result.Files.Count} file(s): " +
          $"{result.GeoTiffCount} GeoTIFF, {result.DtedCount} DTED" +
          (result.ErrorCount == 0 ? "." : $"; {result.ErrorCount} error(s).") +
          " Local DEM data takes priority over downloaded SRTM.";
  }

  private void ApplyNativeGdalResult(NativeGdalScanResult result) {
    if (!Dispatcher.UIThread.CheckAccess()) {
      Dispatcher.UIThread.Post(() => ApplyNativeGdalResult(result));
      return;
    }
    DirectoryPath = result.Directory;
    RasterFiles.Clear();
    foreach (NativeGdalRasterFile file in result.Files) {
      RasterFiles.Add(file);
    }
    string ignored = result.UnrecognizedFiles == 0
        ? ""
        : $"; {result.UnrecognizedFiles} unrecognized file(s) ignored";
    NativeGdalStatus = !NativeGdalMapService.IsAvailable
        ? result.Backend
        : result.IndexedCount == 0
        ? result.Backend + (result.ExaminedFiles == 0
            ? ". No candidate files of at least 1 KiB were found."
            : $". No georeferenced raster was indexed{ignored}.")
        : $"{result.Backend}. Indexed {result.IndexedCount}/{result.Files.Count} raster(s)" +
          (result.ErrorCount == 0 ? ignored + "." :
              $"; {result.ErrorCount} error(s){ignored}.") +
          " Select GDAL Custom in Flight Planner to overlay them on satellite imagery.";
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _scanCancellation?.Cancel();
    // The running operation owns and disposes the source in its finally block. Disposing it here
    // can race a progress callback or cancellation check on the background indexer.
  }

  private void ThrowIfDisposed() {
    ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
