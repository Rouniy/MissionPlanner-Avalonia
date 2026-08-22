using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Grid;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

internal sealed record SurveyGridPreviewState(
    IReadOnlyList<PointLatLngAlt> Boundary,
    IReadOnlyList<PointLatLngAlt> Grid,
    PointLatLngAlt Home,
    bool Corridor,
    bool ShowBoundary,
    bool ShowGrid,
    bool ShowMarkers,
    bool ShowInternals,
    bool ShowFootprints,
    double Angle,
    bool CameraFacingForward,
    bool HoldHeading,
    double Heading,
    double HorizontalFovDegrees,
    double VerticalFovDegrees);

public partial class GridUIViewModel : ViewModelBase {
  private const double _rad2Deg = 180 / Math.PI;

  private List<PointLatLngAlt> _polygon;
  private readonly PointLatLngAlt _home;
  private readonly Func<PointLatLngAlt, double?> _elevationProvider;

  private readonly Dictionary<string, SurveyCameraProfile> _cameras = new();
  private readonly bool _loading = true;
  private bool _suppressRecalc;

  private static readonly HashSet<string> _outputProps = new() {
    nameof(Status), nameof(AreaText), nameof(DistanceText), nameof(SpacingText),
    nameof(GrndResText), nameof(DistBetweenLinesText), nameof(FootprintText), nameof(TurnRadText),
    nameof(PhotoCount), nameof(StripCount), nameof(WaypointCount), nameof(FlightTimeText),
    nameof(PhotoEveryText), nameof(MinShutterText), nameof(FovH), nameof(FovV), nameof(CmPixel),
    nameof(Result), nameof(UseSpeed), nameof(TriggerMode), nameof(StopTriggerAtStripEnds),
    nameof(AddTakeoff), nameof(TakeoffAltitude), nameof(FinishAction),
    nameof(UseSplineWaypoints), nameof(HoldHeading), nameof(Heading), nameof(WaypointDelay),
    nameof(ServoNumber), nameof(ServoPwm), nameof(ServoRepeatSeconds),
    nameof(ServoLowPwm), nameof(ServoHighPwm), nameof(GroundElevationText),
    nameof(IsPointStart), nameof(BoundaryPointCount),
  };

  private static readonly HashSet<string> _previewDisplayProps = new() {
    nameof(ShowBoundary), nameof(ShowGrid), nameof(ShowMarkers),
    nameof(ShowInternals), nameof(ShowFootprints),
  };

  public List<PointLatLngAlt> Result { get; private set; } = new();

  public event Action<SurveyMissionPlan>? GridAccepted;

  public event Action<IReadOnlyList<PointLatLngAlt>>? BoundaryAccepted;

  public event Action? CloseRequested;

  internal event Action<SurveyGridPreviewState, bool>? PreviewChanged;

  public ObservableCollection<string> Cameras { get; } = new();

  public ObservableCollection<string> StartPositions { get; } =
      new(Enum.GetNames(typeof(Grid.StartPosition)));

  public ObservableCollection<string> TriggerModes { get; } =
      new(new[] {
        SurveyMissionBuilder.TriggerNone,
        SurveyMissionBuilder.TriggerDistance,
        SurveyMissionBuilder.TriggerDigicam,
        SurveyMissionBuilder.TriggerRepeatServo,
        SurveyMissionBuilder.TriggerSetServo,
      });

  public ObservableCollection<string> FinishActions { get; } =
      new(new[] {
        SurveyMissionBuilder.FinishNone,
        SurveyMissionBuilder.FinishRtl,
        SurveyMissionBuilder.FinishLand,
      });

  [ObservableProperty]
  private double _altitude = 100;

  [ObservableProperty]
  private double _angle;

  [ObservableProperty]
  private double _flyingSpeed = 5;

  [ObservableProperty]
  private bool _useSpeed;

  [ObservableProperty]
  private string _triggerMode = SurveyMissionBuilder.TriggerNone;

  [ObservableProperty]
  private bool _stopTriggerAtStripEnds;

  [ObservableProperty]
  private bool _addTakeoff;

  [ObservableProperty]
  private double _takeoffAltitude = 30;

  [ObservableProperty]
  private string _finishAction = SurveyMissionBuilder.FinishNone;

  [ObservableProperty]
  private bool _useSplineWaypoints;

  [ObservableProperty]
  private bool _holdHeading;

  [ObservableProperty]
  private double _heading;

  [ObservableProperty]
  private double _waypointDelay;

  [ObservableProperty]
  private int _servoNumber = 9;

  [ObservableProperty]
  private int _servoPwm = 1900;

  [ObservableProperty]
  private double _servoRepeatSeconds = 1;

  [ObservableProperty]
  private int _servoLowPwm = 1100;

