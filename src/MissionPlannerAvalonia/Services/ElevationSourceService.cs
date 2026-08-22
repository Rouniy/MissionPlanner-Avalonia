using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct ElevationScanProgress(
    int Completed,
    int Total,
    string CurrentFile);

public sealed record ElevationSourceFile(
    string FullPath,
    string Format,
    bool Indexed,
    string Coverage,
    string? Error = null) {
  public string Name => Path.GetFileName(FullPath);
  public string State => Error != null ? "Error" : Indexed ? "Indexed" : "Skipped";
}

internal sealed record ElevationScanResult(
    string Directory,
    IReadOnlyList<ElevationSourceFile> Files,
    DateTime CompletedUtc) {
  internal int IndexedCount => Files.Count(file => file.Indexed);
  internal int ErrorCount => Files.Count(file => file.Error != null);
  internal int GeoTiffCount => Files.Count(file => file.Format == "GeoTIFF");
  internal int DtedCount => Files.Count(file => file.Format == "DTED");
}

/// <summary>
/// Restores the official Mission Planner local DEM workflow without loading native GDAL.
/// GeoTIFF and DTED parsing is delegated to the implementations pinned in the upstream
/// Mission Planner submodule, so srtm.getAltitude keeps the official GeoTIFF -> DTED ->
/// downloaded SRTM precedence.
/// </summary>
internal static class ElevationSourceService {
  internal const string SettingsKey = "GDALImageDir";

  private static readonly SemaphoreSlim _scanGate = new(1, 1);
  private static readonly object _stateGate = new();
  private static readonly Dictionary<string, ElevationSourceFile> _dtedMetadata =
      new(PathComparer);
  private static bool _startupInitialized;
  private static bool _geoTiffTagsRegistered;
  private static ElevationScanResult? _lastResult;
  private static Task<ElevationScanResult>? _startupTask;

  internal static ElevationScanResult? LastResult {
    get {
      lock (_stateGate) {
        return _lastResult;
      }
    }
  }

  internal static Task<ElevationScanResult>? StartupTask {
    get {
      lock (_stateGate) {
        return _startupTask;
      }
    }
  }

  internal static string SavedDirectory =>
      Settings.Instance.GetString(SettingsKey, "").Trim();

  internal static void InitializeFromSettings() {
    // Register GeoTIFF tags even when there is no custom directory: the ordinary SRTM
    // cache may itself contain local .tif files and GeoTiff's static index is lazy.
    EnsureGeoTiffTagsRegistered();
    lock (_stateGate) {
      if (_startupInitialized) {
        return;
      }
      _startupInitialized = true;

      string directory = SavedDirectory;
      if (string.IsNullOrWhiteSpace(directory)) {
        return;
      }

      // Official Mission Planner queues LoadGDALImages on the thread pool. Keep startup
      // responsive too, while isolating all file failures inside the completed result.
      _startupTask = ScanAsync(directory, progress: null, CancellationToken.None);
      _ = ObserveStartupAsync(_startupTask);
    }
  }

  internal static string SaveDirectory(string directory) {
    string fullPath = NormalizeExistingDirectory(directory);
    Settings.Instance[SettingsKey] = fullPath;
    Settings.Instance.Save();
    return fullPath;
  }

  internal static void ClearSavedDirectory() {
    Settings.Instance[SettingsKey] = "";
    Settings.Instance.Save();
  }

  internal static Task<ElevationScanResult> ScanAsync(
      string directory,
      IProgress<ElevationScanProgress>? progress,
      CancellationToken cancellationToken) => Task.Run(
          () => ScanCore(directory, progress, cancellationToken), cancellationToken);

  internal static IReadOnlyList<string> FindSupportedFiles(string directory) {
    string root = NormalizeExistingDirectory(directory);
    var options = new EnumerationOptions {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      ReturnSpecialDirectories = false,
      MatchCasing = MatchCasing.CaseInsensitive,
    };

    return Directory.EnumerateFiles(root, "*", options)
        .Where(IsSupported)
        .OrderBy(FormatOrder)
        .ThenBy(path => path, PathComparer)
        .ToArray();
  }

  internal static bool RequiresRestartToSwitch(string directory) {
    ElevationScanResult? active = LastResult;
    if (active == null || active.IndexedCount == 0) {
      return false;
    }
    string candidate = Path.GetFullPath(directory);
    return !PathComparer.Equals(Path.GetFullPath(active.Directory), candidate);
  }

  internal static void EnsureGeoTiffTagsRegistered() {
    lock (_stateGate) {
      if (_geoTiffTagsRegistered) {
        return;
      }

      var fields = new[] {
        CustomField(33550, TiffType.DOUBLE, "ModelPixelScaleTag"),
        CustomField(33922, TiffType.DOUBLE, "ModelTiepointTag"),
        CustomField(34735, TiffType.SHORT, "GeoKeyDirectoryTag"),
        CustomField(34736, TiffType.DOUBLE, "GeoDoubleParamsTag"),
        CustomField(34737, TiffType.ASCII, "GeoAsciiParamsTag"),
        CustomField(42112, TiffType.ASCII, "GDAL_METADATA"),
        CustomField(42113, TiffType.ASCII, "GDAL_NODATA"),
      };
      Tiff.TiffExtendProc? previous = null;
      previous = Tiff.SetTagExtender(tiff => {
        previous?.Invoke(tiff);
        tiff.MergeFieldInfo(fields, fields.Length);
      });
      _geoTiffTagsRegistered = true;
    }
  }

