using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class MapCacheViewModel : ViewModelBase {
  public MapCacheViewModel() {
    _ = RefreshAsync();
  }

  public ObservableCollection<MapCacheRow> Entries { get; } = new();

  [ObservableProperty]
  private MapCacheRow? _selectedEntry;

  [ObservableProperty]
  private string _status = "Scanning the map tile cache…";

  [ObservableProperty]
  private bool _busy;

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
