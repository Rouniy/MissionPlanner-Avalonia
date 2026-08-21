using System;
using System.Collections.Generic;
using System.Linq;
using ClipperLib;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal static class PlannerGeometry {
  private const double _scale = 1000.0;

  internal static IReadOnlyList<PointLatLngAlt> OffsetPolygon(
      IReadOnlyList<PointLatLngAlt> points, double offsetMeters) {
    if (points.Count < 3 || !double.IsFinite(offsetMeters) || offsetMeters == 0) {
      return points.Select(Clone).ToList();
    }

    int zone = points[0].GetUTMZone();
    var path = points.Select(point => {
      double[] utm = point.ToUTM(zone);
      return new IntPoint(utm[0] * _scale, utm[1] * _scale);
    }).ToList();
    if (path.Count > 1 && path[0] == path[^1]) {
      path.RemoveAt(path.Count - 1);
    }

    var offset = new ClipperOffset();
    offset.AddPath(path, JoinType.jtMiter, EndType.etClosedPolygon);
    var tree = new PolyTree();
    offset.Execute(ref tree, offsetMeters * _scale);

    var contour = tree.Childs
        .Select(child => child.Contour)
        .Where(candidate => candidate.Count >= 3)
        .OrderByDescending(candidate => Math.Abs(Clipper.Area(candidate)))
        .FirstOrDefault();
    if (contour == null) {
      return Array.Empty<PointLatLngAlt>();
    }

    return contour.Select(point =>
        new utmpos(point.X / _scale, point.Y / _scale, zone).ToLLA()).ToList();
  }

  private static PointLatLngAlt Clone(PointLatLngAlt point) =>
      new(point.Lat, point.Lng, point.Alt, point.Tag);
}
