using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class LogDownloadRow : ObservableObject {
  public ushort Id { get; init; }
  public uint SizeBytes { get; init; }
  public DateTime TimeUtc { get; init; }

  public string SizeText =>
      SizeBytes >= 1024 * 1024 ? (SizeBytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
                               : (SizeBytes / 1024.0).ToString("0.0") + " KB";

  public string TimeText => TimeUtc == DateTime.MinValue ? "—"
                                                         : TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public partial class LogDownloadViewModel : ViewModelBase {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private static readonly DateTime _epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  public ObservableCollection<LogDownloadRow> Logs { get; } = new();

  [ObservableProperty]
  private LogDownloadRow? _selectedLog;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private bool _createKmlAfterDownload;

  [RelayCommand]
  [Obsolete]
  private async Task Refresh() {
    if (!AppState.IsConnected) {
      Status = "Not connected.";
      return;
    }
    if (IsBusy) {
      return;
    }

    IsBusy = true;
    Status = "Requesting log list…";
    Logs.Clear();
    try {
      var list = await Task.Run(() => _comPort.GetLogEntry());
      foreach (var e in list.Values.OrderBy(a => a.id)) {
        if (e.size == 0) {
          continue;
        }
        Logs.Add(new LogDownloadRow {
          Id = e.id,
          SizeBytes = e.size,
          TimeUtc = e.time_utc == 0 ? DateTime.MinValue : _epoch.AddSeconds(e.time_utc),
        });
      }
      Status = $"{Logs.Count} log(s) on board.";
    } catch (Exception ex) {
      Status = "List failed: " + ex.Message;
    } finally {
      IsBusy = false;
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task Download() {
    var sel = SelectedLog;
    if (sel == null) {
      Status = "Select a log first.";
      return;
    }
    if (!AppState.IsConnected) {
      Status = "Not connected.";
      return;
    }
    if (IsBusy) {
      return;
    }

    var dest = await PickSaveAsync($"log_{sel.Id}.bin");
    if (dest == null) {
      return;
    }

    await DownloadRows([sel], _ => dest);
  }

  [RelayCommand]
  [Obsolete]
  private async Task DownloadAll() {
    if (!AppState.IsConnected) {
      Status = "Not connected.";
      return;
    }
    if (IsBusy) {
      return;
    }
    if (Logs.Count == 0) {
      await Refresh();
      if (Logs.Count == 0) {
        return;
      }
    }

    string? folder = await PickFolderAsync();
    if (folder == null) {
      return;
    }
    Directory.CreateDirectory(folder);
    await DownloadRows(Logs.ToList(), row => Path.Combine(folder, SuggestedFileName(row)));
  }

  [RelayCommand]
  private async Task EraseAll() {
    if (!AppState.IsConnected) {
      Status = "Not connected.";
      return;
    }
    if (IsBusy || !await Services.Dialogs.Confirm("Erase onboard logs",
          "Erase every DataFlash log stored on the vehicle?\n\n"
          + "Download anything you need first. This operation cannot be undone.")) {
      return;
    }

    IsBusy = true;
    Status = "Erasing onboard logs…";
    try {
      await Task.Run(() => _comPort.EraseLog());
      Logs.Clear();
      SelectedLog = null;
      Progress = 0;
      Status = "All onboard logs were erased.";
    } catch (Exception ex) {
      Status = "Erase failed: " + ex.Message;
    } finally {
      IsBusy = false;
    }
  }

  private async Task DownloadRows(
      IReadOnlyList<LogDownloadRow> rows, Func<LogDownloadRow, string> destinationFor) {
    if (rows.Count == 0) {
      Status = "No logs to download.";
      return;
    }
    IsBusy = true;
    Progress = 0;
    long total = rows.Sum(row => (long)row.SizeBytes);
    long completed = 0;
    int saved = 0;
    var kmlErrors = new System.Collections.Generic.List<string>();

    void OnProgress(int offset, string _) {
      long current = Interlocked.Read(ref completed) + Math.Max(0, offset);
      double pct = total > 0 ? Math.Min(100.0, 100.0 * current / total) : 0;
      Dispatcher.UIThread.Post(() => Progress = pct);
    }

    _comPort.Progress += OnProgress;
    try {
      foreach (var row in rows) {
        string destination = destinationFor(row);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Status = $"Downloading log {row.Id} ({saved + 1}/{rows.Count})…";
        await DownloadOne(row.Id, destination);
        long completedNow = Interlocked.Add(ref completed, row.SizeBytes);
        saved++;
        Progress = total > 0 ? Math.Min(100.0, 100.0 * completedNow / total) : 100;
        if (CreateKmlAfterDownload) {
          try {
            string kml = Path.ChangeExtension(destination, ".kml");
            await Task.Run(() => DataFlashLog.ExportKml(destination, kml));
          } catch (Exception ex) {
            kmlErrors.Add($"log {row.Id}: {ex.Message}");
          }
        }
      }
      Progress = 100;
      Status = $"Downloaded {saved} log(s)."
               + (CreateKmlAfterDownload
                   ? kmlErrors.Count == 0
                       ? " KML track(s) created."
                       : $" KML failed for {kmlErrors.Count}: {string.Join("; ", kmlErrors)}"
                   : "");
    } catch (Exception ex) {
      Status = $"Download failed after {saved}/{rows.Count}: {ex.Message}";
    } finally {
      _comPort.Progress -= OnProgress;
      IsBusy = false;
    }
  }

  private async Task DownloadOne(ushort id, string destination) {
    string? tempPath = null;
    try {
      using var temp = await _comPort.GetLog(id);
      tempPath = temp.Name;
      temp.Position = 0;
      await using var output = new FileStream(
          destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
      await temp.CopyToAsync(output);
    } finally {
      if (tempPath != null) {
        try {
          File.Delete(tempPath);
        } catch {

        }
      }
    }
  }

  internal static string SuggestedFileName(LogDownloadRow row) =>
      row.TimeUtc == DateTime.MinValue
          ? $"log_{row.Id}.bin"
          : $"{row.TimeUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}_{row.Id}.bin";

  private static async Task<string?> PickSaveAsync(string suggested) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return null;
    }
    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save dataflash log",
      SuggestedFileName = suggested,
      DefaultExtension = "bin",
      FileTypeChoices = new[] {
        new FilePickerFileType("Dataflash log") { Patterns = new[] { "*.bin" } },
      },
    });
    return file?.TryGetLocalPath();
  }

  private static async Task<string?> PickFolderAsync() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return null;
    }
    var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Select folder for all DataFlash logs",
      AllowMultiple = false,
    });
    return folders.FirstOrDefault()?.TryGetLocalPath();
  }
}
