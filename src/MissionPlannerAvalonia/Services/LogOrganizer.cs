using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MissionPlanner.Log;

namespace MissionPlannerAvalonia.Services;

public static class LogOrganizer {
  private static readonly HashSet<string> _extensions =
      new(StringComparer.OrdinalIgnoreCase) { ".tlog", ".rlog", ".bin", ".log" };

  public static string[] FindCandidates(string root) {
    if (!Directory.Exists(root)) {
      return Array.Empty<string>();
    }

    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => _extensions.Contains(Path.GetExtension(path)))
        .ToArray();
  }

  public static int Organize(string root) {
    var files = FindCandidates(root);
    if (files.Length > 0) {
      LogSort.SortLogs(files, root);
    }
    return files.Length;
  }
}
