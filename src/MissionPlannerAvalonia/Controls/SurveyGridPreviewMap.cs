using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Avalonia;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

internal sealed record SurveyPreviewMarker(PointLatLngAlt Position, string Label, bool IsPhoto);

internal sealed record SurveyPreviewFootprint(
    IReadOnlyList<PointLatLngAlt> Corners, Color Color, int Index);

internal readonly record struct SurveyCoveragePoint(double Lat, double Lng, int Count);

internal sealed record SurveyGridPreviewGeometry(
    IReadOnlyList<PointLatLngAlt> Route,
    IReadOnlyList<SurveyPreviewMarker> Markers,
    IReadOnlyList<SurveyPreviewFootprint> Footprints,
    IReadOnlyList<SurveyCoveragePoint> Coverage);

public enum SurveyBoundaryEditMode {
  Pan,
  DrawRectangle,
  ShiftEdge,
  MoveBoundary,
}

internal static class SurveyBoundaryGeometryEditor {
  internal static IReadOnlyList<PointLatLngAlt> CreateRectangle(
      MPoint first, MPoint second, double altitude) {
    double left = Math.Min(first.X, second.X);
    double right = Math.Max(first.X, second.X);
    double bottom = Math.Min(first.Y, second.Y);
    double top = Math.Max(first.Y, second.Y);
    return new[] {
      FromWorld(left, top, altitude),
      FromWorld(right, top, altitude),
      FromWorld(right, bottom, altitude),
      FromWorld(left, bottom, altitude),
    };
  }

  internal static IReadOnlyList<PointLatLngAlt> Translate(
      IReadOnlyList<PointLatLngAlt> boundary, double deltaX, double deltaY) =>
      boundary.Select(point => {
        var world = SphericalMercator.FromLonLat(point.Lng, point.Lat);
        return FromWorld(world.x + deltaX, world.y + deltaY, point.Alt);
      }).ToArray();

  internal static IReadOnlyList<PointLatLngAlt> ShiftEdge(
      IReadOnlyList<PointLatLngAlt> boundary, int edgeIndex, double deltaX, double deltaY) {
    PointLatLngAlt[] result = boundary.Select(point => new PointLatLngAlt(point)).ToArray();
    if (result.Length < 2 || edgeIndex < 0 || edgeIndex >= result.Length) {
      return result;
    }

    int nextIndex = (edgeIndex + 1) % result.Length;
    var first = SphericalMercator.FromLonLat(result[edgeIndex].Lng, result[edgeIndex].Lat);
    var second = SphericalMercator.FromLonLat(result[nextIndex].Lng, result[nextIndex].Lat);
    double edgeX = second.x - first.x;
    double edgeY = second.y - first.y;
    double length = Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
    if (length < double.Epsilon) {
      return result;
    }

    double normalX = -edgeY / length;
    double normalY = edgeX / length;
    double normalDistance = deltaX * normalX + deltaY * normalY;
    double shiftX = normalX * normalDistance;
    double shiftY = normalY * normalDistance;
    result[edgeIndex] = FromWorld(first.x + shiftX, first.y + shiftY,
        result[edgeIndex].Alt);
    result[nextIndex] = FromWorld(second.x + shiftX, second.y + shiftY,
        result[nextIndex].Alt);
    return result;
  }

  private static PointLatLngAlt FromWorld(double x, double y, double altitude) {
    var (lng, lat) = SphericalMercator.ToLonLat(x, y);
    return new PointLatLngAlt(lat, lng, altitude);
  }
}

internal static class SurveyGridPreviewBuilder {
  internal const double CoverageStepDegrees = OverlapCoverageBuilder.StepDegrees;

  internal static IReadOnlyList<Color> CoverageColors => OverlapCoverageBuilder.Colors;

