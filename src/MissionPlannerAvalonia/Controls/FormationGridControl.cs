using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Controls;

/// <summary>
/// Native replacement for MissionPlanner.Swarm.Grid. Followers can be dragged in formation-local
/// X/Y coordinates; the numeric grid remains the precise editor for X/Y/Z.
/// </summary>
public sealed class FormationGridControl : Control {
  public static readonly StyledProperty<IEnumerable<FormationVehicleItem>?> ItemsProperty =
      AvaloniaProperty.Register<FormationGridControl, IEnumerable<FormationVehicleItem>?>(
          nameof(Items));

  private readonly DispatcherTimer _timer;
  private FormationVehicleItem? _dragged;
  private double _halfSpanM = 25;

  public FormationGridControl() {
    ClipToBounds = true;
    Focusable = true;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _timer.Tick += (_, _) => InvalidateVisual();
  }

  static FormationGridControl() => AffectsRender<FormationGridControl>(ItemsProperty);

  public IEnumerable<FormationVehicleItem>? Items {
    get => GetValue(ItemsProperty);
    set => SetValue(ItemsProperty, value);
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _timer.Start();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _timer.Stop();
    _dragged = null;
    base.OnDetachedFromVisualTree(e);
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
      return;
    }
    Point point = e.GetPosition(this);
    _dragged = Rows()
        .Where(row => row.Included && row.IsEligible && !row.IsLeader && HasFiniteOffset(row))
        .OrderBy(row => DistanceSquared(point, ScreenPoint(row)))
        .FirstOrDefault(row => DistanceSquared(point, ScreenPoint(row)) <= 20 * 20);
    if (_dragged == null) {
      return;
    }
    e.Pointer.Capture(this);
    e.Handled = true;
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (_dragged == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
      return;
    }
    Point point = e.GetPosition(this);
    Rect plot = PlotBounds();
    double pixelsPerMeter = PixelsPerMeter(plot);
    double x = Math.Clamp((point.X - plot.Center.X) / pixelsPerMeter,
        -_halfSpanM, _halfSpanM);
    double y = Math.Clamp((plot.Center.Y - point.Y) / pixelsPerMeter,
        -_halfSpanM, _halfSpanM);
    _dragged.SetOffset(Math.Round(x, 1), Math.Round(y, 1), _dragged.Z,
        notifyChanged: true);
    InvalidateVisual();
    e.Handled = true;
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    if (_dragged != null) {
      e.Pointer.Capture(null);
      _dragged = null;
      e.Handled = true;
    }
    base.OnPointerReleased(e);
  }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    _halfSpanM = Math.Clamp(
        e.Delta.Y > 0 ? _halfSpanM / 1.25 : _halfSpanM * 1.25,
        5, 1000);
    InvalidateVisual();
    e.Handled = true;
    base.OnPointerWheelChanged(e);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(new SolidColorBrush(Color.Parse("#151817")), Bounds);
    if (Bounds.Width < 180 || Bounds.Height < 180) {
      return;
    }

    Rect plot = PlotBounds();
    double pixelsPerMeter = PixelsPerMeter(plot);
    var minorPen = new Pen(new SolidColorBrush(Color.Parse("#303833")), 1);
    var majorPen = new Pen(new SolidColorBrush(Color.Parse("#59645D")), 1);
    var axisPen = new Pen(Brushes.LightGray, 1.5);
    double step = GridStep(_halfSpanM);

    for (double value = -_halfSpanM; value <= _halfSpanM + step / 2; value += step) {
      double x = plot.Center.X + value * pixelsPerMeter;
      double y = plot.Center.Y - value * pixelsPerMeter;
      bool major = Math.Abs(value % (step * 5)) < 0.001;
      context.DrawLine(major ? majorPen : minorPen,
          new Point(x, plot.Top), new Point(x, plot.Bottom));
      context.DrawLine(major ? majorPen : minorPen,
          new Point(plot.Left, y), new Point(plot.Right, y));
    }
    context.DrawLine(axisPen,
        new Point(plot.Left, plot.Center.Y), new Point(plot.Right, plot.Center.Y));
    context.DrawLine(axisPen,
        new Point(plot.Center.X, plot.Top), new Point(plot.Center.X, plot.Bottom));

    DrawText(context, "+Y", new Point(plot.Center.X + 5, plot.Top + 3),
        Brushes.LightGray, 11);
    DrawText(context, "+X", new Point(plot.Right - 24, plot.Center.Y + 5),
        Brushes.LightGray, 11);
    DrawText(context, $"±{_halfSpanM:0.#} m · wheel zoom · drag enabled followers",
        new Point(8, 7), Brushes.LightGray, 11);

    foreach (FormationVehicleItem row in Rows()
        .Where(row => row.IsEligible && HasFiniteOffset(row))) {
      Point point = ScreenPoint(row);
      bool outside = !plot.Contains(point);
      point = new Point(
          Math.Clamp(point.X, plot.Left + 8, plot.Right - 8),
          Math.Clamp(point.Y, plot.Top + 8, plot.Bottom - 8));
      IBrush fill = row.IsLeader
          ? Brushes.Gold
          : row.Included ? Brushes.DeepSkyBlue : Brushes.DimGray;
      IBrush stroke = outside ? Brushes.OrangeRed : Brushes.White;
      double radius = row.IsLeader ? 9 : 7;
      context.DrawEllipse(fill, new Pen(stroke, 1.5), point, radius, radius);
      if (row.IsLeader) {
        context.DrawLine(new Pen(Brushes.Gold, 2), point,
            new Point(point.X, point.Y - 18));
      }
      DrawText(context, $"{row.SystemId}:{row.ComponentId}",
          new Point(point.X + 10, point.Y - 9), fill, 11);
    }
  }

  private FormationVehicleItem[] Rows() => Items?.ToArray() ?? [];

  private Rect PlotBounds() => new(24, 32,
      Math.Max(1, Bounds.Width - 48), Math.Max(1, Bounds.Height - 56));

  private double PixelsPerMeter(Rect plot) =>
      Math.Max(0.001, Math.Min(plot.Width, plot.Height) / (_halfSpanM * 2));

  private Point ScreenPoint(FormationVehicleItem row) {
    Rect plot = PlotBounds();
    double scale = PixelsPerMeter(plot);
    return new Point(plot.Center.X + row.X * scale, plot.Center.Y - row.Y * scale);
  }

  private static double DistanceSquared(Point left, Point right) {
    double dx = left.X - right.X;
    double dy = left.Y - right.Y;
    return dx * dx + dy * dy;
  }

  private static bool HasFiniteOffset(FormationVehicleItem row) =>
      double.IsFinite(row.X) && double.IsFinite(row.Y);

  private static double GridStep(double halfSpan) {
    double raw = halfSpan * 2 / 10;
    double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
    double normalized = raw / magnitude;
    double step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
    return step * magnitude;
  }

  private static void DrawText(DrawingContext context, string text, Point point,
      IBrush brush, double size) => context.DrawText(
      new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
          new Typeface("Inter"), size, brush), point);
}
