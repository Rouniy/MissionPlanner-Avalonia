using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed partial class OsdVideoOverlayViewModel : ViewModelBase, IDisposable {
  private CancellationTokenSource? _cancellation;
  private Task? _activeOperation;
  private IOsdVideoFrameRenderer? _renderer;
  private bool _disposed;

  [ObservableProperty]
  private string _videoPath = "";

  [ObservableProperty]
  private string _tlogPath = "";

  [ObservableProperty]
  private string _outputPath = "";

  [ObservableProperty]
  private decimal _timeOffsetSeconds;

  [ObservableProperty]
  private bool _fullResolution;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  private int _writtenFrames;

  [ObservableProperty]
  private string _status =
      "Choose a source video and its synchronized .tlog. The output is a silent MJPEG AVI, matching Mission Planner's OSDVideo workflow.";

  internal void SetRenderer(IOsdVideoFrameRenderer renderer) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
  }

  [RelayCommand]
  private async Task BrowseVideo() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    var files = await Dialogs.Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select video for OSD overlay",
      AllowMultiple = false,
      FileTypeFilter = [
        new FilePickerFileType("Video files") {
          Patterns = ["*.avi", "*.mpe", "*.mpeg", "*.mpg", "*.mp4", "*.mov", "*.mkv"],
        },
        new FilePickerFileType("All files") { Patterns = ["*"], },
      ],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }
    VideoPath = Path.GetFullPath(path);
    OutputPath = OsdVideoOverlayService.DefaultOutputPath(VideoPath);
    Status = "Select the matching telemetry log and adjust the time offset if needed.";
  }

  [RelayCommand]
  private async Task BrowseTlog() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    var files = await Dialogs.Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select synchronized telemetry log",
      AllowMultiple = false,
      FileTypeFilter = [
        new FilePickerFileType("Telemetry log") { Patterns = ["*.tlog"], },
        new FilePickerFileType("All files") { Patterns = ["*"], },
      ],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      TlogPath = Path.GetFullPath(path);
      Status = "Ready to render after reviewing the output name and synchronization offset.";
    }
  }

  [RelayCommand]
  private async Task BrowseOutput() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    string suggested = !string.IsNullOrWhiteSpace(OutputPath)
        ? Path.GetFileName(OutputPath)
        : !string.IsNullOrWhiteSpace(VideoPath)
            ? Path.GetFileName(OsdVideoOverlayService.DefaultOutputPath(VideoPath))
            : "video-overlay.avi";
    IStorageFile? file = await Dialogs.Owner.StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions {
          Title = "Save synchronized OSD video",
          SuggestedFileName = suggested,
          DefaultExtension = "avi",
          FileTypeChoices = [new FilePickerFileType("MJPEG AVI") { Patterns = ["*.avi"], }],
        });
    string? path = file?.TryGetLocalPath();
    if (path != null) {
      OutputPath = Path.GetFullPath(path);
    }
  }

  [RelayCommand]
  public async Task StartAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (IsBusy) {
      return;
    }
    if (_renderer == null) {
      Status = "The Avalonia HUD renderer is not available.";
      return;
    }
    if (string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(VideoPath)) {
      OutputPath = OsdVideoOverlayService.DefaultOutputPath(VideoPath);
    }

    var options = new OsdVideoExportOptions(
        VideoPath,
        TlogPath,
        OutputPath,
        (int)Math.Clamp(
            TimeOffsetSeconds,
            OsdVideoOverlayService.MinimumOffsetSeconds,
            OsdVideoOverlayService.MaximumOffsetSeconds),
        FullResolution);
    try {
      OsdVideoOverlayService.Validate(options);
    } catch (Exception ex) {
      Status = "Cannot start OSD video: " + ex.Message;
      return;
    }

    if (!await Dialogs.ConfirmDangerous(
            "Create synchronized OSD video?",
            Dialogs.SensitiveExportWarning
                + "\n\nThe output also contains every visible source-video frame and HUD values. "
                + "Mission Planner's OSDVideo output is a silent MJPEG AVI; audio is not copied."
                + $"\n\nOutput:\n{Path.GetFullPath(OutputPath)}",
            "CREATE OSD VIDEO")) {
      Status = "OSD video export cancelled before any output was created.";
      return;
    }

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;
    IsBusy = true;
    Progress = 0;
    WrittenFrames = 0;
    Status = "Reading telemetry log…";
    var progress = new Progress<OsdVideoExportProgress>(update => {
      Progress = Math.Clamp(update.Fraction * 100, 0, 100);
      WrittenFrames = update.WrittenFrames;
      Status = update.Phase;
    });
    Task<OsdVideoExportResult> operation = OsdVideoOverlayService.ExportAsync(
        options, _renderer, progress, cancellation.Token);
    _activeOperation = operation;
    try {
      OsdVideoExportResult result = await operation;
      Progress = 100;
      WrittenFrames = result.WrittenFrames;
      Status = $"Saved {result.WrittenFrames:N0} frames at {result.Width}×{result.Height}, "
          + $"{result.FramesPerSecond} fps:\n{result.OutputPath}";
    } catch (OperationCanceledException) {
      Status = PartialStatus("OSD video export cancelled.");
    } catch (Exception ex) {
      Status = PartialStatus("OSD video export failed: " + ex.Message);
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
    Status = "Cancellation requested; finalizing the partial AVI…";
    _cancellation.Cancel();
  }

  internal async Task CancelAndWaitAsync() {
    _cancellation?.Cancel();
    Task? operation = _activeOperation;
    if (operation == null) {
      return;
    }
    try {
      await operation;
    } catch {
      // StartAsync converts cancellation/failure into a visible status message.
    }
  }

  private string PartialStatus(string prefix) {
    try {
      if (File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0) {
        return prefix + " A playable partial AVI was retained at:\n" + OutputPath;
      }
    } catch {
      // The primary cancellation/error remains useful when the output cannot be inspected.
    }
    return prefix + " No output frames were written.";
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _cancellation?.Cancel();
  }
}