  internal static SurveyGridPreviewGeometry Build(
      SurveyGridPreviewState state,
      Func<PointLatLngAlt, double, double, double,
          IReadOnlyList<PointLatLngAlt>>? footprintProjector = null) {
    footprintProjector ??= ProjectFootprint;
    var route = new List<PointLatLngAlt>();
    var markers = new List<SurveyPreviewMarker>();
    var footprints = new List<SurveyPreviewFootprint>();
    PointLatLngAlt? previousNavigation = null;
    int label = 1;

    foreach (PointLatLngAlt item in state.Grid) {
      bool isPhoto = string.Equals(item.Tag?.ToString(), "M", StringComparison.Ordinal);
      if (!isPhoto) {
        route.Add(new PointLatLngAlt(item));
        previousNavigation = item;
        if (state.ShowMarkers) {
          markers.Add(new SurveyPreviewMarker(new PointLatLngAlt(item),
              label.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
        }
        label++;
        continue;
      }

      if (state.ShowInternals) {
        route.Add(new PointLatLngAlt(item));
        markers.Add(new SurveyPreviewMarker(new PointLatLngAlt(item),
            label.ToString(System.Globalization.CultureInfo.InvariantCulture), true));
        label++;
      }

      if (!state.ShowFootprints || state.HorizontalFovDegrees is <= 0 or >= 180 ||
          state.VerticalFovDegrees is <= 0 or >= 180) {
        if (state.ShowInternals) {
          previousNavigation = item;
        }
        continue;
      }

      double bearing = state.HoldHeading ? state.Heading : state.Angle;
      if (state.Corridor && previousNavigation != null) {
        bearing = previousNavigation.GetBearing(item);
      }
      double cameraOffset = state.CameraFacingForward ? 0 : 90;
      var camera = new PointLatLngAlt(item) { Alt = item.Alt + state.Home.Alt };
      try {
        IReadOnlyList<PointLatLngAlt> projected = footprintProjector(
            camera, bearing + cameraOffset, state.HorizontalFovDegrees,
            state.VerticalFovDegrees);
        PointLatLngAlt[] valid = projected
            .Where(IsValidCoordinate)
            .Select(point => new PointLatLngAlt(point))
            .ToArray();
        if (valid.Length >= 3) {
          int colorSeed = label;
          footprints.Add(new SurveyPreviewFootprint(valid,
              new Color(
                  (byte)(250 - colorSeed * 5 % 240),
                  (byte)(250 - colorSeed * 3 % 240),
                  (byte)(250 - colorSeed * 9 % 240)), label));
          label++;
        }
      } catch {
        // The official preview treats unavailable terrain or a bad projection as a skipped image.
      }
      if (state.ShowInternals) {
        previousNavigation = item;
      }
    }

    return new SurveyGridPreviewGeometry(route, markers, footprints,
        Array.Empty<SurveyCoveragePoint>());
  }

  internal static IReadOnlyList<SurveyCoveragePoint> BuildCoverage(
      IReadOnlyList<SurveyPreviewFootprint> footprints,
      CancellationToken cancellationToken = default) {
    IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> input = footprints
        .Select(footprint => (IReadOnlyList<(double Lat, double Lng)>)footprint.Corners
            .Select(point => (point.Lat, point.Lng)).ToArray())
        .ToArray();
    return OverlapCoverageBuilder.Build(input, cancellationToken)
        .Select(point => new SurveyCoveragePoint(point.Lat, point.Lng, point.Count))
        .ToArray();
  }

  internal static Color CoverageColor(int count) =>
      OverlapCoverageBuilder.ColorForCount(count);

  private static IReadOnlyList<PointLatLngAlt> ProjectFootprint(
      PointLatLngAlt camera, double yaw, double horizontalFov, double verticalFov) =>
      ImageProjection.calc(camera, 0, 0, yaw, horizontalFov, verticalFov);

  private static bool IsValidCoordinate(PointLatLngAlt point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;
}

public sealed class SurveyGridPreviewMap : MapControl {
  private const double _hitThresholdPixels = 14;
  private TileLayer _baseLayer;
  private readonly WritableLayer _boundary = new() { Name = "Survey boundary" };
  private readonly WritableLayer _footprints = new() { Name = "Survey footprints" };
  private readonly WritableLayer _route = new() { Name = "Survey route" };
  private readonly WritableLayer _coverage = new() { Name = "Survey overlap" };
  private readonly WritableLayer _markers = new() { Name = "Survey markers" };
  private GridUIViewModel? _viewModel;
  private SurveyGridPreviewState? _state;
  private int _dragIndex = -1;
  private SurveyBoundaryEditMode _editMode;
  private MPoint? _gestureStart;
  private IReadOnlyList<PointLatLngAlt>? _gestureBoundary;
  private int _gestureEdge = -1;
  private bool _attached;
  private CancellationTokenSource? _coverageCancellation;
  private long _previewRevision;
  private IReadOnlyList<PointLatLngAlt> _routePoints = Array.Empty<PointLatLngAlt>();
  private string? _routeTooltip;

  public SurveyGridPreviewMap() {
    var map = new Map { BackColor = new Color(0x26, 0x27, 0x28) };
    _baseLayer = MapTileSourceFactory.CreateMapLayer(MapTileSourceFactory.CurrentMapType);
    map.Layers.Add(_baseLayer);
    map.Layers.Add(_boundary);
    map.Layers.Add(_footprints);
    map.Layers.Add(_route);
    map.Layers.Add(_markers);
    map.Layers.Add(_coverage);
    map.Navigator.Limiter = new Mapsui.Limiting.ViewportLimiterKeepWithinExtent();
    Map = map;

    MapPointerPressed += OnMapPointerPressed;
    MapPointerMoved += OnMapPointerMoved;
    MapPointerReleased += OnMapPointerReleased;
    DataContextChanged += (_, _) => {
      if (_attached) {
        AttachViewModel(DataContext as GridUIViewModel);
      }
    };
  }

  public SurveyBoundaryEditMode EditMode {
    get => _editMode;
    set {
      if (_editMode == value) {
        return;
      }
      ResetBoundaryGesture();
      _editMode = value;
    }
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _attached = true;
    AttachViewModel(DataContext as GridUIViewModel);
    MapTileSourceFactory.AccessModeChanged += OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged += OnMapTypeChanged;
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _attached = false;
    ResetBoundaryGesture();
    CancelCoverage();
    AttachViewModel(null);
    MapTileSourceFactory.AccessModeChanged -= OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged -= OnMapTypeChanged;
    base.OnDetachedFromVisualTree(e);
  }

  internal void ZoomToPreview() {
    if (_state != null) {
      FitPreview(_state);
    }
  }

  private void AttachViewModel(GridUIViewModel? viewModel) {
    if (ReferenceEquals(_viewModel, viewModel)) {
      return;
    }
    if (_viewModel != null) {
      _viewModel.PreviewChanged -= OnPreviewChanged;
    }
    _viewModel = viewModel;
    if (_viewModel != null) {
      _viewModel.PreviewChanged += OnPreviewChanged;
      UpdatePreview(_viewModel.GetPreviewState(), fit: true);
    }
  }

  private void OnPreviewChanged(SurveyGridPreviewState state, bool fit) =>
      UpdatePreview(state, fit && _dragIndex < 0);

  private void UpdatePreview(SurveyGridPreviewState state, bool fit) {
    CancelCoverage();
    long revision = ++_previewRevision;
    _state = state;
    SurveyGridPreviewGeometry geometry = SurveyGridPreviewBuilder.Build(state);
    _routePoints = geometry.Route;
    DrawBoundary(state);
    DrawFootprints(geometry.Footprints);
    DrawRoute(geometry.Route, state.ShowGrid);
    DrawMarkers(state, geometry.Markers);
    DrawCoverage(Array.Empty<SurveyCoveragePoint>());
    if (state.ShowFootprints && geometry.Footprints.Count > 0) {
      StartCoverage(geometry.Footprints, revision);
    }
    if (fit) {
      FitPreview(state);
    }
    RefreshGraphics();
  }

  private void DrawBoundary(SurveyGridPreviewState state) {
    _boundary.Clear();
    _boundary.Enabled = state.ShowBoundary;
    if (!state.ShowBoundary || state.Boundary.Count == 0) {
      _boundary.DataHasChanged();
      return;
    }

    Coordinate[] coordinates = Project(state.Boundary);
    if (state.Corridor && coordinates.Length >= 2) {
      var feature = new GeometryFeature { Geometry = new LineString(coordinates) };
      feature.Styles.Add(new VectorStyle { Line = new Pen(Color.Red, 2) });
      _boundary.Add(feature);
    } else if (coordinates.Length >= 3) {
      Coordinate[] closed = coordinates.Append(new Coordinate(coordinates[0])).ToArray();
      var feature = new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Polygon(new LinearRing(closed)),
      };
      feature.Styles.Add(new VectorStyle {
        Fill = new Brush(new Color(255, 0, 0, 0)),
        Line = new Pen(Color.Red, 2),
      });
      _boundary.Add(feature);
    }

    _boundary.DataHasChanged();
  }

  private void DrawFootprints(IReadOnlyList<SurveyPreviewFootprint> footprints) {
    _footprints.Clear();
    foreach (SurveyPreviewFootprint footprint in footprints) {
      Coordinate[] coordinates = Project(footprint.Corners);
      if (coordinates.Length < 3) {
        continue;
      }
      Coordinate[] closed = coordinates.Append(new Coordinate(coordinates[0])).ToArray();
      var feature = new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Polygon(new LinearRing(closed)),
      };
      feature.Styles.Add(new VectorStyle {
        Fill = new Brush(new Color(footprint.Color.R, footprint.Color.G,
            footprint.Color.B, 0)),
        Line = new Pen(footprint.Color, 1),
      });
      _footprints.Add(feature);
    }
    _footprints.DataHasChanged();
  }

  private void DrawRoute(IReadOnlyList<PointLatLngAlt> route, bool enabled) {
    _route.Clear();
    _route.Enabled = enabled;
    if (enabled && route.Count >= 2) {
      var feature = new GeometryFeature { Geometry = new LineString(Project(route)) };
      feature.Styles.Add(new VectorStyle { Line = new Pen(Color.Yellow, 4) });
      _route.Add(feature);
    }
    _route.DataHasChanged();
  }

  private void DrawCoverage(IReadOnlyList<SurveyCoveragePoint> coverage) {
    _coverage.Clear();
    foreach (SurveyCoveragePoint point in coverage) {
      _coverage.Add(CoverageFeature(point));
    }
    _coverage.DataHasChanged();
  }

  private void DrawMarkers(SurveyGridPreviewState state,
      IReadOnlyList<SurveyPreviewMarker> markers) {
    _markers.Clear();
    if (state.ShowBoundary) {
      int boundaryIndex = 1;
      foreach (PointLatLngAlt point in state.Boundary) {
        var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
        var boundaryMarker = new PointFeature(new MPoint(x, y));
        boundaryMarker.Styles.Add(new SymbolStyle {
          SymbolType = SymbolType.Ellipse,
          Fill = new Brush(Color.Red),
          Outline = new Pen(Color.White, 1),
          SymbolScale = 0.55,
        });
        boundaryMarker.Styles.Add(NumberLabel(boundaryIndex.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
        _markers.Add(boundaryMarker);
        boundaryIndex++;
      }
    }
    foreach (SurveyPreviewMarker marker in markers) {
      var (x, y) = SphericalMercator.FromLonLat(marker.Position.Lng, marker.Position.Lat);
      var feature = new PointFeature(new MPoint(x, y));
      feature.Styles.Add(new SymbolStyle {
        SymbolType = SymbolType.Ellipse,
        Fill = new Brush(marker.IsPhoto ? Color.Green : new Color(0x2F, 0x81, 0xF7)),
        Outline = new Pen(Color.White, 1),
        SymbolScale = marker.IsPhoto ? 0.42 : 0.55,
      });
      feature.Styles.Add(NumberLabel(marker.Label));
      _markers.Add(feature);
    }
    _markers.DataHasChanged();
  }

  private void StartCoverage(IReadOnlyList<SurveyPreviewFootprint> footprints, long revision) {
    _coverageCancellation = new CancellationTokenSource();
    CancellationToken token = _coverageCancellation.Token;
    _ = Task.Run(() => SurveyGridPreviewBuilder.BuildCoverage(
            footprints, token), token)
        .ContinueWith(task => {
          if (task.IsFaulted) {
            Exception? error = task.Exception?.GetBaseException();
            Dispatcher.UIThread.Post(() => {
              if (!token.IsCancellationRequested && revision == _previewRevision &&
                  _viewModel != null) {
                _viewModel.Status = "Overlap preview failed: " +
                                    (error?.Message ?? "unknown error");
              }
            });
            return;
          }
          if (task.Status != TaskStatus.RanToCompletion || token.IsCancellationRequested) {
            return;
          }
          Dispatcher.UIThread.Post(() => {
            if (!token.IsCancellationRequested && revision == _previewRevision) {
              DrawCoverage(task.Result);
              RefreshGraphics();
            }
          });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
  }

  private void CancelCoverage() {
    CancellationTokenSource? cancellation = _coverageCancellation;
    _coverageCancellation = null;
    if (cancellation == null) {
      return;
    }
    cancellation.Cancel();
    cancellation.Dispose();
  }

  private static GeometryFeature CoverageFeature(SurveyCoveragePoint point) {
    var center = SphericalMercator.FromLonLat(point.Lng, point.Lat);
    double cosine = Math.Abs(Math.Cos(point.Lat * Math.PI / 180));
    double radius = 2.5 / Math.Max(0.01, cosine);
    var coordinates = new Coordinate[9];
    for (int index = 0; index < 8; index++) {
      double angle = index * Math.PI * 2 / 8;
      coordinates[index] = new Coordinate(
          center.x + Math.Cos(angle) * radius,
          center.y + Math.Sin(angle) * radius);
    }
    coordinates[^1] = coordinates[0];
    Color color = SurveyGridPreviewBuilder.CoverageColor(point.Count);
    var feature = new GeometryFeature {
      Geometry = new NetTopologySuite.Geometries.Polygon(new LinearRing(coordinates)),
    };
    feature.Styles.Add(new VectorStyle { Fill = new Brush(color), Line = null, Outline = null });
    return feature;
  }

  private static LabelStyle NumberLabel(string text) => new() {
    Text = text,
    ForeColor = Color.White,
    BackColor = new Brush(new Color(0, 0, 0, 150)),
    Font = new Font { Size = 10, Bold = true },
    Offset = new Offset(0, 14),
  };

  private void FitPreview(SurveyGridPreviewState state) {
    IReadOnlyList<PointLatLngAlt> points = state.Boundary.Count > 0
        ? state.Boundary
        : state.Grid;
    if (points.Count == 0) {
      return;
    }
    Coordinate[] coordinates = Project(points);
    double minX = coordinates.Min(point => point.X);
    double minY = coordinates.Min(point => point.Y);
    double maxX = coordinates.Max(point => point.X);
    double maxY = coordinates.Max(point => point.Y);
    if (maxX - minX < 1 && maxY - minY < 1) {
      Map.Navigator.CenterOnAndZoomTo(new MPoint(minX, minY),
          156543.03392804097 / Math.Pow(2, 17));
      return;
    }
    double padX = Math.Max(10, (maxX - minX) * 0.12);
    double padY = Math.Max(10, (maxY - minY) * 0.12);
    Map.Navigator.ZoomToBox(new MRect(
        minX - padX, minY - padY, maxX + padX, maxY + padY));
  }

  private void OnMapPointerPressed(object? sender, MapEventArgs e) {
    if (_state == null || _viewModel == null || !_state.ShowBoundary) {
      return;
    }

    if (EditMode != SurveyBoundaryEditMode.Pan) {
      if (EditMode != SurveyBoundaryEditMode.DrawRectangle && _state.Boundary.Count < 3) {
        return;
      }
      _gestureStart = new MPoint(e.WorldPosition.X, e.WorldPosition.Y);
      _gestureBoundary = _state.Boundary
          .Select(point => new PointLatLngAlt(point)).ToArray();
      _gestureEdge = EditMode == SurveyBoundaryEditMode.ShiftEdge
          ? HitTestBoundaryEdge(e.ScreenPosition, _state.Boundary)
          : -1;
      e.Handled = true;
      return;
    }

    _dragIndex = HitTestBoundary(e.ScreenPosition, _state.Boundary);
    if (_dragIndex >= 0) {
      e.Handled = true;
    }
  }

  private void OnMapPointerMoved(object? sender, MapEventArgs e) {
    if (_gestureStart != null && _gestureBoundary != null && _viewModel != null) {
      HideRouteTooltip();
      double deltaX = e.WorldPosition.X - _gestureStart.X;
      double deltaY = e.WorldPosition.Y - _gestureStart.Y;
      IReadOnlyList<PointLatLngAlt>? boundary = EditMode switch {
        SurveyBoundaryEditMode.DrawRectangle =>
            SurveyBoundaryGeometryEditor.CreateRectangle(_gestureStart, e.WorldPosition,
                _gestureBoundary.FirstOrDefault()?.Alt ?? _state?.Home.Alt ?? 0),
        SurveyBoundaryEditMode.MoveBoundary =>
            SurveyBoundaryGeometryEditor.Translate(_gestureBoundary, deltaX, deltaY),
        SurveyBoundaryEditMode.ShiftEdge when _gestureEdge >= 0 =>
            SurveyBoundaryGeometryEditor.ShiftEdge(
                _gestureBoundary, _gestureEdge, deltaX, deltaY),
        _ => null,
      };
      if (boundary != null) {
        _viewModel.ReplaceBoundary(boundary);
      }
      e.Handled = true;
      return;
    }

    if (_dragIndex < 0) {
      UpdateRouteTooltip(e.ScreenPosition);
      return;
    }
    if (_viewModel == null) {
      return;
    }
    HideRouteTooltip();
    var (lng, lat) = SphericalMercator.ToLonLat(e.WorldPosition.X, e.WorldPosition.Y);
    _viewModel.MoveBoundaryPoint(_dragIndex, lat, lng);
    e.Handled = true;
  }

  protected override void OnPointerExited(PointerEventArgs e) {
    HideRouteTooltip();
    base.OnPointerExited(e);
  }

  private void UpdateRouteTooltip(Mapsui.Manipulations.ScreenPosition pointer) {
    if (_state?.ShowGrid != true || _routePoints.Count < 2) {
      HideRouteTooltip();
      return;
    }
    var viewport = Map.Navigator.Viewport;
    double best = 7;
    double bestDistanceMeters = 0;
    for (int index = 1; index < _routePoints.Count; index++) {
      PointLatLngAlt first = _routePoints[index - 1];
      PointLatLngAlt second = _routePoints[index];
      var firstWorld = SphericalMercator.FromLonLat(first.Lng, first.Lat);
      var secondWorld = SphericalMercator.FromLonLat(second.Lng, second.Lat);
      var firstScreen = viewport.WorldToScreen(firstWorld.x, firstWorld.y);
      var secondScreen = viewport.WorldToScreen(secondWorld.x, secondWorld.y);
      double distance = DistanceToSegment(pointer.X, pointer.Y,
          firstScreen.X, firstScreen.Y, secondScreen.X, secondScreen.Y);
      if (distance < best) {
        best = distance;
        bestDistanceMeters = first.GetDistance(second);
      }
    }
    if (best >= 7) {
      HideRouteTooltip();
      return;
    }
    string text = $"Line: {bestDistanceMeters:0.##} m";
    if (string.Equals(_routeTooltip, text, StringComparison.Ordinal)) {
      return;
    }
    ToolTip.SetTip(this, text);
    ToolTip.SetIsOpen(this, true);
    _routeTooltip = text;
  }

  private void HideRouteTooltip() {
    if (_routeTooltip == null) {
      return;
    }
    ToolTip.SetIsOpen(this, false);
    ToolTip.SetTip(this, null);
    _routeTooltip = null;
  }

  private static double DistanceToSegment(double px, double py, double ax, double ay,
      double bx, double by) {
    double dx = bx - ax;
    double dy = by - ay;
    if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon) {
      return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
    }
    double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
    double nearestX = ax + t * dx;
    double nearestY = ay + t * dy;
    return Math.Sqrt((px - nearestX) * (px - nearestX) +
                     (py - nearestY) * (py - nearestY));
  }

  private void OnMapPointerReleased(object? sender, MapEventArgs e) {
    if (_gestureStart != null) {
      ResetBoundaryGesture();
      e.Handled = true;
      return;
    }
    if (_dragIndex < 0) {
      return;
    }
    _dragIndex = -1;
    e.Handled = true;
  }

  private void ResetBoundaryGesture() {
    _dragIndex = -1;
    _gestureStart = null;
    _gestureBoundary = null;
    _gestureEdge = -1;
  }

  private int HitTestBoundary(Mapsui.Manipulations.ScreenPosition screen,
      IReadOnlyList<PointLatLngAlt> boundary) {
    var viewport = Map.Navigator.Viewport;
    double best = _hitThresholdPixels;
    int found = -1;
    for (int index = 0; index < boundary.Count; index++) {
      var (x, y) = SphericalMercator.FromLonLat(boundary[index].Lng, boundary[index].Lat);
      double distance = viewport.WorldToScreen(x, y).Distance(screen);
      if (distance < best) {
        best = distance;
        found = index;
      }
    }
    return found;
  }

  private int HitTestBoundaryEdge(Mapsui.Manipulations.ScreenPosition screen,
      IReadOnlyList<PointLatLngAlt> boundary) {
    if (boundary.Count < 2) {
      return -1;
    }
    var viewport = Map.Navigator.Viewport;
    double best = double.MaxValue;
    int found = -1;
    for (int index = 0; index < boundary.Count; index++) {
      PointLatLngAlt first = boundary[index];
      PointLatLngAlt second = boundary[(index + 1) % boundary.Count];
      var firstWorld = SphericalMercator.FromLonLat(first.Lng, first.Lat);
      var secondWorld = SphericalMercator.FromLonLat(second.Lng, second.Lat);
      var firstScreen = viewport.WorldToScreen(firstWorld.x, firstWorld.y);
      var secondScreen = viewport.WorldToScreen(secondWorld.x, secondWorld.y);
      double distance = DistanceToSegment(screen.X, screen.Y,
          firstScreen.X, firstScreen.Y, secondScreen.X, secondScreen.Y);
      if (distance < best) {
        best = distance;
        found = index;
      }
    }
    return found;
  }

  private void OnTileAccessModeChanged() => ReplaceBaseOnUi(
      MapTileSourceFactory.CurrentMapType);

  private void OnMapTypeChanged(string mapType) => ReplaceBaseOnUi(mapType);

  private void ReplaceBaseOnUi(string mapType) => Dispatcher.UIThread.Post(() => {
    if (_attached) {
      ReplaceBase(MapTileSourceFactory.CreateMapLayer(mapType));
    }
  });

  private void ReplaceBase(TileLayer layer) {
    Map.Layers.Remove(_baseLayer);
    _baseLayer = layer;
    Map.Layers.Add(_baseLayer);
    Map.Layers.MoveToBottom(_baseLayer);
    RefreshGraphics();
  }

  private static Coordinate[] Project(IEnumerable<PointLatLngAlt> points) => points
      .Where(point => double.IsFinite(point.Lat) && double.IsFinite(point.Lng))
      .Select(point => {
        var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
        return new Coordinate(x, y);
      })
      .ToArray();
}
