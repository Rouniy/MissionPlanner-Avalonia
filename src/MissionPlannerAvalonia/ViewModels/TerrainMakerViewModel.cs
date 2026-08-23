using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed partial class TerrainMakerViewModel : ViewModelBase, IDisposable {
  private CancellationTokenSource? _cancellation;
  private Task? _activeOperation;
  private bool _disposed;

  [ObservableProperty]
  private decimal _south;

  [ObservableProperty]
  private decimal _west;

  [ObservableProperty]
  private decimal _north;

  [ObservableProperty]
  private decimal _east;

  [ObservableProperty]
  private int _spacingMeters = 30;

  [ObservableProperty]
  private string _outputDirectory = Path.Combine(AppPaths.DataRoot, "TerrainData");

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private double _progressPercent;

  [ObservableProperty]
  private string _estimateText = "";

  [ObservableProperty]
  private string _status =
      "Review the visible map bounds and grid spacing. Existing DAT tiles are replaced only after a complete new tile has been written and flushed.";

  internal TerrainMakerViewModel(TerrainBounds visibleBounds) {
    South = RoundCoordinate(visibleBounds.South);
    West = RoundCoordinate(visibleBounds.West);
    North = RoundCoordinate(visibleBounds.North);
    East = RoundCoordinate(visibleBounds.East);
    RefreshEstimate();
  }

  public bool CanEdit => !IsBusy;

  partial void OnSouthChanged(decimal value) => RefreshEstimate();

  partial void OnWestChanged(decimal value) => RefreshEstimate();

  partial void OnNorthChanged(decimal value) => RefreshEstimate();

  partial void OnEastChanged(decimal value) => RefreshEstimate();

  partial void OnSpacingMetersChanged(int value) => RefreshEstimate();

  partial void OnOutputDirectoryChanged(string value) => RefreshEstimate();

  partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

  [RelayCommand]
  private async Task BrowseOutput() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    var folders = await Dialogs.Owner.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions {
          Title = "Select Terrain DAT output folder",
          AllowMultiple = false,
          SuggestedStartLocation = await SuggestedStartLocationAsync(),
        });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      OutputDirectory = Path.GetFullPath(path);
    }
  }

  [RelayCommand]
  public async Task StartAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (IsBusy) {
      return;
    }

    TerrainMakerOptions options;
    TerrainMakerEstimate estimate;
    try {
      options = BuildOptions();
      estimate = TerrainDataService.Estimate(options);
    } catch (Exception ex) {
      Status = "Cannot generate Terrain DAT: " + ex.Message;
      return;
    }

    int replacements = TerrainDataService.TilesForBounds(options.Bounds)
        .Count(tile => File.Exists(Path.Combine(
            Path.GetFullPath(options.OutputDirectory), tile.FileName)));
    string replacementWarning = replacements == 0
        ? ""
        : $"\n\n{replacements:N0} existing complete DAT file(s) will be atomically replaced.";
    if (!await Dialogs.ConfirmDangerous(
            "Generate ArduPilot Terrain DAT?",
            $"Generate {estimate.TileCount:N0} whole-degree tile(s), "
                + $"{estimate.BlockCount:N0} blocks and {estimate.SampleCount:N0} elevation samples "
                + $"({FormatBytes(estimate.OutputBytes)})?"
                + replacementWarning
                + "\n\nThe official elevation order is local GeoTIFF, DTED and cached/downloaded SRTM. "
                + "Missing or zero samples remain marked invalid in the DAT bitmap."
                + $"\n\nOutput:\n{Path.GetFullPath(options.OutputDirectory)}",
            "GENERATE TERRAIN")) {
      Status = "Terrain DAT generation cancelled before any output was changed.";
      return;
    }

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;
    IsBusy = true;
    ProgressPercent = 0;
    Status = "Preparing terrain tiles…";
    var progress = new Progress<TerrainMakerProgress>(update => {
      ProgressPercent = update.Fraction * 100;
      Status = $"{update.CurrentFile}: block {update.CompletedBlocks:N0} of "
          + $"{update.TotalBlocks:N0}; completed tiles {update.CompletedTiles:N0} of "
          + $"{update.TotalTiles:N0}.";
    });
    Task<TerrainMakerResult> operation = TerrainDataService.GenerateAsync(
        options,
        progress,
        cancellation.Token);
    _activeOperation = operation;
    try {
      TerrainMakerResult result = await operation;
      ProgressPercent = 100;
      string missing = result.MissingSamples == 0
          ? "All elevation samples were valid and non-zero."
          : $"{result.MissingSamples:N0} sample(s) were unavailable or zero and are marked invalid.";
      Status = $"Created {result.Files.Count:N0} Terrain DAT file(s), "
          + $"{FormatBytes(result.OutputBytes)}. {missing}\n{Path.GetFullPath(options.OutputDirectory)}";
    } catch (OperationCanceledException) {
      Status = "Terrain DAT generation cancelled. Completed tiles were retained; the in-progress "
          + "temporary tile was removed and no prior complete tile was damaged.";
    } catch (Exception ex) {
      Status = "Terrain DAT generation failed: " + ex.Message
          + " Completed tiles were retained; an incomplete tile was not published.";
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
    Status = "Cancellation requested; removing the in-progress temporary tile…";
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
      // StartAsync translates cancellation and failure into a visible status.
    }
  }

  internal void RefreshEstimate() {
    try {
      TerrainMakerEstimate estimate = TerrainDataService.Estimate(BuildOptions());
      EstimateText = $"{estimate.TileCount:N0} tile(s) · {estimate.BlockCount:N0} blocks · "
          + $"{estimate.SampleCount:N0} samples · {FormatBytes(estimate.OutputBytes)}";
    } catch (Exception ex) {
      EstimateText = "Invalid settings: " + ex.Message;
    }
  }

  internal TerrainMakerOptions BuildOptions() => new(
      new TerrainBounds((double)South, (double)West, (double)North, (double)East),
      checked((ushort)SpacingMeters),
      OutputDirectory);

  internal static string FormatBytes(long bytes) {
    string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1) {
      value /= 1024;
      unit++;
    }
    return value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)
        + " " + units[unit];
  }

  private async Task<IStorageFolder?> SuggestedStartLocationAsync() {
    try {
      if (!Directory.Exists(OutputDirectory) || Dialogs.Owner == null) {
        return null;
      }
      return await Dialogs.Owner.StorageProvider.TryGetFolderFromPathAsync(OutputDirectory);
    } catch {
      return null;
    }
  }

  private static decimal RoundCoordinate(double value) =>
      decimal.Round((decimal)Math.Clamp(value, -180, 180), 7);

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _cancellation?.Cancel();
  }
}
