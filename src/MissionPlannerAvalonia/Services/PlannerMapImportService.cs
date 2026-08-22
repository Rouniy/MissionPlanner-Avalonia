using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DotSpatial.Projections;
using MissionPlanner.Utilities;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using DxfDocument = netDxf.DxfDocument;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct ImportedGeoPoint(double Lat, double Lng, double Alt = double.NaN);

internal readonly record struct ImportedMapColor(byte R, byte G, byte B, byte A = 255);

internal sealed record ImportedOverlayRoute(
    string Name,
    IReadOnlyList<ImportedGeoPoint> Points,
    ImportedMapColor Color,
    bool Closed = false,
    double Width = 3);

internal sealed record ImportedOverlayMarker(
    string Name,
    ImportedGeoPoint Point,
    bool ShowLabel = false);

internal sealed record ImportedOverlayRaster(
    string Name,
    byte[] Png,
    double North,
    double South,
    double East,
    double West);

internal sealed record ImportedMapOverlay(
    IReadOnlyList<ImportedOverlayRoute> Routes,
    IReadOnlyList<ImportedOverlayMarker> Markers,
    IReadOnlyList<ImportedOverlayRaster> Rasters) {
  internal ImportedMapOverlay(
      IReadOnlyList<ImportedOverlayRoute> routes,
      IReadOnlyList<ImportedOverlayMarker> markers)
      : this(routes, markers, Array.Empty<ImportedOverlayRaster>()) {
  }

  internal static ImportedMapOverlay Empty { get; } = new(
      Array.Empty<ImportedOverlayRoute>(),
      Array.Empty<ImportedOverlayMarker>(),
      Array.Empty<ImportedOverlayRaster>());

  internal int PointCount => Routes.Sum(route => route.Points.Count) + Markers.Count;
  internal bool HasContent => Routes.Count > 0 || Markers.Count > 0 || Rasters.Count > 0;
}

internal sealed record ImportedMissionPoint(double Lat, double Lng, double Alt, double Order);

internal sealed record ShapefileMissionImport(
    IReadOnlyList<ImportedMissionPoint> Points,
    string? ProjectionName,
    bool SortedByWaypointAttribute);

internal sealed record ShapefilePolygonImport(
    IReadOnlyList<ImportedGeoPoint> Points,
    string? ProjectionName,
    int FeatureCount);

internal sealed record ShapefilePolyExportResult(
    IReadOnlyList<string> Files,
    int PointCount,
    string? ProjectionName);

internal static class ShapefileImportService {
  private static readonly ProjectionInfo _wgs84 =
      KnownCoordinateSystems.Geographic.World.WGS1984;

