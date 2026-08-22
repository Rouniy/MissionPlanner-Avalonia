using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Controls;

public sealed class SequenceLayoutControl : Control, IDisposable {
  public static readonly StyledProperty<IEnumerable<SequenceOffsetItem>?> ItemsProperty =
      AvaloniaProperty.Register<SequenceLayoutControl, IEnumerable<SequenceOffsetItem>?>(
          nameof(Items));

  private INotifyCollectionChanged? _collection;
  private readonly List<SequenceOffsetItem> _subscribedItems = [];
  private SequenceOffsetItem? _dragged;
  private Bitmap? _background;
  private double _halfSpanM = 20;
  private double _backgroundX;
  private double _backgroundY;
  private double _backgroundWidth = 1;
  private double _backgroundHeight = 1;
  private double _backgroundStep = 1;

  static SequenceLayoutControl() {
    AffectsRender<SequenceLayoutControl>(ItemsProperty);
    ItemsProperty.Changed.AddClassHandler<SequenceLayoutControl>((control, _) =>
        control.SubscribeItems());
  }

  public SequenceLayoutControl() {
    ClipToBounds = true;
    Focusable = true;
    PointerWheelChanged += OnWheel;
    PointerPressed += OnPressed;
    PointerMoved += OnMoved;
    PointerReleased += OnReleased;
  }

  public IEnumerable<SequenceOffsetItem>? Items {
    get => GetValue(ItemsProperty);
    set => SetValue(ItemsProperty, value);
  }

  public double BackgroundStep => _backgroundStep;

  public void LoadBackground(string path) {
    using FileStream stream = File.OpenRead(path);
    var next = new Bitmap(stream);
    _background?.Dispose();
    _background = next;
    InvalidateVisual();
  }

  public void ClearBackground() {
    _background?.Dispose();
    _background = null;
    InvalidateVisual();
  }

  public void MoveBackground(double deltaX, double deltaY) {
    _backgroundX += deltaX * _backgroundStep;
    _backgroundY += deltaY * _backgroundStep;
    InvalidateVisual();
  }

  public void ResizeBackground(double deltaWidth, double deltaHeight) {
    _backgroundWidth = Math.Max(0.1, _backgroundWidth + deltaWidth * _backgroundStep);
    _backgroundHeight = Math.Max(0.1, _backgroundHeight + deltaHeight * _backgroundStep);
    InvalidateVisual();
  }

  public void SetBackgroundStep(double step) => _backgroundStep = step is 0.1 or 1 ? step : 1;

  public override void Render(DrawingContext context) {
    base.Render(context);
    Rect plot = PlotBounds();
    context.FillRectangle(new SolidColorBrush(Color.Parse("#191B1D")), Bounds);
    if (plot.Width <= 1 || plot.Height <= 1) {
      return;
    }

    double scale = PixelsPerMeter(plot);
    if (_background != null) {
      var target = new Rect(
          plot.Center.X + _backgroundX * scale,
          plot.Center.Y + _backgroundY * scale,
          _backgroundWidth * scale,
          _backgroundHeight * scale);
      context.DrawImage(_background,
          new Rect(0, 0, _background.PixelSize.Width, _background.PixelSize.Height), target);
    }

    DrawGrid(context, plot, scale);
    foreach (SequenceOffsetItem item in CurrentItems()) {
      Point point = ScreenPoint(item, plot, scale);
      bool outside = !plot.Contains(point);
      point = new Point(
          Math.Clamp(point.X, plot.Left + 8, plot.Right - 8),
          Math.Clamp(point.Y, plot.Top + 8, plot.Bottom - 8));
      IBrush fill = outside ? Brushes.OrangeRed : Brushes.DeepSkyBlue;
      context.DrawEllipse(fill, new Pen(Brushes.White, 1.5), point, 8, 8);
      DrawText(context, item.SystemId.ToString(CultureInfo.InvariantCulture),
          new Point(point.X + 11, point.Y - 9), fill, 11);
      DrawText(context, $"z {item.Z:0.#}",
          new Point(point.X + 11, point.Y + 4), Brushes.LightGray, 10);
    }
    DrawText(context, $"±{_halfSpanM:0.#} m · wheel zoom · drag vehicles",
        new Point(8, 7), Brushes.LightGray, 11);
  }

