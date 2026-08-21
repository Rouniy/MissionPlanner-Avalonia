using System;
using System.Collections.Generic;
using System.Linq;
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
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

public class MapView : MapControl {
  private const string _satelliteName = "Satellite";
  private const string _satelliteUrl =
      "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

  private TileLayer _baseLayer;
  private readonly WritableLayer _track = new() { Name = "Track" };
  private readonly WritableLayer _missionRoute = new() { Name = "Mission route" };
  private readonly WritableLayer _missionMarkers = new() { Name = "Mission waypoints" };
  private readonly WritableLayer _fence = new() { Name = "GeoFence" };
  private readonly WritableLayer _rally = new() { Name = "Rally points" };
  private readonly WritableLayer _guidedTarget = new() { Name = "Guided target" };
  private readonly WritableLayer _poi = new() { Name = "POI" };
  private readonly WritableLayer _photoMarkers = new() { Name = "Camera feedback" };
  private readonly WritableLayer _photoFootprints = new() { Name = "Camera footprints" };
  private readonly WritableLayer _vehicle = new() { Name = "Vehicle" };
  private readonly WritableLayer _traffic = new() { Name = "ADS-B traffic" };
  private readonly DispatcherTimer _timer;
  private bool _centered;
  private MPoint? _lastTrackPt;
  private readonly Queue<Coordinate> _trackPts = new();
  private bool _trafficWasVisible;
  private long _trafficRevision = -1;
  private bool _followingHeading;
  private int _lastPhotoCount = -1;
  private ulong _lastPhotoTime;
  private double _lastPhotoHfov = double.NaN;
  private double _lastPhotoVfov = double.NaN;
  private double _lastPhotoMinimumInterval = double.NaN;
  private DateTime _nextOperationalOverlayUpdateUtc = DateTime.MinValue;
  private int _lastGuidedX;
  private int _lastGuidedY;
  private float _lastGuidedZ;
  private bool _guidedWasVisible;

  public (double Lat, double Lng) LastClickLatLng { get; private set; }

  public event Action<double, double>? MapLeftClicked;

  public bool AutoPan { get; set; }

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
    _baseLayer = MapTileSourceFactory.CreateLayer(_satelliteName, _satelliteUrl, "© Esri");
    map.Layers.Add(_baseLayer);

    map.Layers.Add(_missionRoute);
    map.Layers.Add(_fence);
    map.Layers.Add(_missionMarkers);
    map.Layers.Add(_rally);
    map.Layers.Add(_poi);
    map.Layers.Add(_track);
    map.Layers.Add(_photoFootprints);
    map.Layers.Add(_photoMarkers);
    map.Layers.Add(_traffic);
    map.Layers.Add(_guidedTarget);
    _vehicle.Style = MavMarker.Vehicle(0);
    map.Layers.Add(_vehicle);

    map.Navigator.Limiter = new Mapsui.Limiting.ViewportLimiterKeepWithinExtent();
    Map = map;

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    _timer.Tick += (_, _) => {
      UpdateVehicle();
      UpdateCameraFeedback();
      UpdateTraffic();
      UpdateGuidedTarget();
      if (DateTime.UtcNow >= _nextOperationalOverlayUpdateUtc) {
        UpdateOperationalOverlays();
        _nextOperationalOverlayUpdateUtc = DateTime.UtcNow.AddSeconds(2);
      }
    };
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    MapTileSourceFactory.AccessModeChanged += OnTileAccessModeChanged;
    _timer.Start();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    _timer.Stop();
    MapTileSourceFactory.AccessModeChanged -= OnTileAccessModeChanged;
    base.OnDetachedFromVisualTree(e);
  }

  private void OnTileAccessModeChanged() {
    Dispatcher.UIThread.Post(() => {
      Map.Layers.Remove(_baseLayer);
      _baseLayer = MapTileSourceFactory.CreateLayer(_satelliteName, _satelliteUrl, "© Esri");
      Map.Layers.Add(_baseLayer);
      Map.Layers.MoveToBottom(_baseLayer);
      RefreshGraphics();
    });
  }

  public bool LiveVehicle { get; set; } = true;

  private void UpdateVehicle() {
    if (!LiveVehicle) {
      return;
    }
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

  private void UpdateOperationalOverlays() {
    var mav = AppState.comPort.MAV;
    if (mav == null) {
      ClearOperationalOverlays();
      return;
    }

    UpdateMissionOverlay(mav.wps.OrderBy(item => item.Key).ToArray(), mav.cs);
    UpdateFenceOverlay(mav.fencepoints.OrderBy(item => item.Key).ToArray());
    UpdateRallyOverlay(mav.rallypoints.OrderBy(item => item.Key).ToArray());
    UpdatePoiOverlay();
  }

  private void ClearOperationalOverlays() {
    foreach (var layer in new[] { _missionRoute, _missionMarkers, _fence, _rally, _poi }) {
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
    if (follow && AppState.comPort.MAVlist.Count > 1) {
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
    bool visible = MissionPlanner.Utilities.Settings.Instance.GetBoolean("enableadsb", false);
    if (!visible) {
      if (_trafficWasVisible) {
        _traffic.Clear();
        _traffic.DataHasChanged();
        _trafficWasVisible = false;
        _trafficRevision = -1;
      }
      return;
    }

    var snapshot = AppState.Traffic.SnapshotWithRevision();
    if (_trafficWasVisible && snapshot.Revision == _trafficRevision) {
      return;
    }
    _traffic.Clear();
    foreach (var target in snapshot.Targets) {
      var (x, y) = SphericalMercator.FromLonLat(target.Lng, target.Lat);
      var feature = new PointFeature(new MPoint(x, y));
      feature.Styles.Add(MavMarker.Traffic(target.Heading,
          target.ThreatLevel != MAVLink.MAV_COLLISION_THREAT_LEVEL.NONE));
      string label = string.IsNullOrWhiteSpace(target.CallSign) ? target.Id : target.CallSign;
      feature.Styles.Add(new LabelStyle {
        Text = $"{label}  {target.Alt:0} m",
        ForeColor = Color.White,
        BackColor = new Brush(new Color(0, 0, 0, 150)),
        Font = new Font { Size = 10 },
        Offset = new Offset(0, 13),
      });
      _traffic.Add(feature);
    }
    _traffic.DataHasChanged();
    _trafficWasVisible = true;
    _trafficRevision = snapshot.Revision;
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
      double lat = point.lat / 1e7;
      double lng = point.lng / 1e7;
      if (!double.IsFinite(lat) || !double.IsFinite(lng) ||
          lat is < -90 or > 90 || lng is < -180 or > 180 || (lat == 0 && lng == 0)) {
        continue;
      }

      double seconds = point.time_usec / 1_000_000.0;
      bool tooSoon = minimumInterval > 0 && previousSeconds != double.MinValue &&
                     seconds - previousSeconds < minimumInterval;
      previousSeconds = seconds;

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
    _lastPhotoCount = points.Length;
    _lastPhotoTime = newest;
    _lastPhotoHfov = hfov;
    _lastPhotoVfov = vfov;
    _lastPhotoMinimumInterval = minimumInterval;
    _photoMarkers.DataHasChanged();
    _photoFootprints.DataHasChanged();
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
