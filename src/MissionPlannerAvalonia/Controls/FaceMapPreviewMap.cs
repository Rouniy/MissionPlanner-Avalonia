using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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

/// <summary>Native Face Map preview with draggable facade path vertices.</summary>
public sealed class FaceMapPreviewMap : MapControl {
  private const double _hitThresholdPixels = 14;
  private TileLayer _baseLayer;
  private readonly WritableLayer _facePath = new() { Name = "Face path" };
  private readonly WritableLayer _route = new() { Name = "Face Map route" };
  private readonly WritableLayer _markers = new() { Name = "Face Map markers" };
  private FaceMapViewModel? _viewModel;
  private FaceMapPreviewState? _state;
  private int _dragIndex = -1;
  private bool _attached;

  public FaceMapPreviewMap() {
    var map = new Map { BackColor = new Color(0x26, 0x27, 0x28) };
    _baseLayer = MapTileSourceFactory.CreateMapLayer(MapTileSourceFactory.CurrentMapType);
    map.Layers.Add(_baseLayer);
    map.Layers.Add(_facePath);
    map.Layers.Add(_route);
    map.Layers.Add(_markers);
    map.Navigator.Limiter = new Mapsui.Limiting.ViewportLimiterKeepWithinExtent();
    Map = map;

    MapPointerPressed += OnMapPointerPressed;
    MapPointerMoved += OnMapPointerMoved;
    MapPointerReleased += OnMapPointerReleased;
    DataContextChanged += (_, _) => {
      if (_attached) {
        AttachViewModel(DataContext as FaceMapViewModel);
      }
    };
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _attached = true;
    AttachViewModel(DataContext as FaceMapViewModel);
    MapTileSourceFactory.AccessModeChanged += OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged += OnMapTypeChanged;
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _attached = false;
    AttachViewModel(null);
    MapTileSourceFactory.AccessModeChanged -= OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged -= OnMapTypeChanged;
    base.OnDetachedFromVisualTree(e);
  }

  public void ZoomToPreview() {
    if (_state != null) {
      FitPreview(_state);
    }
  }

  private void AttachViewModel(FaceMapViewModel? viewModel) {
    if (ReferenceEquals(_viewModel, viewModel)) {
      return;
    }
    if (_viewModel != null) {
      _viewModel.PreviewChanged -= OnPreviewChanged;
    }
    _viewModel = viewModel;
    if (_viewModel != null) {
      _viewModel.PreviewChanged += OnPreviewChanged;
      UpdatePreview(_viewModel.GetPreviewState(), true);
    }
  }

  private void OnPreviewChanged(FaceMapPreviewState state, bool fit) =>
      UpdatePreview(state, fit && _dragIndex < 0);

  private void UpdatePreview(FaceMapPreviewState state, bool fit) {
    _state = state;
    DrawFacePath(state);
    DrawRoute(state);
    DrawMarkers(state);
    if (fit) {
      FitPreview(state);
    }
    RefreshGraphics();
  }

  private void DrawFacePath(FaceMapPreviewState state) {
    _facePath.Clear();
    _facePath.Enabled = state.ShowFacePath;
    if (state.ShowFacePath && state.FacePath.Count >= 2) {
      var line = new GeometryFeature { Geometry = new LineString(Project(state.FacePath)) };
      line.Styles.Add(new VectorStyle { Line = new Pen(Color.Red, 3) });
      _facePath.Add(line);
    }
    _facePath.DataHasChanged();
  }

  private void DrawRoute(FaceMapPreviewState state) {
    _route.Clear();
    _route.Enabled = state.ShowRoute;
    if (state.ShowRoute && state.Route.Count >= 2) {
      var line = new GeometryFeature { Geometry = new LineString(Project(state.Route)) };
      line.Styles.Add(new VectorStyle { Line = new Pen(Color.Yellow, 3) });
      _route.Add(line);
    }
    _route.DataHasChanged();
  }