  private void DrawGrid(DrawingContext context, Rect plot, double scale) {
    double step = GridStep(_halfSpanM);
    var fine = new Pen(new SolidColorBrush(Color.Parse("#384047")), 1);
    var axis = new Pen(Brushes.SeaGreen, 1.5);
    for (double value = -Math.Floor(_halfSpanM / step) * step;
         value <= _halfSpanM; value += step) {
      double x = plot.Center.X + value * scale;
      double y = plot.Center.Y - value * scale;
      context.DrawLine(Math.Abs(value) < 0.0001 ? axis : fine,
          new Point(x, plot.Top), new Point(x, plot.Bottom));
      context.DrawLine(Math.Abs(value) < 0.0001 ? axis : fine,
          new Point(plot.Left, y), new Point(plot.Right, y));
    }
    DrawText(context, "+E", new Point(plot.Right - 24, plot.Center.Y + 5),
        Brushes.LightGray, 11);
    DrawText(context, "+N", new Point(plot.Center.X + 5, plot.Top + 3),
        Brushes.LightGray, 11);
  }

  private void OnWheel(object? sender, PointerWheelEventArgs e) {
    _halfSpanM = Math.Clamp(_halfSpanM + (e.Delta.Y < 0 ? 2 : -2), 4, 50000);
    InvalidateVisual();
    e.Handled = true;
  }

  private void OnPressed(object? sender, PointerPressedEventArgs e) {
    Point pointer = e.GetPosition(this);
    Rect plot = PlotBounds();
    double scale = PixelsPerMeter(plot);
    _dragged = CurrentItems()
        .OrderBy(item => DistanceSquared(pointer, ScreenPoint(item, plot, scale)))
        .FirstOrDefault(item => DistanceSquared(pointer, ScreenPoint(item, plot, scale)) <= 18 * 18);
    if (_dragged != null) {
      e.Pointer.Capture(this);
      e.Handled = true;
    }
  }

  private void OnMoved(object? sender, PointerEventArgs e) {
    if (_dragged == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
      return;
    }
    Rect plot = PlotBounds();
    double scale = PixelsPerMeter(plot);
    Point pointer = e.GetPosition(this);
    _dragged.X = Math.Round((pointer.X - plot.Center.X) / scale, 2);
    _dragged.Y = Math.Round((plot.Center.Y - pointer.Y) / scale, 2);
    InvalidateVisual();
    e.Handled = true;
  }

  private void OnReleased(object? sender, PointerReleasedEventArgs e) {
    if (_dragged != null) {
      _dragged = null;
      e.Pointer.Capture(null);
      e.Handled = true;
    }
  }

  private void SubscribeItems() {
    if (_collection != null) {
      _collection.CollectionChanged -= OnCollectionChanged;
    }
    foreach (SequenceOffsetItem item in _subscribedItems) {
      item.PropertyChanged -= OnItemChanged;
    }
    _subscribedItems.Clear();
    _collection = Items as INotifyCollectionChanged;
    if (_collection != null) {
      _collection.CollectionChanged += OnCollectionChanged;
    }
    foreach (SequenceOffsetItem item in CurrentItems()) {
      item.PropertyChanged += OnItemChanged;
      _subscribedItems.Add(item);
    }
    InvalidateVisual();
  }

  private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    if (e.OldItems != null) {
      foreach (SequenceOffsetItem item in e.OldItems.OfType<SequenceOffsetItem>()) {
        item.PropertyChanged -= OnItemChanged;
        _subscribedItems.Remove(item);
      }
    }
    if (e.NewItems != null) {
      foreach (SequenceOffsetItem item in e.NewItems.OfType<SequenceOffsetItem>()) {
        item.PropertyChanged += OnItemChanged;
        _subscribedItems.Add(item);
      }
    }
    InvalidateVisual();
  }

  private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

  private SequenceOffsetItem[] CurrentItems() => Items?.ToArray() ?? [];

  private Rect PlotBounds() => new(24, 28,
      Math.Max(1, Bounds.Width - 48), Math.Max(1, Bounds.Height - 52));

  private double PixelsPerMeter(Rect plot) =>
      Math.Max(0.001, Math.Min(plot.Width, plot.Height) / (_halfSpanM * 2));

  private static Point ScreenPoint(SequenceOffsetItem item, Rect plot, double scale) =>
      new(plot.Center.X + item.X * scale, plot.Center.Y - item.Y * scale);

  private static double DistanceSquared(Point left, Point right) {
    double dx = left.X - right.X;
    double dy = left.Y - right.Y;
    return dx * dx + dy * dy;
  }

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

  public void Dispose() {
    if (_collection != null) {
      _collection.CollectionChanged -= OnCollectionChanged;
      _collection = null;
    }
    foreach (SequenceOffsetItem item in _subscribedItems) {
      item.PropertyChanged -= OnItemChanged;
    }
    _subscribedItems.Clear();
    _background?.Dispose();
    _background = null;
  }
}
