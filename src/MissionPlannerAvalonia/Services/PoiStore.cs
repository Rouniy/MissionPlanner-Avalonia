using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MissionPlannerAvalonia.Services;

public sealed record PoiPoint(double Lat, double Lng, double Alt, string Name);

public static class PoiStore {

  public static string FilePath => AppPaths.PoiFilePath;

  private static readonly List<PoiPoint> _points = [];

  public static IReadOnlyList<PoiPoint> All => _points;

  public static void Add(PoiPoint p) {
    _points.Add(p);
    Save();
  }

  public static void Add(double lat, double lng, double alt, string name) =>
      Add(new PoiPoint(lat, lng, alt, name));

  public static int AddRange(IEnumerable<PoiPoint> points) {
    var additions = points.ToList();
    if (additions.Count == 0) {
      return 0;
    }
    _points.AddRange(additions);
    Save();
    return additions.Count;
  }

  public static bool Remove(PoiPoint p) {
    bool removed = _points.Remove(p);
    if (removed) {
      Save();
    }
    return removed;
  }

  public static bool Replace(PoiPoint existing, PoiPoint replacement) {
    int index = _points.IndexOf(existing);
    if (index < 0) {
      return false;
    }
    _points[index] = replacement;
    Save();
    return true;
  }

  public static void Clear() {
    _points.Clear();
    Save();
  }

  public static void Load() => Load(FilePath);

  public static void Load(string path) {
    _points.Clear();
    _points.AddRange(Read(path));
  }

  public static int Import(string path) {
    var imported = Read(path);
    _points.AddRange(imported);
    return imported.Count;
  }

  private static List<PoiPoint> Read(string path) {
    if (!File.Exists(path)) {
      return [];
    }
    return [.. ParseLines(File.ReadLines(path))];
  }

  internal static IReadOnlyList<PoiPoint> ParseLines(IEnumerable<string> lines) {
    var result = new List<PoiPoint>();
    foreach (var line in lines) {
      if (string.IsNullOrWhiteSpace(line)) {
        continue;
      }
      var f = line.Split('\t');
      if (f.Length < 3) {
        continue;
      }
      if (!TryD(f[0], out var lat) || !TryD(f[1], out var lng)) {
        continue;
      }

      if (f.Length >= 4 && TryD(f[2], out var alt)) {
        result.Add(new PoiPoint(lat, lng, alt, f[3]));
      } else {
        result.Add(new PoiPoint(lat, lng, 0, f[2]));
      }
    }
    return result;
  }

  public static void Save() => Save(FilePath);

  public static void Save(string path) {
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) {
      Directory.CreateDirectory(dir);
    }
    using var sw = new StreamWriter(path, false);
    foreach (var p in _points) {
      sw.WriteLine(string.Join("\t", new[] {
        p.Lat.ToString(CultureInfo.InvariantCulture),
        p.Lng.ToString(CultureInfo.InvariantCulture),
        p.Alt.ToString(CultureInfo.InvariantCulture),
        p.Name ?? "",
      }));
    }
  }

  private static bool TryD(string s, out double v) =>
      double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);
}
