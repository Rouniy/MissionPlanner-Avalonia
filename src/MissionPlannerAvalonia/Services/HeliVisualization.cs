using System;
using System.Collections.Generic;

namespace MissionPlannerAvalonia.Services;

public readonly record struct HeliCurvePoint(double InputPercent, double Output);

public readonly record struct HeliInputRange(double Minimum, double Maximum) {
  public bool HasSamples => Minimum <= Maximum;
}

/// <summary>
/// Portable calculations used by Mission Planner's Traditional Heli live setup display.
/// Keeping these independent of Avalonia makes the upstream curve and range semantics testable.
/// </summary>
public static class HeliVisualization {
  public static IReadOnlyList<HeliCurvePoint> BuildStabilizeCurve(
      double point0, double point40, double point60, double point100) => [
    new(0, FiniteOrZero(point0)),
    new(40, FiniteOrZero(point40)),
    new(60, FiniteOrZero(point60)),
    new(100, FiniteOrZero(point100)),
  ];

  public static IReadOnlyList<HeliCurvePoint> BuildAcroCurve(double expo) {
    expo = double.IsFinite(expo) ? Math.Clamp(expo, 0, 1) : 0;
    var points = new HeliCurvePoint[101];
    for (int input = 0; input <= 100; input++) {
      // This is the exact ConfigTradHeli.GenerateGraphData formula.
      double normalized = (input - 50.0) / 50.0;
      double shaped = expo * normalized * normalized * normalized
          + (1 - expo) * normalized;
      points[input] = new HeliCurvePoint(input, 500 + shaped * 500);
    }
    return points;
  }

  public static double MapCollectiveCursor(double pwm, double minimum, double maximum) {
    if (!double.IsFinite(pwm) || !double.IsFinite(minimum) || !double.IsFinite(maximum)
        || maximum <= minimum) {
      return 0;
    }
    return Math.Clamp((pwm - minimum) * 100 / (maximum - minimum), 0, 100);
  }

  public static HeliInputRange CaptureRange(
      HeliInputRange current, double pwm, bool manualServoActive) {
    if (!manualServoActive || !double.IsFinite(pwm) || pwm is < 800 or > 2200) {
      return current;
    }
    if (!current.HasSamples) {
      return new HeliInputRange(pwm, pwm);
    }
    return new HeliInputRange(Math.Min(current.Minimum, pwm), Math.Max(current.Maximum, pwm));
  }

  private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
