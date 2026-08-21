using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Avalonia;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

internal sealed class AirportOverlayController : IDisposable {
  private const int CircleSegments = 64;

  private readonly MapControl _map;
  private readonly bool _alwaysShow;
  private readonly WritableLayer _layer = new() { Name = "Airports", Style = null };
  private IReadOnlyList<AirportMapItem> _rendered = Array.Empty<AirportMapItem>();
  private (double Lat, double Lng)? _lastQueryCenter;
  private bool _suspended = true;
  private bool _loadContinuationQueued;
  private string? _tooltipAirport;

  internal AirportOverlayController(MapControl map, bool alwaysShow) {
    _map = map;
    _alwaysShow = alwaysShow;
    _layer.MaxVisible = AirportService.MaximumVisibleResolution;
    map.Map.Layers.Insert(1, _layer);
    map.MapPointerMoved += OnMapPointerMoved;
    map.PointerExited += OnPointerExited;
    ToolTip.SetPlacement(map, PlacementMode.Pointer);
  }

  internal void Resume() {
    _suspended = false;
    Update();
  }

  internal void Suspend() {
    _suspended = true;
    HideTooltip();
  }

  internal void Update() {
    if (_suspended) {
      return;
    }

    bool enabled = _alwaysShow || Settings.Instance.GetBoolean("showairports", true);
    _layer.Enabled = enabled;
    if (!enabled) {
      _lastQueryCenter = null;
      _rendered = Array.Empty<AirportMapItem>();
      ClearLayer();
      HideTooltip();
      return;
    }

    Task<AirportLoadResult> load = AirportService.EnsureLoadedAsync();
    if (!load.IsCompleted) {
      QueueUpdateAfterLoad(load);
      return;
    }
    if (!load.IsCompletedSuccessfully || !load.Result.Available) {
      ClearLayer();
      return;
    }

    var viewport = _map.Map.Navigator.Viewport;
    if (!double.IsFinite(viewport.CenterX) || !double.IsFinite(viewport.CenterY)) {
      return;
    }
    var (lngRaw, latRaw) = SphericalMercator.ToLonLat(viewport.CenterX, viewport.CenterY);
    if (!double.IsFinite(latRaw) || !double.IsFinite(lngRaw)) {
      return;
    }
    double lat = Math.Clamp(latRaw, -90, 90);
    double lng = Math.Clamp(lngRaw, -180, 180);

    if (_lastQueryCenter is { } previous
        && AirportService.DistanceMeters(previous.Lat, previous.Lng, lat, lng)
        < Airports.proximity / 3.0 * 2.0) {
      return;
    }

    IReadOnlyList<AirportMapItem> nearby = AirportService.GetNearby(lat, lng);
    _lastQueryCenter = (lat, lng);
    if (_rendered.SequenceEqual(nearby)) {
      return;
    }
    _rendered = nearby;
    Redraw();
  }

  private void QueueUpdateAfterLoad(Task<AirportLoadResult> load) {
    if (_loadContinuationQueued) {
      return;
    }
    _loadContinuationQueued = true;
    _ = load.ContinueWith(_ => Dispatcher.UIThread.Post(() => {
      _loadContinuationQueued = false;
      _lastQueryCenter = null;
      Update();
    }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
  }

  private void Redraw() {
    _layer.Clear();
    foreach (AirportMapItem airport in _rendered) {
      _layer.Add(BuildRadiusFeature(airport));
    }
    _layer.DataHasChanged();
    _map.RefreshGraphics();
  }

  internal static GeometryFeature BuildRadiusFeature(AirportMapItem airport) {
    var center = SphericalMercator.FromLonLat(airport.Lng, airport.Lat);
    double cosLatitude = Math.Abs(Math.Cos(airport.Lat * Math.PI / 180));
    double projectedRadius = airport.RadiusMeters / Math.Max(0.01, cosLatitude);
    var coordinates = new Coordinate[CircleSegments + 1];
    for (int i = 0; i <= CircleSegments; i++) {
      double angle = Math.PI * 2 * i / CircleSegments;
      coordinates[i] = new Coordinate(
          center.x + Math.Cos(angle) * projectedRadius,
          center.y + Math.Sin(angle) * projectedRadius);
    }

    var feature = new GeometryFeature {
      Geometry = new Polygon(new LinearRing(coordinates)),
      Data = airport,
    };
    feature.Styles.Add(new VectorStyle {
      // GMapMarkerAirport uses this exact fill. The layer style must stay null so
      // Mapsui does not paint its default opaque white polygon underneath it.
      Fill = new Brush(Color.FromArgb(25, 255, 0, 0)),
      Line = null,
      Outline = null,
    });
    return feature;
  }

  private void OnMapPointerMoved(object? sender, MapEventArgs e) {
    if (_suspended || !_layer.Enabled || _rendered.Count == 0
        || _map.Map.Navigator.Viewport.Resolution >= AirportService.MaximumVisibleResolution) {
      HideTooltip();
      return;
    }

    string? nearest = null;
    double halfHitSize = AirportService.HoverSizePixels / 2.0;
    double best = double.MaxValue;
    var viewport = _map.Map.Navigator.Viewport;
    foreach (AirportMapItem airport in _rendered) {
      var (x, y) = SphericalMercator.FromLonLat(airport.Lng, airport.Lat);
      var screen = viewport.WorldToScreen(x, y);
      double dx = screen.X - e.ScreenPosition.X;
      double dy = screen.Y - e.ScreenPosition.Y;
      // GMapMarkerAirport uses a centred 50x50 marker rectangle solely for hover events.
      if (Math.Abs(dx) > halfHitSize || Math.Abs(dy) > halfHitSize) {
        continue;
      }
      double distance = Math.Sqrt(dx * dx + dy * dy);
      if (distance < best) {
        best = distance;
        nearest = airport.Name;
      }
    }

    ShowTooltip(nearest);
  }

  private void OnPointerExited(object? sender, PointerEventArgs e) => HideTooltip();

  private void ShowTooltip(string? airport) {
    if (string.IsNullOrWhiteSpace(airport)) {
      HideTooltip();
      return;
    }
    if (airport == _tooltipAirport) {
      return;
    }

    ToolTip.SetIsOpen(_map, false);
    ToolTip.SetTip(_map, airport);
    ToolTip.SetIsOpen(_map, true);
    _tooltipAirport = airport;
  }

  private void HideTooltip() {
    if (_tooltipAirport == null) {
      return;
    }
    ToolTip.SetIsOpen(_map, false);
    ToolTip.SetTip(_map, null);
    _tooltipAirport = null;
  }

  private void ClearLayer() {
    if (!_layer.GetFeatures().Any()) {
      return;
    }
    _layer.Clear();
    _layer.DataHasChanged();
    _map.RefreshGraphics();
  }

  public void Dispose() {
    Suspend();
    _map.MapPointerMoved -= OnMapPointerMoved;
    _map.PointerExited -= OnPointerExited;
  }
}
