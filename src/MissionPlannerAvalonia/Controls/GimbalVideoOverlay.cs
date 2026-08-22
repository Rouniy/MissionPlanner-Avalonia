using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Controls;

internal sealed class GimbalVideoOverlay : Control, IDisposable {
  private static readonly Pen TrackingPen = new(Brushes.Red, 2);
  private readonly Func<double> _videoAspectRatio;
  private readonly Func<GimbalTrackingOverlay> _trackingOverlay;
  private readonly DispatcherTimer _refreshTimer;
  private GimbalVideoPoint? _dragStart;
  private GimbalVideoPoint? _dragEnd;
  private KeyModifiers _pressModifiers;
  private bool _disposed;

  public event Action<GimbalVideoPointerCommand>? CommandRequested;
  public event Action<Key, KeyModifiers, bool>? KeyStateChanged;
  public event Action? InputReleased;

  public GimbalVideoOverlay(
      Func<double> videoAspectRatio,
      Func<GimbalTrackingOverlay> trackingOverlay) {
    _videoAspectRatio = videoAspectRatio;
    _trackingOverlay = trackingOverlay;
    Focusable = true;
    Cursor = new Cursor(StandardCursorType.Cross);
    _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _refreshTimer.Tick += OnRefresh;
    _refreshTimer.Start();
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(Brushes.Transparent, Bounds);
    Rect image = ImageBounds(Bounds.Size, _videoAspectRatio());

    GimbalTrackingOverlay tracking = _trackingOverlay();
    if (tracking.Shape == GimbalTrackingShape.Point) {
      double radius = Math.Max(5, tracking.Radius * Math.Min(image.Width, image.Height));
      var center = new Point(
          image.X + tracking.X * image.Width,
          image.Y + tracking.Y * image.Height);
      context.DrawEllipse(null, TrackingPen, center, radius, radius);
    } else if (tracking.Shape == GimbalTrackingShape.Rectangle) {
      context.DrawRectangle(
          null,
          TrackingPen,
          new Rect(
              image.X + tracking.X * image.Width,
              image.Y + tracking.Y * image.Height,
              tracking.Width * image.Width,
              tracking.Height * image.Height));
    }

    if (_dragStart is { } start && _dragEnd is { } end
        && _pressModifiers.HasFlag(KeyModifiers.Alt)) {
      Point a = ToSurface(start, image);
      Point b = ToSurface(end, image);
      context.DrawRectangle(
          null,
          TrackingPen,
          new Rect(
              Math.Min(a.X, b.X),
              Math.Min(a.Y, b.Y),
              Math.Abs(a.X - b.X),
              Math.Abs(a.Y - b.Y)));
    }
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    var properties = e.GetCurrentPoint(this).Properties;
    if (!properties.IsLeftButtonPressed
        || !GimbalVideoInteraction.TryMapToVideo(
            e.GetPosition(this), Bounds.Size, _videoAspectRatio(), out var point)) {
      return;
    }
    Focus();
    _dragStart = point;
    _dragEnd = point;
    _pressModifiers = e.KeyModifiers;
    e.Pointer.Capture(this);
    e.Handled = true;
    InvalidateVisual();
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (_dragStart == null
        || !GimbalVideoInteraction.TryMapToVideo(
            e.GetPosition(this), Bounds.Size, _videoAspectRatio(), out var point)) {
      return;
    }
    _dragEnd = point;
    e.Handled = true;
    InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    if (_dragStart is not { } start) {
      return;
    }
    GimbalVideoPoint end = _dragEnd ?? start;
    if (GimbalVideoInteraction.TryMapToVideo(
        e.GetPosition(this), Bounds.Size, _videoAspectRatio(), out var released)) {
      end = released;
    }
    e.Pointer.Capture(null);
    _dragStart = null;
    _dragEnd = null;
    var command = GimbalVideoInteraction.PointerCommand(start, end, _pressModifiers);
    _pressModifiers = KeyModifiers.None;
    e.Handled = true;
    InvalidateVisual();
    CommandRequested?.Invoke(command);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    base.OnKeyDown(e);
    if (GimbalVideoInteraction.IsMotionKey(e.Key)
        || GimbalVideoInteraction.IsModifierKey(e.Key)
        || GimbalVideoInteraction.Hotkey(e.Key, e.KeyModifiers)
            != GimbalVideoHotkeyAction.None) {
      KeyStateChanged?.Invoke(e.Key, e.KeyModifiers, true);
      e.Handled = true;
    }
  }

  protected override void OnKeyUp(KeyEventArgs e) {
    base.OnKeyUp(e);
    if (GimbalVideoInteraction.IsMotionKey(e.Key)
        || GimbalVideoInteraction.IsModifierKey(e.Key)
        || GimbalVideoInteraction.IsHotkeyKey(e.Key)) {
      KeyStateChanged?.Invoke(e.Key, e.KeyModifiers, false);
      e.Handled = true;
    }
  }

  protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) {
    base.OnLostFocus(e);
    InputReleased?.Invoke();
  }

  private void OnRefresh(object? sender, EventArgs e) => InvalidateVisual();

  private static Rect ImageBounds(Size surface, double videoAspectRatio) {
    double aspect = double.IsFinite(videoAspectRatio) && videoAspectRatio > 0
        ? videoAspectRatio
        : surface.Width / Math.Max(surface.Height, 1);
    double width = Math.Min(surface.Width, surface.Height * aspect);
    double height = Math.Min(surface.Height, surface.Width / aspect);
    return new Rect(
        (surface.Width - width) / 2,
        (surface.Height - height) / 2,
        Math.Max(0, width),
        Math.Max(0, height));
  }

  private static Point ToSurface(GimbalVideoPoint point, Rect image) => new(
      image.X + (point.X + 1) / 2 * image.Width,
      image.Y + (point.Y + 1) / 2 * image.Height);

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _refreshTimer.Stop();
    _refreshTimer.Tick -= OnRefresh;
    InputReleased?.Invoke();
  }
}
