using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.ViewModels;

public partial class FlightPlannerViewModel : ViewModelBase {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private bool _recomputing;
  private bool _restoringUndo;
  private int _undoMutationDepth;
  private readonly List<MissionSnapshot> _undoHistory = [];

  public event Action? WaypointsChanged;

  public ObservableCollection<WpRow> Waypoints { get; } = [];

  public event Action? PoiChanged;

  public FlightPlannerViewModel() {
    Waypoints.CollectionChanged += OnWaypointsCollectionChanged;
    try {
      Services.PoiStore.Load();
    } catch {

    }
    try {
      var s = Settings.Instance;
      if (s["fpminaltwarning"] != null
          && double.TryParse(s["fpminaltwarning"], NumberStyles.Any, CultureInfo.InvariantCulture,
              out var aw)) {
        _altWarn = aw;
      }
      _verifyHeight = s.GetBoolean("fpverifyheight", false);
      LoadPlannerSettings(s);
    } catch {

    }
  }

  private void LoadPlannerSettings(Settings settings) {
    HomeLat = LoadDouble(settings, "TXT_homelat", HomeLat);
    HomeLng = LoadDouble(settings, "TXT_homelng", HomeLng);
    HomeAlt = FromDisplayAltitude(
        LoadDouble(settings, "TXT_homealt", ToDisplayAltitude(HomeAlt)));
    WpRadius = LoadDouble(settings, "TXT_WPRad", WpRadius);
    LoiterRadius = LoadDouble(settings, "TXT_loiterrad", LoiterRadius);
    DefaultAlt = FromDisplayAltitude(
        LoadDouble(settings, "TXT_DefaultAlt", ToDisplayAltitude(DefaultAlt)));

    string? savedMapType = settings["MapType"];
    if (!string.IsNullOrWhiteSpace(savedMapType) && MapTypes.Contains(savedMapType)) {
      MapType = savedMapType;
    }
    ShowGrid = settings.GetBoolean("FP_showgrid", false);

    string? savedFrame = settings["FPaltmode"];
    if (byte.TryParse(savedFrame, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out byte frame)) {
      DefaultFrame = FrameName(frame);
    } else if (settings["CMB_altmode"] is { Length: > 0 } legacyFrame
               && DefaultFrames.Contains(legacyFrame)) {
      DefaultFrame = legacyFrame;
    }
  }

  private static double LoadDouble(Settings settings, string key, double fallback) {
    if (double.TryParse(settings[key], NumberStyles.Any, CultureInfo.InvariantCulture,
            out double value) && double.IsFinite(value)) {
      return value;
    }
    return fallback;
  }

  internal void SavePlannerSettings() {
    try {
      var settings = Settings.Instance;
      settings["TXT_homelat"] = F(HomeLat);
      settings["TXT_homelng"] = F(HomeLng);
      settings["TXT_homealt"] = HomeAltDisplay.ToString(CultureInfo.InvariantCulture);
      settings["TXT_WPRad"] = WpRadius.ToString(CultureInfo.InvariantCulture);
      settings["TXT_loiterrad"] = LoiterRadius.ToString(CultureInfo.InvariantCulture);
      settings["TXT_DefaultAlt"] = DefaultAltDisplay.ToString(CultureInfo.InvariantCulture);
      settings["FPaltmode"] = DefaultFrameId.ToString(CultureInfo.InvariantCulture);
      settings["CMB_altmode"] = DefaultFrame;
      settings["MapType"] = MapType;
      settings["FP_showgrid"] = ShowGrid.ToString();
      settings.Save();
    } catch {
      // Planner state is a convenience; a read-only or damaged settings file must not close it.
    }
  }

  public bool CanUndo => _undoHistory.Count > 0;

  private IDisposable BeginUndoMutation() {
    if (_undoMutationDepth == 0) {
      CaptureUndo();
    }
    _undoMutationDepth++;
    return new UndoMutationScope(this);
  }

  private void EndUndoMutation() => _undoMutationDepth--;

  private void CaptureUndo() {
    if (_restoringUndo || _undoMutationDepth > 0) {
      return;
    }

    var snapshot = new MissionSnapshot(
        MissionType,
        EffectiveRows("Mission"),
        EffectiveRows("Fence"),
        EffectiveRows("Rally"),
        HomeLat, HomeLng, HomeAlt);
    if (_undoHistory.Count > 0 && SameMission(_undoHistory[^1], snapshot)) {
      return;
    }
    _undoHistory.Add(snapshot);
    if (_undoHistory.Count > 40) {
      _undoHistory.RemoveRange(0, _undoHistory.Count - 40);
    }
    OnPropertyChanged(nameof(CanUndo));
  }

  private List<WpRow> EffectiveRows(string type) =>
      [.. (MissionType == type ? Waypoints.AsEnumerable() : StoreFor(type)).Select(CloneRow)];

  [RelayCommand]
  private void Undo() {
    if (_undoHistory.Count == 0) {
      Status = "Nothing to undo.";
      return;
    }

    var snapshot = _undoHistory[^1];
    _undoHistory.RemoveAt(_undoHistory.Count - 1);
    _restoringUndo = true;
    try {
      HomeLat = snapshot.HomeLat;
      HomeLng = snapshot.HomeLng;
      HomeAlt = snapshot.HomeAlt;
      ReplaceStore(_missionStore, snapshot.MissionRows);
      ReplaceStore(_fenceStore, snapshot.FenceRows);
      ReplaceStore(_rallyStore, snapshot.RallyRows);
      MissionType = snapshot.MissionType;
      var current = StoreFor(snapshot.MissionType);
      Replace(current.Select(CloneRow).ToList());
      Status = $"Undo restored {current.Count} {snapshot.MissionType.ToLowerInvariant()} item(s).";
    } finally {
      _restoringUndo = false;
    }
    OnPropertyChanged(nameof(CanUndo));
  }

  private static void ReplaceStore(List<WpRow> target, IEnumerable<WpRow> source) {
    target.Clear();
    target.AddRange(source.Select(CloneRow));
  }

  private static WpRow CloneRow(WpRow row) => new() {
    Seq = row.Seq,
    Command = row.Command,
    Frame = row.Frame,
    P1 = row.P1,
    P2 = row.P2,
    P3 = row.P3,
    P4 = row.P4,
    Lat = row.Lat,
    Lng = row.Lng,
    Alt = row.Alt,
  };

  private static bool SameMission(MissionSnapshot left, MissionSnapshot right) =>
      left.MissionType == right.MissionType
      && left.HomeLat.Equals(right.HomeLat)
      && left.HomeLng.Equals(right.HomeLng)
      && left.HomeAlt.Equals(right.HomeAlt)
      && SameRows(left.MissionRows, right.MissionRows)
      && SameRows(left.FenceRows, right.FenceRows)
      && SameRows(left.RallyRows, right.RallyRows);

  private static bool SameRows(IReadOnlyList<WpRow> left, IReadOnlyList<WpRow> right) =>
      left.Count == right.Count
      && left.Zip(right).All(pair => SameRow(pair.First, pair.Second));

  private static bool SameRow(WpRow left, WpRow right) =>
      left.Command == right.Command && left.Frame == right.Frame
      && left.P1.Equals(right.P1) && left.P2.Equals(right.P2)
      && left.P3.Equals(right.P3) && left.P4.Equals(right.P4)
      && left.Lat.Equals(right.Lat) && left.Lng.Equals(right.Lng)
      && left.Alt.Equals(right.Alt);

  private sealed record MissionSnapshot(
      string MissionType,
      List<WpRow> MissionRows,
      List<WpRow> FenceRows,
      List<WpRow> RallyRows,
      double HomeLat,
      double HomeLng,
      double HomeAlt);

  private sealed class UndoMutationScope(FlightPlannerViewModel owner) : IDisposable {
    public void Dispose() => owner.EndUndoMutation();
  }

  public async Task AddPoi(double lat, double lng) {
    var name = await Services.Dialogs.InputBox("Add POI", "Name", "POI");
    if (name == null) {
      return;
    }
    Services.PoiStore.Add(lat, lng, DefaultAlt, name);
    PoiChanged?.Invoke();
  }