  static ShapefileImportService() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  }

  internal static ShapefileMissionImport ReadMission(string path) {
    ShapefileSource source = ReadSource(path);
    var points = new List<ImportedMissionPoint>(source.Records.Count);
    bool hasWaypointAttribute = source.Records.Any(record =>
        TryAttribute(record.Attributes, "wp", out _));

    foreach (ShapefileRecord record in source.Records) {
      if (record.Geometry is not Point point || point.IsEmpty || point.Coordinate == null) {
        continue;
      }
      Coordinate coordinate = point.Coordinate;
      double altitude = -1;
      if ((!TryNumericAttribute(record.Attributes, "ELEVATION", out altitude) || altitude == -1) &&
          (!TryNumericAttribute(record.Attributes, "alt", out altitude) || altitude == -1)) {
        altitude = double.IsFinite(coordinate.Z) ? coordinate.Z : -1;
      }
      double order = TryNumericAttribute(record.Attributes, "wp", out double parsedOrder)
          ? parsedOrder
          : 0;
      ImportedGeoPoint transformed = Transform(
          coordinate.X, coordinate.Y, altitude, source.Projection);
      if (IsValid(transformed)) {
        points.Add(new ImportedMissionPoint(
            transformed.Lat, transformed.Lng, transformed.Alt, order));
      }
    }

    if (hasWaypointAttribute) {
      points.Sort((left, right) => left.Order.CompareTo(right.Order));
    }
    return new ShapefileMissionImport(points, source.Projection?.Name, hasWaypointAttribute);
  }

  internal static ShapefilePolygonImport ReadPolygon(string path) {
    ShapefileSource source = ReadSource(path);
    var points = new List<ImportedGeoPoint>();
    int featureCount = 0;
    foreach (ShapefileRecord record in source.Records) {
      if (record.Geometry.IsEmpty) {
        continue;
      }
      var featurePoints = record.Geometry.Coordinates
          .Select(coordinate => Transform(
              coordinate.X, coordinate.Y,
              double.IsFinite(coordinate.Z) ? coordinate.Z : 0,
              source.Projection))
          .Where(IsValid)
          .ToList();
      if (featurePoints.Count == 0) {
        continue;
      }
      if (featurePoints.Count > 1 && SamePosition(featurePoints[0], featurePoints[^1])) {
        featurePoints.RemoveAt(featurePoints.Count - 1);
      }
      points.AddRange(featurePoints);
      featureCount++;
    }
    return new ShapefilePolygonImport(points, source.Projection?.Name, featureCount);
  }

  internal static ShapefilePolyExportResult ExportPolyFiles(
      string path, CancellationToken cancellationToken = default) {
    ShapefileSource source = ReadSource(path);
    string directory = Path.GetDirectoryName(Path.GetFullPath(path))
        ?? throw new InvalidOperationException("The shapefile has no parent directory.");
    var files = new List<string>();
    int pointCount = 0;

    foreach (ShapefileRecord record in source.Records) {
      cancellationToken.ThrowIfCancellationRequested();
      if (record.Geometry.IsEmpty) {
        continue;
      }
      ImportedGeoPoint[] points = record.Geometry.Coordinates
          .Select(coordinate => Transform(
              coordinate.X, coordinate.Y,
              double.IsFinite(coordinate.Z) ? coordinate.Z : 0,
              source.Projection))
          .Where(IsValid)
          .ToArray();
      if (points.Length == 0) {
        continue;
      }

      string output = Path.Combine(directory, $"poly-{files.Count + 1}.poly");
      WritePolyAtomic(output, points, cancellationToken);
      files.Add(output);
      pointCount += points.Length;
    }

    return new ShapefilePolyExportResult(files, pointCount, source.Projection?.Name);
  }

  internal static ImportedMapOverlay ReadOverlay(string path) {
    ShapefileSource source = ReadSource(path);
    var routes = new List<ImportedOverlayRoute>();
    var markers = new List<ImportedOverlayMarker>();
    for (int index = 0; index < source.Records.Count; index++) {
      AddGeometry(source.Records[index].Geometry, $"SHP {index + 1}", source.Projection,
          routes, markers);
    }
    return new ImportedMapOverlay(routes, markers);
  }

  private static ShapefileSource ReadSource(string path) {
    if (!File.Exists(path)) {
      throw new FileNotFoundException("Shapefile not found.", path);
    }
    var options = new ShapefileReaderOptions();
    string? cpgPath = FindSidecar(path, ".cpg");
    if (cpgPath != null && TryReadCodePage(cpgPath) is { } encoding) {
      options.Encoding = encoding;
    }

    var records = new List<ShapefileRecord>();
    string? dbfPath = FindSidecar(path, ".dbf");
    if (dbfPath != null) {
      using var shp = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var dbf = File.Open(dbfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
      foreach (IFeature feature in Shapefile.ReadAllFeatures(shp, dbf, options)) {
        records.Add(new ShapefileRecord(feature.Geometry,
            Attributes(feature.Attributes)));
      }
    } else {
      foreach (Geometry geometry in Shapefile.ReadAllGeometries(path, options)) {
        records.Add(new ShapefileRecord(geometry,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
      }
    }

    return new ShapefileSource(records, ReadProjection(path));
  }

  private static Encoding? TryReadCodePage(string path) {
    string value = File.ReadAllText(path).TrimStart('\uFEFF').Trim();
    if (string.IsNullOrWhiteSpace(value)) {
      return null;
    }
    try {
      return Encoding.GetEncoding(value);
    } catch (ArgumentException) {
    }

    string upper = value.ToUpperInvariant();
    string digits = new(value.Where(char.IsDigit).ToArray());
    if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture,
            out int numericCodePage) &&
        (digits == value || upper.StartsWith("ANSI", StringComparison.Ordinal) ||
         upper.StartsWith("OEM", StringComparison.Ordinal))) {
      try {
        return Encoding.GetEncoding(numericCodePage);
      } catch (ArgumentException) {
      }
    }

    string compact = new(upper.Where(char.IsLetterOrDigit).ToArray());
    const string isoPrefix = "ISO8859";
    if (compact.StartsWith(isoPrefix, StringComparison.Ordinal) &&
        compact.Length > isoPrefix.Length) {
      try {
        return Encoding.GetEncoding("iso-8859-" + compact[isoPrefix.Length..]);
      } catch (ArgumentException) {
      }
    }
    // A CPG file is only an attribute-encoding hint. Preserve geometry import when a
    // legacy or malformed value cannot be resolved, as upstream does via DBF metadata.
    return null;
  }

  private static Dictionary<string, object?> Attributes(IAttributesTable table) {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    foreach (string name in table.GetNames()) {
      result[name] = table[name];
    }
    return result;
  }

  private static ProjectionInfo? ReadProjection(string path) {
    string? prjPath = FindSidecar(path, ".prj");
    if (prjPath == null) {
      return null;
    }
    string? esri = File.ReadAllText(prjPath).Trim();
    if (string.IsNullOrWhiteSpace(esri)) {
      return null;
    }
    var projection = new ProjectionInfo();
    projection.ParseEsriString(esri);
    return projection;
  }

  internal static string? FindSidecar(string path, string extension) {
    string directory = Path.GetDirectoryName(path) ?? ".";
    string stem = Path.GetFileNameWithoutExtension(path);
    return Directory.EnumerateFiles(directory)
        .FirstOrDefault(candidate =>
            string.Equals(Path.GetFileNameWithoutExtension(candidate), stem,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(candidate), extension,
                StringComparison.OrdinalIgnoreCase));
  }

  private static ImportedGeoPoint Transform(double x, double y, double z,
                                             ProjectionInfo? source) {
    if (source == null) {
      return new ImportedGeoPoint(y, x, z);
    }
    double[] xy = { x, y };
    double[] altitude = { z };
    Reproject.ReprojectPoints(xy, altitude, source, _wgs84, 0, 1);
    return new ImportedGeoPoint(xy[1], xy[0], altitude[0]);
  }

  private static void WritePolyAtomic(
      string output, IReadOnlyList<ImportedGeoPoint> points,
      CancellationToken cancellationToken) {
    string temporary = output + $".{Guid.NewGuid():N}.tmp";
    try {
      using (var stream = new FileStream(
                 temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
        writer.NewLine = "\r\n";
        writer.WriteLine("#Shap to Poly - Mission Planner");
        foreach (ImportedGeoPoint point in points) {
          cancellationToken.ThrowIfCancellationRequested();
          writer.Write(point.Lat.ToString(CultureInfo.InvariantCulture));
          writer.Write('\t');
          writer.WriteLine(point.Lng.ToString(CultureInfo.InvariantCulture));
        }
      }
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporary, output, overwrite: true);
    } finally {
      try {
        File.Delete(temporary);
      } catch {
        // The destination is already complete; a failed best-effort temp cleanup is non-fatal.
      }
    }
  }

  internal static void AddGeometry(
      Geometry geometry,
      string name,
      ProjectionInfo? projection,
      ICollection<ImportedOverlayRoute> routes,
      ICollection<ImportedOverlayMarker> markers) {
    switch (geometry) {
      case Point point:
        AddMarker(point.Coordinate, name, projection, markers);
        return;
      case MultiPoint multiPoint:
        for (int index = 0; index < multiPoint.NumGeometries; index++) {
          AddGeometry(multiPoint.GetGeometryN(index), $"{name}.{index + 1}", projection,
              routes, markers);
        }
        return;
      case LineString line:
        AddRoute(line.Coordinates, name, projection, false, routes);
        return;
      case Polygon polygon:
        AddRoute(polygon.ExteriorRing.Coordinates, name, projection, true, routes);
        for (int index = 0; index < polygon.NumInteriorRings; index++) {
          AddRoute(polygon.GetInteriorRingN(index).Coordinates,
              $"{name}.hole{index + 1}", projection, true, routes);
        }
        return;
      case GeometryCollection collection:
        for (int index = 0; index < collection.NumGeometries; index++) {
          AddGeometry(collection.GetGeometryN(index), $"{name}.{index + 1}", projection,
              routes, markers);
        }
        return;
    }
  }

  private static void AddMarker(Coordinate coordinate, string name, ProjectionInfo? projection,
                                ICollection<ImportedOverlayMarker> markers) {
    ImportedGeoPoint point = Transform(coordinate.X, coordinate.Y,
        double.IsFinite(coordinate.Z) ? coordinate.Z : 0, projection);
    if (IsValid(point)) {
      markers.Add(new ImportedOverlayMarker(name, point));
    }
  }

  private static void AddRoute(
      IReadOnlyList<Coordinate> coordinates,
      string name,
      ProjectionInfo? projection,
      bool closed,
      ICollection<ImportedOverlayRoute> routes) {
    ImportedGeoPoint[] points = coordinates
        .Select(coordinate => Transform(
            coordinate.X, coordinate.Y,
            double.IsFinite(coordinate.Z) ? coordinate.Z : 0,
            projection))
        .Where(IsValid)
        .ToArray();
    if (points.Length >= 2) {
      routes.Add(new ImportedOverlayRoute(name, points,
          new ImportedMapColor(255, 0, 0), closed));
    }
  }

  private static bool TryAttribute(
      IReadOnlyDictionary<string, object?> attributes,
      string name,
      out object? value) => attributes.TryGetValue(name, out value) &&
      value != null && value != DBNull.Value;

  private static bool TryNumericAttribute(
      IReadOnlyDictionary<string, object?> attributes,
      string name,
      out double value) {
    value = 0;
    if (!TryAttribute(attributes, name, out object? raw)) {
      return false;
    }
    try {
      value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
      return double.IsFinite(value);
    } catch {
      return false;
    }
  }

  private static bool SamePosition(ImportedGeoPoint left, ImportedGeoPoint right) =>
      left.Lat == right.Lat && left.Lng == right.Lng;

  private static bool IsValid(ImportedGeoPoint point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;

  private sealed record ShapefileRecord(
      Geometry Geometry,
      IReadOnlyDictionary<string, object?> Attributes);

  private sealed record ShapefileSource(
      IReadOnlyList<ShapefileRecord> Records,
      ProjectionInfo? Projection);
}

internal static class DxfOverlayReader {
  internal static ImportedMapOverlay Read(string path, int? signedUtmZone = null) {
    ValidateZone(signedUtmZone);
    DxfDocument document = DxfDocument.Load(path)
        ?? throw new InvalidDataException("The DXF file is unsupported or invalid.");
    var routes = new List<ImportedOverlayRoute>();

    foreach (netDxf.Entities.Line line in document.Lines.Where(entity => entity.IsVisible)) {
      Add(routes, line.Handle, line.Color,
          new[] { line.StartPoint, line.EndPoint }, signedUtmZone);
    }
    foreach (netDxf.Entities.Polyline line in
             document.Polylines.Where(entity => entity.IsVisible)) {
      Add(routes, line.Handle, line.Color,
          line.Vertexes.Select(vertex => vertex.Position), signedUtmZone);
    }
    foreach (netDxf.Entities.LwPolyline line in
             document.LwPolylines.Where(entity => entity.IsVisible)) {
      Add(routes, line.Handle, line.Color,
          line.Vertexes.Select(vertex => new netDxf.Vector3(
              vertex.Position.X, vertex.Position.Y, line.Elevation)), signedUtmZone);
    }
    foreach (netDxf.Entities.MLine line in document.MLines.Where(entity => entity.IsVisible)) {
      Add(routes, line.Handle, line.Color,
          line.Vertexes.Select(vertex => new netDxf.Vector3(
              vertex.Location.X, vertex.Location.Y, line.Elevation)), signedUtmZone);
    }

    return new ImportedMapOverlay(routes, Array.Empty<ImportedOverlayMarker>());
  }

  internal static void ValidateZone(int? signedUtmZone) {
    if (signedUtmZone.HasValue &&
        Math.Abs((long)signedUtmZone.Value) is < 1 or > 60) {
      throw new ArgumentOutOfRangeException(nameof(signedUtmZone),
          "UTM zone must be 1 through 60; use a negative value for the southern hemisphere.");
    }
  }

  private static void Add(
      ICollection<ImportedOverlayRoute> routes,
      string? name,
      netDxf.AciColor color,
      IEnumerable<netDxf.Vector3> vertices,
      int? signedUtmZone) {
    ImportedGeoPoint[] points = vertices
        .Select(vertex => ConvertPoint(vertex, signedUtmZone))
        .Where(point => point.HasValue)
        .Select(point => point!.Value)
        .ToArray();
    if (points.Length < 2) {
      return;
    }
    routes.Add(new ImportedOverlayRoute(name ?? "DXF", points,
        new ImportedMapColor(color.R, color.G, color.B)));
  }

  private static ImportedGeoPoint? ConvertPoint(netDxf.Vector3 vertex, int? signedUtmZone) {
    ImportedGeoPoint point;
    if (signedUtmZone.HasValue) {
      PointLatLngAlt converted = new utmpos(vertex.X, vertex.Y, signedUtmZone.Value).ToLLA();
      point = new ImportedGeoPoint(converted.Lat, converted.Lng, vertex.Z);
    } else {
      point = new ImportedGeoPoint(vertex.Y, vertex.X, vertex.Z);
    }
    return double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
           point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180
        ? point
        : null;
  }
}
