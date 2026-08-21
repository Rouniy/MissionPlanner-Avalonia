using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed class LogIndexRow : IDisposable {
  private readonly LogIndexEntry _entry;

  internal LogIndexRow(LogIndexEntry entry) {
    using var stream = new MemoryStream(entry.ThumbnailJpeg, writable: false);
    Thumbnail = Bitmap.DecodeToWidth(stream, 240);
    _entry = entry with { ThumbnailJpeg = [] };
  }

  internal LogIndexEntry Entry => _entry;
  public Bitmap Thumbnail { get; }
  public string FullPath => _entry.FullPath;
  public string Name => _entry.Name;
  public string Directory => _entry.Directory;
  public string DateText => _entry.Date == DateTime.MinValue
      ? _entry.SourceLastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
      : _entry.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
  public DateTime DateSort => _entry.Date == DateTime.MinValue
      ? _entry.SourceLastWriteUtc
      : _entry.Date.ToUniversalTime();
  public string Frame => _entry.Frame;
  public string SystemIdText => _entry.SystemId == 0 ? "—" : _entry.SystemId.ToString(CultureInfo.InvariantCulture);
  public int SystemIdSort => _entry.SystemId;
  public string DurationText => FormatDuration(_entry.Duration);
  public double DurationSeconds => _entry.Duration.TotalSeconds;
  public string SizeText => FormatSize(_entry.SizeBytes);
  public long SizeBytes => _entry.SizeBytes;
  public string HomeText => _entry.Home?.ToString() ?? "—";
  public string TimeInAirText => FormatDuration(_entry.TimeInAir);
  public double TimeInAirSeconds => _entry.TimeInAir.TotalSeconds;
  public string DistanceText => _entry.DistanceMeters >= 1000
      ? $"{_entry.DistanceMeters / 1000:0.00} km"
      : $"{_entry.DistanceMeters:0} m";
  public double DistanceMeters => _entry.DistanceMeters;
  public string CameraMessagesText => _entry.CameraMessages.ToString(CultureInfo.InvariantCulture);
  public int CameraMessages => _entry.CameraMessages;
  public string ErrorText => _entry.Error ?? "";

  public void Dispose() => Thumbnail.Dispose();

  internal static string FormatDuration(TimeSpan value) =>
      value <= TimeSpan.Zero ? "00:00:00" :
      value.TotalDays >= 1 ? value.ToString("d'.'hh':'mm':'ss", CultureInfo.InvariantCulture) :
      value.ToString("hh':'mm':'ss", CultureInfo.InvariantCulture);

  private static string FormatSize(long bytes) {
    string[] units = ["B", "KiB", "MiB", "GiB"];
    double value = Math.Max(0, bytes);
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1) {
      value /= 1024;
      unit++;
    }
    return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
  }
}

public sealed partial class LogIndexViewModel : ViewModelBase, IDisposable, IAsyncDisposable {
  private readonly ILogIndexService _service;
  private readonly Func<string, string, string, Task<bool>> _confirmDangerous;
  private CancellationTokenSource? _operationCancellation;
  private Task? _activeOperation;
  private bool _disposed;

  [ObservableProperty]
  private string _rootDirectory;

  [ObservableProperty]
  private string _status = "Choose a log directory or refresh the default one.";

  [ObservableProperty]
  private int _progress;

  [ObservableProperty]
  private int _progressMaximum = 1;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private string _selectedSummary = "No logs selected.";

  public ObservableCollection<LogIndexRow> Rows { get; } = [];

  public LogIndexViewModel(string rootDirectory) : this(
      rootDirectory, new LogIndexService(), Dialogs.ConfirmDangerous) {
  }

  internal LogIndexViewModel(string rootDirectory, ILogIndexService service,
      Func<string, string, string, Task<bool>> confirmDangerous) {
    _rootDirectory = rootDirectory;
    _service = service;
    _confirmDangerous = confirmDangerous;
  }

  public Task RefreshAsync() => ScanAsync(RootDirectory);

