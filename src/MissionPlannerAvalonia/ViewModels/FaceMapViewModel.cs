using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

internal sealed record FaceMapPreviewState(
    IReadOnlyList<PointLatLngAlt> FacePath,
    IReadOnlyList<PointLatLngAlt> Route,
    PointLatLngAlt Home,
    bool ShowFacePath,
    bool ShowRoute,
    bool ShowMarkers);

public partial class FaceMapViewModel : ViewModelBase {
  private const double _radToDeg = 180 / Math.PI;
  private readonly PointLatLngAlt _home;
  private readonly byte _frame;
  private List<PointLatLngAlt> _path;
  private readonly Dictionary<string, SurveyCameraProfile> _cameras =
      new(StringComparer.Ordinal);
  private bool _loading = true;
  private bool _applyingCamera;

  private static readonly HashSet<string> _previewProperties = new() {
    nameof(ShowFacePath), nameof(ShowRoute), nameof(ShowMarkers),
  };

  private static readonly HashSet<string> _outputProperties = new() {
    nameof(VerticalSpacing), nameof(TriggerDistance), nameof(RadialPitchStep),
    nameof(FootprintText), nameof(GroundResolutionText), nameof(FovText),
    nameof(DistanceText), nameof(PhotoCount), nameof(StripCount), nameof(WaypointCount),
    nameof(FlightTimeText), nameof(Status),
  };

