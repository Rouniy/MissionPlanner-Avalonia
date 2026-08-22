using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Cache;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct MapTileImportProgress(
    long Discovered,
    long Imported,
    long Skipped,
    long Failed,
    string CurrentFile);

internal readonly record struct MapTileImportResult(
    long Discovered,
    long Imported,
    long Skipped,
    long Failed,
    long ImportedBytes);

/// <summary>
/// Imports the Z&lt;zoom&gt;/&lt;row&gt;/&lt;column&gt; image hierarchy accepted by Mission Planner's
/// GE Injection developer utility into the persistent cache for one exact map provider.
/// </summary>
internal static class MapTileImporter {
  private const int MaximumZoom = 21;
  private const long MaximumTileBytes = 32L * 1024 * 1024;

  internal static Task<MapTileImportResult> ImportAsync(
      string sourceRoot,
      string mapType,
      IProgress<MapTileImportProgress>? progress = null,
      CancellationToken cancellationToken = default) =>
    Task.Run(() => Import(
        sourceRoot,
        MapTileSourceFactory.GetPersistentCacheForMapType(mapType),
        progress,
        cancellationToken), cancellationToken);

  internal static MapTileImportResult Import(
      string sourceRoot,
      IPersistentCache<byte[]> destination,
      IProgress<MapTileImportProgress>? progress = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
    ArgumentNullException.ThrowIfNull(destination);

    string root = Path.GetFullPath(sourceRoot);
    if (!Directory.Exists(root)) {
      throw new DirectoryNotFoundException($"Tile source directory does not exist: {root}");
    }

    long discovered = 0;
    long imported = 0;
    long skipped = 0;
    long failed = 0;
    long bytes = 0;
    var importedIndexes = new HashSet<TileIndex>();
    var pending = new Stack<string>();
    pending.Push(root);

    while (pending.Count > 0) {
      cancellationToken.ThrowIfCancellationRequested();
      string directory = pending.Pop();
      string[] files;
      string[] directories;
      try {
        files = Directory.GetFiles(directory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        directories = Directory.GetDirectories(directory)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
      } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        failed++;
        Report(progress, discovered, imported, skipped, failed, directory);
        continue;
      }

      foreach (string child in directories) {
        try {
          if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) {
            pending.Push(child);
          }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
          failed++;
        }
      }

      foreach (string file in files) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedImage(file)) {
          continue;
        }
        discovered++;

        if (!TryParseOfficialPath(root, file, out TileIndex index)
            || !importedIndexes.Add(index)) {
          skipped++;
          ReportPeriodically(progress, discovered, imported, skipped, failed, file);
          continue;
        }

        try {
          long length = new FileInfo(file).Length;
          if (length <= 0 || length > MaximumTileBytes) {
            skipped++;
            ReportPeriodically(progress, discovered, imported, skipped, failed, file);
            continue;
          }
          byte[] data = File.ReadAllBytes(file);
          if (!IsDecodableImage(data)) {
            skipped++;
          } else {
            destination.Remove(index);
            destination.Add(index, data);
            imported++;
            bytes += data.LongLength;
          }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
          failed++;
        }
        ReportPeriodically(progress, discovered, imported, skipped, failed, file);
      }
    }

    Report(progress, discovered, imported, skipped, failed, root);
    return new MapTileImportResult(discovered, imported, skipped, failed, bytes);
  }

  internal static bool TryParseOfficialPath(string sourceRoot, string file, out TileIndex index) {
    index = default;
    string root = Path.GetFullPath(sourceRoot);
    string fullPath = Path.GetFullPath(file);
    string relative = Path.GetRelativePath(root, fullPath);
    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)) {
      return false;
    }

    string[] parts = relative.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i + 2 < parts.Length; i++) {
      if (i + 2 != parts.Length - 1) {
        continue;
      }
      string zoomPart = parts[i];
      if (zoomPart.Length < 2 || (zoomPart[0] != 'Z' && zoomPart[0] != 'z')
          || !int.TryParse(zoomPart.AsSpan(1), NumberStyles.None,
              CultureInfo.InvariantCulture, out int zoom)
          || zoom < 0 || zoom > MaximumZoom
          || !int.TryParse(parts[i + 1], NumberStyles.None,
              CultureInfo.InvariantCulture, out int row)
          || !int.TryParse(Path.GetFileNameWithoutExtension(parts[i + 2]), NumberStyles.None,
              CultureInfo.InvariantCulture, out int column)) {
        continue;
      }

      int width = 1 << zoom;
      if (row < 0 || column < 0 || row >= width || column >= width) {
        return false;
      }
      index = new TileIndex(column, row, zoom);
      return true;
    }
    return false;
  }

  private static bool IsSupportedImage(string path) =>
      Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
      || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
      || Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

  private static bool IsDecodableImage(byte[] data) {
    bool signature = data.Length >= 3
        && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff
        || data.Length >= 8
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4e
        && data[3] == 0x47 && data[4] == 0x0d && data[5] == 0x0a
        && data[6] == 0x1a && data[7] == 0x0a;
    if (!signature) {
      return false;
    }
    try {
      using SKBitmap? bitmap = SKBitmap.Decode(data);
      return bitmap is { Width: > 0, Height: > 0 };
    } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
      return false;
    }
  }

  private static void ReportPeriodically(
      IProgress<MapTileImportProgress>? progress,
      long discovered,
      long imported,
      long skipped,
      long failed,
      string currentFile) {
    if (discovered == 1 || discovered % 25 == 0) {
      Report(progress, discovered, imported, skipped, failed, currentFile);
    }
  }

  private static void Report(
      IProgress<MapTileImportProgress>? progress,
      long discovered,
      long imported,
      long skipped,
      long failed,
      string currentFile) =>
    progress?.Report(new MapTileImportProgress(
        discovered, imported, skipped, failed, currentFile));
}