  public async Task AddPoiAtCoords() {
    var s = await Services.Dialogs.InputBox("POI at Coords", "lat;lng", "0;0");
    var parts = s?.Split(';');
    if (parts is { Length: >= 2 }
        && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)
        && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var lng)) {
      await AddPoi(lat, lng);
    }
  }

  public void DeleteNearestPoi(double lat, double lng) {
    Services.PoiPoint? best = NearestPoi(lat, lng);
    if (best != null) {
      Services.PoiStore.Remove(best);
      PoiChanged?.Invoke();
    }
  }

  public async Task EditNearestPoi(double lat, double lng) {
    Services.PoiPoint? poi = NearestPoi(lat, lng);
    if (poi == null) {
      Status = "No POI to edit.";
      return;
    }
    string? name = await Services.Dialogs.InputBox("Edit POI", "Name", poi.Name);
    if (name != null && Services.PoiStore.Replace(poi, poi with { Name = name })) {
      PoiChanged?.Invoke();
      Status = "POI updated.";
    }
  }

  private static Services.PoiPoint? NearestPoi(double lat, double lng) {
    Services.PoiPoint? best = null;
    double bestDistance = double.MaxValue;
    foreach (var point in Services.PoiStore.All) {
      double distance = (point.Lat - lat) * (point.Lat - lat)
          + (point.Lng - lng) * (point.Lng - lng);
      if (distance < bestDistance) {
        bestDistance = distance;
        best = point;
      }
    }
    return best;
  }

  public void ClearPois() {
    Services.PoiStore.Clear();
    PoiChanged?.Invoke();
  }

  public event Action? DrawnPolygonChanged;

  public List<PointLatLngAlt> DrawnPolygon { get; } = [];

  public void AddPolygonPoint(double lat, double lng) {
    DrawnPolygon.Add(new PointLatLngAlt(lat, lng, DefaultAlt));
    DrawnPolygonChanged?.Invoke();
  }

  public void ClearPolygon() {
    DrawnPolygon.Clear();
    DrawnPolygonChanged?.Invoke();
    Status = "Polygon cleared.";
  }

  public void BuildPolygonFromWaypoints() {
    DrawnPolygon.Clear();
    foreach (var w in Waypoints) {
      if (w.Command != (ushort)MAVLink.MAV_CMD.WAYPOINT
          || (w.Lat == 0 && w.Lng == 0)) {
        continue;
      }
      DrawnPolygon.Add(new PointLatLngAlt(w.Lat, w.Lng, w.Alt));
    }
    DrawnPolygonChanged?.Invoke();
    Status = $"Polygon built from {DrawnPolygon.Count} point(s).";
  }

  public void AddDrawnPolygonToFence(bool inclusion) {
    if (DrawnPolygon.Count < 3) {
      Status = "Draw at least 3 polygon points first.";
      return;
    }
    if (MissionType != "Fence") {
      MissionType = "Fence";
    }
    using var undo = BeginUndoMutation();
    var command = inclusion
        ? MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION
        : MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
    int count = DrawnPolygon.Count;
    foreach (var point in DrawnPolygon) {
      AddCommandRow(command, point.Lat, point.Lng, 0, p1: count,
          frame: (byte)MAVLink.MAV_FRAME.GLOBAL);
    }
    Status = $"Added {(inclusion ? "inclusion" : "exclusion")} fence polygon ({count} vertices).";
  }

  public async Task AddFenceCircle(double lat, double lng, bool inclusion) {
    var text = await Services.Dialogs.InputBox(
        "Fence circle", "Radius (m)", "100");
    if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double radius)
        || radius <= 0) {
      Status = "Fence circle radius must be greater than zero.";
      return;
    }
    if (MissionType != "Fence") {
      MissionType = "Fence";
    }
    using var undo = BeginUndoMutation();
    AddCommandRow(
        inclusion ? MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION : MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION,
        lat, lng, 0, p1: radius, frame: (byte)MAVLink.MAV_FRAME.GLOBAL);
    Status = $"Added {(inclusion ? "inclusion" : "exclusion")} fence circle ({radius:0.##} m).";
  }

  public string PolygonArea() {
    if (DrawnPolygon.Count < 3) {
      Status = "Need at least 3 polygon points for area.";
      return Status;
    }
    double area = PolygonAreaM2(DrawnPolygon);
    Status = $"Polygon area: {area:0} m² ({area / 10000:0.00} ha).";
    return Status;
  }

  public async Task OffsetDrawnPolygonAsync() {
    if (DrawnPolygon.Count < 3) {
      Status = "Need at least 3 polygon points to offset.";
      return;
    }
    string? input = await Services.Dialogs.InputBox(
        "Offset polygon",
        "Offset in metres (negative makes the polygon smaller)",
        "0");
    if (!double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double meters) || !double.IsFinite(meters) || meters == 0) {
      Status = input == null ? "Polygon offset cancelled." : "Invalid polygon offset.";
      return;
    }

    IReadOnlyList<PointLatLngAlt> offset = Services.PlannerGeometry.OffsetPolygon(
        DrawnPolygon, meters);
    if (offset.Count < 3) {
      Status = "The requested offset collapses the polygon.";
      return;
    }
    using var undo = BeginUndoMutation();
    DrawnPolygon.Clear();
    DrawnPolygon.AddRange(offset);
    DrawnPolygonChanged?.Invoke();
    Status = $"Polygon offset by {meters:0.##} m.";
  }

  public async Task AddWaypointFromUtmAsync(double defaultLat, double defaultLng) {
    var (defaultZone, defaultEasting, defaultNorthing) = Services.Geo.ToUtm(defaultLat, defaultLng);
    string zoneText = Math.Abs(defaultZone).ToString(CultureInfo.InvariantCulture) +
        (defaultZone < 0 ? "S" : "N");
    string? zoneInput = await Services.Dialogs.InputBox(
        "UTM waypoint", "Zone (for example 50S or 11N)", zoneText);
    if (zoneInput == null) {
      return;
    }
    string? eastingInput = await Services.Dialogs.InputBox(
        "UTM waypoint", "Easting", defaultEasting.ToString("0.##", CultureInfo.InvariantCulture));
    if (eastingInput == null) {
      return;
    }
    string? northingInput = await Services.Dialogs.InputBox(
        "UTM waypoint", "Northing", defaultNorthing.ToString("0.##", CultureInfo.InvariantCulture));
    if (!Services.Geo.TryParseUtmZone(zoneInput, out int zone)
        || !double.TryParse(eastingInput, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double easting)
        || !double.TryParse(northingInput, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double northing)) {
      await Services.Dialogs.Alert("UTM waypoint", "Invalid UTM zone, easting or northing.");
      return;
    }

    try {
      var (lat, lng) = Services.Geo.FromUtm(easting, northing, zone);
      AddWaypointAt(lat, lng);
      Status = $"Added waypoint from UTM {zoneInput.Trim().ToUpperInvariant()}.";
    } catch (Exception ex) {
      await Services.Dialogs.Alert("UTM waypoint", "Conversion failed: " + ex.Message);
    }
  }

  public async Task SetTrackerHomeAsync(double lat, double lng) {
    double currentMeters = _comPort.MAV.cs.TrackerLocation.Alt != 0
        ? _comPort.MAV.cs.TrackerLocation.Alt
        : _comPort.MAV.cs.HomeAlt;
    double displayAlt = currentMeters * CurrentState.multiplieralt;
    string? input = await Services.Dialogs.InputBox(
        "Tracker Home",
        $"Tracker ASL altitude ({CurrentState.AltUnit})",
        displayAlt.ToString("0.##", CultureInfo.InvariantCulture));
    if (!double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double entered)) {
      if (input != null) {
        await Services.Dialogs.Alert("Tracker Home", "Invalid altitude.");
      }
      return;
    }
    _comPort.MAV.cs.TrackerLocation = new PointLatLngAlt(
        lat, lng, entered / CurrentState.multiplieralt);
    Status = "Tracker Home set from the map.";
  }

  private static double PolygonAreaM2(IReadOnlyList<PointLatLngAlt> pts) {
    double lat0 = pts.Average(p => p.Lat) * Math.PI / 180;
    const double mPerDeg = 111320.0;
    double sum = 0;
    for (int i = 0; i < pts.Count; i++) {
      var a = pts[i];
      var b = pts[(i + 1) % pts.Count];
      double ax = a.Lng * mPerDeg * Math.Cos(lat0), ay = a.Lat * mPerDeg;
      double bx = b.Lng * mPerDeg * Math.Cos(lat0), by = b.Lat * mPerDeg;
      sum += ax * by - bx * ay;
    }
    return Math.Abs(sum) / 2;
  }

  public void InsertWaypointAfterSeq(int afterSeq, double lat, double lng, bool? spline = null) {
    int idx = -1;
    for (int i = 0; i < Waypoints.Count; i++) {
      if (Waypoints[i].Seq == afterSeq) {
        idx = i;
        break;
      }
    }
    if (idx < 0) {
      AddWaypointAt(lat, lng);
      return;
    }
    using var undo = BeginUndoMutation();
    var cmd = (spline ?? SplineDefault)
        ? MAVLink.MAV_CMD.SPLINE_WAYPOINT
        : MAVLink.MAV_CMD.WAYPOINT;
    Waypoints.Insert(idx + 1, new WpRow {
      Command = (ushort)cmd,
      Frame = DefaultFrameId,
      Lat = lat,
      Lng = lng,
      Alt = VerifyPlaceAlt(lat, lng, DefaultAlt, DefaultFrameId),
    });
    Renumber();
    WaypointsChanged?.Invoke();
  }

  // ponytail: one grid + three backing stores for Mission/Fence/Rally, like upstream reusing one DataGridView per list; no per-type row class.
  public string[] MissionTypes { get; } = ["Mission", "Fence", "Rally"];

  [ObservableProperty]
  private string _missionType = "Mission";

  private readonly List<WpRow> _missionStore = [];
  private readonly List<WpRow> _fenceStore = [];
  private readonly List<WpRow> _rallyStore = [];

  private MAVLink.MAV_MISSION_TYPE CurrentMissionType => MissionType switch {
    "Fence" => MAVLink.MAV_MISSION_TYPE.FENCE,
    "Rally" => MAVLink.MAV_MISSION_TYPE.RALLY,
    _ => MAVLink.MAV_MISSION_TYPE.MISSION,
  };

  private List<WpRow> StoreFor(string? type) => type switch {
    "Fence" => _fenceStore,
    "Rally" => _rallyStore,
    _ => _missionStore,
  };

  partial void OnMissionTypeChanged(string? oldValue, string newValue) {
    if (_restoringUndo) {
      OnPropertyChanged(nameof(CanUndo));
      return;
    }
    var prev = StoreFor(oldValue);
    prev.Clear();
    prev.AddRange(Waypoints);
    Replace(StoreFor(newValue).ToList());
    WaypointsChanged?.Invoke();
    OnPropertyChanged(nameof(CanUndo));
  }

  public ObservableCollection<string> MapTypes { get; } =
      [
            "GoogleSatelliteMap",
            "GoogleHybridMap",
            "BingSatelliteMap",
            "OpenStreetMap",
            "EsriWorldImagery",
      ];

  [ObservableProperty]
  private string _mapType = "GoogleSatelliteMap";

  [ObservableProperty]
  private string _status = "Connect, then Read. Or Load File to preview a mission.";

  [ObservableProperty]
  private double _defaultAlt = 100;

  public double DefaultAltDisplay {
    get => ToDisplayAltitude(DefaultAlt);
    set => DefaultAlt = FromDisplayAltitude(value);
  }

  public string DefaultAltLabel => $"Default Alt ({CurrentState.AltUnit})";

  public IReadOnlyList<string> DefaultFrames { get; } = WpRow.FrameList;

  [ObservableProperty]
  private string _defaultFrame = "Relative";

  internal byte DefaultFrameId => FrameId(DefaultFrame);

  internal static byte FrameId(string? frame) => frame switch {
    "Absolute" => (byte)MAVLink.MAV_FRAME.GLOBAL,
    "Terrain" => (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
    _ => (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
  };

  internal static string FrameName(byte frame) => frame switch {
    (byte)MAVLink.MAV_FRAME.GLOBAL => "Absolute",
    (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT => "Terrain",
    _ => "Relative",
  };

  [ObservableProperty]
  private double _wpRadius = 90;

  [ObservableProperty]
  private double _loiterRadius = 100;

  [ObservableProperty]
  private double _altWarn;

  public double AltWarnDisplay {
    get => ToDisplayAltitude(AltWarn);
    set => AltWarn = FromDisplayAltitude(value);
  }

  public string AltWarnLabel => $"Alt Warn ({CurrentState.AltUnit})";

  public string WpRadiusLabel => $"WP Radius ({CurrentState.DistanceUnit})";

  public string LoiterRadiusLabel => $"Loiter Radius ({CurrentState.DistanceUnit})";

  [ObservableProperty]
  private bool _verifyHeight;

  partial void OnAltWarnChanged(double value) {
    OnPropertyChanged(nameof(AltWarnDisplay));
    try {
      Settings.Instance["fpminaltwarning"] = value.ToString(CultureInfo.InvariantCulture);
    } catch {

    }
  }

  partial void OnVerifyHeightChanged(bool value) {
    try {
      Settings.Instance["fpverifyheight"] = value.ToString();
    } catch {

    }
  }

  [ObservableProperty]
  private double _homeLat;

  [ObservableProperty]
  private double _homeLng;

  [ObservableProperty]
  private double _homeAlt;

  public double HomeAltDisplay {
    get => ToDisplayAltitude(HomeAlt);
    set => HomeAlt = FromDisplayAltitude(value);
  }

  public string HomeAltLabel => $"ASL ({CurrentState.AltUnit})";

  [ObservableProperty]
  private bool _showGrid;

  [ObservableProperty]
  private string _totalDist = "0";

  [ObservableProperty]
  private string _homeDist = "0";

  [ObservableProperty]
  private string _prevDist = "0";

  public bool IsConnected => _comPort.BaseStream?.IsOpen == true;

  [RelayCommand]
  [Obsolete]
  private async Task ReadWaypoints() {
    if (!IsConnected) {
      Status = "Not connected.";
      return;
    }
    var type = CurrentMissionType;
    Status = $"Reading {MissionType.ToLowerInvariant()}…";
    try {
      byte sysid = _comPort.MAV.sysid;
      byte compid = _comPort.MAV.compid;
      var rows = await DownloadRowsAsync(type, sysid, compid);
      if (type == MAVLink.MAV_MISSION_TYPE.MISSION && rows.Count > 0) {
        var home = rows[0];
        HomeLat = home.Lat;
        HomeLng = home.Lng;
        HomeAlt = home.Alt;
        rows.RemoveAt(0);
        for (int index = 0; index < rows.Count; index++) {
          rows[index].Seq = index;
        }
      }
      Replace(rows);
      Status = $"Read {rows.Count} {MissionType.ToLowerInvariant()} point(s).";
    } catch (Exception ex) {
      Status = "Read failed: " + ex.Message;
    }
  }

  internal async Task ReadMissionOnConnectAsync() {
    if (MissionType != "Mission") {
      MissionType = "Mission";
    }
#pragma warning disable CS0612 // ReadWaypoints wraps legacy upstream mission APIs.
    await ReadWaypoints();
#pragma warning restore CS0612
  }

  internal async Task ReadAncillaryOnConnectAsync() {
    if (!IsConnected) {
      return;
    }

    byte sysid = _comPort.MAV.sysid;
    byte compid = _comPort.MAV.compid;
    await WaitForBackgroundParametersAsync(sysid, compid);
    if (!IsSameOpenVehicle(sysid, compid)) {
      return;
    }

    int fenceCount = 0;
    int rallyCount = 0;
    var errors = new List<string>();
    var fenceRows = new List<WpRow>();
    var rallyRows = new List<WpRow>();

    if (ShouldDownloadFence()) {
      try {
        fenceRows = await DownloadRowsAsync(MAVLink.MAV_MISSION_TYPE.FENCE, sysid, compid);
        fenceCount = fenceRows.Count;
      } catch (Exception ex) {
        errors.Add("fence: " + ex.Message);
      }
    }

    if (IsSameOpenVehicle(sysid, compid) && ParamValue("RALLY_TOTAL") > 0) {
      try {
        rallyRows = await DownloadRowsAsync(MAVLink.MAV_MISSION_TYPE.RALLY, sysid, compid);
        rallyCount = rallyRows.Count;
        await WarnIfRallySpreadExceedsLimit(rallyRows);
      } catch (Exception ex) {
        errors.Add("rally: " + ex.Message);
      }
    }

    if (!IsSameOpenVehicle(sysid, compid)) {
      return;
    }
    bool storesChanged = _fenceStore.Count > 0 || _rallyStore.Count > 0 ||
        fenceRows.Count > 0 || rallyRows.Count > 0;
    ReplaceStore(_fenceStore, fenceRows);
    ReplaceStore(_rallyStore, rallyRows);
    if (storesChanged) {
      WaypointsChanged?.Invoke();
    }
    if (fenceCount > 0 || rallyCount > 0) {
      Status = $"Loaded vehicle extras: {fenceCount} fence, {rallyCount} rally point(s).";
    }
    if (errors.Count > 0) {
      Status = "Connected, but ancillary mission read failed (" + string.Join("; ", errors) + ").";
    }
  }

  private Task<List<WpRow>> DownloadRowsAsync(
      MAVLink.MAV_MISSION_TYPE type, byte sysid, byte compid) => Task.Run(async () => {
        var list = new List<WpRow>();
        if (type == MAVLink.MAV_MISSION_TYPE.MISSION) {
          ushort count = _comPort.getWPCount(sysid, compid, type);
          for (ushort i = 0; i < count; i++) {
            list.Add(WpRow.From(i, _comPort.getWP(sysid, compid, i, type)));
          }
        } else if (type == MAVLink.MAV_MISSION_TYPE.FENCE && !SupportsMissionFence(
                     _comPort.MAV.cs.capabilities)) {
          list.AddRange(await DownloadLegacyFenceAsync());
        } else if (type == MAVLink.MAV_MISSION_TYPE.RALLY && !_comPort.MAV.mavlinkv2) {
          list.AddRange(await DownloadLegacyRallyAsync());
        } else {
          var locations = await mav_mission.download(_comPort, sysid, compid, type);
          for (int i = 0; i < locations.Count; i++) {
            list.Add(WpRow.From(i, locations[i]));
          }
        }
        return list;
      });

  private bool ShouldDownloadFence() =>
      SupportsMissionFence(_comPort.MAV.cs.capabilities)
      || (ParamValue("FENCE_TOTAL") > 1
          && (_comPort.MAV.param.ContainsKey("FENCE_ACTION")
              || _comPort.MAV.param.ContainsKey("FENCE_ENABLE")));

  internal static bool SupportsMissionFence(uint capabilities) =>
      (capabilities & (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.MISSION_FENCE) != 0;

  [Obsolete]
  private async Task<List<WpRow>> DownloadLegacyFenceAsync() {
    var result = new List<WpRow>();
    var first = await _comPort.getFencePoint(0);
    int total = first.total;
    if (total <= 0) {
      return result;
    }
    result.Add(NewFileRow(MAVLink.MAV_CMD.FENCE_RETURN_POINT,
        first.plla.Lat, first.plla.Lng, 0, frame: (byte)MAVLink.MAV_FRAME.GLOBAL));

    var vertices = new List<PointLatLngAlt>();
    for (int index = 1; index < total; index++) {
      var point = await _comPort.getFencePoint(index);
      vertices.Add(point.plla);
      total = point.total;
    }
    if (vertices.Count > 1 && vertices[0].GetDistance(vertices[^1]) < 0.1) {
      vertices.RemoveAt(vertices.Count - 1);
    }
    foreach (var point in vertices) {
      result.Add(NewFileRow(MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION,
          point.Lat, point.Lng, 0, p1: vertices.Count));
    }
    return result;
  }

  [Obsolete]
  private async Task<List<WpRow>> DownloadLegacyRallyAsync() {
    int total = Math.Clamp((int)Math.Round(ParamValue("RALLY_TOTAL")), 0, byte.MaxValue);
    var result = new List<WpRow>(total);
    for (int index = 0; index < total; index++) {
      var point = await _comPort.getRallyPoint(index);
      result.Add(NewFileRow(MAVLink.MAV_CMD.RALLY_POINT,
          point.plla.Lat, point.plla.Lng, point.plla.Alt));
      total = Math.Clamp(point.total, 0, byte.MaxValue);
    }
    return result;
  }

  private bool IsSameOpenVehicle(byte sysid, byte compid) =>
      IsConnected && _comPort.MAV.sysid == sysid && _comPort.MAV.compid == compid;

  private async Task WaitForBackgroundParametersAsync(byte sysid, byte compid) {
    if (!MissionPlanner.Utilities.Settings.Instance.GetBoolean("Params_BG", false)) {
      return;
    }

    // Upstream performs the fence/rally checks only after its parameter read. The Avalonia port
    // may deliberately load parameters in the background, so defer the checks until that read is
    // complete instead of silently missing extras whose *_TOTAL parameters have not arrived yet.
    DateTime deadline = DateTime.UtcNow.AddSeconds(60);
    while (IsSameOpenVehicle(sysid, compid) && DateTime.UtcNow < deadline) {
      var parameters = _comPort.MAV.param;
      if (parameters.TotalReported > 0 && parameters.TotalReceived >= parameters.TotalReported) {
        return;
      }
      await Task.Delay(250);
    }
  }

  private double ParamValue(string name) {
    try {
      return _comPort.MAV.param.ContainsKey(name) ? _comPort.MAV.param[name].Value : 0;
    } catch {
      return 0;
    }
  }

  private async Task WarnIfRallySpreadExceedsLimit(IReadOnlyList<WpRow> rows) {
    double limitKm = ParamValue("RALLY_LIMIT_KM");
    if (limitKm <= 0 || rows.Count < 2) {
      return;
    }
    double maximumMeters = 0;
    foreach (var first in rows) {
      foreach (var second in rows) {
        maximumMeters = Math.Max(maximumMeters,
            new PointLatLngAlt(first.Lat, first.Lng).GetDistance(
                new PointLatLngAlt(second.Lat, second.Lng)));
      }
    }
    if (maximumMeters / 1000.0 > limitKm) {
      await Services.Dialogs.Alert("Rally point warning",
          $"Maximum rally-point separation is {maximumMeters / 1000.0:0.00} km, " +
          $"above RALLY_LIMIT_KM ({limitKm:0.00} km).");
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task WriteWaypoints() {
    if (!IsConnected) {
      Status = "Not connected — cannot write.";
      return;
    }
    var rows = Waypoints.ToList();
    var type = CurrentMissionType;

    if (rows.Count == 0 && type == MAVLink.MAV_MISSION_TYPE.MISSION) {
      Status = "No waypoints to write.";
      return;
    }
    if (type == MAVLink.MAV_MISSION_TYPE.MISSION && !await ConfirmMissionSafetyAsync(rows)) {
      return;
    }
    Status = $"Writing {rows.Count} {MissionType.ToLowerInvariant()} point(s)…";
    try {
      await Task.Run(async () => {
        if (type == MAVLink.MAV_MISSION_TYPE.MISSION) {
          WriteRadiusParams();
          _comPort.setWPTotal((ushort)(rows.Count + 1), type);
          var home = new WpRow {
            Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
            Frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
            Lat = HomeLat,
            Lng = HomeLng,
            Alt = HomeAlt,
          };
          _comPort.setWP(home.ToLocationwp(), 0, MAVLink.MAV_FRAME.GLOBAL, 1);
          for (int i = 0; i < rows.Count; i++) {
            _comPort.setWP(rows[i].ToLocationwp(), (ushort)(i + 1),
                (MAVLink.MAV_FRAME)rows[i].Frame, 0);
          }
          _comPort.setWPACK(type);
        } else if (type == MAVLink.MAV_MISSION_TYPE.FENCE && !SupportsMissionFence(
                     _comPort.MAV.cs.capabilities)) {
          UploadLegacyFence(rows);
        } else if (type == MAVLink.MAV_MISSION_TYPE.RALLY && !_comPort.MAV.mavlinkv2) {
          UploadLegacyRally(rows);
        } else {
          await mav_mission.upload(_comPort, _comPort.MAV.sysid, _comPort.MAV.compid, type,
              [.. rows.Select(r => r.ToLocationwp())]);
        }
      });
      Status = $"Wrote {rows.Count} {MissionType.ToLowerInvariant()} point(s).";
    } catch (Exception ex) {
      Status = "Write failed: " + ex.Message;
    }
  }

  internal static IReadOnlyList<PointLatLngAlt> BuildLegacyFenceTransfer(
      IReadOnlyList<WpRow> rows) {
    var returnPoints = rows.Where(row =>
        row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT).ToList();
    var vertices = rows.Where(row =>
        row.Command == (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION).ToList();
    bool unsupported = rows.Any(row => row.Command is not
        ((ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT) and not
        ((ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION));
    if (returnPoints.Count != 1) {
      throw new InvalidOperationException("Legacy fence upload needs exactly one return location.");
    }
    if (vertices.Count < 3) {
      throw new InvalidOperationException("Legacy fence upload needs an inclusion polygon with at least 3 vertices.");
    }
    if (unsupported) {
      throw new InvalidOperationException(
          "This controller's legacy fence protocol cannot upload exclusion polygons or circles.");
    }
    if (vertices.Count + 2 > byte.MaxValue) {
      throw new InvalidOperationException("The legacy fence protocol supports at most 253 vertices.");
    }

    var transfer = new List<PointLatLngAlt>(vertices.Count + 2) {
      new(returnPoints[0].Lat, returnPoints[0].Lng),
    };
    transfer.AddRange(vertices.Select(row => new PointLatLngAlt(row.Lat, row.Lng)));
    transfer.Add(new PointLatLngAlt(vertices[0].Lat, vertices[0].Lng));
    return transfer;
  }

  [Obsolete]
  private void UploadLegacyFence(IReadOnlyList<WpRow> rows) {
    var transfer = BuildLegacyFenceTransfer(rows);
    byte count = checked((byte)transfer.Count);
    for (byte index = 0; index < count; index++) {
      if (!_comPort.setFencePoint(index, transfer[index], count)) {
        throw new IOException($"The controller did not verify legacy fence point {index}.");
      }
    }
  }

  [Obsolete]
  private void UploadLegacyRally(IReadOnlyList<WpRow> rows) {
    if (rows.Count > byte.MaxValue) {
      throw new InvalidOperationException("The legacy rally protocol supports at most 255 points.");
    }
    if (rows.Any(row => row.Command != (ushort)MAVLink.MAV_CMD.RALLY_POINT)) {
      throw new InvalidOperationException("Legacy rally upload accepts only RALLY_POINT items.");
    }
    if (!_comPort.setParam("RALLY_TOTAL", rows.Count)) {
      throw new IOException("The controller rejected RALLY_TOTAL.");
    }
    byte count = checked((byte)rows.Count);
    for (byte index = 0; index < count; index++) {
      var row = rows[index];
      var point = new PointLatLngAlt(row.Lat, row.Lng, row.Alt);
      short breakAltitude = (short)Math.Clamp(Math.Round(row.P1), short.MinValue, short.MaxValue);
      ushort landingDirection = (ushort)Math.Clamp(Math.Round(row.P2), ushort.MinValue, ushort.MaxValue);
      byte flags = (byte)Math.Clamp(Math.Round(row.P3), byte.MinValue, byte.MaxValue);
      if (!_comPort.setRallyPoint(index, point, breakAltitude, landingDirection, flags, count)) {
        throw new IOException($"The controller did not verify legacy rally point {index}.");
      }
    }
  }

  private async Task<bool> ConfirmMissionSafetyAsync(List<WpRow> rows) {
    if (ContainsAbsoluteAltitude(rows) && !await Services.Dialogs.Confirm(
          "Absolute altitude",
          "This mission contains absolute (AMSL) altitudes. Write it to the vehicle?")) {
      Status = "Write aborted: absolute altitude was not confirmed.";
      return false;
    }
    return await ConfirmAltitudesAsync(rows);
  }

  internal static bool ContainsAbsoluteAltitude(IEnumerable<WpRow> rows) =>
      rows.Any(row => row.Frame == (byte)MAVLink.MAV_FRAME.GLOBAL);

  private async Task<bool> ConfirmAltitudesAsync(List<WpRow> rows) {
    for (int a = 0; a < rows.Count; a++) {
      var cmd = (MAVLink.MAV_CMD)rows[a].Command;
      if (rows[a].Command < (ushort)MAVLink.MAV_CMD.LAST
          && cmd != MAVLink.MAV_CMD.TAKEOFF && cmd != MAVLink.MAV_CMD.LAND
          && cmd != MAVLink.MAV_CMD.RETURN_TO_LAUNCH
          && rows[a].Alt < AltWarn) {
        await Services.Dialogs.Alert("Low alt",
            "Low alt on WP#" + (a + 1)
            + "\nPlease reduce the alt warning, or increase the altitude");
        Status = "Write aborted: low alt on WP#" + (a + 1);
        return false;
      }
    }
    return true;
  }

  [RelayCommand]
  [Obsolete]
  private async Task WriteWaypointsFast() {
    if (!IsConnected) {
      Status = "Not connected — cannot write.";
      return;
    }
    if (CurrentMissionType != MAVLink.MAV_MISSION_TYPE.MISSION) {
      // The pipelined path only covers the mission store; fence/rally use the normal upload.
      await WriteWaypoints();
      return;
    }
    var rows = Waypoints.ToList();
    if (rows.Count == 0) {
      Status = "No waypoints to write.";
      return;
    }
    if (!await ConfirmMissionSafetyAsync(rows)) {
      return;
    }
    Status = $"Writing {rows.Count} waypoint(s) (fast)…";
    try {
      await Task.Run(() => SaveWpsFast(rows));
      Status = $"Wrote {rows.Count} waypoint(s) (fast).";
    } catch (Exception ex) {
      Status = "Write failed: " + ex.Message;
    }
  }

  // Port of upstream FlightPlanner.saveWPsFast: stream MISSION_ITEM_INT packets in batches of
  // ten, resynchronizing on the autopilot's MISSION_REQUEST/MISSION_ACK feedback, then wait for
  // the final MISSION_ACK that confirms the whole mission.
  [Obsolete]
  private void SaveWpsFast(List<WpRow> rows) {
    var total = (ushort)(rows.Count + 1);
    int reqno = 0;
    int latestResult = (int)MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED;
    // Preserve every ACK, not only its count: two ACKs can arrive before the upload thread runs,
    // and the later value must not overwrite an earlier ACCEPTED completion.
    var ackResults = new ConcurrentQueue<MAVLink.MAV_MISSION_RESULT>();
    byte sysid = (byte)_comPort.sysidcurrent;
    byte compid = (byte)_comPort.compidcurrent;

    bool ForOtherGcs(byte targetSystem, byte targetComponent) =>
        targetSystem != MAVLinkInterface.gcssysid ||
        targetComponent != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER;

    var sub1 = _comPort.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.MISSION_ACK, message => {
      var data = (MAVLink.mavlink_mission_ack_t)message.data;
      if (!ForOtherGcs(data.target_system, data.target_component)) {
        var ack = (MAVLink.MAV_MISSION_RESULT)data.type;
        System.Threading.Volatile.Write(ref latestResult, (int)ack);
        ackResults.Enqueue(ack);
      }
      return true;
    }, sysid, compid);
    var sub2 = _comPort.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST, message => {
      var data = (MAVLink.mavlink_mission_request_t)message.data;
      if (!ForOtherGcs(data.target_system, data.target_component)) {
        System.Threading.Volatile.Write(ref reqno, data.seq);
      }
      return true;
    }, sysid, compid);
    var sub3 = _comPort.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT,
        message => {
          var data = (MAVLink.mavlink_mission_request_int_t)message.data;
          if (!ForOtherGcs(data.target_system, data.target_component)) {
            System.Threading.Volatile.Write(ref reqno, data.seq);
          }
          return true;
        }, sysid, compid);

    try {
      WriteRadiusParams();
      _comPort.setWPTotal(total);

      var home = new WpRow {
        Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        Frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
        Lat = HomeLat,
        Lng = HomeLng,
        Alt = HomeAlt,
      };
      var commandlist = new List<Locationwp> { home.ToLocationwp() };
      var frames = new List<MAVLink.MAV_FRAME> { MAVLink.MAV_FRAME.GLOBAL };
      foreach (var r in rows) {
        commandlist.Add(r.ToLocationwp());
        frames.Add((MAVLink.MAV_FRAME)r.Frame);
      }

      void SendItem(int a) {
        var loc = commandlist[a];
        var req = new MAVLink.mavlink_mission_item_int_t {
          target_system = _comPort.MAV.sysid,
          target_component = _comPort.MAV.compid,
          command = loc.id,
          current = 0,
          autocontinue = 1,
          frame = (byte)frames[a],
          z = (float)loc.alt,
          param1 = loc.p1,
          param2 = loc.p2,
          param3 = loc.p3,
          param4 = loc.p4,
          seq = (ushort)a,
        };
        if (loc.id == (ushort)MAVLink.MAV_CMD.DO_DIGICAM_CONTROL ||
            loc.id == (ushort)MAVLink.MAV_CMD.DO_DIGICAM_CONFIGURE) {
          req.x = (int)loc.lat;
          req.y = (int)loc.lng;
        } else {
          req.x = (int)(loc.lat * 1.0e7);
          req.y = (int)(loc.lng * 1.0e7);
        }

        _comPort.sendPacket(req, _comPort.MAV.sysid, _comPort.MAV.compid);
      }

      void ThrowOnTerminalResult(int a, MAVLink.MAV_MISSION_RESULT result) {
        if (result == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_NO_SPACE) {
          throw new Exception("Upload failed, please reduce the number of wp's");
        }
        if (result == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_INVALID) {
          throw new Exception($"Upload failed, the MAV rejected item wp# {a} ({result})");
        }
        if (result != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED &&
            result != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ERROR &&
            result != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_INVALID_SEQUENCE) {
          throw new Exception($"Upload wps failed at " +
                              $"{System.Threading.Volatile.Read(ref reqno)} ({result})");
        }
      }

      for (int a = 0; a < commandlist.Count; a++) {
        if (a % 10 == 0 && a != 0) {
          var start = DateTime.Now;
          while (true) {
            int requested = System.Threading.Volatile.Read(ref reqno);
            if (requested == a) {
              break;
            }
            if (start.AddSeconds(1.1) < DateTime.Now) {
              a = requested;
              break;
            }
            var result = (MAVLink.MAV_MISSION_RESULT)
                System.Threading.Volatile.Read(ref latestResult);
            if (result == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_INVALID_SEQUENCE) {
              System.Threading.Thread.Sleep(500);
            }
            if (result == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ERROR) {
              requested = System.Threading.Volatile.Read(ref reqno);
              _comPort.setWPPartialUpdate((ushort)requested, total);
              a = requested;
              break;
            }
            ThrowOnTerminalResult(a, result);
            System.Threading.Thread.Sleep(10);
          }
        }

        SendItem(a);
      }

      // The autopilot confirms the whole mission with a final MISSION_ACK; keep answering any
      // MISSION_REQUEST for items lost in flight (a short mission never enters the batch
      // resynchronization above, so this is the only completion check it gets).
      int lastSeq = commandlist.Count - 1;
      var deadline = DateTime.UtcNow.AddSeconds(5);
      var lastResend = DateTime.MinValue;
      while (true) {
        bool accepted = false;
        while (ackResults.TryDequeue(out var r)) {
          if (r == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED) {
            accepted = true;
            break;
          }
          if (r != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_INVALID_SEQUENCE &&
              r != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ERROR) {
            ThrowOnTerminalResult(lastSeq, r);
            throw new Exception($"Mission upload rejected ({r})");
          }
          // Transient: the autopilot re-requests the item it needs; keep serving it.
        }
        if (accepted) {
          break;
        }
        if (DateTime.UtcNow > deadline) {
          throw new Exception("No MISSION_ACK received after upload.");
        }
        if ((DateTime.UtcNow - lastResend).TotalMilliseconds > 250) {
          SendItem(Math.Clamp(System.Threading.Volatile.Read(ref reqno), 0, lastSeq));
          lastResend = DateTime.UtcNow;
        }
        System.Threading.Thread.Sleep(10);
      }

      _comPort.setWPACK();
    } finally {
      _comPort.UnSubscribeToPacketType(sub1);
      _comPort.UnSubscribeToPacketType(sub2);
      _comPort.UnSubscribeToPacketType(sub3);
    }
  }

  public string GenerateMissionKmlAndOpen() {
    var pts = Waypoints.Where(w => w.Lat != 0 || w.Lng != 0).ToList();
    if (pts.Count == 0) {
      return "No waypoints to export.";
    }
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>Mission</name>");
    sb.AppendLine("<Placemark><name>Route</name><LineString><tessellate>1</tessellate><coordinates>");
    foreach (var w in pts) {
      sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", w.Lng, w.Lat, w.Alt));
    }
    sb.AppendLine("</coordinates></LineString></Placemark></Document></kml>");
    var path = Path.Combine(Path.GetTempPath(), "mission.kml");
    File.WriteAllText(path, sb.ToString());
    Services.Dialogs.OpenUrl(path);
    return "Opened mission KML.";
  }

  [Obsolete]
  private void WriteRadiusParams() {
    var param = _comPort.MAV.param;
    void Set(string name, double value) {
      if (param.ContainsKey(name)) {
        _comPort.setParam(name, (float)(value / CurrentState.multiplierdist));
      }
    }
    Set("WP_RADIUS", WpRadius);
    Set("WP_RADIUS_M", WpRadius);
    Set("WP_LOITER_RAD", LoiterRadius);
    Set("LOITER_RAD", LoiterRadius);
  }

  public async Task SaveFileAsync(string path) {
    try {
      string extension = Path.GetExtension(path).ToLowerInvariant();
      switch (extension) {
        case ".mission":
        case ".plan":
          bool omittedFenceReturn = SaveJsonMission(path);
          int savedCount = RowsForFile("Mission").Count;
          if (extension == ".plan") {
            savedCount += RowsForFile("Fence").Count + RowsForFile("Rally").Count;
          }
          Status = omittedFenceReturn
              ? $"Saved {Path.GetFileName(path)}. Note: QGC Plan has no fence-return field; use .fen to preserve it."
              : $"Saved {savedCount} item(s) to {Path.GetFileName(path)}.";
          return;
        case ".poly":
          await SavePolygonAsync(path);
          return;
        case ".fen":
          await SaveLegacyFenceAsync(path);
          return;
        case ".ral":
          await SaveLegacyRallyAsync(path);
          return;
      }

      var lines = new List<string> { "QGC WPL 110" };
      int sequenceOffset = 0;
      if (MissionType == "Mission") {
        lines.Add(ToWplLine(new WpRow {
          Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
          Frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
          Lat = HomeLat,
          Lng = HomeLng,
          Alt = HomeAlt,
        }, 0, true));
        sequenceOffset = 1;
      }
      for (int i = 0; i < Waypoints.Count; i++) {
        lines.Add(ToWplLine(Waypoints[i], i + sequenceOffset, false));
      }
      await File.WriteAllLinesAsync(path, lines);
      Status = $"Saved {Waypoints.Count} waypoint(s) to {Path.GetFileName(path)}.";
    } catch (Exception ex) {
      Status = "Save failed: " + ex.Message;
    }
  }

  public async Task LoadFileAsync(string path, bool append = false) {
    try {
      string extension = Path.GetExtension(path).ToLowerInvariant();
      switch (extension) {
        case ".mission":
        case ".plan":
          LoadJsonMission(path, append);
          return;
        case ".poly":
          await LoadPolygonAsync(path, append);
          return;
        case ".fen":
          await LoadLegacyFenceAsync(path, append);
          return;
        case ".ral":
          await LoadLegacyRallyAsync(path, append);
          return;
        case ".kml":
        case ".kmz":
          LoadKmlMission(path, append);
          return;
      }

      var lines = await File.ReadAllLinesAsync(path);
      if (lines.Length == 0 || !lines[0].StartsWith("QGC WPL", StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidDataException("Unsupported mission file header.");
      }
      var rows = new List<WpRow>();
      foreach (var line in lines.Skip(1)) {
        var t = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 12) {
          continue;
        }

        rows.Add(
            new WpRow {
              Seq = int.Parse(t[0], CultureInfo.InvariantCulture),
              Command = (ushort)int.Parse(t[3], CultureInfo.InvariantCulture),
              Frame = byte.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte frame)
                  ? frame
                  : (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
              P1 = D(t[4]),
              P2 = D(t[5]),
              P3 = D(t[6]),
              P4 = D(t[7]),
              Lat = D(t[8]),
              Lng = D(t[9]),
              Alt = D(t[10]),
            }
        );
      }
      if (MissionType != "Mission") {
        MissionType = "Mission";
      }
      if (rows.FirstOrDefault() is { Seq: 0, Command: (ushort)MAVLink.MAV_CMD.WAYPOINT } home) {
        rows.RemoveAt(0);
        if (!append) {
          HomeLat = home.Lat;
          HomeLng = home.Lng;
          HomeAlt = home.Alt;
        }
      }
      ReplaceOrAppend(rows, append);
      Status = $"{(append ? "Appended" : "Loaded")} {rows.Count} waypoint(s) from {Path.GetFileName(path)}.";
    } catch (Exception ex) {
      Status = "Load failed: " + ex.Message;
    }
  }

  private void LoadKmlMission(string path, bool append) {
    Services.KmlMissionContent content = Services.KmlMissionReader.Read(path);
    var rows = content.Route.Select((point, index) => new WpRow {
      Seq = index,
      Command = (ushort)(SplineDefault
          ? MAVLink.MAV_CMD.SPLINE_WAYPOINT
          : MAVLink.MAV_CMD.WAYPOINT),
      Frame = DefaultFrameId,
      Lat = point.Lat,
      Lng = point.Lng,
      Alt = point.Alt,
    }).ToList();
    if (rows.Count > 0) {
      if (MissionType != "Mission") {
        MissionType = "Mission";
      }
      ReplaceOrAppend(rows, append);
    }
    if (content.Pois.Count > 0) {
      Services.PoiStore.AddRange(content.Pois);
      PoiChanged?.Invoke();
    }
    Status = $"{(append ? "Appended" : "Loaded")} {rows.Count} KML waypoint(s) and " +
        $"{content.Pois.Count} POI(s) from {Path.GetFileName(path)}.";
  }

  private static string ToWplLine(WpRow row, int sequence, bool current) =>
      string.Join("\t", new[] {
        sequence.ToString(CultureInfo.InvariantCulture),
        current ? "1" : "0",
        row.Frame.ToString(CultureInfo.InvariantCulture),
        row.Command.ToString(CultureInfo.InvariantCulture),
        F(row.P1), F(row.P2), F(row.P3), F(row.P4),
        F(row.Lat), F(row.Lng), F(row.Alt), "1",
      });

  private bool SaveJsonMission(string path) {
    var missionRows = RowsForFile("Mission");
    var format = new MissionFile.RootObject {
      fileType = "Plan",
      groundStation = "MissionPlanner",
      version = 1,
      mission = new MissionFile.Mission {
        cruiseSpeed = 15,
        firmwareType = (int)_comPort.MAV.apname,
        hoverSpeed = 5,
        items = [.. missionRows.Select(ToPlanItem)],
        plannedHomePosition = [HomeLat, HomeLng, HomeAlt],
        vehicleType = (int)_comPort.MAV.aptype,
        version = 2,
      },
    };

    bool isPlan = Path.GetExtension(path).Equals(".plan", StringComparison.OrdinalIgnoreCase);
    var fenceRows = RowsForFile("Fence");
    if (isPlan) {
      format.geoFence = BuildPlanGeoFence(fenceRows);
      format.rallyPoints = new MissionFile.RallyPoints {
        points = [.. RowsForFile("Rally")
            .Where(row => row.Command == (ushort)MAVLink.MAV_CMD.RALLY_POINT)
            .Select(row => new List<double> { row.Lat, row.Lng, row.Alt })],
        version = 2,
      };
    }

    MissionFile.WriteFile(path, format);
    return isPlan && fenceRows.Any(row =>
        row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT);
  }

  private void LoadJsonMission(string path, bool append) {
    var format = MissionFile.ReadFile(path)
                 ?? throw new InvalidDataException("The JSON mission is empty.");
    if (format.mission == null) {
      throw new InvalidDataException("The JSON file has no mission section.");
    }

    if (!append && format.mission.plannedHomePosition is { Count: >= 3 } home) {
      HomeLat = home[0];
      HomeLng = home[1];
      HomeAlt = home[2];
    }

    var missionRows = ReadPlanMissionItems(format.mission.items);
    bool hasPlanSections = Path.GetExtension(path).Equals(".plan", StringComparison.OrdinalIgnoreCase)
                           || format.geoFence != null
                           || format.rallyPoints != null;
    if (!hasPlanSections) {
      if (MissionType != "Mission") {
        MissionType = "Mission";
      }
      ReplaceOrAppend(missionRows, append);
      Status = $"{(append ? "Appended" : "Loaded")} {missionRows.Count} waypoint(s) from {Path.GetFileName(path)}.";
      return;
    }

    var fenceRows = ReadPlanGeoFence(format.geoFence);
    var rallyRows = ReadPlanRally(format.rallyPoints);
    MergePlanStores(missionRows, fenceRows, rallyRows, append);
    Status = $"{(append ? "Appended" : "Loaded")} QGC Plan: {missionRows.Count} mission, "
             + $"{fenceRows.Count} fence and {rallyRows.Count} rally item(s).";
  }

  private IReadOnlyList<WpRow> RowsForFile(string type) =>
      MissionType == type ? Waypoints.ToList() : [.. StoreFor(type)];

  private static MissionFile.Item ToPlanItem(WpRow row) => new() {
    autoContinue = true,
    command = row.Command,
    doJumpId = row.Seq + 1,
    frame = row.Frame,
    @params = [row.P1, row.P2, row.P3, row.P4, row.Lat, row.Lng, row.Alt],
    type = "SimpleItem",
  };

  private static MissionFile.GeoFence BuildPlanGeoFence(IReadOnlyList<WpRow> rows) {
    var result = new MissionFile.GeoFence {
      circles = [],
      polygons = [],
      version = 2,
    };
    for (int index = 0; index < rows.Count;) {
      var row = rows[index];
      bool circleInclusion = row.Command == (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION;
      bool circleExclusion = row.Command == (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION;
      if (circleInclusion || circleExclusion) {
        result.circles.Add(new MissionFile.Circle {
          circle = new MissionFile.Circle2 {
            center = [row.Lat, row.Lng],
            radius = row.P1,
          },
          inclusion = circleInclusion,
          version = 1,
        });
        index++;
        continue;
      }

      bool polygonInclusion = row.Command == (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
      bool polygonExclusion = row.Command == (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
      if (!polygonInclusion && !polygonExclusion) {
        index++;
        continue;
      }

      ushort command = row.Command;
      int declaredCount = (int)Math.Round(row.P1);
      int availableCount = rows.Skip(index).TakeWhile(item => item.Command == command).Count();
      int count = declaredCount >= 3 ? Math.Min(declaredCount, availableCount) : availableCount;
      var points = rows.Skip(index).Take(count)
          .Select(item => new List<double> { item.Lat, item.Lng }).ToList();
      if (points.Count >= 3) {
        result.polygons.Add(new MissionFile.Polygon {
          inclusion = polygonInclusion,
          polygon = points,
          version = 1,
        });
      }
      index += Math.Max(count, 1);
    }
    return result;
  }

  private static List<WpRow> ReadPlanMissionItems(IEnumerable<MissionFile.Item>? items) {
    var result = new List<WpRow>();
    foreach (var item in items ?? []) {
      if (item.type == "ComplexItem") {
        foreach (var child in item.TransectStyleComplexItem?.Items
                     ?? Enumerable.Empty<MissionFile.Item>()) {
          result.Add(FromPlanItem(child, result.Count));
        }
      } else {
        // Mission Planner's older exporter omitted type; accept it as a SimpleItem.
        result.Add(FromPlanItem(item, result.Count));
      }
    }
    return result;
  }

  private static WpRow FromPlanItem(MissionFile.Item item, int sequence) {
    var values = item.@params ?? [];
    double Param(int index) => values.Count > index ? values[index] ?? 0 : 0;
    return new WpRow {
      Seq = sequence,
      Command = (ushort)item.command,
      Frame = (byte)item.frame,
      P1 = Param(0),
      P2 = Param(1),
      P3 = Param(2),
      P4 = Param(3),
      Lat = Param(4),
      Lng = Param(5),
      Alt = Param(6),
    };
  }

  private static List<WpRow> ReadPlanGeoFence(MissionFile.GeoFence? fence) {
    var result = new List<WpRow>();
    foreach (var polygon in fence?.polygons ?? Enumerable.Empty<MissionFile.Polygon>()) {
      var points = polygon.polygon ?? [];
      var validPoints = points.Where(point => point.Count >= 2).ToList();
      var command = polygon.inclusion
          ? MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION
          : MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
      foreach (var point in validPoints) {
        result.Add(NewFileRow(command, point[0], point[1], 0, p1: validPoints.Count));
      }
    }
    foreach (var circle in fence?.circles ?? Enumerable.Empty<MissionFile.Circle>()) {
      if (circle.circle?.center is not { Count: >= 2 } center) {
        continue;
      }
      result.Add(NewFileRow(
          circle.inclusion ? MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION : MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION,
          center[0], center[1], 0, p1: circle.circle.radius));
    }
    return result;
  }

  private static List<WpRow> ReadPlanRally(MissionFile.RallyPoints? rally) =>
      [.. (rally?.points ?? [])
          .Where(point => point.Count >= 2)
          .Select(point => NewFileRow(MAVLink.MAV_CMD.RALLY_POINT, point[0], point[1],
              point.Count >= 3 ? point[2] : 0,
              frame: (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT))];

  private void MergePlanStores(List<WpRow> mission, List<WpRow> fence, List<WpRow> rally,
      bool append) {
    var activeStore = StoreFor(MissionType);
    activeStore.Clear();
    activeStore.AddRange(Waypoints);

    static void Merge(List<WpRow> target, IEnumerable<WpRow> source, bool appendRows) {
      if (!appendRows) {
        target.Clear();
      }
      target.AddRange(source);
      for (int index = 0; index < target.Count; index++) {
        target[index].Seq = index;
      }
    }

    Merge(_missionStore, mission, append);
    Merge(_fenceStore, fence, append);
    Merge(_rallyStore, rally, append);
    Replace(StoreFor(MissionType).ToList());
    WaypointsChanged?.Invoke();
  }

  private async Task SavePolygonAsync(string path) {
    if (DrawnPolygon.Count < 3) {
      throw new InvalidOperationException("Draw at least 3 polygon points first.");
    }
    var lines = new List<string> { "# saved by MissionPlanner-Avalonia" };
    lines.AddRange(DrawnPolygon.Select(point => $"{F(point.Lat)} {F(point.Lng)}"));
    lines.Add($"{F(DrawnPolygon[0].Lat)} {F(DrawnPolygon[0].Lng)}");
    await File.WriteAllLinesAsync(path, lines);
    Status = $"Saved polygon with {DrawnPolygon.Count} vertices to {Path.GetFileName(path)}.";
  }

  private async Task LoadPolygonAsync(string path, bool append) {
    var points = ParseCoordinateLines(await File.ReadAllLinesAsync(path));
    RemoveClosingPoint(points);
    if (!append) {
      DrawnPolygon.Clear();
    }
    DrawnPolygon.AddRange(points.Select(point => new PointLatLngAlt(point.Lat, point.Lng, DefaultAlt)));
    DrawnPolygonChanged?.Invoke();
    Status = $"{(append ? "Appended" : "Loaded")} polygon with {points.Count} vertices from {Path.GetFileName(path)}.";
  }

  private async Task SaveLegacyFenceAsync(string path) {
    var fenceRows = RowsForFile("Fence");
    var returnPoint = fenceRows.FirstOrDefault(row =>
        row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT);
    var polygon = fenceRows.Where(row =>
        row.Command == (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION).ToList();
    bool hasUnsupported = fenceRows.Any(row => row.Command is
        (ushort)MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION or
        (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION or
        (ushort)MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION);
    if (returnPoint == null || polygon.Count < 3) {
      throw new InvalidOperationException("Legacy .fen needs a return point and an inclusion polygon.");
    }
    if (hasUnsupported) {
      throw new InvalidOperationException(
          "Legacy .fen cannot represent exclusion polygons or circles; use QGC .plan instead.");
    }
    var lines = new List<string> {
      "# saved by MissionPlanner-Avalonia",
      $"{F(returnPoint.Lat)} {F(returnPoint.Lng)}",
    };
    lines.AddRange(polygon.Select(point => $"{F(point.Lat)} {F(point.Lng)}"));
    lines.Add($"{F(polygon[0].Lat)} {F(polygon[0].Lng)}");
    await File.WriteAllLinesAsync(path, lines);
    Status = $"Saved legacy fence to {Path.GetFileName(path)}.";
  }

  private async Task LoadLegacyFenceAsync(string path, bool append) {
    var points = ParseCoordinateLines(await File.ReadAllLinesAsync(path));
    if (points.Count < 4) {
      throw new InvalidDataException("Legacy .fen needs a return point and at least 3 polygon vertices.");
    }
    var (Lat, Lng) = points[0];
    points.RemoveAt(0);
    RemoveClosingPoint(points);
    if (MissionType != "Fence") {
      MissionType = "Fence";
    }
    var rows = new List<WpRow>();
    if (!append || !Waypoints.Any(row =>
          row.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT)) {
      rows.Add(NewFileRow(MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat, Lng, 0));
    }
    rows.AddRange(points.Select(point =>
        NewFileRow(MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, point.Lat, point.Lng, 0,
            p1: points.Count)));
    ReplaceOrAppend(rows, append);
    Status = $"{(append ? "Appended" : "Loaded")} legacy fence from {Path.GetFileName(path)}.";
  }

  private async Task SaveLegacyRallyAsync(string path) {
    var rally = RowsForFile("Rally")
        .Where(row => row.Command == (ushort)MAVLink.MAV_CMD.RALLY_POINT).ToList();
    if (rally.Count == 0) {
      throw new InvalidOperationException("No rally points to save.");
    }
    var lines = new List<string> { "# saved by MissionPlanner-Avalonia" };
    lines.AddRange(rally.Select(point => string.Join("\t", new[] {
      "RALLY", F(point.Lat), F(point.Lng), F(point.Alt), F(point.P1), F(point.P2), F(point.P3),
    })));
    await File.WriteAllLinesAsync(path, lines);
    Status = $"Saved {rally.Count} rally point(s) to {Path.GetFileName(path)}.";
  }

  private async Task LoadLegacyRallyAsync(string path, bool append) {
    var rows = new List<WpRow>();
    foreach (string line in await File.ReadAllLinesAsync(path)) {
      string trimmed = line.Trim();
      if (trimmed.Length == 0 || trimmed.StartsWith('#')) {
        continue;
      }
      string[] fields = trimmed.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length < 4 || !fields[0].Equals("RALLY", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }
      rows.Add(NewFileRow(MAVLink.MAV_CMD.RALLY_POINT, D(fields[1]), D(fields[2]), D(fields[3]),
          p1: fields.Length > 4 ? D(fields[4]) : 0,
          p2: fields.Length > 5 ? D(fields[5]) : 0,
          p3: fields.Length > 6 ? D(fields[6]) : 0,
          frame: (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT));
    }
    if (MissionType != "Rally") {
      MissionType = "Rally";
    }
    ReplaceOrAppend(rows, append);
    Status = $"{(append ? "Appended" : "Loaded")} {rows.Count} rally point(s) from {Path.GetFileName(path)}.";
  }

  private static List<(double Lat, double Lng)> ParseCoordinateLines(IEnumerable<string> lines) {
    var points = new List<(double, double)>();
    foreach (string line in lines) {
      string trimmed = line.Trim();
      if (trimmed.Length == 0 || trimmed.StartsWith('#')) {
        continue;
      }
      string[] fields = trimmed.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length >= 2
          && double.TryParse(fields[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat)
          && double.TryParse(fields[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng)) {
        points.Add((lat, lng));
      }
    }
    return points;
  }

  private static void RemoveClosingPoint(List<(double Lat, double Lng)> points) {
    if (points.Count > 1
        && Math.Abs(points[0].Lat - points[^1].Lat) < 1e-9
        && Math.Abs(points[0].Lng - points[^1].Lng) < 1e-9) {
      points.RemoveAt(points.Count - 1);
    }
  }

  private static WpRow NewFileRow(MAVLink.MAV_CMD command, double lat, double lng, double alt,
      double p1 = 0, double p2 = 0, double p3 = 0, double p4 = 0,
      byte frame = (byte)MAVLink.MAV_FRAME.GLOBAL) =>
      new() {
        Command = (ushort)command,
        Frame = frame,
        Lat = lat,
        Lng = lng,
        Alt = alt,
        P1 = p1,
        P2 = p2,
        P3 = p3,
        P4 = p4,
      };

  private void ReplaceOrAppend(IEnumerable<WpRow> rows, bool append) {
    using var undo = BeginUndoMutation();
    if (!append) {
      Replace(rows);
      return;
    }
    foreach (var row in rows) {
      Waypoints.Add(row);
    }
    Renumber();
    WaypointsChanged?.Invoke();
  }

  [RelayCommand]
  private void AddWaypoint() => AddWaypointAt(HomeLat, HomeLng);

  public void AddWaypointAt(double lat, double lng) {
    using var undo = BeginUndoMutation();
    switch (MissionType) {
      case "Fence":
        AddCommandRow(MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, lat, lng, 0,
            frame: (byte)MAVLink.MAV_FRAME.GLOBAL);
        break;
      case "Rally":
        AddCommandRow(MAVLink.MAV_CMD.RALLY_POINT, lat, lng,
            VerifyPlaceAlt(lat, lng, DefaultAlt, DefaultFrameId), frame: DefaultFrameId);
        break;
      default:
        AddCommandRow(SplineDefault ? MAVLink.MAV_CMD.SPLINE_WAYPOINT : MAVLink.MAV_CMD.WAYPOINT,
            lat, lng,
            VerifyPlaceAlt(lat, lng, DefaultAlt, DefaultFrameId), frame: DefaultFrameId);
        break;
    }
  }

  [ObservableProperty]
  private bool _splineDefault;

  public void AddSplineWp(double lat, double lng) {
    if (SelectedWaypoint != null) {
      InsertWaypointAfterSeq(SelectedWaypoint.Seq, lat, lng, spline: true);
      return;
    }
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.SPLINE_WAYPOINT, lat, lng,
        VerifyPlaceAlt(lat, lng, DefaultAlt, DefaultFrameId), frame: DefaultFrameId);
  }

  public void InsertAtCurrentPosition() {
    var cs = _comPort.MAV?.cs;
    if (cs == null || (cs.lat == 0 && cs.lng == 0)) {
      Status = "No vehicle position.";
      return;
    }
    AddWaypointAt(cs.lat, cs.lng);
  }

  public async Task AddJumpStart() {
    string? input = await Services.Dialogs.InputBox(
        "Jump repeat", "Number of times to repeat", "5");
    if (!double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double repeat)) {
      return;
    }
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.DO_JUMP, 0, 0, 0, p1: 1, p2: repeat);
  }

  public async Task CreateWpCircle(double lat, double lng) {
    if (await Ask("Radius", $"Radius ({CurrentState.DistanceUnit})", "50") is not { } s1
        || !int.TryParse(s1, out var radius)) {
      return;
    }
    if (await Ask("Points", "Number of points to generate Circle", "20") is not { } s2
        || !int.TryParse(s2, out var points) || points <= 0) {
      return;
    }
    if (await Ask("Points", "Direction of circle (-1 or 1)", "1") is not { } s3
        || !int.TryParse(s3, out var direction) || direction is not (-1 or 1)) {
      return;
    }
    if (await Ask("angle", "Angle of first point (whole degrees)", "0") is not { } s4
        || !int.TryParse(s4, out var startangle)) {
      return;
    }
    double rad = radius / CurrentState.multiplierdist;
    double a = startangle;
    double step = 360.0 / points;
    if (direction == -1) {
      a += 360;
      step *= -1;
    }
    using var undo = BeginUndoMutation();
    var center = new PointLatLngAlt(lat, lng);
    int n = 0;
    for (; a <= startangle + 360 && a >= 0; a += step) {
      var p = center.newpos(a, rad);
      AddCommandRow(MAVLink.MAV_CMD.WAYPOINT, p.Lat, p.Lng,
          VerifyPlaceAlt(p.Lat, p.Lng, DefaultAlt, DefaultFrameId), frame: DefaultFrameId);
      n++;
    }
    Status = $"WP circle: {n} point(s).";
  }

  public async Task CreateSplineCircle(double lat, double lng) {
    if (await Ask("Radius", $"Radius ({CurrentState.DistanceUnit})", "50") is not { } s1
        || !int.TryParse(s1, out var radius)) {
      return;
    }
    if (await Ask("min alt", $"Min Alt ({CurrentState.AltUnit})", "5") is not { } s2
        || !int.TryParse(s2, out var minalt)) {
      return;
    }
    if (await Ask("max alt", $"Max Alt ({CurrentState.AltUnit})", "20") is not { } s3
        || !int.TryParse(s3, out var maxalt)) {
      return;
    }
    if (await Ask("alt step", $"Alt step ({CurrentState.AltUnit})", "5") is not { } s4
        || !int.TryParse(s4, out var altstep) || altstep <= 0 || maxalt < minalt) {
      return;
    }
    if (await Ask("angle", "Angle of first point (whole degrees)", "0") is not { } s5
        || !int.TryParse(s5, out var startangle)) {
      return;
    }
    int points = 4;
    double step = 360.0 / points;
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.DO_SET_ROI, lat, lng, 0, frame: DefaultFrameId);
    var center = new PointLatLngAlt(lat, lng);
    double radiusMeters = radius / CurrentState.multiplierdist;
    bool startup = true;
    for (double stepalt = minalt; stepalt <= maxalt;) {
      for (double a = startangle; a <= startangle + 360; a += step) {
        var p = center.newpos(a, radiusMeters);
        AddCommandRow(MAVLink.MAV_CMD.SPLINE_WAYPOINT, p.Lat, p.Lng,
            FromDisplayAltitude(stepalt),
            frame: DefaultFrameId);
        if (!startup) {
          stepalt += altstep / (double)points;
        }
      }
      if (startup) {
        stepalt = minalt;
      }
      startup = false;
    }
    Status = "Spline circle added.";
  }

  public async Task CreateCircleSurvey(double lat, double lng) {
    if (await Ask("", $"Start altitude ({CurrentState.AltUnit})", "10") is not { } s1
        || !int.TryParse(s1, out var startalt)) {
      return;
    }
    if (await Ask("", $"End altitude ({CurrentState.AltUnit})", "20") is not { } s2
        || !int.TryParse(s2, out var endalt)) {
      return;
    }
    if (await Ask("", $"Altitude separation ({CurrentState.AltUnit})", "2") is not { } s3
        || !int.TryParse(s3, out var seperation)
        || seperation <= 0 || endalt < startalt) {
      return;
    }
    if (await Ask("", $"Radius ({CurrentState.DistanceUnit})", "5") is not { } s4
        || !int.TryParse(s4, out var radius)) {
      return;
    }
    if (await Ask("", "photos", "50") is not { } s5 || !int.TryParse(s5, out var photos)
        || photos <= 0) {
      return;
    }
    if (await Ask("", "start heading", "0") is not { } s6
        || !int.TryParse(s6, out var startheading)) {
      return;
    }
    using var undo = BeginUndoMutation();
    var center = new PointLatLngAlt(lat, lng);
    double radiusMeters = radius / CurrentState.multiplierdist;
    AddCommandRow(MAVLink.MAV_CMD.DO_SET_ROI, lat, lng, 0, frame: DefaultFrameId);
    for (int alt = startalt; alt <= endalt; alt += seperation) {
      for (int photo = 0; photo <= photos; photo++) {
        double heading = startheading + photo * 360.0 / photos;
        var np = center.newpos(heading, radiusMeters);
        AddCommandRow(MAVLink.MAV_CMD.WAYPOINT, np.Lat, np.Lng,
            FromDisplayAltitude(alt), p1: 2,
            frame: DefaultFrameId);
        AddCommandRow(MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, 0, 0, 0, p2: 1);
      }
    }
    Status = "Circle survey added.";
  }

  public async Task CreateTextWaypoints(double lat, double lng) {
    string? text = await Ask("Text waypoints", "Text", "MP");
    if (string.IsNullOrWhiteSpace(text)) {
      return;
    }
    string? sizeInput = await Ask(
        "Text waypoints", $"Character size ({CurrentState.DistanceUnit})", "5");
    string? rotationInput = await Ask("Text waypoints", "Rotation (degrees)", "0");
    if (!double.TryParse(sizeInput, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double displaySize)
        || !double.TryParse(rotationInput, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double rotation)
        || displaySize <= 0) {
      await Services.Dialogs.Alert("Text waypoints", "Invalid size or rotation.");
      return;
    }

    double sizeMeters = displaySize / CurrentState.multiplierdist;
    IReadOnlyList<(double X, double Y)> geometry = Services.PlannerTextGeometry.Create(
        text, sizeMeters, rotation);
    if (geometry.Count == 0) {
      Status = "The selected font produced no text path.";
      return;
    }
    if (geometry.Count > 700 && !await Services.Dialogs.Confirm(
          "Large text mission",
          $"The text creates {geometry.Count} waypoints and may exceed the vehicle limit. Continue?")) {
      Status = "Text waypoint generation cancelled.";
      return;
    }

    var origin = new PointLatLngAlt(lat, lng);
    int zone = origin.GetUTMZone();
    double[] projected = origin.ToUTM(zone);
    if (MissionType != "Mission") {
      MissionType = "Mission";
    }
    using var undo = BeginUndoMutation();
    foreach (var point in geometry) {
      PointLatLngAlt location = new utmpos(
          projected[0] + point.X, projected[1] - point.Y, zone).ToLLA();
      AddCommandRow(MAVLink.MAV_CMD.WAYPOINT, location.Lat, location.Lng,
          VerifyPlaceAlt(location.Lat, location.Lng, DefaultAlt, DefaultFrameId),
          frame: DefaultFrameId);
    }
    Status = $"Text '{text}' added as {geometry.Count} waypoint(s).";
  }

  private static Task<string?> Ask(string title, string prompt, string value) =>
      Services.Dialogs.InputBox(title, prompt, value);

  public (double[] Dist, double[] Terrain, double[] Planned)? BuildElevationProfile() {
    var pts = Waypoints.Where(w => w.Lat != 0 || w.Lng != 0).ToList();
    if (pts.Count < 2) {
      return null;
    }
    double m = CurrentState.multiplieralt;
    double md = CurrentState.multiplierdist;
    double homeTerr = HomeTerrainAlt();
    var dist = new List<double>();
    var terr = new List<double>();
    var plan = new List<double>();
    double cum = 0;
    var first = new PointLatLngAlt(pts[0].Lat, pts[0].Lng);
    AddSample(dist, terr, plan, 0, pts[0].Lat, pts[0].Lng, pts[0].Alt, pts[0].Frame, homeTerr, m);
    for (int i = 1; i < pts.Count; i++) {
      var a = pts[i - 1];
      var b = pts[i];
      var pa = new PointLatLngAlt(a.Lat, a.Lng);
      var pb = new PointLatLngAlt(b.Lat, b.Lng);
      double leg = pb.GetDistance(pa);
      int segs = Math.Max(1, (int)(leg / 100));
      for (int s = 1; s <= segs; s++) {
        double f = (double)s / segs;
        double la = a.Lat + (b.Lat - a.Lat) * f;
        double lo = a.Lng + (b.Lng - a.Lng) * f;
        double alt = a.Alt + (b.Alt - a.Alt) * f;
        cum += leg / segs * md;
        AddSample(dist, terr, plan, cum, la, lo, alt, b.Frame, homeTerr, m);
      }
    }
    return (dist.ToArray(), terr.ToArray(), plan.ToArray());
  }

  private static void AddSample(List<double> dist, List<double> terr, List<double> plan, double x,
      double lat, double lng, double alt, byte frame, double homeTerr, double m) {
    // alt, terrain and homeTerr are raw metres; m converts to display units for the graph only.
    var t = srtm.getAltitude(lat, lng);
    double terrain = t.currenttype == srtm.tiletype.invalid ? 0 : t.alt;
    double planned = (MAVLink.MAV_FRAME)frame switch {
      MAVLink.MAV_FRAME.GLOBAL => alt,
      MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT => alt + terrain,
      _ => alt + homeTerr,
    };
    dist.Add(x);
    terr.Add(terrain * m);
    plan.Add(planned * m);
  }

  private PointLatLngAlt? _measureStart;

  public async Task MeasureClick(double lat, double lng) {
    if (_measureStart == null) {
      _measureStart = new PointLatLngAlt(lat, lng);
      Status = "Measure: start set — click Measure again at the end point.";
      return;
    }
    var end = new PointLatLngAlt(lat, lng);
    double dist = end.GetDistance(_measureStart) * CurrentState.multiplierdist;
    double az = (end.GetBearing(_measureStart) + 180) % 360;
    _measureStart = null;
    Status = $"Distance: {dist:0.0} {CurrentState.DistanceUnit}  AZ: {az:0}";
    await Services.Dialogs.Alert("Measure",
        $"Distance: {dist:0.0} {CurrentState.DistanceUnit}  AZ: {az:0}");
  }

  public void SetFenceReturn(double lat, double lng) {
    using var undo = BeginUndoMutation();
    var existing = Waypoints.Where(w => w.Command == (ushort)MAVLink.MAV_CMD.FENCE_RETURN_POINT)
                       .ToList();
    foreach (var r in existing) {
      Waypoints.Remove(r);
    }
    AddCommandRow(MAVLink.MAV_CMD.FENCE_RETURN_POINT, lat, lng, 0,
        frame: (byte)MAVLink.MAV_FRAME.GLOBAL);
  }

  private WpRow AddCommandRow(MAVLink.MAV_CMD cmd, double lat, double lng, double alt,
      double p1 = 0, double p2 = 0, double p3 = 0, double p4 = 0,
      byte frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT) {
    var row = new WpRow {
      Seq = Waypoints.Count,
      Command = (ushort)cmd,
      Frame = frame,
      Alt = alt,
      Lat = lat,
      Lng = lng,
      P1 = p1,
      P2 = p2,
      P3 = p3,
      P4 = p4,
    };
    Waypoints.Add(row);
    Renumber();
    WaypointsChanged?.Invoke();
    return row;
  }

  public void InsertWaypointAt(double lat, double lng) {
    if (SelectedWaypoint != null) {
      InsertWaypointAfterSeq(SelectedWaypoint.Seq, lat, lng, spline: false);
    } else {
      AddWaypointAt(lat, lng);
    }
  }

  public void DeleteNearest(double lat, double lng) {
    WpRow? best = null;
    double bestD = double.MaxValue;
    foreach (var w in Waypoints) {
      double d = (w.Lat - lat) * (w.Lat - lat) + (w.Lng - lng) * (w.Lng - lng);
      if (d < bestD) {
        bestD = d;
        best = w;
      }
    }
    if (best != null) {
      using var undo = BeginUndoMutation();
      Waypoints.Remove(best);
      Renumber();
      WaypointsChanged?.Invoke();
    }
  }

  public void AddRtl() {
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, 0, 0, 0);
  }

  public void AddLand(double lat, double lng) {
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.LAND, lat, lng, 0, frame: DefaultFrameId);
  }

  public async Task AddTakeoff(double lat, double lng) {
    double defaultDisplayAlt = ToDisplayAltitude(10);
    string? altitudeInput = await Services.Dialogs.InputBox(
        "Takeoff", $"Takeoff altitude ({CurrentState.AltUnit})",
        defaultDisplayAlt.ToString("0", CultureInfo.InvariantCulture));
    if (!double.TryParse(altitudeInput, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double displayAltitude)) {
      return;
    }

    double pitch = 0;
    bool plane = _comPort.MAV.cs.firmware is Firmwares.ArduPlane or Firmwares.Ateryx;
    bool skipPitch = _comPort.MAV.param.ContainsKey("Q_OPTIONS")
                     && (((int)ParamValue("Q_OPTIONS") & (1 << 1)) == 0);
    if (plane && !skipPitch) {
      string? pitchInput = await Services.Dialogs.InputBox(
          "Takeoff", "Takeoff pitch (degrees)", "15");
      if (!double.TryParse(pitchInput, NumberStyles.Any, CultureInfo.InvariantCulture,
              out pitch)) {
        return;
      }
    }

    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.TAKEOFF, lat, lng,
        FromDisplayAltitude(displayAltitude), p1: pitch, frame: DefaultFrameId);
  }

  [Obsolete]
  public void AddRoi(double lat, double lng) {
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.DO_SET_ROI, lat, lng, DefaultAlt, frame: DefaultFrameId);
  }

  public void AddLoiterForever(double lat, double lng) {
    using var undo = BeginUndoMutation();
    AddCommandRow(MAVLink.MAV_CMD.LOITER_UNLIM, lat, lng, DefaultAlt, frame: DefaultFrameId);
  }

  public async Task AddLoiterTime(double lat, double lng) {
    var s = await Services.Dialogs.InputBox("Loiter Time", "Seconds", "60");
    if (double.TryParse(s, out var sec)) {
      using var undo = BeginUndoMutation();
      AddCommandRow(MAVLink.MAV_CMD.LOITER_TIME, lat, lng, DefaultAlt, p1: sec,
          frame: DefaultFrameId);
    }
  }

  public async Task AddLoiterCircles(double lat, double lng) {
    var s = await Services.Dialogs.InputBox("Loiter Circles", "Turns", "3");
    if (double.TryParse(s, out var turns)) {
      using var undo = BeginUndoMutation();
      AddCommandRow(MAVLink.MAV_CMD.LOITER_TURNS, lat, lng, DefaultAlt, p1: turns,
          frame: DefaultFrameId);
    }
  }

  public async Task AddJump() {
    var s = await Services.Dialogs.InputBox("Jump (DO_JUMP)", "Target WP #", "0");
    if (double.TryParse(s, out var wp)) {
      using var undo = BeginUndoMutation();
      AddCommandRow(MAVLink.MAV_CMD.DO_JUMP, 0, 0, 0, p1: wp, p2: -1);
    }
  }

  [RelayCommand]
  private void ClearMission() {
    using var undo = BeginUndoMutation();
    Waypoints.Clear();
    WaypointsChanged?.Invoke();
  }

  [RelayCommand]
  private void ReverseWaypoints() {
    using var undo = BeginUndoMutation();
    var rev = Waypoints.Reverse().ToList();
    Waypoints.Clear();
    foreach (var w in rev) {
      Waypoints.Add(w);
    }
    Renumber();
    WaypointsChanged?.Invoke();
  }

  public async Task ModifyAllAlt() {
    string? expression = await Services.Dialogs.InputBox(
        "Altitude change",
        $"Enter an altitude change in {CurrentState.AltUnit} (20 = up 20, *2 = altitude × 2)",
        "0");
    if (expression == null) {
      return;
    }
    if (!TryParseAltitudeChange(expression, CurrentState.multiplieralt,
            out bool multiply, out double operand)) {
      await Services.Dialogs.Alert("Altitude change", "Invalid number entered.");
      return;
    }
    using var undo = BeginUndoMutation();
    ApplyAltitudeChange(Waypoints, multiply, operand);
    WaypointsChanged?.Invoke();
  }

  internal static bool TryModifyAltitudes(
      IEnumerable<WpRow> rows, string expression, double displayMultiplier) {
    if (!TryParseAltitudeChange(expression, displayMultiplier,
            out bool multiply, out double operand)) {
      return false;
    }
    ApplyAltitudeChange(rows, multiply, operand);
    return true;
  }

  private static bool TryParseAltitudeChange(
      string expression, double displayMultiplier, out bool multiply, out double operand) {
    string value = expression.Trim();
    multiply = value.StartsWith('*');
    if (multiply) {
      value = value[1..].Trim();
    }
    if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture,
            out operand) || !double.IsFinite(operand)
        || !double.IsFinite(displayMultiplier) || displayMultiplier <= 0) {
      return false;
    }
    if (!multiply) {
      operand /= displayMultiplier;
    }
    return true;
  }

  private static void ApplyAltitudeChange(
      IEnumerable<WpRow> rows, bool multiply, double operand) {
    foreach (var row in rows) {
      row.Alt = multiply ? row.Alt * operand : row.Alt + operand;
    }
  }

  [ObservableProperty]
  private WpRow? _selectedWaypoint;

  [RelayCommand]
  private void DeleteWaypoint(WpRow? row) {
    row ??= SelectedWaypoint;
    if (row != null) {
      using var undo = BeginUndoMutation();
      Waypoints.Remove(row);
      Renumber();
      WaypointsChanged?.Invoke();
    }
  }

  [RelayCommand]
  private void MoveWaypointUp(WpRow? row) {
    int i = row == null ? -1 : Waypoints.IndexOf(row);
    if (i > 0) {
      using var undo = BeginUndoMutation();
      Waypoints.Move(i, i - 1);
      Renumber();
      WaypointsChanged?.Invoke();
    }
  }

  [RelayCommand]
  private void MoveWaypointDown(WpRow? row) {
    int i = row == null ? -1 : Waypoints.IndexOf(row);
    if (i >= 0 && i < Waypoints.Count - 1) {
      using var undo = BeginUndoMutation();
      Waypoints.Move(i, i + 1);
      Renumber();
      WaypointsChanged?.Invoke();
    }
  }

  [RelayCommand]
  private async Task SetHomeFromVehicle() {
    if (!IsConnected) {
      Status = "Not connected.";
      return;
    }
    await Task.Yield();
    var cs = _comPort.MAV.cs;
    using var undo = BeginUndoMutation();
    HomeLat = cs.lat;
    HomeLng = cs.lng;
    // cs.altasl is in display units; the waypoint model stores raw metres.
    HomeAlt = cs.altasl / CurrentState.multiplieralt;
    Status = "Home set from vehicle position.";
  }

  public void SetHome(double lat, double lng) {
    using var undo = BeginUndoMutation();
    HomeLat = lat;
    HomeLng = lng;
    var t = srtm.getAltitude(lat, lng);
    if (t.currenttype != srtm.tiletype.invalid) {
      HomeAlt = Math.Round(t.alt, 2);
    }
    Status = "Home set to clicked location.";
  }

  private double VerifyPlaceAlt(double lat, double lng, double baseAlt, byte frame) {
    if (!VerifyHeight) {
      return baseAlt;
    }
    var t = srtm.getAltitude(lat, lng);
    if (t.currenttype == srtm.tiletype.invalid) {
      return baseAlt;
    }
    double terr = t.alt;
    return (MAVLink.MAV_FRAME)frame switch {
      MAVLink.MAV_FRAME.GLOBAL => terr + baseAlt,
      MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT => baseAlt,
      _ => terr + baseAlt - HomeTerrainAlt(),
    };
  }

  private double HomeTerrainAlt() {
    var h = srtm.getAltitude(HomeLat, HomeLng);
    return h.currenttype == srtm.tiletype.invalid ? 0 : h.alt;
  }

  public void MoveWaypoint(int seq, double lat, double lng) {
    var row = Waypoints.FirstOrDefault(r => r.Seq == seq);
    if (row == null) {
      return;
    }

    using var undo = BeginUndoMutation();
    if (VerifyHeight
        && (MAVLink.MAV_FRAME)row.Frame != MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT) {
      var oldT = srtm.getAltitude(row.Lat, row.Lng);
      var newT = srtm.getAltitude(lat, lng);
      if (oldT.currenttype != srtm.tiletype.invalid
          && newT.currenttype != srtm.tiletype.invalid) {
        row.Alt = row.Alt + newT.alt - oldT.alt;
      }
    }

    row.Lat = lat;
    row.Lng = lng;
  }

  public string GenerateSurveyGrid(double altitude, double spacing, double angle) {
    var polygon = SurveyBoundary(altitude);
    if (polygon.Count < 3) {
      return "Draw at least 3 polygon points to outline the survey area.";
    }

    var home = (HomeLat == 0 && HomeLng == 0)
                   ? polygon[0]
                   : new PointLatLngAlt(HomeLat, HomeLng, HomeAlt);
    try {
      var grid = Grid.CreateGrid(polygon, altitude, spacing, 0, angle, 0, 0,
          Grid.StartPosition.Home, false, 0, 0, 0, home);
      if (grid.Count == 0) {
        return "Grid generation produced no waypoints.";
      }

      using var undo = BeginUndoMutation();
      foreach (var p in grid) {
        Waypoints.Add(new WpRow {
          Seq = Waypoints.Count,
          Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
          Lat = p.Lat,
          Lng = p.Lng,
          Alt = p.Alt,
        });
      }

      Renumber();
      RecomputeGrid();
      WaypointsChanged?.Invoke();
      return $"Survey grid added {grid.Count} waypoint(s).";
    } catch (Exception ex) {
      return "Survey failed: " + ex.Message;
    }
  }

  public (System.Collections.Generic.List<PointLatLngAlt> polygon, PointLatLngAlt home)? BuildSurveyArea() {
    var polygon = SurveyBoundary(DefaultAlt);
    if (polygon.Count < 3) {
      return null;
    }

    var home = (HomeLat == 0 && HomeLng == 0)
                   ? polygon[0]
                   : new PointLatLngAlt(HomeLat, HomeLng, HomeAlt);
    return (polygon, home);
  }

  private List<PointLatLngAlt> SurveyBoundary(double altitude) {
    if (DrawnPolygon.Count >= 3) {
      return DrawnPolygon
          .Select(point => new PointLatLngAlt(point.Lat, point.Lng, altitude))
          .ToList();
    }

    // Preserve the port's earlier mission-outline workflow as a compatibility fallback,
    // while matching upstream's drawn-polygon workflow whenever a polygon is available.
    return Waypoints.Where(row => Services.MissionRoute.IsNavigation(row.Command)
                                  && !(row.Lat == 0 && row.Lng == 0))
        .Select(row => new PointLatLngAlt(row.Lat, row.Lng, altitude))
        .ToList();
  }

  public string AppendSurveyGrid(System.Collections.Generic.List<PointLatLngAlt> grid) {
    if (grid == null || grid.Count == 0) {
      return "Grid produced no waypoints.";
    }

    using var undo = BeginUndoMutation();
    foreach (var p in grid) {
      Waypoints.Add(new WpRow {
        Seq = Waypoints.Count,
        Command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        Lat = p.Lat,
        Lng = p.Lng,
        Alt = p.Alt,
      });
    }

    Renumber();
    RecomputeGrid();
    WaypointsChanged?.Invoke();
    return $"Survey grid added {grid.Count} waypoint(s).";
  }

  public string AppendSurveyPlan(SurveyMissionPlan plan) {
    if (plan.Commands.Count == 0) {
      return "Grid produced no mission commands.";
    }

    using var undo = BeginUndoMutation();
    foreach (var item in plan.Commands) {
      Waypoints.Add(new WpRow {
        Seq = Waypoints.Count,
        Command = (ushort)item.Command,
        Frame = item.Frame,
        Lat = item.Lat,
        Lng = item.Lng,
        Alt = item.Alt,
        P1 = item.P1,
        P2 = item.P2,
        P3 = item.P3,
        P4 = item.P4,
      });
    }

    Renumber();
    RecomputeGrid();
    WaypointsChanged?.Invoke();
    return $"Survey added {plan.NavigationCount} navigation point(s) and " +
           $"{plan.CameraCommandCount} camera command(s).";
  }

  private void OnWaypointsCollectionChanged(object? sender,
      System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
    if (e.OldItems != null) {
      foreach (WpRow r in e.OldItems) {
        r.PropertyChanged -= OnRowChanged;
        r.PropertyChanging -= OnRowChanging;
      }
    }

    if (e.NewItems != null) {
      foreach (WpRow r in e.NewItems) {
        r.PropertyChanged -= OnRowChanged;
        r.PropertyChanged += OnRowChanged;
        r.PropertyChanging -= OnRowChanging;
        r.PropertyChanging += OnRowChanging;
      }
    }

    RecomputeGrid();
    WaypointsChanged?.Invoke();
  }

  private void OnRowChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e) {
    if (_restoringUndo || _undoMutationDepth > 0 || _recomputing) {
      return;
    }
    if (e.PropertyName is nameof(WpRow.Command) or nameof(WpRow.Frame)
        or nameof(WpRow.P1) or nameof(WpRow.P2) or nameof(WpRow.P3) or nameof(WpRow.P4)
        or nameof(WpRow.Lat) or nameof(WpRow.Lng) or nameof(WpRow.Alt)
        or nameof(WpRow.Zone) or nameof(WpRow.Easting) or nameof(WpRow.Northing)
        or nameof(WpRow.Mgrs)) {
      CaptureUndo();
    }
  }

  private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
    if (_recomputing) {
      return;
    }

    if (e.PropertyName is nameof(WpRow.Lat) or nameof(WpRow.Lng) or nameof(WpRow.Alt)) {
      RecomputeGrid();
      WaypointsChanged?.Invoke();
    }
  }

  private void RecomputeGrid() {
    if (_recomputing) {
      return;
    }

    _recomputing = true;
    try {
      var home = (HomeLat == 0 && HomeLng == 0)
                     ? null
                     : new PointLatLngAlt(HomeLat, HomeLng, HomeAlt);
      PointLatLngAlt? last = home;
      double total = 0, prev = 0;
      bool first = true;
      foreach (var w in Waypoints) {
        if ((MissionType == "Mission" && !Services.MissionRoute.IsNavigation(w.Command))
            || (w.Lat == 0 && w.Lng == 0)) {
          w.Grad = w.Angle = w.Dist = w.Az = "";
          continue;
        }

        var cur = new PointLatLngAlt(w.Lat, w.Lng, w.Alt);
        if (last == null) {
          w.Grad = w.Angle = w.Dist = w.Az = "";
        } else {
          double height = w.Alt - last.Alt;
          double distance = cur.GetDistance(last);
          double leg = Math.Sqrt(distance * distance + height * height);
          double grad = distance > 0 ? height / distance : 0;
          w.Grad = (grad * 100).ToString("0.0", CultureInfo.InvariantCulture);
          w.Angle = ((180.0 / Math.PI) * Math.Atan(grad)).ToString("0.0",
              CultureInfo.InvariantCulture);
          w.Dist = (leg * CurrentState.multiplierdist).ToString(
              "0.0", CultureInfo.InvariantCulture);
          w.Az = ((cur.GetBearing(last) + 180) % 360).ToString("0", CultureInfo.InvariantCulture);

          if (!first) {
            total += leg;
          }
          prev = leg;
        }

        last = cur;
        first = false;
      }

      TotalDist = (total * CurrentState.multiplierdist).ToString(
          "0", CultureInfo.InvariantCulture);
      PrevDist = (prev * CurrentState.multiplierdist).ToString(
          "0", CultureInfo.InvariantCulture);
      HomeDist = (home != null && last != null && last != home)
                     ? (last.GetDistance(home) * CurrentState.multiplierdist)
                         .ToString("0", CultureInfo.InvariantCulture)
                     : "0";
    } finally {
      _recomputing = false;
    }
  }

  partial void OnHomeLatChanged(double value) => RecomputeGrid();

  partial void OnHomeLngChanged(double value) => RecomputeGrid();

  partial void OnHomeAltChanged(double value) {
    OnPropertyChanged(nameof(HomeAltDisplay));
    RecomputeGrid();
  }

  partial void OnDefaultAltChanged(double value) =>
      OnPropertyChanged(nameof(DefaultAltDisplay));

  internal static double ToDisplayAltitude(double metres, double? multiplier = null) =>
      metres * ValidAltitudeMultiplier(multiplier);

  internal static double FromDisplayAltitude(double value, double? multiplier = null) =>
      value / ValidAltitudeMultiplier(multiplier);

  private static double ValidAltitudeMultiplier(double? multiplier) {
    double value = multiplier ?? CurrentState.multiplieralt;
    return double.IsFinite(value) && value > 0 ? value : 1;
  }

  private void Replace(IEnumerable<WpRow> rows) {
    void Apply() {
      Waypoints.Clear();
      foreach (var r in rows) {
        Waypoints.Add(r);
      }

      Renumber();
    }
    if (Dispatcher.UIThread.CheckAccess()) {
      Apply();
    } else {
      Dispatcher.UIThread.Post(Apply);
    }
  }

  private void Renumber() {
    for (int i = 0; i < Waypoints.Count; i++) {
      Waypoints[i].Seq = i;
    }
  }

  private static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

  private static double D(string s) =>
      double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

public partial class WpRow : ObservableObject {
  private static readonly List<WeakReference<WpRow>> _instances = [];

  public WpRow() {
    lock (_instances) {
      _instances.Add(new WeakReference<WpRow>(this));
    }
  }

  [ObservableProperty]
  private int _seq;

  [ObservableProperty]
  private ushort _command;

  [ObservableProperty]
  private double _p1;

  [ObservableProperty]
  private double _p2;

  [ObservableProperty]
  private double _p3;

  [ObservableProperty]
  private double _p4;

  [ObservableProperty]
  private double _lat;

  [ObservableProperty]
  private double _lng;

  [ObservableProperty]
  private double _alt;

  public double AltDisplay {
    get => FlightPlannerViewModel.ToDisplayAltitude(Alt);
    set => Alt = FlightPlannerViewModel.FromDisplayAltitude(value);
  }

  partial void OnAltChanged(double value) => OnPropertyChanged(nameof(AltDisplay));

  [ObservableProperty]
  private string _grad = "";

  [ObservableProperty]
  private string _angle = "";

  [ObservableProperty]
  private string _dist = "";

  [ObservableProperty]
  private string _az = "";

  [ObservableProperty]
  private byte _frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;

  [ObservableProperty]
  private string _zone = "";

  [ObservableProperty]
  private string _easting = "";

  [ObservableProperty]
  private string _northing = "";

  [ObservableProperty]
  private string _mgrs = "";

  public static ObservableCollection<string> CommandList { get; } =
      new(Services.MissionCommandCatalog.Names());

  public string CommandName {
    get => Services.MissionCommandCatalog.GetName(Command) ?? ((MAVLink.MAV_CMD)Command).ToString();
    set {
      if (Services.MissionCommandCatalog.TryGetId(value, out var id)) {
        Command = id;
        OnPropertyChanged(nameof(CommandName));
      }
    }
  }

  public static void RefreshCommandCatalog() {
    var names = Services.MissionCommandCatalog.Names();
    CommandList.Clear();
    foreach (var name in names) {
      CommandList.Add(name);
    }
    lock (_instances) {
      for (int index = _instances.Count - 1; index >= 0; index--) {
        if (_instances[index].TryGetTarget(out var row)) {
          row.OnPropertyChanged(nameof(CommandName));
        } else {
          _instances.RemoveAt(index);
        }
      }
    }
  }

  public static readonly string[] FrameList = ["Relative", "Absolute", "Terrain"];

  public string FrameName {
    get => Frame switch {
      (byte)MAVLink.MAV_FRAME.GLOBAL => "Absolute",
      (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT => "Terrain",
      _ => "Relative",
    };
    set {
      Frame = value switch {
        "Absolute" => (byte)MAVLink.MAV_FRAME.GLOBAL,
        "Terrain" => (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
        _ => (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
      };
      OnPropertyChanged(nameof(FrameName));
    }
  }

  private bool _coordGuard;

  partial void OnLatChanged(double value) => RecomputeCoords();

  partial void OnLngChanged(double value) => RecomputeCoords();

  private void RecomputeCoords() {
    if (_coordGuard) {
      return;
    }
    if (Lat == 0 && Lng == 0) {
      _coordGuard = true;
      Zone = Easting = Northing = Mgrs = "";
      _coordGuard = false;
      return;
    }
    try {
      var (zone, e, n) = Services.Geo.ToUtm(Lat, Lng);
      _coordGuard = true;
      Zone = zone.ToString(CultureInfo.InvariantCulture);
      Easting = e.ToString("0.0", CultureInfo.InvariantCulture);
      Northing = n.ToString("0.0", CultureInfo.InvariantCulture);
      Mgrs = Services.Geo.ToMgrs(Lat, Lng);
    } catch {

    } finally {
      _coordGuard = false;
    }
  }

  partial void OnZoneChanged(string value) => ReverseFromUtm();

  partial void OnEastingChanged(string value) => ReverseFromUtm();

  partial void OnNorthingChanged(string value) => ReverseFromUtm();

  private void ReverseFromUtm() {
    if (_coordGuard) {
      return;
    }
    if (!int.TryParse(Zone, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zone)
        || !double.TryParse(Easting, NumberStyles.Any, CultureInfo.InvariantCulture, out var e)
        || !double.TryParse(Northing, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) {
      return;
    }
    try {
      _coordGuard = true;
      var (lat, lng) = Services.Geo.FromUtm(e, n, zone);
      Lat = lat;
      Lng = lng;
    } catch {

    } finally {
      _coordGuard = false;
    }
  }

  partial void OnMgrsChanged(string value) {
    if (_coordGuard || string.IsNullOrWhiteSpace(value)) {
      return;
    }
    try {
      _coordGuard = true;
      var (lat, lng) = Services.Geo.FromMgrs(value.Trim());
      Lat = lat;
      Lng = lng;
    } catch {

    } finally {
      _coordGuard = false;
    }
  }

  public static WpRow From(int seq, Locationwp wp) =>
      new() {
        Seq = seq,
        Command = wp.id,
        Frame = wp.frame,
        P1 = wp.p1,
        P2 = wp.p2,
        P3 = wp.p3,
        P4 = wp.p4,
        Lat = wp.lat,
        Lng = wp.lng,
        Alt = wp.alt,
      };

  public Locationwp ToLocationwp() =>
      new() {
        id = Command,
        frame = Frame,
        p1 = (float)P1,
        p2 = (float)P2,
        p3 = (float)P3,
        p4 = (float)P4,
        lat = Lat,
        lng = Lng,
        alt = (float)Alt,
      };
}
