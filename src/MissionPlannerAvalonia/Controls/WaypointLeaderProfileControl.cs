using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Controls;

/// <summary>Native altitude-over-mission graph equivalent to WaypointLeader's ZedGraph view.</summary>
public sealed class WaypointLeaderProfileControl : Control {
  public static readonly StyledProperty<IReadOnlyList<WaypointLeaderProfilePoint>?> ProfileProperty =
      AvaloniaProperty.Register<WaypointLeaderProfileControl,
          IReadOnlyList<WaypointLeaderProfilePoint>?>(nameof(Profile));
  public static readonly StyledProperty<IEnumerable<WaypointLeaderVehicleItem>?> ItemsProperty =
      AvaloniaProperty.Register<WaypointLeaderProfileControl,
          IEnumerable<WaypointLeaderVehicleItem>?>(nameof(Items));

  private static readonly IBrush BackgroundBrush =
      new SolidColorBrush(Color.Parse("#151817"));
  private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#46504B"));
  private static readonly IBrush PathBrush = new SolidColorBrush(Color.Parse("#FF5B72"));
  private readonly DispatcherTimer _timer;

  static WaypointLeaderProfileControl() =>
      AffectsRender<WaypointLeaderProfileControl>(ProfileProperty, ItemsProperty);

  public WaypointLeaderProfileControl() {
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    _timer.Tick += (_, _) => InvalidateVisual();
    AttachedToVisualTree += (_, _) => _timer.Start();
    DetachedFromVisualTree += (_, _) => _timer.Stop();
  }

  public IReadOnlyList<WaypointLeaderProfilePoint>? Profile {
    get => GetValue(ProfileProperty);
    set => SetValue(ProfileProperty, value);
  }

  public IEnumerable<WaypointLeaderVehicleItem>? Items {
    get => GetValue(ItemsProperty);
    set => SetValue(ItemsProperty, value);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(BackgroundBrush, Bounds);
    if (Bounds.Width < 180 || Bounds.Height < 130) {
      return;
    }

    WaypointLeaderProfilePoint[] profile = Profile?
        .Where(point => double.IsFinite(point.DistanceM) && double.IsFinite(point.AltitudeM))
        .OrderBy(point => point.DistanceM).ToArray() ?? [];
    var plot = new Rect(56, 26, Math.Max(1, Bounds.Width - 72),
        Math.Max(1, Bounds.Height - 68));
    if (profile.Length < 2 || profile[^1].DistanceM <= 0) {
      DrawCentered(context, "Select an air master with a downloaded waypoint mission",
          plot.Center, Brushes.LightGray, 12);
      return;
    }

    WaypointLeaderVehicleItem[] vehicles = Items?.Where(item =>
        double.IsFinite(item.PathDistanceM) && double.IsFinite(item.CurrentAltitudeM)).ToArray() ?? [];
    double minAltitude = Math.Min(0, profile.Min(point => point.AltitudeM));
    double maxAltitude = Math.Max(1, profile.Max(point => point.AltitudeM));
    if (vehicles.Length > 0) {
      minAltitude = Math.Min(minAltitude, vehicles.Min(item => item.CurrentAltitudeM));
      maxAltitude = Math.Max(maxAltitude, vehicles.Max(item => item.CurrentAltitudeM));
    }
    double padding = Math.Max(2, (maxAltitude - minAltitude) * 0.1);
    minAltitude -= padding;
    maxAltitude += padding;
    double length = profile[^1].DistanceM;

    var gridPen = new Pen(GridBrush, 1);
    var axisPen = new Pen(Brushes.LightGray, 1.5);
    for (int step = 0; step <= 4; step++) {
      double ratio = step / 4d;
      double x = plot.Left + ratio * plot.Width;
      double y = plot.Bottom - ratio * plot.Height;
      context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
      context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
      DrawCentered(context, (length * ratio).ToString("0", CultureInfo.InvariantCulture),
          new Point(x, plot.Bottom + 12), Brushes.LightGray, 10);
      DrawRight(context,
          (minAltitude + (maxAltitude - minAltitude) * ratio)
              .ToString("0.0", CultureInfo.InvariantCulture),
          new Point(plot.Left - 5, y), Brushes.LightGray, 10);
    }
    context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
    context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);

    Point? previous = null;
    var pathPen = new Pen(PathBrush, 2.5);
    foreach (WaypointLeaderProfilePoint point in profile) {
      Point current = PlotPoint(plot, point.DistanceM, point.AltitudeM,
          length, minAltitude, maxAltitude);
      if (previous is { } start) {
        context.DrawLine(pathPen, start, current);
      }
      context.DrawEllipse(BackgroundBrush, pathPen, current, 3, 3);
      previous = current;
    }

    foreach (WaypointLeaderVehicleItem vehicle in vehicles) {
      Point point = PlotPoint(plot, vehicle.PathDistanceM, vehicle.CurrentAltitudeM,
          length, minAltitude, maxAltitude);
      IBrush brush = vehicle.IsGroundMaster
          ? Brushes.LimeGreen
          : vehicle.IsAirMaster
              ? Brushes.Gold
              : Brushes.DeepSkyBlue;
      context.DrawEllipse(brush, new Pen(Brushes.Black, 1), point, 5, 5);
      DrawText(context, vehicle.SystemId.ToString(CultureInfo.InvariantCulture),
          new Point(point.X + 7, point.Y - 8), brush, 10);
    }

    DrawText(context, "Mission altitude profile", new Point(plot.Left, 4), Brushes.White, 13);
    DrawCentered(context, "Distance along mission (m)",
        new Point(plot.Center.X, Bounds.Bottom - 9), Brushes.LightGray, 11);
    DrawText(context, "Alt (m)", new Point(5, 7), Brushes.LightGray, 10);
    DrawText(context, "Ground", new Point(plot.Right - 180, 5), Brushes.LimeGreen, 10);
    DrawText(context, "Air", new Point(plot.Right - 120, 5), Brushes.Gold, 10);
    DrawText(context, "Follower", new Point(plot.Right - 78, 5), Brushes.DeepSkyBlue, 10);
  }

  private static Point PlotPoint(
      Rect plot, double distance, double altitude, double length,
      double minAltitude, double maxAltitude) => new(
      plot.Left + Math.Clamp(distance / length, 0, 1) * plot.Width,
      plot.Bottom - Math.Clamp(
          (altitude - minAltitude) / (maxAltitude - minAltitude), 0, 1) * plot.Height);

  private static FormattedText Text(string value, IBrush brush, double size) =>
      new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
          new Typeface("Inter"), size, brush);

  private static void DrawText(
      DrawingContext context, string value, Point point, IBrush brush, double size) =>
      context.DrawText(Text(value, brush, size), point);

  private static void DrawCentered(
      DrawingContext context, string value, Point center, IBrush brush, double size) {
    FormattedText formatted = Text(value, brush, size);
    context.DrawText(formatted,
        new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
  }

  private static void DrawRight(
      DrawingContext context, string value, Point right, IBrush brush, double size) {
    FormattedText formatted = Text(value, brush, size);
    context.DrawText(formatted,
        new Point(right.X - formatted.Width, right.Y - formatted.Height / 2));
  }
}
