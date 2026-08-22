using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using AvPixelFormat = Avalonia.Platform.PixelFormat;
using NumericsVector3 = System.Numerics.Vector3;

namespace MissionPlannerAvalonia.ViewModels;

public partial class Terrain3DViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly DispatcherTimer _timer;
  private readonly SemaphoreSlim _renderGate = new(1, 1);
  private CancellationTokenSource? _reloadCancellation;
  private Terrain3DWorld? _world;
  private Terrain3DSnapshot? _lastSnapshot;
  private Terrain3DGeoPoint? _lastTelemetryPosition;
  private DateTime _lastPositionChangeUtc = DateTime.UtcNow;
  private Terrain3DCamera? _freeCamera;
  private Terrain3DCamera _lastCamera;
  private bool _started;
  private bool _loading;
  private bool _disposed;
  private int _viewportWidth = 960;
  private int _viewportHeight = 600;

  [ObservableProperty]
  private WriteableBitmap? _frame;

  [ObservableProperty]
  private string _status = "Waiting for a valid vehicle GPS position.";

  [ObservableProperty]
  private string _details = "SRTM terrain and map imagery have not been loaded.";

  [ObservableProperty]
  private string _pointerPosition = "Move over the terrain to inspect a point.";

  [ObservableProperty]
  private bool _lockToVehicle = true;

  [ObservableProperty]
  private bool _fogEnabled = true;

  [ObservableProperty]
  private bool _imageryEnabled = true;

  [ObservableProperty]
  private double _rangeM = 1500;

  [ObservableProperty]
  private int _gridSize = 33;

  [ObservableProperty]
  private int _textureMinZoom = 12;

  [ObservableProperty]
  private int _textureMaxZoom = 20;

  [ObservableProperty]
  private double _verticalExaggeration = 1;

  public Terrain3DViewModel() {
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += OnTimerTick;
  }

  public void Start() {
    if (_disposed || _started) {
      return;
    }
    _started = true;
    _timer.Start();
    _ = Reload();
  }

  public void SetViewport(double width, double height) {
    if (!double.IsFinite(width) || !double.IsFinite(height)) {
      return;
    }
    _viewportWidth = Math.Clamp((int)Math.Round(width), 320, 1280);
    _viewportHeight = Math.Clamp((int)Math.Round(height), 240, 800);
  }

  internal void MoveCamera(Terrain3DCameraMotion motion) {
    if (_world == null || _lastSnapshot == null) {
      return;
    }
    if (LockToVehicle) {
      LockToVehicle = false;
    }
    Terrain3DCamera current = _freeCamera
        ?? Terrain3DCamera.Locked(_world, _lastSnapshot, DateTime.UtcNow);
    double amount = motion switch {
      Terrain3DCameraMotion.YawLeft or Terrain3DCameraMotion.YawRight => 3,
      Terrain3DCameraMotion.PitchUp or Terrain3DCameraMotion.PitchDown => 2,
      Terrain3DCameraMotion.Up or Terrain3DCameraMotion.Down => 5,
      _ => 10,
    };
    _freeCamera = current.Move(motion, amount);
    _ = RenderLatestAsync();
  }

  public void InspectPoint(double x, double y) {
    if (!TryTerrainPoint(x, y, out Terrain3DGeoPoint point)) {
      PointerPosition = "No terrain intersection.";
      return;
    }
    PointerPosition = $"{point.Latitude:0.000000}, {point.Longitude:0.000000} · terrain {point.AltitudeM:0.0} m AMSL";
  }

  public async Task SendGuidedTargetAsync(double x, double y) {
    if (_comPort.BaseStream?.IsOpen != true) {
      Status = "Guided target not sent: no vehicle connection.";
      return;
    }
    if (!TryTerrainPoint(x, y, out Terrain3DGeoPoint point)) {
      Status = "Guided target not sent: the pointer does not intersect loaded terrain.";
      return;
    }
    double guidedAltitude = _comPort.MAV.GuidedMode.z;
    if (!double.IsFinite(guidedAltitude) || Math.Abs(guidedAltitude) < 0.01) {
      Status = "Guided target not sent: set a non-zero guided altitude in Flight Data first.";
      return;
    }
    var target = new Locationwp().Set(
        point.Latitude,
        point.Longitude,
        guidedAltitude,
        (ushort)MAVLink.MAV_CMD.WAYPOINT);
    try {
      await Task.Run(() => _comPort.setGuidedModeWP(
          _comPort.MAV.sysid, _comPort.MAV.compid, target, false));
      Status = $"Guided target sent: {point.Latitude:0.000000}, {point.Longitude:0.000000} at current guided altitude {guidedAltitude:0.0} m.";
    } catch (Exception ex) {
      Status = "Guided target failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task Reload() {
    if (_disposed || _loading) {
      return;
    }
    Terrain3DSnapshot snapshot;
    try {
      snapshot = CaptureSnapshot();
    } catch (Exception ex) {
      Status = "Could not read vehicle state: " + ex.Message;
      return;
    }
    if (!snapshot.HasPosition) {
      Status = "Waiting for a valid vehicle GPS position.";
      return;
    }

    _loading = true;
    _reloadCancellation?.Cancel();
    _reloadCancellation?.Dispose();
    _reloadCancellation = new CancellationTokenSource();
    CancellationToken token = _reloadCancellation.Token;
    Terrain3DSettings settings = CurrentSettings;
    Status = $"Loading {settings.GridSize}×{settings.GridSize} SRTM terrain and map imagery…";
    try {
      Terrain3DWorld next = await Terrain3DWorldBuilder.BuildAsync(
          snapshot,
          settings,
          MapTileSourceFactory.CurrentMapType,
          ElevationAt,
          MapTileSourceFactory.GetTileAsync,
          token);
      if (_disposed || token.IsCancellationRequested) {
        next.Dispose();
        return;
      }
      await _renderGate.WaitAsync(token);
      try {
        Terrain3DWorld? previous = _world;
        _world = next;
        _lastSnapshot = snapshot;
        _freeCamera = null;
        _lastCamera = Terrain3DCamera.Locked(next, snapshot, DateTime.UtcNow);
        previous?.Dispose();
      } finally {
        _renderGate.Release();
      }
      Status = "Terrain ready. Click the view to send a target using the current guided altitude.";
      await RenderLatestAsync();
    } catch (OperationCanceledException) {
    } catch (Exception ex) {
      Status = "Terrain load failed: " + ex.Message;
    } finally {
      _loading = false;
    }
  }

  partial void OnLockToVehicleChanged(bool value) {
    if (!value && _world != null && _lastSnapshot != null) {
      _freeCamera = _lastCamera;
    }
    _ = RenderLatestAsync();
  }

  partial void OnFogEnabledChanged(bool value) => _ = RenderLatestAsync();

  partial void OnVerticalExaggerationChanged(double value) => _ = RenderLatestAsync();

  partial void OnImageryEnabledChanged(bool value) => MarkReloadRequired();

  partial void OnRangeMChanged(double value) => MarkReloadRequired();

  partial void OnGridSizeChanged(int value) => MarkReloadRequired();

  partial void OnTextureMinZoomChanged(int value) => MarkReloadRequired();

  partial void OnTextureMaxZoomChanged(int value) => MarkReloadRequired();

  private void MarkReloadRequired() {
    if (_world != null && !_loading) {
      Status = "Terrain settings changed; select Reload terrain to apply them.";
    }
  }

  private void OnTimerTick(object? sender, EventArgs e) {
    if (_disposed) {
      return;
    }
    Terrain3DSnapshot snapshot;
    try {
      snapshot = CaptureSnapshot();
    } catch {
      return;
    }
    if (!snapshot.HasPosition) {
      Status = "Waiting for a valid vehicle GPS position.";
      return;
    }
    _lastSnapshot = snapshot;
    if (_world == null) {
      if (!_loading) {
        _ = Reload();
      }
      return;
    }
    (double east, double north) = Terrain3DGeoMath.ToLocal(_world.Center, snapshot.Vehicle);
    if (!_loading && Math.Max(Math.Abs(east), Math.Abs(north)) > _world.RangeM * 0.6) {
      _ = Reload();
    }
    _ = RenderLatestAsync();
  }

  private async Task RenderLatestAsync() {
    if (_disposed || _world == null || _lastSnapshot == null
        || !await _renderGate.WaitAsync(0)) {
      return;
    }
    try {
      Terrain3DWorld world = _world;
      Terrain3DSnapshot snapshot = _lastSnapshot;
      Terrain3DCamera camera = LockToVehicle
          ? Terrain3DCamera.Locked(world, snapshot, DateTime.UtcNow)
          : _freeCamera ?? Terrain3DCamera.Locked(world, snapshot, DateTime.UtcNow);
      _lastCamera = camera;
      Terrain3DSettings settings = CurrentSettings;
      Terrain3DRenderResult rendered = await Task.Run(() => Terrain3DRenderer.Render(
          world, snapshot, settings, camera, _viewportWidth, _viewportHeight));
      if (_disposed) {
        return;
      }
      WriteableBitmap next = ToBitmap(rendered);
      WriteableBitmap? previous = Frame;
      Frame = next;
      previous?.Dispose();
      Details = rendered.Details + (LockToVehicle
          ? " · camera locked to MAV"
          : " · free camera W/S/A/D Q/E R/F; arrows change pitch/yaw");
    } catch (Exception ex) {
      if (!_disposed) {
        Status = "3D render failed: " + ex.Message;
      }
    } finally {
      _renderGate.Release();
    }
  }

  private bool TryTerrainPoint(double x, double y, out Terrain3DGeoPoint point) {
    point = default;
    Terrain3DWorld? world = _world;
    if (world == null) {
      return false;
    }
    NumericsVector3 ray;
    try {
      ray = Terrain3DProjection.ScreenRay(
          _lastCamera, x, y, _viewportWidth, _viewportHeight);
    } catch {
      return false;
    }
    return Terrain3DRaycaster.TryIntersect(world, _lastCamera, ray, out point);
  }

  private Terrain3DSettings CurrentSettings => new Terrain3DSettings(
      RangeM, GridSize, TextureMinZoom, TextureMaxZoom,
      VerticalExaggeration, FogEnabled, ImageryEnabled).Normalize();

  private Terrain3DSnapshot CaptureSnapshot() {
    DateTime now = DateTime.UtcNow;
    Terrain3DSnapshot snapshot = Terrain3DSnapshotFactory.Capture(
        _comPort, now, SnapshotElevationAt);
    if (_lastTelemetryPosition is not { } previous
        || previous.Latitude != snapshot.Vehicle.Latitude
        || previous.Longitude != snapshot.Vehicle.Longitude) {
      _lastTelemetryPosition = snapshot.Vehicle;
      _lastPositionChangeUtc = now;
    }
    return snapshot with { CapturedUtc = _lastPositionChangeUtc };
  }

  private double? SnapshotElevationAt(double latitude, double longitude) {
    Terrain3DWorld? world = _world;
    if (world == null) {
      return null;
    }
    (double east, double north) = Terrain3DGeoMath.ToLocal(
        world.Center, new Terrain3DGeoPoint(latitude, longitude, 0));
    double altitude = world.SampleAltitude(east, north);
    return double.IsFinite(altitude) ? altitude : null;
  }

  private static double? ElevationAt(double latitude, double longitude) {
    var sample = srtm.getAltitude(latitude, longitude);
    return sample.currenttype is srtm.tiletype.valid or srtm.tiletype.ocean
        ? sample.alt
        : null;
  }

  private static WriteableBitmap ToBitmap(Terrain3DRenderResult rendered) {
    var bitmap = new WriteableBitmap(
        new PixelSize(rendered.Width, rendered.Height),
        new Avalonia.Vector(96, 96),
        AvPixelFormat.Bgra8888,
        Avalonia.Platform.AlphaFormat.Premul);
    using var framebuffer = bitmap.Lock();
    int copyBytes = Math.Min(rendered.RowBytes, framebuffer.RowBytes);
    for (int row = 0; row < rendered.Height; row++) {
      Marshal.Copy(
          rendered.Pixels,
          row * rendered.RowBytes,
          framebuffer.Address + row * framebuffer.RowBytes,
          copyBytes);
    }
    return bitmap;
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _timer.Stop();
    _timer.Tick -= OnTimerTick;
    _reloadCancellation?.Cancel();
    _reloadCancellation?.Dispose();
    _reloadCancellation = null;
    Frame?.Dispose();
    Frame = null;
    _ = DisposeWorldAsync();
  }

  private async Task DisposeWorldAsync() {
    await _renderGate.WaitAsync();
    try {
      _world?.Dispose();
      _world = null;
    } finally {
      _renderGate.Release();
    }
  }
}
