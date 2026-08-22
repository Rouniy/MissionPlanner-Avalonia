using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class GimbalVideoInteractionTests {
  [AvaloniaFact]
  public void Overlay_disposal_releases_any_held_payload_input() {
    var overlay = new GimbalVideoOverlay(
        () => 16.0 / 9,
        () => GimbalTrackingOverlay.None);
    int released = 0;
    overlay.InputReleased += () => released++;

    overlay.Dispose();
    overlay.Dispose();

    Assert.Equal(1, released);
  }

  [Fact]
  public void Pointer_mapping_uses_the_decoded_video_letterbox_not_the_window_bounds() {
    Assert.True(GimbalVideoInteraction.TryMapToVideo(
        new Point(50, 21.875), new Size(100, 100), 16.0 / 9, out var top));
    Assert.True(GimbalVideoInteraction.TryMapToVideo(
        new Point(100, 78.125), new Size(100, 100), 16.0 / 9, out var bottomRight));

    Assert.Equal(0, top.X, 6);
    Assert.Equal(-1, top.Y, 6);
    Assert.Equal(1, bottomRight.X, 6);
    Assert.Equal(1, bottomRight.Y, 6);
  }

  [Fact]
  public void Pointer_mapping_clamps_letterbox_clicks_and_rejects_invalid_surfaces() {
    Assert.True(GimbalVideoInteraction.TryMapToVideo(
        new Point(50, 0), new Size(100, 100), 16.0 / 9, out var point));
    Assert.Equal(-1, point.Y);
    Assert.False(GimbalVideoInteraction.TryMapToVideo(
        new Point(0, 0), new Size(0, 100), 16.0 / 9, out _));
  }

  [Fact]
  public void Mouse_bindings_match_the_official_gimbal_video_control() {
    var start = new GimbalVideoPoint(-0.2, -0.1);
    var close = new GimbalVideoPoint(-0.19, -0.1);
    var far = new GimbalVideoPoint(0.5, 0.4);

    Assert.Equal(
        GimbalVideoPointerAction.PanTilt,
        GimbalVideoInteraction.PointerCommand(start, close, KeyModifiers.None).Action);
    Assert.Equal(
        GimbalVideoPointerAction.PointOfInterest,
        GimbalVideoInteraction.PointerCommand(start, close, KeyModifiers.Control).Action);
    Assert.Equal(
        GimbalVideoPointerAction.TrackPoint,
        GimbalVideoInteraction.PointerCommand(start, close, KeyModifiers.Alt).Action);
    Assert.Equal(
        GimbalVideoPointerAction.TrackRectangle,
        GimbalVideoInteraction.PointerCommand(start, far, KeyModifiers.Alt).Action);
  }

  [Fact]
  public void Keyboard_motion_matches_upstream_axes_modifiers_and_zoom() {
    var held = new HashSet<Key> { Key.W, Key.A, Key.E };

    Assert.Equal(
        new GimbalVideoMotion(5, -5, 0.5f),
        GimbalVideoInteraction.Motion(held, KeyModifiers.None, 1, 5, 25, 0.5));
    Assert.Equal(
        new GimbalVideoMotion(25, -25, 0.5f),
        GimbalVideoInteraction.Motion(held, KeyModifiers.Shift, 1, 5, 25, 0.5));
    Assert.Equal(
        new GimbalVideoMotion(1, -1, 0.5f),
        GimbalVideoInteraction.Motion(held, KeyModifiers.Control, 1, 5, 25, 0.5));
  }

  [Fact]
  public void Opposite_motion_keys_cancel_and_invalid_speeds_fail_safe_to_zero() {
    var held = new HashSet<Key> { Key.W, Key.S, Key.Q, Key.E };

    var motion = GimbalVideoInteraction.Motion(
        held, KeyModifiers.None, 1, double.NaN, 25, double.PositiveInfinity);

    Assert.Equal(new GimbalVideoMotion(0, 0, 0), motion);
  }

  [Theory]
  [InlineData(40, 30, true)]
  [InlineData(0, 30, false)]
  [InlineData(40, 0, false)]
  [InlineData(181, 30, false)]
  [InlineData(float.NaN, 30, false)]
  public void Reported_fov_is_used_only_after_a_valid_status_arrives(
      float horizontal,
      float vertical,
      bool expected) {
    Assert.Equal(
        expected,
        GimbalVideoInteraction.HasUsableReportedFov(horizontal, vertical));
  }

  [Fact]
  public void Slew_and_stop_packets_keep_the_captured_modem_address() {
    var slew = GimbalVideoInteraction.RatePacket(42, 191, 3, 5, -10, true);
    var stop = GimbalVideoInteraction.RatePacket(42, 191, 3, 0, 0, true);

    Assert.Equal((byte)42, slew.target_system);
    Assert.Equal((byte)191, slew.target_component);
    Assert.Equal((byte)3, slew.gimbal_device_id);
    Assert.Equal(5, slew.pitch_rate);
    Assert.Equal(-10, slew.yaw_rate);
    Assert.Equal((uint)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK, slew.flags);
    Assert.Equal(slew.target_system, stop.target_system);
    Assert.Equal(slew.target_component, stop.target_component);
    Assert.Equal(slew.gimbal_device_id, stop.gimbal_device_id);
    Assert.Equal(0, stop.pitch_rate);
    Assert.Equal(0, stop.yaw_rate);
  }

  [Theory]
  [InlineData(Key.F, KeyModifiers.Alt, "TakePicture")]
  [InlineData(Key.R, KeyModifiers.Alt, "ToggleRecording")]
  [InlineData(Key.L, KeyModifiers.None, "ToggleYawLock")]
  [InlineData(Key.N, KeyModifiers.None, "Neutral")]
  [InlineData(Key.H, KeyModifiers.None, "Home")]
  [InlineData(Key.F, KeyModifiers.None, "None")]
  public void Hotkeys_match_the_official_defaults(
      Key key,
      KeyModifiers modifiers,
      string expected) {
    Assert.Equal(expected, GimbalVideoInteraction.Hotkey(key, modifiers).ToString());
  }

  [Fact]
  public void Active_tracking_point_becomes_a_red_overlay_shape() {
    var status = BaseStatus();
    status.tracking_mode = (byte)MAVLink.CAMERA_TRACKING_MODE.POINT;
    status.point_x = 0.25f;
    status.point_y = 0.75f;
    status.radius = 0.04f;

    var overlay = GimbalVideoInteraction.TrackingOverlay(status);

    Assert.Equal(GimbalTrackingShape.Point, overlay.Shape);
    Assert.Equal(0.25, overlay.X, 6);
    Assert.Equal(0.75, overlay.Y, 6);
    Assert.Equal(0.04, overlay.Radius, 6);
  }

  [Fact]
  public void Tracking_rectangle_is_normalized_regardless_of_corner_order() {
    var status = BaseStatus();
    status.tracking_mode = (byte)MAVLink.CAMERA_TRACKING_MODE.RECTANGLE;
    status.rec_top_x = 0.8f;
    status.rec_top_y = 0.9f;
    status.rec_bottom_x = 0.2f;
    status.rec_bottom_y = 0.3f;

    var overlay = GimbalVideoInteraction.TrackingOverlay(status);

    Assert.Equal(GimbalTrackingShape.Rectangle, overlay.Shape);
    Assert.Equal(0.2, overlay.X, 6);
    Assert.Equal(0.3, overlay.Y, 6);
    Assert.Equal(0.6, overlay.Width, 6);
    Assert.Equal(0.6, overlay.Height, 6);
  }

  [Fact]
  public void Rendered_inactive_or_malformed_tracking_status_is_hidden() {
    var rendered = BaseStatus();
    rendered.target_data |= (byte)MAVLink.CAMERA_TRACKING_TARGET_DATA.RENDERED;
    Assert.Equal(
        GimbalTrackingShape.None,
        GimbalVideoInteraction.TrackingOverlay(rendered).Shape);

    var inactive = BaseStatus();
    inactive.tracking_status = (byte)MAVLink.CAMERA_TRACKING_STATUS_FLAGS.IDLE;
    Assert.Equal(
        GimbalTrackingShape.None,
        GimbalVideoInteraction.TrackingOverlay(inactive).Shape);

    var malformed = BaseStatus();
    malformed.tracking_mode = (byte)MAVLink.CAMERA_TRACKING_MODE.POINT;
    malformed.point_x = float.NaN;
    Assert.Equal(
        GimbalTrackingShape.None,
        GimbalVideoInteraction.TrackingOverlay(malformed).Shape);
  }

  private static MAVLink.mavlink_camera_tracking_image_status_t BaseStatus() => new() {
    tracking_status = (byte)MAVLink.CAMERA_TRACKING_STATUS_FLAGS.ACTIVE,
    target_data = (byte)MAVLink.CAMERA_TRACKING_TARGET_DATA.IN_STATUS,
    tracking_mode = (byte)MAVLink.CAMERA_TRACKING_MODE.POINT,
    point_x = 0.5f,
    point_y = 0.5f,
  };
}
