using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public class OsdVideoOverlayTests {
  [Fact]
  public void Timeline_selects_latest_state_at_or_before_offset_video_time() {
    DateTime start = new(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);
    var timeline = new OsdTelemetryTimeline([
      Sample(start.AddSeconds(3), 30),
      Sample(start, 0),
      Sample(start.AddSeconds(1), 10),
      Sample(start.AddSeconds(1), 11),
    ]);

    Assert.Equal(3, timeline.Count);
    Assert.Equal(0, timeline.At(TimeSpan.FromSeconds(-5), TimeSpan.Zero).Roll);
    Assert.Equal(11, timeline.At(TimeSpan.FromSeconds(1.9), TimeSpan.Zero).Roll);
    Assert.Equal(30, timeline.At(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).Roll);
    Assert.Equal(0, timeline.At(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(-10)).Roll);
    Assert.Equal(30, timeline.At(TimeSpan.FromDays(10), TimeSpan.Zero).Roll);
  }

  [Fact]
  public void Timeline_rounds_log_time_down_to_upstream_100ms_buckets() {
    var value = new DateTime(2026, 8, 22, 10, 11, 12, 987, DateTimeKind.Local)
        .AddTicks(6543);

    DateTime rounded = OsdTelemetryTimeline.RoundDown(
        value, TimeSpan.FromMilliseconds(100));

    Assert.Equal(new DateTime(2026, 8, 22, 10, 11, 12, 900, DateTimeKind.Local), rounded);
    Assert.Equal(DateTimeKind.Local, rounded.Kind);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        OsdTelemetryTimeline.RoundDown(value, TimeSpan.Zero));
  }

  [Fact]
  public void Default_output_path_matches_upstream_suffix_and_never_overwrites() {
    string root = CreateTempDirectory();
    try {
      string video = Path.Combine(root, "camera.avi");
      File.WriteAllBytes(video, [1]);

      string first = OsdVideoOverlayService.DefaultOutputPath(video);
      Assert.Equal(Path.Combine(root, "camera-overlay.avi"), first);
      File.WriteAllBytes(first, [1]);

      Assert.Equal(
          Path.Combine(root, "camera-overlay-2.avi"),
          OsdVideoOverlayService.DefaultOutputPath(video));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void Validation_rejects_overwrite_wrong_log_and_unsafe_offsets() {
    string root = CreateTempDirectory();
    try {
      string video = Path.Combine(root, "camera.avi");
      string tlog = Path.Combine(root, "flight.tlog");
      string output = Path.Combine(root, "overlay.avi");
      File.WriteAllBytes(video, [1]);
      File.WriteAllBytes(tlog, [1]);

      OsdVideoOverlayService.Validate(new OsdVideoExportOptions(
          video, tlog, output, 0, false));

      Assert.Throws<IOException>(() => OsdVideoOverlayService.Validate(
          new OsdVideoExportOptions(video, tlog, video, 0, false)));
      Assert.Throws<ArgumentOutOfRangeException>(() => OsdVideoOverlayService.Validate(
          new OsdVideoExportOptions(video, tlog, output, 901, false)));

      string binaryLog = Path.Combine(root, "flight.bin");
      File.WriteAllBytes(binaryLog, [1]);
      Assert.Throws<NotSupportedException>(() => OsdVideoOverlayService.Validate(
          new OsdVideoExportOptions(video, binaryLog, output, 0, false)));

      File.WriteAllBytes(output, [1]);
      Assert.Throws<IOException>(() => OsdVideoOverlayService.Validate(
          new OsdVideoExportOptions(video, tlog, output, 0, false)));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(1920, 1080, false, 960, 960, 540)]
  [InlineData(1920, 1081, false, 960, 960, 540)]
  [InlineData(640, 480, false, 960, 640, 480)]
  [InlineData(1920, 1081, true, 960, 1920, 1081)]
  public void Output_size_preserves_aspect_and_source_resolution_option(
      int sourceWidth,
      int sourceHeight,
      bool fullResolution,
      int previewWidth,
      int expectedWidth,
      int expectedHeight) {
    Assert.Equal(
        (expectedWidth, expectedHeight),
        LibVlcOsdExportSession.OutputSize(
            sourceWidth, sourceHeight, fullResolution, previewWidth));
  }

  [Fact]
  public void Video_background_uses_the_entire_output_instead_of_hud_letterboxing() {
    var available = new Size(1000, 600);

    Rect regular = HudLayout.Viewport(available, sixteenByNine: false, hasVideoBackground: false);
    Rect video = HudLayout.Viewport(available, sixteenByNine: false, hasVideoBackground: true);

    Assert.True(regular.Width < available.Width);
    Assert.Equal(new Rect(0, 0, 1000, 600), video);
  }

  [AvaloniaFact]
  public void Native_window_and_developer_tools_expose_the_official_workflow() {
    var window = new OsdVideoOverlayWindow();
    using var developerTools = new ConfigDeveloperToolsViewModel();

    Assert.IsType<OsdVideoOverlayViewModel>(window.DataContext);
    Assert.NotNull(window.FindControl<Image>("PreviewImage"));
    Assert.Contains(developerTools.Actions,
        action => action.Label == "OSD Video — Telemetry Overlay");
  }

  [Fact]
  public async Task Timestamped_tlog_is_loaded_into_full_current_state_timeline() {
    string root = CreateTempDirectory();
    try {
      string path = Path.Combine(root, "flight.tlog");
      DateTime start = new(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);
      using (var stream = File.Create(path)) {
        WriteTlogPacket(stream, start, MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
            new MAVLink.mavlink_heartbeat_t(
                0,
                (byte)MAVLink.MAV_TYPE.QUADROTOR,
                (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED,
                0,
                3));
        WriteTlogPacket(stream, start.AddMilliseconds(100), MAVLink.MAVLINK_MSG_ID.ATTITUDE,
            new MAVLink.mavlink_attitude_t(
                100, MathF.PI / 18, -MathF.PI / 36, MathF.PI / 2, 0, 0, 0));
        WriteTlogPacket(stream, start.AddMilliseconds(200), MAVLink.MAVLINK_MSG_ID.VFR_HUD,
            new MAVLink.mavlink_vfr_hud_t(21.5f, 18.25f, 123.5f, -2.75f, 91, 47));
        WriteTlogPacket(
            stream,
            start.AddMilliseconds(300),
            MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
            new MAVLink.mavlink_global_position_int_t(
                300, 351000000, 332000000, 150000, 123500, 0, 0, 275, 9100));
        WriteTlogPacket(stream, start.AddMilliseconds(400), MAVLink.MAVLINK_MSG_ID.SYS_STATUS,
            new MAVLink.mavlink_sys_status_t(
                0, 0, 0, 0, 15800, 420, 0, 0, 0, 0, 0, 0, 73));
        WriteTlogPacket(stream, start.AddMilliseconds(500), MAVLink.MAVLINK_MSG_ID.BATTERY_STATUS,
            new MAVLink.mavlink_battery_status_t(
                100,
                -1,
                2500,
                [4000, 4000, 4000, 3800, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue,
                  ushort.MaxValue, ushort.MaxValue, ushort.MaxValue],
                420,
                0,
                0,
                0,
                73,
                600,
                0,
                [ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue],
                0,
                0));
      }

      OsdTelemetryTimeline timeline = await OsdTelemetryTimeline.LoadAsync(
          path, null, CancellationToken.None);
      OsdTelemetrySample state = timeline.At(TimeSpan.FromSeconds(1), TimeSpan.Zero);

      Assert.True(timeline.Count >= 4);
      Assert.Equal(start.ToLocalTime(), timeline.StartTime);
      Assert.InRange(state.Roll, 9.9, 10.1);
      Assert.InRange(state.Pitch, -5.1, -4.9);
      Assert.InRange(state.Yaw, 89.9, 90.1);
      Assert.InRange(state.AirSpeed, 21.4, 21.6);
      Assert.InRange(state.GroundSpeed, 18.1, 18.4);
      Assert.InRange(state.Alt, 123.4, 123.6);
      Assert.InRange(state.BatteryVoltage, 15.7, 15.9);
      Assert.Equal(73, state.BatteryRemaining);
      Assert.True(state.Armed);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task Libvlc_pipeline_exports_a_playable_mjpeg_avi_when_runtime_is_available() {
    if (!OperatingSystem.IsLinux()
        || (!File.Exists("/lib/x86_64-linux-gnu/libvlc.so.5")
            && !File.Exists("/usr/lib/aarch64-linux-gnu/libvlc.so.5"))) {
      return;
    }

    string root = CreateTempDirectory();
    try {
      string source = Path.Combine(root, "source.avi");
      string tlog = Path.Combine(root, "flight.tlog");
      string output = Path.Combine(root, "source-overlay.avi");
      using (var writer = new MjpegAviWriter(source, 64, 48, 5)) {
        for (int frame = 0; frame < 6; frame++) {
          writer.WriteJpeg(SolidJpeg(64, 48, new SKColor(
              (byte)(30 + frame * 20), 60, 180)));
        }
      }
      DateTime start = DateTime.UtcNow.AddMinutes(-1);
      using (var stream = File.Create(tlog)) {
        WriteTlogPacket(stream, start, MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
            new MAVLink.mavlink_heartbeat_t(
                0,
                (byte)MAVLink.MAV_TYPE.QUADROTOR,
                (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED,
                0,
                3));
        WriteTlogPacket(stream, start.AddMilliseconds(100), MAVLink.MAVLINK_MSG_ID.ATTITUDE,
            new MAVLink.mavlink_attitude_t(
                100, MathF.PI / 12, 0, MathF.PI / 4, 0, 0, 0));
      }
      using var renderer = new RecordingFrameRenderer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

      OsdVideoExportResult result = await OsdVideoOverlayService.ExportAsync(
          new OsdVideoExportOptions(source, tlog, output, 1, true),
          renderer,
          null,
          timeout.Token);

      Assert.Equal(output, result.OutputPath);
      Assert.Equal(64, result.Width);
      Assert.Equal(48, result.Height);
      Assert.InRange(result.WrittenFrames, 5, 8);
      Assert.InRange(renderer.Samples.Count, 1, result.WrittenFrames);
      Assert.All(renderer.Samples, sample => Assert.InRange(sample.Roll, 14.9, 15.1));
      Assert.All(renderer.Samples, sample => Assert.True(sample.Armed));
      Assert.True(new FileInfo(output).Length > 500);
      byte[] avi = File.ReadAllBytes(output);
      Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(avi, 0, 4));
      Assert.Equal("AVI ", System.Text.Encoding.ASCII.GetString(avi, 8, 4));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  private static OsdTelemetrySample Sample(DateTime time, double roll) => new(
      Time: time,
      Roll: roll,
      Pitch: 0,
      Yaw: 0,
      Alt: 0,
      AirSpeed: 0,
      GroundSpeed: 0,
      VerticalSpeed: 0,
      SatCount: 0,
      Armed: false,
      PrearmOk: false,
      GpsFixType: 0,
      Mode: "STABILIZE",
      BatteryVoltage: 0,
      BatteryRemaining: 0,
      CurrentAmps: 0,
      NavBearing: 0,
      TargetAlt: 0,
      TargetSpeed: 0,
      WindDir: 0,
      WindVel: 0,
      Aoa: 0,
      Ssa: 0,
      XTrackError: 0,
      TurnRate: 0,
      BatteryVoltage2: 0,
      BatteryRemaining2: 0,
      CurrentAmps2: 0,
      ThrottlePercent: 0,
      Failsafe: false,
      SafetyActive: false,
      LinkQuality: 0,
      WpDist: 0,
      WpNo: 0);

  private static void WriteTlogPacket(
      Stream destination, DateTime time, MAVLink.MAVLINK_MSG_ID id, object payload) {
    ulong microseconds = (ulong)((time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10);
    byte[] stamp = BitConverter.GetBytes(microseconds);
    if (BitConverter.IsLittleEndian) {
      Array.Reverse(stamp);
    }
    destination.Write(stamp);
    var parser = new MAVLink.MavlinkParse();
    destination.Write(parser.GenerateMAVLinkPacket20(id, payload, false, 1, 1));
  }

  private static byte[] SolidJpeg(int width, int height, SKColor color) {
    using var bitmap = new SKBitmap(width, height);
    bitmap.Erase(color);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
    return data.ToArray();
  }

  private static string CreateTempDirectory() {
    string path = Path.Combine(
        Path.GetTempPath(), "mp-osd-video-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  private sealed class RecordingFrameRenderer : IOsdVideoFrameRenderer {
    internal List<OsdTelemetrySample> Samples { get; } = [];

    public ValueTask<byte[]> RenderJpegAsync(
        byte[] bgraPixels,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        int outputWidth,
        int outputHeight,
        OsdTelemetrySample sample,
        int jpegQuality,
        CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Assert.True(bgraPixels.Length >= sourceStride * sourceHeight);
      Samples.Add(sample);
      return ValueTask.FromResult(SolidJpeg(outputWidth, outputHeight, SKColors.DarkCyan));
    }

    public void Dispose() {
    }
  }
}