  public FaceMapViewModel() : this(new List<PointLatLngAlt>(), PointLatLngAlt.Zero,
      (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT) {
  }

  public FaceMapViewModel(List<PointLatLngAlt> path, PointLatLngAlt home, byte frame) {
    _path = path?.Select(point => new PointLatLngAlt(point)).ToList() ?? [];
    _home = home == null ? PointLatLngAlt.Zero : new PointLatLngAlt(home);
    _frame = frame;
    LoadCameras();
    LoadSettings();
    ApplySelectedCamera();
    if (!UnlockCameraPitch) {
      CameraPitch = 90 - FaceAngle;
    }
    _loading = false;
    Recalculate();
  }

  public event Action<SurveyMissionPlan>? PlanAccepted;
  public event Action<IReadOnlyList<PointLatLngAlt>>? PathAccepted;
  public event Action? CloseRequested;
  internal event Action<FaceMapPreviewState, bool>? PreviewChanged;

  public ObservableCollection<string> Cameras { get; } = new();
  public ObservableCollection<string> TriggerModes { get; } = new([
    FaceMapMissionBuilder.TriggerNone,
    FaceMapMissionBuilder.TriggerDistance,
    FaceMapMissionBuilder.TriggerDigicam,
    FaceMapMissionBuilder.TriggerRepeatServo,
    FaceMapMissionBuilder.TriggerSetServo,
  ]);
  public ObservableCollection<string> FinishActions { get; } = new([
    FaceMapMissionBuilder.FinishNone,
    FaceMapMissionBuilder.FinishRtl,
    FaceMapMissionBuilder.FinishLand,
  ]);

  public List<PointLatLngAlt> Result { get; private set; } = [];
  public int PathPointCount => _path.Count;
  public bool CameraPitchLocked => !UnlockCameraPitch;
  public bool HasRepeatServoOptions => TriggerMode == FaceMapMissionBuilder.TriggerRepeatServo;
  public bool HasSetServoOptions => TriggerMode == FaceMapMissionBuilder.TriggerSetServo;
  public bool UsesIntervalTrigger => TriggerMode == FaceMapMissionBuilder.TriggerDistance;

  [ObservableProperty]
  private string _selectedCamera = "";

  [ObservableProperty]
  private double _focalLength = 5;

  [ObservableProperty]
  private string _sensorWidth = "6.17";

  [ObservableProperty]
  private string _sensorHeight = "4.55";

  [ObservableProperty]
  private string _imageWidth = "4000";

  [ObservableProperty]
  private string _imageHeight = "3000";

  [ObservableProperty]
  private double _benchHeight = 10;

  [ObservableProperty]
  private double _faceAngle = 90;

  [ObservableProperty]
  private bool _flipDirection;

  [ObservableProperty]
  private double _bermDepth = 5;

  [ObservableProperty]
  private int _benchCount = 1;

  [ObservableProperty]
  private double _cameraPitch;

  [ObservableProperty]
  private bool _unlockCameraPitch;

  [ObservableProperty]
  private double _toeHeight;

  [ObservableProperty]
  private double _toePointHeight = 5;

  [ObservableProperty]
  private int _toePointRuns;

  [ObservableProperty]
  private bool _followPathHome = true;

  [ObservableProperty]
  private double _distanceFromFace = 10;

  [ObservableProperty]
  private double _overlap = 50;

  [ObservableProperty]
  private double _sidelap = 60;

  [ObservableProperty]
  private double _verticalSpacing;

  [ObservableProperty]
  private double _triggerDistance;

  [ObservableProperty]
  private double _radialPitchStep;

  [ObservableProperty]
  private string _footprintText = "";

  [ObservableProperty]
  private string _groundResolutionText = "";

  [ObservableProperty]
  private string _fovText = "";

  [ObservableProperty]
  private bool _useSpeed = true;

  [ObservableProperty]
  private double _flyingSpeed = 2;

  [ObservableProperty]
  private bool _addTakeoff = true;

  [ObservableProperty]
  private string _finishAction = FaceMapMissionBuilder.FinishRtl;

  [ObservableProperty]
  private int _splitCount = 1;

  [ObservableProperty]
  private bool _extraImages;

  [ObservableProperty]
  private double _copterDelay;

  [ObservableProperty]
  private string _triggerMode = FaceMapMissionBuilder.TriggerDistance;

  [ObservableProperty]
  private bool _stopTriggerAtStripEnds = true;

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
  private bool _showFacePath = true;

  [ObservableProperty]
  private bool _showRoute = true;

  [ObservableProperty]
  private bool _showMarkers = true;

  [ObservableProperty]
  private string _distanceText = "";

  [ObservableProperty]
  private int _photoCount;

  [ObservableProperty]
  private int _stripCount;

  [ObservableProperty]
  private int _waypointCount;

  [ObservableProperty]
  private string _flightTimeText = "";

  [ObservableProperty]
  private string _status = "";

  protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
    base.OnPropertyChanged(e);
    if (_loading || e.PropertyName == null) {
      return;
    }
    if (_outputProperties.Contains(e.PropertyName)) {
      return;
    }

    if (e.PropertyName == nameof(SelectedCamera) && !_applyingCamera) {
      ApplySelectedCamera();
      return;
    }
    if (e.PropertyName == nameof(UnlockCameraPitch)) {
      OnPropertyChanged(nameof(CameraPitchLocked));
      if (!UnlockCameraPitch) {
        CameraPitch = 90 - FaceAngle;
        return;
      }
    }
    if (e.PropertyName == nameof(FaceAngle) && !UnlockCameraPitch) {
      CameraPitch = 90 - FaceAngle;
      return;
    }
    if (e.PropertyName == nameof(TriggerMode)) {
      OnPropertyChanged(nameof(HasRepeatServoOptions));
      OnPropertyChanged(nameof(HasSetServoOptions));
      OnPropertyChanged(nameof(UsesIntervalTrigger));
    }
    if (_previewProperties.Contains(e.PropertyName)) {
      PublishPreview(false);
      return;
    }
    Recalculate(false);
  }

  internal FaceMapPreviewState GetPreviewState() => new(
      _path.Select(point => new PointLatLngAlt(point)).ToArray(),
      Result.Select(point => new PointLatLngAlt(point)).ToArray(),
      new PointLatLngAlt(_home), ShowFacePath, ShowRoute, ShowMarkers);

  internal void MovePathPoint(int index, double lat, double lng) {
    if (index < 0 || index >= _path.Count || !double.IsFinite(lat) ||
        !double.IsFinite(lng)) {
      return;
    }
    PointLatLngAlt point = _path[index];
    point.Lat = Math.Clamp(lat, -90, 90);
    point.Lng = Math.Clamp(lng, -180, 180);
    _path[index] = point;
    Recalculate(false);
  }

  public void LoadSamplePhoto(string filename) {
    try {
      SurveyPhotoMetadata metadata = SurveyGridSupport.ReadSamplePhoto(filename);
      _loading = true;
      ImageWidth = metadata.Width.ToString(CultureInfo.InvariantCulture);
      ImageHeight = metadata.Height.ToString(CultureInfo.InvariantCulture);
      if (metadata.FocalLength.HasValue) {
        FocalLength = metadata.FocalLength.Value;
      }
      _loading = false;
      Recalculate();
      Status = $"Loaded camera metadata from {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      _loading = false;
      Status = "Unable to read sample photo: " + ex.Message;
    }
  }

