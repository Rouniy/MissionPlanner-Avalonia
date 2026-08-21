using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Avalonia;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

internal readonly record struct PropagationMapState(
    PropagationPoint Home,
    PropagationPoint Drone,
    double DroneAltitudeAmsl,
    double BatteryKilometersLeft);

internal sealed class PropagationOverlayController : IDisposable {
  private const int RasterExtensionPixels = 384;

  private readonly MapControl _map;
  private readonly WritableLayer _raster = new() { Name = "Propagation elevation / terrain" };
  private readonly WritableLayer _rf = new() { Name = "RF propagation" };
  private readonly WritableLayer _distance = new() { Name = "Propagation distance left" };
  private readonly WritableLayer _status = new() { Name = "Propagation status / scale" };
  private CancellationTokenSource? _rasterCancellation;
  private CancellationTokenSource? _rfCancellation;
  private RasterKey? _rasterKey;
  private RfKey? _rfKey;
  private DistanceKey? _distanceKey;
  private long _rasterGeneration;
  private long _rfGeneration;
  private bool _suspended = true;
  private bool _rasterNeedsRetry;
  private bool _rfNeedsRetry;
  private DateTime _nextRasterRetryUtc;
  private DateTime _nextRfRetryUtc;
  private string? _rasterStatus;
  private string? _rfStatus;
  private string? _legend;
  private bool _settingsSubscribed;

  internal PropagationOverlayController(MapControl map) {
    _map = map;
    map.Map.Layers.Insert(1, new ILayer[] { _raster, _rf, _distance, _status });
  }

  internal void Resume() {
    if (!_settingsSubscribed) {
      PropagationService.SettingsChanged += OnSettingsChanged;
      _settingsSubscribed = true;
    }
    _suspended = false;
    Invalidate();
  }

  internal void Suspend() {
    if (_settingsSubscribed) {
      PropagationService.SettingsChanged -= OnSettingsChanged;
      _settingsSubscribed = false;
    }
    _suspended = true;
    CancelRaster();
    CancelRf();
  }

  internal void Update(PropagationMapState state) {
    if (_suspended) {
      return;
    }
    PropagationOptions options = PropagationOptions.Load();
    UpdateDistance(state, options);
    UpdateRaster(state, options);
    UpdateRf(state, options);
  }

