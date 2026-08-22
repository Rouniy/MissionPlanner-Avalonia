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
  private string _status = "Choose a directory containing GeoTIFF or DTED elevation files.";

  [ObservableProperty]
  private int _progress;

  [ObservableProperty]
  private int _progressMaximum = 1;

  [ObservableProperty]
  private bool _isBusy;

  public ObservableCollection<ElevationSourceFile> Files { get; } = [];

  public ConfigElevationSourcesViewModel() {
    if (ElevationSourceService.LastResult is { } result) {
      ApplyResult(result);
    } else if (ElevationSourceService.StartupTask is { IsCompleted: false } startup) {
      IsBusy = true;
      Status = "Restoring the saved elevation directory in the background…";
      _activeScan = ObserveStartupAsync(startup);
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
      Status = "Elevation scan failed: " + ex.Message;
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
    DirectoryPath = "";
    Status = "Saved elevation directory cleared. Restart to unload files already indexed in this session.";
  }

  private async Task ScanDirectoryAsync(string directory) {
    ThrowIfDisposed();
    Progress = 0;
    ProgressMaximum = 1;
    IsBusy = true;
    Status = "Discovering GeoTIFF and DTED files…";
    var cancellation = new CancellationTokenSource();
    _scanCancellation = cancellation;
    var progress = new Progress<ElevationScanProgress>(item => {
      if (_disposed) {
        return;
      }
      ProgressMaximum = Math.Max(1, item.Total);
      Progress = item.Completed;
      Status = item.Completed >= item.Total
          ? "Finishing elevation index…"
          : $"Indexing {item.Completed + 1}/{item.Total}: {Path.GetFileName(item.CurrentFile)}";
    });

    Task<ElevationScanResult> operation = ElevationSourceService.ScanAsync(
        directory, progress, cancellation.Token);
    _activeScan = operation;
    try {
      ElevationScanResult result = await operation;
      ApplyResult(result);
    } catch (OperationCanceledException) {
      Status = "Elevation indexing cancelled. Files completed before cancellation remain active.";
    } catch (Exception ex) {
      Status = "Elevation scan failed: " + ex.Message;
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

  private async Task ObserveStartupAsync(Task<ElevationScanResult> startup) {
    try {
      ElevationScanResult result = await startup;
      if (!_disposed) {
        ApplyResult(result);
      }
    } catch (Exception ex) {
      if (!_disposed) {
        Status = "Saved elevation directory failed to load: " + ex.Message;
      }
    } finally {
      if (!_disposed) {
        IsBusy = false;
        _activeScan = null;
      }
    }
  }

  private void ApplyResult(ElevationScanResult result) {
    if (!Dispatcher.UIThread.CheckAccess()) {
      Dispatcher.UIThread.Post(() => ApplyResult(result));
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
