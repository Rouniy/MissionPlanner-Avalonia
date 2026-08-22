using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;

namespace MissionPlannerAvalonia.Services;

internal enum GimbalVideoPointerAction {
  PanTilt,
  PointOfInterest,
  TrackPoint,
  TrackRectangle,
}

internal enum GimbalVideoHotkeyAction {
  None,
  TakePicture,
  ToggleRecording,
  ToggleYawLock,
  Neutral,
  Home,
}

internal enum GimbalTrackingShape {
  None,
  Point,
  Rectangle,
}

internal readonly record struct GimbalVideoPoint(double X, double Y);

internal readonly record struct GimbalVideoPointerCommand(
    GimbalVideoPointerAction Action,
    GimbalVideoPoint Start,
    GimbalVideoPoint End);

internal readonly record struct GimbalVideoMotion(
    float PitchRate,
    float YawRate,
    float ZoomRate);

internal readonly record struct GimbalTrackingOverlay(
    GimbalTrackingShape Shape,
    double X,
    double Y,
    double Width,
    double Height,
    double Radius) {
  public static GimbalTrackingOverlay None =>
      new(GimbalTrackingShape.None, 0, 0, 0, 0, 0);
}

internal static class GimbalVideoInteraction {
  private const double DragThreshold = 0.025;

  public static bool TryMapToVideo(
      Point pointer,
      Size surface,
      double videoAspectRatio,
      out GimbalVideoPoint point) {
    point = default;
    if (!double.IsFinite(surface.Width) || !double.IsFinite(surface.Height)
        || surface.Width <= 0 || surface.Height <= 0) {
      return false;
    }

    double aspect = double.IsFinite(videoAspectRatio) && videoAspectRatio > 0
        ? videoAspectRatio
        : surface.Width / surface.Height;
    double imageWidth = Math.Min(surface.Width, surface.Height * aspect);
    double imageHeight = Math.Min(surface.Height, surface.Width / aspect);
    if (imageWidth <= 0 || imageHeight <= 0) {
      return false;
    }

    double left = (surface.Width - imageWidth) / 2;
    double top = (surface.Height - imageHeight) / 2;
    double x = Math.Clamp((pointer.X - left) / imageWidth, 0, 1);
    double y = Math.Clamp((pointer.Y - top) / imageHeight, 0, 1);
    point = new GimbalVideoPoint(x * 2 - 1, y * 2 - 1);
    return true;
  }

