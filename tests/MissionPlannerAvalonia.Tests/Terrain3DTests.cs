using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public class Terrain3DTests {
  [Fact]
  public void Settings_are_bounded_and_mesh_size_is_odd() {
    Terrain3DSettings settings = new Terrain3DSettings(
        double.PositiveInfinity, 64, -5, 99, -4, true, true).Normalize();

    Assert.Equal(1500, settings.RangeM);
    Assert.Equal(65, settings.GridSize);
    Assert.Equal(1, settings.TextureMinZoom);
    Assert.Equal(20, settings.TextureMaxZoom);
    Assert.Equal(0.25, settings.VerticalExaggeration);
  }

  [Fact]
  public void Geographic_offsets_round_trip_in_local_metres() {
    var origin = new Terrain3DGeoPoint(35.1856, 33.3823, 125);
    Terrain3DGeoPoint moved = Terrain3DGeoMath.Offset(origin, 420, -275, 18);
    (double east, double north) = Terrain3DGeoMath.ToLocal(origin, moved);

    Assert.InRange(east, 419.9, 420.1);
    Assert.InRange(north, -275.1, -274.9);
    Assert.Equal(143, moved.AltitudeM, 6);
  }

  [Fact]
  public void Mesh_uses_dem_samples_and_a_stable_fallback_for_missing_cells() {
    Terrain3DSnapshot snapshot = Snapshot(altitude: 130, relativeAltitude: 30);
    using Terrain3DWorld world = Terrain3DWorldBuilder.BuildMesh(
        snapshot,
        new Terrain3DSettings(400, 17, 12, 16, 1, true, false),
        (latitude, longitude) => longitude > snapshot.Vehicle.Longitude
            ? 100 + (latitude - snapshot.Vehicle.Latitude) * 1000
            : null);

    Assert.Equal(17 * 17, world.Vertices.Length);
    Assert.True(world.MissingSamples > 0);
    Assert.True(world.MissingSamples < world.Vertices.Length);
    Assert.True(double.IsFinite(world.SampleAltitude(0, 0)));
    Assert.InRange(world.MinimumAltitudeM, 95, 105);
    Assert.InRange(world.MaximumAltitudeM, 95, 105);
  }

  [Fact]
  public void Perspective_projects_forward_world_point_to_screen_center() {
    var camera = new Terrain3DCamera(0, 0, 100, 0, 0, 0);
    Terrain3DProjectedPoint forward = Terrain3DProjection.Project(
        camera, new Vector3(0, 100, 100), 800, 600);
    Terrain3DProjectedPoint behind = Terrain3DProjection.Project(
        camera, new Vector3(0, -100, 100), 800, 600);

    Assert.True(forward.Visible);
    Assert.InRange(forward.X, 399.9, 400.1);
    Assert.InRange(forward.Y, 299.9, 300.1);
    Assert.False(behind.Visible);
  }

  [Fact]
  public void Locked_camera_projects_recent_ned_velocity_and_free_camera_moves() {
    Terrain3DSnapshot snapshot = Snapshot(
        altitude: 100,
        relativeAltitude: 100,
        capturedUtc: DateTime.UtcNow.AddMilliseconds(-500)) with {
      VelocityEastMps = 4,
      VelocityNorthMps = 6,
      VelocityVerticalMps = 2,
    };
    using Terrain3DWorld world = Terrain3DWorldBuilder.BuildMesh(
        snapshot,
        new Terrain3DSettings(500, 17, 12, 16, 1, true, false),
        (_, _) => 0);

    Terrain3DCamera locked = Terrain3DCamera.Locked(world, snapshot, DateTime.UtcNow);
    Assert.InRange(locked.EastM, 1.8, 2.2);
    Assert.InRange(locked.NorthM, 2.8, 3.2);
    Assert.InRange(locked.AltitudeM, 100.9, 101.1);

    Terrain3DCamera moved = locked.Move(Terrain3DCameraMotion.Forward, 10)
        .Move(Terrain3DCameraMotion.Right, 5)
        .Move(Terrain3DCameraMotion.Up, 2);
    Assert.InRange(moved.EastM - locked.EastM, 4.9, 5.1);
    Assert.InRange(moved.NorthM - locked.NorthM, 9.9, 10.1);
    Assert.Equal(locked.AltitudeM + 2, moved.AltitudeM, 6);
  }

  [Fact]
  public void Center_screen_ray_intersects_flat_terrain() {
    Terrain3DSnapshot snapshot = Snapshot(altitude: 100, relativeAltitude: 100);
    using Terrain3DWorld world = Terrain3DWorldBuilder.BuildMesh(
        snapshot,
        new Terrain3DSettings(500, 17, 12, 16, 1, true, false),
        (_, _) => 0);
    var camera = new Terrain3DCamera(0, 0, 100, 0, -45, 0);
    Vector3 ray = Terrain3DProjection.ScreenRay(camera, 400, 300, 800, 600);

    Assert.True(Terrain3DRaycaster.TryIntersect(world, camera, ray, out var hit));
    Assert.InRange(hit.AltitudeM, -0.01, 0.01);
    Assert.True(hit.Latitude > snapshot.Vehicle.Latitude);
    Assert.InRange(hit.Longitude, snapshot.Vehicle.Longitude - 0.00001,
        snapshot.Vehicle.Longitude + 0.00001);
  }

  [Fact]
  public void Mission_altitude_frames_resolve_to_amsl() {
    static double? Elevation(double latitude, double longitude) => 200;

    Assert.Equal(50, Terrain3DSnapshotFactory.ResolveAltitude(
        0, 50, 35, 33, 100, Elevation));
    Assert.Equal(50, Terrain3DSnapshotFactory.ResolveAltitude(
        5, 50, 35, 33, 100, Elevation));
    Assert.Equal(150, Terrain3DSnapshotFactory.ResolveAltitude(
        3, 50, 35, 33, 100, Elevation));
    Assert.Equal(250, Terrain3DSnapshotFactory.ResolveAltitude(
        10, 50, 35, 33, 100, Elevation));
    Assert.Equal(250, Terrain3DSnapshotFactory.ResolveAltitude(
        11, 50, 35, 33, 100, Elevation));
  }

  [Fact]
  public void Software_renderer_outputs_a_bgra_frame_and_terrain_diagnostics() {
    Terrain3DSnapshot snapshot = Snapshot(altitude: 80, relativeAltitude: 80) with {
      PitchDeg = -12,
      Waypoints = [
        new Terrain3DWaypoint(1,
            Terrain3DGeoMath.Offset(Snapshot().Vehicle, 0, 150, 20), "WP 1"),
      ],
    };
    using Terrain3DWorld world = Terrain3DWorldBuilder.BuildMesh(
        snapshot,
        new Terrain3DSettings(500, 17, 12, 16, 1, true, false),
        (latitude, longitude) => 5 + (latitude - snapshot.Vehicle.Latitude) * 1000);
    Terrain3DCamera camera = Terrain3DCamera.Locked(world, snapshot, snapshot.CapturedUtc);

    Terrain3DRenderResult result = Terrain3DRenderer.Render(
        world, snapshot,
        new Terrain3DSettings(500, 17, 12, 16, 1, true, false),
        camera, 640, 400);

    Assert.Equal(640, result.Width);
    Assert.Equal(400, result.Height);
    Assert.True(result.RowBytes >= result.Width * 4);
    Assert.Equal(result.RowBytes * result.Height, result.Pixels.Length);
    Assert.Contains(result.Pixels, value => value != 0);
    Assert.Contains("17×17 mesh", result.Details);
  }

  [Fact]
  public async Task Imagery_atlas_reduces_zoom_to_a_bounded_tile_request() {
    byte[] tileData;
    using (var bitmap = new SKBitmap(2, 2)) {
      bitmap.Erase(SKColors.ForestGreen);
      using SKImage image = SKImage.FromBitmap(bitmap);
      using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 90);
      tileData = encoded.ToArray();
    }
    int requested = 0;

    using Terrain3DTexture? texture = await Terrain3DWorldBuilder.LoadTextureAsync(
        Snapshot().Vehicle,
        5000,
        12,
        20,
        "GoogleSatelliteMap",
        (mapType, tile, token) => {
          Interlocked.Increment(ref requested);
          return Task.FromResult<byte[]?>(tileData);
        },
        CancellationToken.None);

    Assert.NotNull(texture);
    Assert.InRange(requested, 1, Terrain3DWorldBuilder.MaximumTextureTiles);
    Assert.Equal(requested, texture.RequestedTiles);
    Assert.Equal(requested, texture.LoadedTiles);
    Assert.True(texture.Zoom < 20);
  }

  [AvaloniaFact]
  public void Terrain_view_exposes_reload_image_and_status_controls() {
    var view = new Terrain3DView();

    Assert.NotNull(view.FindControl<Image>("TerrainImage"));
    Assert.NotNull(view.FindControl<Button>("ReloadTerrain"));
    Assert.NotNull(view.FindControl<TextBlock>("TerrainStatus"));
    Assert.NotNull(view.FindControl<TextBlock>("TerrainPointerStatus"));
  }

  [AvaloniaFact]
  public void Terrain_window_composes_the_native_view_and_disposable_view_model() {
    var window = new Terrain3DWindow();

    Assert.Equal("3D Terrain View", window.Title);
    Assert.IsType<Terrain3DView>(window.Content);
    Assert.IsAssignableFrom<IDisposable>(window.DataContext).Dispose();
  }

  [Fact]
  public void Official_3d_view_is_available_from_developer_tools() {
    using var tools = new ConfigDeveloperToolsViewModel();

    Assert.Contains(tools.Actions, action => action.Label == "3D Terrain View");
  }

  private static Terrain3DSnapshot Snapshot(
      double altitude = 100,
      double relativeAltitude = 30,
      DateTime? capturedUtc = null) => new(
      new Terrain3DGeoPoint(35.1856, 33.3823, altitude),
      relativeAltitude,
      0,
      0,
      0,
      0,
      0,
      0,
      capturedUtc ?? DateTime.UtcNow,
      "LOITER",
      false,
      1,
      1,
      []);
}