  public async Task ScanAsync(string root) {
    ThrowIfDisposed();
    if (IsBusy) {
      return;
    }

    Progress = 0;
    ProgressMaximum = 1;
    IsBusy = true;
    Status = "Discovering flight logs…";
    var cancellation = new CancellationTokenSource();
    _operationCancellation = cancellation;
    var progress = new Progress<LogIndexProgress>(item => {
      ProgressMaximum = Math.Max(1, item.Total);
      Progress = item.Completed;
      Status = $"Indexing {item.Completed}/{item.Total}: {Path.GetFileName(item.CurrentFile)}";
    });

    Task<IReadOnlyList<LogIndexEntry>> operation =
        _service.ScanAsync(root, progress, cancellation.Token);
    _activeOperation = operation;
    try {
      IReadOnlyList<LogIndexEntry> entries = await operation;
      RootDirectory = root;
      ReplaceRows(entries);
      ProgressMaximum = Math.Max(1, entries.Count);
      Progress = entries.Count;
      int errors = entries.Count(item => !string.IsNullOrWhiteSpace(item.Error));
      Status = $"Indexed {entries.Count} log{(entries.Count == 1 ? "" : "s")}" +
               (errors == 0 ? "." : $"; {errors} could only be read partially.");
    } catch (OperationCanceledException) {
      Status = "Log indexing cancelled.";
    } catch (Exception ex) {
      Status = "Log indexing failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_operationCancellation, cancellation)) {
        _operationCancellation = null;
        _activeOperation = null;
      }
      cancellation.Dispose();
      IsBusy = false;
    }
  }

  public void UpdateSelection(IEnumerable<LogIndexRow> selected) {
    LogIndexRow[] rows = selected.Distinct().ToArray();
    if (rows.Length == 0) {
      SelectedSummary = "No logs selected.";
      return;
    }
    TimeSpan air = TimeSpan.FromTicks(rows.Sum(row => row.Entry.TimeInAir.Ticks));
    double distance = rows.Sum(row => row.Entry.DistanceMeters);
    SelectedSummary = $"Selected: {rows.Length}    Time in air: {LogIndexRow.FormatDuration(air)}    " +
                      $"Distance: {(distance >= 1000 ? $"{distance / 1000:0.00} km" : $"{distance:0} m")}";
  }

  public async Task DeleteSelectedAsync(IReadOnlyList<LogIndexRow> selected) {
    ThrowIfDisposed();
    if (IsBusy || selected.Count == 0) {
      return;
    }
    string names = string.Join(", ", selected.Take(4).Select(row => row.Name));
    if (selected.Count > 4) {
      names += $", and {selected.Count - 4} more";
    }
    if (!await _confirmDangerous(
            "Delete indexed flight logs?",
            $"Permanently delete {selected.Count} selected log(s) and their exact thumbnail/paired .rlog files?\n\n{names}",
            "Delete permanently")) {
      return;
    }

    IsBusy = true;
    Status = "Deleting selected logs…";
    var cancellation = new CancellationTokenSource();
    _operationCancellation = cancellation;
    Task<LogIndexDeleteResult> operation =
        _service.DeleteAsync(RootDirectory, selected.Select(row => row.Entry).ToArray(), cancellation.Token);
    _activeOperation = operation;
    try {
      LogIndexDeleteResult result = await operation;
      var deleted = result.DeletedLogs.ToHashSet(PathComparer);
      var removed = Rows.Where(row => deleted.Contains(row.FullPath)).ToArray();
      foreach (LogIndexRow row in removed) {
        Rows.Remove(row);
      }
      DisposeRowsDeferred(removed);
      UpdateSelection([]);
      Status = $"Deleted {result.DeletedLogs.Count} log(s)." +
               (result.Errors.Count == 0 ? "" : " " + string.Join(" ", result.Errors));
    } catch (OperationCanceledException) {
      Status = "Log deletion cancelled.";
    } catch (Exception ex) {
      Status = "Log deletion failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_operationCancellation, cancellation)) {
        _operationCancellation = null;
        _activeOperation = null;
      }
      cancellation.Dispose();
      IsBusy = false;
    }
  }

  public void Cancel() {
    if (_operationCancellation == null) {
      return;
    }
    Status = "Cancellation requested; finishing the current read…";
    _operationCancellation.Cancel();
  }

  public async Task CancelAndWaitAsync() {
    _operationCancellation?.Cancel();
    Task? operation = _activeOperation;
    if (operation != null) {
      try {
        await operation;
      } catch (OperationCanceledException) {
      } catch {
        // The operation method reports its own user-facing failure state.
      }
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _operationCancellation?.Cancel();
    ClearRows();
  }

  public async ValueTask DisposeAsync() {
    await CancelAndWaitAsync();
    Dispose();
  }

  private void ReplaceRows(IReadOnlyList<LogIndexEntry> entries) {
    if (_disposed) {
      return;
    }
    var replacements = new List<LogIndexRow>(entries.Count);
    try {
      replacements.AddRange(entries.Select(entry => new LogIndexRow(entry)));
    } catch {
      foreach (LogIndexRow row in replacements) {
        row.Dispose();
      }
      throw;
    }
    if (_disposed) {
      DisposeRowsDeferred(replacements);
      return;
    }
    ClearRows();
    foreach (LogIndexRow row in replacements) {
      Rows.Add(row);
    }
    UpdateSelection([]);
  }

  private void ClearRows() {
    LogIndexRow[] removed = Rows.ToArray();
    Rows.Clear();
    DisposeRowsDeferred(removed);
  }

  private static void DisposeRowsDeferred(IEnumerable<LogIndexRow> rows) {
    LogIndexRow[] snapshot = rows.ToArray();
    if (snapshot.Length == 0) {
      return;
    }
    Dispatcher.UIThread.Post(() => {
      foreach (LogIndexRow row in snapshot) {
        row.Dispose();
      }
    }, DispatcherPriority.Background);
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private static StringComparer PathComparer => OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase
      : StringComparer.Ordinal;
}
