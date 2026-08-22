using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;
using DrawingContext = Avalonia.Media.DrawingContext;
using FormattedText = Avalonia.Media.FormattedText;
using MediaBrushes = Avalonia.Media.Brushes;
using MediaColor = Avalonia.Media.Color;
using SolidColorBrush = Avalonia.Media.SolidColorBrush;
using Typeface = Avalonia.Media.Typeface;

namespace MissionPlannerAvalonia.Controls;

public class MapView : MapControl {
  private TileLayer _baseLayer;
  private readonly WritableLayer _track = new() { Name = "Track" };
  private readonly WritableLayer _missionRoute = new() { Name = "Mission route" };
  private readonly WritableLayer _missionMarkers = new() { Name = "Mission waypoints" };
  private readonly WritableLayer _importedOverlay = new() { Name = "Imported map overlay" };
  private readonly WritableLayer _importedOverlayRaster = new() { Name = "Imported map raster" };
  private readonly WritableLayer _fence = new() { Name = "GeoFence" };
  private readonly WritableLayer _rally = new() { Name = "Rally points" };
  private readonly WritableLayer _movingBase = new() { Name = "Moving base" };
  private readonly WritableLayer _guidedTarget = new() { Name = "Guided target" };
  private readonly WritableLayer _poi = new() { Name = "POI" };
  private readonly WritableLayer _photoMarkers = new() { Name = "Camera feedback" };
  private readonly WritableLayer _photoFootprints = new() { Name = "Camera footprints" };
  private readonly WritableLayer _photoOverlap = new() { Name = "Camera overlap count" };
  private readonly WritableLayer _cameraTarget = new() { Name = "Camera target" };
  private readonly WritableLayer _otherVehicles = new() { Name = "Other vehicles" };
  private readonly WritableLayer _vehicle = new() { Name = "Vehicle" };
  private readonly WritableLayer _traffic = new() { Name = "ADS-B / AIS traffic" };
  private readonly DispatcherTimer _timer;
  private readonly AirportOverlayController _airports;
  private readonly PropagationOverlayController _propagation;
  private bool _centered;
  private MPoint? _lastTrackPt;
  private readonly Queue<Coordinate> _trackPts = new();
  private long _trafficRevision = -1;
  private bool _followingHeading;
  private int _lastPhotoCount = -1;
  private ulong _lastPhotoTime;
  private double _lastPhotoHfov = double.NaN;
  private double _lastPhotoVfov = double.NaN;
  private double _lastPhotoMinimumInterval = double.NaN;
  private CancellationTokenSource? _photoOverlapCancellation;
  private long _photoOverlapRevision;
  private bool _cameraOverlapEnabled;
  private bool _overlapHasPhotos;
  private bool _gimbalProjectionRunning;
  private bool _gimbalEligible;
  private bool _gimbalTargetVisible;
  private long _gimbalProjectionRevision;
  private DateTime _nextGimbalProjectionUtc = DateTime.MinValue;
  private bool _attached;
  private DateTime _nextOperationalOverlayUpdateUtc = DateTime.MinValue;
  private int _lastGuidedX;
  private int _lastGuidedY;
  private float _lastGuidedZ;
  private bool _guidedWasVisible;
  private bool _viewportRestored;

  public (double Lat, double Lng) LastClickLatLng { get; private set; }

  public event Action<double, double>? MapLeftClicked;

  public bool AutoPan { get; set; }

  public bool CameraOverlapEnabled {
    get => _cameraOverlapEnabled;
    set {
      if (_cameraOverlapEnabled == value) {
        return;
      }
      _cameraOverlapEnabled = value;
      // Upstream removes photo markers when the checkbox is turned off; the next map pass
      // reconstructs them from MAV.camerapoints.
      if (!value) {
        _photoMarkers.Clear();
        _photoMarkers.DataHasChanged();
        ClearCameraOverlap();
      }
      _lastPhotoCount = -1;
      _lastPhotoTime = 0;
      InvalidateVisual();
    }
  }

  public static readonly StyledProperty<int> ZoomLevelProperty =
      AvaloniaProperty.Register<MapView, int>(nameof(ZoomLevel), 16);

  public int ZoomLevel {
    get => GetValue(ZoomLevelProperty);
    set => SetValue(ZoomLevelProperty, value);
  }

  public event Action<double, double>? CursorMoved;

  static MapView() {
    ZoomLevelProperty.Changed.AddClassHandler<MapView>((m, e) => m.SetZoomLevel((int)e.NewValue!));
  }

  private static double ResolutionForLevel(int level) =>
      156543.03392804097 / Math.Pow(2, level);

  public void SetZoomLevel(int level) {
    level = Math.Clamp(level, 1, 21);
    try {
      Map?.Navigator.ZoomTo(ResolutionForLevel(level));
    } catch {

    }
  }

  public MapView() {

    var map = new Map { BackColor = new Color(0x26, 0x27, 0x28) };
    _baseLayer = MapTileSourceFactory.CreateMapLayer(MapTileSourceFactory.CurrentMapType);
    map.Layers.Add(_baseLayer);

    map.Layers.Add(_missionRoute);
    map.Layers.Add(_fence);
    map.Layers.Add(_missionMarkers);
    map.Layers.Add(_importedOverlay);
    map.Layers.Add(_importedOverlayRaster);
    map.Layers.Add(_rally);
    map.Layers.Add(_poi);
    map.Layers.Add(_track);
    map.Layers.Add(_photoFootprints);
    map.Layers.Add(_photoOverlap);
    map.Layers.Add(_photoMarkers);
    map.Layers.Add(_traffic);
    map.Layers.Add(_guidedTarget);
    map.Layers.Add(_movingBase);
    map.Layers.Add(_cameraTarget);
    map.Layers.Add(_otherVehicles);
    _vehicle.Style = MavMarker.Vehicle(0);
    map.Layers.Add(_vehicle);

    map.Navigator.Limiter = new Mapsui.Limiting.ViewportLimiterKeepWithinExtent();
    Map = map;
    ApplyImportedOverlay();
    _airports = new AirportOverlayController(this, alwaysShow: false);
    _propagation = new PropagationOverlayController(this);

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    _timer.Tick += (_, _) => {
      UpdateVehicle();
      UpdatePropagation();
      UpdateCameraFeedback();
      UpdateGimbalTarget();
      UpdateTraffic();
      UpdateGuidedTarget();
      if (DateTime.UtcNow >= _nextOperationalOverlayUpdateUtc) {
        UpdateOperationalOverlays();
        _airports.Update();
        _nextOperationalOverlayUpdateUtc = DateTime.UtcNow.AddSeconds(2);
      }
    };
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    _attached = true;
    if (_cameraOverlapEnabled) {
      // Detaching cancels an in-flight overlap calculation. Force a rebuild even
      // when the camera-point collection did not change while this view was hidden.
      _lastPhotoCount = -1;
    }
    RestoreLastViewport();
    MapTileSourceFactory.AccessModeChanged += OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged += OnMapTypeChanged;
    ImportedOverlayStore.FlightDataChanged += OnImportedOverlayChanged;
    ApplyImportedOverlay();
    _airports.Resume();
    _propagation.Resume();
    _timer.Start();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _attached = false;
    _gimbalProjectionRevision++;
    CancelCameraOverlap();
    SaveLastViewport();
    _timer.Stop();
    _airports.Suspend();
    _propagation.Suspend();
    MapTileSourceFactory.AccessModeChanged -= OnTileAccessModeChanged;
    MapTileSourceFactory.MapTypeChanged -= OnMapTypeChanged;
    ImportedOverlayStore.FlightDataChanged -= OnImportedOverlayChanged;
    base.OnDetachedFromVisualTree(e);
  }

