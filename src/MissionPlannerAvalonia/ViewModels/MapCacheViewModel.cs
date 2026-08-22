using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class MapCacheViewModel : ViewModelBase, IDisposable {
  private CancellationTokenSource? _importCancellation;

  public MapCacheViewModel() {
    foreach (string mapType in MapTileSourceFactory.BuiltInMapTypes) {
      ImportMapTypes.Add(mapType);
    }
    string current = MapTileSourceFactory.CurrentMapType;
    if (!ImportMapTypes.Contains(current)) {
      ImportMapTypes.Add(current);
    }
    SelectedImportMapType = current;
    _ = RefreshAsync();
  }

  public ObservableCollection<MapCacheRow> Entries { get; } = new();
  public ObservableCollection<string> ImportMapTypes { get; } = new();

  [ObservableProperty]
  private string _selectedImportMapType = "GoogleSatelliteMap";

  [ObservableProperty]
  private MapCacheRow? _selectedEntry;

  [ObservableProperty]
  private string _status = "Scanning the map tile cache…";

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private bool _importing;

  [RelayCommand]
  private async Task ImportTilesAsync() {
    if (Busy) {
      return;
    }
    var owner = Dialogs.Owner;
    if (owner?.StorageProvider == null) {
      Status = "No window is available for selecting a tile directory.";
      return;
    }

    var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Import Mission Planner Z/row/column map tiles",
      AllowMultiple = false,
    });
    string? source = folders.FirstOrDefault()?.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(source)) {
      return;
    }

    string provider = MapTileSourceFactory.NormalizeMapType(SelectedImportMapType);
    if (!await Dialogs.Confirm(
            "Import Map Tiles",
            $"Import JPEG/PNG tiles under Z<zoom>/<row>/<column> from '{source}' "
            + $"into the persistent '{provider}' cache? Existing coordinates will be replaced.")) {
      return;
    }

    Busy = true;
    Importing = true;
    var cancellation = new CancellationTokenSource();
    _importCancellation = cancellation;
    MapTileImportResult result;
    try {
      var progress = new Progress<MapTileImportProgress>(value => {
        Status = $"Importing {provider}: scanned {value.Discovered:N0}, "
            + $"imported {value.Imported:N0}, skipped {value.Skipped:N0}, "
            + $"failed {value.Failed:N0}…";
      });
      result = await MapTileImporter.ImportAsync(
          source, provider, progress, cancellation.Token);
    } catch (OperationCanceledException) {
      Status = "Map tile import cancelled. Tiles already imported remain in the cache.";
      return;
    } catch (Exception ex) {
      Status = "Map tile import failed: " + ex.Message;
      return;
    } finally {
      if (ReferenceEquals(_importCancellation, cancellation)) {
        _importCancellation = null;
      }
      cancellation.Dispose();
      Importing = false;
      Busy = false;
    }

    await RefreshAsync();
    Status = $"Imported {result.Imported:N0} of {result.Discovered:N0} image files "
        + $"({MapCacheManager.FormatBytes(result.ImportedBytes)}) into {provider}; "
        + $"skipped {result.Skipped:N0}, failed {result.Failed:N0}.";
    if (MapTileSourceFactory.CurrentMapType == provider) {
      MapTileSourceFactory.RefreshMapType(provider);
    }
  }

  [RelayCommand]
  private void CancelImport() => _importCancellation?.Cancel();

  [RelayCommand]
  private async Task RefreshAsync() {
    if (Busy) {
      return;
    }
    Busy = true;
    try {
      var snapshots = await Task.Run(() => MapCacheManager.Scan());
      string? selectedPath = SelectedEntry?.Snapshot.Path;
      Entries.Clear();
      foreach (var snapshot in snapshots) {
        Entries.Add(new MapCacheRow(snapshot));
      }
      SelectedEntry = Entries.FirstOrDefault(item => item.Snapshot.Path == selectedPath)
          ?? Entries.LastOrDefault();
      var total = snapshots.Last();
      Status = $"{total.FileCount:N0} cached tiles/files, "
          + $"{MapCacheManager.FormatBytes(total.SizeBytes)} in {AppPaths.MapTileCacheRoot}.";
    } catch (Exception ex) {
      Status = "Map cache scan failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  [RelayCommand]
  private async Task RemoveOldAsync() {
    var selected = SelectedEntry;
    if (selected == null || Busy || !await Dialogs.Confirm(
            "Clean Map Cache",
            $"Remove cached map tiles older than 30 days from '{selected.Name}'?")) {
      return;
    }
    await DeleteAsync(selected, DateTime.UtcNow.AddDays(-30));
  }

  [RelayCommand]
  private async Task RemoveAllAsync() {
    var selected = SelectedEntry;
    if (selected == null || Busy || !await Dialogs.Confirm(
            "Clear Map Cache",
            $"Permanently remove all cached map tiles from '{selected.Name}'? "
            + "Offline maps in this selection will no longer be available.")) {
      return;
    }
    await DeleteAsync(selected, DateTime.MaxValue);
  }

  private async Task DeleteAsync(MapCacheRow selected, DateTime cutoffUtc) {
    Busy = true;
    string resultStatus;
    try {
      var result = await Task.Run(() =>
          MapCacheManager.DeleteOlderThan(selected.Snapshot, cutoffUtc));
      resultStatus = $"Removed {result.RemovedFiles:N0} files and freed "
          + MapCacheManager.FormatBytes(result.FreedBytes)
          + (result.FailedFiles == 0 ? "." : $"; {result.FailedFiles:N0} files could not be removed.");
    } catch (Exception ex) {
      resultStatus = "Map cache cleanup failed: " + ex.Message;
    } finally {
      Busy = false;
    }
    await RefreshAsync();
    Status = resultStatus;
  }

  public void Dispose() {
    _importCancellation?.Cancel();
  }
}

public sealed class MapCacheRow {
  internal MapCacheRow(MapCacheSnapshot snapshot) => Snapshot = snapshot;

  internal MapCacheSnapshot Snapshot { get; }
  public string Name => Snapshot.Name;
  public string Size => MapCacheManager.FormatBytes(Snapshot.SizeBytes);
  public string Files => Snapshot.FileCount.ToString("N0", CultureInfo.CurrentCulture);
  public string LastWrite => Snapshot.LastWriteUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
  public string Path => Snapshot.Path;
}
