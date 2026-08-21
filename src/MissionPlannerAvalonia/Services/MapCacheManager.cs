using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MissionPlannerAvalonia.Services;

internal sealed record MapCacheSnapshot(
    string Name,
    string Path,
    long SizeBytes,
    long FileCount,
    DateTime? LastWriteUtc,
    bool IsTotal = false);

internal readonly record struct MapCacheDeleteResult(long RemovedFiles, long FreedBytes, long FailedFiles);

internal static class MapCacheManager {
  internal static IReadOnlyList<MapCacheSnapshot> Scan(string? root = null) {
    root = NormalizeRoot(root ?? AppPaths.MapTileCacheRoot);
    if (!Directory.Exists(root)) {
      return [new MapCacheSnapshot("Total", root, 0, 0, null, true)];
    }

    var entries = Directory.EnumerateDirectories(root)
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .Select(path => Snapshot(Path.GetFileName(path), path))
        .ToList();
    DateTime latest = entries.Select(item => item.LastWriteUtc)
        .Where(value => value.HasValue)
        .Select(value => value!.Value)
        .DefaultIfEmpty()
        .Max();
    entries.Add(new MapCacheSnapshot(
        "Total",
        root,
        entries.Sum(item => item.SizeBytes),
        entries.Sum(item => item.FileCount),
        latest == default ? null : latest,
        true));
    return entries;
  }

  internal static MapCacheDeleteResult DeleteOlderThan(
      MapCacheSnapshot entry, DateTime cutoffUtc, string? root = null) {
    root = NormalizeRoot(root ?? AppPaths.MapTileCacheRoot);
    string target = Path.GetFullPath(entry.Path);
    EnsureInsideRoot(root, target, entry.IsTotal);
    if (!Directory.Exists(target)) {
      return default;
    }

    long removed = 0;
    long bytes = 0;
    long failed = 0;
    foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) {
      try {
        var info = new FileInfo(file);
        if (info.LastWriteTimeUtc >= cutoffUtc) {
          continue;
        }
        long length = info.Length;
        info.Delete();
        removed++;
        bytes += length;
      } catch {
        failed++;
      }
    }

    RemoveEmptyDirectories(target, keepTarget: true);
    return new MapCacheDeleteResult(removed, bytes, failed);
  }

  internal static string FormatBytes(long bytes) {
    string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
    double value = Math.Max(0, bytes);
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1) {
      value /= 1024;
      unit++;
    }
    return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
  }

  private static MapCacheSnapshot Snapshot(string name, string path) {
    long bytes = 0;
    long count = 0;
    DateTime? latest = null;
    try {
      foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
        try {
          var info = new FileInfo(file);
          bytes += info.Length;
          count++;
          if (!latest.HasValue || info.LastWriteTimeUtc > latest.Value) {
            latest = info.LastWriteTimeUtc;
          }
        } catch {
        }
      }
    } catch {
    }
    return new MapCacheSnapshot(name, path, bytes, count, latest);
  }

  private static string NormalizeRoot(string root) =>
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

  private static void EnsureInsideRoot(string root, string target, bool allowRoot) {
    var comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    if ((allowRoot && target.Equals(root, comparison))
        || target.StartsWith(root + Path.DirectorySeparatorChar, comparison)) {
      return;
    }
    throw new InvalidOperationException("Map cache target is outside the configured cache root.");
  }

  private static void RemoveEmptyDirectories(string target, bool keepTarget) {
    foreach (string directory in Directory.EnumerateDirectories(target)) {
      RemoveEmptyDirectories(directory, keepTarget: false);
    }
    if (!keepTarget && !Directory.EnumerateFileSystemEntries(target).Any()) {
      try {
        Directory.Delete(target);
      } catch {
      }
    }
  }
}