  private void OnImportedOverlayChanged() {
    if (Dispatcher.UIThread.CheckAccess()) {
      ApplyImportedOverlay();
    } else {
      Dispatcher.UIThread.Post(ApplyImportedOverlay);
    }
  }

  private void ApplyImportedOverlay() {
    ImportedMapOverlayRenderer.Populate(
        _importedOverlay, _importedOverlayRaster, ImportedOverlayStore.FlightData);
    RefreshGraphics();
  }

  private void RestoreLastViewport() {
    if (_viewportRestored) {
      return;
    }
    _viewportRestored = true;
    try {
      var settings = Settings.Instance;
      if (!double.TryParse(settings["maplast_lat"], NumberStyles.Any,
              CultureInfo.InvariantCulture, out double lat)
          || !double.TryParse(settings["maplast_lng"], NumberStyles.Any,
              CultureInfo.InvariantCulture, out double lng)
          || !double.TryParse(settings["maplast_zoom"], NumberStyles.Any,
              CultureInfo.InvariantCulture, out double zoom)
          || !double.IsFinite(lat) || !double.IsFinite(lng) || !double.IsFinite(zoom)
          || (lat == 0 && lng == 0)) {
        return;
      }
      var (x, y) = SphericalMercator.FromLonLat(lng, lat);
      Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), ResolutionForLevel(
          Math.Clamp((int)Math.Round(zoom), 1, 21)));
      ZoomLevel = Math.Clamp((int)Math.Round(zoom), 1, 21);
      _centered = true;
    } catch {
      // A stale viewport is non-fatal; the next live vehicle update will centre the map.
    }
  }

  private void SaveLastViewport() {
    try {
      var viewport = Map.Navigator.Viewport;
      if (!double.IsFinite(viewport.CenterX) || !double.IsFinite(viewport.CenterY)
          || !double.IsFinite(viewport.Resolution) || viewport.Resolution <= 0) {
        return;
      }
      var (lng, lat) = SphericalMercator.ToLonLat(viewport.CenterX, viewport.CenterY);
      double zoom = ZoomLevelForResolution(viewport.Resolution);
      var settings = Settings.Instance;
      settings["maplast_lat"] = lat.ToString(CultureInfo.InvariantCulture);
      settings["maplast_lng"] = lng.ToString(CultureInfo.InvariantCulture);
      settings["maplast_zoom"] = zoom.ToString("0.###", CultureInfo.InvariantCulture);
      settings.Save();
    } catch {
      // Saving the viewport must never prevent Flight Data from closing.
    }
  }

  internal static double ZoomLevelForResolution(double resolution) =>
      Math.Log(156543.03392804097 / resolution, 2);

  private void OnTileAccessModeChanged() {
    Dispatcher.UIThread.Post(() => {
      Map.Layers.Remove(_baseLayer);
      _baseLayer = MapTileSourceFactory.CreateMapLayer(MapTileSourceFactory.CurrentMapType);
      Map.Layers.Add(_baseLayer);
      Map.Layers.MoveToBottom(_baseLayer);
      RefreshGraphics();
    });
  }

  private void OnMapTypeChanged(string mapType) {
    Dispatcher.UIThread.Post(() => {
      Map.Layers.Remove(_baseLayer);
      _baseLayer = MapTileSourceFactory.CreateMapLayer(mapType);
      Map.Layers.Add(_baseLayer);
      Map.Layers.MoveToBottom(_baseLayer);
      RefreshGraphics();
    });
  }

  public bool LiveVehicle { get; set; } = true;

  private void UpdatePropagation() {
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null) {
      _propagation.Update(new PropagationMapState(
          default, default, 0, double.NaN));
      return;
    }
    double multiplier = MissionPlanner.CurrentState.multiplieralt;
    double altitudeAmsl = multiplier == 0 ? cs.altasl : cs.altasl / multiplier;
    var home = cs.HomeLocation;
    _propagation.Update(new PropagationMapState(
        new PropagationPoint(home.Lat, home.Lng, home.Alt),
        new PropagationPoint(cs.lat, cs.lng, altitudeAmsl),
        altitudeAmsl,
        cs.battery_kmleft));
  }

  private void UpdateVehicle() {
    if (!LiveVehicle) {
      return;
    }
    UpdateOtherVehicles();
    var cs = AppState.comPort.MAV?.cs;
    if (cs == null || (cs.lat == 0 && cs.lng == 0)) {
      return;
    }

    var (x, y) = SphericalMercator.FromLonLat(cs.lng, cs.lat);
    var pt = new MPoint(x, y);
    _vehicle.Style = MavMarker.Vehicle(cs.yaw);
    _vehicle.Clear();
    _vehicle.Add(new PointFeature(pt));

    DrawBearingOverlays(cs, pt);
    _vehicle.DataHasChanged();

    UpdateMapRotation(cs);

    AppendTrack(pt);

    if (!_centered) {
      double res = 156543.03392804097 / Math.Pow(2, 16);
      Map.Navigator.CenterOnAndZoomTo(pt, res);
      _centered = true;
    } else if (AutoPan) {
      Map.Navigator.CenterOn(pt);
    }
  }

  private void UpdateOtherVehicles() {
    _otherVehicles.Clear();
    MAVLinkInterface activeLink = AppState.comPort;
    byte activeSysId = activeLink.MAV.sysid;
    byte activeCompId = activeLink.MAV.compid;
    foreach (MavLinkConnection connection in AppState.Connections.Snapshot()) {
      if (!connection.IsOpen) {
        continue;
      }
      foreach (MAVState mav in connection.Link.MAVlist.ToArray()) {
        if (ReferenceEquals(connection.Link, activeLink) && mav.sysid == activeSysId &&
            mav.compid == activeCompId) {
          continue;
        }
        CurrentState state = mav.cs;
        if (!ValidLatLng(state.lat, state.lng) || (state.lat == 0 && state.lng == 0)) {
          continue;
        }
        var (x, y) = SphericalMercator.FromLonLat(state.lng, state.lat);
        var feature = new PointFeature(new MPoint(x, y));
        feature.Styles.Add(MavMarker.Vehicle(state.yaw, active: false));
        _otherVehicles.Add(feature);
      }
    }
    _otherVehicles.DataHasChanged();
  }

  private void UpdateOperationalOverlays() {
    var mav = AppState.comPort.MAV;
    if (mav == null) {
      ClearOperationalOverlays();
      return;
    }

    UpdateMissionOverlay(mav.wps.OrderBy(item => item.Key).ToArray(), mav.cs);
    UpdateFenceOverlay(mav.fencepoints.OrderBy(item => item.Key).ToArray());
    UpdateRallyOverlay(mav.rallypoints.OrderBy(item => item.Key).ToArray());
    UpdateMovingBaseOverlay(mav.cs);
    UpdatePoiOverlay();
  }

  private void ClearOperationalOverlays() {
    foreach (var layer in new[] {
        _missionRoute, _missionMarkers, _fence, _rally, _movingBase, _poi,
    }) {
      layer.Clear();
      layer.DataHasChanged();
    }
  }

  private void UpdateMissionOverlay(
      IReadOnlyList<KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>> items,
      MissionPlanner.CurrentState cs) {
    _missionRoute.Clear();
    _missionMarkers.Clear();

    var route = new List<Coordinate>();
    var home = cs.HomeLocation;
    if (home == null || !ValidLatLng(home.Lat, home.Lng)) {
      home = cs.PlannedHomeLocation;
    }
    if (home != null && ValidLatLng(home.Lat, home.Lng)) {
      var projected = SphericalMercator.FromLonLat(home.Lng, home.Lat);
      route.Add(new Coordinate(projected.x, projected.y));
      _missionMarkers.Add(BuildLabeledMarker(
          home.Lat, home.Lng, "H", Color.FromArgb(255, 0, 190, 0), Color.White));
    }

    foreach (var pair in items) {
      // ArduPilot exposes the planned home as mission item zero; upstream FlightData removes it
      // and draws CurrentState.HomeLocation separately.
      if (pair.Key == 0 || !TryGlobalPosition(pair.Value, out double lat, out double lng)) {
        continue;
      }
      var projected = SphericalMercator.FromLonLat(lng, lat);
      route.Add(new Coordinate(projected.x, projected.y));
      bool current = pair.Key == cs.wpno;
      _missionMarkers.Add(BuildLabeledMarker(
          lat, lng, pair.Key.ToString(),
          current ? Color.FromArgb(255, 40, 220, 80) : Color.FromArgb(255, 255, 204, 0),
          Color.Black));
    }

    if (route.Count >= 2) {
      var feature = new GeometryFeature { Geometry = new LineString(route.ToArray()) };
      feature.Styles.Add(new VectorStyle {
        Line = new Pen(Color.FromArgb(255, 255, 220, 30), 3),
      });
      _missionRoute.Add(feature);
    }
    _missionRoute.DataHasChanged();
    _missionMarkers.DataHasChanged();
  }

  private void UpdateFenceOverlay(
      IReadOnlyList<KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>> items) {
    _fence.Clear();
    int index = 0;
    while (index < items.Count) {
      var pair = items[index];
      var command = (MAVLink.MAV_CMD)pair.Value.command;
      if (command is MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION
          or MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION) {
        int declaredCount = Math.Max(1, (int)Math.Round(pair.Value.param1));
        var vertices = new List<(int Seq, double Lat, double Lng)>();
        int next = index;
        while (next < items.Count && vertices.Count < declaredCount
               && items[next].Value.command == pair.Value.command) {
          if (TryGlobalPosition(items[next].Value, out double lat, out double lng)) {
            vertices.Add((items[next].Key, lat, lng));
          }
          next++;
        }
        AddFencePolygon(vertices, command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION);
        index = Math.Max(index + 1, next);
        continue;
      }

      if (TryGlobalPosition(pair.Value, out double pointLat, out double pointLng)) {
        if (command is MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION
            or MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION) {
          bool inclusion = command == MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION;
          AddFenceCircle(pointLat, pointLng, Math.Abs(pair.Value.param1), inclusion);
        }
        _fence.Add(BuildLabeledMarker(
            pointLat, pointLng, command == MAVLink.MAV_CMD.FENCE_RETURN_POINT ? "F" : $"F{pair.Key}",
            command == MAVLink.MAV_CMD.FENCE_RETURN_POINT
                ? Color.White
                : Color.FromArgb(255, 255, 70, 70),
            Color.Black));
      }
      index++;
    }
    _fence.DataHasChanged();
  }

  private void AddFencePolygon(
      IReadOnlyList<(int Seq, double Lat, double Lng)> vertices, bool inclusion) {
    if (vertices.Count < 3) {
      foreach (var vertex in vertices) {
        _fence.Add(BuildLabeledMarker(vertex.Lat, vertex.Lng, $"F{vertex.Seq}",
            Color.FromArgb(255, 255, 70, 70), Color.Black));
      }
      return;
    }

    var coordinates = vertices.Select(vertex => {
      var point = SphericalMercator.FromLonLat(vertex.Lng, vertex.Lat);
      return new Coordinate(point.x, point.y);
    }).ToList();
    coordinates.Add(coordinates[0]);
    var lineColor = inclusion
        ? Color.FromArgb(255, 30, 160, 255)
        : Color.FromArgb(255, 255, 60, 60);
    var fillColor = inclusion
        ? Color.FromArgb(35, 30, 160, 255)
        : Color.FromArgb(45, 255, 60, 60);
    var polygon = new GeometryFeature {
      Geometry = new Polygon(new LinearRing(coordinates.ToArray())),
    };
    polygon.Styles.Add(new VectorStyle {
      Fill = new Brush(fillColor),
      Line = new Pen(lineColor, 2),
      Outline = new Pen(lineColor, 2),
    });
    _fence.Add(polygon);
    foreach (var vertex in vertices) {
      _fence.Add(BuildLabeledMarker(vertex.Lat, vertex.Lng, $"F{vertex.Seq}", lineColor, Color.Black));
    }
  }

  private void AddFenceCircle(double lat, double lng, double radiusM, bool inclusion) {
    if (!double.IsFinite(radiusM) || radiusM <= 0) {
      return;
    }
    var center = SphericalMercator.FromLonLat(lng, lat);
    double projectedRadius = radiusM / Math.Max(0.01, Math.Cos(lat * Math.PI / 180.0));
    const int segments = 64;
    var coordinates = new Coordinate[segments + 1];
    for (int i = 0; i <= segments; i++) {
      double angle = Math.PI * 2 * i / segments;
      coordinates[i] = new Coordinate(
          center.x + Math.Cos(angle) * projectedRadius,
          center.y + Math.Sin(angle) * projectedRadius);
    }
    var lineColor = inclusion
        ? Color.FromArgb(255, 30, 160, 255)
        : Color.FromArgb(255, 255, 60, 60);
    var fillColor = inclusion
        ? Color.FromArgb(25, 30, 160, 255)
        : Color.FromArgb(40, 255, 60, 60);
    var circle = new GeometryFeature {
      Geometry = new Polygon(new LinearRing(coordinates)),
    };
    circle.Styles.Add(new VectorStyle {
      Fill = new Brush(fillColor),
      Line = new Pen(lineColor, 2) { PenStyle = PenStyle.Dash },
      Outline = new Pen(lineColor, 2) { PenStyle = PenStyle.Dash },
    });
    _fence.Add(circle);
  }

  private void UpdateRallyOverlay(
      IReadOnlyList<KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>> items) {
    _rally.Clear();
    foreach (var pair in items) {
      if (!TryGlobalPosition(pair.Value, out double lat, out double lng)) {
        continue;
      }
      var marker = BuildLabeledMarker(
          lat, lng, $"R{pair.Key}", Color.FromArgb(255, 60, 220, 80), Color.Black);
      marker.Styles.Add(new LabelStyle {
        Text = $"{pair.Value.z:0} m",
        ForeColor = Color.White,
        BackColor = new Brush(Color.FromArgb(150, 0, 0, 0)),
        Font = new Font { Size = 9 },
        Offset = new Offset(0, 15),
      });
      _rally.Add(marker);
    }
    _rally.DataHasChanged();
  }

  private void UpdateMovingBaseOverlay(MissionPlanner.CurrentState currentState) {
    _movingBase.Clear();
    var location = currentState.Base;
    if (location != null && ValidLatLng(location.Lat, location.Lng)) {
      var marker = BuildLabeledMarker(
          location.Lat, location.Lng, "BASE", Color.FromArgb(255, 0, 210, 210), Color.Black);
      marker.Styles.Add(new LabelStyle {
        Text = $"{location.Alt:0.0} m AMSL"
            + (string.IsNullOrWhiteSpace(location.Tag?.ToString()) ? "" : $"  {location.Tag}"),
        ForeColor = Color.White,
        BackColor = new Brush(Color.FromArgb(160, 0, 0, 0)),
        Font = new Font { Size = 9 },
        Offset = new Offset(0, 16),
      });
      _movingBase.Add(marker);
    }
    _movingBase.DataHasChanged();
  }

  private void UpdatePoiOverlay() {
    _poi.Clear();
    foreach (var point in Services.PoiStore.All.ToArray()) {
      if (!ValidLatLng(point.Lat, point.Lng)) {
        continue;
      }
      _poi.Add(BuildLabeledMarker(
          point.Lat, point.Lng, point.Name, Color.FromArgb(255, 255, 64, 255), Color.White));
    }
    _poi.DataHasChanged();
  }

  private void UpdateGuidedTarget() {
    var mav = AppState.comPort.MAV;
    var guided = mav.GuidedMode;
    bool hasPosition = TryGlobalPosition(guided, out double lat, out double lng);
    bool visible = string.Equals(mav.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase)
        && hasPosition;
    if (visible == _guidedWasVisible && (!visible
        || (guided.x == _lastGuidedX && guided.y == _lastGuidedY && guided.z.Equals(_lastGuidedZ)))) {
      return;
    }

    _guidedTarget.Clear();
    if (visible) {
      var marker = BuildLabeledMarker(
          lat, lng, "GUIDED", Color.FromArgb(255, 30, 130, 255), Color.White);
      marker.Styles.Add(new LabelStyle {
        Text = $"{guided.z:0} m",
        ForeColor = Color.White,
        BackColor = new Brush(Color.FromArgb(160, 0, 0, 0)),
        Font = new Font { Size = 9 },
        Offset = new Offset(0, 16),
      });
      _guidedTarget.Add(marker);
    }
    _guidedTarget.DataHasChanged();
    _guidedWasVisible = visible;
    _lastGuidedX = guided.x;
    _lastGuidedY = guided.y;
    _lastGuidedZ = guided.z;
  }

  private static PointFeature BuildLabeledMarker(
      double lat, double lng, string label, Color fill, Color textColor) {
    var projected = SphericalMercator.FromLonLat(lng, lat);
    var marker = new PointFeature(new MPoint(projected.x, projected.y));
    marker.Styles.Add(new SymbolStyle {
      SymbolType = SymbolType.Ellipse,
      Fill = new Brush(fill),
      Outline = new Pen(Color.Black, 1),
      SymbolScale = 0.65,
    });
    marker.Styles.Add(new LabelStyle {
      Text = label ?? "",
      ForeColor = textColor,
      BackColor = new Brush(Color.Transparent),
      Font = new Font { Size = 10, Bold = true },
    });
    return marker;
  }

  internal static bool TryGlobalPosition(
      MAVLink.mavlink_mission_item_int_t item, out double lat, out double lng) {
    var frame = (MAVLink.MAV_FRAME)item.frame;
    bool global = frame is MAVLink.MAV_FRAME.GLOBAL
        or MAVLink.MAV_FRAME.GLOBAL_INT
        or MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
        or MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT
        or MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT
        or MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT;
    lat = item.x / 1e7;
    lng = item.y / 1e7;
    return global && ValidLatLng(lat, lng);
  }

  private static bool ValidLatLng(double lat, double lng) =>
      double.IsFinite(lat) && double.IsFinite(lng)
      && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
      && (lat != 0 || lng != 0);

  private void UpdateMapRotation(MissionPlanner.CurrentState cs) {
    var settings = MissionPlanner.Utilities.Settings.Instance;
    bool follow = settings.GetBoolean("CHK_maprotation", false);
    if (follow && AppState.Connections.Snapshot()
        .Where(connection => connection.IsOpen)
        .Sum(connection => connection.Link.MAVlist.Count) > 1) {
      // As in upstream, heading-up is ambiguous with multiple vehicles.
      settings["CHK_maprotation"] = false.ToString();
      follow = false;
    }

    if (follow) {
      Map.Navigator.RotateTo((cs.yaw + 360.0) % 360.0);
      _followingHeading = true;
    } else if (_followingHeading) {
      Map.Navigator.RotateTo(0);
      _followingHeading = false;
    }
  }

  private void UpdateTraffic() {
    try {
      var location = AppState.comPort.MAV.cs.Location;
      if (double.IsFinite(location.Lat) && double.IsFinite(location.Lng)
          && (location.Lat != 0 || location.Lng != 0)) {
        AppState.Traffic.SetObserverPosition(location.Lat, location.Lng);
      } else {
        var viewport = Map.Navigator.Viewport;
        var (lng, lat) = SphericalMercator.ToLonLat(viewport.CenterX, viewport.CenterY);
        AppState.Traffic.SetObserverPosition(lat, lng);
      }
    } catch {
      // The HTTP receiver waits until either a map centre or vehicle location is available.
    }

    var snapshot = AppState.Traffic.SnapshotWithRevision();
    if (snapshot.Revision == _trafficRevision) {
      return;
    }
    _traffic.Clear();
    foreach (var target in snapshot.Targets) {
      if (target.Kind == TrafficKind.Obstacle) {
        _traffic.Add(BuildTrafficRadius(target.Lng, target.Lat, target.Radius));
        continue;
      }
      var (x, y) = SphericalMercator.FromLonLat(target.Lng, target.Lat);
      var feature = new PointFeature(new MPoint(x, y));
      feature.Styles.Add(target.Kind == TrafficKind.Vessel
          ? MavMarker.Vessel(target.Heading)
          : MavMarker.Traffic(target.Heading,
              target.ThreatLevel == MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH));
      string label = string.IsNullOrWhiteSpace(target.CallSign) ? target.Id : target.CallSign;
      feature.Styles.Add(new LabelStyle {
        Text = target.Kind == TrafficKind.Vessel
            ? $"{label}  {target.Speed / 100:0.0} m/s"
            : $"{label}  {target.Alt:0} m",
        ForeColor = Color.White,
        BackColor = new Brush(new Color(0, 0, 0, 150)),
        Font = new Font { Size = 10 },
        Offset = new Offset(0, 13),
      });
      _traffic.Add(feature);
    }
    _traffic.DataHasChanged();
    _trafficRevision = snapshot.Revision;
  }

  internal static GeometryFeature BuildTrafficRadius(double lng, double lat, double radiusM) {
    var center = SphericalMercator.FromLonLat(lng, lat);
    double projectedRadius = Math.Max(0, radiusM)
        / Math.Max(0.01, Math.Cos(lat * Math.PI / 180));
    const int segments = 48;
    var coordinates = new Coordinate[segments + 1];
    for (int i = 0; i <= segments; i++) {
      double angle = Math.PI * 2 * i / segments;
      coordinates[i] = new Coordinate(center.x + Math.Cos(angle) * projectedRadius,
          center.y + Math.Sin(angle) * projectedRadius);
    }
    var feature = new GeometryFeature {
      Geometry = new Polygon(new LinearRing(coordinates)),
    };
    feature.Styles.Add(new VectorStyle {
      Fill = new Brush(new Color(255, 0, 0, 35)),
      Line = new Pen(Color.Red, 3),
      Outline = new Pen(Color.Red, 3),
    });
    return feature;
  }

  private void UpdateCameraFeedback() {
    MAVLink.mavlink_camera_feedback_t[] points;
    try {
      points = AppState.comPort.MAV.camerapoints.ToArray();
    } catch {
      // The shared reader can append while List<T>.ToArray copies. Retry on the next map tick.
      return;
    }

    var settings = MissionPlanner.Utilities.Settings.Instance;
    double hfov = Math.Clamp(settings.GetDouble("camera_fovh", 63), 1, 179);
    double vfov = Math.Clamp(settings.GetDouble("camera_fovv", 43), 1, 179);
    double minimumInterval = 0;
    try {
      if (AppState.comPort.MAV.param.ContainsKey("CAM_MIN_INTERVAL")) {
        minimumInterval = AppState.comPort.MAV.param["CAM_MIN_INTERVAL"].Value / 1000.0;
      }
    } catch {
      // Parameter access is best-effort while a background parameter load is in progress.
    }
    ulong newest = points.Length == 0 ? 0 : points[^1].time_usec;
    if (points.Length == _lastPhotoCount && newest == _lastPhotoTime &&
        hfov.Equals(_lastPhotoHfov) && vfov.Equals(_lastPhotoVfov) &&
        minimumInterval.Equals(_lastPhotoMinimumInterval)) {
      return;
    }

    bool canAppendMarkers = minimumInterval.Equals(_lastPhotoMinimumInterval) &&
                            _lastPhotoCount > 0 && points.Length >= _lastPhotoCount &&
                            points[_lastPhotoCount - 1].time_usec == _lastPhotoTime;
    int firstNewMarker = canAppendMarkers ? _lastPhotoCount : 0;
    if (!canAppendMarkers) {
      _photoMarkers.Clear();
    }
    _photoFootprints.Clear();

    double previousSeconds = firstNewMarker > 0
        ? points[firstNewMarker - 1].time_usec / 1_000_000.0
        : double.MinValue;
    for (int i = firstNewMarker; i < points.Length; i++) {
      var point = points[i];
      double seconds = point.time_usec / 1_000_000.0;
      double timeSinceLastShot = previousSeconds == double.MinValue
          ? 0
          : seconds - previousSeconds;
      AppState.comPort.MAV.cs.timesincelastshot = timeSinceLastShot;
      bool tooSoon = minimumInterval > 0 && previousSeconds != double.MinValue &&
                     timeSinceLastShot < minimumInterval;
      previousSeconds = seconds;

      double lat = point.lat / 1e7;
      double lng = point.lng / 1e7;
      if (!double.IsFinite(lat) || !double.IsFinite(lng) ||
          lat is < -90 or > 90 || lng is < -180 or > 180 || (lat == 0 && lng == 0)) {
        continue;
      }

      var (x, y) = SphericalMercator.FromLonLat(lng, lat);
      var marker = new PointFeature(new MPoint(x, y));
      marker.Styles.Add(MavMarker.Camera(tooSoon));
      marker.Styles.Add(new LabelStyle {
        Text = point.img_idx.ToString(),
        ForeColor = Color.White,
        BackColor = new Brush(new Color(0, 0, 0, 150)),
        Font = new Font { Size = 9 },
        Offset = new Offset(0, 11),
      });
      _photoMarkers.Add(marker);
    }

    int firstFootprint = Math.Max(0, points.Length - 4);
    for (int i = firstFootprint; i < points.Length; i++) {
      var point = points[i];
      double lat = point.lat / 1e7;
      double lng = point.lng / 1e7;
      if (!double.IsFinite(lat) || !double.IsFinite(lng) ||
          lat is < -90 or > 90 || lng is < -180 or > 180 || (lat == 0 && lng == 0)) {
        continue;
      }
      var footprint = CameraFeedbackProjection.Project(point, hfov, vfov);
      if (footprint.Count < 3) {
        continue;
      }
      var coordinates = new List<Coordinate>(footprint.Count + 1);
      foreach (var corner in footprint) {
        var projected = SphericalMercator.FromLonLat(corner.Lng, corner.Lat);
        coordinates.Add(new Coordinate(projected.x, projected.y));
      }
      coordinates.Add(coordinates[0]);
      var polygon = new GeometryFeature {
        Geometry = new Polygon(new LinearRing(coordinates.ToArray())),
      };
      polygon.Styles.Add(new VectorStyle {
        Fill = new Brush(new Color(220, 20, 60, 24)),
        Line = new Pen(new Color(220, 20, 60), 1.5),
      });
      _photoFootprints.Add(polygon);
    }

    if (_cameraOverlapEnabled) {
      _overlapHasPhotos = points.Length > 0;
      StartCameraOverlap(points, hfov, vfov);
    } else {
      _overlapHasPhotos = false;
      ClearCameraOverlap();
    }
    _lastPhotoCount = points.Length;
    _lastPhotoTime = newest;
    _lastPhotoHfov = hfov;
    _lastPhotoVfov = vfov;
    _lastPhotoMinimumInterval = minimumInterval;
    _photoMarkers.DataHasChanged();
    _photoFootprints.DataHasChanged();
    InvalidateVisual();
  }

  private void StartCameraOverlap(MAVLink.mavlink_camera_feedback_t[] points,
                                  double hfov, double vfov) {
    CancelCameraOverlap();
    long revision = ++_photoOverlapRevision;
    _photoOverlap.Clear();
    _photoOverlap.DataHasChanged();
    var cancellation = new CancellationTokenSource();
    _photoOverlapCancellation = cancellation;
    CancellationToken token = cancellation.Token;

    _ = Task.Run(() => {
      IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> footprints =
          CameraOverlapProjection.BuildFootprints(points, hfov, vfov, token);
      return OverlapCoverageBuilder.Build(footprints, token);
    }, token)
        .ContinueWith(task => {
          if (task.IsFaulted) {
            _ = task.Exception;
          }
          if (task.Status != TaskStatus.RanToCompletion || token.IsCancellationRequested) {
            return;
          }
          Dispatcher.UIThread.Post(() => {
            if (!_attached || token.IsCancellationRequested ||
                revision != _photoOverlapRevision || !_cameraOverlapEnabled) {
              return;
            }
            DrawCameraOverlap(task.Result);
          });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
  }

  private void DrawCameraOverlap(IReadOnlyList<OverlapCoveragePoint> coverage) {
    _photoOverlap.Clear();
    foreach (OverlapCoveragePoint point in coverage) {
      _photoOverlap.Add(CameraOverlapFeature(point));
    }
    _photoOverlap.DataHasChanged();
    RefreshGraphics();
    InvalidateVisual();
  }

  private void ClearCameraOverlap() {
    CancelCameraOverlap();
    _overlapHasPhotos = false;
    _photoOverlap.Clear();
    _photoOverlap.DataHasChanged();
  }

  private void CancelCameraOverlap() {
    CancellationTokenSource? cancellation = _photoOverlapCancellation;
    _photoOverlapCancellation = null;
    _photoOverlapRevision++;
    if (cancellation == null) {
      return;
    }
    cancellation.Cancel();
    cancellation.Dispose();
  }

  internal static GeometryFeature CameraOverlapFeature(OverlapCoveragePoint point) {
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
    Color color = OverlapCoverageBuilder.ColorForCount(point.Count);
    var feature = new GeometryFeature {
      Geometry = new Polygon(new LinearRing(coordinates)),
    };
    feature.Styles.Add(new VectorStyle {
      Fill = new Brush(color),
      Line = null,
      Outline = null,
    });
    return feature;
  }

  private void UpdateGimbalTarget() {
    float? stabilizeTilt = MountParameter("MNT_STAB_TILT");
    float? stabilizeRoll = MountParameter("MNT_STAB_ROLL");
    float? mountType = MountParameter("MNT_TYPE");
    bool hasStabilizePan = HasMountParameter("MNT_STAB_PAN");
    bool eligible = GimbalTargetProjection.ShouldProject(
        stabilizeTilt, stabilizeRoll, mountType, hasStabilizePan);
    if (!eligible) {
      if (_gimbalEligible) {
        _gimbalEligible = false;
        _gimbalProjectionRevision++;
      }
      ClearGimbalTarget();
      return;
    }

    _gimbalEligible = true;
    if (_gimbalProjectionRunning || DateTime.UtcNow < _nextGimbalProjectionUtc) {
      return;
    }
    _gimbalProjectionRunning = true;
    _nextGimbalProjectionUtc = DateTime.UtcNow.AddSeconds(1);
    long revision = ++_gimbalProjectionRevision;
    var comPort = AppState.comPort;
    _ = Task.Run(() => GimbalPoint.ProjectPoint(comPort))
        .ContinueWith(task => {
          if (task.IsFaulted) {
            _ = task.Exception;
          }
          Dispatcher.UIThread.Post(() => {
            _gimbalProjectionRunning = false;
            if (!_attached || revision != _gimbalProjectionRevision || !_gimbalEligible) {
              return;
            }
            if (task.Status != TaskStatus.RanToCompletion ||
                !GimbalTargetProjection.IsValid(task.Result)) {
              ClearGimbalTarget();
              return;
            }
            DrawGimbalTarget(task.Result);
          });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
  }

  private static float? MountParameter(string name) {
    try {
      var parameters = AppState.comPort.MAV.param;
      return parameters.ContainsKey(name) ? (float)parameters[name] : null;
    } catch {
      // Parameters can be replaced while the background parameter download completes.
      return null;
    }
  }

  private static bool HasMountParameter(string name) {
    try {
      return AppState.comPort.MAV.param.ContainsKey(name);
    } catch {
      return false;
    }
  }

  private void DrawGimbalTarget(PointLatLngAlt point) {
    AppState.comPort.MAV.cs.GimbalPoint = point;
    var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
    var feature = new PointFeature(new MPoint(x, y));
    feature.Styles.Add(new SymbolStyle {
      SymbolType = SymbolType.Ellipse,
      Fill = new Brush(new Color(0x2F, 0x81, 0xF7)),
      Outline = new Pen(Color.White, 1),
      SymbolScale = 0.55,
    });
    feature.Styles.Add(new LabelStyle {
      Text = "Camera Target",
      ForeColor = Color.White,
      BackColor = new Brush(new Color(0, 0, 0, 150)),
      Font = new Font { Size = 10 },
      Offset = new Offset(0, 13),
    });
    _cameraTarget.Clear();
    _cameraTarget.Add(feature);
    _cameraTarget.DataHasChanged();
    _gimbalTargetVisible = true;
  }

  private void ClearGimbalTarget() {
    if (!_gimbalTargetVisible) {
      return;
    }
    _cameraTarget.Clear();
    _cameraTarget.DataHasChanged();
    _gimbalTargetVisible = false;
  }

  private static readonly VectorStyle _trackStyle = new() {
    Line = new Pen(new Color(255, 220, 30), 2),
  };

  private static readonly VectorStyle _headingStyle = new() {
    Line = new Pen(new Color(255, 0, 0), 2),
  };
  private static readonly VectorStyle _cogStyle = new() {
    Line = new Pen(new Color(0, 0, 0), 2),
  };
  private static readonly VectorStyle _navBearingStyle = new() {
    Line = new Pen(new Color(0, 128, 0), 2),
  };
  private static readonly VectorStyle _targetStyle = new() {
    Line = new Pen(new Color(255, 165, 0), 2),
  };
  private static readonly VectorStyle _radiusStyle = new() {
    Line = new Pen(new Color(255, 105, 180), 2),
  };

  private void DrawBearingOverlays(MissionPlanner.CurrentState cs, MPoint pt) {
    double resMpp = Map.Navigator.Viewport.Resolution;
    if (resMpp <= 0) {
      return;
    }

    var s = MissionPlanner.Utilities.Settings.Instance;
    double lenPx = s.GetInt32("GMapMarkerBase_Length", 500);
    double len = lenPx * resMpp;

    if (s.GetBoolean("GMapMarkerBase_DisplayHeading", true)) {
      AddBearingLine(pt, cs.yaw, len, _headingStyle);
    }
    if (s.GetBoolean("GMapMarkerBase_DisplayNavBearing", true)) {
      AddBearingLine(pt, cs.nav_bearing, len, _navBearingStyle);
    }
    if (s.GetBoolean("GMapMarkerBase_DisplayCOG", true)) {
      AddBearingLine(pt, cs.groundcourse, len, _cogStyle);
    }
    if (s.GetBoolean("GMapMarkerBase_DisplayTarget", true)) {
      AddBearingLine(pt, cs.target_bearing, len, _targetStyle);
    }
    if (s.GetBoolean("GMapMarkerBase_DisplayRadius", true)) {
      AddRadiusArc(pt, cs.groundcourse, cs.radius, resMpp);
    }
  }

  private void AddBearingLine(MPoint pt, double bearingDeg, double len, VectorStyle style) {
    double rad = bearingDeg * Math.PI / 180.0;
    var end = new MPoint(pt.X + Math.Sin(rad) * len, pt.Y + Math.Cos(rad) * len);
    var line = new GeometryFeature {
      Geometry = new LineString(new[] { new Coordinate(pt.X, pt.Y), new Coordinate(end.X, end.Y) }),
    };
    line.Styles.Add(style);
    _vehicle.Add(line);
  }

  private void AddRadiusArc(MPoint pt, double cogDeg, double radius, double resMpp) {
    if (Math.Abs(radius) <= 1) {
      return;
    }

    const double desiredLeadDist = 100.0;
    double m2pixelwidth = 1.0 / resMpp;
    double alpha = desiredLeadDist * m2pixelwidth / radius * (180.0 / Math.PI);
    if (Math.Abs(alpha) <= 1) {
      return;
    }
    alpha = Math.Clamp(alpha, -360.0, 360.0);

    double radiusM = radius;
    double cog = cogDeg * Math.PI / 180.0;
    double cx = pt.X + Math.Cos(cog) * radiusM;
    double cy = pt.Y + Math.Sin(cog) * radiusM;
    double start = (cogDeg - 180.0) * Math.PI / 180.0;

    var coords = new List<Coordinate>();
    const int steps = 24;
    for (int i = 0; i <= steps; i++) {
      double theta = start + alpha * (Math.PI / 180.0) * i / steps;
      coords.Add(new Coordinate(cx + Math.Cos(theta) * radiusM, cy + Math.Sin(theta) * radiusM));
    }
    var arc = new GeometryFeature { Geometry = new LineString(coords.ToArray()) };
    arc.Styles.Add(_radiusStyle);
    _vehicle.Add(arc);
  }

  private void AppendTrack(MPoint pt) {
    if (_lastTrackPt is { } prev) {
      double dx = pt.X - prev.X, dy = pt.Y - prev.Y;
      if (Math.Sqrt(dx * dx + dy * dy) < 0.5) {
        return;
      }
    }
    _lastTrackPt = pt;
    _trackPts.Enqueue(new Coordinate(pt.X, pt.Y));
    int maximumTrackPoints = Math.Clamp(
        MissionPlanner.Utilities.Settings.Instance.GetInt32("NUM_tracklength", 200), 100, 50000);
    while (_trackPts.Count > maximumTrackPoints) {
      _trackPts.Dequeue();
    }
    if (_trackPts.Count < 2) {
      return;
    }

    var line = new GeometryFeature { Geometry = new LineString(_trackPts.ToArray()) };
    line.Styles.Add(_trackStyle);
    _track.Clear();
    _track.Add(line);
    _track.DataHasChanged();
  }

  public void ShowStaticTrack(IReadOnlyList<(double Lat, double Lng)> pts) {
    _track.Clear();
    _trackPts.Clear();
    if (pts.Count == 0) {
      _track.DataHasChanged();
      return;
    }
    foreach (var (lat, lng) in pts) {
      if (lat == 0 && lng == 0) {
        continue;
      }
      var (x, y) = SphericalMercator.FromLonLat(lng, lat);
      _trackPts.Enqueue(new Coordinate(x, y));
    }
    if (_trackPts.Count >= 2) {
      var line = new GeometryFeature { Geometry = new LineString(_trackPts.ToArray()) };
      line.Styles.Add(_trackStyle);
      _track.Add(line);
    }
    _track.DataHasChanged();
    if (_trackPts.Count > 0) {
      double res = 156543.03392804097 / Math.Pow(2, 15);
      var first = _trackPts.Peek();
      Map.Navigator.CenterOnAndZoomTo(new MPoint(first.X, first.Y), res);
      _centered = true;
    }
  }

  public void ShowSampleMarker(double lat, double lng) {
    if (lat == 0 && lng == 0) {
      return;
    }
    var (x, y) = SphericalMercator.FromLonLat(lng, lat);
    var pt = new MPoint(x, y);
    _vehicle.Style = MavMarker.Vehicle(0);
    _vehicle.Clear();
    _vehicle.Add(new PointFeature(pt));
    _vehicle.DataHasChanged();
    Map.Navigator.CenterOn(pt);
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    if (!_cameraOverlapEnabled || !_overlapHasPhotos) {
      return;
    }

    const double diameter = 20;
    const double radius = diameter / 2;
    for (int index = 0; index < OverlapCoverageBuilder.Colors.Count; index++) {
      Color color = OverlapCoverageBuilder.Colors[index];
      var fill = new SolidColorBrush(MediaColor.FromArgb(
          (byte)color.A, (byte)color.R, (byte)color.G, (byte)color.B));
      var center = new Avalonia.Point(20, 100 + index * (diameter + 5));
      context.DrawEllipse(fill, null, center, radius, radius);
      var text = new FormattedText(
          (index + 1).ToString(CultureInfo.InvariantCulture),
          CultureInfo.InvariantCulture, global::Avalonia.Media.FlowDirection.LeftToRight,
          Typeface.Default, 12, MediaBrushes.White);
      context.DrawText(text, new Avalonia.Point(
          center.X - text.Width / 2, center.Y - text.Height / 2));
    }
  }

  public void ClearTrack() {
    _track.Clear();
    _trackPts.Clear();
    _lastTrackPt = null;
    _track.DataHasChanged();
    try {
      AppState.comPort.MAV.camerapoints.Clear();
    } catch {
    }
    _photoMarkers.Clear();
    _photoMarkers.DataHasChanged();
    _photoFootprints.Clear();
    _photoFootprints.DataHasChanged();
    ClearCameraOverlap();
    _lastPhotoCount = 0;
    _lastPhotoTime = 0;
  }

  private (double Lat, double Lng) ToLatLng(Avalonia.Point screen) {
    var w = Map.Navigator.Viewport.ScreenToWorld(screen.X, screen.Y);
    var (lng, lat) = SphericalMercator.ToLonLat(w.X, w.Y);
    return (lat, lng);
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    LastClickLatLng = ToLatLng(e.GetPosition(this));
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    var ll = ToLatLng(e.GetPosition(this));
    LastClickLatLng = ll;
    if (e.InitialPressMouseButton == MouseButton.Left) {
      MapLeftClicked?.Invoke(ll.Lat, ll.Lng);
    }
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (CursorMoved == null) {
      return;
    }
    var (lat, lng) = ToLatLng(e.GetPosition(this));
    CursorMoved.Invoke(lat, lng);
  }

  public void CenterOn(double lat, double lng) {
    var (x, y) = SphericalMercator.FromLonLat(lng, lat);
    Map.Navigator.CenterOn(new MPoint(x, y));
  }
}

internal static class CameraFeedbackProjection {
  internal static IReadOnlyList<(double Lat, double Lng)> Project(
      MAVLink.mavlink_camera_feedback_t point, double hfov, double vfov) {
    if (point.alt_msl <= 0 || !float.IsFinite(point.alt_msl) ||
        !double.IsFinite(hfov) || !double.IsFinite(vfov) ||
        hfov is <= 0 or >= 180 || vfov is <= 0 or >= 180) {
      return Array.Empty<(double, double)>();
    }

    try {
      var location = new MissionPlanner.Utilities.PointLatLngAlt(
          point.lat / 1e7, point.lng / 1e7, point.alt_msl);
      var projected = MissionPlanner.Utilities.ImageProjection.calc(
          location, point.roll, point.pitch, point.yaw, hfov, vfov);
      var result = new List<(double Lat, double Lng)>(projected.Count);
      foreach (var corner in projected) {
        if (double.IsFinite(corner.Lat) && double.IsFinite(corner.Lng) &&
            corner.Lat is >= -90 and <= 90 && corner.Lng is >= -180 and <= 180 &&
            !result.Exists(existing => Math.Abs(existing.Lat - corner.Lat) < 1e-9 &&
                                       Math.Abs(existing.Lng - corner.Lng) < 1e-9)) {
          result.Add((corner.Lat, corner.Lng));
        }
      }
      return result.Count >= 3 ? result : Array.Empty<(double, double)>();
    } catch {
      // Bad camera attitude or unavailable terrain data must not break the live map timer.
      return Array.Empty<(double, double)>();
    }
  }
}

internal static class CameraOverlapProjection {
  internal static IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> BuildFootprints(
      IReadOnlyList<MAVLink.mavlink_camera_feedback_t> points,
      double hfov,
      double vfov,
      CancellationToken cancellationToken = default,
      Func<MAVLink.mavlink_camera_feedback_t, double, double,
          IReadOnlyList<(double Lat, double Lng)>>? projector = null) {
    projector ??= CameraFeedbackProjection.Project;
    var result = new List<IReadOnlyList<(double Lat, double Lng)>>();
    var seen = new HashSet<ulong>();
    foreach (MAVLink.mavlink_camera_feedback_t point in points) {
      cancellationToken.ThrowIfCancellationRequested();
      // GMapMarkerOverlapCount receives only unique GMapMarkerPhoto footprints with this roll gate.
      if (!seen.Add(point.time_usec) || Math.Abs(point.roll) >= 25) {
        continue;
      }
      IReadOnlyList<(double Lat, double Lng)> footprint = projector(point, hfov, vfov);
      if (footprint.Count >= 3) {
        result.Add(footprint);
      }
    }
    return result;
  }
}

internal static class GimbalTargetProjection {
  internal static bool ShouldProject(float? stabilizeTilt, float? stabilizeRoll,
                                     float? mountType, bool hasStabilizePan) =>
      stabilizeTilt.HasValue && stabilizeRoll.HasValue && mountType.HasValue &&
      (hasStabilizePan && stabilizeTilt.Value == 1 && stabilizeRoll.Value == 0 ||
       mountType.Value == 4);

  internal static bool IsValid(PointLatLngAlt? point) =>
      point != null && double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180 &&
      (point.Lat != 0 || point.Lng != 0);
}
