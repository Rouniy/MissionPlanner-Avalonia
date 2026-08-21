using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MissionPlannerAvalonia.Services;

internal static class PlannerTextGeometry {
  internal static IReadOnlyList<(double X, double Y)> Create(
      string text, double sizeMeters, double rotationDegrees) {
    if (string.IsNullOrWhiteSpace(text) || !double.IsFinite(sizeMeters) || sizeMeters <= 0
        || !double.IsFinite(rotationDegrees)) {
      return [];
    }

    using var path = new GraphicsPath();
    path.AddString(text, FontFamily.GenericSansSerif, (int)FontStyle.Regular,
        (float)(sizeMeters * 1.35), new PointF(0, 0), StringFormat.GenericTypographic);
    double radians = rotationDegrees * System.Math.PI / 180.0;
    double cos = System.Math.Cos(radians);
    double sin = System.Math.Sin(radians);

    var result = new List<(double X, double Y)>();
    foreach (PointF point in path.PathPoints) {
      var candidate = (
          X: point.X * cos - point.Y * sin,
          Y: point.X * sin + point.Y * cos);
      if (result.Count == 0
          || System.Math.Abs(result[^1].X - candidate.X) > 0.01
          || System.Math.Abs(result[^1].Y - candidate.Y) > 0.01) {
        result.Add(candidate);
      }
    }
    return result;
  }
}
