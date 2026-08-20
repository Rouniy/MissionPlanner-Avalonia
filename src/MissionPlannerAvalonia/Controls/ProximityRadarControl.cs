using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace MissionPlannerAvalonia.Controls;

public sealed class ProximityRadarControl : Control {
  private readonly DispatcherTimer _timer;
  private double _radiusCm = 500;
  private double _vehicleSizeCm = 80;

  public ProximityRadarControl() {
    Focusable = true;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += (_, _) => InvalidateVisual();
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _timer.Start();
    Focus();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _timer.Stop();
    base.OnDetachedFromVisualTree(e);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    switch (e.Key) {
      case Key.Add:
      case Key.OemPlus:
        _radiusCm = Math.Max(50, _radiusCm - 50);
        e.Handled = true;
        break;
      case Key.Subtract:
      case Key.OemMinus:
        _radiusCm += 50;
        e.Handled = true;
        break;
      case Key.OemOpenBrackets:
        _vehicleSizeCm = Math.Max(10, _vehicleSizeCm - 10);
        e.Handled = true;
        break;
      case Key.OemCloseBrackets:
        _vehicleSizeCm += 10;
        e.Handled = true;
        break;
    }
    InvalidateVisual();
    base.OnKeyDown(e);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(new SolidColorBrush(Color.Parse("#151817")), Bounds);

    var center = Bounds.Center;
    var maxPixels = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 35);
    var scale = _radiusCm / maxPixels;
    var gridPen = new Pen(new SolidColorBrush(Color.Parse("#46504B")), 1);
    var dangerPen = new Pen(Brushes.Red, 3);
    var customPen = new Pen(Brushes.Gold, 3);

    for (double cm = 50; cm <= _radiusCm; cm += 50) {
      var r = cm / scale;
      context.DrawEllipse(null, gridPen, center, r, r);
      DrawText(context, $"{cm / 100:0.0}m", new Point(center.X + 4, center.Y - r - 14),
          Brushes.LimeGreen, 11);
    }

    for (double angle = 0; angle < 360; angle += 45) {
      var edge = Polar(center, maxPixels, angle);
      context.DrawLine(gridPen, center, edge);
      var label = Polar(center, maxPixels + 12, angle);
      DrawCenteredText(context, angle.ToString("0", CultureInfo.InvariantCulture), label,
          Brushes.Gray, 11);
    }

    var vehicleRadius = Math.Max(4, _vehicleSizeCm / scale / 2);
    context.DrawEllipse(new SolidColorBrush(Color.Parse("#2F81F7")), new Pen(Brushes.White, 1),
        center, vehicleRadius, vehicleRadius);
    context.DrawLine(new Pen(Brushes.White, 2), center,
        new Point(center.X, center.Y - vehicleRadius - 8));

    var proximity = AppState.comPort.MAV?.Proximity;
    var samples = proximity?.DirectionState.GetRaw().ToList();
    if (samples == null || samples.Count == 0) {
      DrawCenteredText(context, "Waiting for DISTANCE_SENSOR / OBSTACLE_DISTANCE",
          new Point(center.X, Bounds.Bottom - 18), Brushes.Gray, 12);
      return;
    }

    foreach (var sample in samples) {
      var angle = sample.Orientation == MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_CUSTOM
          ? sample.Angle
          : OrientationAngle(sample.Orientation);
      var distance = Math.Min(sample.Distance, _radiusCm);
      var radius = distance / scale;
      var width = sample.Orientation == MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_CUSTOM
          ? sample.Size
          : 45;

      DrawArc(context, center, radius, angle - width / 2, angle + width / 2,
          sample.Orientation == MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_CUSTOM
              ? customPen
              : dangerPen);
      var labelAt = Polar(center, Math.Max(20, radius - 12), angle);
      DrawCenteredText(context, $"{sample.Distance / 100:0.0}m", labelAt, Brushes.LimeGreen, 12);
    }

    DrawText(context, $"Radius: {_radiusCm / 100:0.0} m (+/−), vehicle: {_vehicleSizeCm / 100:0.0} m ([/])",
        new Point(8, 8), Brushes.White, 12);
  }

  private static double OrientationAngle(MAVLink.MAV_SENSOR_ORIENTATION orientation) => orientation switch {
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_NONE => 0,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_45 => 45,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_90 => 90,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_135 => 135,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_180 => 180,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_225 => 225,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_270 => 270,
    MAVLink.MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_315 => 315,
    _ => 0,
  };

  private static Point Polar(Point center, double radius, double angleDegrees) {
    var radians = angleDegrees * Math.PI / 180.0;
    return new Point(center.X + Math.Sin(radians) * radius,
        center.Y - Math.Cos(radians) * radius);
  }

  private static void DrawArc(DrawingContext context, Point center, double radius,
                              double startAngle, double endAngle, Pen pen) {
    if (radius <= 0) {
      return;
    }
    var figure = new PathFigure { StartPoint = Polar(center, radius, startAngle) };
    figure.Segments!.Add(new ArcSegment {
      Point = Polar(center, radius, endAngle),
      Size = new Size(radius, radius),
      SweepDirection = SweepDirection.Clockwise,
      IsLargeArc = Math.Abs(endAngle - startAngle) > 180,
    });
    var geometry = new PathGeometry();
    geometry.Figures!.Add(figure);
    context.DrawGeometry(null, pen, geometry);
  }

  private static void DrawText(DrawingContext context, string text, Point point,
                               IBrush brush, double size) =>
      context.DrawText(MakeText(text, brush, size), point);

  private static void DrawCenteredText(DrawingContext context, string text, Point center,
                                       IBrush brush, double size) {
    var formatted = MakeText(text, brush, size);
    context.DrawText(formatted,
        new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
  }

  private static FormattedText MakeText(string text, IBrush brush, double size) =>
      new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
          new Typeface("Inter"), size, brush);
}