  [ObservableProperty]
  private int _servoHighPwm = 1900;

  [ObservableProperty]
  private int _splitCount = 1;

  [ObservableProperty]
  private string _selectedCamera = "";

  [ObservableProperty]
  private bool _camDirection = true;

  [ObservableProperty]
  private double _distance = 50;

  [ObservableProperty]
  private double _spacing = 30;

  [ObservableProperty]
  private double _overshoot1;

  [ObservableProperty]
  private double _overshoot2;

  [ObservableProperty]
  private double _overlap = 50;

  [ObservableProperty]
  private double _sidelap = 60;

  [ObservableProperty]
  private double _leadin;

  [ObservableProperty]
  private double _leadin2;

  [ObservableProperty]
  private bool _crossGrid;

  [ObservableProperty]
  private bool _corridor;

  [ObservableProperty]
  private double _corridorWidth = 100;

  [ObservableProperty]
  private bool _spiral;

  [ObservableProperty]
  private string _startFrom = "Home";

  [ObservableProperty]
  private double _minLaneSeparation;

  [ObservableProperty]
  private bool _optimizeForDistance;

  [ObservableProperty]
  private int _startPointNumber = 1;

  [ObservableProperty]
  private int _clockwiseLaps = 1;

  [ObservableProperty]
  private int _laps = 1;

  [ObservableProperty]
  private bool _matchSpiralPerimeter;

  [ObservableProperty]
  private double _focalLength = 5;

  [ObservableProperty]
  private string _sensorWidth = "";

  [ObservableProperty]
  private string _sensorHeight = "";

  [ObservableProperty]
  private string _imageWidth = "";

  [ObservableProperty]
  private string _imageHeight = "";

  [ObservableProperty]
  private string _fovH = "";

  [ObservableProperty]
  private string _fovV = "";

  [ObservableProperty]
  private string _cmPixel = "";

  [ObservableProperty]
  private bool _showFootprints;

  [ObservableProperty]
  private bool _showInternals;

  [ObservableProperty]
  private bool _showBoundary = true;

  [ObservableProperty]
  private bool _showGrid = true;

  [ObservableProperty]
  private bool _showMarkers = true;

  [ObservableProperty]
  private string _areaText = "";

  [ObservableProperty]
  private string _distanceText = "";

  [ObservableProperty]
  private string _spacingText = "";

  [ObservableProperty]
  private string _grndResText = "";

  [ObservableProperty]
  private string _distBetweenLinesText = "";

  [ObservableProperty]
  private string _footprintText = "";

  [ObservableProperty]
  private string _turnRadText = "";

  [ObservableProperty]
  private string _groundElevationText = "Unavailable";

  [ObservableProperty]
  private int _photoCount;

  [ObservableProperty]
  private int _stripCount;

  [ObservableProperty]
  private int _waypointCount;

  [ObservableProperty]
  private string _flightTimeText = "";

  [ObservableProperty]
  private string _photoEveryText = "";

  [ObservableProperty]
  private string _minShutterText = "";

  [ObservableProperty]
  private string _status = "";

  public bool IsPointStart => string.Equals(StartFrom, Grid.StartPosition.Point.ToString(),
      StringComparison.Ordinal);

  public int BoundaryPointCount => _polygon.Count;

  public GridUIViewModel() : this(new List<PointLatLngAlt>(), PointLatLngAlt.Zero) {
  }

  public GridUIViewModel(List<PointLatLngAlt> polygon, PointLatLngAlt home,
      Func<PointLatLngAlt, double?>? elevationProvider = null) {
    _polygon = polygon ?? new List<PointLatLngAlt>();
    _home = home ?? PointLatLngAlt.Zero;
    _elevationProvider = elevationProvider ?? GetElevation;

    LoadCameras();
    LoadSettings();
    ApplyCamera();

    if (Angle == 0) {
      Angle = (GetAngleOfLongestSide(_polygon) + 360) % 360;
    }

    _loading = false;
    Recalc();
  }

  protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
    base.OnPropertyChanged(e);
    if (_loading || _suppressRecalc || e.PropertyName == null) {
      return;
    }

    if (_previewDisplayProps.Contains(e.PropertyName)) {
      PublishPreview(fit: false);
      return;
    }