  public static GimbalVideoPointerCommand PointerCommand(
      GimbalVideoPoint start,
      GimbalVideoPoint end,
      KeyModifiers modifiers) {
    if (modifiers.HasFlag(KeyModifiers.Alt)) {
      double distance = Math.Sqrt(
          Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
      return new GimbalVideoPointerCommand(
          distance >= DragThreshold
              ? GimbalVideoPointerAction.TrackRectangle
              : GimbalVideoPointerAction.TrackPoint,
          start,
          end);
    }
    return new GimbalVideoPointerCommand(
        modifiers.HasFlag(KeyModifiers.Control)
            ? GimbalVideoPointerAction.PointOfInterest
            : GimbalVideoPointerAction.PanTilt,
        start,
        end);
  }

  public static GimbalVideoMotion Motion(
      IReadOnlySet<Key> heldKeys,
      KeyModifiers modifiers,
      double slowSpeed,
      double normalSpeed,
      double fastSpeed,
      double zoomSpeed) {
    double speed = modifiers.HasFlag(KeyModifiers.Shift)
        ? fastSpeed
        : modifiers.HasFlag(KeyModifiers.Control) ? slowSpeed : normalSpeed;
    float pitch = Direction(heldKeys, Key.W, Key.S) * PositiveFinite(speed);
    float yaw = Direction(heldKeys, Key.D, Key.A) * PositiveFinite(speed);
    float zoom = Direction(heldKeys, Key.E, Key.Q) * PositiveFinite(zoomSpeed);
    return new GimbalVideoMotion(pitch, yaw, zoom);
  }

  public static bool IsMotionKey(Key key) => key is
      Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

  public static bool IsModifierKey(Key key) => key is
      Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl;

  public static bool IsHotkeyKey(Key key) => key is
      Key.F or Key.R or Key.L or Key.N or Key.H;

  public static bool HasUsableReportedFov(float horizontal, float vertical) =>
      float.IsFinite(horizontal) && horizontal > 0 && horizontal <= 180
      && float.IsFinite(vertical) && vertical > 0 && vertical <= 180;

  public static MAVLink.mavlink_gimbal_manager_set_pitchyaw_t RatePacket(
      byte systemId,
      byte componentId,
      byte deviceId,
      float pitchRate,
      float yawRate,
      bool yawLocked) => new() {
        target_system = systemId,
        target_component = componentId,
        gimbal_device_id = deviceId,
        pitch = float.NaN,
        yaw = float.NaN,
        pitch_rate = pitchRate,
        yaw_rate = yawRate,
        flags = yawLocked ? (uint)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0,
      };

  public static GimbalVideoHotkeyAction Hotkey(Key key, KeyModifiers modifiers) {
    bool alt = modifiers.HasFlag(KeyModifiers.Alt);
    if (alt && key == Key.F) {
      return GimbalVideoHotkeyAction.TakePicture;
    }
    if (alt && key == Key.R) {
      return GimbalVideoHotkeyAction.ToggleRecording;
    }
    if (modifiers != KeyModifiers.None) {
      return GimbalVideoHotkeyAction.None;
    }
    return key switch {
      Key.L => GimbalVideoHotkeyAction.ToggleYawLock,
      Key.N => GimbalVideoHotkeyAction.Neutral,
      Key.H => GimbalVideoHotkeyAction.Home,
      _ => GimbalVideoHotkeyAction.None,
    };
  }

  public static GimbalTrackingOverlay TrackingOverlay(
      MAVLink.mavlink_camera_tracking_image_status_t status) {
    var flags = (MAVLink.CAMERA_TRACKING_TARGET_DATA)status.target_data;
    bool active = status.tracking_status
        == (byte)MAVLink.CAMERA_TRACKING_STATUS_FLAGS.ACTIVE;
    bool inStatus = flags.HasFlag(MAVLink.CAMERA_TRACKING_TARGET_DATA.IN_STATUS);
    bool alreadyRendered = flags.HasFlag(MAVLink.CAMERA_TRACKING_TARGET_DATA.RENDERED);
    if (!active || !inStatus || alreadyRendered) {
      return GimbalTrackingOverlay.None;
    }

    if (status.tracking_mode == (byte)MAVLink.CAMERA_TRACKING_MODE.POINT
        && IsUnit(status.point_x) && IsUnit(status.point_y)) {
      double radius = float.IsFinite(status.radius) && status.radius > 0
          ? status.radius
          : 0.02;
      return new GimbalTrackingOverlay(
          GimbalTrackingShape.Point,
          status.point_x,
          status.point_y,
          0,
          0,
          radius);
    }

    if (status.tracking_mode == (byte)MAVLink.CAMERA_TRACKING_MODE.RECTANGLE
        && IsUnit(status.rec_top_x) && IsUnit(status.rec_top_y)
        && IsUnit(status.rec_bottom_x) && IsUnit(status.rec_bottom_y)) {
      double left = Math.Min(status.rec_top_x, status.rec_bottom_x);
      double top = Math.Min(status.rec_top_y, status.rec_bottom_y);
      return new GimbalTrackingOverlay(
          GimbalTrackingShape.Rectangle,
          left,
          top,
          Math.Abs(status.rec_bottom_x - status.rec_top_x),
          Math.Abs(status.rec_bottom_y - status.rec_top_y),
          0);
    }
    return GimbalTrackingOverlay.None;
  }

  private static float Direction(IReadOnlySet<Key> keys, Key positive, Key negative) =>
      (keys.Contains(positive) ? 1 : 0) - (keys.Contains(negative) ? 1 : 0);

  private static float PositiveFinite(double value) =>
      double.IsFinite(value) && value > 0 ? (float)value : 0;

  private static bool IsUnit(float value) =>
      float.IsFinite(value) && value is >= 0 and <= 1;
}