  private void DrawMarkers(FaceMapPreviewState state) {
    _markers.Clear();
    if (state.ShowFacePath) {
      for (int index = 0; index < state.FacePath.Count; index++) {
        AddMarker(state.FacePath[index], (index + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture), Color.Red, 0.6);
      }
    }
    if (state.ShowMarkers) {
      int navigation = 1;
      foreach (PointLatLngAlt point in state.Route) {
        string tag = point.Tag?.ToString() ?? "";
        if (tag is "SM" or "ME") {
          continue;
        }
        Color color = tag == "R" ? Color.Orange
            : tag == "M" ? Color.Green
            : new Color(0x2F, 0x81, 0xF7);
        AddMarker(point, navigation.ToString(
            System.Globalization.CultureInfo.InvariantCulture), color, 0.48);
        navigation++;
      }
    }
    _markers.DataHasChanged();
  }

  private void AddMarker(PointLatLngAlt point, string label, Color color, double scale) {
    var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
    var feature = new PointFeature(new MPoint(x, y));
    feature.Styles.Add(new SymbolStyle {
      SymbolType = SymbolType.Ellipse,
      Fill = new Brush(color),
      Outline = new Pen(Color.White, 1),
      SymbolScale = scale,
    });
    feature.Styles.Add(new LabelStyle {
      Text = label,
      ForeColor = Color.White,
      BackColor = new Brush(new Color(0, 0, 0, 150)),
      Font = new Font { Size = 9, Bold = true },
      Offset = new Offset(0, 13),
    });
    _markers.Add(feature);
  }

  private void FitPreview(FaceMapPreviewState state) {
    IReadOnlyList<PointLatLngAlt> points = state.FacePath.Count > 0
        ? state.FacePath.Concat(state.Route).ToArray()
        : state.Route;
    if (points.Count == 0) {
      return;
    }
    Coordinate[] projected = Project(points);
    if (projected.Length == 0) {
      return;
    }
    double minX = projected.Min(point => point.X);
    double minY = projected.Min(point => point.Y);
    double maxX = projected.Max(point => point.X);
    double maxY = projected.Max(point => point.Y);
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
    if (_state?.ShowFacePath != true || _viewModel == null) {
      return;
    }
    _dragIndex = HitTestPath(e.ScreenPosition, _state.FacePath);
    if (_dragIndex >= 0) {
      e.Handled = true;
    }
  }

  private void OnMapPointerMoved(object? sender, MapEventArgs e) {
    if (_dragIndex < 0 || _viewModel == null) {
      return;
    }
    var (lng, lat) = SphericalMercator.ToLonLat(e.WorldPosition.X, e.WorldPosition.Y);
    _viewModel.MovePathPoint(_dragIndex, lat, lng);
    e.Handled = true;
  }

  private void OnMapPointerReleased(object? sender, MapEventArgs e) {
    if (_dragIndex < 0) {
      return;
    }
    _dragIndex = -1;
    e.Handled = true;
  }

  private int HitTestPath(Mapsui.Manipulations.ScreenPosition screen,
      IReadOnlyList<PointLatLngAlt> path) {
    var viewport = Map.Navigator.Viewport;
    double best = _hitThresholdPixels;
    int found = -1;
    for (int index = 0; index < path.Count; index++) {
      var (x, y) = SphericalMercator.FromLonLat(path[index].Lng, path[index].Lat);
      double distance = viewport.WorldToScreen(x, y).Distance(screen);
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
    if (!_attached) {
      return;
    }
    Map.Layers.Remove(_baseLayer);
    _baseLayer = MapTileSourceFactory.CreateMapLayer(mapType);
    Map.Layers.Add(_baseLayer);
    Map.Layers.MoveToBottom(_baseLayer);
    RefreshGraphics();
  });

  private static Coordinate[] Project(IEnumerable<PointLatLngAlt> points) => points
      .Where(point => double.IsFinite(point.Lat) && double.IsFinite(point.Lng))
      .Select(point => {
        var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
        return new Coordinate(x, y);
      }).ToArray();
}