  private static ElevationScanResult ScanCore(
      string directory,
      IProgress<ElevationScanProgress>? progress,
      CancellationToken cancellationToken) {
    _scanGate.Wait(cancellationToken);
    try {
      string root = NormalizeExistingDirectory(directory);
      IReadOnlyList<string> files = FindSupportedFiles(root);
      var results = new List<ElevationSourceFile>(files.Count);

      for (int index = 0; index < files.Count; index++) {
        cancellationToken.ThrowIfCancellationRequested();
        string file = files[index];
        progress?.Report(new ElevationScanProgress(index, files.Count, file));
        try {
          results.Add(IsGeoTiff(file) ? IndexGeoTiff(file) : IndexDted(file));
        } catch (Exception ex) {
          results.Add(new ElevationSourceFile(
              file,
              IsGeoTiff(file) ? "GeoTIFF" : "DTED",
              Indexed: false,
              Coverage: "—",
              Error: FriendlyError(ex)));
        }
      }

      progress?.Report(new ElevationScanProgress(files.Count, files.Count, ""));
      var result = new ElevationScanResult(root, results, DateTime.UtcNow);
      lock (_stateGate) {
        _lastResult = result;
      }
      return result;
    } finally {
      _scanGate.Release();
    }
  }

  private static ElevationSourceFile IndexGeoTiff(string path) {
    string fullPath = Path.GetFullPath(path);
    GeoTiff.geotiffdata data;

    // LibTiff deliberately treats GeoTIFF keys as application-defined tags. The
    // official parser reads them directly, so register their field definitions before
    // its static constructor opens either the SRTM cache or this custom file.
    EnsureGeoTiffTagsRegistered();

    // LoadFile appends to this public official index. Hold its own lock across the
    // duplicate check and load so repeated scans cannot register the same raster twice.
    lock (GeoTiff.index) {
      data = GeoTiff.index.FirstOrDefault(item => PathsEqual(item.FileName, fullPath))!;
      if (data == null) {
        data = new GeoTiff.geotiffdata();
        data.LoadFile(fullPath);
      }
    }

    return new ElevationSourceFile(
        fullPath,
        "GeoTIFF",
        Indexed: true,
        CoverageText(data.Area.Top, data.Area.Bottom, data.Area.Left, data.Area.Right,
            data.width, data.height));
  }

  private static ElevationSourceFile IndexDted(string path) {
    string fullPath = Path.GetFullPath(path);
    lock (_stateGate) {
      if (_dtedMetadata.TryGetValue(fullPath, out ElevationSourceFile? known)) {
        return known;
      }
    }

    // DTEDdata.LoadFile performs the same header validation and registration used by
    // DTED.AddCustomDirectory. Loading candidates one by one adds cancellation and
    // per-file error reporting while retaining the official parser and altitude cache.
    var data = new DTED.DTEDdata();
    data.LoadFile(fullPath);
    if (string.IsNullOrWhiteSpace(data.FileName)) {
      var skipped = new ElevationSourceFile(
          fullPath, "DTED", Indexed: false, Coverage: "Already indexed or invalid header");
      lock (_stateGate) {
        _dtedMetadata[fullPath] = skipped;
      }
      return skipped;
    }

    var result = new ElevationSourceFile(
        fullPath,
        "DTED",
        Indexed: true,
        CoverageText(data.Area.Top, data.Area.Bottom, data.Area.Left, data.Area.Right,
            data.width, data.height));
    lock (_stateGate) {
      _dtedMetadata[fullPath] = result;
    }
    return result;
  }

  private static async Task ObserveStartupAsync(Task<ElevationScanResult> task) {
    try {
      await task.ConfigureAwait(false);
    } catch (Exception ex) {
      // Keep background startup indexing observed and expose a useful failure to the UI.
      var result = new ElevationScanResult(
          SavedDirectory,
          [new ElevationSourceFile(
              SavedDirectory, "Directory", Indexed: false, Coverage: "—", FriendlyError(ex))],
          DateTime.UtcNow);
      lock (_stateGate) {
        _lastResult = result;
      }
    }
  }

  private static string NormalizeExistingDirectory(string directory) {
    if (string.IsNullOrWhiteSpace(directory)) {
      throw new ArgumentException("Choose a DEM directory first.", nameof(directory));
    }
    string fullPath = Path.GetFullPath(directory.Trim());
    if (!Directory.Exists(fullPath)) {
      throw new DirectoryNotFoundException($"DEM directory does not exist: {fullPath}");
    }
    return fullPath;
  }

  private static TiffFieldInfo CustomField(int tag, TiffType type, string name) => new(
      (TiffTag)tag,
      TiffFieldInfo.Variable2,
      TiffFieldInfo.Variable2,
      type,
      FieldBit.Custom,
      okToChange: true,
      passCount: true,
      name);

  private static bool IsSupported(string path) {
    string extension = Path.GetExtension(path);
    return extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".dt0", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".dt1", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".dt2", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsGeoTiff(string path) {
    string extension = Path.GetExtension(path);
    return extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
  }

  private static int FormatOrder(string path) =>
      Path.GetExtension(path).ToLowerInvariant() switch {
        ".tif" or ".tiff" => 0,
        ".dt2" => 1,
        ".dt1" => 2,
        ".dt0" => 3,
        _ => 4,
      };

  private static string CoverageText(
      double north, double south, double west, double east, int width, int height) =>
      $"{width}×{height}; N {north:0.######}, S {south:0.######}, " +
      $"W {west:0.######}, E {east:0.######}";

  private static bool PathsEqual(string? left, string right) =>
      left != null && PathComparer.Equals(Path.GetFullPath(left), right);

  private static string FriendlyError(Exception exception) {
    Exception error = exception is AggregateException aggregate
        ? aggregate.GetBaseException()
        : exception;
    return string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name
        : error.Message;
  }

  private static StringComparer PathComparer => OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase
      : StringComparer.Ordinal;
}