    if (!_outputProps.Contains(e.PropertyName)) {
      if (e.PropertyName == nameof(SelectedCamera)) {
        ApplyCamera();
      }

      Recalc(fitPreview: e.PropertyName != nameof(Angle));
    }
  }

  partial void OnStartFromChanged(string value) => OnPropertyChanged(nameof(IsPointStart));

  [RelayCommand]
  private void Accept() {
    try {
      var options = new SurveyMissionOptions(UseSpeed, FlyingSpeed, TriggerMode, Spacing,
          StopTriggerAtStripEnds, AddTakeoff, TakeoffAltitude, FinishAction,
          UseSplineWaypoints, HoldHeading, Heading, WaypointDelay, ServoNumber, ServoPwm,
          ServoRepeatSeconds, ServoLowPwm, ServoHighPwm, SplitCount, ReadRestoreSpeed());
      GridAccepted?.Invoke(SurveyMissionBuilder.Build(Result, _home, options));
      BoundaryAccepted?.Invoke(_polygon.Select(point => new PointLatLngAlt(point)).ToArray());
      SaveCameraFov();
      CloseRequested?.Invoke();
    } catch (Exception ex) {
      Status = ex.Message;
    }
  }

  [RelayCommand]
  private void Close() => CloseRequested?.Invoke();

  private void ApplyCamera() {
    if (!_cameras.TryGetValue(SelectedCamera, out var cam)) {
      return;
    }

    _suppressRecalc = true;
    FocalLength = cam.FocalLength;
    ImageHeight = cam.ImageHeight.ToString(CultureInfo.InvariantCulture);
    ImageWidth = cam.ImageWidth.ToString(CultureInfo.InvariantCulture);
    SensorHeight = cam.SensorHeight.ToString(CultureInfo.InvariantCulture);
    SensorWidth = cam.SensorWidth.ToString(CultureInfo.InvariantCulture);
    _suppressRecalc = false;
  }

  private void DoCalc() {
    try {
      double flyalt = Altitude;
      int imagewidth = int.Parse(ImageWidth, CultureInfo.InvariantCulture);
      int imageheight = int.Parse(ImageHeight, CultureInfo.InvariantCulture);

      GetFov(flyalt, out double viewwidth, out double viewheight);

      _suppressRecalc = true;
      FovH = viewwidth.ToString("#.#", CultureInfo.InvariantCulture);
      FovV = viewheight.ToString("#.#", CultureInfo.InvariantCulture);
      CmPixel = (viewheight / imageheight * 100).ToString("0.00 cm", CultureInfo.InvariantCulture);

      if (CamDirection) {
        Spacing = (1 - Overlap / 100.0) * viewheight;
        Distance = (1 - Sidelap / 100.0) * viewwidth;
      } else {
        Spacing = (1 - Overlap / 100.0) * viewwidth;
        Distance = (1 - Sidelap / 100.0) * viewheight;
      }

      _suppressRecalc = false;
    } catch {
      _suppressRecalc = false;
    }
  }

  private void GetFov(double flyalt, out double fovh, out double fovv) {
    double focallen = FocalLength;
    double sensorwidth = double.Parse(SensorWidth, CultureInfo.InvariantCulture);
    double sensorheight = double.Parse(SensorHeight, CultureInfo.InvariantCulture);

    double flscale = 1000 * flyalt / focallen;
    fovh = sensorwidth * flscale / 1000;
    fovv = sensorheight * flscale / 1000;
  }

  private void Recalc(bool fitPreview = true) {
    if (_polygon.Count < 3) {
      Status = "Need at least 3 polygon points to generate a survey.";
      Result = new List<PointLatLngAlt>();
      WaypointCount = 0;
      PhotoCount = 0;
      StripCount = 0;
      PublishPreview(fitPreview);
      return;
    }

    if (!string.IsNullOrEmpty(SelectedCamera) && !string.IsNullOrEmpty(SensorWidth)) {
      DoCalc();
    }

    if (!Enum.TryParse(StartFrom, out Grid.StartPosition startpos)) {
      startpos = Grid.StartPosition.Home;
    }

    List<PointLatLngAlt> grid;
    try {
      if (startpos == Grid.StartPosition.Point) {
        int startIndex = Math.Clamp(StartPointNumber - 1, 0, _polygon.Count - 1);
        Grid.StartPointLatLngAlt = new PointLatLngAlt(_polygon[startIndex]);
      }
      if (Corridor) {
        grid = Grid.CreateCorridor(_polygon, Altitude, Distance, Spacing, Angle, Overshoot1,
            Overshoot2, startpos, false, (float)MinLaneSeparation, CorridorWidth, (float)Leadin);
      } else if (Spiral) {
        grid = Grid.CreateRotary(_polygon, Altitude, Distance, Spacing, Angle, Overshoot1,
            Overshoot2, startpos, false, (float)MinLaneSeparation, (float)Leadin, _home,
            ClockwiseLaps, MatchSpiralPerimeter, Laps);
      } else {
        grid = Grid.CreateGrid(_polygon, Altitude, Distance, Spacing, Angle, Overshoot1, Overshoot2,
            startpos, false, (float)MinLaneSeparation, (float)Leadin, (float)Leadin2, _home,
            OptimizeForDistance);
      }

      if (grid.Count > 0 && CrossGrid) {
        Grid.StartPointLatLngAlt = grid[grid.Count - 1];
        grid.AddRange(Grid.CreateGrid(_polygon, Altitude, Distance, Spacing, Angle + 90.0,
            Overshoot1, Overshoot2, Grid.StartPosition.Point, false, (float)MinLaneSeparation,
            (float)Leadin, (float)Leadin2, _home, OptimizeForDistance));
      }
    } catch (Exception ex) {
      Status = "Grid generation failed: " + ex.Message;
      Result = new List<PointLatLngAlt>();
      WaypointCount = 0;
      PhotoCount = 0;
      StripCount = 0;
      PublishPreview(fitPreview);
      return;
    }

    Result = grid;
    ComputeStats(grid);
    PublishPreview(fitPreview);
  }

  internal void MoveBoundaryPoint(int index, double lat, double lng) {
    if (index < 0 || index >= _polygon.Count || !double.IsFinite(lat) ||
        !double.IsFinite(lng)) {
      return;
    }

    PointLatLngAlt current = _polygon[index];
    current.Lat = Math.Clamp(lat, -90, 90);
    current.Lng = Math.Clamp(lng, -180, 180);
    _polygon[index] = current;
    Recalc(fitPreview: false);
  }

  internal void ReplaceBoundary(IReadOnlyList<PointLatLngAlt> boundary) {
    ArgumentNullException.ThrowIfNull(boundary);
    var replacement = boundary
        .Where(point => double.IsFinite(point.Lat) && double.IsFinite(point.Lng))
        .Select(point => new PointLatLngAlt(point) {
          Lat = Math.Clamp(point.Lat, -90, 90),
          Lng = Math.Clamp(point.Lng, -180, 180),
        })
        .ToList();
    if (replacement.Count < 3) {
      return;
    }

    _polygon = replacement;
    StartPointNumber = Math.Clamp(StartPointNumber, 1, _polygon.Count);
    OnPropertyChanged(nameof(BoundaryPointCount));
    Recalc(fitPreview: false);
  }

  internal SurveyGridPreviewState GetPreviewState() {
    double horizontalFov = 0;
    double verticalFov = 0;
    if (double.IsFinite(FocalLength) && FocalLength > 0 &&
        double.TryParse(SensorWidth, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double sensorWidth) && sensorWidth > 0 &&
        double.TryParse(SensorHeight, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double sensorHeight) && sensorHeight > 0) {
      horizontalFov = Math.Atan(sensorWidth / (2 * FocalLength)) * _rad2Deg * 2;
      verticalFov = Math.Atan(sensorHeight / (2 * FocalLength)) * _rad2Deg * 2;
    }

    return new SurveyGridPreviewState(
        _polygon.Select(point => new PointLatLngAlt(point)).ToArray(),
        Result.Select(point => new PointLatLngAlt(point)).ToArray(),
        new PointLatLngAlt(_home), Corridor, ShowBoundary, ShowGrid, ShowMarkers,
        ShowInternals, ShowFootprints, Angle, CamDirection, HoldHeading, Heading,
        horizontalFov, verticalFov);
  }

  private void PublishPreview(bool fit) => PreviewChanged?.Invoke(GetPreviewState(), fit);

  private void ComputeStats(List<PointLatLngAlt> grid) {
    if (grid.Count == 0) {
      Status = "Grid produced no waypoints.";
      WaypointCount = 0;
      PhotoCount = 0;
      StripCount = 0;
      return;
    }

    int strips = 0;
    int images = 0;
    int waypoints = 0;

    double routetotal = grid.First().GetDistance(_home) / 1000.0 +
                        grid.Last().GetDistance(_home) / 1000.0;

    var prev = grid[0];
    foreach (var item in grid) {
      if (item.Tag == "M") {
        images++;
      } else {
        if (item.Tag != "SM" && item.Tag != "ME") {
          strips++;
        }

        waypoints++;
        routetotal += prev.GetDistance(item) / 1000.0;
        prev = item;
      }
    }

    double area = CalcPolygonArea(_polygon);

    double v = FlyingSpeed;
    double turnrad = v * v / (9.808 * Math.Tan(45 / _rad2Deg));

    AreaText = area.ToString("#", CultureInfo.InvariantCulture) + " m^2";
    DistanceText = routetotal.ToString("0.##", CultureInfo.InvariantCulture) + " km";
    SpacingText = Spacing.ToString("0.#", CultureInfo.InvariantCulture) + " m";
    GrndResText = CmPixel;
    DistBetweenLinesText = Distance.ToString("0.##", CultureInfo.InvariantCulture) + " m";
    FootprintText = FovH + " x " + FovV + " m";
    TurnRadText = (turnrad * 2).ToString("0", CultureInfo.InvariantCulture) + " m";

    var terrain = SurveyGridSupport.ComputeTerrainStats(grid, _elevationProvider);
    GroundElevationText = terrain.SampleCount == 0
        ? "Unavailable"
        : terrain.Minimum.ToString("0", CultureInfo.InvariantCulture) + "-" +
          terrain.Maximum.ToString("0", CultureInfo.InvariantCulture) + " m";

    double flyspeedms = FlyingSpeed <= 0 ? 1 : FlyingSpeed;
    PhotoCount = images;
    StripCount = strips / 2;
    WaypointCount = waypoints;

    double seconds = routetotal * 1000.0 / (flyspeedms * 0.8);
    FlightTimeText = SecondsToNice(seconds);
    PhotoEveryText = SecondsToNice(Spacing / flyspeedms);

    try {
      if (!string.IsNullOrEmpty(CmPixel)) {
        double cmpix = double.Parse(CmPixel.TrimEnd('c', 'm', ' '), CultureInfo.InvariantCulture);
        double minmpix = cmpix * 0.01 / 2.0;
        double minshutter = flyspeedms / minmpix;
        MinShutterText = "1/" + (minshutter - minshutter % 1).ToString(CultureInfo.InvariantCulture);
      }
    } catch {
    }

    Status = $"Generated {grid.Count} point(s): {waypoints} waypoints, {images} photos.";
  }

  private static double CalcPolygonArea(List<PointLatLngAlt> polygon) {
    if (polygon.Count < 3) {
      return 0;
    }

    double lat0 = polygon.Average(p => p.Lat);
    double mPerDegLat = 111319.9;
    double mPerDegLng = 111319.9 * Math.Cos(lat0 * Math.PI / 180.0);

    double sum = 0;
    for (int i = 0; i < polygon.Count; i++) {
      var a = polygon[i];
      var b = polygon[(i + 1) % polygon.Count];
      double ax = a.Lng * mPerDegLng;
      double ay = a.Lat * mPerDegLat;
      double bx = b.Lng * mPerDegLng;
      double by = b.Lat * mPerDegLat;
      sum += ax * by - bx * ay;
    }

    return Math.Abs(sum) / 2.0;
  }

  private static double GetAngleOfLongestSide(List<PointLatLngAlt> list) {
    if (list.Count == 0) {
      return 0;
    }

    double angle = 0;
    double maxdist = 0;
    var last = list[list.Count - 1];
    foreach (var item in list) {
      if (item.GetDistance(last) > maxdist) {
        angle = item.GetBearing(last);
        maxdist = item.GetDistance(last);
      }

      last = item;
    }

    return (angle + 360) % 360;
  }

  private static string SecondsToNice(double seconds) {
    if (seconds < 0) {
      return "Infinity Seconds";
    }

    double secs = seconds % 60;
    int mins = (int)(seconds / 60) % 60;
    int hours = (int)(seconds / 3600);

    if (hours > 0) {
      return hours + ":" + mins.ToString("00") + ":" + secs.ToString("00") + " Hours";
    }

    if (mins > 0) {
      return mins + ":" + secs.ToString("00") + " Minutes";
    }

    return secs.ToString("0.00") + " Seconds";
  }

  private void LoadCameras() {
    _cameras.Clear();
    foreach (var (name, profile) in SurveyGridSupport.ReadCameraFiles(
                 Path.Combine(Settings.GetRunningDirectory(), "camerasBuiltin.xml"),
                 Path.Combine(Settings.GetUserDataDirectory(), "cameras.xml"))) {
      _cameras[name] = profile;
    }
    RefreshCameraNames();
  }

  public void LoadSamplePhoto(string filename) {
    try {
      var metadata = SurveyGridSupport.ReadSamplePhoto(filename);
      _suppressRecalc = true;
      ImageWidth = metadata.Width.ToString(CultureInfo.InvariantCulture);
      ImageHeight = metadata.Height.ToString(CultureInfo.InvariantCulture);
      if (metadata.FocalLength.HasValue) {
        FocalLength = metadata.FocalLength.Value;
      }
      _suppressRecalc = false;
      Recalc();
      Status = $"Loaded camera metadata from {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      _suppressRecalc = false;
      Status = "Unable to read sample photo: " + ex.Message;
    }
  }

  public bool SaveCameraProfile(string name) {
    name = name.Trim();
    if (name.Length == 0 || !TryPositiveFloat(ImageWidth, out float imageWidth) ||
        !TryPositiveFloat(ImageHeight, out float imageHeight) ||
        !TryPositiveFloat(SensorWidth, out float sensorWidth) ||
        !TryPositiveFloat(SensorHeight, out float sensorHeight) ||
        !double.IsFinite(FocalLength) || FocalLength <= 0 || FocalLength > float.MaxValue) {
      Status = "Camera name and all focal, sensor and image values must be positive numbers.";
      return false;
    }

    var profile = new SurveyCameraProfile(name, (float)FocalLength, sensorWidth, sensorHeight,
        imageWidth, imageHeight);
    _cameras[name] = profile;
    try {
      SurveyGridSupport.WriteCameraFile(
          Path.Combine(Settings.GetUserDataDirectory(), "cameras.xml"), _cameras.Values);
      RefreshCameraNames();
      SelectedCamera = name;
      Status = $"Saved camera profile '{name}'.";
      return true;
    } catch (Exception ex) {
      Status = "Unable to save camera profile: " + ex.Message;
      return false;
    }
  }

  public void SaveGridFile(string filename) {
    try {
      SurveyGridSupport.SaveGrid(filename, CreateGridData());
      SaveSettings();
      Status = $"Saved survey configuration to {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      Status = "Unable to save survey configuration: " + ex.Message;
    }
  }

  public void LoadGridFile(string filename) {
    try {
      ApplyGridData(SurveyGridSupport.LoadGrid(filename));
      Status = $"Loaded survey configuration from {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      Status = "Unable to load survey configuration: " + ex.Message;
    }
  }

  public void SaveSettings() {
    Set("grid_alt", Altitude);
    Set("grid_angle", Angle);
    Set("grid_speed", FlyingSpeed);
    Set("grid_use_speed", UseSpeed);
    Set("grid_trigger_mode", TriggerMode);
    Set("grid_stop_trigger", StopTriggerAtStripEnds);
    Set("grid_add_takeoff", AddTakeoff);
    Set("grid_takeoff_alt", TakeoffAltitude);
    Set("grid_finish_action", FinishAction);
    Set("grid_spline", UseSplineWaypoints);
    Set("grid_hold_heading", HoldHeading);
    Set("grid_heading", Heading);
    Set("grid_delay", WaypointDelay);
    Set("grid_servo_number", ServoNumber);
    Set("grid_servo_pwm", ServoPwm);
    Set("grid_servo_repeat", ServoRepeatSeconds);
    Set("grid_servo_low", ServoLowPwm);
    Set("grid_servo_high", ServoHighPwm);
    Set("grid_camera", SelectedCamera);
    Set("grid_camdir", CamDirection);
    Set("grid_dist", Distance);
    Set("grid_spacing", Spacing);
    Set("grid_overshoot1", Overshoot1);
    Set("grid_overshoot2", Overshoot2);
    Set("grid_overlap", Overlap);
    Set("grid_sidelap", Sidelap);
    Set("grid_leadin1", Leadin);
    Set("grid_leadin2", Leadin2);
    Set("grid_crossgrid", CrossGrid);
    Set("grid_spiral", Spiral);
    Set("grid_startfrom", StartFrom);
    Set("grid_min_lane_separation", MinLaneSeparation);
    Set("grid_internals", ShowInternals);
    Set("grid_footprints", ShowFootprints);
    Set("grid_corridor", Corridor);
    Set("grid_corridor_width", CorridorWidth);
    Set("grid_clockwise_laps", ClockwiseLaps);
    Set("grid_laps", Laps);
    Set("grid_match_spiral_perimeter", MatchSpiralPerimeter);
  }

  private void LoadSettings() {
    Altitude = GetD("grid_alt", Altitude);
    FlyingSpeed = GetD("grid_speed", FlyingSpeed);
    UseSpeed = GetB("grid_use_speed", UseSpeed);
    TriggerMode = GetChoice("grid_trigger_mode", TriggerMode, TriggerModes);
    StopTriggerAtStripEnds = GetB("grid_stop_trigger", StopTriggerAtStripEnds);
    AddTakeoff = GetB("grid_add_takeoff", AddTakeoff);
    TakeoffAltitude = GetD("grid_takeoff_alt", TakeoffAltitude);
    FinishAction = GetChoice("grid_finish_action", FinishAction, FinishActions);
    UseSplineWaypoints = GetB("grid_spline", UseSplineWaypoints);
    HoldHeading = GetB("grid_hold_heading", HoldHeading);
    Heading = GetD("grid_heading", Heading);
    WaypointDelay = GetD("grid_delay", WaypointDelay);
    ServoNumber = (int)GetD("grid_servo_number", ServoNumber);
    ServoPwm = (int)GetD("grid_servo_pwm", ServoPwm);
    ServoRepeatSeconds = GetD("grid_servo_repeat", ServoRepeatSeconds);
    ServoLowPwm = (int)GetD("grid_servo_low", ServoLowPwm);
    ServoHighPwm = (int)GetD("grid_servo_high", ServoHighPwm);
    CamDirection = GetB("grid_camdir", CamDirection);
    Distance = GetD("grid_dist", Distance);
    Spacing = GetD("grid_spacing", Spacing);
    Overshoot1 = GetD("grid_overshoot1", Overshoot1);
    Overshoot2 = GetD("grid_overshoot2", Overshoot2);
    Overlap = GetD("grid_overlap", Overlap);
    Sidelap = GetD("grid_sidelap", Sidelap);
    Leadin = GetD("grid_leadin1", Leadin);
    Leadin2 = GetD("grid_leadin2", Leadin2);
    CrossGrid = GetB("grid_crossgrid", CrossGrid);
    Spiral = GetB("grid_spiral", Spiral);
    StartFrom = GetS("grid_startfrom", StartFrom);
    MinLaneSeparation = GetD("grid_min_lane_separation", MinLaneSeparation);
    ShowInternals = GetB("grid_internals", ShowInternals);
    ShowFootprints = GetB("grid_footprints", ShowFootprints);
    Corridor = GetB("grid_corridor", Corridor);
    CorridorWidth = GetD("grid_corridor_width", CorridorWidth);
    ClockwiseLaps = (int)GetD("grid_clockwise_laps", ClockwiseLaps);
    Laps = (int)GetD("grid_laps", Laps);
    MatchSpiralPerimeter = GetB("grid_match_spiral_perimeter", MatchSpiralPerimeter);

    SelectedCamera = GetS("grid_camera", SelectedCamera);
  }

  internal GridData CreateGridData() => new() {
    poly = _polygon.ToList(),
    camera = SelectedCamera,
    alt = (decimal)Altitude,
    angle = (decimal)Angle,
    camdir = CamDirection,
    speed = (decimal)FlyingSpeed,
    usespeed = UseSpeed,
    autotakeoff = AddTakeoff,
    autotakeoff_RTL = FinishAction == SurveyMissionBuilder.FinishRtl,
    splitmission = SplitCount,
    internals = ShowInternals,
    footprints = ShowFootprints,
    advanced = true,
    dist = (decimal)Distance,
    overshoot1 = (decimal)Overshoot1,
    overshoot2 = (decimal)Overshoot2,
    leadin = (decimal)Leadin,
    leadin2 = (decimal)Leadin2,
    startfrom = StartFrom,
    overlap = (decimal)Overlap,
    sidelap = (decimal)Sidelap,
    spacing = (decimal)Spacing,
    crossgrid = CrossGrid,
    spiral = Spiral,
    copter_delay = (decimal)WaypointDelay,
    copter_headinghold_chk = HoldHeading,
    copter_headinghold = (decimal)Heading,
    copter_spline = UseSplineWaypoints,
    minlaneseparation = (decimal)MinLaneSeparation,
    clockwiseLaps = ClockwiseLaps,
    laps = Laps,
    matchPerimeter = MatchSpiralPerimeter,
    trigdist = TriggerMode == SurveyMissionBuilder.TriggerDistance,
    digicam = TriggerMode == SurveyMissionBuilder.TriggerDigicam,
    repeatservo = TriggerMode == SurveyMissionBuilder.TriggerRepeatServo,
    breaktrigdist = StopTriggerAtStripEnds,
    repeatservo_no = ServoNumber,
    repeatservo_pwm = ServoPwm,
    repeatservo_cycle = (decimal)ServoRepeatSeconds,
    setservo_no = TriggerMode == SurveyMissionBuilder.TriggerSetServo ? ServoNumber : 0,
    setservo_low = ServoLowPwm,
    setservo_high = ServoHighPwm,
  };

  internal void ApplyGridData(GridData data) {
    _suppressRecalc = true;
    _polygon = data.poly?.ToList() ?? new List<PointLatLngAlt>();
    StartPointNumber = Math.Clamp(StartPointNumber, 1, Math.Max(1, _polygon.Count));
    OnPropertyChanged(nameof(BoundaryPointCount));
    Altitude = (double)data.alt;
    Angle = (double)data.angle;
    CamDirection = data.camdir;
    UseSpeed = data.usespeed;
    FlyingSpeed = (double)data.speed;
    AddTakeoff = data.autotakeoff;
    FinishAction = !data.autotakeoff ? SurveyMissionBuilder.FinishNone
        : data.autotakeoff_RTL ? SurveyMissionBuilder.FinishRtl : SurveyMissionBuilder.FinishLand;
    SplitCount = Math.Max(1, (int)data.splitmission);
    Distance = (double)data.dist;
    Overshoot1 = (double)data.overshoot1;
    Overshoot2 = (double)data.overshoot2;
    Leadin = (double)data.leadin;
    Leadin2 = (double)data.leadin2;
    StartFrom = Enum.TryParse<Grid.StartPosition>(data.startfrom, out _) ? data.startfrom : StartFrom;
    Overlap = (double)data.overlap;
    Sidelap = (double)data.sidelap;
    Spacing = (double)data.spacing;
    CrossGrid = data.crossgrid;
    Spiral = data.spiral;
    WaypointDelay = (double)data.copter_delay;
    HoldHeading = data.copter_headinghold_chk;
    Heading = (double)data.copter_headinghold;
    UseSplineWaypoints = data.copter_spline;
    MinLaneSeparation = (double)data.minlaneseparation;
    ClockwiseLaps = Math.Max(0, (int)data.clockwiseLaps);
    Laps = Math.Max(0, (int)data.laps);
    MatchSpiralPerimeter = data.matchPerimeter;
    TriggerMode = data.trigdist ? SurveyMissionBuilder.TriggerDistance
        : data.digicam ? SurveyMissionBuilder.TriggerDigicam
        : data.repeatservo ? SurveyMissionBuilder.TriggerRepeatServo
        : data.setservo_no > 0 ? SurveyMissionBuilder.TriggerSetServo
        : SurveyMissionBuilder.TriggerNone;
    StopTriggerAtStripEnds = data.breaktrigdist;
    if (data.repeatservo_no > 0) {
      ServoNumber = (int)data.repeatservo_no;
    } else if (data.setservo_no > 0) {
      ServoNumber = (int)data.setservo_no;
    }
    if (data.repeatservo_pwm > 0) {
      ServoPwm = (int)data.repeatservo_pwm;
    }
    ServoRepeatSeconds = (double)data.repeatservo_cycle;
    if (data.setservo_low > 0) {
      ServoLowPwm = (int)data.setservo_low;
    }
    if (data.setservo_high > 0) {
      ServoHighPwm = (int)data.setservo_high;
    }
    ShowInternals = data.internals;
    ShowFootprints = data.footprints;
    SelectedCamera = data.camera ?? "";
    _suppressRecalc = false;
    ApplyCamera();
    Recalc();
  }

  private static void Set(string key, double v) =>
      Settings.Instance[key] = v.ToString(CultureInfo.InvariantCulture);

  private static void Set(string key, bool v) => Settings.Instance[key] = v.ToString();

  private static void Set(string key, string v) => Settings.Instance[key] = v;

  private static double GetD(string key, double fallback) =>
      Settings.Instance.ContainsKey(key) &&
              double.TryParse(Settings.Instance[key], NumberStyles.Any, CultureInfo.InvariantCulture,
                  out var v)
          ? v
          : fallback;

  private static bool GetB(string key, bool fallback) =>
      Settings.Instance.ContainsKey(key) && bool.TryParse(Settings.Instance[key], out var v)
          ? v
          : fallback;

  private static string GetS(string key, string fallback) =>
      Settings.Instance.ContainsKey(key) && Settings.Instance[key] != null ? Settings.Instance[key]
                                                                           : fallback;

  private static string GetChoice(string key, string fallback, IEnumerable<string> choices) {
    string value = GetS(key, fallback);
    return choices.Contains(value, StringComparer.Ordinal) ? value : fallback;
  }

  private void RefreshCameraNames() {
    Cameras.Clear();
    foreach (string name in _cameras.Keys.OrderBy(name => name, StringComparer.Ordinal)) {
      Cameras.Add(name);
    }
  }

  private static bool TryPositiveFloat(string text, out float value) =>
      float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
      float.IsFinite(value) && value > 0;

  private static double? GetElevation(PointLatLngAlt point) {
    var result = srtm.getAltitude(point.Lat, point.Lng);
    return result.currenttype == srtm.tiletype.invalid ? null : result.alt;
  }

  private static double ReadRestoreSpeed() {
    try {
      var parameters = AppState.comPort.MAV.param;
      if (parameters["WPNAV_SPEED"] != null) {
        return parameters["WPNAV_SPEED"].Value / 100.0;
      }
      if (parameters["WP_SPD"] != null) {
        return parameters["WP_SPD"].Value;
      }
    } catch {
    }
    return 0;
  }

  private void SaveCameraFov() {
    if (!TryPositiveFloat(SensorWidth, out float width) ||
        !TryPositiveFloat(SensorHeight, out float height) || FocalLength <= 0) {
      return;
    }
    double horizontal = 2 * Math.Atan(width / (2 * FocalLength)) * _rad2Deg;
    double vertical = 2 * Math.Atan(height / (2 * FocalLength)) * _rad2Deg;
    Settings.Instance["camera_fovh"] = (CamDirection ? horizontal : vertical)
        .ToString(CultureInfo.InvariantCulture);
    Settings.Instance["camera_fovv"] = (CamDirection ? vertical : horizontal)
        .ToString(CultureInfo.InvariantCulture);
  }
}