  private void UpdateRaster(PropagationMapState state, PropagationOptions options) {
    bool enabled = options.ElevationMap || options.TerrainMap;
    if (!enabled) {
      CancelRaster();
      _rasterKey = null;
      _rasterNeedsRetry = false;
      _rasterStatus = null;
      _legend = null;
      Clear(_raster);
      UpdateStatusLayer();
      return;
    }

    var viewport = _map.Map.Navigator.Viewport;
    if (!double.IsFinite(viewport.Resolution) || viewport.Resolution <= 0
        || viewport.Width < 2 || viewport.Height < 2) {
      return;
    }
    MRect visible = viewport.ToExtent();
    double padding = RasterExtensionPixels / 2.0 * viewport.Resolution;
    var extent = new MRect(visible.MinX - padding, visible.MinY - padding,
        visible.MaxX + padding, visible.MaxY + padding);
    int width = Math.Clamp((int)Math.Ceiling(extent.Width / viewport.Resolution), 1, 4096);
    int height = Math.Clamp((int)Math.Ceiling(extent.Height / viewport.Resolution), 1, 4096);
    var key = new RasterKey(
        extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, width, height,
        QuantizeAltitude(state.DroneAltitudeAmsl), options);
    if (_rasterKey == key
        && (!_rasterNeedsRetry || DateTime.UtcNow < _nextRasterRetryUtc)) {
      return;
    }
    if (_rasterCancellation != null && _rasterKey is { } runningRaster
        && runningRaster.SameExceptAltitude(key)) {
      // Let the current computation publish a slightly stale result. Once it finishes,
      // the next timer tick schedules the latest altitude instead of starving forever
      // on normal barometric jitter.
      return;
    }

    CancelRaster();
    _rasterKey = key;
    _rasterNeedsRetry = false;
    UpdateStatusLayer();
    var cancellation = new CancellationTokenSource();
    _rasterCancellation = cancellation;
    long generation = ++_rasterGeneration;
    double zoom = Math.Log(156543.03392804097 / viewport.Resolution, 2);
    var request = new PropagationRasterRequest(
        extent, width, height, state.DroneAltitudeAmsl, options);
    _ = Task.Run(() => PropagationService.BuildRaster(
            request,
            (lat, lng) => PropagationService.GetSrtmSample(lat, lng, zoom),
            cancellation.Token), cancellation.Token)
        .ContinueWith(task => ApplyRaster(task, key, generation),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
  }

  private void ApplyRaster(Task<PropagationRasterResult> task, RasterKey key, long generation) {
    if (task.IsCanceled || task.Exception != null) {
      if (task.Exception != null) {
        Dispatcher.UIThread.Post(() => {
          if (generation != _rasterGeneration || _suspended) {
            return;
          }
          ReleaseRasterCancellation();
          _rasterNeedsRetry = true;
          _nextRasterRetryUtc = DateTime.UtcNow.AddSeconds(2);
          _rasterStatus = "Terrain overlay failed: " +
                          (task.Exception.GetBaseException().Message ?? "unknown error");
          UpdateStatusLayer();
        });
      }
      return;
    }
    PropagationRasterResult result = task.Result;
    byte[]? png = result.ValidSampleCount > 0 ? result.EncodePng() : null;
    Dispatcher.UIThread.Post(() => {
      if (_suspended || generation != _rasterGeneration || _rasterKey != key) {
        return;
      }
      ReleaseRasterCancellation();
      _raster.Clear();
      if (png != null) {
        var feature = new RasterFeature(new MRaster(png, result.Extent));
        feature.Styles.Add(new RasterStyle());
        _raster.Add(feature);
      }
      _raster.DataHasChanged();
      _rasterNeedsRetry = result.MissingSampleCount > 0;
      _nextRasterRetryUtc = DateTime.UtcNow.AddSeconds(5);
      _rasterStatus = result.ValidSampleCount == 0
          ? "Propagation: no elevation data for this map area"
          : result.MissingSampleCount > 0
              ? $"Propagation: partial elevation data ({result.ValidSampleCount} samples)"
              : null;
      _legend = key.Options.ShowScale && result.ValidSampleCount > 0
          ? key.Options.ElevationMap
              ? $"Rel to Terrain: 0 m → {key.Options.ClearanceMeters:0.#} m"
              : $"Elevation (AMSL): {result.MaximumAltitude:0} m → {result.MinimumAltitude:0} m"
          : null;
      UpdateStatusLayer();
      _map.RefreshGraphics();
    });
  }

  private void UpdateRf(PropagationMapState state, PropagationOptions options) {
    bool hasHome = HasCoordinate(state.Home);
    if (!options.RfMap || !hasHome) {
      CancelRf();
      _rfKey = null;
      _rfNeedsRetry = false;
      _rfStatus = options.RfMap && !hasHome
          ? "RF propagation: set a Home location first"
          : null;
      Clear(_rf);
      UpdateStatusLayer();
      return;
    }

    var key = new RfKey(state.Home, QuantizeAltitude(state.DroneAltitudeAmsl), options);
    if (_rfKey == key && (!_rfNeedsRetry || DateTime.UtcNow < _nextRfRetryUtc)) {
      return;
    }
    if (_rfCancellation != null && _rfKey is { } runningRf
        && runningRf.SameExceptAltitude(key)) {
      return;
    }
    CancelRf();
    _rfKey = key;
    _rfNeedsRetry = false;
    var cancellation = new CancellationTokenSource();
    _rfCancellation = cancellation;
    long generation = ++_rfGeneration;
    _ = Task.Run(() => PropagationService.BuildRfCoverage(
            state.Home, state.DroneAltitudeAmsl, options,
            (lat, lng) => PropagationService.GetSrtmSample(lat, lng),
            cancellation.Token), cancellation.Token)
        .ContinueWith(task => ApplyRf(task, key, generation),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
  }

  private void ApplyRf(Task<PropagationRfResult> task, RfKey key, long generation) {
    if (task.IsCanceled || task.Exception != null) {
      if (task.Exception != null) {
        Dispatcher.UIThread.Post(() => {
          if (generation != _rfGeneration || _suspended) {
            return;
          }
          ReleaseRfCancellation();
          _rfNeedsRetry = true;
          _nextRfRetryUtc = DateTime.UtcNow.AddSeconds(2);
          _rfStatus = "RF propagation failed: " + task.Exception.GetBaseException().Message;
          UpdateStatusLayer();
        });
      }
      return;
    }
    PropagationRfResult result = task.Result;
    Dispatcher.UIThread.Post(() => {
      if (_suspended || generation != _rfGeneration || _rfKey != key) {
        return;
      }
      ReleaseRfCancellation();
      _rf.Clear();
      if (result.Complete) {
        var coordinates = new Coordinate[result.Points.Count];
        for (int i = 0; i < result.Points.Count; i++) {
          var (x, y) = SphericalMercator.FromLonLat(
              result.Points[i].Lng, result.Points[i].Lat);
          coordinates[i] = new Coordinate(x, y);
        }
        var feature = new GeometryFeature {
          Geometry = new Polygon(new LinearRing(coordinates)),
        };
        feature.Styles.Add(new VectorStyle {
          Fill = new Brush(Color.Transparent),
          Outline = new Pen(Color.White, 3) { PenStyle = PenStyle.Dash },
          Line = new Pen(Color.White, 3) { PenStyle = PenStyle.Dash },
        });
        _rf.Add(feature);
      }
      _rf.DataHasChanged();
      _rfNeedsRetry = result.MissingSampleCount > 0;
      _nextRfRetryUtc = DateTime.UtcNow.AddSeconds(5);
      _rfStatus = result.MissingSampleCount > 0
          ? "RF propagation: elevation data is still unavailable"
          : result.Complete ? null : "RF propagation: no coverage polygon";
      UpdateStatusLayer();
      _map.RefreshGraphics();
    });
  }

  private void UpdateDistance(PropagationMapState state, PropagationOptions options) {
    bool enabled = options.HomeDistance || options.DroneDistance;
    double battery = double.IsFinite(state.BatteryKilometersLeft)
                     && state.BatteryKilometersLeft > 0
        ? state.BatteryKilometersLeft
        : 0;
    var key = new DistanceKey(
        options.HomeDistance ? state.Home : default,
        options.DroneDistance ? state.Drone : default,
        enabled ? battery : 0,
        enabled ? options.Tolerance : 0,
        options.HomeDistance,
        options.DroneDistance);
    if (_distanceKey == key) {
      return;
    }
    _distanceKey = key;
    if (!enabled || battery <= 0) {
      Clear(_distance);
      return;
    }
    _distance.Clear();
    double radius = battery * 1000;
    if (options.HomeDistance && HasCoordinate(state.Home)) {
      AddDistancePair(state.Home, radius, options.Tolerance);
    }
    if (options.DroneDistance && HasCoordinate(state.Drone)) {
      AddDistancePair(state.Drone, radius, options.Tolerance);
    }
    _distance.DataHasChanged();
  }

  private void AddDistancePair(PropagationPoint center, double radius, double tolerance) {
    AddCircle(center, radius, new Color(255, 0, 0));
    if (double.IsFinite(tolerance) && tolerance > 0) {
      AddCircle(center, radius * tolerance, new Color(255, 165, 0));
    }
  }

  private void AddCircle(PropagationPoint center, double radius, Color color) {
    IReadOnlyList<PropagationPoint> points = PropagationService.BuildCircle(center, radius);
    if (points.Count < 4) {
      return;
    }
    var coordinates = new Coordinate[points.Count];
    for (int i = 0; i < points.Count; i++) {
      var (x, y) = SphericalMercator.FromLonLat(points[i].Lng, points[i].Lat);
      coordinates[i] = new Coordinate(x, y);
    }
    var feature = new GeometryFeature { Geometry = new LineString(coordinates) };
    feature.Styles.Add(new VectorStyle {
      Line = new Pen(color, 1) { PenStyle = PenStyle.Dash },
    });
    _distance.Add(feature);
  }

  private void UpdateStatusLayer() {
    if (_suspended) {
      return;
    }
    _status.Clear();
    var viewport = _map.Map.Navigator.Viewport;
    if (!double.IsFinite(viewport.Resolution) || viewport.Resolution <= 0) {
      _status.DataHasChanged();
      return;
    }
    MRect extent = viewport.ToExtent();
    double x = extent.MinX + 12 * viewport.Resolution;
    double y = extent.MaxY - 72 * viewport.Resolution;
    if (!string.IsNullOrWhiteSpace(_legend)) {
      AddStatusLabel(x, y, _legend!, Color.White, new Color(0, 0, 0, 145));
      y -= 28 * viewport.Resolution;
    }
    string message = string.Join(" · ", new[] { _rasterStatus, _rfStatus }
        .Where(value => !string.IsNullOrWhiteSpace(value))!);
    if (message.Length > 0) {
      AddStatusLabel(x, y, message, new Color(255, 225, 120), new Color(0, 0, 0, 180));
    }
    _status.DataHasChanged();
  }

  private void AddStatusLabel(double x, double y, string text, Color foreground,
      Color background) {
    var feature = new PointFeature(new MPoint(x, y));
    feature.Styles.Add(new LabelStyle {
      Text = text,
      ForeColor = foreground,
      BackColor = new Brush(background),
      HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Left,
      VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Top,
      Font = new Font { Size = 11 },
    });
    _status.Add(feature);
  }

  private void OnSettingsChanged() => Dispatcher.UIThread.Post(Invalidate);

  private void Invalidate() {
    _rasterKey = null;
    _rfKey = null;
    _distanceKey = null;
    _rasterNeedsRetry = false;
    _rfNeedsRetry = false;
  }

  private void CancelRaster() {
    if (_rasterCancellation == null) {
      return;
    }
    _rasterCancellation.Cancel();
    _rasterCancellation.Dispose();
    _rasterCancellation = null;
    _rasterGeneration++;
  }

  private void CancelRf() {
    if (_rfCancellation == null) {
      return;
    }
    _rfCancellation.Cancel();
    _rfCancellation.Dispose();
    _rfCancellation = null;
    _rfGeneration++;
  }

  private void ReleaseRasterCancellation() {
    _rasterCancellation?.Dispose();
    _rasterCancellation = null;
  }

  private void ReleaseRfCancellation() {
    _rfCancellation?.Dispose();
    _rfCancellation = null;
  }

  internal static double QuantizeAltitude(double altitude) =>
      double.IsFinite(altitude)
          ? Math.Round(altitude * 2, MidpointRounding.AwayFromZero) / 2
          : 0;

  private static bool HasCoordinate(PropagationPoint point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng)
      && (point.Lat != 0 || point.Lng != 0);

  private static void Clear(WritableLayer layer) {
    if (!layer.GetFeatures().Any()) {
      return;
    }
    layer.Clear();
    layer.DataHasChanged();
  }

  public void Dispose() {
    Suspend();
  }

  private readonly record struct RasterKey(
      double MinX, double MinY, double MaxX, double MaxY,
      int Width, int Height, double DroneAltitudeAmsl,
      PropagationOptions Options) {
    internal bool SameExceptAltitude(RasterKey other) =>
        this with { DroneAltitudeAmsl = other.DroneAltitudeAmsl } == other;
  }

  private readonly record struct RfKey(
      PropagationPoint Home, double DroneAltitudeAmsl, PropagationOptions Options) {
    internal bool SameExceptAltitude(RfKey other) =>
        this with { DroneAltitudeAmsl = other.DroneAltitudeAmsl } == other;
  }

  private readonly record struct DistanceKey(
      PropagationPoint Home, PropagationPoint Drone, double BatteryKilometersLeft,
      double Tolerance, bool HomeDistance, bool DroneDistance);
}
