using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Services;

public static class NoFlyOverlay {
  private static readonly Color _noFlyRed = new(220, 0, 0, 255);

  public static event Action? VisibilityChanged;

  public static void NotifyVisibilityChanged() => VisibilityChanged?.Invoke();

  public static ILayer? BuildLayer(string path, string name = "NoFly") {
    var rings = LoadPolygons(path);
    return rings.Count == 0 ? null : BuildLayer(rings, name);
  }

  public static string DefaultDirectory => Path.Combine(AppPaths.DataRoot, "NoFly");

  public static ILayer? BuildLayerFromDirectory(string dir, string name = "NoFly") {
    if (!Directory.Exists(dir)) {
      return null;
    }
    var rings = new List<IReadOnlyList<(double Lat, double Lng)>>();
    foreach (var file in Directory.EnumerateFiles(dir)
                 .Where(f => f.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      try {
        rings.AddRange(LoadPolygons(file));
      } catch {
        // Skip unreadable overlay files; the rest still load.
      }
    }
    return rings.Count == 0 ? null : BuildLayer(rings, name);
  }

  internal static ILayer? BuildLayerFromDirectoryAndHongKong(
      string directory,
      IReadOnlyList<HongKongNoFlyZone> hongKongZones,
      string name = "NoFly") {
    var localLayer = BuildLayerFromDirectory(directory, name) as WritableLayer;
    var layer = localLayer ?? new WritableLayer { Name = name };
    int added = 0;
    foreach (HongKongNoFlyZone zone in hongKongZones) {
      LinearRing? shell = ProjectRing(zone.Outer);
      if (shell == null) {
        continue;
      }
      LinearRing[] holes = zone.Holes
          .Select(ProjectRing)
          .Where(ring => ring != null)
          .Cast<LinearRing>()
          .ToArray();
      var feature = new GeometryFeature {
        Geometry = new Polygon(shell, holes),
      };
      feature.Styles.Add(new VectorStyle {
        Fill = new Brush(new Color(0, 0, 255, 30)),
        Outline = new Pen(new Color(128, 0, 128, 255), 2),
      });
      layer.Add(feature);
      added++;
    }
    if (added == 0 && localLayer == null) {
      return null;
    }
    layer.DataHasChanged();
    return layer;
  }

  public static ILayer BuildLayer(IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> rings,
      string name = "NoFly") {
    var layer = new WritableLayer { Name = name };
    foreach (var ring in rings) {
      if (ring.Count < 2) {
        continue;
      }
      var pts = new List<MPoint>(ring.Count + 1);
      foreach (var (lat, lng) in ring) {
        var (x, y) = SphericalMercator.FromLonLat(lng, lat);
        pts.Add(new MPoint(x, y));
      }

      if (pts.Count > 0 && !pts[0].Equals(pts[^1])) {
        pts.Add(pts[0]);
      }
      AddOutline(layer, pts);
    }
    return layer;
  }

  public static List<IReadOnlyList<(double Lat, double Lng)>> LoadPolygons(string path) {
    var doc = XDocument.Parse(ReadKmlText(path));
    var rings = new List<IReadOnlyList<(double Lat, double Lng)>>();
    foreach (var poly in Descendants(doc.Root, "Polygon")) {

      var coordsEl = Descendants(poly, "outerBoundaryIs")
                         .SelectMany(b => Descendants(b, "coordinates")).FirstOrDefault()
                     ?? Descendants(poly, "coordinates").FirstOrDefault();
      if (coordsEl == null) {
        continue;
      }
      var ring = ParseCoordinates(coordsEl.Value);
      if (ring.Count >= 3) {
        rings.Add(ring);
      }
    }
    return rings;
  }

  private static void AddOutline(WritableLayer layer, IReadOnlyList<MPoint> pts) {
    if (pts.Count < 2) {
      return;
    }
    var dot = new SymbolStyle {
      SymbolType = SymbolType.Ellipse,
      Fill = new Brush(_noFlyRed),
      SymbolScale = 6.0 / 30.0,
    };
    for (int i = 1; i < pts.Count; i++) {
      var a = pts[i - 1];
      var b = pts[i];
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      double len = Math.Sqrt(dx * dx + dy * dy);
      int steps = Math.Clamp((int)(len / 3.0), 1, 600);
      for (int s = 0; s <= steps; s++) {
        double t = (double)s / steps;
        var f = new PointFeature(new MPoint(a.X + dx * t, a.Y + dy * t));
        f.Styles.Add(dot);
        layer.Add(f);
      }
    }
  }

  private static LinearRing? ProjectRing(IReadOnlyList<(double Lat, double Lng)> ring) {
    if (ring.Count < 3) {
      return null;
    }
    var coordinates = new Coordinate[ring.Count + 1];
    for (int index = 0; index < ring.Count; index++) {
      var projected = SphericalMercator.FromLonLat(ring[index].Lng, ring[index].Lat);
      coordinates[index] = new Coordinate(projected.x, projected.y);
    }
    coordinates[^1] = coordinates[0].Copy();
    return new LinearRing(coordinates);
  }

  private static string ReadKmlText(string path) {
    if (path.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase)) {
      using var zip = ZipFile.OpenRead(path);
      var entry = zip.Entries.FirstOrDefault(
                      e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidDataException("No .kml entry inside the .kmz.");
      using var sr = new StreamReader(entry.Open());
      return sr.ReadToEnd();
    }
    return File.ReadAllText(path);
  }

  private static List<(double Lat, double Lng)> ParseCoordinates(string text) {
    var pts = new List<(double, double)>();
    foreach (var tok in text.Split(new[] { ' ', '\n', '\r', '\t' },
                 StringSplitOptions.RemoveEmptyEntries)) {
      var parts = tok.Split(',');
      if (parts.Length >= 2 &&
          double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lng) &&
          double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)) {
        pts.Add((lat, lng));
      }
    }
    return pts;
  }

  private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
      root == null
          ? Enumerable.Empty<XElement>()
          : root.Descendants().Where(e => e.Name.LocalName == localName);
}