  public bool SaveCameraProfile(string name) {
    name = name.Trim();
    if (name.Length == 0 || !TryPositiveFloat(SensorWidth, out float sensorWidth) ||
        !TryPositiveFloat(SensorHeight, out float sensorHeight) ||
        !TryPositiveFloat(ImageWidth, out float imageWidth) ||
        !TryPositiveFloat(ImageHeight, out float imageHeight) ||
        !double.IsFinite(FocalLength) || FocalLength <= 0 || FocalLength > float.MaxValue) {
      Status = "Camera name and all focal, sensor and image values must be positive numbers.";
      return false;
    }
    var profile = new SurveyCameraProfile(name, (float)FocalLength,
        sensorWidth, sensorHeight, imageWidth, imageHeight);
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

  public void LoadFaceMapFile(string filename) {
    try {
      ApplyFileData(FaceMapSupport.Load(filename));
      Status = $"Loaded Face Map configuration from {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      Status = "Unable to load Face Map configuration: " + ex.Message;
    }
  }

  public void SaveFaceMapFile(string filename) {
    try {
      FaceMapSupport.Save(filename, CreateFileData());
      SaveSettings();
      Status = $"Saved Face Map configuration to {Path.GetFileName(filename)}.";
    } catch (Exception ex) {
      Status = "Unable to save Face Map configuration: " + ex.Message;
    }
  }

  public void SaveSettings() {
    Set("facemap_camera", SelectedCamera);
    Set("facemap_benchheight", BenchHeight);
    Set("facemap_facedir", FlipDirection);
    Set("facemap_autotakeoff", AddTakeoff);
    Set("facemap_autotakeoff_RTL", FinishAction == FaceMapMissionBuilder.FinishRtl);
    Set("facemap_followpathhome", FollowPathHome);
    Set("facemap_benchangle", FaceAngle);
    Set("facemap_bermdepth", BermDepth);
    Set("facemap_numbenches", BenchCount);
    Set("facemap_campitch", CameraPitch);
    Set("facemap_toeheight", ToeHeight);
    Set("facemap_unlockcampitch", UnlockCameraPitch);
    Set("facemap_extraimages", ExtraImages);
    Set("facemap_overlap", Overlap);
    Set("facemap_sidelap", Sidelap);
    Set("facemap_distance", DistanceFromFace);
    Set("facemap_usespeed", UseSpeed);
    Set("facemap_speed", FlyingSpeed);
    Set("facemap_height_test", ToePointHeight);
    Set("facemap_toepoint_runs", ToePointRuns);
    Set("facemap_trigger_mode", TriggerMode);
    Set("facemap_breakstopstart", StopTriggerAtStripEnds);
    Set("facemap_repeatservo_no", ServoNumber);
    Set("facemap_repeatservo_pwm", ServoPwm);
    Set("facemap_repeatservo_cycle", ServoRepeatSeconds);
    Set("facemap_setservo_no", ServoNumber);
    Set("facemap_setservo_low", ServoLowPwm);
    Set("facemap_setservo_high", ServoHighPwm);
    Set("facemap_copter_delay", CopterDelay);
    Set("facemap_split", SplitCount);
    try {
      Settings.Instance.Save();
    } catch {
      // Settings are a convenience and must never prevent accepting a generated mission.
    }
  }

  [RelayCommand(CanExecute = nameof(CanAccept))]
  private void Accept() {
    try {
      SurveyMissionPlan plan = FaceMapMissionBuilder.Build(Result, _home,
          new FaceMapMissionOptions(UseSpeed, FlyingSpeed, TriggerMode, TriggerDistance,
              StopTriggerAtStripEnds, AddTakeoff, FinishAction, ExtraImages, CopterDelay,
              CameraPitch, ToePointRuns, RadialPitchStep, FlipDirection, FollowPathHome,
              SplitCount,
              ServoNumber, ServoPwm, ServoRepeatSeconds, ServoLowPwm, ServoHighPwm, _frame));
      if (plan.Commands.Count == 0) {
        Status = "Face Map produced no mission commands.";
        return;
      }
      SaveCameraFov();
      SaveSettings();
      PlanAccepted?.Invoke(plan);
      PathAccepted?.Invoke(_path.Select(point => new PointLatLngAlt(point)).ToArray());
      CloseRequested?.Invoke();
    } catch (Exception ex) {
      Status = "Unable to accept Face Map mission: " + ex.Message;
    }
  }

  private bool CanAccept() => Result.Count > 0;

  [RelayCommand]
  private void Close() => CloseRequested?.Invoke();

  private void Recalculate(bool fitPreview = true) {
    CalculateCamera();
    try {
      double altitudeOffset = _frame == (byte)MAVLink.MAV_FRAME.GLOBAL ? _home.Alt : 0;
      Result = FaceMapGeometry.Create(_path,
          new FaceMapGeometryOptions(BenchHeight, VerticalSpacing, DistanceFromFace,
              FaceAngle, CameraPitch, FlipDirection, BermDepth, BenchCount, ToeHeight,
              ToePointHeight, ToePointRuns, FollowPathHome, altitudeOffset));
      ComputeStats();
    } catch (Exception ex) {
      Result = [];
      DistanceText = "0 km";
      PhotoCount = 0;
      StripCount = 0;
      WaypointCount = 0;
      FlightTimeText = "";
      Status = "Face Map generation failed: " + ex.Message;
    }
    AcceptCommand.NotifyCanExecuteChanged();
    PublishPreview(fitPreview);
  }

  private void CalculateCamera() {
    if (!double.IsFinite(FocalLength) || FocalLength <= 0 ||
        !TryPositiveDouble(SensorWidth, out double sensorWidth) ||
        !TryPositiveDouble(SensorHeight, out double sensorHeight) ||
        !TryPositiveDouble(ImageHeight, out double imageHeight) ||
        !double.IsFinite(DistanceFromFace) || DistanceFromFace <= 0) {
      VerticalSpacing = 0;
      TriggerDistance = 0;
      RadialPitchStep = 0;
      FootprintText = "Invalid camera";
      GroundResolutionText = "";
      FovText = "";
      return;
    }

    double viewWidth = sensorWidth * DistanceFromFace / FocalLength;
    double viewHeight = sensorHeight * DistanceFromFace / FocalLength;
    double verticalFov = Math.Atan(sensorHeight / (2 * FocalLength)) * _radToDeg * 2;
    double horizontalFov = Math.Atan(sensorWidth / (2 * FocalLength)) * _radToDeg * 2;
    _loading = true;
    TriggerDistance = Math.Max(0.01, (1 - Sidelap / 100) * viewWidth);
    VerticalSpacing = Math.Max(0.1, (1 - Overlap / 100) * viewHeight);
    RadialPitchStep = Math.Max(0, verticalFov * (1 - Overlap / 100));
    _loading = false;
    FootprintText = $"{viewWidth:0.##} × {viewHeight:0.##} m";
    GroundResolutionText = $"{viewHeight / imageHeight * 100:0.00} cm/px";
    FovText = $"{horizontalFov:0.#}° × {verticalFov:0.#}°";
  }

  private void ComputeStats() {
    double distance = 0;
    for (int index = 1; index < Result.Count; index++) {
      distance += Result[index - 1].GetDistance(Result[index]);
    }
    StripCount = Result.Count(point => point.Tag?.ToString() == "E");
    WaypointCount = Result.Count(point => point.Tag?.ToString() is not ("SM" or "ME"));
    PhotoCount = TriggerMode switch {
      FaceMapMissionBuilder.TriggerDistance when TriggerDistance > 0 =>
          BoundedCount(Math.Ceiling(distance / TriggerDistance)),
      FaceMapMissionBuilder.TriggerDigicam => Result.Count(point =>
          point.Tag?.ToString() is "SM" or "M" or "ME"),
      _ => 0,
    };
    DistanceText = $"{distance / 1000:0.##} km";
    double seconds = distance / (Math.Max(0.1, FlyingSpeed) * 0.8);
    FlightTimeText = FormatDuration(seconds);
    Status = $"Generated {StripCount} face strip(s), {WaypointCount} navigation point(s).";
  }

  private void LoadCameras() {
    foreach ((string name, SurveyCameraProfile profile) in SurveyGridSupport.ReadCameraFiles(
                 Path.Combine(Settings.GetRunningDirectory(), "camerasBuiltin.xml"),
                 Path.Combine(Settings.GetUserDataDirectory(), "cameras.xml"))) {
      _cameras[name] = profile;
    }
    RefreshCameraNames();
  }

  private void RefreshCameraNames() {
    string selected = SelectedCamera;
    Cameras.Clear();
    foreach (string name in _cameras.Keys.OrderBy(name => name, StringComparer.Ordinal)) {
      Cameras.Add(name);
    }
    if (_cameras.ContainsKey(selected)) {
      SelectedCamera = selected;
    } else if (Cameras.Count > 0) {
      SelectedCamera = Cameras[0];
    }
  }

  private void ApplySelectedCamera() {
    if (!_cameras.TryGetValue(SelectedCamera, out SurveyCameraProfile? camera)) {
      Recalculate();
      return;
    }
    _applyingCamera = true;
    _loading = true;
    FocalLength = camera.FocalLength;
    SensorWidth = camera.SensorWidth.ToString(CultureInfo.InvariantCulture);
    SensorHeight = camera.SensorHeight.ToString(CultureInfo.InvariantCulture);
    ImageWidth = camera.ImageWidth.ToString(CultureInfo.InvariantCulture);
    ImageHeight = camera.ImageHeight.ToString(CultureInfo.InvariantCulture);
    _loading = false;
    _applyingCamera = false;
    Recalculate();
  }

  private FaceMapFileData CreateFileData() => new() {
    poly = _path.Select(point => new PointLatLngAlt(point)).ToList(),
    camera = SelectedCamera,
    benchheight = (decimal)BenchHeight,
    angle = (decimal)FaceAngle,
    facedirection = FlipDirection,
    speed = (decimal)FlyingSpeed,
    usespeed = UseSpeed,
    autotakeoff = AddTakeoff,
    autotakeoff_RTL = FinishAction == FaceMapMissionBuilder.FinishRtl,
    extraimages = ExtraImages,
    height_test = (decimal)ToePointHeight,
    toepoint_runs = ToePointRuns,
    splitmission = SplitCount,
    bermdepth = (decimal)BermDepth,
    numbenches = BenchCount,
    camerapitch = (decimal)CameraPitch,
    toeheight = (decimal)ToeHeight,
    campitchunlock = UnlockCameraPitch,
    dist = (decimal)DistanceFromFace,
    overlap = (decimal)Overlap,
    sidelap = (decimal)Sidelap,
    spacing = (decimal)TriggerDistance,
    copter_delay = (decimal)CopterDelay,
    trigdist = TriggerMode == FaceMapMissionBuilder.TriggerDistance,
    digicam = TriggerMode == FaceMapMissionBuilder.TriggerDigicam,
    repeatservo = TriggerMode == FaceMapMissionBuilder.TriggerRepeatServo,
    breaktrigdist = StopTriggerAtStripEnds,
    repeatservo_no = ServoNumber,
    repeatservo_pwm = ServoPwm,
    repeatservo_cycle = (decimal)ServoRepeatSeconds,
    setservo_no = TriggerMode == FaceMapMissionBuilder.TriggerSetServo ? ServoNumber : 0,
    setservo_low = ServoLowPwm,
    setservo_high = ServoHighPwm,
    followpathhome = FollowPathHome,
    radialpitchoffset = (decimal)RadialPitchStep,
  };

  private void ApplyFileData(FaceMapFileData data) {
    _loading = true;
    if (data.poly.Count >= 3) {
      _path = data.poly.Select(point => new PointLatLngAlt(point)).ToList();
      OnPropertyChanged(nameof(PathPointCount));
    }
    if (_cameras.ContainsKey(data.camera)) {
      SelectedCamera = data.camera;
    }
    BenchHeight = Positive((double)data.benchheight, BenchHeight);
    FaceAngle = Math.Clamp(Positive((double)data.angle, FaceAngle), 1, 90);
    FlipDirection = data.facedirection;
    FlyingSpeed = Positive((double)data.speed, FlyingSpeed);
    UseSpeed = data.usespeed;
    AddTakeoff = data.autotakeoff;
    FinishAction = !data.autotakeoff ? FaceMapMissionBuilder.FinishNone
        : data.autotakeoff_RTL ? FaceMapMissionBuilder.FinishRtl
        : FaceMapMissionBuilder.FinishLand;
    ExtraImages = data.extraimages;
    ToePointHeight = Math.Max(0, (double)data.height_test);
    ToePointRuns = DecimalInt(data.toepoint_runs, 0, 10_000, ToePointRuns);
    SplitCount = DecimalInt(data.splitmission, 1, 300, 1);
    BermDepth = Math.Max(0, (double)data.bermdepth);
    BenchCount = DecimalInt(data.numbenches, 1, 10_000, 1);
    CameraPitch = Math.Clamp((double)data.camerapitch, 0, 89);
    ToeHeight = (double)data.toeheight;
    UnlockCameraPitch = data.campitchunlock;
    DistanceFromFace = Positive((double)data.dist, DistanceFromFace);
    Overlap = Math.Clamp((double)data.overlap, 0, 99);
    Sidelap = Math.Clamp((double)data.sidelap, 0, 99);
    CopterDelay = Math.Max(0, (double)data.copter_delay);
    TriggerMode = data.trigdist ? FaceMapMissionBuilder.TriggerDistance
        : data.digicam ? FaceMapMissionBuilder.TriggerDigicam
        : data.repeatservo ? FaceMapMissionBuilder.TriggerRepeatServo
        : data.setservo_no > 0 ? FaceMapMissionBuilder.TriggerSetServo
        : FaceMapMissionBuilder.TriggerNone;
    StopTriggerAtStripEnds = data.breaktrigdist;
    ServoNumber = DecimalInt(data.setservo_no > 0
        ? data.setservo_no : data.repeatservo_no, 1, 16, ServoNumber);
    ServoPwm = DecimalInt(data.repeatservo_pwm, 800, 2200, ServoPwm);
    ServoRepeatSeconds = Math.Max(0, (double)data.repeatservo_cycle);
    ServoLowPwm = DecimalInt(data.setservo_low, 800, 2200, ServoLowPwm);
    ServoHighPwm = DecimalInt(data.setservo_high, 800, 2200, ServoHighPwm);
    FollowPathHome = data.followpathhome;
    if (!UnlockCameraPitch) {
      CameraPitch = 90 - FaceAngle;
    }
    _loading = false;
    ApplySelectedCamera();
  }

  private void LoadSettings() {
    SelectedCamera = GetString("facemap_camera", SelectedCamera);
    BenchHeight = GetDouble("facemap_benchheight", BenchHeight);
    FlipDirection = GetBool("facemap_facedir", FlipDirection);
    AddTakeoff = GetBool("facemap_autotakeoff", AddTakeoff);
    bool rtl = GetBool("facemap_autotakeoff_RTL", true);
    FinishAction = !AddTakeoff ? FaceMapMissionBuilder.FinishNone
        : rtl ? FaceMapMissionBuilder.FinishRtl : FaceMapMissionBuilder.FinishLand;
    FollowPathHome = GetBool("facemap_followpathhome", FollowPathHome);
    FaceAngle = Math.Clamp(GetDouble("facemap_benchangle", FaceAngle), 1, 90);
    BermDepth = Math.Max(0, GetDouble("facemap_bermdepth", BermDepth));
    BenchCount = Math.Clamp(GetInt("facemap_numbenches", BenchCount), 1, 9999);
    CameraPitch = Math.Clamp(GetDouble("facemap_campitch", CameraPitch), 0, 89);
    ToeHeight = GetDouble("facemap_toeheight", ToeHeight);
    UnlockCameraPitch = GetBool("facemap_unlockcampitch", UnlockCameraPitch);
    ExtraImages = GetBool("facemap_extraimages", ExtraImages);
    Overlap = Math.Clamp(GetDouble("facemap_overlap", Overlap), 0, 99);
    Sidelap = Math.Clamp(GetDouble("facemap_sidelap", Sidelap), 0, 99);
    DistanceFromFace = Positive(GetDouble("facemap_distance", DistanceFromFace), 10);
    UseSpeed = GetBool("facemap_usespeed", UseSpeed);
    FlyingSpeed = Positive(GetDouble("facemap_speed", FlyingSpeed), 2);
    ToePointHeight = Math.Max(0, GetDouble("facemap_height_test", ToePointHeight));
    ToePointRuns = Math.Clamp(GetInt("facemap_toepoint_runs", ToePointRuns), 0, 9999);
    TriggerMode = GetString("facemap_trigger_mode", TriggerMode);
    if (!TriggerModes.Contains(TriggerMode)) {
      TriggerMode = GetBool("facemap_digicam", false)
          ? FaceMapMissionBuilder.TriggerDigicam
          : GetBool("facemap_repeatservo", false)
              ? FaceMapMissionBuilder.TriggerRepeatServo
              : FaceMapMissionBuilder.TriggerDistance;
    }
    StopTriggerAtStripEnds = GetBool("facemap_breakstopstart", StopTriggerAtStripEnds);
    ServoNumber = Math.Clamp(GetInt("facemap_repeatservo_no", ServoNumber), 1, 16);
    ServoPwm = Math.Clamp(GetInt("facemap_repeatservo_pwm", ServoPwm), 800, 2200);
    ServoRepeatSeconds = Math.Max(0,
        GetDouble("facemap_repeatservo_cycle", ServoRepeatSeconds));
    ServoLowPwm = Math.Clamp(GetInt("facemap_setservo_low", ServoLowPwm), 800, 2200);
    ServoHighPwm = Math.Clamp(GetInt("facemap_setservo_high", ServoHighPwm), 800, 2200);
    CopterDelay = Math.Clamp(GetDouble("facemap_copter_delay", CopterDelay), 0, 60);
    SplitCount = Math.Clamp(GetInt("facemap_split", SplitCount), 1, 300);
  }

  private void SaveCameraFov() {
    if (!TryPositiveDouble(SensorWidth, out double sensorWidth) ||
        !TryPositiveDouble(SensorHeight, out double sensorHeight) || FocalLength <= 0) {
      return;
    }
    double horizontal = Math.Atan(sensorWidth / (2 * FocalLength)) * _radToDeg * 2;
    double vertical = Math.Atan(sensorHeight / (2 * FocalLength)) * _radToDeg * 2;
    Settings.Instance["camera_fovh"] = vertical.ToString(CultureInfo.InvariantCulture);
    Settings.Instance["camera_fovv"] = horizontal.ToString(CultureInfo.InvariantCulture);
  }

  private void PublishPreview(bool fit) => PreviewChanged?.Invoke(GetPreviewState(), fit);

  private static string FormatDuration(double seconds) {
    if (!double.IsFinite(seconds) || seconds < 0) {
      return "Unavailable";
    }
    if (seconds >= TimeSpan.MaxValue.TotalSeconds) {
      return "Over 10,675,199 days";
    }
    TimeSpan duration = TimeSpan.FromSeconds(seconds);
    return duration.TotalHours >= 1
        ? $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes}:{duration.Seconds:00}";
  }

