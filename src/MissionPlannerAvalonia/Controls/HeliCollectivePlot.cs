using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Controls;

/// <summary>
/// Native Avalonia replacement for the ZedGraph collective plot in ConfigTradHeli.
/// </summary>
public sealed class HeliCollectivePlot : Control {
  public static readonly StyledProperty<IReadOnlyList<HeliCurvePoint>?> StabilizeCurveProperty =
      AvaloniaProperty.Register<HeliCollectivePlot, IReadOnlyList<HeliCurvePoint>?>(
          nameof(StabilizeCurve));
  public static readonly StyledProperty<IReadOnlyList<HeliCurvePoint>?> AcroCurveProperty =
      AvaloniaProperty.Register<HeliCollectivePlot, IReadOnlyList<HeliCurvePoint>?>(
          nameof(AcroCurve));
  public static readonly StyledProperty<double> CursorPercentProperty =
      AvaloniaProperty.Register<HeliCollectivePlot, double>(nameof(CursorPercent));

  private static readonly IBrush _background = new SolidColorBrush(Color.Parse("#151817"));
  private static readonly IBrush _grid = new SolidColorBrush(Color.Parse("#46504B"));
  private static readonly IBrush _stabilize = new SolidColorBrush(Color.Parse("#1E90FF"));

  static HeliCollectivePlot() {
    AffectsRender<HeliCollectivePlot>(
        StabilizeCurveProperty, AcroCurveProperty, CursorPercentProperty);
  }

  public IReadOnlyList<HeliCurvePoint>? StabilizeCurve {
    get => GetValue(StabilizeCurveProperty);
    set => SetValue(StabilizeCurveProperty, value);
  }

  public IReadOnlyList<HeliCurvePoint>? AcroCurve {
    get => GetValue(AcroCurveProperty);
    set => SetValue(AcroCurveProperty, value);
  }

  public double CursorPercent {
    get => GetValue(CursorPercentProperty);
    set => SetValue(CursorPercentProperty, value);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(_background, Bounds);
    if (Bounds.Width < 140 || Bounds.Height < 120) {
      return;
    }

    var plot = new Rect(52, 28, Math.Max(1, Bounds.Width - 66),
        Math.Max(1, Bounds.Height - 70));
    var gridPen = new Pen(_grid, 1);
    var axisPen = new Pen(Brushes.LightGray, 1.5);
    for (int input = 0; input <= 100; input += 20) {
      double x = X(plot, input);
      context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
      DrawCentered(context, input.ToString(CultureInfo.InvariantCulture),
          new Point(x, plot.Bottom + 12), Brushes.LightGray, 10);
    }
    for (int output = 0; output <= 1000; output += 200) {
      double y = Y(plot, output);
      context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
      DrawRight(context, output.ToString(CultureInfo.InvariantCulture),
          new Point(plot.Left - 5, y), Brushes.LightGray, 10);
    }
    context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
    context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);

    DrawCurve(context, plot, StabilizeCurve, new Pen(_stabilize, 2.5), true);
    DrawCurve(context, plot, AcroCurve, new Pen(Brushes.Gold, 2), false);

    double cursor = double.IsFinite(CursorPercent) ? Math.Clamp(CursorPercent, 0, 100) : 0;
    double cursorX = X(plot, cursor);
    context.DrawLine(new Pen(Brushes.Red, 2), new Point(cursorX, plot.Top),
        new Point(cursorX, plot.Bottom));

    DrawText(context, "Collective Control", new Point(plot.Left, 5), Brushes.White, 13);
    DrawCentered(context, "Collective Input (%)",
        new Point(plot.Center.X, Bounds.Bottom - 9), Brushes.LightGray, 11);
    DrawText(context, "Output", new Point(5, 7), Brushes.LightGray, 10);
    DrawText(context, "Stabilize", new Point(plot.Right - 122, 7), _stabilize, 10);
    DrawText(context, "Acro", new Point(plot.Right - 60, 7), Brushes.Gold, 10);
  }

  private static void DrawCurve(DrawingContext context, Rect plot,
      IReadOnlyList<HeliCurvePoint>? points, Pen pen, bool markers) {
    if (points == null || points.Count == 0) {
      return;
    }
    Point? previous = null;
    foreach (HeliCurvePoint point in points) {
      if (!double.IsFinite(point.InputPercent) || !double.IsFinite(point.Output)) {
        continue;
      }
      var current = new Point(X(plot, point.InputPercent), Y(plot, point.Output));
      if (previous is { } from) {
        context.DrawLine(pen, from, current);
      }
      if (markers) {
        context.DrawEllipse(_background, pen, current, 3.5, 3.5);
      }
      previous = current;
    }
  }

  private static double X(Rect plot, double input) =>
      plot.Left + Math.Clamp(input, 0, 100) * plot.Width / 100;

  private static double Y(Rect plot, double output) =>
      plot.Bottom - Math.Clamp(output, 0, 1000) * plot.Height / 1000;

  private static FormattedText Text(string text, IBrush brush, double size) =>
      new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
          new Typeface("Inter"), size, brush);

  private static void DrawText(DrawingContext context, string text, Point point,
      IBrush brush, double size) => context.DrawText(Text(text, brush, size), point);

  private static void DrawCentered(DrawingContext context, string text, Point center,
      IBrush brush, double size) {
    FormattedText formatted = Text(text, brush, size);
    context.DrawText(formatted,
        new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
  }

  private static void DrawRight(DrawingContext context, string text, Point centerRight,
      IBrush brush, double size) {
    FormattedText formatted = Text(text, brush, size);
    context.DrawText(formatted,
        new Point(centerRight.X - formatted.Width, centerRight.Y - formatted.Height / 2));
  }
}
