using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Mapsui;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

internal readonly record struct OverlapCoveragePoint(double Lat, double Lng, int Count);

internal static class OverlapCoverageBuilder {
  internal const double StepDegrees = 0.0001;

  private static readonly Color[] _colors = {
    new(128, 0, 128, 140),
    new(0, 0, 255, 140),
    new(0, 255, 255, 140),
    new(0, 128, 0, 140),
    new(255, 255, 0, 140),
    new(255, 165, 0, 140),
    new(255, 0, 0, 140),
    new(139, 0, 0, 140),
  };

  internal static IReadOnlyList<Color> Colors => _colors;

  internal static IReadOnlyList<OverlapCoveragePoint> Build(
      IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> footprints,
      CancellationToken cancellationToken = default) {
    if (footprints.Count == 0) {
      return Array.Empty<OverlapCoveragePoint>();
    }

    var polygons = new List<Polygon>(footprints.Count);
    foreach (IReadOnlyList<(double Lat, double Lng)> footprint in footprints) {
      cancellationToken.ThrowIfCancellationRequested();
      Coordinate[] coordinates = footprint
          .Where(point => IsValidCoordinate(point.Lat, point.Lng))
          .Select(point => new Coordinate(point.Lng, point.Lat))
          .ToArray();
      if (coordinates.Length < 3) {
        continue;
      }
      if (!coordinates[0].Equals2D(coordinates[^1])) {
        coordinates = coordinates.Append(new Coordinate(coordinates[0])).ToArray();
      }
      var polygon = new Polygon(new LinearRing(coordinates));
      if (!polygon.IsEmpty) {
        polygons.Add(polygon);
      }
    }
    if (polygons.Count == 0) {
      return Array.Empty<OverlapCoveragePoint>();
    }

    double minLat = polygons.Min(polygon => polygon.EnvelopeInternal.MinY);
    double maxLat = polygons.Max(polygon => polygon.EnvelopeInternal.MaxY);
    double minLng = polygons.Min(polygon => polygon.EnvelopeInternal.MinX);
    double maxLng = polygons.Max(polygon => polygon.EnvelopeInternal.MaxX);
    // Preserve the official GMapMarkerOverlapCount guard and fixed sampling lattice.
    if (maxLat - minLat > 1 || maxLng - minLng > 1) {
      return Array.Empty<OverlapCoveragePoint>();
    }

    double startLat = Math.Round(maxLat, 4);
    double startLng = Math.Round(minLng, 4);
    var result = new List<OverlapCoveragePoint>();
    var factory = new GeometryFactory();
    for (double rawLat = startLat; rawLat >= minLat; rawLat -= StepDegrees) {
      cancellationToken.ThrowIfCancellationRequested();
      double lat = Math.Round(rawLat, 4);
      long column = 0;
      for (double rawLng = startLng; rawLng <= maxLng; rawLng += StepDegrees) {
        if ((column++ & 255) == 0) {
          cancellationToken.ThrowIfCancellationRequested();
        }
        double lng = Math.Round(rawLng, 4);
        var point = factory.CreatePoint(new Coordinate(lng, lat));
        int count = 0;
        foreach (Polygon polygon in polygons) {
          if (polygon.EnvelopeInternal.Contains(lng, lat) && polygon.Covers(point)) {
            count++;
          }
        }
        if (count > 0) {
          result.Add(new OverlapCoveragePoint(lat, lng, count));
        }
      }
    }
    return result;
  }

  internal static Color ColorForCount(int count) =>
      _colors[Math.Clamp(count - 1, 0, _colors.Length - 1)];

  private static bool IsValidCoordinate(double lat, double lng) =>
      double.IsFinite(lat) && double.IsFinite(lng) &&
      lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
}