  private static int BoundedCount(double value) =>
      !double.IsFinite(value) || value <= 0 ? 0
      : value >= int.MaxValue ? int.MaxValue
      : (int)value;

  private static bool TryPositiveDouble(string text, out double value) =>
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
      double.IsFinite(value) && value > 0;

  private static bool TryPositiveFloat(string text, out float value) =>
      float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
      float.IsFinite(value) && value > 0;

  private static double Positive(double value, double fallback) =>
      double.IsFinite(value) && value > 0 ? value : fallback;

  private static int DecimalInt(decimal value, int minimum, int maximum, int fallback) {
    if (value < minimum || value > maximum) {
      return fallback;
    }
    return (int)decimal.Truncate(value);
  }

  private static void Set(string key, string value) => Settings.Instance[key] = value;
  private static void Set(string key, bool value) => Settings.Instance[key] = value.ToString();
  private static void Set(string key, double value) =>
      Settings.Instance[key] = value.ToString(CultureInfo.InvariantCulture);
  private static void Set(string key, int value) =>
      Settings.Instance[key] = value.ToString(CultureInfo.InvariantCulture);

  private static string GetString(string key, string fallback) =>
      Settings.Instance[key] is { Length: > 0 } value ? value : fallback;

  private static bool GetBool(string key, bool fallback) =>
      bool.TryParse(Settings.Instance[key], out bool value) ? value : fallback;

  private static double GetDouble(string key, double fallback) =>
      double.TryParse(Settings.Instance[key], NumberStyles.Float, CultureInfo.InvariantCulture,
          out double value) && double.IsFinite(value) ? value : fallback;

  private static int GetInt(string key, int fallback) =>
      int.TryParse(Settings.Instance[key], NumberStyles.Integer, CultureInfo.InvariantCulture,
          out int value) ? value : fallback;
}
