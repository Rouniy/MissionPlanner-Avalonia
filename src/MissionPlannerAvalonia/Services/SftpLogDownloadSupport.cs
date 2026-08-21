using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

internal sealed record DownloadedDataFlashFiles(
    string BinPath,
    string LogPath,
    string? KmlPath,
    DateTime? FirstGpsTime,
    IReadOnlyList<string> Warnings);

internal static class SftpLocalFile {
  private const int _maxFileNameLength = 100;

  public static string SafeBinName(string? remoteName, int fallbackIndex = 0) {
    string leaf = Path.GetFileName((remoteName ?? "").Replace('\0', '_').Replace('\\', '/'));
    var chars = leaf.Select(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or '.' or '_' or '-'
            ? character
            : '_').ToArray();
    string name = new string(chars).Trim();
    if (name.Length == 0 || name is "." or "..") {
      name = $"log_{Math.Max(0, fallbackIndex)}.bin";
    }
    if (name[0] is '.' or '-') {
      name = "log_" + name.TrimStart('.', '-');
    }
    if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) {
      name += ".bin";
    }
    if (name.Length > _maxFileNameLength) {
      name = name[..(_maxFileNameLength - 4)] + ".bin";
    }
    return name;
  }

  public static string CombineContained(string directory, string fileName) {
    string root = Path.GetFullPath(directory);
    string candidate = Path.GetFullPath(Path.Combine(root, fileName));
    string prefix = root.EndsWith(Path.DirectorySeparatorChar)
        ? root
        : root + Path.DirectorySeparatorChar;
    var comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    if (!candidate.StartsWith(prefix, comparison)) {
      throw new IOException("The generated log path escaped the selected destination directory.");
    }
    return candidate;
  }

  public static string UniquePath(string desiredPath, params string[] siblingExtensions) {
    string directory = Path.GetDirectoryName(desiredPath)
        ?? throw new ArgumentException("A destination directory is required.", nameof(desiredPath));
    string stem = Path.GetFileNameWithoutExtension(desiredPath);
    string extension = Path.GetExtension(desiredPath);
    for (int suffix = 0; suffix < 10000; suffix++) {
      string candidateStem = suffix == 0 ? stem : $"{stem}-{suffix}";
      string candidate = CombineContained(directory, candidateStem + extension);
      if (File.Exists(candidate)) {
        continue;
      }
      bool siblingExists = siblingExtensions.Any(item =>
          File.Exists(CombineContained(directory, candidateStem + item)));
      if (!siblingExists) {
        return candidate;
      }
    }
    throw new IOException("Unable to choose an unused local filename for the downloaded log.");
  }

  public static string NewPartialPath(string destinationPath) {
    string directory = Path.GetDirectoryName(destinationPath)!;
    string fileName = "." + Path.GetFileName(destinationPath) + "." +
        Guid.NewGuid().ToString("N") + ".partial";
    return CombineContained(directory, fileName);
  }
}

internal static class DataFlashDownloadPostProcessor {
  public static async Task<DownloadedDataFlashFiles> ProcessAsync(
      string binPath, bool createKml, CancellationToken cancellationToken) {
    var warnings = new List<string>();
    DateTime? firstGpsTime = null;
    try {
      firstGpsTime = await Task.Run(() =>
          DataFlashLog.ReadTrack(binPath)
              .Select(point => point.time)
              .FirstOrDefault(time => time != DateTime.MinValue), cancellationToken);
      if (firstGpsTime == DateTime.MinValue) {
        firstGpsTime = null;
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      warnings.Add("GPS timestamp unavailable: " + ex.Message);
    }

    if (firstGpsTime.HasValue) {
      string directory = Path.GetDirectoryName(binPath)!;
      DateTime local = firstGpsTime.Value.Kind == DateTimeKind.Local
          ? firstGpsTime.Value
          : firstGpsTime.Value.ToLocalTime();
      string desired = SftpLocalFile.CombineContained(
          directory, local.ToString("yyyy-MM-dd HH-mm-ss") + ".bin");
      string timestamped = SftpLocalFile.UniquePath(desired, ".log", ".kml");
      if (!string.Equals(timestamped, binPath, StringComparison.Ordinal)) {
        File.Move(binPath, timestamped, overwrite: false);
        binPath = timestamped;
      }
    }

    cancellationToken.ThrowIfCancellationRequested();
    string logPath = SftpLocalFile.UniquePath(Path.ChangeExtension(binPath, ".log"));
    try {
      await Task.Run(() => DataFlashLog.ConvertBinToLog(binPath, logPath), cancellationToken);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      TryDelete(logPath);
      warnings.Add("BIN-to-LOG conversion failed: " + ex.Message);
      logPath = "";
    }

    string? kmlPath = null;
    if (createKml) {
      cancellationToken.ThrowIfCancellationRequested();
      kmlPath = SftpLocalFile.UniquePath(Path.ChangeExtension(binPath, ".kml"));
      try {
        await Task.Run(() => DataFlashLog.ExportKml(binPath, kmlPath), cancellationToken);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        TryDelete(kmlPath);
        warnings.Add("KML creation failed: " + ex.Message);
        kmlPath = null;
      }
    }

    return new DownloadedDataFlashFiles(
        binPath, logPath, kmlPath, firstGpsTime, warnings);
  }

  private static void TryDelete(string? path) {
    if (string.IsNullOrEmpty(path)) {
      return;
    }
    try {
      File.Delete(path);
    } catch {
    }
  }
}
